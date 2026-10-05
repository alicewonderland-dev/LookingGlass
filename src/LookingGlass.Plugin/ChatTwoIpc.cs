using Dalamud.Plugin.Ipc;
using LookingGlass.Core.Client;

namespace LookingGlass.Plugin;

/// <summary>
/// What sticky mode needs from ChatTwo, which replaces the game's chat window with its own. Everything here is optional:
/// with ChatTwo missing, older, or failing, sticky mode still never lets typed text reach game chat (see docs/design.md).
/// <list type="bullet">
/// <item><c>ChatTwo.GetChatInputState</c> (ChatTwo's typing IPC): the channel its input box sends to, so the command
/// ChatTwo puts in front of plain text ("/s hello") is recognised.</item>
/// <item><c>ExtraChat.OverrideChannelColour</c>: ChatTwo's only way for another plugin to name the channel its input
/// box shows. ChatTwo subscribes to it by that name (it was made for ExtraChat); LookingGlass sends its own channel's tag
/// on it while talking in a channel, and null when it stops. It changes what ChatTwo shows, not where ChatTwo sends.</item>
/// <item><c>ChatTwo.Available</c>: ChatTwo (re)loaded, so the override is sent again.</item>
/// </list>
/// Game thread only.
/// </summary>
internal sealed class ChatTwoIpc : IDisposable {
    private const string InternalName = "ChatTwo";

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
                if (Services.PluginInterface.InstalledPlugins.Any(plugin => plugin.IsLoaded && plugin.InternalName == InternalName)) {
                    return true;
                }
            } catch (Exception ex) {
                Services.Log.Warning(ex, "Couldn't list the loaded plugins");
            }

            return this.InputChannel() != null;
        }
    }

    /// <summary>The chat type (ChatTwo's numbering, see <see cref="ChatChannelPrefixes.ForChatTwoChatType"/>) ChatTwo's input sends to, or null if it doesn't say.</summary>
    public int? InputChannel() {
        try {
            return this._inputState.InvokeFunc().Item6;
        } catch (Exception) {
            // Not loaded, or a version without the typing IPC.
            return null;
        }
    }

    /// <summary>Shows <paramref name="label"/> as ChatTwo's input channel (in a UIColor row's colour), or with null, its own again.</summary>
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
