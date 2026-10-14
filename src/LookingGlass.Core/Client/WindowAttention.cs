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
/// Each pulse is a blink: on at once, at full for about a fifth of a second, then fully off (the window as it usually
/// looks) for a quarter of a second before the next, so three read as three. In 0.2.18 a smooth rise and fall, at full
/// only for an instant, with a fainter tint and a thinner border, went unseen; in 0.2.19 each blink was lit for most of
/// its half second and dark for about a tenth, with soft edges, and the owner saw one flash where there were three.
/// <para>
/// Its clock runs from the window's first drawn frame and only as frames are drawn (<see cref="Frame"/>), a long frame
/// counting as at most <see cref="MaxFrameStep"/>: a window that takes a while to appear, or a hitch as it opens, can't
/// skip a blink. With reduced motion (Dalamud's setting), a steady, softer highlight for as long instead. The numbers are
/// here, in one place, to be tuned from what players say. Draw thread only.
/// </para>
/// </summary>
public sealed class WindowFlash {
    /// <summary>How many times a window that opened by itself pulses.</summary>
    public const int OpenedPulses = 3;

    /// <summary>How many times a window pulses when a tab was added to it by itself.</summary>
    public const int TabAddedPulses = 1;

    /// <summary>One pulse: a blink (on, at full, off), then dark until the next.</summary>
    public static readonly TimeSpan PulseLength = TimeSpan.FromMilliseconds(500);

    /// <summary>The most a single frame moves the flash's clock on: a longer frame (a hitch) doesn't skip a blink.</summary>
    public static readonly TimeSpan MaxFrameStep = TimeSpan.FromMilliseconds(100);

    /// <summary>With reduced motion, how strong the steady highlight is (full is 1).</summary>
    public const float ReducedMotionStrength = 0.5f;

    /// <summary>At full strength, how far the title bar's colour goes toward the channel's (so its title stays readable).</summary>
    public const float TitleBarTint = 0.85f;

    /// <summary>The border drawn around the window while it flashes, in pixels before the global scale (at full strength, opaque).</summary>
    public const float BorderThickness = 3f;

    // Within a pulse, as parts of it: on (a short ease, not a hard edge) until OnAt, full until OffFrom, off at DarkAt, then
    // fully dark for the rest: about 25 ms up, 200 ms full, 25 ms down, 250 ms dark.
    private const double OnAt = 0.05;
    private const double OffFrom = 0.45;
    private const double DarkAt = 0.5;

    // How long the flash lasts, how far its clock has run (in drawn frames), and when the last frame was drawn (null: none yet).
    private TimeSpan _length;
    private TimeSpan _elapsed;
    private DateTimeOffset? _lastFrame;

    /// <summary>Whether it is flashing (or waiting for the window's first frame to start).</summary>
    public bool IsRunning => this._elapsed < this._length;

    /// <summary>
    /// Starts flashing <paramref name="pulses"/> times, from the window's next drawn frame; a flash still running with more
    /// left than that goes on as it was.
    /// </summary>
    /// <returns>True if it started (else nothing changed).</returns>
    public bool Start(int pulses) {
        var length = pulses * PulseLength;
        if (pulses <= 0 || length <= this._length - this._elapsed) {
            return false;
        }

        this._length = length;
        this._elapsed = TimeSpan.Zero;
        this._lastFrame = null;
        return true;
    }

    /// <summary>
    /// The window draws a frame at <paramref name="now"/> (call once per frame, before <see cref="Strength"/>): the clock moves
    /// on by the time since the last frame, at most <see cref="MaxFrameStep"/>; the first frame after <see cref="Start"/> is its 0.
    /// </summary>
    public void Frame(DateTimeOffset now) {
        if (!this.IsRunning) {
            return;
        }

        if (this._lastFrame is { } last) {
            var step = now - last;
            this._elapsed += step < TimeSpan.Zero ? TimeSpan.Zero : step > MaxFrameStep ? MaxFrameStep : step;
        }

        this._lastFrame = now;
    }

    /// <summary>
    /// How strong the flash is at this frame, from 0 (none: the window as it usually looks) to 1: each pulse comes on at once,
    /// stays at full, goes off, and stays fully off until the next.
    /// </summary>
    public float Strength(bool reducedMotion) {
        if (!this.IsRunning || this._lastFrame == null) {
            return 0;
        }

        if (reducedMotion) {
            return ReducedMotionStrength;
        }

        var phase = this._elapsed.Ticks % PulseLength.Ticks / (double) PulseLength.Ticks;
        var strength = phase < OnAt ? Ease(phase / OnAt)
            : phase < OffFrom ? 1
            : phase < DarkAt ? 1 - Ease((phase - OffFrom) / (DarkAt - OffFrom))
            : 0;
        return (float) strength;
    }

    /// <summary>From 0 to 1 smoothly (smoothstep), so a blink's edges aren't a hard jump.</summary>
    private static double Ease(double t) => t * t * (3 - 2 * t);
}
