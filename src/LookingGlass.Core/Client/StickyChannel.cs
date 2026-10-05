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
/// Every input ChatTwo has (its main window, each tab, each pop-out with input) can be on any channel without the game
/// knowing, so while talking in a channel with ChatTwo loaded, every one of these followed by anything at all (text, a
/// link, an auto-translate phrase) goes to the channel. The long forms (/say, /party, /shout, /linkshell1,
/// /cwlinkshell1) are only ever typed, and go to the game.
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
    /// The commands that stand for plain text while talking in a channel (see <see cref="StickyRoute.For"/>): with ChatTwo,
    /// <see cref="ChatTwo"/>; without it, none, as the game's chat box sends plain text as it is.
    /// </summary>
    public static IReadOnlyCollection<string> SentAs(bool chatTwo) => chatTwo ? ChatTwo : [];

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
/// <param name="Text">Its text (the plugin reads it with Dalamud: links and auto-translate phrases become their text).</param>
public sealed record ChatBoxLine(byte[] Raw, string Text) {
    /// <summary>
    /// What the chat box holds for a link until it is sent: the game puts these in the chat input when an item, a map
    /// flag or a status is linked, and makes them links only after the line has left the chat box function. ChatTwo's
    /// input holds them the same way (see docs/design.md).
    /// </summary>
    public static readonly IReadOnlyList<string> LinkPlaceholders = ["<item>", "<flag>", "<status>"];

    /// <summary>A line of plain text, as typed.</summary>
    public static ChatBoxLine Plain(string text) => new(Encoding.UTF8.GetBytes(text), text);

    /// <summary>Anything but spaces in <paramref name="text"/>, not counting link placeholders.</summary>
    public static bool HasText(string text) {
        foreach (var placeholder in LinkPlaceholders) {
            text = text.Replace(placeholder, " ", StringComparison.OrdinalIgnoreCase);
        }

        return !string.IsNullOrWhiteSpace(text);
    }

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
    /// <item>Anything that doesn't start with "/" goes to the channel, as text, and never to the game. A line with no text
    /// (only a link) is kept from the game, saying so; a blank one quietly.</item>
    /// <item>A command in <paramref name="sentAs"/> followed by anything at all (text, a link, an auto-translate phrase):
    /// how ChatTwo sends what was typed in it (see <see cref="ChatChannelPrefixes"/>). The same: to the channel, or kept
    /// from the game if it has no text.</item>
    /// <item>A channel switch (<paramref name="switches"/>) on its own: <see cref="Leave"/>.</item>
    /// <item>Any other command goes to the game, /lgc included.</item>
    /// </list>
    /// </summary>
    /// <param name="channelId">The channel being talked in, or null.</param>
    /// <param name="tag">The channel's tag, for what is said when nothing is sent.</param>
    /// <param name="line">What was submitted. Not trimmed first: only a "/" at the very start is a command.</param>
    /// <param name="sentAs">Commands that stand for plain text (see <see cref="ChatChannelPrefixes.SentAs"/>).</param>
    /// <param name="switches">Channel switches (<see cref="ChatChannelPrefixes.Switches"/> if null).</param>
    public static StickyRoute For(string? channelId, string tag, ChatBoxLine line, IReadOnlyCollection<string> sentAs,
        IReadOnlyCollection<string>? switches = null) {
        if (channelId == null) {
            return Game;
        }

        if (line.Raw.Length > 0 && line.Raw[0] == (byte) '/') {
            var (command, anythingAfter) = line.Command();
            if (!anythingAfter) {
                return (switches ?? ChatChannelPrefixes.Switches).Contains(command, StringComparer.OrdinalIgnoreCase) ? Leave : Game;
            }

            return sentAs.Contains(command, StringComparer.OrdinalIgnoreCase)
                ? ToChannelOrDropped(channelId, tag, TextAfter(line.Text, command))
                : Game;
        }

        var text = line.Text.Trim();
        if (!ChatBoxLine.HasText(text) && !ChatBoxLine.HasContent(line.Raw)) {
            return new Dropped(null);
        }

        return ToChannelOrDropped(channelId, tag, text);
    }

    /// <summary>For tests and plain text: <see cref="For(string?, string, ChatBoxLine, IReadOnlyCollection{string}, IReadOnlyCollection{string}?)"/>.</summary>
    public static StickyRoute For(string? channelId, string tag, string input, IReadOnlyCollection<string> sentAs) =>
        For(channelId, tag, ChatBoxLine.Plain(input), sentAs);

    private static StickyRoute ToChannelOrDropped(string channelId, string tag, string text) =>
        ChatBoxLine.HasText(text) ? new ToChannel(channelId, text) : new Dropped(StickyMessages.NotSent(tag, StickyMessages.NoTextReason));

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
}

/// <summary>What sticky mode sees of the world, once a frame and when asked to start.</summary>
/// <param name="Session">The current session (compared by reference), or null.</param>
/// <param name="ContentId">The logged-in character, or 0.</param>
/// <param name="Channel">The game's chat channel, or null if it can't be read.</param>
public sealed record StickyWorld(object? Session, ulong ContentId, SessionSnapshot Snapshot, GameChannel? Channel);

/// <summary>The outcome of asking to talk in a channel.</summary>
/// <param name="Entered">True if now talking in it.</param>
/// <param name="Text">What to tell the player either way.</param>
public readonly record struct StickyStart(bool Entered, string Text);

/// <summary>
/// Sticky mode's state: the channel that plain text typed in the chat box goes to (/lgc3 or /lgc sky with no message),
/// and the session, character and game chat channel it was started in. Leaves when any of them changes. Kept free of game
/// types so it can be tested; the plugin calls it on the game thread only.
/// </summary>
public sealed class StickyChannel {
    private object? _session;
    private ulong _contentId;
    private GameChannel _gameChannel;

    /// <summary>The channel being talked in, or null.</summary>
    public string? ChannelId { get; private set; }

    /// <summary>Starts (or moves) talking in a channel, or says why not.</summary>
    /// <param name="tag">The channel's tag, as in [sky] or [LGC3].</param>
    /// <param name="inputHooked">The chat box hooks are in place: without them typing can't be kept from game chat.</param>
    /// <param name="chatTwo">ChatTwo is in use: its tells skip the chat box (see <see cref="StickyMessages.NoTellsWithChatTwo"/>).</param>
    /// <param name="chatTwoTell">ChatTwo's input is on a /tell.</param>
    public StickyStart Enter(string channelId, string tag, StickyWorld world, bool inputHooked, bool chatTwo, bool chatTwoTell) {
        var refusal = !inputHooked ? StickyMessages.Unavailable
            : world.ContentId == 0 || world.Session == null ? StickyMessages.NotConnected(tag)
            : MembershipRefusal(world.Snapshot, channelId, tag)
            ?? (world.Channel == null ? StickyMessages.Unavailable
                : chatTwo && (world.Channel.Value.IsTell || chatTwoTell) ? StickyMessages.NoTellsWithChatTwo : null);
        if (refusal != null) {
            return new StickyStart(false, refusal);
        }

        this.ChannelId = channelId;
        this._session = world.Session;
        this._contentId = world.ContentId;
        this._gameChannel = world.Channel!.Value;
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
            : world.Snapshot is { State: ConnectionState.Ready, ChannelsLoaded: true } && MembershipRefusal(world.Snapshot, channelId, "") != null ? StickyEnd.NotInChannel
            : world.Channel == null ? StickyEnd.ChannelUnknown
            : world.Channel != this._gameChannel ? StickyEnd.ChannelSwitched
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

    /// <summary>Stops talking in the channel.</summary>
    /// <returns>The channel it was, or null if it wasn't on.</returns>
    public string? Leave() {
        var channelId = this.ChannelId;
        this.ChannelId = null;
        this._session = null;
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
}

/// <summary>What sticky mode tells the player. Plain words, short, shown in both modes.</summary>
public static class StickyMessages {
    public const string Unavailable =
        "Talking in a channel without /lgc doesn't work in this game version yet. Use /lgc3 <message> instead.";

    public const string StillLoading = "Your LookingGlass channels are still loading. Try again in a moment.";

    public const string NoTellsWithChatTwo = "Switch ChatTwo off the tell first (type /s), then try again.";

    public static string NotConnected(string tag) => $"Can't switch to {tag}: not connected to LookingGlass.";

    public static string NotAMember(string tag) => $"Can't switch to {tag}: you're not in that channel.";

    public static string Entered(string tag) => $"Now talking in {tag}.";

    /// <summary>
    /// Said once ever, the first time talking in a channel starts with ChatTwo loaded: what ChatTwo's label means, and
    /// how to talk in a game channel once.
    /// </summary>
    public static string ChatTwoNote(string tag) =>
        $"ChatTwo's \"(Warning: …)\" only names the game channel underneath: messages still go only to {tag}, and the long form (/party hi) talks in a game channel once.";

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
        _ => $"Stopped talking in {tag}.",
    };

    /// <summary>A message typed while talking in a channel that wasn't sent: it didn't reach game chat either.</summary>
    public static string NotSent(string tag, string reason) {
        reason = reason.Trim();
        if (!reason.EndsWith('.') && !reason.EndsWith('!') && !reason.EndsWith('?')) {
            reason += ".";
        }

        return $"Not sent to {tag} or game chat: {reason}";
    }

    public const string NotConnectedReason = "not connected to LookingGlass.";

    public const string NoTextReason = "no text (links can't be sent).";

    public const string SomethingWentWrongReason = "something went wrong.";
}
