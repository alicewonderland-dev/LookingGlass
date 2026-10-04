using System.Globalization;
using System.Text;

namespace WonderlandChat.Core.Util;

/// <summary>
/// Cleans text that came from other users or the server before it is shown.
/// FFXIV chat strings treat byte 0x02 as the start of a formatting macro
/// (links, colours, icons), and Dalamud's text payload doesn't escape it, so
/// remote text containing control characters could inject live formatting.
/// </summary>
public static class TextSanitizer {
    public const int MaxNameLength = 64;
    public const int MaxMessageLength = 1000;

    /// <summary>Removes control and invisible format characters, collapses line breaks to spaces, and caps the length.</summary>
    public static string Clean(string? text, int maxLength = MaxMessageLength) {
        if (string.IsNullOrEmpty(text)) {
            return "";
        }

        var builder = new StringBuilder(Math.Min(text.Length, maxLength));
        foreach (var rune in text.EnumerateRunes()) {
            if (builder.Length >= maxLength) {
                builder.Append('…');
                break;
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (rune.Value is '\n' or '\r' or '\t') {
                builder.Append(' ');
            } else if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
                       or UnicodeCategory.OtherNotAssigned) {
                // Drop: includes 0x02/0x03 (game macros), bidi overrides and zero-width characters.
                // Private-use characters stay: the game draws its own icons with them.
            } else {
                builder.Append(rune.ToString());
            }
        }

        return builder.ToString();
    }

    public static string Name(string? text) => Clean(text, MaxNameLength);
}
