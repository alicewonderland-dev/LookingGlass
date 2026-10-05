using System.Collections.Immutable;
using LookingGlass.Core.Client;
using LookingGlass.Protocol;

namespace LookingGlass.Tests;

/// <summary>
/// Talking in a channel without /lgc (/lgc3 or /lgc sky with no message). The rule that matters most: while it's on,
/// nothing typed in the chat box falls through to game chat, where it would be public.
/// </summary>
public sealed class StickyChannelTests {
    private const string Tag = "[sky]";
    private static readonly IReadOnlyCollection<string> NoPrefixes = [];
    private static readonly GameChannel Say = new(1);
    private static readonly GameChannel Party = new(2);

    // ---------------------------------------------------------------- where typed text goes

    [Fact]
    public void WithoutAChannelEverythingGoesToTheGame() {
        Assert.Equal(StickyRoute.Game, StickyRoute.For(null, Tag, "hello", NoPrefixes));
        Assert.Equal(StickyRoute.Game, StickyRoute.For(null, Tag, "/s hello", ["/s"]));
        Assert.Equal(StickyRoute.Game, StickyRoute.For(null, Tag, "", NoPrefixes));
    }

    [Theory]
    [InlineData("hello", "hello")]
    [InlineData("  hello there  ", "hello there")]
    [InlineData("hello /s there", "hello /s there")]
    // Only a "/" at the very start is a command: a line that merely looks like one after a space never reaches the game.
    [InlineData(" /s hello", "/s hello")]
    [InlineData("\t/p hello", "/p hello")]
    public void PlainTextGoesToTheChannelAndNeverToTheGame(string input, string sent) {
        Assert.Equal(new StickyRoute.ToChannel("aaa", sent), StickyRoute.For("aaa", Tag, input, NoPrefixes));
    }

    [Theory]
    [InlineData("/s")]
    [InlineData("/p")]
    [InlineData("/s hello")]
    [InlineData("/p brb")]
    [InlineData("/l1")]
    [InlineData("/cwl1 hi")]
    [InlineData("/t Bob Smith@Zalera hi")]
    [InlineData("/lgc3")]
    [InlineData("/lgc3 hello")]
    [InlineData("/lgc sky hello")]
    [InlineData("/lg")]
    [InlineData("/em waves")]
    [InlineData("/")]
    public void CommandsGoToTheGameUntouchedWithTheGamesChatBox(string input) {
        Assert.Equal(StickyRoute.Game, StickyRoute.For("aaa", Tag, input, NoPrefixes));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void ABlankLineGoesNowhereQuietly(string input) {
        Assert.Equal(new StickyRoute.Dropped(null), StickyRoute.For("aaa", Tag, input, NoPrefixes));
    }

    [Fact]
    public void ALineWithOnlyALinkIsDroppedWithALineSayingSo() {
        Assert.Equal(new StickyRoute.Dropped(StickyMessages.NotSent(Tag, StickyMessages.NoTextReason)), StickyRoute.For("aaa", Tag, " ", NoPrefixes, notOnlyText: true));
        Assert.Equal(new StickyRoute.ToChannel("aaa", "look"), StickyRoute.For("aaa", Tag, "look ", NoPrefixes, notOnlyText: true));
    }

    /// <summary>Every short channel command ChatTwo 1.40.9 can put in front of plain text (InputChannelExt.Prefix), but /t, /e and /ecl.</summary>
    public static TheoryData<string> ChatTwoPrefixes() {
        var data = new TheoryData<string>();
        foreach (var prefix in new[] { "/s", "/p", "/a", "/y", "/sh", "/fc", "/pt", "/b" }) {
            data.Add(prefix);
        }

        for (var i = 1; i <= 8; i++) {
            data.Add($"/cwl{i}");
            data.Add($"/l{i}");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ChatTwoPrefixes))]
    public void WithChatTwoEveryShortChannelCommandWithTextGoesToTheChannel(string prefix) {
        // Whatever the game's channel and whatever channel any ChatTwo input (a tab, a pop-out) is on: ChatTwo sends
        // "hello" typed in a pop-out set to Say as "/s hello" even while the game is on the Free Company.
        var withChatTwo = ChatChannelPrefixes.SentAs(chatTwo: true);
        Assert.Equal(new StickyRoute.ToChannel("aaa", "hello"), StickyRoute.For("aaa", Tag, $"{prefix} hello", withChatTwo));
        Assert.Equal(new StickyRoute.ToChannel("aaa", "hello  there"), StickyRoute.For("aaa", Tag, $"{prefix.ToUpperInvariant()}   hello  there ", withChatTwo));
        Assert.Equal(new StickyRoute.ToChannel("aaa", "hi"), StickyRoute.For("aaa", Tag, $"{prefix}\thi", withChatTwo));

        // On its own it is a channel switch: the game's.
        Assert.Equal(StickyRoute.Game, StickyRoute.For("aaa", Tag, prefix, withChatTwo));
        Assert.Equal(StickyRoute.Game, StickyRoute.For("aaa", Tag, $"{prefix}   ", withChatTwo));

        // Without ChatTwo the game's chat box never adds one, so it is the player's own command: the game's.
        Assert.Equal(StickyRoute.Game, StickyRoute.For("aaa", Tag, $"{prefix} hello", ChatChannelPrefixes.SentAs(chatTwo: false)));

        // Not talking in a channel: the game's, ChatTwo or not.
        Assert.Equal(StickyRoute.Game, StickyRoute.For(null, Tag, $"{prefix} hello", withChatTwo));
    }

    [Theory]
    // The long forms are only ever typed: the way to talk in a game channel once.
    [InlineData("/say hello")]
    [InlineData("/shout hello")]
    [InlineData("/yell hello")]
    [InlineData("/party hello")]
    [InlineData("/alliance hello")]
    [InlineData("/freecompany hello")]
    [InlineData("/pvpteam hello")]
    [InlineData("/beginner hello")]
    [InlineData("/novice hello")]
    [InlineData("/linkshell1 hello")]
    [InlineData("/linkshell8 hello")]
    [InlineData("/cwlinkshell1 hello")]
    [InlineData("/cwlinkshell8 hello")]
    // Echo is only shown to the player (ChatTwo uses it for an input with no channel), tells name a player first,
    // ExtraChat's commands are ExtraChat's.
    [InlineData("/e hello")]
    [InlineData("/echo hello")]
    [InlineData("/t Bob Smith@Zalera hello")]
    [InlineData("/tell Bob Smith@Zalera hello")]
    [InlineData("/ecl1 hello")]
    // Not channel commands at all.
    [InlineData("/l9 hello")]
    [InlineData("/cwl9 hello")]
    [InlineData("/lgc3 hello")]
    [InlineData("/lgc sky hello")]
    [InlineData("/em waves")]
    [InlineData("/ph hello")]
    public void WithChatTwoOtherCommandsStillGoToTheGame(string input) {
        Assert.Equal(StickyRoute.Game, StickyRoute.For("aaa", Tag, input, ChatChannelPrefixes.SentAs(chatTwo: true)));
    }

    [Fact]
    public void ChatTwosPrefixesAreExactlyThese() {
        var expected = ChatTwoPrefixes().Select(row => row.Data).ToHashSet();
        Assert.Equal(34 - 8 - 2, expected.Count); // ChatTwo's 34 short commands, less /ecl1 to /ecl8, /t and /e.
        Assert.True(expected.SetEquals(ChatChannelPrefixes.ChatTwo));
        Assert.Empty(ChatChannelPrefixes.SentAs(chatTwo: false));
    }

    // ---------------------------------------------------------------- the gate: fail closed

    [Fact]
    public void NotTalkingInAChannelTheGateLetsEverythingThroughWithoutLooking() {
        var looked = false;
        Assert.False(ChatBoxGate.KeepFromGame(false, () => looked = true, _ => throw new InvalidOperationException()));
        Assert.False(looked);
    }

    [Fact]
    public void TalkingInAChannelTheGateDoesWhatTheDecisionSays() {
        Assert.True(ChatBoxGate.KeepFromGame(true, () => true, _ => Assert.Fail("Nothing failed.")));
        Assert.False(ChatBoxGate.KeepFromGame(true, () => false, _ => Assert.Fail("Nothing failed.")));
        Assert.True(StickyRoute.For("aaa", Tag, "hello", NoPrefixes).KeepsFromGame);
        Assert.True(StickyRoute.For("aaa", Tag, "  ", NoPrefixes).KeepsFromGame);
        Assert.True(StickyRoute.For("aaa", Tag, "/p hi", ChatChannelPrefixes.SentAs(true)).KeepsFromGame);
        Assert.False(StickyRoute.For("aaa", Tag, "/p hi", NoPrefixes).KeepsFromGame);
        Assert.False(StickyRoute.For(null, Tag, "hello", NoPrefixes).KeepsFromGame);
    }

    [Fact]
    public void ALineWhoseFateCouldntBeDecidedNeverReachesTheGame() {
        Exception? told = null;
        Assert.True(ChatBoxGate.KeepFromGame(true, () => throw new InvalidOperationException("boom"), ex => told = ex));
        Assert.Equal("boom", told?.Message);

        // Even if telling the player fails too.
        Assert.True(ChatBoxGate.KeepFromGame(true, () => throw new InvalidOperationException("boom"), _ => throw new InvalidOperationException("again")));
    }

    // ---------------------------------------------------------------- starting

    [Fact]
    public void StartingNeedsTheHooksAConnectionAndMembership() {
        var session = new object();

        // No hooks (a game version this plugin doesn't know): refused, so typing never half-works.
        var sticky = new StickyChannel();
        var start = sticky.Enter("aaa", Tag, World(session), inputHooked: false, chatTwo: false, chatTwoTell: false);
        Assert.False(start.Entered);
        Assert.Equal(StickyMessages.Unavailable, start.Text);
        Assert.Null(sticky.ChannelId);

        // Not connected, logged out, still loading, or not a member.
        AssertRefused(World(null), StickyMessages.NotConnected(Tag));
        AssertRefused(World(session) with { ContentId = 0 }, StickyMessages.NotConnected(Tag));
        AssertRefused(World(session, Snapshot(ConnectionState.Reconnecting, true, Member("aaa"))), StickyMessages.NotConnected(Tag));
        AssertRefused(World(session, Snapshot(ConnectionState.Connecting, false)), StickyMessages.NotConnected(Tag));
        AssertRefused(World(session, Snapshot(ConnectionState.Ready, false, Member("aaa"))), StickyMessages.StillLoading);
        AssertRefused(World(session, Snapshot(ConnectionState.Ready, true, Member("bbb"))), StickyMessages.NotAMember(Tag));
        AssertRefused(World(session, Snapshot(ConnectionState.Ready, true, Member("aaa") with { MyRank = Rank.Invited })), StickyMessages.NotAMember(Tag));
        AssertRefused(World(session, Snapshot(ConnectionState.Ready, true, Member("aaa") with { MyRank = Rank.Unspecified })), StickyMessages.NotAMember(Tag));
        AssertRefused(World(session, Snapshot(ConnectionState.Ready, true, Member("aaa") with { OldKeyMembership = true })), StickyMessages.NotAMember(Tag));

        // The game's channel can't be read: leaving couldn't be seen, so refused.
        AssertRefused(World(session) with { Channel = null }, StickyMessages.Unavailable);

        // Every rank of member can.
        foreach (var rank in new[] { Rank.Member, Rank.Moderator, Rank.Admin }) {
            var member = new StickyChannel();
            Assert.True(member.Enter("aaa", Tag, World(session, Snapshot(ConnectionState.Ready, true, Member("aaa") with { MyRank = rank })), true, false, false).Entered);
            Assert.Equal("aaa", member.ChannelId);
        }
    }

    [Fact]
    public void WithChatTwoNotFromATell() {
        var session = new object();

        // ChatTwo sends tells straight to the server, past the chat box, so they couldn't be kept from the game.
        AssertRefused(World(session) with { Channel = new GameChannel(17) }, StickyMessages.NoTellsWithChatTwo, chatTwo: true);
        AssertRefused(World(session) with { Channel = new GameChannel(0) }, StickyMessages.NoTellsWithChatTwo, chatTwo: true);
        AssertRefused(World(session), StickyMessages.NoTellsWithChatTwo, chatTwo: true, chatTwoTell: true);

        // The game's own chat box sends tells through the function that is hooked: fine.
        Assert.True(new StickyChannel().Enter("aaa", Tag, World(session) with { Channel = new GameChannel(17) }, true, false, false).Entered);
    }

    [Fact]
    public void StartingSaysHowToGoBack() {
        var start = new StickyChannel().Enter("aaa", Tag, World(new object()), true, false, false);
        Assert.True(start.Entered);
        Assert.Equal(StickyMessages.Entered(Tag, chatTwo: false), start.Text);
        Assert.Contains(Tag, start.Text);
        Assert.Contains("/s", start.Text);
        Assert.DoesNotContain("ChatTwo", start.Text);

        // With ChatTwo, why its label says "(Warning: Party)", and that it's still only this channel.
        var withChatTwo = new StickyChannel().Enter("aaa", Tag, World(new object()), true, true, false);
        Assert.True(withChatTwo.Entered);
        Assert.Contains("ChatTwo", withChatTwo.Text);
        Assert.Contains("(Warning: ...)", withChatTwo.Text);
        // Every ChatTwo input and short command goes to the channel; the long command is the way to talk in a game channel once.
        Assert.Contains("pop-out", withChatTwo.Text);
        Assert.Contains("/party hi", withChatTwo.Text);
        Assert.Contains("tell", withChatTwo.Text);
    }

    // ---------------------------------------------------------------- leaving

    [Fact]
    public void ItGoesOnWhileNothingChanges() {
        var session = new object();
        var sticky = Started(session);

        Assert.Null(sticky.Check(World(session)));
        // A reconnect keeps it: messages typed meanwhile fail (and say so) rather than go to the game.
        Assert.Null(sticky.Check(World(session, Snapshot(ConnectionState.Reconnecting, false))));
        Assert.Null(sticky.Check(World(session, Snapshot(ConnectionState.Ready, false))));
        Assert.Null(sticky.Check(World(session, Snapshot(ConnectionState.Reconnecting, true, Member("bbb")))));
        Assert.Equal("aaa", sticky.ChannelId);
    }

    [Theory]
    [MemberData(nameof(Endings))]
    public void ItEndsWhenAnythingItNeedsChanges(string what, StickyEnd expected) {
        var session = new object();
        var sticky = Started(session);
        var world = what switch {
            "logged out" => World(session) with { ContentId = 0 },
            "other character" => World(session) with { ContentId = 99 },
            "session ended" => World(null),
            "new session" => World(new object()),
            "left" => World(session, Snapshot(ConnectionState.Ready, true, Member("bbb"))),
            "invited only" => World(session, Snapshot(ConnectionState.Ready, true, Member("aaa") with { MyRank = Rank.Invited })),
            "old setup" => World(session, Snapshot(ConnectionState.Ready, true, Member("aaa") with { OldKeyMembership = true })),
            "switched" => World(session) with { Channel = Party },
            "linkshell" => World(session) with { Channel = new GameChannel(20) },
            "unreadable" => World(session) with { Channel = null },
            _ => throw new ArgumentOutOfRangeException(nameof(what)),
        };

        Assert.Equal(expected, sticky.Check(world));
        Assert.Null(sticky.ChannelId);

        // Ended once: nothing more to end, even if the world goes back.
        Assert.Null(sticky.Check(world));
        Assert.Null(sticky.Check(World(session)));
    }

    public static TheoryData<string, StickyEnd> Endings() => new() {
        { "logged out", StickyEnd.LoggedOut },
        { "other character", StickyEnd.LoggedOut },
        { "session ended", StickyEnd.SessionEnded },
        { "new session", StickyEnd.SessionEnded },
        { "left", StickyEnd.NotInChannel },
        { "invited only", StickyEnd.NotInChannel },
        { "old setup", StickyEnd.NotInChannel },
        { "switched", StickyEnd.ChannelSwitched },
        { "linkshell", StickyEnd.ChannelSwitched },
        { "unreadable", StickyEnd.ChannelUnknown },
    };

    [Fact]
    public void AnotherChannelTakesOverAndLeavingEndsIt() {
        var session = new object();
        var sticky = Started(session);
        var both = Snapshot(ConnectionState.Ready, true, Member("aaa"), Member("bbb"));

        // /lgc of another channel: talking in that one now, measured from the game's channel at that moment.
        Assert.True(sticky.Enter("bbb", "[moon]", World(session, both) with { Channel = Party }, true, false, false).Entered);
        Assert.Equal("bbb", sticky.ChannelId);
        Assert.Null(sticky.Check(World(session, both) with { Channel = Party }));

        // A refused switch keeps the channel it was in.
        Assert.False(sticky.Enter("ccc", "[sun]", World(session, both) with { Channel = Party }, true, false, false).Entered);
        Assert.Equal("bbb", sticky.ChannelId);

        // /s (a channel switch to the same channel is seen by the hook, not here), the info bar, unloading: Leave.
        Assert.Equal("bbb", sticky.Leave());
        Assert.Null(sticky.ChannelId);
        Assert.Null(sticky.Leave());
        Assert.Equal(StickyRoute.Game, StickyRoute.For(sticky.ChannelId, Tag, "hello", NoPrefixes));
    }

    [Fact]
    public void ASwitchCallEndsItOnlyIfTypedOrIfTheChannelChanged() {
        var session = new object();

        // ChatTwo calls the switch with the channel it is already on at every tab change, and when its input loses focus
        // after a one-off channel: it goes on.
        var sticky = Started(session);
        Assert.Null(sticky.ChannelSwitchCalled(Say, fromTypedCommand: false));
        Assert.Null(sticky.ChannelSwitchCalled(Say, fromTypedCommand: false));
        Assert.Equal("aaa", sticky.ChannelId);

        // /s typed while in Say: the channel doesn't change, but the player is switching back.
        Assert.Equal(StickyEnd.ChannelSwitched, sticky.ChannelSwitchCalled(Say, fromTypedCommand: true));
        Assert.Null(sticky.ChannelId);
        Assert.Null(sticky.ChannelSwitchCalled(Party, fromTypedCommand: true));

        // A ChatTwo tab with another channel, or the picker: the channel changes.
        sticky = Started(session);
        Assert.Equal(StickyEnd.ChannelSwitched, sticky.ChannelSwitchCalled(Party, fromTypedCommand: false));
        Assert.Null(sticky.ChannelId);

        // Another linkshell is another channel.
        sticky = Started(session, new GameChannel(19));
        Assert.Null(sticky.ChannelSwitchCalled(new GameChannel(19), false));
        Assert.Equal(StickyEnd.ChannelSwitched, sticky.ChannelSwitchCalled(new GameChannel(20), false));

        // Unreadable after the call: ends.
        sticky = Started(session);
        Assert.Equal(StickyEnd.ChannelUnknown, sticky.ChannelSwitchCalled(null, false));

        // Not talking in a channel: nothing to end.
        Assert.Null(new StickyChannel().ChannelSwitchCalled(Party, true));
    }

    // ---------------------------------------------------------------- what is said

    [Fact]
    public void EverythingItSaysIsPlain() {
        var texts = new List<string> {
            StickyMessages.Unavailable,
            StickyMessages.StillLoading,
            StickyMessages.NoTellsWithChatTwo,
            StickyMessages.NotConnected(Tag),
            StickyMessages.NotAMember(Tag),
            StickyMessages.Entered(Tag, false),
            StickyMessages.Entered(Tag, true),
            StickyMessages.NotSent(Tag, StickyMessages.NotConnectedReason),
            StickyMessages.NotSent(Tag, StickyMessages.NoTextReason),
            StickyMessages.NotSent(Tag, StickyMessages.SomethingWentWrongReason),
            StickyMessages.ExtraChatLoaded,
            ChannelCommand.NicknameUsage,
        };
        texts.AddRange(Enum.GetValues<StickyEnd>().Select(end => StickyMessages.Ended(Tag, end)));

        Assert.All(texts, PlainLanguage.AssertPlain);
    }

    [Fact]
    public void AMessageThatWasntSentSaysItDidntGoToGameChat() {
        Assert.Equal("Not sent to [sky]: too fast. It didn't go to game chat either.", StickyMessages.NotSent(Tag, "too fast"));
        Assert.Equal("Not sent to [sky]: too fast. It didn't go to game chat either.", StickyMessages.NotSent(Tag, " too fast. "));
        Assert.Equal("Not sent to [sky]: Slow down (RateLimited). It didn't go to game chat either.", StickyMessages.NotSent(Tag, "Slow down (RateLimited)"));
        Assert.All(Enum.GetValues<StickyEnd>().Where(end => end != StickyEnd.LoggedOut),
            end => Assert.Contains("game chat again", StickyMessages.Ended(Tag, end)));
    }

    private static void AssertRefused(StickyWorld world, string why, bool chatTwo = false, bool chatTwoTell = false) {
        var sticky = new StickyChannel();
        var start = sticky.Enter("aaa", Tag, world, inputHooked: true, chatTwo, chatTwoTell);
        Assert.False(start.Entered);
        Assert.Equal(why, start.Text);
        Assert.Null(sticky.ChannelId);
        Assert.Equal(StickyRoute.Game, StickyRoute.For(sticky.ChannelId, Tag, "hello", NoPrefixes));
    }

    private static StickyChannel Started(object session, GameChannel? channel = null) {
        var sticky = new StickyChannel();
        Assert.True(sticky.Enter("aaa", Tag, World(session) with { Channel = channel ?? Say }, true, false, false).Entered);
        return sticky;
    }

    private static StickyWorld World(object? session, SessionSnapshot? snapshot = null) =>
        new(session, 42, snapshot ?? Snapshot(ConnectionState.Ready, true, Member("aaa")), Say);

    private static ChannelView Member(string id) => new(id, null, 0, 0, true, false, Rank.Member, []);

    private static SessionSnapshot Snapshot(ConnectionState state, bool loaded, params ChannelView[] channels) =>
        SessionSnapshot.Empty with { State = state, ChannelsLoaded = loaded, Channels = channels.ToImmutableArray() };
}
