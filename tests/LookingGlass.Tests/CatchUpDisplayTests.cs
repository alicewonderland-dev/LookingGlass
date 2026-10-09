using LookingGlass.Core.Client;
using LookingGlass.Protocol;

namespace LookingGlass.Tests;

/// <summary>
/// How messages caught up from while the player was away show: in game chat, one line saying how many and at most the
/// last 50, each with the time it was sent; in the channel's window, every one (after a line saying how many), with the
/// time it was sent; and counted as unread.
/// </summary>
public sealed class CatchUpDisplayTests {
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 18, 30, 0, TimeSpan.Zero);
    private static readonly User Bob = new() { UserId = 2, Name = "Bob Hatter", WorldName = "Lich" };

    private static List<IncomingMessage> Missed(int count, string channelId = "aaa") =>
        Enumerable.Range(1, count)
            .Select(i => new IncomingMessage(channelId, "Tea party", Bob, false, $"missed {i}", false, Now.AddMinutes(-count + i)) { CaughtUp = true })
            .ToList();

    [Fact]
    public void AFewMissedMessagesAreAllPrintedAfterOneLine() {
        var plan = CatchUpChat.Plan("[sky]", Missed(3));
        Assert.Equal("[sky] 3 messages were sent while you were away:", plan.Header);
        Assert.Equal(["missed 1", "missed 2", "missed 3"], plan.Shown.Select(m => m.Text));
        Assert.Equal(0, plan.NotShown);
        Assert.Equal("[LGC1] 1 message was sent while you were away:", CatchUpChat.Plan("[LGC1]", Missed(1)).Header);
    }

    [Fact]
    public void ALongAbsencePrintsOnlyTheLastFiftyAndSaysWhereTheRestAre() {
        var plan = CatchUpChat.Plan("[sky]", Missed(73));
        Assert.Equal(CatchUpChat.GameChatCap, plan.Shown.Count);
        Assert.Equal("missed 24", plan.Shown[0].Text);
        Assert.Equal("missed 73", plan.Shown[^1].Text);
        Assert.Equal(23, plan.NotShown);
        Assert.Equal("[sky] 73 messages were sent while you were away. The last 50 are below; to read the 23 before them, open the channel's window " +
                     "(right-click the channel in the main window).", plan.Header);
        PlainLanguage.AssertPlain(plan.Header);

        // More than the window keeps: it says so.
        var huge = CatchUpChat.Plan("[sky]", Missed(600), historyCapacity: 500);
        Assert.EndsWith("which keeps the last 500.", huge.Header);
        Assert.Equal(550, huge.NotShown);
    }

    [Fact]
    public void ACaughtUpMessageShowsWhenItWasSentWithTheDayIfNotToday() {
        var utc = TimeZoneInfo.Utc;
        Assert.Equal("18:05", CatchUpChat.TimeLabel(Now.AddMinutes(-25), Now, utc));
        Assert.Equal("Tue 23:59", CatchUpChat.TimeLabel(new DateTimeOffset(2026, 10, 6, 23, 59, 0, TimeSpan.Zero), Now, utc));
        Assert.Equal("Thu 09:00", CatchUpChat.TimeLabel(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero), Now, utc));
        // By the clock's own time zone: just after midnight there is still "today".
        var tokyo = TimeZoneInfo.CreateCustomTimeZone("Plus9", TimeSpan.FromHours(9), "Plus9", "Plus9");
        Assert.Equal("03:00", CatchUpChat.TimeLabel(new DateTimeOffset(2026, 10, 7, 18, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 7, 18, 30, 0, TimeSpan.Zero), tokyo));
    }

    [Fact]
    public void TheWindowGetsEveryOneAfterALineSayingHowMany() {
        var history = new ChannelHistory(time: new FixedClock(Now));
        history.Add(new IncomingMessage("aaa", "Tea party", Bob, false, "before you left", false, Now.AddHours(-3)));
        var missed = Missed(3);

        Assert.Equal(3, history.AddCaughtUp(new CaughtUpMessages("aaa", "Tea party", missed)));

        var lines = history.LinesOf("aaa");
        Assert.Equal(5, lines.Length);
        Assert.Equal(HistoryLineKind.Notice, lines[1].Kind);
        Assert.Equal("3 messages were sent while you were away:", lines[1].TextFor(advanced: false));
        Assert.Equal(NoticeTone.Info, lines[1].Tone);
        Assert.Equal(["missed 1", "missed 2", "missed 3"], lines.Skip(2).Select(line => line.Message!.Text));
        Assert.All(lines.Skip(2), line => Assert.True(line.CaughtUp && line.FromOthers));
        Assert.Equal(missed.Select(m => m.Timestamp), lines.Skip(2).Select(line => line.SentAt));
        Assert.False(lines[0].CaughtUp);
        // The window's tab counts them as new for others.
        Assert.Equal(3, history.UnreadAfter("aaa", lines[0].Seq));
    }

    [Fact]
    public void TheWindowHoldsEachOnceAndNothingForAnOlderSession() {
        var history = new ChannelHistory();
        var missed = Missed(2);
        history.Add(missed[0]);

        Assert.Equal(1, history.AddCaughtUp(new CaughtUpMessages("aaa", "Tea party", missed)));
        Assert.Equal(0, history.AddCaughtUp(new CaughtUpMessages("aaa", "Tea party", missed)));
        Assert.Equal(["missed 1", "missed 2"], history.LinesOf("aaa").Where(line => line.Kind == HistoryLineKind.Message).Select(line => line.Message!.Text));

        var old = history.Generation;
        history.Clear();
        Assert.Equal(0, history.AddCaughtUp(new CaughtUpMessages("aaa", "Tea party", Missed(1, "bbb")), old));
        Assert.Empty(history.LinesOf("bbb"));
    }

    [Fact]
    public void CaughtUpMessagesCountAsUnreadOnce() {
        var unread = new UnreadCounter();
        foreach (var message in Missed(4)) {
            Assert.True(unread.Add(message));
        }

        Assert.Equal(4, unread.CountOf("aaa"));
        // The same one again (say, also delivered live) isn't counted twice.
        Assert.False(unread.Add(Missed(4)[0]));
        Assert.Equal(4, unread.Total);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
