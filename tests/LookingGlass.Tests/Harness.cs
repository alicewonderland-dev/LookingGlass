using System.Collections.Concurrent;
using System.Net.WebSockets;
using Google.Protobuf;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
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

    /// <param name="serverTime">The server's clock, for tests that move it forward (key login challenges expire by it).</param>
    /// <param name="environment">
    /// The server's hosting environment. In Development (the default here, as on the test server) key login may go by
    /// the connection's Host header when no PublicUrls are set; anywhere else it needs them.
    /// </param>
    /// <param name="settings">Extra server configuration, for example <c>("LookingGlass:Limits:MaxIdentitiesPerRequest", "2")</c>.</param>
    public Harness(string? dataDirectory = null, bool allowDebugAccounts = true, TimeProvider? serverTime = null, string environment = "Development",
        params (string Key, string Value)[] settings) {
        this.DataDirectory = dataDirectory ?? Path.Combine(Path.GetTempPath(), "lgt-" + Guid.NewGuid().ToString("N"));
        this.Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => {
            builder.UseEnvironment(environment);
            builder.UseSetting("LookingGlass:DataDirectory", this.DataDirectory);
            builder.UseSetting("LookingGlass:Dev:AllowDebugAccounts", allowDebugAccounts ? "true" : "false");
            builder.UseSetting("LookingGlass:Dev:HostEchoBot", "false");
            foreach (var (key, value) in settings) {
                builder.UseSetting(key, value);
            }

            if (serverTime != null) {
                builder.ConfigureTestServices(services => services.AddSingleton(serverTime));
            }
        });
        _ = this.Factory.Server;
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

    public string DataDirectory { get; }
    public WebApplicationFactory<Program> Factory { get; }

    /// <summary>The server's connection registry: lets a test act as a malicious server and push arbitrary events.</summary>
    public ConnectionRegistry Registry => this.Factory.Services.GetRequiredService<ConnectionRegistry>();

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

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
    public ClientSessionOptions Options(bool autoRekey = true, Action<NoticeLevel, string>? log = null, TimeProvider? time = null, Func<CancellationToken, Task>? beforeConnect = null,
        uint protocolVersion = ProtocolInfo.CurrentVersion, Func<WebSocket, WebSocket>? wrap = null, TimeSpan? forkCheckInterval = null, TimeSpan? loginRetryDelay = null,
        Uri? serverUri = null) => new() {
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
    };

    /// <summary>Opens a WebSocket to this server, whatever address <paramref name="uri"/> names (as a client's Connect).</summary>
    public Task<WebSocket> ConnectAsync(Uri uri, CancellationToken ct) => this.Factory.Server.CreateWebSocketClient().ConnectAsync(uri, ct);

    public void Track(IAsyncDisposable disposable) => this._disposables.Add(disposable);

    public TestClient StartClient(string name, ISecretStore? store = null, ClientSessionOptions? options = null) {
        store ??= new InMemorySecretStore();
        var client = new TestClient(name, new ClientSession(options ?? this.Options(), store), store);
        this._disposables.Add(client.Session);
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

    public async ValueTask DisposeAsync() {
        foreach (var disposable in this._disposables) {
            await disposable.DisposeAsync();
        }

        await this.Factory.DisposeAsync();
    }

    public static void DeleteDirectory(string path) {
        try {
            Directory.Delete(path, true);
        } catch {
            // SQLite may still hold the file briefly.
        }
    }
}

/// <summary>The real clock plus an offset a test can move forward.</summary>
public sealed class ManualClock : TimeProvider {
    public TimeSpan Offset { get; set; }

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + this.Offset;
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

/// <summary>A WebSocket to the server driven request by request, with no client logic in between. Events are skipped.</summary>
public sealed class RawConnection(WebSocket socket) : IAsyncDisposable {
    private uint _nextRequestId;

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

    public TestClient(string name, ClientSession session, ISecretStore store) {
        this.Name = name;
        this.Session = session;
        this.Store = store;
        session.MessageReceived += this._messages.Enqueue;
        session.Notice += this._notices.Enqueue;
    }

    public string Name { get; }
    public ClientSession Session { get; }
    public ISecretStore Store { get; }
    public long UserId => this.Session.Snapshot.Me!.UserId;
    public IReadOnlyCollection<IncomingMessage> Messages => this._messages.ToArray();
    public IReadOnlyCollection<SessionNotice> Notices => this._notices.ToArray();

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
            provider._entries.Enqueue((logLevel, category, formatter(state, exception)));
        }
    }
}
