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
                KeyCommitment = sent.KeyCommitment,
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
    public async Task ATooLongMessageIsRefusedBeforeAnyoneIsLookedUp() {
        var alice = await this._server.RegisterAsync("Alice Wordy Local");
        var bob = await this._server.RegisterAsync("Bob Wordy Local");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => alice.Session.SendLocalAsync([Near(bob)], Say(new string('x', 5000)), Ct));

        Assert.Equal("That message is too long.", refused.Message);
        Assert.DoesNotContain(alice.Session.GetTrace(), entry => entry.Outgoing && entry.Summary.EndsWith(nameof(ClientFrame.BodyOneofCase.LookupUser)));
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
    public async Task TheSendersKeysAreHeldOnlyOnceTheMessageIsShown() {
        var alice = await this._server.RegisterAsync("Alice First Use");
        var bob = await this._server.RegisterAsync("Bob First Use");
        Assert.False(bob.Store.Load().PinnedIdentities.ContainsKey(alice.UserId));

        await this._server.SendAndSettleAsync(bob, Forge(alice, bob, "first"));

        // Opened and checked against the keys that came with it, but not held yet: the plugin hasn't said the sender is
        // near and a friend, so the message may never be shown.
        var first = Assert.Single(bob.LocalMessages);
        Assert.Equal("first", first.Text);
        Assert.False(bob.Store.Load().PinnedIdentities.ContainsKey(alice.UserId));

        // Shown: held from now on (trust on first use).
        Assert.Equal(LocalConfirmation.Show, bob.Session.ConfirmLocalSender(first));
        Assert.Equal(alice.Keys().SigningKeyArray(), bob.Store.Load().PinnedIdentities[alice.UserId].SigningPublicKey);
        // The same message isn't shown twice.
        Assert.Equal(LocalConfirmation.Replayed, bob.Session.ConfirmLocalSender(first));
    }

    [Fact]
    public async Task AMessageUnderFirstSeenKeysIsntShownIfOtherKeysWereHeldMeanwhile() {
        var alice = await this._server.RegisterAsync("Alice Raced Local");
        var bob = await this._server.RegisterAsync("Bob Raced Local");
        using var mallory = IdentityKeys.Generate();

        // Two first messages, before either is shown: the server's keys for Alice, and other ones.
        await this._server.SendAndSettleAsync(bob, Forge(alice, bob, "genuine"), Forge(alice, bob, "other keys", signWith: mallory, claim: mallory.ToBundle()));
        var (genuine, other) = (bob.LocalMessages.First(), bob.LocalMessages.Last());

        Assert.Equal(LocalConfirmation.Show, bob.Session.ConfirmLocalSender(genuine));
        Assert.Equal(LocalConfirmation.OtherKeysHeld, bob.Session.ConfirmLocalSender(other));
        Assert.Equal(alice.Keys().SigningKeyArray(), bob.Store.Load().PinnedIdentities[alice.UserId].SigningPublicKey);
        Assert.DoesNotContain(bob.Notices, notice => notice.Kind == NoticeKind.KeyChanged);
    }

    [Fact]
    public async Task ASenderHeldUnderAnotherNameIsntShownButCanBeHinted() {
        var alice = await this._server.RegisterAsync("Alice Named Local");
        var bob = await this._server.RegisterAsync("Bob Named Local");
        await this._server.SendAndSettleAsync(bob, Forge(alice, bob, "hello"));
        Assert.Equal(LocalConfirmation.Show, bob.Session.ConfirmLocalSender(bob.LocalMessages.Single()));

        // The server says Alice's account has another name now (a rename or world transfer; or a server passing her off as a
        // friend standing near Bob), with Alice's own keys.
        var renamed = Forge(alice, bob, "who am I");
        renamed.LocalMessage.Sender.User.Name = "Carol Somebody Else";
        await this._server.SendAndSettleAsync(bob, renamed);

        // Not shown under either name: the plugin may only hint, if "Carol" is a friend near Bob.
        Assert.Single(bob.LocalMessages);
        var hint = Assert.Single(bob.LocalUnchecked);
        Assert.Equal(LocalUncheckedReason.Renamed, hint.Reason);
        Assert.Equal("Carol Somebody Else", hint.Sender.Name);
        Assert.Equal(alice.Name, bob.Store.Load().PinnedIdentities[alice.UserId].Name);
    }

    [Fact]
    public async Task AnotherAccountClaimingAHeldFriendsNameIsntShownOrHeld() {
        var alice = await this._server.RegisterAsync("Alice Real Local");
        var bob = await this._server.RegisterAsync("Bob Fooled Local");
        var mallory = await this._server.RegisterAsync("Mallory Impostor Local");
        await this._server.SendAndSettleAsync(bob, Forge(alice, bob, "the real one"));
        Assert.Equal(LocalConfirmation.Show, bob.Session.ConfirmLocalSender(bob.LocalMessages.Single()));

        // Mallory's own account, signed with her own keys, but named as Alice by the server.
        var impostor = Forge(mallory, bob, "it's me, Alice");
        impostor.LocalMessage.Sender.User.Name = alice.Name;
        await this._server.SendAndSettleAsync(bob, impostor);

        // It opens (no keys were held for that account), but the plugin's confirming it refuses: Alice is held as another account.
        var claimed = bob.LocalMessages.Last();
        Assert.Equal(alice.Name, claimed.Sender.Name);
        Assert.Equal(LocalConfirmation.NameHeldByAnother, bob.Session.ConfirmLocalSender(claimed));
        Assert.False(bob.Store.Load().PinnedIdentities.ContainsKey(mallory.UserId));
        Assert.DoesNotContain(bob.Notices, notice => notice.Kind == NoticeKind.NameNowAnotherAccount);
    }

    [Fact]
    public async Task OneSignedWithOtherKeysThanTheSendersIsDropped() {
        var alice = await this._server.RegisterAsync("Alice Forged Local");
        var bob = await this._server.RegisterAsync("Bob Forged Local");
        await this._server.SendAndSettleAsync(bob, Forge(alice, bob, "genuine"));
        Assert.Equal(LocalConfirmation.Show, bob.Session.ConfirmLocalSender(bob.LocalMessages.Single()));
        using var mallory = IdentityKeys.Generate();

        // Signed by someone else, claiming Alice's own keys.
        await this._server.SendAndSettleAsync(bob, Forge(alice, bob, "forged", signWith: mallory));

        Assert.Equal(["genuine"], bob.LocalMessages.Select(m => m.Text));
        Assert.DoesNotContain(bob.Notices, notice => notice.Kind == NoticeKind.KeyChanged);
    }

    [Fact]
    public async Task OneUnderKeysOtherThanThoseHeldIsDroppedAndChangesNothing() {
        var alice = await this._server.RegisterAsync("Alice Swapped Local");
        var bob = await this._server.RegisterAsync("Bob Swapped Local");
        await this._server.SendAndSettleAsync(bob, Forge(alice, bob, "genuine"));
        Assert.Equal(LocalConfirmation.Show, bob.Session.ConfirmLocalSender(bob.LocalMessages.Single()));
        // Bob compared fingerprints with Alice.
        bob.Session.AcknowledgeKeyChange(alice.UserId, alice.Keys().Fingerprint, compared: true);
        using var mallory = IdentityKeys.Generate();

        // The server claims Alice has other keys now, and a message signed with them.
        await this._server.SendAndSettleAsync(bob, Forge(alice, bob, "swapped", signWith: mallory, claim: mallory.ToBundle()));

        Assert.Equal(["genuine"], bob.LocalMessages.Select(m => m.Text));
        // Passed on only as a message that couldn't be checked, for the plugin to hint at if Alice is a friend near Bob.
        Assert.Equal(LocalUncheckedReason.KeysChanged, Assert.Single(bob.LocalUnchecked).Reason);
        // A local message never changes the keys held for anyone: changes come through lookups and channels, with their warnings.
        var pinned = bob.Store.Load().PinnedIdentities[alice.UserId];
        Assert.Equal(alice.Keys().SigningKeyArray(), pinned.SigningPublicKey);
        Assert.True(pinned.Compared);
        Assert.False(pinned.KeyChangeUnacknowledged);
        Assert.DoesNotContain(bob.Notices, notice => notice.Kind == NoticeKind.KeyChanged);

        // So Bob's next local message is still sealed to Alice's own keys.
        Assert.Equal(1, (await bob.Session.SendLocalAsync([Near(alice)], Say("still you?"), Ct)).Sent);
        Assert.Equal("still you?", (await WaitFor(() => alice.LocalMessages.FirstOrDefault())).Text);
    }

    [Fact]
    public async Task KeyChangesFromManySendersChangeNothingEither() {
        var bob = await this._server.RegisterAsync("Bob Many Senders");
        var senders = new List<TestClient>();
        foreach (var name in new[] { "Alice Many Senders", "Carol Many Senders", "Dave Many Senders", "Erin Many Senders" }) {
            var sender = await this._server.RegisterAsync(name);
            await this._server.SendAndSettleAsync(bob, Forge(sender, bob, "genuine"));
            Assert.Equal(LocalConfirmation.Show, bob.Session.ConfirmLocalSender(bob.LocalMessages.Last()));
            senders.Add(sender);
        }

        using var mallory = IdentityKeys.Generate();
        await this._server.SendAndSettleAsync(bob, senders.Select(sender => Forge(sender, bob, "swapped", signWith: mallory, claim: mallory.ToBundle())).ToArray());

        Assert.Equal(senders.Count, bob.LocalMessages.Count);
        var pinned = bob.Store.Load().PinnedIdentities;
        Assert.All(senders, sender => Assert.Equal(sender.Keys().SigningKeyArray(), pinned[sender.UserId].SigningPublicKey));
        Assert.DoesNotContain(bob.Notices, notice => notice.Kind == NoticeKind.KeyChanged);
    }

    [Fact]
    public async Task OneMeantForSomeoneElseIsDropped() {
        var alice = await this._server.RegisterAsync("Alice Misdelivered");
        var bob = await this._server.RegisterAsync("Bob Misdelivered");
        var carol = await this._server.RegisterAsync("Carol Misdelivered");

        await this._server.SendAndSettleAsync(bob, Forge(alice, carol, "for carol"));

        Assert.Empty(bob.LocalMessages);
        // And one that fails pins nobody: the keys that came with it aren't taken on first use.
        Assert.False(bob.Store.Load().PinnedIdentities.ContainsKey(alice.UserId));
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

    private static int Lookups(TestClient client) =>
        client.Session.GetTrace().Count(entry => entry.Outgoing && entry.Summary.EndsWith(nameof(ClientFrame.BodyOneofCase.LookupUser)));

    /// <summary>
    /// A server can fake a "couldn't be checked" hint (a held account, other keys, named as a friend standing near). It mustn't
    /// prime a key swap: nothing is forgotten when it arrives, and afterwards only what was looked up under the name held for
    /// that account, and only if the server gave that very name.
    /// </summary>
    [Fact]
    public async Task AFakedKeyChangeForgetsNoLookups() {
        var alice = await this._server.RegisterAsync("Alice Looked Up Local");
        var bob = await this._server.RegisterAsync("Bob Looking Local");
        await bob.Session.SendLocalAsync([Near(alice)], Say("hi"), Ct);
        var before = Lookups(bob);
        using var mallory = IdentityKeys.Generate();

        var faked = Forge(alice, bob, "x", signWith: mallory, claim: mallory.ToBundle());
        faked.LocalMessage.Sender.User.Name = "Carol Nearby Local";
        await this._server.SendAndSettleAsync(bob, faked);

        var hint = Assert.Single(bob.LocalUnchecked);
        Assert.Equal("Carol Nearby Local", hint.Sender.Name);
        Assert.False(bob.Session.ForgetLookupAfterHint(hint));
        await bob.Session.SendLocalAsync([Near(alice)], Say("still you?"), Ct);
        Assert.Equal(before, Lookups(bob));
    }

    [Fact]
    public async Task AKeyChangeUnderTheNameHeldForgetsThatLookupOnceJudged() {
        var alice = await this._server.RegisterAsync("Alice Set Up Again Local");
        var bob = await this._server.RegisterAsync("Bob Noticing Local");
        await bob.Session.SendLocalAsync([Near(alice)], Say("hi"), Ct);
        var before = Lookups(bob);
        using var mallory = IdentityKeys.Generate();

        await this._server.SendAndSettleAsync(bob, Forge(alice, bob, "x", signWith: mallory, claim: mallory.ToBundle()));
        // Not on arrival: only once the plugin found the sender near and a friend.
        await bob.Session.SendLocalAsync([Near(alice)], Say("before"), Ct);
        Assert.Equal(before, Lookups(bob));

        Assert.True(bob.Session.ForgetLookupAfterHint(Assert.Single(bob.LocalUnchecked)));
        await bob.Session.SendLocalAsync([Near(alice)], Say("after"), Ct);
        Assert.Equal(before + 1, Lookups(bob));
    }

    [Fact]
    public async Task AHeldSenderWithNoNameHeldCantTakeAnothersName() {
        var alice = await this._server.RegisterAsync("Alice Unnamed Local");
        var bob = await this._server.RegisterAsync("Bob Holding Local");
        var carol = await this._server.RegisterAsync("Carol Held Local");
        await this._server.SendAndSettleAsync(bob, Forge(carol, bob, "carol"), Forge(alice, bob, "alice"));
        Assert.All(bob.LocalMessages, message => Assert.Equal(LocalConfirmation.Show, bob.Session.ConfirmLocalSender(message)));

        // Alice held with no name, as a channel's membership log can pin someone.
        await bob.Session.DisposeAsync();
        var secrets = bob.Store.Load();
        secrets.PinnedIdentities[alice.UserId].Name = "";
        secrets.PinnedIdentities[alice.UserId].WorldName = "";
        bob.Store.Save(secrets);
        var back = await this._server.RestartAsync(bob);

        // Alice's own account and keys, named as Carol, who is held as another account.
        var claimed = Forge(alice, back, "it's Carol");
        claimed.LocalMessage.Sender.User.Name = carol.Name;
        await this._server.SendAndSettleAsync(back, claimed);
        Assert.Equal(LocalConfirmation.NameHeldByAnother, back.Session.ConfirmLocalSender(back.LocalMessages.Single()));

        // Under her own name: shown, and the name held with her keys from then on.
        await this._server.SendAndSettleAsync(back, Forge(alice, back, "really Alice"));
        Assert.Equal(LocalConfirmation.Show, back.Session.ConfirmLocalSender(back.LocalMessages.Last()));
        await back.Session.DisposeAsync();
        Assert.Equal(alice.Name, back.Store.Load().PinnedIdentities[alice.UserId].Name);
    }

    [Fact]
    public void ThePairLimitsMemoryIsCapped() {
        var limits = new LookingGlass.Server.Services.KeyedRateLimits<(long, long)>(1, 5) { MaxKeys = 10 };
        for (var i = 0; i < 25; i++) {
            Assert.True(limits.TryTake((i, 1)));
        }

        Assert.InRange(limits.TrackedKeys, 1, 10);
        Assert.Equal(30, new LookingGlass.Server.LimitOptions().LocalMessagesBetweenBurst);
        Assert.Equal(2, new LookingGlass.Server.LimitOptions().LocalMessagesBetweenIntervalSeconds);
    }

    [Fact]
    public async Task OnlyMessagesShownAreRememberedAgainstReplays() {
        var alice = await this._server.RegisterAsync("Alice Remembered Local");
        var bob = await this._server.RegisterAsync("Bob Remembered Local");
        var carol = await this._server.RegisterAsync("Carol Stranger Local");

        // A stranger's message, never shown (they aren't near, or not a friend): nothing kept for them.
        await this._server.SendAndSettleAsync(bob, Forge(carol, bob, "not shown"));
        // Alice's, shown.
        await this._server.SendAndSettleAsync(bob, Forge(alice, bob, "shown"));
        Assert.Equal(LocalConfirmation.Show, bob.Session.ConfirmLocalSender(bob.LocalMessages.Last()));
        await bob.Session.DisposeAsync();

        var kept = bob.Store.Load().NewestMessageTimes[ClientSession.LocalReplayKey];
        Assert.Equal([alice.UserId], kept.Keys);
        Assert.False(bob.Store.Load().PinnedIdentities.ContainsKey(carol.UserId));
    }

    /// <summary>
    /// What one sender may send one player (LocalMessagesBetweenBurst, LocalMessagesBetweenIntervalSeconds), checked first:
    /// a couple of accounts can't use up what everyone together may send them, so their friends still get through.
    /// </summary>
    [Fact]
    public async Task WhatOneSenderMaySendOnePlayerIsLimitedSoOthersStillGetThrough() {
        await using var server = new Harness(settings: [
            ("LookingGlass:Limits:LocalMessagesBetweenBurst", "2"), ("LookingGlass:Limits:LocalMessagesBetweenIntervalSeconds", "600"),
            ("LookingGlass:Limits:LocalMessageBurst", "10"),
        ]);
        var bob = await server.RegisterAsync("Bob Targeted Local");
        var mallory = await server.RegisterAsync("Mallory Spamming Local");
        var alice = await server.RegisterAsync("Alice Friendly Local");

        for (var i = 0; i < 5; i++) {
            await mallory.Session.SendLocalAsync([Near(bob)], Say($"spam {i}"), Ct);
        }

        await alice.Session.SendLocalAsync([Near(bob)], Say("hi bob"), Ct);
        await server.SendAndSettleAsync(bob);

        Assert.Equal(2, bob.LocalMessages.Count(m => m.Sender.UserId == mallory.UserId));
        Assert.Contains(bob.LocalMessages, m => m.Text == "hi bob");
    }

    [Fact]
    public async Task AReplayAfterARestartIsDroppedToo() {
        var alice = await this._server.RegisterAsync("Alice Restart Local");
        var bob = await this._server.RegisterAsync("Bob Restart Local");
        var once = Forge(alice, bob, "once");
        var older = Forge(alice, bob, "three minutes before", DateTimeOffset.UtcNow.AddMinutes(-3));
        await this._server.SendAndSettleAsync(bob, once);
        Assert.Equal(LocalConfirmation.Show, bob.Session.ConfirmLocalSender(bob.LocalMessages.Single()));

        // Within 10 minutes, after Bob's plugin restarted (the in-memory seen-set is gone, the newest times aren't).
        await bob.Session.DisposeAsync();
        var restarted = await this._server.RestartAsync(bob);
        await this._server.SendAndSettleAsync(restarted, once.Clone(), older);
        Assert.Empty(restarted.LocalMessages);

        await this._server.SendAndSettleAsync(restarted, Forge(alice, bob, "new"));
        Assert.Equal(["new"], restarted.LocalMessages.Select(m => m.Text));
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

    /// <summary>
    /// What one player may be sent, by everyone together (LocalMessagesReceivedBurst, LocalMessagesReceivedIntervalSeconds):
    /// past it their copies are dropped, and the senders aren't told (that would say who is online).
    /// </summary>
    [Fact]
    public async Task WhatOnePlayerIsSentIsLimitedBySettings() {
        await using var server = new Harness(settings: [("LookingGlass:Limits:LocalMessagesReceivedBurst", "3"), ("LookingGlass:Limits:LocalMessagesReceivedIntervalSeconds", "600"),
            ("LookingGlass:Limits:LocalMessagesBetweenBurst", "2"), ("LookingGlass:Limits:LocalMessagesBetweenIntervalSeconds", "1200")]);
        var bob = await server.RegisterAsync("Bob Flooded Local");
        var carol = await server.RegisterAsync("Carol Bystander Local");
        var senders = new List<TestClient>();
        foreach (var name in new[] { "Alice Flooding Local", "Dave Flooding Local" }) {
            senders.Add(await server.RegisterAsync(name));
        }

        foreach (var sender in senders) {
            for (var i = 0; i < 2; i++) {
                // Each sender within its own allowance: every one is taken.
                Assert.Equal(1, (await sender.Session.SendLocalAsync([Near(bob)], Say($"{sender.Name} {i}"), Ct)).Sent);
            }
        }

        // Someone else's allowance is their own.
        await senders[0].Session.SendLocalAsync([Near(carol)], Say("for carol"), Ct);
        await WaitFor(() => carol.LocalMessages.FirstOrDefault());
        // Everything the server passed on to Bob has been handled once this has.
        await server.SendAndSettleAsync(bob);
        Assert.Equal(3, bob.LocalMessages.Count);
    }

    /// <summary>
    /// A player whose connection is slow to take what it is sent loses local messages rather than the connection: a local
    /// message is only queued while the queue is less than half full, so it can't fill the room channel events need either.
    /// </summary>
    [Fact]
    public void ASlowConnectionDropsLocalMessagesInsteadOfBeingClosed() {
        var connection = new LookingGlass.Server.Realtime.ClientConnection(new UnusedWebSocket(), "203.0.113.7", 128 * 1024, 4,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var local = new Event { LocalMessage = new LocalMessage { MessageId = ByteString.CopyFrom(new byte[16]) } };

        Assert.Equal([true, true, false, false, false], Enumerable.Range(0, 5).Select(_ => connection.TrySendDroppable(local)).ToList());
        Assert.False(connection.Aborted.IsCancellationRequested);

        // Channel events still fit, and the connection is closed only when they don't, as before.
        connection.SendEvent(new Event { Announcement = new Announcement { Text = "a" } });
        connection.SendEvent(new Event { Announcement = new Announcement { Text = "b" } });
        Assert.False(connection.Aborted.IsCancellationRequested);
        connection.SendEvent(new Event { Announcement = new Announcement { Text = "c" } });
        Assert.True(connection.Aborted.IsCancellationRequested);
    }

    /// <summary>A socket for a connection that is never run (nothing is sent or received on it).</summary>
    private sealed class UnusedWebSocket : System.Net.WebSockets.WebSocket {
        public override System.Net.WebSockets.WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override System.Net.WebSockets.WebSocketState State => System.Net.WebSockets.WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort() {
        }

        public override Task CloseAsync(System.Net.WebSockets.WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Dispose() {
        }

        public override Task<System.Net.WebSockets.WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotSupportedException();

        public override Task SendAsync(ArraySegment<byte> buffer, System.Net.WebSockets.WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
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
        Assert.Contains("LocalMessagesReceivedBurst", new LookingGlass.Server.LimitOptions { LocalMessagesReceivedBurst = 0 }.Problem());
        Assert.Contains("LocalMessagesReceivedIntervalSeconds", new LookingGlass.Server.LimitOptions { LocalMessagesReceivedIntervalSeconds = 86_401 }.Problem());
        Assert.Contains("LocalMessagesBetweenBurst", new LookingGlass.Server.LimitOptions { LocalMessagesBetweenBurst = 0 }.Problem());
        Assert.Contains("LocalMessagesBetweenIntervalSeconds", new LookingGlass.Server.LimitOptions { LocalMessagesBetweenIntervalSeconds = 0 }.Problem());
        // One sender's allowance must stay smaller and slower than everyone's together, as for invites.
        Assert.Contains("LocalMessagesBetweenBurst", new LookingGlass.Server.LimitOptions { LocalMessagesBetweenBurst = 120 }.Problem());
        Assert.Contains("LocalMessagesBetweenIntervalSeconds", new LookingGlass.Server.LimitOptions { LocalMessagesBetweenIntervalSeconds = 1 }.Problem());

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
