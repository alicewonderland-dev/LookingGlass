using Google.Protobuf;
using LookingGlass.Core.Client;
using LookingGlass.Protocol;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// Item, map flag and status links in channel messages: how a typed line becomes a message, how the message is encoded
/// (and read by older clients), and what a received link must pass before it is shown as one.
/// </summary>
public sealed class ChatLinkTests {
    private static readonly ChatLink.Item PotionItem = new(4551);
    private static readonly ChatLink.MapFlag LimsaFlag = new(129, 11, 9500, -11200);
    private static readonly ChatLink.Status Sprint = new(50);
    private static readonly ChatLink.PartyFinder Listing = new(31_337, false);

    private static readonly TypedLink Potion = new(PotionItem, "Potion");
    private static readonly TypedLink Flag = new(LimsaFlag, "Limsa Lominsa Lower Decks ( 9.5 , 11.2 )");
    private static readonly TypedLink Running = new(Sprint, "Sprint");
    private static readonly TypedLink Recruiting = new(Listing, "Looking for Party (Alice Test)");

    private static string M(int index) => LinkText.Marker(index).ToString();

    private static TypedLink? Placeholders(string placeholder) => placeholder switch {
        "<item>" => Potion,
        "<flag>" => Flag,
        "<status>" => Running,
        "<pfinder>" => Recruiting,
        _ => null,
    };

    private static void AssertLinks(LinkedText message, params (string Shown, ChatLink Target)[] expected) {
        Assert.Equal(expected.Length, message.Links.Count);
        for (var i = 0; i < expected.Length; i++) {
            var link = message.Links[i];
            Assert.Equal(expected[i].Shown, message.Text.Substring(link.Start, link.Length));
            Assert.Equal(expected[i].Target, link.Target);
        }
    }

    // ---------------------------------------------------------------- a typed line becomes a message

    [Fact]
    public void ALinkGoesAsItsNameInBracketsWithALinkOverIt() {
        var (message, leftOut) = LinkText.Resolve("look <item>", Placeholders);
        Assert.False(leftOut);
        Assert.Equal("look [Potion]", message.Text);
        AssertLinks(message, ("[Potion]", PotionItem));

        (message, _) = LinkText.Resolve("meet at <flag> now", Placeholders);
        Assert.Equal("meet at [Limsa Lominsa Lower Decks ( 9.5 , 11.2 )] now", message.Text);
        AssertLinks(message, ("[Limsa Lominsa Lower Decks ( 9.5 , 11.2 )]", LimsaFlag));

        (message, _) = LinkText.Resolve("<status> on", Placeholders);
        Assert.Equal("[Sprint] on", message.Text);
        AssertLinks(message, ("[Sprint]", Sprint));

        Assert.Equal(LinkedText.Plain("no links here"), LinkText.Resolve("no links here", Placeholders).Message);
    }

    [Fact]
    public void LinksAtTheStartInTheMiddleAndAtTheEndKeepTheirPlaces() {
        var line = new TypedLine($"{M(0)} then {M(1)} and {M(2)}", [Potion, Flag, Running]);
        var (message, leftOut) = LinkText.Compose(line);
        Assert.False(leftOut);
        Assert.Equal("[Potion] then [Limsa Lominsa Lower Decks ( 9.5 , 11.2 )] and [Sprint]", message.Text);
        AssertLinks(message, ("[Potion]", PotionItem), ("[Limsa Lominsa Lower Decks ( 9.5 , 11.2 )]", LimsaFlag), ("[Sprint]", Sprint));
    }

    [Fact]
    public void TheSamePlaceholderTwiceIsAskedForOnceAndLinkedTwice() {
        var asked = 0;
        var (message, _) = LinkText.Resolve("<ITEM> or <item>?", placeholder => {
            asked++;
            return Placeholders(placeholder);
        });
        Assert.Equal(1, asked);
        Assert.Equal("[Potion] or [Potion]?", message.Text);
        AssertLinks(message, ("[Potion]", PotionItem), ("[Potion]", PotionItem));
    }

    [Fact]
    public void ALinkWithANameButNoTargetGoesAsItsNameOnly() {
        // A symbolic item, say: the game gave its name but not what it is.
        var (message, leftOut) = LinkText.Resolve("look <item>", _ => new TypedLink(null, "Potion"));
        Assert.False(leftOut);
        Assert.Equal(LinkedText.Plain("look [Potion]"), message);

        // A target out of range is never sent as a link.
        (message, _) = LinkText.Resolve("look <item>", _ => new TypedLink(new ChatLink.Item(0), "Potion"));
        Assert.Equal(LinkedText.Plain("look [Potion]"), message);
    }

    [Fact]
    public void ALinkWithNothingKnownIsLeftOutAndTheRestSent() {
        Assert.Equal((LinkedText.Plain("look at this"), true), LinkText.Resolve("look at <item> this", _ => null));
        Assert.Equal((LinkedText.Plain("look"), true), LinkText.Resolve("look <status>", _ => throw new InvalidOperationException()));
        Assert.Equal((LinkedText.Plain("look"), true), LinkText.Resolve("look <item>", _ => new TypedLink(PotionItem, "   ")));

        // The links around it keep their places.
        var (message, leftOut) = LinkText.Compose(new TypedLine($"{M(0)}  {M(1)}  {M(2)}", [Potion, new TypedLink(null, null), Running]));
        Assert.True(leftOut);
        Assert.Equal("[Potion] [Sprint]", message.Text);
        AssertLinks(message, ("[Potion]", PotionItem), ("[Sprint]", Sprint));

        // Only a link nobody can read: nothing to send.
        Assert.Equal((LinkedText.Plain(""), true), LinkText.Resolve("<flag>", _ => null));
    }

    [Fact]
    public void ANameCantInjectAnything() {
        var (message, _) = LinkText.Resolve("look <item>", _ => new TypedLink(PotionItem, "Po\u0002tion <flag> [x]﷑"));
        Assert.Equal("look [Potion (flag) (x)]", message.Text);
        AssertLinks(message, ("[Potion (flag) (x)]", PotionItem));

        // Typed text can't pass for a marker.
        Assert.Equal("ab", TypedLine.Plain($"a{M(0)}b").Text);
        Assert.Equal(LinkedText.Plain("ab"), LinkText.Compose(TypedLine.Plain($"a{M(0)}b")).Message);
    }

    [Fact]
    public void ALongNameIsCutOnACharacterSoTheLinkCanAlwaysBeSent() {
        // 63 letters and an emoji (two UTF-16 units) and more: cut whole, with the brackets within the limit.
        foreach (var name in new[] { new string('a', 63) + "\U0001F600" + new string('b', 10), new string('a', 64) + "\U0001F600", new string('c', 200),
                     string.Concat(Enumerable.Repeat("\U0001F600", 40)) }) {
            var (message, _) = LinkText.Resolve("<item>", _ => new TypedLink(PotionItem, name));
            Assert.True(message.Text.Length <= ChatLinks.MaxTextLength, $"{message.Text.Length} characters");
            Assert.True(System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(message.Text)) == message.Text, "a surrogate pair was split");
            Assert.Single(MessageContent.ValidLinks(message.Text, MessageContent.Encode(message).Text.Links));
        }
    }

    [Fact]
    public void NoMoreThanFiveLinksGoAsLinksTheRestAsTheirNames() {
        var links = Enumerable.Range(0, 7).Select(i => new TypedLink(new ChatLink.Item((uint) (100 + i)), $"Item {i}")).ToList();
        var line = new TypedLine(string.Join(" ", Enumerable.Range(0, 7).Select(M)), links);
        var (message, _) = LinkText.Compose(line);
        Assert.Equal(string.Join(" ", Enumerable.Range(0, 7).Select(i => $"[Item {i}]")), message.Text);
        Assert.Equal(ChatLinks.MaxPerMessage, message.Links.Count);
        Assert.Equal(Enumerable.Range(0, 5).Select(i => (ChatLink) new ChatLink.Item((uint) (100 + i))), message.Links.Select(link => link.Target));
    }

    [Fact]
    public void LinksFromTheLinesBytesAndPlaceholdersGoTogetherInOrder() {
        // The line's own link bytes were read into marker 0; the placeholders come after it.
        var line = LinkText.ResolvePlaceholders(new TypedLine($"{M(0)} vs <item> at <flag>", [Running]), Placeholders);
        Assert.Equal(new TypedLine($"{M(0)} vs {M(1)} at {M(2)}", [Running, Potion, Flag]), line);
        var (message, _) = LinkText.Compose(line);
        AssertLinks(message, ("[Sprint]", Sprint), ("[Potion]", PotionItem), ("[Limsa Lominsa Lower Decks ( 9.5 , 11.2 )]", LimsaFlag));

        // Resolved once: nothing left to ask.
        Assert.Same(line, LinkText.ResolvePlaceholders(line, _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void APlaceholderPrefersWhatItPointsAtAndTheSheetsName() {
        // The order the plugin reads a placeholder in: the target if it is well formed; the name from the game's sheet,
        // else the game's own text for it; nothing if neither is known.
        Assert.Equal(new TypedLink(PotionItem, "Potion"), LinkText.PlaceholderLink(PotionItem, "Potion", "Old name"));
        Assert.Equal(new TypedLink(PotionItem, "Old name"), LinkText.PlaceholderLink(PotionItem, null, "Old name"));
        Assert.Equal(new TypedLink(PotionItem, "Old name"), LinkText.PlaceholderLink(PotionItem, "  ", "Old name"));
        Assert.Equal(new TypedLink(null, "Potion"), LinkText.PlaceholderLink(new ChatLink.Item(0), "Potion", null));
        Assert.Equal(new TypedLink(null, "Potion"), LinkText.PlaceholderLink(null, null, "Potion"));
        Assert.Null(LinkText.PlaceholderLink(null, null, null));
        Assert.Null(LinkText.PlaceholderLink(null, " ", ""));
    }

    [Fact]
    public void AChannelCommandsLinksGoWithItsMessage() {
        // "/lgc sky look <link>", read at the gate: the arguments keep the markers, and the message after the nickname too.
        var line = new TypedLine($"/lgc sky look {M(0)}", [Potion]);
        var arguments = line.Arguments();
        Assert.Equal(new TypedLine($"sky look {M(0)}", [Potion]), arguments);
        var command = ChannelCommand.ForNickname(new Dictionary<string, string> { ["c1"] = "sky" }, arguments.Text);
        var send = Assert.IsType<ChannelCommand.Send>(command);
        var (message, _) = LinkText.Compose(arguments.WithText(send.Text));
        Assert.Equal("look [Potion]", message.Text);
        AssertLinks(message, ("[Potion]", PotionItem));

        // A Japanese IME's full-width space ends the command too, the same for the command and its arguments.
        var fullWidth = new TypedLine($"/lgc1　look {M(0)}", [Potion]);
        Assert.Equal("/lgc1", fullWidth.Command());
        Assert.Equal($"look {M(0)}", fullWidth.Arguments().Text);
        Assert.Equal("/lgc1", new TypedLine("/lgc1", []).Command());
        Assert.Equal("", new TypedLine("/lgc1", []).Arguments().Text);

        // A link alone after /lgc3 is a message, not "talk in the channel".
        Assert.IsType<ChannelCommand.Send>(ChannelCommand.ForSlot(new Dictionary<string, int> { ["c1"] = 3 }, 3, new TypedLine($"/lgc3 {M(0)}", [Potion]).Arguments().Text));

        // Dalamud's text, for a line not read at the gate: a link's bytes are taken out, placeholders stay to be resolved.
        Assert.Equal(TypedLine.Plain("look  at <item>"), TypedLine.FromArguments("look \u0002'\u0004Fxy\u0003 at <item>"));
        Assert.Equal(TypedLine.Plain("ab"), TypedLine.FromArguments($"a{M(3)}b"));
    }

    /// <summary>An item link as the game encodes it (colour, glow, the link with a byte 3 in its data, arrow and name, the ends).</summary>
    private static readonly byte[] ItemLinkBytes = [
        0x02, 0x48, 0x04, 0xF2, 0x02, 0x25, 0x03,
        0x02, 0x49, 0x04, 0xF2, 0x02, 0x26, 0x03,
        0x02, 0x27, 0x07, 0x03, 0xF2, 0x14, 0xD5, 0x02, 0x01, 0x03,
        0xEE, 0x82, 0xBB, (byte) 'P', (byte) 'o', (byte) 't', (byte) 'i', (byte) 'o', (byte) 'n',
        0x02, 0x49, 0x02, 0x01, 0x03,
        0x02, 0x48, 0x02, 0x01, 0x03,
        0x02, 0x27, 0x07, 0xCF, 0x01, 0x01, 0x01, 0xFF, 0x01, 0x03,
    ];

    [Fact]
    public void DalamudsTextOfALinesBytesLosesThePayloadsWhole() {
        // What Dalamud gives a handler: the line's bytes read as UTF-8 (Utf8String.ToString). Each payload is skipped by
        // its length, even where its data holds a byte 3; the link's own text stays.
        string Garbled(params byte[][] parts) => System.Text.Encoding.UTF8.GetString(parts.SelectMany(part => part).ToArray());
        var look = System.Text.Encoding.UTF8.GetBytes("look ");
        var now = System.Text.Encoding.UTF8.GetBytes(" now");
        Assert.Equal(TypedLine.Plain("look Potion now"), TypedLine.FromArguments(Garbled(look, ItemLinkBytes, now)));

        // A map link (packed ids, multi-byte integers) and an auto-translate phrase.
        byte[] map = [0x02, 0x27, 0x11, 0x04, 0xF6, 0x01, 0x03, 0x02, 0xF6, 0x03, 0x03, 0x03, 0xFE, 0xFF, 0xFF, 0x03, 0x03, 0xFF, 0x01, 0x03];
        Assert.Equal(TypedLine.Plain("a b"), TypedLine.FromArguments(Garbled("a"u8.ToArray(), map, " b"u8.ToArray())));
        Assert.Equal(TypedLine.Plain("hi !"), TypedLine.FromArguments(Garbled("hi "u8.ToArray(), [0x02, 0x2E, 0x03, 0x01, 0x66, 0x03], "!"u8.ToArray())));

        // A payload whose length can't be read, or which runs past the end: dropped to its end byte, or the line's end.
        Assert.Equal(TypedLine.Plain("x y"), TypedLine.FromArguments("x \u0002'ÿ\u0001\u0003y"));
        Assert.Equal(TypedLine.Plain("x "), TypedLine.FromArguments("x \u0002'\u0010ab"));
    }

    // ---------------------------------------------------------------- the message as sent

    private static LinkedText RoundTrip(LinkedText message) =>
        MessageContent.Decode(Content.Parser.ParseFrom(MessageContent.Encode(message).ToByteArray()))!;

    [Fact]
    public void LinksSurviveTheMessageFormatWhereverTheyAre() {
        foreach (var typed in new[] {
                     new TypedLine($"{M(0)} is cheap", [Potion]),
                     new TypedLine($"look {M(0)} now", [Potion]),
                     new TypedLine($"meet at {M(0)}", [Flag]),
                     new TypedLine($"{M(0)}", [Running]),
                     new TypedLine($"{M(0)}{M(1)} and {M(2)}", [Potion, Running, Flag]),
                     new TypedLine($"join {M(0)}!", [Recruiting]),
                     new TypedLine($"{M(0)} for {M(1)}", [new TypedLink(Listing with { HomeWorldOnly = true }, "Looking for Party (Bob Test)"), Potion]),
                 }) {
            var (message, _) = LinkText.Compose(typed);
            Assert.Equal(message, RoundTrip(message));
        }
    }

    [Fact]
    public void AMessageWithoutLinksDecodesAsBefore() {
        // What every client before links sent.
        var old = new Content { Text = new TextContent { Text = "look [Potion]" } };
        Assert.Equal(LinkedText.Plain("look [Potion]"), MessageContent.Decode(Content.Parser.ParseFrom(old.ToByteArray())));
        Assert.Equal(old, MessageContent.Encode(LinkedText.Plain("look [Potion]")));

        // Another content kind isn't text.
        Assert.Null(MessageContent.Decode(new Content()));
    }

    [Fact]
    public void AnOlderClientReadsAMessageWithLinksAsItsText() {
        var (message, _) = LinkText.Compose(new TypedLine($"look {M(0)} at {M(1)}", [Potion, Flag]));
        var bytes = MessageContent.Encode(message).ToByteArray();

        // An older client's Content: a text kind (field 1) whose TextContent has only its text (field 1). Announcement is
        // exactly that message shape, so it stands in for the old TextContent.
        var input = new CodedInputStream(bytes);
        Assert.Equal(WireFormat.MakeTag(1, WireFormat.WireType.LengthDelimited), input.ReadTag());
        var oldText = Announcement.Parser.ParseFrom(input.ReadBytes());
        Assert.True(input.IsAtEnd);
        Assert.Equal("look [Potion] at [Limsa Lominsa Lower Decks ( 9.5 , 11.2 )]", oldText.Text);
    }

    private static TextLink ItemAt(int start, int length, uint rawId = 4551) => new() { Start = (uint) start, Length = (uint) length, Item = new ItemLink { RawId = rawId } };

    [Fact]
    public void AReceivedLinkMustStandOverABracketedNameInTheText() {
        const string text = "look [Potion] and [Ether]";
        Assert.Single(MessageContent.ValidLinks(text, [ItemAt(5, 8)]));
        Assert.Equal(2, MessageContent.ValidLinks(text, [ItemAt(5, 8), ItemAt(18, 7)]).Count);

        Assert.Empty(MessageContent.ValidLinks(text, [ItemAt(4, 8)])); // not over the brackets
        Assert.Empty(MessageContent.ValidLinks(text, [ItemAt(5, 7)]));
        Assert.Empty(MessageContent.ValidLinks(text, [ItemAt(18, 8)])); // past the end
        Assert.Empty(MessageContent.ValidLinks(text, [ItemAt(int.MaxValue, 8)]));
        Assert.Empty(MessageContent.ValidLinks(text, [new TextLink { Start = 5, Length = uint.MaxValue, Item = new ItemLink { RawId = 4551 } }]));
        Assert.Empty(MessageContent.ValidLinks(text, [ItemAt(5, 1)]));
        Assert.Empty(MessageContent.ValidLinks("[]", [ItemAt(0, 1)]));

        // In order, never overlapping: a link before the one already taken is dropped, the one after kept.
        Assert.Equal(new[] { 5 }, MessageContent.ValidLinks(text, [ItemAt(5, 8), ItemAt(5, 8)]).Select(link => link.Start));
        Assert.Equal(new[] { 18 }, MessageContent.ValidLinks(text, [ItemAt(18, 7), ItemAt(5, 8)]).Select(link => link.Start));

        // At the very end, with no length (or past it): dropped, never an error.
        Assert.Empty(MessageContent.ValidLinks(text, [ItemAt(text.Length, 0)]));
        Assert.Empty(MessageContent.ValidLinks(text, [ItemAt(text.Length, 2)]));
        Assert.Empty(MessageContent.ValidLinks("", [ItemAt(0, 0)]));

        // Too long a name for any link.
        var longName = $"[{new string('a', ChatLinks.MaxTextLength)}]";
        Assert.Empty(MessageContent.ValidLinks(longName, [ItemAt(0, longName.Length)]));
    }

    [Fact]
    public void AReceivedLinkMustHaveAKnownKindAndNumbersInRange() {
        const string text = "[x]";
        TextLink At(Action<TextLink> set) {
            var link = new TextLink { Start = 0, Length = 3 };
            set(link);
            return link;
        }

        Assert.Empty(MessageContent.ValidLinks(text, [new TextLink { Start = 0, Length = 3 }])); // no kind (a newer one)
        foreach (var bad in new uint[] { 0, 500_000, 1_000_000, 1_500_000, 1_999_999, 3_000_000, uint.MaxValue }) {
            Assert.Empty(MessageContent.ValidLinks(text, [At(link => link.Item = new ItemLink { RawId = bad })]));
        }

        foreach (var good in new uint[] { 1, 4551, 504_551, 1_004_551, 2_000_001 }) {
            Assert.Single(MessageContent.ValidLinks(text, [At(link => link.Item = new ItemLink { RawId = good })]));
        }

        Assert.Single(MessageContent.ValidLinks(text, [At(link => link.Map = new MapLink { TerritoryId = 129, MapId = 11, RawX = 9500, RawY = -11200 })]));
        Assert.Empty(MessageContent.ValidLinks(text, [At(link => link.Map = new MapLink { TerritoryId = 0, MapId = 11 })]));
        Assert.Empty(MessageContent.ValidLinks(text, [At(link => link.Map = new MapLink { TerritoryId = 129, MapId = 70_000 })]));
        Assert.Empty(MessageContent.ValidLinks(text, [At(link => link.Map = new MapLink { TerritoryId = 129, MapId = 11, RawX = int.MinValue })]));
        Assert.Empty(MessageContent.ValidLinks(text, [At(link => link.Map = new MapLink { TerritoryId = 129, MapId = 11, RawY = 5_000_000 })]));
        Assert.Single(MessageContent.ValidLinks(text, [At(link => link.Status = new StatusLink { StatusId = 50 })]));
        Assert.Empty(MessageContent.ValidLinks(text, [At(link => link.Status = new StatusLink { StatusId = 0 })]));
        Assert.Empty(MessageContent.ValidLinks(text, [At(link => link.Status = new StatusLink { StatusId = 100_000 })]));
    }

    [Fact]
    public void AMessageWithTooManyLinksShowsAsItsTextOnly() {
        var text = string.Join(" ", Enumerable.Range(0, 6).Select(_ => "[x]"));
        var links = Enumerable.Range(0, 6).Select(i => ItemAt(i * 4, 3)).ToList();
        Assert.Empty(MessageContent.ValidLinks(text, links));
        Assert.Equal(5, MessageContent.ValidLinks(text, links.Take(5).ToList()).Count);
    }

    [Fact]
    public void AMessageWithLinksStaysWellWithinTheSizeLimit() {
        // Five links with the longest names a message can carry, and a game chat line's worth of text.
        var name = new string('n', LookingGlass.Core.Util.TextSanitizer.MaxNameLength);
        var links = Enumerable.Range(0, ChatLinks.MaxPerMessage).Select(_ => new TypedLink(new ChatLink.MapFlag(65_535, 65_535, -4_096_000, 4_096_000), name)).ToList();
        var line = new TypedLine(new string('t', 500) + string.Concat(Enumerable.Range(0, links.Count).Select(M)), links);
        var (message, _) = LinkText.Compose(line);
        Assert.Equal(ChatLinks.MaxPerMessage, message.Links.Count);
        // The ciphertext adds the AEAD tag (16 bytes) to this: far under the 4 KiB limit.
        Assert.True(MessageContent.Encode(message).CalculateSize() < 1200);
    }

    // ---------------------------------------------------------------- checked against the recipient's own game data

    private sealed class FakeSheets : ILinkSheets {
        public LinkItemRow? Item(uint id) => id switch {
            4551 => new LinkItemRow("Potion", true, false),
            5333 => new LinkItemRow("Ether", false, false),
            12345 => new LinkItemRow("Rarefied Ore", false, true),
            777 => new LinkItemRow("", false, false),
            _ => null,
        };

        public string? EventItemName(uint id) => id == 2_000_001 ? "Key" : null;

        public LinkMapRow? Map(uint id) => id switch {
            11 => new LinkMapRow(129, 200, 0, 0),
            12 => new LinkMapRow(128, 200, 0, 0),
            13 => new LinkMapRow(130, 0, 0, 0),
            6 => new LinkMapRow(153, 100, 0, 0),
            _ => null,
        };

        // Territory 192 is an instanced copy of 153 (as a duty is): its default map, 6, is 153's.
        public LinkTerritoryRow? Territory(uint id) => id switch {
            129 => new LinkTerritoryRow("Limsa Lominsa Lower Decks", 11),
            128 => new LinkTerritoryRow("Limsa Lominsa Upper Decks", 12),
            130 => new LinkTerritoryRow("Ul'dah", 13),
            153 => new LinkTerritoryRow("South Shroud", 6),
            192 => new LinkTerritoryRow("South Shroud", 6),
            193 => new LinkTerritoryRow("", 6),
            _ => null,
        };

        public string? StatusName(uint id) => id == 50 ? "Sprint" : null;
    }

    [Fact]
    public void AnItemMustBeOneTheRecipientsGameKnowsInThatKind() {
        var sheets = new FakeSheets();
        Assert.Equal("Potion", ChatLinks.Check(new ChatLink.Item(4551), sheets));
        Assert.Equal("Potion", ChatLinks.Check(new ChatLink.Item(1_004_551), sheets)); // high quality: it can be
        Assert.Null(ChatLinks.Check(new ChatLink.Item(504_551), sheets)); // not a collectable
        Assert.Null(ChatLinks.Check(new ChatLink.Item(1_005_333), sheets)); // can't be high quality
        Assert.Equal("Rarefied Ore", ChatLinks.Check(new ChatLink.Item(512_345), sheets));
        Assert.Equal("Key", ChatLinks.Check(new ChatLink.Item(2_000_001), sheets));
        Assert.Null(ChatLinks.Check(new ChatLink.Item(2_000_002), sheets));
        Assert.Null(ChatLinks.Check(new ChatLink.Item(9999), sheets)); // no such row
        Assert.Null(ChatLinks.Check(new ChatLink.Item(777), sheets)); // no name
        Assert.Null(ChatLinks.Check(new ChatLink.Item(0), sheets));
    }

    [Fact]
    public void AMapFlagMustBeOnAMapOfItsTerritory() {
        var sheets = new FakeSheets();
        Assert.Equal("Limsa Lominsa Lower Decks", ChatLinks.Check(LimsaFlag, sheets));
        Assert.Null(ChatLinks.Check(LimsaFlag with { MapId = 12 }, sheets)); // another territory's map
        Assert.Null(ChatLinks.Check(LimsaFlag with { MapId = 99 }, sheets)); // no such map
        Assert.Null(ChatLinks.Check(LimsaFlag with { TerritoryId = 130, MapId = 13 }, sheets)); // a map with no size
        // Size factor 200: the map's square is 1,024 world units a side around its centre.
        Assert.Equal("Limsa Lominsa Lower Decks", ChatLinks.Check(LimsaFlag with { RawX = 512_000, RawY = -512_000 }, sheets));
        Assert.Null(ChatLinks.Check(LimsaFlag with { RawX = 600_000 }, sheets));
        Assert.Null(ChatLinks.Check(LimsaFlag with { RawY = -600_000 }, sheets));
        Assert.Null(ChatLinks.Check(LimsaFlag with { RawX = int.MaxValue }, sheets));
    }

    [Fact]
    public void AMapFlagInADutyIsOnTheMapItsTerritoryUses() {
        // Nearly half the game's territories (duties, instanced copies of open-world zones) use another territory's map:
        // TerritoryType 192's map is 6, whose own territory is 153. A flag set there names 192 and map 6.
        var sheets = new FakeSheets();
        var inDuty = new ChatLink.MapFlag(192, 6, 100_000, -200_000);
        Assert.Equal("South Shroud", ChatLinks.Check(inDuty, sheets));
        Assert.Equal("South Shroud", ChatLinks.Check(inDuty with { TerritoryId = 153 }, sheets));
        // Not its map, and not the map's territory: no.
        Assert.Null(ChatLinks.Check(inDuty with { MapId = 11 }, sheets));
        Assert.Null(ChatLinks.Check(inDuty with { TerritoryId = 999 }, sheets));
        // Still on the map, and still with a place name.
        Assert.Null(ChatLinks.Check(inDuty with { RawX = 1_200_000 }, sheets));
        Assert.Null(ChatLinks.Check(inDuty with { TerritoryId = 193 }, sheets));
    }

    [Fact]
    public void AFlagTheGameCantShowStillGoesAsItsPlace() {
        // The plugin's fallback name for a flag whose link doesn't check out: its place, else "flag".
        Assert.Equal("South Shroud", LinkText.FlagName(new FakeSheets(), 192));
        Assert.Equal(LinkText.UnnamedFlag, LinkText.FlagName(new FakeSheets(), 193));
        Assert.Equal(LinkText.UnnamedFlag, LinkText.FlagName(new FakeSheets(), 999));
        var (message, leftOut) = LinkText.Resolve("meet <flag>", _ => LinkText.PlaceholderLink(null, null, LinkText.FlagName(new FakeSheets(), 999)));
        Assert.False(leftOut);
        Assert.Equal(LinkedText.Plain("meet [flag]"), message);
    }

    [Fact]
    public void TerritoryAndMapIdsMustFitInSixteenBits() {
        Assert.True(ChatLinks.IsWellFormed(new ChatLink.MapFlag(65_535, 65_535, 0, 0)));
        Assert.False(ChatLinks.IsWellFormed(new ChatLink.MapFlag(65_536, 1, 0, 0)));
        Assert.False(ChatLinks.IsWellFormed(new ChatLink.MapFlag(1, 65_536, 0, 0)));
        Assert.True(ChatLinks.IsWellFormed(new ChatLink.Status(65_535)));
        Assert.False(ChatLinks.IsWellFormed(new ChatLink.Status(65_536)));
    }

    [Fact]
    public void AStatusMustBeOneTheRecipientsGameKnows() {
        var sheets = new FakeSheets();
        Assert.Equal("Sprint", ChatLinks.Check(Sprint, sheets));
        Assert.Null(ChatLinks.Check(new ChatLink.Status(51), sheets));
        Assert.Null(ChatLinks.Check(new ChatLink.Status(0), sheets));
    }

    [Fact]
    public void TheOnMapCheckFollowsTheMapsOffsetAndScale() {
        // Size factor 100, offset -100: the square runs from -924 to 1,124 world units.
        var map = new LinkMapRow(1, 100, -100, -100);
        Assert.True(ChatLinks.OnMap(1_100_000, -900_000, map));
        Assert.False(ChatLinks.OnMap(1_300_000, 0, map));
        Assert.False(ChatLinks.OnMap(0, -1_100_000, map));
    }

    // ---------------------------------------------------------------- shown

    [Fact]
    public void AMessageIsShownAsTextAndLinksInOrder() {
        var (message, _) = LinkText.Compose(new TypedLine($"{M(0)} then {M(1)}", [Potion, Running]));
        Assert.Equal(new MessagePart[] {
            new MessagePart.Link(PotionItem, "[Potion]"),
            new MessagePart.Text(" then "),
            new MessagePart.Link(Sprint, "[Sprint]"),
        }, message.Parts());
        Assert.Equal(new MessagePart[] { new MessagePart.Text("plain") }, LinkedText.Plain("plain").Parts());
        Assert.Empty(LinkedText.Plain("").Parts());
    }

    [Fact]
    public void AMessageWithLinksIsShownWithinOneLengthLimitAndSanitised() {
        var text = new string('a', 900) + "[Potion]" + new string('b', 900) + "[Ether]" + "\u0002c";
        var message = new LinkedText(text, [new MessageLink(900, 8, PotionItem), new MessageLink(1808, 7, new ChatLink.Item(5333))]);
        var parts = message.ShownParts();
        int Length(MessagePart part) => part switch {
            MessagePart.Text t => t.Value.Length,
            MessagePart.Link l => l.Shown.Length,
            _ => 0,
        };

        // As one plain message is: at most 1,000 characters and the "…".
        Assert.True(parts.Sum(Length) <= LookingGlass.Core.Util.TextSanitizer.MaxMessageLength + 1, $"{parts.Sum(Length)} characters");
        Assert.Equal(new MessagePart.Link(PotionItem, "[Potion]"), parts[1]);
        Assert.EndsWith("…", Assert.IsType<MessagePart.Text>(parts[^1]).Value);
        Assert.DoesNotContain(parts, part => part is MessagePart.Link { Target: ChatLink.Item { RawId: 5333 } });

        // A short one is whole, its text sanitised like any.
        var (shortOne, _) = LinkText.Compose(new TypedLine($"look {M(0)} now", [Potion]));
        Assert.Equal(new MessagePart[] { new MessagePart.Text("look "), new MessagePart.Link(PotionItem, "[Potion]"), new MessagePart.Text(" now") },
            new LinkedText("look\u0002 [Potion] now", [new MessageLink(6, 8, PotionItem)]).ShownParts());
        Assert.Equal(shortOne.Parts(), shortOne.ShownParts());
    }

    [Fact]
    public void ALinkThatDoesntCheckOutShowsAsTheSendersTextSanitised() {
        Assert.Equal("[Potion]", new MessagePart.Link(PotionItem, "[Potion]").Fallback);
        Assert.Equal("[Potion]", new MessagePart.Link(PotionItem, "[Po\u0002\u0003tion]").Fallback);
        Assert.Equal(MessagePart.UnknownLink, new MessagePart.Link(PotionItem, "[\u0002\u001F]").Fallback);
        Assert.Equal(MessagePart.UnknownLink, new MessagePart.Link(PotionItem, "[ ]").Fallback);
    }

    // ---------------------------------------------------------------- party finder listings

    [Fact]
    public void APartyFinderLinkGoesAsTheListingUnderTheSendersTextForIt() {
        // The recruitment window's chat button puts <pfinder> in the chat input, as linking an item puts <item>.
        var (message, leftOut) = LinkText.Resolve("join <PFinder> now", Placeholders);
        Assert.False(leftOut);
        Assert.Equal("join [Looking for Party (Alice Test)] now", message.Text);
        AssertLinks(message, ("[Looking for Party (Alice Test)]", Listing));

        Assert.True(LinkText.IsPlaceholder("<pfinder>"));
        Assert.Contains("<pfinder>", ChatBoxLine.LinkPlaceholders);
        Assert.True(ChatBoxLine.HasSomethingToSend("<pfinder>"));
        Assert.False(ChatBoxLine.HasText("<pfinder>"));

        // A long name is cut, so the link is still sent, and received, as one.
        var (longOne, _) = LinkText.Resolve("<pfinder>", _ => new TypedLink(Listing, "Looking for Party (" + new string('x', 200) + ")"));
        Assert.True(longOne.Text.Length <= ChatLinks.MaxTextLength, $"{longOne.Text.Length} characters");
        Assert.Equal(Listing, Assert.Single(MessageContent.ValidLinks(longOne.Text, MessageContent.Encode(longOne).Text.Links)).Target);
        Assert.True(new MessagePart.Link(Listing, longOne.Text).Name(new FakeSheets())!.Length <= ChatLinks.MaxTextLength - 2);

        // Nothing known: left out, as any link. A name but no listing id: the name only.
        Assert.Equal((LinkedText.Plain("join"), true), LinkText.Resolve("join <pfinder>", _ => null));
        Assert.Equal(new TypedLink(null, "Alice Test"), LinkText.PlaceholderLink(new ChatLink.PartyFinder(0, false), null, "Alice Test"));
        Assert.Equal(LinkedText.Plain("join [Alice Test]"),
            LinkText.Resolve("join <pfinder>", _ => LinkText.PlaceholderLink(new ChatLink.PartyFinder(0, false), null, "Alice Test")).Message);
    }

    private static TextLink ListingAt(int start, int length, uint listingId = 31_337, bool homeWorldOnly = false) =>
        new() { Start = (uint) start, Length = (uint) length, PartyFinder = new PartyFinderLink { ListingId = listingId, HomeWorldOnly = homeWorldOnly } };

    [Fact]
    public void AReceivedPartyFinderLinkNeedsAListingIdAndABracketedName() {
        const string text = "join [Looking for Party (Alice Test)]!";
        var link = Assert.Single(MessageContent.ValidLinks(text, [ListingAt(5, 32)]));
        Assert.Equal(new MessageLink(5, 32, Listing), link);
        Assert.Equal(new ChatLink.PartyFinder(31_337, true), Assert.Single(MessageContent.ValidLinks(text, [ListingAt(5, 32, homeWorldOnly: true)])).Target);
        Assert.Single(MessageContent.ValidLinks(text, [ListingAt(5, 32, 1)]));
        Assert.Single(MessageContent.ValidLinks(text, [ListingAt(5, 32, uint.MaxValue)]));

        Assert.Empty(MessageContent.ValidLinks(text, [ListingAt(5, 32, 0)])); // no listing
        Assert.Empty(MessageContent.ValidLinks(text, [ListingAt(4, 32)])); // not over the brackets
        Assert.Empty(MessageContent.ValidLinks(text, [ListingAt(5, 33)]));
        Assert.Empty(MessageContent.ValidLinks(text, [ListingAt(6, 31)]));
        Assert.False(ChatLinks.IsWellFormed(new ChatLink.PartyFinder(0, true)));
        Assert.True(ChatLinks.IsWellFormed(Listing));

        // A new link kind on its own field: what older clients skip.
        Assert.Equal(6, TextLink.PartyFinderFieldNumber);
    }

    [Fact]
    public void AnOlderClientReadsAPartyFinderLinkAsItsText() {
        var (message, _) = LinkText.Compose(new TypedLine($"join {M(0)}", [Recruiting]));
        var input = new CodedInputStream(MessageContent.Encode(message).ToByteArray());
        Assert.Equal(WireFormat.MakeTag(1, WireFormat.WireType.LengthDelimited), input.ReadTag());
        Assert.Equal("join [Looking for Party (Alice Test)]", Announcement.Parser.ParseFrom(input.ReadBytes()).Text);
    }

    [Fact]
    public void AClientThatDoesntKnowTheListingFieldDropsTheLinkAndShowsTheText() {
        // The link as encoded: start, length, and the listing on field 6.
        var (message, _) = LinkText.Compose(new TypedLine($"join {M(0)}", [Recruiting]));
        var encoded = MessageContent.Encode(message).Text.Links.Single();
        var input = new CodedInputStream(encoded.ToByteArray());
        Assert.Equal(WireFormat.MakeTag(1, WireFormat.WireType.Varint), input.ReadTag());
        input.ReadUInt32();
        Assert.Equal(WireFormat.MakeTag(2, WireFormat.WireType.Varint), input.ReadTag());
        input.ReadUInt32();
        Assert.Equal(WireFormat.MakeTag(6, WireFormat.WireType.LengthDelimited), input.ReadTag());
        var listing = input.ReadBytes();
        Assert.True(input.IsAtEnd);

        // An older client has no field 6 in its TextLink, as this one has no field 7: the same bytes on field 7 stand in
        // for what it reads. The field is kept as unknown, the link has no kind, and it is dropped; the text stays.
        var stream = new MemoryStream();
        var output = new CodedOutputStream(stream);
        output.WriteTag(1, WireFormat.WireType.Varint);
        output.WriteUInt32(encoded.Start);
        output.WriteTag(2, WireFormat.WireType.Varint);
        output.WriteUInt32(encoded.Length);
        output.WriteTag(7, WireFormat.WireType.LengthDelimited);
        output.WriteBytes(listing);
        output.Flush();
        var unknown = TextLink.Parser.ParseFrom(stream.ToArray());
        Assert.Equal(TextLink.TargetOneofCase.None, unknown.TargetCase);

        var content = new Content { Text = new TextContent { Text = message.Text, Links = { unknown } } };
        Assert.Equal(LinkedText.Plain("join [Looking for Party (Alice Test)]"), MessageContent.Decode(Content.Parser.ParseFrom(content.ToByteArray())));
    }

    [Fact]
    public void APartyFinderListingIsNamedByTheSendersTextAsPlainText() {
        // No game data holds a listing: the name shown is the sender's, with no game formatting or icons of its own (the
        // recipient's plugin adds the party finder's marks around it).
        var sheets = new FakeSheets();
        Assert.Null(ChatLinks.Check(Listing, sheets));
        Assert.Equal("Looking for Party (Alice Test)", new MessagePart.Link(Listing, "[Looking for Party (Alice Test)]").Name(sheets));
        Assert.Equal("Free gil", new MessagePart.Link(Listing, "[Free\u0002 gil]").Name(sheets));
        Assert.Equal("a (b) (c)", new MessagePart.Link(Listing, "[a [b] <c>]").Name(sheets));
        // The game's link arrow and party finder and cross-world marks, and private-use characters past U+FFFF (two
        // UTF-16 units each), never come from the sender: the recipient adds its own marks.
        Assert.Equal("Free gil", new MessagePart.Link(Listing, "[ Free gil]").Name(sheets));
        Assert.Equal("Free gil", new MessagePart.Link(Listing, "[\U000F0001Free \U0010FFFDgil\U000F0000]").Name(sheets));
        Assert.Equal("abc", LinkText.Clean("a\U000F0001b\U0010FFFDc"));
        Assert.Equal("a\U0001F600b", LinkText.Clean("a\U0001F600b")); // other characters past U+FFFF stay
        Assert.Null(new MessagePart.Link(Listing, "[ \u0002]").Name(sheets));
        Assert.Null(new MessagePart.Link(new ChatLink.PartyFinder(0, false), "[Free gil]").Name(sheets));
        Assert.Equal(MessagePart.UnknownLink, new MessagePart.Link(Listing, "[\u0002]").Fallback);

        // Every other kind is named by the recipient's own game, whatever the sender's text.
        Assert.Equal("Potion", new MessagePart.Link(PotionItem, "[Free gil]").Name(sheets));
        Assert.Equal("Sprint", new MessagePart.Link(Sprint, "[x]").Name(sheets));
        Assert.Null(new MessagePart.Link(new ChatLink.Item(9999), "[Potion]").Name(sheets));
    }

    // ---------------------------------------------------------------- end to end

    [Fact]
    public async Task LinksReachTheOtherMembersEncryptedAndSigned() {
        var server = new Harness();
        try {
            var alice = await server.RegisterAsync("Alice Test");
            var bob = await server.RegisterAsync("Bob Test");
            var channelId = await alice.Session.CreateChannelAsync("Tea Party", Ct);
            await alice.Session.InviteAsync(channelId, "Bob Test", ProtocolInfo.DebugWorldName, Ct);
            await WaitFor(() => bob.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == channelId && i.ChannelName != null));
            await bob.Session.RespondToInviteAsync(channelId, true, Ct);
            await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { HasKey: true, RekeyPending: false } c ? c : null);

            var (message, _) = LinkText.Compose(new TypedLine($"look {M(0)} at {M(1)}", [Potion, Flag]));
            await alice.Session.SendAsync(channelId, message, Ct);

            var atBob = await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == message.Text));
            Assert.Equal(message.Links, atBob.Links);
            Assert.Equal(message, atBob.Linked);
            var own = Assert.Single(alice.Messages, m => m.IsOwn && m.Text == message.Text);
            Assert.Equal(message.Links, own.Links);

            // A link that wouldn't pass at the other end isn't sent at all.
            var bad = new LinkedText("look [Potion]", [new MessageLink(4, 8, PotionItem)]);
            await Assert.ThrowsAsync<ArgumentException>(() => alice.Session.SendAsync(channelId, bad, Ct));
        } finally {
            await server.DisposeAsync();
            DeleteDirectory(server.DataDirectory);
        }
    }
}
