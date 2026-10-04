using Dalamud.Game.Command;
using WonderlandChat.Core.Client;

namespace WonderlandChat.Plugin;

/// <summary>/wcl1 to /wcl8 send to a channel; /wonderlandchat and /wcdebug open the windows.</summary>
public sealed class Commands : IDisposable {
    private const string SlotPrefix = "/wcl";
    private const string MainCommand = "/wonderlandchat";
    private const string ShortMainCommand = "/wchat";
    private const string DebugCommand = "/wcdebug";

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
                HelpMessage = $"Send a message to your WonderlandChat channel in slot {slot}.",
                ShowInHelp = slot == 1,
            });
        }

        Services.Commands.AddHandler(MainCommand, new CommandInfo((_, _) => this._toggleMain()) {
            HelpMessage = "Open WonderlandChat (register, create and manage channels).",
        });
        Services.Commands.AddHandler(ShortMainCommand, new CommandInfo((_, _) => this._toggleMain()) {
            HelpMessage = "Short for /wonderlandchat.",
        });
        Services.Commands.AddHandler(DebugCommand, new CommandInfo((_, _) => this._toggleDebug()) {
            HelpMessage = "Open the WonderlandChat debug window.",
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
            this._chat.Notice(NoticeLevel.Warning, "Not connected to WonderlandChat. Open /wonderlandchat to check.");
            return;
        }

        var channelId = this._sessions.ChannelInSlot(slot);
        if (channelId == null) {
            this._chat.Notice(NoticeLevel.Warning, $"No channel in slot {slot}. Assign one in /wonderlandchat.");
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
