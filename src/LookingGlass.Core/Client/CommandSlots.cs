using System.Globalization;

namespace LookingGlass.Core.Client;

/// <summary>
/// The rules for the plugin's numbered channel commands (/lgc1 to /lgc50), as a
/// map from channel ID to slot. Kept free of game types so they can be tested.
/// A slot stays with its channel until that channel is gone, across restarts.
/// </summary>
public static class CommandSlots {
    /// <summary>The channel commands: /lgc1 to /lgc50, and /lgc &lt;nickname&gt; (see <see cref="ChannelNicknames"/>).</summary>
    public const string Prefix = "/lgc";

    /// <summary>One slot for every channel the server lets a user be in (its default <c>MaxChannelsPerUser</c>).</summary>
    public const int Count = 50;

    /// <summary>The slot of a numbered command such as /lgc12, or null if it isn't one.</summary>
    public static int? SlotOfCommand(string command) {
        if (!command.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) {
            return null;
        }

        var digits = command.AsSpan(Prefix.Length);
        if (digits.IsEmpty || digits[0] == '0' || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var slot)) {
            return null;
        }

        return slot <= Count ? slot : null;
    }
    public static string? ChannelIn(IReadOnlyDictionary<string, int> slots, int slot) {
        foreach (var (channelId, assigned) in slots) {
            if (assigned == slot) {
                return channelId;
            }
        }

        return null;
    }

    /// <summary>
    /// Frees the slots of channels you're no longer in, and gives new channels a free slot.
    /// Does nothing until the snapshot holds the complete channel list: before that, a
    /// missing channel may simply not have been listed yet.
    /// </summary>
    /// <returns>True if anything changed.</returns>
    public static bool Sync(Dictionary<string, int> slots, SessionSnapshot snapshot, int slotCount) {
        if (snapshot.State != ConnectionState.Ready || !snapshot.ChannelsLoaded) {
            return false;
        }

        var listed = snapshot.Channels.Select(channel => channel.Id).ToList();
        var changed = false;
        foreach (var gone in slots.Keys.Where(id => !listed.Contains(id)).ToList()) {
            slots.Remove(gone);
            changed = true;
        }

        foreach (var id in listed.Where(id => !slots.ContainsKey(id))) {
            var free = Enumerable.Range(1, slotCount).FirstOrDefault(slot => ChannelIn(slots, slot) == null);
            if (free == 0) {
                break;
            }

            slots[id] = free;
            changed = true;
        }

        return changed;
    }

    /// <summary>Moves a channel to a slot, swapping with whatever was there.</summary>
    public static void Assign(Dictionary<string, int> slots, string channelId, int slot) {
        var previous = ChannelIn(slots, slot);
        var oldSlot = slots.TryGetValue(channelId, out var held) ? held : (int?) null;
        if (previous != null && previous != channelId) {
            if (oldSlot != null) {
                slots[previous] = oldSlot.Value;
            } else {
                slots.Remove(previous);
            }
        }

        slots[channelId] = slot;
    }
}
