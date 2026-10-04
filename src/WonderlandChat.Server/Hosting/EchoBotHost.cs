using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using WonderlandChat.Core.Client;
using WonderlandChat.Core.Debug;
using WonderlandChat.Protocol;

namespace WonderlandChat.Server.Hosting;

/// <summary>Runs the echo bot inside the server when <c>Dev:HostEchoBot</c> is on.</summary>
public sealed class EchoBotHost(
    IOptions<ServerOptions> options,
    IServer server,
    IHostApplicationLifetime lifetime,
    ILogger<EchoBotHost> logger) : IHostedService {
    private EchoBot? _bot;

    public Task StartAsync(CancellationToken cancellationToken) {
        var dev = options.Value.Dev;
        if (!dev.HostEchoBot) {
            return Task.CompletedTask;
        }

        if (!dev.AllowDebugAccounts) {
            logger.LogWarning("Dev:HostEchoBot needs Dev:AllowDebugAccounts; the echo bot won't start.");
            return Task.CompletedTask;
        }

        // Wait for Kestrel to bind so we know our own address.
        lifetime.ApplicationStarted.Register(() => {
            try {
                var uri = this.ResolveServerUri(dev.EchoBotServerUrl);
                var store = new FileSecretStore(Path.Combine(options.Value.DataDirectory, "echo-bot-secrets.json"));
                this._bot = new EchoBot(new ClientSessionOptions { ServerUri = uri, ClientVersion = "echo-bot" }, store, dev.EchoBotName,
                    line => logger.LogInformation("[echo bot] {Line}", line));
                this._bot.Start();
                logger.LogInformation("Echo bot connecting to {Uri}. Invite it as \"{Name}\" on world \"{World}\".", uri, dev.EchoBotName, ProtocolInfo.DebugWorldName);
            } catch (Exception ex) {
                logger.LogError(ex, "Couldn't start the echo bot");
            }
        });

        return Task.CompletedTask;
    }

    private Uri ResolveServerUri(string configured) {
        if (!string.IsNullOrWhiteSpace(configured)) {
            return new Uri(configured);
        }

        var address = server.Features.Get<IServerAddressesFeature>()?.Addresses
            .FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("No http:// address to connect the echo bot to; set Dev:EchoBotServerUrl.");

        var builder = new UriBuilder(address.Replace("://+", "://localhost").Replace("://*", "://localhost")) {
            Scheme = "ws",
            Path = ProtocolInfo.WebSocketPath,
        };
        if (builder.Host is "0.0.0.0" or "[::]" or "::") {
            builder.Host = "127.0.0.1";
        }

        return builder.Uri;
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        if (this._bot != null) {
            await this._bot.DisposeAsync();
        }
    }
}
