using System.Net.WebSockets;
using System.Threading.Channels;
using Google.Protobuf;
using WonderlandChat.Protocol;
using WonderlandChat.Server.Data;

namespace WonderlandChat.Server.Realtime;

/// <summary>A registration started on this connection; only this connection can complete it.</summary>
public sealed record PendingRegistration(
    long UserId,
    string Name,
    uint WorldId,
    string WorldName,
    IdentityBundle Identity,
    string Code,
    DateTimeOffset Expires,
    bool IsDebug);

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

    public ClientConnection(WebSocket socket, string remoteAddress, int maxFrameBytes, int queueLength, ILogger logger) {
        this._socket = socket;
        this.RemoteAddress = remoteAddress;
        this._maxFrameBytes = maxFrameBytes;
        this._logger = logger;
        this._outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(queueLength) {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <summary>How long a connection may stay without logging in (registration included).</summary>
    public static readonly TimeSpan UnauthenticatedLifetime = TimeSpan.FromMinutes(20);

    public string RemoteAddress { get; }
    public bool HelloDone { get; set; }
    public UserRow? User { get; set; }
    public PendingRegistration? PendingRegistration { get; set; }
    public int VerifyAttempts { get; set; }
    public DateTimeOffset LastVerifyAttempt { get; set; } = DateTimeOffset.MinValue;
    public CancellationToken Aborted => this._cts.Token;

    public async Task RunAsync(Func<ClientConnection, ClientFrame, CancellationToken, Task<Response>> handle) {
        var sendLoop = Task.Run(this.SendLoop);
        // Disposed when the connection ends, so a closed connection isn't kept alive for the full lifetime.
        var loginDeadline = new Timer(_ => {
            if (this.User == null) {
                this.Abort("Not logged in");
            }
        }, null, UnauthenticatedLifetime, Timeout.InfiniteTimeSpan);
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

                var response = await handle(this, request, this._cts.Token);
                response.RequestId = request.RequestId;
                this.Enqueue(new ServerFrame { Response = response });
            }
        } catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) {
            // Client went away or we aborted.
        } finally {
            await loginDeadline.DisposeAsync();
            this._outbound.Writer.TryComplete();
            await sendLoop;
            await this.CloseAsync(this._abortStatus, this._abortReason);
        }
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
        this._cts.Cancel();
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
        } catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException) {
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
