using Microsoft.Extensions.Options;
using LookingGlass.Server.Data;
using LookingGlass.Server.Realtime;
using LookingGlass.Server.Services;

namespace LookingGlass.Server.Hosting;

/// <summary>
/// Puts bans made while the server runs into effect: every <see cref="AbuseOptions.BanCheckSeconds"/> it reads the bans
/// (see <see cref="BanList"/>) and closes every open connection a ban covers, by its account or its address. The plugin
/// reconnects, is told it is blocked when it logs in (or says hello), and stops trying for a while. Also, at startup and
/// every <see cref="SweepEvery"/>, deletes bans lifted or ended longer ago than <see cref="AbuseOptions.BanHistoryDays"/>
/// and flags that have expired.
/// </summary>
public sealed class BanEnforcer(BanList bans, ConnectionRegistry registry, Database db, IOptions<ServerOptions> options, ILogger<BanEnforcer> logger,
    TimeProvider? time = null) : BackgroundService {
    /// <summary>How often old bans and expired flags are deleted.</summary>
    public static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(10);

    /// <summary>Why a connection a ban covers is closed (the plugin is told the rest when it reconnects).</summary>
    public const string ClosedReason = "Blocked by the server's operator";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public override Task StartAsync(CancellationToken cancellationToken) {
        this.Sweep();
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(options.Value.Abuse.BanCheckSeconds, 1, 60)), this._time);
        var lastSweep = this._time.GetUtcNow();
        try {
            while (await timer.WaitForNextTickAsync(stoppingToken)) {
                try {
                    bans.Refresh();
                    this.Enforce();
                } catch (Exception ex) {
                    // Tried again at the next check; the server goes on meanwhile.
                    logger.LogWarning(ex, "Checking the bans failed");
                }

                if (this._time.GetUtcNow() - lastSweep >= SweepEvery) {
                    lastSweep = this._time.GetUtcNow();
                    this.Sweep();
                }
            }
        } catch (OperationCanceledException) {
            // Stopping.
        }
    }

    /// <summary>Closes every open connection a ban in force covers, as the list was last read.</summary>
    /// <returns>How many were closed.</returns>
    internal int Enforce() {
        var closed = 0;
        foreach (var connection in registry.OpenConnections) {
            if (connection.Aborted.IsCancellationRequested) {
                // Closing already.
                continue;
            }

            var ban = connection.User is { } user ? bans.ForUser(user.UserId) : null;
            ban ??= bans.ForAddress(connection.RemoteAddress);
            if (ban == null) {
                continue;
            }

            logger.LogInformation("Closing a connection of {Subject}, which is banned",
                connection.User is { } banned ? $"user {banned.UserId}" : $"address {connection.RemoteAddress}");
            connection.Abort(ClosedReason);
            closed++;
        }

        return closed;
    }

    private void Sweep() {
        try {
            var now = this._time.GetUtcNow().ToUnixTimeSeconds();
            var settings = options.Value.Abuse;
            var bansGone = db.SweepBans(now, Math.Clamp(settings.BanHistoryDays, 1, 3650));
            var flagsGone = db.SweepFlags(now - Math.Clamp(settings.FlagExpiresAfterHours, 1, 8760) * 3600L);
            if (bansGone + flagsGone > 0) {
                logger.LogDebug("Deleted {Bans} old bans and {Flags} expired flags", bansGone, flagsGone);
            }
        } catch (Exception ex) {
            logger.LogWarning(ex, "Deleting old bans and expired flags failed");
        }
    }
}
