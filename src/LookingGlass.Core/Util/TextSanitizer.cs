using System.Globalization;
using System.Text;
using LookingGlass.Core.Crypto;

namespace LookingGlass.Core.Util;

/// <summary>
/// Cleans text that came from other users or the server before it is shown.
/// FFXIV chat strings treat byte 0x02 as the start of a formatting macro
/// (links, colours, icons), and Dalamud's text payload doesn't escape it, so
/// remote text containing control characters could inject live formatting.
/// </summary>
public static class TextSanitizer {
    public const int MaxNameLength = 64;
    public const int MaxMessageLength = 1000;

    /// <summary>
    /// Removes control and invisible format characters, collapses line breaks to spaces, caps the length, and removes
    /// registration codes (see <see cref="LodestoneCode.Redact"/>).
    /// </summary>
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

        // Nobody's text is the place for a registration code: the user's own is shown where registering is.
        return LodestoneCode.Redact(builder.ToString());
    }

    public static string Name(string? text) => Clean(text, MaxNameLength);

    /// <summary>
    /// Whether <paramref name="text"/> is plain text, as a name is: nothing <see cref="Clean"/> would drop or turn into
    /// a space (control and format characters, line breaks and tabs, unassigned code points, broken surrogate pairs),
    /// nor a line or paragraph separator. Text that isn't can't be logged as it is: a line break there forges log lines.
    /// </summary>
    public static bool IsPlain(string text) {
        for (var at = 0; at < text.Length;) {
            if (Rune.DecodeFromUtf16(text.AsSpan(at), out var rune, out var used) != System.Buffers.OperationStatus.Done
                || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
                    or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) {
                return false;
            }

            at += used;
        }

        return true;
    }
}
