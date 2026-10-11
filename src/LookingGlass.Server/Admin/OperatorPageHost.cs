using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Options;

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
/// </summary>
public sealed class OperatorPageHost(OperatorPage page, IOptions<ServerOptions> options, ILoggerFactory loggers, IHostEnvironment environment, IServiceProvider services)
    : IHostedService, IAsyncDisposable {
    private readonly AdminOptions _admin = options.Value.Admin;
    private readonly ILogger _logger = loggers.CreateLogger<OperatorPageHost>();
    private WebApplication? _app;

    /// <summary>The page's host, once started (for tests: its server).</summary>
    internal WebApplication? App => this._app;

    public async Task StartAsync(CancellationToken cancellationToken) {
        if (!this._admin.On) {
            return;
        }

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions {
            ApplicationName = typeof(OperatorPageHost).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = environment.EnvironmentName,
        });
        // Logged where the server logs; the factory stays the server's (this host doesn't dispose it).
        builder.Services.AddSingleton(loggers);
        builder.Services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        var socket = this._admin.SocketPath;
        if (services.GetService<OperatorPageListener>() is { } listener) {
            listener.Configure(builder.WebHost);
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
                ClearStaleSocket(socket);
            }
        }

        var app = builder.Build();
        app.Run(page.HandleAsync);
        await app.StartAsync(cancellationToken);
        this._app = app;
        if (socket != null && services.GetService<OperatorPageListener>() == null && !OperatingSystem.IsWindows()) {
            // Only the service's user (and root, which tailscaled runs as) may open it, whatever the umask (the unit's UMask=0077
            // and RuntimeDirectoryMode=0700 do the same).
            File.SetUnixFileMode(socket, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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
    /// A socket file left by a server that didn't stop cleanly (without systemd, which empties /run/lookingglass when the service
    /// stops): deleted, so the page can listen there again; unless something answers on it (another server running), which is refused.
    /// </summary>
    private static void ClearStaleSocket(string path) {
        if (!File.Exists(path)) {
            return;
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

    /// <summary>The server the page's host runs on, for tests.</summary>
    internal IServer? Server => this._app?.Services.GetService<IServer>();
}
