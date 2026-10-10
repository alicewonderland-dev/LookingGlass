using System.Text;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using LookingGlass.Core.Client;
using Lumina.Excel.Sheets;
using InventoryItem = FFXIVClientStructs.FFXIV.Client.Game.InventoryItem;

namespace LookingGlass.Plugin;

/// <summary>
/// The game's side of links in channel messages (the rules are in Core: <see cref="LinkText"/>, <see cref="ChatLinks"/>,
/// <see cref="MessageContent"/>). Reading: a typed line's link bytes and placeholders into <see cref="TypedLink"/>s.
/// Showing: a checked link rebuilt from its ids with Dalamud's own link builders, never from bytes that came over the
/// network. Game thread only (the agents, and Dalamud's link builders).
/// </summary>
internal static unsafe class GameLinks {
    /// <summary>The game's sheets, as checking a received link needs them, in the client's language.</summary>
    public static readonly ILinkSheets Sheets = new GameSheets();

    /// <summary>
    /// A line's bytes as LookingGlass reads it: its text (as Dalamud reads it: auto-translate phrases as their text), with
    /// a marker for each item, map, status or party finder link, and those links (what each points at, and its name: the
    /// sender's own sheet's, else the link's text; a listing's is always its text, "Looking for Party (name)"). Any other
    /// link (a player, a quest, the party finder's "advanced search" notice) is left as its text.
    /// </summary>
    public static TypedLine ReadLine(ReadOnlySpan<byte> raw) {
        var text = new StringBuilder();
        var links = new List<TypedLink>();
        ChatLink? target = null;
        StringBuilder? name = null;

        void End() {
            if (name == null) {
                return;
            }

            // Only a link the player's own game can show goes as one; any other as its text. A listing is in no sheet: it
            // goes by its text.
            var link = target is ChatLink.PartyFinder ? new TypedLink(target, name.ToString())
                : SheetName(target) is { } sheetName ? new TypedLink(target, sheetName)
                : new TypedLink(null, name.ToString());
            if (links.Count < LinkText.MaxMarkers) {
                text.Append(LinkText.Marker(links.Count));
                links.Add(link);
            } else if (LinkText.Clean(link.Name) is { } plain) {
                text.Append($"[{plain}]");
            }

            (target, name) = (null, null);
        }

        foreach (var payload in SeString.Parse(raw).Payloads) {
            ChatLink? starts = payload switch {
                ItemPayload item => new ChatLink.Item(item.RawItemId),
                MapLinkPayload map => new ChatLink.MapFlag(map.TerritoryType.RowId, map.Map.RowId, map.RawX, map.RawY),
                StatusPayload status => new ChatLink.Status(status.Status.RowId),
                // The "advanced search results" notice is a party finder link to no listing: left as its text. So is a link
                // whose flag Dalamud doesn't know, which it reads as the notice.
                PartyFinderPayload { LinkType: not PartyFinderPayload.PartyFinderLinkType.PartyFinderNotification } listing =>
                    new ChatLink.PartyFinder(listing.ListingId, listing.LinkType == PartyFinderPayload.PartyFinderLinkType.LimitedToHomeWorld),
                _ => null,
            };
            if (starts != null) {
                End();
                (target, name) = (starts, new StringBuilder());
            } else if (payload is RawPayload terminator && terminator.Equals(RawPayload.LinkTerminator)) {
                End();
            } else if (payload is ITextProvider provider) {
                (name ?? text).Append(LinkText.StripMarkers(provider.Text));
            }
        }

        End();
        return new TypedLine(text.ToString(), links);
    }

    /// <summary>
    /// What a link placeholder in the chat input stands for now (see <see cref="LinkText.ResolvePlaceholders"/>):
    /// "&lt;item&gt;" the item the chat log agent holds as linked, "&lt;flag&gt;" the map flag, "&lt;status&gt;" the
    /// status, "&lt;pfinder&gt;" the party finder listing; null if there is none, or it can't be read. Where the game keeps
    /// the first three is as ChatTwo 1.40.9 reads them for its input preview (<c>Message.DecodeTextParam</c>); the listing
    /// is beside them (FFXIVClientStructs' <c>AgentChatLog.LinkedPartyFinderId</c>; ChatTwo doesn't preview it).
    /// </summary>
    public static TypedLink? Placeholder(string placeholder) {
        switch (placeholder) {
            case "<item>": {
                var agent = AgentChatLog.Instance();
                if (agent == null) {
                    return null;
                }

                // The fields, not a call into the game. A symbolic item (a link to another) holds no id there.
                var item = ItemOf(agent->LinkedItem.IsSymbolic ? 0 : agent->LinkedItem.ItemId, agent->LinkedItem.Flags);
                return Checked(item, TextOf(agent->LinkedItemName));
            }
            case "<status>": {
                var agent = AgentChatLog.Instance();
                if (agent == null) {
                    return null;
                }

                var status = agent->ContextStatusId == 0 ? null : new ChatLink.Status(agent->ContextStatusId);
                return Checked(status, TextOf(agent->ContextStatusName));
            }
            case "<flag>": {
                var map = AgentMap.Instance();
                if (map == null || map->FlagMarkerCount == 0) {
                    return null;
                }

                // As the game and ChatTwo make a map link of the flag: world coordinates, to a thousandth, times 1,000.
                var flag = map->FlagMapMarkers[0];
                var link = new ChatLink.MapFlag(flag.TerritoryId, flag.MapId,
                    (int) (MathF.Round(flag.XFloat, 3, MidpointRounding.AwayFromZero) * 1000),
                    (int) (MathF.Round(flag.YFloat, 3, MidpointRounding.AwayFromZero) * 1000));
                // A flag the game can't show as a link (it shouldn't happen) still goes, as its place.
                return Checked(link, LinkText.FlagName(Sheets, flag.TerritoryId));
            }
            case "<pfinder>": {
                var agent = AgentChatLog.Instance();
                if (agent == null) {
                    return null;
                }

                // The fields, as for <item>: the listing, and its leader's name. No sheet holds a listing, so its name is
                // the game's own text for its link, "Looking for Party (name)", else the leader's name.
                var id = agent->LinkedPartyFinderId;
                var leader = TextOf(agent->LinkedPartyFinderLeaderName);
                var listing = id is > 0 and <= uint.MaxValue ? new ChatLink.PartyFinder((uint) id, HomeWorldOnly(id)) : null;
                return LinkText.PlaceholderLink(listing, listing != null && leader != null ? PartyFinderText(listing, leader) : null, leader);
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// Whether a listing is limited to its leader's home world: if it is the one the party finder showed last (where its
    /// chat button is), its "world" search area; else if it is the player's own recruitment, its criteria as last set
    /// ("limit recruiting to world", 0 when on, as FFXIVClientStructs has it). Otherwise it isn't known, and taken as
    /// open to other worlds, as most listings are; that only changes whether the link shows the cross-world mark.
    /// </summary>
    private static bool HomeWorldOnly(ulong listingId) {
        var agent = AgentLookingForGroup.Instance();
        if (agent == null) {
            return false;
        }

        if (agent->LastViewedListing.ListingId == listingId) {
            return agent->LastViewedListing.JoinConditionFlags.HasFlag(AgentLookingForGroup.JoinCondition.World);
        }

        return agent->OwnListingId != 0 && agent->OwnListingId == listingId && agent->StoredRecruitmentInfo.LimitRecruitingToWorld == 0;
    }

    /// <summary>A listing's link text as the game writes it, in the player's language (Dalamud's <c>CreatePartyFinderLink</c>), or null.</summary>
    private static string? PartyFinderText(ChatLink.PartyFinder listing, string leader) {
        try {
            return SeString.CreatePartyFinderLink(listing.ListingId, leader, !listing.HomeWorldOnly).TextValue;
        } catch (Exception ex) {
            Services.Log.Warning(ex, "Couldn't write a party finder link's text");
            return null;
        }
    }

    /// <summary>
    /// The linked item's raw id: its row, with the high-quality (+1,000,000) or collectable (+500,000) offset, which the
    /// id may already carry or the item's flags say; a key item (2,000,000 and up) as it is.
    /// </summary>
    private static ChatLink.Item? ItemOf(uint id, InventoryItem.ItemFlags flags) {
        if (id == 0) {
            return null;
        }

        if (id >= ChatLinks.EventItemStart) {
            return new ChatLink.Item(id);
        }

        var parts = ChatLinks.ItemParts(id);
        if (parts is not var (row, kind)) {
            return null;
        }

        kind = kind != ItemLinkKind.Normal ? kind
            : flags.HasFlag(InventoryItem.ItemFlags.HighQuality) ? ItemLinkKind.HighQuality
            : flags.HasFlag(InventoryItem.ItemFlags.Collectable) ? ItemLinkKind.Collectable
            : kind;
        return new ChatLink.Item(ChatLinks.RawItemId(row, kind));
    }

    /// <summary>
    /// A placeholder's link (see <see cref="LinkText.PlaceholderLink"/>): its target only if the player's own game can
    /// show it, named from the player's own sheets, else by the game's text for it.
    /// </summary>
    private static TypedLink? Checked(ChatLink? target, string? gameText) {
        var sheetName = SheetName(target);
        return LinkText.PlaceholderLink(sheetName == null ? null : target, sheetName, gameText);
    }

    /// <summary>
    /// The name of what a link points at in the player's own sheets, or null if it isn't one the game can show: the
    /// checks a received link must pass (see <see cref="ChatLinks.Check"/>), and for a map flag its place and coordinates.
    /// </summary>
    internal static string? SheetName(ChatLink? link) {
        if (link == null) {
            return null;
        }

        try {
            return link is ChatLink.MapFlag map ? MapName(map) : ChatLinks.Check(link, Sheets);
        } catch (Exception ex) {
            Services.Log.Warning(ex, "Couldn't read a link's name");
            return null;
        }
    }

    /// <summary>A map flag's name as a map link shows it: the place, and the coordinates.</summary>
    private static string? MapName(ChatLink.MapFlag map) {
        if (ChatLinks.Check(map, Sheets) == null) {
            return null;
        }

        var payload = new MapLinkPayload(map.TerritoryId, map.MapId, map.RawX, map.RawY);
        return $"{payload.PlaceName} {payload.CoordinateString}";
    }

    private static string? TextOf(Utf8String text) {
        var span = text.AsSpan();
        return span.IsEmpty ? null : SeString.Parse(span).TextValue;
    }

    /// <summary>
    /// The name a channel window shows a received link by, or null if it isn't shown as one: an item's or a status's in
    /// the player's own sheets, a map flag's place and coordinates (see <see cref="SheetName"/>), a party finder listing's
    /// the sender's, as plain text (<see cref="MessagePart.Link.Name"/>).
    /// </summary>
    internal static string? ShownName(MessagePart.Link link) =>
        link.Target is ChatLink.PartyFinder ? link.Name(Sheets) : SheetName(link.Target);

    /// <summary>
    /// Adds a received link to a chat line as the game's own interactive link, rebuilt from its ids only, if it checks out
    /// (<see cref="MessagePart.Link.Name"/>): an item as the game links one (its rarity's colour, its high-quality mark),
    /// a map flag with the link arrow, the place and coordinates, a status with the arrow and its name, all named by the
    /// player's own game, never the sender; a party finder listing with the arrow, the party finder's mark and the
    /// sender's name for it (see <see cref="PartyFinderLink"/>).
    /// </summary>
    /// <returns>False if it doesn't check out, or couldn't be built: then nothing was added.</returns>
    public static bool TryAppend(SeStringBuilder builder, MessagePart.Link link) {
        try {
            if (link.Name(Sheets) is not { } name) {
                return false;
            }

            var built = link.Target switch {
                ChatLink.Item item => ItemLink(item),
                ChatLink.MapFlag map => SeString.CreateMapLink(map.TerritoryId, map.MapId, map.RawX, map.RawY),
                ChatLink.Status status => StatusLink(status, name),
                ChatLink.PartyFinder listing => PartyFinderLink(listing, name),
                _ => null,
            };
            if (built == null) {
                return false;
            }

            builder.Append(built);
            return true;
        } catch (Exception ex) {
            Services.Log.Warning(ex, "Couldn't build a link");
            return false;
        }
    }

    /// <summary>
    /// Opens the map at a received map flag, as clicking the game's own map link does (Dalamud's <c>OpenMapWithMapLink</c>),
    /// if it checks out against the player's own sheets. Game thread.
    /// </summary>
    public static void OpenMap(ChatLink.MapFlag map) {
        try {
            if (ChatLinks.Check(map, Sheets) != null) {
                Services.GameGui.OpenMapWithMapLink(new MapLinkPayload(map.TerritoryId, map.MapId, map.RawX, map.RawY));
            }
        } catch (Exception ex) {
            Services.Log.Warning(ex, "Couldn't open the map at a link");
        }
    }

    /// <summary>
    /// Opens a received party finder listing, as clicking the game's own link does: the party finder's
    /// <c>AgentLookingForGroup.OpenListing</c>, which ChatTwo calls for one. A listing that has ended, or that the player's
    /// data centre can't see, is the game's to answer, as it is for its own link. Game thread.
    /// </summary>
    public static void OpenPartyFinder(ChatLink.PartyFinder listing) {
        try {
            var agent = AgentLookingForGroup.Instance();
            if (agent != null && ChatLinks.IsWellFormed(listing)) {
                agent->OpenListing(listing.ListingId);
            }
        } catch (Exception ex) {
            Services.Log.Warning(ex, "Couldn't open a party finder listing");
        }
    }

    private static SeString ItemLink(ChatLink.Item item) {
        var (id, kind) = ItemUtil.GetBaseId(item.RawId);
        return SeString.CreateItemLink(id, kind);
    }

    /// <summary>A status link as ChatTwo previews one: the link, the arrow, a buff or debuff mark, the name, the end of the link.</summary>
    private static SeString StatusLink(ChatLink.Status status, string name) {
        var category = Services.Data.GetExcelSheet<Status>().GetRowOrDefault(status.StatusId)?.StatusCategory ?? 0;
        var mark = category switch {
            1 => ((char) SeIconChar.Buff).ToString(),
            2 => ((char) SeIconChar.Debuff).ToString(),
            _ => "",
        };

        return new SeStringBuilder()
            .Add(new StatusPayload(status.StatusId))
            .Append(SeString.TextArrowPayloads)
            .AddText(mark + name)
            .Add(RawPayload.LinkTerminator)
            .Build();
    }

    /// <summary>
    /// A party finder link as the game makes one: the link, the arrow, its text, the end of the link, as Dalamud's
    /// <c>CreatePartyFinderLink</c> puts them together. There the text is the Addon sheet's row 2265 evaluated with the
    /// leader's name and whether the listing is open to other worlds, which adds the cross-world mark after it if so;
    /// here the text is the sender's, so the cross-world mark is added the same way, and the party finder's mark goes
    /// before the text: whatever it says, it is seen to be a listing.
    /// </summary>
    private static SeString PartyFinderLink(ChatLink.PartyFinder listing, string name) {
        var builder = new SeStringBuilder()
            .Add(new PartyFinderPayload(listing.ListingId, listing.HomeWorldOnly
                ? PartyFinderPayload.PartyFinderLinkType.LimitedToHomeWorld
                : PartyFinderPayload.PartyFinderLinkType.NotSpecified))
            .Append(SeString.TextArrowPayloads)
            .AddIcon(BitmapFontIcon.LookingForParty)
            .AddText(name);
        if (!listing.HomeWorldOnly) {
            builder.AddText(" ").AddIcon(BitmapFontIcon.CrossWorld);
        }

        return builder.Add(RawPayload.LinkTerminator).Build();
    }

    /// <summary>The sheets in the client's language, through Dalamud's data manager.</summary>
    private sealed class GameSheets : ILinkSheets {
        public LinkItemRow? Item(uint id) =>
            Services.Data.GetExcelSheet<Item>().GetRowOrDefault(id) is { } row ? new LinkItemRow(row.Name.ExtractText(), row.CanBeHq, row.IsCollectable) : null;

        public string? EventItemName(uint id) =>
            Services.Data.GetExcelSheet<EventItem>().GetRowOrDefault(id) is not null ? ItemUtil.GetItemName(id, false).ExtractText() : null;

        public LinkMapRow? Map(uint id) =>
            Services.Data.GetExcelSheet<Map>().GetRowOrDefault(id) is { } row
                ? new LinkMapRow(row.TerritoryType.RowId, row.SizeFactor, row.OffsetX, row.OffsetY)
                : null;

        public LinkTerritoryRow? Territory(uint id) =>
            Services.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(id) is { } row
                ? new LinkTerritoryRow(row.PlaceName.ValueNullable?.Name.ExtractText(), row.Map.RowId)
                : null;

        public string? StatusName(uint id) => Services.Data.GetExcelSheet<Status>().GetRowOrDefault(id)?.Name.ExtractText();
    }
}
