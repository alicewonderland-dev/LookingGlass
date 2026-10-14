using LookingGlass.Core.Client;
using LookingGlass.Core.Util;

namespace LookingGlass.Tests;

/// <summary>
/// The colour fields LookingGlass sends ChatTwo on <c>ExtraChat.OverrideChannelColour</c> (see "The label" under ChatTwo in
/// docs/design.md): the row and its 0xRRGGBBAA colour, as ExtraChat sent them, for the default colour, a game colour, a
/// custom one and local chat's. How ChatTwo shows them is checked in game (custom-colours-checklist.md 12,
/// local-chat-checklist.md 42).
/// </summary>
public sealed class ChatTwoOverrideColourTests {
    private const ushort Swatch = 12;
    private const ushort PinkRow = 561;

    /// <summary>A stand-in for the UIColor sheet's Dark column: row 37 as the real sheet has it, the others made up.</summary>
    private static readonly Dictionary<ushort, uint> Sheet = new() {
        [NoticeColours.Blue] = 0x0099FFFF,
        [Swatch] = 0xE63838FF,
        [PinkRow] = 0xFF80C0FF,
        [600] = 0x123456FF,
    };

    private static readonly UiColourTable Table = new(Sheet.Select(row => (row.Key, row.Value)));

    private static uint? RowRgba(ushort row) => Sheet.TryGetValue(row, out var rgba) ? rgba : null;

    private static ChatTwoOverrideColour Of(ChannelColour? colour) => ChatTwoOverrideColour.Of(colour, RowRgba, Table.Nearest);

    [Fact]
    public void TheDefaultIsLookingGlassBlue() {
        var sent = Of(null);
        Assert.Equal(NoticeColours.Blue, sent.UiColour);
        Assert.Equal(0x0099FFFFu, sent.Rgba);
        Assert.Equal(Of(ColouredText.DefaultTag), sent);
    }

    [Fact]
    public void AGameColourIsItsRowAndTheSheetsColour() {
        var sent = Of(ChannelColour.OfRow(Swatch));
        Assert.Equal(Swatch, sent.UiColour);
        Assert.Equal(0xE63838FFu, sent.Rgba);
    }

    [Fact]
    public void ACustomColourIsExactWithTheClosestRow() {
        var sent = Of(ChannelColour.Custom(0xFF66CC));
        Assert.Equal(0xFF66CCFFu, sent.Rgba);
        Assert.Equal(PinkRow, sent.UiColour);
    }

    [Fact]
    public void ACustomColourWithNoClosestRowStillGoesExactly() {
        var sent = ChatTwoOverrideColour.Of(ChannelColour.Custom(0xFF66CC), RowRgba, _ => null);
        Assert.Equal(new ChatTwoOverrideColour(0, 0xFF66CCFF), sent);
    }

    [Fact]
    public void TheBytesAreRedGreenBlueAlpha() {
        // ChatTwo reverses the bytes for ImGui (ColourUtil.RgbaToAbgr), so red must be the high byte and alpha the low one.
        var rgba = Of(ChannelColour.Custom(0xFF66CC)).Rgba;
        Assert.Equal(0xFFu, rgba >> 24);
        Assert.Equal(0x66u, (rgba >> 16) & 0xFF);
        Assert.Equal(0xCCu, (rgba >> 8) & 0xFF);
        Assert.Equal(0xFFu, rgba & 0xFF);
        Assert.Equal(0xFFCC66FFu, UiColorPacking.ToImGui(rgba));
        Assert.Equal(0x000000FFu, ChatTwoOverrideColour.Opaque(0));
        Assert.Equal(0x123456FFu, ChatTwoOverrideColour.Opaque(0xAB123456));
    }

    [Fact]
    public void LocalChatsDefaultIsTheDefault() {
        Assert.Null(LocalChat.ColourOf(0, null));
        Assert.Equal(new ChatTwoOverrideColour(NoticeColours.Blue, 0x0099FFFF), Of(LocalChat.ColourOf(0, null)));
    }

    [Fact]
    public void LocalChatsOwnColoursGoAsAChannelsDo() {
        Assert.Equal(new ChatTwoOverrideColour(Swatch, 0xE63838FF), Of(LocalChat.ColourOf(Swatch, null)));
        Assert.Equal(new ChatTwoOverrideColour(PinkRow, 0xFF66CCFF), Of(LocalChat.ColourOf(Swatch, 0xFF66CC)));
    }

    [Fact]
    public void ARowTheSheetDoesntHaveIsOpaqueWhiteNotInvisible() {
        // ChatTwo paints whatever Rgba says on the typed text: 0 would be transparent.
        Assert.Equal(new ChatTwoOverrideColour(999, ChatTwoOverrideColour.White), Of(ChannelColour.OfRow(999)));
        Assert.Equal(new ChatTwoOverrideColour(37, ChatTwoOverrideColour.White), ChatTwoOverrideColour.Of(null, _ => 0, Table.Nearest));
    }

    [Fact]
    public void ARowThatIsntOpaqueIsSentOpaque() {
        Assert.Equal(0x0099FFFFu, ChatTwoOverrideColour.Of(null, _ => 0x0099FF00, Table.Nearest).Rgba);
    }

    [Fact]
    public void NoOverrideIsOpaqueWhite() {
        Assert.Equal(new ChatTwoOverrideColour(0, 0xFFFFFFFF), ChatTwoOverrideColour.None);
    }
}
