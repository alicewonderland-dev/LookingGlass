using System.Collections.Concurrent;
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

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.Section));
builder.Services.PostConfigure<ServerOptions>(options => {
    options.DataDirectory = Path.GetFullPath(options.DataDirectory, builder.Environment.ContentRootPath);
});
builder.Services.AddSingleton(services => {
    var options = services.GetRequiredService<IOptions<ServerOptions>>().Value;
    Directory.CreateDirectory(options.DataDirectory);
    return new Database(Path.Combine(options.DataDirectory, "lookingglass.db"), services.GetRequiredService<ILogger<Database>>());
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
switch (RequestHandler.ChooseKeyLoginOrigins(publicOrigins, app.Environment.IsDevelopment())) {
    case RequestHandler.KeyLoginOrigins.PublicUrls:
        app.Logger.LogInformation("Registrations, key logins and identity resets are accepted when signed for {Origins}", string.Join(", ", publicOrigins));
        // The checks tell this server from another by address alone, so each listed address must be this server's only.
        foreach (var url in options.PublicUrls.Where(url => !string.IsNullOrWhiteSpace(url)).Select(url => url.Trim()).Distinct()) {
            if (RequestHandler.WhyNotUnique(ServerOrigin.FromUrl(url)!) is { Count: > 0 } reasons) {
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

if (options.Dev.AllowDebugAccounts) {
    app.Logger.LogWarning("Debug accounts are ENABLED. Anyone can register a fake character on world \"{World}\". Never do this on a public server.", ProtocolInfo.DebugWorldName);
}

app.UseForwardedHeaders();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

var connectionsPerAddress = new ConcurrentDictionary<string, int>();

app.MapGet("/health", (ConnectionRegistry registry) => Results.Ok(new { status = "ok", online = registry.OnlineCount }));

app.Map(ProtocolInfo.WebSocketPath, async (HttpContext context, RequestHandler handler, ConnectionRegistry registry, ILoggerFactory loggers) => {
    if (!context.WebSockets.IsWebSocketRequest) {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("LookingGlass WebSocket endpoint.");
        return;
    }

    var address = ClientAddresses.LimitKey(context.Connection.RemoteIpAddress);
    if (connectionsPerAddress.AddOrUpdate(address, 1, (_, count) => count + 1) > options.Limits.ConnectionsPerIp) {
        connectionsPerAddress.AddOrUpdate(address, 0, (_, count) => count - 1);
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        return;
    }

    try {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var connection = new ClientConnection(socket, address, (int) handler.Limits.MaxFrameBytes, options.Limits.SendQueueLength,
            loggers.CreateLogger<ClientConnection>()) {
            // As the client addressed it: the Host header, and the scheme (https behind a trusted TLS proxy, from X-Forwarded-Proto).
            RequestOrigin = ServerOrigin.FromRequest(context.Request.Scheme, context.Request.Host.Value),
        };

        try {
            await connection.RunAsync(handler.HandleAsync, registry.Respond);
        } finally {
            if (connection.User != null) {
                registry.SetOffline(connection.User.UserId, connection);
            }
        }
    } finally {
        if (connectionsPerAddress.AddOrUpdate(address, 0, (_, count) => count - 1) <= 0) {
            connectionsPerAddress.TryRemove(new KeyValuePair<string, int>(address, 0));
        }
    }
});

app.Run();

/// <summary>Exposed for integration tests.</summary>
public partial class Program;
