using LookingGlass.Core.Client;
using LookingGlass.Core.Util;
using static LookingGlass.Core.Client.TextPart;

namespace LookingGlass.Tests;

/// <summary>
/// Custom channel colours (see "Channel numbers, nicknames and colours" in docs/design.md): colour codes, the closest game
/// colour under a custom one, the warning for colours too dark to read, the layered formatting every coloured line of game
/// text gets, and the colour test. How the game and ChatTwo show them is checked in game
/// (docs/testing/custom-colours-checklist.md).
/// </summary>
public sealed class CustomColourTests {
    // ================================================================ colour codes

    [Theory]
    [InlineData("#3FA7D6", 0x3FA7D6u)]
    [InlineData("3FA7D6", 0x3FA7D6u)]
    [InlineData("#3fa7d6", 0x3FA7D6u)]
    [InlineData("  #3Fa7D6 ", 0x3FA7D6u)]
    [InlineData("#000000", 0x000000u)]
    [InlineData("#FFFFFF", 0xFFFFFFu)]
    public void ColourCodesAreRead(string text, uint rgb) {
        Assert.True(HexColour.TryParse(text, out var read));
        Assert.Equal(rgb, read);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("#FFF")]
    [InlineData("#3FA7D")]
    [InlineData("#3FA7D6F")]
    [InlineData("#3FA7D6FF")]
    [InlineData("##3FA7D6")]
    [InlineData("#3FA7G6")]
    [InlineData("#3F A7D6")]
    [InlineData("0x3FA7D6")]
    [InlineData("+3FA7D6")]
    [InlineData("-3FA7D")]
    [InlineData("#３FA7D6")]
    [InlineData("rgb(1,2,3)")]
    public void AnythingButSixHexDigitsIsRefused(string? text) {
        Assert.False(HexColour.TryParse(text, out var read));
        Assert.Equal(0u, read);
    }

    [Fact]
    public void ColourCodesAreShownWithAHashInCapitals() {
        Assert.Equal("#3FA7D6", HexColour.Format(0x3FA7D6));
        Assert.Equal("#000000", HexColour.Format(0));
        Assert.Equal("#00000A", HexColour.Format(0xA));
        // Only the colour: anything above 24 bits is ignored.
        Assert.Equal("#ABCDEF", HexColour.Format(0xFFABCDEF));

        foreach (var rgb in new uint[] { 0, 1, 0x3FA7D6, 0x00FF00, 0xFFFFFF }) {
            Assert.True(HexColour.TryParse(HexColour.Format(rgb), out var read));
            Assert.Equal(rgb, read);
        }
    }

    // ================================================================ the closest game colour

    /// <summary>A small UIColor sheet, packed 0xRRGGBBAA as the real one is.</summary>
    private static readonly (ushort Row, uint Rgba)[] FakeSheet = [
        (0, 0xFF0000FF), // row 0 has no colour, whatever it holds
        (1, 0xFFFFFFFF),
        (2, 0x000000FF),
        (17, 0xDC0000FF),
        (37, 0x0099FFFF),
        (45, 0x00CC22FF),
        (50, 0x7F7F7FFF),
        (70, 0x00C000FF),
        (71, 0x40FF40FF),
        (514, 0xE1C500EE), // not fully opaque: never picked
        (600, 0xDC0000FF), // the same as 17
    ];

    [Fact]
    public void TheClosestRowIsFound() {
        var table = new UiColourTable(FakeSheet);
        Assert.Equal(9, table.Count);

        Assert.Equal((ushort) 37, table.Nearest(0x0099FF));
        Assert.Equal((ushort) 37, table.Nearest(0x10A0F0));
        Assert.Equal((ushort) 1, table.Nearest(0xF0F0F0));
        Assert.Equal((ushort) 2, table.Nearest(0x101010));
        Assert.Equal((ushort) 50, table.Nearest(0x808080));
        Assert.Equal((ushort) 45, table.Nearest(0x00CC22));
    }

    [Fact]
    public void RowZeroAndSeeThroughRowsAreNeverPickedAndTiesGoToTheLowestRow() {
        var table = new UiColourTable(FakeSheet);
        Assert.Equal((ushort) 17, table.Nearest(0xFF0000));
        Assert.Equal((ushort) 17, table.Nearest(0xDC0000));
        Assert.NotEqual((ushort) 514, table.Nearest(0xE1C500));

        // In any order.
        Assert.Equal((ushort) 17, new UiColourTable(FakeSheet.Reverse()).Nearest(0xDC0000));

        // The same row fully opaque is picked.
        Assert.Equal((ushort) 514, new UiColourTable([.. FakeSheet, (514, 0xE1C500FF)]).Nearest(0xE1C500));
    }

    [Fact]
    public void ClosestMeansClosestByEyeNotByNumbers() {
        // Pure green: a darker green is nearer in RGB (63 against 90), but a lighter one looks nearer, and is (ΔE 13 against 30).
        Assert.True(ColourMatch.Distance(0x00FF00, 0x40FF40) < ColourMatch.Distance(0x00FF00, 0x00C000));
        Assert.Equal((ushort) 71, new UiColourTable(FakeSheet).Nearest(0x00FF00));

        Assert.Equal(0, ColourMatch.Distance(0x3FA7D6, 0x3FA7D6), 6);
        Assert.Equal(100, ColourMatch.Distance(0x000000, 0xFFFFFF), 0);
    }

    [Fact]
    public void AnEmptySheetHasNoClosestRow() {
        Assert.Null(new UiColourTable([]).Nearest(0x3FA7D6));
        Assert.Null(new UiColourTable([(0, 0xFFFFFFFF), (514, 0xE1C500EE)]).Nearest(0x3FA7D6));
    }

    // ================================================================ readability

    [Theory]
    [InlineData(0x000000u)]
    [InlineData(0x1A2B6Du)]
    [InlineData(0x000080u)]
    [InlineData(0x333333u)]
    [InlineData(0x800000u)]
    [InlineData(0x4B0082u)]
    [InlineData(0x0000FFu)]
    public void VeryDarkColoursAreHardToRead(uint rgb) {
        Assert.True(ColourMatch.HardToRead(rgb));
    }

    [Theory]
    [InlineData(0xFFFFFFu)]
    [InlineData(0x0099FFu)]
    [InlineData(0xFF66CCu)]
    [InlineData(0x8844FFu)]
    [InlineData(0x33DDAAu)]
    [InlineData(0x555555u)]
    [InlineData(0xAE0000u)] // the critical warnings' dark red
    public void OtherColoursAreFine(uint rgb) {
        Assert.False(ColourMatch.HardToRead(rgb));
    }

    [Fact]
    public void ContrastIsWcags() {
        Assert.Equal(21, ColourMatch.Contrast(0x000000, 0xFFFFFF), 6);
        Assert.Equal(1, ColourMatch.Contrast(0x3FA7D6, 0x3FA7D6), 6);
        Assert.Equal(ColourMatch.Contrast(0x123456, 0xFEDCBA), ColourMatch.Contrast(0xFEDCBA, 0x123456), 9);
    }

    // ================================================================ layered formatting

    private static readonly ChannelColour Teal = ChannelColour.Custom(0x33DDAA);

    /// <summary>A stand-in for the closest-row lookup: every custom colour's closest row is 45.</summary>
    private static ushort? Nearest45(uint rgb) => 45;

    private static ushort? NoSheet(uint rgb) => null;

    private static void AssertParts(TextPart[] expected, IEnumerable<TextPart> actual) => Assert.Equal(expected, actual.ToArray());

    [Fact]
    public void ARowIsTheUiForegroundMacroAsAlways() {
        AssertParts([new ForegroundOn(45), new Text("hi"), new ForegroundOff()], ColouredText.Wrap(ChannelColour.OfRow(45), Nearest45, "hi"));
        // Row 0 has no colour: nothing is put on.
        AssertParts([new Text("hi")], ColouredText.Wrap(ChannelColour.OfRow(0), Nearest45, "hi"));
    }

    [Fact]
    public void ACustomColourIsLayeredOverTheClosestRow() {
        // UIForeground(closest) outside, Color(exact) inside, closed in the opposite order.
        AssertParts([new ForegroundOn(45), new ColourOn(0x33DDAA), new Text("hi"), new ColourOff(), new ForegroundOff()],
            ColouredText.Wrap(Teal, Nearest45, "hi"));

        // The closest row is asked for the custom colour.
        uint? asked = null;
        _ = ColouredText.Wrap(Teal, rgb => {
            asked = rgb;
            return 45;
        }, "hi").ToList();
        Assert.Equal(0x33DDAAu, asked);
    }

    [Fact]
    public void WithNoSheetACustomColourIsTheExactColourAlone() {
        AssertParts([new ColourOn(0x33DDAA), new Text("hi"), new ColourOff()], ColouredText.Wrap(Teal, NoSheet, "hi"));
    }

    [Fact]
    public void AMessageInACustomColour() {
        // The whole line coloured: the tag, then the sender and the message, each layered.
        AssertParts([
            new ForegroundOn(45), new ColourOn(0x33DDAA), new Text("[sky]"), new ColourOff(), new ForegroundOff(),
            new ForegroundOn(45), new ColourOn(0x33DDAA), new Text("<B@W> "), new Body(), new ColourOff(), new ForegroundOff(),
        ], ColouredText.Message("[sky]", Teal, null, colourWholeLine: true, Nearest45, "B", "W"));

        // Only the tag coloured.
        AssertParts([
            new ForegroundOn(45), new ColourOn(0x33DDAA), new Text("[sky]"), new ColourOff(), new ForegroundOff(),
            new Text("<B@W> "), new Body(),
        ], ColouredText.Message("[sky]", Teal, null, colourWholeLine: false, Nearest45, "B", "W"));

        // Caught up on: the time between the tag and the message, uncoloured.
        AssertParts([
            new ForegroundOn(45), new ColourOn(0x33DDAA), new Text("[sky]"), new ColourOff(), new ForegroundOff(),
            new Text("[Yesterday 21:04] "),
            new ForegroundOn(45), new ColourOn(0x33DDAA), new Text("<B@W> "), new Body(), new ColourOff(), new ForegroundOff(),
        ], ColouredText.Message("[sky]", Teal, "Yesterday 21:04", colourWholeLine: true, Nearest45, "B", "W"));
    }

    [Fact]
    public void AMessageInARowOrTheDefaultIsAsBefore() {
        AssertParts([new ForegroundOn(45), new Text("[sky]"), new ForegroundOff(), new ForegroundOn(45), new Text("<B@W> "), new Body(), new ForegroundOff()],
            ColouredText.Message("[sky]", ChannelColour.OfRow(45), null, colourWholeLine: true, Nearest45, "B", "W"));

        // The default: only the tag, in LookingGlass blue, whatever the setting.
        foreach (var wholeLine in new[] { true, false }) {
            AssertParts([new ForegroundOn(NoticeColours.Blue), new Text("[LGC3]"), new ForegroundOff(), new Text("<B@W> "), new Body()],
                ColouredText.Message("[LGC3]", null, null, wholeLine, Nearest45, "B", "W"));
        }
    }

    [Fact]
    public void ANoticeShowsATagInItsCustomColour() {
        AssertParts([
            new ForegroundOn(NoticeColours.Blue), new Text("[LookingGlass] "), new ForegroundOff(),
            new ForegroundOn(NoticeColours.Blue), new Text("Now talking in "), new ForegroundOff(),
            new ForegroundOn(45), new ColourOn(0x33DDAA), new Text("[sky]"), new ColourOff(), new ForegroundOff(),
            new ForegroundOn(NoticeColours.Blue), new Text("."), new ForegroundOff(),
        ], ColouredText.Notice(NoticeTone.Info, "Now talking in [sky].", "[sky]", Teal, Nearest45));

        // Without a tag in it: the prefix and the text in the tone's colour.
        AssertParts([
            new ForegroundOn(NoticeColours.Blue), new Text("[LookingGlass] "), new ForegroundOff(),
            new ForegroundOn(NoticeColours.LightRed), new Text("Careful."), new ForegroundOff(),
        ], ColouredText.Notice(NoticeTone.Warning, "Careful.", null, Teal, Nearest45));

        // The tag at the very start or end: no empty pieces; a default tag in LookingGlass blue.
        AssertParts([
            new ForegroundOn(NoticeColours.Blue), new Text("[LookingGlass] "), new ForegroundOff(),
            new ForegroundOn(NoticeColours.Blue), new Text("[sky]"), new ForegroundOff(),
        ], ColouredText.Notice(NoticeTone.Critical, "[sky]", "[sky]", null, Nearest45));
    }

    [Fact]
    public void TheInfoBarAndTheGameMenuAreLayeredToo() {
        AssertParts([new ForegroundOn(45), new ColourOn(0x33DDAA), new Text("LG [sky]"), new ColourOff(), new ForegroundOff()],
            ColouredText.InfoBar("[sky]", Teal, Nearest45));
        AssertParts([new ForegroundOn(NoticeColours.Blue), new Text("LG [LGC]"), new ForegroundOff()], ColouredText.InfoBar("[LGC]", null, Nearest45));

        var offer = new InviteOffer("sky", "[sky]", Teal, "Sky pirates", "already a member");
        AssertParts([
            new ForegroundOn(45), new ColourOn(0x33DDAA), new Text("[sky]"), new ColourOff(), new ForegroundOff(),
            new Text(" Sky pirates (already a member)"),
        ], ColouredText.MenuEntry(offer, Nearest45));
        AssertParts([new ForegroundOn(NoticeColours.Blue), new Text("[sky]"), new ForegroundOff(), new Text(" Sky pirates")],
            ColouredText.MenuEntry(offer with { Colour = null, Unavailable = null }, Nearest45));
    }

    [Fact]
    public void EveryColourPushedIsPoppedInTheOppositeOrder() {
        var lines = new List<IReadOnlyList<TextPart>> {
            ColouredText.Message("[sky]", Teal, "12:00", true, Nearest45, "B", "W"),
            ColouredText.Message("[sky]", Teal, null, false, NoSheet, "B", "W"),
            ColouredText.Notice(NoticeTone.Info, "a [sky] b", "[sky]", Teal, Nearest45),
            ColouredText.InfoBar("[sky]", Teal, NoSheet),
        };
        lines.AddRange(ColouredText.Samples(0x33DDAA, Nearest45));
        lines.AddRange(ColouredText.Samples(0x33DDAA, NoSheet));
        foreach (var line in lines) {
            var open = new Stack<string>();
            foreach (var part in line) {
                switch (part) {
                    case ForegroundOn:
                        open.Push("foreground");
                        break;
                    case ColourOn:
                        open.Push("colour");
                        break;
                    case ForegroundOff:
                        Assert.Equal("foreground", open.Pop());
                        break;
                    case ColourOff:
                        Assert.Equal("colour", open.Pop());
                        break;
                }
            }

            Assert.Empty(open);
        }
    }

    // ================================================================ the colour test

    [Fact]
    public void TheColourTestShowsLayeredRowOnlyAndExactOnly() {
        var lines = ColouredText.Samples(0xFF66CC, Nearest45);
        Assert.Equal(3, lines.Count);
        var prefix = new TextPart[] { new ForegroundOn(NoticeColours.Blue), new Text(ColouredText.NoticePrefix), new ForegroundOff() };

        AssertParts([.. prefix, new ForegroundOn(45), new ColourOn(0xFF66CC), new Text("#FF66CC exact, game colour 45 under it"), new ColourOff(), new ForegroundOff()],
            lines[0]);
        AssertParts([.. prefix, new ForegroundOn(45), new Text("#FF66CC closest game colour 45 only"), new ForegroundOff()], lines[1]);
        AssertParts([.. prefix, new ColourOn(0xFF66CC), new Text("#FF66CC exact only, nothing under it"), new ColourOff()], lines[2]);

        // With no sheet, the second line says so, uncoloured.
        AssertParts([.. prefix, new Text("#FF66CC: no game colour found")], ColouredText.Samples(0xFF66CC, NoSheet)[1]);
    }

    [Fact]
    public void TheColourTestTakesColourCodesOrFiveSamples() {
        Assert.Equal(ColouredText.TestColours, ColouredText.TestColoursFrom(""));
        Assert.Equal(ColouredText.TestColours, ColouredText.TestColoursFrom("   "));
        Assert.Equal(5, ColouredText.TestColours.Count);
        Assert.Contains(ColouredText.TestColours, ColourMatch.HardToRead);

        Assert.Equal([0xFF66CCu, 0x33DDAAu, 0x123456u], ColouredText.TestColoursFrom("#FF66CC 33ddaa, #123456"));
        Assert.Null(ColouredText.TestColoursFrom("#FF66CC #nope"));
        Assert.Equal(8, ColouredText.TestColoursFrom(string.Join(' ', Enumerable.Repeat("#FFFFFF", 12)))!.Count);
    }

    // ================================================================ the picker's words

    [Fact]
    public void ThePickersWordsArePlain() {
        foreach (var text in ColourWords.All()) {
            PlainLanguage.AssertPlain(text);
        }

        Assert.Equal("This channel's custom colour, #3FA7D6. Click to change it.", ColourWords.CurrentCustom(0x3FA7D6));
        // The example code in the words is one the field accepts.
        Assert.True(HexColour.TryParse("#3FA7D6", out _));
    }
}
