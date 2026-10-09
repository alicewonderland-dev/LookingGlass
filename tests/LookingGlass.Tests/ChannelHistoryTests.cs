using LookingGlass.Core.Client;
using LookingGlass.Protocol;
using static LookingGlass.Tests.CommandSlotTests;

namespace LookingGlass.Tests;

/// <summary>
/// What channel windows show: each channel's messages since login, your own included, LookingGlass's notices about it, and
/// feedback on what was typed there; oldest first, a few hundred lines per channel, in memory only, from zero at each login.
/// </summary>
public sealed class ChannelHistoryTests {
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly User Alice = new() { UserId = 1, Name = "Alice Liddell", WorldName = "Twintania" };
    private static readonly User Bob = new() { UserId = 2, Name = "Bob Hatter", WorldName = "Lich" };

    private static int _sequence;

    /// <summary>A message with a timestamp of its own, so no two are alike unless a test wants them to be.</summary>
    private static IncomingMessage From(User sender, string channelId, string text = "hello", bool own = false, DateTimeOffset? at = null) =>
        new(channelId, "Channel " + channelId, sender, own, text, false, at ?? Start.AddMilliseconds(Interlocked.Increment(ref _sequence)));

    [Fact]
    public void LinesAreKeptPerChannelInTheOrderTheyArrivedYourOwnIncluded() {
        var history = new ChannelHistory();

        // Bob's clock is behind: the order is arrival, not the sender's time.
        history.Add(From(Bob, "aaa", "first", at: Start.AddMinutes(-5)));
        history.Add(From(Alice, "aaa", "mine", own: true));
        history.Add(From(Bob, "bbb", "elsewhere"));
        history.Add(From(Bob, "aaa", "third"));

        var lines = history.LinesOf("aaa");
        Assert.Equal(["first", "mine", "third"], lines.Select(line => line.Message!.Text));
        Assert.True(lines.Select(line => line.Seq).SequenceEqual(lines.Select(line => line.Seq).Order()));
        Assert.Equal([false, true, false], lines.Select(line => line.IsOwn));
        Assert.Equal(["Bob Hatter", "Alice Liddell", "Bob Hatter"], lines.Select(line => line.Sender!.Name));
        Assert.Equal(Start.AddMinutes(-5), lines[0].SentAt);
        Assert.Equal("elsewhere", Assert.Single(history.LinesOf("bbb")).Message!.Text);
        Assert.Empty(history.LinesOf("ccc"));
    }

    [Fact]
    public void LinksAndUnsupportedMessagesAreKeptAsTheyCame() {
        var history = new ChannelHistory();
        var link = new MessageLink(5, 8, new ChatLink.Item(4551));
        history.Add(From(Bob, "aaa", "look [Potion]") with { Links = [link] });
        history.Add(new IncomingMessage("aaa", "A", Bob, false, null, true, Start));

        var lines = history.LinesOf("aaa");
        Assert.Equal(link, Assert.Single(lines[0].Message!.Links));
        Assert.True(lines[1].Unsupported);
        Assert.Null(lines[1].Message);
        Assert.True(lines[1].FromOthers);
    }

    [Fact]
    public void AMessageDeliveredTwiceIsShownOnce() {
        var history = new ChannelHistory();
        var message = From(Bob, "aaa");

        Assert.True(history.Add(message));
        Assert.False(history.Add(message));
        Assert.Single(history.LinesOf("aaa"));

        // The same words at another time are another message.
        Assert.True(history.Add(From(Bob, "aaa")));
        Assert.Equal(2, history.LinesOf("aaa").Length);
    }

    [Fact]
    public void EachChannelKeepsItsNewestLinesUpToTheCap() {
        var history = new ChannelHistory(capacity: 3);
        for (var i = 1; i <= 5; i++) {
            history.Add(From(Bob, "aaa", $"m{i}"));
        }

        history.Add(From(Bob, "bbb", "other"));
        Assert.Equal(["m3", "m4", "m5"], history.LinesOf("aaa").Select(line => line.Message!.Text));
        Assert.Single(history.LinesOf("bbb"));

        // Notices and feedback take their places too.
        history.AddNotice(new SessionNotice(NoticeLevel.Info, "Bob left Channel aaa.", "aaa"));
        history.AddFeedback("aaa", NoticeTone.Info, "Not sent: not connected to LookingGlass.");
        Assert.Equal([HistoryLineKind.Message, HistoryLineKind.Notice, HistoryLineKind.Feedback], history.LinesOf("aaa").Select(line => line.Kind));
    }

    [Fact]
    public void AMessageThatFellOutCanBeShownAgain() {
        var history = new ChannelHistory(capacity: 2);
        var first = From(Bob, "aaa", "first");
        history.Add(first);
        history.Add(From(Bob, "aaa", "second"));
        history.Add(From(Bob, "aaa", "third"));

        // Nothing remembers it once it's gone, so the set of held messages doesn't grow without end.
        Assert.True(history.Add(first));
    }

    [Fact]
    public void TheDefaultCapIsAFewHundredLines() {
        var history = new ChannelHistory();
        Assert.Equal(500, history.Capacity);
        for (var i = 0; i < 520; i++) {
            history.Add(From(Bob, "aaa", $"m{i}"));
        }

        var lines = history.LinesOf("aaa");
        Assert.Equal(500, lines.Length);
        Assert.Equal("m20", lines[0].Message!.Text);
        Assert.Equal("m519", lines[^1].Message!.Text);
    }

    [Fact]
    public void NoticesAboutAChannelAreKeptInBothModesWordsAndOthersArent() {
        var history = new ChannelHistory();
        var joined = new SessionNotice(NoticeLevel.Info, "Bob Hatter@Lich joined Sky.", "aaa");
        var reVerified = SessionNotice.Of(NoticeLevel.Info, PlainMessages.ReVerifiedWording("Bob Hatter@Lich", false), "aaa");
        var dropped = SessionNotice.Of(NoticeLevel.Warning, PlainMessages.MessageFailedChecks("Bob Hatter"), "aaa");

        Assert.True(history.AddNotice(joined));
        Assert.True(history.AddNotice(reVerified));
        Assert.True(history.AddNotice(dropped));
        Assert.False(history.AddNotice(new SessionNotice(NoticeLevel.Info, "Connected.")));
        Assert.False(history.AddNotice(new SessionNotice(NoticeLevel.Debug, "Details.", "aaa")));

        var lines = history.LinesOf("aaa");
        Assert.Equal(3, lines.Length);
        Assert.All(lines, line => Assert.False(line.FromOthers));
        Assert.Equal([NoticeTone.Info, NoticeTone.Info, NoticeTone.Warning], lines.Select(line => line.Tone));
        Assert.Equal(reVerified.Plain, lines[1].TextFor(advanced: false));
        Assert.Equal(reVerified.Text, lines[1].TextFor(advanced: true));
        foreach (var line in lines) {
            PlainLanguage.AssertShownInBothModes(line.Notice!, ["Bob Hatter@Lich", "Bob Hatter", "Sky"]);
        }
    }

    [Fact]
    public void CriticalNoticesKeepTheirTone() {
        var history = new ChannelHistory();
        history.AddNotice(SessionNotice.Of(NoticeLevel.Info, Wording.Same("x") with { Kind = NoticeKind.MembershipForked }, "aaa"));
        Assert.Equal(NoticeTone.Critical, Assert.Single(history.LinesOf("aaa")).Tone);
    }

    [Fact]
    public void ARenameIsSaidOnceTheNameWasKnownAndTheSameInBothModes() {
        var history = new ChannelHistory();
        var unnamed = Named(("aaa", null), ("bbb", "Tea Party"));
        var named = Named(("aaa", "Sky"), ("bbb", "Tea Party"));
        var renamed = Named(("aaa", "Sky"), ("bbb", "Mad Tea Party"));

        // A name becoming known isn't a rename; seeing the same name again says nothing.
        Assert.False(history.NoteNames(unnamed));
        Assert.False(history.NoteNames(named));
        Assert.False(history.NoteNames(named));
        Assert.Empty(history.LinesOf("aaa"));

        Assert.True(history.NoteNames(renamed));
        var line = Assert.Single(history.LinesOf("bbb"));
        Assert.Equal(HistoryLineKind.Notice, line.Kind);
        Assert.Equal(ChannelHistory.Renamed("Mad Tea Party"), line.TextFor(advanced: true));
        Assert.Equal(line.TextFor(advanced: true), line.TextFor(advanced: false));
        PlainLanguage.AssertPlain(line.TextFor(advanced: false)!.Replace("Mad Tea Party", ""));
        Assert.Empty(history.LinesOf("aaa"));
    }

    [Fact]
    public void FeedbackIsKeptAsSaid() {
        var history = new ChannelHistory();
        history.AddFeedback("aaa", NoticeTone.Info, "Not sent: you're sending too fast.");
        var line = Assert.Single(history.LinesOf("aaa"));
        Assert.Equal(HistoryLineKind.Feedback, line.Kind);
        Assert.Equal("Not sent: you're sending too fast.", line.TextFor(advanced: false));
        Assert.Equal(NoticeTone.Info, line.Tone);
        Assert.Null(line.Sender);
    }

    [Fact]
    public void ANewSessionStartsFromNothingAndTheOldOnesLinesAreDropped() {
        var history = new ChannelHistory();
        var first = history.Clear();
        history.Add(From(Bob, "aaa"), first);
        history.AddNotice(new SessionNotice(NoticeLevel.Info, "Bob left.", "aaa"), first);
        Assert.Equal(2, history.LinesOf("aaa").Length);

        // Logged out, another character, another server: all of it goes.
        var second = history.Clear();
        Assert.NotEqual(first, second);
        Assert.Equal(second, history.Generation);
        Assert.Empty(history.LinesOf("aaa"));

        // What the old session had already sent on its way doesn't land in the new one.
        Assert.False(history.Add(From(Bob, "aaa"), first));
        Assert.False(history.AddNotice(new SessionNotice(NoticeLevel.Info, "Bob joined.", "aaa"), first));
        Assert.Empty(history.LinesOf("aaa"));

        Assert.True(history.Add(From(Bob, "aaa"), second));
        Assert.Single(history.LinesOf("aaa"));
    }

    [Fact]
    public void FeedbackAboutAMessageTypedBeforeTheSessionChangedIsDropped() {
        var history = new ChannelHistory();
        var typedIn = history.Clear();
        Assert.True(history.AddFeedback("aaa", NoticeTone.Info, "Not sent: too fast.", typedIn));

        // The send failed after a relog: its "Not sent" belongs to the old session.
        history.Clear();
        Assert.False(history.AddFeedback("aaa", NoticeTone.Info, "Not sent: disconnected.", typedIn));
        Assert.Empty(history.LinesOf("aaa"));
        Assert.True(history.AddFeedback("aaa", NoticeTone.Info, "Not sent: not connected to LookingGlass."));
    }

    [Fact]
    public void AMessageTheLastSessionHadIsShownAgainInTheNext() {
        var history = new ChannelHistory();
        var message = From(Bob, "aaa");
        history.Add(message, history.Clear());
        Assert.True(history.Add(message, history.Clear()));
    }

    [Fact]
    public void LinesAreNumberedOnAcrossChannelsAndSessions() {
        var history = new ChannelHistory();
        history.Add(From(Bob, "aaa"));
        var before = history.LastSeq("aaa");
        history.Clear();
        history.Add(From(Bob, "bbb"));
        Assert.True(history.LastSeq("bbb") > before);
        Assert.Equal(0, history.LastSeq("aaa"));
    }

    [Fact]
    public void ATabsUnreadCountIsMessagesFromOthersAfterItWasLastShown() {
        var history = new ChannelHistory();
        history.Add(From(Bob, "aaa"));
        var seen = history.LastSeq("aaa");
        Assert.Equal(0, history.FromOthersAfter("aaa", seen));

        history.Add(From(Bob, "aaa"));
        history.Add(From(Alice, "aaa", own: true));
        history.AddNotice(new SessionNotice(NoticeLevel.Info, "Bob left.", "aaa"));
        history.AddFeedback("aaa", NoticeTone.Info, "Not sent.");
        history.Add(From(Bob, "aaa"));
        history.Add(From(Bob, "bbb"));

        Assert.Equal(2, history.FromOthersAfter("aaa", seen));
        Assert.Equal(3, history.FromOthersAfter("aaa", 0));
        Assert.Equal(0, history.FromOthersAfter("aaa", history.LastSeq("aaa")));
        Assert.Equal(0, history.FromOthersAfter("ccc", 0));
    }

    [Fact]
    public void TalkingInAChannelReadsWhatCameBefore() {
        var history = new ChannelHistory();
        history.Add(From(Bob, "aaa"));
        history.Add(From(Bob, "aaa"));
        Assert.Equal(2, history.UnreadAfter("aaa", 0));

        // Your own message (from any window, or the chat box): what came before it is read, as in the channel list.
        history.Add(From(Alice, "aaa", own: true));
        Assert.Equal(0, history.UnreadAfter("aaa", 0));

        history.AddNotice(new SessionNotice(NoticeLevel.Info, "Bob left.", "aaa"));
        history.Add(From(Bob, "aaa"));
        history.Add(From(Bob, "bbb"));
        Assert.Equal(1, history.UnreadAfter("aaa", 0));
        // Seen later than your own message: from there.
        Assert.Equal(0, history.UnreadAfter("aaa", history.LastSeq("aaa")));
        Assert.Equal(1, history.UnreadAfter("bbb", 0));
        Assert.Equal(0, history.UnreadAfter("ccc", 0));
    }

    [Fact]
    public void ChannelsYouLeftAreForgottenOnlyAgainstTheCompleteList() {
        var history = new ChannelHistory();
        history.Add(From(Bob, "aaa"));
        history.Add(From(Bob, "bbb"));

        Assert.False(history.Retain(Snapshot(ConnectionState.Ready, false, "aaa")));
        Assert.False(history.Retain(Snapshot(ConnectionState.Reconnecting, true, "aaa")));
        Assert.Single(history.LinesOf("bbb"));

        Assert.True(history.Retain(Snapshot(ConnectionState.Ready, true, "aaa")));
        Assert.Empty(history.LinesOf("bbb"));
        Assert.Single(history.LinesOf("aaa"));
    }

    [Fact]
    public async Task LinesCanBeReadWhileMessagesArrive() {
        var history = new ChannelHistory(capacity: 50);
        var adding = Task.Run(() => {
            for (var i = 0; i < 2000; i++) {
                history.Add(From(Bob, "aaa", $"m{i}"));
            }
        }, TestContext.Current.CancellationToken);

        while (!adding.IsCompleted) {
            var lines = history.LinesOf("aaa");
            Assert.True(lines.Length <= 50);
            Assert.True(lines.Select(line => line.Seq).SequenceEqual(lines.Select(line => line.Seq).Order()));
        }

        await adding;
        Assert.Equal("m1999", history.LinesOf("aaa")[^1].Message!.Text);
    }

    private static SessionSnapshot Named(params (string Id, string? Name)[] channels) => SessionSnapshot.Empty with {
        State = ConnectionState.Ready,
        ChannelsLoaded = true,
        Channels = [.. channels.Select(channel => new ChannelView(channel.Id, channel.Name, 0, 0, true, false, Rank.Member, []))],
    };
}
