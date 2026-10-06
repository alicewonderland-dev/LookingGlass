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
    /// a marker for each item, map or status link, and those links (what each points at, and its name: the sender's own
    /// sheet's, else the link's text). Any other link (a player, a quest) is left as its text.
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

            // Only a link the player's own game can show goes as one; any other as its text.
            var sheetName = SheetName(target);
            var link = new TypedLink(sheetName == null ? null : target, sheetName ?? name.ToString());
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
    /// status; null if there is none, or it can't be read. Where the game keeps them is as ChatTwo 1.40.9 reads them for
    /// its input preview (<c>Message.DecodeTextParam</c>).
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
                return Checked(link, null);
            }
            default:
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

    /// <summary>The name of what a link points at in the player's own sheets, or null if it isn't one the game can show.</summary>
    private static string? SheetName(ChatLink? link) {
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
    /// Adds a received link to a chat line as the game's own interactive link, rebuilt from its ids only, if it checks out
    /// against the player's own sheets (<see cref="ChatLinks.Check"/>): an item as the game links one (its rarity's
    /// colour, its high-quality mark), a map flag with the link arrow, the place and coordinates, a status with the arrow
    /// and its name. The name is the player's own game's, never the sender's.
    /// </summary>
    /// <returns>False if it doesn't check out, or couldn't be built: then nothing was added.</returns>
    public static bool TryAppend(SeStringBuilder builder, ChatLink link) {
        try {
            if (ChatLinks.Check(link, Sheets) is not { } name) {
                return false;
            }

            var built = link switch {
                ChatLink.Item item => ItemLink(item),
                ChatLink.MapFlag map => SeString.CreateMapLink(map.TerritoryId, map.MapId, map.RawX, map.RawY),
                ChatLink.Status status => StatusLink(status, name),
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

        public string? PlaceName(uint territoryId) =>
            Services.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryId) is { } row ? row.PlaceName.ValueNullable?.Name.ExtractText() : null;

        public string? StatusName(uint id) => Services.Data.GetExcelSheet<Status>().GetRowOrDefault(id)?.Name.ExtractText();
    }
}
