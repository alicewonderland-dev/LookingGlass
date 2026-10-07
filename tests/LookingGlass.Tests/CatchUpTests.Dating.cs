using Google.Protobuf;
using LookingGlass.Core.Client;
using LookingGlass.Core.Crypto;
using LookingGlass.Core.Membership;
using LookingGlass.Protocol;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// How long a key may speak in a catch-up: until the next key was made, as whoever made it signed (a member then, never the
/// server, someone who left, or a place's replaced keys), and, where a key doesn't say (an older client made it), until
/// the next membership change, as this client dates it, trusting no time the server or the change's own subject chose.
/// </summary>
public sealed partial class CatchUpTests {
    /// <summary>
    /// Options under which every epoch key reaching the client says nothing of when it was made, as keys made by older
    /// clients don't; and the server adds <paramref name="extra"/> to every page of stored messages.
    /// </summary>
    private ClientSessionOptions WithoutCreationTimes(TimeProvider? time, params ChatMessage[] extra) => this._server.Options(time: time, wrap: socket =>
        new RewritingWebSocket(socket, frame => {
            static void Strip(SealedEpochKey? key) {
                if (key != null) {
                    key.CreatedUnixMs = 0;
                    key.CreatedSignature = ByteString.Empty;
                }
            }

            foreach (var key in frame.Response?.EpochKeys?.Keys ?? []) {
                Strip(key.Key);
            }

            Strip(frame.Event?.EpochAdvanced?.MyKey);
            if (frame.Response?.StoredMessages is { } stored && extra.Length > 0) {
                var id = Math.Max(stored.LatestId, stored.Messages.Count == 0 ? 0 : stored.Messages[^1].ServerId);
                foreach (var message in extra) {
                    var copy = message.Clone();
                    copy.ServerId = ++id;
                    stored.Messages.Add(copy);
                }

                stored.LatestId = id;
            }

            return frame;
        }));

    /// <summary>
    /// A key recovered entry's time is the server's (nothing signs it): a server that dates a recovery a day later still
    /// can't let the old keys (a stolen computer's) post as their owner, to someone away throughout: the key made after the
    /// recovery says, signed by whoever made it, when the old one stopped being the channel's.
    /// </summary>
    [Fact]
    public async Task AServerCantDateARecoveryLaterToLetOldKeysSpeak() {
        var alice = await this._server.RegisterAsync("Alice Stolen Again");
        var bob = await this._server.RegisterAsync("Bob Away Throughout");
        var channelId = await ChannelWith(alice, "Late Recovery Channel", bob);
        await SayAsync(alice, channelId, "the real alice");
        await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == "the real alice"));
        var epoch = alice.Session.Snapshot.FindChannel(channelId)!.Epoch;
        var oldEpochKey = alice.LoadEpochKey(channelId, epoch);
        using var oldKeys = alice.LoadIdentity();
        await bob.Session.DisposeAsync();

        var again = await KeyRecoveryTests.NewComputerAsync(this._server, alice);
        // Nobody can give her new keys the channel's key while Bob, who holds it, is away: no new key is made before he is back.
        await WaitFor(() => again.Session.Snapshot.FindChannel(channelId));
        await again.Session.DisposeAsync();
        // The server dates the recovery a day ahead.
        var recovered = this._server.Database.GetLogEntries(channelId, 0, 100).Last();
        Assert.Equal(MembershipEntryKind.KeyRecovered, recovered.Kind);
        recovered.TimestampUnixMs = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds();
        var hash = MembershipEntries.Hash(recovered);
        this._server.ExecuteSql("UPDATE membership_log SET entry = $e, hash = $h WHERE channel_id = $c AND seq = $s;",
            ("$e", recovered.ToByteArray()), ("$h", hash), ("$c", channelId), ("$s", (long) recovered.Seq));
        this._server.ExecuteSql("UPDATE channels SET log_hash = $h WHERE channel_id = $c;", ("$h", hash), ("$c", channelId));

        var thief = Signed(oldKeys, alice.UserId, oldEpochKey, channelId, epoch, "the thief, after the recovery", DateTimeOffset.UtcNow.AddMinutes(5));
        var back = await this.BackAsync(bob, this.Adding(new ManualClock { Offset = TimeSpan.FromMinutes(10) }, thief));
        Assert.DoesNotContain(back.Messages, m => m.Text == "the thief, after the recovery");
        Assert.Contains(back.Notices, n => n.Kind == NoticeKind.MessagesNotCaughtUp);
    }

    /// <summary>
    /// Right after updating (no membership changes recorded yet), entries since the last membership change (a rank, an
    /// invite) don't make a genuine missed message under the key replaced since look undatable.
    /// </summary>
    [Fact]
    public async Task GenuineMessagesUnderAKeyReplacedSinceTheUpdateShow() {
        var alice = await this._server.RegisterAsync("Alice Promotes");
        var bob = await this._server.RegisterAsync("Bob Just Updated");
        var carol = await this._server.RegisterAsync("Carol Joins After");
        var channelId = await ChannelWith(alice, "Promoted Channel", bob);
        await alice.Session.SetRankAsync(channelId, bob.UserId, Rank.Moderator, Ct);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { MyRank: Rank.Moderator } c ? c : null);
        await bob.Session.DisposeAsync();
        var secrets = bob.Store.Load();
        secrets.MembershipChanges.Clear();
        bob.Store.Save(secrets);

        await SayAsync(alice, channelId, "genuine, under the old key");
        await AddMemberAsync(alice, channelId, carol);

        var back = await this.BackAsync(bob, this.WithoutCreationTimes(null));
        Assert.Equal(["genuine, under the old key"], back.Messages.Select(m => m.Text));
        Assert.DoesNotContain(back.Notices, n => n.Kind == NoticeKind.MessagesNotCaughtUp);
    }

    /// <summary>Fetching a missed epoch's key fails once: the catch-up, tried again, fetches it again rather than drop the message.</summary>
    [Fact]
    public async Task AKeyFetchThatFailsIsMadeAgainWhenRetried() {
        var alice = await this._server.RegisterAsync("Alice Keys Fail");
        var bob = await this._server.RegisterAsync("Bob Keys Fail");
        var carol = await this._server.RegisterAsync("Carol Keys Fail");
        var dave = await this._server.RegisterAsync("Dave Keys Fail");
        var channelId = await ChannelWith(alice, "Failing Keys Channel", bob);
        await bob.Session.DisposeAsync();
        await AddMemberAsync(alice, channelId, carol);
        await SayAsync(alice, channelId, "under a missed key");
        await AddMemberAsync(alice, channelId, dave);
        await alice.Session.RekeyAsync(channelId, Ct, force: true);
        await WaitFor(() => dave.Session.Snapshot.FindChannel(channelId) is { RekeyPending: false, HasKey: true } c ? c : null);

        var catchingUp = 0;
        var failOn = 0L;
        var failed = 0;
        var options = this._server.Options(wrap: socket => new RewritingWebSocket(socket, frame => {
            if (frame.Response is { } response && response.RequestId == Interlocked.Read(ref failOn) && Interlocked.Exchange(ref failed, 1) == 0) {
                response.Error = new Error { Code = ErrorCode.Internal, Message = "Internal server error." };
            }

            return frame;
        }, sent => {
            if (sent.BodyCase == ClientFrame.BodyOneofCase.FetchMessages) {
                Interlocked.Exchange(ref catchingUp, 1);
            } else if (Volatile.Read(ref catchingUp) == 1 && sent.BodyCase == ClientFrame.BodyOneofCase.FetchEpochKeys) {
                Interlocked.CompareExchange(ref failOn, sent.RequestId, 0);
            }
        }));

        var back = await this.BackAsync(bob, options);
        Assert.Equal(1, failed);
        Assert.Equal(["under a missed key"], back.Messages.Select(m => m.Text));
    }

    /// <summary>
    /// Where the keys don't say when they were made (older clients made them), a recovery still dates the end of its old key,
    /// by the next entry someone else signed: a genuine message from before it shows.
    /// </summary>
    [Fact]
    public async Task WithoutCreationTimesAMessageFromBeforeARecoveryStillShows() {
        var alice = await this._server.RegisterAsync("Alice Recovers Later");
        var bob = await this._server.RegisterAsync("Bob Reads Later");
        var channelId = await ChannelWith(alice, "Recovered Later Channel", bob);
        await bob.Session.DisposeAsync();
        await SayAsync(alice, channelId, "before the recovery");
        var again = await KeyRecoveryTests.NewComputerAsync(this._server, alice);
        await WaitFor(() => again.Session.Snapshot.FindChannel(channelId));
        // Signed by Alice's new keys after the recovery, about Bob: it dates the recovery as Bob can trust.
        await again.Session.SetRankAsync(channelId, bob.UserId, Rank.Moderator, Ct);

        var back = await this.BackAsync(bob, this.WithoutCreationTimes(null));
        Assert.Equal(["before the recovery"], back.Messages.Select(m => m.Text));
    }

    /// <summary>
    /// Where the keys don't say when they were made, a leave its signer dated a day ahead is dated by when it was seen: told
    /// to members who saw it happen, and the one who left can't post as if still a member up to that day.
    /// </summary>
    [Fact]
    public async Task AChangeDatedAheadIsDatedByWhenItWasSeenAndSaidSo() {
        var alice = await this._server.RegisterAsync("Alice Sees It");
        var bob = await this._server.RegisterAsync("Bob Sees It", options: this.WithoutCreationTimes(null));
        var carol = await this._server.RegisterAsync("Carol Dates Ahead", options: this._server.Options(time: new ManualClock { Offset = TimeSpan.FromDays(1) }));
        var channelId = await ChannelWith(alice, "Dated Ahead Channel", bob, carol);
        var epoch = alice.Session.Snapshot.FindChannel(channelId)!.Epoch;
        var carolsKey = carol.LoadEpochKey(channelId, epoch);
        using var carolsKeys = carol.LoadIdentity();
        // Written just before she leaves, and (as the server has it) never relayed live.
        var goodbye = Signed(carolsKeys, carol.UserId, carolsKey, channelId, epoch, "carol's goodbye", DateTimeOffset.UtcNow);
        await Task.Delay(20, Ct);

        await carol.Session.LeaveAsync(channelId, Ct);
        await WaitFor(() => bob.Notices.FirstOrDefault(n => n.Kind == NoticeKind.MembershipChangeDatedAhead));
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { RekeyPending: false } c && c.Members.All(m => m.User.Name != carol.Name) ? c : null);
        await bob.Session.DisposeAsync();
        await SayAsync(alice, channelId, "after carol left");

        var afterLeaving = Signed(carolsKeys, carol.UserId, carolsKey, channelId, epoch, "carol, an hour after leaving", DateTimeOffset.UtcNow.AddMinutes(5));
        var back = await this.BackAsync(bob, this.WithoutCreationTimes(new ManualClock { Offset = TimeSpan.FromMinutes(10) }, goodbye, afterLeaving));
        // What she wrote before Bob saw her leave is hers; nothing after.
        Assert.Equal(["after carol left", "carol's goodbye"], back.Messages.Select(m => m.Text));
    }

    /// <summary>
    /// Where the keys don't say when they were made, and the client didn't record the first change after a key (it updated
    /// after it), a later change can't stand in for it: the key's messages can't be dated.
    /// </summary>
    [Fact]
    public async Task WithoutCreationTimesALaterChangeDoesntDateAKeyForAnEarlierOne() {
        var alice = await this._server.RegisterAsync("Alice Two Changes");
        var bob = await this._server.RegisterAsync("Bob Two Changes", options: this.WithoutCreationTimes(null));
        var carol = await this._server.RegisterAsync("Carol First Change");
        var dave = await this._server.RegisterAsync("Dave Second Change");
        var channelId = await ChannelWith(alice, "Two Changes Channel", bob, carol);
        var epoch = alice.Session.Snapshot.FindChannel(channelId)!.Epoch;
        var carolsKey = carol.LoadEpochKey(channelId, epoch);
        using var carolsKeys = carol.LoadIdentity();
        await carol.Session.LeaveAsync(channelId, Ct);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { RekeyPending: false } c && c.Members.All(m => m.User.Name != carol.Name) ? c : null);
        await bob.Session.DisposeAsync();
        // As if Bob updated only now: Carol's leave is behind what he records.
        var secrets = bob.Store.Load();
        secrets.MembershipChanges.Clear();
        bob.Store.Save(secrets);

        var afterLeaving = Signed(carolsKeys, carol.UserId, carolsKey, channelId, epoch, "carol after leaving, before dave", DateTimeOffset.UtcNow);
        await Task.Delay(50, Ct);
        await AddMemberAsync(alice, channelId, dave);

        var back = await this.BackAsync(bob, this.WithoutCreationTimes(null, afterLeaving));
        Assert.DoesNotContain(back.Messages, m => m.Text == "carol after leaving, before dave");
    }

    /// <summary>
    /// Where the keys don't say when they were made, a join its signer dated a day ahead is dated by when it was seen: a
    /// member and the server can't date a message under the key it replaced up to that day.
    /// </summary>
    [Fact]
    public async Task WithoutCreationTimesAJoinDatedAheadDoesntKeepTheOldKeyOpen() {
        var alice = await this._server.RegisterAsync("Alice Old Key Open");
        var bob = await this._server.RegisterAsync("Bob Old Key Open", options: this.WithoutCreationTimes(null));
        var dave = await this._server.RegisterAsync("Dave Joins Ahead", options: this._server.Options(time: new ManualClock { Offset = TimeSpan.FromDays(1) }));
        var channelId = await ChannelWith(alice, "Old Key Open Channel", bob);
        var epoch = alice.Session.Snapshot.FindChannel(channelId)!.Epoch;
        await AddMemberAsync(alice, channelId, dave);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { RekeyPending: false } c && c.Epoch > epoch ? c : null);
        await bob.Session.DisposeAsync();

        var late = alice.ForgeMessage(channelId, epoch, "under the old key, after dave joined", DateTimeOffset.UtcNow.AddMinutes(5));
        var back = await this.BackAsync(bob, this.WithoutCreationTimes(new ManualClock { Offset = TimeSpan.FromMinutes(10) }, late));
        Assert.Empty(back.Messages);
        Assert.Contains(back.Notices, n => n.Kind == NoticeKind.MessagesNotCaughtUp);
    }

    /// <summary>
    /// For someone away throughout, a join its signer dated a day ahead can only be dated by when they came back; the next
    /// key says, signed by the member who made it, when the old one stopped, and a message under it dated later doesn't pass.
    /// </summary>
    [Fact]
    public async Task TheNextKeysCreationTimeEndsTheOldKeyForThoseAway() {
        var alice = await this._server.RegisterAsync("Alice Makes Keys");
        var bob = await this._server.RegisterAsync("Bob Away For The Join");
        var dave = await this._server.RegisterAsync("Dave Joins A Day Ahead", options: this._server.Options(time: new ManualClock { Offset = TimeSpan.FromDays(1) }));
        var channelId = await ChannelWith(alice, "Key Time Channel", bob);
        var epoch = alice.Session.Snapshot.FindChannel(channelId)!.Epoch;
        await bob.Session.DisposeAsync();
        await SayAsync(alice, channelId, "before dave");
        await AddMemberAsync(alice, channelId, dave);

        var late = alice.ForgeMessage(channelId, epoch, "under the old key, after the new one", DateTimeOffset.UtcNow.AddMinutes(5));
        var back = await this.BackAsync(bob, this.Adding(new ManualClock { Offset = TimeSpan.FromMinutes(10) }, late));
        Assert.Equal(["before dave"], back.Messages.Select(m => m.Text));
        Assert.Contains(back.Notices, n => n.Kind == NoticeKind.MessagesNotCaughtUp);
    }

    /// <summary>
    /// Someone who made the next key themselves (a rekey, dated a day ahead) and then left can't use its time to keep their
    /// old key speaking: only a key someone else made says when theirs stopped.
    /// </summary>
    [Fact]
    public async Task SomeoneWhoLeftCantVouchForTheirOwnKeysEnd() {
        var alice = await this._server.RegisterAsync("Alice Stays Again");
        var bob = await this._server.RegisterAsync("Bob Away Again");
        var carol = await this._server.RegisterAsync("Carol Rekeys Ahead", options: this._server.Options(time: new ManualClock { Offset = TimeSpan.FromDays(1) }));
        var channelId = await ChannelWith(alice, "Own Key Channel", bob, carol);
        var epoch = alice.Session.Snapshot.FindChannel(channelId)!.Epoch;
        var carolsKey = carol.LoadEpochKey(channelId, epoch);
        using var carolsKeys = carol.LoadIdentity();
        await bob.Session.DisposeAsync();
        await carol.Session.RekeyAsync(channelId, Ct, force: true);
        await WaitFor(() => alice.Session.Snapshot.FindChannel(channelId) is { Epoch: var e } c && e == epoch + 1 ? c : null);
        await carol.Session.LeaveAsync(channelId, Ct);
        await WaitFor(() => alice.Session.Snapshot.FindChannel(channelId) is { RekeyPending: false } c && c.Members.All(m => m.User.Name != carol.Name) ? c : null);
        await SayAsync(alice, channelId, "after carol left");

        // Bob comes back after the time her key claims (as he might, days later): only who made it rules it out.
        var afterLeaving = Signed(carolsKeys, carol.UserId, carolsKey, channelId, epoch, "carol, after leaving, under the key before hers", DateTimeOffset.UtcNow.AddMinutes(5));
        var back = await this.BackAsync(bob, this.Adding(new ManualClock { Offset = TimeSpan.FromDays(2) }, afterLeaving));
        Assert.Equal(["after carol left"], back.Messages.Select(m => m.Text));
    }

    /// <summary>
    /// The first login after updating, from the last hour: what was received live before, under a key that can't be dated
    /// (the changes after it weren't recorded), is had already, and isn't counted as dropped.
    /// </summary>
    [Fact]
    public async Task MessagesHadLiveBeforeTheUpdateArentCountedAsDropped() {
        var alice = await this._server.RegisterAsync("Alice Before Update");
        var bob = await this._server.RegisterAsync("Bob Before Update", options: this.WithoutCreationTimes(null));
        var carol = await this._server.RegisterAsync("Carol Before Update");
        var channelId = await ChannelWith(alice, "Before Update Channel", bob);
        await SayAsync(alice, channelId, "seen live");
        await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == "seen live"));
        await AddMemberAsync(alice, channelId, carol);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { RekeyPending: false, HasKey: true } c && c.Members.Any(m => m.User.Name == carol.Name) ? c : null);
        await bob.Session.DisposeAsync();
        var secrets = bob.Store.Load();
        secrets.LastMessageIds.Clear();
        secrets.MembershipChanges.Clear();
        bob.Store.Save(secrets);

        var back = await this.BackAsync(bob, this.WithoutCreationTimes(null));
        Assert.Empty(back.Messages);
        Assert.DoesNotContain(back.Notices, n => n.Kind == NoticeKind.MessagesNotCaughtUp);
    }

    /// <summary>
    /// A key made only once someone was back (their own rekey, as they log in) says nothing of when another's keys stopped
    /// while they were away: old keys moved by a recovery still can't post as their owner, dated after it.
    /// </summary>
    [Fact]
    public async Task AKeyMadeOnComingBackDoesntDateWhatHappenedWhileAway() {
        var alice = await this._server.RegisterAsync("Alice Moved Away");
        var bob = await this._server.RegisterAsync("Bob Rekeys On Return");
        var channelId = await ChannelWith(alice, "Return Key Channel", bob);
        await SayAsync(alice, channelId, "the real alice");
        await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == "the real alice"));
        var epoch = alice.Session.Snapshot.FindChannel(channelId)!.Epoch;
        var oldEpochKey = alice.LoadEpochKey(channelId, epoch);
        using var oldKeys = alice.LoadIdentity();
        await bob.Session.DisposeAsync();
        var again = await KeyRecoveryTests.NewComputerAsync(this._server, alice);
        await WaitFor(() => again.Session.Snapshot.FindChannel(channelId));
        var thief = Signed(oldKeys, alice.UserId, oldEpochKey, channelId, epoch, "the thief, just after the recovery", DateTimeOffset.UtcNow);

        // Bob's catch-up gets errors (and tries again) until he has made the channel's next key: he is the one who holds it.
        var options = this._server.Options(wrap: socket => new RewritingWebSocket(socket, frame => {
            if (frame.Response?.StoredMessages is { } stored) {
                if (this._server.Database.GetChannel(channelId) is not { RekeyPending: false } c || c.Epoch != epoch + 1) {
                    frame.Response.Error = new Error { Code = ErrorCode.Internal, Message = "Internal server error." };
                    return frame;
                }

                var copy = thief.Clone();
                copy.ServerId = Math.Max(stored.LatestId, 1) + 1;
                stored.Messages.Add(copy);
                stored.LatestId = copy.ServerId;
            }

            return frame;
        }));
        var back = this._server.StartClient(bob.Name, bob.Store, options);
        await WaitFor(() => back.Session.Snapshot.FindChannel(channelId) is { Epoch: var e } c && e == epoch + 1 ? c : null);
        await this.CaughtUpAsync(back);

        Assert.DoesNotContain(back.Messages, m => m.Text == "the thief, just after the recovery");
        Assert.Contains(back.Notices, n => n.Kind == NoticeKind.MessagesNotCaughtUp);
    }

    /// <summary>Two live messages in one millisecond: either, sent again live after a restart, is had already.</summary>
    [Fact]
    public async Task TwoLiveMessagesInOneMillisecondAreEachTakenOnce() {
        var alice = await this._server.RegisterAsync("Alice Same Live Moment");
        var bob = await this._server.RegisterAsync("Bob Same Live Moment");
        var channelId = await ChannelWith(alice, "Same Live Moment Channel", bob);
        var epoch = alice.Session.Snapshot.FindChannel(channelId)!.Epoch;
        var moment = DateTimeOffset.UtcNow;
        var first = alice.ForgeMessage(channelId, epoch, "first live at once", moment);
        var second = alice.ForgeMessage(channelId, epoch, "second live at once", moment);
        await this._server.SendAndSettleAsync(bob, new Event { ChatMessage = first }, new Event { ChatMessage = second });
        Assert.Equal(2, bob.Messages.Count(m => m.Text!.EndsWith("live at once")));
        await bob.Session.DisposeAsync();

        var back = await this.BackAsync(bob);
        await this._server.SendAndSettleAsync(back, new Event { ChatMessage = first }, new Event { ChatMessage = second });
        Assert.Empty(back.Messages);
    }

    /// <summary>
    /// The connection drops while the first channel's catch-up waits: what came live meanwhile, in either channel, is let go
    /// and fetched by the next login after what was missed, in order.
    /// </summary>
    [Fact]
    public async Task LiveMessagesOfChannelsNotReachedWhenTheConnectionDropsComeInOrder() {
        var alice = await this._server.RegisterAsync("Alice Two Rooms");
        var bob = await this._server.RegisterAsync("Bob Two Rooms");
        var tea = await ChannelWith(alice, "Tea Room", bob);
        var coffee = await ChannelWith(alice, "Coffee Room", bob);
        await bob.Session.DisposeAsync();
        await SayAsync(alice, tea, "missed tea");
        await SayAsync(alice, coffee, "missed coffee");

        HoldingWebSocket? first = null;
        var options = this._server.Options(wrap: socket => {
            if (first != null) {
                return socket;
            }

            first = new HoldingWebSocket(socket);
            first.HoldNext(ClientFrame.BodyOneofCase.FetchMessages);
            return first;
        });
        var back = this._server.StartClient(bob.Name, bob.Store, options);
        await WaitFor(() => first);
        await first!.Held;
        await SayAsync(alice, tea, "live tea");
        await SayAsync(alice, coffee, "live coffee");
        await Task.Delay(100, Ct);
        back.Session.Reconnect();

        await WaitFor(() => back.Session.CatchUpsDone >= 2 ? new object() : null);
        await this._server.SendAndSettleAsync(back);
        Assert.Equal(["missed tea", "live tea"], back.Messages.Where(m => m.ChannelId == tea).Select(m => m.Text));
        Assert.Equal(["missed coffee", "live coffee"], back.Messages.Where(m => m.ChannelId == coffee).Select(m => m.Text));
    }
}
