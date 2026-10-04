using System.Net;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using LookingGlass.Core.Crypto;
using LookingGlass.Core.Membership;
using LookingGlass.Protocol;
using LookingGlass.Server;
using LookingGlass.Server.Data;
using LookingGlass.Server.Hosting;
using LookingGlass.Server.Realtime;
using LookingGlass.Server.Services;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// Calls <see cref="RequestHandler"/> directly, with a stub Lodestone, for the
/// registration and login paths an in-process client can't reach (real worlds).
/// </summary>
public sealed class RequestHandlerTests : IDisposable {
    private const string World = "Gilgamesh";
    private const long LodestoneId = 31337;

    // The address the clients here sign for: any will do on a server with no PublicUrls outside Development.
    private const string Url = "ws://localhost/ws";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lgt-handler-" + Guid.NewGuid().ToString("N"));
    private readonly Database _db;
    private readonly ConnectionRegistry _registry;
    private readonly StubLodestone _lodestone = new();
    private RequestHandler _handler;

    public RequestHandlerTests() {
        Directory.CreateDirectory(this._directory);
        this._db = new Database(Path.Combine(this._directory, "test.db"));
        this._registry = new ConnectionRegistry(this._db, NullLogger<ConnectionRegistry>.Instance);
        this._handler = this.NewHandler();
    }

    private RequestHandler NewHandler(params string[] publicUrls) {
        var options = Options.Create(new ServerOptions {
            Dev = { AllowDebugAccounts = true },
            Lodestone = { BaseUrl = "https://lodestone.test", MinDelaySeconds = 0 },
            Limits = { RegistrationsPerHourPerIp = 3 },
            PublicUrls = publicUrls,
        });
        var lodestone = new LodestoneClient(new HttpClient(this._lodestone), options, NullLogger<LodestoneClient>.Instance);
        return new RequestHandler(this._db, this._registry, lodestone, options, NullLogger<RequestHandler>.Instance,
            SignedLogMembershipProvider.Instance, SealedEpochKeyProvider.Instance);
    }

    public void Dispose() => DeleteDirectory(this._directory);

    [Fact]
    public async Task RegistrationsArePerAddressAndIpv6CountsPerSlash64() {
        // Three registrations an hour from one address; both of these are in the same /64.
        var first = ClientAddresses.LimitKey(IPAddress.Parse("2001:db8:1:2::10"));
        var second = ClientAddresses.LimitKey(IPAddress.Parse("2001:db8:1:2:ffff::20"));
        for (var i = 0; i < 3; i++) {
            Assert.NotNull((await this.StartRegistrationAsync(await this.HelloAsync(i % 2 == 0 ? first : second))).RegistrationChallenge);
        }

        var refused = await this.StartRegistrationAsync(await this.HelloAsync(second));
        Assert.Equal(ErrorCode.RateLimited, refused.Error?.Code);

        // Another /64 has its own allowance.
        var elsewhere = await this.StartRegistrationAsync(await this.HelloAsync(ClientAddresses.LimitKey(IPAddress.Parse("2001:db8:1:3::10"))));
        Assert.NotNull(elsewhere.RegistrationChallenge);
    }

    [Fact]
    public async Task VerifyingHasACooldown() {
        var connection = await this.HelloAsync("203.0.113.10");
        using var keys = IdentityKeys.Generate();
        var challenge = (await this.StartRegistrationAsync(connection, keys: keys)).RegistrationChallenge!;

        // The stub profile doesn't contain the code, so the first attempt fails normally...
        var first = await this.CompleteRegistrationAsync(connection, keys, challenge);
        Assert.Equal(ErrorCode.RegistrationFailed, first.Error?.Code);
        Assert.Contains("isn't in your Lodestone profile", first.Error!.Message);

        // ...and an immediate retry is refused without asking the Lodestone again.
        var second = await this.CompleteRegistrationAsync(connection, keys, challenge);
        Assert.Equal(ErrorCode.RateLimited, second.Error?.Code);
        Assert.Contains("Wait", second.Error!.Message);
        Assert.Equal(1, connection.VerifyAttempts);
    }

    /// <summary>
    /// A registration through the Lodestone is signed for the address the client connected to, which is checked against
    /// the server's own addresses as for key login: a malicious server the user registers with can't relay this server's
    /// challenge (code and nonce) to the user and register the user's key here. Checked before asking the Lodestone.
    /// </summary>
    [Fact]
    public async Task ARegistrationSignedForAnotherServerIsRefused() {
        this._handler = this.NewHandler("wss://chat.example.com/ws");
        var connection = await this.HelloAsync("203.0.113.60");
        using var keys = IdentityKeys.Generate();
        var challenge = (await this.StartRegistrationAsync(connection, keys: keys)).RegistrationChallenge!;
        this._lodestone.Profile = $"My code: {challenge.Code}";

        foreach (var url in new[] { "wss://evil.example/ws", "ws://localhost/ws", "wss://chat.example.com.evil.example/ws" }) {
            var refused = await this.CompleteRegistrationAsync(connection, keys, challenge, url);
            Assert.Equal(ErrorCode.RegistrationFailed, refused.Error?.Code);
        }

        Assert.Equal(0, connection.VerifyAttempts);
        Assert.Null(this._db.GetUser(LodestoneId));

        var registered = await this.CompleteRegistrationAsync(connection, keys, challenge, "wss://chat.example.com/ws");
        Assert.NotNull(registered.RegistrationComplete);
        Assert.Equal(keys.SigningPublicKey, this._db.GetUser(LodestoneId)!.SigningKey);
    }

    /// <summary>A registration through the Lodestone is signed by the key being registered, like a debug one.</summary>
    [Fact]
    public async Task ALodestoneRegistrationNeedsASignatureByTheKey() {
        var connection = await this.HelloAsync("203.0.113.61");
        using var keys = IdentityKeys.Generate();
        using var otherKeys = IdentityKeys.Generate();
        var challenge = (await this.StartRegistrationAsync(connection, keys: keys)).RegistrationChallenge!;
        this._lodestone.Profile = $"My code: {challenge.Code}";

        Assert.Equal(ErrorCode.RegistrationFailed, (await this.SendAsync(connection, new ClientFrame { CompleteRegistration = new CompleteRegistration() })).Error?.Code);
        Assert.Equal(ErrorCode.RegistrationFailed, (await this.CompleteRegistrationAsync(connection, otherKeys, challenge)).Error?.Code);
        Assert.Equal(0, connection.VerifyAttempts);
        Assert.Null(this._db.GetUser(LodestoneId));

        Assert.NotNull((await this.CompleteRegistrationAsync(connection, keys, challenge)).RegistrationComplete);
    }

    [Fact]
    public async Task SecondAuthenticateOnAConnectionIsRejected() {
        var firstToken = await this.RegisterDebugAsync("Reauth One");
        var secondToken = await this.RegisterDebugAsync("Reauth Two");
        var firstId = RequestHandler.DebugUserId("Reauth One");

        var connection = await this.HelloAsync("203.0.113.20");
        Assert.NotNull((await this.SendAsync(connection, new ClientFrame { Authenticate = new Authenticate { DeviceToken = firstToken } })).AuthenticateOk);

        var again = await this.SendAsync(connection, new ClientFrame { Authenticate = new Authenticate { DeviceToken = secondToken } });
        Assert.Equal(ErrorCode.InvalidRequest, again.Error?.Code);
        Assert.Equal(firstId, connection.User!.UserId);
        Assert.True(this._registry.IsOnline(firstId));
        Assert.False(this._registry.IsOnline(RequestHandler.DebugUserId("Reauth Two")));
    }

    [Fact]
    public async Task RegistrationWithAnAllZeroAgreementKeyIsRejected() {
        // Validly bound to the signing key, but nothing can be sealed to it, so it would block every rekey.
        using var keys = IdentityKeys.Generate();
        var connection = await this.HelloAsync("203.0.113.40");
        var response = await this.SendAsync(connection, new ClientFrame {
            StartRegistration = new StartRegistration {
                Character = new Character { Name = "Zero Key", WorldName = ProtocolInfo.DebugWorldName },
                Identity = CryptoTests.BundleWithAgreementKey(keys, new byte[32]),
            },
        });

        Assert.Equal(ErrorCode.InvalidRequest, response.Error?.Code);
        Assert.Null(connection.PendingRegistration);
        Assert.Null(this._db.GetUser(RequestHandler.DebugUserId("Zero Key")));
    }

    /// <summary>
    /// v0.2 changed the wire protocol (log positions in signatures, entries on membership requests), so a
    /// 0.1 plugin, which only offers protocol version 1, must be turned away at Hello with a clear message,
    /// not let in to fail confusingly later.
    /// </summary>
    [Fact]
    public async Task HelloOfferingOnlyProtocolVersion1IsAskedToUpdate() {
        var connection = new ClientConnection(new ClosedWebSocket(), "203.0.113.50", 128 * 1024, 64, NullLogger.Instance);
        var hello = new Hello { ClientVersion = "0.1.0" };
        hello.ProtocolVersions.Add(1);

        var response = await this.SendAsync(connection, new ClientFrame { Hello = hello });

        Assert.Null(response.Welcome);
        Assert.Equal(ErrorCode.UnsupportedVersion, response.Error?.Code);
        Assert.Contains("Please update the plugin", response.Error!.Message);
        Assert.False(connection.HelloDone);
    }

    // ---------------------------------------------------------------- helpers

    private Task<Response> SendAsync(ClientConnection connection, ClientFrame frame) => this._handler.HandleAsync(connection, frame, Ct);

    private async Task<ClientConnection> HelloAsync(string address) {
        var connection = new ClientConnection(new ClosedWebSocket(), address, 128 * 1024, 64, NullLogger.Instance);
        var hello = new Hello();
        hello.ProtocolVersions.Add(ProtocolInfo.CurrentVersion);
        Assert.NotNull((await this.SendAsync(connection, new ClientFrame { Hello = hello })).Welcome);
        return connection;
    }

    /// <param name="keys">The identity to register (by default new keys, thrown away).</param>
    private Task<Response> StartRegistrationAsync(ClientConnection connection, string name = "Test Person", string world = World, IdentityKeys? keys = null) {
        using var generated = keys == null ? IdentityKeys.Generate() : null;
        return this.SendAsync(connection, new ClientFrame {
            StartRegistration = new StartRegistration { Character = new Character { Name = name, WorldName = world }, Identity = (keys ?? generated!).ToBundle() },
        });
    }

    /// <summary>Completes a registration as an honest client does: signed by <paramref name="keys"/>, for <paramref name="url"/>.</summary>
    private Task<Response> CompleteRegistrationAsync(ClientConnection connection, IdentityKeys keys, RegistrationChallenge challenge, string url = Url) {
        return this.SendAsync(connection, new ClientFrame {
            CompleteRegistration = new CompleteRegistration {
                ServerUrl = url,
                Signature = Google.Protobuf.ByteString.CopyFrom(RegistrationProof.Sign(keys, challenge.Nonce.Span, challenge.LodestoneId, url)),
            },
        });
    }

    /// <returns>A device token for a new debug account.</returns>
    private async Task<string> RegisterDebugAsync(string name) {
        var connection = await this.HelloAsync("203.0.113.30");
        using var keys = IdentityKeys.Generate();
        var challenge = (await this.StartRegistrationAsync(connection, name, ProtocolInfo.DebugWorldName, keys)).RegistrationChallenge!;
        var complete = await this.CompleteRegistrationAsync(connection, keys, challenge);
        return complete.RegistrationComplete!.DeviceToken;
    }

    /// <summary>Answers every search with "Test Person" on Gilgamesh, and every profile with <see cref="Profile"/>.</summary>
    private sealed class StubLodestone : HttpMessageHandler {
        /// <summary>The profile's text (by default without any code).</summary>
        public string Profile { get; set; } = "Nothing to see here.";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            var html = request.RequestUri!.AbsolutePath.TrimEnd('/') == "/lodestone/character"
                ? $"""<a href="/lodestone/character/{LodestoneId}/" class="entry__link"><p class="entry__name">Test Person</p><p class="entry__world">{World} [Aether]</p></a>"""
                : $"""<div class="character__selfintroduction">{this.Profile}</div>""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) });
        }
    }
}

/// <summary>A WebSocket that is already closed. Enough for a <see cref="ClientConnection"/> that is never run, or runs to an immediate close.</summary>
public sealed class ClosedWebSocket : WebSocket {
    public override WebSocketCloseStatus? CloseStatus => WebSocketCloseStatus.NormalClosure;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => WebSocketState.Closed;
    public override string? SubProtocol => null;

    public override void Abort() {
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

    public override void Dispose() {
    }

    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) {
        return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, null));
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
}
