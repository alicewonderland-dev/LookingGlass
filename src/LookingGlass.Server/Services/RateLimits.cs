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

/// <summary>Counts events per key in a sliding window.</summary>
public sealed class WindowCounter(int limit, TimeSpan window) {
    private readonly ConcurrentDictionary<string, LinkedList<DateTimeOffset>> _events = new();

    public bool TryAdd(string key) {
        var events = this._events.GetOrAdd(key, _ => new LinkedList<DateTimeOffset>());
        lock (events) {
            var now = DateTimeOffset.UtcNow;
            Expire(events, now);
            if (events.Count >= limit) {
                return false;
            }

            events.AddLast(now);
            return true;
        }
    }

    /// <summary>Takes back the newest event counted for the key (one that turned out not to count), if any.</summary>
    public void Refund(string key) {
        if (!this._events.TryGetValue(key, out var events)) {
            return;
        }

        lock (events) {
            if (events.Count > 0) {
                events.RemoveLast();
            }
        }
    }

    /// <summary>Whether the key has reached the limit within the window, without counting anything.</summary>
    public bool IsFull(string key) {
        if (!this._events.TryGetValue(key, out var events)) {
            return limit <= 0;
        }

        lock (events) {
            Expire(events, DateTimeOffset.UtcNow);
            return events.Count >= limit;
        }
    }

    private void Expire(LinkedList<DateTimeOffset> events, DateTimeOffset now) {
        while (events.First is { } oldest && now - oldest.Value > window) {
            events.RemoveFirst();
        }
    }
}
