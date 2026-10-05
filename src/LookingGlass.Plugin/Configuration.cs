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

    /// <summary>A channel's colour is used for its whole chat line, not only the [LGC] tag.</summary>
    public bool ColourWholeLine { get; set; } = true;

    /// <summary>A channel with a nickname is tagged [nickname] in chat instead of [LGCn]. See <see cref="ChannelTag"/>.</summary>
    public bool NicknameTags { get; set; } = true;

    /// <summary>
    /// Show the encryption details: fingerprints to compare and mark verified, keys, and the technical words of warnings.
    /// Off (simple mode, the default, also for settings saved before it existed), the same warnings are said in everyday
    /// words (see <see cref="Wording"/>), and nothing technical is shown.
    /// </summary>
    public bool AdvancedMode { get; set; }

    /// <summary>
    /// The one line about ChatTwo's "(Warning: …)" label has been shown: it follows "Now talking in" only the first time
    /// talking in a channel starts with ChatTwo loaded (see <see cref="StickyMessages.ChatTwoNote"/>).
    /// </summary>
    public bool ChatTwoStickyNoteShown { get; set; }

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

    /// <summary>Channel ID → UIColor sheet row for its chat lines and its place in the channel list. Never sent to the server.</summary>
    public Dictionary<string, ushort> ChannelColours { get; set; } = new();

    public int? SlotOf(string channelId) => this.ChannelSlots.TryGetValue(channelId, out var slot) ? slot : null;

    public string? ChannelInSlot(int slot) => CommandSlots.ChannelIn(this.ChannelSlots, slot);

    /// <summary>
    /// See <see cref="CommandSlots.Sync"/>, <see cref="ChannelNicknames.Sync"/> and <see cref="Core.Client.ChannelColours.Sync"/>:
    /// nothing changes until the snapshot holds the complete channel list.
    /// </summary>
    /// <returns>True if anything changed.</returns>
    public bool Sync(SessionSnapshot snapshot) {
        var slots = CommandSlots.Sync(this.ChannelSlots, snapshot, Configuration.SlotCount);
        var nicknames = ChannelNicknames.Sync(this.Nicknames, snapshot);
        var colours = Core.Client.ChannelColours.Sync(this.ChannelColours, snapshot);
        return slots || nicknames || colours;
    }

    /// <summary>Sets a channel's colour (a UIColor row), or with null its default.</summary>
    public void SetColour(string channelId, ushort? colour) => Core.Client.ChannelColours.Set(this.ChannelColours, channelId, colour);

    /// <summary>Moves a channel to a slot, swapping with whatever was there.</summary>
    public void AssignSlot(string channelId, int slot) => CommandSlots.Assign(this.ChannelSlots, channelId, slot);

    /// <summary>Sets or (with an empty one) clears a channel's nickname.</summary>
    /// <returns>Why it was refused, or null.</returns>
    public string? SetNickname(string channelId, string nickname) => ChannelNicknames.Set(this.Nicknames, channelId, nickname);
}
