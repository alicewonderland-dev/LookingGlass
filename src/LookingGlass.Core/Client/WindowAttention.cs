namespace LookingGlass.Core.Client;

/// <summary>
/// The counts on channel windows' tabs: on a tab not selected, the messages from others in its channel since a window last
/// showed it (in any window: a channel can be a tab in several) or the player last talked in it
/// (<see cref="ChannelHistory.UnreadAfter"/>). A tab never shown counts what came since login. Read by the same rule as the
/// channel list's counts (<see cref="UnreadCounter"/>), through <see cref="WindowShows"/>: a channel is read while it is the
/// selected tab of a window that is drawn, whether that window has the focus or not. For the session only; draw thread only.
/// </summary>
public sealed class TabUnread {
    /// <summary>
    /// A channel window draws, showing <paramref name="channelId"/> as its selected tab (null: none, it is closing): read for
    /// the tabs' counts and the channel list's alike, the one rule for both. Call every frame the window draws.
    /// </summary>
    /// <param name="viewer">The window's name as a viewer (see <see cref="UnreadCounter.Viewing(string, string?)"/>).</param>
    public static void WindowShows(UnreadCounter unread, TabUnread tabs, ChannelHistory history, string viewer, string? channelId) {
        unread.Viewing(viewer, channelId);
        if (channelId != null) {
            tabs.Shown(channelId, history.LastSeq(channelId));
        }
    }

    // The newest history line of each channel when a window last showed it.
    private readonly Dictionary<string, long> _shown = new();

    /// <summary>A window shows a channel's tab now, with <paramref name="seq"/> its newest line (call every frame it does).</summary>
    public void Shown(string channelId, long seq) {
        if (seq > this._shown.GetValueOrDefault(channelId)) {
            this._shown[channelId] = seq;
        }
    }

    /// <summary>The count on a tab of the channel that isn't selected.</summary>
    public int CountOf(ChannelHistory history, string channelId) => history.UnreadAfter(channelId, this._shown.GetValueOrDefault(channelId));

    /// <summary>A new session: nothing has been shown yet.</summary>
    public void Clear() => this._shown.Clear();
}

/// <summary>
/// A channel window's brief flash, to be noticed without taking the keyboard: its title bar and border pulse in a channel's
/// colour. A window that opened by itself for a line that arrived (windows only, or a channel kept out of game chat that no
/// window showed) pulses <see cref="OpenedPulses"/> times; one that got a tab by itself, <see cref="TabAddedPulses"/>.
/// With reduced motion (Dalamud's setting), a steady, softer highlight for as long instead. The numbers are here, in one
/// place, to be tuned from what players say. Draw thread only.
/// </summary>
public sealed class WindowFlash {
    /// <summary>How many times a window that opened by itself pulses.</summary>
    public const int OpenedPulses = 3;

    /// <summary>How many times a window pulses when a tab was added to it by itself.</summary>
    public const int TabAddedPulses = 1;

    /// <summary>One pulse: from nothing to full and back.</summary>
    public static readonly TimeSpan PulseLength = TimeSpan.FromMilliseconds(500);

    /// <summary>With reduced motion, how strong the steady highlight is (full is 1).</summary>
    public const float ReducedMotionStrength = 0.5f;

    /// <summary>At full strength, how far the title bar's colour goes toward the channel's (so its title stays readable).</summary>
    public const float TitleBarTint = 0.6f;

    private DateTimeOffset _start;
    private DateTimeOffset _end;

    /// <summary>Starts flashing <paramref name="pulses"/> times; a flash still running that would end later goes on as it was.</summary>
    /// <returns>True if it started (else nothing changed).</returns>
    public bool Start(DateTimeOffset now, int pulses) {
        var end = now + pulses * PulseLength;
        if (pulses <= 0 || end <= this._end) {
            return false;
        }

        this._start = now;
        this._end = end;
        return true;
    }

    /// <summary>Whether it is flashing now.</summary>
    public bool IsRunning(DateTimeOffset now) => now >= this._start && now < this._end;

    /// <summary>How strong the flash is now, from 0 (none) to 1: each pulse rises and falls smoothly.</summary>
    public float StrengthAt(DateTimeOffset now, bool reducedMotion) {
        if (!this.IsRunning(now)) {
            return 0;
        }

        if (reducedMotion) {
            return ReducedMotionStrength;
        }

        var phase = (now - this._start).Ticks % PulseLength.Ticks / (double) PulseLength.Ticks;
        var wave = Math.Sin(Math.PI * phase);
        return (float) (wave * wave);
    }
}
