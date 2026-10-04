using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using WonderlandChat.Core.Client;
using WonderlandChat.Core.Crypto;
using WonderlandChat.Protocol;
using WonderlandChat.Server.Data;
using WonderlandChat.Server.Realtime;

namespace WonderlandChat.Tests;

/// <summary>An in-process server plus helpers to create registered clients.</summary>
public sealed class Harness : IAsyncDisposable {
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly List<IAsyncDisposable> _disposables = [];

    /// <param name="settings">Extra server configuration, for example <c>("WonderlandChat:Limits:MaxIdentitiesPerRequest", "2")</c>.</param>
    public Harness(string? dataDirectory = null, bool allowDebugAccounts = true, params (string Key, string Value)[] settings) {
        this.DataDirectory = dataDirectory ?? Path.Combine(Path.GetTempPath(), "wct-" + Guid.NewGuid().ToString("N"));
        this.Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => {
            builder.UseSetting("WonderlandChat:DataDirectory", this.DataDirectory);
            builder.UseSetting("WonderlandChat:Dev:AllowDebugAccounts", allowDebugAccounts ? "true" : "false");
            builder.UseSetting("WonderlandChat:Dev:HostEchoBot", "false");
            foreach (var (key, value) in settings) {
                builder.UseSetting(key, value);
            }
        });
        _ = this.Factory.Server;
    }

    public string DataDirectory { get; }
    public WebApplicationFactory<Program> Factory { get; }

    /// <summary>The server's connection registry: lets a test act as a malicious server and push arbitrary events.</summary>
    public ConnectionRegistry Registry => this.Factory.Services.GetRequiredService<ConnectionRegistry>();

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The server's database, for tests that play a malicious or misbehaving server.</summary>
    public Database Database => this.Factory.Services.GetRequiredService<Database>();

    /// <param name="beforeConnect">Awaited before every connection attempt, so a test can keep a client offline.</param>
    public ClientSessionOptions Options(bool autoRekey = true, Action<NoticeLevel, string>? log = null, TimeProvider? time = null, Func<CancellationToken, Task>? beforeConnect = null) => new() {
        ServerUri = new Uri(this.Factory.Server.BaseAddress, ProtocolInfo.WebSocketPath),
        Connect = async (uri, ct) => {
            if (beforeConnect != null) {
                await beforeConnect(ct);
            }

            return await this.Factory.Server.CreateWebSocketClient().ConnectAsync(uri, ct);
        },
        ReconnectMinDelay = TimeSpan.FromMilliseconds(100),
        AutoRekeyWhenDesignated = autoRekey,
        Log = log,
        TimeProvider = time ?? TimeProvider.System,
    };

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
            DataSource = Path.Combine(this.DataDirectory, "wonderlandchat.db"),
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
    /// Only for events handled as they arrive; EpochAdvanced and ChatMessage are
    /// queued, so wait for their outcome instead.
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
