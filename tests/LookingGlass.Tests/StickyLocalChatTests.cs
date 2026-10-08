using System.Collections.Immutable;
using System.Text.RegularExpressions;
using LookingGlass.Core.Client;
using LookingGlass.Protocol;

namespace LookingGlass.Tests;

/// <summary>
/// Talking in local chat without /lgl (/lgl with no message): sticky mode with local chat as its one pseudo-channel
/// (<see cref="StickyChannel.LocalId"/>). It works as talking in a channel does, but for what it needs to start: the
/// privacy notice accepted and a server that offers local chat, rather than membership of a channel. See "Talking in
/// local chat" in docs/design.md.
/// </summary>
public sealed class StickyLocalChatTests {
    private const string Local = StickyChannel.LocalId;
    private static readonly string Tag = LocalChat.Tag;
    private static readonly IReadOnlyCollection<string> NoPrefixes = [];
    private static readonly GameChannel Say = new(1);
    private static readonly GameChannel Party = new(2);

    // ---------------------------------------------------------------- the pseudo-channel

    [Fact]
    public void ItsIdCanNeverBeAChannels() {
        // A channel's ID is 32 hex digits: nothing that holds channels can ever be handed this one by mistake and find one.
        Assert.DoesNotMatch(new Regex("^[0-9a-fA-F]{32}$"), Local);
        Assert.True(StickyChannel.IsLocal(Local));
        Assert.False(StickyChannel.IsLocal(null));
        Assert.False(StickyChannel.IsLocal("aaa"));
        Assert.False(StickyChannel.IsLocal("0123456789abcdef0123456789abcdef"));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    // A Japanese IME's full-width space.
    [InlineData("　", true)]
    [InlineData("hello", false)]
    [InlineData("  hi  ", false)]
    [InlineData("<item>", false)]
    public void LglAloneStartsTalkingAndAnythingAfterItIsSent(string arguments, bool talks) {
        Assert.Equal(talks, LocalChat.StartsTalking(arguments));
    }

    [Fact]
    public void LglWithOnlyALinkIsSentNotTalkedIn() {
        Assert.False(LocalChat.StartsTalking(LinkText.Marker(0).ToString()));
    }

    // ---------------------------------------------------------------- starting

    [Fact]
    public void StartingNeedsTheHooksThePrivacyNoticeAConnectionAndTheServer() {
        var session = new object();

        // No hooks: refused, saying how to send instead.
        var sticky = new StickyChannel();
        var start = sticky.Enter(Local, Tag, World(session), inputHooked: false, chatTwo: false, chatTwoTell: false);
        Assert.False(start.Entered);
        Assert.False(start.AskPrivacy);
        Assert.Equal(StickyMessages.LocalUnavailable, start.Text);
        Assert.Null(sticky.ChannelId);

        // The privacy notice not accepted: asked, before anything else, as /lgl <message> asks; nothing starts.
        foreach (var world in new[] { World(session), World(null), World(session) with { LocalChatAvailable = false } }) {
            start = sticky.Enter(Local, Tag, world with { LocalPrivacyAccepted = false }, true, false, false);
            Assert.False(start.Entered);
            Assert.True(start.AskPrivacy);
            Assert.Equal(LocalChatWords.PrivacyAskedToTalk.Plain, start.Text);
            Assert.Null(sticky.ChannelId);
        }

        // Not connected, logged out, reconnecting, or a server that doesn't offer local chat: one plain line why.
        AssertRefused(World(null), StickyMessages.NotConnected(Tag));
        AssertRefused(World(session) with { ContentId = 0 }, StickyMessages.NotConnected(Tag));
        AssertRefused(World(session, Snapshot(ConnectionState.Reconnecting, true)), StickyMessages.NotConnected(Tag));
        AssertRefused(World(session, Snapshot(ConnectionState.Connecting, false)), StickyMessages.NotConnected(Tag));
        AssertRefused(World(session) with { LocalChatAvailable = false }, LocalChatWords.NotOnThisServer.Plain);

        // The game's channel can't be read: leaving couldn't be seen.
        AssertRefused(World(session) with { Channel = null }, StickyMessages.LocalUnavailable);

        // ChatTwo on a tell: as for a channel.
        AssertRefused(World(session) with { Channel = new GameChannel(17) }, StickyMessages.NoTellsWithChatTwo, chatTwo: true);
        AssertRefused(World(session), StickyMessages.NoTellsWithChatTwo, chatTwo: true, chatTwoTell: true);

        // Channels don't matter: none, or still loading.
        foreach (var snapshot in new[] { Snapshot(ConnectionState.Ready, false), Snapshot(ConnectionState.Ready, true) }) {
            var local = new StickyChannel();
            start = local.Enter(Local, Tag, World(session, snapshot), true, false, false);
            Assert.True(start.Entered);
            Assert.False(start.AskPrivacy);
            Assert.Equal("Now talking in [Local].", start.Text);
            Assert.Equal(Local, local.ChannelId);
        }
    }

    [Fact]
    public void AChannelNeverNeedsThePrivacyNoticeOrLocalChat() {
        var world = World(new object(), Snapshot(ConnectionState.Ready, true, Member("aaa"))) with { LocalPrivacyAccepted = false, LocalChatAvailable = false };
        var start = new StickyChannel().Enter("aaa", "[sky]", world, true, false, false);
        Assert.True(start.Entered);
        Assert.False(start.AskPrivacy);
    }

    [Fact]
    public void MovingBetweenLocalChatAndAChannel() {
        var session = new object();
        var both = Snapshot(ConnectionState.Ready, true, Member("aaa"));
        var sticky = Started(session, both);

        // /lgl alone again: said again, as /lgc3 alone is in its own channel.
        var again = sticky.Enter(Local, Tag, World(session, both), true, false, false);
        Assert.True(again.Entered);
        Assert.Equal("Now talking in [Local].", again.Text);

        // /lgc3 alone: that channel now.
        Assert.True(sticky.Enter("aaa", "[sky]", World(session, both) with { Channel = Party }, true, false, false).Entered);
        Assert.Equal("aaa", sticky.ChannelId);

        // /lgl alone: local chat now, measured from the game's channel at that moment.
        Assert.True(sticky.Enter(Local, Tag, World(session, both) with { Channel = Party }, true, false, false).Entered);
        Assert.Equal(Local, sticky.ChannelId);
        Assert.Null(sticky.Check(World(session, both) with { Channel = Party }));

        // A refused move keeps local chat.
        Assert.False(sticky.Enter("ccc", "[sun]", World(session, both) with { Channel = Party }, true, false, false).Entered);
        Assert.Equal(Local, sticky.ChannelId);
        Assert.Equal(Local, sticky.Leave());
        Assert.Null(sticky.ChannelId);
    }

    // ---------------------------------------------------------------- leaving

    [Fact]
    public void ItIsNeverEndedForNotBeingInAChannel() {
        var session = new object();
        var sticky = Started(session);

        // Its pseudo-channel is in no channel list, and must never be looked for there.
        Assert.Null(sticky.Check(World(session, Snapshot(ConnectionState.Ready, true))));
        Assert.Null(sticky.Check(World(session, Snapshot(ConnectionState.Ready, true, Member("bbb")))));
        Assert.Null(sticky.Check(World(session, Snapshot(ConnectionState.Ready, false))));
        // A dropped connection keeps it, as a channel: what is typed meanwhile isn't sent, and says so.
        foreach (var state in Enum.GetValues<ConnectionState>()) {
            Assert.Null(sticky.Check(World(session, Snapshot(state, false))));
        }

        // Local chat no longer offered after a reconnect: it goes on, and each line says local chat isn't available.
        Assert.Null(sticky.Check(World(session) with { LocalChatAvailable = false }));
        Assert.Equal(Local, sticky.ChannelId);
    }

    [Theory]
    [InlineData("logged out", StickyEnd.LoggedOut)]
    [InlineData("other character", StickyEnd.LoggedOut)]
    [InlineData("disconnected", StickyEnd.Disconnected)]
    [InlineData("new session", StickyEnd.SessionEnded)]
    [InlineData("switched", StickyEnd.ChannelSwitched)]
    [InlineData("linkshell", StickyEnd.ChannelSwitched)]
    [InlineData("unreadable", StickyEnd.ChannelUnknown)]
    public void ItEndsAsAChannelDoes(string what, StickyEnd expected) {
        var session = new object();
        var sticky = Started(session);
        var world = what switch {
            "logged out" => World(session) with { ContentId = 0 },
            "other character" => World(session) with { ContentId = 99 },
            "disconnected" => World(null),
            "new session" => World(new object()),
            "switched" => World(session) with { Channel = Party },
            "linkshell" => World(session) with { Channel = new GameChannel(20) },
            "unreadable" => World(session) with { Channel = null },
            _ => throw new ArgumentOutOfRangeException(nameof(what)),
        };

        Assert.Equal(expected, sticky.Check(world));
        Assert.Null(sticky.ChannelId);
        Assert.Equal(StickyRoute.Game, StickyRoute.For(sticky.ChannelId, Tag, "hello", NoPrefixes));
    }

    [Fact]
    public void ASwitchCallAndAOneOffSwitchEndItAsForAChannel() {
        var session = new object();
        var sticky = Started(session);
        Assert.Null(sticky.ChannelSwitchCalled(Say, fromTypedCommand: false));
        Assert.Equal(StickyEnd.ChannelSwitched, sticky.ChannelSwitchCalled(Say, fromTypedCommand: true));

        var idle = new ChatBoxState(1, 0, "", 1, 7);
        sticky = new StickyChannel();
        Assert.True(sticky.Enter(Local, Tag, World(session) with { ChatBox = idle }, true, false, false).Entered);
        Assert.Equal(StickyEnd.ChatBoxSwitched, sticky.Check(World(session) with { ChatBox = idle with { TempChatType = 1, TempCommand = "/s" } }));
    }

    [Fact]
    public void StoppingIsSaidAsForAChannel() {
        Assert.Equal("Stopped talking in [Local].", StickyMessages.Ended(Tag, StickyEnd.ChannelSwitched));
        Assert.Equal("Stopped talking in [Local]: disconnected.", StickyMessages.Ended(Tag, StickyEnd.Disconnected));
        Assert.Equal("Stopped talking in [Local]: you logged out.", StickyMessages.Ended(Tag, StickyEnd.LoggedOut));
        Assert.Equal("Stopped talking in [Local]: LookingGlass was turned off.", StickyMessages.Ended(Tag, StickyEnd.Unloading));
    }

    // ---------------------------------------------------------------- where typed text goes

    [Theory]
    [InlineData("hello", "hello")]
    [InlineData("  hello there  ", "hello there")]
    [InlineData(" /s hello", "/s hello")]
    public void PlainTextGoesToLocalChatAndNeverToTheGame(string input, string sent) {
        var route = StickyRoute.For(Local, Tag, input, NoPrefixes);
        Assert.Equal(new StickyRoute.ToChannel(Local, sent), route);
        Assert.True(route.KeepsFromGame);
    }

    [Theory]
    [InlineData("/p brb")]
    [InlineData("/s hello")]
    [InlineData("/lgc3 hello")]
    [InlineData("/lgc3")]
    [InlineData("/lgl hello")]
    [InlineData("/lgl")]
    [InlineData("/t Bob Smith@Zalera hi")]
    public void CommandsAndOneOffsGoToTheGameAsForAChannel(string input) {
        Assert.Equal(StickyRoute.Game, StickyRoute.For(Local, Tag, input, NoPrefixes));
    }

    [Theory]
    [InlineData("/s")]
    [InlineData("/party")]
    [InlineData("/cwl1")]
    public void AChannelCommandOnItsOwnEndsIt(string input) {
        Assert.Equal(StickyRoute.Leave, StickyRoute.For(Local, Tag, input, NoPrefixes));
    }

    [Fact]
    public void ChatTwosTypingIsTreatedAsForAChannel() {
        // ChatTwo's main input on Party, plain text it sent as "/p hi": local chat's.
        var plain = ShortCommandRule.For(LineSource.Plugin, ChatTwoLine.Of(14, true, 2, "/p hi", "hi"));
        Assert.Equal(new StickyRoute.ToChannel(Local, "hi"), StickyRoute.For(Local, Tag, "/p hi", plain.AsText));

        // "/p hi" typed as it is there: Party, once.
        var typed = ShortCommandRule.For(LineSource.Plugin, ChatTwoLine.Of(14, true, 5, "/p hi", "/p hi"));
        Assert.Equal(StickyRoute.Game, StickyRoute.For(Local, Tag, "/p hi", typed.AsText));

        // A pop-out's line: the strict rule.
        var popOut = ShortCommandRule.For(LineSource.Plugin, ChatTwoLine.Of(14, false, 0, "/fc hi"));
        Assert.Equal(new StickyRoute.ToChannel(Local, "hi"), StickyRoute.For(Local, Tag, "/fc hi", popOut.AsText));
    }

    [Fact]
    public void ALineWithNothingToSendSaysSoInLocalChatsWords() {
        Assert.Equal(new StickyRoute.Dropped(null), StickyRoute.For(Local, Tag, "   ", NoPrefixes));

        // Only an auto-translate phrase's bytes, no text and no link.
        var route = StickyRoute.For(Local, Tag, new ChatBoxLine([0x02, 0x2E, 0x03, 0x01, 0x66, 0x03], ""), NoPrefixes);
        Assert.Equal(new StickyRoute.Dropped("Not sent to [Local] or game chat: nothing in it can be sent."), route);
        Assert.True(route.KeepsFromGame);
    }

    // ---------------------------------------------------------------- what is said

    [Fact]
    public void TheUsageSaysLglAloneTalksInLocalChatAndHowToStop() {
        var usage = LocalChatWords.Usage.Plain;
        Assert.Contains($"{LocalChat.Command} <message>", usage);
        Assert.Contains($"{LocalChat.Command} alone", usage);
        Assert.Contains("/s", usage);
        Assert.Contains("on its own", usage);
    }

    [Fact]
    public void TheFirstTimeOneLineSaysWhereTypingGoesAndHowToStop() {
        var note = LocalChatWords.TalkingNoteFor(shownBefore: false);
        Assert.Equal(LocalChatWords.TalkingNote, note);
        Assert.Contains(Tag, note!.Plain);
        Assert.Contains("game chat", note.Plain);
        Assert.Contains("/s", note.Plain);
        Assert.Contains("on its own", note.Plain);
        Assert.Null(LocalChatWords.TalkingNoteFor(shownBefore: true));
    }

    [Fact]
    public void BeingAskedToAcceptFirstSaysWhatToDo() {
        Assert.Contains("window", LocalChatWords.PrivacyAskedToTalk.Plain);
        Assert.Contains(LocalChat.Command, LocalChatWords.PrivacyAskedToTalk.Plain);
        Assert.DoesNotContain("Not sent", LocalChatWords.PrivacyAskedToTalk.Plain);
    }

    [Fact]
    public void OnceAcceptedItSaysWhatToTypeForTheWayItWasAsked() {
        // Asked by /lgl <message>: that message wasn't kept.
        var sending = LocalChatWords.PrivacyAcceptedFor(LocalPrivacyAsked.BySending, talkingInLocal: false)!.Plain;
        Assert.Equal(LocalChatWords.PrivacyAccepted.Plain, sending);
        Assert.Contains($"{LocalChat.Command} <message>", sending);
        Assert.Contains("again", sending);
        Assert.DoesNotContain("alone", sending);

        // Asked by /lgl alone (or a line typed as the notice was withdrawn, which ended talking in local chat): to start it.
        var talking = LocalChatWords.PrivacyAcceptedFor(LocalPrivacyAsked.ByTalking, talkingInLocal: false)!.Plain;
        Assert.Contains($"{LocalChat.Command} again", talking);
        Assert.Contains("talk in local chat", talking);
        Assert.DoesNotContain("<message>", talking);

        // Already talking in local chat, however it was asked: never to type /lgl.
        foreach (var why in new[] { LocalPrivacyAsked.BySending, LocalPrivacyAsked.ByTalking }) {
            var text = LocalChatWords.PrivacyAcceptedFor(why, talkingInLocal: true)!.Plain;
            Assert.DoesNotContain(LocalChat.Command, text);
            Assert.Contains("again", text);
        }

        // Opened from Settings: nothing to say in game chat.
        Assert.Null(LocalChatWords.PrivacyAcceptedFor(LocalPrivacyAsked.FromSettings, talkingInLocal: false));
        Assert.Null(LocalChatWords.PrivacyAcceptedFor(LocalPrivacyAsked.FromSettings, talkingInLocal: true));
        Assert.All(new[] { sending, talking, LocalChatWords.PrivacyAcceptedFor(LocalPrivacyAsked.BySending, true)!.Plain }, PlainLanguage.AssertPlain);
    }

    // ---------------------------------------------------------------- the privacy notice withdrawn

    [Fact]
    public void WithdrawingThePrivacyNoticeEndsItAndSaysSo() {
        var session = new object();
        var sticky = Started(session);
        Assert.Null(sticky.Check(World(session)));

        Assert.Equal(StickyEnd.PrivacyWithdrawn, sticky.Check(World(session) with { LocalPrivacyAccepted = false }));
        Assert.Null(sticky.ChannelId);
        Assert.Equal(StickyRoute.Game, StickyRoute.For(sticky.ChannelId, Tag, "hello", NoPrefixes));

        // Not the player's choice of a channel command: always said, verbose or not.
        Assert.Equal("Stopped talking in [Local]: you withdrew the privacy notice.", StickyMessages.Ended(Tag, StickyEnd.PrivacyWithdrawn));
        Assert.False(StickyMessages.ChosenByThePlayer(StickyEnd.PrivacyWithdrawn));
        Assert.True(StickyMessages.SayEnded(StickyEnd.PrivacyWithdrawn, verbose: false));
        PlainLanguage.AssertPlain(StickyMessages.Ended(Tag, StickyEnd.PrivacyWithdrawn));
    }

    [Fact]
    public void AChannelDoesntCareAboutLocalChatsPrivacyNotice() {
        var session = new object();
        var world = World(session, Snapshot(ConnectionState.Ready, true, Member("aaa")));
        var sticky = new StickyChannel();
        Assert.True(sticky.Enter("aaa", "[sky]", world, true, false, false).Entered);
        Assert.Null(sticky.Check(world with { LocalPrivacyAccepted = false }));
        Assert.Equal("aaa", sticky.ChannelId);
    }

    [Fact]
    public void ARealChannelIsStillEndedWhenNoLongerInIt() {
        // The local chat guard on membership must never spare a real channel, even one moved to from local chat.
        var session = new object();
        var both = Snapshot(ConnectionState.Ready, true, Member("aaa"));
        var sticky = Started(session, both);
        Assert.True(sticky.Enter("aaa", "[sky]", World(session, both), true, false, false).Entered);
        Assert.Equal(StickyEnd.NotInChannel, sticky.Check(World(session, Snapshot(ConnectionState.Ready, true, Member("bbb")))));
        Assert.Null(sticky.ChannelId);
    }

    // ---------------------------------------------------------------- refusals while talking in local chat

    [Fact]
    public void EveryRefusalSaysItDidntGoToGameChatEither() {
        foreach (var refusal in LocalChatWords.Refusals()) {
            Assert.StartsWith("Not sent: ", refusal.Plain);
            Assert.StartsWith("Not sent: ", refusal.Technical);

            // /lgl <message>: as it is.
            Assert.Same(refusal, LocalChatWords.Refusal(refusal, stickyTag: null));

            // Typed while talking in local chat: as for a channel.
            var sticky = LocalChatWords.Refusal(refusal, Tag);
            Assert.StartsWith("Not sent to [Local] or game chat: ", sticky.Plain);
            Assert.StartsWith("Not sent to [Local] or game chat: ", sticky.Technical);
            Assert.EndsWith(refusal.Plain["Not sent: ".Length..], sticky.Plain);
            PlainLanguage.AssertPlain(sticky.Plain);
        }

        Assert.Equal("Not sent to [Local] or game chat: nobody is near enough to hear you (about 20 yalms, as far as /say).",
            LocalChatWords.Refusal(LocalChatWords.NobodyNear, Tag).Plain);
        Assert.Equal("Not sent to [Local] or game chat: local chat isn't available on this server. It may be an older version, or its operator turned it off.",
            LocalChatWords.Refusal(LocalChatWords.NotOnThisServerNotSent, Tag).Plain);
        Assert.Equal("Not sent to [Local] or game chat: LookingGlass couldn't see who is near you.",
            LocalChatWords.Refusal(LocalChatWords.CouldntSeeWhoIsNear, Tag).Plain);
        Assert.Contains(LocalChatWords.PrivacyAsked, LocalChatWords.Refusals());
        Assert.Contains(LocalChatWords.NobodyUsesIt, LocalChatWords.Refusals());
        Assert.Contains(LocalChatWords.Sent(new LocalSendResult(0, 0, 3))!, LocalChatWords.Refusals());
    }

    // ---------------------------------------------------------------- local chat or a channel: one decision

    [Fact]
    public void ALineForLocalChatIsNeverSentAsAChannels() {
        Assert.Equal(StickySendTo.Local, StickyTarget.SendTo(Local, localPrivacyAccepted: true));
        Assert.Equal(StickySendTo.LocalNotAccepted, StickyTarget.SendTo(Local, localPrivacyAccepted: false));
        Assert.Equal(StickySendTo.Channel, StickyTarget.SendTo("aaa", localPrivacyAccepted: true));
        Assert.Equal(StickySendTo.Channel, StickyTarget.SendTo("aaa", localPrivacyAccepted: false));
    }

    [Fact]
    public void LocalChatsTagAndColourAreNeverLookedUpAsAChannels() {
        var local = ChannelColour.OfRow(45);
        var sky = ChannelColour.Custom(0x33DDAA);
        Assert.Equal(Tag, StickyTarget.Tag(Local, _ => throw new InvalidOperationException("looked up as a channel")));
        Assert.Equal("[sky]", StickyTarget.Tag("aaa", id => id == "aaa" ? "[sky]" : "?"));
        Assert.Equal(local, StickyTarget.Colour(Local, local, _ => throw new InvalidOperationException("looked up as a channel")));
        Assert.Null(StickyTarget.Colour(Local, null, _ => sky));
        Assert.Equal(sky, StickyTarget.Colour("aaa", local, _ => sky));
    }

    [Fact]
    public void TheInfoBarSaysWhereTypingGoes() {
        Assert.Equal("What you type in chat goes to the LookingGlass channel [sky], not to game chat. Click to stop.",
            StickyMessages.InfoBarTooltip("[sky]", local: false));
        var local = StickyMessages.InfoBarTooltip(Tag, local: true);
        Assert.Contains("friends near you", local);
        Assert.Contains("not to game chat", local);
        Assert.EndsWith("Click to stop.", local);
    }

    [Fact]
    public void EverythingItSaysIsPlain() {
        Assert.All(new[] {
            StickyMessages.LocalUnavailable,
            StickyMessages.InfoBarTooltip(Tag, local: true),
            StickyMessages.NotSent(Tag, StickyMessages.LocalNoTextReason),
            LocalChatWords.PrivacyAskedToTalk.Plain,
            LocalChatWords.PrivacyAccepted.Plain,
            LocalChatWords.TalkingNote.Plain,
            LocalChatWords.Usage.Plain,
        }, PlainLanguage.AssertPlain);
        Assert.Contains(LocalChat.Command, StickyMessages.LocalUnavailable);
    }

    // ---------------------------------------------------------------- the diagnostic log

    [Fact]
    public void TheDiagnosticLogNamesLocalChatByItsTagAndNeverWhatWasTyped() {
        var line = ChatBoxLine.Plain("meet me by the fountain");
        var route = StickyRoute.Decide(Local, Tag, line, NoPrefixes);
        var logged = StickyDiagnostics.Line(Tag, false, line, route.Route, route.Reason, source: LineSource.Game);
        Assert.Contains("talking in [Local]", logged);
        Assert.Contains("-> to LookingGlass (plain text)", logged);
        Assert.DoesNotContain("fountain", logged);
        Assert.DoesNotContain(Local + ",", logged);

        // /lgl is a known command: named, never its text.
        Assert.Equal("/lgl", StickyDiagnostics.Token(ChatBoxLine.Plain("/lgl secret words")));
        Assert.Equal("/lgl", StickyDiagnostics.Token(ChatBoxLine.Plain("/lgl")));

        Assert.StartsWith("[sticky] start: talking in [Local], ChatTwo no", StickyDiagnostics.Started(Tag, false, Say, false));
        Assert.Equal("[sticky] end: stopped talking in [Local] (ChannelSwitched), chat box unreadable", StickyDiagnostics.Ended(Tag, StickyEnd.ChannelSwitched, null));
    }

    // ---------------------------------------------------------------- helpers

    private static void AssertRefused(StickyWorld world, string why, bool chatTwo = false, bool chatTwoTell = false) {
        var sticky = new StickyChannel();
        var start = sticky.Enter(Local, Tag, world, inputHooked: true, chatTwo, chatTwoTell);
        Assert.False(start.Entered);
        Assert.False(start.AskPrivacy);
        Assert.Equal(why, start.Text);
        Assert.Null(sticky.ChannelId);
    }

    private static StickyChannel Started(object session, SessionSnapshot? snapshot = null) {
        var sticky = new StickyChannel();
        Assert.True(sticky.Enter(Local, Tag, World(session, snapshot), true, false, false).Entered);
        Assert.Equal(Local, sticky.ChannelId);
        return sticky;
    }

    /// <summary>Logged in, ready, the privacy notice accepted and local chat offered.</summary>
    private static StickyWorld World(object? session, SessionSnapshot? snapshot = null) =>
        new(session, 42, snapshot ?? Snapshot(ConnectionState.Ready, true), Say) { LocalChatAvailable = true, LocalPrivacyAccepted = true };

    private static ChannelView Member(string id) => new(id, null, 0, 0, true, false, Rank.Member, []);

    private static SessionSnapshot Snapshot(ConnectionState state, bool loaded, params ChannelView[] channels) =>
        SessionSnapshot.Empty with { State = state, ChannelsLoaded = loaded, Channels = channels.ToImmutableArray() };
}
