using System.Collections.Concurrent;

namespace LookingGlass.Core.Util;

/// <summary>
/// The game's colour table (the UIColor sheet's rows, each packed 0xRRGGBBAA as <see cref="UiColorPacking"/> says), for
/// finding the row closest to a custom colour: what a renderer that ignores the exact colour shows instead (see
/// <see cref="Client.ColouredText"/>). Closest by eye: the distance in CIELAB (ΔE*76), not in raw RGB. Thread-safe.
/// </summary>
public sealed class UiColourTable {
    private readonly (ushort Row, Lab Lab)[] _rows;
    private readonly ConcurrentDictionary<uint, ushort?> _nearest = new();

    /// <param name="rows">Every row and its colour. Rows that aren't fully opaque, and row 0 (no colour), are never picked.</param>
    public UiColourTable(IEnumerable<(ushort Row, uint Rgba)> rows) {
        this._rows = rows
            .Where(row => row.Row != 0 && (row.Rgba & 0xFF) == 0xFF)
            .OrderBy(row => row.Row)
            .Select(row => (row.Row, Lab.Of(row.Rgba >> 8)))
            .ToArray();
    }

    /// <summary>How many rows can be picked.</summary>
    public int Count => this._rows.Length;

    /// <summary>The row closest to a colour (0xRRGGBB), the lowest-numbered of equally close ones; null if the table has none.</summary>
    public ushort? Nearest(uint rgb) => this._nearest.GetOrAdd(rgb & 0xFFFFFF, this.Find);

    private ushort? Find(uint rgb) {
        var lab = Lab.Of(rgb);
        ushort? best = null;
        var bestDistance = double.MaxValue;
        // In row order, and only strictly closer ones replace the best: ties go to the lowest row.
        foreach (var (row, rowLab) in this._rows) {
            var distance = Lab.DistanceSquared(lab, rowLab);
            if (distance < bestDistance) {
                best = row;
                bestDistance = distance;
            }
        }

        return best;
    }
}

/// <summary>Colour arithmetic for custom colours: perceptual distance, and whether a colour is too dark to read in chat.</summary>
public static class ColourMatch {
    /// <summary>
    /// A typical chat background: the chat log's dark, slightly see-through panel over the game, taken as dark grey
    /// (0x1E1E1E). Dalamud's windows are about as dark.
    /// </summary>
    public const uint TypicalChatBackground = 0x1E1E1E;

    /// <summary>
    /// Below this contrast ratio (WCAG's, 1 to 21) against <see cref="TypicalChatBackground"/>, a colour is hard to read
    /// in chat: very dark colours such as navy, maroon or dark grey. A warning only; the colour can still be used.
    /// </summary>
    public const double MinimumContrast = 2.0;

    /// <summary>The perceptual distance between two colours (0xRRGGBB): ΔE*76, about 2.3 being just noticeable.</summary>
    public static double Distance(uint rgbA, uint rgbB) => Math.Sqrt(Lab.DistanceSquared(Lab.Of(rgbA), Lab.Of(rgbB)));

    /// <summary>WCAG's contrast ratio between two colours (0xRRGGBB), from 1 (the same) to 21 (black and white).</summary>
    public static double Contrast(uint rgbA, uint rgbB) {
        var a = RelativeLuminance(rgbA);
        var b = RelativeLuminance(rgbB);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>Whether a colour (0xRRGGBB) is hard to read on a typical chat background (see <see cref="MinimumContrast"/>).</summary>
    public static bool HardToRead(uint rgb) => Contrast(rgb, TypicalChatBackground) < MinimumContrast;

    /// <summary>WCAG's relative luminance of an sRGB colour (0xRRGGBB), from 0 (black) to 1 (white).</summary>
    public static double RelativeLuminance(uint rgb) {
        var (r, g, b) = Lab.Linear(rgb);
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }
}

/// <summary>A colour in CIELAB (D65 white), from sRGB.</summary>
internal readonly record struct Lab(double L, double A, double B) {
    // D65 reference white.
    private const double Xn = 0.95047;
    private const double Yn = 1.0;
    private const double Zn = 1.08883;

    public static Lab Of(uint rgb) {
        var (r, g, b) = Linear(rgb);
        var x = (0.4124564 * r + 0.3575761 * g + 0.1804375 * b) / Xn;
        var y = (0.2126729 * r + 0.7151522 * g + 0.0721750 * b) / Yn;
        var z = (0.0193339 * r + 0.1191920 * g + 0.9503041 * b) / Zn;
        var (fx, fy, fz) = (F(x), F(y), F(z));
        return new Lab(116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    public static double DistanceSquared(Lab a, Lab b) {
        var (dl, da, db) = (a.L - b.L, a.A - b.A, a.B - b.B);
        return dl * dl + da * da + db * db;
    }

    /// <summary>The colour's channels as linear light, 0 to 1 (sRGB's transfer function undone).</summary>
    public static (double R, double G, double B) Linear(uint rgb) =>
        (ToLinear((rgb >> 16) & 0xFF), ToLinear((rgb >> 8) & 0xFF), ToLinear(rgb & 0xFF));

    private static double ToLinear(uint channel) {
        var c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static double F(double t) {
        const double delta = 6.0 / 29;
        return t > delta * delta * delta ? Math.Cbrt(t) : t / (3 * delta * delta) + 4.0 / 29;
    }
}
