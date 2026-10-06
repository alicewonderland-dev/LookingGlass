using Microsoft.Extensions.Options;
using LookingGlass.Server.Data;

namespace LookingGlass.Server.Hosting;

/// <summary>
/// Explicit checkpoints of the database's write-ahead log, every <see cref="DatabaseOptions.CheckpointMinutes"/> (none by
/// default: SQLite checkpoints by itself as the log grows), and once more when the server has stopped. Only PASSIVE ones,
/// which never wait for or block a reader or writer: Litestream, replicating the database, keeps a reader open so that it
/// sees every frame of the log before it is folded in, and does its own checkpoints.
/// </summary>
public sealed class DatabaseMaintenance(Database db, IOptions<ServerOptions> options, IHostApplicationLifetime lifetime, ILogger<DatabaseMaintenance> logger)
    : BackgroundService {
    public override Task StartAsync(CancellationToken cancellationToken) {
        // After the web server has stopped (so no request is still writing), not merely when stopping starts.
        lifetime.ApplicationStopped.Register(this.AfterStopping);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        var minutes = options.Value.Database.CheckpointMinutes;
        if (minutes <= 0) {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
        try {
            while (await timer.WaitForNextTickAsync(stoppingToken)) {
                this.Checkpoint("Periodic");
            }
        } catch (OperationCanceledException) {
            // Stopping.
        }
    }

    private void AfterStopping() {
        // Whatever goes wrong here, the server has stopped: nothing may be thrown back at the host, which is finishing.
        try {
            if (!File.Exists(db.FilePath)) {
                // Moved away meanwhile (a test's folder deleted, say): opening it would make a new, empty one.
                return;
            }

            this.Checkpoint("Shutdown");
            // Closes the pooled connections: the last one to close folds the rest of the log in (unless another process, such
            // as Litestream, still has the database open), and the files are left closed.
            Database.ReleasePooledConnections(db.FilePath);
        } catch (Exception) {
            // Logging may be gone by now too.
        }
    }

    private void Checkpoint(string when) {
        try {
            var (_, frames, done) = db.Checkpoint();
            logger.LogDebug("{When} checkpoint: {Done} of {Frames} write-ahead log frames in the database", when, done, frames);
        } catch (Exception ex) {
            logger.LogWarning(ex, "{When} checkpoint of the database failed", when);
        }
    }
}
