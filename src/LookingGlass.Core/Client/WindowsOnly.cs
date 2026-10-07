namespace LookingGlass.Core.Client;

/// <summary>How a window is found for a channel that no window shows, while windows only is on (see <see cref="WindowsOnly"/>).</summary>
public enum WindowOpening {
    /// <summary>Add it as a tab to the window used last (the default, also for settings saved before it existed).</summary>
    AddToLastUsed,

    /// <summary>Open a new window for it each time.</summary>
    NewWindow,
}

/// <summary>Where a channel went: the window, and whether it was opened for it (else it was added there as a tab).</summary>
public sealed record WindowPlacement(ChannelWindowLayout Window, bool Created);

/// <summary>
/// "Show LookingGlass messages only in windows" (off by default): one setting that keeps every channel's messages and
/// information lines out of game chat, whatever each channel's own "Show in game chat" says. What still goes there:
/// <list type="bullet">
/// <item>warnings and critical lines (light and dark red), as for a channel kept out of game chat, so none is ever hidden;</item>
/// <item>lines no window could show: about no channel in particular (the connection, the player's identity), or a
/// channel the player isn't in (an invite to it);</item>
/// <item>answers to what the player did in the game itself, where they are looking: a command typed in the chat box
/// (usage, "Not sent", "Not connected"), talking in a channel ("Now talking in", "Stopped talking in"), and a right-click
/// invite's "Invited Bob@Lich to [sky]". Those are printed by the plugin directly, never through these rules.</item>
/// </list>
/// A line it keeps out of game chat asks for a window (<see cref="PendingWindows"/>): one for a channel no window shows
/// opens one, or a tab in one (<see cref="Place"/>), so nothing is shown nowhere.
/// </summary>
public static class WindowsOnly {
    /// <summary>The setting, as the settings window names it.</summary>
    public const string SettingName = "Show LookingGlass messages only in windows";

    /// <summary>What the setting does, for its tooltip.</summary>
    public const string SettingTooltip =
        "On: no channel's messages, and none of its information lines (someone joined or left, an invite accepted), show in game chat, " +
        "whatever each channel's \"Show in game chat\" says. Your own messages show only in windows too. A channel that no window shows " +
        "opens in one, without taking the keyboard from the game: in combat, a cutscene or a loading screen, once it's over. " +
        "Warnings, and answers to what you type in the chat box (a /lgc command, \"Now talking in\"), still show in game chat.";

    /// <summary>The tooltip of a channel's "Show in game chat" while this is on, and the item can't be changed.</summary>
    public const string GameChatItemTooltip =
        "\"" + SettingName + "\" is on, so no channel shows in game chat now (warnings still do). Change it in Settings, under Chat.";

    /// <summary>The words for each way of opening, for the settings window.</summary>
    public static string NameOf(WindowOpening how) => how switch {
        WindowOpening.NewWindow => "Open a new window each time",
        _ => "Add it as a tab to the window used last",
    };

    /// <summary>Whether a channel's message goes to game chat: never while windows only is on; else as its own setting says.</summary>
    public static bool MessageToGameChat(bool windowsOnly, IReadOnlySet<string> off, string channelId) =>
        !windowsOnly && GameChatChannels.Shows(off, channelId);

    /// <summary>
    /// Where a notice goes besides the channel's history. Off, as before (<see cref="GameChatChannels.NoticeToGameChat"/>). On,
    /// to game chat only if it is shown light or dark red (<see cref="NoticeColours.ToneOf"/>: a warning or a critical line is
    /// never kept from game chat), or no window can show it: it is about no channel, or one the player isn't in (an invite
    /// to it, as "Bob invited you to sky"), or a place from their old keys (see <see cref="CanHaveWindow"/>).
    /// </summary>
    /// <param name="snapshot">The session's now: which channels the player is in.</param>
    public static bool NoticeToGameChat(bool windowsOnly, IReadOnlySet<string> off, SessionNotice notice, SessionSnapshot snapshot) =>
        !windowsOnly
            ? GameChatChannels.NoticeToGameChat(off, notice)
            : notice.ChannelId is not { } channelId || NoticeColours.ToneOf(notice.Level, notice.Kind) != NoticeTone.Info
              || !CanHaveWindow(snapshot, channelId);

    /// <summary>
    /// Whether a notice asks for a window for its channel: only one windows only keeps out of game chat (one that goes there
    /// anyway is seen there; a debug one is shown nowhere).
    /// </summary>
    public static bool NoticeWantsWindow(bool windowsOnly, SessionNotice notice, SessionSnapshot snapshot) =>
        windowsOnly && notice.ChannelId != null && notice.Level != NoticeLevel.Debug
        && !NoticeToGameChat(true, new HashSet<string>(), notice, snapshot);

    /// <summary>Whether a channel can be a window's tab: one in the player's channel list, not a place from their old keys.</summary>
    public static bool CanHaveWindow(SessionSnapshot snapshot, string channelId) => snapshot.FindChannel(channelId) is { OldKeyMembership: false };

    /// <summary>
    /// Finds a window for a channel that a line arrived for. If a window has it already, nothing changes (null). Otherwise,
    /// as <paramref name="how"/> says: a new window with it as its only tab, selected; or a tab at the end of the window used
    /// last (<paramref name="lastUsed"/>, or if that one is gone or none was used yet, the one opened last), not selected, so
    /// the player's tab stays where it was and the new one shows its unread count. With no window open, a new one either way.
    /// </summary>
    public static WindowPlacement? Place(List<ChannelWindowLayout> windows, string channelId, WindowOpening how, string? lastUsed) {
        // What a hand-edited settings file holds where a window or its tabs should be counts as nothing.
        var usable = windows.Where(window => window?.Tabs != null).ToList();
        if (usable.Any(window => window.Tabs.Contains(channelId))) {
            return null;
        }

        var target = how == WindowOpening.AddToLastUsed
            ? usable.FirstOrDefault(window => lastUsed != null && window.Id == lastUsed) ?? usable.LastOrDefault()
            : null;
        if (target == null) {
            return new WindowPlacement(ChannelWindowLayouts.Open(windows, channelId), Created: true);
        }

        target.Tabs.Add(channelId);
        return new WindowPlacement(target, Created: false);
    }
}

/// <summary>
/// The channels waiting for a window while windows only is on: a line arrived for each that game chat didn't show. They
/// wait while the game is busy (in combat, a cutscene or a loading screen), and for the complete channel list, then come
/// out once each, in the order they asked. Asked from any thread (sessions deliver on background threads); taken on the
/// draw thread.
/// </summary>
public sealed class PendingWindows {
    private readonly Lock _lock = new();
    private readonly List<string> _channels = new();

    /// <summary>How many channels are waiting.</summary>
    public int Count {
        get {
            lock (this._lock) {
                return this._channels.Count;
            }
        }
    }

    /// <summary>A line arrived for a channel that game chat didn't show: it wants a window, if none shows it.</summary>
    public void Want(string channelId) {
        lock (this._lock) {
            if (!this._channels.Contains(channelId)) {
                this._channels.Add(channelId);
            }
        }
    }

    /// <summary>
    /// The channels to find a window for now: none while the game is busy or the channel list isn't complete (they wait).
    /// Then all of them, but those no longer in the list and places from the user's old keys, which are dropped.
    /// </summary>
    public IReadOnlyList<string> Take(SessionSnapshot snapshot, bool busy) {
        if (busy || snapshot is not { State: ConnectionState.Ready, ChannelsLoaded: true }) {
            return [];
        }

        List<string> taken;
        lock (this._lock) {
            taken = this._channels.ToList();
            this._channels.Clear();
        }

        return taken.Where(id => WindowsOnly.CanHaveWindow(snapshot, id)).ToList();
    }

    /// <summary>Forgets every channel waiting: the session changed, or windows only was turned off.</summary>
    public void Clear() {
        lock (this._lock) {
            this._channels.Clear();
        }
    }
}
