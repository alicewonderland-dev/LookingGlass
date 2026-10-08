using System.Globalization;
using Microsoft.Extensions.Options;
using LookingGlass.Server.Data;
using LookingGlass.Server.Hosting;

namespace LookingGlass.Server.Services;

/// <summary>
/// Notices accounts and addresses refused by limits again and again. Every refusal by a limit (invites, messages, lookups,
/// registrations, key logins, connections, and the rest) is counted for the account, if the connection is logged in, and
/// for its address: an IPv4 address, or an IPv6 /64 and its /56 each. One refused by limits in at least
/// <see cref="AbuseOptions.FlagAfterMinutesRefused"/> different minutes of the last <see cref="AbuseOptions.WindowMinutes"/>,
/// or by at least <see cref="AbuseOptions.FlagAfterLimits"/> different limits within <see cref="AbuseOptions.FlagLimitsWithinMinutes"/>,
/// is flagged: one warning in the log (limit names and the user ID or address, never a name or anything said), and an entry
/// in the database that <c>--bans</c> lists. A flag lasts <see cref="AbuseOptions.FlagExpiresAfterHours"/> after the last
/// refusal, then expires by itself. Nothing is banned for it, except, if the operator turns it on, an address refused
/// <see cref="AbuseOptions.AutoBlockAfterRefusals"/> times within the window, for <see cref="AbuseOptions.AutoBlockMinutes"/>.
/// <para>
/// Memory stays bounded: at most <see cref="AbuseOptions.MaxTrackedKeys"/> accounts and addresses are counted, each keeping
/// one count per minute of the window and the time of its last refusal by each limit (there are a few dozen limits). Past
/// the cap, the least recently refused are forgotten (a flag already made stays in the database).
/// </para>
/// </summary>
public sealed class AbuseMonitor(Database db, BanList bans, IOptions<ServerOptions> options, ILogger<AbuseMonitor> logger, TimeProvider? time = null) {
    // How often a flag still being refused has its last refusal written to the database: it lasts a day by default, so
    // this need not be at every refusal.
    private static readonly TimeSpan SaveFlagEvery = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly AbuseOptions _settings = options.Value.Abuse;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Tracked> _tracked = new(StringComparer.Ordinal);
    private DateTimeOffset _lastSweep = (time ?? TimeProvider.System).GetUtcNow();

    /// <summary>How many accounts and addresses are counted now, for tests.</summary>
    internal int TrackedKeys {
        get {
            lock (this._lock) {
                return this._tracked.Count;
            }
        }
    }

    private TimeSpan Window => TimeSpan.FromMinutes(Math.Clamp(this._settings.WindowMinutes, 10, 1440));

    /// <summary>
    /// Counts a refusal by a limit. Never throws: whatever goes wrong here (the database, say) is logged, and the request is
    /// answered as it would have been.
    /// </summary>
    /// <param name="limit">The limit, as its setting is named (or the request's kind, for limits that have none).</param>
    /// <param name="userId">The account, if the connection is logged in (never one a request only names).</param>
    /// <param name="address">The address, as <see cref="ClientAddresses.LimitKey"/> gives it.</param>
    public void Refused(string limit, long? userId, string? address) {
        try {
            var subjects = new List<(string Subject, long? UserId, string? Address)>(3);
            if (userId is { } id) {
                subjects.Add(($"user {id}", id, null));
            }

            if (!string.IsNullOrEmpty(address) && address != "unknown") {
                subjects.Add(($"address {address}", null, address));
                var widened = ClientAddresses.WidenToConnectionKey(address);
                if (widened != address) {
                    subjects.Add(($"address {widened}", null, widened));
                }
            }

            foreach (var (subject, user, prefix) in subjects) {
                this.Count(subject, user, prefix, limit);
            }
        } catch (Exception ex) {
            logger.LogWarning(ex, "Counting a refusal by {Limit} failed", limit);
        }
    }

    private void Count(string subject, long? userId, string? address, string limit) {
        var now = this._time.GetUtcNow();
        Decision decision;
        lock (this._lock) {
            this.SweepIfDue(now);
            if (!this._tracked.TryGetValue(subject, out var tracked)) {
                if (this._tracked.Count >= Math.Max(1, this._settings.MaxTrackedKeys)) {
                    this.Trim(now);
                }

                tracked = new Tracked();
                this._tracked[subject] = tracked;
            }

            decision = this.CountLocked(tracked, now, limit, address != null);
        }

        if (decision.Flag is { } why) {
            this.Flag(subject, userId, address, now, why, decision.Limits);
        } else if (decision.SaveFlag) {
            this.SaveFlag(subject, userId, address, now, decision.Limits);
        }

        if (decision.Block) {
            this.Block(address!, now, decision.Refusals);
        }
    }

    /// <summary>Counts the refusal, and decides what to do about it. Under the lock; no database here.</summary>
    private Decision CountLocked(Tracked tracked, DateTimeOffset now, string limit, bool isAddress) {
        var minute = now.ToUnixTimeSeconds() / 60;
        var window = Math.Clamp(this._settings.WindowMinutes, 10, 1440);
        // A flag that has expired meanwhile (no refusals for long enough) is announced again if it is earned again.
        if (tracked.Flagged && now - tracked.LastRefused > TimeSpan.FromHours(this._settings.FlagExpiresAfterHours)) {
            tracked.Flagged = false;
        }

        tracked.LastRefused = now;
        var left = tracked.Minutes.FindIndex(entry => entry.Minute > minute - window);
        tracked.Minutes.RemoveRange(0, left < 0 ? tracked.Minutes.Count : left);
        if (tracked.Minutes.Count > 0 && tracked.Minutes[^1].Minute == minute) {
            tracked.Minutes[^1] = tracked.Minutes[^1] with { Count = tracked.Minutes[^1].Count + 1 };
        } else {
            tracked.Minutes.Add(new MinuteCount(minute, 1));
        }

        foreach (var stale in tracked.Limits.Where(pair => now - pair.Value > this.Window).Select(pair => pair.Key).ToList()) {
            tracked.Limits.Remove(stale);
        }

        tracked.Limits[limit] = now;

        var limitsWithin = TimeSpan.FromMinutes(this._settings.FlagLimitsWithinMinutes);
        var recentLimits = tracked.Limits.Count(pair => now - pair.Value < limitsWithin);
        var minutes = tracked.Minutes.Count;
        string? why = minutes >= this._settings.FlagAfterMinutesRefused
            ? $"refused by limits in {minutes} of the last {window} minutes"
            : recentLimits >= this._settings.FlagAfterLimits
                ? $"refused by {recentLimits} different limits within {this._settings.FlagLimitsWithinMinutes} minutes"
                : null;
        var limits = string.Join(", ", tracked.Limits.Keys.Order(StringComparer.Ordinal));

        var decision = new Decision(null, limits, false, false, 0);
        if (why != null && !tracked.Flagged) {
            tracked.Flagged = true;
            tracked.FlagSaved = now;
            decision = decision with { Flag = why };
        } else if (tracked.Flagged && now - tracked.FlagSaved >= SaveFlagEvery) {
            tracked.FlagSaved = now;
            decision = decision with { SaveFlag = true };
        }

        if (isAddress && this._settings.AutoBlockMinutes > 0 && now >= tracked.BlockedUntil) {
            var refusals = tracked.Minutes.Sum(entry => entry.Count);
            if (refusals >= this._settings.AutoBlockAfterRefusals) {
                tracked.BlockedUntil = now + TimeSpan.FromMinutes(this._settings.AutoBlockMinutes);
                decision = decision with { Block = true, Refusals = refusals };
            }
        }

        return decision;
    }

    /// <summary>Flags a subject newly over a threshold: unless a flag of its own is still in force (from before a restart, say), with a warning.</summary>
    private void Flag(string subject, long? userId, string? address, DateTimeOffset now, string why, string limits) {
        var at = now.ToUnixTimeSeconds();
        var existing = db.GetFlag(subject);
        if (existing != null && existing.LastRefusedAt >= at - this._settings.FlagExpiresAfterHours * 3600L) {
            db.SaveFlag(existing with { LastRefusedAt = at, Limits = Merge(existing.Limits, limits) });
            return;
        }

        db.SaveFlag(new FlagRow(subject, userId, address, at, at, limits, why));
        logger.LogWarning(
            "Flagged {Subject}: {Why}, by {Limits}. Repeated refusals by limits like these are unlikely to be by accident; see it with --bans, " +
            "and ban it with --ban {Target} if it is abuse. The flag expires {Hours} hours after the last refusal.",
            subject, why, limits, userId?.ToString(CultureInfo.InvariantCulture) ?? address, this._settings.FlagExpiresAfterHours);
    }

    private void SaveFlag(string subject, long? userId, string? address, DateTimeOffset now, string limits) {
        var at = now.ToUnixTimeSeconds();
        var existing = db.GetFlag(subject);
        db.SaveFlag(existing == null
            ? new FlagRow(subject, userId, address, at, at, limits, "refused by limits again")
            : existing with { LastRefusedAt = at, Limits = Merge(existing.Limits, limits) });
    }

    /// <summary>Blocks an address far past the threshold for a few minutes, unless a ban in force covers it already (one the operator made, say).</summary>
    private void Block(string address, DateTimeOffset now, int refusals) {
        var at = now.ToUnixTimeSeconds();
        if (db.GetActiveBans(at).Any(ban => ban.Address != null && ClientAddresses.Covers(ban.Address, address))) {
            return;
        }

        var prefix = ClientAddresses.BanPrefix(address, out _) ?? address;
        var minutes = this._settings.AutoBlockMinutes;
        db.AddBan(null, prefix, "", at + minutes * 60L, automatic: true, at, out _);
        bans.Refresh();
        logger.LogWarning(
            "Blocked address {Address} for {Minutes} minutes automatically: refused {Refusals} times within {Window} minutes " +
            "(LookingGlass:Abuse:AutoBlockAfterRefusals). It is listed by --bans, and --unban {Address} lifts it.",
            prefix, minutes, refusals, Math.Clamp(this._settings.WindowMinutes, 10, 1440), prefix);
    }

    /// <summary>Two comma separated lists of limits, as one, in order.</summary>
    private static string Merge(string first, string second) =>
        string.Join(", ", first.Split(", ", StringSplitOptions.RemoveEmptyEntries).Union(second.Split(", ", StringSplitOptions.RemoveEmptyEntries))
            .Order(StringComparer.Ordinal));

    /// <summary>Forgets what has left the window, now and then (at most every tenth of it). Under the lock.</summary>
    private void SweepIfDue(DateTimeOffset now) {
        if (now - this._lastSweep < this.Window / 10) {
            return;
        }

        this._lastSweep = now;
        this.Sweep(now);
    }

    private void Sweep(DateTimeOffset now) {
        foreach (var (subject, tracked) in this._tracked.Where(pair => now - pair.Value.LastRefused > this.Window && now >= pair.Value.BlockedUntil).ToList()) {
            this._tracked.Remove(subject);
        }
    }

    /// <summary>
    /// At the cap: forgets what has left the window, and if that isn't enough, the least recently refused, down to nine
    /// tenths of the cap (so this, which goes through every one, runs once per tenth of the cap new ones at most). Under the lock.
    /// </summary>
    private void Trim(DateTimeOffset now) {
        this.Sweep(now);
        var excess = this._tracked.Count - Math.Max(1, this._settings.MaxTrackedKeys) * 9 / 10;
        if (excess <= 0) {
            return;
        }

        foreach (var subject in this._tracked.OrderBy(pair => pair.Value.LastRefused).Take(excess).Select(pair => pair.Key).ToList()) {
            this._tracked.Remove(subject);
        }
    }

    private readonly record struct MinuteCount(long Minute, int Count);

    private readonly record struct Decision(string? Flag, string Limits, bool SaveFlag, bool Block, int Refusals);

    /// <summary>What is counted for one account or address.</summary>
    private sealed class Tracked {
        /// <summary>The minutes of the window it was refused in, oldest first, with how many times in each.</summary>
        public readonly List<MinuteCount> Minutes = new();

        /// <summary>When it was last refused by each limit, within the window.</summary>
        public readonly Dictionary<string, DateTimeOffset> Limits = new(StringComparer.Ordinal);

        public DateTimeOffset LastRefused;

        /// <summary>Flagged (and announced) while counted here; and when the flag was last written to the database.</summary>
        public bool Flagged;
        public DateTimeOffset FlagSaved;

        /// <summary>Until when it is blocked automatically, if it was.</summary>
        public DateTimeOffset BlockedUntil;
    }
}
