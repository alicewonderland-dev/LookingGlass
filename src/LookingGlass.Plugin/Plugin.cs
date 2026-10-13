using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using LookingGlass.Core.Client;
using LookingGlass.Plugin.SelfTest;
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
    private readonly SelfTestService _selfTest;
    private readonly SelfTestWindow _selfTestWindow;

    public Plugin(IDalamudPluginInterface pluginInterface) {
        pluginInterface.Create<Services>();
        // The self-test's log tap: passes everything on unchanged, and counts only while a run is on.
        var logTap = new LogTap(Services.Log);
        Services.Log = logTap;

        this._config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        // Before any session, and before the address can be changed: see ServerSecretFiles.
        ProtectedSecretStore.MigrateOldFiles(this._config.ServerUrl);
        // Before any session too: a self-test that was interrupted puts back the settings it changed, at once.
        var selfTestStart = SelfTestService.AtStart(this._config, pluginInterface.ConfigDirectory.FullName);
        var chat = new ChatOutput(this._config);
        this._player = new PlayerTracker();
        this._sessions = new SessionManager(this._config, this._player, chat);
        var sender = new ChannelSender(this._sessions, chat);
        // Asked once accepted (the sticky mode made below by then) whether talking in local chat, so what it says then never
        // tells the player to type /lgl again.
        var localPrivacy = new LocalChatPrivacyWindow(this._config, chat, () => this._sticky?.TalkingInLocal ?? false);
        var local = new LocalSender(this._sessions, chat, () => this._config.LocalChatPrivacyAccepted, localPrivacy.Ask);
        this._sticky = new StickyMode(this._config, this._player, this._sessions, chat, sender, local);

        // One action runner for both windows, so the main window's status line shows what Settings started too.
        var actions = new UiActions(() => this._sessions.Snapshot.PendingChallenge?.Code, () => this._config.AdvancedMode);
        this._fonts = new UiFonts(pluginInterface.UiBuilder);
        this._channelWindows = new ChannelWindows(this._windows, this._config, this._sessions, sender);
        // "Ban from server", for one of the server's operators: from the right-click menus and the Operator section of Settings.
        var operatorBan = new OperatorBanWindow(this._config, this._sessions, chat);
        this._settingsWindow = new SettingsWindow(this._config, this._sessions, actions, () => localPrivacy.Ask(LocalPrivacyAsked.FromSettings), operatorBan);
        this._mainWindow = new MainWindow(this._config, this._sessions, actions, this._fonts, this._channelWindows, this._settingsWindow.Toggle, () => {
            this._settingsWindow.IsOpen = true;
            this._settingsWindow.BringToFront();
        });
        this._debugWindow = new DebugWindow(this._sessions, chat, () => this._selfTestWindow?.Open());
        this._windows.AddWindow(this._mainWindow);
        this._windows.AddWindow(this._settingsWindow);
        this._windows.AddWindow(this._debugWindow);
        this._windows.AddWindow(localPrivacy);
        this._windows.AddWindow(operatorBan);

        // "Invite to LookingGlass" when right-clicking a player, in the game's menus and in ChatTwo's; and, for an operator, "Ban from server...".
        var inviter = new ContextInviter(this._config, this._sessions, chat);

        // The self-test (/lgdebug test): nothing runs until the tester presses Start, but carrying on a run after a reload.
        this._selfTest = new SelfTestService(new SelfTestParts(this._config, this._player, this._sessions, this._sticky, chat, sender, local, this._channelWindows,
            localPrivacy, this._settingsWindow, this._debugWindow, inviter, logTap), selfTestStart);
        this._selfTestWindow = new SelfTestWindow(this._selfTest);
        this._selfTest.WantsWindow += this._selfTestWindow.Open;
        this._windows.AddWindow(this._selfTestWindow);

        this._commands = new Commands(this._sessions, chat, sender, local, this._sticky, this._mainWindow.Toggle, this._debugWindow.Toggle, this._selfTestWindow.Open);
        var banner = new ContextOperator(this._sessions, operatorBan);
        this._gameMenuInvites = new GameContextMenuInvites(inviter, banner);
        this._chatTwoMenuInvites = new ChatTwoContextMenuInvites(inviter, banner);

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
        // A self-test going stops without cleaning up: its file puts everything back (or carries it on) at the next start.
        this._selfTest.WantsWindow -= this._selfTestWindow.Open;
        this._selfTest.Dispose();

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
