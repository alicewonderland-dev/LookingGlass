using LookingGlass.Core.Client;
using static LookingGlass.Tests.CommandSlotTests;

namespace LookingGlass.Tests;

/// <summary>
/// "Show LookingGlass messages only in windows": what still goes to game chat while it is on, where a window is found for
/// a channel no window shows, and the channels waiting for one while the game is busy (combat, a cutscene, a loading screen).
/// </summary>
public sealed class WindowsOnlyTests {
    private static readonly HashSet<string> NoneOff = new();

    /// <summary>The player is in "aaa" and "bbb".</summary>
    private static readonly SessionSnapshot Joined = Snapshot(ConnectionState.Ready, true, "aaa", "bbb");

    // ================================================================ what goes to game chat

    [Fact]
    public void WhileOnNoChannelsMessagesGoToGameChatWhateverItsOwnSettingSays() {
        var off = new HashSet<string> { "aaa" };

        Assert.False(WindowsOnly.MessageToGameChat(true, NoneOff, "aaa"));
        Assert.False(WindowsOnly.MessageToGameChat(true, off, "aaa"));
        Assert.False(WindowsOnly.MessageToGameChat(true, off, "bbb"));

        // Off, each channel's own "Show in game chat" decides, as before.
        Assert.True(WindowsOnly.MessageToGameChat(false, NoneOff, "aaa"));
        Assert.False(WindowsOnly.MessageToGameChat(false, off, "aaa"));
        Assert.True(WindowsOnly.MessageToGameChat(false, off, "bbb"));
    }

    [Fact]
    public void WhileOnAChannelsInformationLinesStayOutOfGameChatButItsWarningsDont() {
        var joined = new SessionNotice(NoticeLevel.Info, "Bob joined.", "aaa");
        var warning = SessionNotice.Of(NoticeLevel.Warning, PlainMessages.MessageFailedChecks("Bob"), "aaa");
        var error = new SessionNotice(NoticeLevel.Error, "Couldn't read the channel.", "aaa");
        // Dark red whatever its level: shown light or dark red means it goes to game chat.
        var critical = SessionNotice.Of(NoticeLevel.Info, Wording.Same("x", NoticeKind.MembershipForked), "aaa");

        Assert.False(WindowsOnly.NoticeToGameChat(true, NoneOff, joined, Joined));
        Assert.True(WindowsOnly.NoticeToGameChat(true, NoneOff, warning, Joined));
        Assert.True(WindowsOnly.NoticeToGameChat(true, NoneOff, error, Joined));
        Assert.True(WindowsOnly.NoticeToGameChat(true, NoneOff, critical, Joined));
        // One about no channel in particular (the connection, your identity) isn't a channel's line: it goes to game chat.
        Assert.True(WindowsOnly.NoticeToGameChat(true, NoneOff, joined with { ChannelId = null }, Joined));
    }

    [Fact]
    public void ALineForAChannelNoWindowCanShowStillGoesToGameChat() {
        // "Bob invited you to sky": about a channel the player isn't in (yet), so no window can show it.
        var invited = new SessionNotice(NoticeLevel.Info, "Bob invited you to \"sky\".", "invite");
        Assert.True(WindowsOnly.NoticeToGameChat(true, NoneOff, invited, Joined));
        Assert.False(WindowsOnly.NoticeWantsWindow(true, invited, Joined));

        // A place from the user's old keys has no window either.
        var withOld = Joined with { Channels = [Joined.Channels[0], Joined.Channels[1] with { OldKeyMembership = true }] };
        var old = new SessionNotice(NoticeLevel.Info, "Bob joined.", "bbb");
        Assert.True(WindowsOnly.NoticeToGameChat(true, NoneOff, old, withOld));
        Assert.False(WindowsOnly.NoticeWantsWindow(true, old, withOld));
        Assert.False(WindowsOnly.CanHaveWindow(withOld, "bbb"));
        Assert.True(WindowsOnly.CanHaveWindow(withOld, "aaa"));
    }

    [Fact]
    public void WhileOffANoticeGoesWhereItDidBefore() {
        var off = new HashSet<string> { "aaa" };
        var joined = new SessionNotice(NoticeLevel.Info, "Bob joined.", "aaa");
        var critical = SessionNotice.Of(NoticeLevel.Info, Wording.Same("x", NoticeKind.MembershipForked), "aaa");
        foreach (var notice in new[] { joined, joined with { ChannelId = "bbb" }, joined with { ChannelId = null }, critical }) {
            Assert.Equal(GameChatChannels.NoticeToGameChat(off, notice), WindowsOnly.NoticeToGameChat(false, off, notice, Joined));
        }
    }

    [Fact]
    public void OnlyALineKeptFromGameChatByWindowsOnlyAsksForAWindow() {
        var joined = new SessionNotice(NoticeLevel.Info, "Bob joined.", "aaa");
        var warning = new SessionNotice(NoticeLevel.Warning, "Careful.", "aaa");

        Assert.True(WindowsOnly.NoticeWantsWindow(true, joined, Joined));
        Assert.False(WindowsOnly.NoticeWantsWindow(false, joined, Joined));
        // In game chat anyway, or about no channel, or never shown (debug): nothing to open.
        Assert.False(WindowsOnly.NoticeWantsWindow(true, warning, Joined));
        Assert.False(WindowsOnly.NoticeWantsWindow(true, joined with { ChannelId = null }, Joined));
        Assert.False(WindowsOnly.NoticeWantsWindow(true, joined with { Level = NoticeLevel.Debug }, Joined));
    }

    // ================================================================ where a window is found

    [Fact]
    public void WithNoWindowsAChannelOpensInANewOneEitherWay() {
        foreach (var how in new[] { WindowOpening.AddToLastUsed, WindowOpening.NewWindow }) {
            var windows = new List<ChannelWindowLayout>();
            var placed = WindowsOnly.Place(windows, "aaa", how, lastUsed: null);

            Assert.NotNull(placed);
            Assert.True(placed.Created);
            Assert.Equal([placed.Window], windows);
            Assert.Equal(["aaa"], placed.Window.Tabs);
            Assert.Equal("aaa", placed.Window.Selected);
        }
    }

    [Fact]
    public void AChannelAWindowShowsAlreadyOpensNothing() {
        var windows = new List<ChannelWindowLayout>();
        var window = ChannelWindowLayouts.Open(windows, "aaa");
        ChannelWindowLayouts.AddTab(window, "bbb");
        ChannelWindowLayouts.AddTab(window, "aaa");

        Assert.Null(WindowsOnly.Place(windows, "bbb", WindowOpening.AddToLastUsed, window.Id));
        Assert.Null(WindowsOnly.Place(windows, "bbb", WindowOpening.NewWindow, window.Id));
        Assert.Single(windows);
        Assert.Equal(["aaa", "bbb"], window.Tabs);
        Assert.Equal("aaa", window.Selected);
    }

    [Fact]
    public void AddedToTheWindowUsedLastAsATabThatIsntSelected() {
        var windows = new List<ChannelWindowLayout>();
        var used = ChannelWindowLayouts.Open(windows, "aaa");
        var newer = ChannelWindowLayouts.Open(windows, "bbb");

        var placed = WindowsOnly.Place(windows, "ccc", WindowOpening.AddToLastUsed, used.Id);

        Assert.NotNull(placed);
        Assert.False(placed.Created);
        Assert.Same(used, placed.Window);
        Assert.Equal(["aaa", "ccc"], used.Tabs);
        // The player's tab stays selected: the new one shows its unread count behind it.
        Assert.Equal("aaa", used.Selected);
        Assert.Equal(["bbb"], newer.Tabs);
        Assert.Equal(2, windows.Count);
    }

    [Fact]
    public void WithNoWindowUsedYetTheOneOpenedLastTakesIt() {
        var windows = new List<ChannelWindowLayout>();
        ChannelWindowLayouts.Open(windows, "aaa");
        var newest = ChannelWindowLayouts.Open(windows, "bbb");

        // None used yet this session, or the one used last was closed since.
        Assert.Same(newest, WindowsOnly.Place(windows, "ccc", WindowOpening.AddToLastUsed, lastUsed: null)?.Window);
        Assert.Same(newest, WindowsOnly.Place(windows, "ddd", WindowOpening.AddToLastUsed, lastUsed: "closed")?.Window);
        Assert.Equal(["bbb", "ccc", "ddd"], newest.Tabs);
        Assert.Equal("bbb", newest.Selected);
    }

    [Fact]
    public void SeveralChannelsAtOnceShareTheWindowOpenedForTheFirst() {
        var windows = new List<ChannelWindowLayout>();
        var first = WindowsOnly.Place(windows, "aaa", WindowOpening.AddToLastUsed, lastUsed: null);
        var second = WindowsOnly.Place(windows, "bbb", WindowOpening.AddToLastUsed, lastUsed: null);

        Assert.True(first!.Created);
        Assert.False(second!.Created);
        Assert.Same(first.Window, second.Window);
        Assert.Equal(["aaa", "bbb"], first.Window.Tabs);
        Assert.Equal("aaa", first.Window.Selected);
    }

    [Fact]
    public void NewWindowEachTimeOpensOneForEveryChannel() {
        var windows = new List<ChannelWindowLayout>();
        var used = ChannelWindowLayouts.Open(windows, "aaa");

        var placed = WindowsOnly.Place(windows, "bbb", WindowOpening.NewWindow, used.Id);
        var again = WindowsOnly.Place(windows, "ccc", WindowOpening.NewWindow, used.Id);

        Assert.True(placed!.Created);
        Assert.True(again!.Created);
        Assert.Equal(3, windows.Count);
        Assert.Equal(["aaa"], used.Tabs);
        Assert.Equal(["bbb"], placed.Window.Tabs);
        Assert.Equal("ccc", again.Window.Selected);
    }

    [Fact]
    public void WhatAHandEditedSettingsFileHoldsCountsAsNothing() {
        // (A window that is null itself is dropped by ChannelWindowLayouts.Sync, before any window opens.)
        var windows = new List<ChannelWindowLayout> { new() { Id = "odd", Tabs = null! } };

        var placed = WindowsOnly.Place(windows, "aaa", WindowOpening.AddToLastUsed, "odd");

        Assert.NotNull(placed);
        Assert.True(placed.Created);
        Assert.Equal(["aaa"], placed.Window.Tabs);
    }

    // ================================================================ waiting while the game is busy

    [Fact]
    public void ChannelsWaitWhileTheGameIsBusyThenComeOnceEachInTheOrderTheyAsked() {
        var pending = new PendingWindows();
        var ready = Snapshot(ConnectionState.Ready, true, "aaa", "bbb", "ccc");
        pending.Want("bbb");
        pending.Want("aaa");
        pending.Want("bbb");

        Assert.Empty(pending.Take(ready, busy: true));
        Assert.Equal(2, pending.Count);
        Assert.Equal(["bbb", "aaa"], pending.Take(ready, busy: false));
        Assert.Equal(0, pending.Count);
        Assert.Empty(pending.Take(ready, busy: false));
    }

    [Fact]
    public void ChannelsWaitForTheCompleteChannelListAndOnlyChannelsInItOpen() {
        var pending = new PendingWindows();
        pending.Want("aaa");
        pending.Want("gone");

        Assert.Empty(pending.Take(Snapshot(ConnectionState.Ready, false, "aaa"), busy: false));
        Assert.Empty(pending.Take(Snapshot(ConnectionState.Reconnecting, true, "aaa"), busy: false));
        Assert.Equal(2, pending.Count);

        // A channel left (or one from a session that has ended) is dropped, not kept for later.
        Assert.Equal(["aaa"], pending.Take(Snapshot(ConnectionState.Ready, true, "aaa"), busy: false));
        Assert.Equal(0, pending.Count);
    }

    [Fact]
    public void APlaceFromOldKeysNeverOpens() {
        var pending = new PendingWindows();
        var snapshot = Snapshot(ConnectionState.Ready, true, "aaa", "old");
        snapshot = snapshot with { Channels = [snapshot.Channels[0], snapshot.Channels[1] with { OldKeyMembership = true }] };
        pending.Want("old");
        pending.Want("aaa");

        Assert.Equal(["aaa"], pending.Take(snapshot, busy: false));
    }

    [Fact]
    public void ClearingForgetsWhatWaited() {
        var pending = new PendingWindows();
        pending.Want("aaa");
        pending.Clear();

        Assert.Empty(pending.Take(Snapshot(ConnectionState.Ready, true, "aaa"), busy: false));
    }

    [Fact]
    public void ChannelsAskFromAnyThread() {
        var pending = new PendingWindows();
        Parallel.For(0, 1000, i => pending.Want("c" + (i % 50)));

        Assert.Equal(50, pending.Count);
    }

    // ================================================================ words

    [Fact]
    public void ItsWordsArePlain() {
        PlainLanguage.AssertPlain(WindowsOnly.SettingName);
        PlainLanguage.AssertPlain(WindowsOnly.SettingTooltip);
        PlainLanguage.AssertPlain(WindowsOnly.GameChatItemTooltip);
        Assert.Contains("Settings", WindowsOnly.GameChatItemTooltip);
        Assert.Contains(WindowsOnly.SettingName, WindowsOnly.GameChatItemTooltip);
        foreach (var how in Enum.GetValues<WindowOpening>()) {
            PlainLanguage.AssertPlain(WindowsOnly.NameOf(how));
        }
    }

    [Fact]
    public void AddingToTheWindowUsedLastIsTheDefault() => Assert.Equal(WindowOpening.AddToLastUsed, default(WindowOpening));
}
