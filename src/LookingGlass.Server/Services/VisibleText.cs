using System.Globalization;
using System.Text;

namespace LookingGlass.Server.Services;

/// <summary>
/// Text from outside (what an operator typed, a name or reason from the database, a header) made safe to keep and to print
/// on a terminal: control characters (a terminal's escape sequences), invisible format characters (bidirectional
/// overrides, zero-width characters), line and paragraph separators, and broken surrogates are shown as escapes
/// (<c>\u001B</c>), so what was there stays visible and can't move the cursor, recolour the screen, retitle the window or
/// forge a line.
/// </summary>
public static class VisibleText {
    /// <summary><paramref name="text"/> with every character that isn't plain shown as an escape, cut to <paramref name="maxLength"/> characters (then "…").</summary>
    public static string Of(string? text, int maxLength = int.MaxValue) {
        if (string.IsNullOrEmpty(text)) {
            return "";
        }

        var builder = new StringBuilder(Math.Min(text.Length, 256));
        for (var at = 0; at < text.Length;) {
            if (builder.Length >= maxLength) {
                builder.Append('…');
                break;
            }

            if (Rune.DecodeFromUtf16(text.AsSpan(at), out var rune, out var used) != System.Buffers.OperationStatus.Done) {
                builder.Append(CultureInfo.InvariantCulture, $"\\u{(int) text[at]:X4}");
                at++;
                continue;
            }

            if (IsHidden(rune)) {
                builder.Append(rune.IsBmp ? $"\\u{rune.Value:X4}" : $"\\U{rune.Value:X8}");
            } else {
                builder.Append(text, at, used);
            }

            at += used;
        }

        return builder.ToString();
    }

    /// <summary>Whether the text has nothing <see cref="Of"/> would escape.</summary>
    public static bool IsVisible(string text) => Of(text) == text;

    private static bool IsHidden(Rune rune) => Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
        or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate;

    /// <summary>
    /// A writer that prints everything through <see cref="Of"/>: what the commands print, whatever it holds (a name or a reason
    /// from the database, as well as what was typed), can't carry a terminal's escape sequences or forge a line. Only the
    /// line breaks the commands write themselves (<see cref="TextWriter.WriteLine()"/>) get through.
    /// </summary>
    public sealed class Writer(TextWriter inner) : TextWriter {
        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value) {
            if (value is '\n' or '\r') {
                // Only a line break the commands write themselves reaches here as a character on its own (strings are escaped whole).
                inner.Write(value);
            } else {
                inner.Write(Of(value.ToString()));
            }
        }

        public override void Write(string? value) => inner.Write(Of(value));

        public override void Write(char[] buffer, int index, int count) => this.Write(new string(buffer, index, count));

        public override void WriteLine(string? value) {
            inner.Write(Of(value));
            inner.WriteLine();
        }

        public override void WriteLine() => inner.WriteLine();

        public override void Flush() => inner.Flush();
    }
}
