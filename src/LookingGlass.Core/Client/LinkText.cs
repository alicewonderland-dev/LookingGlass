using System.Text;
using System.Text.RegularExpressions;
using LookingGlass.Core.Util;

namespace LookingGlass.Core.Client;

/// <summary>
/// A link in a line typed in game: what it points at, if that could be read (null: only its name is known), and its name
/// in the sender's game data (null: not known either).
/// </summary>
public sealed record TypedLink(ChatLink? Target, string? Name);

/// <summary>
/// A line typed in game, as LookingGlass reads it: its text, with a marker (<see cref="LinkText.Marker"/>) where each
/// link was, and the links. The game's link placeholders (&lt;item&gt;, &lt;flag&gt;, &lt;status&gt;) may still be in
/// the text as typed, until <see cref="LinkText.ResolvePlaceholders"/>. A marker's number is its link's place in
/// <see cref="Links"/>, so the text can be cut (the command taken off) and the links still found.
/// </summary>
public sealed record TypedLine(string Text, IReadOnlyList<TypedLink> Links) {
    /// <summary>Plain text, as typed: anything that reads as a marker is taken out.</summary>
    public static TypedLine Plain(string text) => new(LinkText.StripMarkers(text), []);

    /// <summary>The same links, with other text (a part of this line's).</summary>
    public TypedLine WithText(string text) => this with { Text = text };

    /// <summary>The line after its command ("/lgc1 look [x]" without "/lgc1"): what a command handler gets as its arguments.</summary>
    public TypedLine Arguments() {
        var end = 0;
        while (end < this.Text.Length && !char.IsWhiteSpace(this.Text[end])) {
            end++;
        }

        return this.WithText(this.Text[end..].TrimStart());
    }

    /// <summary>
    /// A command's arguments as Dalamud gives them, for a line that wasn't read at the gate: plain text. A link's bytes,
    /// if any reached it, are taken out (each runs from a byte 2 to the next byte 3), and so is anything that reads as a
    /// marker; placeholders stay, to be resolved.
    /// </summary>
    public static TypedLine FromArguments(string arguments) {
        var text = new StringBuilder(arguments.Length);
        var inPayload = false;
        foreach (var c in arguments) {
            if (c == '\u0002') {
                inPayload = true;
            } else if (inPayload) {
                inPayload = c != '\u0003';
            } else {
                text.Append(c);
            }
        }

        return Plain(text.ToString());
    }

    public bool Equals(TypedLine? other) => other != null && this.Text == other.Text && this.Links.SequenceEqual(other.Links);

    public override int GetHashCode() => HashCode.Combine(this.Text, this.Links.Count);
}

/// <summary>
/// Links in a message sent to a LookingGlass channel. A link goes in two forms (see <see cref="MessageContent"/>): as its
/// name in square brackets in the text, "look &lt;item&gt;" sent as "look [Potion]", which older clients show; and over
/// that, as what it points at (an item, a map flag, a status), which a client that knows links shows as the game's own
/// interactive link.
/// <para>
/// A line typed in game holds a link in one of two ways, in both the game's chat box and ChatTwo's: as a placeholder
/// (<see cref="ChatBoxLine.LinkPlaceholders"/>) until the game runs the line, the game keeping what it stands for
/// elsewhere (the linked item, the map flag, the status), which the plugin reads; or as the link's own bytes, which the
/// plugin reads into a <see cref="TypedLink"/> where the line has a marker.
/// </para>
/// </summary>
public static partial class LinkText {
    /// <summary>The first marker: Unicode noncharacters, which never stand for anything in game text.</summary>
    private const char FirstMarker = '﷐';

    /// <summary>The most links one typed line can mark (the rest go as their names).</summary>
    public const int MaxMarkers = 32;

    [GeneratedRegex("<item>|<flag>|<status>", RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();

    [GeneratedRegex(" {2,}")]
    private static partial Regex Spaces();

    /// <summary>The marker for the link at <paramref name="index"/> in a <see cref="TypedLine"/>.</summary>
    public static char Marker(int index) => index is >= 0 and < MaxMarkers
        ? (char) (FirstMarker + index)
        : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>The link a marker stands for, or null if <paramref name="c"/> isn't one.</summary>
    public static int? MarkerIndex(char c) => c is >= FirstMarker and < (char) (FirstMarker + MaxMarkers) ? c - FirstMarker : null;

    /// <summary><paramref name="text"/> without anything that reads as a marker.</summary>
    public static string StripMarkers(string text) {
        if (!text.Any(c => MarkerIndex(c) != null)) {
            return text;
        }

        return new string(text.Where(c => MarkerIndex(c) == null).ToArray());
    }

    /// <summary>Whether <paramref name="text"/> holds a link: a marker or a placeholder.</summary>
    public static bool HasLink(string text) => text.Any(c => MarkerIndex(c) != null) || Placeholder().IsMatch(text);

    /// <summary><paramref name="text"/> with every placeholder and marker taken out: what is left as typed text.</summary>
    public static string WithoutLinks(string text) => Placeholder().Replace(StripMarkers(text), " ");

    /// <summary>
    /// The line with every link placeholder replaced by a marker for what it stands for now, asked of
    /// <paramref name="resolve"/> at most once for each kind ("&lt;item&gt;", lower case). It may return null (or throw)
    /// if nothing is known; the link then has neither a target nor a name. Placeholders past
    /// <see cref="MaxMarkers"/> links are left as they are, and taken out when the message is made.
    /// </summary>
    public static TypedLine ResolvePlaceholders(TypedLine line, Func<string, TypedLink?> resolve) {
        if (!Placeholder().IsMatch(line.Text)) {
            return line;
        }

        var links = line.Links.ToList();
        var asked = new Dictionary<string, int>();
        var text = Placeholder().Replace(line.Text, match => {
            var placeholder = match.Value.ToLowerInvariant();
            if (!asked.TryGetValue(placeholder, out var index)) {
                if (links.Count >= MaxMarkers) {
                    return match.Value;
                }

                TypedLink? link;
                try {
                    link = resolve(placeholder);
                } catch {
                    link = null;
                }

                index = links.Count;
                links.Add(link ?? new TypedLink(null, null));
                asked[placeholder] = index;
            }

            return Marker(index).ToString();
        });

        return new TypedLine(text, links);
    }

    /// <summary>
    /// The message to send for a line (placeholders already resolved, see <see cref="ResolvePlaceholders"/>): each link as
    /// "[name]", and, for the first <see cref="ChatLinks.MaxPerMessage"/> whose target is known and well formed, a link
    /// over it. A link with a name but no target goes as its name only. One with no name is taken out, with the spaces
    /// around it, and said: so is a placeholder left over.
    /// </summary>
    /// <returns>The message, and whether a link was left out (to say so).</returns>
    public static (LinkedText Message, bool LeftOut) Compose(TypedLine line) {
        // Pieces: typed text, or a link's "[name]" (with its target if it is sent as a link). Text runs on past a link
        // taken out, so two pieces of typed text never stand side by side.
        var pieces = new List<Piece>();
        var leftOut = false;
        var typed = new StringBuilder();
        var linked = 0;

        var text = Placeholder().Replace(line.Text, _ => {
            leftOut = true;
            return "";
        });
        foreach (var c in text) {
            if (MarkerIndex(c) is not { } index) {
                typed.Append(c);
                continue;
            }

            var link = index < line.Links.Count ? line.Links[index] : null;
            if (Clean(link?.Name) is not { } name) {
                leftOut = true;
                continue;
            }

            if (typed.Length > 0) {
                pieces.Add(new Piece(typed.ToString(), false, null));
                typed.Clear();
            }

            var target = link!.Target is { } known && ChatLinks.IsWellFormed(known) && linked < ChatLinks.MaxPerMessage ? known : null;
            if (target != null) {
                linked++;
            }

            pieces.Add(new Piece($"[{name}]", true, target));
        }

        if (typed.Length > 0) {
            pieces.Add(new Piece(typed.ToString(), false, null));
        }

        // Only where something was taken out: the spaces around it, in the typed text (a name has none to spare).
        if (leftOut) {
            for (var i = 0; i < pieces.Count; i++) {
                if (pieces[i].IsLink) {
                    continue;
                }

                var piece = Spaces().Replace(pieces[i].Text, " ");
                piece = i == 0 ? piece.TrimStart() : piece;
                piece = i == pieces.Count - 1 ? piece.TrimEnd() : piece;
                pieces[i] = pieces[i] with { Text = piece };
            }
        }

        var message = new StringBuilder();
        var links = new List<MessageLink>();
        foreach (var piece in pieces) {
            if (piece.Target != null) {
                links.Add(new MessageLink(message.Length, piece.Text.Length, piece.Target));
            }

            message.Append(piece.Text);
        }

        return (new LinkedText(message.ToString(), links), leftOut);
    }

    /// <summary>
    /// What a placeholder stands for, from what the game holds for it: the target if it is well formed; its name from the
    /// game's sheet (<paramref name="sheetName"/>), or else the game's own text for it (<paramref name="gameText"/>);
    /// null if neither is known (it is then left out).
    /// </summary>
    public static TypedLink? PlaceholderLink(ChatLink? target, string? sheetName, string? gameText) {
        var name = Clean(sheetName) ?? Clean(gameText);
        return name == null ? null : new TypedLink(target is { } known && ChatLinks.IsWellFormed(known) ? known : null, name);
    }

    /// <summary>For tests, and plain text: the message for <paramref name="text"/>, its placeholders resolved by <paramref name="resolve"/>.</summary>
    public static (LinkedText Message, bool LeftOut) Resolve(string text, Func<string, TypedLink?> resolve) =>
        Compose(ResolvePlaceholders(TypedLine.Plain(text), resolve));

    private sealed record Piece(string Text, bool IsLink, ChatLink? Target);

    /// <summary>A name as plain text (no game formatting or icons, nothing that reads as a placeholder or marker), or null if empty.</summary>
    public static string? Clean(string? name) {
        if (name == null) {
            return null;
        }

        name = StripMarkers(TextSanitizer.Name(name));
        // The game's own icons (the link arrow, the high-quality mark) are private-use characters: not part of a name.
        name = new string(name.Where(c => char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.PrivateUse).ToArray());
        name = name.Replace('<', '(').Replace('>', ')').Replace('[', '(').Replace(']', ')').Trim();
        name = Spaces().Replace(name, " ");
        return name.Length == 0 ? null : name;
    }
}
