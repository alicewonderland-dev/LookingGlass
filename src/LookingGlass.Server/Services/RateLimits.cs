using System.Collections.Concurrent;

namespace LookingGlass.Server.Services;

/// <summary>A token bucket: <c>burst</c> tokens, refilled at <c>perSecond</c>.</summary>
public sealed class TokenBucket(double perSecond, double burst) {
    private readonly Lock _lock = new();
    private readonly double _burst = burst;
    private double _tokens = burst;
    private DateTimeOffset _updated = DateTimeOffset.UtcNow;

    public bool TryTake() {
        lock (this._lock) {
            this.Refill();
            if (this._tokens < 1) {
                return false;
            }

            this._tokens -= 1;
            return true;
        }
    }

    /// <summary>Whether a token could be taken now, without taking it.</summary>
    public bool HasToken() {
        lock (this._lock) {
            this.Refill();
            return this._tokens >= 1;
        }
    }

    private void Refill() {
        var now = DateTimeOffset.UtcNow;
        this._tokens = Math.Min(this._burst, this._tokens + (now - this._updated).TotalSeconds * perSecond);
        this._updated = now;
    }
}

/// <summary>
/// Per-user limits. Keyed by user, not connection, so reconnecting doesn't
/// reset them. Entries for users who have gone quiet are dropped periodically.
/// </summary>
public sealed class UserRateLimits(double perSecond, double burst) {
    private readonly ConcurrentDictionary<long, (TokenBucket Bucket, DateTimeOffset LastUsed)> _buckets = new();
    private DateTimeOffset _lastSweep = DateTimeOffset.UtcNow;

    public bool TryTake(long userId) {
        var now = DateTimeOffset.UtcNow;
        var entry = this._buckets.AddOrUpdate(userId,
            _ => (new TokenBucket(perSecond, burst), now),
            (_, existing) => (existing.Bucket, now));

        if (now - this._lastSweep > TimeSpan.FromMinutes(10)) {
            this._lastSweep = now;
            foreach (var (id, value) in this._buckets) {
                if (now - value.LastUsed > TimeSpan.FromHours(1)) {
                    this._buckets.TryRemove(id, out _);
                }
            }
        }

        return entry.Bucket.TryTake();
    }

    /// <summary>Whether <see cref="TryTake"/> would succeed now, without taking anything or tracking the user.</summary>
    public bool HasToken(long userId) {
        return !this._buckets.TryGetValue(userId, out var entry) || entry.Bucket.HasToken();
    }
}

/// <summary>
/// Counts events per key in a sliding window. Keys whose events have all expired are dropped now and then (when the
/// counter is used, at most once per window), so an address seen once isn't kept forever.
/// </summary>
public sealed class WindowCounter(int limit, TimeSpan window, TimeProvider? time = null) {
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private long _lastSweepTicks = (time ?? TimeProvider.System).GetUtcNow().UtcTicks;

    /// <summary>How many keys are tracked now, for tests.</summary>
    internal int TrackedKeys => this._entries.Count;

    public bool TryAdd(string key) {
        var now = this._time.GetUtcNow();
        this.SweepIfDue(now);
        while (true) {
            var entry = this._entries.GetOrAdd(key, _ => new Entry());
            lock (entry) {
                if (entry.Removed) {
                    // Swept between finding it and locking it: count in the key's new entry instead.
                    continue;
                }

                this.Expire(entry.Events, now);
                if (entry.Events.Count >= limit) {
                    return false;
                }

                entry.Events.AddLast(now);
                return true;
            }
        }
    }

    /// <summary>Takes back the newest event counted for the key (one that turned out not to count), if any.</summary>
    public void Refund(string key) {
        if (!this._entries.TryGetValue(key, out var entry)) {
            return;
        }

        lock (entry) {
            if (!entry.Removed && entry.Events.Count > 0) {
                entry.Events.RemoveLast();
            }
        }
    }

    /// <summary>Whether the key has reached the limit within the window, without counting anything.</summary>
    public bool IsFull(string key) {
        if (!this._entries.TryGetValue(key, out var entry)) {
            return limit <= 0;
        }

        lock (entry) {
            if (entry.Removed) {
                return limit <= 0;
            }

            this.Expire(entry.Events, this._time.GetUtcNow());
            return entry.Events.Count >= limit;
        }
    }

    private void SweepIfDue(DateTimeOffset now) {
        var last = Interlocked.Read(ref this._lastSweepTicks);
        if (now.UtcTicks - last < window.Ticks || Interlocked.CompareExchange(ref this._lastSweepTicks, now.UtcTicks, last) != last) {
            return;
        }

        foreach (var (key, entry) in this._entries) {
            lock (entry) {
                this.Expire(entry.Events, now);
                if (entry.Events.Count == 0) {
                    // Marked under its lock, so nothing is counted in it after it's gone (see TryAdd).
                    entry.Removed = true;
                    this._entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
                }
            }
        }
    }

    private void Expire(LinkedList<DateTimeOffset> events, DateTimeOffset now) {
        while (events.First is { } oldest && now - oldest.Value > window) {
            events.RemoveFirst();
        }
    }

    private sealed class Entry {
        public readonly LinkedList<DateTimeOffset> Events = new();
        public bool Removed;
    }
}
