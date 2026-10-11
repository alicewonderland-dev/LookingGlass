using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Options;
using LookingGlass.Server.Services;

namespace LookingGlass.Server.Admin;

/// <summary>
/// For tests: sets up the operator web page's host instead of its own listener (with a test server, say). Registered in the
/// server's services, it replaces the Unix socket or loopback port.
/// </summary>
public sealed record OperatorPageListener(Action<IWebHostBuilder> Configure);

/// <summary>
/// The operator web page's own web host, inside the server process (see "How it is reached" in docs/operator-tools-plan.md):
/// a second, small host with only the page (<see cref="OperatorPage"/>), on a Unix socket only the service's user and root can
/// open, or on a loopback port. The public host has no route to the page, and this one has no <c>/ws</c>, no <c>/health</c>,
/// nothing else, so nothing in front of the public server can reach the page however it is set up. Off unless
/// <c>LookingGlass:Admin:Listen</c> is set (checked at startup: see <see cref="AdminOptions.Problem"/>).
/// <para>
/// If the page can't start where its settings say (the socket in use, its folder not the service's own or open to others, the
/// port taken), the chat server runs on without it (the owner's choice, 2026-10-11): an error in the log, and an urgent alert.
/// Settings that are wrong in themselves still stop the server at startup.
/// </para>
/// </summary>
public sealed class OperatorPageHost(OperatorPage page, IOptions<ServerOptions> options, ILoggerFactory loggers, IHostEnvironment environment, IServiceProvider services,
    ServerAlerts alerts) : IHostedService, IAsyncDisposable {
    private readonly AdminOptions _admin = options.Value.Admin;
    private readonly ILogger _logger = loggers.CreateLogger<OperatorPageHost>();
    private WebApplication? _app;

    /// <summary>The page's host, once started (for tests: its server).</summary>
    internal WebApplication? App => this._app;

    /// <summary>The server the page's host runs on, for tests.</summary>
    internal IServer? Server => this._app?.Services.GetService<IServer>();

    /// <summary>Why the page didn't start, if it didn't.</summary>
    internal string? Failed { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken) {
        if (!this._admin.On) {
            return;
        }

        try {
            await this.StartPageAsync(cancellationToken);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            this.Failed = ex.Message;
            if (this._app != null) {
                await this._app.DisposeAsync();
                this._app = null;
            }

            this._logger.LogError(ex, "The operator web page didn't start ({Why}); the chat server runs on without it. Fix that, then restart " +
                                      "(see \"The operator web page\" in docs/server.md)", Services.VisibleText.Of(ex.Message, 300));
            alerts.PageFailed(ex.Message);
        }
    }

    private async Task StartPageAsync(CancellationToken cancellationToken) {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions {
            ApplicationName = typeof(OperatorPageHost).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = environment.EnvironmentName,
        });
        // Logged where the server logs; the factory stays the server's (this host doesn't dispose it).
        builder.Services.AddSingleton(loggers);
        builder.Services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        // The server's own host has the process's lifetime (Ctrl+C, SIGTERM, "Application started"); this one only follows it.
        builder.Services.AddSingleton<IHostLifetime, FollowingLifetime>();
        var socket = this._admin.SocketPath;
        var test = services.GetService<OperatorPageListener>();
        if (test != null) {
            test.Configure(builder.WebHost);
        } else {
            builder.WebHost.UseKestrelCore();
            builder.WebHost.ConfigureKestrel(kestrel => {
                kestrel.AddServerHeader = false;
                // A page, a stylesheet and small forms, for one operator: small limits.
                kestrel.Limits.MaxConcurrentConnections = 64;
                kestrel.Limits.MaxRequestBodySize = 16 * 1024;
                kestrel.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
                kestrel.Limits.MaxRequestLineSize = 8 * 1024;
                kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
                kestrel.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(1);
                if (socket != null) {
                    kestrel.ListenUnixSocket(socket);
                } else {
                    kestrel.Listen(this._admin.LoopbackEndPoint!);
                }
            });
            if (socket != null) {
                CheckFolder(socket);
                ClearStaleSocket(socket);
            }
        }

        var app = builder.Build();
        app.Run(page.HandleAsync);
        this._app = app;
        if (socket != null && test == null && OperatingSystem.IsLinux()) {
            // Made 0600 as it is made (the umask is the process's, so only for the moment of listening), and set so again
            // after: only the service's user (and root, which tailscaled runs as) may open it.
            var before = UnixFiles.SetUmask(0x7F);
            try {
                await app.StartAsync(cancellationToken);
            } finally {
                UnixFiles.SetUmask(before);
            }

            File.SetUnixFileMode(socket, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        } else {
            await app.StartAsync(cancellationToken);
        }

        this._logger.LogInformation("The operator web page listens on {Listen}, for {Url} (reached through tailscale serve only)",
            Services.VisibleText.Of(this._admin.Listen.Trim()), this._admin.PageLink);
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        if (this._app != null) {
            await this._app.StopAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync() {
        if (this._app != null) {
            await this._app.DisposeAsync();
            this._app = null;
        }
    }

    /// <summary>
    /// On Linux, the socket's folder must be a folder (not a link to one) owned by the service's user, which nobody else may
    /// write in: otherwise someone else could swap the socket for their own.
    /// </summary>
    private static void CheckFolder(string socket) {
        if (!OperatingSystem.IsLinux()) {
            return;
        }

        var folder = Path.GetDirectoryName(socket)!;
        UnixFiles.Stat stat;
        try {
            stat = UnixFiles.Lstat(folder) ?? throw new InvalidOperationException($"The folder {folder} doesn't exist.");
        } catch (IOException) {
            // No statx (an old or unusual C library): at least the permissions .NET can read.
            if ((File.GetUnixFileMode(folder) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0) {
                throw new InvalidOperationException($"Others can write in {folder}, the operator web page's socket's folder, so it doesn't start there.");
            }

            return;
        }

        if (!stat.IsDirectory) {
            throw new InvalidOperationException($"{folder}, the operator web page's socket's folder, isn't a folder (a link to one, say), so the page doesn't start there.");
        }

        if (stat.Owner != UnixFiles.EffectiveUser()) {
            throw new InvalidOperationException($"{folder}, the operator web page's socket's folder, belongs to another user than the server's, so the page " +
                                                "doesn't start there (the unit's RuntimeDirectory=lookingglass makes the right one).");
        }

        if ((stat.Permissions & 0x12) != 0) {
            throw new InvalidOperationException($"Others can write in {folder}, the operator web page's socket's folder, so the page doesn't start there " +
                                                "(the unit's RuntimeDirectoryMode=0700 makes it private).");
        }
    }

    /// <summary>
    /// A socket left by a server that didn't stop cleanly (without systemd, which empties /run/lookingglass when the service stops):
    /// deleted, so the page can listen there again; unless something answers on it (another server running). Anything else in its
    /// place (a file, a link, which is never followed) is left alone, and the page doesn't start.
    /// </summary>
    private static void ClearStaleSocket(string path) {
        bool socket;
        if (OperatingSystem.IsLinux()) {
            UnixFiles.Stat? stat;
            try {
                stat = UnixFiles.Lstat(path);
            } catch (IOException) {
                // No statx: what .NET can tell. A socket is an empty file that isn't a link.
                var info = new FileInfo(path);
                stat = !info.Exists && info.LinkTarget == null ? null
                    : new UnixFiles.Stat(info.LinkTarget != null ? 0xA000u : info.Length == 0 ? 0xC000u : 0x8000u, 0, 0);
            }

            if (stat == null) {
                return;
            }

            socket = stat.Value.IsSocket;
        } else {
            // Windows: a Unix socket is a reparse point that isn't a link.
            var info = new FileInfo(path);
            if (!info.Exists) {
                return;
            }

            socket = info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.LinkTarget == null;
        }

        if (!socket) {
            throw new InvalidOperationException($"{path} is there already and isn't a socket, so the operator web page doesn't replace it: move it away, or choose another path.");
        }

        try {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            probe.Connect(new UnixDomainSocketEndPoint(path));
        } catch (SocketException) {
            // Nothing listens: stale.
            File.Delete(path);
            return;
        }

        throw new InvalidOperationException($"Something is listening on {path} already (another LookingGlass server?), so the operator web page can't.");
    }

    /// <summary>The page's host's lifetime: nothing of its own (no signal handlers, no "Application started"); the server's host stops it.</summary>
    private sealed class FollowingLifetime : IHostLifetime {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
