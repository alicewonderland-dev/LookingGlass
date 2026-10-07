using LookingGlass.Core.Client;
using LookingGlass.Core.Util;
using static LookingGlass.Core.Client.TextPart;

namespace LookingGlass.Tests;

/// <summary>
/// Name colours (see "Name colours" in docs/design.md): one colour per person, kept on this computer, by who they are (their
/// user ID, which is their Lodestone ID), and put on their name alone in game chat lines, with the rest of the line as it
/// was. How the game and ChatTwo show them is checked in game (docs/testing/name-colours-checklist.md).
/// </summary>
public sealed class NameColourTests {
    private const string Server = "wss://lookingglass.example:8443/ws";
    private const string OtherServer = "wss://elsewhere.example/ws";
    private const long Alice = 12345678;
    private const long Bob = 87654321;
    private const uint Pink = 0xFF66CC;
    private const uint Gold = 0xFFAA00;

    private static readonly ChannelColour Teal = ChannelColour.Custom(0x33DDAA);

    /// <summary>Every custom colour's closest row is 45.</summary>
    private static ushort? Nearest45(uint rgb) => 45;

    // ================================================================ who a colour belongs to

    [Fact]
    public void APersonIsKeptByTheirLodestoneIdTheSameOnEveryServer() {
        Assert.Equal("12345678", NameColours.KeyOf(Alice, Server));
        // A Lodestone ID is the same character on every server: their colour follows them to another address.
        Assert.Equal(NameColours.KeyOf(Alice, Server), NameColours.KeyOf(Alice, OtherServer));
        Assert.NotEqual(NameColours.KeyOf(Alice, Server), NameColours.KeyOf(Bob, Server));
    }

    [Fact]
    public void ATestServersMadeUpAccountsAreKeptPerServer() {
        // Debug accounts have negative IDs, which each test server hands out on its own: -1 here isn't -1 there.
        Assert.NotEqual(NameColours.KeyOf(-1, Server), NameColours.KeyOf(-1, OtherServer));
        Assert.Equal(NameColours.KeyOf(-1, Server), NameColours.KeyOf(-1, Server));
        // Never the same as a real character's.
        Assert.NotEqual(NameColours.KeyOf(-1, Server), NameColours.KeyOf(1, Server));
        Assert.DoesNotContain(NameColours.KeyOf(-1, Server), new[] { NameColours.KeyOf(1, Server), "-1", "1" });
    }

    [Fact]
    public void NobodyKnownHasNoColour() {
        // User ID 0: a sender not known (a line from a chat log written without one).
        Assert.Null(NameColours.KeyOf(0, Server));
        var colours = new Dictionary<string, uint>();
        Assert.False(NameColours.Set(colours, 0, Server, Pink));
        Assert.Empty(colours);
        Assert.Null(NameColours.Of(colours, 0, Server));
    }

    [Fact]
    public void ColoursAreSetChangedAndCleared() {
        var colours = new Dictionary<string, uint>();

        Assert.True(NameColours.Set(colours, Alice, Server, Pink));
        Assert.True(NameColours.Set(colours, Bob, Server, Pink));
        Assert.Equal(Pink, NameColours.Of(colours, Alice, Server));
        Assert.Equal(Pink, NameColours.Of(colours, Bob, Server));
        Assert.Null(NameColours.Of(colours, 11111111, Server));

        Assert.True(NameColours.Set(colours, Alice, Server, Gold));
        Assert.Equal(Gold, NameColours.Of(colours, Alice, Server));
        // The same again changes nothing (nothing to save).
        Assert.False(NameColours.Set(colours, Alice, Server, Gold));

        // Back to the default: the entry goes.
        Assert.True(NameColours.Set(colours, Alice, Server, null));
        Assert.False(NameColours.Set(colours, Alice, Server, null));
        Assert.Null(NameColours.Of(colours, Alice, Server));
        Assert.Equal(new[] { "87654321" }, colours.Keys);
    }

    [Fact]
    public void AColourIsTheirsWhateverTheirNameIs() {
        // Kept by ID only: a new name, or a move to another world, keeps it.
        var colours = new Dictionary<string, uint>();
        NameColours.Set(colours, Alice, Server, Pink);
        Assert.Equal(Pink, NameColours.Of(colours, new LookingGlass.Protocol.User { UserId = Alice, Name = "Alice Renamed", WorldName = "Elsewhere" }, Server));
        Assert.Null(NameColours.Of(colours, null, Server));
    }

    [Fact]
    public void OnlyTheColourIsKept() {
        // Anything above 24 bits (as from a hand-edited settings file) is ignored, when set and when read.
        var colours = new Dictionary<string, uint>();
        NameColours.Set(colours, Alice, Server, 0xFFFF66CC);
        Assert.Equal(Pink, colours["12345678"]);
        colours["12345678"] = 0xAAFF66CC;
        Assert.Equal(Pink, NameColours.Of(colours, Alice, Server));
    }

    [Fact]
    public void SettingsSavedBeforeHaveNoNameColours() {
        // Settings saved before name colours have none (null reads as none).
        Assert.Null(NameColours.Of(null, Alice, Server));
    }

    // ================================================================ in game chat

    private static void AssertParts(TextPart[] expected, IEnumerable<TextPart> actual) => Assert.Equal(expected, actual.ToArray());

    /// <summary>
    /// A chat line as it was before name colours, when the plugin put the sender into the body itself: the parts of
    /// <see cref="ColouredText.Message"/> then, with the body's own two pieces (the sender's text, then the message) in its
    /// place. The plugin turns each part into one call of the game's text builder, in order (see GameText), so the same
    /// parts are the same bytes.
    /// </summary>
    private static TextPart[] Before(string sender, params TextPart[] then) =>
        then.SelectMany(part => part is Body ? new TextPart[] { new Text(sender), new Body() } : [part]).ToArray();

    [Fact]
    public void WithNoNameColourEveryLineIsAsBefore() {
        const string sender = "<Alice Liddell@Ultros> ";
        var tealTag = new TextPart[] { new ForegroundOn(45), new ColourOn(0x33DDAA), new Text("[sky]"), new ColourOff(), new ForegroundOff() };
        // A custom channel colour on the whole line.
        AssertParts(Before(sender, [.. tealTag, new ForegroundOn(45), new ColourOn(0x33DDAA), new Body(), new ColourOff(), new ForegroundOff()]),
            ColouredText.Message("[sky]", Teal, null, colourWholeLine: true, Nearest45, "Alice Liddell", "Ultros"));
        // Only on the tag.
        AssertParts(Before(sender, [.. tealTag, new Body()]),
            ColouredText.Message("[sky]", Teal, null, colourWholeLine: false, Nearest45, "Alice Liddell", "Ultros"));
        // Caught up on.
        AssertParts(Before(sender, [.. tealTag, new Text("[Yesterday 21:04] "), new ForegroundOn(45), new ColourOn(0x33DDAA), new Body(), new ColourOff(), new ForegroundOff()]),
            ColouredText.Message("[sky]", Teal, "Yesterday 21:04", colourWholeLine: true, Nearest45, "Alice Liddell", "Ultros"));
        // A row.
        AssertParts(Before(sender, [new ForegroundOn(45), new Text("[sky]"), new ForegroundOff(), new ForegroundOn(45), new Body(), new ForegroundOff()]),
            ColouredText.Message("[sky]", ChannelColour.OfRow(45), null, colourWholeLine: true, Nearest45, "Alice Liddell", "Ultros"));
        // The default, whatever the setting.
        foreach (var wholeLine in new[] { true, false }) {
            AssertParts(Before(sender, [new ForegroundOn(NoticeColours.Blue), new Text("[LGC3]"), new ForegroundOff(), new Body()]),
                ColouredText.Message("[LGC3]", null, null, wholeLine, Nearest45, "Alice Liddell", "Ultros", nameColour: null));
        }
    }

    [Fact]
    public void TheSenderIsCleanedAsBefore() {
        // Raw control bytes would become live game formatting: the sender is cleaned as it always was, with or without a colour.
        var parts = ColouredText.Message("[sky]", null, null, true, Nearest45, "Ali\u0002ce\nLiddell", "Ul\u0003tros");
        Assert.Contains(new Text($"<{TextSanitizer.Name("Ali\u0002ce\nLiddell")}@{TextSanitizer.Name("Ul\u0003tros")}> "), parts);
        Assert.Contains(new Text("<Alice Liddell@Ultros> "), parts);

        var coloured = ColouredText.Message("[sky]", null, null, true, Nearest45, "Ali\u0002ce\nLiddell", "Ul\u0003tros", Pink);
        Assert.Contains(new Text("Alice Liddell@Ultros"), coloured);
        Assert.DoesNotContain(coloured.OfType<Text>(), text => text.Value.Contains('\u0002') || text.Value.Contains('\u0003'));

        // Nothing known about them: as before.
        Assert.Contains(new Text("<@> "), ColouredText.Message("[sky]", null, null, true, Nearest45, null, null));
    }

    [Fact]
    public void OnlyTheNameTakesItsColourAndTheLineGoesOnInTheChannels() {
        // The whole line in the channel's custom colour: the channel's colour is closed before the name and opened again
        // after it, so the message (with any links in it) is in the channel's colour exactly as before, and nothing is nested.
        AssertParts([
            new ForegroundOn(45), new ColourOn(0x33DDAA), new Text("[sky]"), new ColourOff(), new ForegroundOff(),
            new ForegroundOn(45), new ColourOn(0x33DDAA), new Text("<"), new ColourOff(), new ForegroundOff(),
            new ForegroundOn(45), new ColourOn(Pink), new Text("Alice Liddell@Ultros"), new ColourOff(), new ForegroundOff(),
            new ForegroundOn(45), new ColourOn(0x33DDAA), new Text("> "), new Body(), new ColourOff(), new ForegroundOff(),
        ], ColouredText.Message("[sky]", Teal, null, colourWholeLine: true, Nearest45, "Alice Liddell", "Ultros", Pink));

        // In a row's colour.
        AssertParts([
            new ForegroundOn(45), new Text("[sky]"), new ForegroundOff(),
            new ForegroundOn(45), new Text("<"), new ForegroundOff(),
            new ForegroundOn(45), new ColourOn(Pink), new Text("Alice Liddell@Ultros"), new ColourOff(), new ForegroundOff(),
            new ForegroundOn(45), new Text("> "), new Body(), new ForegroundOff(),
        ], ColouredText.Message("[sky]", ChannelColour.OfRow(45), null, colourWholeLine: true, Nearest45, "Alice Liddell", "Ultros", Pink));
    }

    [Fact]
    public void WithOnlyTheTagColouredTheRestOfTheLineIsUncolouredButTheName() {
        var tag = new TextPart[] { new ForegroundOn(45), new ColourOn(0x33DDAA), new Text("[sky]"), new ColourOff(), new ForegroundOff() };
        var name = new TextPart[] { new ForegroundOn(45), new ColourOn(Pink), new Text("Alice Liddell@Ultros"), new ColourOff(), new ForegroundOff() };
        AssertParts([.. tag, new Text("<"), .. name, new Text("> "), new Body()],
            ColouredText.Message("[sky]", Teal, null, colourWholeLine: false, Nearest45, "Alice Liddell", "Ultros", Pink));

        // The default channel colour, and a caught-up line's time before the sender.
        AssertParts([
            new ForegroundOn(NoticeColours.Blue), new Text("[LGC3]"), new ForegroundOff(), new Text("[12:00] "),
            new Text("<"), .. name, new Text("> "), new Body(),
        ], ColouredText.Message("[LGC3]", null, "12:00", colourWholeLine: true, Nearest45, "Alice Liddell", "Ultros", Pink));
    }

    [Fact]
    public void WithNoSheetANameIsInTheExactColourAlone() {
        AssertParts([
            new ForegroundOn(NoticeColours.Blue), new Text("[LGC3]"), new ForegroundOff(),
            new Text("<"), new ColourOn(Pink), new Text("Alice Liddell@Ultros"), new ColourOff(), new Text("> "), new Body(),
        ], ColouredText.Message("[LGC3]", null, null, colourWholeLine: true, _ => null, "Alice Liddell", "Ultros", Pink));
    }

    [Fact]
    public void TheMessageIsInTheChannelsColourRightAfterTheName() {
        // Whatever the colours, the message (and any link in it) comes once, last, inside the channel's colour when the
        // whole line has it, and with the name's colour already closed.
        foreach (var channel in new ChannelColour?[] { Teal, ChannelColour.OfRow(45), null }) {
            foreach (var wholeLine in new[] { true, false }) {
                var parts = ColouredText.Message("[sky]", channel, null, wholeLine, Nearest45, "Alice Liddell", "Ultros", Pink);
                Assert.Single(parts.OfType<Body>());
                var open = new List<TextPart>();
                foreach (var part in parts) {
                    switch (part) {
                        case ForegroundOn or ColourOn:
                            open.Add(part);
                            break;
                        case ForegroundOff or ColourOff:
                            open.RemoveAt(open.Count - 1);
                            break;
                        case Body:
                            var expected = channel is { } c && wholeLine
                                ? c.IsCustom ? new TextPart[] { new ForegroundOn(45), new ColourOn(c.Rgb) } : [new ForegroundOn(c.Row)]
                                : [];
                            Assert.Equal(expected, open);
                            break;
                    }
                }
            }
        }
    }

    [Fact]
    public void EveryColourPushedIsPoppedInTheOppositeOrderAndNoneIsNested() {
        foreach (var channel in new ChannelColour?[] { Teal, ChannelColour.OfRow(45), null }) {
            foreach (var wholeLine in new[] { true, false }) {
                foreach (var nearest in new Func<uint, ushort?>[] { Nearest45, _ => null }) {
                    var open = new Stack<string>();
                    foreach (var part in ColouredText.Message("[sky]", channel, "12:00", wholeLine, nearest, "Alice Liddell", "Ultros", Pink)) {
                        switch (part) {
                            case ForegroundOn:
                                // One colour at a time: a closest row, with at most its exact colour inside it.
                                Assert.Empty(open);
                                open.Push("foreground");
                                break;
                            case ColourOn:
                                Assert.True(open.Count == 0 || (open.Count == 1 && open.Peek() == "foreground"));
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
        }
    }

    // ================================================================ the words

    [Fact]
    public void TheNameColourWordsArePlain() {
        foreach (var text in NameColourWords.All()) {
            PlainLanguage.AssertPlain(text);
        }

        Assert.Equal("Name colour...", NameColourWords.MenuItem);
    }
}
