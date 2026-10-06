using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using LookingGlass.Core.Client;
using LookingGlass.Protocol;
using LookingGlass.Server.Hosting;
using LookingGlass.Server.Realtime;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// Connections that never log in can't fill the server: only a few per address, closed after minutes, and at the
/// server's cap the oldest of them makes way for a new connection, so plugins reconnecting always get in.
/// </summary>
public sealed class ConnectionLimitTests {
    [Fact]
    public void AnAddressMayHoldOnlyAFewConnectionsThatHaventLoggedIn() {
        var gate = new ConnectionGate(max: 100, perAddress: 20, notLoggedInPerAddress: 2, newPerMinute: 100);
        var first = gate.TryAdmit("203.0.113.1").Ticket!;
        Assert.NotNull(gate.TryAdmit("203.0.113.1").Ticket);
        Assert.Equal((null, ConnectionRefusal.TooManyNotLoggedIn), gate.TryAdmit("203.0.113.1"));

        // Another address isn't held up; and once one logs in, there's room again.
        Assert.NotNull(gate.TryAdmit("203.0.113.2").Ticket);
        first.SetLoggedIn(true);
        Assert.NotNull(gate.TryAdmit("203.0.113.1").Ticket);
        Assert.Equal(4, gate.Open);
        Assert.Equal(3, gate.NotLoggedIn);

        // Closing gives the place back.
        first.Dispose();
        first.Dispose();
        Assert.Equal(3, gate.Open);
    }

    [Fact]
    public void AtTheCapTheOldestConnectionNotLoggedInMakesWay() {
        var gate = new ConnectionGate(max: 3, perAddress: 20, notLoggedInPerAddress: 20, newPerMinute: 100);
        var closed = new List<string>();
        (ConnectionGate.Ticket Ticket, string Name) Admit(string name, bool registering = false) {
            var (ticket, refusal) = gate.TryAdmit("198.51.100." + name.Length);
            Assert.Equal(ConnectionRefusal.None, refusal);
            ticket!.Attach(() => registering, () => closed.Add(name));
            return (ticket, name);
        }

        var registering = Admit("registering", registering: true);
        var loggedIn = Admit("logged in");
        loggedIn.Ticket.SetLoggedIn(true);
        Admit("idle");

        // Full: the idle one goes, not the older one that is registering, nor the logged-in one.
        var newcomer = Admit("newcomer");
        Assert.Equal(["idle"], closed);
        Assert.Equal(3, gate.Open);

        // Then the newcomer (it hasn't logged in), and only then the one registering.
        Admit("second");
        Assert.Equal(["idle", "newcomer"], closed);
        newcomer.Ticket.Dispose();
        Assert.Equal(3, gate.Open);

        // The one registering goes only when no other that hasn't logged in is left.
        Admit("third").Ticket.SetLoggedIn(true);
        Assert.Equal(["idle", "newcomer", "second"], closed);
        Admit("fourth").Ticket.SetLoggedIn(true);
        Assert.Equal(["idle", "newcomer", "second", "registering"], closed);

        // When every connection has logged in, a new one is refused.
        Assert.Equal(0, gate.NotLoggedIn);
        Assert.Equal((null, ConnectionRefusal.ServerFull), gate.TryAdmit("192.0.2.10"));
        Assert.Equal(4, closed.Count);
    }

    [Fact]
    public void AConnectionChosenToMakeWayBeforeItWasSetUpClosesAsSoonAsItIs() {
        var gate = new ConnectionGate(max: 1, perAddress: 20, notLoggedInPerAddress: 20, newPerMinute: 100);
        var early = gate.TryAdmit("203.0.113.5").Ticket!;
        Assert.NotNull(gate.TryAdmit("203.0.113.6").Ticket);
        var closed = false;
        early.Attach(() => false, () => closed = true);
        Assert.True(closed);
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:aa:bbcc:1:2:3:4", "2001:db8:aa:bb00::/56")]
    [InlineData("2001:db8:aa:bbff:ffff::1", "2001:db8:aa:bb00::/56")]
    public void ConnectionsAreCountedPerIpv6Slash56(string address, string key) {
        Assert.Equal(key, ClientAddresses.ConnectionLimitKey(IPAddress.Parse(address)));
    }

    /// <summary>Through the server: past the per-address limit of connections not logged in, the upgrade is refused.</summary>
    [Fact]
    public async Task TheServerRefusesAnAddressMoreConnectionsThatHaventLoggedIn() {
        await using var server = new Harness(settings: ("LookingGlass:Limits:NotLoggedInConnectionsPerIp", "2"));
        try {
            var alice = await server.RegisterAsync("Alice Logs In Later");
            await alice.Session.DisposeAsync();
            var first = await server.ConnectRawAsync(remoteAddress: "203.0.113.30");
            await using var second = await server.ConnectRawAsync(remoteAddress: "203.0.113.30");
            await Assert.ThrowsAnyAsync<Exception>(() => server.ConnectRawAsync(remoteAddress: "203.0.113.30"));

            // Once one of them logs in, there's room for another.
            Assert.NotNull((await first.SendAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = alice.Store.Load().DeviceToken } })).AuthenticateOk);
            await using var third = await server.ConnectRawAsync(remoteAddress: "203.0.113.30");
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
    }

    /// <summary>Through the server: at its cap, a new connection gets in, and the oldest that hasn't logged in is closed.</summary>
    [Fact]
    public async Task AtItsCapTheServerClosesTheOldestConnectionNotLoggedIn() {
        await using var server = new Harness(settings: ("LookingGlass:Limits:MaxConnections", "2"));
        try {
            var alice = await server.RegisterAsync("Alice At The Cap");
            await alice.Session.DisposeAsync();
            await WaitFor(() => server.Registry.IsOnline(alice.UserId) ? null : new object());
            var loggedIn = await server.ConnectRawAsync(remoteAddress: "198.51.100.40");
            Assert.NotNull((await loggedIn.SendAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = alice.Store.Load().DeviceToken } })).AuthenticateOk);
            var idle = await server.ConnectRawAsync(remoteAddress: "198.51.100.41");

            var newcomer = await server.ConnectRawAsync(remoteAddress: "198.51.100.42");

            await Assert.ThrowsAnyAsync<Exception>(() => idle.SendAsync(new ClientFrame { Ping = new Ping() }));
            Assert.NotNull((await newcomer.SendAsync(new ClientFrame { Ping = new Ping() })).Pong);
            Assert.NotNull((await loggedIn.SendAsync(new ClientFrame { Ping = new Ping() })).Pong);
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
    }

    /// <summary>A connection that doesn't log in is closed after its lifetime; one registering, once its challenge expires.</summary>
    [Fact]
    public async Task AConnectionThatDoesntLogInIsClosedUnlessItIsRegistering() {
        var idle = new ClientConnection(new OpenWebSocket(), "203.0.113.50", 1024, 4, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            notLoggedInLifetime: TimeSpan.FromMilliseconds(100));
        await idle.RunAsync((_, _, _) => Task.FromResult(new Response())).WaitAsync(Harness.Timeout, Ct);

        var registering = new ClientConnection(new OpenWebSocket(), "203.0.113.51", 1024, 4, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            notLoggedInLifetime: TimeSpan.FromMilliseconds(100)) {
            PendingRegistration = new PendingRegistration(1, "Someone", 0, "Gilgamesh", new IdentityBundle(), "", DateTimeOffset.UtcNow.AddSeconds(1.5), false, [], null),
        };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await registering.RunAsync((_, _, _) => Task.FromResult(new Response())).WaitAsync(Harness.Timeout, Ct);
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(1.4), $"Closed after {clock.Elapsed.TotalMilliseconds:0} ms, before its challenge expired");
    }

    /// <summary>
    /// At the web server's own limit on WebSockets, accepting throws. That is answered 503, and logged as a warning once a
    /// minute, not as an error with a stack trace per attempt.
    /// </summary>
    [Fact]
    public async Task AnUpgradeTheWebServerRefusesIsAnswered503AndLoggedOnceAMinute() {
        var logs = new CapturingLoggerProvider();
        var clock = new ManualClock();
        var acceptor = new WebSocketAcceptor(logs.CreateLogger("acceptor"), clock);
        for (var i = 0; i < 5; i++) {
            var context = new DefaultHttpContext();
            context.Features.Set<IHttpWebSocketFeature>(new RefusingWebSocketFeature());
            Assert.Null(await acceptor.AcceptAsync(context));
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        }

        Assert.Single(logs.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, logs.Entries.Single().Level);
        clock.Offset += TimeSpan.FromMinutes(2);
        acceptor.RefuseFull(new DefaultHttpContext());
        Assert.Equal(2, logs.Entries.Count);
    }

    private sealed class RefusingWebSocketFeature : IHttpWebSocketFeature {
        public bool IsWebSocketRequest => true;

        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) =>
            throw new InvalidOperationException("Request cannot be upgraded because the server has already opened the maximum number of upgraded connections.");
    }

    /// <summary>An open socket that never receives anything until it is aborted.</summary>
    private sealed class OpenWebSocket : WebSocket {
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort() {
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Dispose() {
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) {
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
