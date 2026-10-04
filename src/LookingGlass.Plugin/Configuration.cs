using Dalamud.Configuration;
using Dalamud.Game.Text;
using LookingGlass.Core.Client;

namespace LookingGlass.Plugin;

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
    /// <summary>Channel ID → command slot (1 to 8, as in /lgc1).</summary>
    public Dictionary<string, int> ChannelSlots { get; set; } = new();

    public int? SlotOf(string channelId) => this.ChannelSlots.TryGetValue(channelId, out var slot) ? slot : null;

    public string? ChannelInSlot(int slot) => CommandSlots.ChannelIn(this.ChannelSlots, slot);

    /// <summary>See <see cref="CommandSlots.Sync"/>: nothing changes until the snapshot holds the complete channel list.</summary>
    /// <returns>True if anything changed.</returns>
    public bool SyncSlots(SessionSnapshot snapshot) => CommandSlots.Sync(this.ChannelSlots, snapshot, Configuration.SlotCount);

    /// <summary>Moves a channel to a slot, swapping with whatever was there.</summary>
    public void AssignSlot(string channelId, int slot) => CommandSlots.Assign(this.ChannelSlots, channelId, slot);
}
