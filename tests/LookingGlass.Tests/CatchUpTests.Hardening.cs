using LookingGlass.Core.Client;
using LookingGlass.Core.Crypto;
using LookingGlass.Protocol;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// Message catch-up against a server that sends back what it shouldn't, and the bookkeeping that makes each message taken
/// once: across restarts, in one millisecond, when a page is out of order or the server's numbers go back, when too many live
/// messages come to hold, and soon saved.
/// </summary>
public sealed partial class CatchUpTests {
    /// <summary>A message the server stored, as it would send it back.</summary>
    private ChatMessage StoredCopy(string channelId, TestClient reader, string text, TestClient sender) {
        var stored = this._server.Database.ReadStoredMessages(channelId, reader.UserId, reader.Keys(), 0, null, 500, 1 << 22)!.Messages;
        using var keys = sender.LoadIdentity();
        return stored.Single(m => ChannelCrypto.DecryptMessage(m, reader.LoadEpochKey(channelId, m.Epoch), keys.SigningPublicKey)?.Text.Text == text);
    }

    [Fact]
    public async Task AnOlderMessageSentAgainAfterARestartIsntTakenAgain() {
        var alice = await this._server.RegisterAsync("Alice Older Again");
        var bob = await this._server.RegisterAsync("Bob Older Again");
        var channelId = await ChannelWith(alice, "Older Again Channel", bob);
        var epoch = alice.Session.Snapshot.FindChannel(channelId)!.Epoch;
        await bob.Session.DisposeAsync();
        this._server.Database.StoreMessage(alice.ForgeMessage(channelId, epoch, "a minute ago", DateTimeOffset.UtcNow.AddMinutes(-1)),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 5000);
        await SayAsync(alice, channelId, "now");
        var back = await this.BackAsync(bob);
        Assert.Equal(["a minute ago", "now"], back.Messages.Select(m => m.Text));
        var older = this.StoredCopy(channelId, back, "a minute ago", alice);

        // After a restart (nothing seen in memory), the server sends the older one again, under a new number.
        await back.Session.DisposeAsync();
        var again = await this.BackAsync(back, this.Adding(null, older));
        Assert.Empty(again.Messages);
    }

    [Fact]
    public async Task AMessageDatedInTheFutureIsntCaughtUp() {
        var alice = await this._server.RegisterAsync("Alice Future");
        var bob = await this._server.RegisterAsync("Bob Future");
        var channelId = await ChannelWith(alice, "Future Channel", bob);
        var epoch = alice.Session.Snapshot.FindChannel(channelId)!.Epoch;
        await bob.Session.DisposeAsync();
        await SayAsync(alice, channelId, "now");
        var future = alice.ForgeMessage(channelId, epoch, "from the future", DateTimeOffset.UtcNow.AddMinutes(15));

        var back = await this.BackAsync(bob, this.Adding(null, future));
        Assert.Equal(["now"], back.Messages.Select(m => m.Text));
        Assert.Contains(back.Notices, n => n.Kind == NoticeKind.MessagesNotCaughtUp);
        // Nor does it hold back what Alice says next.
        await SayAsync(alice, channelId, "right after");
        await WaitFor(() => back.Messages.FirstOrDefault(m => m.Text == "right after"));
    }

    [Fact]
    public async Task SomeoneWhoLeftCantUseAKeyMadeAfterTheyLeft() {
        var alice = await this._server.RegisterAsync("Alice Newer Key");
        var bob = await this._server.RegisterAsync("Bob Newer Key");
        var carol = await this._server.RegisterAsync("Carol Newer Key");
        var channelId = await ChannelWith(alice, "Newer Key Channel", bob, carol);
        using var carolsKeys = carol.LoadIdentity();
        await bob.Session.DisposeAsync();
        await carol.Session.LeaveAsync(channelId, Ct);
        var after = await WaitFor(() => alice.Session.Snapshot.FindChannel(channelId) is { RekeyPending: false } c && c.Members.All(m => m.User.Name != carol.Name) ? c : null);
        await SayAsync(alice, channelId, "after carol left");
        // The key made after she left, as if it leaked to her.
        var leaked = Signed(carolsKeys, carol.UserId, alice.LoadEpochKey(channelId, after.Epoch), channelId, after.Epoch, "carol with the new key", DateTimeOffset.UtcNow);

        var back = await this.BackAsync(bob, this.Adding(null, leaked));
        Assert.Equal(["after carol left"], back.Messages.Select(m => m.Text));
        Assert.Contains(back.Notices, n => n.Kind == NoticeKind.MessagesNotCaughtUp);
    }

    [Fact]
    public async Task AKeyNewerThanTheNewestHeldIsntTakenForReadingCaughtUpMessages() {
        var alice = await this._server.RegisterAsync("Alice Fake Key");
        var bob = await this._server.RegisterAsync("Bob Fake Key");
        var channelId = await ChannelWith(alice, "Fake Key Channel", bob);
        var epoch = alice.Session.Snapshot.FindChannel(channelId)!.Epoch;
        using var aliceKeys = alice.LoadIdentity();
        using var bobKeys = bob.LoadIdentity();
        var position = PositionOf(alice, channelId);
        await bob.Session.DisposeAsync();
        await SayAsync(alice, channelId, "genuine");

        // A key for an epoch the channel never reached, signed by Alice for the current membership and sealed to Bob, and a
        // message under it: only an epoch key taken with every check may read a newer epoch.
        var future = epoch + 3;
        var fake = ChannelCrypto.NewEpochKey();
        var sealedKey = ChannelCrypto.SealEpochKey(fake, channelId, future, position, aliceKeys, alice.UserId, bob.UserId, bobKeys.AgreementPublicKey);
        var underFake = Signed(aliceKeys, alice.UserId, fake, channelId, future, "under a key nobody made", DateTimeOffset.UtcNow);
        var pastKeyRequests = new System.Collections.Concurrent.ConcurrentDictionary<uint, byte>();
        var options = this._server.Options(wrap: socket => new RewritingWebSocket(socket, frame => {
            if (frame.Response?.StoredMessages is { } stored) {
                var copy = underFake.Clone();
                copy.ServerId = Math.Max(stored.LatestId, 1) + 1;
                stored.Messages.Add(copy);
                stored.LatestId = copy.ServerId;
            } else if (frame.Response?.EpochKeys is { } keys && pastKeyRequests.ContainsKey(frame.Response.RequestId)) {
                keys.Keys.Add(new EpochKeyForMe { Epoch = future, AuthorId = alice.UserId, Key = sealedKey });
            }

            return frame;
        }, sent => {
            if (sent.FetchEpochKeys is { FromEpoch: var from } && from == future) {
                pastKeyRequests[sent.RequestId] = 0;
            }
        }));

        var back = await this.BackAsync(bob, options);
        Assert.NotEmpty(pastKeyRequests);
        Assert.Equal(["genuine"], back.Messages.Select(m => m.Text));
        Assert.Equal(epoch, back.Session.Snapshot.FindChannel(channelId)!.Epoch);
    }

    [Fact]
    public async Task APageIsTakenOnlyForItsChannelAndInOrder() {
        var alice = await this._server.RegisterAsync("Alice Pages Order");
        var bob = await this._server.RegisterAsync("Bob Pages Order");
        var tea = await ChannelWith(alice, "Tea Order", bob);
        var coffee = await ChannelWith(alice, "Coffee Order", bob);
        var epoch = alice.Session.Snapshot.FindChannel(tea)!.Epoch;
        await bob.Session.DisposeAsync();
        await SayAsync(alice, tea, "first");
        await SayAsync(alice, tea, "second");

        // Never stored: one numbered before what came before it in the page, and one of the other channel's.
        var backwards = alice.ForgeMessage(tea, epoch, "numbered backwards", DateTimeOffset.UtcNow);
        var elsewhere = alice.ForgeMessage(coffee, alice.Session.Snapshot.FindChannel(coffee)!.Epoch, "from the other channel", DateTimeOffset.UtcNow);
        var options = this._server.Options(wrap: socket => new RewritingWebSocket(socket, frame => {
            if (frame.Response?.StoredMessages is { Messages.Count: > 0 } stored && stored.ChannelId == tea) {
                var low = backwards.Clone();
                low.ServerId = 1;
                var other = elsewhere.Clone();
                other.ServerId = stored.Messages[^1].ServerId + 10;
                stored.Messages.Add(low);
                stored.Messages.Add(other);
            }

            return frame;
        }));

        var back = await this.BackAsync(bob, options);
        Assert.Equal(["first", "second"], back.Messages.Select(m => m.Text));
        // Left out quietly: neither was taken for a message to check.
        Assert.DoesNotContain(back.Notices, n => n.Kind == NoticeKind.MessagesNotCaughtUp);
    }

    [Fact]
    public async Task ThePositionMovesOnPastWhatTheMemberMayNotRead() {
        var alice = await this._server.RegisterAsync("Alice Skipped");
        var bob = await this._server.RegisterAsync("Bob Skipped");
        var channelId = await ChannelWith(alice, "Skipped Channel", bob);
        var epoch = alice.Session.Snapshot.FindChannel(channelId)!.Epoch;
        await bob.Session.DisposeAsync();
        await SayAsync(alice, channelId, "for bob");
        // Bob's own (from another computer, say), which the server doesn't send back to him.
        this._server.Database.StoreMessage(bob.ForgeMessage(channelId, epoch, "bob's own", DateTimeOffset.UtcNow), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 5000);

        var back = await this.BackAsync(bob);
        Assert.Equal(["for bob"], back.Messages.Select(m => m.Text));
        Assert.Equal(2UL, back.Store.Load().LastMessageIds[channelId]);
    }

    [Fact]
    public async Task ThePositionGoesBackWithTheServersNumbers() {
        var alice = await this._server.RegisterAsync("Alice Restored");
        var bob = await this._server.RegisterAsync("Bob Restored");
        var channelId = await ChannelWith(alice, "Restored Channel", bob);
        await SayAsync(alice, channelId, "one");
        await SayAsync(alice, channelId, "two");
        await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == "two"));
        await bob.Session.DisposeAsync();
        Assert.Equal(2UL, bob.Store.Load().LastMessageIds[channelId]);

        // As if the server's database were restored from a backup made after the first message.
        var options = this._server.Options(wrap: socket => new RewritingWebSocket(socket, frame => {
            if (frame.Response?.StoredMessages is { } stored) {
                stored.Messages.Clear();
                stored.More = false;
                stored.LatestId = 1;
            }

            return frame;
        }));
        var back = await this.BackAsync(bob, options);
        Assert.Equal(1UL, back.Store.Load().LastMessageIds[channelId]);
    }

    [Fact]
    public async Task TheNewestMessageSentAgainLiveAfterARestartIsntTakenAgain() {
        var alice = await this._server.RegisterAsync("Alice Newest Again");
        var bob = await this._server.RegisterAsync("Bob Newest Again");
        var channelId = await ChannelWith(alice, "Newest Again Channel", bob);
        await SayAsync(alice, channelId, "newest");
        await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == "newest"));
        var newest = this.StoredCopy(channelId, bob, "newest", alice);
        await bob.Session.DisposeAsync();

        var back = await this.BackAsync(bob);
        await this._server.SendAndSettleAsync(back, new Event { ChatMessage = newest });
        Assert.Empty(back.Messages);
    }

    [Fact]
    public async Task TwoMessagesInOneMillisecondAreEachTakenOnce() {
        var alice = await this._server.RegisterAsync("Alice Same Moment");
        var bob = await this._server.RegisterAsync("Bob Same Moment");
        var channelId = await ChannelWith(alice, "Same Moment Channel", bob);
        var epoch = alice.Session.Snapshot.FindChannel(channelId)!.Epoch;
        await bob.Session.DisposeAsync();
        var moment = DateTimeOffset.UtcNow.AddSeconds(-10);
        var first = alice.ForgeMessage(channelId, epoch, "first at once", moment);
        var second = alice.ForgeMessage(channelId, epoch, "second at once", moment);
        foreach (var message in new[] { first, second }) {
            this._server.Database.StoreMessage(message, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 5000);
        }

        var back = await this.BackAsync(bob);
        Assert.Equal(["first at once", "second at once"], back.Messages.Select(m => m.Text));
        await back.Session.DisposeAsync();

        var again = await this.BackAsync(back, this.Adding(null, first));
        Assert.Empty(again.Messages);
    }

    [Fact]
    public async Task PositionsAreSavedSoonNotOnlyEveryFewMinutes() {
        var alice = await this._server.RegisterAsync("Alice Saves Soon");
        var bob = await this._server.RegisterAsync("Bob Saves Soon", options: this._server.Options(replaySaveDelay: TimeSpan.FromMilliseconds(200)));
        var channelId = await ChannelWith(alice, "Saved Soon Channel", bob);
        await SayAsync(alice, channelId, "one");
        await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == "one"));
        await SayAsync(alice, channelId, "two");
        await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == "two"));

        // Without stopping the session (as before a crash), both are saved.
        await WaitFor(() => bob.Store.Load().LastMessageIds.GetValueOrDefault(channelId) == 2UL ? new object() : null);
    }

    [Fact]
    public async Task AFailedCatchUpIsTriedAgainWhileLiveMessagesWait() {
        var alice = await this._server.RegisterAsync("Alice Second Try");
        var bob = await this._server.RegisterAsync("Bob Second Try");
        var channelId = await ChannelWith(alice, "Second Try Channel", bob);
        await bob.Session.DisposeAsync();
        await SayAsync(alice, channelId, "missed");

        // The first answer is an error; Alice speaks while Bob waits to try again.
        var failed = 0;
        var options = this._server.Options(wrap: socket => new RewritingWebSocket(socket, frame => {
            if (frame.Response?.StoredMessages != null && Interlocked.Exchange(ref failed, 1) == 0) {
                frame.Response.Error = new Error { Code = ErrorCode.Internal, Message = "Internal server error." };
                _ = SayAsync(alice, channelId, "while it failed");
            }

            return frame;
        }));
        var back = await this.BackAsync(bob, options);
        await WaitFor(() => back.Messages.Count >= 2 ? new object() : null);
        Assert.Equal(["missed", "while it failed"], back.Messages.Select(m => m.Text));
    }

    [Fact]
    public async Task LiveMessagesHeldWhenTheConnectionDropsComeAfterWhatWasMissed() {
        var alice = await this._server.RegisterAsync("Alice Dropped Line");
        var bob = await this._server.RegisterAsync("Bob Dropped Line");
        var channelId = await ChannelWith(alice, "Dropped Line Channel", bob);
        await bob.Session.DisposeAsync();
        await SayAsync(alice, channelId, "missed");

        // The first connection's catch-up waits on its way; Alice speaks; then the connection drops.
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
        await SayAsync(alice, channelId, "while held");
        await Task.Delay(100, Ct);
        back.Session.Reconnect();

        await WaitFor(() => back.Session.CatchUpsDone >= 2 ? new object() : null);
        await this._server.SendAndSettleAsync(back);
        Assert.Equal(["missed", "while held"], back.Messages.Select(m => m.Text));
    }

    [Fact]
    public async Task TooManyLiveMessagesToHoldLoseNothingMissed() {
        var alice = await this._server.RegisterAsync("Alice Floods");
        var bob = await this._server.RegisterAsync("Bob Overflows");
        var channelId = await ChannelWith(alice, "Flood Channel", bob);
        await bob.Session.DisposeAsync();
        await SayAsync(alice, channelId, "missed");

        HoldingWebSocket? holding = null;
        var options = this._server.Options(maxHeldLive: 1, wrap: socket => {
            holding = new HoldingWebSocket(socket);
            holding.HoldNext(ClientFrame.BodyOneofCase.FetchMessages);
            return holding;
        });
        var back = this._server.StartClient(bob.Name, bob.Store, options);
        await WaitFor(() => holding);
        await holding!.Held;
        await SayAsync(alice, channelId, "live 1");
        await SayAsync(alice, channelId, "live 2");
        await SayAsync(alice, channelId, "live 3");
        await WaitFor(() => back.Messages.FirstOrDefault(m => m.Text == "live 3"));
        holding.Release();
        await this.CaughtUpAsync(back);

        Assert.Equal(["live 1", "live 2", "live 3", "missed"], back.Messages.Select(m => m.Text).Order());
        await back.Session.DisposeAsync();
        var again = await this.BackAsync(back);
        Assert.Empty(again.Messages);
    }
}
