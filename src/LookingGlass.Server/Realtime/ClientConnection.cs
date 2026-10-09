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
    // The loop sending what is queued, once RunAsync started it.
    private Task? _sendLoop;
    // The last event's number, and the lock that keeps numbers in the order events are queued (see TrySendDroppable).
    private long _eventSeq;
    private readonly Lock _eventLock = new();
    private readonly int _queueLength;
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
        this._queueLength = queueLength;
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

    /// <summary>
    /// The client offered local chat ("local.v1") in Hello and the server agreed: only such a connection may send a local
    /// message, and only to such a connection is one passed on, so an older plugin never sees one.
    /// </summary>
    public bool LocalChatAgreed { get; set; }

    /// <summary>
    /// The client offered "devices.v1" in Hello and the server agreed: only such a connection is told when its account gets a
    /// new device (DeviceAdded), so an older plugin never sees the event.
    /// </summary>
    public bool DevicesAgreed { get; set; }

    /// <summary>
    /// The nonces the last few lists of devices sent on this connection gave, each for one "Sign out everywhere else" to sign,
    /// and until when. Only the request being handled uses them (one at a time per connection).
    /// </summary>
    public List<(byte[] Nonce, DateTimeOffset Expires)> SignOutNonces { get; } = [];

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
        // Debug only, as closing is: for an operator checking the server sees each client's own address, not its proxy's.
        this._logger.LogDebug("Connection from {Address}", this.RemoteAddress);
        var sendLoop = Task.Run(this.SendLoop);
        this._sendLoop = sendLoop;
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

                if (Volatile.Read(ref this._abortReason) != null) {
                    // Closing (see CloseAfterQueued): a replaced connection does nothing more.
                    break;
                }

                await this.WaitForRequestBudgetAsync(this._cts.Token);
                var response = await handle(this, request, this._cts.Token);
                response.RequestId = request.RequestId;
                if (respond != null) {
                    respond(this, response);
                } else {
                    this.SendResponse(response);
                }

                if (Volatile.Read(ref this._closeAfterResponse) is { } reason) {
                    // Nothing more is read; what was queued goes out first (see below), then the close.
                    Interlocked.CompareExchange(ref this._abortReason, reason, null);
                    this._abortStatus = WebSocketCloseStatus.PolicyViolation;
                    break;
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
        bool queued;
        lock (this._eventLock) {
            queued = this._outbound.Writer.TryWrite(this.Numbered(ev));
        }

        if (!queued) {
            this.Abort("Too slow to receive messages");
        }
    }

    /// <summary>
    /// Queues an event that can be lost (a local message), only while the queue is less than half full: a connection too
    /// slow to take it loses it rather than being closed, and what it is sent this way never fills the room other events
    /// need. Numbered only if queued, so the numbers have no gap.
    /// </summary>
    /// <returns>Whether it was queued.</returns>
    public bool TrySendDroppable(Event ev) {
        lock (this._eventLock) {
            if (this._outbound.Reader.Count * 2 >= this._queueLength) {
                return false;
            }

            var copy = ev.Clone();
            copy.Seq = (ulong) (this._eventSeq + 1);
            if (!this._outbound.Writer.TryWrite(new ServerFrame { Event = copy }.ToByteArray())) {
                return false;
            }

            this._eventSeq++;
            return true;
        }
    }

    /// <summary>An event copied with this connection's next number, ready to queue. Call inside <see cref="_eventLock"/>.</summary>
    private byte[] Numbered(Event ev) {
        var copy = ev.Clone();
        copy.Seq = (ulong) ++this._eventSeq;
        return new ServerFrame { Event = copy }.ToByteArray();
    }

    /// <summary>
    /// Closes the connection once the request being handled is answered, rather than at once: the answer (a refusal saying
    /// why, say) still reaches the client. Nothing more is read from it.
    /// </summary>
    public void CloseAfterResponse(string reason) => Interlocked.CompareExchange(ref this._closeAfterResponse, reason, null);

    private string? _closeAfterResponse;

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

    /// <summary>How long <see cref="CloseAfterQueued"/> waits for what is queued to go out, at most.</summary>
    public static readonly TimeSpan FlushGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Closes the connection once what is queued for it has gone out (for at most <see cref="FlushGrace"/>), rather than at
    /// once as <see cref="Abort"/> does: for a connection replaced by a newer login of its user, whose queue may hold what the
    /// newer login's own requests just told it (that the account has a new device, say). Nothing more is read from it, or
    /// queued for it.
    /// </summary>
    public void CloseAfterQueued(string reason) {
        if (Interlocked.CompareExchange(ref this._abortReason, reason, null) != null) {
            return;
        }

        this._logger.LogDebug("Closing connection from {Address} once its queue is sent: {Reason}", this.RemoteAddress, reason);
        this._abortStatus = WebSocketCloseStatus.PolicyViolation;
        // The send loop sends what is queued and ends; then the close goes out after it (cancelling a receive would tear the
        // socket down at once, queue and all), and the client's answer to it ends the receive loop.
        this._outbound.Writer.TryComplete();
        _ = this.CloseWhenSentAsync(reason);
    }

    private async Task CloseWhenSentAsync(string reason) {
        try {
            if (this._sendLoop is { } sending) {
                await sending.WaitAsync(FlushGrace);
            }

            if (this._socket.State == WebSocketState.Open) {
                using var timeout = new CancellationTokenSource(FlushGrace);
                await this._socket.CloseOutputAsync(this._abortStatus, reason.Length > 120 ? reason[..120] : reason, timeout.Token);
            }
        } catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or WebSocketException or ObjectDisposedException or IOException) {
            // Not sent in time, or gone already: closed below either way.
        }

        // A client that doesn't take what is queued, or answer the close, doesn't hold the connection open.
        await Task.Delay(FlushGrace);
        try {
            this._cts.Cancel();
        } catch (Exception) {
            // Its callbacks' problem (see Abort); the connection is closed anyway.
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
