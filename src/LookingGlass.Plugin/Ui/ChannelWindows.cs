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
public sealed class ChannelWindows(WindowSystem system, Configuration config, SessionManager sessions, ChannelSender sender) : IDisposable {
    private readonly List<ChannelWindow> _open = new();
    // Layouts the player opened this session (not reopened at login): their windows appear where the mouse is.
    private readonly HashSet<string> _opened = new();
    // Windows to flash, by their layout's ID: how many pulses, and the channel whose colour (see WantFlash).
    private readonly Dictionary<string, (int Pulses, string ChannelId)> _flashes = new();
    private ClientSession? _session;
    private SessionSnapshot? _synced;
    private bool _restored;
    // The window the player used last (see Used), by its layout's ID.
    private string? _lastUsed;
    // The window last opened for a line that arrived, which the next steps from; and the one caught-up channels share this session.
    private ChannelWindowLayout? _lastPlaced;
    private string? _catchUpWindow;

    /// <summary>The windows open now, in the order they were opened.</summary>
    public IReadOnlyList<ChannelWindow> Open => this._open;

    /// <summary>The counts on the windows' tabs not selected, shared by every window (see <see cref="TabUnread"/>).</summary>
    internal TabUnread TabCounts { get; } = new();

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
            this._lastPlaced = null;
            this._catchUpWindow = null;
            this._flashes.Clear();
            this.TabCounts.Clear();
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

        // Each channel a line arrived for that game chat didn't show (windows only, or the channel turned off there on its
        // own) gets a window, or a tab in one, if none shows it. A channel off game chat stays off when its last tab or
        // window closes: its next line opens one again (owner decision, 2026-10-09).
        if (sessions.WantWindows.Count > 0) {
            this.OpenWaiting(snapshot);
        }

        this.StartFlashes();
    }

    /// <summary>
    /// In combat, a cutscene or a loading screen: no window opens by itself then, as one appearing mid-fight would be
    /// in the way. The channels wait (their lines are kept meanwhile), and open once it's over.
    /// </summary>
    private static bool GameBusy() => Services.Condition.Any(ConditionFlag.InCombat, ConditionFlag.OccupiedInCutSceneEvent,
        ConditionFlag.WatchingCutscene, ConditionFlag.WatchingCutscene78, ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51);

    /// <summary>
    /// A window for each channel waiting that no window shows, once the game isn't busy (<see cref="GameBusy"/>), found as
    /// <see cref="Configuration.WindowOpening"/> says (see <see cref="WindowsOnly.Place"/>): a tab added to the window used
    /// last, not selected, or a new window; channels caught up at login share one new window. A new window doesn't take the
    /// focus from the game, and opens a step below and right of the one opened this way before it (else the one used last),
    /// on the screen (<see cref="WindowsOnly.NextPlace"/>). Either way the window flashes briefly (<see cref="WindowFlash"/>).
    /// A channel whose lines go to game chat now (windows only turned off, or its "Show in game chat" back on, while it
    /// waited) still gets its window: the line it waited with was never in game chat. The settings are saved once, after all
    /// of them.
    /// </summary>
    private void OpenWaiting(SessionSnapshot snapshot) {
        if (sessions.SessionPlayer is not { } player) {
            return;
        }

        var taken = sessions.WantWindows.Take(snapshot, GameBusy());
        if (taken.Count == 0) {
            return;
        }

        var settings = config.ForCharacter(player.ContentId);
        var windows = settings.WindowsOn(sessions.ServerUrl);
        var changed = false;
        foreach (var wanted in taken) {
            if (WindowsOnly.Place(windows, wanted.ChannelId, config.WindowOpening, this._lastUsed, wanted.CaughtUp, this._catchUpWindow) is not { } placed) {
                continue;
            }

            changed = true;
            if (wanted.CaughtUp) {
                this._catchUpWindow = placed.Window.Id;
            }

            if (placed.Created) {
                placed.Window.Place = this.NewPlace();
                this._lastPlaced = placed.Window;
            }

            this.WantFlash(placed.Window.Id, placed.Created ? WindowFlash.OpenedPulses : WindowFlash.TabAddedPulses, wanted.ChannelId);
        }

        if (changed) {
            config.Save();
        } else {
            settings.DropEmptyWindowLists();
        }
    }

    /// <summary>
    /// A window to flash, by its layout's ID: in the colour of the channel it opened for or got a tab for. Several in one
    /// frame (channels caught up at login sharing a window) flash once, as many times as the most asked for.
    /// </summary>
    private void WantFlash(string layoutId, int pulses, string channelId) {
        if (!this._flashes.TryGetValue(layoutId, out var wanted) || wanted.Pulses < pulses) {
            this._flashes[layoutId] = (pulses, channelId);
        }
    }

    /// <summary>
    /// Starts the flashes asked for, on the windows open now: a window opened this frame is drawn from the next, so its flash
    /// waits for it. One whose window was closed meanwhile is dropped.
    /// </summary>
    private void StartFlashes() {
        if (this._flashes.Count == 0) {
            return;
        }

        // Read again: a window opened just now may have made the list.
        var layouts = this.Layouts ?? [];
        foreach (var (layoutId, (pulses, channelId)) in this._flashes.ToList()) {
            if (this._open.FirstOrDefault(window => window.Layout.Id == layoutId) is { } window) {
                window.Flash(pulses, channelId);
                this._flashes.Remove(layoutId);
            } else if (layouts.All(layout => layout.Id != layoutId)) {
                this._flashes.Remove(layoutId);
            }
        }
    }

    /// <summary>Where a window opened by itself appears (see <see cref="WindowsOnly.NextPlace"/>): on the game's main viewport.</summary>
    private WindowPlace NewPlace() {
        var viewport = ImGui.GetMainViewport();
        var area = new WindowPlace(viewport.WorkPos.X, viewport.WorkPos.Y, viewport.WorkSize.X, viewport.WorkSize.Y);
        var size = ChannelWindow.DefaultSize * Widgets.Scale;
        var from = this._lastPlaced?.Place ?? this.LastUsed()?.Layout.Place;
        return WindowsOnly.NextPlace(from, area, 30 * Widgets.Scale, size.X, size.Y);
    }

    /// <summary>The window used last (see <see cref="Used"/>), or if it is gone or none was used yet, the one opened last.</summary>
    private ChannelWindow? LastUsed() => this._open.FirstOrDefault(window => window.Layout.Id == this._lastUsed) ?? this._open.LastOrDefault();

    /// <summary>
    /// A window the player is using (it has the focus): the one a channel is added to when it opens by itself. Kept for the
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
