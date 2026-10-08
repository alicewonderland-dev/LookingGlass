using Microsoft.Extensions.Options;
using LookingGlass.Server.Data;
using LookingGlass.Server.Hosting;

namespace LookingGlass.Server.Services;

/// <summary>
/// The bans in force, as the running server checks them at every login and connection: read from the database again at
/// most every <see cref="AbuseOptions.BanCheckSeconds"/> (when next asked), so a ban made with <c>--ban</c>, another process
/// writing the same database, applies within that without a restart. A ban that ends stops applying at once, whenever the
/// list was read. <see cref="BanEnforcer"/> reads it on that schedule too, and drops the connections a new ban covers.
/// </summary>
public sealed class BanList(Database db, IOptions<ServerOptions> options, TimeProvider? time = null) {
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly TimeSpan _checkEvery = TimeSpan.FromSeconds(Math.Clamp(options.Value.Abuse.BanCheckSeconds, 1, 60));
    private volatile Snapshot? _snapshot;
    // Only one request reads the database when the list is due; the others go on with the list as it was.
    private int _reading;

    /// <summary>The ban in force on a character (by user ID), if any.</summary>
    public BanRow? ForUser(long userId) {
        var snapshot = this.Current();
        return snapshot.Users.TryGetValue(userId, out var ban) && ban.InForce(this.Now) ? ban : null;
    }

    /// <summary>The ban in force on an address (as <see cref="ClientAddresses.LimitKey"/> gives it), if any.</summary>
    public BanRow? ForAddress(string limitKey) {
        var snapshot = this.Current();
        if (snapshot.Addresses.Count == 0) {
            return null;
        }

        var now = this.Now;
        return snapshot.Addresses.FirstOrDefault(ban => ban.InForce(now) && ClientAddresses.Covers(ban.Address!, limitKey));
    }

    /// <summary>Reads the bans from the database now.</summary>
    public void Refresh() {
        var now = this._time.GetUtcNow();
        var bans = db.GetActiveBans(now.ToUnixTimeSeconds());
        this._snapshot = new Snapshot(
            bans.Where(ban => ban.UserId != null).GroupBy(ban => ban.UserId!.Value).ToDictionary(group => group.Key, group => group.Last()),
            bans.Where(ban => ban.Address != null).ToList(),
            now);
    }

    private long Now => this._time.GetUtcNow().ToUnixTimeSeconds();

    private Snapshot Current() {
        var snapshot = this._snapshot;
        if (snapshot == null) {
            this.Refresh();
            return this._snapshot!;
        }

        if (this._time.GetUtcNow() - snapshot.ReadAt >= this._checkEvery && Interlocked.CompareExchange(ref this._reading, 1, 0) == 0) {
            try {
                this.Refresh();
            } finally {
                Volatile.Write(ref this._reading, 0);
            }
        }

        return this._snapshot!;
    }

    private sealed record Snapshot(Dictionary<long, BanRow> Users, List<BanRow> Addresses, DateTimeOffset ReadAt);
}
