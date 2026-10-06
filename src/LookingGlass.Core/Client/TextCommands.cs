using System.Text;
using System.Text.RegularExpressions;
using LookingGlass.Core.Util;

namespace LookingGlass.Core.Client;

/// <summary>
/// The game's text commands in a line sent to a channel: &lt;t&gt; for the target, &lt;me&gt; for the player's own name
/// and the like, which the game replaces with what they stand for when it sends a chat line, and which LookingGlass
/// replaces the same way before a message is encrypted (see docs/design.md, Text commands). Only on the sender's side: a
/// received message is never looked at for them. The game's link placeholders (&lt;item&gt;, &lt;flag&gt;,
/// &lt;status&gt;) are links, not text commands (see <see cref="LinkText"/>).
/// </summary>
public static partial class TextCommands {
    /// <summary>
    /// The text commands replaced, lower case: the target, the target's target, the focus target, the player, the
    /// mouseover, the last target, party members 1 to 8, the last tell partner, and the player's position.
    /// </summary>
    public static readonly IReadOnlyList<string> Supported =
        ["<t>", "<tt>", "<f>", "<me>", "<mo>", "<lt>", .. Enumerable.Range(1, 8).Select(i => $"<{i}>"), "<r>", "<pos>"];

    private static readonly HashSet<string> SupportedSet = new(Supported, StringComparer.OrdinalIgnoreCase);

    /// <summary>Anything that may be a text command or a link placeholder: a short word between angle brackets.</summary>
    [GeneratedRegex(@"\G<[0-9a-z]{1,8}>", RegexOptions.IgnoreCase)]
    private static partial Regex Token();

    /// <summary>What a piece of a line is.</summary>
    public enum PieceKind {
        /// <summary>Typed text, an unknown text command (&lt;se.1&gt;, &lt;hp&gt;) included.</summary>
        Text,

        /// <summary>A supported text command, as typed (&lt;t&gt;, &lt;T&gt;).</summary>
        Command,

        /// <summary>A link: its marker (see <see cref="LinkText.Marker"/>), or a link placeholder left in the text.</summary>
        Link,
    }

    /// <summary>A piece of a line, as typed.</summary>
    public readonly record struct Piece(string Text, PieceKind Kind);

    /// <summary>
    /// The line cut into pieces: typed text, supported text commands, and links (markers and link placeholders). Joined
    /// back together, they are the line.
    /// </summary>
    public static IReadOnlyList<Piece> Split(string text) {
        var pieces = new List<Piece>();
        var typed = new StringBuilder();

        void Flush() {
            if (typed.Length > 0) {
                pieces.Add(new Piece(typed.ToString(), PieceKind.Text));
                typed.Clear();
            }
        }

        for (var at = 0; at < text.Length;) {
            if (LinkText.MarkerIndex(text[at]) != null) {
                Flush();
                pieces.Add(new Piece(text[at].ToString(), PieceKind.Link));
                at++;
                continue;
            }

            if (text[at] == '<' && Token().Match(text, at) is { Success: true } match) {
                PieceKind? kind = SupportedSet.Contains(match.Value) ? PieceKind.Command
                    : LinkText.IsPlaceholder(match.Value) ? PieceKind.Link
                    : null;
                if (kind is { } known) {
                    Flush();
                    pieces.Add(new Piece(match.Value, known));
                    at += match.Length;
                    continue;
                }
            }

            typed.Append(text[at++]);
        }

        Flush();
        return pieces;
    }

    /// <summary>Whether <paramref name="text"/> has a supported text command in it.</summary>
    public static bool HasCommand(string text) => Split(text).Any(piece => piece.Kind == PieceKind.Command);

    /// <summary>
    /// The line with each supported text command replaced by what <paramref name="resolve"/> says it stands for now,
    /// asked once for each, as typed (so the game's own rules on upper and lower case apply), before its links are
    /// composed (<see cref="LinkText.Compose"/>).
    /// One it says nothing about (null, empty, the text command handed back, or it throws) stays as typed. What a text
    /// command stands for goes as plain text (see <see cref="CleanValue"/>), never as a link, and is never looked at again
    /// for text commands. Typed text, links and anything else between angle brackets are left as they are.
    /// </summary>
    /// <returns>The line, and how many text commands were replaced (for the diagnostic log: a count, never what they stood for).</returns>
    public static (TypedLine Line, int Replaced) Resolve(TypedLine line, Func<string, string?> resolve) {
        var pieces = Split(line.Text);
        if (pieces.All(piece => piece.Kind != PieceKind.Command)) {
            return (line, 0);
        }

        var text = new StringBuilder(line.Text.Length);
        var asked = new Dictionary<string, string?>();
        var replaced = 0;
        foreach (var piece in pieces) {
            if (piece.Kind != PieceKind.Command) {
                text.Append(piece.Text);
                continue;
            }

            var command = piece.Text;
            if (!asked.TryGetValue(command, out var value)) {
                string? raw;
                try {
                    raw = resolve(command);
                } catch {
                    raw = null;
                }

                // Handed back unchanged: not resolved.
                value = raw != null && string.Equals(raw.Trim(), command, StringComparison.OrdinalIgnoreCase) ? null : CleanValue(raw);
                asked[command] = value;
            }

            if (value == null) {
                text.Append(piece.Text);
            } else {
                text.Append(value);
                replaced++;
            }
        }

        return (line.WithText(text.ToString()), replaced);
    }

    /// <summary>
    /// What a text command stands for, as plain text: no game formatting, no line breaks, nothing that reads as a link's
    /// marker or placeholder (angle brackets become round ones). The game's own icons (private-use characters, such as
    /// the cross-world mark before a world's name) stay. Null if nothing is left.
    /// </summary>
    public static string? CleanValue(string? value) {
        if (value == null) {
            return null;
        }

        var clean = LinkText.StripMarkers(TextSanitizer.Clean(value, TextSanitizer.MaxNameLength * 2))
            .Replace('<', '(').Replace('>', ')')
            .Trim();
        return clean.Length == 0 ? null : clean;
    }
}
