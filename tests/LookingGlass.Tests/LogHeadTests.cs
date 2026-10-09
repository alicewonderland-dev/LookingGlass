using Google.Protobuf;
using LookingGlass.Core.Client;
using LookingGlass.Core.Membership;
using LookingGlass.Protocol;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// Log heads in messages, among honest members (see "Log heads in messages" in docs/design.md): each channel message carries,
/// inside its encrypted, signed content, the newest membership log entry its sender verified, and receivers compare it with
/// theirs. Nothing changes for normal chat. A server or member that lies about it is tested in <see cref="MaliciousServerTests"/>
/// and <see cref="MaliciousMemberTests"/>.
/// </summary>
public sealed class LogHeadTests : IAsyncLifetime {
    private Harness _server = null!;

    public ValueTask InitializeAsync() {
        this._server = new Harness();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() {
        await this._server.DisposeAsync();
    }

    [Fact]
    public async Task AMessageCarriesItsSendersVerifiedLogHead() {
        var alice = await this._server.RegisterAsync("Alice Carries Head");
        var bob = await this._server.RegisterAsync("Bob Reads Head");
        var channelId = await alice.Session.CreateChannelAsync("Heads", Ct);
        await AddMemberAsync(alice, channelId, bob);

        await alice.Session.SendTextAsync(channelId, "with my head", Ct);
        await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == "with my head"));
        var content = alice.ReadSent(this._server, channelId, "with my head", bob);
        Assert.Equal(PositionOf(alice, channelId), content.LogHead);
        // The hash the next entry chains to: the entry's own, as the log has it.
        Assert.Equal(this._server.Database.GetChannel(channelId)!.LogHead, content.LogHead);

        // Both see the same check code, made from it, to compare over /tell.
        var code = alice.Session.Snapshot.FindChannel(channelId)!.CheckCode;
        Assert.Matches(@"^#\d+ \d{5} \d{5} \d{5} \d{5}$", code);
        Assert.StartsWith($"#{content.LogHead.Seq} ", code);
        Assert.Equal(code, bob.Session.Snapshot.FindChannel(channelId)!.CheckCode);
        Assert.Equal(code, MembershipCheckCode.Of(content.LogHead));
        var other = content.LogHead.Clone();
        other.Hash = ByteString.CopyFrom(new byte[MembershipEntries.HashSize]);
        Assert.NotEqual(code, MembershipCheckCode.Of(other));
    }

    /// <summary>
    /// After a restart, the hashes the saved membership kept are carried on from: a join afterwards (from which the membership
    /// itself keeps only newer hashes) doesn't stop a head from before it being compared.
    /// </summary>
    [Fact]
    public async Task AfterARestartAnOlderHeadIsStillComparedPastAJoin() {
        var alice = await this._server.RegisterAsync("Alice Before Restart");
        var carol = await this._server.RegisterAsync("Carol Restarts");
        var dave = await this._server.RegisterAsync("Dave After Restart");
        var channelId = await alice.Session.CreateChannelAsync("Restarted", Ct);
        await AddMemberAsync(alice, channelId, carol);
        var older = PositionOf(carol, channelId);
        await carol.Session.DisposeAsync();
        carol = await this._server.RestartAsync(carol);

        await AddMemberAsync(alice, channelId, dave);
        var epoch = dave.Session.Snapshot.FindChannel(channelId)!.Epoch;
        await WaitFor(() => carol.Store.Load().EpochKeys.GetValueOrDefault(channelId)?.ContainsKey(epoch) == true ? new object() : null);
        await WaitFor(() => PositionOf(carol, channelId).Seq == older.Seq + 2 ? new object() : null);
        var fetches = LogFetches(carol);

        await this._server.SendAndSettleAsync(carol, new Event { ChatMessage = alice.ForgeMessage(channelId, epoch, "right older head", DateTimeOffset.UtcNow, older) });
        Assert.Equal(fetches, LogFetches(carol));
        var wrong = new LogPosition { Seq = older.Seq, Hash = ByteString.CopyFrom(new byte[MembershipEntries.HashSize]) };
        await this._server.SendAndSettleAsync(carol, new Event { ChatMessage = alice.ForgeMessage(channelId, epoch, "wrong older head", DateTimeOffset.UtcNow, wrong) });
        var notice = await WaitFor(() => carol.Notices.FirstOrDefault(n => n.Kind == NoticeKind.MemberSeesOtherMembership));
        Assert.StartsWith("Alice Before Restart@", notice.Text);
    }

    /// <summary>A plugin from before log heads sends none: its messages are shown as before, and nothing is fetched or said.</summary>
    [Fact]
    public async Task AMessageWithoutALogHeadIsTakenAsBefore() {
        var alice = await this._server.RegisterAsync("Alice Old Plugin");
        var bob = await this._server.RegisterAsync("Bob New Plugin");
        var channelId = await alice.Session.CreateChannelAsync("Old And New", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var epoch = bob.Session.Snapshot.FindChannel(channelId)!.Epoch;
        var fetches = LogFetches(bob);

        await alice.Session.SendRawAsync(new ClientFrame { SendMessage = alice.ForgeSend(channelId, epoch, "from an older plugin", DateTimeOffset.UtcNow) }, Ct);
        await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == "from an older plugin"));
        // And the other way round: an older plugin skips the field it doesn't know (protobuf does), so it reads the text.
        await bob.Session.SendTextAsync(channelId, "from a newer plugin", Ct);
        var content = bob.ReadSent(this._server, channelId, "from a newer plugin", alice);
        Assert.NotNull(content.LogHead);
        Assert.Equal("from a newer plugin", TextContent.Parser.ParseFrom(content.Text.ToByteString()).Text);

        await this._server.SendAndSettleAsync(bob);
        Assert.Equal(fetches, LogFetches(bob));
        Assert.DoesNotContain(bob.Notices, n => n.Level >= NoticeLevel.Warning);
    }

    /// <summary>
    /// Many messages, each with its sender's head: each is compared with a lookup, so none costs a fetch of the log, and
    /// nothing is said.
    /// </summary>
    [Fact]
    public async Task ManyMessagesCostNoLogFetches() {
        var alice = await this._server.RegisterAsync("Alice Chatty");
        // A trace long enough to keep every fetch among the messages' events.
        var bob = await this._server.RegisterAsync("Bob Listens", options: this._server.Options(traceCapacity: 5000));
        var channelId = await alice.Session.CreateChannelAsync("Chatty", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var epoch = bob.Session.Snapshot.FindChannel(channelId)!.Epoch;
        await WaitFor(() => alice.Store.Load().EpochKeys.GetValueOrDefault(channelId)?.ContainsKey(epoch) == true ? new object() : null);
        var head = PositionOf(alice, channelId);
        var fetches = LogFetches(bob);

        const int count = 500;
        var start = DateTimeOffset.UtcNow;
        var messages = Enumerable.Range(0, count)
            .Select(i => new Event { ChatMessage = alice.ForgeMessage(channelId, epoch, $"chat {i}", start.AddMilliseconds(i), head) })
            .ToArray();
        await this._server.SendAndSettleAsync(bob, messages);

        Assert.Equal(count, bob.Messages.Count(m => m.Text?.StartsWith("chat ") == true));
        Assert.Equal(fetches, LogFetches(bob));
        Assert.DoesNotContain(bob.Notices, n => n.Level >= NoticeLevel.Warning);
    }

    /// <summary>
    /// A sender who hasn't caught up with the log yet sends an older head. It is compared with what the receiver verified at
    /// that position, which is remembered even past a join (after which the membership itself keeps only the newest entries'
    /// hashes): the right hash costs nothing; a wrong one is looked into and put down to its sender.
    /// </summary>
    [Fact]
    public async Task AnOlderLogHeadIsComparedWithWhatWasVerifiedThere() {
        var alice = await this._server.RegisterAsync("Alice Behind");
        var carol = await this._server.RegisterAsync("Carol Remembers");
        var dave = await this._server.RegisterAsync("Dave Joins Later");
        var channelId = await alice.Session.CreateChannelAsync("Behind", Ct);
        await AddMemberAsync(alice, channelId, carol);
        var older = PositionOf(carol, channelId);
        await AddMemberAsync(alice, channelId, dave);
        var epoch = dave.Session.Snapshot.FindChannel(channelId)!.Epoch;
        await WaitFor(() => carol.Store.Load().EpochKeys.GetValueOrDefault(channelId)?.ContainsKey(epoch) == true ? new object() : null);
        await WaitFor(() => PositionOf(carol, channelId).Seq == older.Seq + 2 ? new object() : null);
        var fetches = LogFetches(carol);

        await this._server.SendAndSettleAsync(carol, new Event { ChatMessage = alice.ForgeMessage(channelId, epoch, "sent before I saw Dave", DateTimeOffset.UtcNow, older) });
        Assert.Contains(carol.Messages, m => m.Text == "sent before I saw Dave");
        Assert.Equal(fetches, LogFetches(carol));
        Assert.DoesNotContain(carol.Notices, n => n.Level >= NoticeLevel.Warning);

        var wrong = new LogPosition { Seq = older.Seq, Hash = ByteString.CopyFrom(new byte[MembershipEntries.HashSize]) };
        await this._server.SendAndSettleAsync(carol, new Event { ChatMessage = alice.ForgeMessage(channelId, epoch, "with a wrong older head", DateTimeOffset.UtcNow, wrong) });
        var notice = await WaitFor(() => carol.Notices.FirstOrDefault(n => n.Kind == NoticeKind.MemberSeesOtherMembership));
        Assert.StartsWith("Alice Behind@", notice.Text);
        Assert.Contains($"#{older.Seq}", notice.Text);
        Assert.Null(carol.Session.Snapshot.FindChannel(channelId)!.MembershipWarning);
    }

    /// <summary>
    /// A sender ahead of the receiver (an entry whose event the receiver missed): the receiver fetches the log, verifies it as
    /// always, and finds the same entry there. Nothing to say.
    /// </summary>
    [Fact]
    public async Task ALogHeadAheadIsCaughtUpWithQuietly() {
        var alice = await this._server.RegisterAsync("Alice Ahead");
        var carol = await this._server.RegisterAsync("Carol Catches Up");
        var dave = await this._server.RegisterAsync("Dave Invited Quietly");
        var channelId = await alice.Session.CreateChannelAsync("Ahead", Ct);
        await AddMemberAsync(alice, channelId, carol);
        var epoch = carol.Session.Snapshot.FindChannel(channelId)!.Epoch;

        // An invite reaches the log without Carol being told (her event was lost, say).
        var invite = this._server.NextEntry(channelId, alice, MembershipEntryKind.Invite, dave.UserId, dave.Keys());
        Assert.True(this._server.Database.AppendEntry(channelId, invite, new SealedBox {
            EphemeralPublicKey = ByteString.CopyFrom(new byte[32]), Ciphertext = ByteString.CopyFrom(new byte[48]),
        }, new byte[64]));
        var newHead = MembershipEntries.PositionOf(invite);

        await alice.Session.SendRawAsync(new ClientFrame { SendMessage = alice.ForgeSend(channelId, epoch, "did you see that?", DateTimeOffset.UtcNow, newHead) }, Ct);
        await WaitFor(() => carol.Messages.FirstOrDefault(m => m.Text == "did you see that?"));
        await WaitFor(() => PositionOf(carol, channelId).Equals(newHead) ? new object() : null);
        await this._server.SendAndSettleAsync(carol);
        Assert.DoesNotContain(carol.Notices, n => n.Level >= NoticeLevel.Warning);
        Assert.Null(carol.Session.Snapshot.FindChannel(channelId)!.MembershipWarning);
    }

    private static int LogFetches(TestClient client) => client.Session.GetTrace().Count(entry => entry.Outgoing && entry.Summary.EndsWith(" FetchMembershipLog"));
}
