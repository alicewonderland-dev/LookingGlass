using Dalamud.Game.Command;
using LookingGlass.Core.Client;

namespace LookingGlass.Plugin;

/// <summary>
/// /lgc1 to /lgc50 and /lgc &lt;nickname&gt; send to a channel, or with no message, talk in it from now on (see
/// <see cref="StickyMode"/>); /lookingglass (or /lg) and /lgdebug open the windows. Handlers run on the framework thread;
/// they read only the session manager's immutable copies of the slots and nicknames, and send in the background.
/// </summary>
public sealed class Commands : IDisposable {
    private const string MainCommand = "/lookingglass";
    private const string ShortMainCommand = "/lg";
    private const string DebugCommand = "/lgdebug";

    private readonly SessionManager _sessions;
    private readonly ChatOutput _chat;
    private readonly ChannelSender _sender;
    private readonly StickyMode _sticky;
    private readonly Action _toggleMain;
    private readonly Action _toggleDebug;

    public Commands(SessionManager sessions, ChatOutput chat, ChannelSender sender, StickyMode sticky, Action toggleMain, Action toggleDebug) {
        this._sessions = sessions;
        this._chat = chat;
        this._sender = sender;
        this._sticky = sticky;
        this._toggleMain = toggleMain;
        this._toggleDebug = toggleDebug;

        // Fifty numbered commands would swamp Dalamud's command list, so only /lgc is listed, and explains them.
        for (var slot = 1; slot <= Configuration.SlotCount; slot++) {
            Services.Commands.AddHandler(CommandSlots.Prefix + slot, new CommandInfo(this.OnSlotCommand) {
                HelpMessage = $"Send a message to the channel on {CommandSlots.Prefix}{slot}, or with no message, talk in it.",
                ShowInHelp = false,
            });
        }

        Services.Commands.AddHandler(CommandSlots.Prefix, new CommandInfo(this.OnNicknameCommand) {
            HelpMessage = $"Send a message to a channel: {CommandSlots.Prefix}<N> <message> by its number ({CommandSlots.Prefix}1 to {CommandSlots.Prefix}{Configuration.SlotCount}), " +
                          $"or {CommandSlots.Prefix} <nickname> <message> by its nickname. Leave out the message to talk in that channel until you " +
                          $"switch back (for example with /s). Set both in {ShortMainCommand}.",
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

        var typed = this.Typed(command, arguments);
        this.Run(ChannelCommand.ForSlot(this._sessions.Slots, slot, typed.Text), typed);
    }

    private void OnNicknameCommand(string command, string arguments) {
        var typed = this.Typed(command, arguments);
        this.Run(ChannelCommand.ForNickname(this._sessions.Nicknames, typed.Text), typed);
    }

    /// <summary>
    /// The command's arguments with their links: as read at the gate when the game is running the line (its links' bytes
    /// and placeholders, see <see cref="StickyMode.TypedCommandLine"/>), or else Dalamud's text for them (another plugin
    /// ran the command), whose placeholders are resolved when it is sent.
    /// </summary>
    private TypedLine Typed(string command, string arguments) {
        try {
            if (this._sticky.TypedCommandLine(command) is { } line) {
                return line.Arguments();
            }
        } catch (Exception ex) {
            Services.Log.Error(ex, "Couldn't read a command's links; it is sent as text");
        }

        return TypedLine.FromArguments(arguments);
    }

    /// <summary>The one path for both kinds of channel command.</summary>
    /// <param name="typed">The arguments' links: a message's text is a part of the arguments, with the same markers.</param>
    private void Run(ChannelCommand command, TypedLine typed) {
        switch (command) {
            case ChannelCommand.Usage usage:
                this._chat.Notice(NoticeTone.Info, usage.Text);
                return;
            case ChannelCommand.TalkIn talk:
                // Checks the connection and the membership itself, and says why not.
                this._sticky.Enter(talk.ChannelId);
                return;
        }

        var session = this._sessions.Session;
        if (session == null || session.Snapshot.State != ConnectionState.Ready) {
            this._chat.Notice(NoticeTone.Info, "Not connected to LookingGlass. Open /lookingglass to check.");
            return;
        }

        switch (command) {
            case ChannelCommand.NotFound notFound:
                this._chat.Notice(NoticeTone.Info, notFound.Text);
                break;
            case ChannelCommand.Send send:
                this._sender.Send(send.ChannelId, typed.WithText(send.Text));
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
