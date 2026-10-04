using Dalamud.Configuration;
using Dalamud.Game.Text;
using LookingGlass.Core.Client;

namespace LookingGlass.Plugin;

/// <summary>Plugin settings. Secrets are never stored here; see <see cref="ProtectedSecretStore"/>.</summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration {
    /// <summary>/lgc1 to /lgc50: one for every channel the server lets a user be in.</summary>
    public const int SlotCount = CommandSlots.Count;

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
    /// <summary>Channel ID → command slot (1 to 50, as in /lgc1).</summary>
    public Dictionary<string, int> ChannelSlots { get; set; } = new();

    /// <summary>Channel ID → local nickname, as in /lgc sky. Never sent to the server.</summary>
    public Dictionary<string, string> Nicknames { get; set; } = new();

    public int? SlotOf(string channelId) => this.ChannelSlots.TryGetValue(channelId, out var slot) ? slot : null;

    public string? ChannelInSlot(int slot) => CommandSlots.ChannelIn(this.ChannelSlots, slot);

    /// <summary>
    /// See <see cref="CommandSlots.Sync"/> and <see cref="ChannelNicknames.Sync"/>: nothing
    /// changes until the snapshot holds the complete channel list.
    /// </summary>
    /// <returns>True if anything changed.</returns>
    public bool Sync(SessionSnapshot snapshot) {
        var slots = CommandSlots.Sync(this.ChannelSlots, snapshot, Configuration.SlotCount);
        var nicknames = ChannelNicknames.Sync(this.Nicknames, snapshot);
        return slots || nicknames;
    }

    /// <summary>Moves a channel to a slot, swapping with whatever was there.</summary>
    public void AssignSlot(string channelId, int slot) => CommandSlots.Assign(this.ChannelSlots, channelId, slot);

    /// <summary>Sets or (with an empty one) clears a channel's nickname.</summary>
    /// <returns>Why it was refused, or null.</returns>
    public string? SetNickname(string channelId, string nickname) => ChannelNicknames.Set(this.Nicknames, channelId, nickname);
}
