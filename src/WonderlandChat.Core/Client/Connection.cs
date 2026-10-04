using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using Google.Protobuf;
using WonderlandChat.Protocol;

namespace WonderlandChat.Core.Client;

/// <summary>
/// One WebSocket connection. A single send loop owns all writes to the socket,
/// a single receive loop owns all reads, and every request completes with a
/// response, a timeout or a disconnect error.
/// </summary>
internal sealed class Connection : IAsyncDisposable {
    private readonly WebSocket _socket;
    private readonly int _maxReceiveBytes;
    private readonly TimeSpan _requestTimeout;
    private readonly Action<Event> _onEvent;
    private readonly Action<bool, string> _trace;
    private readonly Channel<byte[]> _outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256) {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<Response>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _nextRequestId;
    private int _closing;
    private string _closeReason = "Connection closed";

    public Connection(WebSocket socket, int maxReceiveBytes, TimeSpan requestTimeout, Action<Event> onEvent, Action<bool, string> trace) {
        this._socket = socket;
        this._maxReceiveBytes = maxReceiveBytes;
        this._requestTimeout = requestTimeout;
        this._onEvent = onEvent;
        this._trace = trace;
    }

    /// <summary>Completes when the connection has closed for any reason.</summary>
    public Task Closed => this._closed.Task;

    public string CloseReason => this._closeReason;

    public void Start() {
        _ = Task.Run(this.SendLoop);
        _ = Task.Run(this.ReceiveLoop);
    }

    public async Task<Response> RequestAsync(ClientFrame frame, CancellationToken ct, TimeSpan? timeout = null) {
        var id = (uint) Interlocked.Increment(ref this._nextRequestId);
        frame.RequestId = id;
        var tcs = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        this._pending[id] = tcs;
        var limit = timeout ?? this._requestTimeout;

        // The timeout covers the whole request, including waiting for room in the send queue.
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(limit);

        try {
            if (Volatile.Read(ref this._closing) == 1) {
                throw new SessionDisconnectedException(this._closeReason);
            }

            this._trace(true, $"#{id} {frame.BodyCase}");
            await this._outbound.Writer.WriteAsync(frame.ToByteArray(), timer.Token);
            return await tcs.Task.WaitAsync(timer.Token);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            throw new TimeoutException($"The server did not answer {frame.BodyCase} within {limit.TotalSeconds:0} seconds.");
        } catch (ChannelClosedException) {
            throw new SessionDisconnectedException(this._closeReason);
        } finally {
            this._pending.TryRemove(id, out _);
        }
    }

    public void Abort(string reason) {
        this.Close(reason);
    }

    private async Task SendLoop() {
        try {
            await foreach (var data in this._outbound.Reader.ReadAllAsync(this._cts.Token)) {
                await this._socket.SendAsync(data, WebSocketMessageType.Binary, true, this._cts.Token);
            }
        } catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException) {
            // Closing.
        } finally {
            this.Close("Send failed");
        }
    }

    private async Task ReceiveLoop() {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        try {
            while (!this._cts.IsCancellationRequested) {
                var result = await this._socket.ReceiveAsync(buffer, this._cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) {
                    this.Close(string.IsNullOrEmpty(result.CloseStatusDescription)
                        ? "Server closed the connection"
                        : $"Server closed the connection: {result.CloseStatusDescription}");
                    return;
                }

                if (message.Length + result.Count > this._maxReceiveBytes) {
                    this.Close("Server sent a frame that was too large");
                    return;
                }

                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) {
                    continue;
                }

                ServerFrame frame;
                try {
                    frame = ServerFrame.Parser.ParseFrom(message.GetBuffer(), 0, (int) message.Length);
                } catch (InvalidProtocolBufferException) {
                    this.Close("Server sent an unreadable frame");
                    return;
                } finally {
                    message.SetLength(0);
                }

                this.Dispatch(frame);
            }
        } catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException) {
            this.Close(ex is WebSocketException ? $"Connection lost: {ex.Message}" : "Connection closed");
        } catch (Exception ex) {
            // Never leave the connection half-alive: callers wait on Closed.
            this.Close($"Connection failed: {ex.Message}");
        }
    }

    private void Dispatch(ServerFrame frame) {
        switch (frame.BodyCase) {
            case ServerFrame.BodyOneofCase.Response: {
                var response = frame.Response;
                this._trace(false, $"#{response.RequestId} {response.ResultCase}{(response.Error != null ? $" {response.Error.Code}" : "")}");
                if (this._pending.TryRemove(response.RequestId, out var tcs)) {
                    tcs.TrySetResult(response);
                }

                break;
            }
            case ServerFrame.BodyOneofCase.Event:
                this._trace(false, $"event {frame.Event.Seq} {frame.Event.KindCase}");
                this._onEvent(frame.Event);
                break;
        }
    }

    private void Close(string reason) {
        if (Interlocked.Exchange(ref this._closing, 1) == 1) {
            return;
        }

        this._closeReason = reason;
        this._outbound.Writer.TryComplete();
        this._cts.Cancel();

        foreach (var (id, tcs) in this._pending) {
            if (this._pending.TryRemove(id, out _)) {
                tcs.TrySetException(new SessionDisconnectedException(reason));
            }
        }

        this._closed.TrySetResult();
    }

    public async ValueTask DisposeAsync() {
        this.Close("Connection disposed");
        try {
            if (this._socket.State == WebSocketState.Open) {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await this._socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
            }
        } catch {
            // Best effort.
        }

        this._socket.Dispose();
        this._cts.Dispose();
    }
}
