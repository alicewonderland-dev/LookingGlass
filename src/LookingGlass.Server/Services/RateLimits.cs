using System.Collections.Concurrent;

namespace LookingGlass.Server.Services;

/// <summary>A token bucket: <c>burst</c> tokens, refilled at <c>perSecond</c>.</summary>
public sealed class TokenBucket(double perSecond, double burst, TimeProvider? time = null) {
    private readonly Lock _lock = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly double _burst = burst;
    private double _tokens = burst;
    private DateTimeOffset _updated = (time ?? TimeProvider.System).GetUtcNow();

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

    private void Refill() {
        var now = this._time.GetUtcNow();
        this._tokens = Math.Min(this._burst, this._tokens + Math.Max(0, (now - this._updated).TotalSeconds) * perSecond);
        this._updated = now;
    }
}

/// <summary>
/// A token bucket per key (a user, or an inviter and invitee). Keyed by account, not connection, so reconnecting doesn't
/// reset them. Keys unused for an hour (by then every bucket here is full again) are dropped every 10 minutes.
/// </summary>
public class KeyedRateLimits<TKey>(double perSecond, double burst, TimeProvider? time = null) where TKey : notnull {
    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan IdleFor = TimeSpan.FromHours(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<TKey, (TokenBucket Bucket, DateTimeOffset LastUsed)> _buckets = new();
    private long _lastSweepTicks = (time ?? TimeProvider.System).GetUtcNow().UtcTicks;

    /// <summary>How many keys are tracked now, for tests.</summary>
    internal int TrackedKeys => this._buckets.Count;

    public bool TryTake(TKey key) {
        var now = this._time.GetUtcNow();
        var entry = this._buckets.AddOrUpdate(key,
            _ => (new TokenBucket(perSecond, burst, this._time), now),
            (_, existing) => (existing.Bucket, now));

        var last = Interlocked.Read(ref this._lastSweepTicks);
        if (now.UtcTicks - last > SweepEvery.Ticks && Interlocked.CompareExchange(ref this._lastSweepTicks, now.UtcTicks, last) == last) {
            foreach (var (id, value) in this._buckets) {
                if (now - value.LastUsed > IdleFor) {
                    // Only if unused since: one used meanwhile keeps its bucket (and what it has spent).
                    this._buckets.TryRemove(new KeyValuePair<TKey, (TokenBucket, DateTimeOffset)>(id, value));
                }
            }
        }

        return entry.Bucket.TryTake();
    }
}

/// <summary>Per-user limits (see <see cref="KeyedRateLimits{TKey}"/>).</summary>
public sealed class UserRateLimits(double perSecond, double burst, TimeProvider? time = null) : KeyedRateLimits<long>(perSecond, burst, time);

/// <summary>
/// Counts events per key in a sliding window. Keys whose events have all expired are dropped now and then (when the
/// counter is used, at most once per window), so an address seen once isn't kept forever. And at most
/// <see cref="MaxKeys"/> keys are kept: past that, a new key sweeps at once, and if every key is still in its window,
/// the least recently counted are forgotten (their limits start afresh), so a stream of new addresses within one window
/// can't grow memory without bound either.
/// </summary>
public sealed class WindowCounter(int limit, TimeSpan window, TimeProvider? time = null) {
    /// <summary>The default for <see cref="MaxKeys"/>: far more addresses than a small server sees in an hour.</summary>
    public const int DefaultMaxKeys = 100_000;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly Lock _trimming = new();
    private long _lastSweepTicks = (time ?? TimeProvider.System).GetUtcNow().UtcTicks;

    /// <summary>Events allowed per key within the window.</summary>
    public int Limit => limit;

    /// <summary>Most keys kept at once (see the class summary).</summary>
    public int MaxKeys { get; init; } = DefaultMaxKeys;

    /// <summary>How many keys are tracked now, for tests.</summary>
    internal int TrackedKeys => this._entries.Count;

    public bool TryAdd(string key) {
        var now = this._time.GetUtcNow();
        this.SweepIfDue(now);
        if (this._entries.Count >= this.MaxKeys && !this._entries.ContainsKey(key)) {
            this.Trim(now);
        }

        while (true) {
            var entry = this._entries.GetOrAdd(key, _ => new Entry());
            lock (entry) {
                if (entry.Removed) {
                    // Swept between finding it and locking it: count in the key's new entry instead.
                    continue;
                }

                entry.LastSeen = now;
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
    public bool IsFull(string key) => this.Count(key) >= limit;

    /// <summary>How many events are counted for the key within the window, without counting anything.</summary>
    public int Count(string key) {
        if (!this._entries.TryGetValue(key, out var entry)) {
            return 0;
        }

        lock (entry) {
            if (entry.Removed) {
                return 0;
            }

            this.Expire(entry.Events, this._time.GetUtcNow());
            return entry.Events.Count;
        }
    }

    private void SweepIfDue(DateTimeOffset now) {
        var last = Interlocked.Read(ref this._lastSweepTicks);
        if (now.UtcTicks - last < window.Ticks || Interlocked.CompareExchange(ref this._lastSweepTicks, now.UtcTicks, last) != last) {
            return;
        }

        this.Sweep(now);
    }

    private void Sweep(DateTimeOffset now) {
        foreach (var (key, entry) in this._entries) {
            lock (entry) {
                this.Expire(entry.Events, now);
                if (entry.Events.Count == 0) {
                    this.Remove(key, entry);
                }
            }
        }
    }

    /// <summary>
    /// At the cap: sweeps now, and if that leaves too many, forgets the keys least recently used, down to nine tenths of
    /// the cap (so this, which goes through every key, runs once per tenth of the cap new keys at most). One trim at a time;
    /// a key added meanwhile by another thread may overshoot the cap by a few.
    /// </summary>
    private void Trim(DateTimeOffset now) {
        if (!this._trimming.TryEnter()) {
            return;
        }

        try {
            this.Sweep(now);
            var excess = this._entries.Count - this.MaxKeys * 9 / 10;
            if (excess <= 0) {
                return;
            }

            var oldest = this._entries
                .Select(pair => (pair.Key, pair.Value, Seen: LastSeen(pair.Value)))
                .OrderBy(item => item.Seen)
                .Take(excess)
                .ToList();
            foreach (var (key, entry, _) in oldest) {
                lock (entry) {
                    this.Remove(key, entry);
                }
            }
        } finally {
            this._trimming.Exit();
        }

        static DateTimeOffset LastSeen(Entry entry) {
            lock (entry) {
                return entry.LastSeen;
            }
        }
    }

    /// <summary>Call under the entry's lock: marked there, so nothing is counted in it after it's gone (see TryAdd).</summary>
    private void Remove(string key, Entry entry) {
        entry.Removed = true;
        this._entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
    }

    private void Expire(LinkedList<DateTimeOffset> events, DateTimeOffset now) {
        while (events.First is { } oldest && now - oldest.Value > window) {
            events.RemoveFirst();
        }
    }

    private sealed class Entry {
        public readonly LinkedList<DateTimeOffset> Events = new();
        public bool Removed;

        /// <summary>When the key was last counted or refused: what the cap forgets least recently first.</summary>
        public DateTimeOffset LastSeen;
    }
}
