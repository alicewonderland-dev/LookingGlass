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

    /// <summary>Decides what happens to a submitted line (its raw bytes, payloads and all), and acts on it.</summary>
    /// <returns>True to keep it from the game.</returns>
    bool KeepFromGame(byte[] message);

    /// <summary><see cref="KeepFromGame"/> threw while <see cref="Active"/>: the line was kept from the game.</summary>
    void Failed(Exception ex);

    /// <summary>Something called the game's chat channel switch, which has returned.</summary>
    /// <param name="fromTypedCommand">It was called while a line submitted through the chat box was being run (/s, /p).</param>
    void ChannelSwitchCalled(bool fromTypedCommand);
}

/// <summary>
/// The one module that touches the game's chat functions, for sticky mode (see <see cref="StickyMode"/>). Addresses
/// come from FFXIVClientStructs, resolved by Dalamud; a missing one disables sticky mode only.
/// <list type="bullet">
/// <item><c>UIModule.ProcessChatBoxEntry</c>: what the chat box calls with the line typed when Enter is pressed (and
/// what ChatTwo calls to send, see docs/design.md). Commands go on from there to the shell, plain text to the current
/// channel. While talking in a channel, the detour asks the listener first and doesn't call the game for a line it
/// keeps; if deciding throws, the line is kept (fail closed, see <see cref="ChatBoxGate"/>). A kept line still goes into
/// the chat input's history when the game would have put it there.</item>
/// <item><c>RaptureShellModule.ChangeChatChannel</c>: switches the game's chat channel (/s, /p, ChatTwo's picker and
/// tabs). Seen even when the channel switched to is the one it was on; the listener is told whether a typed line
/// (/s, /p) made the call, as ChatTwo also calls it with the channel already on at every tab change.</item>
/// </list>
/// Both hooks stay enabled while the plugin is loaded; unless talking in a channel, their detours only call the game
/// (and count a running line). Reading the channel and the chat input's label needs no hook.
/// </summary>
internal sealed unsafe class ChatInterop : IDisposable {
    private readonly IChatBoxListener _listener;
    private Hook<UIModule.Delegates.ProcessChatBoxEntry>? _chatBoxHook;
    private Hook<RaptureShellModule.Delegates.ChangeChatChannel>? _changeChannelHook;
    // Lines from the chat box being run by the game now (game thread only).
    private int _linesRunning;

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
        // The decision lives in Core (ChatBoxGate), where it is tested: while talking in a channel, a line whose fate
        // couldn't be decided is kept from the game.
        var address = (nint) message;
        if (message != null && ChatBoxGate.KeepFromGame(this._listener.Active,
                () => this._listener.KeepFromGame(((Utf8String*) address)->AsSpan().ToArray()), this._listener.Failed)) {
            if (saveToHistory) {
                AddToChatHistory(module, message);
            }

            return;
        }

        // A channel switch while this runs came from the line: a typed /s, /p.
        this._linesRunning++;
        try {
            this._chatBoxHook!.Original(module, message, a4, saveToHistory);
        } finally {
            this._linesRunning--;
        }
    }

    /// <summary>
    /// Puts a line kept from the game in the chat input's history anyway, as the game would have, so the up arrow brings
    /// it back (to send again after "Not sent"). Best effort: the history the game chat input uses, if it can be found.
    /// </summary>
    private static void AddToChatHistory(UIModule* module, Utf8String* message) {
        try {
            var addon = (AddonChatLog*) Services.GameGui.GetAddonByName("ChatLog").Address;
            if (module == null || addon == null || addon->TextInput == null) {
                return;
            }

            int index = addon->TextInput->AtkHistoryIndex;
            if (index >= 0 && index < module->AtkHistory.Length) {
                module->AddAtkHistoryEntry(message, index);
            }
        } catch (Exception ex) {
            Services.Log.Warning(ex, "Couldn't add a line to the chat input's history");
        }
    }

    private bool ChangeChannelDetour(RaptureShellModule* shell, int channel, uint linkshellIndex, Utf8String* tellTarget, bool setChatType) {
        var result = this._changeChannelHook!.Original(shell, channel, linkshellIndex, tellTarget, setChatType);
        try {
            this._listener.ChannelSwitchCalled(this._linesRunning > 0);
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
