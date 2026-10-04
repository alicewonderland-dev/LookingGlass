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
            var now = DateTimeOffset.UtcNow;
            this._tokens = Math.Min(this._burst, this._tokens + (now - this._updated).TotalSeconds * perSecond);
            this._updated = now;
            if (this._tokens < 1) {
                return false;
            }

            this._tokens -= 1;
            return true;
        }
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
}

/// <summary>Counts events per key in a sliding window.</summary>
public sealed class WindowCounter(int limit, TimeSpan window) {
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _events = new();

    public bool TryAdd(string key) {
        var queue = this._events.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (queue) {
            var now = DateTimeOffset.UtcNow;
            while (queue.Count > 0 && now - queue.Peek() > window) {
                queue.Dequeue();
            }

            if (queue.Count >= limit) {
                return false;
            }

            queue.Enqueue(now);
            return true;
        }
    }
}
