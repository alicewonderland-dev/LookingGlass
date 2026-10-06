using System.Collections.Concurrent;
using System.Net.WebSockets;
using Google.Protobuf;
using LookingGlass.Core.Client;
using LookingGlass.Protocol;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// Member presence: who is online, as the server tells members who share a channel, and
/// as the client shows it on <see cref="MemberView.Online"/>.
/// </summary>
public sealed class PresenceTests : IAsyncLifetime {
    private Harness _server = null!;

    public ValueTask InitializeAsync() {
        this._server = new Harness();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() {
        await this._server.DisposeAsync();
        DeleteDirectory(this._server.DataDirectory);
    }

    [Fact]
    public async Task MemberIsSeenComingOnlineAndGoingOfflineByOnlineCoMembers() {
        var alice = await this._server.RegisterAsync("Alice Presence");
        var bob = await this._server.RegisterAsync("Bob Presence");
        var (aliceId, bobId) = (alice.UserId, bob.UserId);
        var channelId = await alice.Session.CreateChannelAsync("Lounge", Ct);
        await AddMemberAsync(alice, channelId, bob);

        await WaitFor(() => MemberOf(alice, channelId, bobId) is { Online: true } m ? m : null);
        await WaitFor(() => MemberOf(bob, channelId, aliceId) is { Online: true } m ? m : null);

        await bob.Session.DisposeAsync();
        await WaitFor(() => MemberOf(alice, channelId, bobId) is { Online: false } m ? m : null);

        await this._server.RestartAsync(bob);
        await WaitFor(() => MemberOf(alice, channelId, bobId) is { Online: true } m ? m : null);
    }

    [Fact]
    public async Task OnlyTheFirstConnectionAnnouncesAndOnlyTheLastCloseReportsOffline() {
        var alice = await this._server.RegisterAsync("Alice Twice");
        var bob = await this._server.RegisterAsync("Bob Twice");
        var (aliceId, bobId) = (alice.UserId, bob.UserId);
        var channelId = await alice.Session.CreateChannelAsync("Two Doors", Ct);
        await AddMemberAsync(alice, channelId, bob);
        await alice.Session.DisposeAsync();
        await bob.Session.DisposeAsync();
        await WaitFor(() => !this._server.Registry.IsOnline(aliceId) && !this._server.Registry.IsOnline(bobId) ? new object() : null);

        await using var observer = await RawClient.ConnectAsync(this._server, alice.Store);
        var first = await RawClient.ConnectAsync(this._server, bob.Store);
        await WaitFor(() => observer.Presence.Count == 1 ? new object() : null);
        Assert.Equal((bobId, true), observer.Presence[0]);

        // A second connection takes over from the first: Bob stays online throughout, so nothing is announced.
        var second = await RawClient.ConnectAsync(this._server, bob.Store);
        await first.Closed.WaitAsync(Harness.Timeout, Ct);
        await first.DisposeAsync();
        await Task.Delay(300, Ct);
        await observer.SettleAsync(this._server, aliceId);
        Assert.Single(observer.Presence);
        Assert.True(this._server.Registry.IsOnline(bobId));

        // Closing the last one does.
        await second.DisposeAsync();
        await WaitFor(() => observer.Presence.Count == 2 ? new object() : null);
        Assert.Equal((bobId, false), observer.Presence[1]);
        await Task.Delay(200, Ct);
        await observer.SettleAsync(this._server, aliceId);
        Assert.Equal(2, observer.Presence.Count);
    }

    /// <summary>
    /// Who to tell that someone came online is a database query. It used to run under the one lock every login and
    /// disconnect takes, so a slow one held up everyone else's: here Alice's is held, and Bob still logs in meanwhile.
    /// </summary>
    [Fact]
    public async Task ASlowLookupOfWhomToTellDoesNotHoldUpOtherLogins() {
        var alice = await this._server.RegisterAsync("Alice Slow Lookup");
        var bob = await this._server.RegisterAsync("Bob Not Held Up");
        var (aliceId, bobId) = (alice.UserId, bob.UserId);
        await alice.Session.DisposeAsync();
        await bob.Session.DisposeAsync();
        await WaitFor(() => !this._server.Registry.IsOnline(aliceId) && !this._server.Registry.IsOnline(bobId) ? new object() : null);

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        this._server.Registry.AfterCoMemberQueryForTests = userId => {
            if (userId == aliceId && !entered.IsSet) {
                entered.Set();
                release.Wait(Harness.Timeout);
            }
        };

        try {
            var aliceLogin = Task.Run(() => RawClient.ConnectAsync(this._server, alice.Store), Ct);
            Assert.True(entered.Wait(Harness.Timeout, Ct));

            await using var bobRaw = await RawClient.ConnectAsync(this._server, bob.Store).WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.True(this._server.Registry.IsOnline(bobId));

            release.Set();
            await using var aliceRaw = await aliceLogin.WaitAsync(Harness.Timeout, Ct);
            Assert.True(this._server.Registry.IsOnline(aliceId));
        } finally {
            release.Set();
            this._server.Registry.AfterCoMemberQueryForTests = null;
        }
    }

    /// <summary>
    /// The lookup of whom to tell now runs outside that lock, so it can be out of date by the time Bob is online: Carol
    /// joins the channel meanwhile, after her channel list said he was offline. She is told anyway.
    /// </summary>
    [Fact]
    public async Task SomeoneWhoJoinsWhileAMemberComesOnlineIsToldTheyAreOnline() {
        var alice = await this._server.RegisterAsync("Alice Joins Race");
        var bob = await this._server.RegisterAsync("Bob Joins Race");
        var carol = await this._server.RegisterAsync("Carol Joins Race");
        var bobId = bob.UserId;
        var channelId = await alice.Session.CreateChannelAsync("Join Race", Ct);
        await AddMemberAsync(alice, channelId, bob);
        await alice.Session.InviteAsync(channelId, carol.Name, ProtocolInfo.DebugWorldName, Ct);
        await WaitFor(() => carol.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == channelId && i.Verified));
        await bob.Session.DisposeAsync();
        await WaitFor(() => this._server.Registry.IsOnline(bobId) ? null : new object());

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        this._server.Registry.AfterCoMemberQueryForTests = userId => {
            if (userId == bobId && !entered.IsSet) {
                entered.Set();
                release.Wait(Harness.Timeout);
            }
        };

        try {
            // Bob's lookup is made (without Carol) and held; Carol joins, and is shown him offline.
            var bobLogin = Task.Run(() => RawClient.ConnectAsync(this._server, bob.Store), Ct);
            Assert.True(entered.Wait(Harness.Timeout, Ct));
            await carol.Session.RespondToInviteAsync(channelId, true, Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.False(MemberOf(carol, channelId, bobId)!.Online);

            release.Set();
            await using var bobRaw = await bobLogin.WaitAsync(Harness.Timeout, Ct);
            await WaitFor(() => MemberOf(carol, channelId, bobId) is { Online: true } m ? m : null);
        } finally {
            release.Set();
            this._server.Registry.AfterCoMemberQueryForTests = null;
        }
    }

    /// <summary>
    /// The same going offline: Bob's lookup of whom to tell is made (without Carol) and held while he is still online; Carol
    /// joins, and is shown him online. When he goes, she is told.
    /// </summary>
    [Fact]
    public async Task SomeoneWhoJoinsWhileAMemberGoesOfflineIsToldTheyWent() {
        var alice = await this._server.RegisterAsync("Alice Leaves Race");
        var bob = await this._server.RegisterAsync("Bob Leaves Race");
        var carol = await this._server.RegisterAsync("Carol Leaves Race");
        var bobId = bob.UserId;
        var channelId = await alice.Session.CreateChannelAsync("Leave Race", Ct);
        await AddMemberAsync(alice, channelId, bob);
        await alice.Session.InviteAsync(channelId, carol.Name, ProtocolInfo.DebugWorldName, Ct);
        await WaitFor(() => carol.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == channelId && i.Verified));

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        this._server.Registry.AfterCoMemberQueryForTests = userId => {
            if (userId == bobId && !entered.IsSet) {
                entered.Set();
                release.Wait(Harness.Timeout);
            }
        };

        try {
            await bob.Session.DisposeAsync();
            Assert.True(entered.Wait(Harness.Timeout, Ct));
            await carol.Session.RespondToInviteAsync(channelId, true, Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.True(MemberOf(carol, channelId, bobId)!.Online);

            release.Set();
            await WaitFor(() => MemberOf(carol, channelId, bobId) is { Online: false } m ? m : null);
        } finally {
            release.Set();
            this._server.Registry.AfterCoMemberQueryForTests = null;
        }
    }

    [Fact]
    public async Task ConnectionsOpeningAndClosingAtOnceStillAlternate() {
        var alice = await this._server.RegisterAsync("Alice Race");
        var bob = await this._server.RegisterAsync("Bob Race");
        var (aliceId, bobId) = (alice.UserId, bob.UserId);
        var channelId = await alice.Session.CreateChannelAsync("Crowd", Ct);
        await AddMemberAsync(alice, channelId, bob);
        await alice.Session.DisposeAsync();
        await bob.Session.DisposeAsync();
        await WaitFor(() => !this._server.Registry.IsOnline(aliceId) && !this._server.Registry.IsOnline(bobId) ? new object() : null);

        await using var observer = await RawClient.ConnectAsync(this._server, alice.Store);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(async () => {
            try {
                await using var connection = await RawClient.ConnectAsync(this._server, bob.Store);
                await Task.Delay(Random.Shared.Next(0, 20), Ct);
            } catch (Exception) {
                // Replaced by another of Bob's connections before it was logged in.
            }
        }, Ct)));

        await WaitFor(() => !this._server.Registry.IsOnline(bobId) ? new object() : null);
        await Task.Delay(200, Ct);
        await observer.SettleAsync(this._server, aliceId);

        // Never online twice, nor offline twice, in a row; offline at the end.
        var events = observer.Presence;
        Assert.NotEmpty(events);
        Assert.All(events, presence => Assert.Equal(bobId, presence.UserId));
        for (var i = 0; i < events.Count; i++) {
            Assert.Equal(i % 2 == 0, events[i].Online);
        }

        Assert.False(events[^1].Online);
    }

    [Fact]
    public async Task StrangersAndInviteesAreNeverToldAndNeverTell() {
        var alice = await this._server.RegisterAsync("Alice Private");
        var bob = await this._server.RegisterAsync("Bob Private");
        var carol = await this._server.RegisterAsync("Carol Private");
        var dave = await this._server.RegisterAsync("Dave Private");
        var (aliceId, bobId, carolId, daveId) = (alice.UserId, bob.UserId, carol.UserId, dave.UserId);
        var channelId = await alice.Session.CreateChannelAsync("Members Only", Ct);
        await AddMemberAsync(alice, channelId, bob);
        // Carol is only invited; Dave shares nothing with anyone.
        await alice.Session.InviteAsync(channelId, "Carol Private", ProtocolInfo.DebugWorldName, Ct);
        await WaitFor(() => carol.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == channelId));
        foreach (var client in new[] { alice, bob, carol, dave }) {
            await client.Session.DisposeAsync();
        }

        await WaitFor(() => new[] { aliceId, bobId, carolId, daveId }.Any(this._server.Registry.IsOnline) ? null : new object());

        await using var aliceRaw = await RawClient.ConnectAsync(this._server, alice.Store);
        await using var carolRaw = await RawClient.ConnectAsync(this._server, carol.Store);
        await using var daveRaw = await RawClient.ConnectAsync(this._server, dave.Store);
        var bobRaw = await RawClient.ConnectAsync(this._server, bob.Store);
        await WaitFor(() => aliceRaw.Presence.Count == 1 ? new object() : null);
        await bobRaw.DisposeAsync();
        await WaitFor(() => aliceRaw.Presence.Count == 2 ? new object() : null);
        await Task.Delay(200, Ct);

        await aliceRaw.SettleAsync(this._server, aliceId);
        await carolRaw.SettleAsync(this._server, carolId);
        await daveRaw.SettleAsync(this._server, daveId);

        // Alice heard about Bob only: not about Carol (an invitee) or Dave (a stranger) connecting.
        Assert.Equal(new List<(long, bool)> { (bobId, true), (bobId, false) }, aliceRaw.Presence);
        Assert.Empty(carolRaw.Presence);
        Assert.Empty(daveRaw.Presence);
    }

    [Fact]
    public async Task ChannelInfoSaysWhichMembersAreOnline() {
        var alice = await this._server.RegisterAsync("Alice Listing");
        var bob = await this._server.RegisterAsync("Bob Listing");
        var carol = await this._server.RegisterAsync("Carol Listing");
        var (aliceId, bobId, carolId) = (alice.UserId, bob.UserId, carol.UserId);
        var channelId = await alice.Session.CreateChannelAsync("Roll Call", Ct);
        await AddMemberAsync(alice, channelId, bob);
        await alice.Session.InviteAsync(channelId, "Carol Listing", ProtocolInfo.DebugWorldName, Ct);
        await WaitFor(() => carol.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == channelId));
        await alice.Session.DisposeAsync();
        await WaitFor(() => this._server.Registry.IsOnline(aliceId) ? null : new object());

        await using var aliceRaw = await RawClient.ConnectAsync(this._server, alice.Store);
        var members = (await aliceRaw.ListChannelsAsync()).Single(c => c.ChannelId == channelId).Members.ToDictionary(m => m.User.UserId);
        Assert.True(members[aliceId].Online);
        Assert.True(members[bobId].Online);
        // Carol is connected, but only invited: her presence isn't shared.
        Assert.Equal(Rank.Invited, members[carolId].Rank);
        Assert.False(members[carolId].Online);

        await bob.Session.DisposeAsync();
        await WaitFor(() => this._server.Registry.IsOnline(bobId) ? null : new object());
        members = (await aliceRaw.ListChannelsAsync()).Single(c => c.ChannelId == channelId).Members.ToDictionary(m => m.User.UserId);
        Assert.True(members[aliceId].Online);
        Assert.False(members[bobId].Online);
    }

    [Fact]
    public async Task ReconnectingReplacesPresenceInsteadOfKeepingItStale() {
        var gate = new Gate();
        var alice = await this._server.RegisterAsync("Alice Away", options: this._server.Options(beforeConnect: gate.WaitAsync));
        var bob = await this._server.RegisterAsync("Bob Away");
        var (aliceId, bobId) = (alice.UserId, bob.UserId);
        var channelId = await alice.Session.CreateChannelAsync("Night Shift", Ct);
        await AddMemberAsync(alice, channelId, bob);
        await WaitFor(() => MemberOf(alice, channelId, bobId) is { Online: true } m ? m : null);
        Assert.True(MemberOf(alice, channelId, aliceId)!.Online);

        // Alice drops off. While she's away, nothing she knew about presence is shown, her own row included.
        gate.Close();
        alice.Session.Reconnect();
        await WaitFor(() => alice.Session.Snapshot.State != ConnectionState.Ready ? new object() : null);
        await WaitFor(() => MemberOf(alice, channelId, bobId) is { Online: false } m ? m : null);
        Assert.False(MemberOf(alice, channelId, aliceId)!.Online);

        // Bob leaves while she can't hear it, and isn't shown online when she comes back.
        await bob.Session.DisposeAsync();
        await WaitFor(() => this._server.Registry.IsOnline(bobId) ? null : new object());
        gate.Open();
        await WaitFor(() => alice.Session.Snapshot is { State: ConnectionState.Ready, ChannelsLoaded: true } ? new object() : null);
        await alice.Session.RefreshAsync(Ct);
        Assert.False(MemberOf(alice, channelId, bobId)!.Online);
        Assert.True(MemberOf(alice, channelId, aliceId)!.Online);

        // And comes online again.
        await this._server.RestartAsync(bob);
        await WaitFor(() => MemberOf(alice, channelId, bobId) is { Online: true } m ? m : null);
    }

    [Fact]
    public async Task MemberWhoJoinsIsSeenOnlineAndSeesTheOthers() {
        var alice = await this._server.RegisterAsync("Alice Host");
        var bob = await this._server.RegisterAsync("Bob Guest");
        var (aliceId, bobId) = (alice.UserId, bob.UserId);
        var channelId = await alice.Session.CreateChannelAsync("Open House", Ct);

        // Invited, Bob isn't shown online (invitees' presence isn't shared)...
        await alice.Session.InviteAsync(channelId, "Bob Guest", ProtocolInfo.DebugWorldName, Ct);
        await WaitFor(() => MemberOf(alice, channelId, bobId));
        Assert.False(MemberOf(alice, channelId, bobId)!.Online);

        // ...until he joins: Alice, who shares no other channel with him, sees him online, and he sees her.
        await WaitFor(() => bob.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == channelId && i.ChannelName != null));
        await bob.Session.RespondToInviteAsync(channelId, true, Ct);
        await WaitFor(() => MemberOf(alice, channelId, bobId) is { Rank: Rank.Member, Online: true } m ? m : null);
        await WaitFor(() => MemberOf(bob, channelId, aliceId) is { Online: true } m ? m : null);
        Assert.True(MemberOf(bob, channelId, bobId)!.Online);
    }

    [Fact]
    public async Task MemberViewFollowsPresenceEvents() {
        var alice = await this._server.RegisterAsync("Alice Events");
        var bob = await this._server.RegisterAsync("Bob Events");
        var carol = await this._server.RegisterAsync("Carol Events");
        var (bobId, carolId) = (bob.UserId, carol.UserId);
        var channelId = await alice.Session.CreateChannelAsync("Signals", Ct);
        await AddMemberAsync(alice, channelId, bob);
        await alice.Session.InviteAsync(channelId, "Carol Events", ProtocolInfo.DebugWorldName, Ct);
        await WaitFor(() => MemberOf(alice, channelId, carolId));
        await WaitFor(() => MemberOf(alice, channelId, bobId) is { Online: true } m ? m : null);

        await this._server.SendAndSettleAsync(alice, Presence(bobId, false));
        Assert.False(MemberOf(alice, channelId, bobId)!.Online);

        await this._server.SendAndSettleAsync(alice, Presence(bobId, true));
        Assert.True(MemberOf(alice, channelId, bobId)!.Online);

        // An invitee is never shown online, whatever the server says.
        await this._server.SendAndSettleAsync(alice, Presence(carolId, true));
        Assert.False(MemberOf(alice, channelId, carolId)!.Online);
    }

    private static Event Presence(long userId, bool online) => new() { PresenceChanged = new PresenceChanged { UserId = userId, Online = online } };

    private static MemberView? MemberOf(TestClient viewer, string channelId, long userId) =>
        viewer.Session.Snapshot.FindChannel(channelId)?.Members.FirstOrDefault(member => member.User.UserId == userId);

    /// <summary>Holds a client's connection attempts while closed.</summary>
    private sealed class Gate {
        private volatile TaskCompletionSource _open = Opened();

        public void Close() => this._open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open() => this._open.TrySetResult();

        public Task WaitAsync(CancellationToken ct) => this._open.Task.WaitAsync(ct);

        private static TaskCompletionSource Opened() {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            tcs.SetResult();
            return tcs;
        }
    }

    /// <summary>A bare protocol client logged in with a registered client's device token. Records the presence events it gets.</summary>
    private sealed class RawClient : IAsyncDisposable {
        private readonly WebSocket _socket;
        private readonly ConcurrentQueue<Event> _events = new();
        private readonly ConcurrentDictionary<uint, TaskCompletionSource<Response>> _pending = new();
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _receive;
        private int _nextRequestId;

        private RawClient(WebSocket socket) {
            this._socket = socket;
            this._receive = Task.Run(this.ReceiveLoop);
        }

        /// <summary>Completes when the server has closed the connection.</summary>
        public Task Closed => this._closed.Task;

        public List<(long UserId, bool Online)> Presence => this._events
            .Where(ev => ev.KindCase == Event.KindOneofCase.PresenceChanged)
            .Select(ev => (ev.PresenceChanged.UserId, ev.PresenceChanged.Online))
            .ToList();

        public static async Task<RawClient> ConnectAsync(Harness server, ISecretStore store) {
            var socket = await server.Factory.Server.CreateWebSocketClient().ConnectAsync(new Uri(server.Factory.Server.BaseAddress, ProtocolInfo.WebSocketPath), Ct);
            var client = new RawClient(socket);
            try {
                var hello = new Hello { ClientVersion = "test" };
                hello.ProtocolVersions.Add(ProtocolInfo.CurrentVersion);
                await client.RequestAsync(new ClientFrame { Hello = hello });
                var ok = await client.RequestAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = store.Load().DeviceToken } });
                if (ok.AuthenticateOk == null) {
                    throw new InvalidOperationException($"Login failed: {ok.Error?.Message}");
                }
            } catch {
                await client.DisposeAsync();
                throw;
            }

            return client;
        }

        public async Task<IReadOnlyList<ChannelInfo>> ListChannelsAsync() {
            var response = await this.RequestAsync(new ClientFrame { ListChannels = new ListChannels() });
            return response.ChannelList?.Channels.ToList() ?? throw new InvalidOperationException($"ListChannels failed: {response.Error?.Message}");
        }

        /// <summary>Has the server send this user an announcement and waits for it: every event sent before it has arrived.</summary>
        public async Task SettleAsync(Harness server, long userId) {
            var sentinel = "sentinel " + Guid.NewGuid().ToString("N");
            server.Registry.Send(userId, new Event { Announcement = new Announcement { Text = sentinel } });
            await WaitFor(() => this._events.Any(ev => ev.Announcement?.Text == sentinel) ? new object() : null);
        }

        public async Task<Response> RequestAsync(ClientFrame frame) {
            frame.RequestId = (uint) Interlocked.Increment(ref this._nextRequestId);
            var tcs = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
            this._pending[frame.RequestId] = tcs;
            if (this.Closed.IsCompleted) {
                throw new InvalidOperationException("Closed.");
            }

            await this._socket.SendAsync(frame.ToByteArray(), WebSocketMessageType.Binary, true, Ct);
            return await tcs.Task.WaitAsync(Harness.Timeout, Ct);
        }

        private async Task ReceiveLoop() {
            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();
            try {
                while (true) {
                    var result = await this._socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) {
                        break;
                    }

                    message.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage) {
                        continue;
                    }

                    var frame = ServerFrame.Parser.ParseFrom(message.GetBuffer(), 0, (int) message.Length);
                    message.SetLength(0);
                    if (frame.Event != null) {
                        this._events.Enqueue(frame.Event);
                    } else if (frame.Response != null && this._pending.TryRemove(frame.Response.RequestId, out var tcs)) {
                        tcs.TrySetResult(frame.Response);
                    }
                }
            } catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException) {
                // Closed.
            } finally {
                this._closed.TrySetResult();
                foreach (var tcs in this._pending.Values) {
                    tcs.TrySetException(new InvalidOperationException("Closed."));
                }
            }
        }

        public async ValueTask DisposeAsync() {
            try {
                if (this._socket.State is WebSocketState.Open or WebSocketState.CloseReceived) {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await this._socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
                }
            } catch {
                // Best effort.
            }

            await this._receive.WaitAsync(Harness.Timeout).ContinueWith(_ => { }, TaskScheduler.Default);
            this._socket.Dispose();
        }
    }
}
