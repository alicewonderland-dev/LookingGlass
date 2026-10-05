using System.Text;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using LookingGlass.Core.Client;

namespace LookingGlass.Plugin;

/// <summary>Told what the game's chat box submits and when its channel is switched. Called on the game thread.</summary>
internal interface IChatBoxListener {
    /// <summary>True while submitted text may have to be kept from the game. A field read, checked before anything else runs.</summary>
    bool Active { get; }

    /// <summary>Decides what happens to a submitted line (its raw bytes, payloads and all).</summary>
    /// <returns>True to keep it from the game.</returns>
    bool KeepFromGame(byte[] message);

    /// <summary><see cref="KeepFromGame"/> threw while <see cref="Active"/>: the line was kept from the game.</summary>
    void Failed(Exception ex);

    /// <summary>Something asked the game to switch its chat channel (after the game did so).</summary>
    void ChannelChangeRequested();
}

/// <summary>
/// The one module that touches the game's chat functions, for sticky mode (see <see cref="StickyMode"/>). Addresses
/// come from FFXIVClientStructs, resolved by Dalamud; a missing one disables sticky mode only.
/// <list type="bullet">
/// <item><c>UIModule.ProcessChatBoxEntry</c>: what the chat box calls with the line typed when Enter is pressed (and
/// what ChatTwo calls to send, see docs/design.md). Commands go on from there to the shell, plain text to the current
/// channel. While talking in a channel, the detour asks the listener first and doesn't call the game for a line it
/// keeps; if deciding throws, the line is kept (fail closed).</item>
/// <item><c>RaptureShellModule.ChangeChatChannel</c>: switches the game's chat channel (/s, /p, ChatTwo's picker and
/// tabs). Seen even when the channel switched to is the one it was on.</item>
/// </list>
/// Both hooks stay enabled while the plugin is loaded; their detours do nothing more than a field read unless talking in
/// a channel. Reading the channel and the chat input's label needs no hook.
/// </summary>
internal sealed unsafe class ChatInterop : IDisposable {
    private readonly IChatBoxListener _listener;
    private Hook<UIModule.Delegates.ProcessChatBoxEntry>? _chatBoxHook;
    private Hook<RaptureShellModule.Delegates.ChangeChatChannel>? _changeChannelHook;

    public ChatInterop(IChatBoxListener listener) {
        this._listener = listener;
        this._chatBoxHook = TryHook<UIModule.Delegates.ProcessChatBoxEntry>(
            "UIModule.ProcessChatBoxEntry", UIModule.Addresses.ProcessChatBoxEntry.Value, this.ChatBoxDetour);
        this._changeChannelHook = TryHook<RaptureShellModule.Delegates.ChangeChatChannel>(
            "RaptureShellModule.ChangeChatChannel", RaptureShellModule.Addresses.ChangeChatChannel.Value, this.ChangeChannelDetour);

        // Both or neither: catching typed text without seeing channel switches would keep talking in a channel after /s.
        if (this._chatBoxHook == null || this._changeChannelHook == null) {
            this.DisposeHooks();
            Services.Log.Warning("Talking in a channel without /lgc is unavailable: a chat function wasn't found in this game version");
            return;
        }

        this._changeChannelHook.Enable();
        this._chatBoxHook.Enable();
    }

    /// <summary>Both hooks are in place: typed text can be kept from game chat, and channel switches are seen.</summary>
    public bool InputHooked => this._chatBoxHook != null && this._changeChannelHook != null;

    /// <summary>The game's current chat channel, or null if it can't be read. Game thread only.</summary>
    public static GameChannel? CurrentChannel() {
        var shell = RaptureShellModule.Instance();
        return shell == null ? null : new GameChannel(shell->ChatType);
    }

    /// <summary>
    /// Shows <paramref name="label"/> as the game chat input's channel name (where it says "Say" or "Party"), or with null,
    /// puts back the game's own name for its current channel. Call before the chat log is drawn, on the game thread.
    /// </summary>
    /// <param name="addon">The ChatLog addon.</param>
    public static void SetChannelLabel(nint addon, string? label) {
        if (addon == 0) {
            return;
        }

        var node = ((AddonChatLog*) addon)->CurrentChannelTextNode;
        if (node == null) {
            return;
        }

        ReadOnlySpan<byte> wanted;
        if (label != null) {
            wanted = Encoding.UTF8.GetBytes(label);
        } else {
            var agent = AgentChatLog.Instance();
            if (agent == null) {
                return;
            }

            wanted = agent->ChannelLabel.AsSpan();
        }

        if (!node->NodeText.AsSpan().SequenceEqual(wanted)) {
            node->SetText(wanted);
        }
    }

    private void ChatBoxDetour(UIModule* module, Utf8String* message, nint a4, bool saveToHistory) {
        if (this.KeepFromGame(message)) {
            return;
        }

        this._chatBoxHook!.Original(module, message, a4, saveToHistory);
    }

    private bool KeepFromGame(Utf8String* message) {
        if (!this._listener.Active || message == null) {
            return false;
        }

        try {
            return this._listener.KeepFromGame(message->AsSpan().ToArray());
        } catch (Exception ex) {
            // Fail closed: while talking in a channel, a line that couldn't be looked at never reaches game chat.
            try {
                this._listener.Failed(ex);
            } catch {
                // Nothing more can be done here; the line is still kept.
            }

            return true;
        }
    }

    private bool ChangeChannelDetour(RaptureShellModule* shell, int channel, uint linkshellIndex, Utf8String* tellTarget, bool setChatType) {
        var result = this._changeChannelHook!.Original(shell, channel, linkshellIndex, tellTarget, setChatType);
        try {
            this._listener.ChannelChangeRequested();
        } catch (Exception ex) {
            Services.Log.Error(ex, "Error handling a chat channel switch");
        }

        return result;
    }

    private static Hook<T>? TryHook<T>(string name, nint address, T detour) where T : Delegate {
        if (address == 0) {
            Services.Log.Warning($"{name} wasn't found in this game version");
            return null;
        }

        try {
            return Services.GameInterop.HookFromAddress(address, detour);
        } catch (Exception ex) {
            Services.Log.Warning(ex, $"Couldn't hook {name}");
            return null;
        }
    }

    private void DisposeHooks() {
        this._chatBoxHook?.Dispose();
        this._chatBoxHook = null;
        this._changeChannelHook?.Dispose();
        this._changeChannelHook = null;
    }

    public void Dispose() {
        this.DisposeHooks();
    }
}
