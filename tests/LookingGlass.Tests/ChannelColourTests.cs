using System.Numerics;
using LookingGlass.Core.Client;
using LookingGlass.Core.Util;
using static LookingGlass.Tests.CommandSlotTests;

namespace LookingGlass.Tests;

/// <summary>
/// Per-channel colours (rows of the game's UIColor sheet), kept per character like nicknames and
/// dropped by the same rule; and the conversion of the sheet's packed colours for the UI.
/// </summary>
public sealed class ChannelColourTests {
    [Fact]
    public void ColoursAreSetChangedAndCleared() {
        var colours = new Dictionary<string, ushort>();

        ChannelColours.Set(colours, "aaa", 45);
        ChannelColours.Set(colours, "bbb", 45);
        Assert.Equal((ushort) 45, ChannelColours.Of(colours, "aaa"));
        Assert.Equal((ushort) 45, ChannelColours.Of(colours, "bbb"));
        Assert.Null(ChannelColours.Of(colours, "ccc"));

        ChannelColours.Set(colours, "aaa", 500);
        Assert.Equal((ushort) 500, ChannelColours.Of(colours, "aaa"));

        // Default: null, or row 0 (which has no colour).
        ChannelColours.Set(colours, "aaa", null);
        ChannelColours.Set(colours, "bbb", 0);
        ChannelColours.Set(colours, "ccc", null);
        Assert.Empty(colours);
    }

    [Fact]
    public void ColoursSurviveARestartAndAreOnlyRemovedWhenTheirChannelIsGone() {
        var colours = new Dictionary<string, ushort> { ["aaa"] = 45, ["bbb"] = 500, ["ccc"] = 37 };
        var saved = new Dictionary<string, ushort>(colours);

        // What a restart publishes before the list is in: Ready with no channels, then some of them.
        Assert.False(ChannelColours.Sync(colours, Snapshot(ConnectionState.Ready, false)));
        Assert.False(ChannelColours.Sync(colours, Snapshot(ConnectionState.Ready, false, "ccc")));
        Assert.False(ChannelColours.Sync(colours, Snapshot(ConnectionState.Reconnecting, true, "aaa")));
        Assert.False(ChannelColours.Sync(colours, Snapshot(ConnectionState.Stopped, true)));
        Assert.Equal(saved, colours);

        // The complete list, in another order and with a new channel: nothing changes (new channels get no colour).
        Assert.False(ChannelColours.Sync(colours, Snapshot(ConnectionState.Ready, true, "ddd", "ccc", "bbb", "aaa")));
        Assert.Equal(saved, colours);

        // Leaving (or being removed from) a channel drops its colour.
        Assert.True(ChannelColours.Sync(colours, Snapshot(ConnectionState.Ready, true, "ddd", "ccc", "aaa")));
        Assert.Equal(new Dictionary<string, ushort> { ["aaa"] = 45, ["ccc"] = 37 }, colours);

        Assert.True(ChannelColours.Sync(colours, Snapshot(ConnectionState.Ready, true)));
        Assert.Empty(colours);
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
}
