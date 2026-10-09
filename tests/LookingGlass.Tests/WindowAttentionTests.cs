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
            Assert.InRange(flash.StrengthAt(At(i * pulse + pulse / 4), false), 0.4f, 0.6f);
        }

        Assert.True(flash.IsRunning(At(WindowFlash.OpenedPulses * pulse - 1)));
        Assert.False(flash.IsRunning(At(WindowFlash.OpenedPulses * pulse)));
        Assert.Equal(0, flash.StrengthAt(At(WindowFlash.OpenedPulses * pulse + pulse / 2), false));
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
