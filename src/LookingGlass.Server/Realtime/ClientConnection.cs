using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading.Channels;
using Google.Protobuf;
using LookingGlass.Core.Client;
using LookingGlass.Protocol;
using LookingGlass.Server.Data;

namespace LookingGlass.Server.Realtime;

/// <summary>A registration started on this connection; only this connection can complete it.</summary>
/// <param name="Nonce">What the client signs, with the identity key it registers, to complete it.</param>
/// <param name="Origin">
/// The address registering was started for, which the code was made for: it completes only through that one (null for a
/// debug account, which has no code).
/// </param>
public sealed record PendingRegistration(
    long UserId,
    string Name,
    uint WorldId,
    string WorldName,
    IdentityBundle Identity,
    string Code,
    DateTimeOffset Expires,
    bool IsDebug,
    byte[] Nonce,
    ServerOrigin? Origin);

/// <summary>A key login challenge issued on this connection; only this connection can answer it, once.</summary>
/// <param name="CountedForAddress">
/// Counted as a failure for its address until answered correctly. Not when the account already failed from that address
/// within the hour: an account failing again (a plugin trying a login the server no longer knows on every connection)
/// counts once against the address, and its own per-address allowance limits the rest.
/// </param>
public sealed record PendingKeyLogin(long UserId, byte[] Challenge, DateTimeOffset Expires, bool CountedForAddress = true);

/// <summary>
/// One client's WebSocket. Requests are handled one at a time, in order.
/// Outgoing frames go through a bounded queue drained by a single send loop;
/// a client that can't keep up is disconnected rather than waited for.
/// </summary>
public sealed class ClientConnection {
    private readonly WebSocket _socket;
    private readonly int _maxFrameBytes;
    private readonly ILogger _logger;
    private readonly Channel<byte[]> _outbound;
    private readonly CancellationTokenSource _cts = new();
    private long _eventSeq;
    private string? _abortReason;
    private WebSocketCloseStatus _abortStatus = WebSocketCloseStatus.NormalClosure;
    // Requests this connection may make now (see WaitForRequestBudgetAsync); only the receive loop uses them.
    private readonly double _requestsPerSecond;
    private readonly double _requestBurst;
    private double _requestTokens;
    private long _requestTokensAt = Stopwatch.GetTimestamp();

    /// <param name="requestsPerSecond">Requests handled per second on average, past <paramref name="requestBurst"/> at once; 0 for no limit.</param>
    /// <param name="notLoggedInLifetime">How long it may stay without logging in (by default <see cref="DefaultNotLoggedInLifetime"/>).</param>
    public ClientConnection(WebSocket socket, string remoteAddress, int maxFrameBytes, int queueLength, ILogger logger,
        double requestsPerSecond = 0, int requestBurst = 0, TimeSpan? notLoggedInLifetime = null) {
        this._notLoggedInLifetime = notLoggedInLifetime ?? DefaultNotLoggedInLifetime;
        this._socket = socket;
        this.RemoteAddress = remoteAddress;
        this._maxFrameBytes = maxFrameBytes;
        this._logger = logger;
        this._requestsPerSecond = requestsPerSecond;
        this._requestBurst = Math.Max(1, requestBurst);
        this._requestTokens = this._requestBurst;
        this._outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(queueLength) {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <summary>
    /// How long a connection may stay without logging in, unless it is registering: then until its registration challenge
    /// expires. A plugin with a saved login logs in within milliseconds; one without (not registered yet, or a login the
    /// server doesn't know) is closed after this and reconnects, which is cheap; and connections that never log in can't
    /// pile up.
    /// </summary>
    public static readonly TimeSpan DefaultNotLoggedInLifetime = TimeSpan.FromMinutes(3);

    private readonly TimeSpan _notLoggedInLifetime;

    public string RemoteAddress { get; }
    public bool HelloDone { get; set; }
    public UserRow? User { get; set; }

    /// <summary>The hash of the device token <see cref="User"/> logged in with (what RetireIdentity's signature covers).</summary>
    public byte[]? DeviceTokenHash { get; set; }

    public PendingRegistration? PendingRegistration { get; set; }
    public int VerifyAttempts { get; set; }

    /// <summary>Verify attempts of this challenge the Lodestone couldn't answer, and so weren't counted in <see cref="VerifyAttempts"/>.</summary>
    public int UncountedVerifyAttempts { get; set; }
    public DateTimeOffset LastVerifyAttempt { get; set; } = DateTimeOffset.MinValue;

    /// <summary>
    /// Where the client says it connected to (scheme and Host header). Only used, in Development without PublicUrls,
    /// to check the address a key login names; the connecting side chooses it. Null if unknown, which no address matches.
    /// </summary>
    public ServerOrigin? RequestOrigin { get; init; }

    public PendingKeyLogin? PendingKeyLogin { get; set; }

    /// <summary>Key login challenges issued on this connection.</summary>
    public int KeyLoginChallenges { get; set; }
    public CancellationToken Aborted => this._cts.Token;

    /// <param name="respond">Queues each response; by default <see cref="SendResponse"/>. The server's goes through the
    /// <see cref="ConnectionRegistry"/>, which fills in presence on the way.</param>
    public async Task RunAsync(Func<ClientConnection, ClientFrame, CancellationToken, Task<Response>> handle,
        Action<ClientConnection, Response>? respond = null) {
        var sendLoop = Task.Run(this.SendLoop);
        // Disposed when the connection ends, so a closed connection isn't kept alive for the full lifetime. Started only once
        // assigned: a callback running before that (a thread held up past a short lifetime) couldn't put itself off for a
        // registration, and the connection would never close.
        Timer loginDeadline = null!;
        loginDeadline = new Timer(_ => {
            if (this.User != null) {
                return;
            }

            // Registering takes the user a while (putting the code in their Lodestone profile): until the challenge expires.
            var left = this.PendingRegistration is { } pending ? pending.Expires - DateTimeOffset.UtcNow : TimeSpan.Zero;
            if (left > TimeSpan.Zero) {
                try {
                    loginDeadline.Change(left + TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
                } catch (ObjectDisposedException) {
                    // The connection ended meanwhile.
                }

                return;
            }

            this.Abort("Not logged in");
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        loginDeadline.Change(this._notLoggedInLifetime, Timeout.InfiniteTimeSpan);
        var buffer = new byte[16 * 1024];
        using var frame = new MemoryStream();

        try {
            while (!this._cts.IsCancellationRequested) {
                var result = await this._socket.ReceiveAsync(buffer, this._cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) {
                    break;
                }

                if (frame.Length + result.Count > this._maxFrameBytes) {
                    this.Abort("Frame too large", WebSocketCloseStatus.MessageTooBig);
                    break;
                }

                frame.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) {
                    continue;
                }

                ClientFrame request;
                try {
                    request = ClientFrame.Parser.ParseFrom(frame.GetBuffer(), 0, (int) frame.Length);
                } catch (InvalidProtocolBufferException) {
                    this.Abort("Unreadable frame", WebSocketCloseStatus.ProtocolError);
                    break;
                } finally {
                    frame.SetLength(0);
                }

                await this.WaitForRequestBudgetAsync(this._cts.Token);
                var response = await handle(this, request, this._cts.Token);
                response.RequestId = request.RequestId;
                if (respond != null) {
                    respond(this, response);
                } else {
                    this.SendResponse(response);
                }
            }
        } catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException) {
            // Client went away (a socket torn down mid-receive can say so with an IOException) or we aborted.
        } finally {
            await loginDeadline.DisposeAsync();
            this._outbound.Writer.TryComplete();
            await sendLoop;
            await this.CloseAsync(this._abortStatus, this._abortReason);
        }
    }


    /// <summary>
    /// Takes one request from this connection's budget, waiting until one is due if it has none: a client sending requests
    /// faster than the limit is slowed down (its next request isn't read until then), never refused, so no plugin, however
    /// old, sees an error for it.
    /// </summary>
    private async ValueTask WaitForRequestBudgetAsync(CancellationToken ct) {
        if (this._requestsPerSecond <= 0) {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        this._requestTokens = Math.Min(this._requestBurst, this._requestTokens + Stopwatch.GetElapsedTime(this._requestTokensAt, now).TotalSeconds * this._requestsPerSecond);
        this._requestTokensAt = now;
        if (this._requestTokens < 1) {
            await Task.Delay(TimeSpan.FromSeconds((1 - this._requestTokens) / this._requestsPerSecond), ct);
            this._requestTokens = 1;
            this._requestTokensAt = Stopwatch.GetTimestamp();
        }

        this._requestTokens -= 1;
    }
    /// <summary>Queues a response.</summary>
    public void SendResponse(Response response) {
        this.Enqueue(new ServerFrame { Response = response });
    }

    /// <summary>Queues an event. Each connection numbers its own events, so the event is copied.</summary>
    public void SendEvent(Event ev) {
        var copy = ev.Clone();
        copy.Seq = (ulong) Interlocked.Increment(ref this._eventSeq);
        this.Enqueue(new ServerFrame { Event = copy });
    }

    public void Abort(string reason, WebSocketCloseStatus status = WebSocketCloseStatus.PolicyViolation) {
        if (Interlocked.CompareExchange(ref this._abortReason, reason, null) != null) {
            return;
        }

        this._logger.LogDebug("Closing connection from {Address}: {Reason}", this.RemoteAddress, reason);
        this._abortStatus = status;
        this._outbound.Writer.TryComplete();
        try {
            // Runs this connection's cancellation callbacks (its socket's, its send loop's) on the caller's thread: often
            // another connection's request (a newer login replacing this one, a registration dropping the account's
            // sessions). Whatever they throw is this connection's problem, not that request's, which has already done
            // its work and must still be answered. Every callback runs regardless (throwOnFirstException is false).
            this._cts.Cancel();
        } catch (Exception ex) {
            this._logger.LogWarning(ex, "Closing connection from {Address} ({Reason}): a cancellation callback failed", this.RemoteAddress, reason);
        }
    }

    private void Enqueue(ServerFrame frame) {
        if (!this._outbound.Writer.TryWrite(frame.ToByteArray())) {
            this.Abort("Too slow to receive messages");
        }
    }

    private async Task SendLoop() {
        try {
            await foreach (var data in this._outbound.Reader.ReadAllAsync(this._cts.Token)) {
                await this._socket.SendAsync(data, WebSocketMessageType.Binary, true, this._cts.Token);
            }
        } catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException or IOException) {
            this.Abort("Send failed");
        }
    }

    private async Task CloseAsync(WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure, string? reason = null) {
        try {
            if (this._socket.State is WebSocketState.Open or WebSocketState.CloseReceived) {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var description = reason ?? this._abortReason ?? "bye";
                await this._socket.CloseOutputAsync(status, description.Length > 120 ? description[..120] : description, timeout.Token);
            }
        } catch {
            // Best effort.
        }
    }
}
