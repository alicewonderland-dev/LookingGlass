using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using LookingGlass.Plugin.Ui;

namespace LookingGlass.Plugin;

public sealed class Plugin : IDalamudPlugin {
    private readonly Configuration _config;
    private readonly PlayerTracker _player;
    private readonly SessionManager _sessions;
    private readonly StickyMode _sticky;
    private readonly Commands _commands;
    private readonly WindowSystem _windows = new("LookingGlass");
    private readonly MainWindow _mainWindow;
    private readonly SettingsWindow _settingsWindow;
    private readonly DebugWindow _debugWindow;
    private readonly ChannelWindows _channelWindows;
    private readonly UiFonts _fonts;
    private readonly GameContextMenuInvites _gameMenuInvites;
    private readonly ChatTwoContextMenuInvites _chatTwoMenuInvites;

    public Plugin(IDalamudPluginInterface pluginInterface) {
        pluginInterface.Create<Services>();

        this._config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        // Before any session, and before the address can be changed: see ServerSecretFiles.
        ProtectedSecretStore.MigrateOldFiles(this._config.ServerUrl);
        var chat = new ChatOutput(this._config);
        this._player = new PlayerTracker();
        this._sessions = new SessionManager(this._config, this._player, chat);
        var sender = new ChannelSender(this._sessions, chat);
        var local = new LocalSender(this._sessions, chat);
        this._sticky = new StickyMode(this._config, this._player, this._sessions, chat, sender);

        // One action runner for both windows, so the main window's status line shows what Settings started too.
        var actions = new UiActions(() => this._sessions.Snapshot.PendingChallenge?.Code, () => this._config.AdvancedMode);
        this._fonts = new UiFonts(pluginInterface.UiBuilder);
        this._channelWindows = new ChannelWindows(this._windows, this._config, this._sessions, sender, chat);
        this._settingsWindow = new SettingsWindow(this._config, this._sessions, actions);
        this._mainWindow = new MainWindow(this._config, this._sessions, actions, this._fonts, this._channelWindows, this._settingsWindow.Toggle, () => {
            this._settingsWindow.IsOpen = true;
            this._settingsWindow.BringToFront();
        });
        this._debugWindow = new DebugWindow(this._sessions, chat);
        this._windows.AddWindow(this._mainWindow);
        this._windows.AddWindow(this._settingsWindow);
        this._windows.AddWindow(this._debugWindow);

        this._commands = new Commands(this._sessions, chat, sender, local, this._sticky, this._mainWindow.Toggle, this._debugWindow.Toggle);

        // "Invite to LookingGlass" when right-clicking a player, in the game's menus and in ChatTwo's.
        var inviter = new ContextInviter(this._config, this._sessions, chat);
        this._gameMenuInvites = new GameContextMenuInvites(inviter);
        this._chatTwoMenuInvites = new ChatTwoContextMenuInvites(inviter);

        pluginInterface.UiBuilder.Draw += this.Draw;
        pluginInterface.UiBuilder.OpenMainUi += this._mainWindow.Toggle;
        pluginInterface.UiBuilder.OpenConfigUi += this._settingsWindow.Toggle;
    }

    private void Draw() {
        // Channel windows open and close between frames, never while the window system draws.
        this._channelWindows.Update();
        this._windows.Draw();
    }

    public void Dispose() {
        // First: what is typed must stop going anywhere but game chat before anything else goes away, and the hooks come off.
        this._sticky.Dispose();

        var ui = Services.PluginInterface.UiBuilder;
        ui.Draw -= this.Draw;
        ui.OpenMainUi -= this._mainWindow.Toggle;
        ui.OpenConfigUi -= this._settingsWindow.Toggle;

        this._chatTwoMenuInvites.Dispose();
        this._gameMenuInvites.Dispose();
        this._commands.Dispose();
        // Taken away without being forgotten: they open again when the plugin next starts.
        this._channelWindows.Dispose();
        this._windows.RemoveAllWindows();
        this._fonts.Dispose();
        this._sessions.Dispose();
        this._player.Dispose();
    }
}
