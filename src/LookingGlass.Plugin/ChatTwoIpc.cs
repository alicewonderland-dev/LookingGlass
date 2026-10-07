using Dalamud.Plugin.Ipc;
using LookingGlass.Core.Client;

namespace LookingGlass.Plugin;

/// <summary>
/// What sticky mode needs from ChatTwo, which replaces the game's chat window with its own. Everything here is optional:
/// with ChatTwo missing, older, or failing, sticky mode still never lets typed text reach game chat (see docs/design.md).
/// <list type="bullet">
/// <item><c>ChatTwo.GetChatInputState</c> (ChatTwo's typing IPC): whether its main input is on a /tell, which ChatTwo
/// sends itself, so talking in a channel isn't started there; and, for each line ChatTwo sends, its main input's channel
/// and what it holds, which tells its plain typing ("/p hi" for "hi" on Party) from a one-off short command typed there
/// (see <see cref="ChatTwoLine"/>). Only the main window's input: a pop-out's isn't reported, so a line that isn't the
/// main input's is held to the strict rule (see <see cref="ShortCommandRule"/>).</item>
/// <item><c>ExtraChat.OverrideChannelColour</c>: ChatTwo's only way for another plugin to name the channel its input
/// box shows. ChatTwo subscribes to it by that name (it was made for ExtraChat); LookingGlass sends its own channel's tag
/// on it while talking in a channel (again every second, in case ChatTwo missed it), and null as soon as it stops, checked
/// once a frame (see <c>StickyMode.SyncIndicators</c>). It changes what ChatTwo shows, not where ChatTwo sends.</item>
/// <item><c>ChatTwo.Available</c>: ChatTwo (re)loaded, so the override is sent again.</item>
/// </list>
/// Game thread only.
/// </summary>
internal sealed class ChatTwoIpc : IDisposable {
    private const string InternalName = "ChatTwo";
    private const string DisplayName = "Chat 2";

    private readonly ICallGateSubscriber<(bool, bool, bool, bool, int, ushort)> _inputState;
    private readonly ICallGateProvider<OverrideInfo, object> _override;
    private readonly ICallGateSubscriber<object?> _available;
    private OverrideInfo _current;

    public ChatTwoIpc() {
        var plugin = Services.PluginInterface;
        this._inputState = plugin.GetIpcSubscriber<(bool, bool, bool, bool, int, ushort)>("ChatTwo.GetChatInputState");
        this._override = plugin.GetIpcProvider<OverrideInfo, object>("ExtraChat.OverrideChannelColour");
        this._available = plugin.GetIpcSubscriber<object?>("ChatTwo.Available");
        this._available.Subscribe(this.OnAvailable);
    }

    /// <summary>ChatTwo is loaded: Dalamud lists it as loaded, or its typing IPC answers.</summary>
    public bool Loaded {
        get {
            try {
                if (Services.PluginInterface.InstalledPlugins.Any(plugin => plugin.IsLoaded && (plugin.InternalName == InternalName || plugin.Name == DisplayName))) {
                    return true;
                }
            } catch (Exception ex) {
                Services.Log.Warning(ex, "Couldn't list the loaded plugins");
            }

            return this.InputChannel() != null;
        }
    }

    /// <summary>The chat type (ChatTwo's numbering: 12 is a tell, <see cref="ChatChannelPrefixes.ChatTwoTell"/>) ChatTwo's input sends to, or null if it doesn't say.</summary>
    public int? InputChannel() => this.InputState()?.ChatType;

    /// <summary>
    /// ChatTwo's main input now, or null if ChatTwo doesn't say (not loaded, or a version without the typing IPC). The IPC's
    /// tuple (ChatTwo 1.40.9, <c>Ipc/TypingIpc.cs</c>): input visible, focused, has text (more than spaces), typing, the
    /// input's length as typed, and the chat type of the channel a line typed there now goes to (its current tab's, or its
    /// one-off channel; ChatTwo's own <c>ChatType</c>, a ushort enum, which Dalamud converts to the ushort asked for).
    /// </summary>
    public (int ChatType, bool HasText, int TextLength)? InputState() {
        try {
            var state = this._inputState.InvokeFunc();
            return (state.Item6, state.Item3, state.Item5);
        } catch (Exception) {
            return null;
        }
    }

    /// <summary>
    /// Shows <paramref name="label"/> as ChatTwo's input channel, or with null, its own again. ChatTwo draws it in
    /// <paramref name="rgba"/> exactly (0xRRGGBBAA, as UIColor rows are packed), so a custom colour needs no fallback here.
    /// </summary>
    public void SetChannelLabel(string? label, uint rgba) {
        this._current = new OverrideInfo { Channel = label, UiColour = 0, Rgba = rgba };
        this.Send();
    }

    private void OnAvailable() {
        if (this._current.Channel != null) {
            this.Send();
        }
    }

    private void Send() {
        try {
            this._override.SendMessage(this._current);
        } catch (Exception ex) {
            Services.Log.Warning(ex, "Couldn't tell ChatTwo which channel its input is in");
        }
    }

    public void Dispose() {
        this._available.Unsubscribe(this.OnAvailable);
        if (this._current.Channel != null) {
            this.SetChannelLabel(null, 0);
        }
    }

    /// <summary>The message ChatTwo expects on <c>ExtraChat.OverrideChannelColour</c>: Dalamud matches its fields by name.</summary>
    [Serializable]
    private struct OverrideInfo {
        public string? Channel;
        public ushort UiColour;
        public uint Rgba;
    }
}
