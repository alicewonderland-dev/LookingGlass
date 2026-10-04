using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WonderlandChat.Core.Client;
using WonderlandChat.Core.Crypto;
using WonderlandChat.Protocol;
using WonderlandChat.Server.Realtime;

namespace WonderlandChat.Tests;

/// <summary>An in-process server plus helpers to create registered clients.</summary>
public sealed class Harness : IAsyncDisposable {
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly List<IAsyncDisposable> _disposables = [];

    public Harness(string? dataDirectory = null, bool allowDebugAccounts = true) {
        this.DataDirectory = dataDirectory ?? Path.Combine(Path.GetTempPath(), "wct-" + Guid.NewGuid().ToString("N"));
        this.Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => {
            builder.UseSetting("WonderlandChat:DataDirectory", this.DataDirectory);
            builder.UseSetting("WonderlandChat:Dev:AllowDebugAccounts", allowDebugAccounts ? "true" : "false");
            builder.UseSetting("WonderlandChat:Dev:HostEchoBot", "false");
        });
        _ = this.Factory.Server;
    }

    public string DataDirectory { get; }
    public WebApplicationFactory<Program> Factory { get; }

    /// <summary>The server's connection registry: lets a test act as a malicious server and push arbitrary events.</summary>
    public ConnectionRegistry Registry => this.Factory.Services.GetRequiredService<ConnectionRegistry>();

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ClientSessionOptions Options() => new() {
        ServerUri = new Uri(this.Factory.Server.BaseAddress, ProtocolInfo.WebSocketPath),
        Connect = (uri, ct) => this.Factory.Server.CreateWebSocketClient().ConnectAsync(uri, ct),
        ReconnectMinDelay = TimeSpan.FromMilliseconds(100),
    };

    public void Track(IAsyncDisposable disposable) => this._disposables.Add(disposable);

    public TestClient StartClient(string name, ISecretStore? store = null) {
        store ??= new InMemorySecretStore();
        var client = new TestClient(name, new ClientSession(this.Options(), store), store);
        this._disposables.Add(client.Session);
        client.Session.Start();
        return client;
    }

    public async Task<TestClient> RegisterAsync(string name, ISecretStore? store = null) {
        var client = this.StartClient(name, store);
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
}
