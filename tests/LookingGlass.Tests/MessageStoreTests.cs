using Google.Protobuf;
using LookingGlass.Core.Client;
using LookingGlass.Core.Crypto;
using LookingGlass.Protocol;
using LookingGlass.Server.Data;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// Message catch-up on the server: the messages it relays are kept as relayed (ciphertext and envelope, nothing it can
/// read), numbered per channel, for 7 days and 5,000 per channel by default, and swept; and only a member gets them back,
/// only those sent under keys their place held (never from before they joined, after they left, to a place under keys the
/// account no longer has, or one removed from its list), in pages, within a read budget.
/// </summary>
public sealed class MessageStoreTests : IAsyncLifetime {
    private Harness _server = null!;

    public ValueTask InitializeAsync() {
        this._server = new Harness();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() {
        await this._server.DisposeAsync();
        DeleteDirectory(this._server.DataDirectory);
    }

    /// <summary>A channel of <paramref name="admin"/>'s with the others as members, each holding its key.</summary>
    private static async Task<string> ChannelWith(TestClient admin, string name, params TestClient[] members) {
        var channelId = await admin.Session.CreateChannelAsync(name, Ct);
        foreach (var member in members) {
            await AddMemberAsync(admin, channelId, member);
        }

        return channelId;
    }

    /// <summary>Sends a message as the client's plugin would, but through the raw request, so the test knows exactly what was sent.</summary>
    private static async Task<ChatMessage> SendAsync(TestClient sender, string channelId, string text) {
        var epoch = sender.Session.Snapshot.FindChannel(channelId)!.Epoch;
        var message = sender.ForgeMessage(channelId, epoch, text, DateTimeOffset.UtcNow);
        var response = await sender.Session.SendRawAsync(new ClientFrame {
            SendMessage = new SendMessage {
                ChannelId = channelId, Epoch = epoch, MessageId = message.MessageId, TimestampUnixMs = message.TimestampUnixMs,
                Ciphertext = message.Ciphertext, Signature = message.Signature,
            },
        }, Ct);
        Assert.NotNull(response.Ack);
        return message;
    }

    /// <summary>One page of stored messages, as the client asks for them.</summary>
    internal static async Task<StoredMessages> FetchAsync(TestClient client, string channelId, ulong afterId = 0, uint withinSeconds = 3600) {
        var response = await client.Session.SendRawAsync(new ClientFrame {
            FetchMessages = new FetchMessages { ChannelId = channelId, AfterId = afterId, WithinSeconds = withinSeconds },
        }, Ct);
        return response.StoredMessages ?? throw new InvalidOperationException($"Unexpected response: {response.ResultCase}");
    }

    private static async Task<ErrorCode> RefusedAsync(TestClient client, string channelId) {
        var refused = await Assert.ThrowsAsync<ServerErrorException>(() => FetchAsync(client, channelId));
        return refused.Code;
    }

    /// <summary>A valid message of <paramref name="sender"/>'s, stored straight into the database as the server stores what it relays.</summary>
    private ulong Store(TestClient sender, string channelId, string text, int maxPerChannel = 5000, DateTimeOffset? relayedAt = null) {
        var epoch = this._server.Database.GetChannel(channelId)!.Epoch;
        var message = sender.ForgeMessage(channelId, epoch, text, DateTimeOffset.UtcNow);
        return this._server.Database.StoreMessage(message, (relayedAt ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds(), maxPerChannel)
               ?? throw new InvalidOperationException("Not stored.");
    }

    [Fact]
    public async Task RelayedMessagesAreKeptExactlyAsRelayedUnderGrowingNumbers() {
        var alice = await this._server.RegisterAsync("Alice Keeps");
        var bob = await this._server.RegisterAsync("Bob Away");
        var channelId = await ChannelWith(alice, "Kept Channel", bob);

        var first = await SendAsync(alice, channelId, "first");
        var second = await SendAsync(alice, channelId, "second");

        var page = await FetchAsync(bob, channelId);
        Assert.Equal([1UL, 2UL], page.Messages.Select(m => m.ServerId));
        Assert.False(page.More);
        Assert.Equal(2UL, page.LatestId);
        foreach (var (sent, kept) in new[] { first, second }.Zip(page.Messages)) {
            Assert.Equal(sent.MessageId, kept.MessageId);
            Assert.Equal(sent.Ciphertext, kept.Ciphertext);
            Assert.Equal(sent.Signature, kept.Signature);
            Assert.Equal(sent.TimestampUnixMs, kept.TimestampUnixMs);
            Assert.Equal(sent.Epoch, kept.Epoch);
            Assert.Equal(alice.UserId, kept.SenderId);
            Assert.Equal(channelId, kept.ChannelId);
            // Still verifies, exactly as relayed.
            Assert.True(ChannelCrypto.VerifyMessage(kept, alice.Keys().SigningPublicKey));
        }

        // Nothing it can read: the table holds what the relay held, and the number.
        var columns = new List<string>();
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(this._server.DataDirectory, "lookingglass.db")};Pooling=False")) {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM pragma_table_info('messages') ORDER BY cid;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) {
                columns.Add(reader.GetString(0));
            }
        }

        Assert.Equal(["channel_id", "seq", "epoch", "sender_id", "message_id", "sent_at", "relayed_at", "ciphertext", "signature"], columns);
    }

    [Fact]
    public async Task EachChannelNumbersItsOwnMessages() {
        var alice = await this._server.RegisterAsync("Alice Two Channels");
        var bob = await this._server.RegisterAsync("Bob Two Channels");
        var one = await ChannelWith(alice, "Channel One", bob);
        var two = await ChannelWith(alice, "Channel Two", bob);

        Assert.Equal(1UL, this.Store(alice, one, "a"));
        Assert.Equal(2UL, this.Store(alice, one, "b"));
        Assert.Equal(1UL, this.Store(alice, two, "c"));
        Assert.Equal([1UL, 2UL], (await FetchAsync(bob, one)).Messages.Select(m => m.ServerId));
        Assert.Equal([1UL], (await FetchAsync(bob, two)).Messages.Select(m => m.ServerId));
    }

    [Fact]
    public async Task NothingIsKeptUnderAnOldEpochOrWhileARekeyIsPending() {
        var alice = await this._server.RegisterAsync("Alice Epochs");
        var bob = await this._server.RegisterAsync("Bob Epochs");
        var channelId = await ChannelWith(alice, "Epoch Channel", bob);
        var epoch = this._server.Database.GetChannel(channelId)!.Epoch;
        var old = alice.ForgeMessage(channelId, epoch - 1, "old", DateTimeOffset.UtcNow);
        Assert.Null(this._server.Database.StoreMessage(old, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 5000));

        this._server.ExecuteSql("UPDATE channels SET rekey_pending = 1 WHERE channel_id = $id;", ("$id", channelId));
        var current = alice.ForgeMessage(channelId, epoch, "while pending", DateTimeOffset.UtcNow);
        Assert.Null(this._server.Database.StoreMessage(current, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 5000));
        Assert.Equal(0, this._server.Database.CountStoredMessages(channelId));
    }

    [Fact]
    public async Task AChannelKeepsAtMostItsCapTheOldestGoingFirst() {
        await using var server = new Harness(settings: ("LookingGlass:Messages:MaxPerChannel", "3"));
        var alice = await server.RegisterAsync("Alice Capped");
        var bob = await server.RegisterAsync("Bob Capped");
        var channelId = await ChannelWith(alice, "Capped Channel", bob);
        foreach (var text in new[] { "1", "2", "3", "4", "5" }) {
            await SendAsync(alice, channelId, text);
        }

        Assert.Equal(3, server.Database.CountStoredMessages(channelId));
        Assert.Equal([3UL, 4UL, 5UL], (await FetchAsync(bob, channelId)).Messages.Select(m => m.ServerId));
        Assert.Equal(3u, bob.Session.Snapshot.Limits!.MaxStoredMessagesPerChannel);
        Assert.Equal(7u, bob.Session.Snapshot.Limits.MessageKeepDays);
    }

    [Fact]
    public async Task TheSweepDeletesWhatIsOlderThanTheDaysKept() {
        var alice = await this._server.RegisterAsync("Alice Sweep");
        var bob = await this._server.RegisterAsync("Bob Sweep");
        var channelId = await ChannelWith(alice, "Swept Channel", bob);
        var now = DateTimeOffset.UtcNow;
        this.Store(alice, channelId, "eight days ago", relayedAt: now.AddDays(-8));
        this.Store(alice, channelId, "just over seven", relayedAt: now.AddDays(-7).AddMinutes(-1));
        this.Store(alice, channelId, "six days ago", relayedAt: now.AddDays(-6));
        this.Store(alice, channelId, "now", relayedAt: now);

        Assert.Equal(2, this._server.Database.SweepMessages(now.ToUnixTimeMilliseconds(), 7, 5000));
        Assert.Equal([3UL, 4UL], (await FetchAsync(bob, channelId, withinSeconds: 8 * 86400)).Messages.Select(m => m.ServerId));
    }

    [Fact]
    public async Task TheSweepAppliesALoweredCapAndOffDeletesEverything() {
        var alice = await this._server.RegisterAsync("Alice Lowered");
        var bob = await this._server.RegisterAsync("Bob Lowered");
        var channelId = await ChannelWith(alice, "Lowered Channel", bob);
        for (var i = 0; i < 6; i++) {
            this.Store(alice, channelId, $"message {i}");
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.Equal(4, this._server.Database.SweepMessages(now, 7, 2));
        Assert.Equal([5UL, 6UL], (await FetchAsync(bob, channelId)).Messages.Select(m => m.ServerId));
        Assert.Equal(2, this._server.Database.SweepMessages(now, 0, 5000));
        Assert.Equal(0, this._server.Database.CountStoredMessages(channelId));
    }

    [Fact]
    public async Task TheServerSweepsWhenItStarts() {
        var directory = Path.Combine(Path.GetTempPath(), "lgt-sweep-" + Guid.NewGuid().ToString("N"));
        try {
            string channelId;
            await using (var first = new Harness(directory)) {
                var alice = await first.RegisterAsync("Alice Restarts");
                var bob = await first.RegisterAsync("Bob Restarts");
                channelId = await ChannelWith(alice, "Restart Channel", bob);
                await SendAsync(alice, channelId, "old news");
                await SendAsync(alice, channelId, "fresh");
                first.ExecuteSql("UPDATE messages SET relayed_at = relayed_at - $days WHERE seq = 1;", ("$days", 8 * 86_400_000L));
                Assert.Equal(2, first.Database.CountStoredMessages(channelId));
            }

            await using var second = new Harness(directory);
            Assert.Equal(1, second.Database.CountStoredMessages(channelId));
        } finally {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task DisbandingOrAbandoningAChannelDeletesItsMessages() {
        var alice = await this._server.RegisterAsync("Alice Disbands");
        var bob = await this._server.RegisterAsync("Bob Disbanded");
        var disbanded = await ChannelWith(alice, "Disbanded Channel", bob);
        await SendAsync(alice, disbanded, "soon gone");
        var abandoned = await ChannelWith(alice, "Abandoned Channel");
        this.Store(alice, abandoned, "alone");
        Assert.Equal(1, this._server.Database.CountStoredMessages(disbanded));
        Assert.Equal(1, this._server.Database.CountStoredMessages(abandoned));

        await alice.Session.DisbandAsync(disbanded, Ct);
        await alice.Session.LeaveAsync(abandoned, Ct);

        Assert.Equal(0, this._server.Database.CountStoredMessages(disbanded));
        Assert.Equal(0, this._server.Database.CountStoredMessages(abandoned));
        Assert.Equal(0L, this.CountAllStoredMessages());
    }

    private long CountAllStoredMessages() {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(this._server.DataDirectory, "lookingglass.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM messages;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    [Fact]
    public async Task AMemberGetsNothingFromBeforeTheyJoined() {
        var alice = await this._server.RegisterAsync("Alice Before");
        var bob = await this._server.RegisterAsync("Bob Before");
        var carol = await this._server.RegisterAsync("Carol Joins Later");
        var channelId = await ChannelWith(alice, "Before Channel", bob);
        await SendAsync(alice, channelId, "before carol");

        await AddMemberAsync(alice, channelId, carol);
        await SendAsync(alice, channelId, "after carol");

        Assert.Equal(["before carol", "after carol"], (await FetchAsync(bob, channelId)).Messages.Select(m => Decrypt(bob, m, alice)));
        var carols = await FetchAsync(carol, channelId);
        Assert.Equal(["after carol"], carols.Messages.Select(m => Decrypt(carol, m, alice)));
        // What she skips is from before she joined: she may carry on from the newest.
        Assert.Equal(2UL, carols.LatestId);
    }

    /// <summary>What a stored message says, read with the key the reader holds for its epoch, checked against its sender's keys.</summary>
    private static string Decrypt(TestClient reader, ChatMessage message, TestClient sender) {
        var content = ChannelCrypto.DecryptMessage(message, reader.LoadEpochKey(message.ChannelId, message.Epoch), sender.Keys().SigningPublicKey);
        return content!.Text.Text;
    }

    [Fact]
    public async Task AMemberWhoLeftOrWasRemovedGetsNothing() {
        var alice = await this._server.RegisterAsync("Alice Removes");
        var bob = await this._server.RegisterAsync("Bob Removed");
        var carol = await this._server.RegisterAsync("Carol Leaves");
        var channelId = await ChannelWith(alice, "Removal Channel", bob, carol);
        await SendAsync(alice, channelId, "while all three");

        await alice.Session.KickAsync(channelId, bob.UserId, Ct);
        await carol.Session.LeaveAsync(channelId, Ct);
        await WaitFor(() => alice.Session.Snapshot.FindChannel(channelId) is { RekeyPending: false } c ? c : null);
        await SendAsync(alice, channelId, "after they went");

        Assert.Equal(ErrorCode.NotFound, await RefusedAsync(bob, channelId));
        Assert.Equal(ErrorCode.NotFound, await RefusedAsync(carol, channelId));
    }

    [Fact]
    public async Task AnInviteeGetsNothing() {
        var alice = await this._server.RegisterAsync("Alice Invites");
        var bob = await this._server.RegisterAsync("Bob Member");
        var dora = await this._server.RegisterAsync("Dora Invited");
        var channelId = await ChannelWith(alice, "Invite Channel", bob);
        await SendAsync(alice, channelId, "not for invitees");
        await alice.Session.InviteAsync(channelId, dora.Name, ProtocolInfo.DebugWorldName, Ct);
        await WaitFor(() => dora.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == channelId));

        Assert.Equal(ErrorCode.Forbidden, await RefusedAsync(dora, channelId));
    }

    [Fact]
    public async Task APlaceUnderOldKeysOrRemovedFromTheListGetsNothing() {
        var alice = await this._server.RegisterAsync("Alice Old Places");
        var bob = await this._server.RegisterAsync("Bob Old Key");
        var channelId = await ChannelWith(alice, "Old Place Channel", bob);
        await SendAsync(alice, channelId, "for bob's old key");

        // Bob registers new keys on a server from before recovery: his place stays with the old ones.
        var again = await ForgetChannelTests.RegisterOnAnOldServerAsync(this._server, bob);
        Assert.Equal(ErrorCode.Forbidden, await RefusedAsync(again, channelId));

        await again.Session.ForgetChannelAsync(channelId, Ct);
        Assert.Equal(ErrorCode.NotFound, await RefusedAsync(again, channelId));
    }

    [Fact]
    public async Task AMemberWhoseKeysMovedGetsNothingFromBeforeTheMove() {
        var alice = await this._server.RegisterAsync("Alice Stays Put");
        var bob = await this._server.RegisterAsync("Bob Recovers");
        var channelId = await ChannelWith(alice, "Recovery Channel", bob);
        await SendAsync(alice, channelId, "under bob's old key");

        // Alice is offline, so nobody gives Bob's new keys the channel's key yet: he waits for it.
        await alice.Session.DisposeAsync();
        var again = await KeyRecoveryTests.NewComputerAsync(this._server, bob);
        Assert.True(this._server.Database.GetMembers(channelId).Single(m => m.User.UserId == again.UserId).AwaitingKey);
        var waiting = await FetchAsync(again, channelId);
        Assert.Empty(waiting.Messages);

        // Once Alice is back and has made a key for his new keys, what is sent under it is his.
        var back = await this._server.RestartAsync(alice);
        await WaitFor(() => again.Session.Snapshot.FindChannel(channelId) is { HasKey: true, RekeyPending: false } c ? c : null);
        await WaitFor(() => back.Session.Snapshot.FindChannel(channelId) is { RekeyPending: false, HasKey: true } c ? c : null);
        await SendAsync(back, channelId, "under bob's new key");
        Assert.Equal(["under bob's new key"], (await FetchAsync(again, channelId)).Messages.Select(m => Decrypt(again, m, back)));
    }

    [Fact]
    public async Task YourOwnMessagesAreNotSentBack() {
        var alice = await this._server.RegisterAsync("Alice Own");
        var bob = await this._server.RegisterAsync("Bob Own");
        var channelId = await ChannelWith(alice, "Own Channel", bob);
        await SendAsync(alice, channelId, "alice's");
        await SendAsync(bob, channelId, "bob's");

        Assert.Equal([bob.UserId], (await FetchAsync(alice, channelId)).Messages.Select(m => m.SenderId));
        Assert.Equal([alice.UserId], (await FetchAsync(bob, channelId)).Messages.Select(m => m.SenderId));
    }

    [Fact]
    public async Task PagesAreBoundedByCountAndBySize() {
        var alice = await this._server.RegisterAsync("Alice Pages");
        var bob = await this._server.RegisterAsync("Bob Pages");
        var channelId = await ChannelWith(alice, "Paged Channel", bob);
        for (var i = 0; i < ProtocolInfo.MaxStoredMessagesPerPage + 5; i++) {
            this.Store(alice, channelId, $"short {i}");
        }

        var first = await FetchAsync(bob, channelId);
        Assert.Equal(ProtocolInfo.MaxStoredMessagesPerPage, first.Messages.Count);
        Assert.True(first.More);
        var rest = await FetchAsync(bob, channelId, first.Messages[^1].ServerId);
        Assert.Equal(5, rest.Messages.Count);
        Assert.False(rest.More);
        Assert.Equal(first.Messages[^1].ServerId + 1, rest.Messages[0].ServerId);

        // Long messages: as many as fit in the page's bytes, and the frame stays within the limit.
        var epoch = this._server.Database.GetChannel(channelId)!.Epoch;
        var after = rest.Messages[^1].ServerId;
        for (var i = 0; i < 40; i++) {
            var big = alice.ForgeMessage(channelId, epoch, new string('x', 3900), DateTimeOffset.UtcNow);
            Assert.True(big.Ciphertext.Length <= 4096);
            this._server.Database.StoreMessage(big, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 5000);
        }

        var full = await FetchAsync(bob, channelId, after);
        Assert.True(full.More);
        Assert.InRange(full.Messages.Sum(m => m.Ciphertext.Length), 1, ProtocolInfo.MaxStoredMessageBytesPerPage);
        Assert.InRange(new ServerFrame { Response = new Response { StoredMessages = full } }.CalculateSize(), 1, 128 * 1024);
    }

    [Fact]
    public async Task WithoutAPositionOnlyRecentMessagesCome() {
        var alice = await this._server.RegisterAsync("Alice Recent");
        var bob = await this._server.RegisterAsync("Bob Recent");
        var channelId = await ChannelWith(alice, "Recent Channel", bob);
        this.Store(alice, channelId, "two hours ago", relayedAt: DateTimeOffset.UtcNow.AddHours(-2));
        this.Store(alice, channelId, "a minute ago", relayedAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.Equal([2UL], (await FetchAsync(bob, channelId, withinSeconds: 3600)).Messages.Select(m => m.ServerId));
        var none = await FetchAsync(bob, channelId, withinSeconds: 0);
        Assert.Empty(none.Messages);
        Assert.Equal(2UL, none.LatestId);
        // With a position, everything after it, however old.
        Assert.Equal([1UL, 2UL], (await FetchAsync(bob, channelId, afterId: 0, withinSeconds: 86400)).Messages.Select(m => m.ServerId));
    }

    [Fact]
    public async Task FetchingStoredMessagesIsLimitedPerUser() {
        var clock = new ManualClock();
        await using var server = new Harness(serverTime: clock);
        var alice = await server.RegisterAsync("Alice Budget");
        var bob = await server.RegisterAsync("Bob Budget");
        var channelId = await ChannelWith(alice, "Budget Channel", bob);

        var allowed = 0;
        ServerErrorException? refused = null;
        for (var i = 0; i < 150 && refused == null; i++) {
            try {
                await FetchAsync(bob, channelId);
                allowed++;
            } catch (ServerErrorException ex) {
                refused = ex;
            }
        }

        Assert.NotNull(refused);
        Assert.Equal(ErrorCode.RateLimited, refused.Code);
        // The burst, give or take what the client's own catch-up used when it connected and what refilled meanwhile.
        Assert.InRange(allowed, 90, 110);

        clock.Offset += TimeSpan.FromSeconds(1);
        await FetchAsync(bob, channelId);
    }

    [Fact]
    public async Task TheCapabilityIsAgreedOnlyWhenMessagesAreKept() {
        static async Task<Welcome> HelloAsync(Harness server, params string[] capabilities) {
            await using var raw = await server.ConnectRawAsync(hello: false);
            var hello = new Hello();
            hello.ProtocolVersions.Add(ProtocolInfo.CurrentVersion);
            hello.Capabilities.AddRange(capabilities);
            return (await raw.SendAsync(new ClientFrame { Hello = hello })).Welcome;
        }

        Assert.Equal([ProtocolInfo.Capabilities.Chat, ProtocolInfo.Capabilities.History],
            (await HelloAsync(this._server, ProtocolInfo.Capabilities.Chat, ProtocolInfo.Capabilities.History)).Capabilities);
        // A plugin from before (0.2.5) doesn't offer it, and isn't given it.
        var old = await HelloAsync(this._server, ProtocolInfo.Capabilities.Chat);
        Assert.Equal([ProtocolInfo.Capabilities.Chat], old.Capabilities);
        Assert.Equal(7u, old.Limits.MessageKeepDays);

        await using var keepsNone = new Harness(settings: ("LookingGlass:Messages:KeepDays", "0"));
        var off = await HelloAsync(keepsNone, ProtocolInfo.Capabilities.Chat, ProtocolInfo.Capabilities.History);
        Assert.Equal([ProtocolInfo.Capabilities.Chat], off.Capabilities);
        Assert.Equal(0u, off.Limits.MessageKeepDays);
        Assert.Equal(0u, off.Limits.MaxStoredMessagesPerChannel);
    }

    [Fact]
    public async Task AServerKeepingNoMessagesStoresNoneAndRelaysAsBefore() {
        await using var server = new Harness(settings: ("LookingGlass:Messages:KeepDays", "0"));
        var alice = await server.RegisterAsync("Alice Keeps None");
        var bob = await server.RegisterAsync("Bob Keeps None");
        var channelId = await ChannelWith(alice, "Unkept Channel", bob);
        await alice.Session.SendTextAsync(channelId, "live only", Ct);
        await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == "live only"));
        Assert.Equal(0, server.Database.CountStoredMessages(channelId));
        Assert.Empty((await FetchAsync(bob, channelId)).Messages);
    }

    [Fact]
    public async Task KeysOfEpochsWithStoredMessagesAreKeptWhileTheirMessagesAre() {
        var alice = await this._server.RegisterAsync("Alice Many Keys");
        var bob = await this._server.RegisterAsync("Bob Many Keys");
        var channelId = await ChannelWith(alice, "Many Keys Channel", bob);
        var withMessage = this._server.Database.GetChannel(channelId)!.Epoch;
        this.Store(alice, channelId, "under an early key");

        // Four rekeys (the rate limit allows five at once, and the join took one): beyond the four newest epochs kept anyway.
        for (var i = 0; i < 4; i++) {
            await alice.Session.RekeyAsync(channelId, Ct, force: true);
        }

        var epoch = this._server.Database.GetChannel(channelId)!.Epoch;
        Assert.Equal(withMessage + 4, epoch);
        var kept = this._server.Database.StoredKeyEpochs(channelId);
        Assert.Contains(withMessage, kept);
        // Of the others, only the newest few, as before.
        Assert.All(kept.Where(e => e != withMessage), e => Assert.True(e + 4 > epoch));

        // Once the message goes, so does its key.
        this._server.ExecuteSql("UPDATE messages SET relayed_at = 0;");
        this._server.Database.SweepMessages(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 7, 5000);
        Assert.DoesNotContain(withMessage, this._server.Database.StoredKeyEpochs(channelId));
    }

    [Fact]
    public void SettingsOutOfRangeAreRefused() {
        Assert.Null(new LookingGlass.Server.MessageOptions().Problem());
        Assert.Null(new LookingGlass.Server.MessageOptions { KeepDays = 0, MaxPerChannel = 0 }.Problem());
        Assert.Contains("KeepDays", new LookingGlass.Server.MessageOptions { KeepDays = -1 }.Problem());
        Assert.Contains("KeepDays", new LookingGlass.Server.MessageOptions { KeepDays = 366 }.Problem());
        Assert.Contains("MaxPerChannel", new LookingGlass.Server.MessageOptions { MaxPerChannel = 100_001 }.Problem());
    }
}
