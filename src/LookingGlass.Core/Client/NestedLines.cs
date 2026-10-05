namespace LookingGlass.Core.Client;

/// <summary>
/// Lines the game runs inside another line, through the same function the gate is on
/// (<c>ShellCommandModule.ExecuteCommandInner</c>).
/// <para>
/// The game's reply command (/r, /reply; its handler <c>ShellCommandChatReply</c>) sets the tell target, then runs
/// that function again with only the text after "/r". Judged, that text would be plain text, sent to the LookingGlass
/// channel: a private reply to the whole group, and no tell. So a line run directly inside a reply the gate let through
/// goes to the game unjudged. (Found by a reviewer in the 2026.09.15 game build; the game's other chat commands send
/// directly, without running a line inside.)
/// </para>
/// <para>
/// Every other line run inside another is judged as usual: a plugin command that submits plain text while it runs, or
/// the game's command that runs a stored line (<c>ShellCommandCommand</c>; what it stores isn't known).
/// </para>
/// </summary>
public static class NestedLines {
    /// <summary>The reply commands, in English. The plugin adds the game's own names in the client's language.</summary>
    public static readonly IReadOnlyCollection<string> Replies = new[] { "/r", "/reply" }.ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary><see cref="Replies"/> and <paramref name="more"/> (the game's own names for it), ignoring case.</summary>
    public static IReadOnlyCollection<string> RepliesWith(IEnumerable<string> more) =>
        Replies.Concat(more.Select(command => command.Trim()).Where(command => command.Length > 1 && command.StartsWith('/')))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a line the game is about to run goes through unjudged: it runs directly inside a line the gate let through
    /// whose command is a reply.
    /// </summary>
    /// <param name="running">
    /// The commands of the lines the gate let through that are still running, outermost first (null for a line that
    /// isn't a command). Empty for a line on its own.
    /// </param>
    /// <param name="replies">The reply commands (<see cref="Replies"/> or <see cref="RepliesWith"/>).</param>
    public static bool PassThrough(IReadOnlyList<string?> running, IReadOnlyCollection<string> replies) =>
        running.Count > 0 && running[^1] is { } enclosing && replies.Contains(enclosing, StringComparer.OrdinalIgnoreCase);

    /// <summary>The line's command, if it starts with "/" (see <see cref="ChatBoxLine.Command"/>), or null.</summary>
    public static string? CommandOf(ChatBoxLine line) =>
        line.Raw.Length > 0 && line.Raw[0] == (byte) '/' ? line.Command().Command : null;
}
