using System.Net.WebSockets;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using LookingGlass.Core.Client;
using LookingGlass.Core.Crypto;
using LookingGlass.Protocol;
using LookingGlass.Server.Data;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>Running a server on a public host: what it refuses to start with, what it tells, backups and shutting down.</summary>
public sealed class OperationsTests {
    private const string PublicUrl = "wss://chat.example.com/ws";

    /// <summary>
    /// Debug accounts let anyone take over any debug account, and the echo bot needs them. Outside Development the server
    /// doesn't start with either, and says what to do, unless the operator says they mean it.
    /// </summary>
    [Theory]
    [InlineData("AllowDebugAccounts")]
    [InlineData("HostEchoBot")]
    public async Task OutsideDevelopmentDebugAccountsAndTheEchoBotRefuseToStart(string setting) {
        var logs = new CapturingLoggerProvider();
        await ExitCodeGate.WaitAsync(Ct);
        var exitCode = Environment.ExitCode;
        var directory = Path.Combine(Path.GetTempPath(), "lgt-" + Guid.NewGuid().ToString("N"));
        try {
            Exception? failed = null;
            try {
                await using var server = new Harness(directory, allowDebugAccounts: setting == "AllowDebugAccounts", environment: "Production", logs: logs, settings: [
                    ("LookingGlass:PublicUrls:0", PublicUrl),
                    ("LookingGlass:Dev:HostEchoBot", setting == "HostEchoBot" ? "true" : "false"),
                    ("LookingGlass:Dev:AllowOutsideDevelopment", "false"),
                ]);
                await using var raw = await server.ConnectRawAsync();
            } catch (Exception ex) {
                failed = ex;
            }

            Assert.NotNull(failed);
            var critical = Assert.Single(logs.AtLeast(Microsoft.Extensions.Logging.LogLevel.Critical));
            Assert.Contains($"LookingGlass:Dev:{setting}", critical);
            Assert.Contains("LookingGlass:Dev:AllowOutsideDevelopment", critical);
            Assert.Equal(1, Environment.ExitCode);
        } finally {
            Environment.ExitCode = exitCode;
            ExitCodeGate.Release();
            DeleteDirectory(directory);
        }
    }

    /// <summary>With the override, it starts, and says so loudly.</summary>
    [Fact]
    public async Task OutsideDevelopmentDebugAccountsStartOnlyWhenMeantAndSaySo() {
        var logs = new CapturingLoggerProvider();
        await using var server = new Harness(environment: "Production", logs: logs, settings: ("LookingGlass:PublicUrls:0", PublicUrl));
        try {
            await using var raw = await server.ConnectRawAsync();
            Assert.Contains(logs.AtLeast(Microsoft.Extensions.Logging.LogLevel.Warning),
                warning => warning.Contains("Debug accounts are ENABLED") && warning.Contains("Production") && warning.Contains("AllowOutsideDevelopment"));
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
    }

    /// <summary>At startup the server says how it is set up, in one line an operator can check.</summary>
    [Fact]
    public async Task TheServerSaysHowItIsSetUpWhenItStarts() {
        var logs = new CapturingLoggerProvider();
        await using var server = new Harness(allowDebugAccounts: false, environment: "Production", logs: logs, settings: ("LookingGlass:PublicUrls:0", PublicUrl));
        try {
            await using var raw = await server.ConnectRawAsync();
            var summary = Assert.Single(logs.Entries, entry => entry.Message.StartsWith("LookingGlass server ")).Message;
            Assert.Contains("Production", summary);
            Assert.Contains("debug accounts off", summary);
            Assert.Contains("echo bot off", summary);
            Assert.Contains("every address is wss:// with a fully qualified name", summary);
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
    }

    /// <summary>/health says the server is up, and its version: nothing about who uses it.</summary>
    [Fact]
    public async Task HealthSaysOnlyThatTheServerIsUpAndItsVersion() {
        await using var server = new Harness();
        try {
            var alice = await server.RegisterAsync("Alice Healthy");
            using var http = server.Factory.CreateClient();
            using var response = await http.GetAsync("/health", Ct);
            Assert.True(response.IsSuccessStatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal(["status", "version"], json.RootElement.EnumerateObject().Select(property => property.Name).Order());
            Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
            Assert.Matches(@"^\d+\.\d+\.\d+$", json.RootElement.GetProperty("version").GetString());
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
    }

    /// <summary>
    /// Stopping the server (systemd sends SIGTERM) closes every connection at once, saying it is going away, so clients
    /// reconnect later rather than wait; and leaves the database whole, its write-ahead log folded in.
    /// </summary>
    [Fact]
    public async Task StoppingTheServerClosesConnectionsAndFoldsInTheLog() {
        var server = new Harness();
        try {
            await using (server) {
                var alice = await server.RegisterAsync("Alice Shutdown");
                await alice.Session.CreateChannelAsync("Before Shutdown", Ct);
                var raw = await server.ConnectRawAsync();
                var lifetime = server.Factory.Services.GetRequiredService<IHostApplicationLifetime>();

                lifetime.StopApplication();

                // Closed, saying why; the in-process test server can tear the socket down before the close frame gets through.
                var closed = await Assert.ThrowsAnyAsync<Exception>(() => raw.SendAsync(new ClientFrame { Ping = new Ping() }));
                if (closed is InvalidOperationException) {
                    Assert.Contains("shutting down", closed.Message);
                } else {
                    Assert.IsType<IOException>(closed);
                }

                await WaitFor(() => lifetime.ApplicationStopped.IsCancellationRequested ? new object() : null);
            }

            var path = Path.Combine(server.DataDirectory, "lookingglass.db");
            // Folded in and closed: SQLite deletes the log when its last connection closes.
            await WaitFor(() => new FileInfo(path + "-wal") is var wal && (!wal.Exists || wal.Length == 0) ? wal : null);
            using var check = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            check.Open();
            using var command = check.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", command.ExecuteScalar());
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
    }

    /// <summary>
    /// A backup made while the server writes is a whole, consistent database in one file (no -wal beside it), which can
    /// replace the server's own to restore it.
    /// </summary>
    [Fact]
    public async Task ABackupMadeWhileTheServerWritesIsWholeAndSelfContained() {
        var directory = Path.Combine(Path.GetTempPath(), "lgt-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            var path = Path.Combine(directory, "lookingglass.db");
            var db = new Database(path);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var registered = 0;
            var writing = Task.Run(() => {
                while (!stop.IsCancellationRequested) {
                    using var keys = IdentityKeys.Generate();
                    var id = Interlocked.Increment(ref registered);
                    db.RegisterUser(-id, $"Writer {id}", 0, ProtocolInfo.DebugWorldName, keys.ToBundle(), true);
                }
            }, Ct);

            await WaitFor(() => Volatile.Read(ref registered) > 20 ? new object() : null);
            var target = Path.Combine(directory, "copy.db");
            var result = db.Backup(target);
            await stop.CancelAsync();
            await writing;

            Assert.Equal(target, result);
            Assert.False(File.Exists(target + "-wal"));
            using var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
            copy.Open();
            Assert.Equal("ok", Scalar(copy, "PRAGMA integrity_check;"));
            Assert.Equal("delete", Scalar(copy, "PRAGMA journal_mode;"));
            Assert.InRange(Convert.ToInt64(Scalar(copy, "SELECT COUNT(*) FROM users;")), 20, registered);

            // Restoring: the copy, put in place of the database, opens as a server's database.
            Database.ReleasePooledConnections(path);
            var restoredPath = Path.Combine(directory, "restored.db");
            File.Copy(target, restoredPath);
            Assert.NotNull(new Database(restoredPath).GetUser(-1));
            Database.ReleasePooledConnections(restoredPath);
        } finally {
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// <c>LookingGlass.Server --backup &lt;folder&gt; --keep N</c> makes a dated backup of the configured database without
    /// starting the server, and keeps only the newest N in that folder; for a timer or cron job.
    /// </summary>
    [Fact]
    public async Task TheBackupCommandKeepsTheNewestCopiesWithoutStartingTheServer() {
        var data = Path.Combine(Path.GetTempPath(), "lgt-data-" + Guid.NewGuid().ToString("N"));
        var backups = Path.Combine(data, "backups");
        Directory.CreateDirectory(data);
        await ExitCodeGate.WaitAsync(Ct);
        var exitCode = Environment.ExitCode;
        try {
            var path = Path.Combine(data, "lookingglass.db");
            var db = new Database(path);
            using (var keys = IdentityKeys.Generate()) {
                db.RegisterUser(-1, "Backed Up", 0, ProtocolInfo.DebugWorldName, keys.ToBundle(), true);
            }

            // Not a file this folder holds: left alone.
            Directory.CreateDirectory(backups);
            File.WriteAllText(Path.Combine(backups, "notes.txt"), "mine");
            for (var i = 0; i < 3; i++) {
                Environment.ExitCode = 0;
                RunServer("--backup", backups, "--keep", "2", $"--LookingGlass:DataDirectory={data}");
                Assert.Equal(0, Environment.ExitCode);
            }

            var copies = Directory.GetFiles(backups, "lookingglass-*.db");
            Assert.Equal(2, copies.Length);
            Assert.True(File.Exists(Path.Combine(backups, "notes.txt")));
            foreach (var copy in copies) {
                Assert.NotNull(new Database(copy).GetUser(-1));
                Database.ReleasePooledConnections(copy);
            }

            // Nothing to back up: an error, and no database made.
            Environment.ExitCode = 0;
            var empty = Path.Combine(data, "empty");
            Directory.CreateDirectory(empty);
            RunServer("--backup", backups, $"--LookingGlass:DataDirectory={empty}");
            Assert.Equal(1, Environment.ExitCode);
            Assert.False(File.Exists(Path.Combine(empty, "lookingglass.db")));
            Database.ReleasePooledConnections(path);
        } finally {
            Environment.ExitCode = exitCode;
            ExitCodeGate.Release();
            DeleteDirectory(data);
        }
    }

    /// <summary>
    /// The server never logs a secret: not device tokens, registration codes, key login challenges or signatures, keys,
    /// or anything of a message. (It never sees a message's text, and its log isn't the place for its ciphertext.)
    /// </summary>
    [Fact]
    public async Task NothingSecretIsLogged() {
        var logs = new CapturingLoggerProvider();
        var lodestone = new FakeLodestone { Name = "Alice Logged", World = "Gilgamesh" };
        await using var server = new Harness(lodestone: lodestone, logs: logs, settings: [
            ("Logging:LogLevel:Default", "Trace"),
            ("Logging:LogLevel:LookingGlass", "Trace"),
            ("LookingGlass:PublicUrls:0", "ws://localhost/ws"),
        ]);
        try {
            var alice = server.StartClient("Alice Logged");
            await WaitFor(() => alice.Session.Snapshot.State == ConnectionState.Unregistered ? new object() : null);
            var challenge = await alice.Session.StartRegistrationAsync(new Character { Name = "Alice Logged", WorldName = "Gilgamesh" }, Ct);
            lodestone.Profile = challenge.Code;
            await alice.Session.CompleteRegistrationAsync(Ct);
            await WaitFor(() => alice.Session.Snapshot.State == ConnectionState.Ready ? new object() : null);
            var firstToken = alice.Store.Load().DeviceToken!;

            // A key login, for its challenge and signature.
            await alice.Session.DisposeAsync();
            server.ExecuteSql("DELETE FROM devices WHERE user_id = $id;", ("$id", alice.UserId));
            alice = await server.RestartAsync(alice);
            var secondToken = alice.Store.Load().DeviceToken!;
            Assert.NotEqual(firstToken, secondToken);

            var bob = await server.RegisterAsync("Bob Logged");
            var channelId = await alice.Session.CreateChannelAsync("Logged Channel", Ct);
            await AddMemberAsync(alice, channelId, bob);
            await alice.Session.SendTextAsync(channelId, "a very private message", Ct);
            await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == "a very private message"));

            var secrets = alice.Store.Load();
            var forbidden = new List<string> {
                firstToken, secondToken, challenge.Code, challenge.Code.ToLowerInvariant(), "a very private message", "Logged Channel",
                Convert.ToBase64String(secrets.SigningPrivateKey!), Convert.ToHexString(secrets.SigningPrivateKey!),
                Convert.ToBase64String(alice.LoadEpochKey(channelId, alice.Session.Snapshot.FindChannel(channelId)!.Epoch)),
                Convert.ToBase64String(RequestHandlerHash(firstToken)), Convert.ToHexString(RequestHandlerHash(firstToken)),
            };

            var text = string.Join("\n", logs.Entries.Select(entry => entry.Message));
            Assert.NotEmpty(logs.Entries);
            foreach (var secret in forbidden) {
                Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
            }
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
    }

    /// <summary>New connections per address are limited per minute too, not only how many are open at once.</summary>
    [Fact]
    public async Task NewConnectionsFromOneAddressAreLimitedPerMinute() {
        await using var server = new Harness(settings: ("LookingGlass:Limits:ConnectionsPerMinutePerIp", "3"));
        try {
            for (var i = 0; i < 3; i++) {
                await using var raw = await server.ConnectRawAsync(remoteAddress: "203.0.113.60");
            }

            await Assert.ThrowsAnyAsync<Exception>(() => server.ConnectRawAsync(remoteAddress: "203.0.113.60"));
            await using var other = await server.ConnectRawAsync(remoteAddress: "203.0.113.61");
            // Behind a proxy on this machine (Tailscale Funnel, Caddy), the client's own address is what counts.
            for (var i = 0; i < 3; i++) {
                await using var proxied = await server.ConnectRawAsync(remoteAddress: "127.0.0.1", forwardedFor: ("198.51.100.62", "https"));
            }

            await Assert.ThrowsAnyAsync<Exception>(() => server.ConnectRawAsync(remoteAddress: "127.0.0.1", forwardedFor: ("198.51.100.62", "https")));
            await using var neighbour = await server.ConnectRawAsync(remoteAddress: "127.0.0.1", forwardedFor: ("198.51.100.63", "https"));
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
    }

    /// <summary>
    /// A connection sending requests faster than its allowance (here 5 at once, then 50 a second) is slowed down, not refused:
    /// every request is answered, the later ones only when due.
    /// </summary>
    [Fact]
    public async Task ABusyConnectionIsSlowedDownNotRefused() {
        var frames = Enumerable.Range(1, 15).Select(i => new ClientFrame { RequestId = (uint) i, Ping = new Ping() }.ToByteArray()).ToList();
        var connection = new LookingGlass.Server.Realtime.ClientConnection(new ScriptedWebSocket(frames), "203.0.113.70", 1024, 64,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, requestsPerSecond: 50, requestBurst: 5);
        var handled = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await connection.RunAsync((_, _, _) => {
            Interlocked.Increment(ref handled);
            return Task.FromResult(new Response { Pong = new Pong() });
        }).WaitAsync(Harness.Timeout, Ct);

        Assert.Equal(15, handled);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(150), $"15 requests took only {clock.Elapsed.TotalMilliseconds:0} ms");
    }

    /// <summary>A socket that delivers the given frames, then closes.</summary>
    private sealed class ScriptedWebSocket(List<byte[]> frames) : WebSocket {
        private int _next;

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

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) {
            if (this._next >= frames.Count) {
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, null));
            }

            var frame = frames[this._next++];
            frame.CopyTo(buffer.Array!, buffer.Offset);
            return Task.FromResult(new WebSocketReceiveResult(frame.Length, WebSocketMessageType.Binary, true));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static byte[] RequestHandlerHash(string token) => LookingGlass.Server.Realtime.RequestHandler.HashToken(token);

    private static object? Scalar(SqliteConnection connection, string sql) {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    /// <summary>Runs the server's entry point, as the command line would.</summary>
    private static void RunServer(params string[] args) {
        var entry = typeof(Program).Assembly.EntryPoint!;
        var result = entry.Invoke(null, [args]);
        if (result is Task task) {
            task.GetAwaiter().GetResult();
        }
    }
}
