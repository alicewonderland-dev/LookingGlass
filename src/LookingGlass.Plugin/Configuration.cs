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

    /// <summary>WebSocket URL of the server. By default the project's own server.</summary>
    public string ServerUrl { get; set; } = DefaultServerUrl;

    /// <summary>The project's own server (Oracle Cloud, behind Tailscale Funnel on port 8443).</summary>
    public const string DefaultServerUrl = "wss://windup-relay-oracle.ancon-universe.ts.net:8443/ws";

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
    /// The one line about ChatTwo's "(Warning: …)" label and short commands has been shown: it follows "Now talking in"
    /// only the first time talking in a channel starts with ChatTwo loaded (see <see cref="StickyMessages.ChatTwoNote"/>).
    /// A new name for a new note: the ones shown before (saved as ChatTwoStickyNoteShown, then ChatTwoOwnCommandNoteShown)
    /// said every short command, then ChatTwo's own channel's, went to the channel, which is no longer so.
    /// </summary>
    public bool ChatTwoLabelNoteShown { get; set; }

    /// <summary>
    /// Verbose channel messages: say "Now talking in" and every "Stopped talking in". Off (the default, also for settings
    /// saved before it existed), only stops the player didn't choose are said (see <see cref="StickyMessages.SayEnded"/>):
    /// the server info bar and the chat box labels always show where typing goes.
    /// </summary>
    public bool VerboseChannelMessages { get; set; }

    /// <summary>
    /// "Show LookingGlass messages only in windows" (off by default, also for settings saved before it existed): no channel's
    /// messages or information lines go to game chat, whatever each channel's own setting says, and a channel no window
    /// shows opens in one. Warnings, and answers to what is typed in the chat box, still go there. See <see cref="WindowsOnly"/>.
    /// Each channel's own choice is kept as it was, for when it is turned off.
    /// </summary>
    public bool MessagesOnlyInWindows { get; set; }

    /// <summary>
    /// While <see cref="MessagesOnlyInWindows"/> is on, how a window is found for a channel no window shows: as a tab in the
    /// window used last (the default, also for settings saved before it existed), or a new window each time (channels caught
    /// up at login share one).
    /// </summary>
    public WindowOpening WindowOpening { get; set; }

    /// <summary>
    /// Keep a chat log on this computer (off by default): every channel's messages, encrypted, for channel windows to show
    /// again after the next login. One setting for every channel, character and server; each character's log for each
    /// server is kept apart. See <see cref="ChatLog"/>.
    /// </summary>
    public bool KeepChatLog { get; set; }

    /// <summary>
    /// The most room each chat log may take, in megabytes (see <see cref="ChatLogLimits"/>): the oldest messages go first.
    /// Read through <see cref="ChatLogMaxBytes"/>, which keeps a hand-edited value in range.
    /// </summary>
    public int ChatLogMegabytes { get; set; } = ChatLogLimits.DefaultMegabytes;

    /// <summary><see cref="ChatLogMegabytes"/> in bytes, within its range (a method, so it isn't saved with the settings).</summary>
    public long ChatLogMaxBytes() => ChatLogLimits.Bytes(this.ChatLogMegabytes);

    /// <summary>
    /// Person → name colour (0xRRGGBB), for their name in every channel's lines and in member lists, keyed as
    /// <see cref="Core.Client.NameColours.KeyOf"/> says (their user ID, which is their Lodestone ID). One for every
    /// character played on this computer, not per character. Settings saved before name colours have none. Kept when the
    /// person leaves a channel. Never sent to the server.
    /// </summary>
    public Dictionary<string, uint> NameColours {
        // Never null, even from a hand-edited file.
        get => this._nameColours ??= new Dictionary<string, uint>();
        set => this._nameColours = value;
    }

    private Dictionary<string, uint>? _nameColours;

    /// <summary>
    /// Local chat's colour (see <see cref="LocalChat"/>), as a UIColor row; 0 (the default, also for settings saved before
    /// local chat) is none. <see cref="LocalChatCustomColour"/> wins if both are set. One for every character. Never sent to
    /// the server.
    /// </summary>
    public ushort LocalChatColourRow { get; set; }

    /// <summary>Local chat's colour as a custom colour (0xRRGGBB), or null for none.</summary>
    public uint? LocalChatCustomColour { get; set; }

    /// <summary>Local chat's colour, or null for the default: only the [Local] tag coloured, in LookingGlass blue.</summary>
    public ChannelColour? LocalChatColour() => LocalChat.ColourOf(this.LocalChatColourRow, this.LocalChatCustomColour);

    /// <summary>Sets local chat's colour, or with null its default. A row clears a custom colour, and a custom colour a row.</summary>
    public void SetLocalChatColour(ChannelColour? colour) {
        this.LocalChatColourRow = colour is { IsCustom: false } row ? row.Row : (ushort) 0;
        this.LocalChatCustomColour = colour is { IsCustom: true } custom ? custom.Rgb : null;
    }

    /// <summary>Per character, keyed by content ID.</summary>
    public Dictionary<ulong, CharacterSettings> Characters { get; set; } = new();

    public CharacterSettings ForCharacter(ulong contentId) {
        if (!this.Characters.TryGetValue(contentId, out var settings) || settings == null) {
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

    /// <summary>
    /// Channel ID → custom colour (0xRRGGBB) for its chat lines and its place in the channel list, instead of a UIColor row
    /// (a channel is in only one of the two; see <see cref="Core.Client.ChannelColours"/>). Settings saved before custom
    /// colours have none. Never sent to the server.
    /// </summary>
    public Dictionary<string, uint> CustomChannelColours {
        // Never null, even from a hand-edited file.
        get => this._customChannelColours ??= new Dictionary<string, uint>();
        set => this._customChannelColours = value;
    }

    private Dictionary<string, uint>? _customChannelColours;


    /// <summary>
    /// Channels whose messages don't also go to the game's chat log ("Show in game chat" turned off): they show only in
    /// their channel windows. Every other channel's do. Never sent to the server.
    /// </summary>
    public HashSet<string> GameChatOff {
        // Never null, even from a hand-edited file.
        get => this._gameChatOff ??= new HashSet<string>();
        set => this._gameChatOff = value;
    }

    private HashSet<string>? _gameChatOff;

    /// <summary>Server address → the channel windows open there, to open again at the next login. See <see cref="ChannelWindowLayouts"/>.</summary>
    public Dictionary<string, List<ChannelWindowLayout>> ChannelWindows {
        get => this._channelWindows ??= new Dictionary<string, List<ChannelWindowLayout>>();
        set => this._channelWindows = value;
    }

    private Dictionary<string, List<ChannelWindowLayout>>? _channelWindows;

    /// <summary>The channel windows on a server address, or null if there are none (nothing is added to the settings for asking).</summary>
    public List<ChannelWindowLayout>? WindowsIfAny(string serverUrl) =>
        this.ChannelWindows.GetValueOrDefault(serverUrl);

    /// <summary>The channel windows on a server address, to add one to: a list is kept for it from now on.</summary>
    public List<ChannelWindowLayout> WindowsOn(string serverUrl) {
        if (this.ChannelWindows.GetValueOrDefault(serverUrl) is not { } windows) {
            windows = new List<ChannelWindowLayout>();
            this.ChannelWindows[serverUrl] = windows;
        }

        return windows;
    }

    /// <summary>Forgets a server address's list once it has no windows, so the settings hold no empty ones.</summary>
    public void DropEmptyWindowLists() {
        foreach (var (serverUrl, _) in this.ChannelWindows.Where(entry => entry.Value is not { Count: > 0 }).ToList()) {
            this.ChannelWindows.Remove(serverUrl);
        }
    }

    public int? SlotOf(string channelId) => this.ChannelSlots.TryGetValue(channelId, out var slot) ? slot : null;

    public string? ChannelInSlot(int slot) => CommandSlots.ChannelIn(this.ChannelSlots, slot);

    /// <summary>
    /// See <see cref="CommandSlots.Sync"/>, <see cref="ChannelNicknames.Sync"/>, <see cref="Core.Client.ChannelColours.Sync"/>
    /// and <see cref="GameChatChannels.Sync"/>: nothing changes until the snapshot holds the complete channel list. (Channel
    /// windows follow it in <see cref="Ui.ChannelWindows"/>, which knows the server address.)
    /// </summary>
    /// <returns>True if anything changed.</returns>
    public bool Sync(SessionSnapshot snapshot) {
        var slots = CommandSlots.Sync(this.ChannelSlots, snapshot, Configuration.SlotCount);
        var nicknames = ChannelNicknames.Sync(this.Nicknames, snapshot);
        var colours = Core.Client.ChannelColours.Sync(this.ChannelColours, this.CustomChannelColours, snapshot);
        var gameChat = GameChatChannels.Sync(this.GameChatOff, snapshot);
        return slots || nicknames || colours || gameChat;
    }

    /// <summary>Sets a channel's colour (a UIColor row or a custom colour), or with null its default.</summary>
    public void SetColour(string channelId, ChannelColour? colour) =>
        Core.Client.ChannelColours.Set(this.ChannelColours, this.CustomChannelColours, channelId, colour);

    /// <summary>Moves a channel to a slot, swapping with whatever was there.</summary>
    public void AssignSlot(string channelId, int slot) => CommandSlots.Assign(this.ChannelSlots, channelId, slot);

    /// <summary>Sets or (with an empty one) clears a channel's nickname.</summary>
    /// <returns>Why it was refused, or null.</returns>
    public string? SetNickname(string channelId, string nickname) => ChannelNicknames.Set(this.Nicknames, channelId, nickname);
}
