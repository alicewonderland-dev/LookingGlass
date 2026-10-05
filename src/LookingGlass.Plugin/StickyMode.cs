using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using LookingGlass.Core.Client;
using Lumina.Excel.Sheets;

namespace LookingGlass.Plugin;

/// <summary>
/// Talking in a channel without /lgc (sticky mode): /lgc3 or /lgc sky with no message makes what is typed in the chat
/// box go to that channel, and never to game chat, until the game's chat channel is switched, the player logs out, the
/// session ends or they're no longer in the channel (see <see cref="StickyChannel"/>, which holds the rules).
/// Shows the channel's tag where the chat input names its channel, in ChatTwo's input, and in the server info bar.
/// Everything runs on the game thread: the hooks' detours, commands, Framework.Update and the addon listener.
/// </summary>
public sealed class StickyMode : IChatBoxListener, IDisposable {
    private const string ChatLogAddon = "ChatLog";
    private const uint White = 0xFFFFFFFF;

    private readonly Configuration _config;
    private readonly PlayerTracker _player;
    private readonly SessionManager _sessions;
    private readonly ChatOutput _chat;
    private readonly ChannelSender _sender;
    private readonly StickyChannel _state = new();
    private readonly ChatInterop _interop;
    private readonly ChatTwoIpc _chatTwo = new();
    private readonly IDtrBarEntry? _infoBar;
    private (string Tag, ushort Colour)? _shown;
    // The game's chat input still shows a tag that must be replaced by the game's own channel name.
    private bool _labelOwed;
    private bool _disposed;

    internal StickyMode(Configuration config, PlayerTracker player, SessionManager sessions, ChatOutput chat, ChannelSender sender) {
        this._config = config;
        this._player = player;
        this._sessions = sessions;
        this._chat = chat;
        this._sender = sender;
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
        var chatTwo = this._chatTwo.Loaded;
        var chatTwoTell = chatTwo && this._chatTwo.InputChannel() == ChatChannelPrefixes.ChatTwoTell;
        var start = this._state.Enter(channelId, tag, this.World(), this._interop.InputHooked, chatTwo, chatTwoTell);
        if (!start.Entered) {
            this._chat.Notice(NoticeLevel.Warning, start.Text);
            return;
        }

        this._chat.ChannelNotice(start.Text, this._sessions.ColourOf(channelId));
        if (ExtraChatLoaded()) {
            this._chat.Notice(NoticeLevel.Warning, StickyMessages.ExtraChatLoaded);
        }

        this.ShowIndicators();
    }

    /// <inheritdoc/>
    bool IChatBoxListener.KeepFromGame(byte[] message) {
        if (this._state.ChannelId is not { } channelId) {
            return false;
        }

        var tag = this.TagOf(channelId);
        var line = SeString.Parse(message);
        var notOnlyText = line.Payloads.Any(payload => payload is not TextPayload);
        var route = StickyRoute.For(channelId, tag, line.TextValue, ChatChannelPrefixes.SentAs(this._chatTwo.Loaded), notOnlyText);
        switch (route) {
            case StickyRoute.ToChannel send:
                this._sender.Send(send.ChannelId, send.Text, tag);
                break;
            case StickyRoute.Dropped { Text: { } notice }:
                this._chat.Notice(NoticeLevel.Warning, notice);
                break;
        }

        return route.KeepsFromGame;
    }

    /// <inheritdoc/>
    void IChatBoxListener.Failed(Exception ex) {
        Services.Log.Error(ex, "Error deciding where a chat line goes; it was kept from game chat");
        var tag = this._shown?.Tag ?? ChannelTag.Fallback;
        this._chat.Notice(NoticeLevel.Error, StickyMessages.NotSent(tag, StickyMessages.SomethingWentWrongReason));
    }

    /// <inheritdoc/>
    void IChatBoxListener.ChannelSwitchCalled(bool fromTypedCommand) {
        if (this._state.ChannelId is { } channelId && this._state.ChannelSwitchCalled(ChatInterop.CurrentChannel(), fromTypedCommand) is { } end) {
            this.Ended(channelId, end);
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

    private void OnUpdate(IFramework framework) {
        try {
            if (this._state.ChannelId is not { } channelId) {
                return;
            }

            if (this._state.Check(this.World()) is { } end) {
                this.Ended(channelId, end);
            } else {
                this.ShowIndicators();
            }
        } catch (Exception ex) {
            Services.Log.Error(ex, "Error checking the channel being talked in");
            this.Leave(StickyEnd.ChannelUnknown);
        }
    }

    private void OnChatLogPreDraw(AddonEvent type, AddonArgs args) {
        try {
            if (this._state.ChannelId is { } channelId) {
                ChatInterop.SetChannelLabel(args.Addon.Address, this.TagOf(channelId));
                this._labelOwed = true;
            } else if (this._labelOwed) {
                ChatInterop.SetChannelLabel(args.Addon.Address, null);
                this._labelOwed = false;
            }
        } catch (Exception ex) {
            Services.Log.Error(ex, "Couldn't update the chat input's channel name");
        }
    }

    private void Leave(StickyEnd why) {
        if (this._state.Leave() is { } channelId) {
            this.Ended(channelId, why);
        }
    }

    /// <summary>Talking in the channel has ended: say so (in every case, see docs/design.md), and take the indicators down.</summary>
    private void Ended(string channelId, StickyEnd why) {
        // As last shown: after a logout or a disconnect, the channel's number and nickname are no longer at hand.
        var (tag, colour) = this._shown ?? (this.TagOf(channelId), this._sessions.ColourOf(channelId) ?? ChatOutput.TagColour);
        this._chat.ChannelNotice(StickyMessages.Ended(tag, why), colour);
        this.HideIndicators();
    }

    private void ShowIndicators() {
        if (this._state.ChannelId is not { } channelId) {
            return;
        }

        // Once a frame: only a changed tag or colour (a nickname or colour set meanwhile) is shown again.
        var shown = (Tag: this.TagOf(channelId), Colour: this._sessions.ColourOf(channelId) ?? ChatOutput.TagColour);
        if (shown == this._shown) {
            return;
        }

        this._shown = shown;
        this._chatTwo.SetChannelLabel($"LookingGlass {shown.Tag}", RgbaOf(shown.Colour));
        if (this._infoBar != null) {
            this._infoBar.Text = new SeStringBuilder().AddUiForeground($"LG {shown.Tag}", shown.Colour).Build();
            this._infoBar.Tooltip = $"What you type in chat goes to the LookingGlass channel {shown.Tag}, not to game chat. Click to stop.";
            this._infoBar.Shown = true;
        }
    }

    private void HideIndicators() {
        this._shown = null;
        this._chatTwo.SetChannelLabel(null, White);
        if (this._infoBar != null) {
            this._infoBar.Shown = false;
        }
    }

    private StickyWorld World() =>
        new(this._sessions.Session, this._player.Current?.ContentId ?? 0, this._sessions.Snapshot, ChatInterop.CurrentChannel());

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
