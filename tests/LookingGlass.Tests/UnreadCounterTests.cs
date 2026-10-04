using LookingGlass.Core.Client;
using LookingGlass.Protocol;
using static LookingGlass.Tests.CommandSlotTests;

namespace LookingGlass.Tests;

/// <summary>
/// The sidebar's unread markers: messages from others since a channel was last read. A channel is
/// read by showing it in the main window, or by sending to it.
/// </summary>
public sealed class UnreadCounterTests {
    private static readonly DateTimeOffset Start = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly User Alice = new() { UserId = 1, Name = "Alice Liddell", WorldName = "Twintania" };
    private static readonly User Bob = new() { UserId = 2, Name = "Bob Hatter", WorldName = "Lich" };

    private static int _sequence;

    /// <summary>A message with a timestamp of its own, so no two are alike unless a test wants them to be.</summary>
    private static IncomingMessage From(User sender, string channelId, string text = "hello", bool own = false, DateTimeOffset? at = null) {
        return new IncomingMessage(channelId, "Channel " + channelId, sender, own, text, false, at ?? Start.AddMilliseconds(Interlocked.Increment(ref _sequence)));
    }

    [Fact]
    public void MessagesFromOthersCountPerChannelAndOwnMessagesDont() {
        var unread = new UnreadCounter(new ManualClock());

        Assert.True(unread.Add(From(Bob, "aaa")));
        Assert.True(unread.Add(From(Bob, "aaa")));
        Assert.True(unread.Add(From(Bob, "bbb")));
        Assert.False(unread.Add(From(Alice, "aaa", own: true)));

        Assert.Equal(2, unread.CountOf("aaa"));
        Assert.Equal(1, unread.CountOf("bbb"));
        Assert.Equal(0, unread.CountOf("ccc"));
        Assert.Equal(3, unread.Total);
    }

    [Fact]
    public void UnsupportedMessagesCountToo() {
        var unread = new UnreadCounter(new ManualClock());
        Assert.True(unread.Add(new IncomingMessage("aaa", "A", Bob, false, null, true, Start)));
        Assert.Equal(1, unread.CountOf("aaa"));
    }

    [Fact]
    public void SendingToAChannelReadsIt() {
        var unread = new UnreadCounter(new ManualClock());
        unread.Add(From(Bob, "aaa"));
        unread.Add(From(Bob, "aaa"));
        unread.Add(From(Bob, "bbb"));

        unread.MarkRead("aaa");

        Assert.Equal(0, unread.CountOf("aaa"));
        Assert.Equal(1, unread.CountOf("bbb"));
        Assert.Equal(1, unread.Total);

        // New messages count again from zero.
        unread.Add(From(Bob, "aaa"));
        Assert.Equal(1, unread.CountOf("aaa"));
    }

    [Fact]
    public void ShowingAChannelReadsItAndMessagesArrivingWhileItShowsDontCount() {
        var clock = new ManualClock();
        var unread = new UnreadCounter(clock);
        unread.Add(From(Bob, "aaa"));
        unread.Add(From(Bob, "bbb"));

        // The main window draws the pane every frame.
        unread.Viewing("aaa");
        Assert.Equal(0, unread.CountOf("aaa"));

        clock.Offset += TimeSpan.FromMilliseconds(16);
        unread.Viewing("aaa");
        Assert.False(unread.Add(From(Bob, "aaa")));
        Assert.True(unread.Add(From(Bob, "bbb")));
        Assert.Equal(0, unread.CountOf("aaa"));
        Assert.Equal(2, unread.CountOf("bbb"));
    }

    [Fact]
    public void AChannelStopsBeingShownWhenTheWindowStopsDrawingIt() {
        var clock = new ManualClock();
        var unread = new UnreadCounter(clock);

        unread.Viewing("aaa");
        // Closed or collapsed: the window isn't drawn, so nothing marks it as shown any more.
        clock.Offset += UnreadCounter.ViewingGrace + TimeSpan.FromMilliseconds(1);
        Assert.True(unread.Add(From(Bob, "aaa")));
        Assert.Equal(1, unread.CountOf("aaa"));
    }

    [Fact]
    public void ShowingNothingEndsViewingAtOnce() {
        var unread = new UnreadCounter(new ManualClock());

        unread.Viewing("aaa");
        unread.Viewing(null);
        Assert.True(unread.Add(From(Bob, "aaa")));

        // Showing another channel ends it too.
        unread.Viewing("aaa");
        unread.Viewing("bbb");
        Assert.True(unread.Add(From(Bob, "aaa")));
        Assert.False(unread.Add(From(Bob, "bbb")));
        Assert.Equal(2, unread.CountOf("aaa"));
    }

    [Fact]
    public void TheSameMessageDeliveredTwiceCountsOnce() {
        var unread = new UnreadCounter(new ManualClock());
        var message = From(Bob, "aaa", "once");

        Assert.True(unread.Add(message));
        Assert.False(unread.Add(message with { ChannelName = "renamed meanwhile" }));
        Assert.Equal(1, unread.CountOf("aaa"));

        // Same text, but a different time, sender or channel: different messages.
        Assert.True(unread.Add(message with { Timestamp = message.Timestamp.AddMilliseconds(1) }));
        Assert.True(unread.Add(message with { Sender = Alice }));
        Assert.True(unread.Add(message with { ChannelId = "bbb" }));
        Assert.Equal(3, unread.CountOf("aaa"));
        Assert.Equal(1, unread.CountOf("bbb"));
    }

    [Fact]
    public void ARestartStartsAgainFromZeroWithoutCountingReplaysAgain() {
        var unread = new UnreadCounter(new ManualClock());
        var before = From(Bob, "aaa", "before the restart");
        unread.Add(before);
        unread.Add(From(Bob, "bbb"));
        unread.Viewing("bbb");

        unread.Reset();

        Assert.Equal(0, unread.Total);
        Assert.Equal(0, unread.CountOf("aaa"));

        // A message the last session already delivered isn't new.
        Assert.False(unread.Add(before));
        // Nothing is shown until the window draws again.
        Assert.True(unread.Add(From(Bob, "bbb")));
        Assert.Equal(1, unread.Total);
    }

    [Fact]
    public void RememberingDeliveredMessagesIsBounded() {
        var unread = new UnreadCounter(new ManualClock());
        var first = From(Bob, "aaa", "first");
        unread.Add(first);
        for (var i = 0; i < UnreadCounter.Remembered; i++) {
            unread.Add(From(Bob, "aaa"));
        }

        unread.MarkRead("aaa");
        // Long forgotten: only the most recent messages are remembered.
        Assert.True(unread.Add(first));
    }

    [Fact]
    public void CountsOfChannelsYouLeftAreDroppedOnlyOnceTheListIsComplete() {
        var unread = new UnreadCounter(new ManualClock());
        unread.Add(From(Bob, "aaa"));
        unread.Add(From(Bob, "bbb"));

        Assert.False(unread.Retain(Snapshot(ConnectionState.Ready, false)));
        Assert.False(unread.Retain(Snapshot(ConnectionState.Reconnecting, true, "aaa")));
        Assert.Equal(2, unread.Total);

        Assert.True(unread.Retain(Snapshot(ConnectionState.Ready, true, "aaa", "ccc")));
        Assert.Equal(1, unread.CountOf("aaa"));
        Assert.Equal(0, unread.CountOf("bbb"));
        Assert.Equal(1, unread.Total);
        Assert.False(unread.Retain(Snapshot(ConnectionState.Ready, true, "aaa", "ccc")));
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "1")]
    [InlineData(99, "99")]
    [InlineData(100, "99+")]
    [InlineData(12345, "99+")]
    public void CountsAreShownUpTo99(int count, string shown) {
        Assert.Equal(shown, UnreadCounter.Format(count));
    }
}
