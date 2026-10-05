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
/// The short commands ChatTwo puts in front of plain text it sends for the channel its input shows ("hello" typed in Say
/// is sent as "/s hello"), so sticky mode can tell that text from a command the player typed. ChatTwo's own tables, as
/// its public source has them (<c>InputChannelExt.Prefix</c>, <c>ChatType</c>).
/// </summary>
public static class ChatChannelPrefixes {
    /// <summary>
    /// For the game's chat type (see <see cref="GameChannel"/>), or null for one that isn't a channel to talk in. Null for a
    /// /tell too: ChatTwo sends tells without the chat box, so a "/t" in front of text is always the player's own command.
    /// </summary>
    public static string? ForGameChatType(int chatType) => chatType switch {
        1 => "/s",
        2 => "/p",
        3 => "/a",
        4 => "/y",
        5 => "/sh",
        6 => "/fc",
        7 => "/pt",
        8 => "/b",
        >= 9 and <= 16 => $"/cwl{chatType - 8}",
        >= 19 and <= 26 => $"/l{chatType - 18}",
        _ => null,
    };

    /// <summary>For the chat type ChatTwo reports for its input (its <c>ChatType</c>, the game's chat log types), or null (a /tell too).</summary>
    public static string? ForChatTwoChatType(int chatType) => chatType switch {
        10 => "/s",
        11 => "/sh",
        14 => "/p",
        15 => "/a",
        >= 16 and <= 23 => $"/l{chatType - 15}",
        24 => "/fc",
        27 => "/b",
        30 => "/y",
        36 => "/pt",
        37 => "/cwl1",
        >= 101 and <= 107 => $"/cwl{chatType - 99}",
        _ => null,
    };

    /// <summary>ChatTwo's chat type for a /tell (<c>TellOutgoing</c>).</summary>
    public const int ChatTwoTell = 12;

    /// <summary>
    /// The commands that stand for plain text while talking in a channel (see <see cref="StickyRoute.For"/>). With ChatTwo,
    /// those of the game's channel (which ChatTwo's input follows) and of the channel ChatTwo says its input sends to
    /// (a tab with its own channel); without it, none: the game's chat box sends plain text as it is.
    /// </summary>
    /// <param name="chatTwoInput">ChatTwo's input channel, in its numbering, if it said.</param>
    public static IReadOnlyCollection<string> SentAs(bool chatTwo, GameChannel? game, int? chatTwoInput) {
        if (!chatTwo) {
            return [];
        }

        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (game is { } channel && ForGameChatType(channel.ChatType) is { } gamePrefix) {
            prefixes.Add(gamePrefix);
        }

        if (chatTwoInput is { } input && ForChatTwoChatType(input) is { } inputPrefix) {
            prefixes.Add(inputPrefix);
        }

        return prefixes;
    }
}

/// <summary>Where something submitted from the chat box goes while (or when not) talking in a channel.</summary>
public abstract record StickyRoute {
    private StickyRoute() {
    }

    /// <summary>On to the game, untouched: not talking in a channel, or a command.</summary>
    public sealed record ToGame : StickyRoute;

    /// <summary>To the channel instead of the game.</summary>
    public sealed record ToChannel(string ChannelId, string Text) : StickyRoute;

    /// <summary>Kept from the game, and not sent anywhere; <paramref name="Text"/> says so, if there is anything to say.</summary>
    public sealed record Dropped(string? Text) : StickyRoute;

    public static readonly StickyRoute Game = new ToGame();

    /// <summary>
    /// The one decision for everything the chat box submits. Not talking in a channel: the game's. Otherwise anything that
    /// doesn't start with "/" goes to the channel, and never to the game; so does a command in <paramref name="sentAs"/>
    /// followed by text, which is how ChatTwo sends what was typed in it (see <see cref="ChatChannelPrefixes"/>). Other
    /// commands go to the game, /lgc and channel switches included.
    /// </summary>
    /// <param name="channelId">The channel being talked in, or null.</param>
    /// <param name="tag">The channel's tag, for what is said when nothing is sent.</param>
    /// <param name="input">What was submitted, as text. Not trimmed first: only a "/" at the very start is a command.</param>
    /// <param name="sentAs">Commands that stand for the channel the chat input shows (empty when only the game's chat box is in use).</param>
    public static StickyRoute For(string? channelId, string tag, string input, IReadOnlyCollection<string> sentAs) {
        if (channelId == null) {
            return Game;
        }

        if (input.StartsWith('/')) {
            var command = CommandAndText(input);
            if (command.Text.Length > 0 && sentAs.Any(prefix => string.Equals(prefix, command.Command, StringComparison.OrdinalIgnoreCase))) {
                return new ToChannel(channelId, command.Text);
            }

            return Game;
        }

        var text = input.Trim();
        return text.Length > 0 ? new ToChannel(channelId, text) : new Dropped(null);
    }

    private static (string Command, string Text) CommandAndText(string input) {
        var end = 0;
        while (end < input.Length && !char.IsWhiteSpace(input[end])) {
            end++;
        }

        return (input[..end], input[end..].Trim());
    }
}

/// <summary>Why talking in a channel ended.</summary>
public enum StickyEnd {
    /// <summary>The game's chat channel was changed: /s, /p, Tab, ChatTwo's channel picker or tabs, another plugin.</summary>
    ChannelSwitched,

    /// <summary>The player clicked the server info bar entry.</summary>
    Stopped,

    /// <summary>Logged out, or another character logged in.</summary>
    LoggedOut,

    /// <summary>The session ended: disconnected by hand, the server address changed, or the identity was reset or restored.</summary>
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
        return new StickyStart(true, StickyMessages.Entered(tag, chatTwo));
    }

    /// <summary>Checks the world against what talking in the channel needs; leaves if something changed.</summary>
    /// <returns>Why it ended, or null if it goes on (or wasn't on).</returns>
    public StickyEnd? Check(StickyWorld world) {
        if (this.ChannelId is not { } channelId) {
            return null;
        }

        StickyEnd? end = world.ContentId != this._contentId ? StickyEnd.LoggedOut
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

/// <summary>What sticky mode tells the player. Plain words, shown in both modes.</summary>
public static class StickyMessages {
    public const string Unavailable =
        "Talking in a channel without /lgc isn't available right now (this game version isn't supported yet). Use /lgc3 <message> or /lgc <nickname> <message>.";

    public const string StillLoading = "LookingGlass is still loading your channels. Try again in a moment.";

    public const string NoTellsWithChatTwo =
        "Switch ChatTwo away from a /tell first (for example type /s), then try again. ChatTwo sends tells in a way LookingGlass can't catch.";

    public static string NotConnected(string tag) =>
        $"Can't switch to {tag}: not connected to LookingGlass. Open /lookingglass to check.";

    public static string NotAMember(string tag) => $"Can't switch to {tag}: you're not in that channel on this server.";

    public static string Entered(string tag, bool chatTwo) =>
        $"Now talking in {tag}: what you type in chat goes only to this LookingGlass channel. Type /s (or any chat channel command) to go back." +
        (chatTwo ? " ChatTwo may add \"(Warning: ...)\" with the game's channel; your messages still go only to " + tag + "." : "");

    public static string Ended(string tag, StickyEnd why) => why switch {
        StickyEnd.ChannelSwitched or StickyEnd.Stopped => $"Stopped talking in {tag}. What you type goes to game chat again.",
        StickyEnd.LoggedOut => $"Stopped talking in {tag}: you logged out.",
        StickyEnd.SessionEnded => $"Stopped talking in {tag}: disconnected from LookingGlass. What you type goes to game chat again.",
        StickyEnd.NotInChannel => $"Stopped talking in {tag}: you're no longer in that channel. What you type goes to game chat again.",
        StickyEnd.Unloading => $"Stopped talking in {tag}: LookingGlass was turned off or updated. What you type goes to game chat again.",
        _ => $"Stopped talking in {tag}. What you type goes to game chat again.",
    };

    /// <summary>A message typed while talking in a channel that wasn't sent: it didn't reach game chat either.</summary>
    public static string NotSent(string tag, string reason) {
        reason = reason.Trim();
        if (!reason.EndsWith('.') && !reason.EndsWith('!') && !reason.EndsWith('?')) {
            reason += ".";
        }

        return $"Not sent to {tag}: {reason} It didn't go to game chat either.";
    }

    public const string NotConnectedReason = "not connected to LookingGlass. Open /lookingglass to check.";

    public const string NoTextReason = "there was no text to send (LookingGlass sends text only).";

    public const string SomethingWentWrongReason = "something went wrong.";
}
