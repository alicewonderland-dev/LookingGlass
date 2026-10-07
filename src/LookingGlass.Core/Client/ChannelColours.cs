using System.Collections.Immutable;

namespace LookingGlass.Core.Client;

/// <summary>
/// Local channel colours (see <see cref="ChannelColour"/>), kept as two maps from channel ID: one to a row of the game's
/// UIColor sheet (as every colour was kept before custom colours, so settings saved then read unchanged), and one to a
/// custom colour (0xRRGGBB). A channel is in at most one of them; if a hand-edited file has it in both, the custom colour
/// wins. Like nicknames (<see cref="ChannelNicknames"/>) they belong to one character, never go to the server, and stay
/// with their channel until it's gone. No entry means the default colour.
/// </summary>
public static class ChannelColours {
    /// <summary>A channel's colour from the merged map (see <see cref="Merge"/>), or null for the default.</summary>
    public static ChannelColour? Of(IReadOnlyDictionary<string, ChannelColour> colours, string channelId) =>
        colours.TryGetValue(channelId, out var colour) ? colour : null;

    /// <summary>A channel's colour from the two saved maps, or null for the default.</summary>
    /// <param name="custom">The custom colours; null (settings saved before custom colours, or a hand-edited file) is none.</param>
    public static ChannelColour? Of(IReadOnlyDictionary<string, ushort> rows, IReadOnlyDictionary<string, uint>? custom, string channelId) {
        if (custom != null && custom.TryGetValue(channelId, out var rgb)) {
            return ChannelColour.Custom(rgb);
        }

        return rows.TryGetValue(channelId, out var row) && row != 0 ? ChannelColour.OfRow(row) : null;
    }

    /// <summary>Every channel's colour, from the two saved maps (the custom colour winning if a channel is in both).</summary>
    public static ImmutableDictionary<string, ChannelColour> Merge(IReadOnlyDictionary<string, ushort> rows, IReadOnlyDictionary<string, uint>? custom) {
        var merged = ImmutableDictionary.CreateBuilder<string, ChannelColour>();
        foreach (var (channelId, row) in rows) {
            if (row != 0) {
                merged[channelId] = ChannelColour.OfRow(row);
            }
        }

        foreach (var (channelId, rgb) in custom ?? ImmutableDictionary<string, uint>.Empty) {
            merged[channelId] = ChannelColour.Custom(rgb);
        }

        return merged.ToImmutable();
    }

    /// <summary>
    /// Gives a channel a colour, or with null (or row 0, which has none) sets it back to the default. A row clears any
    /// custom colour it had, and a custom colour any row, so a channel is only ever in one map.
    /// </summary>
    public static void Set(Dictionary<string, ushort> rows, Dictionary<string, uint> custom, string channelId, ChannelColour? colour) {
        rows.Remove(channelId);
        custom.Remove(channelId);
        switch (colour) {
            case { IsCustom: true } c:
                custom[channelId] = c.Rgb;
                break;
            case { Row: not 0 } c:
                rows[channelId] = c.Row;
                break;
        }
    }

    /// <summary>
    /// Drops the colours of channels you're no longer in, from both maps. Does nothing until the snapshot holds the
    /// complete channel list: before that, a missing channel may simply not have been listed yet.
    /// </summary>
    /// <returns>True if anything changed.</returns>
    public static bool Sync(Dictionary<string, ushort> rows, Dictionary<string, uint> custom, SessionSnapshot snapshot) {
        var rowsChanged = ChannelMaps.DropGone(rows, snapshot);
        var customChanged = ChannelMaps.DropGone(custom, snapshot);
        return rowsChanged || customChanged;
    }
}

/// <summary>Per-channel local settings, keyed by channel ID.</summary>
internal static class ChannelMaps {
    /// <summary>Removes the entries of channels missing from a complete channel list; does nothing with a partial one.</summary>
    /// <returns>True if anything changed.</returns>
    public static bool DropGone<T>(Dictionary<string, T> map, SessionSnapshot snapshot) {
        if (snapshot.State != ConnectionState.Ready || !snapshot.ChannelsLoaded) {
            return false;
        }

        var listed = snapshot.Channels.Select(channel => channel.Id).ToHashSet();
        var changed = false;
        foreach (var gone in map.Keys.Where(id => !listed.Contains(id)).ToList()) {
            map.Remove(gone);
            changed = true;
        }

        return changed;
    }
}
