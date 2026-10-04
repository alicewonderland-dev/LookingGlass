namespace LookingGlass.Core.Client;

/// <summary>
/// Local channel colours, as a map from channel ID to a row of the game's UIColor sheet. Like
/// nicknames (<see cref="ChannelNicknames"/>) they belong to one character, never go to the
/// server, and stay with their channel until it's gone. No entry means the default colour.
/// </summary>
public static class ChannelColours {
    public static ushort? Of(IReadOnlyDictionary<string, ushort> colours, string channelId) {
        return colours.TryGetValue(channelId, out var colour) ? colour : null;
    }

    /// <summary>Gives a channel a colour, or with null (or row 0, which has none) sets it back to the default.</summary>
    public static void Set(Dictionary<string, ushort> colours, string channelId, ushort? colour) {
        if (colour is null or 0) {
            colours.Remove(channelId);
        } else {
            colours[channelId] = colour.Value;
        }
    }

    /// <summary>
    /// Drops the colours of channels you're no longer in. Does nothing until the snapshot holds
    /// the complete channel list: before that, a missing channel may simply not have been listed yet.
    /// </summary>
    /// <returns>True if anything changed.</returns>
    public static bool Sync(Dictionary<string, ushort> colours, SessionSnapshot snapshot) => ChannelMaps.DropGone(colours, snapshot);
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
