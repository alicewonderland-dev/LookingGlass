using LookingGlass.Core.Client;
using LookingGlass.Protocol;

namespace LookingGlass.Tests;

/// <summary>
/// What a channel window does to be noticed: the count of new messages on each tab not selected (<see cref="TabUnread"/>),
/// and the brief flash of a window that opened, or got a tab, by itself (<see cref="WindowFlash"/>).
/// </summary>
public sealed class WindowAttentionTests {
    private static readonly DateTimeOffset Start = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly User Alice = new() { UserId = 1, Name = "Alice Liddell", WorldName = "Twintania" };
    private static readonly User Bob = new() { UserId = 2, Name = "Bob Hatter", WorldName = "Lich" };

    private static int _sequence;

    private static IncomingMessage From(User sender, string channelId, bool own = false) =>
        new(channelId, "Channel " + channelId, sender, own, "hello", false, Start.AddMilliseconds(Interlocked.Increment(ref _sequence)));

    // ================================================================ counts on tabs

    [Fact]
    public void ATabNeverShownCountsTheMessagesFromOthersSinceLogin() {
        var history = new ChannelHistory();
        var tabs = new TabUnread();
        history.Add(From(Bob, "aaa"));
        history.Add(From(Bob, "aaa"));
        history.AddNotice(new SessionNotice(NoticeLevel.Info, "Bob joined.", "aaa"));
        history.AddNotice(new SessionNotice(NoticeLevel.Info, "Bob joined.", "bbb"));

        Assert.Equal(2, tabs.CountOf(history, "aaa"));
        // Information lines alone are no count.
        Assert.Equal(0, tabs.CountOf(history, "bbb"));
        Assert.Equal(0, tabs.CountOf(history, "ccc"));
    }

    [Fact]
    public void ShowingATabInAnyWindowClearsItsCountInEveryWindow() {
        var history = new ChannelHistory();
        var tabs = new TabUnread();
        history.Add(From(Bob, "aaa"));
        history.Add(From(Bob, "bbb"));

        // Selected in one window: read there, and in any other window that has it as a tab not selected.
        tabs.Shown("aaa", history.LastSeq("aaa"));
        Assert.Equal(0, tabs.CountOf(history, "aaa"));
        Assert.Equal(1, tabs.CountOf(history, "bbb"));

        history.Add(From(Bob, "aaa"));
        history.Add(From(Bob, "aaa"));
        Assert.Equal(2, tabs.CountOf(history, "aaa"));

        // An older mark (a window drawn later with what it saw before) never brings a count back.
        var seen = history.LastSeq("aaa");
        tabs.Shown("aaa", seen);
        tabs.Shown("aaa", 1);
        Assert.Equal(0, tabs.CountOf(history, "aaa"));
    }

    [Fact]
    public void TalkingInAChannelClearsItsTabsCounts() {
        var history = new ChannelHistory();
        var tabs = new TabUnread();
        history.Add(From(Bob, "aaa"));
        history.Add(From(Alice, "aaa", own: true));
        Assert.Equal(0, tabs.CountOf(history, "aaa"));

        history.Add(From(Bob, "aaa"));
        Assert.Equal(1, tabs.CountOf(history, "aaa"));
    }

    [Fact]
    public void ANewSessionForgetsWhatTabsShowed() {
        var tabs = new TabUnread();
        tabs.Shown("aaa", 1000);
        var history = new ChannelHistory();
        history.Add(From(Bob, "aaa"));
        Assert.Equal(0, tabs.CountOf(history, "aaa"));

        tabs.Clear();
        Assert.Equal(1, tabs.CountOf(history, "aaa"));
    }

    [Fact]
    public void ATabShownInAWindowIsReadForTheTabsAndTheChannelListAlikeFocusedOrNot() {
        // One rule for both counts: the selected tab of a window that is drawn (whether it has the focus or not).
        var clock = new ManualClock();
        var unread = new UnreadCounter(clock);
        var history = new ChannelHistory();
        var tabs = new TabUnread();
        foreach (var message in new[] { From(Bob, "aaa"), From(Bob, "bbb") }) {
            history.Add(message);
            unread.Add(message);
        }

        TabUnread.WindowShows(unread, tabs, history, "window:one", "aaa");
        Assert.Equal(0, unread.CountOf("aaa"));
        Assert.Equal(0, tabs.CountOf(history, "aaa"));
        Assert.Equal(1, unread.CountOf("bbb"));
        Assert.Equal(1, tabs.CountOf(history, "bbb"));

        // What arrives while it is shown counts in neither.
        var later = From(Bob, "aaa");
        history.Add(later);
        Assert.False(unread.Add(later));
        TabUnread.WindowShows(unread, tabs, history, "window:one", "aaa");
        Assert.Equal(0, tabs.CountOf(history, "aaa"));

        // Nothing shown (the window closed or collapsed): both count again.
        TabUnread.WindowShows(unread, tabs, history, "window:one", null);
        var after = From(Bob, "aaa");
        history.Add(after);
        Assert.True(unread.Add(after));
        Assert.Equal(1, unread.CountOf("aaa"));
        Assert.Equal(1, tabs.CountOf(history, "aaa"));
    }

    // ================================================================ the flash

    private static DateTimeOffset At(double milliseconds) => Start.AddMilliseconds(milliseconds);

    /// <summary>A window drawing frames at these times (milliseconds from <see cref="Start"/>): the flash's strength at each.</summary>
    private static List<(double At, float Strength)> Draw(WindowFlash flash, IEnumerable<double> frames, bool reducedMotion = false) {
        var seen = new List<(double, float)>();
        foreach (var at in frames) {
            flash.Frame(At(at));
            seen.Add((at, flash.Strength(reducedMotion)));
        }

        return seen;
    }

    /// <summary>Frames every <paramref name="step"/> ms from <paramref name="from"/> for <paramref name="length"/> ms.</summary>
    private static IEnumerable<double> Frames(double from, double length, double step = 1000.0 / 60) {
        for (var at = from; at < from + length; at += step) {
            yield return at;
        }
    }

    /// <summary>The blinks seen: each run of frames at full strength, from fully dark to fully dark (start and end, in ms).</summary>
    private static List<(double On, double Off)> Blinks(List<(double At, float Strength)> seen) {
        var blinks = new List<(double, double)>();
        double? on = null;
        var dark = true;
        foreach (var (at, strength) in seen) {
            if (strength <= 0.001f) {
                if (on is { } start) {
                    blinks.Add((start, at));
                    on = null;
                }

                dark = true;
            } else if (strength >= 0.999f && dark && on == null) {
                on = at;
                dark = false;
            }
        }

        if (on is { } last) {
            blinks.Add((last, seen[^1].At));
        }

        return blinks;
    }

    [Fact]
    public void AWindowThatOpenedByItselfBlinksThreeTimesInAboutASecondAndAHalf() {
        Assert.Equal(3, WindowFlash.OpenedPulses);
        Assert.InRange(WindowFlash.OpenedPulses * WindowFlash.PulseLength.TotalSeconds, 1.0, 1.6);
        // A tab added by itself: one blink.
        Assert.Equal(1, WindowFlash.TabAddedPulses);

        var flash = new WindowFlash();
        Assert.False(flash.IsRunning);
        Assert.Equal(0, flash.Strength(reducedMotion: false));

        flash.Start(WindowFlash.OpenedPulses);
        var seen = Draw(flash, Frames(0, 2000));
        Assert.Equal(WindowFlash.OpenedPulses, Blinks(seen).Count);
        Assert.All(seen, frame => Assert.InRange(frame.Strength, 0f, 1f));
        Assert.False(flash.IsRunning);
        Assert.Equal(0, seen[^1].Strength);
    }

    /// <summary>
    /// The owner saw one flash where there were three (0.2.19): each blink was lit for most of its half second and dark for
    /// about a tenth of one, with soft edges, and the three ran together. Now about half on, half clearly off.
    /// </summary>
    [Fact]
    public void EachBlinkIsFullyOnForAQuarterOfASecondThenFullyOffForAsLong() {
        var flash = new WindowFlash();
        flash.Start(WindowFlash.OpenedPulses);
        var seen = Draw(flash, Frames(0, 2000));
        var blinks = Blinks(seen);
        Assert.Equal(3, blinks.Count);
        for (var i = 0; i < blinks.Count; i++) {
            var lit = seen.Where(frame => frame.At >= blinks[i].On && frame.At < blinks[i].Off && frame.Strength >= 0.999f).ToList();
            // At full for about a fifth of a second or more, quickly on and off.
            Assert.True(lit[^1].At - lit[0].At >= 180, $"blink {i} was at full for {lit[^1].At - lit[0].At} ms");
            Assert.True(blinks[i].Off - blinks[i].On <= 280, $"blink {i} was lit for {blinks[i].Off - blinks[i].On} ms");
            if (i > 0) {
                // Fully dark between blinks, for about a quarter of a second: as the window looks without a flash.
                var gap = seen.Where(frame => frame.At >= blinks[i - 1].Off && frame.At < blinks[i].On).ToList();
                Assert.All(gap.Where(frame => frame.Strength > 0.001f), frame => Assert.True(frame.At > blinks[i].On - 40, $"not dark at {frame.At}"));
                Assert.True(blinks[i].On - blinks[i - 1].Off >= 200, $"the gap before blink {i} was {blinks[i].On - blinks[i - 1].Off} ms");
            }
        }

        // It comes on at once (no waiting before the first blink).
        Assert.True(blinks[0].On <= 50);
    }

    /// <summary>
    /// The flash's clock runs from the window's first drawn frame, and only as frames are drawn (a long one counts as at most
    /// <see cref="WindowFlash.MaxFrameStep"/>): a window that takes a while to appear, or a frame that hitches, can't skip a blink.
    /// </summary>
    [Fact]
    public void NoBlinkIsLostBeforeTheFirstFrameOrToAHitch() {
        var late = new WindowFlash();
        late.Start(WindowFlash.OpenedPulses);
        // First drawn two seconds after it was asked for: still three blinks, from then.
        Assert.Equal(3, Blinks(Draw(late, Frames(2000, 2000))).Count);

        var hitch = new WindowFlash();
        hitch.Start(WindowFlash.OpenedPulses);
        // A frame that took a second, in the middle of the first blink: the others still show.
        var frames = Frames(0, 100).Concat(Frames(1100, 2000));
        Assert.Equal(3, Blinks(Draw(hitch, frames)).Count);

        // Slow frames (12 a second) still show each blink and each gap.
        var slow = new WindowFlash();
        slow.Start(WindowFlash.OpenedPulses);
        Assert.Equal(3, Blinks(Draw(slow, Frames(0, 3000, 1000.0 / 12))).Count);
        Assert.InRange(WindowFlash.MaxFrameStep.TotalMilliseconds, 50, 120);
    }

    [Fact]
    public void TheFlashIsStrongEnoughToSee() {
        // The title bar goes most of the way to the channel's colour, with its title still readable on it.
        Assert.InRange(WindowFlash.TitleBarTint, 0.75f, 0.9f);
        // A border a few pixels wide (before the global scale), not a hairline.
        Assert.InRange(WindowFlash.BorderThickness, 3f, 5f);
    }

    [Fact]
    public void WithReducedMotionItIsASteadySofterHighlightForTheSameTime() {
        var flash = new WindowFlash();
        flash.Start(WindowFlash.OpenedPulses);
        var end = (WindowFlash.OpenedPulses * WindowFlash.PulseLength).TotalMilliseconds;
        var seen = Draw(flash, Frames(0, end + 200), reducedMotion: true);

        Assert.All(seen.Where(frame => frame.At < end - 20), frame => Assert.Equal(WindowFlash.ReducedMotionStrength, frame.Strength));
        Assert.All(seen.Where(frame => frame.At > end + 20), frame => Assert.Equal(0, frame.Strength));
        Assert.InRange(WindowFlash.ReducedMotionStrength, 0.2f, 0.7f);
    }

    [Fact]
    public void ALongerFlashStillRunningIsntCutShortByABriefOne() {
        var flash = new WindowFlash();
        var pulse = WindowFlash.PulseLength.TotalMilliseconds;
        flash.Start(WindowFlash.OpenedPulses);
        var seen = Draw(flash, Frames(0, pulse / 2));

        // A tab added by itself just after the window opened: the window's own flash goes on as it was.
        Assert.False(flash.Start(WindowFlash.TabAddedPulses));
        seen.AddRange(Draw(flash, Frames(pulse / 2, 2 * pulse)));
        Assert.Equal(3, Blinks(seen).Count);

        // Once it is over, or nearly, another tab blinks again in full.
        Assert.True(flash.Start(WindowFlash.TabAddedPulses));
        Assert.Single(Blinks(Draw(flash, Frames(3 * pulse, 2 * pulse))));
        Assert.False(flash.IsRunning);
    }

    [Fact]
    public void NoPulsesIsNoFlash() {
        var flash = new WindowFlash();
        Assert.False(flash.Start(0));
        Assert.Empty(Blinks(Draw(flash, Frames(0, 1000))));
        Assert.False(flash.IsRunning);
    }
}
