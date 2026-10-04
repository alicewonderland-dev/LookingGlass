using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using WonderlandChat.Core.Client;
using WonderlandChat.Core.Debug;
using WonderlandChat.Protocol;

namespace WonderlandChat.Tests;

/// <summary>
/// Full flows against an in-process server with debug accounts enabled:
/// registration, invites, automatic rekeying, messaging, kicks and the echo bot.
/// </summary>
public sealed class EndToEndTests : IAsyncLifetime {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "wct-" + Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _disposables = [];
    private WebApplicationFactory<Program> _factory = null!;

    public ValueTask InitializeAsync() {
        this._factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => {
            builder.UseSetting("WonderlandChat:DataDirectory", this._dataDirectory);
            builder.UseSetting("WonderlandChat:Dev:AllowDebugAccounts", "true");
            builder.UseSetting("WonderlandChat:Dev:HostEchoBot", "false");
        });
        _ = this._factory.Server;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() {
        foreach (var disposable in this._disposables) {
            await disposable.DisposeAsync();
        }

        await this._factory.DisposeAsync();
        try {
            Directory.Delete(this._dataDirectory, true);
        } catch {
            // SQLite may still hold the file briefly.
        }
    }

    [Fact]
    public async Task InviteJoinRekeyAndChat() {
        var alice = await this.RegisterAsync("Alice Test");
        var bob = await this.RegisterAsync("Bob Test");

        var channelId = await alice.Session.CreateChannelAsync("Tea Party", Ct);
        await alice.Session.InviteAsync(channelId, "Bob Test", ProtocolInfo.DebugWorldName, Ct);

        var invite = await WaitFor(() => bob.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == channelId && i.ChannelName != null));
        Assert.Equal("Tea Party", invite.ChannelName);
        Assert.True(invite.Verified);

        await bob.Session.RespondToInviteAsync(channelId, true, Ct);

        // Alice is designated to rekey for the new member; Bob receives the new epoch key.
        var bobChannel = await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { HasKey: true, RekeyPending: false } c ? c : null);
        Assert.Equal(1UL, bobChannel.Epoch);
        Assert.Equal("Tea Party", bobChannel.Name);

        await alice.Session.SendTextAsync(channelId, "hello bob", Ct);
        var atBob = await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == "hello bob"));
        Assert.Equal("Alice Test", atBob.Sender.Name);
        Assert.False(atBob.IsOwn);

        await bob.Session.SendTextAsync(channelId, "hi alice", Ct);
        await WaitFor(() => alice.Messages.FirstOrDefault(m => m.Text == "hi alice"));

        // Senders see their own message once, flagged as theirs.
        Assert.Single(alice.Messages, m => m.Text == "hello bob" && m.IsOwn);
    }

    [Fact]
    public async Task KickedMemberIsCutOffFromNewEpochs() {
        var alice = await this.RegisterAsync("Alice Kick");
        var bob = await this.RegisterAsync("Bob Kick");
        var carol = await this.RegisterAsync("Carol Kick");

        var channelId = await alice.Session.CreateChannelAsync("Book Club", Ct);
        foreach (var member in new[] { bob, carol }) {
            await alice.Session.InviteAsync(channelId, member.Name, ProtocolInfo.DebugWorldName, Ct);
            await WaitFor(() => member.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == channelId && i.ChannelName != null));
            await member.Session.RespondToInviteAsync(channelId, true, Ct);
            await WaitFor(() => member.Session.Snapshot.FindChannel(channelId) is { HasKey: true, RekeyPending: false } c ? c : null);
        }

        var epochBeforeKick = alice.Session.Snapshot.FindChannel(channelId)!.Epoch;
        var carolUserId = carol.Session.Snapshot.Me!.UserId;
        await alice.Session.KickAsync(channelId, carolUserId, Ct);

        await WaitFor(() => carol.Session.Snapshot.FindChannel(channelId) == null ? new object() : null);
        var bobChannel = await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { Epoch: var e, HasKey: true } c && e > epochBeforeKick ? c : null);
        Assert.DoesNotContain(bobChannel.Members, m => m.User.UserId == carolUserId);

        await alice.Session.SendTextAsync(channelId, "after the kick", Ct);
        await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == "after the kick"));
        await Task.Delay(300, Ct);
        Assert.DoesNotContain(carol.Messages, m => m.Text == "after the kick");
    }

    [Fact]
    public async Task MemberCannotKickModerator() {
        var alice = await this.RegisterAsync("Alice Rank");
        var bob = await this.RegisterAsync("Bob Rank");
        var channelId = await alice.Session.CreateChannelAsync("Ranks", Ct);
        await alice.Session.InviteAsync(channelId, bob.Name, ProtocolInfo.DebugWorldName, Ct);
        await WaitFor(() => bob.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == channelId));
        await bob.Session.RespondToInviteAsync(channelId, true, Ct);

        var error = await Assert.ThrowsAsync<ServerErrorException>(() => bob.Session.KickAsync(channelId, alice.Session.Snapshot.Me!.UserId, Ct));
        Assert.Equal(ErrorCode.Forbidden, error.Code);
    }

    [Fact]
    public async Task EchoBotAnswers() {
        var alice = await this.RegisterAsync("Alice Echo");

        var bot = new EchoBot(this.Options(), new InMemorySecretStore(), "Echo Test Bot");
        this._disposables.Add(bot);
        bot.Start();
        await bot.WaitUntilReadyAsync(Timeout);

        var channelId = await alice.Session.CreateChannelAsync("Echo Chamber", Ct);
        await alice.Session.InviteAsync(channelId, "Echo Test Bot", ProtocolInfo.DebugWorldName, Ct);
        await WaitFor(() => alice.Session.Snapshot.FindChannel(channelId)?.Members.FirstOrDefault(m => m.User.Name == "Echo Test Bot" && m.Rank == Rank.Member));
        await WaitFor(() => alice.Session.Snapshot.FindChannel(channelId) is { RekeyPending: false } c ? c : null);

        await alice.Session.SendTextAsync(channelId, "is anyone there?", Ct);
        var reply = await WaitFor(() => alice.Messages.FirstOrDefault(m => m.Text == "echo: is anyone there?"));
        Assert.Equal("Echo Test Bot", reply.Sender.Name);
    }

    [Fact]
    public async Task RestartedClientKeepsIdentityAndKeys() {
        var store = new InMemorySecretStore();
        var first = await this.RegisterAsync("Dana Restart", store);
        var channelId = await first.Session.CreateChannelAsync("Persistent", Ct);
        var fingerprint = first.Session.Snapshot.MyFingerprint;
        await first.Session.DisposeAsync();

        var second = this.StartClient("Dana Restart", store);
        var channel = await WaitFor(() => second.Session.Snapshot.FindChannel(channelId) is { HasKey: true, Name: not null } c ? c : null);
        Assert.Equal("Persistent", channel.Name);
        Assert.Equal(fingerprint, second.Session.Snapshot.MyFingerprint);
    }

    // ================================================================ helpers

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ClientSessionOptions Options() => new() {
        ServerUri = new Uri(this._factory.Server.BaseAddress, ProtocolInfo.WebSocketPath),
        Connect = (uri, ct) => this._factory.Server.CreateWebSocketClient().ConnectAsync(uri, ct),
        ReconnectMinDelay = TimeSpan.FromMilliseconds(100),
    };

    private TestClient StartClient(string name, ISecretStore? store = null) {
        var client = new TestClient(name, new ClientSession(this.Options(), store ?? new InMemorySecretStore()));
        this._disposables.Add(client.Session);
        client.Session.Start();
        return client;
    }

    private async Task<TestClient> RegisterAsync(string name, ISecretStore? store = null) {
        var client = this.StartClient(name, store);
        await WaitFor(() => client.Session.Snapshot.State == ConnectionState.Unregistered ? new object() : null);
        var challenge = await client.Session.StartRegistrationAsync(new Character { Name = name, WorldName = ProtocolInfo.DebugWorldName }, Ct);
        Assert.True(challenge.VerificationSkipped);
        await client.Session.CompleteRegistrationAsync(Ct);
        await WaitFor(() => client.Session.Snapshot.State == ConnectionState.Ready ? new object() : null);
        return client;
    }

    private static async Task<T> WaitFor<T>(Func<T?> probe) where T : class {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(Timeout);
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

    private sealed class TestClient {
        private readonly ConcurrentQueue<IncomingMessage> _messages = new();

        public TestClient(string name, ClientSession session) {
            this.Name = name;
            this.Session = session;
            session.MessageReceived += this._messages.Enqueue;
        }

        public string Name { get; }
        public ClientSession Session { get; }
        public IReadOnlyCollection<IncomingMessage> Messages => this._messages.ToArray();
    }
}
