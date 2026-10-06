using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LookingGlass.Core.Client;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>How the client opens its connection to the server.</summary>
public sealed class ClientConnectTests {
    /// <summary>
    /// A redirect would hand the connection (and the login sent on it) to another server while the client still
    /// files its keys and login under the address it was given. The client refuses to follow one, and says so.
    /// </summary>
    [Fact]
    public async Task TheClientDoesNotFollowRedirects() {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint) listener.LocalEndpoint).Port;
        var requests = new ConcurrentQueue<string>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        // Answers every request with a redirect to another path on itself, and records the request lines.
        var serving = Task.Run(async () => {
            while (!stop.IsCancellationRequested) {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                var stream = client.GetStream();
                var buffer = new byte[8192];
                var text = new StringBuilder();
                while (!text.ToString().Contains("\r\n\r\n")) {
                    var read = await stream.ReadAsync(buffer, stop.Token);
                    if (read == 0) {
                        break;
                    }

                    text.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                requests.Enqueue(text.ToString().Split("\r\n")[0]);
                var response = $"HTTP/1.1 302 Found\r\nLocation: ws://127.0.0.1:{port}/elsewhere\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), stop.Token);
            }
        }, stop.Token);

        var logs = new ConcurrentQueue<string>();
        await using (var session = new ClientSession(new ClientSessionOptions {
            ServerUri = new Uri($"ws://127.0.0.1:{port}/ws"),
            ReconnectMinDelay = TimeSpan.FromHours(1),
            Log = (_, text) => logs.Enqueue(text),
        }, new InMemorySecretStore())) {
            session.Start();
            var snapshot = await WaitFor(() => session.Snapshot is { State: ConnectionState.Reconnecting } s ? s : null);

            // One request, to the address given, and none to where it was sent.
            Assert.Equal(["GET /ws HTTP/1.1"], requests.ToArray());
            Assert.Contains(logs, line => line.Contains("redirect", StringComparison.OrdinalIgnoreCase));
            // The user sees why too, not only "reconnecting".
            Assert.Contains("redirect", snapshot.StatusText, StringComparison.OrdinalIgnoreCase);
        }

        await stop.CancelAsync();
        listener.Stop();
        try {
            await serving;
        } catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) {
            // Stopped.
        }
    }

    /// <summary>
    /// A cancellation callback that throws as the connection closes (its socket's, torn down meanwhile) doesn't escape the
    /// close: aborting a connection never throws at whoever aborts it, and the connection still closes.
    /// </summary>
    [Fact]
    public async Task AbortingAConnectionWhoseSocketThrowsOnCancelIsSafe() {
        var socket = new ThrowOnCancelWebSocket();
        var connection = new Connection(socket, 1024, TimeSpan.FromSeconds(5), _ => { }, _ => { }, (_, _) => { });
        connection.Start();
        await socket.Receiving.WaitAsync(Harness.Timeout, Ct);

        connection.Abort("Session stopped");

        await connection.Closed.WaitAsync(Harness.Timeout, Ct);
        await connection.DisposeAsync();
    }

    /// <summary>An open socket whose pending receive fails its cancellation callback.</summary>
    private sealed class ThrowOnCancelWebSocket : System.Net.WebSockets.WebSocket {
        private readonly TaskCompletionSource _receiving = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Receiving => this._receiving.Task;
        public override System.Net.WebSockets.WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override System.Net.WebSockets.WebSocketState State => System.Net.WebSockets.WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort() {
        }

        public override Task CloseAsync(System.Net.WebSockets.WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Dispose() {
        }

        public override async Task<System.Net.WebSockets.WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) {
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (cancellationToken.Register(() => {
                cancelled.TrySetResult();
                throw new ObjectDisposedException("socket");
            })) {
                this._receiving.TrySetResult();
                await cancelled.Task;
            }

            throw new OperationCanceledException(cancellationToken);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, System.Net.WebSockets.WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Stopping a session aborts its connection while the session's own loop disposes it. Disposing used to free what the
    /// abort, already under way, went on to cancel, which threw ObjectDisposedException out of the session's DisposeAsync
    /// (seen as an occasional test failure while a harness shut down). Disposing waits for a close in progress.
    /// </summary>
    [Fact]
    public async Task DisposingAConnectionWhileItIsBeingAbortedIsSafe() {
        var connection = new Connection(new ClosedWebSocket(), 1024, TimeSpan.FromSeconds(5), _ => { }, _ => { }, (_, _) => { });
        Task? disposing = null;
        connection.WhileClosingForTests = () => {
            // The other side disposes now; with the fix it waits for this close to finish, so it can't complete here.
            disposing = Task.Run(async () => await connection.DisposeAsync());
            disposing.Wait(TimeSpan.FromMilliseconds(300));
        };

        connection.Abort("Session stopped");
        await disposing!.WaitAsync(Harness.Timeout, Ct);
        await connection.Closed.WaitAsync(Harness.Timeout, Ct);
    }
}
