using System.Numerics;
using LookingGlass.Core.Client;
using LookingGlass.Core.Util;
using Lumina.Excel.Sheets;

namespace LookingGlass.Plugin.Ui;

/// <summary>
/// Channel colours are rows of the game's UIColor sheet, so chat shows them natively, or custom colours (any RGB; see
/// <see cref="ChannelColour"/>). The picker offers a curated set of distinct rows that read well on the chat log, in hue
/// order; the swatches use each row's colour from the sheet itself (its Dark column, as Dalamud's windows are), and a
/// custom colour is shown exactly wherever ImGui draws it.
/// </summary>
internal static class ChannelPalette {
    public const int Columns = 8;

    /// <summary>Five rows of eight: reds and pinks, oranges and yellows, greens, cyans and blues, purples and light neutrals.</summary>
    private static readonly ushort[] Rows = [
        12, 539, 15, 16, 524, 561, 578, 537,
        500, 32, 557, 527, 28, 31, 559, 25,
        62, 61, 43, 42, 60, 45, 575, 72,
        40, 58, 34, 576, 35, 57, 542, 553,
        56, 48, 555, 541, 522, 1, 2, 8,
    ];

    private static readonly Dictionary<ushort, Vector4?> Cache = new();
    private static IReadOnlyList<(ushort Row, Vector4 Colour)>? _swatches;

    /// <summary>The whole UIColor sheet (Dark column), for the closest row to a custom colour; null if it couldn't be read.</summary>
    private static readonly Lazy<UiColourTable?> Table = new(LoadTable, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The picker's colours, skipping any row the sheet doesn't have. Draw thread only.</summary>
    public static IReadOnlyList<(ushort Row, Vector4 Colour)> Swatches {
        get {
            if (_swatches == null) {
                var swatches = new List<(ushort, Vector4)>();
                foreach (var row in Rows) {
                    if (ColourOf(row) is { } colour) {
                        swatches.Add((row, colour));
                    }
                }

                _swatches = swatches;
            }

            return _swatches;
        }
    }

    /// <summary>The colour of a UIColor row as ImGui draws it, or null if the sheet has no such row. Draw thread only.</summary>
    public static Vector4? ColourOf(ushort row) {
        if (!Cache.TryGetValue(row, out var colour)) {
            try {
                colour = Services.Data.GetExcelSheet<UIColor>().GetRowOrDefault(row) is { } sheetRow
                    ? UiColorPacking.ToVector4(sheetRow.Dark)
                    : null;
            } catch (Exception ex) {
                Services.Log.Warning(ex, $"Couldn't read UIColor row {row}");
                colour = null;
            }

            Cache[row] = colour;
        }

        return colour;
    }

    /// <summary>A channel's colour as ImGui draws it: a custom colour exactly, a row from the sheet. Draw thread only.</summary>
    public static Vector4? ColourOf(ChannelColour colour) => colour.IsCustom ? OfRgb(colour.Rgb) : ColourOf(colour.Row);

    /// <summary>The colour chat uses for a channel: its own, or the default tag colour.</summary>
    public static Vector4? ChatColourOf(ChannelColour? colour) => ColourOf(colour ?? ColouredText.DefaultTag);

    /// <summary>A colour (0xRRGGBB) as ImGui draws it, fully opaque.</summary>
    public static Vector4 OfRgb(uint rgb) => UiColorPacking.ToVector4(((rgb & 0xFFFFFF) << 8) | 0xFF);

    /// <summary>A colour as 0xRRGGBB, from ImGui's (alpha ignored).</summary>
    public static uint RgbOf(Vector3 colour) =>
        (Byte(colour.X) << 16) | (Byte(colour.Y) << 8) | Byte(colour.Z);

    /// <summary>The UIColor row closest to a custom colour (0xRRGGBB), or null if the sheet couldn't be read. Any thread.</summary>
    public static ushort? Nearest(uint rgb) => Table.Value?.Nearest(rgb);

    /// <summary>The colour a channel's colour is packed as in UIColor rows, 0xRRGGBBAA (for ChatTwo), or null if unknown.</summary>
    public static uint? RgbaOf(ChannelColour colour) {
        if (colour.IsCustom) {
            return (colour.Rgb << 8) | 0xFF;
        }

        try {
            return Services.Data.GetExcelSheet<UIColor>().GetRowOrDefault(colour.Row) is { } row && row.Dark != 0 ? row.Dark : null;
        } catch (Exception ex) {
            Services.Log.Warning(ex, $"Couldn't read UIColor row {colour.Row}");
            return null;
        }
    }

    private static uint Byte(float channel) => (uint) Math.Clamp(MathF.Round(channel * 255), 0, 255);

    private static UiColourTable? LoadTable() {
        try {
            var table = new UiColourTable(Services.Data.GetExcelSheet<UIColor>().Where(row => row.RowId <= ushort.MaxValue).Select(row => ((ushort) row.RowId, row.Dark)));
            return table.Count > 0 ? table : null;
        } catch (Exception ex) {
            Services.Log.Warning(ex, "Couldn't read the UIColor sheet; custom colours have no closest game colour under them");
            return null;
        }
    }
}
