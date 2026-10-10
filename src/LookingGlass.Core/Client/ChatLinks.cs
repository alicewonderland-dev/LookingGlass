using LookingGlass.Core.Util;
using LookingGlass.Protocol;

namespace LookingGlass.Core.Client;

/// <summary>
/// What a link in a channel message points at: an item, a map flag, a status or a party finder listing, as the game's
/// own chat links do. Only ids and numbers: the name a recipient sees comes from their own game data (see
/// <see cref="ChatLinks.Check"/>), except a listing's, which no game data holds (see <see cref="MessagePart.Link.Name"/>).
/// </summary>
public abstract record ChatLink {
    private ChatLink() {
    }

    /// <summary>An item, by the game's raw id (see <see cref="ChatLinks.ItemParts"/>).</summary>
    public sealed record Item(uint RawId) : ChatLink;

    /// <summary>A map position: TerritoryType and Map rows, and world coordinates times 1,000, as a map link carries them.</summary>
    public sealed record MapFlag(uint TerritoryId, uint MapId, int RawX, int RawY) : ChatLink;

    /// <summary>A status (a Status row).</summary>
    public sealed record Status(uint StatusId) : ChatLink;

    /// <summary>
    /// A party finder listing, by the game's listing id, and whether it is limited to its leader's home world (the game's
    /// link has its cross-world mark otherwise).
    /// </summary>
    public sealed record PartyFinder(uint ListingId, bool HomeWorldOnly) : ChatLink;
}

/// <summary>The kinds of item a raw item id stands for, as the game numbers them.</summary>
public enum ItemLinkKind {
    Normal,
    Collectable,
    HighQuality,
    EventItem,
}

/// <summary>An Item sheet row, as a link needs it.</summary>
public readonly record struct LinkItemRow(string Name, bool CanBeHq, bool IsCollectable);

/// <summary>A Map sheet row, as a link needs it.</summary>
public readonly record struct LinkMapRow(uint TerritoryId, ushort SizeFactor, short OffsetX, short OffsetY);

/// <summary>A TerritoryType sheet row, as a link needs it: its place name, and its default map (its <c>Map</c>).</summary>
public readonly record struct LinkTerritoryRow(string? PlaceName, uint MapId);

/// <summary>The game's own data (its sheets, in the client's language), as checking a link needs it. Null: no such row.</summary>
public interface ILinkSheets {
    LinkItemRow? Item(uint id);

    /// <summary>An EventItem (key item) row's name.</summary>
    string? EventItemName(uint id);

    LinkMapRow? Map(uint id);

    /// <summary>A TerritoryType row: its place name and its default map.</summary>
    LinkTerritoryRow? Territory(uint id);

    /// <summary>A Status row's name.</summary>
    string? StatusName(uint id);
}

/// <summary>
/// The rules for links in channel messages (see docs/design.md, Messages): their limits, the game's item id offsets,
/// and the checks a link must pass before it is shown as one.
/// </summary>
public static class ChatLinks {
    /// <summary>At most this many links in one message; a sender puts any more in as plain text.</summary>
    public const int MaxPerMessage = 5;

    /// <summary>The longest "[name]" a link may stand over: a name as <see cref="TextSanitizer.Name"/> leaves it, in brackets.</summary>
    public const int MaxTextLength = TextSanitizer.MaxNameLength + 3;

    /// <summary>A collectable's raw id is its Item row plus this.</summary>
    public const uint CollectableOffset = 500_000;

    /// <summary>A high-quality item's raw id is its Item row plus this.</summary>
    public const uint HighQualityOffset = 1_000_000;

    /// <summary>Raw ids from this one are EventItem (key item) rows, as they are.</summary>
    public const uint EventItemStart = 2_000_000;

    private const uint EventItemEnd = 3_000_000;

    /// <summary>Row ids above this are never in the sheets a link names (and the game packs them into 16 bits).</summary>
    private const uint MaxRowId = ushort.MaxValue;

    /// <summary>A world coordinate (times 1,000) further out than this is on no map.</summary>
    private const int MaxRawCoordinate = 4_096_000;

    /// <summary>How far outside its map's square (in map pixels, of 2,048) a position may be, for rounding.</summary>
    private const float MapMargin = 64f;

    /// <summary>
    /// The Item (or EventItem) row and kind a raw item id stands for, as the game numbers them: under 500,000 an item,
    /// then its collectable (+500,000) and high-quality (+1,000,000) forms, and from 2,000,000 a key item. Null for an id
    /// that stands for nothing (0, or past those ranges).
    /// </summary>
    public static (uint Id, ItemLinkKind Kind)? ItemParts(uint rawId) => rawId switch {
        0 or CollectableOffset or HighQualityOffset => null,
        < CollectableOffset => (rawId, ItemLinkKind.Normal),
        < HighQualityOffset => (rawId - CollectableOffset, ItemLinkKind.Collectable),
        < HighQualityOffset + CollectableOffset => (rawId - HighQualityOffset, ItemLinkKind.HighQuality),
        >= EventItemStart and < EventItemEnd => (rawId, ItemLinkKind.EventItem),
        _ => null,
    };

    /// <summary>The raw id of an Item row in a kind (the inverse of <see cref="ItemParts"/>).</summary>
    public static uint RawItemId(uint id, ItemLinkKind kind) => kind switch {
        ItemLinkKind.Collectable => id + CollectableOffset,
        ItemLinkKind.HighQuality => id + HighQualityOffset,
        _ => id,
    };

    /// <summary>
    /// The numbers are in range, before any game data is asked: ids that can be rows, coordinates that can be on a map,
    /// a listing id that isn't 0.
    /// </summary>
    public static bool IsWellFormed(ChatLink link) => link switch {
        ChatLink.Item item => ItemParts(item.RawId) != null,
        ChatLink.MapFlag map => map.TerritoryId is > 0 and <= MaxRowId && map.MapId is > 0 and <= MaxRowId
                                && Math.Abs((long) map.RawX) <= MaxRawCoordinate && Math.Abs((long) map.RawY) <= MaxRawCoordinate,
        ChatLink.Status status => status.StatusId is > 0 and <= MaxRowId,
        ChatLink.PartyFinder listing => listing.ListingId > 0,
        _ => false,
    };

    /// <summary>
    /// Whether a position (world coordinates times 1,000) is on a map: inside its square, as the game draws it (2,048
    /// pixels a side at its size factor, around its offset), give or take a little for rounding.
    /// </summary>
    public static bool OnMap(int rawX, int rawY, LinkMapRow map) {
        if (map.SizeFactor == 0) {
            return false;
        }

        var scale = map.SizeFactor / 100f;
        bool Inside(int raw, short offset) {
            var pixel = (raw / 1000f + offset) * scale + 1024f;
            return pixel is >= -MapMargin and <= 2048f + MapMargin;
        }

        return Inside(rawX, map.OffsetX) && Inside(rawY, map.OffsetY);
    }

    /// <summary>
    /// The link's name in the recipient's own game data if it is one the game can show, or null: an item must be an Item
    /// row (high quality only if it can be, a collectable only if it is one) or an EventItem row; a map flag's territory
    /// and map must exist, the map be the territory's own (its default map) or the map's territory be the flag's (nearly
    /// half the game's territories, its duties and instanced copies of zones, use another's map), and the position be on
    /// it; a status must be a Status row; each with a name. A party finder listing is in no game data: null (its name is
    /// the sender's, see <see cref="MessagePart.Link.Name"/>).
    /// </summary>
    public static string? Check(ChatLink link, ILinkSheets sheets) {
        if (!IsWellFormed(link)) {
            return null;
        }

        var name = link switch {
            ChatLink.Item item => ItemName(item.RawId, sheets),
            ChatLink.MapFlag map => MapName(map, sheets),
            ChatLink.Status status => sheets.StatusName(status.StatusId),
            _ => null,
        };

        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static string? MapName(ChatLink.MapFlag map, ILinkSheets sheets) =>
        sheets.Map(map.MapId) is { } row && sheets.Territory(map.TerritoryId) is { } territory
        && (row.TerritoryId == map.TerritoryId || territory.MapId == map.MapId) && OnMap(map.RawX, map.RawY, row)
            ? territory.PlaceName
            : null;

    private static string? ItemName(uint rawId, ILinkSheets sheets) {
        if (ItemParts(rawId) is not var (id, kind)) {
            return null;
        }

        if (kind == ItemLinkKind.EventItem) {
            return sheets.EventItemName(id);
        }

        return sheets.Item(id) is { } row
               && (kind != ItemLinkKind.HighQuality || row.CanBeHq)
               && (kind != ItemLinkKind.Collectable || row.IsCollectable)
            ? row.Name
            : null;
    }
}

/// <summary>A link in a message: where its "[name]" is in the text (UTF-16 code units), and what it points at.</summary>
public sealed record MessageLink(int Start, int Length, ChatLink Target) {
    public int End => this.Start + this.Length;
}

/// <summary>
/// A message's text and the links in it. The text is the whole message as plain text, each link as its name in
/// brackets ("look [Potion]"): what an older client shows, and what is shown for a link that doesn't check out.
/// </summary>
public sealed record LinkedText(string Text, IReadOnlyList<MessageLink> Links) {
    public static LinkedText Plain(string text) => new(text, []);

    /// <summary>The text in order, cut into plain text and links (see <see cref="MessagePart"/>). The links must be valid (<see cref="MessageContent.ValidLinks"/>).</summary>
    public IReadOnlyList<MessagePart> Parts() {
        var parts = new List<MessagePart>();
        var at = 0;
        foreach (var link in this.Links) {
            if (link.Start > at) {
                parts.Add(new MessagePart.Text(this.Text[at..link.Start]));
            }

            parts.Add(new MessagePart.Link(link.Target, this.Text.Substring(link.Start, link.Length)));
            at = link.End;
        }

        if (at < this.Text.Length) {
            parts.Add(new MessagePart.Text(this.Text[at..]));
        }

        return parts;
    }

    /// <summary>
    /// <see cref="Parts"/> as shown: each piece of text sanitised like all remote text, and the whole within one length
    /// limit, as a message without links is (<see cref="TextSanitizer.MaxMessageLength"/>, a link counting as its
    /// "[name]"). What doesn't fit is cut, with "…".
    /// </summary>
    public IReadOnlyList<MessagePart> ShownParts(int budget = TextSanitizer.MaxMessageLength) {
        var shown = new List<MessagePart>();
        var left = budget;
        foreach (var part in this.Parts()) {
            if (part is MessagePart.Link link) {
                if (link.Shown.Length > left) {
                    shown.Add(new MessagePart.Text("…"));
                    break;
                }

                shown.Add(link);
                left -= link.Shown.Length;
                continue;
            }

            var text = TextSanitizer.Clean(((MessagePart.Text) part).Value, Math.Max(left, 0));
            if (text.Length > 0) {
                shown.Add(new MessagePart.Text(text));
            }

            // Cut: Clean ends it with "…" once it is past what is left.
            if (text.Length > left) {
                break;
            }

            left -= text.Length;
        }

        return shown;
    }

    public bool Equals(LinkedText? other) =>
        other != null && this.Text == other.Text && this.Links.SequenceEqual(other.Links);

    public override int GetHashCode() => HashCode.Combine(this.Text, this.Links.Count);
}

/// <summary>A piece of a message as shown: plain text, or a link.</summary>
public abstract record MessagePart {
    private MessagePart() {
    }

    public sealed record Text(string Value) : MessagePart;

    /// <param name="Shown">The sender's text for it, "[Potion]": shown, as plain text, if the link doesn't check out.</param>
    public sealed record Link(ChatLink Target, string Shown) : MessagePart {
        /// <summary>The sender's name for it: <see cref="Shown"/> without its brackets.</summary>
        private string SendersName => this.Shown.Length >= 2 ? this.Shown[1..^1] : this.Shown;

        /// <summary>
        /// What to show instead of the link: the sender's "[name]", sanitised like all remote text, or "[unknown link]" if
        /// nothing is left of the name.
        /// </summary>
        public string Fallback {
            get {
                var name = TextSanitizer.Name(this.SendersName).Trim();
                return name.Length == 0 ? UnknownLink : $"[{name}]";
            }
        }

        /// <summary>
        /// The name to show it as a link by, or null if it isn't shown as one (<see cref="Fallback"/> is shown instead): an
        /// item, a map flag or a status by the recipient's own game data (<see cref="ChatLinks.Check"/>), never the
        /// sender's text. A party finder listing, which no game data holds, by the sender's name for it, as plain text (no
        /// game formatting or icons, see <see cref="LinkText.Clean"/>): whoever shows it marks it as a listing, so a name
        /// like "Free gil" is still seen to be one, and it can only ever open a listing.
        /// </summary>
        public string? Name(ILinkSheets sheets) => this.Target is ChatLink.PartyFinder listing
            ? ChatLinks.IsWellFormed(listing) ? LinkText.Clean(this.SendersName) : null
            : ChatLinks.Check(this.Target, sheets);
    }

    public const string UnknownLink = "[unknown link]";
}

/// <summary>
/// The plaintext of a channel message (<see cref="Content"/>, inside the encrypted, signed ciphertext), with links. A
/// text message keeps its content kind and its plain text; links are an added field older clients skip, so they show
/// the text ("look [Potion]"). Nothing about it reaches the server.
/// </summary>
public static class MessageContent {
    public static Content Encode(LinkedText message) {
        var text = new TextContent { Text = message.Text };
        foreach (var link in message.Links) {
            var encoded = new TextLink { Start = (uint) link.Start, Length = (uint) link.Length };
            switch (link.Target) {
                case ChatLink.Item item:
                    encoded.Item = new ItemLink { RawId = item.RawId };
                    break;
                case ChatLink.MapFlag map:
                    encoded.Map = new MapLink { TerritoryId = map.TerritoryId, MapId = map.MapId, RawX = map.RawX, RawY = map.RawY };
                    break;
                case ChatLink.Status status:
                    encoded.Status = new StatusLink { StatusId = status.StatusId };
                    break;
                case ChatLink.PartyFinder listing:
                    encoded.PartyFinder = new PartyFinderLink { ListingId = listing.ListingId, HomeWorldOnly = listing.HomeWorldOnly };
                    break;
                default:
                    continue;
            }

            text.Links.Add(encoded);
        }

        return new Content { Text = text };
    }

    /// <summary>A text message's text and its valid links (see <see cref="ValidLinks"/>), or null for another content kind.</summary>
    public static LinkedText? Decode(Content content) {
        if (content.KindCase != Content.KindOneofCase.Text) {
            return null;
        }

        var text = content.Text.Text;
        return new LinkedText(text, ValidLinks(text, content.Text.Links));
    }

    /// <summary>
    /// The links of a received message that may be shown as links. None if there are more than
    /// <see cref="ChatLinks.MaxPerMessage"/> (no client of ours sends that many); otherwise each one whose kind is known,
    /// whose numbers are in range (<see cref="ChatLinks.IsWellFormed"/>), and that stands over a "[name]" in the text,
    /// after the one before it. The others are dropped, and their text shows as it is.
    /// </summary>
    public static IReadOnlyList<MessageLink> ValidLinks(string text, IReadOnlyList<TextLink> links) {
        if (links.Count == 0 || links.Count > ChatLinks.MaxPerMessage) {
            return [];
        }

        var valid = new List<MessageLink>();
        var after = 0L;
        foreach (var link in links) {
            long start = link.Start, end = (long) link.Start + link.Length;
            if (start < after || end > text.Length || link.Length < 2 || link.Length > ChatLinks.MaxTextLength
                || text[(int) start] != '[' || text[(int) end - 1] != ']') {
                continue;
            }

            ChatLink? target = link.TargetCase switch {
                TextLink.TargetOneofCase.Item => new ChatLink.Item(link.Item.RawId),
                TextLink.TargetOneofCase.Map => new ChatLink.MapFlag(link.Map.TerritoryId, link.Map.MapId, link.Map.RawX, link.Map.RawY),
                TextLink.TargetOneofCase.Status => new ChatLink.Status(link.Status.StatusId),
                TextLink.TargetOneofCase.PartyFinder => new ChatLink.PartyFinder(link.PartyFinder.ListingId, link.PartyFinder.HomeWorldOnly),
                _ => null,
            };
            if (target == null || !ChatLinks.IsWellFormed(target)) {
                continue;
            }

            valid.Add(new MessageLink((int) start, (int) link.Length, target));
            after = end;
        }

        return valid;
    }
}
