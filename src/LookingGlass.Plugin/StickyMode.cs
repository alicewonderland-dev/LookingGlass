using Dalamud.Game;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using LookingGlass.Core.Client;
using Lumina.Excel.Sheets;

namespace LookingGlass.Plugin;

/// <summary>
/// Talking in a channel without /lgc (sticky mode): /lgc3 or /lgc sky with no message makes what is typed in the chat
/// box go to that channel, and never to game chat, until the game's chat channel is switched, the player logs out, the
/// session ends or they're no longer in the channel (see <see cref="StickyChannel"/>, which holds the rules).
/// Shows the channel's tag where the chat input names its channel, in ChatTwo's input, and in the server info bar, and
/// keeps all three in step with the state once a frame, whatever ended it.
/// Everything runs on the game thread: the hooks' detours, commands, Framework.Update and the addon listener.
/// </summary>
public sealed class StickyMode : IChatBoxListener, IDisposable {
    private const string ChatLogAddon = "ChatLog";
    private const uint White = 0xFFFFFFFF;

    /// <summary>
    /// The game's channel commands (TextCommand rows), for their names in the client's language: Say, Party, Alliance,
    /// Yell, Shout, Free Company, PvP team, Novice Network, cross-world linkshells 1 to 8, linkshells 1 to 8. The rows
    /// ChatTwo's public source (1.40.9, <c>InputChannelExt.TextCommands</c>) gives for its channels.
    /// </summary>
    private static readonly uint[] ChannelCommandRows =
        [102, 105, 119, 117, 103, 115, 91, 101, .. Enumerable.Range(13, 8).Select(i => (uint) i), .. Enumerable.Range(107, 8).Select(i => (uint) i)];

    private readonly Configuration _config;
    private readonly PlayerTracker _player;
    private readonly SessionManager _sessions;
    private readonly ChatOutput _chat;
    private readonly ChannelSender _sender;
    private readonly StickyChannel _state = new();
    private readonly ChatInterop _interop;
    private readonly ChatTwoIpc _chatTwo = new();
    private readonly IDtrBarEntry? _infoBar;
    private readonly IReadOnlyCollection<string> _switches;
    // The reply commands (/r): a line the game runs inside one is the reply's (see NestedLines).
    private readonly IReadOnlyCollection<string> _replies;
    // ChatTwo's label: set while talking in a channel (and sent again now and then), cleared as soon as it stops.
    private readonly LabelKeeper _chatTwoLabel = new(1000);
    // The tag and colour shown while talking in a channel, or null.
    private (string Tag, ushort Colour)? _shown;
    // The chat box state last written to the diagnostic log while talking in a channel.
    private ChatBoxState? _loggedChatBox;
    // The tag is held back from the game chat input's label (see OnChatLogPreDraw).
    private bool _labelHeldBack;
    // The game's chat input still shows a tag that must be replaced by the game's own channel name.
    private bool _labelOwed;
    // ExtraChat's warning has been given since talking in the channel started; when it was last looked for.
    private bool _extraChatWarned;
    private long _extraChatLookedAt;
    private bool _disposed;

    internal StickyMode(Configuration config, PlayerTracker player, SessionManager sessions, ChatOutput chat, ChannelSender sender) {
        this._config = config;
        this._player = player;
        this._sessions = sessions;
        this._chat = chat;
        this._sender = sender;
        this._switches = ChatChannelPrefixes.SwitchesWith(GameChannelCommandNames());
        this._replies = NestedLines.RepliesWith(GameReplyCommandNames());
        this._interop = new ChatInterop(this);

        try {
            this._infoBar = Services.DtrBar.Get("LookingGlass");
            this._infoBar.Shown = false;
            this._infoBar.OnClick = _ => this.Leave(StickyEnd.Stopped);
        } catch (Exception ex) {
            Services.Log.Warning(ex, "Couldn't add the server info bar entry");
        }

        Services.Framework.Update += this.OnUpdate;
        Services.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, ChatLogAddon, this.OnChatLogPreDraw);
    }

    /// <inheritdoc/>
    bool IChatBoxListener.Active => this._state.ChannelId != null;

    /// <summary>/lgc3 or /lgc sky with no message. Call on the framework thread.</summary>
    public void Enter(string channelId) {
        var tag = this.TagOf(channelId);
        var wasOn = this._state.ChannelId != null;
        var chatTwo = this._chatTwo.Loaded;
        var chatTwoTell = chatTwo && this._chatTwo.InputChannel() == ChatChannelPrefixes.ChatTwoTell;
        var world = this.World();
        var start = this._state.Enter(channelId, tag, world, this._interop.InputHooked, chatTwo, chatTwoTell);
        if (!start.Entered) {
            Log(() => StickyDiagnostics.Refused(start.Text, chatTwo, world.Channel));
            this._chat.Notice(NoticeTone.Info, start.Text);
            return;
        }

        this._loggedChatBox = world.ChatBox;
        Log(() => StickyDiagnostics.Started(tag, chatTwo, world.Channel, wasOn, world.ChatBox));
        this._chat.ChannelNotice(start.Text, tag, this._sessions.ColourOf(channelId));
        if (StickyMessages.ChatTwoNoteFor(tag, chatTwo, this._config.ChatTwoOwnCommandNoteShown) is { } note) {
            this._chat.Notice(NoticeTone.Info, note, tag, this._sessions.ColourOf(channelId));
            this._config.ChatTwoOwnCommandNoteShown = true;
            this._config.Save();
        }

        this._extraChatWarned = false;
        this.WarnIfExtraChatLoaded();
        this.SyncIndicators();
    }

    /// <summary>
    /// Gives ExtraChat's warning once while talking in a channel: at the start, or as soon as ExtraChat is turned on later.
    /// </summary>
    private void WarnIfExtraChatLoaded() {
        if (this._extraChatWarned || this._state.ChannelId == null) {
            return;
        }

        this._extraChatLookedAt = Environment.TickCount64;
        if (ExtraChatLoaded()) {
            this._extraChatWarned = true;
            this._chat.Notice(NoticeLevel.Warning, StickyMessages.ExtraChatLoaded);
        }
    }

    /// <inheritdoc/>
    bool IChatBoxListener.KeepFromGame(byte[] message, LineSource source, ChatTwoLine? chatTwoLine) {
        if (this._state.ChannelId is not { } channelId) {
            return false;
        }

        var tag = this.TagOf(channelId);
        // Its text with a marker for each link in its bytes (see GameLinks.ReadLine), and those links.
        var typed = GameLinks.ReadLine(message);
        var line = new ChatBoxLine(message, typed.Text) { Links = typed.Links };
        var chatTwo = this._chatTwo.Loaded;
        // Short commands: the player's one-off in the game's chat box; in ChatTwo's main input, all but its own channel's.
        var rule = ShortCommandRule.For(source, chatTwoLine);
        var (route, reason) = StickyRoute.Decide(channelId, tag, line, rule.AsText, this._switches);
        // Before acting on it, so the log has the line even if acting fails. Never the text itself.
        Log(() => StickyDiagnostics.Line(tag, chatTwo, line, route, reason, this._switches, source, rule.Why));
        switch (route) {
            case StickyRoute.ToChannel send:
                this.SendTyped(send, line, tag);
                break;
            case StickyRoute.Dropped { Text: { } notice }:
                this._chat.Notice(NoticeTone.Info, notice);
                break;
            case StickyRoute.LeaveThenGame:
                // /s on its own: stop now, saying so and taking the labels down, then let the game switch. This doesn't
                // depend on whether, or when, the game calls its channel switch for it (it may not, for the channel already on).
                this.Leave(StickyEnd.ChannelSwitched);
                break;
        }

        return route.KeepsFromGame;
    }

    /// <summary>
    /// Sends what was typed to the channel, with its links (the line's own link bytes, and the chat box's &lt;item&gt;
    /// and the like, read now): see <see cref="ChannelSender.Send"/>. The diagnostic log gets sizes and counts only.
    /// </summary>
    private void SendTyped(StickyRoute.ToChannel send, ChatBoxLine line, string tag) {
        if (this._sender.Send(send.ChannelId, line.Typed.WithText(send.Text), tag) is var (message, leftOut)) {
            Log(() => StickyDiagnostics.Sent(tag, line.Raw.Length, message, leftOut));
        }
    }

    /// <summary>The /lgc line being run now, as read at the gate, if its command is <paramref name="command"/> (see <see cref="ChatInterop.TypedCommandLine"/>).</summary>
    internal TypedLine? TypedCommandLine(string command) => this._interop.TypedCommandLine(command);

    /// <inheritdoc/>
    ChatTwoLine? IChatBoxListener.PluginLine(byte[] message) =>
        this._chatTwo.InputState() is { } state
            ? ChatTwoLine.Of(state.ChatType, state.HasText, state.TextLength, SeString.Parse(message).TextValue)
            : null;

    /// <inheritdoc/>
    void IChatBoxListener.LinePassed() {
        var chatBox = ChatInterop.ReadChatBox();
        if (this._state.ChannelId is { } channelId && chatBox != this._state.ChatBoxBaseline) {
            Log(() => StickyDiagnostics.ChatBoxChanged(this._shown?.Tag ?? this.TagOf(channelId), "after a line, counted as unchanged", this._state.ChatBoxBaseline, chatBox));
        }

        this._state.LinePassed(chatBox);
        this._loggedChatBox = chatBox;
    }

    /// <inheritdoc/>
    void IChatBoxListener.ChatBoxRenamed(ChatBoxState? before) {
        if (this._state.ChannelId is { } channelId) {
            var after = ChatInterop.ReadChatBox();
            Log(() => StickyDiagnostics.ChatBoxChanged(this._shown?.Tag ?? this.TagOf(channelId), "the game renamed its channel", before, after));
        }

        this.CheckNow("the game renamed its channel");
    }

    /// <inheritdoc/>
    bool IChatBoxListener.PassNested(IReadOnlyList<string?> running, int bytes) {
        if (this._state.ChannelId is not { } channelId || !NestedLines.PassThrough(running, this._replies)) {
            return false;
        }

        Log(() => StickyDiagnostics.NestedPassed(this._shown?.Tag ?? this.TagOf(channelId), bytes));
        return true;
    }

    /// <inheritdoc/>
    void IChatBoxListener.LinkInserted(uint param) {
        if (this._state.ChannelId is { } channelId) {
            Log(() => StickyDiagnostics.LinkInserted(this._shown?.Tag ?? this.TagOf(channelId), param, ChatInterop.ReadChatBox()));
        }
    }

    /// <inheritdoc/>
    void IChatBoxListener.Failed(Exception ex) {
        Services.Log.Error(ex, "Error deciding where a chat line goes; it was kept from game chat");
        var tag = this._shown?.Tag ?? ChannelTag.Fallback;
        // Kept from the game, but something broke: a warning.
        this._chat.Notice(NoticeTone.Warning, StickyMessages.NotSent(tag, StickyMessages.SomethingWentWrongReason));
    }

    /// <inheritdoc/>
    void IChatBoxListener.ChannelSwitchCalled(GameChannel? before, bool fromTypedCommand) {
        var after = ChatInterop.CurrentChannel();
        if (this._state.ChannelId is not { } channelId) {
            // Not talking in a channel: only at Debug, so it costs nothing worth noting.
            Services.Log.Debug(StickyDiagnostics.ChannelSwitch(null, before, after, fromTypedCommand, null));
            return;
        }

        var tag = this._shown?.Tag ?? this.TagOf(channelId);
        var end = this._state.ChannelSwitchCalled(after, fromTypedCommand);
        Log(() => StickyDiagnostics.ChannelSwitch(tag, before, after, fromTypedCommand, end));
        if (end is { } why) {
            this.Ended(channelId, why);
        }
    }

    /// <summary>
    /// A diagnostic line for dalamud.log (see <see cref="StickyDiagnostics"/>: never what was typed). Never throws: it
    /// runs inside the chat box hook.
    /// </summary>
    private static void Log(Func<string> text) {
        try {
            Services.Log.Information(text());
        } catch {
            // Logging must never decide where a line goes.
        }
    }

    /// <summary>ExtraChat, or a fork of it, is loaded: it shares ChatTwo's label IPC and hooks the chat box too.</summary>
    private static bool ExtraChatLoaded() {
        try {
            return Services.PluginInterface.InstalledPlugins.Any(plugin => plugin.IsLoaded &&
                (plugin.InternalName.StartsWith("ExtraChat", StringComparison.OrdinalIgnoreCase) ||
                 plugin.Name.StartsWith("ExtraChat", StringComparison.OrdinalIgnoreCase)));
        } catch (Exception ex) {
            Services.Log.Warning(ex, "Couldn't list the loaded plugins");
            return false;
        }
    }

    /// <summary>The names of the game's channel commands in the client's language (/s, /say and so on), or none if unreadable.</summary>
    private static IEnumerable<string> GameChannelCommandNames() {
        try {
            var sheet = Services.Data.GetExcelSheet<TextCommand>();
            var names = new List<string>();
            foreach (var row in ChannelCommandRows) {
                if (sheet.GetRowOrDefault(row) is { } command) {
                    names.Add(command.Command.ExtractText());
                    names.Add(command.ShortCommand.ExtractText());
                    names.Add(command.Alias.ExtractText());
                    names.Add(command.ShortAlias.ExtractText());
                }
            }

            return names;
        } catch (Exception ex) {
            Services.Log.Warning(ex, "Couldn't read the game's channel commands; only the English ones end talking in a channel when typed on their own");
            return [];
        }
    }

    /// <summary>
    /// The game's names for its reply command (/r, /reply) in the client's language: the TextCommand rows that are "/reply"
    /// or "/r" in English, read in the client's language. None if unreadable (the English ones still count).
    /// </summary>
    private static IEnumerable<string> GameReplyCommandNames() {
        try {
            var english = Services.Data.GetExcelSheet<TextCommand>(ClientLanguage.English);
            var local = Services.Data.GetExcelSheet<TextCommand>();
            var names = new List<string>();
            foreach (var row in english) {
                string[] forms = [row.Command.ExtractText(), row.ShortCommand.ExtractText(), row.Alias.ExtractText(), row.ShortAlias.ExtractText()];
                if (!forms.Any(form => NestedLines.Replies.Contains(form.Trim(), StringComparer.OrdinalIgnoreCase)) ||
                    local.GetRowOrDefault(row.RowId) is not { } command) {
                    continue;
                }

                names.AddRange([command.Command.ExtractText(), command.ShortCommand.ExtractText(), command.Alias.ExtractText(), command.ShortAlias.ExtractText()]);
            }

            return names;
        } catch (Exception ex) {
            Services.Log.Warning(ex, "Couldn't read the game's reply command; only /r and /reply count as replies");
            return [];
        }
    }

    private void OnUpdate(IFramework framework) {
        this.CheckNow("frame");

        // Whatever happened, the labels say what is so: the tag while talking in a channel, never once it has stopped.
        this.SyncIndicators();

        // Listing the plugins isn't free: every few seconds is soon enough.
        if (Environment.TickCount64 - this._extraChatLookedAt >= 3000) {
            this.WarnIfExtraChatLoaded();
        }
    }

    private void OnChatLogPreDraw(AddonEvent type, AddonArgs args) {
        try {
            // Right before the label is drawn: if the chat box has just switched its channel, stop first, so the tag is never
            // drawn over a channel that what is typed would go to.
            this.CheckNow("draw");
            if (this._state.ChannelId is { } channelId) {
                // The game's chat input may be naming a one-off channel the switch check can't be sure of: show the game's
                // own name then, never the tag over it (what is typed still goes to the LookingGlass channel).
                var unsettled = ChatBoxState.Unsettled(this._state.ChatBoxBaseline, ChatInterop.ReadChatBox());
                if (unsettled != this._labelHeldBack) {
                    this._labelHeldBack = unsettled;
                    Log(() => $"{StickyDiagnostics.Prefix} label: talking in {this._shown?.Tag ?? this.TagOf(channelId)}, " +
                              (unsettled ? "the saved channel changed (a one-off switch?), so the game's own name is shown" : "the tag is shown again"));
                }

                ChatInterop.SetChannelLabel(args.Addon.Address, unsettled ? null : this.TagOf(channelId));
                this._labelOwed = !unsettled;
            } else if (this._labelOwed) {
                ChatInterop.SetChannelLabel(args.Addon.Address, null);
                this._labelOwed = false;
            }
        } catch (Exception ex) {
            Services.Log.Error(ex, "Couldn't update the chat input's channel name");
        }
    }

    /// <summary>
    /// Checks the world now (see <see cref="StickyChannel.Check"/>) and ends talking in the channel if it changed; writes a
    /// changed chat box state to the diagnostic log. Fails closed: if checking throws, it ends.
    /// </summary>
    /// <param name="where">Where it was called from, for the log.</param>
    private void CheckNow(string where) {
        try {
            if (this._state.ChannelId is not { } channelId) {
                return;
            }

            var world = this.World();
            var lineInFlight = this._interop.LineInFlight;
            if (world.ChatBox != this._loggedChatBox) {
                var before = this._loggedChatBox;
                this._loggedChatBox = world.ChatBox;
                Log(() => StickyDiagnostics.ChatBoxChanged(this._shown?.Tag ?? this.TagOf(channelId),
                    lineInFlight ? $"{where}, while a line runs" : where, before, world.ChatBox));
            }

            // A line let through to the game (a command, a one-off "/p hi") may set the saved channel
            // while it runs: measured again once it is done (LinePassed), not counted as the player switching.
            if (this._state.Check(lineInFlight ? world with { ChatBox = null } : world) is { } end) {
                this.Ended(channelId, end);
            }
        } catch (Exception ex) {
            Services.Log.Error(ex, "Error checking the channel being talked in");
            this.Leave(StickyEnd.ChannelUnknown);
        }
    }

    private void Leave(StickyEnd why) {
        if (this._state.Leave() is { } channelId) {
            this.Ended(channelId, why);
        }
    }

    /// <summary>
    /// Talking in the channel has ended: take the labels down at once, then say so (in every case, see docs/design.md),
    /// in the same chat channel as "Now talking in". Each step runs even if the other fails.
    /// </summary>
    private void Ended(string channelId, StickyEnd why) {
        // As last shown: after a logout or a disconnect, the channel's number and nickname are no longer at hand.
        var (tag, colour) = this._shown ?? (this.TagOf(channelId), this._sessions.ColourOf(channelId) ?? ChatOutput.TagColour);
        Log(() => StickyDiagnostics.Ended(tag, why, ChatInterop.ReadChatBox()));
        this._loggedChatBox = null;
        this._labelHeldBack = false;
        try {
            this.SyncIndicators();
        } finally {
            try {
                this._chat.ChannelNotice(StickyMessages.Ended(tag, why), tag, colour);
            } catch (Exception ex) {
                Services.Log.Error(ex, $"Couldn't say that talking in a channel stopped ({why})");
            }
        }
    }

    /// <summary>
    /// Makes the labels match the state: while talking in a channel, its tag in ChatTwo's input and the server info bar
    /// (sent again if the tag or colour changed, ChatTwo's now and then anyway); otherwise neither. Once a frame, and at
    /// every start and end. The game chat input's own name is kept in step before each draw (<see cref="OnChatLogPreDraw"/>).
    /// </summary>
    private void SyncIndicators() {
        (string Tag, ushort Colour)? wanted = this._state.ChannelId is { } channelId
            ? (this.TagOf(channelId), this._sessions.ColourOf(channelId) ?? ChatOutput.TagColour)
            : null;

        try {
            if (this._chatTwoLabel.ShouldSend(wanted is { } key ? $"{key.Tag}\n{key.Colour}" : null, Environment.TickCount64)) {
                this._chatTwo.SetChannelLabel(wanted is { } shown ? $"LookingGlass {shown.Tag}" : null, wanted is { } coloured ? RgbaOf(coloured.Colour) : White);
            }
        } catch (Exception ex) {
            Services.Log.Warning(ex, "Couldn't update ChatTwo's channel name");
        }

        if (wanted == this._shown) {
            return;
        }

        this._shown = wanted;
        if (this._infoBar == null) {
            return;
        }

        if (wanted is { } bar) {
            this._infoBar.Text = new SeStringBuilder().AddUiForeground($"LG {bar.Tag}", bar.Colour).Build();
            this._infoBar.Tooltip = $"What you type in chat goes to the LookingGlass channel {bar.Tag}, not to game chat. Click to stop.";
            this._infoBar.Shown = true;
        } else {
            this._infoBar.Shown = false;
        }
    }

    private StickyWorld World() =>
        new(this._sessions.Session, this._player.Current?.ContentId ?? 0, this._sessions.Snapshot, ChatInterop.CurrentChannel()) {
            ChatBox = ChatInterop.ReadChatBox(),
        };

    private string TagOf(string channelId) => ChannelTag.For(this._sessions.SlotOf(channelId), this._sessions.NicknameOf(channelId), this._config.NicknameTags);

    private static uint RgbaOf(ushort row) {
        try {
            return Services.Data.GetExcelSheet<UIColor>().GetRowOrDefault(row) is { } colour && colour.Dark != 0 ? colour.Dark : White;
        } catch (Exception ex) {
            Services.Log.Warning(ex, $"Couldn't read UIColor row {row}");
            return White;
        }
    }

    /// <summary>Unloading: stops talking in the channel first (and says so), puts the chat input's name back, then removes the hooks.</summary>
    public void Dispose() {
        if (this._disposed) {
            return;
        }

        this._disposed = true;
        Services.Framework.Update -= this.OnUpdate;
        this.Leave(StickyEnd.Unloading);
        Services.AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, ChatLogAddon, this.OnChatLogPreDraw);
        if (this._labelOwed) {
            try {
                ChatInterop.SetChannelLabel(Services.GameGui.GetAddonByName(ChatLogAddon).Address, null);
            } catch (Exception ex) {
                Services.Log.Warning(ex, "Couldn't put the chat input's channel name back");
            }
        }

        this._interop.Dispose();
        this._chatTwo.Dispose();
        this._infoBar?.Remove();
    }
}
