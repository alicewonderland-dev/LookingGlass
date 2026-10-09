namespace LookingGlass.Core.Client;

/// <summary>
/// Counts the messages from others in each channel since it was last read, for the main
/// window's unread markers. A channel is read when a window shows it (<see cref="Viewing(string?)"/>:
/// the main window, every frame it's drawn, if its messages go to game chat (<see cref="MainWindowReads"/>); or a
/// channel window, every frame it has the focus with that channel's tab selected) or when you send to it
/// (<see cref="MarkRead"/>). Counts live only in
/// memory. Safe from any thread: messages arrive on the session's threads, the window reads on
/// the draw thread.
/// </summary>
public sealed class UnreadCounter {
    /// <summary>How long after the window last drew a channel it still counts as shown. Frames are far shorter.</summary>
    public static readonly TimeSpan ViewingGrace = TimeSpan.FromSeconds(1);

    /// <summary>How many recently delivered messages are remembered, so a second delivery isn't counted again.</summary>
    public const int Remembered = 1000;

    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, int> _counts = new();
    private readonly HashSet<MessageKey> _seen = new();
    private readonly Queue<MessageKey> _seenOrder = new();
    // What each window (the main window, each channel window) showed last, and when.
    private readonly Dictionary<string, (string ChannelId, DateTimeOffset At)> _viewing = new();
    private int _total;

    public UnreadCounter(TimeProvider? time = null) {
        this._time = time ?? TimeProvider.System;
    }

    /// <summary>All unread messages, in every channel.</summary>
    public int Total {
        get {
            lock (this._lock) {
                return this._total;
            }
        }
    }

    public int CountOf(string channelId) {
        lock (this._lock) {
            return this._counts.GetValueOrDefault(channelId);
        }
    }

    /// <summary>A message was delivered.</summary>
    /// <returns>True if it counts as unread: from someone else, not delivered before, and its channel isn't showing.</returns>
    public bool Add(IncomingMessage message) {
        if (message.IsOwn) {
            return false;
        }

        lock (this._lock) {
            if (!this.Remember(new MessageKey(message.ChannelId, message.Sender.UserId, message.Timestamp, message.Text))) {
                return false;
            }

            if (this.IsShowing(message.ChannelId)) {
                return false;
            }

            this._counts[message.ChannelId] = this._counts.GetValueOrDefault(message.ChannelId) + 1;
            this._total++;
            return true;
        }
    }

    /// <summary>
    /// The window is showing this channel (call every frame it draws it), or none. Reads it, and
    /// keeps what arrives meanwhile from counting.
    /// </summary>
    public void Viewing(string? channelId) => this.Viewing(MainWindow, channelId);

    /// <summary>The main window, as a viewer (see <see cref="Viewing(string, string?)"/>).</summary>
    public const string MainWindow = "main";

    /// <summary>
    /// Whether the main window reads a channel it shows: only one whose messages go to game chat (seen there, as before
    /// windows). Its channel pane shows no messages, so a channel seen only in windows (windows only, or turned off game
    /// chat) stays counted until a channel window's tab shows it, or the player talks in it.
    /// </summary>
    public static bool MainWindowReads(bool windowsOnly, IReadOnlySet<string> off, string channelId) =>
        WindowsOnly.MessageToGameChat(windowsOnly, off, channelId);

    /// <summary>
    /// A window is showing this channel (call every frame it shows it), or none: one of several, each with a name of its
    /// own (<see cref="MainWindow"/>, or a channel window's). A channel any of them shows is read, and what arrives meanwhile
    /// doesn't count.
    /// </summary>
    public void Viewing(string viewer, string? channelId) {
        lock (this._lock) {
            if (channelId == null) {
                this._viewing.Remove(viewer);
                return;
            }

            this._viewing[viewer] = (channelId, this._time.GetUtcNow());
            this.ClearCount(channelId);
        }
    }

    /// <summary>You sent to the channel: you've seen it.</summary>
    public void MarkRead(string channelId) {
        lock (this._lock) {
            this.ClearCount(channelId);
        }
    }

    /// <summary>
    /// Drops the counts of channels you're no longer in. Does nothing until the snapshot holds
    /// the complete channel list, as with command slots.
    /// </summary>
    /// <returns>True if anything changed.</returns>
    public bool Retain(SessionSnapshot snapshot) {
        if (snapshot.State != ConnectionState.Ready || !snapshot.ChannelsLoaded) {
            return false;
        }

        var listed = snapshot.Channels.Select(channel => channel.Id).ToHashSet();
        lock (this._lock) {
            var gone = this._counts.Keys.Where(id => !listed.Contains(id)).ToList();
            foreach (var channelId in gone) {
                this.ClearCount(channelId);
            }

            return gone.Count > 0;
        }
    }

    /// <summary>
    /// A new session (another character, or another server): counts start from zero. Delivered
    /// messages are still remembered, so one the last session already delivered isn't new.
    /// </summary>
    public void Reset() {
        lock (this._lock) {
            this._counts.Clear();
            this._total = 0;
            this._viewing.Clear();
        }
    }

    /// <summary>How a count is shown: nothing for none, and at most "99+".</summary>
    public static string Format(int count) => count switch {
        <= 0 => "",
        > 99 => "99+",
        _ => count.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private bool IsShowing(string channelId) {
        var now = this._time.GetUtcNow();
        foreach (var (shown, at) in this._viewing.Values) {
            if (shown == channelId && now - at <= ViewingGrace) {
                return true;
            }
        }

        return false;
    }

    private void ClearCount(string channelId) {
        if (this._counts.Remove(channelId, out var count)) {
            this._total -= count;
        }
    }

    private bool Remember(MessageKey key) {
        if (!this._seen.Add(key)) {
            return false;
        }

        this._seenOrder.Enqueue(key);
        while (this._seenOrder.Count > Remembered) {
            this._seen.Remove(this._seenOrder.Dequeue());
        }

        return true;
    }

    /// <summary>Messages carry no ID here; the same sender, time (signed, to the millisecond) and text is the same message.</summary>
    private readonly record struct MessageKey(string ChannelId, long SenderId, DateTimeOffset Timestamp, string? Text);
}
