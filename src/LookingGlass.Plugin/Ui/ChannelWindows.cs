using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Windowing;
using LookingGlass.Core.Client;

namespace LookingGlass.Plugin.Ui;

/// <summary>
/// The channel windows (pop-out chat, see <see cref="ChannelWindow"/>): which are open, opening and closing them, and
/// remembering them. Their layouts (tabs, order, selected tab, place) are kept in the settings per character and server
/// address (<see cref="CharacterSettings.ChannelWindows"/>) and follow the rules in <see cref="ChannelWindowLayouts"/>.
/// The windows themselves exist only for the session: they close when it stops (logging out, another character or
/// server) without forgetting anything, and open again once the next session for the same character and server has its
/// channel list. Only a window the player closes is forgotten.
/// <para>
/// Draw thread only. <see cref="Update"/> runs before the window system draws, and is the only place windows are added
/// to it or removed from it: menus that open or close a window run while it is drawing, so they change the layouts, and
/// the windows follow at the next <see cref="Update"/>.
/// </para>
/// </summary>
public sealed class ChannelWindows(WindowSystem system, Configuration config, SessionManager sessions, ChannelSender sender, ChatOutput chat) : IDisposable {
    private readonly List<ChannelWindow> _open = new();
    // Layouts the player opened this session (not reopened at login): their windows appear where the mouse is.
    private readonly HashSet<string> _opened = new();
    private ClientSession? _session;
    private SessionSnapshot? _synced;
    private bool _restored;
    // The window the player used last (see Used), by its layout's ID; and windows only as it was last followed.
    private string? _lastUsed;
    private bool _windowsOnly = config.MessagesOnlyInWindows;

    /// <summary>The windows open now, in the order they were opened.</summary>
    public IReadOnlyList<ChannelWindow> Open => this._open;

    internal SessionManager Sessions => sessions;

    internal ChannelSender Sender => sender;

    internal Configuration Config => config;

    /// <summary>
    /// The current character's windows on the current server: null when there is no session, empty (and not added to the
    /// settings) when there are none.
    /// </summary>
    private List<ChannelWindowLayout>? Layouts =>
        sessions.Session != null && sessions.SessionPlayer is { } player ? config.ForCharacter(player.ContentId).WindowsIfAny(sessions.ServerUrl) ?? [] : null;

    /// <summary>
    /// Opens and closes windows to match the session and the layouts. Call every frame, before the window system draws.
    /// Never throws: a fault (a settings file edited by hand into something unexpected) is logged once, and the rest of
    /// the UI goes on.
    /// </summary>
    public void Update() {
        try {
            this.UpdateWindows();
        } catch (Exception ex) {
            if (!this._faultLogged) {
                this._faultLogged = true;
                Services.Log.Error(ex, "Couldn't update the channel windows");
            }
        }
    }

    private bool _faultLogged;

    private void UpdateWindows() {
        var session = sessions.Session;
        if (session != this._session) {
            // Logged out, another character or server, or disconnected: the windows go, their layouts stay.
            this.CloseAll();
            this._session = session;
            this._synced = null;
            this._restored = false;
            this._opened.Clear();
            this._lastUsed = null;
        }

        if (session == null || this.Layouts is not { } layouts) {
            return;
        }

        var snapshot = session.Snapshot;
        if (!ReferenceEquals(snapshot, this._synced)) {
            this._synced = snapshot;
            // Channels left (or disbanded, or removed from) lose their tabs; a window without tabs closes.
            if (ChannelWindowLayouts.Sync(layouts, snapshot)) {
                this.DropEmptyLists();
                config.Save();
            }

            // Reopened at login once the channel list is in, so tabs of channels no longer there are dropped first.
            this._restored |= snapshot is { State: ConnectionState.Ready, ChannelsLoaded: true };
        }

        foreach (var window in this._open.Where(window => !layouts.Contains(window.Layout)).ToList()) {
            this.Remove(window);
        }

        if (!this._restored) {
            return;
        }

        foreach (var layout in layouts.Where(layout => this._open.All(window => window.Layout != layout)).ToList()) {
            var window = new ChannelWindow(this, layout, opened: this._opened.Remove(layout.Id));
            system.AddWindow(window);
            this._open.Add(window);
        }

        if (this.FollowWindowsOnly(snapshot)) {
            return;
        }

        // A channel off game chat that no window shows any more (its last tab or window closed, or none came back at
        // login) would show nowhere: it goes back to game chat, and says so there.
        foreach (var channelId in GameChatChannels.ShownNowhere(sessions.GameChatOff, layouts, snapshot)) {
            sessions.SetShowInGameChat(channelId, true);
            var tag = ChannelTag.For(sessions.SlotOf(channelId), sessions.NicknameOf(channelId), config.NicknameTags);
            chat.ChannelNotice(GameChatChannels.BackInGameChat(tag), tag, sessions.ColourOf(channelId));
        }
    }

    /// <summary>
    /// "Show LookingGlass messages only in windows" (see <see cref="WindowsOnly"/>), once the windows are back. While it is
    /// on, each channel a line arrived for that game chat didn't show gets a window, or a tab in one, if none shows it: once
    /// the game isn't busy (<see cref="GameBusy"/>), and without taking the keyboard from the game. Just turned off, a
    /// channel turned off game chat on its own that no window shows opens in one too, so its choice is kept rather than
    /// undone by the rule below that puts such a channel back in game chat.
    /// </summary>
    /// <returns>
    /// True while it is on, or was just turned off: then no channel is put back in game chat for being shown nowhere (just
    /// turned off, not until the windows opened for that are in the layouts the rule reads, at the next frame).
    /// </returns>
    private bool FollowWindowsOnly(SessionSnapshot snapshot) {
        if (config.MessagesOnlyInWindows) {
            this._windowsOnly = true;
            if (sessions.WantWindows.Count > 0) {
                foreach (var channelId in sessions.WantWindows.Take(snapshot, GameBusy())) {
                    this.OpenInBackground(channelId);
                }
            }

            return true;
        }

        // Off: what was waiting goes to game chat from now on, as each channel's own setting says.
        sessions.WantWindows.Clear();
        if (!this._windowsOnly) {
            return false;
        }

        // Just turned off (or while logged out): once the channel list is complete, so no channel is missed.
        if (snapshot is { State: ConnectionState.Ready, ChannelsLoaded: true } && this.Layouts is { } layouts) {
            this._windowsOnly = false;
            foreach (var channelId in GameChatChannels.ShownNowhere(sessions.GameChatOff, layouts, snapshot)) {
                this.OpenInBackground(channelId);
            }
        }

        return true;
    }

    /// <summary>
    /// In combat, a cutscene or a loading screen: no window opens for windows only then, as one appearing mid-fight would be
    /// in the way. The channels wait (their lines are kept meanwhile), and open once it's over.
    /// </summary>
    private static bool GameBusy() => Services.Condition.Any(ConditionFlag.InCombat, ConditionFlag.OccupiedInCutSceneEvent,
        ConditionFlag.WatchingCutscene, ConditionFlag.WatchingCutscene78, ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51);

    /// <summary>
    /// A window for a channel that no window shows, found as <see cref="Configuration.WindowOpening"/> says (see
    /// <see cref="WindowsOnly.Place"/>): a tab added to the window used last, not selected, or a new window. A new one opens
    /// a little below and right of the window used last, if there is one, and doesn't take the focus from the game.
    /// </summary>
    private void OpenInBackground(string channelId) {
        if (sessions.SessionPlayer is not { } player) {
            return;
        }

        var windows = config.ForCharacter(player.ContentId).WindowsOn(sessions.ServerUrl);
        if (WindowsOnly.Place(windows, channelId, config.WindowOpening, this._lastUsed) is not { } placed) {
            return;
        }

        if (placed.Created && this.LastUsed()?.Layout.Place is { } near) {
            var step = 30 * Widgets.Scale;
            placed.Window.Place = near with { X = near.X + step, Y = near.Y + step };
        }

        config.Save();
    }

    /// <summary>The window used last (see <see cref="Used"/>), or if it is gone or none was used yet, the one opened last.</summary>
    private ChannelWindow? LastUsed() => this._open.FirstOrDefault(window => window.Layout.Id == this._lastUsed) ?? this._open.LastOrDefault();

    /// <summary>
    /// A window the player is using (it has the focus): the one a channel is added to while windows only is on. Kept for the
    /// session only, not saved.
    /// </summary>
    internal void Used(ChannelWindow window) => this._lastUsed = window.Layout.Id;

    /// <summary>A new window with the channel as its only tab.</summary>
    public void OpenNew(string channelId) {
        if (this.Layouts == null || sessions.SessionPlayer is not { } player) {
            return;
        }

        var layout = ChannelWindowLayouts.Open(config.ForCharacter(player.ContentId).WindowsOn(sessions.ServerUrl), channelId);
        this._opened.Add(layout.Id);
        // Opened by the player, so it doesn't wait for the channel list (it is in: the channel was picked from it).
        this._restored = true;
        config.Save();
    }

    /// <summary>Adds a channel to a window as a tab, or selects it there if it has it; and brings the window to the front.</summary>
    public void AddTo(ChannelWindow window, string channelId) {
        ChannelWindowLayouts.AddTab(window.Layout, channelId);
        config.Save();
        window.Show(channelId);
    }

    /// <summary>The first window with a channel as a tab, or null.</summary>
    public ChannelWindow? Showing(string channelId) => this._open.FirstOrDefault(window => window.Layout.Tabs.Contains(channelId));

    /// <summary>
    /// Turns "Show in game chat" on or off for a channel. Turned off while no window has it, it opens in a new one, so
    /// its messages still show somewhere.
    /// </summary>
    public void SetShowInGameChat(string channelId, bool show) {
        sessions.SetShowInGameChat(channelId, show);
        if (!show && this.Layouts is { } layouts && !ChannelWindowLayouts.Showing(layouts, channelId).Any()) {
            this.OpenNew(channelId);
        }
    }

    /// <summary>
    /// The items of a channel's right-click menu in the channel list: open it in a new window, add it to an open one, or
    /// show the window it is in. Call between ImGui's BeginPopup and EndPopup.
    /// </summary>
    public void DrawChannelMenuItems(string channelId) {
        if (ImGui.MenuItem("Open in new window")) {
            this.OpenNew(channelId);
        }

        if (ImGui.BeginMenu("Add to window", this._open.Count > 0)) {
            foreach (var window in this._open) {
                // A tick where it is a tab already: choosing it then shows it there.
                if (ImGui.MenuItem($"{window.MenuName}##{window.Layout.Id}", "", window.Layout.Tabs.Contains(channelId))) {
                    this.AddTo(window, channelId);
                }
            }

            ImGui.EndMenu();
        }

        if (this.Showing(channelId) is { } showing && ImGui.MenuItem("Show its window")) {
            showing.Show(channelId);
        }
    }

    /// <summary>The player closed a window, or its last tab: it is forgotten.</summary>
    internal void Forget(ChannelWindow window) {
        if (this.Layouts is { } layouts && layouts.Remove(window.Layout)) {
            this.DropEmptyLists();
            config.Save();
        }
    }

    /// <summary>No empty list of windows is kept in the settings for a server address.</summary>
    private void DropEmptyLists() {
        if (sessions.SessionPlayer is { } player) {
            config.ForCharacter(player.ContentId).DropEmptyWindowLists();
        }
    }

    private void Remove(ChannelWindow window) {
        window.Detaching = true;
        system.RemoveWindow(window);
        this._open.Remove(window);
        sessions.Unread.Viewing(window.Viewer, null);
    }

    private void CloseAll() {
        foreach (var window in this._open.ToList()) {
            this.Remove(window);
        }
    }

    /// <summary>The plugin is unloading: the windows go without being forgotten, to open again when it next starts.</summary>
    public void Dispose() => this.CloseAll();
}
