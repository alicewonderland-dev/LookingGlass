using System.Net.WebSockets;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using LookingGlass.Core.Client;
using LookingGlass.Core.Crypto;
using LookingGlass.Core.Membership;
using LookingGlass.Protocol;
using LookingGlass.Server;
using LookingGlass.Server.Data;
using LookingGlass.Server.Hosting;
using LookingGlass.Server.Realtime;
using LookingGlass.Server.Services;

// Config files and relative paths resolve against the install folder, not the
// current directory, so the server behaves the same however it is launched
// (systemd, Windows service, Docker, or by hand).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

string DatabasePath(ServerOptions options) =>
    Path.Combine(Path.GetFullPath(options.DataDirectory, builder.Environment.ContentRootPath), "lookingglass.db");

// "--backup <file or folder> [--keep N]": an online backup of the configured database, and no server (nothing listens).
BackupCommand.Request? backup;
try {
    backup = BackupCommand.Parse(args);
} catch (ArgumentException ex) {
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 2;
    return;
}

if (backup != null) {
    var configured = builder.Configuration.GetSection(ServerOptions.Section).Get<ServerOptions>() ?? new ServerOptions();
    Environment.ExitCode = BackupCommand.Run(backup, DatabasePath(configured), Console.Out, Console.Error);
    return;
}

// The web server's limits (connections, header sizes and timeouts), from Kestrel:Limits in appsettings.json, which
// Kestrel doesn't read by itself: see "Limits worth knowing" in docs/server.md.
builder.WebHost.ConfigureKestrel((context, kestrel) => context.Configuration.GetSection("Kestrel:Limits").Bind(kestrel.Limits));

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.Section));
builder.Services.PostConfigure<ServerOptions>(options => {
    options.DataDirectory = Path.GetFullPath(options.DataDirectory, builder.Environment.ContentRootPath);
});
builder.Services.AddSingleton(services => {
    var options = services.GetRequiredService<IOptions<ServerOptions>>().Value;
    Directory.CreateDirectory(options.DataDirectory);
    return new Database(DatabasePath(options), services.GetRequiredService<ILogger<Database>>());
});
// The membership and group-key layers, behind interfaces so MLS can replace them later.
builder.Services.AddSingleton<IMembershipProvider>(SignedLogMembershipProvider.Instance);
builder.Services.AddSingleton<IGroupKeyProvider>(SealedEpochKeyProvider.Instance);
builder.Services.AddSingleton<ConnectionRegistry>();
builder.Services.AddSingleton<RequestHandler>();
builder.Services.AddHttpClient<LodestoneClient>(client => {
    client.DefaultRequestHeaders.UserAgent.ParseAdd("LookingGlass/0.1 (+character verification)");
    client.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddHostedService<EchoBotHost>();
builder.Services.AddHostedService<DatabaseMaintenance>();
builder.Services.AddSingleton<MessageSweeper>();
builder.Services.AddHostedService(services => services.GetRequiredService<MessageSweeper>());

// Behind a reverse proxy, take the client address from X-Forwarded-For so
// per-IP limits apply to real clients. Only proxies on this machine are
// trusted by default; add others (addresses or CIDR networks) under
// LookingGlass:TrustedProxies.
builder.Services.Configure<ForwardedHeadersOptions>(forwarded => {
    forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    ClientAddresses.AddTrustedProxies(forwarded, builder.Configuration.GetSection("LookingGlass:TrustedProxies").Get<string[]>() ?? []);
});

var app = builder.Build();
var options = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value;

// Open the database now so migration errors stop startup instead of the first request.
try {
    app.Services.GetRequiredService<Database>();
} catch (UnsupportedDatabaseException ex) {
    app.Logger.LogCritical("{Problem}", ex.Message);
    Environment.ExitCode = 1;
    return;
}

// A registration challenge's lifetime bounds how long its Lodestone code can be held open (see LodestoneCode).
if (options.Lodestone.ChallengeMinutes is < LodestoneOptions.MinChallengeMinutes or > LodestoneOptions.MaxChallengeMinutes) {
    app.Logger.LogCritical(
        "LookingGlass:Lodestone:ChallengeMinutes is {Minutes}, so the server won't start: it must be {Min} to {Max} (minutes a registration challenge lasts; 15 by default).",
        options.Lodestone.ChallengeMinutes, LodestoneOptions.MinChallengeMinutes, LodestoneOptions.MaxChallengeMinutes);
    Environment.ExitCode = 1;
    return;
}

// How long, and how many, relayed messages are kept for members who were away.
if (options.Messages.Problem() is { } messageSettings) {
    app.Logger.LogCritical("{Problem}", messageSettings);
    Environment.ExitCode = 1;
    return;
}

// Debug accounts let anyone who can reach the server register, or take over, any debug account (the echo bot needs them).
// Outside Development that is almost certainly a mistake, such as a test server's settings copied to a public one.
var debugSettings = new[] { (Name: "AllowDebugAccounts", On: options.Dev.AllowDebugAccounts), (Name: "HostEchoBot", On: options.Dev.HostEchoBot) }
    .Where(setting => setting.On).Select(setting => $"LookingGlass:Dev:{setting.Name}").ToList();
if (!app.Environment.IsDevelopment() && debugSettings.Count > 0) {
    if (!options.Dev.AllowOutsideDevelopment) {
        app.Logger.LogCritical(
            "{Settings} is on in {Environment}, so the server won't start: with debug accounts, anyone who can reach the server can register or take " +
            "over any debug account, channels and all. Turn it off for a public server. For a private test server, run in Development " +
            "(ASPNETCORE_ENVIRONMENT=Development), or set LookingGlass:Dev:AllowOutsideDevelopment=true if you really mean it here.",
            string.Join(" and ", debugSettings), app.Environment.EnvironmentName);
        Environment.ExitCode = 1;
        return;
    }

    app.Logger.LogWarning(
        "Debug accounts are ENABLED in {Environment} ({Settings}, allowed by LookingGlass:Dev:AllowOutsideDevelopment). Anyone who can reach this " +
        "server can register or take over any debug account on world \"{World}\". Never do this on a public server.",
        app.Environment.EnvironmentName, string.Join(", ", debugSettings), ProtocolInfo.DebugWorldName);
}

// Likewise for the key login addresses: a typo would otherwise only show as every key login failing.
IReadOnlyList<ServerOrigin> publicOrigins;
try {
    publicOrigins = RequestHandler.ParsePublicUrls(options.PublicUrls);
} catch (InvalidOperationException ex) {
    app.Logger.LogCritical("{Problem}", ex.Message);
    Environment.ExitCode = 1;
    return;
}

// Registrations, key logins and retirements are signed for the server's address, and only an address the operator
// configured can be trusted: the Host header is whatever the connecting side sends, so a relaying server sends the one
// the user signed for. Outside Development there is no starting without them, as with an unusable database.
var sharedAddresses = 0;
switch (RequestHandler.ChooseKeyLoginOrigins(publicOrigins, app.Environment.IsDevelopment())) {
    case RequestHandler.KeyLoginOrigins.PublicUrls:
        app.Logger.LogInformation("Registrations, key logins and identity resets are accepted when signed for {Origins}", string.Join(", ", publicOrigins));
        // The checks tell this server from another by address alone, so each listed address must be this server's only.
        foreach (var url in options.PublicUrls.Where(url => !string.IsNullOrWhiteSpace(url)).Select(url => url.Trim()).Distinct()) {
            if (RequestHandler.WhyNotUnique(ServerOrigin.FromUrl(url)!) is { Count: > 0 } reasons) {
                sharedAddresses++;
                app.Logger.LogWarning(
                    "LookingGlass:PublicUrls lists {Url}, which may not be this server's alone: {Reasons}. Registrations, key logins, identity resets and " +
                    "Lodestone codes are bound to the address a client connected to, so a server that users also reach under this address (say, a machine " +
                    "with the same short name on another tailnet or LAN) could pass them on here, and take over accounts. Fine on a private network you " +
                    "control; for a server other people use, list a wss:// address with a fully qualified name (such as wss://<machine>.<tailnet>.ts.net/ws).",
                    url, string.Join("; ", reasons));
            }
        }

        break;
    case RequestHandler.KeyLoginOrigins.HostHeader:
        app.Logger.LogWarning(
            "LookingGlass:PublicUrls is not set. In Development, registrations, key logins and identity resets are accepted when signed for " +
            "whatever address each connection names in its Host header, which a relaying server chooses, so the address check stops nothing: " +
            "a malicious server a user also registers on could pass the registration on and get a login to their account here. " +
            "Fine for a private test server; list the addresses clients use in LookingGlass:PublicUrls to check them properly.");
        break;
    default:
        app.Logger.LogCritical("{Problem}", RequestHandler.PublicUrlsRequired);
        Environment.ExitCode = 1;
        return;
}

if (options.Dev.AllowDebugAccounts && app.Environment.IsDevelopment()) {
    app.Logger.LogWarning("Debug accounts are ENABLED. Anyone can register a fake character on world \"{World}\". Never do this on a public server.", ProtocolInfo.DebugWorldName);
}

// One line saying how this server is set up, for the operator to check after every start or update.
app.Logger.LogInformation(
    "LookingGlass server {Version} in {Environment}: debug accounts {DebugAccounts}, echo bot {EchoBot}; {Messages}; {Addresses}; database {Database}",
    RequestHandler.ServerVersion, app.Environment.EnvironmentName, options.Dev.AllowDebugAccounts ? "ON" : "off",
    options.Dev.HostEchoBot && options.Dev.AllowDebugAccounts ? "ON" : "off",
    options.Messages.Enabled
        ? $"messages kept {options.Messages.KeepDays} days, at most {options.Messages.MaxPerChannel} per channel, for members who were away"
        : "no messages kept",
    publicOrigins.Count == 0 ? "no PublicUrls (going by each connection's Host header)"
    : sharedAddresses == 0 ? $"every address is wss:// with a fully qualified name ({publicOrigins.Count})"
    : $"{sharedAddresses} of {publicOrigins.Count} addresses may not be this server's alone (see the warnings above)",
    app.Services.GetRequiredService<Database>().FilePath);

app.UseForwardedHeaders();
app.UseWebSockets(new WebSocketOptions {
    KeepAliveInterval = TimeSpan.FromSeconds(30),
    // A client that doesn't answer a ping within this is gone (a dropped network says nothing): its connection closes,
    // and the user shows as offline, rather than lingering until the operating system gives up on it.
    KeepAliveTimeout = TimeSpan.FromSeconds(60),
});

// Which connections the server takes, per address and in all (see ConnectionGate), and a quiet 503 when it is full.
var gate = new ConnectionGate(options.Limits.MaxConnections, options.Limits.ConnectionsPerIp, options.Limits.NotLoggedInConnectionsPerIp,
    options.Limits.ConnectionsPerMinutePerIp);
var acceptor = new WebSocketAcceptor(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<WebSocketAcceptor>());

// Whether the server is up, and its version: nothing about who uses it.
app.MapGet("/health", () => Results.Ok(new { status = "ok", version = RequestHandler.ServerVersion }));

app.Map(ProtocolInfo.WebSocketPath, async (HttpContext context, RequestHandler handler, ConnectionRegistry registry, ILoggerFactory loggers) => {
    if (!context.WebSockets.IsWebSocketRequest) {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("LookingGlass WebSocket endpoint.");
        return;
    }

    var (ticket, refusal) = gate.TryAdmit(ClientAddresses.ConnectionLimitKey(context.Connection.RemoteIpAddress));
    if (ticket == null) {
        if (refusal == ConnectionRefusal.ServerFull) {
            acceptor.RefuseFull(context);
        } else {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        }

        return;
    }

    using (ticket) {
        using var socket = await acceptor.AcceptAsync(context);
        if (socket == null) {
            return;
        }

        // Per-address request limits (registration, key login) count IPv6 per /64.
        var connection = new ClientConnection(socket, ClientAddresses.LimitKey(context.Connection.RemoteIpAddress), (int) handler.Limits.MaxFrameBytes,
            options.Limits.SendQueueLength, loggers.CreateLogger<ClientConnection>(), options.Limits.RequestsPerSecondPerConnection,
            options.Limits.RequestBurstPerConnection, TimeSpan.FromSeconds(Math.Max(1, options.Limits.NotLoggedInSeconds))) {
            // As the client addressed it: the Host header, and the scheme (https behind a trusted TLS proxy, from X-Forwarded-Proto).
            RequestOrigin = ServerOrigin.FromRequest(context.Request.Scheme, context.Request.Host.Value),
        };
        // At the cap, the oldest connection that hasn't logged in (and isn't registering, if any isn't) makes room for a new one.
        ticket.Attach(() => connection.PendingRegistration != null, () => connection.Abort("Server busy", WebSocketCloseStatus.EndpointUnavailable));

        // Stopping (systemd sends SIGTERM) closes every connection at once, saying the server is going away, so clients
        // reconnect later and the server stops without waiting out its shutdown timeout.
        using var stopping = app.Lifetime.ApplicationStopping.Register(() => connection.Abort("Server shutting down", WebSocketCloseStatus.EndpointUnavailable));
        try {
            await connection.RunAsync(handler.HandleAsync, (c, response) => {
                // Logged in (or out again) by the request just answered.
                ticket.SetLoggedIn(c.User != null);
                registry.Respond(c, response);
            });
        } finally {
            if (connection.User != null) {
                registry.SetOffline(connection.User.UserId, connection);
            }
        }
    }
});

app.Run();

/// <summary>Exposed for integration tests.</summary>
public partial class Program;
