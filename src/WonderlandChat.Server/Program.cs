using Microsoft.Extensions.Options;
using WonderlandChat.Protocol;
using WonderlandChat.Server;
using WonderlandChat.Server.Data;
using WonderlandChat.Server.Hosting;
using WonderlandChat.Server.Realtime;
using WonderlandChat.Server.Services;

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
    return new Database(Path.Combine(options.DataDirectory, "wonderlandchat.db"));
});
builder.Services.AddSingleton<ConnectionRegistry>();
builder.Services.AddSingleton<RequestHandler>();
builder.Services.AddHttpClient<LodestoneClient>(client => {
    client.DefaultRequestHeaders.UserAgent.ParseAdd("WonderlandChat/0.1 (+character verification)");
    client.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddHostedService<EchoBotHost>();

var app = builder.Build();
var options = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value;

// Open the database now so migration errors stop startup instead of the first request.
app.Services.GetRequiredService<Database>();

if (options.Dev.AllowDebugAccounts) {
    app.Logger.LogWarning("Debug accounts are ENABLED. Anyone can register a fake character on world \"{World}\". Never do this on a public server.", ProtocolInfo.DebugWorldName);
}

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.MapGet("/health", (ConnectionRegistry registry) => Results.Ok(new { status = "ok", online = registry.OnlineCount }));

app.Map(ProtocolInfo.WebSocketPath, async (HttpContext context, RequestHandler handler, ConnectionRegistry registry, ILoggerFactory loggers) => {
    if (!context.WebSockets.IsWebSocketRequest) {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("WonderlandChat WebSocket endpoint.");
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var connection = new ClientConnection(socket, address, (int) handler.Limits.MaxFrameBytes, options.Limits.SendQueueLength,
        handler.Limits, loggers.CreateLogger<ClientConnection>());

    try {
        await connection.RunAsync(handler.HandleAsync);
    } finally {
        if (connection.User != null) {
            registry.SetOffline(connection.User.UserId, connection);
        }
    }
});

app.Run();

/// <summary>Exposed for integration tests.</summary>
public partial class Program;
