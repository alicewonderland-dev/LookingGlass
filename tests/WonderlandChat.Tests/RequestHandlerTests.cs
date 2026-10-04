using System.Net;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WonderlandChat.Core.Crypto;
using WonderlandChat.Core.Membership;
using WonderlandChat.Protocol;
using WonderlandChat.Server;
using WonderlandChat.Server.Data;
using WonderlandChat.Server.Hosting;
using WonderlandChat.Server.Realtime;
using WonderlandChat.Server.Services;
using static WonderlandChat.Tests.Harness;

namespace WonderlandChat.Tests;

/// <summary>
/// Calls <see cref="RequestHandler"/> directly, with a stub Lodestone, for the
/// registration and login paths an in-process client can't reach (real worlds).
/// </summary>
public sealed class RequestHandlerTests : IDisposable {
    private const string World = "Gilgamesh";
    private const long LodestoneId = 31337;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wct-handler-" + Guid.NewGuid().ToString("N"));
    private readonly Database _db;
    private readonly ConnectionRegistry _registry = new();
    private readonly RequestHandler _handler;

    public RequestHandlerTests() {
        Directory.CreateDirectory(this._directory);
        this._db = new Database(Path.Combine(this._directory, "test.db"));
        var options = Options.Create(new ServerOptions {
            Dev = { AllowDebugAccounts = true },
            Lodestone = { BaseUrl = "https://lodestone.test", MinDelaySeconds = 0 },
            Limits = { RegistrationsPerHourPerIp = 3 },
        });
        var lodestone = new LodestoneClient(new HttpClient(new StubLodestone()), options, NullLogger<LodestoneClient>.Instance);
        this._handler = new RequestHandler(this._db, this._registry, lodestone, options, NullLogger<RequestHandler>.Instance,
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
        Assert.NotNull((await this.StartRegistrationAsync(connection)).RegistrationChallenge);

        // The stub profile never contains the code, so the first attempt fails normally...
        var first = await this.SendAsync(connection, new ClientFrame { CompleteRegistration = new CompleteRegistration() });
        Assert.Equal(ErrorCode.RegistrationFailed, first.Error?.Code);

        // ...and an immediate retry is refused without asking the Lodestone again.
        var second = await this.SendAsync(connection, new ClientFrame { CompleteRegistration = new CompleteRegistration() });
        Assert.Equal(ErrorCode.RateLimited, second.Error?.Code);
        Assert.Contains("Wait", second.Error!.Message);
        Assert.Equal(1, connection.VerifyAttempts);
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

    private Task<Response> StartRegistrationAsync(ClientConnection connection, string name = "Test Person", string world = World) {
        using var keys = IdentityKeys.Generate();
        return this.SendAsync(connection, new ClientFrame {
            StartRegistration = new StartRegistration { Character = new Character { Name = name, WorldName = world }, Identity = keys.ToBundle() },
        });
    }

    /// <returns>A device token for a new debug account.</returns>
    private async Task<string> RegisterDebugAsync(string name) {
        var connection = await this.HelloAsync("203.0.113.30");
        Assert.NotNull((await this.StartRegistrationAsync(connection, name, ProtocolInfo.DebugWorldName)).RegistrationChallenge);
        var complete = await this.SendAsync(connection, new ClientFrame { CompleteRegistration = new CompleteRegistration() });
        return complete.RegistrationComplete!.DeviceToken;
    }

    /// <summary>Answers every search with "Test Person" on Gilgamesh, and every profile without the code.</summary>
    private sealed class StubLodestone : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            var html = request.RequestUri!.AbsolutePath.TrimEnd('/') == "/lodestone/character"
                ? $"""<a href="/lodestone/character/{LodestoneId}/" class="entry__link"><p class="entry__name">Test Person</p><p class="entry__world">{World} [Aether]</p></a>"""
                : """<div class="character__selfintroduction">Nothing to see here.</div>""";
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
