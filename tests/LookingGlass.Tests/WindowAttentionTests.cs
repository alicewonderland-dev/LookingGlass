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

    [Fact]
    public void AWindowThatOpenedByItselfPulsesAFewTimesInAboutASecondAndAHalf() {
        Assert.InRange(WindowFlash.OpenedPulses, 2, 3);
        Assert.InRange(WindowFlash.OpenedPulses * WindowFlash.PulseLength.TotalSeconds, 1.0, 1.6);
        // A tab added by itself: briefer.
        Assert.Equal(1, WindowFlash.TabAddedPulses);

        var flash = new WindowFlash();
        Assert.False(flash.IsRunning(Start));
        Assert.Equal(0, flash.StrengthAt(Start, reducedMotion: false));

        flash.Start(Start, WindowFlash.OpenedPulses);
        var pulse = WindowFlash.PulseLength.TotalMilliseconds;
        for (var i = 0; i < WindowFlash.OpenedPulses; i++) {
            // Each pulse rises from nothing to full and back.
            Assert.Equal(0, flash.StrengthAt(At(i * pulse), false), 3);
            Assert.Equal(1, flash.StrengthAt(At(i * pulse + pulse / 2), false), 3);
        }

        Assert.True(flash.IsRunning(At(WindowFlash.OpenedPulses * pulse - 1)));
        Assert.False(flash.IsRunning(At(WindowFlash.OpenedPulses * pulse)));
        Assert.Equal(0, flash.StrengthAt(At(WindowFlash.OpenedPulses * pulse + pulse / 2), false));
    }

    /// <summary>The flash an owner didn't see (0.2.18): pulses that were full only for a moment, a tint and border too faint.</summary>
    [Fact]
    public void EachPulseIsAtFullStrengthForMostOfItThenDarkBeforeTheNext() {
        var flash = new WindowFlash();
        flash.Start(Start, WindowFlash.OpenedPulses);
        var pulse = WindowFlash.PulseLength.TotalMilliseconds;
        const int samples = 1000;
        for (var i = 0; i < WindowFlash.OpenedPulses; i++) {
            var full = 0;
            var dark = 0;
            var last = -1f;
            var rising = true;
            for (var s = 0; s < samples; s++) {
                var strength = flash.StrengthAt(At(i * pulse + pulse * s / samples), false);
                Assert.InRange(strength, 0f, 1f);
                full += strength >= 0.99f ? 1 : 0;
                dark += strength <= 0.01f ? 1 : 0;
                // Up once, then down once: one blink, not a flicker.
                if (rising && strength < last) {
                    rising = false;
                }

                Assert.True(rising || strength <= last + 1e-6f, $"pulse {i} rose again at {s}/{samples}");
                last = strength;
            }

            // Lit for most of the pulse, so it is seen; dark for a moment at its end, so three pulses read as three.
            Assert.True(full >= samples * 0.4, $"pulse {i} was at full strength for only {full}/{samples}");
            Assert.True(dark >= samples * 0.1, $"pulse {i} was dark for only {dark}/{samples}");
            // It comes on quickly.
            Assert.True(flash.StrengthAt(At(i * pulse + pulse / 5), false) >= 0.99f);
        }
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
        flash.Start(Start, WindowFlash.OpenedPulses);
        var end = (WindowFlash.OpenedPulses * WindowFlash.PulseLength).TotalMilliseconds;

        foreach (var at in new[] { 0, 1, end / 3, end / 2, end - 1 }) {
            Assert.Equal(WindowFlash.ReducedMotionStrength, flash.StrengthAt(At(at), reducedMotion: true));
        }

        Assert.InRange(WindowFlash.ReducedMotionStrength, 0.2f, 0.7f);
        Assert.Equal(0, flash.StrengthAt(At(end), reducedMotion: true));
    }

    [Fact]
    public void ALongerFlashStillRunningIsntCutShortByABriefOne() {
        var flash = new WindowFlash();
        var pulse = WindowFlash.PulseLength.TotalMilliseconds;
        flash.Start(Start, WindowFlash.OpenedPulses);

        // A tab added by itself just after the window opened: the window's own flash goes on as it was.
        Assert.False(flash.Start(At(pulse / 2), WindowFlash.TabAddedPulses));
        Assert.Equal(1, flash.StrengthAt(At(pulse + pulse / 2), false), 3);
        Assert.True(flash.IsRunning(At(WindowFlash.OpenedPulses * pulse - 1)));

        // Once it is nearly over, another tab flashes again in full.
        var late = At(WindowFlash.OpenedPulses * pulse - 10);
        Assert.True(flash.Start(late, WindowFlash.TabAddedPulses));
        Assert.Equal(1, flash.StrengthAt(late.AddMilliseconds(pulse / 2), false), 3);
        Assert.False(flash.IsRunning(late.Add(WindowFlash.PulseLength)));
    }

    [Fact]
    public void NoPulsesIsNoFlash() {
        var flash = new WindowFlash();
        flash.Start(Start, 0);
        Assert.False(flash.IsRunning(Start));
        Assert.Equal(0, flash.StrengthAt(Start, false));
    }
}
