namespace LookingGlass.Core.Client;

/// <summary>
/// The game chat box's own channel state, read once a frame while talking in a channel (game types stay in the plugin).
/// <para>
/// The game's shell keeps, beside its channel (<c>RaptureShellModule.ChatType</c>), a one-line channel
/// (<c>RaptureShellModule.TempChatType</c> and <c>TempChatCommand</c>, from FFXIVClientStructs). That is what the game's
/// chat box uses when a channel command is typed at the start of the input followed by a space ("/s "): the input
/// switches to that channel for the line, and the line is sent there. This is inferred from the fields' names and from
/// the owner's test (the game's code isn't read), so sticky mode treats any change of it as the player switching the
/// chat box, and ends, saying so. The chat log agent's own channel and label (<c>AgentChatLog.CurrentChannel</c>,
/// <c>ChannelLabel</c>) are read too, for the diagnostic log only.
/// </para>
/// </summary>
/// <param name="ChatType">The shell's channel (as <see cref="GameChannel"/>).</param>
/// <param name="TempChatType">The shell's one-line channel.</param>
/// <param name="TempCommand">The shell's one-line channel command, as the game holds it (a command such as "/s", or empty).</param>
/// <param name="AgentChannel">The chat log agent's current channel, or -1 if unreadable.</param>
/// <param name="LabelHash">A hash of the chat log agent's channel label (never the label: it can hold a linkshell's name).</param>
public readonly record struct ChatBoxState(int ChatType, int TempChatType, string TempCommand, int AgentChannel, int LabelHash) {
    /// <summary>
    /// The chat box switched to a one-line channel since <paramref name="baseline"/>: it holds a one-line command now, and
    /// that or its one-line channel changed. Sticky mode ends on this. A one-line state merely cleared (the line sent, the
    /// input closed) isn't a switch. Unknown on either side: no.
    /// </summary>
    public static bool Switched(ChatBoxState? baseline, ChatBoxState? now) =>
        baseline is { } then && now is { } current && current.TempCommand.Trim().Length > 0 && Differs(then, current);

    /// <summary>
    /// Anything about the one-line channel differs from <paramref name="baseline"/>, even without a command: the game's
    /// chat input may be naming another channel, so the channel's tag isn't drawn over it (the game's own name is shown,
    /// while what is typed still goes to the LookingGlass channel, the safe way round). Unknown on either side: no.
    /// </summary>
    public static bool Unsettled(ChatBoxState? baseline, ChatBoxState? now) =>
        baseline is { } then && now is { } current && Differs(then, current);

    private static bool Differs(ChatBoxState then, ChatBoxState now) =>
        then.TempChatType != now.TempChatType || !string.Equals(then.TempCommand.Trim(), now.TempCommand.Trim(), StringComparison.Ordinal);

    /// <summary>For the diagnostic log: numbers, and the one-line command only if it is a known one.</summary>
    public override string ToString() =>
        $"chat type {this.ChatType}, one-line type {this.TempChatType}, one-line command {CommandToken(this.TempCommand)}, " +
        $"agent channel {this.AgentChannel}, label #{this.LabelHash & 0xFFFF:X4}";

    private static string CommandToken(string command) {
        command = command.Trim();
        return command.Length == 0 ? "(none)" : StickyDiagnostics.Token(ChatBoxLine.Plain(command));
    }
}
