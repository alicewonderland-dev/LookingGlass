using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using WonderlandChat.Plugin.Ui;

namespace WonderlandChat.Plugin;

public sealed class Plugin : IDalamudPlugin {
    private readonly Configuration _config;
    private readonly PlayerTracker _player;
    private readonly SessionManager _sessions;
    private readonly Commands _commands;
    private readonly WindowSystem _windows = new("WonderlandChat");
    private readonly MainWindow _mainWindow;
    private readonly DebugWindow _debugWindow;

    public Plugin(IDalamudPluginInterface pluginInterface) {
        pluginInterface.Create<Services>();

        this._config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        var chat = new ChatOutput(this._config);
        this._player = new PlayerTracker();
        this._sessions = new SessionManager(this._config, this._player, chat);

        this._mainWindow = new MainWindow(this._config, this._sessions);
        this._debugWindow = new DebugWindow(this._sessions);
        this._windows.AddWindow(this._mainWindow);
        this._windows.AddWindow(this._debugWindow);

        this._commands = new Commands(this._sessions, chat, this._mainWindow.Toggle, this._debugWindow.Toggle);

        pluginInterface.UiBuilder.Draw += this._windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi += this._mainWindow.Toggle;
        pluginInterface.UiBuilder.OpenConfigUi += this._mainWindow.Toggle;
    }

    public void Dispose() {
        var ui = Services.PluginInterface.UiBuilder;
        ui.Draw -= this._windows.Draw;
        ui.OpenMainUi -= this._mainWindow.Toggle;
        ui.OpenConfigUi -= this._mainWindow.Toggle;

        this._commands.Dispose();
        this._windows.RemoveAllWindows();
        this._sessions.Dispose();
        this._player.Dispose();
    }
}
