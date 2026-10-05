using System.Text.RegularExpressions;

namespace LookingGlass.Core.Client;

/// <summary>
/// The diagnostic log lines sticky mode writes (to Dalamud's log, at Information level, while talking in a channel), so
/// an in-game test can be read back afterwards from dalamud.log. Built only from what is safe to keep in a log file: the
/// channel's tag, a known command's name, sizes, yes/no flags, chat type numbers and fixed words. Never what was typed,
/// a link's contents, or a command nobody knows (it could be a message typed after a "/" by mistake).
/// </summary>
public static partial class StickyDiagnostics {
    /// <summary>What every line starts with, after Dalamud's own "[LookingGlass]".</summary>
    public const string Prefix = "[sticky]";

    private static readonly IReadOnlyCollection<string> OtherKnownCommands = new[] {
            "/t", "/tell", "/r", "/reply", "/e", "/echo", "/em", "/emote", "/lg", "/lookingglass", "/lgdebug",
        }
        .Concat(Enumerable.Range(1, 8).Select(i => $"/ecl{i}"))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^/lgc\d{0,2}$", RegexOptions.IgnoreCase)]
    private static partial Regex LgcCommand();

    /// <summary>
    /// The line's leading command, if it is a known one ("/s", "/cwl1", "/lgc3"); otherwise what kind of line it is:
    /// "(other command)", "(text)", "(link placeholder)" (only &lt;item&gt; and the like), "(payload)" (only links or
    /// auto-translate phrases), or "(blank)".
    /// </summary>
    public static string Token(ChatBoxLine line, IReadOnlyCollection<string>? switches = null) {
        if (line.Raw.Length > 0 && line.Raw[0] == (byte) '/') {
            var (command, _) = line.Command();
            var known = (switches ?? ChatChannelPrefixes.Switches).Contains(command, StringComparer.OrdinalIgnoreCase)
                        || OtherKnownCommands.Contains(command) || LgcCommand().IsMatch(command);
            return known ? command.ToLowerInvariant() : "(other command)";
        }

        var text = line.Text.Trim();
        return ChatBoxLine.HasText(text) ? "(text)"
            : HasPayload(line) ? "(payload)"
            : ChatBoxLine.HasContent(line.Raw) ? "(link placeholder)"
            : "(blank)";
    }

    /// <summary>The line holds SeString payloads (links, auto-translate phrases): a byte 2 anywhere.</summary>
    public static bool HasPayload(ChatBoxLine line) => line.Raw.AsSpan().Contains((byte) 0x02);

    /// <summary>The decision, in fixed words.</summary>
    public static string Decision(StickyRoute route) => route switch {
        StickyRoute.ToChannel => "to LookingGlass",
        StickyRoute.Dropped => "kept from game",
        StickyRoute.LeaveThenGame => "stop talking in the channel, then to game",
        _ => "to game",
    };

    /// <summary>One line seen by the gate while talking in a channel, and what was decided.</summary>
    /// <param name="source">Which way it came, if known.</param>
    public static string Line(string tag, bool chatTwo, ChatBoxLine line, StickyRoute route, string reason, IReadOnlyCollection<string>? switches = null,
        LineSource? source = null) =>
        $"{Prefix} line{SourceOf(source)}: talking in {tag}, ChatTwo {YesNo(chatTwo)}, {Token(line, switches)}, {line.Raw.Length} bytes, " +
        $"payload {YesNo(HasPayload(line))} -> {Decision(route)} ({reason})";

    /// <summary>A call to the game's channel switch, with the chat type before and after it.</summary>
    /// <param name="tag">The channel being talked in, or null.</param>
    /// <param name="end">Why it ended talking in the channel, if it did.</param>
    public static string ChannelSwitch(string? tag, GameChannel? before, GameChannel? after, bool lineInFlight, StickyEnd? end) =>
        $"{Prefix} channel switch: {(tag == null ? "not talking in a channel" : $"talking in {tag}")}, chat type {TypeOf(before)} -> {TypeOf(after)}, " +
        $"typed line in flight {YesNo(lineInFlight)} -> {(end is { } why ? $"ended ({why})" : tag == null ? "nothing to do" : "goes on")}";

    /// <summary>Talking in a channel started.</summary>
    public static string Started(string tag, bool chatTwo, GameChannel? channel, bool moved) =>
        $"{Prefix} start: talking in {tag}{(moved ? " (was already talking in a channel)" : "")}, ChatTwo {YesNo(chatTwo)}, chat type {TypeOf(channel)}";

    /// <summary>Talking in a channel started, with the chat box state it is measured against.</summary>
    public static string Started(string tag, bool chatTwo, GameChannel? channel, bool moved, ChatBoxState? chatBox) =>
        $"{Started(tag, chatTwo, channel, moved)}; {StateOf(chatBox)}";

    /// <summary>Talking in a channel was refused: <paramref name="why"/> is one of <see cref="StickyMessages"/>' fixed refusals.</summary>
    public static string Refused(string why, bool chatTwo, GameChannel? channel) =>
        $"{Prefix} start refused: ChatTwo {YesNo(chatTwo)}, chat type {TypeOf(channel)}: {why}";

    /// <summary>Talking in a channel ended.</summary>
    public static string Ended(string tag, StickyEnd why, ChatBoxState? chatBox) =>
        $"{Prefix} end: stopped talking in {tag} ({why}), {StateOf(chatBox)}";

    /// <summary>
    /// The chat box's own state changed while talking in a channel (<paramref name="what"/> says where it was seen: once a
    /// frame, before a draw, the game renaming its channel, after a line).
    /// </summary>
    public static string ChatBoxChanged(string tag, string what, ChatBoxState? before, ChatBoxState? after) =>
        $"{Prefix} chat box ({what}): talking in {tag}, {StateOf(before)} -> {StateOf(after)}" +
        (ChatBoxState.Switched(before, after) ? ", one-off switch" : "");

    /// <summary>The game put a link placeholder in its chat input (only its number: which kind of link).</summary>
    public static string LinkInserted(string tag, uint param, ChatBoxState? chatBox) =>
        $"{Prefix} link put in the chat input: talking in {tag}, kind {param}, {StateOf(chatBox)}";

    private static string StateOf(ChatBoxState? state) => state is { } known ? known.ToString() : "chat box unreadable";

    private static string SourceOf(LineSource? source) => source switch {
        LineSource.Game => " from the game",
        LineSource.Plugin => " from a plugin (ProcessChatBoxEntry)",
        _ => "",
    };

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static string TypeOf(GameChannel? channel) => channel is { } known ? known.ChatType.ToString() : "unknown";
}

/// <summary>Which way a line reached the gate (<c>ShellCommandModule.ExecuteCommandInner</c>), for the diagnostic log.</summary>
public enum LineSource {
    /// <summary>
    /// Straight from the game: its own chat box (which never goes through <c>UIModule.ProcessChatBoxEntry</c>), a macro
    /// line, a gear set, and the like.
    /// </summary>
    Game,

    /// <summary>Through <c>UIModule.ProcessChatBoxEntry</c>: a plugin's chat box, such as ChatTwo.</summary>
    Plugin,
}
