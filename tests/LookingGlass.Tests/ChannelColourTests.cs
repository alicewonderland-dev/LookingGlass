using System.Numerics;
using System.Text.Json;
using LookingGlass.Core.Client;
using LookingGlass.Core.Util;
using static LookingGlass.Tests.CommandSlotTests;

namespace LookingGlass.Tests;

/// <summary>
/// Per-channel colours (rows of the game's UIColor sheet, or custom colours), kept per character like nicknames and
/// dropped by the same rule; and the conversion of the sheet's packed colours for the UI.
/// </summary>
public sealed class ChannelColourTests {
    private static readonly ChannelColour Green = ChannelColour.OfRow(45);
    private static readonly ChannelColour Orange = ChannelColour.OfRow(500);
    private static readonly ChannelColour Teal = ChannelColour.Custom(0x33DDAA);

    [Fact]
    public void ColoursAreSetChangedAndCleared() {
        var rows = new Dictionary<string, ushort>();
        var custom = new Dictionary<string, uint>();

        ChannelColours.Set(rows, custom, "aaa", Green);
        ChannelColours.Set(rows, custom, "bbb", Green);
        Assert.Equal(Green, ChannelColours.Of(rows, custom, "aaa"));
        Assert.Equal(Green, ChannelColours.Of(rows, custom, "bbb"));
        Assert.Null(ChannelColours.Of(rows, custom, "ccc"));

        ChannelColours.Set(rows, custom, "aaa", Orange);
        Assert.Equal(Orange, ChannelColours.Of(rows, custom, "aaa"));

        // Default: null, or row 0 (which has no colour).
        ChannelColours.Set(rows, custom, "aaa", null);
        ChannelColours.Set(rows, custom, "bbb", ChannelColour.OfRow(0));
        ChannelColours.Set(rows, custom, "ccc", null);
        Assert.Empty(rows);
        Assert.Empty(custom);
    }

    [Fact]
    public void ACustomColourReplacesARowAndARowACustomColour() {
        var rows = new Dictionary<string, ushort>();
        var custom = new Dictionary<string, uint>();

        ChannelColours.Set(rows, custom, "aaa", Green);
        ChannelColours.Set(rows, custom, "aaa", Teal);
        Assert.Equal(Teal, ChannelColours.Of(rows, custom, "aaa"));
        Assert.Empty(rows);
        Assert.Equal(new Dictionary<string, uint> { ["aaa"] = 0x33DDAA }, custom);

        ChannelColours.Set(rows, custom, "aaa", Orange);
        Assert.Equal(Orange, ChannelColours.Of(rows, custom, "aaa"));
        Assert.Empty(custom);

        ChannelColours.Set(rows, custom, "aaa", Teal);
        ChannelColours.Set(rows, custom, "aaa", null);
        Assert.Empty(rows);
        Assert.Empty(custom);
    }

    [Fact]
    public void ACustomColourIsAnyRgbValueAndNothingMore() {
        Assert.Equal(0x123456u, ChannelColour.Custom(0x123456).Rgb);
        Assert.True(ChannelColour.Custom(0).IsCustom);
        Assert.Equal(0u, ChannelColour.Custom(0).Rgb);
        // A hand-edited value above 24 bits keeps only the colour.
        Assert.Equal(0xABCDEFu, ChannelColour.Custom(0xFFABCDEF).Rgb);
        Assert.Equal(ChannelColour.Custom(0xABCDEF), ChannelColours.Of(new Dictionary<string, ushort>(), new Dictionary<string, uint> { ["aaa"] = 0xFFABCDEF }, "aaa"));
        // A custom colour and a row are never equal, even black and row 0.
        Assert.NotEqual(ChannelColour.Custom(0), ChannelColour.OfRow(0));
        Assert.Equal("#33DDAA", Teal.ToString());
    }

    [Fact]
    public void SettingsSavedBeforeCustomColoursReadTheSame() {
        // A character's settings from before custom colours: rows only, and no custom map at all.
        var saved = JsonSerializer.Deserialize<SavedColours>("""{ "ChannelColours": { "aaa": 45, "bbb": 500 } }""")!;
        Assert.Null(saved.CustomChannelColours);

        Assert.Equal(Green, ChannelColours.Of(saved.ChannelColours, saved.CustomChannelColours, "aaa"));
        Assert.Equal(Orange, ChannelColours.Of(saved.ChannelColours, saved.CustomChannelColours, "bbb"));
        Assert.Null(ChannelColours.Of(saved.ChannelColours, saved.CustomChannelColours, "ccc"));
        var merged = ChannelColours.Merge(saved.ChannelColours, saved.CustomChannelColours);
        Assert.Equal(2, merged.Count);
        Assert.Equal(Green, ChannelColours.Of(merged, "aaa"));
        Assert.Equal(Orange, ChannelColours.Of(merged, "bbb"));
    }

    [Fact]
    public void CustomColoursSurviveSavingAndLoading() {
        var custom = new Dictionary<string, uint>();
        var saved = new SavedColours { CustomChannelColours = custom };
        ChannelColours.Set(saved.ChannelColours, custom, "aaa", Green);
        ChannelColours.Set(saved.ChannelColours, custom, "bbb", Teal);
        ChannelColours.Set(saved.ChannelColours, custom, "ccc", ChannelColour.Custom(0));

        var loaded = JsonSerializer.Deserialize<SavedColours>(JsonSerializer.Serialize(saved))!;

        var merged = ChannelColours.Merge(loaded.ChannelColours, loaded.CustomChannelColours);
        Assert.Equal(3, merged.Count);
        Assert.Equal(Green, merged["aaa"]);
        Assert.Equal(Teal, merged["bbb"]);
        Assert.Equal(ChannelColour.Custom(0), merged["ccc"]);
        // The rows stay where an older version of the plugin reads them; the custom colours are a map of their own.
        Assert.Equal(new Dictionary<string, ushort> { ["aaa"] = 45 }, loaded.ChannelColours);
    }

    [Fact]
    public void AHandEditedFileWithBothKeepsTheCustomColour() {
        var rows = new Dictionary<string, ushort> { ["aaa"] = 45, ["bbb"] = 0 };
        var custom = new Dictionary<string, uint> { ["aaa"] = 0x33DDAA };

        Assert.Equal(Teal, ChannelColours.Of(rows, custom, "aaa"));
        // Row 0 has no colour.
        Assert.Null(ChannelColours.Of(rows, custom, "bbb"));
        var merged = ChannelColours.Merge(rows, custom);
        Assert.Equal(Teal, merged["aaa"]);
        Assert.False(merged.ContainsKey("bbb"));

        // Setting it again leaves it in one map only.
        ChannelColours.Set(rows, custom, "aaa", Green);
        Assert.Equal(Green, ChannelColours.Of(rows, custom, "aaa"));
        Assert.Empty(custom);
    }

    [Fact]
    public void ColoursSurviveARestartAndAreOnlyRemovedWhenTheirChannelIsGone() {
        var rows = new Dictionary<string, ushort> { ["aaa"] = 45, ["bbb"] = 500, ["ccc"] = 37 };
        var custom = new Dictionary<string, uint> { ["eee"] = 0x33DDAA, ["fff"] = 0xFF66CC };
        var savedRows = new Dictionary<string, ushort>(rows);
        var savedCustom = new Dictionary<string, uint>(custom);

        // What a restart publishes before the list is in: Ready with no channels, then some of them.
        Assert.False(ChannelColours.Sync(rows, custom, Snapshot(ConnectionState.Ready, false)));
        Assert.False(ChannelColours.Sync(rows, custom, Snapshot(ConnectionState.Ready, false, "ccc")));
        Assert.False(ChannelColours.Sync(rows, custom, Snapshot(ConnectionState.Reconnecting, true, "aaa")));
        Assert.False(ChannelColours.Sync(rows, custom, Snapshot(ConnectionState.Stopped, true)));
        Assert.Equal(savedRows, rows);
        Assert.Equal(savedCustom, custom);

        // The complete list, in another order and with a new channel: nothing changes (new channels get no colour).
        Assert.False(ChannelColours.Sync(rows, custom, Snapshot(ConnectionState.Ready, true, "ddd", "fff", "ccc", "eee", "bbb", "aaa")));
        Assert.Equal(savedRows, rows);
        Assert.Equal(savedCustom, custom);

        // Leaving (or being removed from) a channel drops its colour, a row or a custom one.
        Assert.True(ChannelColours.Sync(rows, custom, Snapshot(ConnectionState.Ready, true, "ddd", "ccc", "aaa", "fff")));
        Assert.Equal(new Dictionary<string, ushort> { ["aaa"] = 45, ["ccc"] = 37 }, rows);
        Assert.Equal(new Dictionary<string, uint> { ["fff"] = 0xFF66CC }, custom);

        // Only a custom colour gone is a change too.
        Assert.True(ChannelColours.Sync(rows, custom, Snapshot(ConnectionState.Ready, true, "aaa", "ccc")));
        Assert.Empty(custom);

        Assert.True(ChannelColours.Sync(rows, custom, Snapshot(ConnectionState.Ready, true)));
        Assert.Empty(rows);
    }

    // Values read from the game's UIColor sheet (its Dark column) with Lumina: the alpha is the low byte.
    [Theory]
    [InlineData(0x0099FFFFu, 0x00, 0x99, 0xFF, 0xFF)] // row 37, the [LGC] tag's default: azure
    [InlineData(0xDC0000FFu, 0xDC, 0x00, 0x00, 0xFF)] // row 17, warnings: red
    [InlineData(0x00CC22FFu, 0x00, 0xCC, 0x22, 0xFF)] // row 45: green
    [InlineData(0xFFFFFFFFu, 0xFF, 0xFF, 0xFF, 0xFF)] // row 1: white
    [InlineData(0xE1C500EEu, 0xE1, 0xC5, 0x00, 0xEE)] // row 514: the one row not fully opaque
    public void SheetColoursArePackedRedGreenBlueAlpha(uint packed, int r, int g, int b, int a) {
        Assert.Equal(new Vector4(r / 255f, g / 255f, b / 255f, a / 255f), UiColorPacking.ToVector4(packed));
    }

    [Fact]
    public void SheetColoursConvertToImGuisPackedOrder() {
        // ImGui packs a colour as 0xAABBGGRR.
        Assert.Equal(0xFFFF9900u, UiColorPacking.ToImGui(0x0099FFFFu));
        Assert.Equal(0xEE00C5E1u, UiColorPacking.ToImGui(0xE1C500EEu));
    }

    /// <summary>The two colour maps of a character's saved settings (CharacterSettings in the plugin), by the same names.</summary>
    private sealed class SavedColours {
        public Dictionary<string, ushort> ChannelColours { get; set; } = new();

        public Dictionary<string, uint>? CustomChannelColours { get; set; }
    }
}
