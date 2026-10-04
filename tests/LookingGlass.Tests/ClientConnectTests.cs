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
}
