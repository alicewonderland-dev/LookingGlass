using Google.Protobuf;
using LookingGlass.Core.Client;
using LookingGlass.Core.Crypto;
using LookingGlass.Protocol;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// Local chat against an in-process server: a message reaches only the players named (friends near the sender, as the
/// plugin picks them), sealed to each; the server checks it, passes it on and keeps nothing; older plugins never see it;
/// and the receiving session shows only what it can open and verify against the key it holds for the sender, never from
/// someone blocked. (Whether the sender is near and a friend is the plugin's to check: see <see cref="LocalChatTests"/>.)
/// </summary>
public sealed class LocalChatEndToEndTests : IAsyncLifetime {
    private Harness _server = null!;

    public ValueTask InitializeAsync() {
        this._server = new Harness();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() {
        await this._server.DisposeAsync();
    }

    private static (string Name, string WorldName) Near(TestClient client) => (client.Name, ProtocolInfo.DebugWorldName);

    private static LinkedText Say(string text) => LinkedText.Plain(text);

    /// <summary>A local message from <paramref name="from"/> to <paramref name="to"/>, sealed and signed as a client would, and delivered as the server would.</summary>
    private static Event Forge(TestClient from, TestClient to, string text, DateTimeOffset? when = null, IdentityKeys? signWith = null, IdentityBundle? claim = null) {
        using var own = from.LoadIdentity();
        var keys = signWith ?? own;
        var recipient = to.Keys();
        var sent = LocalCrypto.Seal(new Content { Text = new TextContent { Text = text } }, keys, from.UserId,
            [(to.UserId, recipient.AgreementKeyArray())], (when ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds());
        return Delivered(sent, from, to.UserId, claim ?? own.ToBundle());
    }

    private static Event Delivered(SendLocalMessage sent, TestClient from, long to, IdentityBundle identity) {
        var copy = sent.Copies.Single(c => c.RecipientId == to);
        return new Event {
            LocalMessage = new LocalMessage {
                Sender = new UserIdentity {
                    User = new User { UserId = from.UserId, Name = from.Name, WorldName = ProtocolInfo.DebugWorldName },
                    Identity = identity,
                    KeyVersion = 1,
                },
                MessageId = sent.MessageId,
                TimestampUnixMs = sent.TimestampUnixMs,
                Ciphertext = sent.Ciphertext,
                SealedKey = copy.SealedKey,
                Signature = copy.Signature,
            },
        };
    }

    // ================================================================ sending and receiving

    [Fact]
    public async Task AFriendNamedGetsItAndNobodyElse() {
        var alice = await this._server.RegisterAsync("Alice Local");
        var bob = await this._server.RegisterAsync("Bob Local");
        var carol = await this._server.RegisterAsync("Carol Local");
        Assert.True(alice.Session.LocalChatAvailable);
        Assert.Equal(50u, alice.Session.Snapshot.Limits!.MaxLocalRecipients);

        var result = await alice.Session.SendLocalAsync([Near(bob)], Say("hello over here"), Ct);

        Assert.Equal(new LocalSendResult(1, 0, 0), result);
        var got = await WaitFor(() => bob.LocalMessages.FirstOrDefault());
        Assert.Equal("hello over here", got.Text);
        Assert.Equal(alice.UserId, got.Sender.UserId);
        Assert.Equal(alice.Name, got.Sender.Name);
        Assert.False(got.IsOwn);
        // The sender sees their own line, once the server took it.
        var own = Assert.Single(alice.LocalMessages);
        Assert.True(own.IsOwn);
        Assert.Equal("hello over here", own.Text);

        // Carol wasn't named: nothing for her, and nothing about it.
        await bob.Session.SendLocalAsync([Near(alice)], Say("and back"), Ct);
        await WaitFor(() => alice.LocalMessages.FirstOrDefault(m => !m.IsOwn));
        Assert.Empty(carol.LocalMessages);
        Assert.DoesNotContain(carol.Session.GetTrace(), entry => entry.Summary.Contains(nameof(Event.KindOneofCase.LocalMessage)));
    }

    [Fact]
    public async Task ItGoesToSeveralFriendsEachWithTheirOwnCopy() {
        var alice = await this._server.RegisterAsync("Alice Crowd Local");
        var friends = new List<TestClient>();
        foreach (var name in new[] { "Bob Crowd Local", "Carol Crowd Local", "Dave Crowd Local" }) {
            friends.Add(await this._server.RegisterAsync(name));
        }

        var result = await alice.Session.SendLocalAsync(friends.Select(Near).ToList(), Say("everyone near"), Ct);

        Assert.Equal(3, result.Sent);
        foreach (var friend in friends) {
            Assert.Equal("everyone near", (await WaitFor(() => friend.LocalMessages.FirstOrDefault())).Text);
        }
    }

    [Fact]
    public async Task PlayersWhoDontUseLookingGlassAreSkippedAndNotLookedUpAgainForAWhile() {
        var alice = await this._server.RegisterAsync("Alice Lonely Local");
        var bob = await this._server.RegisterAsync("Bob Lonely Local");
        (string, string) stranger = ("Nobody Registered", ProtocolInfo.DebugWorldName);

        Assert.Equal(new LocalSendResult(1, 1, 0), await alice.Session.SendLocalAsync([stranger, Near(bob)], Say("one"), Ct));
        var lookups = alice.Session.GetTrace().Count(entry => entry.Outgoing && entry.Summary.EndsWith(nameof(ClientFrame.BodyOneofCase.LookupUser)));
        Assert.Equal(2, lookups);

        Assert.Equal(new LocalSendResult(1, 1, 0), await alice.Session.SendLocalAsync([stranger, Near(bob)], Say("two"), Ct));
        Assert.Equal(lookups, alice.Session.GetTrace().Count(entry => entry.Outgoing && entry.Summary.EndsWith(nameof(ClientFrame.BodyOneofCase.LookupUser))));
        await WaitFor(() => bob.LocalMessages.Count == 2 ? bob : null);

        // Nobody who uses it: nothing is sent at all.
        Assert.Equal(new LocalSendResult(0, 1, 0), await alice.Session.SendLocalAsync([stranger], Say("three"), Ct));
        Assert.Equal(2, alice.LocalMessages.Count);
    }

    [Fact]
    public async Task NobodyIsSentTheirOwnOrABlockedPlayersCopy() {
        var alice = await this._server.RegisterAsync("Alice Self Local");
        var bob = await this._server.RegisterAsync("Bob Self Local");
        var carol = await this._server.RegisterAsync("Carol Blocked Local");
        alice.Session.BlockUser(carol.UserId);

        var result = await alice.Session.SendLocalAsync([Near(alice), Near(bob), Near(carol)], Say("just bob"), Ct);

        Assert.Equal(1, result.Sent);
        await WaitFor(() => bob.LocalMessages.FirstOrDefault());
        Assert.Empty(carol.LocalMessages);
    }

    [Fact]
    public async Task ItCanCarryLinks() {
        var alice = await this._server.RegisterAsync("Alice Linking Local");
        var bob = await this._server.RegisterAsync("Bob Linking Local");
        var linked = new LinkedText("look [Potion]", [new MessageLink(5, 8, new ChatLink.Item(4551))]);

        await alice.Session.SendLocalAsync([Near(bob)], linked, Ct);

        var got = await WaitFor(() => bob.LocalMessages.FirstOrDefault());
        Assert.Equal("look [Potion]", got.Text);
        Assert.Equal(new ChatLink.Item(4551), Assert.Single(got.Links).Target);
    }

    [Fact]
    public async Task ABlockedPlayersMessagesAreNeverShown() {
        var alice = await this._server.RegisterAsync("Alice Blocking Local");
        var bob = await this._server.RegisterAsync("Bob Blocked Local");
        var carol = await this._server.RegisterAsync("Carol Friendly Local");
        alice.Session.BlockUser(bob.UserId);

        await bob.Session.SendLocalAsync([Near(alice)], Say("let me in"), Ct);
        await carol.Session.SendLocalAsync([Near(alice)], Say("after bob"), Ct);

        // Carol's came after Bob's, through the same queue: Bob's was dropped, not still on its way.
        Assert.Equal("after bob", (await WaitFor(() => alice.LocalMessages.FirstOrDefault())).Text);
        Assert.Single(alice.LocalMessages);
    }

    // ================================================================ what the receiving session checks

    [Fact]
    public async Task TheSendersKeysAreTrustedOnFirstUseAndHeldFromThen() {
        var alice = await this._server.RegisterAsync("Alice First Use");
        var bob = await this._server.RegisterAsync("Bob First Use");
        Assert.False(bob.Store.Load().PinnedIdentities.ContainsKey(alice.UserId));

        await this._server.SendAndSettleAsync(bob, Forge(alice, bob, "first"));

        Assert.Equal("first", Assert.Single(bob.LocalMessages).Text);
        Assert.Equal(alice.Keys().SigningKeyArray(), bob.Store.Load().PinnedIdentities[alice.UserId].SigningPublicKey);
    }

    [Fact]
    public async Task OneSignedWithOtherKeysThanTheSendersIsDropped() {
        var alice = await this._server.RegisterAsync("Alice Forged Local");
        var bob = await this._server.RegisterAsync("Bob Forged Local");
        await this._server.SendAndSettleAsync(bob, Forge(alice, bob, "genuine"));
        using var mallory = IdentityKeys.Generate();

        // Signed by someone else, claiming Alice's own keys.
        await this._server.SendAndSettleAsync(bob, Forge(alice, bob, "forged", signWith: mallory));

        Assert.Equal(["genuine"], bob.LocalMessages.Select(m => m.Text));
        Assert.DoesNotContain(bob.Notices, notice => notice.Kind == NoticeKind.KeyChanged);
    }

    [Fact]
    public async Task OneUnderKeysOtherThanThoseHeldIsDroppedAndTheChangeIsNeverSilent() {
        var alice = await this._server.RegisterAsync("Alice Swapped Local");
        var bob = await this._server.RegisterAsync("Bob Swapped Local");
        await this._server.SendAndSettleAsync(bob, Forge(alice, bob, "genuine"));
        using var mallory = IdentityKeys.Generate();

        // The server claims Alice has other keys now, and a message signed with them.
        await this._server.SendAndSettleAsync(bob, Forge(alice, bob, "swapped", signWith: mallory, claim: mallory.ToBundle()));

        Assert.Equal(["genuine"], bob.LocalMessages.Select(m => m.Text));
        // Told, as for any other change of someone's keys; held from now on, with the warning.
        var warning = Assert.Single(bob.Notices, notice => notice.Kind == NoticeKind.KeyChanged);
        Assert.Contains(alice.Name, warning.Text);
        Assert.True(bob.Store.Load().PinnedIdentities[alice.UserId].KeyChangeUnacknowledged);
    }

    [Fact]
    public async Task OneMeantForSomeoneElseIsDropped() {
        var alice = await this._server.RegisterAsync("Alice Misdelivered");
        var bob = await this._server.RegisterAsync("Bob Misdelivered");
        var carol = await this._server.RegisterAsync("Carol Misdelivered");

        await this._server.SendAndSettleAsync(bob, Forge(alice, carol, "for carol"));

        Assert.Empty(bob.LocalMessages);
    }

    [Fact]
    public async Task ReplaysAndOldMessagesAreDropped() {
        var alice = await this._server.RegisterAsync("Alice Replayed Local");
        var bob = await this._server.RegisterAsync("Bob Replayed Local");
        var once = Forge(alice, bob, "once");

        await this._server.SendAndSettleAsync(bob, once, once.Clone());
        await this._server.SendAndSettleAsync(bob, Forge(alice, bob, "from an hour ago", DateTimeOffset.UtcNow.AddHours(-1)));
        await this._server.SendAndSettleAsync(bob, Forge(alice, bob, "from the future", DateTimeOffset.UtcNow.AddHours(1)));

        Assert.Equal(["once"], bob.LocalMessages.Select(m => m.Text));
    }

    [Fact]
    public async Task ItsOwnMessagesComingBackAreDropped() {
        var alice = await this._server.RegisterAsync("Alice Echo Local");

        await this._server.SendAndSettleAsync(alice, Forge(alice, alice, "to myself"));

        Assert.Empty(alice.LocalMessages);
    }

    // ================================================================ older plugins and servers

    [Fact]
    public async Task AnOlderPluginNeverSeesLocalChat() {
        var alice = await this._server.RegisterAsync("Alice New Plugin");
        var bob = await this._server.RegisterAsync("Bob Old Plugin", options: this._server.Options(offerLocalChat: false));
        Assert.False(bob.Session.LocalChatAvailable);

        // The server takes it (it doesn't tell anyone who is online, or on which version) and passes nothing on.
        Assert.Equal(1, (await alice.Session.SendLocalAsync([Near(bob)], Say("can you see this?"), Ct)).Sent);
        await alice.Session.PingAsync(Ct);
        await bob.Session.PingAsync(Ct);

        Assert.DoesNotContain(bob.Session.GetTrace(), entry => entry.Summary.Contains(nameof(Event.KindOneofCase.LocalMessage)));
        // And it can't send: the server didn't agree to it with that connection.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => bob.Session.SendLocalAsync([Near(alice)], Say("hi"), Ct));
        Assert.Equal(LocalChatWords.NotOnThisServer.Technical, refused.Message);
        var raw = LocalCrypto.Seal(new Content { Text = new TextContent { Text = "hi" } }, bob.LoadIdentity(), bob.UserId,
            [(alice.UserId, alice.Keys().AgreementKeyArray())], DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var error = await Assert.ThrowsAsync<ServerErrorException>(() => bob.Session.SendRawAsync(new ClientFrame { SendLocalMessage = raw }, Ct));
        Assert.Equal(ErrorCode.InvalidRequest, error.Code);
    }

    [Fact]
    public async Task AServerCanTurnLocalChatOff() {
        await using var server = new Harness(settings: ("LookingGlass:Limits:MaxLocalRecipients", "0"));
        var alice = await server.RegisterAsync("Alice No Local");
        var bob = await server.RegisterAsync("Bob No Local");

        Assert.False(alice.Session.LocalChatAvailable);
        Assert.Equal(0u, alice.Session.Snapshot.Limits!.MaxLocalRecipients);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => alice.Session.SendLocalAsync([Near(bob)], Say("hi"), Ct));
        Assert.Equal(LocalChatWords.NotOnThisServer.Technical, refused.Message);
    }

    // ================================================================ what the server checks

    private static SendLocalMessage Seal(TestClient from, IEnumerable<TestClient> to, string text = "hi") {
        using var keys = from.LoadIdentity();
        return LocalCrypto.Seal(new Content { Text = new TextContent { Text = text } }, keys, from.UserId,
            to.Select(client => (client.UserId, client.Keys().AgreementKeyArray())).ToList(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private static async Task<ErrorCode> Refused(TestClient from, SendLocalMessage message) =>
        (await Assert.ThrowsAsync<ServerErrorException>(() => from.Session.SendRawAsync(new ClientFrame { SendLocalMessage = message }, Ct))).Code;

    [Fact]
    public async Task TheServerRefusesMalformedOrForgedCopies() {
        // Each refusal spends one of the sender's local messages, so this server allows more at once.
        await using var server = new Harness(settings: ("LookingGlass:Limits:LocalMessageBurst", "100"));
        var alice = await server.RegisterAsync("Alice Malformed Local");
        var bob = await server.RegisterAsync("Bob Malformed Local");
        var carol = await server.RegisterAsync("Carol Malformed Local");

        Assert.Equal(ErrorCode.InvalidRequest, await Refused(alice, new SendLocalMessage { MessageId = ByteString.CopyFrom(new byte[16]) }));
        Assert.Equal(ErrorCode.InvalidRequest, await Refused(alice, Seal(alice, [alice])));

        var twice = Seal(alice, [bob]);
        twice.Copies.Add(twice.Copies[0].Clone());
        Assert.Equal(ErrorCode.InvalidRequest, await Refused(alice, twice));

        var shortId = Seal(alice, [bob]);
        shortId.MessageId = ByteString.CopyFrom(new byte[8]);
        Assert.Equal(ErrorCode.InvalidRequest, await Refused(alice, shortId));

        // Bob's copy readdressed to Carol: its signature doesn't cover her.
        var readdressed = Seal(alice, [bob]);
        readdressed.Copies[0].RecipientId = carol.UserId;
        Assert.Equal(ErrorCode.InvalidRequest, await Refused(alice, readdressed));

        // Signed by Bob, sent by Alice.
        Assert.Equal(ErrorCode.InvalidRequest, await Refused(alice, Seal(bob, [carol])));

        var huge = Seal(alice, [bob]);
        huge.Ciphertext = ByteString.CopyFrom(new byte[5000]);
        Assert.Equal(ErrorCode.TooLarge, await Refused(alice, huge));

        Assert.Empty(bob.LocalMessages);
        Assert.Empty(carol.LocalMessages);
    }

    [Fact]
    public async Task RecipientsPerMessageAreCapped() {
        await using var server = new Harness(settings: ("LookingGlass:Limits:MaxLocalRecipients", "2"));
        var alice = await server.RegisterAsync("Alice Capped Local");
        var friends = new List<TestClient>();
        foreach (var name in new[] { "Bob Capped Local", "Carol Capped Local", "Dave Capped Local" }) {
            friends.Add(await server.RegisterAsync(name));
        }

        Assert.Equal(ErrorCode.TooLarge, await Refused(alice, Seal(alice, friends)));

        // The plugin sends to the first ones it is given (the closest) only.
        Assert.Equal(2, (await alice.Session.SendLocalAsync(friends.Select(Near).ToList(), Say("the closest two"), Ct)).Sent);
        await WaitFor(() => friends[1].LocalMessages.FirstOrDefault());
        Assert.Empty(friends[2].LocalMessages);
    }

    [Fact]
    public async Task LocalMessagesAreRateLimitedBySettings() {
        await using var server = new Harness(settings: [("LookingGlass:Limits:LocalMessageBurst", "2"), ("LookingGlass:Limits:LocalMessageIntervalSeconds", "600")]);
        var alice = await server.RegisterAsync("Alice Chatty Local");
        var bob = await server.RegisterAsync("Bob Chatty Local");

        await alice.Session.SendLocalAsync([Near(bob)], Say("one"), Ct);
        await alice.Session.SendLocalAsync([Near(bob)], Say("two"), Ct);
        var refused = await Assert.ThrowsAsync<ServerErrorException>(() => alice.Session.SendLocalAsync([Near(bob)], Say("three"), Ct));

        Assert.Equal(ErrorCode.RateLimited, refused.Code);
        PlainLanguage.AssertPlain(refused.ServerMessage);
    }

    [Fact]
    public async Task LocalSettingsOutOfRangeAreRefused() {
        Assert.Null(new LookingGlass.Server.LimitOptions().Problem());
        Assert.Null(new LookingGlass.Server.LimitOptions { MaxLocalRecipients = 0 }.Problem());
        Assert.Null(new LookingGlass.Server.LimitOptions { MaxLocalRecipients = 200 }.Problem());
        Assert.Contains("MaxLocalRecipients", new LookingGlass.Server.LimitOptions { MaxLocalRecipients = 201 }.Problem());
        Assert.Contains("MaxLocalRecipients", new LookingGlass.Server.LimitOptions { MaxLocalRecipients = -1 }.Problem());
        Assert.Contains("LocalMessageBurst", new LookingGlass.Server.LimitOptions { LocalMessageBurst = 0 }.Problem());
        Assert.Contains("LocalMessageIntervalSeconds", new LookingGlass.Server.LimitOptions { LocalMessageIntervalSeconds = 0 }.Problem());

        var logs = new CapturingLoggerProvider();
        await ExitCodeGate.WaitAsync(Ct);
        var exitCode = Environment.ExitCode;
        var directory = Path.Combine(Path.GetTempPath(), "lgt-" + Guid.NewGuid().ToString("N"));
        try {
            Exception? failed = null;
            try {
                await using var server = new Harness(directory, logs: logs, settings: ("LookingGlass:Limits:MaxLocalRecipients", "1000"));
                await using var raw = await server.ConnectRawAsync();
            } catch (Exception ex) {
                failed = ex;
            }

            Assert.NotNull(failed);
            Assert.Contains("LookingGlass:Limits:MaxLocalRecipients", Assert.Single(logs.AtLeast(Microsoft.Extensions.Logging.LogLevel.Critical)));
        } finally {
            Environment.ExitCode = exitCode;
            ExitCodeGate.Release();
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task TheServerLogsNoNamesOrWords() {
        var logs = new CapturingLoggerProvider();
        await using var server = new Harness(logs: logs);
        var alice = await server.RegisterAsync("Alice Quiet Local");
        var bob = await server.RegisterAsync("Bob Quiet Local");

        await alice.Session.SendLocalAsync([Near(bob)], Say("a secret between friends"), Ct);
        await WaitFor(() => bob.LocalMessages.FirstOrDefault());

        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains("secret") || entry.Message.Contains("Quiet Local"));
    }

    [Fact]
    public async Task TheServerKeepsNothing() {
        var alice = await this._server.RegisterAsync("Alice Nothing Kept");
        var bob = await this._server.RegisterAsync("Bob Nothing Kept", options: this._server.Options());
        await bob.Session.DisposeAsync();

        // Bob is offline: his copy goes nowhere, and isn't there when he comes back.
        Assert.Equal(1, (await alice.Session.SendLocalAsync([Near(bob)], Say("while you were out"), Ct)).Sent);
        var back = await this._server.RestartAsync(bob);
        await back.Session.PingAsync(Ct);

        Assert.Empty(back.LocalMessages);
    }
}
