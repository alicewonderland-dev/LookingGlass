using System.Text;
using LookingGlass.Core.Util;

namespace LookingGlass.Core.Client;

/// <summary>
/// A line typed in a channel window's input box that starts with "/": a command, run by the game as if typed in its own
/// chat box (/sit, /p hello, /lgc2 hi), never sent to the channel as text. Lines the game's chat box couldn't hold aren't
/// run: the window keeps what was typed, and says why.
/// </summary>
public static class WindowCommands {
    /// <summary>The longest line the game takes from its chat box, in UTF-8 bytes.</summary>
    public const int MaxBytes = 500;

    public const string TooLong = "Not run: that command is too long for game chat.";

    public const string NotPlain = "Not run: that command has characters game chat can't take.";

    public const string Unavailable = "Not run: commands can't be run from this window in this game version. Type it in game chat instead.";

    /// <summary>Whether <paramref name="text"/> (as sent, trimmed) is a command for the game rather than a message.</summary>
    public static bool IsCommand(string text) => text.StartsWith('/');

    /// <summary>Why the game can't be given <paramref name="line"/>, or null if it can.</summary>
    public static string? Refusal(string line) =>
        !TextSanitizer.IsPlain(line) ? NotPlain
        : Encoding.UTF8.GetByteCount(line) > MaxBytes ? TooLong
        : null;
}
