using System.Text.RegularExpressions;
using LookingGlass.Core.Util;

namespace LookingGlass.Core.Client;

/// <summary>
/// Links in a message sent to a LookingGlass channel. Channels carry text only, so a link goes as its name, in square
/// brackets, as plain text: "look &lt;item&gt;" is sent as "look [Potion]". What the chat box holds for a link until the
/// game runs the line is a placeholder (<see cref="ChatBoxLine.LinkPlaceholders"/>), in both the game's chat box and
/// ChatTwo's; the game keeps what it stands for elsewhere (the linked item, the map flag, the status), which the plugin
/// reads. A link already in the line as its own bytes is sent as its text (its name) without help.
/// </summary>
public static partial class LinkText {
    [GeneratedRegex("<item>|<flag>|<status>", RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();

    [GeneratedRegex(" {2,}")]
    private static partial Regex Spaces();

    /// <summary>
    /// <paramref name="text"/> with every link placeholder replaced by "[name]", or taken out if its name can't be found.
    /// </summary>
    /// <param name="nameOf">
    /// The name of what a placeholder ("&lt;item&gt;", lower case) stands for now, or null if it can't be found. Asked
    /// at most once for each kind; it may throw, which counts as not found.
    /// </param>
    /// <returns>The text to send, and whether a link was left out (to say so).</returns>
    public static (string Text, bool LeftOut) Resolve(string text, Func<string, string?> nameOf) {
        var names = new Dictionary<string, string?>();
        var leftOut = false;
        var resolved = Placeholder().Replace(text, match => {
            var placeholder = match.Value.ToLowerInvariant();
            if (!names.TryGetValue(placeholder, out var name)) {
                try {
                    name = Clean(nameOf(placeholder));
                } catch {
                    name = null;
                }

                names[placeholder] = name;
            }

            if (name == null) {
                leftOut = true;
                return "";
            }

            return $"[{name}]";
        });

        // Only where something was taken out: the spaces around it.
        return leftOut ? (Spaces().Replace(resolved, " ").Trim(), true) : (resolved, false);
    }

    /// <summary>A name as plain text (no game formatting, nothing that reads as another placeholder), or null if empty.</summary>
    private static string? Clean(string? name) {
        if (name == null) {
            return null;
        }

        name = TextSanitizer.Name(name).Replace('<', '(').Replace('>', ')').Replace('[', '(').Replace(']', ')').Trim();
        return name.Length == 0 ? null : name;
    }
}
