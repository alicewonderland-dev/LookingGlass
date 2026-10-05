using Dalamud.Game.Command;
using LookingGlass.Core.Client;

namespace LookingGlass.Plugin;

/// <summary>
/// /lgc1 to /lgc50 and /lgc &lt;nickname&gt; send to a channel; /lookingglass (or /lg) and
/// /lgdebug open the windows. Handlers run on the framework thread; they read only the
/// session manager's immutable copies of the slots and nicknames, and send in the background.
/// </summary>
public sealed class Commands : IDisposable {
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

        // Fifty numbered commands would swamp Dalamud's command list, so only /lgc is listed, and explains them.
        for (var slot = 1; slot <= Configuration.SlotCount; slot++) {
            Services.Commands.AddHandler(CommandSlots.Prefix + slot, new CommandInfo(this.OnSlotCommand) {
                HelpMessage = $"Send a message to the channel on {CommandSlots.Prefix}{slot}.",
                ShowInHelp = false,
            });
        }

        Services.Commands.AddHandler(CommandSlots.Prefix, new CommandInfo(this.OnNicknameCommand) {
            HelpMessage = $"Send a message to a channel: {CommandSlots.Prefix}<N> <message> by its number ({CommandSlots.Prefix}1 to {CommandSlots.Prefix}{Configuration.SlotCount}), " +
                          $"or {CommandSlots.Prefix} <nickname> <message> by its nickname. Set both in {ShortMainCommand}.",
        });
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
        if (CommandSlots.SlotOfCommand(command) is not { } slot) {
            return;
        }

        this.Run(ChannelCommand.ForSlot(this._sessions.Slots, slot, arguments));
    }

    private void OnNicknameCommand(string command, string arguments) {
        this.Run(ChannelCommand.ForNickname(this._sessions.Nicknames, arguments));
    }

    /// <summary>The one send path for both kinds of channel command.</summary>
    private void Run(ChannelCommand command) {
        if (command is ChannelCommand.Usage usage) {
            this._chat.Notice(NoticeLevel.Info, usage.Text);
            return;
        }

        var session = this._sessions.Session;
        if (session == null || session.Snapshot.State != ConnectionState.Ready) {
            this._chat.Notice(NoticeLevel.Warning, "Not connected to LookingGlass. Open /lookingglass to check.");
            return;
        }

        switch (command) {
            case ChannelCommand.NotFound notFound:
                this._chat.Notice(NoticeLevel.Warning, notFound.Text);
                break;
            case ChannelCommand.Send send:
                // Talking in a channel means you've caught up with it.
                this._sessions.Unread.MarkRead(send.ChannelId);
                _ = Task.Run(async () => {
                    try {
                        await session.SendTextAsync(send.ChannelId, send.Text);
                    } catch (Exception ex) {
                        this._chat.Notice(NoticeLevel.Error, PlainMessages.Of($"Not sent: {ex.Message}"));
                    }
                });
                break;
        }
    }

    public void Dispose() {
        for (var slot = 1; slot <= Configuration.SlotCount; slot++) {
            Services.Commands.RemoveHandler(CommandSlots.Prefix + slot);
        }

        Services.Commands.RemoveHandler(CommandSlots.Prefix);
        Services.Commands.RemoveHandler(MainCommand);
        Services.Commands.RemoveHandler(ShortMainCommand);
        Services.Commands.RemoveHandler(DebugCommand);
    }
}
