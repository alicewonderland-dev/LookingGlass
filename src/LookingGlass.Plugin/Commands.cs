using Dalamud.Game.Command;
using LookingGlass.Core.Client;

namespace LookingGlass.Plugin;

/// <summary>/lgc1 to /lgc8 send to a channel; /lookingglass and /lgdebug open the windows.</summary>
public sealed class Commands : IDisposable {
    private const string SlotPrefix = "/lgc";
    private const string MainCommand = "/lookingglass";
    private const string ShortMainCommand = "/lg";
    private const string DebugCommand = "/lgdebug";

    private readonly SessionManager _sessions;
    private readonly ChatOutput _chat;
    private readonly Action _toggleMain;
    private readonly Action _toggleDebug;

    public Commands(SessionManager sessions, ChatOutput chat, Action toggleMain, Action toggleDebug) {
        this._sessions = sessions;
        this._chat = chat;
        this._toggleMain = toggleMain;
        this._toggleDebug = toggleDebug;

        for (var slot = 1; slot <= Configuration.SlotCount; slot++) {
            Services.Commands.AddHandler(SlotPrefix + slot, new CommandInfo(this.OnSlotCommand) {
                HelpMessage = $"Send a message to your LookingGlass channel in slot {slot}.",
                ShowInHelp = slot == 1,
            });
        }

        Services.Commands.AddHandler(MainCommand, new CommandInfo((_, _) => this._toggleMain()) {
            HelpMessage = "Open LookingGlass (register, create and manage channels).",
        });
        Services.Commands.AddHandler(ShortMainCommand, new CommandInfo((_, _) => this._toggleMain()) {
            HelpMessage = "Short for /lookingglass.",
        });
        Services.Commands.AddHandler(DebugCommand, new CommandInfo((_, _) => this._toggleDebug()) {
            HelpMessage = "Open the LookingGlass debug window.",
        });
    }

    private void OnSlotCommand(string command, string arguments) {
        if (!int.TryParse(command.AsSpan(SlotPrefix.Length), out var slot)) {
            return;
        }

        var text = arguments.Trim();
        if (text.Length == 0) {
            this._chat.Notice(NoticeLevel.Info, $"Usage: {SlotPrefix}{slot} <message>");
            return;
        }

        var session = this._sessions.Session;
        if (session == null || session.Snapshot.State != ConnectionState.Ready) {
            this._chat.Notice(NoticeLevel.Warning, "Not connected to LookingGlass. Open /lookingglass to check.");
            return;
        }

        var channelId = this._sessions.ChannelInSlot(slot);
        if (channelId == null) {
            this._chat.Notice(NoticeLevel.Warning, $"No channel in slot {slot}. Assign one in /lookingglass.");
            return;
        }

        _ = Task.Run(async () => {
            try {
                await session.SendTextAsync(channelId, text);
            } catch (Exception ex) {
                this._chat.Notice(NoticeLevel.Error, $"Not sent: {ex.Message}");
            }
        });
    }

    public void Dispose() {
        for (var slot = 1; slot <= Configuration.SlotCount; slot++) {
            Services.Commands.RemoveHandler(SlotPrefix + slot);
        }

        Services.Commands.RemoveHandler(MainCommand);
        Services.Commands.RemoveHandler(ShortMainCommand);
        Services.Commands.RemoveHandler(DebugCommand);
    }
}
