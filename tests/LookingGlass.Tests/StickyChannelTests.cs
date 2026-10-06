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
    [InlineData("/s hello")]
    [InlineData("/p brb")]
    [InlineData("/cwl1 hi")]
    [InlineData("/t Bob Smith@Zalera hi")]
    [InlineData("/t Bob Smith@Zalera")]
    [InlineData("/lgc3")]
    [InlineData("/lgc3 hello")]
    [InlineData("/lgc sky hello")]
    [InlineData("/lg")]
    [InlineData("/em waves")]
    [InlineData("/e")]
    [InlineData("/ecl1")]
    [InlineData("/")]
    public void CommandsGoToTheGameUntouchedWithTheGamesChatBox(string input) {
        Assert.Equal(StickyRoute.Game, StickyRoute.For("aaa", Tag, input, NoPrefixes));
    }

    [Theory]
    [InlineData("/s")]
    [InlineData("/S")]
    [InlineData("/s   ")]
    [InlineData("/p")]
    [InlineData("/say")]
    [InlineData("/party")]
    [InlineData("/fc")]
    [InlineData("/freecompany")]
    [InlineData("/l1")]
    [InlineData("/linkshell8")]
    [InlineData("/cwl1")]
    [InlineData("/cwlinkshell3")]
    [InlineData("/n")]
    [InlineData("/beginner")]
    public void AChannelCommandOnItsOwnEndsItFirstThenGoesToTheGame(string input) {
        // The player switching back: it ends right then, saying so, without waiting for the game to call its channel
        // switch (which it may not do for the channel already on), and the game then switches.
        foreach (var sentAs in new[] { NoPrefixes, ChatChannelPrefixes.SentAs() }) {
            var route = StickyRoute.For("aaa", Tag, input, sentAs);
            Assert.Equal(StickyRoute.Leave, route);
            Assert.False(route.KeepsFromGame);
        }

        // Not talking in a channel: just the game's.
        Assert.Equal(StickyRoute.Game, StickyRoute.For(null, Tag, input, ChatChannelPrefixes.SentAs()));
    }

    [Fact]
    public void TheGamesOwnNamesForChannelCommandsEndItToo() {
        // The plugin adds the names in the client's language from the game's TextCommand sheet.
        var switches = ChatChannelPrefixes.SwitchesWith(["/sagen", " /gruppe ", "", "x", "/"]);
        Assert.Equal(StickyRoute.Leave, StickyRoute.For("aaa", Tag, ChatBoxLine.Plain("/sagen"), NoPrefixes, switches));
        Assert.Equal(StickyRoute.Leave, StickyRoute.For("aaa", Tag, ChatBoxLine.Plain("/GRUPPE"), NoPrefixes, switches));
        Assert.Equal(StickyRoute.Leave, StickyRoute.For("aaa", Tag, ChatBoxLine.Plain("/s"), NoPrefixes, switches));
        Assert.Equal(StickyRoute.Game, StickyRoute.For("aaa", Tag, ChatBoxLine.Plain("/sagen hallo"), NoPrefixes, switches));
        Assert.DoesNotContain("/", switches);
        Assert.DoesNotContain("", switches);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void ABlankLineGoesNowhereQuietly(string input) {
        Assert.Equal(new StickyRoute.Dropped(null), StickyRoute.For("aaa", Tag, input, NoPrefixes));
    }

    // ---------------------------------------------------------------- links and auto-translate (the bytes the hook sees)

    /// <summary>An item link as the game encodes it in a line (SeString payloads), with the item's name shown.</summary>
    private static readonly byte[] NamedItemLink = [
        0x02, 0x48, 0x04, 0xF2, 0x02, 0x25, 0x03, // colour on
        0x02, 0x49, 0x04, 0xF2, 0x02, 0x26, 0x03, // glow on
        0x02, 0x27, 0x07, 0x03, 0xF2, 0x14, 0xD5, 0x02, 0x01, 0x03, // the link: item 5333
        0xEE, 0x82, 0xBB, (byte) 'P', (byte) 'o', (byte) 't', (byte) 'i', (byte) 'o', (byte) 'n', // link marker and name
        0x02, 0x49, 0x02, 0x01, 0x03, // glow off
        0x02, 0x48, 0x02, 0x01, 0x03, // colour off
        0x02, 0x27, 0x07, 0xCF, 0x01, 0x01, 0x01, 0xFF, 0x01, 0x03, // end of the link
    ];

    /// <summary>The same link without any text: only payloads.</summary>
    private static readonly byte[] BareItemLink = [0x02, 0x27, 0x07, 0x03, 0xF2, 0x14, 0xD5, 0x02, 0x01, 0x03, 0x02, 0x27, 0x07, 0xCF, 0x01, 0x01, 0x01, 0xFF, 0x01, 0x03];

    /// <summary>An auto-translate phrase (group 1, phrase 101).</summary>
    private static readonly byte[] AutoTranslate = [0x02, 0x2E, 0x03, 0x01, 0x66, 0x03];

    private static ChatBoxLine Line(string before, byte[] payload, string text) =>
        new([.. System.Text.Encoding.UTF8.GetBytes(before), .. payload], text);

    private static readonly TypedLink Potion = new(new ChatLink.Item(5333), "Potion");

    /// <summary>
    /// A line with a link's bytes, as the plugin reads it: its text with a marker where the link is, and the link
    /// (<see cref="ChatBoxLine.Links"/>).
    /// </summary>
    private static ChatBoxLine LinkLine(string before, byte[] payload, string after = "") =>
        new([.. System.Text.Encoding.UTF8.GetBytes(before), .. payload, .. System.Text.Encoding.UTF8.GetBytes(after)],
            before + LinkText.Marker(0) + after) { Links = [Potion] };

    private static string Marked(string before, string after = "") => before + LinkText.Marker(0) + after;

    [Fact]
    public void ALineWithOnlyALinkGoesToTheChannel() {
        // The game's chat box: a link with no text, as the link's bytes, or as the "<item>" the chat input holds for it.
        Assert.Equal(new StickyRoute.ToChannel("aaa", Marked("")), StickyRoute.For("aaa", Tag, LinkLine("", BareItemLink), NoPrefixes));
        Assert.Equal(new StickyRoute.ToChannel("aaa", Marked("")), StickyRoute.For("aaa", Tag, LinkLine(" ", NamedItemLink, " "), NoPrefixes));
        Assert.Equal(new StickyRoute.ToChannel("aaa", "<item>"), StickyRoute.For("aaa", Tag, ChatBoxLine.Plain("<item>"), NoPrefixes));
        Assert.Equal(new StickyRoute.ToChannel("aaa", "<flag> <status>"), StickyRoute.For("aaa", Tag, ChatBoxLine.Plain(" <flag> <status> "), NoPrefixes));
        Assert.Equal("links only", StickyRoute.Decide("aaa", Tag, LinkLine("", BareItemLink), NoPrefixes).Reason);
        Assert.Equal("links only", StickyRoute.Decide("aaa", Tag, ChatBoxLine.Plain("<item>"), NoPrefixes).Reason);

        // With text, the text and the links go.
        Assert.Equal(new StickyRoute.ToChannel("aaa", Marked("look ")), StickyRoute.For("aaa", Tag, LinkLine("look ", NamedItemLink), NoPrefixes));
        Assert.Equal(new StickyRoute.ToChannel("aaa", "look <item>"), StickyRoute.For("aaa", Tag, ChatBoxLine.Plain("look <item>"), NoPrefixes));

        // Payloads with no text and no link LookingGlass can read: kept from the game, saying so.
        var nothing = new StickyRoute.Dropped(StickyMessages.NotSent(Tag, StickyMessages.NoTextReason));
        Assert.Equal(nothing, StickyRoute.For("aaa", Tag, Line("", BareItemLink, ""), NoPrefixes));
        Assert.Equal(nothing, StickyRoute.For("aaa", Tag, Line(" ", BareItemLink, " "), NoPrefixes));
        Assert.Equal("nothing to send", StickyRoute.Decide("aaa", Tag, Line("", BareItemLink, ""), NoPrefixes).Reason);
    }

    [Fact]
    public void TheLinksOfALineGoWithItsText() {
        var line = LinkLine("look ", NamedItemLink, " here");
        Assert.Equal(new TypedLine(Marked("look ", " here"), [Potion]), line.Typed);
        Assert.Equal(new StickyRoute.ToChannel("aaa", Marked("look ", " here")), StickyRoute.For("aaa", Tag, line, NoPrefixes));

        // The text after a short command keeps its markers, so its links are still found.
        var afterCommand = LinkLine("/cwl1 look ", NamedItemLink);
        var sent = (StickyRoute.ToChannel) StickyRoute.For("aaa", Tag, afterCommand, ChatChannelPrefixes.SentAs());
        Assert.Equal(("look [Potion]", 1), LinkText.Compose(afterCommand.Typed.WithText(sent.Text)) is var (message, _) ? (message.Text, message.Links.Count) : default);
    }

    [Theory]
    [MemberData(nameof(ChatTwoPrefixes))]
    public void WithChatTwoAShortCommandFollowedByAnythingIsTheChannels(string prefix) {
        // ChatTwo sends a link typed in an input on a cross-world linkshell as "/cwl1 <item>" (its input holds the link
        // as the game's "<item>"), or with the link's own bytes after the command: never game chat, and now sent.
        var withChatTwo = ChatChannelPrefixes.SentAs();
        var linkOnly = new StickyRoute.ToChannel("aaa", Marked(""));

        Assert.Equal(new StickyRoute.ToChannel("aaa", "<item>"), StickyRoute.For("aaa", Tag, ChatBoxLine.Plain($"{prefix} <item>"), withChatTwo));
        Assert.Equal(linkOnly, StickyRoute.For("aaa", Tag, LinkLine($"{prefix} ", BareItemLink), withChatTwo));
        // Straight after the command, with no space.
        Assert.Equal(linkOnly, StickyRoute.For("aaa", Tag, LinkLine(prefix, BareItemLink), withChatTwo));
        Assert.Equal(linkOnly, StickyRoute.For("aaa", Tag, LinkLine($"{prefix}  ", BareItemLink), withChatTwo));
        Assert.Equal("short command with links only", StickyRoute.Decide("aaa", Tag, LinkLine($"{prefix} ", BareItemLink), withChatTwo).Reason);

        // Text and a link, an auto-translate phrase: sent.
        Assert.Equal(new StickyRoute.ToChannel("aaa", Marked("look at ")), StickyRoute.For("aaa", Tag, LinkLine($"{prefix} look at ", NamedItemLink), withChatTwo));
        Assert.Equal(new StickyRoute.ToChannel("aaa", "Hello"), StickyRoute.For("aaa", Tag, Line($"{prefix} ", AutoTranslate, $"{prefix} Hello"), withChatTwo));

        // Whatever it holds, it never reaches the game while talking in a channel.
        foreach (var payload in new[] { BareItemLink, NamedItemLink, AutoTranslate }) {
            Assert.True(StickyRoute.For("aaa", Tag, Line($"{prefix} ", payload, $"{prefix} "), withChatTwo).KeepsFromGame);
            Assert.True(StickyRoute.For("aaa", Tag, Line("", payload, ""), NoPrefixes).KeepsFromGame);
            Assert.True(StickyRoute.For("aaa", Tag, LinkLine($"{prefix} ", payload), withChatTwo).KeepsFromGame);
        }

        // Only the bare command, with nothing at all after it (a string's closing zero byte is nothing), switches.
        Assert.Equal(StickyRoute.Leave, StickyRoute.For("aaa", Tag, new ChatBoxLine([.. System.Text.Encoding.UTF8.GetBytes(prefix), 0x00], prefix), withChatTwo));
        Assert.Equal(StickyRoute.Leave, StickyRoute.For("aaa", Tag, ChatBoxLine.Plain($"{prefix} \t "), withChatTwo));
    }

    [Fact]
    public void InTheGamesChatBoxAShortCommandWithALinkIsStillAOneOff() {
        // Typed in the game's own chat box, a short command with a link (and nothing else) is the player's one-off, as
        // now: the game sends the link to that channel once, itself.
        var typedInGame = ShortCommandRule.For(LineSource.Game, null).AsText;
        Assert.Equal(StickyRoute.Game, StickyRoute.For("aaa", Tag, ChatBoxLine.Plain("/cwl1 <item>"), typedInGame));
        Assert.Equal(StickyRoute.Game, StickyRoute.For("aaa", Tag, LinkLine("/cwl1 ", BareItemLink), typedInGame));
        Assert.Equal(StickyRoute.Game, StickyRoute.For("aaa", Tag, LinkLine("/p look ", NamedItemLink), typedInGame));

        // Where every short command stands for text (the strict rule), it goes to the channel with its link.
        Assert.Equal(new StickyRoute.ToChannel("aaa", "<item>"), StickyRoute.For("aaa", Tag, ChatBoxLine.Plain("/cwl1 <item>"), ChatChannelPrefixes.SentAs()));
        Assert.Equal(new StickyRoute.ToChannel("aaa", Marked("")), StickyRoute.For("aaa", Tag, LinkLine("/p ", BareItemLink), ChatChannelPrefixes.SentAs()));

        // The long form is the player's own one-off: the game's.
        Assert.Equal(StickyRoute.Game, StickyRoute.For("aaa", Tag, ChatBoxLine.Plain("/cwlinkshell1 <item>"), ChatChannelPrefixes.SentAs()));
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
        var withChatTwo = ChatChannelPrefixes.SentAs();
        Assert.Equal(new StickyRoute.ToChannel("aaa", "hello"), StickyRoute.For("aaa", Tag, $"{prefix} hello", withChatTwo));
        Assert.Equal(new StickyRoute.ToChannel("aaa", "hello  there"), StickyRoute.For("aaa", Tag, $"{prefix.ToUpperInvariant()}   hello  there ", withChatTwo));
        Assert.Equal(new StickyRoute.ToChannel("aaa", "hi"), StickyRoute.For("aaa", Tag, $"{prefix}\thi", withChatTwo));

        // On its own it is a channel switch: it ends talking in the channel, then goes to the game.
        Assert.Equal(StickyRoute.Leave, StickyRoute.For("aaa", Tag, prefix, withChatTwo));
        Assert.Equal(StickyRoute.Leave, StickyRoute.For("aaa", Tag, $"{prefix}   ", withChatTwo));

        // The strict rule, for a line from an input whose channel isn't known (see ShortCommandRule): always the channel's.
        Assert.Equal(new StickyRoute.ToChannel("aaa", "hello"), StickyRoute.For("aaa", Tag, ChatBoxLine.Plain($"{prefix} hello"), ChatChannelPrefixes.SentAs()));

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
        Assert.Equal(StickyRoute.Game, StickyRoute.For("aaa", Tag, input, ChatChannelPrefixes.SentAs()));
    }

    [Fact]
    public void ChatTwosPrefixesAreExactlyThese() {
        var expected = ChatTwoPrefixes().Select(row => row.Data).ToHashSet();
        Assert.Equal(34 - 8 - 2, expected.Count); // ChatTwo's 34 short commands, less /ecl1 to /ecl8, /t and /e.
        Assert.True(expected.SetEquals(ChatChannelPrefixes.ChatTwo));
        Assert.Same(ChatChannelPrefixes.ChatTwo, ChatChannelPrefixes.SentAs());
    }

    // ---------------------------------------------------------------- short commands: the player's one-off, unless ChatTwo's typing

    /// <summary>Where a line goes, decided as the plugin decides it: the rule for its way in, then the route.</summary>
    private static StickyRoute Route(string input, LineSource source, ChatTwoLine? chatTwo = null) =>
        StickyRoute.For("aaa", Tag, ChatBoxLine.Plain(input), ShortCommandRule.For(source, chatTwo).AsText);

    /// <summary>ChatTwo's main input on <paramref name="chatType"/>, as it is while it sends <paramref name="typed"/>.</summary>
    private static ChatTwoLine MainInput(int chatType, string typed, string sent) =>
        ChatTwoLine.Of(chatType, typed.Trim().Length > 0, typed.Length, sent);

    private const int ChatTwoSay = 10;
    private const int ChatTwoParty = 14;
    private const int ChatTwoFreeCompany = 24;
    private const int ChatTwoCrossLinkshell1 = 37;

    [Theory]
    [MemberData(nameof(ChatTwoPrefixes))]
    public void TypedInTheGameAShortCommandTalksInThatGameChannelOnce(string prefix) {
        // FFXIV's own rule, and what players type: "/p brb" talks in Party once. Talking in the channel goes on (the
        // route is the game's, not a switch), and plain text still goes to the channel.
        Assert.Equal(StickyRoute.Game, Route($"{prefix} hi", LineSource.Game));
        Assert.Equal(StickyRoute.Game, Route($"{prefix} <item>", LineSource.Game));
        Assert.Equal(new StickyRoute.ToChannel("aaa", "hi"), Route("hi", LineSource.Game));
        // On its own it is still a switch back.
        Assert.Equal(StickyRoute.Leave, Route(prefix, LineSource.Game));
    }

    [Fact]
    public void AMacrosShortCommandTalksInThatGameChannelOnce() {
        // A raid macro's "/p Pull in 5" goes to Party, as the owner saw and wants; its plain text still goes to the channel.
        Assert.Equal(StickyRoute.Game, Route("/p Pull in 5", LineSource.Game));
        Assert.Equal(new StickyRoute.ToChannel("aaa", "hello"), Route("hello", LineSource.Game));
    }

    [Fact]
    public void InChatTwoPlainTypingSentWithItsChannelsCommandGoesToTheChannel() {
        // "hi" typed in ChatTwo on Party is sent as "/p hi" while its input still holds "hi".
        Assert.Equal(new StickyRoute.ToChannel("aaa", "hi"), Route("/p hi", LineSource.Plugin, MainInput(ChatTwoParty, "hi", "/p hi")));
        Assert.Equal(new StickyRoute.ToChannel("aaa", "hello there"),
            Route("/cwl1 hello there", LineSource.Plugin, MainInput(ChatTwoCrossLinkshell1, "hello there", "/cwl1 hello there")));
        // A link alone: sent to the channel too.
        Assert.Equal(new StickyRoute.ToChannel("aaa", "<item>"),
            Route("/cwl1 <item>", LineSource.Plugin, MainInput(ChatTwoCrossLinkshell1, "<item>", "/cwl1 <item>")));
    }

    [Fact]
    public void InChatTwoAnotherChannelsShortCommandTalksInThatGameChannelOnce() {
        // ChatTwo on Party, "/s hi" typed: sent as typed, so the input holds the whole line. Say, once.
        Assert.Equal(StickyRoute.Game, Route("/s hi", LineSource.Plugin, MainInput(ChatTwoParty, "/s hi", "/s hi")));
        Assert.Equal(StickyRoute.Game, Route("/fc hi", LineSource.Plugin, MainInput(ChatTwoParty, "/fc hi", "/fc hi")));
        Assert.Equal(StickyRoute.Game, Route("/cwl1 hi", LineSource.Plugin, MainInput(ChatTwoFreeCompany, "/cwl1 hi", "/cwl1 hi")));
        Assert.Equal(StickyRoute.Game, Route("/p brb", LineSource.Plugin, MainInput(ChatTwoSay, "/p brb", "/p brb")));
    }

    [Fact]
    public void InChatTwoItsOwnChannelsShortCommandTypedIsTheChannelsTheAcceptedEdge() {
        // ChatTwo on Party, "/p hi" typed: indistinguishable from typing "hi" there, so it goes to the LookingGlass channel.
        // The long form talks in Party.
        Assert.Equal(new StickyRoute.ToChannel("aaa", "hi"), Route("/p hi", LineSource.Plugin, MainInput(ChatTwoParty, "/p hi", "/p hi")));
        Assert.Equal(StickyRoute.Game, Route("/party hi", LineSource.Plugin, MainInput(ChatTwoParty, "/party hi", "/party hi")));
    }

    [Fact]
    public void FromChatTwoWithoutItsMainInputsWordEveryShortCommandIsTheChannels() {
        var notMain = MainInput(ChatTwoParty, "", "/s hi");
        Assert.False(notMain.FromMainInput);
        foreach (var chatTwo in new ChatTwoLine?[] {
                     // A pop-out with its own input on Say, the main input empty (or holding a draft of another length).
                     notMain,
                     MainInput(ChatTwoParty, "a draft", "/s hi"),
                     // ChatTwo's typing IPC didn't answer, or named a channel LookingGlass doesn't know.
                     null,
                     ChatTwoLine.Of(9999, true, 5, "/s hi"),
                 }) {
            Assert.Equal(new StickyRoute.ToChannel("aaa", "hi"), Route("/s hi", LineSource.Plugin, chatTwo));
            Assert.Equal(new StickyRoute.ToChannel("aaa", "hi"), Route("/p hi", LineSource.Plugin, chatTwo));
        }

        // The way in not known (the ProcessChatBoxEntry hook missing): it could be ChatTwo's typing.
        Assert.Equal(new StickyRoute.ToChannel("aaa", "hi"), Route("/s hi", LineSource.Unknown));
        Assert.Equal(new StickyRoute.ToChannel("aaa", "hi"), Route("/s hi", LineSource.Unknown, MainInput(ChatTwoParty, "/s hi", "/s hi")));
        // The long forms are the game's from anywhere.
        Assert.Equal(StickyRoute.Game, Route("/say hi", LineSource.Plugin));
        Assert.Equal(StickyRoute.Game, Route("/say hi", LineSource.Unknown));
    }

    [Fact]
    public void ChatTwosMainInputIsKnownByWhatItHoldsWhileItSends() {
        // Typed as a command: the input holds the line as sent.
        Assert.True(ChatTwoLine.Of(ChatTwoParty, true, 5, "/s hi").FromMainInput);
        // Plain text: the input holds it without the command ChatTwo put in front.
        Assert.True(ChatTwoLine.Of(ChatTwoParty, true, 2, "/p hi").FromMainInput);
        Assert.Equal("/p", ChatTwoLine.Of(ChatTwoParty, true, 2, "/p hi").Prefix);
        // Not its channel's command, and not the whole line: some other input sent it.
        Assert.False(ChatTwoLine.Of(ChatTwoParty, true, 2, "/s hi").FromMainInput);
        // An empty input sent nothing.
        Assert.False(ChatTwoLine.Of(ChatTwoParty, false, 0, "/s hi").FromMainInput);
        Assert.False(ChatTwoLine.Of(ChatTwoParty, false, 5, "/s hi").FromMainInput);
    }

    [Fact]
    public void InChatTwoOnEchoATellOrExtraChatEveryShortCommandIsTheGames() {
        // None of the game's short commands is the typing of an input with no channel (echo), a tell or ExtraChat's.
        Assert.Equal(StickyRoute.Game, Route("/p hi", LineSource.Plugin, MainInput(56, "/p hi", "/p hi")));
        Assert.Equal(StickyRoute.Game, Route("/s hi", LineSource.Plugin, MainInput(1001, "/s hi", "/s hi")));
        Assert.Equal(StickyRoute.Game, Route("/e hi", LineSource.Plugin, MainInput(56, "hi", "/e hi")));
    }

    [Theory]
    [InlineData(10, "/s")]
    [InlineData(11, "/sh")]
    [InlineData(12, "/t")]
    [InlineData(14, "/p")]
    [InlineData(15, "/a")]
    [InlineData(16, "/l1")]
    [InlineData(23, "/l8")]
    [InlineData(24, "/fc")]
    [InlineData(27, "/b")]
    [InlineData(30, "/y")]
    [InlineData(36, "/pt")]
    [InlineData(37, "/cwl1")]
    [InlineData(101, "/cwl2")]
    [InlineData(107, "/cwl8")]
    [InlineData(56, "/e")]
    [InlineData(1001, "/ecl1")]
    [InlineData(1008, "/ecl8")]
    [InlineData(32, null)] // ChatTwo's cross-party: no input channel
    [InlineData(0, null)]
    [InlineData(108, null)]
    public void ChatTwosChatTypesNameTheirShortCommands(int chatType, string? prefix) {
        // ChatTwo 1.40.9: ChatType numbers (Code/ChatType.cs) and InputChannelExt.Prefix.
        Assert.Equal(prefix, ChatChannelPrefixes.OfChatTwoType(chatType));
    }

    [Fact]
    public void EveryShortCommandChatTwoSendsTextWithHasItsChatType() {
        var named = Enumerable.Range(0, 1100).Select(ChatChannelPrefixes.OfChatTwoType).Where(prefix => prefix != null).ToHashSet();
        Assert.True(ChatChannelPrefixes.ChatTwo.All(named.Contains));
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
        Assert.True(StickyRoute.For("aaa", Tag, "/p hi", ChatChannelPrefixes.SentAs()).KeepsFromGame);
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
    public void StartingSaysOnlyWhereTypingGoesNow() {
        foreach (var chatTwo in new[] { false, true }) {
            var start = new StickyChannel().Enter("aaa", Tag, World(new object()), true, chatTwo, false);
            Assert.True(start.Entered);
            Assert.Equal("Now talking in [sky].", start.Text);
        }
    }

    [Fact]
    public void WithChatTwoOneShortLineExplainsItsLabelOnlyTheFirstTime() {
        // Why its label says "(Warning: Party)", that it's still only this channel, and the long command for a game
        // channel once: one sentence.
        var note = StickyMessages.ChatTwoNoteFor(Tag, chatTwo: true, shownBefore: false);
        Assert.Equal(StickyMessages.ChatTwoNote(Tag), note);
        Assert.Contains("(Warning: …)", note);
        Assert.Contains(Tag, note);
        Assert.Contains("/party hi", note);
        Assert.Single(note!.Split(". ", StringSplitOptions.RemoveEmptyEntries));
        Assert.True(note.Length < 160, note);

        // Once ever, and never without ChatTwo.
        Assert.Null(StickyMessages.ChatTwoNoteFor(Tag, chatTwo: true, shownBefore: true));
        Assert.Null(StickyMessages.ChatTwoNoteFor(Tag, chatTwo: false, shownBefore: false));
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

    [Fact]
    public void ADroppedConnectionKeepsItButDisconnectEndsIt() {
        var session = new object();
        var sticky = Started(session);

        // The connection drops on its own: the session stays and reconnects, whatever state it is in meanwhile. Talking in
        // the channel goes on, failing closed: what is typed isn't sent, says so, and never goes to game chat.
        foreach (var state in Enum.GetValues<ConnectionState>()) {
            Assert.Null(sticky.Check(World(session, Snapshot(state, false))));
        }

        Assert.Equal("aaa", sticky.ChannelId);
        Assert.True(StickyRoute.For(sticky.ChannelId, Tag, "secret", NoPrefixes).KeepsFromGame);

        // Disconnect pressed: the session is gone, so it ends, saying so.
        Assert.Equal(StickyEnd.Disconnected, sticky.Check(World(null)));
        Assert.Null(sticky.ChannelId);
        Assert.Equal("Stopped talking in [sky]: disconnected.", StickyMessages.Ended(Tag, StickyEnd.Disconnected));

        // A new session (another server, an identity reset) is another ending.
        sticky = Started(session);
        Assert.Equal(StickyEnd.SessionEnded, sticky.Check(World(new object())));

        // Logging out stops the session too, but is said as a logout.
        sticky = Started(session);
        Assert.Equal(StickyEnd.LoggedOut, sticky.Check(World(null) with { ContentId = 0 }));
    }

    [Theory]
    [MemberData(nameof(Endings))]
    public void ItEndsWhenAnythingItNeedsChanges(string what, StickyEnd expected) {
        var session = new object();
        var sticky = Started(session);
        var world = what switch {
            "logged out" => World(session) with { ContentId = 0 },
            "other character" => World(session) with { ContentId = 99 },
            "disconnected" => World(null),
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
        { "disconnected", StickyEnd.Disconnected },
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
            StickyMessages.Entered(Tag),
            StickyMessages.ChatTwoNote(Tag),
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
        Assert.Equal("Not sent to [sky] or game chat: too fast.", StickyMessages.NotSent(Tag, "too fast"));
        Assert.Equal("Not sent to [sky] or game chat: too fast.", StickyMessages.NotSent(Tag, " too fast. "));
        Assert.Equal("Not sent to [sky] or game chat: Slow down (RateLimited).", StickyMessages.NotSent(Tag, "Slow down (RateLimited)"));
        Assert.Equal("Not sent to [sky] or game chat: nothing in it can be sent to a channel.", StickyMessages.NotSent(Tag, StickyMessages.NoTextReason));
    }

    [Fact]
    public void StoppingSaysSoInAFewWords() {
        Assert.Equal("Stopped talking in [sky].", StickyMessages.Ended(Tag, StickyEnd.ChannelSwitched));
        Assert.Equal("Stopped talking in [sky].", StickyMessages.Ended(Tag, StickyEnd.Stopped));
        Assert.Equal("Stopped talking in [sky]: you logged out.", StickyMessages.Ended(Tag, StickyEnd.LoggedOut));
        Assert.Equal("Stopped talking in [sky]: disconnected.", StickyMessages.Ended(Tag, StickyEnd.Disconnected));
        Assert.All(Enum.GetValues<StickyEnd>(), end => {
            var text = StickyMessages.Ended(Tag, end);
            Assert.StartsWith("Stopped talking in [sky]", text);
            Assert.True(text.Length <= 64, text);
        });
    }

    // ---------------------------------------------------------------- the labels follow the state

    [Fact]
    public void ChatTwosLabelIsSetWhileTalkingAndClearedAtOnceWhenItStops() {
        var keeper = new LabelKeeper(1000);

        // Never talked in a channel: nothing is sent, so another plugin's label is left alone.
        Assert.False(keeper.ShouldSend(null, 0));
        Assert.Null(keeper.Shown);

        // Talking: set once, then again only when it changes, or after a while in case it was lost.
        Assert.True(keeper.ShouldSend("[sky]", 10));
        Assert.False(keeper.ShouldSend("[sky]", 20));
        Assert.False(keeper.ShouldSend("[sky]", 1009));
        Assert.True(keeper.ShouldSend("[sky]", 1010));
        Assert.True(keeper.ShouldSend("[moon]", 1020));
        Assert.Equal("[moon]", keeper.Shown);

        // Stopped, however it stopped: cleared on the very next check, once.
        Assert.True(keeper.ShouldSend(null, 1030));
        Assert.Null(keeper.Shown);
        Assert.False(keeper.ShouldSend(null, 1031));
        Assert.False(keeper.ShouldSend(null, 99999));
    }

    [Fact]
    public void SlashSOnItsOwnNeverLeavesTheLabelSayingTheChannelWhileTextGoesToTheGame() {
        // The case from the game: ChatTwo on Say, talking in [sky], "/s" typed. It ends on the line itself, so by the
        // next check the label is cleared, and only then does "/s test" (ChatTwo's "test") go to Say.
        var session = new object();
        var sticky = Started(session);
        var keeper = new LabelKeeper(1000);
        var withChatTwo = ChatChannelPrefixes.SentAs();
        Assert.True(keeper.ShouldSend(Tag, 0));

        Assert.True(StickyRoute.For(sticky.ChannelId, Tag, "/s test", withChatTwo).KeepsFromGame);
        Assert.Equal(StickyRoute.Leave, StickyRoute.For(sticky.ChannelId, Tag, "/s", withChatTwo));
        sticky.Leave();
        Assert.True(keeper.ShouldSend(sticky.ChannelId, 1));
        Assert.Null(keeper.Shown);
        Assert.Equal(StickyRoute.Game, StickyRoute.For(sticky.ChannelId, Tag, "/s test", withChatTwo));

        // The game's channel switch for it, if it comes, finds nothing to end.
        Assert.Null(sticky.ChannelSwitchCalled(Say, fromTypedCommand: true));
    }

    // ---------------------------------------------------------------- lines the game runs inside another line

    [Fact]
    public void AReplysTextRunInsideItIsTheGamesNotTheChannels() {
        // The game's /r sets the tell target, then runs the gate's function again with only the text after "/r". Judged,
        // that "hello" would go to the whole channel and the tell would never be sent.
        var replies = NestedLines.Replies;
        Assert.True(NestedLines.PassThrough(["/r"], replies));
        Assert.True(NestedLines.PassThrough(["/reply"], replies));
        Assert.True(NestedLines.PassThrough(["/R"], replies));
        // The directly enclosing line is the one that counts.
        Assert.True(NestedLines.PassThrough(["/lgc3", "/r"], replies));
        Assert.False(NestedLines.PassThrough(["/r", "/lgc3"], replies));
    }

    [Fact]
    public void AnyOtherLineRunInsideALineIsStillJudged() {
        var replies = NestedLines.Replies;
        // A line on its own (not inside another).
        Assert.False(NestedLines.PassThrough([], replies));
        // A plugin command that submits plain text while it runs: still judged, so it goes to the channel, not game chat.
        Assert.False(NestedLines.PassThrough(["/lgc3"], replies));
        Assert.False(NestedLines.PassThrough(["/xlhelp"], replies));
        // The game's own command that runs a stored line, a tell, a channel command: judged.
        Assert.False(NestedLines.PassThrough(["/t"], replies));
        Assert.False(NestedLines.PassThrough(["/tell"], replies));
        Assert.False(NestedLines.PassThrough(["/p"], replies));
        Assert.False(NestedLines.PassThrough(["/rr"], replies));
        // The enclosing line was plain text (no command).
        Assert.False(NestedLines.PassThrough([null], replies));
    }

    [Fact]
    public void TheGamesOwnNamesForReplyCountToo() {
        var replies = NestedLines.RepliesWith(["/antworten", " /a2 ", "", "x", "/"]);
        Assert.True(NestedLines.PassThrough(["/antworten"], replies));
        Assert.True(NestedLines.PassThrough(["/A2"], replies));
        Assert.True(NestedLines.PassThrough(["/r"], replies));
        Assert.DoesNotContain("/", replies);
        Assert.DoesNotContain("", replies);
    }

    [Fact]
    public void TheEnclosingCommandIsReadFromTheLinesBytes() {
        Assert.Equal("/r", NestedLines.CommandOf(ChatBoxLine.Plain("/r hello there")));
        Assert.Equal("/reply", NestedLines.CommandOf(ChatBoxLine.Plain("/reply")));
        Assert.Null(NestedLines.CommandOf(ChatBoxLine.Plain("hello /r")));
        Assert.Null(NestedLines.CommandOf(ChatBoxLine.Plain("")));
    }

    [Fact]
    public void ANestedPassThroughIsLoggedInFixedWords() {
        Assert.Equal("[sticky] nested line: talking in [sky], 5 bytes, inside a reply (/r) -> to game unjudged (the reply's own text)",
            StickyDiagnostics.NestedPassed(Tag, 5));
        Assert.DoesNotContain("hello", StickyDiagnostics.NestedPassed(Tag, 5));
    }

    // ---------------------------------------------------------------- the diagnostic log (dalamud.log)

    [Fact]
    public void TheDiagnosticLogSaysWhichWayALineCame() {
        // The game's own chat box, macros and gear sets reach the gate directly; ChatTwo (and other plugins) through
        // ProcessChatBoxEntry first. Telling them apart is what showed the game's chat box had gone past the old gate.
        var line = ChatBoxLine.Plain("hello");
        var (route, reason) = StickyRoute.Decide("aaa", Tag, line, ChatChannelPrefixes.SentAs());
        Assert.Equal("[sticky] line from the game: talking in [sky], ChatTwo no, (text), 5 bytes, payload no -> to LookingGlass (plain text)",
            StickyDiagnostics.Line(Tag, false, line, route, reason, source: LineSource.Game));
        Assert.StartsWith("[sticky] line from a plugin (ProcessChatBoxEntry): ", StickyDiagnostics.Line(Tag, true, line, route, reason, source: LineSource.Plugin));
    }

    public static TheoryData<string, string, string> LoggedLines() => new() {
        // input, token, decision
        { "my secret plans", "(text)", "to LookingGlass" },
        { "/s my secret plans", "/s", "to LookingGlass" },
        { "/cwl1 my secret plans", "/cwl1", "to LookingGlass" },
        { "/party my secret plans", "/party", "to game" },
        { "/s", "/s", "stop talking in the channel, then to game" },
        { "/cwl1 <item>", "/cwl1", "to LookingGlass" },
        { "<item>", "(link placeholder)", "to LookingGlass" },
        { "   ", "(blank)", "kept from game" },
        { "/t Secret Person@Zalera my secret plans", "/t", "to game" },
        { "/lgc3 my secret plans", "/lgc3", "to game" },
        // A command nobody knows may be a message typed after a "/" by mistake: not named.
        { "/mysecretplans are here", "(other command)", "to game" },
    };

    [Theory]
    [MemberData(nameof(LoggedLines))]
    public void TheDiagnosticLogSaysWhatWasDecidedButNeverWhatWasTyped(string input, string token, string decision) {
        var withChatTwo = ChatChannelPrefixes.SentAs();
        var line = ChatBoxLine.Plain(input);
        var (route, reason) = StickyRoute.Decide("aaa", Tag, line, withChatTwo);
        Assert.Equal(route, StickyRoute.For("aaa", Tag, line, withChatTwo));

        var log = StickyDiagnostics.Line(Tag, chatTwo: true, line, route, reason);
        Assert.StartsWith("[sticky] line: talking in [sky], ChatTwo yes, ", log);
        Assert.Contains($", {token}, {line.Raw.Length} bytes, payload no -> {decision} (", log);
        Assert.DoesNotContain("secret", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Person", log);
        Assert.DoesNotContain("plans", log);
        Assert.DoesNotContain("<item>", log);
    }

    [Fact]
    public void TheDiagnosticLogNeverHoldsALinksContents() {
        var withChatTwo = ChatChannelPrefixes.SentAs();
        foreach (var line in new[] {
                     Line("/cwl1 ", BareItemLink, "/cwl1 "),
                     LinkLine("/cwl1 ", BareItemLink),
                     LinkLine("/p secret ", NamedItemLink),
                     LinkLine("", NamedItemLink),
                     LinkLine("secret ", NamedItemLink, " plans"),
                     Line("", BareItemLink, ""),
                 }) {
            var (route, reason) = StickyRoute.Decide("aaa", Tag, line, withChatTwo);
            var log = StickyDiagnostics.Line(Tag, chatTwo: true, line, route, reason);
            Assert.Contains("payload yes", log);
            Assert.Contains($"{line.Raw.Length} bytes", log);
            Assert.DoesNotContain("Potion", log);
            Assert.DoesNotContain("5333", log);
            Assert.DoesNotContain("secret", log);
            Assert.DoesNotContain("plans", log);
            Assert.All(log, c => Assert.True(c >= ' ' && c < 0x7F, $"Not plain ASCII in the log: {(int) c}"));
        }

        Assert.Equal("(payload)", StickyDiagnostics.Token(Line("", BareItemLink, "")));
        Assert.Equal("(payload)", StickyDiagnostics.Token(LinkLine("", NamedItemLink)));
        Assert.Equal("(text)", StickyDiagnostics.Token(LinkLine("look ", NamedItemLink)));
        Assert.Equal("(link placeholder)", StickyDiagnostics.Token(ChatBoxLine.Plain("<item>")));
        var linkOnly = LinkLine("/cwl1 ", BareItemLink);
        var decided = StickyRoute.Decide("aaa", Tag, linkOnly, withChatTwo);
        Assert.Equal("[sticky] line: talking in [sky], ChatTwo yes, /cwl1, 26 bytes, payload yes -> to LookingGlass (short command with links only)",
            StickyDiagnostics.Line(Tag, true, linkOnly, decided.Route, decided.Reason));
    }

    [Fact]
    public void TheDiagnosticLogSaysWhatWasSentInCountsOnly() {
        var (message, _) = LinkText.Compose(new TypedLine(Marked("secret ", " plans"), [Potion]));
        var log = StickyDiagnostics.Sent(Tag, 40, message, false);
        Assert.Equal("[sticky] sending: talking in [sky], 40 bytes typed, 21 characters, 1 link(s)", log);
        Assert.DoesNotContain("Potion", log);
        Assert.DoesNotContain("5333", log);
        Assert.DoesNotContain("secret", log);

        var flag = new TypedLink(new ChatLink.MapFlag(129, 11, 9500, -11200), "Limsa Lominsa Lower Decks ( 9.5 , 11.2 )");
        var (withFlag, leftOut) = LinkText.Compose(new TypedLine($"{LinkText.Marker(0)} {LinkText.Marker(1)}", [flag, new TypedLink(null, null)]));
        Assert.True(leftOut);
        log = StickyDiagnostics.Sent(Tag, 30, withFlag, leftOut);
        Assert.EndsWith(", 1 link(s), a link left out (couldn't be read)", log);
        foreach (var secret in new[] { "Limsa", "129", "9500", "11200", "9.5" }) {
            Assert.DoesNotContain(secret, log);
        }
    }
    [Fact]
    public void TheDiagnosticLogNamesSwitchesStartsAndEnds() {
        Assert.Equal("[sticky] channel switch: talking in [sky], chat type 1 -> 1, typed line in flight yes -> ended (ChannelSwitched)",
            StickyDiagnostics.ChannelSwitch(Tag, Say, Say, true, StickyEnd.ChannelSwitched));
        Assert.Equal("[sticky] channel switch: talking in [sky], chat type 9 -> 9, typed line in flight no -> goes on",
            StickyDiagnostics.ChannelSwitch(Tag, new GameChannel(9), new GameChannel(9), false, null));
        Assert.Equal("[sticky] channel switch: not talking in a channel, chat type unknown -> 2, typed line in flight no -> nothing to do",
            StickyDiagnostics.ChannelSwitch(null, null, Party, false, null));
        Assert.Equal("[sticky] start: talking in [sky], ChatTwo yes, chat type 1", StickyDiagnostics.Started(Tag, true, Say, false));
        Assert.Equal("[sticky] end: stopped talking in [sky] (Disconnected), chat type 2, saved type 0, saved command (none), agent channel 2, label #0012",
            StickyDiagnostics.Ended(Tag, StickyEnd.Disconnected, new ChatBoxState(2, 0, "", 2, 0x12)));
        Assert.Equal("[sticky] end: stopped talking in [sky] (Stopped), chat box unreadable", StickyDiagnostics.Ended(Tag, StickyEnd.Stopped, null));
        Assert.StartsWith("[sticky] start refused: ChatTwo no, chat type 1: ", StickyDiagnostics.Refused(StickyMessages.StillLoading, false, Say));
        // A short command let through as the player's one-off says so, and which rule let it through.
        Assert.Equal("short command, to the game once", StickyRoute.Decide("aaa", Tag, ChatBoxLine.Plain("/s hi"), NoPrefixes).Reason);
        Assert.Equal("command", StickyRoute.Decide("aaa", Tag, ChatBoxLine.Plain("/party hi"), NoPrefixes).Reason);
        var line = ChatBoxLine.Plain("/s my secret plans");
        var rule = ShortCommandRule.For(LineSource.Game, null);
        var (route, reason) = StickyRoute.Decide("aaa", Tag, line, rule.AsText);
        Assert.Equal("[sticky] line from the game: talking in [sky], ChatTwo no, /s, 18 bytes, payload no -> to game (short command, to the game once); " +
                     "rule: typed in the game: short commands go to the game once",
            StickyDiagnostics.Line(Tag, false, line, route, reason, source: LineSource.Game, rule: rule.Why));
        Assert.StartsWith("[sticky] line from an unknown way in (no ProcessChatBoxEntry hook): ",
            StickyDiagnostics.Line(Tag, true, line, route, reason, source: LineSource.Unknown));
    }

    [Fact]
    public void EveryRuleIsLoggedInFixedWords() {
        var rules = new[] {
            ShortCommandRule.For(LineSource.Game, null),
            ShortCommandRule.For(LineSource.Plugin, null),
            ShortCommandRule.For(LineSource.Plugin, new ChatTwoLine(null, true)),
            ShortCommandRule.For(LineSource.Plugin, new ChatTwoLine("/p", false)),
            ShortCommandRule.For(LineSource.Plugin, new ChatTwoLine("/p", true)),
            ShortCommandRule.For(LineSource.Plugin, new ChatTwoLine("/e", true)),
            ShortCommandRule.For(LineSource.Unknown, new ChatTwoLine("/p", true)),
        };
        Assert.Equal(rules.Length, rules.Select(rule => rule.Why).Distinct().Count());
        Assert.All(rules, rule => Assert.All(rule.Why, c => Assert.True(c >= ' ' && c < 0x7F, rule.Why)));
    }

    // ---------------------------------------------------------------- a one-off switch (the channel the game saves to go back to)

    private static readonly ChatBoxState Idle = new(1, 0, "", 1, 7);

    [Fact]
    public void AOneOffSwitchEndsIt() {
        // For a one-off switch (a tell from a menu, a channel for one line), the game saves the channel it is on to go back
        // to (RaptureShellModule.TempChatType and TempChatCommand), then switches without making it its channel. That is
        // a switch: it ends, saying so.
        var session = new object();
        var sticky = new StickyChannel();
        Assert.True(sticky.Enter("aaa", Tag, World(session) with { ChatBox = Idle }, true, false, false).Entered);
        Assert.Null(sticky.Check(World(session) with { ChatBox = Idle }));

        // Only the label or the agent's channel changing (a linkshell renamed, the input opened): goes on.
        Assert.Null(sticky.Check(World(session) with { ChatBox = Idle with { LabelHash = 9, AgentChannel = 3 } }));
        // Unreadable: the shell's channel check still holds; this one can't say.
        Assert.Null(sticky.Check(World(session) with { ChatBox = null }));

        Assert.Equal(StickyEnd.ChatBoxSwitched, sticky.Check(World(session) with { ChatBox = Idle with { TempChatType = 1, TempCommand = "/s" } }));
        Assert.Null(sticky.ChannelId);
        Assert.Equal("Stopped talking in [sky].", StickyMessages.Ended(Tag, StickyEnd.ChatBoxSwitched));

        // The same for a command saved alone (its type unchanged) or another channel.
        foreach (var switched in new[] { Idle with { TempCommand = "/cwl1" }, Idle with { TempChatType = 9, TempCommand = "/party" } }) {
            sticky = new StickyChannel();
            Assert.True(sticky.Enter("aaa", Tag, World(session) with { ChatBox = Idle }, true, false, false).Entered);
            Assert.Equal(StickyEnd.ChatBoxSwitched, sticky.Check(World(session) with { ChatBox = switched }));
        }
    }

    [Fact]
    public void ALineLetThroughToTheGameIsMeasuredAgainAfterItRan() {
        // "/party hi" or "/em waves" may leave the saved channel set: that is the line's, not the player switching.
        var session = new object();
        var sticky = new StickyChannel();
        Assert.True(sticky.Enter("aaa", Tag, World(session) with { ChatBox = Idle }, true, false, false).Entered);
        var afterLine = Idle with { TempChatType = 2, TempCommand = "/party" };
        sticky.LinePassed(afterLine);
        Assert.Equal(afterLine, sticky.ChatBoxBaseline);
        Assert.Null(sticky.Check(World(session) with { ChatBox = afterLine }));

        // Cleared later (the input closed): not a switch, but the label holds back the tag until it settles.
        Assert.False(ChatBoxState.Switched(afterLine, Idle));
        Assert.True(ChatBoxState.Unsettled(afterLine, Idle));
        Assert.Null(sticky.Check(World(session) with { ChatBox = Idle }));

        // Not talking in a channel: nothing to measure.
        sticky.Leave();
        sticky.LinePassed(afterLine);
        Assert.Null(sticky.ChatBoxBaseline);
    }

    [Fact]
    public void TheSavedChannelIsASwitchOnlyWithACommandAndUnsettledOnAnyChange() {
        Assert.False(ChatBoxState.Switched(Idle, Idle));
        Assert.False(ChatBoxState.Unsettled(Idle, Idle));
        Assert.False(ChatBoxState.Switched(Idle, Idle with { TempChatType = 4 }));
        Assert.True(ChatBoxState.Unsettled(Idle, Idle with { TempChatType = 4 }));
        Assert.True(ChatBoxState.Switched(Idle, Idle with { TempCommand = " /s " }));
        Assert.False(ChatBoxState.Switched(Idle with { TempCommand = "/s" }, Idle with { TempCommand = "/s " }));
        Assert.False(ChatBoxState.Switched(null, Idle with { TempCommand = "/s" }));
        Assert.False(ChatBoxState.Unsettled(Idle, null));

        // In the log: numbers, and a known command only.
        Assert.Equal("chat type 1, saved type 1, saved command /s, agent channel 1, label #0007", (Idle with { TempChatType = 1, TempCommand = "/s" }).ToString());
        Assert.Contains("saved command (other command)", (Idle with { TempCommand = "/secret words" }).ToString());
        Assert.DoesNotContain("secret", (Idle with { TempCommand = "/secret words" }).ToString());
        Assert.EndsWith(", one-off switch", StickyDiagnostics.ChatBoxChanged(Tag, "frame", Idle, Idle with { TempCommand = "/s" }));
        Assert.Equal("[sticky] link put in the chat input: talking in [sky], kind 3, chat box unreadable", StickyDiagnostics.LinkInserted(Tag, 3, null));
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
