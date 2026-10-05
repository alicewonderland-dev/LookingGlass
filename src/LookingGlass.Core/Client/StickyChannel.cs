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
/// The short channel commands ChatTwo puts in front of plain text it sends: "hello" typed in a ChatTwo input on Say is
/// sent as "/s hello". From ChatTwo's public source (1.40.9, <c>InputChannelExt.Prefix</c>), all of them but three:
/// <list type="bullet">
/// <item>"/t": ChatTwo sends tells to a known player without the chat box, and a "/t" line from it names the player
/// first, as the player's own would. Left to the game.</item>
/// <item>"/e" (echo, for an input with no channel): shown only to the player. Left to the game.</item>
/// <item>"/ecl1" to "/ecl8", ExtraChat's commands: only offered while ExtraChat is loaded, and never game chat. Left to
/// ExtraChat.</item>
/// </list>
/// Every input ChatTwo has (its main window, each tab, each pop-out with input) can be on any channel without the game
/// knowing, so while talking in a channel with ChatTwo loaded, every one of these followed by text goes to the channel.
/// The long forms (/say, /party, /shout, /linkshell1, /cwlinkshell1) are only ever typed, and go to the game.
/// </summary>
public static class ChatChannelPrefixes {
    /// <summary>Every short channel command ChatTwo sends plain text with, but /t, /e and /ecl1 to /ecl8.</summary>
    public static readonly IReadOnlyCollection<string> ChatTwo = new[] { "/s", "/p", "/a", "/y", "/sh", "/fc", "/pt", "/b" }
        .Concat(Enumerable.Range(1, 8).Select(i => $"/cwl{i}"))
        .Concat(Enumerable.Range(1, 8).Select(i => $"/l{i}"))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>ChatTwo's chat type for a /tell (<c>TellOutgoing</c>), as its <c>ChatTwo.GetChatInputState</c> reports it.</summary>
    public const int ChatTwoTell = 12;

    /// <summary>
    /// The commands that stand for plain text while talking in a channel (see <see cref="StickyRoute.For"/>): with ChatTwo,
    /// <see cref="ChatTwo"/>; without it, none, as the game's chat box sends plain text as it is.
    /// </summary>
    public static IReadOnlyCollection<string> SentAs(bool chatTwo) => chatTwo ? ChatTwo : [];
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

    /// <summary>The line must not reach the game: everything but <see cref="ToGame"/>.</summary>
    public bool KeepsFromGame => this is not ToGame;

    /// <summary>
    /// The one decision for everything the chat box submits. Not talking in a channel: the game's. Otherwise anything that
    /// doesn't start with "/" goes to the channel, and never to the game; so does a command in <paramref name="sentAs"/>
    /// followed by text, which is how ChatTwo sends what was typed in it (see <see cref="ChatChannelPrefixes"/>). Other
    /// commands go to the game, /lgc and channel switches included.
    /// </summary>
    /// <param name="channelId">The channel being talked in, or null.</param>
    /// <param name="tag">The channel's tag, for what is said when nothing is sent.</param>
    /// <param name="input">What was submitted, as text. Not trimmed first: only a "/" at the very start is a command.</param>
    /// <param name="sentAs">Commands that stand for plain text (see <see cref="ChatChannelPrefixes.SentAs"/>).</param>
    /// <param name="notOnlyText">
    /// The line held more than its text (an item link, an auto-translate phrase): if it has no text at all, it is dropped
    /// with a line saying so, rather than quietly.
    /// </param>
    public static StickyRoute For(string? channelId, string tag, string input, IReadOnlyCollection<string> sentAs, bool notOnlyText = false) {
        if (channelId == null) {
            return Game;
        }

        if (input.StartsWith('/')) {
            var command = CommandAndText(input);
            if (command.Text.Length > 0 && sentAs.Contains(command.Command, StringComparer.OrdinalIgnoreCase)) {
                return new ToChannel(channelId, command.Text);
            }

            return Game;
        }

        var text = input.Trim();
        if (text.Length > 0) {
            return new ToChannel(channelId, text);
        }

        return new Dropped(notOnlyText ? StickyMessages.NotSent(tag, StickyMessages.NoTextReason) : null);
    }

    private static (string Command, string Text) CommandAndText(string input) {
        var end = 0;
        while (end < input.Length && !char.IsWhiteSpace(input[end])) {
            end++;
        }

        return (input[..end], input[end..].Trim());
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

/// <summary>Why talking in a channel ended.</summary>
public enum StickyEnd {
    /// <summary>
    /// The game's chat channel was changed (Tab, ChatTwo's channel picker, a ChatTwo tab with another channel, another
    /// plugin), or a channel command was typed (/s, /p, even for the channel already on).
    /// </summary>
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

    /// <summary>
    /// Something called the game's channel switch (<c>RaptureShellModule.ChangeChatChannel</c>), and it has returned.
    /// Ends talking in the channel if the switch came from a command typed in the chat box (/s while already in Say
    /// leaves the channel unchanged, but is the player switching back), or if the game's channel is no longer the one it
    /// started in. Otherwise it goes on: ChatTwo calls the switch with the channel it is already on at every tab change,
    /// and when its input loses focus after a one-off channel.
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
        (chatTwo
            ? $" In ChatTwo, every chat box and pop-out sends to {tag} too, whatever channel it shows, and so do short commands with a message " +
              "(/p hi): to talk in a game channel just once, use the long command (/party hi), or switch channel first. " +
              $"ChatTwo may add \"(Warning: ...)\" with the game's channel; your messages still go to {tag}, except in a ChatTwo tab " +
              "or pop-out set to a tell, which ChatTwo sends itself."
            : "");

    /// <summary>Said when talking in a channel starts while ExtraChat (or a fork of it) is loaded too.</summary>
    public const string ExtraChatLoaded =
        "ExtraChat is also turned on. It watches the same chat box and ChatTwo's channel name, so the name shown may be " +
        "wrong and ExtraChat may take what you type. Turn ExtraChat off while you talk in LookingGlass channels this way.";

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
