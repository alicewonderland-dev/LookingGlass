using System.Numerics;
using LookingGlass.Core.Util;
using Lumina.Excel.Sheets;

namespace LookingGlass.Plugin.Ui;

/// <summary>
/// Channel colours are rows of the game's UIColor sheet, so chat shows them natively. The picker
/// offers a curated set of distinct colours that read well on the chat log, in hue order; the
/// swatches use each row's colour from the sheet itself (its Dark column, as Dalamud's windows are).
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

    /// <summary>The colour chat uses for a channel: its own, or the default tag colour.</summary>
    public static Vector4? ChatColourOf(ushort? row) => ColourOf(row ?? ChatOutput.TagColour);
}
