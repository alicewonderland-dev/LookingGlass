using System.Net.WebSockets;
using Google.Protobuf;
using LookingGlass.Core.Client;
using LookingGlass.Protocol;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// Keys have a maximum age (see "Keys have a maximum age" in docs/design.md): once a channel's newest key is a week old, a
/// member online makes the next, as for any automatic rekey, judged by the time its maker signed into it. Only one of the
/// members online makes it; nothing is shown; members who were away catch up across it; a plugin from before takes the new
/// key as any other. A server can't make a key look old to have it replaced again and again, and can only keep an old key
/// in use by refusing every new one (a known limitation), which costs a client one try per channel an hour.
/// </summary>
public sealed class EpochMaxAgeTests {
    private static readonly TimeSpan AWeek = TimeSpan.FromDays(7);

    /// <summary>Options for a client whose clock is the test's, looking at keys' ages often, waiting at most <paramref name="jitter"/>.</summary>
    private static ClientSessionOptions Often(Harness server, TimeProvider? clock, TimeSpan? jitter = null, Func<WebSocket, WebSocket>? wrap = null,
        bool replaceOldKeys = true, Action<NoticeLevel, string>? log = null) =>
        server.Options(time: clock, keyAgeCheckInterval: TimeSpan.FromMilliseconds(40), keyAgeJitter: jitter ?? TimeSpan.Zero, wrap: wrap,
            replaceOldKeys: replaceOldKeys, log: log);

    /// <summary>Waits until each client has looked at its keys' ages <paramref name="times"/> more times.</summary>
    private static async Task ChecksAsync(int times, params TestClient[] clients) {
        var from = clients.Select(client => client.Session.KeyAgeChecksForTests).ToList();
        await WaitFor(() => clients.Select((client, i) => client.Session.KeyAgeChecksForTests >= from[i] + times).All(done => done) ? new object() : null);
    }

    private static ulong ServerEpoch(Harness server, string channelId) => server.Database.GetChannel(channelId)!.Epoch;

    private static Task HoldsAsync(TestClient client, string channelId, ulong epoch) =>
        WaitFor(() => client.Session.Snapshot.FindChannel(channelId) is { HasKey: true, Epoch: var held } c && held == epoch ? c : null);

    /// <summary>
    /// A channel where nobody joins or leaves gets a new key once its key is a week old, and not before; and then not again
    /// until that one is a week old. Nobody is told: it is as silent as any other automatic rekey.
    /// </summary>
    [Fact]
    public async Task AQuietChannelGetsANewKeyOnceItsKeyIsAWeekOld() {
        var clock = new ManualClock();
        await using var server = new Harness(serverTime: clock);
        var logged = new System.Collections.Concurrent.ConcurrentQueue<(NoticeLevel Level, string Text)>();
        var alice = await server.RegisterAsync("Alice Quiet Week", options: Often(server, clock, log: (level, text) => logged.Enqueue((level, text))));
        var bob = await server.RegisterAsync("Bob Quiet Week", options: Often(server, clock, log: (level, text) => logged.Enqueue((level, text))));
        var channelId = await alice.Session.CreateChannelAsync("Quiet Week Channel", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var epoch = ServerEpoch(server, channelId);
        var notices = (alice.Notices.Count, bob.Notices.Count);

        // Ten minutes short of a week: nothing.
        clock.Offset = AWeek - TimeSpan.FromMinutes(10);
        await ChecksAsync(3, alice, bob);
        Assert.Equal(0, alice.Session.OldKeyTriesForTests + bob.Session.OldKeyTriesForTests);
        Assert.Equal(epoch, ServerEpoch(server, channelId));

        // Past a week: the next key, once, which both hold.
        clock.Offset = AWeek + TimeSpan.FromMinutes(1);
        await WaitFor(() => ServerEpoch(server, channelId) > epoch ? new object() : null);
        await HoldsAsync(alice, channelId, epoch + 1);
        await HoldsAsync(bob, channelId, epoch + 1);
        Assert.Equal(alice.LoadEpochKey(channelId, epoch + 1), bob.LoadEpochKey(channelId, epoch + 1));
        await ChecksAsync(3, alice, bob);
        Assert.Equal(epoch + 1, ServerEpoch(server, channelId));

        // Six days on, that key is young enough; a week and a bit on, it is replaced in turn.
        clock.Offset = AWeek + TimeSpan.FromDays(6);
        await ChecksAsync(3, alice, bob);
        Assert.Equal(epoch + 1, ServerEpoch(server, channelId));
        clock.Offset = AWeek + AWeek + TimeSpan.FromMinutes(2);
        await WaitFor(() => ServerEpoch(server, channelId) > epoch + 1 ? new object() : null);
        await HoldsAsync(alice, channelId, epoch + 2);
        await HoldsAsync(bob, channelId, epoch + 2);

        // Silent, and the channel as it was.
        await server.SendAndSettleAsync(alice);
        await server.SendAndSettleAsync(bob);
        Assert.Equal(notices.Item1 + 1, alice.Notices.Count);
        Assert.Equal(notices.Item2 + 1, bob.Notices.Count);
        Assert.True(bob.Session.Snapshot.FindChannel(channelId) is { Name: "Quiet Week Channel", RekeyPending: false, MembershipWarning: null });

        // The diagnostic logs say what happened at Debug, with no names.
        await alice.Session.DisposeAsync();
        var aged = logged.Where(entry => entry.Text.Contains("too old")).ToList();
        Assert.NotEmpty(aged);
        Assert.All(aged, entry => Assert.Equal(NoticeLevel.Debug, entry.Level));
        Assert.DoesNotContain(aged, entry => entry.Text.Contains("Alice Quiet") || entry.Text.Contains("Bob Quiet") || entry.Text.Contains("Quiet Week Channel"));
    }

    /// <summary>
    /// With four members online when the key turns a week old, and none of them waiting at random first (the worst case),
    /// the channel moves on by exactly one key: the server takes the first, the others' come too late and are dropped
    /// quietly, and everyone holds the same key.
    /// </summary>
    [Fact]
    public async Task OnlyOneOfTheMembersOnlineMakesTheNewKey() {
        var clock = new ManualClock();
        await using var server = new Harness(serverTime: clock);
        var members = new List<TestClient>();
        foreach (var name in new[] { "Alice One Key", "Bob One Key", "Carol One Key", "Dave One Key" }) {
            members.Add(await server.RegisterAsync(name, options: Often(server, clock)));
        }

        var channelId = await members[0].Session.CreateChannelAsync("One Key Channel", Ct);
        foreach (var member in members.Skip(1)) {
            await AddMemberAsync(members[0], channelId, member);
        }

        var epoch = ServerEpoch(server, channelId);
        foreach (var member in members) {
            await HoldsAsync(member, channelId, epoch);
        }

        var notices = members.Select(member => member.Notices.Count).ToList();

        clock.Offset = AWeek + TimeSpan.FromMinutes(1);
        foreach (var member in members) {
            await HoldsAsync(member, channelId, epoch + 1);
        }

        await ChecksAsync(3, [.. members]);
        Assert.Equal(epoch + 1, ServerEpoch(server, channelId));
        var key = members[0].LoadEpochKey(channelId, epoch + 1);
        Assert.All(members, member => Assert.Equal(key, member.LoadEpochKey(channelId, epoch + 1)));
        Assert.All(members, member => Assert.True(member.Session.Snapshot.FindChannel(channelId) is { RekeyPending: false, HasKey: true }));
        for (var i = 0; i < members.Count; i++) {
            await server.SendAndSettleAsync(members[i]);
            Assert.Equal(notices[i] + 1, members[i].Notices.Count);
        }
    }

    /// <summary>
    /// A member waiting at random to replace the old key, while another member makes the new one, makes none when its wait
    /// is over: it holds a new key by then.
    /// </summary>
    [Fact]
    public async Task AMemberStillWaitingWhenTheNewKeyArrivesMakesNone() {
        var clock = new ManualClock();
        await using var server = new Harness(serverTime: clock);
        var alice = await server.RegisterAsync("Alice First Key", options: Often(server, clock));
        var bob = await server.RegisterAsync("Bob Waits Key", options: Often(server, clock, jitter: TimeSpan.FromSeconds(3)));
        var channelId = await alice.Session.CreateChannelAsync("Waits Key Channel", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var epoch = ServerEpoch(server, channelId);
        await alice.Session.DisposeAsync();

        // Bob starts his wait; then Alice comes online and replaces the key at once.
        clock.Offset = AWeek + TimeSpan.FromMinutes(1);
        await WaitFor(() => bob.Session.OldKeyTriesForTests > 0 ? new object() : null);
        var first = server.StartClient(alice.Name, alice.Store, Often(server, clock));
        await HoldsAsync(bob, channelId, epoch + 1);
        await HoldsAsync(first, channelId, epoch + 1);

        // Bob's wait is over by now.
        await Task.Delay(TimeSpan.FromSeconds(3.5), Ct);
        await ChecksAsync(2, bob, first);
        Assert.Equal(epoch + 1, ServerEpoch(server, channelId));
    }

    /// <summary>A member alone in a channel makes no new key for it: nobody else could read what a stolen one opens.</summary>
    [Fact]
    public async Task AMemberAloneInAChannelMakesNoNewKey() {
        var clock = new ManualClock();
        await using var server = new Harness(serverTime: clock);
        var alice = await server.RegisterAsync("Alice Alone Week", options: Often(server, clock));
        var channelId = await alice.Session.CreateChannelAsync("Alone Week Channel", Ct);
        var epoch = ServerEpoch(server, channelId);

        clock.Offset = AWeek + TimeSpan.FromMinutes(1);
        await ChecksAsync(3, alice);
        Assert.Equal(0, alice.Session.OldKeyTriesForTests);
        Assert.Equal(epoch, ServerEpoch(server, channelId));
    }

    /// <summary>
    /// The first member online makes the new key; a member who was away throughout catches up across it: what was sent under
    /// the old key just before (stored for the week, which they were sealed) and under the new one after, in order. The
    /// member who made it needn't stay.
    /// </summary>
    [Fact]
    public async Task AMemberWhoWasAwayCatchesUpAcrossTheNewKey() {
        var clock = new ManualClock();
        await using var server = new Harness(serverTime: clock);
        // Alice makes the key, by the server's clock. Bob and Carol talk and read by their own (the real one): messages are
        // dated by it.
        var alice = await server.RegisterAsync("Alice Makes Weekly", options: Often(server, clock));
        var bob = await server.RegisterAsync("Bob Away Weekly", options: Often(server, null));
        var carol = await server.RegisterAsync("Carol Talks Weekly", options: Often(server, null));
        var channelId = await alice.Session.CreateChannelAsync("Weekly Channel", Ct);
        await AddMemberAsync(alice, channelId, bob);
        await AddMemberAsync(alice, channelId, carol);
        var epoch = ServerEpoch(server, channelId);
        await HoldsAsync(carol, channelId, epoch);
        await carol.Session.SendTextAsync(channelId, "while bob is here", Ct);
        await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == "while bob is here"));

        await bob.Session.DisposeAsync();
        await alice.Session.DisposeAsync();
        clock.Offset = AWeek - TimeSpan.FromMinutes(5);
        await carol.Session.SendTextAsync(channelId, "under the old key", Ct);

        // Alice comes online after the week is up, and makes the next key.
        clock.Offset = AWeek + TimeSpan.FromMinutes(1);
        var maker = server.StartClient(alice.Name, alice.Store, Often(server, clock));
        await WaitFor(() => ServerEpoch(server, channelId) > epoch ? new object() : null);
        await HoldsAsync(maker, channelId, epoch + 1);
        await maker.Session.DisposeAsync();

        await HoldsAsync(carol, channelId, epoch + 1);
        var carolNotices = carol.Notices.Count;
        await carol.Session.SendTextAsync(channelId, "under the new key", Ct);

        var back = server.StartClient(bob.Name, bob.Store, Often(server, null));
        await WaitFor(() => back.Session.CatchUpsDone > 0 ? new object() : null);
        await server.SendAndSettleAsync(back);
        var batch = Assert.Single(back.CaughtUp);
        Assert.Equal(["under the old key", "under the new key"], batch.Messages.Select(m => m.Text));
        Assert.Equal(epoch + 1, back.Session.Snapshot.FindChannel(channelId)!.Epoch);
        Assert.DoesNotContain(back.Notices, n => n.Level >= NoticeLevel.Warning);
        Assert.Equal(carolNotices, carol.Notices.Count);
    }

    /// <summary>
    /// A plugin from before never makes a key because the one in use is old, alone online with it; it takes the new key a
    /// newer member makes as any other.
    /// </summary>
    [Fact]
    public async Task APluginFromBeforeTakesTheNewKeyButNeverMakesOne() {
        var clock = new ManualClock();
        await using var server = new Harness(serverTime: clock);
        var alice = await server.RegisterAsync("Alice New Plugin", options: Often(server, clock));
        var bob = await server.RegisterAsync("Bob Old Plugin", options: Often(server, clock, replaceOldKeys: false));
        var channelId = await alice.Session.CreateChannelAsync("Old Plugin Channel", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var epoch = ServerEpoch(server, channelId);
        await alice.Session.DisposeAsync();

        clock.Offset = AWeek + TimeSpan.FromMinutes(1);
        await ChecksAsync(3, bob);
        Assert.Equal(0, bob.Session.OldKeyTriesForTests);
        Assert.Equal(epoch, ServerEpoch(server, channelId));

        var newer = server.StartClient(alice.Name, alice.Store, Often(server, clock));
        await HoldsAsync(newer, channelId, epoch + 1);
        await HoldsAsync(bob, channelId, epoch + 1);
        Assert.Equal(newer.LoadEpochKey(channelId, epoch + 1), bob.LoadEpochKey(channelId, epoch + 1));
        Assert.DoesNotContain(bob.Notices, n => n.Level >= NoticeLevel.Warning);
    }

    /// <summary>
    /// A server that changes the time a new key states (to make it look a week old, so it is replaced at once, and again,
    /// and again) changes nothing: the time is signed by the key's maker, and one that doesn't check out isn't believed.
    /// The key, which checks out, is taken, and aged from when it arrived.
    /// </summary>
    [Fact]
    public async Task AServerCantMakeAKeyLookOld() {
        var clock = new ManualClock();
        await using var server = new Harness(serverTime: clock);
        var alice = await server.RegisterAsync("Alice Dated Key", options: Often(server, clock, replaceOldKeys: false));
        var bob = await server.RegisterAsync("Bob Told Old", options: Often(server, clock));
        var channelId = await alice.Session.CreateChannelAsync("Dated Key Channel", Ct);
        await AddMemberAsync(alice, channelId, bob);
        await bob.Session.DisposeAsync();

        // From now on, every new key reaching Bob says it was made eight days ago.
        var eightDaysAgo = clock.GetUtcNow().AddDays(-8).ToUnixTimeMilliseconds();
        var lying = server.StartClient(bob.Name, bob.Store, Often(server, clock, wrap: socket => new RewritingWebSocket(socket, frame => {
            if (frame.Event?.EpochAdvanced?.MyKey is { } key) {
                key.CreatedUnixMs = eightDaysAgo;
            }

            return frame;
        })));
        await WaitFor(() => lying.Session.Snapshot is { State: ConnectionState.Ready, ChannelsLoaded: true } ? new object() : null);
        var epoch = ServerEpoch(server, channelId);
        await alice.Session.RekeyAsync(channelId, Ct, force: true);
        await HoldsAsync(lying, channelId, epoch + 1);

        await ChecksAsync(3, lying);
        Assert.Equal(0, lying.Session.OldKeyTriesForTests);
        Assert.Equal(epoch + 1, ServerEpoch(server, channelId));
        await lying.Session.DisposeAsync();
        Assert.Equal(0, lying.Store.Load().EpochKeyPositions[channelId][epoch + 1].CreatedMs);

        // A week after it arrived, it is old.
        clock.Offset = AWeek + TimeSpan.FromMinutes(1);
        var later = server.StartClient(bob.Name, bob.Store, Often(server, clock));
        await HoldsAsync(later, channelId, epoch + 2);
    }

    /// <summary>
    /// A key kept by a version from before keys' ages were judged, which doesn't say when it was made, counts from when this
    /// version first sees it: updating doesn't make every channel's key look old at once.
    /// </summary>
    [Fact]
    public async Task AKeyKeptByAnEarlierVersionIsAgedFromTheUpdate() {
        var clock = new ManualClock();
        await using var server = new Harness(serverTime: clock);
        var alice = await server.RegisterAsync("Alice Earlier Version", options: Often(server, clock, replaceOldKeys: false));
        var bob = await server.RegisterAsync("Bob Earlier Version", options: Often(server, clock, replaceOldKeys: false));
        var channelId = await alice.Session.CreateChannelAsync("Earlier Version Channel", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var epoch = ServerEpoch(server, channelId);
        await bob.Session.DisposeAsync();

        // As an earlier version kept it: when it was made, and when it arrived, unknown.
        var secrets = bob.Store.Load();
        var held = secrets.EpochKeyPositions[channelId][epoch];
        (held.CreatedMs, held.HeldSinceMs) = (0, 0);
        bob.Store.Save(secrets);

        clock.Offset = AWeek + TimeSpan.FromMinutes(1);
        var updated = server.StartClient(bob.Name, bob.Store, Often(server, clock));
        await WaitFor(() => updated.Session.Snapshot is { State: ConnectionState.Ready, ChannelsLoaded: true } ? new object() : null);
        await ChecksAsync(3, updated);
        Assert.Equal(0, updated.Session.OldKeyTriesForTests);
        Assert.Equal(epoch, ServerEpoch(server, channelId));

        clock.Offset = AWeek + AWeek + TimeSpan.FromMinutes(2);
        await HoldsAsync(updated, channelId, epoch + 1);
    }

    /// <summary>
    /// A server can keep an old key in use by refusing every new one (a known limitation: it can withhold, not forge). The
    /// client tries once, says nothing, and doesn't try again for that channel for an hour, so a refusing server can't make
    /// it rekey over and over.
    /// </summary>
    [Fact]
    public async Task AServerRefusingNewKeysIsAskedAtMostOnceAnHour() {
        var clock = new ManualClock();
        await using var server = new Harness(serverTime: clock);
        var alice = await server.RegisterAsync("Alice Refused", options: Often(server, clock, replaceOldKeys: false));
        var bob = await server.RegisterAsync("Bob Refused", options: Often(server, clock));
        var channelId = await alice.Session.CreateChannelAsync("Refused Channel", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var epoch = ServerEpoch(server, channelId);
        await alice.Session.DisposeAsync();
        await bob.Session.DisposeAsync();

        RefusedRekeysWebSocket? socket = null;
        var refused = server.StartClient(bob.Name, bob.Store, Often(server, clock, wrap: inner => socket = new RefusedRekeysWebSocket(inner)));
        await WaitFor(() => refused.Session.Snapshot is { State: ConnectionState.Ready, ChannelsLoaded: true } ? new object() : null);
        var notices = refused.Notices.Count;

        clock.Offset = AWeek + TimeSpan.FromMinutes(1);
        await WaitFor(() => socket!.Rekeys >= 1 ? new object() : null);
        await ChecksAsync(5, refused);
        Assert.Equal(1, socket!.Rekeys);
        Assert.Equal(epoch, ServerEpoch(server, channelId));

        clock.Offset += TimeSpan.FromMinutes(59);
        await ChecksAsync(3, refused);
        Assert.Equal(1, socket.Rekeys);

        clock.Offset += TimeSpan.FromMinutes(2);
        await WaitFor(() => socket.Rekeys >= 2 ? new object() : null);
        await ChecksAsync(3, refused);
        Assert.Equal(2, socket.Rekeys);
        await server.SendAndSettleAsync(refused);
        Assert.Equal(notices + 1, refused.Notices.Count);
    }

    /// <summary>
    /// A client's WebSocket on which the server refuses every new key: each SubmitRekey is spoiled on its way (its epoch
    /// changed, which its signatures then don't match), so the real server answers it with an error, as one withholding new
    /// keys would. Counts them.
    /// </summary>
    private sealed class RefusedRekeysWebSocket(WebSocket inner) : WebSocket {
        private int _rekeys;

        public int Rekeys => Volatile.Read(ref this._rekeys);

        public override WebSocketCloseStatus? CloseStatus => inner.CloseStatus;
        public override string? CloseStatusDescription => inner.CloseStatusDescription;
        public override WebSocketState State => inner.State;
        public override string? SubProtocol => inner.SubProtocol;

        private ArraySegment<byte> Spoil(ReadOnlySpan<byte> buffer) {
            // The client sends each request as one message.
            var frame = ClientFrame.Parser.ParseFrom(buffer);
            if (frame.SubmitRekey is { } rekey) {
                Interlocked.Increment(ref this._rekeys);
                rekey.NewEpoch += 1000;
            }

            return frame.ToByteArray();
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            inner.SendAsync(this.Spoil(buffer.AsSpan()), messageType, endOfMessage, cancellationToken);

        public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            inner.SendAsync((ReadOnlyMemory<byte>) this.Spoil(buffer.Span), messageType, endOfMessage, cancellationToken);

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => inner.ReceiveAsync(buffer, cancellationToken);

        public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) => inner.ReceiveAsync(buffer, cancellationToken);

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => inner.CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

        public override void Abort() => inner.Abort();

        public override void Dispose() => inner.Dispose();
    }
}
