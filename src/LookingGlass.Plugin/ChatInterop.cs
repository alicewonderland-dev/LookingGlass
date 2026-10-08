using System.Text;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.Shell;
using InteropGenerator.Runtime;
using LookingGlass.Core.Client;

namespace LookingGlass.Plugin;

/// <summary>Told what the game is about to run from a chat line, and when its channel is switched. Called on the game thread.</summary>
internal interface IChatBoxListener {
    /// <summary>True while lines may have to be kept from the game. A field read, checked before anything else runs.</summary>
    bool Active { get; }

    /// <summary>Decides what happens to a line (its raw bytes, payloads and all), and acts on it.</summary>
    /// <param name="source">Which way it came: the short-command rule depends on it (see <see cref="ShortCommandRule"/>).</param>
    /// <param name="chatTwo">
    /// What ChatTwo's main input said about the line, if a plugin submitted it through <c>ProcessChatBoxEntry</c> and this
    /// is that line (not one run inside it); see <see cref="PluginLine"/>.
    /// </param>
    /// <returns>True to keep it from the game.</returns>
    bool KeepFromGame(byte[] message, LineSource source, ChatTwoLine? chatTwo);

    /// <summary>
    /// A plugin (ChatTwo) is submitting a line through <c>UIModule.ProcessChatBoxEntry</c>: what ChatTwo's main input says
    /// about it, read now, before the line reaches the gate (where another plugin's hook may change it first). Null if
    /// ChatTwo doesn't answer. Only while <see cref="Active"/>.
    /// </summary>
    ChatTwoLine? PluginLine(byte[] message);

    /// <summary><see cref="KeepFromGame"/> threw while <see cref="Active"/>: the line was kept from the game.</summary>
    void Failed(Exception ex);

    /// <summary>Something called the game's chat channel switch, which has returned.</summary>
    /// <param name="before">The game's chat channel before the call, or null if it couldn't be read (for the diagnostic log).</param>
    /// <param name="fromTypedCommand">It was called while a line was being run by the game (/s, /p).</param>
    void ChannelSwitchCalled(GameChannel? before, bool fromTypedCommand);

    /// <summary>A line was run by the game (<see cref="KeepFromGame"/> let it through). Only while <see cref="Active"/>.</summary>
    void LinePassed();

    /// <summary>
    /// The game renamed its chat input's channel (<c>AgentChatLog.ChangeChannelName</c>): it switched channel, for good or
    /// for a one-off. Only while <see cref="Active"/>.
    /// </summary>
    /// <param name="before">The shell's channel state before the call.</param>
    void ChatBoxRenamed(ChatBoxState? before);

    /// <summary>The game put a link placeholder (&lt;item&gt; and the like) in the chat input. Only while <see cref="Active"/>.</summary>
    void LinkInserted(uint param);

    /// <summary>
    /// Whether a line the game runs inside lines already let through goes to the game unjudged (see <see cref="NestedLines"/>:
    /// a reply's text); says so in the diagnostic log if it does. Only while <see cref="Active"/>.
    /// </summary>
    /// <param name="running">The commands of the lines running, outermost first.</param>
    /// <param name="bytes">The inner line's size, for the log.</param>
    bool PassNested(IReadOnlyList<string?> running, int bytes);
}

/// <summary>
/// The one module that touches the game's chat functions, for sticky mode (see <see cref="StickyMode"/>). Addresses
/// come from FFXIVClientStructs, resolved by Dalamud; a missing required one disables sticky mode only.
/// <list type="bullet">
/// <item><c>ShellCommandModule.ExecuteCommandInner</c> (required): the gate. Every chat line the game runs goes through
/// it: the game's own chat box (Enter goes from the ChatLog addon to a UIModule handler that adds the line to the input
/// history and calls this directly; it never calls <c>ProcessChatBoxEntry</c>), <c>UIModule.ProcessChatBoxEntry</c>
/// (what ChatTwo calls), and the game's other callers: macro lines, gear sets and the like. While talking in a channel,
/// the detour asks the listener first and doesn't call the game for a line it keeps; if deciding throws, the line is
/// kept (fail closed, see <see cref="ChatBoxGate"/>). Commands go on to the game unchanged, so Dalamud's plugin commands
/// (/lgc included) still run: Dalamud dispatches them from its own hook on <c>ShellCommands.TryInvokeDebugCommand</c>,
/// which the game calls while running a command it doesn't know, inside this function, and runs the handler right
/// there. Another plugin hooking this same function is chained by Dalamud: whichever runs first passes the line on.
/// A /lgc (or /lgl) line is also read here, sticky or not, for its links (see <see cref="TypedCommandLine"/>).</item>
/// <item><c>RaptureShellModule.ChangeChatChannel</c> (required): switches the game's chat channel (/s, /p, ChatTwo's
/// picker and tabs). Seen even when the channel switched to is the one it was on; the listener is told whether a line
/// being run made the call, as ChatTwo also calls it with the channel already on at every tab change. Some of the
/// game's own commands set the channel by another function ClientStructs doesn't name; the channel is also read once a
/// frame and before each draw of the chat log, which sees those, and a channel command on its own is caught by the gate.</item>
/// <item><c>UIModule.ProcessChatBoxEntry</c> (optional, pass-through): notes that a line came from a plugin, and, while
/// talking in a channel, asks ChatTwo what its main input holds as the line arrives (see <see cref="ChatTwoLine"/>). The
/// short-command rule depends on it: without this hook, no line can be told to be the game's, and every short command
/// with text counts as possibly ChatTwo's typing (<see cref="LineSource.Unknown"/>).</item>
/// <item><c>AgentChatLog.ChangeChannelName</c> (optional): the game renaming its chat input's channel; checked at once
/// rather than at the next frame, and logged.</item>
/// <item><c>AgentChatLog.InsertTextCommandParam</c> (optional): the game putting a link placeholder (&lt;item&gt;) in its
/// chat input; logged only.</item>
/// </list>
/// All stay enabled while the plugin is loaded; unless talking in a channel, their detours only call the game (and count
/// a running line). Reading the channel, the shell's saved channel (<see cref="ReadChatBox"/>) and the chat input's label
/// needs no hook. A line kept from the game is in the input history already when it came from the game's own chat box,
/// which adds it before running it; ChatTwo keeps its own.
/// </summary>
internal sealed unsafe class ChatInterop : IDisposable {
    private readonly IChatBoxListener _listener;
    private Hook<ShellCommandModule.Delegates.ExecuteCommandInner>? _commandHook;
    private Hook<RaptureShellModule.Delegates.ChangeChatChannel>? _changeChannelHook;
    // Optional: which way a line came, seeing a switch at once, and the diagnostic log.
    private Hook<UIModule.Delegates.ProcessChatBoxEntry>? _chatBoxHook;
    private Hook<AgentChatLog.Delegates.ChangeChannelName>? _renameHook;
    private Hook<AgentChatLog.Delegates.InsertTextCommandParam>? _insertParamHook;
    // Lines being run by the game now (game thread only).
    private int _linesRunning;
    // Lines a plugin is submitting through ProcessChatBoxEntry now (game thread only).
    private int _pluginLines;
    // What ChatTwo's main input said about the line being submitted through ProcessChatBoxEntry, and how many lines were
    // running when it was submitted: only the line run at that depth is the submitted one (game thread only).
    private ChatTwoLine? _pluginLine;
    private int _pluginLineDepth = -1;
    // The commands of the lines let through that are running now, outermost first (null: not a command, or not talking
    // in a channel when it started). Game thread only.
    private readonly List<string?> _running = [];
    // Alongside them: a /lgc or /lgl line, as read at the gate (its links, and what its placeholders stood for then), for its
    // command handler, which Dalamud runs inside the game's call (see TypedCommandLine). Null for any other line.
    private readonly List<TypedLine?> _typed = [];

    public ChatInterop(IChatBoxListener listener) {
        this._listener = listener;
        this._commandHook = TryHook<ShellCommandModule.Delegates.ExecuteCommandInner>(
            "ShellCommandModule.ExecuteCommandInner", ShellCommandModule.Addresses.ExecuteCommandInner.Value, this.CommandDetour);
        this._changeChannelHook = TryHook<RaptureShellModule.Delegates.ChangeChatChannel>(
            "RaptureShellModule.ChangeChatChannel", RaptureShellModule.Addresses.ChangeChatChannel.Value, this.ChangeChannelDetour);

        // Both or neither: catching lines without seeing channel switches would keep talking in a channel after /s.
        if (this._commandHook == null || this._changeChannelHook == null) {
            this.DisposeHooks();
            Services.Log.Warning("Talking in a channel without /lgc is unavailable: a chat function wasn't found in this game version");
            return;
        }

        this._changeChannelHook.Enable();
        this._commandHook.Enable();

        // Not needed for sticky mode to be safe, so a missing one only costs a log detail or the early sight of a switch.
        this._chatBoxHook = TryHook<UIModule.Delegates.ProcessChatBoxEntry>(
            "UIModule.ProcessChatBoxEntry", UIModule.Addresses.ProcessChatBoxEntry.Value, this.ChatBoxDetour);
        this._renameHook = TryHook<AgentChatLog.Delegates.ChangeChannelName>(
            "AgentChatLog.ChangeChannelName", AgentChatLog.Addresses.ChangeChannelName.Value, this.RenameDetour);
        this._insertParamHook = TryHook<AgentChatLog.Delegates.InsertTextCommandParam>(
            "AgentChatLog.InsertTextCommandParam", AgentChatLog.Addresses.InsertTextCommandParam.Value, this.InsertParamDetour);
        this._chatBoxHook?.Enable();
        this._renameHook?.Enable();
        this._insertParamHook?.Enable();
    }

    /// <summary>
    /// The /lgc or /lgl line being run by the game now, if <paramref name="command"/> (as Dalamud gives it to its handler) is
    /// its command: its text (the command included) with a marker for each link, read at the gate before the game ran
    /// it, and its links, the placeholders' already resolved (see <see cref="GameLinks"/>). Null if the line running is
    /// another (a plugin called the command itself), or couldn't be read. Game thread only.
    /// </summary>
    public TypedLine? TypedCommandLine(string command) {
        if (this._typed.Count == 0 || this._typed[^1] is not { } line) {
            return null;
        }

        // Up to any space, as TypedLine.Arguments cuts it (a Japanese IME's full-width space too).
        return string.Equals(line.Command(), command.Trim(), StringComparison.OrdinalIgnoreCase) ? line : null;
    }

    /// <summary>A /lgc or /lgl line read for its handler (see <see cref="TypedCommandLine"/>), or null for any other line, or if reading it failed.</summary>
    private static TypedLine? ReadCommandLine(ReadOnlySpan<byte> raw) {
        if (raw.Length < 4 || (!Ascii.EqualsIgnoreCase(raw[..4], "/lgc"u8) && !Ascii.EqualsIgnoreCase(raw[..4], "/lgl"u8))) {
            return null;
        }

        try {
            return LinkText.ResolvePlaceholders(GameLinks.ReadLine(raw), GameLinks.Placeholder);
        } catch (Exception ex) {
            Services.Log.Error(ex, "Couldn't read the links in a /lgc line; it is sent as typed");
            return null;
        }
    }

    /// <summary>A line is being run by the game now (game thread only).</summary>
    public bool LineInFlight => this._linesRunning > 0;

    /// <summary>Both required hooks are in place: lines can be kept from game chat, and channel switches are seen.</summary>
    public bool InputHooked => this._commandHook != null && this._changeChannelHook != null;

    /// <summary>The shell's channel state (see <see cref="ChatBoxState"/>), or null if it can't be read. Game thread only.</summary>
    public static ChatBoxState? ReadChatBox() {
        var shell = RaptureShellModule.Instance();
        if (shell == null) {
            return null;
        }

        var agent = AgentChatLog.Instance();
        return new ChatBoxState(shell->ChatType, shell->TempChatType, shell->TempChatCommand.ToString(),
            agent == null ? -1 : (int) agent->CurrentChannel, agent == null ? 0 : agent->ChannelLabel.ToString().GetHashCode(StringComparison.Ordinal));
    }

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

    private void CommandDetour(ShellCommandModule* module, Utf8String* message, UIModule* uiModule) {
        // The decision lives in Core (ChatBoxGate), where it is tested: while talking in a channel, a line whose fate
        // couldn't be decided is kept from the game.
        var source = this._chatBoxHook == null ? LineSource.Unknown : this._pluginLines > 0 ? LineSource.Plugin : LineSource.Game;
        var chatTwo = source == LineSource.Plugin && this._running.Count == this._pluginLineDepth ? this._pluginLine : null;
        var active = this._listener.Active;
        var bytes = active && message != null ? message->AsSpan().ToArray() : null;

        // Run inside a reply the gate let through (the game's /r runs its text this way): the reply's own, unjudged.
        // Only while talking in a channel, and only if the rule says so (see NestedLines); if asking throws, judged.
        var passNested = false;
        if (bytes != null && this._running.Count > 0) {
            try {
                passNested = this._listener.PassNested(this._running, bytes.Length);
            } catch (Exception ex) {
                Services.Log.Error(ex, "Error checking a line run inside another; it is judged");
            }
        }

        if (!passNested && message != null && ChatBoxGate.KeepFromGame(active,
                () => this._listener.KeepFromGame(bytes!, source, chatTwo), this._listener.Failed)) {
            return;
        }

        // A channel switch while this runs came from the line: a typed /s, /p. Its command is noted for lines run inside it.
        // Everything that can throw is read first, and every push is undone in the finally, so the stacks stay in step
        // with the lines running whatever happens.
        var runningCommand = bytes == null ? null : NestedLines.CommandOf(new ChatBoxLine(bytes, ""));
        var typed = message == null ? null : ReadCommandLine(message->AsSpan());
        var pushed = false;
        try {
            this._linesRunning++;
            this._running.Add(runningCommand);
            this._typed.Add(typed);
            pushed = true;
            this._commandHook!.Original(module, message, uiModule);
        } finally {
            if (pushed) {
                this._running.RemoveAt(this._running.Count - 1);
                this._typed.RemoveAt(this._typed.Count - 1);
            }

            this._linesRunning--;
        }

        // After the line (which may have been /lgc1, starting it): the saved channel may have been set and reset.
        try {
            if (this._listener.Active) {
                this._listener.LinePassed();
            }
        } catch (Exception ex) {
            Services.Log.Error(ex, "Error after a chat line was run");
        }
    }

    /// <summary>
    /// A plugin (ChatTwo) submitting a line: noted, with what ChatTwo's main input says about it while talking in a channel;
    /// the line is decided in <see cref="CommandDetour"/>.
    /// </summary>
    private void ChatBoxDetour(UIModule* module, Utf8String* message, nint a4, bool saveToHistory) {
        var (outerLine, outerDepth) = (this._pluginLine, this._pluginLineDepth);
        this._pluginLines++;
        try {
            this._pluginLine = null;
            this._pluginLineDepth = this._running.Count;
            if (message != null && this._listener.Active) {
                try {
                    this._pluginLine = this._listener.PluginLine(message->AsSpan().ToArray());
                } catch (Exception ex) {
                    // Unknown: every short command counts as text for this line (fail safe).
                    Services.Log.Error(ex, "Error reading ChatTwo's input for a line");
                }
            }

            this._chatBoxHook!.Original(module, message, a4, saveToHistory);
        } finally {
            this._pluginLines--;
            (this._pluginLine, this._pluginLineDepth) = (outerLine, outerDepth);
        }
    }

    private bool ChangeChannelDetour(RaptureShellModule* shell, int channel, uint linkshellIndex, Utf8String* tellTarget, bool setChatType) {
        var before = shell == null ? (GameChannel?) null : new GameChannel(shell->ChatType);
        var result = this._changeChannelHook!.Original(shell, channel, linkshellIndex, tellTarget, setChatType);
        try {
            this._listener.ChannelSwitchCalled(before, this._linesRunning > 0);
        } catch (Exception ex) {
            Services.Log.Error(ex, "Error handling a chat channel switch");
        }

        return result;
    }

    private CStringPointer RenameDetour(AgentChatLog* agent) {
        var before = this._listener.Active ? ReadChatBox() : null;
        var result = this._renameHook!.Original(agent);
        try {
            if (this._listener.Active) {
                this._listener.ChatBoxRenamed(before);
            }
        } catch (Exception ex) {
            Services.Log.Error(ex, "Error handling a chat input channel rename");
        }

        return result;
    }

    private bool InsertParamDetour(AgentChatLog* agent, uint param, bool a3) {
        var result = this._insertParamHook!.Original(agent, param, a3);
        try {
            if (this._listener.Active) {
                this._listener.LinkInserted(param);
            }
        } catch (Exception ex) {
            Services.Log.Error(ex, "Error handling a chat input link");
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
        this._commandHook?.Dispose();
        this._commandHook = null;
        this._renameHook?.Dispose();
        this._renameHook = null;
        this._insertParamHook?.Dispose();
        this._insertParamHook = null;
        this._chatBoxHook?.Dispose();
        this._chatBoxHook = null;
        this._changeChannelHook?.Dispose();
        this._changeChannelHook = null;
    }

    public void Dispose() {
        this.DisposeHooks();
    }
}
