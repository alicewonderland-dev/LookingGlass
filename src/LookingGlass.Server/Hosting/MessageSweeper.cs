using Microsoft.Extensions.Options;
using LookingGlass.Server.Data;

namespace LookingGlass.Server.Hosting;

/// <summary>
/// Deletes the stored messages (message catch-up) that are due to go (see <see cref="Database.SweepMessages"/>): older than
/// <see cref="MessageOptions.KeepDays"/>, past <see cref="MessageOptions.MaxPerChannel"/> in their channel, or all of them if
/// keeping messages is off. Once when the server starts (so lowered settings apply at once), then every
/// <see cref="Interval"/>. The per-channel cap is also kept as each message is stored, so between sweeps a channel holds at
/// most that many, and a message at most about ten minutes longer than the days set.
/// </summary>
public sealed class MessageSweeper(Database db, IOptions<ServerOptions> options, ILogger<MessageSweeper> logger, TimeProvider? time = null) : BackgroundService {
    /// <summary>How often stored messages are swept.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public override Task StartAsync(CancellationToken cancellationToken) {
        // Before the server takes connections, so nobody is sent what the settings say is gone.
        this.Sweep("Startup");
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        using var timer = new PeriodicTimer(Interval, this._time);
        try {
            while (await timer.WaitForNextTickAsync(stoppingToken)) {
                this.Sweep("Periodic");
            }
        } catch (OperationCanceledException) {
            // Stopping.
        }
    }

    /// <summary>Sweeps now.</summary>
    /// <returns>How many messages went.</returns>
    internal int Sweep(string when) {
        try {
            var settings = options.Value.Messages;
            var deleted = db.SweepMessages(this._time.GetUtcNow().ToUnixTimeMilliseconds(), settings.KeepDays, settings.MaxPerChannel);
            if (deleted > 0) {
                logger.LogDebug("{When} sweep: {Count} stored messages deleted", when, deleted);
            }

            return deleted;
        } catch (Exception ex) {
            // Tried again at the next sweep; the server goes on meanwhile.
            logger.LogWarning(ex, "{When} sweep of stored messages failed", when);
            return 0;
        }
    }
}
