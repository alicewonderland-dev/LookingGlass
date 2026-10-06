using System.Net.WebSockets;

namespace LookingGlass.Server.Hosting;

/// <summary>
/// Accepts WebSocket upgrades, and answers the ones the server can't take with 503 rather than an error per attempt.
/// Kestrel refuses an upgrade past <c>Kestrel:Limits:MaxConcurrentUpgradedConnections</c> by throwing, which unhandled is a
/// 500 and an error with a stack trace in the log for every attempt; a server that is full says so once a minute at most.
/// </summary>
public sealed class WebSocketAcceptor(ILogger logger, TimeProvider? time = null) {
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    // When the last warning was given (UTC ticks); none yet.
    private long _lastWarning;

    /// <returns>The socket, or null if the upgrade was refused (and answered).</returns>
    public async Task<WebSocket?> AcceptAsync(HttpContext context) {
        try {
            return await context.WebSockets.AcceptWebSocketAsync();
        } catch (InvalidOperationException ex) when (!context.Response.HasStarted) {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            this.WarnFull("the web server's limit on WebSocket connections (Kestrel:Limits:MaxConcurrentUpgradedConnections): " + ex.Message);
            return null;
        }
    }

    /// <summary>Answers a connection the server is too full to take: 503, and a warning once a minute at most.</summary>
    public void RefuseFull(HttpContext context) {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        this.WarnFull("LookingGlass:Limits:MaxConnections, and every connection has logged in, so none can be closed to make room");
    }

    private void WarnFull(string which) {
        var now = this._time.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref this._lastWarning);
        if (now - last < TimeSpan.TicksPerMinute || Interlocked.CompareExchange(ref this._lastWarning, now, last) != last) {
            return;
        }

        logger.LogWarning("Refusing new connections: the server is at {Limit}. Refused ones get 503 and retry; this is said once a minute at most.", which);
    }
}
