using System.Text;
using LookingGlass.Protocol;

namespace LookingGlass.Core.Client;

/// <summary>
/// The game's chat channel, as sticky mode watches it: the shell's chat type (<c>RaptureShellModule.ChatType</c>). It
/// names the linkshell too: 19 to 26 are linkshells 1 to 8, 9 to 16 cross-world linkshells 1 to 8.
/// </summary>
public readonly record struct GameChannel(int ChatType) {
    /// <summary>A /tell: chat types 0, 17 and 18.</summary>
    public bool IsTell => this.ChatType is 0 or 17 or 18;
}

/// <summary>
/// The game's chat channel commands, as sticky mode needs them.
/// <para>
/// <see cref="ChatTwo"/>: the short channel commands ChatTwo puts in front of plain text it sends: "hello" typed in a
/// ChatTwo input on Say is sent as "/s hello". From ChatTwo's public source (1.40.9, <c>InputChannelExt.Prefix</c>), all
/// of them but three:
/// </para>
/// <list type="bullet">
/// <item>"/t": ChatTwo sends tells to a known player without the chat box, and a "/t" line from it names the player
/// first, as the player's own would. Left to the game.</item>
/// <item>"/e" (echo, for an input with no channel): shown only to the player. Left to the game.</item>
/// <item>"/ecl1" to "/ecl8", ExtraChat's commands: only offered while ExtraChat is loaded, and never game chat. Left to
/// ExtraChat.</item>
/// </list>
/// Which of these, followed by text, stand for plain text while talking in a channel depends on where the line came from
/// (see <see cref="ShortCommandRule"/>): none, typed in the game; only ChatTwo's current channel's, typed in ChatTwo's main
/// input; all of them (fail safe), from anywhere else a plugin sends from, such as a ChatTwo pop-out with its own input.
/// The long forms (/say, /party, /shout, /linkshell1, /cwlinkshell1) are only ever typed, and go to the game.
/// <para>
/// <see cref="Switches"/>: the commands that switch the game's chat channel when typed on their own (/s, /party, /l1),
/// short and long, in English. The plugin adds the game's own names for them in the client's language.
/// </para>
/// </summary>
public static class ChatChannelPrefixes {
    /// <summary>Every short channel command ChatTwo sends plain text with, but /t, /e and /ecl1 to /ecl8.</summary>
    public static readonly IReadOnlyCollection<string> ChatTwo = new[] { "/s", "/p", "/a", "/y", "/sh", "/fc", "/pt", "/b" }
        .Concat(Enumerable.Range(1, 8).Select(i => $"/cwl{i}"))
        .Concat(Enumerable.Range(1, 8).Select(i => $"/l{i}"))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The channel commands that, typed with nothing after them, switch the game's chat channel: the short ones above and
    /// their long forms, and /n (/novice, /beginner). Not the tell commands: "/t Bob" names a player, and switching to a
    /// tell is seen when the game's channel changes.
    /// </summary>
    public static readonly IReadOnlyCollection<string> Switches = ChatTwo
        .Concat(["/say", "/party", "/alliance", "/yell", "/shout", "/freecompany", "/pvpteam", "/beginner", "/novice", "/n"])
        .Concat(Enumerable.Range(1, 8).Select(i => $"/linkshell{i}"))
        .Concat(Enumerable.Range(1, 8).Select(i => $"/cwlinkshell{i}"))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>ChatTwo's chat type for a /tell (<c>TellOutgoing</c>), as its <c>ChatTwo.GetChatInputState</c> reports it.</summary>
    public const int ChatTwoTell = 12;

    /// <summary>
    /// The strict rule: every short command ChatTwo can send plain text with stands for plain text (see
    /// <see cref="StickyRoute.For"/>), for a line that may be ChatTwo's typing from an input whose channel isn't known.
    /// <see cref="ShortCommandRule"/> chooses it, or a narrower one, for each line.
    /// </summary>
    public static IReadOnlyCollection<string> SentAs() => ChatTwo;

    /// <summary>
    /// The short command ChatTwo sends plain text with for one of its chat types (its own numbering, as
    /// <c>ChatTwo.GetChatInputState</c> reports it: <c>ChatTwo.Code.ChatType</c>, mapped from its input channel by
    /// <c>InputChannelExt.ToChatType</c>, and <c>InputChannelExt.Prefix</c> for the command, in 1.40.9), or null for one
    /// LookingGlass doesn't know.
    /// </summary>
    public static string? OfChatTwoType(int chatType) => chatType switch {
        10 => "/s",
        11 => "/sh",
        ChatTwoTell => "/t",
        14 => "/p",
        15 => "/a",
        >= 16 and <= 23 => $"/l{chatType - 15}",
        24 => "/fc",
        27 => "/b",
        30 => "/y",
        36 => "/pt",
        37 => "/cwl1",
        >= 101 and <= 107 => $"/cwl{chatType - 99}",
        // Echo: an input with no channel.
        56 => "/e",
        >= 1001 and <= 1008 => $"/ecl{chatType - 1000}",
        _ => null,
    };

    /// <summary><see cref="Switches"/> and <paramref name="more"/> (the game's own names for them), ignoring case.</summary>
    public static IReadOnlyCollection<string> SwitchesWith(IEnumerable<string> more) =>
        Switches.Concat(more.Select(command => command.Trim()).Where(command => command.Length > 1 && command.StartsWith('/')))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A line the chat box submitted, as the game's chat box function got it.
/// </summary>
/// <param name="Raw">
/// Its bytes: an SeString, so text, with links and auto-translate phrases as payloads (each starts with the byte 2).
/// </param>
/// <param name="Text">
/// Its text (the plugin reads it with Dalamud: auto-translate phrases become their text, and an item, map, status or
/// party finder link a marker for it, see <see cref="TypedLine"/>; <see cref="Links"/> has what the markers stand for).
/// </param>
public sealed record ChatBoxLine(byte[] Raw, string Text) {
    /// <summary>The links the markers in <see cref="Text"/> stand for (none for a line of plain text).</summary>
    public IReadOnlyList<TypedLink> Links { get; init; } = [];

    /// <summary>The line's text and links, as LookingGlass sends it.</summary>
    public TypedLine Typed => new(this.Text, this.Links);

    /// <summary>
    /// What the chat box holds for a link until it is sent: the game puts these in the chat input when an item, a map
    /// flag, a status or a party finder listing (the recruitment window's chat button) is linked, and makes them links
    /// only after the line has left the chat box function. ChatTwo's input holds them the same way (see docs/design.md).
    /// </summary>
    public static readonly IReadOnlyList<string> LinkPlaceholders = ["<item>", "<flag>", "<status>", "<pfinder>"];

    /// <summary>A line of plain text, as typed.</summary>
    public static ChatBoxLine Plain(string text) => new(Encoding.UTF8.GetBytes(text), text);

    /// <summary>Anything but spaces in <paramref name="text"/>, not counting links (placeholders and markers).</summary>
    public static bool HasText(string text) => !string.IsNullOrWhiteSpace(LinkText.WithoutLinks(text));

    /// <summary>Text or a link (a placeholder or a marker) in <paramref name="text"/>: something to send.</summary>
    public static bool HasSomethingToSend(string text) => HasText(text) || LinkText.HasLink(text);

    /// <summary>
    /// Anything but spaces in <paramref name="bytes"/>: text, or any payload (a link, an auto-translate phrase), whose
    /// first byte (2) isn't a space.
    /// </summary>
    public static bool HasContent(ReadOnlySpan<byte> bytes) {
        foreach (var c in Encoding.UTF8.GetString(bytes)) {
            if (c != '\0' && !char.IsWhiteSpace(c)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// For a line starting with "/": the command, which ends at the first space or control byte (a payload's first byte
    /// too, so "/cwl1" straight before a link is still "/cwl1"), and whether anything but spaces follows it.
    /// </summary>
    public (string Command, bool AnythingAfter) Command() {
        var raw = this.Raw.AsSpan();
        var end = 0;
        while (end < raw.Length && raw[end] > 0x20) {
            end++;
        }

        return (Encoding.UTF8.GetString(raw[..end]), HasContent(raw[end..]));
    }
}

/// <summary>Where something submitted from the chat box goes while (or when not) talking in a channel.</summary>
public abstract record StickyRoute {
    private StickyRoute() {
    }

    /// <summary>On to the game, untouched: not talking in a channel, or a command.</summary>
    public sealed record ToGame : StickyRoute;

    /// <summary>
    /// A channel command on its own (/s, /party, /cwl1): the player is switching back. Talking in the channel ends first,
    /// saying so, and the command then goes on to the game, which switches its channel.
    /// </summary>
    public sealed record LeaveThenGame : StickyRoute;

    /// <summary>To the channel instead of the game.</summary>
    public sealed record ToChannel(string ChannelId, string Text) : StickyRoute;

    /// <summary>Kept from the game, and not sent anywhere; <paramref name="Text"/> says so, if there is anything to say.</summary>
    public sealed record Dropped(string? Text) : StickyRoute;

    public static readonly StickyRoute Game = new ToGame();

    public static readonly StickyRoute Leave = new LeaveThenGame();

    /// <summary>The line must not reach the game.</summary>
    public bool KeepsFromGame => this is ToChannel or Dropped;

    /// <summary>
    /// The one decision for everything the chat box submits. Not talking in a channel: the game's. Otherwise:
    /// <list type="bullet">
    /// <item>Anything that doesn't start with "/" goes to the channel, as text and links (a line with only a link too),
    /// and never to the game. A line with nothing LookingGlass can send (only a payload that is neither text nor a link)
    /// is kept from the game, saying so; a blank one quietly.</item>
    /// <item>A command in <paramref name="sentAs"/> followed by anything at all (text, a link, an auto-translate phrase):
    /// how ChatTwo sends what was typed in it (see <see cref="ChatChannelPrefixes"/>). The same: to the channel, or kept
    /// from the game if there is nothing to send. Any other short channel command followed by something (a link too) is
    /// the player's one-off (/p brb talks in Party once), and goes to the game.</item>
    /// <item>A channel switch (<paramref name="switches"/>) on its own: <see cref="Leave"/>.</item>
    /// <item>Any other command goes to the game, /lgc included.</item>
    /// </list>
    /// </summary>
    /// <param name="channelId">The channel being talked in, or null.</param>
    /// <param name="tag">The channel's tag, for what is said when nothing is sent.</param>
    /// <param name="line">What was submitted. Not trimmed first: only a "/" at the very start is a command.</param>
    /// <param name="sentAs">Commands that stand for plain text for this line (see <see cref="ShortCommandRule"/>).</param>
    /// <param name="switches">Channel switches (<see cref="ChatChannelPrefixes.Switches"/> if null).</param>
    public static StickyRoute For(string? channelId, string tag, ChatBoxLine line, IReadOnlyCollection<string> sentAs,
        IReadOnlyCollection<string>? switches = null) =>
        Decide(channelId, tag, line, sentAs, switches).Route;

    /// <summary>
    /// <see cref="For(string?, string, ChatBoxLine, IReadOnlyCollection{string}, IReadOnlyCollection{string}?)"/>, with
    /// why, in a few fixed words (for the diagnostic log, see <see cref="StickyDiagnostics"/>; never the line's text).
    /// </summary>
    public static (StickyRoute Route, string Reason) Decide(string? channelId, string tag, ChatBoxLine line, IReadOnlyCollection<string> sentAs,
        IReadOnlyCollection<string>? switches = null) {
        if (channelId == null) {
            return (Game, "not talking in a channel");
        }

        if (line.Raw.Length > 0 && line.Raw[0] == (byte) '/') {
            var (command, anythingAfter) = line.Command();
            if (!anythingAfter) {
                return (switches ?? ChatChannelPrefixes.Switches).Contains(command, StringComparer.OrdinalIgnoreCase)
                    ? (Leave, "channel command on its own")
                    : (Game, "command");
            }

            if (!sentAs.Contains(command, StringComparer.OrdinalIgnoreCase)) {
                return (Game, ChatChannelPrefixes.ChatTwo.Contains(command) ? "short command, to the game once" : "command");
            }

            var after = TextAfter(line.Text, command);
            return (ToChannelOrDropped(channelId, tag, after), ChatBoxLine.HasText(after) ? "short command with text"
                : LinkText.HasLink(after) ? "short command with links only" : "short command with nothing to send");
        }

        var text = line.Text.Trim();
        if (!ChatBoxLine.HasSomethingToSend(text) && !ChatBoxLine.HasContent(line.Raw)) {
            return (new Dropped(null), "blank");
        }

        return (ToChannelOrDropped(channelId, tag, text), ChatBoxLine.HasText(text) ? "plain text"
            : LinkText.HasLink(text) ? "links only" : "nothing to send");
    }

    /// <summary>For tests and plain text: <see cref="For(string?, string, ChatBoxLine, IReadOnlyCollection{string}, IReadOnlyCollection{string}?)"/>.</summary>
    public static StickyRoute For(string? channelId, string tag, string input, IReadOnlyCollection<string> sentAs) =>
        For(channelId, tag, ChatBoxLine.Plain(input), sentAs);

    private static StickyRoute ToChannelOrDropped(string channelId, string tag, string text) =>
        ChatBoxLine.HasSomethingToSend(text) ? new ToChannel(channelId, text)
            : new Dropped(StickyMessages.NotSent(tag, StickyChannel.IsLocal(channelId) ? StickyMessages.LocalNoTextReason : StickyMessages.NoTextReason));

    /// <summary>The text after the command: the line's text starts with it (the command is plain text in the line).</summary>
    private static string TextAfter(string text, string command) {
        if (text.StartsWith(command, StringComparison.OrdinalIgnoreCase)) {
            return text[command.Length..].Trim();
        }

        var end = 0;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) {
            end++;
        }

        return text[end..].Trim();
    }
}

/// <summary>
/// The gate in front of the game's chat box function: whether a line goes on to the game. Fails closed: while talking in
/// a channel, a line whose fate couldn't be decided (anything threw) is kept from the game.
/// </summary>
public static class ChatBoxGate {
    /// <param name="active">Talking in a channel. If not, the line goes to the game and nothing else runs.</param>
    /// <param name="decide">Decides, and acts on it (sends, says why not): true to keep the line from the game.</param>
    /// <param name="failed">Told when <paramref name="decide"/> threw; may throw itself.</param>
    /// <returns>True to keep the line from the game.</returns>
    public static bool KeepFromGame(bool active, Func<bool> decide, Action<Exception> failed) {
        if (!active) {
            return false;
        }

        try {
            return decide();
        } catch (Exception ex) {
            try {
                failed(ex);
            } catch {
                // Nothing more can be done; the line is still kept.
            }

            return true;
        }
    }
}

/// <summary>
/// Keeps a label shown by another plugin (ChatTwo's input channel name) in step with sticky mode, once a frame: the label
/// while talking in a channel, sent again now and then in case it was lost (ChatTwo reloaded); and cleared, once, as soon
/// as talking stops, whatever ended it. Only what this side sent is cleared: a label someone else shows is left alone.
/// </summary>
/// <param name="resendAfterMs">How long a label shown is trusted before it is sent again.</param>
public sealed class LabelKeeper(long resendAfterMs) {
    private string? _sent;
    private long _sentAt;

    /// <summary>The label last sent, or null if none is shown (or it was cleared).</summary>
    public string? Shown => this._sent;

    /// <summary>Whether to send <paramref name="wanted"/> now; if so, it counts as sent.</summary>
    /// <param name="wanted">The label to show, or null for none.</param>
    /// <param name="now">The time now, in milliseconds.</param>
    public bool ShouldSend(string? wanted, long now) {
        var send = wanted != this._sent || (wanted != null && now - this._sentAt >= resendAfterMs);
        if (send) {
            this._sent = wanted;
            this._sentAt = now;
        }

        return send;
    }
}

/// <summary>Why talking in a channel ended.</summary>
public enum StickyEnd {
    /// <summary>
    /// The game's chat channel was changed (Tab, ChatTwo's channel picker, a ChatTwo tab with another channel, another
    /// plugin), or a channel command was typed on its own (/s, /p, even for the channel already on).
    /// </summary>
    ChannelSwitched,

    /// <summary>
    /// The game made a one-off switch (a tell from a menu, a channel for one line), saving the channel it was on to go
    /// back to (see <see cref="ChatBoxState"/>).
    /// </summary>
    ChatBoxSwitched,

    /// <summary>The player clicked the server info bar entry.</summary>
    Stopped,

    /// <summary>Logged out, or another character logged in.</summary>
    LoggedOut,

    /// <summary>
    /// The session was stopped: Disconnect pressed in the main window (or a server change waiting to connect). A
    /// connection that drops on its own keeps the session, which reconnects, so it doesn't end talking in a channel.
    /// </summary>
    Disconnected,

    /// <summary>The session was replaced by a new one: the server address changed, or the identity was reset or restored.</summary>
    SessionEnded,

    /// <summary>No longer in the channel: left, removed, disbanded, or "Remove from my list".</summary>
    NotInChannel,

    /// <summary>The game's chat channel couldn't be read any more.</summary>
    ChannelUnknown,

    /// <summary>LookingGlass is being turned off or updated.</summary>
    Unloading,

    /// <summary>
    /// Talking in local chat, its privacy notice was withdrawn (in Settings, or in its window): nothing may be looked up
    /// until it is accepted again.
    /// </summary>
    PrivacyWithdrawn,
}

/// <summary>What sticky mode sees of the world, once a frame and when asked to start.</summary>
/// <param name="Session">The current session (compared by reference), or null.</param>
/// <param name="ContentId">The logged-in character, or 0.</param>
/// <param name="Channel">The game's chat channel, or null if it can't be read.</param>
public sealed record StickyWorld(object? Session, ulong ContentId, SessionSnapshot Snapshot, GameChannel? Channel) {
    /// <summary>The game chat box's own state (the channel saved for a one-off switch), or null if it can't be read.</summary>
    public ChatBoxState? ChatBox { get; init; }

    /// <summary>The server offers local chat on this connection. Only read when starting to talk in local chat.</summary>
    public bool LocalChatAvailable { get; init; }

    /// <summary>The player accepted what local chat tells the server. Read while talking in local chat too: withdrawn, it ends.</summary>
    public bool LocalPrivacyAccepted { get; init; }
}

/// <summary>The outcome of asking to talk in a channel.</summary>
/// <param name="Entered">True if now talking in it.</param>
/// <param name="Text">What to tell the player either way.</param>
public readonly record struct StickyStart(bool Entered, string Text) {
    /// <summary>Local chat's privacy notice must be accepted first: open it (nothing started).</summary>
    public bool AskPrivacy { get; init; }
}

/// <summary>
/// Sticky mode's state: the channel that plain text typed in the chat box goes to (/lgc3 or /lgc sky with no message),
/// and the session, character and game chat channel it was started in. Leaves when any of them changes. Kept free of game
/// types so it can be tested; the plugin calls it on the game thread only.
/// <para>
/// Local chat (/lgl with no message) is held here as one more channel, <see cref="LocalId"/>, so it starts, ends and
/// routes exactly as a channel does. Only what it needs to start differs: the privacy notice accepted and a server that
/// offers local chat, not membership. The plugin sends its lines with local chat's sender, never a channel's.
/// </para>
/// </summary>
public sealed class StickyChannel {
    /// <summary>
    /// The channel ID held while talking in local chat: one no channel can have (theirs are 32 hex digits). It never
    /// leaves sticky mode: nothing that holds channels (windows, unread counts, the chat log) is ever given it.
    /// </summary>
    public const string LocalId = "local";

    private object? _session;
    private ulong _contentId;
    private GameChannel _gameChannel;
    private ChatBoxState? _chatBox;

    /// <summary>The channel being talked in (<see cref="LocalId"/> for local chat), or null.</summary>
    public string? ChannelId { get; private set; }

    /// <summary>Whether <paramref name="channelId"/> is local chat's (<see cref="LocalId"/>).</summary>
    public static bool IsLocal(string? channelId) => channelId == LocalId;

    /// <summary>Starts (or moves) talking in a channel, or in local chat (<see cref="LocalId"/>), or says why not.</summary>
    /// <param name="tag">The channel's tag, as in [sky] or [LGC3]; [Local] for local chat.</param>
    /// <param name="inputHooked">The chat box hooks are in place: without them typing can't be kept from game chat.</param>
    /// <param name="chatTwo">ChatTwo is in use: its tells skip the chat box (see <see cref="StickyMessages.NoTellsWithChatTwo"/>).</param>
    /// <param name="chatTwoTell">ChatTwo's input is on a /tell.</param>
    public StickyStart Enter(string channelId, string tag, StickyWorld world, bool inputHooked, bool chatTwo, bool chatTwoTell) {
        var local = IsLocal(channelId);
        var unavailable = local ? StickyMessages.LocalUnavailable : StickyMessages.Unavailable;
        // As /lgl <message>: until the privacy notice is accepted, ask, before anything else is looked at.
        if (inputHooked && local && LocalChat.FirstStep(world.LocalPrivacyAccepted) == LocalChatStep.AskFirst) {
            return new StickyStart(false, LocalChatWords.PrivacyAskedToTalk.Plain) { AskPrivacy = true };
        }

        var refusal = !inputHooked ? unavailable
            : world.ContentId == 0 || world.Session == null ? StickyMessages.NotConnected(tag)
            : (local ? LocalRefusal(world, tag) : MembershipRefusal(world.Snapshot, channelId, tag))
            ?? (world.Channel == null ? unavailable
                : chatTwo && (world.Channel.Value.IsTell || chatTwoTell) ? StickyMessages.NoTellsWithChatTwo : null);
        if (refusal != null) {
            return new StickyStart(false, refusal);
        }

        this.ChannelId = channelId;
        this._session = world.Session;
        this._contentId = world.ContentId;
        this._gameChannel = world.Channel!.Value;
        this._chatBox = world.ChatBox;
        return new StickyStart(true, StickyMessages.Entered(tag));
    }

    /// <summary>Checks the world against what talking in the channel needs; leaves if something changed.</summary>
    /// <returns>Why it ended, or null if it goes on (or wasn't on).</returns>
    public StickyEnd? Check(StickyWorld world) {
        if (this.ChannelId is not { } channelId) {
            return null;
        }

        // A dropped connection keeps its session (it reconnects), so it never gets here: talking in the channel goes on,
        // and what is typed meanwhile isn't sent (and says so) rather than going to game chat.
        StickyEnd? end = world.ContentId != this._contentId ? StickyEnd.LoggedOut
            : world.Session == null ? StickyEnd.Disconnected
            : !ReferenceEquals(world.Session, this._session) ? StickyEnd.SessionEnded
            : IsLocal(channelId) && !world.LocalPrivacyAccepted ? StickyEnd.PrivacyWithdrawn
            // Local chat is in no channel list, and is never looked for there.
            : !IsLocal(channelId) && world.Snapshot is { State: ConnectionState.Ready, ChannelsLoaded: true } && MembershipRefusal(world.Snapshot, channelId, "") != null ? StickyEnd.NotInChannel
            : world.Channel == null ? StickyEnd.ChannelUnknown
            : world.Channel != this._gameChannel ? StickyEnd.ChannelSwitched
            : ChatBoxState.Switched(this._chatBox, world.ChatBox) ? StickyEnd.ChatBoxSwitched
            : null;
        if (end != null) {
            this.Leave();
        }

        return end;
    }

    /// <summary>
    /// Something called the game's channel switch (<c>RaptureShellModule.ChangeChatChannel</c>), and it has returned.
    /// Ends talking in the channel if the switch came from a command typed in the chat box, or if the game's channel is no
    /// longer the one it started in. Otherwise it goes on: ChatTwo calls the switch with the channel it is already on at
    /// every tab change, and when its input loses focus after a one-off channel. (A channel command typed on its own has
    /// already ended it, see <see cref="StickyRoute.Leave"/>.)
    /// </summary>
    /// <param name="channel">The game's chat channel after the call, or null if it can't be read.</param>
    /// <param name="fromTypedCommand">The call came while a line submitted through the chat box was being run.</param>
    /// <returns>Why it ended, or null if it goes on (or wasn't on).</returns>
    public StickyEnd? ChannelSwitchCalled(GameChannel? channel, bool fromTypedCommand) {
        if (this.ChannelId == null) {
            return null;
        }

        StickyEnd? end = fromTypedCommand ? StickyEnd.ChannelSwitched
            : channel == null ? StickyEnd.ChannelUnknown
            : channel != this._gameChannel ? StickyEnd.ChannelSwitched
            : null;
        if (end != null) {
            this.Leave();
        }

        return end;
    }

    /// <summary>
    /// A line the chat box submitted has been run by the game (a command, or a one-off line to a game channel): the channel
    /// saved for a one-off switch may have been set and reset meanwhile, so what it is now counts as unchanged.
    /// </summary>
    public void LinePassed(ChatBoxState? chatBox) {
        if (this.ChannelId != null) {
            this._chatBox = chatBox;
        }
    }

    /// <summary>The chat box state measured against, for the diagnostic log.</summary>
    public ChatBoxState? ChatBoxBaseline => this._chatBox;

    /// <summary>Stops talking in the channel.</summary>
    /// <returns>The channel it was, or null if it wasn't on.</returns>
    public string? Leave() {
        var channelId = this.ChannelId;
        this.ChannelId = null;
        this._session = null;
        this._chatBox = null;
        this._contentId = 0;
        return channelId;
    }

    /// <summary>Why a channel can't be talked in on this server now, or null if it can: only a member, under their current setup.</summary>
    public static string? MembershipRefusal(SessionSnapshot snapshot, string channelId, string tag) {
        if (snapshot.State != ConnectionState.Ready) {
            return StickyMessages.NotConnected(tag);
        }

        if (!snapshot.ChannelsLoaded) {
            return StickyMessages.StillLoading;
        }

        return snapshot.FindChannel(channelId) is { OldKeyMembership: false, MyRank: Rank.Member or Rank.Moderator or Rank.Admin }
            ? null
            : StickyMessages.NotAMember(tag);
    }

    /// <summary>Why local chat can't be talked in now, or null if it can: connected, to a server that offers it. Channels don't matter.</summary>
    private static string? LocalRefusal(StickyWorld world, string tag) =>
        world.Snapshot.State != ConnectionState.Ready ? StickyMessages.NotConnected(tag)
        : !world.LocalChatAvailable ? LocalChatWords.NotOnThisServer.Plain
        : null;
}

/// <summary>Where a line kept for the channel being talked in is sent (see <see cref="StickyTarget.SendTo"/>).</summary>
public enum StickySendTo {
    /// <summary>To the channel, as /lgc3 &lt;message&gt; would send it.</summary>
    Channel,

    /// <summary>To the friends near, as /lgl &lt;message&gt; would send it.</summary>
    Local,

    /// <summary>
    /// Nowhere: talking in local chat, its privacy notice isn't accepted (withdrawn this very frame, before the frame's
    /// check ended it). Refused, saying so, and talking in local chat ends.
    /// </summary>
    LocalNotAccepted,
}

/// <summary>
/// The one place sticky mode tells local chat (<see cref="StickyChannel.LocalId"/>) from a channel: where a line goes, and
/// which tag and colour are shown. Local chat's ID is never handed to a channel's sender or looked up as a channel.
/// </summary>
public static class StickyTarget {
    /// <summary>Where a line for <paramref name="channelId"/> is sent.</summary>
    public static StickySendTo SendTo(string channelId, bool localPrivacyAccepted) =>
        !StickyChannel.IsLocal(channelId) ? StickySendTo.Channel
        : localPrivacyAccepted ? StickySendTo.Local
        : StickySendTo.LocalNotAccepted;

    /// <summary>The tag shown: [Local] for local chat, or the channel's (<paramref name="channelTag"/>, asked only for a channel).</summary>
    public static string Tag(string channelId, Func<string, string> channelTag) =>
        StickyChannel.IsLocal(channelId) ? LocalChat.Tag : channelTag(channelId);

    /// <summary>The colour shown: local chat's own setting, or the channel's (<paramref name="channelColour"/>, asked only for a channel).</summary>
    public static ChannelColour? Colour(string channelId, ChannelColour? localColour, Func<string, ChannelColour?> channelColour) =>
        StickyChannel.IsLocal(channelId) ? localColour : channelColour(channelId);
}

/// <summary>
/// What sticky mode tells the player. Plain words, short, shown in both modes. "Now talking in", and "Stopped talking
/// in" when the player chose it, only with verbose channel messages on (<see cref="SayEntered"/>, <see cref="SayEnded"/>).
/// </summary>
public static class StickyMessages {
    public const string Unavailable =
        "Talking in a channel without /lgc doesn't work in this game version yet. Use /lgc3 <message> instead.";

    /// <summary><see cref="Unavailable"/>, for local chat.</summary>
    public const string LocalUnavailable =
        $"Talking in local chat without {LocalChat.Command} doesn't work in this game version yet. Use {LocalChat.Command} <message> instead.";

    public const string StillLoading = "Your LookingGlass channels are still loading. Try again in a moment.";

    public const string NoTellsWithChatTwo = "Switch ChatTwo off the tell first (type /s), then try again.";

    public static string NotConnected(string tag) => $"Can't switch to {tag}: not connected to LookingGlass.";

    public static string NotAMember(string tag) => $"Can't switch to {tag}: you're not in that channel.";

    public static string Entered(string tag) => $"Now talking in {tag}.";

    /// <summary>
    /// Said once ever, the first time talking in a channel starts with ChatTwo loaded: what ChatTwo's label means, and
    /// that short commands still talk in a game channel once there, its own channel's too (see <see cref="ShortCommandRule"/>).
    /// </summary>
    public static string ChatTwoNote(string tag) =>
        $"ChatTwo's \"(Warning: …)\" names its own channel: typing still goes to {tag}, and a short command like /p hi talks in that game channel once.";

    /// <summary>Said after a message was sent with a link the game didn't say anything about (not even its name), so it was left out.</summary>
    public const string LinkNotSent = "A link in it couldn't be read, so it was left out.";

    /// <summary>The ChatTwo note to add after "Now talking in", or null: only with ChatTwo loaded, and only if never shown before.</summary>
    public static string? ChatTwoNoteFor(string tag, bool chatTwo, bool shownBefore) => chatTwo && !shownBefore ? ChatTwoNote(tag) : null;

    /// <summary>Said when talking in a channel starts while ExtraChat (or a fork of it) is loaded too.</summary>
    public const string ExtraChatLoaded = "ExtraChat is on too and may take what you type. Turn it off while talking in a channel.";

    public static string Ended(string tag, StickyEnd why) => why switch {
        StickyEnd.LoggedOut => $"Stopped talking in {tag}: you logged out.",
        StickyEnd.Disconnected => $"Stopped talking in {tag}: disconnected.",
        StickyEnd.SessionEnded => $"Stopped talking in {tag}: the connection started over.",
        StickyEnd.NotInChannel => $"Stopped talking in {tag}: you're no longer in it.",
        StickyEnd.Unloading => $"Stopped talking in {tag}: LookingGlass was turned off.",
        StickyEnd.PrivacyWithdrawn => $"Stopped talking in {tag}: you withdrew the privacy notice.",
        _ => $"Stopped talking in {tag}.",
    };

    /// <summary>
    /// Whether the player ended talking in the channel themselves: switched the game's chat channel (a channel command
    /// typed on its own, Tab, ChatTwo's picker or a tab with another channel), made a one-off switch, or clicked the server
    /// info bar entry. (Moving to another LookingGlass channel with /lgcM doesn't end it: it says "Now talking in".)
    /// Everything else wasn't their choice, and so is any reason not listed here: it fails safe, as said.
    /// </summary>
    public static bool ChosenByThePlayer(StickyEnd why) => why switch {
        StickyEnd.ChannelSwitched or StickyEnd.ChatBoxSwitched or StickyEnd.Stopped => true,
        StickyEnd.LoggedOut or StickyEnd.Disconnected or StickyEnd.SessionEnded or StickyEnd.NotInChannel
            or StickyEnd.ChannelUnknown or StickyEnd.Unloading or StickyEnd.PrivacyWithdrawn => false,
        _ => false,
    };

    /// <summary>
    /// Whether to say "Now talking in": only with verbose channel messages on (a setting, off by default). The server info
    /// bar and the chat box labels show it either way, kept in step every frame.
    /// </summary>
    public static bool SayEntered(bool verbose) => verbose;

    /// <summary>
    /// Whether to say "Stopped talking in": always with verbose channel messages on; otherwise only when the player didn't
    /// choose it (<see cref="ChosenByThePlayer"/>), such as a disconnect or a logout, which they couldn't otherwise tell.
    /// </summary>
    public static bool SayEnded(StickyEnd why, bool verbose) => verbose || !ChosenByThePlayer(why);

    /// <summary>A message typed while talking in a channel that wasn't sent: it didn't reach game chat either.</summary>
    public static string NotSent(string tag, string reason) {
        reason = reason.Trim();
        if (!reason.EndsWith('.') && !reason.EndsWith('!') && !reason.EndsWith('?')) {
            reason += ".";
        }

        return $"Not sent to {tag} or game chat: {reason}";
    }

    public const string NotConnectedReason = "not connected to LookingGlass.";

    /// <summary>A line with nothing LookingGlass can send: no text, and no link it can read.</summary>
    public const string NoTextReason = "nothing in it can be sent to a channel.";

    /// <summary><see cref="NoTextReason"/>, for local chat.</summary>
    public const string LocalNoTextReason = "nothing in it can be sent.";

    /// <summary>The server info bar entry's tooltip while talking in a channel, or in local chat.</summary>
    public static string InfoBarTooltip(string tag, bool local) => local
        ? $"What you type in chat goes to {tag}, your friends near you who use LookingGlass, not to game chat. Click to stop."
        : $"What you type in chat goes to the LookingGlass channel {tag}, not to game chat. Click to stop.";

    /// <summary>A line whose only links the game said nothing about (not even their names): nothing was left to send.</summary>
    public const string LinkUnreadableReason = "the link couldn't be read.";

    public const string SomethingWentWrongReason = "something went wrong.";
}
