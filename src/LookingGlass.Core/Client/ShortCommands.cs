namespace LookingGlass.Core.Client;

/// <summary>
/// What ChatTwo's main input said about a line a plugin submitted through <c>UIModule.ProcessChatBoxEntry</c>, read through
/// ChatTwo's typing IPC (<c>ChatTwo.GetChatInputState</c>) as the line arrived there, before the gate (and before any other
/// plugin's hook on the gate could change it).
/// <para>
/// From ChatTwo's public source (1.40.9): the IPC reports its main window's current tab, the channel plain text typed there
/// is sent with (<c>CurrentTab.CurrentChannel</c>, its one-off channel if it has one: the same field <c>SendChatBox</c>
/// reads), and the main input's text and length. ChatTwo empties its input only after the line has been sent, so while
/// the main input's line is on its way the input still holds it: as typed for a command ("/s hi"), or without the
/// channel's command for plain text ("hi", sent as "/p hi"). A line from a pop-out with its own input, from ChatTwo's web
/// interface or from another plugin leaves the main input as it was (usually empty, or a draft of another length).
/// </para>
/// </summary>
/// <param name="Prefix">
/// The short command ChatTwo puts in front of plain text typed in its main input now ("/p" on Party), or null if its
/// channel is one LookingGlass doesn't know.
/// </param>
/// <param name="FromMainInput">The main input holds this very line, so it was typed there.</param>
/// <param name="InputLength">The main input's length as typed, for the diagnostic log, or null if not known.</param>
/// <param name="LineLength">The line's length, for the diagnostic log, or null if not known.</param>
public sealed record ChatTwoLine(string? Prefix, bool FromMainInput, int? InputLength = null, int? LineLength = null) {
    /// <summary>
    /// The most spaces ChatTwo may have trimmed off its input's line (before and after together) for it still to count as
    /// that line. ChatTwo sends <c>chatInput.Trim()</c> but its typing IPC says the input's length as typed, so a stray
    /// space made "/s hi " look like another input's line, and every short command go to the channel. Two (one stray space
    /// at each end), not any number: the more room, the likelier a pop-out's line is taken for a main input draft of nearly
    /// its length.
    /// </summary>
    public const int MostTrimmed = 2;

    /// <param name="chatType">ChatTwo's chat type for its main input's channel (its own numbering: 14 is Party).</param>
    /// <param name="hasText">The main input isn't empty (ChatTwo's own test: its length is more than 0).</param>
    /// <param name="textLength">The main input's length, as typed (untrimmed).</param>
    /// <param name="lineText">The line's text, as it reached <c>ProcessChatBoxEntry</c>.</param>
    public static ChatTwoLine Of(int chatType, bool hasText, int textLength, string lineText) {
        var prefix = ChatChannelPrefixes.OfChatTwoType(chatType);
        // A command, sent as typed (trimmed).
        var typedAsIs = hasText && Trimmed(textLength, lineText.Length);
        // Plain text, sent (trimmed) after the channel's command and a space.
        var prefixed = hasText && prefix != null && lineText.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase)
                       && Trimmed(textLength, lineText.Length - prefix.Length - 1);
        return new ChatTwoLine(prefix, typedAsIs || prefixed, textLength, lineText.Length);
    }

    /// <summary>An input of <paramref name="inputLength"/> could have been <paramref name="sentLength"/> once trimmed.</summary>
    private static bool Trimmed(int inputLength, int sentLength) =>
        inputLength >= sentLength && inputLength - sentLength <= MostTrimmed;
}

/// <summary>Which short channel commands (/s, /p, /cwl1) followed by text stand for plain text, for one line, and why.</summary>
/// <param name="AsText">The short commands that, followed by anything, go to the LookingGlass channel as plain text would.</param>
/// <param name="Why">Why, in fixed words, for the diagnostic log (a known command and lengths at most, never what was typed).</param>
public sealed record ShortCommands(IReadOnlyCollection<string> AsText, string Why);

/// <summary>
/// The one-off rule for short channel commands while talking in a channel. FFXIV's own rule is that "/p brb" talks in Party
/// once, whatever channel the chat box is on, and players type it that way (almost nobody types "/party"), so a short
/// command followed by text goes to that game channel once, and talking in the LookingGlass channel goes on. The one
/// exception is ChatTwo: it sends plain text typed in it as "&lt;its channel's short command&gt; text" ("hi" typed in an
/// input on Party is sent as "/p hi"), which can't be told apart from the player typing "/p hi" there. So:
/// <list type="bullet">
/// <item>A line from the game itself (its own chat box, a macro line, a gear set): short commands go to the game once.</item>
/// <item>A line from ChatTwo's main input (see <see cref="ChatTwoLine"/>): only the short command of ChatTwo's current
/// channel stands for plain text; any other short command goes to the game once. The cost, accepted: "/p hi" typed in
/// ChatTwo while it is on Party goes to the LookingGlass channel (the long form, "/party hi", goes to Party).</item>
/// <item>Anything else from a plugin, or a line whose way in isn't known: every short command stands for plain text, as
/// it did before (fail safe: a pop-out's typing, sent with the pop-out's own channel's command, never reaches game chat).</item>
/// </list>
/// </summary>
public static class ShortCommandRule {
    public static ShortCommands For(LineSource source, ChatTwoLine? chatTwo) {
        var all = ChatChannelPrefixes.ChatTwo;
        return source switch {
            LineSource.Game => new ShortCommands([], "typed in the game: short commands go to the game once"),
            LineSource.Plugin => chatTwo switch {
                null => new ShortCommands(all, "ChatTwo's input unreadable: short commands are text"),
                { Prefix: null } => new ShortCommands(all, "ChatTwo's channel unknown: short commands are text"),
                { FromMainInput: false, InputLength: { } input, LineLength: { } line } =>
                    new ShortCommands(all, $"not ChatTwo's main input (it holds {input} characters, the line {line}): short commands are text"),
                { FromMainInput: false } => new ShortCommands(all, "not ChatTwo's main input: short commands are text"),
                { Prefix: { } prefix } when all.Contains(prefix) =>
                    new ShortCommands(new[] { prefix }.ToHashSet(StringComparer.OrdinalIgnoreCase),
                        $"ChatTwo's main input on {prefix}: only {prefix} is text, other short commands go to the game once"),
                // On echo (no channel), a tell or an ExtraChat channel: none of the game's short commands is its typing.
                { Prefix: { } other } => new ShortCommands([], $"ChatTwo's main input on {other}: short commands go to the game once"),
            },
            _ => new ShortCommands(all, "way in unknown: short commands are text"),
        };
    }
}
