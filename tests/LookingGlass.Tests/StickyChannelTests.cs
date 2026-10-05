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
    public void WhatChatTwoSendsForPlainTextGoesToTheChannel() {
        // ChatTwo sends "hello" typed with its input on Party as "/p hello", through the same chat box function.
        IReadOnlyCollection<string> party = ["/p"];
        Assert.Equal(new StickyRoute.ToChannel("aaa", "hello"), StickyRoute.For("aaa", Tag, "/p hello", party));
        Assert.Equal(new StickyRoute.ToChannel("aaa", "hello  there"), StickyRoute.For("aaa", Tag, "/P   hello  there ", party));
        Assert.Equal(new StickyRoute.ToChannel("aaa", "hi"), StickyRoute.For("aaa", Tag, "/p\thi", party));

        // The channel command on its own is a switch, and other commands are the player's own: on to the game.
        Assert.Equal(StickyRoute.Game, StickyRoute.For("aaa", Tag, "/p", party));
        Assert.Equal(StickyRoute.Game, StickyRoute.For("aaa", Tag, "/p   ", party));
        Assert.Equal(StickyRoute.Game, StickyRoute.For("aaa", Tag, "/s hello", party));
        Assert.Equal(StickyRoute.Game, StickyRoute.For("aaa", Tag, "/party hello", party));
        Assert.Equal(StickyRoute.Game, StickyRoute.For("aaa", Tag, "/lgc3 hello", party));
        Assert.Equal(StickyRoute.Game, StickyRoute.For("aaa", Tag, "/ph hello", party));
    }

    [Fact]
    public void ChatTwosPrefixesAreTheOnesItUses() {
        // The game's chat types (RaptureShellModule.ChatType, as ChatTwo reads them).
        Assert.Equal("/s", ChatChannelPrefixes.ForGameChatType(1));
        Assert.Equal("/p", ChatChannelPrefixes.ForGameChatType(2));
        Assert.Equal("/a", ChatChannelPrefixes.ForGameChatType(3));
        Assert.Equal("/y", ChatChannelPrefixes.ForGameChatType(4));
        Assert.Equal("/sh", ChatChannelPrefixes.ForGameChatType(5));
        Assert.Equal("/fc", ChatChannelPrefixes.ForGameChatType(6));
        Assert.Equal("/pt", ChatChannelPrefixes.ForGameChatType(7));
        Assert.Equal("/b", ChatChannelPrefixes.ForGameChatType(8));
        Assert.Equal("/cwl1", ChatChannelPrefixes.ForGameChatType(9));
        Assert.Equal("/cwl8", ChatChannelPrefixes.ForGameChatType(16));
        Assert.Equal("/l1", ChatChannelPrefixes.ForGameChatType(19));
        Assert.Equal("/l8", ChatChannelPrefixes.ForGameChatType(26));

        // ChatTwo's own numbering, for the channel its input says it sends to.
        Assert.Equal("/s", ChatChannelPrefixes.ForChatTwoChatType(10));
        Assert.Equal("/sh", ChatChannelPrefixes.ForChatTwoChatType(11));
        Assert.Equal("/p", ChatChannelPrefixes.ForChatTwoChatType(14));
        Assert.Equal("/a", ChatChannelPrefixes.ForChatTwoChatType(15));
        Assert.Equal("/l1", ChatChannelPrefixes.ForChatTwoChatType(16));
        Assert.Equal("/l8", ChatChannelPrefixes.ForChatTwoChatType(23));
        Assert.Equal("/fc", ChatChannelPrefixes.ForChatTwoChatType(24));
        Assert.Equal("/b", ChatChannelPrefixes.ForChatTwoChatType(27));
        Assert.Equal("/y", ChatChannelPrefixes.ForChatTwoChatType(30));
        Assert.Equal("/pt", ChatChannelPrefixes.ForChatTwoChatType(36));
        Assert.Equal("/cwl1", ChatChannelPrefixes.ForChatTwoChatType(37));
        Assert.Equal("/cwl2", ChatChannelPrefixes.ForChatTwoChatType(101));
        Assert.Equal("/cwl8", ChatChannelPrefixes.ForChatTwoChatType(107));

        // Both numberings agree on every linkshell.
        for (var i = 0; i < 8; i++) {
            Assert.Equal(ChatChannelPrefixes.ForGameChatType(19 + i), ChatChannelPrefixes.ForChatTwoChatType(16 + i));
            Assert.Equal(ChatChannelPrefixes.ForGameChatType(9 + i), ChatChannelPrefixes.ForChatTwoChatType(i == 0 ? 37 : 100 + i));
        }

        // Tells (ChatTwo sends them another way) and anything that isn't a channel to talk in have none.
        foreach (var tell in new[] { 0, 17, 18 }) {
            Assert.Null(ChatChannelPrefixes.ForGameChatType(tell));
        }

        Assert.Null(ChatChannelPrefixes.ForChatTwoChatType(ChatChannelPrefixes.ChatTwoTell));
        Assert.Null(ChatChannelPrefixes.ForGameChatType(27));
        Assert.Null(ChatChannelPrefixes.ForChatTwoChatType(56));
        Assert.Null(ChatChannelPrefixes.ForChatTwoChatType(1001));
    }

    [Fact]
    public void OnlyChatTwosPrefixesForItsChannelsStandForPlainText() {
        Assert.Empty(ChatChannelPrefixes.SentAs(false, Say, 14));
        Assert.Equal(new[] { "/s" }, ChatChannelPrefixes.SentAs(true, Say, null));
        Assert.Equal(new[] { "/s" }, ChatChannelPrefixes.SentAs(true, Say, 10));
        // A ChatTwo tab with its own channel sends with that channel's command, not the game's.
        Assert.Equal(new HashSet<string> { "/s", "/fc" }, ChatChannelPrefixes.SentAs(true, Say, 24).ToHashSet());
        Assert.Equal(new[] { "/p" }, ChatChannelPrefixes.SentAs(true, null, 14));
        Assert.Empty(ChatChannelPrefixes.SentAs(true, null, null));
        Assert.Empty(ChatChannelPrefixes.SentAs(true, new GameChannel(17), ChatChannelPrefixes.ChatTwoTell));
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
        Assert.Contains("only to " + Tag, withChatTwo.Text);
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

    private static StickyChannel Started(object session) {
        var sticky = new StickyChannel();
        Assert.True(sticky.Enter("aaa", Tag, World(session), true, false, false).Entered);
        return sticky;
    }

    private static StickyWorld World(object? session, SessionSnapshot? snapshot = null) =>
        new(session, 42, snapshot ?? Snapshot(ConnectionState.Ready, true, Member("aaa")), Say);

    private static ChannelView Member(string id) => new(id, null, 0, 0, true, false, Rank.Member, []);

    private static SessionSnapshot Snapshot(ConnectionState state, bool loaded, params ChannelView[] channels) =>
        SessionSnapshot.Empty with { State = state, ChannelsLoaded = loaded, Channels = channels.ToImmutableArray() };
}
