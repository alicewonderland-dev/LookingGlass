using LookingGlass.Core.Util;

namespace LookingGlass.Core.Client;

/// <summary>
/// One step of a coloured line of game text (a chat line, the server info bar, a menu entry), in order. The plugin turns
/// each into the game's own text formatting (an SeString payload); everything about which colours go where, and in what
/// order, is decided here, where it is tested.
/// </summary>
public abstract record TextPart {
    private TextPart() {
    }

    /// <summary>Plain text.</summary>
    public sealed record Text(string Value) : TextPart;

    /// <summary>The game's UIForeground macro with a UIColor row: text after it is in that row's colour.</summary>
    public sealed record ForegroundOn(ushort Row) : TextPart;

    /// <summary>The UIForeground macro with 0: back to the colour before the matching <see cref="ForegroundOn"/>.</summary>
    public sealed record ForegroundOff : TextPart;

    /// <summary>The game's Color macro with an exact colour (0xRRGGBB, fully opaque): text after it is in that colour.</summary>
    public sealed record ColourOn(uint Rgb) : TextPart;

    /// <summary>The Color macro with "stackcolor": back to the colour before the matching <see cref="ColourOn"/>.</summary>
    public sealed record ColourOff : TextPart;

    /// <summary>Where a chat message's own sender and text go (built by the plugin: they hold links).</summary>
    public sealed record Body : TextPart;
}

/// <summary>
/// How a channel's colour is put on game text. A UIColor row is the game's UIForeground macro, as always. A custom colour
/// is layered: the UIForeground macro with the closest row outside, the Color macro with the exact colour inside:
/// <c>UIForeground(closest) → Color(exact) → text → Color off → UIForeground off</c>. Whatever shows the Color macro (the
/// game's own text, as far as known) shows the exact colour, as the innermost one; whatever ignores it shows the closest
/// row instead of no colour at all. ChatTwo, reading the game's chat, honours both (see docs/design.md).
/// </summary>
public static class ColouredText {
    /// <summary>The default tag colour as a channel colour: LookingGlass blue.</summary>
    public static readonly ChannelColour DefaultTag = ChannelColour.OfRow(NoticeColours.Blue);

    /// <summary>The prefix of LookingGlass's own lines in chat, in LookingGlass blue.</summary>
    public const string NoticePrefix = "[LookingGlass] ";

    /// <summary><paramref name="inner"/> in <paramref name="colour"/>, layered as the class says.</summary>
    /// <param name="nearest">The closest UIColor row to a custom colour (0xRRGGBB), or null if there is none (the sheet couldn't be read): the exact colour alone then.</param>
    public static IEnumerable<TextPart> Wrap(ChannelColour colour, Func<uint, ushort?> nearest, IEnumerable<TextPart> inner) {
        if (!colour.IsCustom) {
            if (colour.Row == 0) {
                // Row 0 has no colour: nothing to put on.
                foreach (var part in inner) {
                    yield return part;
                }

                yield break;
            }

            yield return new TextPart.ForegroundOn(colour.Row);
            foreach (var part in inner) {
                yield return part;
            }

            yield return new TextPart.ForegroundOff();
            yield break;
        }

        var fallback = nearest(colour.Rgb);
        if (fallback is { } row) {
            yield return new TextPart.ForegroundOn(row);
        }

        yield return new TextPart.ColourOn(colour.Rgb);
        foreach (var part in inner) {
            yield return part;
        }

        yield return new TextPart.ColourOff();
        if (fallback != null) {
            yield return new TextPart.ForegroundOff();
        }
    }

    /// <summary>Some text in a colour.</summary>
    public static IEnumerable<TextPart> Wrap(ChannelColour colour, Func<uint, ushort?> nearest, string text) =>
        Wrap(colour, nearest, [new TextPart.Text(text)]);

    /// <summary>
    /// A channel's message in game chat: its tag in the channel's colour (LookingGlass blue for the default), the time it
    /// was sent for one caught up on, then the message (<see cref="TextPart.Body"/>), in the channel's colour too if it has
    /// one and <paramref name="colourWholeLine"/> is on.
    /// </summary>
    public static IReadOnlyList<TextPart> Message(string tag, ChannelColour? colour, string? sentAt, bool colourWholeLine, Func<uint, ushort?> nearest) {
        var parts = new List<TextPart>(Wrap(colour ?? DefaultTag, nearest, tag));
        if (sentAt != null) {
            // Before the sender, in brackets, so it reads as when, not as part of what was said.
            parts.Add(new TextPart.Text($"[{sentAt}] "));
        }

        if (colour is { } whole && colourWholeLine) {
            parts.AddRange(Wrap(whole, nearest, [new TextPart.Body()]));
        } else {
            parts.Add(new TextPart.Body());
        }

        return parts;
    }

    /// <summary>
    /// One of LookingGlass's own lines: "[LookingGlass] " in LookingGlass blue, then the text in the tone's colour, with a
    /// channel's tag in it (its first appearance) in the channel's colour (LookingGlass blue for the default).
    /// </summary>
    public static IReadOnlyList<TextPart> Notice(NoticeTone tone, string text, string? tag, ChannelColour? tagColour, Func<uint, ushort?> nearest) {
        var colour = ChannelColour.OfRow(NoticeColours.Of(tone));
        var parts = new List<TextPart>(Wrap(DefaultTag, nearest, NoticePrefix));
        var at = string.IsNullOrEmpty(tag) ? -1 : text.IndexOf(tag, StringComparison.Ordinal);
        if (at < 0) {
            parts.AddRange(Wrap(colour, nearest, text));
            return parts;
        }

        if (at > 0) {
            parts.AddRange(Wrap(colour, nearest, text[..at]));
        }

        parts.AddRange(Wrap(tagColour ?? DefaultTag, nearest, tag!));
        if (at + tag!.Length < text.Length) {
            parts.AddRange(Wrap(colour, nearest, text[(at + tag.Length)..]));
        }

        return parts;
    }

    /// <summary>The server info bar while talking in a channel: "LG [sky]" in the channel's colour.</summary>
    public static IReadOnlyList<TextPart> InfoBar(string tag, ChannelColour? colour, Func<uint, ushort?> nearest) =>
        Wrap(colour ?? DefaultTag, nearest, $"LG {tag}").ToList();

    /// <summary>A channel in the game's "Invite to LookingGlass" menu: its tag in its colour, then its name (and why it can't be picked).</summary>
    public static IReadOnlyList<TextPart> MenuEntry(InviteOffer offer, Func<uint, ushort?> nearest) =>
        [.. Wrap(offer.Colour ?? DefaultTag, nearest, offer.Tag), new TextPart.Text(offer.Rest)];

    /// <summary>
    /// The colour test's colours when none are given: pink, teal, violet and orange, which no swatch matches exactly, and
    /// a dark navy, too dark to read well (the picker warns about it).
    /// </summary>
    public static readonly IReadOnlyList<uint> TestColours = [0xFF66CC, 0x33DDAA, 0x8844FF, 0xFFAA00, 0x1A2B6D];

    /// <summary>
    /// The colours for "/lgdebug colours": the colour codes given (see <see cref="HexColour.TryParse"/>), separated by
    /// spaces or commas, or <see cref="TestColours"/> with none; null if any can't be read. At most eight.
    /// </summary>
    public static IReadOnlyList<uint>? TestColoursFrom(string arguments) {
        var codes = arguments.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (codes.Length == 0) {
            return TestColours;
        }

        var colours = new List<uint>();
        foreach (var code in codes.Take(8)) {
            if (!HexColour.TryParse(code, out var rgb)) {
                return null;
            }

            colours.Add(rgb);
        }

        return colours;
    }

    /// <summary>
    /// The colour test (/lgdebug): three lines for one custom colour, to compare where each renderer shows what. The first
    /// is layered as channels are; the second has only the closest row (what a renderer ignoring the exact colour shows);
    /// the third only the exact colour, with no row under it (a renderer ignoring it shows the chat channel's own colour).
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<TextPart>> Samples(uint rgb, Func<uint, ushort?> nearest) {
        var hex = HexColour.Format(rgb);
        var row = nearest(rgb);
        var rowName = row is { } r ? $"game colour {r}" : "no game colour found";
        var layered = new List<TextPart>(Wrap(DefaultTag, nearest, NoticePrefix));
        layered.AddRange(Wrap(ChannelColour.Custom(rgb), nearest, $"{hex} exact, {rowName} under it"));
        var rowOnly = new List<TextPart>(Wrap(DefaultTag, nearest, NoticePrefix));
        rowOnly.AddRange(row is { } fallback ? Wrap(ChannelColour.OfRow(fallback), nearest, $"{hex} closest {rowName} only") : [new TextPart.Text($"{hex}: {rowName}")]);
        var exactOnly = new List<TextPart>(Wrap(DefaultTag, nearest, NoticePrefix));
        exactOnly.AddRange(Wrap(ChannelColour.Custom(rgb), _ => null, $"{hex} exact only, nothing under it"));
        return [layered, rowOnly, exactOnly];
    }
}
