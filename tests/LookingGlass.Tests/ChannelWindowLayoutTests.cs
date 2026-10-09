using System.Text.Json;
using LookingGlass.Core.Client;
using LookingGlass.Protocol;
using static LookingGlass.Tests.CommandSlotTests;

namespace LookingGlass.Tests;

/// <summary>
/// Channel windows as they are remembered: their tabs, the one selected, and where they were; opening one, adding,
/// closing and reordering tabs, and following the channel list. Then "Show in game chat", and how windows read a
/// channel for the main window's unread counts.
/// </summary>
public sealed class ChannelWindowLayoutTests {
    [Fact]
    public void OpeningMakesANewWindowWithTheChannelAsItsOnlyTabEveryTime() {
        var windows = new List<ChannelWindowLayout>();
        var first = ChannelWindowLayouts.Open(windows, "aaa");
        var second = ChannelWindowLayouts.Open(windows, "aaa");

        Assert.Equal(2, windows.Count);
        Assert.Equal(["aaa"], first.Tabs);
        Assert.Equal("aaa", first.Selected);
        Assert.NotEqual(first.Id, second.Id);
        Assert.False(string.IsNullOrEmpty(first.Id));
        Assert.Null(first.Place);
    }

    [Fact]
    public void AddingATabSelectsItAndAddingOneItHasOnlySelectsIt() {
        var windows = new List<ChannelWindowLayout>();
        var window = ChannelWindowLayouts.Open(windows, "aaa");

        Assert.True(ChannelWindowLayouts.AddTab(window, "bbb"));
        Assert.True(ChannelWindowLayouts.AddTab(window, "ccc"));
        Assert.Equal(["aaa", "bbb", "ccc"], window.Tabs);
        Assert.Equal("ccc", window.Selected);

        Assert.False(ChannelWindowLayouts.AddTab(window, "aaa"));
        Assert.Equal(["aaa", "bbb", "ccc"], window.Tabs);
        Assert.Equal("aaa", window.Selected);
    }

    [Fact]
    public void ClosingTheSelectedTabSelectsTheNextAndTheLastTabClosesTheWindow() {
        var window = new ChannelWindowLayout { Id = "w", Tabs = ["aaa", "bbb", "ccc"], Selected = "bbb" };

        Assert.False(ChannelWindowLayouts.CloseTab(window, "bbb"));
        Assert.Equal(["aaa", "ccc"], window.Tabs);
        Assert.Equal("ccc", window.Selected);

        // The last one: the one before takes its place.
        Assert.False(ChannelWindowLayouts.CloseTab(window, "ccc"));
        Assert.Equal("aaa", window.Selected);

        // A tab not selected leaves the selection alone; one it doesn't have changes nothing.
        window.Tabs.Add("ddd");
        Assert.False(ChannelWindowLayouts.CloseTab(window, "ddd"));
        Assert.False(ChannelWindowLayouts.CloseTab(window, "zzz"));
        Assert.Equal("aaa", window.Selected);

        Assert.True(ChannelWindowLayouts.CloseTab(window, "aaa"));
        Assert.Empty(window.Tabs);
        Assert.Null(window.Selected);
    }

    [Fact]
    public void TabsAreReorderedOnlyToTheSameTabs() {
        var window = new ChannelWindowLayout { Id = "w", Tabs = ["aaa", "bbb", "ccc"], Selected = "aaa" };

        Assert.False(ChannelWindowLayouts.Reorder(window, ["aaa", "bbb", "ccc"]));
        Assert.True(ChannelWindowLayouts.Reorder(window, ["ccc", "aaa", "bbb"]));
        Assert.Equal(["ccc", "aaa", "bbb"], window.Tabs);
        Assert.Equal("aaa", window.Selected);

        // An order seen before a tab was added or closed is out of date.
        Assert.False(ChannelWindowLayouts.Reorder(window, ["aaa", "ccc"]));
        Assert.False(ChannelWindowLayouts.Reorder(window, ["aaa", "bbb", "ddd"]));
        Assert.False(ChannelWindowLayouts.Reorder(window, ["aaa", "aaa", "bbb"]));
        Assert.Equal(["ccc", "aaa", "bbb"], window.Tabs);
    }

    [Fact]
    public void ThePlusOffersTheChannelsTheWindowDoesntHave() {
        var window = new ChannelWindowLayout { Id = "w", Tabs = ["bbb"], Selected = "bbb" };
        Assert.Equal(["aaa", "ccc"], ChannelWindowLayouts.Addable(window, ["aaa", "bbb", "ccc"]));
    }

    [Fact]
    public void WhichWindowsShowAChannel() {
        var windows = new List<ChannelWindowLayout>();
        var one = ChannelWindowLayouts.Open(windows, "aaa");
        var two = ChannelWindowLayouts.Open(windows, "bbb");
        ChannelWindowLayouts.AddTab(two, "aaa");

        Assert.Equal([one, two], ChannelWindowLayouts.Showing(windows, "aaa"));
        Assert.Equal([two], ChannelWindowLayouts.Showing(windows, "bbb"));
        Assert.Empty(ChannelWindowLayouts.Showing(windows, "ccc"));
    }

    [Fact]
    public void ChannelsYouLeftAreDroppedOnlyAgainstTheCompleteListAndEmptyWindowsWithThem() {
        var windows = new List<ChannelWindowLayout> {
            new() { Id = "one", Tabs = ["aaa", "bbb"], Selected = "bbb" },
            new() { Id = "two", Tabs = ["bbb"], Selected = "bbb" },
            new() { Id = "three", Tabs = ["ccc"], Selected = "ccc" },
        };

        // What a restart publishes before the list is in.
        Assert.False(ChannelWindowLayouts.Sync(windows, Snapshot(ConnectionState.Ready, false, "aaa")));
        Assert.False(ChannelWindowLayouts.Sync(windows, Snapshot(ConnectionState.Reconnecting, true, "aaa")));
        Assert.Equal(3, windows.Count);

        Assert.True(ChannelWindowLayouts.Sync(windows, Snapshot(ConnectionState.Ready, true, "aaa", "ccc")));
        Assert.Equal(["one", "three"], windows.Select(window => window.Id));
        Assert.Equal(["aaa"], windows[0].Tabs);
        Assert.Equal("aaa", windows[0].Selected);

        Assert.False(ChannelWindowLayouts.Sync(windows, Snapshot(ConnectionState.Ready, true, "aaa", "ccc")));
    }

    [Fact]
    public void WhatWasSavedIsTidied() {
        var windows = new List<ChannelWindowLayout> {
            new() { Id = "one", Tabs = ["aaa", "aaa", "", "bbb"], Selected = "zzz" },
            new() { Id = "", Tabs = ["aaa"], Selected = "aaa" },
            new() { Id = "one", Tabs = ["bbb"], Selected = "bbb" },
            new() { Id = "empty", Tabs = [], Selected = null },
        };

        Assert.True(ChannelWindowLayouts.Sync(windows, Snapshot(ConnectionState.Connecting, false)));
        var window = Assert.Single(windows);
        Assert.Equal(["aaa", "bbb"], window.Tabs);
        Assert.Equal("aaa", window.Selected);
    }

    [Fact]
    public void AHandEditedFileWithNothingWhereWindowsOrTabsShouldBeIsTidiedNotThrown() {
        var windows = new List<ChannelWindowLayout> {
            null!,
            new() { Id = "one", Tabs = null!, Selected = "aaa" },
            new() { Id = "two", Tabs = ["aaa"], Selected = null },
        };

        Assert.True(ChannelWindowLayouts.Sync(windows, Snapshot(ConnectionState.Ready, true, "aaa")));
        var window = Assert.Single(windows);
        Assert.Equal("two", window.Id);
        Assert.Equal("aaa", window.Selected);
    }

    [Fact]
    public void LayoutsSaveAndLoadAsTheyWere() {
        var windows = new List<ChannelWindowLayout>();
        var window = ChannelWindowLayouts.Open(windows, "aaa");
        ChannelWindowLayouts.AddTab(window, "bbb");
        ChannelWindowLayouts.Reorder(window, ["bbb", "aaa"]);
        window.Place = new WindowPlace(100, 200.5f, 420, 330);
        var saved = new Dictionary<string, List<ChannelWindowLayout>> { ["wss://chat.example.com/ws"] = windows };

        var loaded = JsonSerializer.Deserialize<Dictionary<string, List<ChannelWindowLayout>>>(JsonSerializer.Serialize(saved))!;

        var back = Assert.Single(loaded["wss://chat.example.com/ws"]);
        Assert.Equal(window.Id, back.Id);
        Assert.Equal(["bbb", "aaa"], back.Tabs);
        Assert.Equal("bbb", back.Selected);
        Assert.Equal(window.Place, back.Place);
    }

    [Fact]
    public void PlacesWithinHalfAPixelAreTheSame() {
        var place = new WindowPlace(10, 20, 300, 200);
        Assert.True(place.Near(place with { X = 10.3f }));
        Assert.False(place.Near(place with { Width = 301 }));
        Assert.False(place.Near(null));
    }

    // ================================================================ show in game chat

    [Fact]
    public void EveryChannelShowsInGameChatUntilTurnedOff() {
        var off = new HashSet<string>();
        Assert.True(GameChatChannels.Shows(off, "aaa"));

        Assert.True(GameChatChannels.Set(off, "aaa", show: false));
        Assert.False(GameChatChannels.Set(off, "aaa", show: false));
        Assert.False(GameChatChannels.Shows(off, "aaa"));
        Assert.True(GameChatChannels.Shows(off, "bbb"));

        Assert.True(GameChatChannels.Set(off, "aaa", show: true));
        Assert.Empty(off);
    }

    [Fact]
    public void AChannelOffGameChatStillGetsItsWarningsThere() {
        var off = new HashSet<string> { "aaa" };
        var joined = new SessionNotice(NoticeLevel.Info, "Bob joined.", "aaa");
        var warning = SessionNotice.Of(NoticeLevel.Warning, PlainMessages.MessageFailedChecks("Bob"), "aaa");
        var critical = SessionNotice.Of(NoticeLevel.Info, Wording.Same("x", NoticeKind.MembershipForked), "aaa");

        Assert.False(GameChatChannels.NoticeToGameChat(off, joined));
        Assert.True(GameChatChannels.NoticeToGameChat(off, warning));
        Assert.True(GameChatChannels.NoticeToGameChat(off, joined with { ChannelId = "bbb" }));
        Assert.True(GameChatChannels.NoticeToGameChat(off, joined with { ChannelId = null }));
        Assert.True(GameChatChannels.NoticeToGameChat(new HashSet<string>(), joined));
        // A critical one at Info level (none is raised so today): only the level decides, so it goes to the history only.
        Assert.False(GameChatChannels.NoticeToGameChat(off, critical));
    }

    [Fact]
    public void TheSettingGoesWithItsChannelOnlyAgainstTheCompleteList() {
        var off = new HashSet<string> { "aaa", "bbb" };
        Assert.False(GameChatChannels.Sync(off, Snapshot(ConnectionState.Ready, false, "aaa")));
        Assert.True(GameChatChannels.Sync(off, Snapshot(ConnectionState.Ready, true, "aaa")));
        Assert.Equal(["aaa"], off);
    }

    // ================================================================ unread, with windows

    private static readonly DateTimeOffset Start = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly User Bob = new() { UserId = 2, Name = "Bob Hatter", WorldName = "Lich" };
    private static int _sequence;

    private static IncomingMessage From(string channelId) =>
        new(channelId, "Channel " + channelId, Bob, false, "hi", false, Start.AddMilliseconds(Interlocked.Increment(ref _sequence)));

    [Fact]
    public void AChannelReadInAWindowIsReadInTheChannelListToo() {
        var clock = new ManualClock();
        var unread = new UnreadCounter(clock);
        unread.Add(From("aaa"));
        unread.Add(From("bbb"));

        // The main window shows one channel, a focused channel window another: both are read, and stay so.
        unread.Viewing("bbb");
        unread.Viewing("window:one", "aaa");
        Assert.Equal(0, unread.Total);
        Assert.False(unread.Add(From("aaa")));
        Assert.False(unread.Add(From("bbb")));

        // The window lost the focus (or closed): what arrives counts again; the main window still reads its channel.
        unread.Viewing("window:one", null);
        Assert.True(unread.Add(From("aaa")));
        Assert.False(unread.Add(From("bbb")));

        // A window that stopped drawing (hidden) counts as shown only for a moment.
        unread.Viewing("window:two", "ccc");
        clock.Offset += UnreadCounter.ViewingGrace + TimeSpan.FromMilliseconds(1);
        Assert.True(unread.Add(From("ccc")));
    }

    [Fact]
    public void AChannelOffGameChatInNoWindowStillCountsAsUnread() {
        // Where a message is shown doesn't change what counts: only a window showing the channel does.
        var unread = new UnreadCounter(new ManualClock());
        var off = new HashSet<string> { "aaa" };
        Assert.False(GameChatChannels.Shows(off, "aaa"));
        Assert.True(unread.Add(From("aaa")));
        Assert.Equal(1, unread.CountOf("aaa"));
    }

    [Fact]
    public void TheMainWindowReadsOnlyAChannelWhoseMessagesGoToGameChat() {
        var off = new HashSet<string> { "aaa" };
        Assert.True(UnreadCounter.MainWindowReads(false, off, "bbb"));
        Assert.False(UnreadCounter.MainWindowReads(false, off, "aaa"));
        Assert.False(UnreadCounter.MainWindowReads(true, new HashSet<string>(), "bbb"));

        // Its channel pane shows no messages: one seen only in windows stays counted there until a window tab shows it.
        var unread = new UnreadCounter(new ManualClock());
        unread.Add(From("aaa"));
        unread.Viewing(UnreadCounter.MainWindowReads(false, off, "aaa") ? "aaa" : null);
        Assert.Equal(1, unread.CountOf("aaa"));
        Assert.True(unread.Add(From("aaa")));
        unread.Viewing("window:one", "aaa");
        Assert.Equal(0, unread.CountOf("aaa"));
    }

    [Fact]
    public void ANewSessionForgetsWhatWindowsShowed() {
        var unread = new UnreadCounter(new ManualClock());
        unread.Viewing("window:one", "aaa");
        unread.Reset();
        Assert.True(unread.Add(From("aaa")));
    }
}
