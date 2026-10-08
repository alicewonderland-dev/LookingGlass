using System.Collections.Concurrent;
using System.Net.WebSockets;
using Google.Protobuf;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LookingGlass.Core.Client;
using LookingGlass.Core.Crypto;
using LookingGlass.Core.Membership;
using LookingGlass.Protocol;
using LookingGlass.Server.Data;
using LookingGlass.Server.Realtime;

namespace LookingGlass.Tests;

/// <summary>An in-process server plus helpers to create registered clients.</summary>
public sealed class Harness : IAsyncDisposable {
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly List<TestClient> _clients = [];

    // Everything the server logs, whatever the test asked for: a request that failed with an unexpected exception (which
    // the client only sees as "Internal server error") fails the test, with the exception, when the server is disposed.
    private readonly CapturingLoggerProvider _serverLogs = new();

    /// <param name="serverTime">The server's clock, for tests that move it forward (key login challenges expire by it).</param>
    /// <param name="environment">
    /// The server's hosting environment. In Development (the default here, as on the test server) key login may go by
    /// the connection's Host header when no PublicUrls are set; anywhere else it needs them.
    /// </param>
    /// <param name="logs">Also gets everything the server logs.</param>
    /// <param name="lodestone">Answers the server's Lodestone requests instead of the real Lodestone, for registering real characters.</param>
    /// <param name="settings">Extra server configuration, for example <c>("LookingGlass:Limits:MaxIdentitiesPerRequest", "2")</c>.</param>
    public Harness(string? dataDirectory = null, bool allowDebugAccounts = true, TimeProvider? serverTime = null, string environment = "Development",
        CapturingLoggerProvider? logs = null, FakeLodestone? lodestone = null, params (string Key, string Value)[] settings) {
        this._ownsDataDirectory = dataDirectory == null;
        this.DataDirectory = dataDirectory ?? Path.Combine(Path.GetTempPath(), "lgt-" + Guid.NewGuid().ToString("N"));
        Live[LiveKey(this.DataDirectory)] = this;
        this.Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => {
            builder.UseEnvironment(environment);
            // Only to these: not to the console, nor (on Windows) to the machine's Event Log, which the server's defaults include.
            builder.ConfigureLogging(logging => logging.ClearProviders().AddProvider(this._serverLogs));
            if (logs != null) {
                builder.ConfigureLogging(logging => logging.AddProvider(logs));
            }

            builder.UseSetting("LookingGlass:DataDirectory", this.DataDirectory);
            builder.UseSetting("LookingGlass:Dev:AllowDebugAccounts", allowDebugAccounts ? "true" : "false");
            builder.UseSetting("LookingGlass:Dev:HostEchoBot", "false");
            // Tests of other environments use debug accounts too, which a real server there refuses without this.
            builder.UseSetting("LookingGlass:Dev:AllowOutsideDevelopment", allowDebugAccounts && environment != "Development" ? "true" : "false");
            // Every test client connects from the same (unknown) address, so the per-address connection limits would apply to
            // a test's clients together; tests of those limits set them, and give their connections addresses.
            builder.UseSetting("LookingGlass:Limits:ConnectionsPerIp", "1000");
            builder.UseSetting("LookingGlass:Limits:NotLoggedInConnectionsPerIp", "1000");
            builder.UseSetting("LookingGlass:Limits:ConnectionsPerMinutePerIp", "100000");
            foreach (var (key, value) in settings) {
                builder.UseSetting(key, value);
            }

            if (serverTime != null) {
                builder.ConfigureTestServices(services => services.AddSingleton(serverTime));
            }

            if (lodestone != null) {
                builder.UseSetting("LookingGlass:Lodestone:BaseUrl", FakeLodestone.BaseUrl);
                builder.UseSetting("LookingGlass:Lodestone:MinDelaySeconds", "0");
                builder.ConfigureTestServices(services => services.AddHttpClient<LookingGlass.Server.Services.LodestoneClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => lodestone));
            }
        });
        try {
            _ = this.Factory.Server;
        } catch {
            // Never started (it refused to): nothing will dispose it.
            Live.TryRemove(new KeyValuePair<string, Harness>(LiveKey(this.DataDirectory), this));
            throw;
        }
    }

    /// <summary>The address clients connect to, as <see cref="Options"/> gives it (what they sign for key login).</summary>
    public Uri ServerUri => new(this.Factory.Server.BaseAddress, ProtocolInfo.WebSocketPath);

    /// <summary>
    /// Opens a WebSocket that sends exactly the requests a test gives it, as a misbehaving (or relaying) client would.
    /// </summary>
    /// <param name="host">The Host header to send, instead of the server's own.</param>
    /// <param name="remoteAddress">The address the connection comes from (by default none, which per-IP limits count as one address).</param>
    /// <param name="forwardedFor">X-Forwarded-For and X-Forwarded-Proto, as a proxy in front of the server would add them.</param>
    /// <param name="hello">Send Hello first.</param>
    public async Task<RawConnection> ConnectRawAsync(string? host = null, string? remoteAddress = null, (string For, string Proto)? forwardedFor = null, bool hello = true) {
        var client = this.Factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => {
            if (host != null) {
                request.Host = new Microsoft.AspNetCore.Http.HostString(host);
            }

            if (remoteAddress != null) {
                request.HttpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(remoteAddress);
            }

            if (forwardedFor is { } forwarded) {
                request.Headers["X-Forwarded-For"] = forwarded.For;
                request.Headers["X-Forwarded-Proto"] = forwarded.Proto;
            }
        };

        var raw = new RawConnection(await client.ConnectAsync(this.ServerUri, Ct));
        if (hello) {
            var hi = new Hello();
            hi.ProtocolVersions.Add(ProtocolInfo.CurrentVersion);
            Assert.NotNull((await raw.SendAsync(new ClientFrame { Hello = hi })).Welcome);
        }

        this._disposables.Add(raw);
        return raw;
    }

    /// <summary>
    /// The server's data folder. One the harness made (none was given) is deleted when it is disposed; one a test gave is the
    /// test's to keep or delete (to start another server on it, say).
    /// </summary>
    public string DataDirectory { get; }

    private readonly bool _ownsDataDirectory;

    // Asked to delete its folder (by DeleteDirectory) while it still ran: done when it is disposed.
    private volatile bool _deleteOnDispose;

    // The servers running now, by their data folder (as LiveKey gives it).
    private static readonly ConcurrentDictionary<string, Harness> Live = new(StringComparer.OrdinalIgnoreCase);

    private static string LiveKey(string directory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    public WebApplicationFactory<Program> Factory { get; }

    /// <summary>The server's connection registry: lets a test act as a malicious server and push arbitrary events.</summary>
    public ConnectionRegistry Registry => this.Factory.Services.GetRequiredService<ConnectionRegistry>();

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Held by tests of a server that refuses to start: it sets the process's exit code, which they check and then put back,
    /// and which every test running at the same time shares (two at once would put back each other's).
    /// </summary>
    public static readonly SemaphoreSlim ExitCodeGate = new(1, 1);

    /// <summary>A StartRegistration's client nonce, as a client makes it: 32 random bytes.</summary>
    public static ByteString NewClientNonce() => ByteString.CopyFrom(System.Security.Cryptography.RandomNumberGenerator.GetBytes(LodestoneCode.ClientNonceSize));

    /// <summary>The server's database, for tests that play a malicious or misbehaving server.</summary>
    public Database Database => this.Factory.Services.GetRequiredService<Database>();

    /// <summary>The server's request handler, for its test hooks.</summary>
    public RequestHandler Handler => this.Factory.Services.GetRequiredService<RequestHandler>();

    /// <param name="beforeConnect">Awaited before every connection attempt, so a test can keep a client offline.</param>
    /// <param name="protocolVersion">The protocol version offered in Hello, to play an older plugin.</param>
    /// <param name="wrap">Wraps every connection's WebSocket, for example in a <see cref="HoldingWebSocket"/>.</param>
    /// <param name="forkCheckInterval">How often the client may fetch a channel's whole log to look into a possible fork.</param>
    /// <param name="loginRetryDelay">How often a saved login the server didn't recognise is tried again (by default, soon and often).</param>
    /// <param name="serverUri">The address the client thinks it connects to (and signs for); it reaches this server whatever it is.</param>
    /// <param name="offerCatchUp">Offer message catch-up in Hello; off plays a plugin from before it (0.2.5).</param>
    /// <param name="catchUpWithoutPosition">How far back a channel without a position catches up.</param>
    /// <param name="maxHeldLive">How many live messages are held back while catching up.</param>
    /// <param name="replaySaveDelay">How soon changed message times and positions are saved.</param>
    /// <param name="offerLocalChat">Offer local chat in Hello; off plays a plugin from before it.</param>
    public ClientSessionOptions Options(bool autoRekey = true, Action<NoticeLevel, string>? log = null, TimeProvider? time = null, Func<CancellationToken, Task>? beforeConnect = null,
        uint protocolVersion = ProtocolInfo.CurrentVersion, Func<WebSocket, WebSocket>? wrap = null, TimeSpan? forkCheckInterval = null, TimeSpan? loginRetryDelay = null,
        Uri? serverUri = null, bool offerCatchUp = true, TimeSpan? catchUpWithoutPosition = null, int maxHeldLive = 2000, TimeSpan? replaySaveDelay = null,
        bool offerLocalChat = true) => new() {
        ServerUri = serverUri ?? new Uri(this.Factory.Server.BaseAddress, ProtocolInfo.WebSocketPath),
        Connect = async (uri, ct) => {
            if (beforeConnect != null) {
                await beforeConnect(ct);
            }

            var socket = await this.ConnectAsync(uri, ct);
            return wrap?.Invoke(socket) ?? socket;
        },
        ReconnectMinDelay = TimeSpan.FromMilliseconds(100),
        LoginRetryMinDelay = loginRetryDelay ?? TimeSpan.FromMilliseconds(100),
        LoginRetryMaxDelay = loginRetryDelay ?? TimeSpan.FromMilliseconds(400),
        AutoRekeyWhenDesignated = autoRekey,
        Log = log,
        TimeProvider = time ?? TimeProvider.System,
        ProtocolVersion = protocolVersion,
        ForkCheckInterval = forkCheckInterval ?? TimeSpan.FromMinutes(1),
        OfferMessageCatchUp = offerCatchUp,
        CatchUpWithoutPosition = catchUpWithoutPosition ?? TimeSpan.FromHours(1),
        MaxHeldLiveMessages = maxHeldLive,
        // Soon, so a failed catch-up is tried again within a test.
        CatchUpRetryDelay = TimeSpan.FromMilliseconds(100),
        ReplayStateSaveDelay = replaySaveDelay ?? TimeSpan.FromSeconds(30),
        OfferLocalChat = offerLocalChat,
    };

    /// <summary>Opens a WebSocket to this server, whatever address <paramref name="uri"/> names (as a client's Connect).</summary>
    public Task<WebSocket> ConnectAsync(Uri uri, CancellationToken ct) => this.Factory.Server.CreateWebSocketClient().ConnectAsync(uri, ct);

    public void Track(IAsyncDisposable disposable) => this._disposables.Add(disposable);

    public TestClient StartClient(string name, ISecretStore? store = null, ClientSessionOptions? options = null) {
        store ??= new InMemorySecretStore();
        var client = new TestClient(name, new ClientSession(options ?? this.Options(), store), store);
        this._disposables.Add(client.Session);
        this._clients.Add(client);
        client.Session.Start();
        return client;
    }

    /// <summary>Starts a second session for an existing client's account, as after a restart. Dispose the old one first.</summary>
    public async Task<TestClient> RestartAsync(TestClient client, ClientSessionOptions? options = null) {
        var restarted = this.StartClient(client.Name, client.Store, options);
        await WaitFor(() => restarted.Session.Snapshot.State == ConnectionState.Ready ? new object() : null);
        // The automatic refresh may still be running; one more makes the state settled for assertions.
        await restarted.Session.RefreshAsync(Ct);
        return restarted;
    }

    /// <summary>Runs SQL directly against the server's database, bypassing every server check.</summary>
    public int ExecuteSql(string sql, params (string Name, object Value)[] parameters) {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = Path.Combine(this.DataDirectory, "lookingglass.db"),
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) {
            command.Parameters.AddWithValue(name, value);
        }

        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// Sends events to a client as the server would, followed by an announcement,
    /// and waits for that announcement: once it arrives, the events have been handled.
    /// (A client handles every event, announcements included, in one queue, in order.
    /// Work an event starts in the background, such as a rekey, may still be running.)
    /// </summary>
    public async Task SendAndSettleAsync(TestClient client, params Event[] events) {
        foreach (var ev in events) {
            this.Registry.Send(client.UserId, ev);
        }

        var sentinel = "sentinel " + Guid.NewGuid().ToString("N");
        this.Registry.Send(client.UserId, new Event { Announcement = new Announcement { Text = sentinel } });
        await WaitFor(() => client.Notices.FirstOrDefault(n => n.Text == sentinel));
    }

    public async Task<TestClient> RegisterAsync(string name, ISecretStore? store = null, ClientSessionOptions? options = null) {
        var client = this.StartClient(name, store, options);
        await WaitFor(() => client.Session.Snapshot.State == ConnectionState.Unregistered ? new object() : null);
        var challenge = await client.Session.StartRegistrationAsync(new Character { Name = name, WorldName = ProtocolInfo.DebugWorldName }, Ct);
        Assert.True(challenge.VerificationSkipped);
        await client.Session.CompleteRegistrationAsync(Ct);
        await WaitFor(() => client.Session.Snapshot.State == ConnectionState.Ready ? new object() : null);
        return client;
    }

    /// <summary>
    /// <see cref="RegisterAsync"/> on a server from before key recovery: an account registering new keys keeps its places in
    /// channels under its old ones (see <see cref="RequestHandler.KeepPlacesOnNewKeysForTests"/>). Places like that are
    /// still about, from then.
    /// </summary>
    public async Task<TestClient> RegisterOnAnOldServerAsync(string name, ISecretStore? store = null, ClientSessionOptions? options = null) {
        this.Handler.KeepPlacesOnNewKeysForTests = true;
        try {
            return await this.RegisterAsync(name, store, options);
        } finally {
            this.Handler.KeepPlacesOnNewKeysForTests = false;
        }
    }

    /// <summary>The channel's membership as the server's database has it, as a misbehaving member would build on.</summary>
    public IChannelMembership ServerMembership(string channelId) {
        return SignedLogMembershipProvider.Instance.Restore(this.Database.GetMembershipCheckpoint(channelId)!);
    }

    /// <summary>The next entry of the server's log, signed by <paramref name="actor"/> (checked like any client would make it).</summary>
    public MembershipEntry NextEntry(string channelId, TestClient actor, MembershipEntryKind kind, long subjectId, MemberKeys? inviteeKeys = null, Rank rank = Rank.Unspecified) {
        using var keys = actor.LoadIdentity();
        return this.ServerMembership(channelId).Create(kind, subjectId, keys, actor.UserId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), inviteeKeys, rank);
    }

    /// <summary>
    /// An entry chained to <paramref name="after"/> (by default the server's log head), signed with
    /// <paramref name="actor"/>'s keys, with none of the membership rules checked: what a dishonest
    /// member (or a server holding their keys) could make.
    /// </summary>
    public MembershipEntry ForgeEntry(string channelId, TestClient actor, MembershipEntryKind kind, long subjectId, MemberKeys subjectKeys,
        LogPosition? invite = null, Rank rank = Rank.Unspecified, LogPosition? after = null) {
        after ??= this.ServerMembership(channelId).Head!;
        using var keys = actor.LoadIdentity();
        var entry = new MembershipEntry {
            ChannelId = channelId,
            Seq = after.Seq + 1,
            PreviousHash = after.Hash,
            Kind = kind,
            ActorId = actor.UserId,
            ActorKeyHash = Google.Protobuf.ByteString.CopyFrom(MemberKeys.Of(keys).Hash),
            Subject = subjectKeys.ToProto(subjectId),
            Rank = rank,
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Invite = invite,
        };
        MembershipEntries.Sign(entry, keys);
        return entry;
    }

    /// <summary>The newest membership log position <paramref name="client"/> has verified for a channel.</summary>
    public static LogPosition PositionOf(TestClient client, string channelId) => client.Session.Snapshot.FindChannel(channelId)!.LogHead!;

    /// <summary>Invites <paramref name="member"/> and waits until they hold the channel key.</summary>
    public static async Task AddMemberAsync(TestClient admin, string channelId, TestClient member) {
        await admin.Session.InviteAsync(channelId, member.Name, ProtocolInfo.DebugWorldName, Ct);
        await WaitFor(() => member.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == channelId && i.ChannelName != null));
        await member.Session.RespondToInviteAsync(channelId, true, Ct);
        await WaitFor(() => member.Session.Snapshot.FindChannel(channelId) is { HasKey: true, RekeyPending: false, Name: not null } c ? c : null);
        await WaitFor(() => admin.Session.Snapshot.FindChannel(channelId) is { RekeyPending: false } c ? c : null);
    }

    public static async Task<T> WaitFor<T>(Func<T?> probe, TimeSpan? timeout = null) where T : class {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(timeout ?? Timeout);
        while (true) {
            if (probe() is { } result) {
                return result;
            }

            try {
                await Task.Delay(25, cts.Token);
            } catch (OperationCanceledException) {
                throw new TimeoutException("Condition not met in time.");
            }
        }
    }

    /// <summary>
    /// Also checks that every notice any client was given, in every test, is shown in simple mode (the default) as well as
    /// advanced mode, in plain words when it is about something technical (see <see cref="PlainLanguage.AssertShownInBothModes"/>), and
    /// so is everything each client's last snapshot shows (see <see cref="PlainLanguage.AssertSnapshotInBothModes"/>).
    /// </summary>
    public async ValueTask DisposeAsync() {
        // As each client last showed itself before stopping.
        var snapshots = this._clients.Select(client => client.Session.Snapshot).ToList();
        foreach (var disposable in this._disposables) {
            await disposable.DisposeAsync();
        }

        await this.Factory.DisposeAsync();
        // What the server logged while it ran. The test server can leave a request it abandoned running after it is disposed,
        // into a folder deleted below: that is no failure of the server's.
        var errors = this._serverLogs.Entries.Where(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Error).ToList();
        Live.TryRemove(new KeyValuePair<string, Harness>(LiveKey(this.DataDirectory), this));
        if (this._ownsDataDirectory || this._deleteOnDispose) {
            // Each run of the suite would otherwise leave hundreds of these behind (TestFolders clears old ones too).
            DeleteDirectory(this.DataDirectory);
        }

        Assert.True(errors.Count == 0, "The server logged errors:\n" + string.Join("\n\n", errors.Select(error => $"[{error.Category}] {error.Message}")));

        var names = this._clients.Select(client => client.Name)
            .Concat(snapshots.SelectMany(snapshot => snapshot.Channels).Select(channel => channel.Name ?? ""))
            .Concat(snapshots.SelectMany(snapshot => snapshot.Invites).Select(invite => invite.ChannelName ?? ""))
            .ToHashSet();
        foreach (var notice in this._clients.SelectMany(client => client.Notices)) {
            PlainLanguage.AssertShownInBothModes(notice, names);
        }

        foreach (var snapshot in snapshots) {
            PlainLanguage.AssertSnapshotInBothModes(snapshot, names);
        }
    }

    /// <summary>
    /// Deletes a test's folder: first closing the pooled SQLite connections to each database in it (only those: see
    /// <see cref="Database.ReleasePooledConnections"/>), then trying for a moment, as a server still closing may hold a file
    /// briefly. Never fails a test over files held open: what is left, TestFolders clears in a later run. Refuses (throws)
    /// anything but a test's folder: an "lgt-" folder directly in the temporary folder (see <see cref="TestFolders.CheckDeletable"/>).
    /// </summary>
    public static void DeleteDirectory(string path) {
        path = TestFolders.CheckDeletable(path);

        // A server still running on it (a test's finally runs before its `await using` server is disposed) would go on with an
        // empty database in its place: it is deleted when that server is disposed instead.
        if (Live.TryGetValue(path, out var running)) {
            running._deleteOnDispose = true;
            if (Live.ContainsKey(path)) {
                return;
            }
        }

        for (var attempt = 0; attempt < 20 && Directory.Exists(path); attempt++) {
            if (attempt > 0) {
                Thread.Sleep(50);
            }

            try {
                foreach (var database in Directory.EnumerateFiles(path, "*.db", SearchOption.AllDirectories)) {
                    Database.ReleasePooledConnections(database);
                }

                Directory.Delete(path, true);
            } catch {
                // Held for a moment more; or gone already.
            }
        }
    }
}

/// <summary>The real clock plus an offset a test can move forward.</summary>
public sealed class ManualClock : TimeProvider {
    public TimeSpan Offset { get; set; }

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + this.Offset;
}

/// <summary>
/// A clock that stands still (at the time it was made) until a test moves it on, for limits that refill with time: what a
/// test counts doesn't then depend on how fast it runs.
/// </summary>
public sealed class StoppedClock : TimeProvider {
    private long _ticks = DateTimeOffset.UtcNow.UtcTicks;

    public void Advance(TimeSpan by) => Interlocked.Add(ref this._ticks, by.Ticks);

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref this._ticks), TimeSpan.Zero);
}

/// <summary>
/// A client's WebSocket that can hold back its next request of one kind on the way to the server, until
/// released: the client is then waiting for an answer, at a point the test knows, while everything else
/// (events from the server included) carries on.
/// </summary>
public sealed class HoldingWebSocket(WebSocket inner) : WebSocket {
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _holdNext = -1;

    /// <summary>Completes once a request is being held.</summary>
    public Task Held => this._held.Task;

    /// <summary>Holds the next request of this kind, until <see cref="Release"/>.</summary>
    public void HoldNext(ClientFrame.BodyOneofCase kind) => Volatile.Write(ref this._holdNext, (int) kind);

    public void Release() => this._release.TrySetResult();

    public override WebSocketCloseStatus? CloseStatus => inner.CloseStatus;
    public override string? CloseStatusDescription => inner.CloseStatusDescription;
    public override WebSocketState State => inner.State;
    public override string? SubProtocol => inner.SubProtocol;

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) {
        var kind = Volatile.Read(ref this._holdNext);
        if (kind >= 0 && endOfMessage && (int) ClientFrame.Parser.ParseFrom(buffer.AsSpan()).BodyCase == kind
            && Interlocked.CompareExchange(ref this._holdNext, -1, kind) == kind) {
            this._held.TrySetResult();
            await this._release.Task.WaitAsync(cancellationToken);
        }

        await inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
    }

    public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) {
        return new ValueTask(this.SendAsync(new ArraySegment<byte>(buffer.ToArray()), messageType, endOfMessage, cancellationToken));
    }

    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => inner.ReceiveAsync(buffer, cancellationToken);

    public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) => inner.ReceiveAsync(buffer, cancellationToken);

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => inner.CloseAsync(closeStatus, statusDescription, cancellationToken);

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

    public override void Abort() {
        this._release.TrySetResult();
        inner.Abort();
    }

    public override void Dispose() {
        this._release.TrySetResult();
        inner.Dispose();
    }
}

/// <summary>
/// A client's WebSocket on which a test plays the server the client thinks it reaches: every frame the real server sends
/// goes through <c>rewrite</c> on its way to the client, as a malicious server (or one passing on another's answers)
/// would change it. Requests go to the real server unchanged, and to <c>sent</c> (if given) first, for a test to look at.
/// </summary>
public sealed class RewritingWebSocket(WebSocket inner, Func<ServerFrame, ServerFrame> rewrite, Action<ClientFrame>? sent = null) : WebSocket {
    private readonly byte[] _chunk = new byte[16 * 1024];
    private readonly MemoryStream _message = new();
    // The rewritten frame being handed to the client, and how much of it has been.
    private byte[]? _pending;
    private int _offset;

    public override WebSocketCloseStatus? CloseStatus => inner.CloseStatus;
    public override string? CloseStatusDescription => inner.CloseStatusDescription;
    public override WebSocketState State => inner.State;
    public override string? SubProtocol => inner.SubProtocol;

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) {
        if (this._pending == null) {
            this._message.SetLength(0);
            while (true) {
                var result = await inner.ReceiveAsync(new ArraySegment<byte>(this._chunk), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) {
                    return result;
                }

                this._message.Write(this._chunk, 0, result.Count);
                if (result.EndOfMessage) {
                    break;
                }
            }

            this._pending = rewrite(ServerFrame.Parser.ParseFrom(this._message.ToArray())).ToByteArray();
            this._offset = 0;
        }

        var count = Math.Min(buffer.Count, this._pending.Length - this._offset);
        Array.Copy(this._pending, this._offset, buffer.Array!, buffer.Offset, count);
        this._offset += count;
        var end = this._offset >= this._pending.Length;
        if (end) {
            this._pending = null;
        }

        return new WebSocketReceiveResult(count, WebSocketMessageType.Binary, end);
    }

    public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) {
        var array = new byte[buffer.Length];
        var result = await this.ReceiveAsync(new ArraySegment<byte>(array), cancellationToken);
        array.AsMemory(0, result.Count).CopyTo(buffer);
        return new ValueWebSocketReceiveResult(result.Count, result.MessageType, result.EndOfMessage);
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) {
        // The client sends each request as one message.
        sent?.Invoke(ClientFrame.Parser.ParseFrom(buffer.AsSpan()));
        return inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
    }

    public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) {
        sent?.Invoke(ClientFrame.Parser.ParseFrom(buffer.Span));
        return inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => inner.CloseAsync(closeStatus, statusDescription, cancellationToken);

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

    public override void Abort() => inner.Abort();

    public override void Dispose() => inner.Dispose();
}

/// <summary>
/// The Lodestone as a server sees it: every search finds <see cref="Name"/> on <see cref="World"/> with ID
/// <see cref="CharacterId"/>, and every profile's text is <see cref="Profile"/> (what the user put there).
/// </summary>
public sealed class FakeLodestone : HttpMessageHandler {
    public const string BaseUrl = "https://lodestone.test";

    private int _profileReads;

    public string Name { get; init; } = "Test Person";
    public string World { get; init; } = "Gilgamesh";
    public long CharacterId { get; init; } = 31337;

    /// <summary>The profile's text (by default without any code).</summary>
    public string Profile { get; set; } = "Nothing to see here.";

    /// <summary>Profiles the server has read, to check registration codes.</summary>
    public int ProfileReads => Volatile.Read(ref this._profileReads);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        string html;
        if (request.RequestUri!.AbsolutePath.TrimEnd('/') == "/lodestone/character") {
            html = $"""<a href="/lodestone/character/{this.CharacterId}/" class="entry__link"><p class="entry__name">{this.Name}</p><p class="entry__world">{this.World} [Aether]</p></a>""";
        } else {
            Interlocked.Increment(ref this._profileReads);
            html = $"""<div class="character__selfintroduction">{System.Net.WebUtility.HtmlEncode(this.Profile)}</div>""";
        }

        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(html) });
    }
}

/// <summary>
/// A WebSocket to the server driven request by request, with no client logic in between. Events are kept (in
/// <see cref="Events"/>) as they arrive while waiting for a response: send a Ping to collect those sent before it.
/// </summary>
public sealed class RawConnection(WebSocket socket) : IAsyncDisposable {
    private readonly ConcurrentQueue<Event> _events = new();
    private uint _nextRequestId;

    /// <summary>Every event received so far, oldest first.</summary>
    public IReadOnlyCollection<Event> Events => this._events.ToArray();

    public async Task<Response> SendAsync(ClientFrame frame) {
        frame.RequestId = ++this._nextRequestId;
        await socket.SendAsync(frame.ToByteArray(), WebSocketMessageType.Binary, true, Harness.Ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Harness.Ct);
        timeout.CancelAfter(Harness.Timeout);
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true) {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close) {
                throw new InvalidOperationException($"The server closed the connection: {result.CloseStatusDescription}");
            }

            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) {
                continue;
            }

            var received = ServerFrame.Parser.ParseFrom(message.ToArray());
            message.SetLength(0);
            if (received.Event is { } ev) {
                this._events.Enqueue(ev);
            }

            if (received.Response is { } response && response.RequestId == frame.RequestId) {
                return response;
            }
        }
    }

    public ValueTask DisposeAsync() {
        socket.Abort();
        socket.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Counts saves, to check what is written per message.</summary>
public sealed class CountingSecretStore : ISecretStore {
    private readonly InMemorySecretStore _inner = new();
    private int _saves;

    public int Saves => Volatile.Read(ref this._saves);

    public ClientSecrets Load() => this._inner.Load();

    public void Save(ClientSecrets secrets) {
        Interlocked.Increment(ref this._saves);
        this._inner.Save(secrets);
    }
}

public sealed class TestClient {
    private readonly ConcurrentQueue<IncomingMessage> _messages = new();
    private readonly ConcurrentQueue<SessionNotice> _notices = new();

    private readonly ConcurrentQueue<CaughtUpMessages> _caughtUp = new();
    private readonly ConcurrentQueue<IncomingLocalMessage> _localMessages = new();
    private readonly ConcurrentQueue<LocalUnchecked> _localUnchecked = new();

    public TestClient(string name, ClientSession session, ISecretStore store) {
        this.Name = name;
        this.Session = session;
        this.Store = store;
        session.MessageReceived += this._messages.Enqueue;
        session.LocalMessageReceived += this._localMessages.Enqueue;
        session.LocalMessageUnchecked += this._localUnchecked.Enqueue;
        session.Notice += this._notices.Enqueue;
        // Caught-up messages are in Messages too (flagged CaughtUp), in the order they were raised.
        session.MessagesCaughtUp += batch => {
            this._caughtUp.Enqueue(batch);
            foreach (var message in batch.Messages) {
                this._messages.Enqueue(message);
            }
        };
    }

    /// <summary>Every batch of messages caught up from while this client was disconnected, oldest first.</summary>
    public IReadOnlyCollection<CaughtUpMessages> CaughtUp => this._caughtUp.ToArray();

    public string Name { get; }
    public ClientSession Session { get; }
    public ISecretStore Store { get; }
    public long UserId => this.Session.Snapshot.Me!.UserId;
    public IReadOnlyCollection<IncomingMessage> Messages => this._messages.ToArray();
    public IReadOnlyCollection<SessionNotice> Notices => this._notices.ToArray();

    /// <summary>Local chat messages the session passed on (its own included, flagged), in the order it raised them.</summary>
    public IReadOnlyCollection<IncomingLocalMessage> LocalMessages => this._localMessages.ToArray();

    /// <summary>Local messages the session couldn't check (and didn't open), passed on for the plugin to hint at.</summary>
    public IReadOnlyCollection<LocalUnchecked> LocalUnchecked => this._localUnchecked.ToArray();

    /// <summary>This client's private identity keys, read back from its secret store (as an attacker with the keys would have).</summary>
    public IdentityKeys LoadIdentity() {
        var secrets = this.Store.Load();
        return IdentityKeys.Import(secrets.SigningPrivateKey!, secrets.AgreementPrivateKey!);
    }

    public byte[] LoadEpochKey(string channelId, ulong epoch) => this.Store.Load().EpochKeys[channelId][epoch];

    public MemberKeys Keys() {
        using var keys = this.LoadIdentity();
        return MemberKeys.Of(keys);
    }

    /// <summary>A message signed and encrypted with this client's keys, as the server would deliver it.</summary>
    public ChatMessage ForgeMessage(string channelId, ulong epoch, string text, DateTimeOffset when) {
        using var keys = this.LoadIdentity();
        var sent = ChannelCrypto.EncryptMessage(new Content { Text = new TextContent { Text = text } }, this.LoadEpochKey(channelId, epoch), channelId, epoch, keys, this.UserId, when.ToUnixTimeMilliseconds());
        return new ChatMessage {
            ChannelId = channelId, Epoch = epoch, SenderId = this.UserId, MessageId = sent.MessageId,
            TimestampUnixMs = sent.TimestampUnixMs, Ciphertext = sent.Ciphertext, Signature = sent.Signature,
        };
    }
}

/// <summary>Keeps everything logged through it, for tests of what the server tells its operator.</summary>
public sealed class CapturingLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider {
    private readonly ConcurrentQueue<(Microsoft.Extensions.Logging.LogLevel Level, string Category, string Message)> _entries = new();

    public IReadOnlyCollection<(Microsoft.Extensions.Logging.LogLevel Level, string Category, string Message)> Entries => this._entries.ToArray();

    /// <summary>The messages logged at <paramref name="level"/> or above.</summary>
    public IReadOnlyList<string> AtLeast(Microsoft.Extensions.Logging.LogLevel level) => this.Entries.Where(e => e.Level >= level).Select(e => e.Message).ToList();

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose() {
    }

    private sealed class Logger(CapturingLoggerProvider provider, string category) : Microsoft.Extensions.Logging.ILogger {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) {
            // With the exception, if any, so a test reporting what was logged shows where it came from.
            var message = formatter(state, exception);
            provider._entries.Enqueue((logLevel, category, exception == null ? message : $"{message}\n{exception}"));
        }
    }
}
