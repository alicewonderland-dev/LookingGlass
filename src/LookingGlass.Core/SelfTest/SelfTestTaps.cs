namespace LookingGlass.Core.SelfTest;

/// <summary>
/// What one of the plugin's taps holds while a run is on (lines printed, seen in game chat, or delivered), each with its
/// place (a mark): at most <see cref="Most"/>, the oldest dropped first, so a busy tester's chat never pushes out what the
/// run reads last (a partner's answer). Thread-safe; held in memory for the run only.
/// </summary>
public sealed class RunTap<T> {
    /// <summary>The most a tap holds (a run reads a few hundred).</summary>
    public const int Most = 2000;

    private readonly Lock _lock = new();
    private readonly Queue<(long Seq, T Item)> _items = new();

    public void Add(long seq, T item) {
        lock (this._lock) {
            this._items.Enqueue((seq, item));
            while (this._items.Count > Most) {
                this._items.Dequeue();
            }
        }
    }

    /// <summary>What came after a mark, oldest first.</summary>
    public IReadOnlyList<T> Since(long mark) {
        lock (this._lock) {
            return this._items.Where(entry => entry.Seq > mark).Select(entry => entry.Item).ToList();
        }
    }

    public int Count {
        get {
            lock (this._lock) {
                return this._items.Count;
            }
        }
    }

    public void Clear() {
        lock (this._lock) {
            this._items.Clear();
        }
    }
}

/// <summary>Which delivered messages the two-player tap keeps: never a real channel's.</summary>
public static class SelfTestTaps {
    /// <summary>
    /// A delivered message is kept only if it is in one of the run's test channels (its own, or the other tester's it joined),
    /// or in local chat, and is from the other tester or carries the run's prefix (one's own test and protocol lines, or
    /// someone meddling). Real channels' messages, and anyone else's ordinary lines, are never kept.
    /// </summary>
    /// <param name="testChannels">The run's own and joined test channels.</param>
    /// <param name="other">The other tester, name@world.</param>
    /// <param name="otherId">The other tester's user ID, once known (0 before).</param>
    public static bool Keeps(ReceivedLine line, IReadOnlyCollection<string> testChannels, string? other, long otherId, string code) {
        if (line.ChannelId != null && !testChannels.Contains(line.ChannelId)) {
            return false;
        }

        var fromOther = (other != null && string.Equals(line.Sender, other, StringComparison.OrdinalIgnoreCase)) || (otherId != 0 && line.SenderId == otherId);
        return (fromOther && !line.Own) || SelfTestProtocol.IsOurs(code, line.Text);
    }
}
