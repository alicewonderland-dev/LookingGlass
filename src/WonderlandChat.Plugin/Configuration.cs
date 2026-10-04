using Dalamud.Configuration;
using Dalamud.Game.Text;

namespace WonderlandChat.Plugin;

/// <summary>Plugin settings. Secrets are never stored here; see <see cref="ProtectedSecretStore"/>.</summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration {
    public const int SlotCount = 8;

    public int Version { get; set; } = 1;

    /// <summary>WebSocket URL of the server, for example ws://my-vm.tailnet.ts.net:5180/ws.</summary>
    public string ServerUrl { get; set; } = "ws://127.0.0.1:5180/ws";

    public bool AutoConnect { get; set; } = true;

    public XivChatType ChatType { get; set; } = XivChatType.Debug;

    /// <summary>Per character, keyed by content ID.</summary>
    public Dictionary<ulong, CharacterSettings> Characters { get; set; } = new();

    public CharacterSettings ForCharacter(ulong contentId) {
        if (!this.Characters.TryGetValue(contentId, out var settings)) {
            settings = new CharacterSettings();
            this.Characters[contentId] = settings;
        }

        return settings;
    }

    public void Save() => Services.PluginInterface.SavePluginConfig(this);
}

[Serializable]
public sealed class CharacterSettings {
    /// <summary>Channel ID → command slot (1 to 8, as in /wcl1).</summary>
    public Dictionary<string, int> ChannelSlots { get; set; } = new();

    public int? SlotOf(string channelId) => this.ChannelSlots.TryGetValue(channelId, out var slot) ? slot : null;

    public string? ChannelInSlot(int slot) {
        foreach (var (channelId, assigned) in this.ChannelSlots) {
            if (assigned == slot) {
                return channelId;
            }
        }

        return null;
    }

    /// <summary>Gives every listed channel a slot if one is free, and forgets channels no longer listed.</summary>
    /// <returns>True if anything changed.</returns>
    public bool SyncSlots(IEnumerable<string> channelIds) {
        var ids = channelIds.ToList();
        var changed = false;

        foreach (var stale in this.ChannelSlots.Keys.Where(id => !ids.Contains(id)).ToList()) {
            this.ChannelSlots.Remove(stale);
            changed = true;
        }

        foreach (var id in ids.Where(id => !this.ChannelSlots.ContainsKey(id))) {
            var free = Enumerable.Range(1, Configuration.SlotCount).FirstOrDefault(slot => this.ChannelInSlot(slot) == null);
            if (free == 0) {
                break;
            }

            this.ChannelSlots[id] = free;
            changed = true;
        }

        return changed;
    }

    /// <summary>Moves a channel to a slot, swapping with whatever was there.</summary>
    public void AssignSlot(string channelId, int slot) {
        var previous = this.ChannelInSlot(slot);
        var oldSlot = this.SlotOf(channelId);
        if (previous != null && previous != channelId) {
            if (oldSlot != null) {
                this.ChannelSlots[previous] = oldSlot.Value;
            } else {
                this.ChannelSlots.Remove(previous);
            }
        }

        this.ChannelSlots[channelId] = slot;
    }
}
