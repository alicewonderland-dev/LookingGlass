using Google.Protobuf;
using LookingGlass.Core.Client;
using LookingGlass.Core.Crypto;
using LookingGlass.Protocol;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// Message catch-up end to end: what was sent while a member was disconnected reaches them when they come back, in order,
/// once, checked like a live message, with the time it was sent; live messages meanwhile are neither lost nor doubled; a
/// server can't pass a caught-up message off as new, show one twice, or show what the member couldn't have read; and
/// plugins and servers from before work as they did.
/// </summary>
public sealed partial class CatchUpTests : IAsyncLifetime {
    private Harness _server = null!;

    public ValueTask InitializeAsync() {
        this._server = new Harness();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() {
        await this._server.DisposeAsync();
    }

    private static async Task<string> ChannelWith(TestClient admin, string name, params TestClient[] members) {
        var channelId = await admin.Session.CreateChannelAsync(name, Ct);
        foreach (var member in members) {
            await AddMemberAsync(admin, channelId, member);
        }

        return channelId;
    }

    /// <summary>Sends, and returns the time it was sent with (as the sender's own copy shows it).</summary>
    private static async Task<DateTimeOffset> SayAsync(TestClient sender, string channelId, string text) {
        await sender.Session.SendTextAsync(channelId, text, Ct);
        return sender.Messages.Last(m => m.IsOwn && m.Text == text).Timestamp;
    }

    /// <summary>Starts a client again on its saved state, and waits until its catch-up of every channel is over.</summary>
    private async Task<TestClient> BackAsync(TestClient client, ClientSessionOptions? options = null, int channels = 1) {
        var back = this._server.StartClient(client.Name, client.Store, options);
        await WaitFor(() => back.Session.Snapshot is { State: ConnectionState.Ready, ChannelsLoaded: true } ? new object() : null);
        await this.CaughtUpAsync(back);
        return back;
    }

    /// <summary>Waits until the client's catch-up of its login is over, and what it raised has been handled.</summary>
    private async Task CaughtUpAsync(TestClient client) {
        await WaitFor(() => client.Session.CatchUpsDone > 0 ? new object() : null);
        await this._server.SendAndSettleAsync(client);
    }

    [Fact]
    public async Task WhatWasSentWhileAwayArrivesOnReturnInOrderOnceWithItsTime() {
        var alice = await this._server.RegisterAsync("Alice Writes");
        var bob = await this._server.RegisterAsync("Bob Was Away");
        var channelId = await ChannelWith(alice, "Away Channel", bob);
        await SayAsync(alice, channelId, "while you were here");
        await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == "while you were here"));

        await bob.Session.DisposeAsync();
        var first = await SayAsync(alice, channelId, "one");
        var second = await SayAsync(alice, channelId, "two");
        var third = await SayAsync(alice, channelId, "three");

        var back = await this.BackAsync(bob);
        var batch = Assert.Single(back.CaughtUp);
        Assert.Equal(channelId, batch.ChannelId);
        Assert.Equal("Away Channel", batch.ChannelName);
        Assert.Equal(["one", "two", "three"], batch.Messages.Select(m => m.Text));
        Assert.All(batch.Messages, m => Assert.True(m.CaughtUp && !m.IsOwn && m.Sender.Name == "Alice Writes"));
        Assert.Equal([first, second, third], batch.Messages.Select(m => m.Timestamp));
        // Nothing seen before comes again, and nothing went through as a live message.
        Assert.DoesNotContain(back.Messages, m => m.Text == "while you were here");
        Assert.Equal(3, back.Messages.Count);

        // Live chat carries on, and nothing comes again on the next return.
        await SayAsync(alice, channelId, "welcome back");
        var live = await WaitFor(() => back.Messages.FirstOrDefault(m => m.Text == "welcome back"));
        Assert.False(live.CaughtUp);
        await back.Session.DisposeAsync();
        var again = await this.BackAsync(back);
        Assert.Empty(again.CaughtUp);
        Assert.Empty(again.Messages);
    }

    [Fact]
    public async Task LiveMessagesDuringTheCatchUpAreNeitherLostNorDoubled() {
        var alice = await this._server.RegisterAsync("Alice Talks On");
        var bob = await this._server.RegisterAsync("Bob Catches Up");
        var channelId = await ChannelWith(alice, "Busy Channel", bob);
        await bob.Session.DisposeAsync();
        await SayAsync(alice, channelId, "missed one");
        await SayAsync(alice, channelId, "missed two");

        // Bob's request for what he missed waits on its way while Alice goes on talking: those reach him live meanwhile.
        HoldingWebSocket? holding = null;
        var options = this._server.Options(wrap: socket => {
            holding = new HoldingWebSocket(socket);
            holding.HoldNext(ClientFrame.BodyOneofCase.FetchMessages);
            return holding;
        });
        var back = this._server.StartClient(bob.Name, bob.Store, options);
        await WaitFor(() => holding);
        await holding!.Held;
        await SayAsync(alice, channelId, "during one");
        await SayAsync(alice, channelId, "during two");
        await Task.Delay(200, Ct);
        // Held back until what he missed is in.
        Assert.Empty(back.Messages);

        holding.Release();
        await WaitFor(() => back.Messages.Count >= 4 ? new object() : null);
        await this.CaughtUpAsync(back);
        await SayAsync(alice, channelId, "after");
        await WaitFor(() => back.Messages.FirstOrDefault(m => m.Text == "after"));

        // Each once, in the order they were sent.
        Assert.Equal(["missed one", "missed two", "during one", "during two", "after"], back.Messages.Select(m => m.Text));
        Assert.True(back.Messages.Take(2).All(m => m.CaughtUp));
        Assert.False(back.Messages.Last().CaughtUp);
    }

    [Fact]
    public async Task ALiveMessageTheServerLeftOutOfTheCatchUpIsTakenAfterIt() {
        var alice = await this._server.RegisterAsync("Alice Late Store");
        var bob = await this._server.RegisterAsync("Bob Late Store");
        var channelId = await ChannelWith(alice, "Late Channel", bob);
        await bob.Session.DisposeAsync();
        await SayAsync(alice, channelId, "missed");

        // As if "racing" were stored only after the server answered: it reaches Bob live, and the answer doesn't have it.
        HoldingWebSocket? holding = null;
        var options = this._server.Options(wrap: socket => {
            holding = new HoldingWebSocket(socket);
            holding.HoldNext(ClientFrame.BodyOneofCase.FetchMessages);
            return new RewritingWebSocket(holding, frame => {
                if (frame.Response?.StoredMessages is { } stored) {
                    var keep = stored.Messages.Where(m => m.ServerId == 1).ToList();
                    stored.Messages.Clear();
                    stored.Messages.AddRange(keep);
                    stored.LatestId = 1;
                }

                return frame;
            });
        });
        var back = this._server.StartClient(bob.Name, bob.Store, options);
        await WaitFor(() => holding);
        await holding!.Held;
        await SayAsync(alice, channelId, "racing");
        holding.Release();

        await WaitFor(() => back.Messages.FirstOrDefault(m => m.Text == "racing"));
        await this.CaughtUpAsync(back);
        Assert.Equal(["missed", "racing"], back.Messages.Select(m => m.Text));
        Assert.Equal([true, false], back.Messages.Select(m => m.CaughtUp));
    }

    [Fact]
    public async Task ACaughtUpMessageCantBeReplayedAsLive() {
        var alice = await this._server.RegisterAsync("Alice Replayed");
        var bob = await this._server.RegisterAsync("Bob Replayed");
        var channelId = await ChannelWith(alice, "Replay Channel", bob);
        var epoch = alice.Session.Snapshot.FindChannel(channelId)!.Epoch;
        await bob.Session.DisposeAsync();
        // One written a while ago (the server kept it), and one just now.
        this._server.Database.StoreMessage(alice.ForgeMessage(channelId, epoch, "five minutes ago", DateTimeOffset.UtcNow.AddMinutes(-5)),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 5000);
        await SayAsync(alice, channelId, "just now");

        var back = await this.BackAsync(bob);
        Assert.Equal(["five minutes ago", "just now"], back.Messages.Select(m => m.Text));
        var stored = this._server.Database.ReadStoredMessages(channelId, back.UserId, back.Keys(), 0, null, 100, 1 << 20)!.Messages;

        // Sent again as live, in the same session: had already.
        await this._server.SendAndSettleAsync(back, stored.Select(m => new Event { ChatMessage = m }).ToArray());
        Assert.Equal(2, back.Messages.Count);

        // And after a restart, the older one is older than what is already had from Alice.
        await back.Session.DisposeAsync();
        var restarted = await this.BackAsync(back);
        await this._server.SendAndSettleAsync(restarted, new Event { ChatMessage = stored[0] });
        Assert.Empty(restarted.Messages);
        Assert.Contains(restarted.Notices, n => n.Kind == NoticeKind.MessageReplayed);
    }

    [Fact]
    public async Task AServerShowingMessagesTwiceOrOutOfOrderIsntBelieved() {
        var alice = await this._server.RegisterAsync("Alice Twice");
        var bob = await this._server.RegisterAsync("Bob Twice");
        var channelId = await ChannelWith(alice, "Twice Channel", bob);
        await bob.Session.DisposeAsync();
        await SayAsync(alice, channelId, "first");
        await SayAsync(alice, channelId, "second");
        await SayAsync(alice, channelId, "third");

        // The server sends each twice, under new numbers, and "first" again after "third".
        var options = this._server.Options(wrap: socket => new RewritingWebSocket(socket, frame => {
            if (frame.Response?.StoredMessages is { Messages.Count: > 0 } stored) {
                var original = stored.Messages.ToList();
                stored.Messages.Clear();
                ulong id = 1;
                foreach (var message in original.Concat(original).Append(original[0])) {
                    var copy = message.Clone();
                    copy.ServerId = id++;
                    stored.Messages.Add(copy);
                }

                stored.LatestId = id;
            }

            return frame;
        }));
        var back = await this.BackAsync(bob, options);

        Assert.Equal(["first", "second", "third"], back.Messages.Select(m => m.Text));
        // A server's mistakes like these aren't a warning: nothing was hidden or forged.
        Assert.DoesNotContain(back.Notices, n => n.Kind == NoticeKind.MessagesNotCaughtUp);
    }

    [Fact]
    public async Task WhatTheMemberCouldntHaveReadOrIsntGenuineIsDroppedWithOneWarning() {
        var alice = await this._server.RegisterAsync("Alice Forged");
        var bob = await this._server.RegisterAsync("Bob Forged");
        var mallory = await this._server.RegisterAsync("Mallory Outsider");
        var channelId = await alice.Session.CreateChannelAsync("Forged Channel", Ct);
        // Under the channel's first key, before Bob joined: sealed to Alice only.
        var beforeBob = alice.ForgeMessage(channelId, 0, "before bob joined", DateTimeOffset.UtcNow);
        await AddMemberAsync(alice, channelId, bob);
        var epoch = alice.Session.Snapshot.FindChannel(channelId)!.Epoch;
        await bob.Session.DisposeAsync();
        await SayAsync(alice, channelId, "genuine");

        // Not really Alice's: altered after she signed it.
        var forged = alice.ForgeMessage(channelId, epoch, "altered", DateTimeOffset.UtcNow);
        var bytes = forged.Ciphertext.ToByteArray();
        bytes[^1] ^= 1;
        forged.Ciphertext = ByteString.CopyFrom(bytes);
        // From someone who was never a member, with the channel's key (as if it leaked to her).
        using var malloryKeys = mallory.LoadIdentity();
        var outsider = ChannelCrypto.EncryptMessage(new Content { Text = new TextContent { Text = "from outside" } }, alice.LoadEpochKey(channelId, epoch),
            channelId, epoch, malloryKeys, mallory.UserId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var fromOutside = new ChatMessage {
            ChannelId = channelId, Epoch = epoch, SenderId = mallory.UserId, MessageId = outsider.MessageId, TimestampUnixMs = outsider.TimestampUnixMs,
            Ciphertext = outsider.Ciphertext, Signature = outsider.Signature,
        };

        var options = this._server.Options(wrap: socket => new RewritingWebSocket(socket, frame => {
            if (frame.Response?.StoredMessages is { Messages.Count: > 0 } stored) {
                var genuine = stored.Messages.ToList();
                stored.Messages.Clear();
                ulong id = 1;
                foreach (var message in new[] { beforeBob, forged, fromOutside }.Concat(genuine)) {
                    var copy = message.Clone();
                    copy.ServerId = id++;
                    stored.Messages.Add(copy);
                }
            }

            return frame;
        }));
        var back = await this.BackAsync(bob, options);

        Assert.Equal(["genuine"], back.Messages.Select(m => m.Text));
        var warning = Assert.Single(back.Notices, n => n.Kind == NoticeKind.MessagesNotCaughtUp);
        Assert.Equal(NoticeLevel.Warning, warning.Level);
        Assert.Equal(PlainMessages.MessagesNotCaughtUp("Forged Channel", 3).Technical, warning.Text);
    }

    [Fact]
    public async Task MessagesUnderKeysMadeWhileAwayAreRead() {
        var alice = await this._server.RegisterAsync("Alice Rekeys");
        var bob = await this._server.RegisterAsync("Bob Misses Keys");
        var carol = await this._server.RegisterAsync("Carol Joins");
        var dave = await this._server.RegisterAsync("Dave Joins");
        var channelId = await ChannelWith(alice, "Rekeyed Channel", bob);
        var startEpoch = alice.Session.Snapshot.FindChannel(channelId)!.Epoch;
        await bob.Session.DisposeAsync();

        await SayAsync(alice, channelId, "under the key bob has");
        await AddMemberAsync(alice, channelId, carol);
        await SayAsync(alice, channelId, "after carol joined");
        await AddMemberAsync(alice, channelId, dave);
        await SayAsync(dave, channelId, "dave says hi");
        await alice.Session.RekeyAsync(channelId, Ct, force: true);
        await WaitFor(() => dave.Session.Snapshot.FindChannel(channelId) is { RekeyPending: false, HasKey: true, Epoch: var e } c && e == startEpoch + 3 ? c : null);
        await SayAsync(alice, channelId, "under the newest key");

        var back = await this.BackAsync(bob);
        Assert.Equal(["under the key bob has", "after carol joined", "dave says hi", "under the newest key"], back.Messages.Select(m => m.Text));
        Assert.Equal(["Alice Rekeys", "Alice Rekeys", "Dave Joins", "Alice Rekeys"], back.Messages.Select(m => m.Sender.Name));
        // The key he missed was for reading only: he sends with the newest.
        Assert.Equal(startEpoch + 3, back.Session.Snapshot.FindChannel(channelId)!.Epoch);
    }

    [Fact]
    public async Task AMessageFromSomeoneWhoLeftSinceIsShown() {
        var alice = await this._server.RegisterAsync("Alice Remains");
        var bob = await this._server.RegisterAsync("Bob Comes Back");
        var carol = await this._server.RegisterAsync("Carol Says Bye");
        var channelId = await ChannelWith(alice, "Goodbye Channel", bob, carol);
        await bob.Session.DisposeAsync();

        await SayAsync(carol, channelId, "bye everyone");
        await carol.Session.LeaveAsync(channelId, Ct);
        await WaitFor(() => alice.Session.Snapshot.FindChannel(channelId) is { RekeyPending: false } c && c.Members.All(m => m.User.Name != "Carol Says Bye") ? c : null);
        await SayAsync(alice, channelId, "bye carol");

        var back = await this.BackAsync(bob);
        Assert.Equal(["bye everyone", "bye carol"], back.Messages.Select(m => m.Text));
        Assert.Equal("Carol Says Bye", back.Messages.First().Sender.Name);
    }

    [Fact]
    public async Task WithoutAPositionOnlyTheLastHourComesAndNothingSeenBefore() {
        var alice = await this._server.RegisterAsync("Alice Upgrades");
        var bob = await this._server.RegisterAsync("Bob Upgrades");
        var channelId = await ChannelWith(alice, "Upgrade Channel", bob);
        var epoch = alice.Session.Snapshot.FindChannel(channelId)!.Epoch;
        this._server.Database.StoreMessage(alice.ForgeMessage(channelId, epoch, "two hours ago", DateTimeOffset.UtcNow.AddHours(-2)),
            DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeMilliseconds(), 5000);
        await SayAsync(alice, channelId, "seen live");
        await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == "seen live"));
        await bob.Session.DisposeAsync();
        await SayAsync(alice, channelId, "missed");

        // As after updating from a plugin that kept no position (0.2.5): it knows what it saw live, not where it was.
        var secrets = bob.Store.Load();
        Assert.True(secrets.LastMessageIds.ContainsKey(channelId));
        secrets.LastMessageIds.Clear();
        bob.Store.Save(secrets);

        var back = await this.BackAsync(bob);
        Assert.Equal(["missed"], back.Messages.Select(m => m.Text));
    }

    [Fact]
    public async Task SomeoneRemovedWhileAwayGetsNothing() {
        var alice = await this._server.RegisterAsync("Alice Removes Him");
        var bob = await this._server.RegisterAsync("Bob Removed Away");
        var channelId = await ChannelWith(alice, "Removed Channel", bob);
        await bob.Session.DisposeAsync();
        await SayAsync(alice, channelId, "before the removal");
        await alice.Session.KickAsync(channelId, bob.UserId, Ct);
        await WaitFor(() => alice.Session.Snapshot.FindChannel(channelId) is { RekeyPending: false } c ? c : null);
        await SayAsync(alice, channelId, "after the removal");

        var back = this._server.StartClient(bob.Name, bob.Store);
        await WaitFor(() => back.Session.Snapshot is { State: ConnectionState.Ready, ChannelsLoaded: true } ? new object() : null);
        await this.CaughtUpAsync(back);
        Assert.Null(back.Session.Snapshot.FindChannel(channelId));
        Assert.Empty(back.Messages);
    }

    /// <summary>A message signed by <paramref name="keys"/> as <paramref name="senderId"/>, under an epoch key the sender held.</summary>
    private static ChatMessage Signed(IdentityKeys keys, long senderId, byte[] epochKey, string channelId, ulong epoch, string text, DateTimeOffset when) {
        var sent = ChannelCrypto.EncryptMessage(new Content { Text = new TextContent { Text = text } }, epochKey, channelId, epoch, keys, senderId, when.ToUnixTimeMilliseconds());
        return new ChatMessage {
            ChannelId = channelId, Epoch = epoch, SenderId = senderId, MessageId = sent.MessageId, TimestampUnixMs = sent.TimestampUnixMs,
            Ciphertext = sent.Ciphertext, Signature = sent.Signature,
        };
    }

    /// <summary>Options under which the server adds <paramref name="extra"/> to the end of every page of stored messages.</summary>
    private ClientSessionOptions Adding(TimeProvider? time, params ChatMessage[] extra) => this._server.Options(time: time, wrap: socket => new RewritingWebSocket(socket, frame => {
        if (frame.Response?.StoredMessages is { } stored) {
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
    /// Someone who left, with the server's help, signs a message dated after they left, under the key they held while a
    /// member: it can't pass as one missed while away. (The reader's clock is ahead, so it isn't dated in its future.)
    /// </summary>
    [Fact]
    public async Task SomeoneWhoLeftCantPostAfterLeaving() {
        var alice = await this._server.RegisterAsync("Alice Stays On");
        var bob = await this._server.RegisterAsync("Bob Comes Back Later");
        var carol = await this._server.RegisterAsync("Carol Leaves Early");
        var channelId = await ChannelWith(alice, "Leavers Channel", bob, carol);
        var epoch = alice.Session.Snapshot.FindChannel(channelId)!.Epoch;
        var carolsKey = carol.LoadEpochKey(channelId, epoch);
        using var carolsKeys = carol.LoadIdentity();
        await bob.Session.DisposeAsync();

        await carol.Session.LeaveAsync(channelId, Ct);
        await WaitFor(() => alice.Session.Snapshot.FindChannel(channelId) is { RekeyPending: false } c && c.Members.All(m => m.User.Name != carol.Name) ? c : null);
        await SayAsync(alice, channelId, "after carol left");
        var afterLeaving = Signed(carolsKeys, carol.UserId, carolsKey, channelId, epoch, "carol after leaving", DateTimeOffset.UtcNow.AddMinutes(5));

        var clock = new ManualClock { Offset = TimeSpan.FromMinutes(10) };
        var back = await this.BackAsync(bob, this.Adding(clock, afterLeaving));
        Assert.Equal(["after carol left"], back.Messages.Select(m => m.Text));
        Assert.Contains(back.Notices, n => n.Kind == NoticeKind.MessagesNotCaughtUp);
    }

    /// <summary>
    /// A character re-verified with new keys (its old computer was stolen, say): whoever holds the old keys, with the server's
    /// help, can't post as them, dated after the move, under a key the old keys held.
    /// </summary>
    [Fact]
    public async Task OldKeysCantPostAfterTheirPlaceMoved() {
        var alice = await this._server.RegisterAsync("Alice Stolen Laptop");
        var bob = await this._server.RegisterAsync("Bob Trusts Alice");
        var channelId = await ChannelWith(alice, "Stolen Channel", bob);
        await SayAsync(alice, channelId, "the real alice");
        await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == "the real alice"));
        var epoch = alice.Session.Snapshot.FindChannel(channelId)!.Epoch;
        var oldEpochKey = alice.LoadEpochKey(channelId, epoch);
        using var oldKeys = alice.LoadIdentity();
        await bob.Session.DisposeAsync();

        var again = await KeyRecoveryTests.NewComputerAsync(this._server, alice);
        await WaitFor(() => again.Session.Snapshot.FindChannel(channelId));
        var thief = Signed(oldKeys, again.UserId, oldEpochKey, channelId, epoch, "the thief as alice", DateTimeOffset.UtcNow.AddMinutes(5));

        var clock = new ManualClock { Offset = TimeSpan.FromMinutes(10) };
        var back = await this.BackAsync(bob, this.Adding(clock, thief));
        Assert.DoesNotContain(back.Messages, m => m.Text == "the thief as alice");
        Assert.Contains(back.Notices, n => n.Kind == NoticeKind.MessagesNotCaughtUp);
    }

    /// <summary>
    /// A catch-up that fails (the server errs every time) doesn't lose what was missed: the live messages after it don't move
    /// the position past the gap, nor make the missed ones look older than what was already had, so the next login gets them.
    /// </summary>
    [Fact]
    public async Task AFailedCatchUpLosesNothing() {
        var alice = await this._server.RegisterAsync("Alice Keeps Talking");
        var bob = await this._server.RegisterAsync("Bob Unlucky");
        var channelId = await ChannelWith(alice, "Unlucky Channel", bob);
        await SayAsync(alice, channelId, "seen");
        await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == "seen"));
        await bob.Session.DisposeAsync();
        await SayAsync(alice, channelId, "missed");

        var failing = this._server.Options(wrap: socket => new RewritingWebSocket(socket, frame => {
            if (frame.Response?.StoredMessages != null) {
                frame.Response.Error = new Error { Code = ErrorCode.Internal, Message = "Internal server error." };
            }

            return frame;
        }));
        var first = await this.BackAsync(bob, failing);
        Assert.Empty(first.Messages);
        await SayAsync(alice, channelId, "later, live");
        var live = await WaitFor(() => first.Messages.FirstOrDefault(m => m.Text == "later, live"));
        Assert.False(live.CaughtUp);
        await first.Session.DisposeAsync();

        var second = await this.BackAsync(first);
        Assert.Equal(["missed"], second.Messages.Select(m => m.Text));
        // The gap is over: the position moves on with live messages again.
        Assert.DoesNotContain(channelId, second.Store.Load().CatchUpGaps.Keys);
        // And once it is in, the position carries on as usual.
        await second.Session.DisposeAsync();
        var third = await this.BackAsync(second);
        Assert.Empty(third.Messages);
    }

    [Fact]
    public async Task APluginFromBeforeDoesntAskAndChatsAsBefore() {
        var alice = await this._server.RegisterAsync("Alice New Plugin");
        var requests = new System.Collections.Concurrent.ConcurrentQueue<ClientFrame.BodyOneofCase>();
        var old = this._server.Options(offerCatchUp: false, wrap: socket => new RewritingWebSocket(socket, frame => frame, sent => requests.Enqueue(sent.BodyCase)));
        var bob = await this._server.RegisterAsync("Bob Old Plugin", options: old);
        var channelId = await ChannelWith(alice, "Mixed Channel", bob);
        await SayAsync(alice, channelId, "live to the old plugin");
        await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == "live to the old plugin"));

        await bob.Session.DisposeAsync();
        await SayAsync(alice, channelId, "while the old plugin is away");
        var back = this._server.StartClient(bob.Name, bob.Store, old);
        await WaitFor(() => back.Session.Snapshot is { State: ConnectionState.Ready, ChannelsLoaded: true } ? new object() : null);
        await SayAsync(alice, channelId, "live again");
        await WaitFor(() => back.Messages.FirstOrDefault(m => m.Text == "live again"));

        Assert.DoesNotContain(ClientFrame.BodyOneofCase.FetchMessages, requests);
        Assert.Equal(["live again"], back.Messages.Select(m => m.Text));
    }

    [Fact]
    public async Task AServerWithoutCatchUpIsntAskedAndChatsAsBefore() {
        await using var server = new Harness(settings: ("LookingGlass:Messages:KeepDays", "0"));
        var requests = new System.Collections.Concurrent.ConcurrentQueue<ClientFrame.BodyOneofCase>();
        var watched = server.Options(wrap: socket => new RewritingWebSocket(socket, frame => frame, sent => requests.Enqueue(sent.BodyCase)));
        var alice = await server.RegisterAsync("Alice Old Server");
        var bob = await server.RegisterAsync("Bob Old Server", options: watched);
        var channelId = await ChannelWith(alice, "Old Server Channel", bob);
        await bob.Session.DisposeAsync();
        await SayAsync(alice, channelId, "lost, as before");

        var back = server.StartClient(bob.Name, bob.Store, watched);
        await WaitFor(() => back.Session.Snapshot is { State: ConnectionState.Ready, ChannelsLoaded: true } ? new object() : null);
        await SayAsync(alice, channelId, "live");
        await WaitFor(() => back.Messages.FirstOrDefault(m => m.Text == "live"));

        Assert.DoesNotContain(ClientFrame.BodyOneofCase.FetchMessages, requests);
        Assert.Equal(["live"], back.Messages.Select(m => m.Text));
    }
}
