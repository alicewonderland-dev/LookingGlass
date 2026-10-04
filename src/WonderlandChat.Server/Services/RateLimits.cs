using System.Collections.Concurrent;

namespace WonderlandChat.Server.Services;

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
