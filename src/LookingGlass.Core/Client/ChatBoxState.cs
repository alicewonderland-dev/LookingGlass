namespace LookingGlass.Core.Client;

/// <summary>
/// The game shell's channel state, read once a frame while talking in a channel (game types stay in the plugin).
/// <para>
/// Beside its channel (<c>RaptureShellModule.ChatType</c>), the shell keeps <c>TempChatType</c> and
/// <c>TempChatCommand</c> (FFXIVClientStructs). They hold the channel to go back to after a one-off switch: for a tell
/// from a menu ("Send Tell", <c>SetContextTellTargetInForay</c>) or a channel for one line, the game saves the channel it
/// is on there (its type, command and tell target), then switches with <c>ChangeChatChannel</c> without making the new one
/// its channel (<c>setChatType</c> false), and sets the type to -2 if that fails. (Found by a reviewer in the game's code,
/// 2026.09.15 build.) Sticky mode treats a newly saved channel as the player switching, and ends, saying so; a
/// context-menu tell ending it is accepted. The chat log agent's own channel and label (<c>AgentChatLog.CurrentChannel</c>,
/// <c>ChannelLabel</c>) are read too, for the diagnostic log only.
/// </para>
/// </summary>
/// <param name="ChatType">The shell's channel (as <see cref="GameChannel"/>).</param>
/// <param name="TempChatType">The saved channel's type (the one to go back to after a one-off switch).</param>
/// <param name="TempCommand">The saved channel's command, as the game holds it (a command such as "/s", or empty).</param>
/// <param name="AgentChannel">The chat log agent's current channel, or -1 if unreadable.</param>
/// <param name="LabelHash">A hash of the chat log agent's channel label (never the label: it can hold a linkshell's name).</param>
public readonly record struct ChatBoxState(int ChatType, int TempChatType, string TempCommand, int AgentChannel, int LabelHash) {
    /// <summary>
    /// A one-off switch was made since <paramref name="baseline"/>: a channel to go back to is saved now, and it or its
    /// type changed. Sticky mode ends on this. The saved channel merely cleared (back on the channel, or the switch failed)
    /// isn't a new switch. Unknown on either side: no.
    /// </summary>
    public static bool Switched(ChatBoxState? baseline, ChatBoxState? now) =>
        baseline is { } then && now is { } current && current.TempCommand.Trim().Length > 0 && Differs(then, current);

    /// <summary>
    /// Anything about the saved channel differs from <paramref name="baseline"/>, even without a command: the game's chat
    /// input may be naming another channel, so the channel's tag isn't drawn over it (the game's own name is shown, while
    /// what is typed still goes to the LookingGlass channel, the safe way round). Unknown on either side: no.
    /// </summary>
    public static bool Unsettled(ChatBoxState? baseline, ChatBoxState? now) =>
        baseline is { } then && now is { } current && Differs(then, current);

    private static bool Differs(ChatBoxState then, ChatBoxState now) =>
        then.TempChatType != now.TempChatType || !string.Equals(then.TempCommand.Trim(), now.TempCommand.Trim(), StringComparison.Ordinal);

    /// <summary>For the diagnostic log: numbers, and the saved command only if it is a known one.</summary>
    public override string ToString() =>
        $"chat type {this.ChatType}, saved type {this.TempChatType}, saved command {CommandToken(this.TempCommand)}, " +
        $"agent channel {this.AgentChannel}, label #{this.LabelHash & 0xFFFF:X4}";

    private static string CommandToken(string command) {
        command = command.Trim();
        return command.Length == 0 ? "(none)" : StickyDiagnostics.Token(ChatBoxLine.Plain(command));
    }
}
