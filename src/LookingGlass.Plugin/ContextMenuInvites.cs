using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using LookingGlass.Core.Client;
using LookingGlass.Plugin.Ui;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace LookingGlass.Plugin;

/// <summary>
/// Right-click invites (see "Context menu invites" in docs/design.md): what the game's menus and ChatTwo's share. Which
/// channels are offered, and what is said, is <see cref="ContextInvites"/>'s; the invite is the session's own
/// <see cref="ClientSession.InviteAsync"/>, as from a channel's Invite button.
/// </summary>
internal sealed class ContextInviter(Configuration config, SessionManager sessions, ChatOutput chat) {
    /// <summary>The player a menu is about, from its name and home world, or null if it isn't a player with a home world.</summary>
    public static InviteTarget? TargetOf(string? name, RowRef<World> homeWorld) {
        if (!ContextInvites.IsPlayerName(name) || homeWorld.RowId == 0 || homeWorld.ValueNullable is not { IsPublic: true } world) {
            return null;
        }

        // The game's own name for the world, as the server knows players by.
        var worldName = world.Name.ExtractText();
        return string.IsNullOrWhiteSpace(worldName) ? null : new InviteTarget(name!, worldName);
    }

    /// <summary>The channels to offer for <paramref name="target"/>; empty to show nothing.</summary>
    public IReadOnlyList<InviteOffer> OffersFor(InviteTarget target) => ContextInvites.Offers(sessions.Snapshot, target, sessions.Slots,
        sessions.Nicknames, sessions.Colours, config.NicknameTags, config.AdvancedMode);

    /// <summary>
    /// Sends the invite off the game thread, and says how it went in chat (which prints on the framework thread): "Invited
    /// Bob@Lich to [sky]." or why not, in LookingGlass blue with the tag in the channel's colour.
    /// </summary>
    public void Invite(InviteTarget target, InviteOffer offer) {
        var tag = ContextInvites.TagIn(offer);
        if (sessions.Session is not { } session) {
            chat.Notice(NoticeTone.Info, ContextInvites.NotInvited(target, offer, new SessionDisconnectedException("Not connected to the server."),
                config.AdvancedMode), tag, offer.Colour);
            return;
        }

        _ = Task.Run(async () => {
            string line;
            try {
                await session.InviteAsync(offer.ChannelId, target.Name, target.WorldName);
                line = ContextInvites.Invited(target, offer);
            } catch (Exception ex) {
                // Asked when it happens, as the channel's Invite button does. Names stay out of the plugin's log.
                Services.Log.Debug($"An invite from a menu failed: {ex.GetType().Name}");
                line = ContextInvites.NotInvited(target, offer, ex, config.AdvancedMode);
            }

            chat.Notice(NoticeTone.Info, line, tag, offer.Colour);
        });
    }
}

/// <summary>
/// "Invite to LookingGlass ▸" in the game's own menus on a player (a name in chat, the party list, a target, the friend
/// list and the like), through Dalamud's <see cref="Dalamud.Plugin.Services.IContextMenu"/>: no hooks of LookingGlass's own.
/// Game thread only.
/// </summary>
internal sealed class GameContextMenuInvites : IDisposable {
    private readonly ContextInviter _inviter;

    public GameContextMenuInvites(ContextInviter inviter) {
        this._inviter = inviter;
        Services.ContextMenu.OnMenuOpened += this.OnMenuOpened;
    }

    private void OnMenuOpened(IMenuOpenedArgs args) {
        try {
            if (args.MenuType != ContextMenuType.Default || !ContextInvites.IsPlayerMenu(args.AddonName) || args.Target is not MenuTargetDefault target) {
                return;
            }

            // An NPC, a minion or a retainer in the world; and yourself.
            if (target.TargetObject is { } shown && shown is not IPlayerCharacter) {
                return;
            }

            // A character in the world must be the one the menu names, or the menu's name and world may be left over.
            if (target.TargetObject is IPlayerCharacter character &&
                (character.Name.TextValue != target.TargetName || character.HomeWorld.RowId != target.TargetHomeWorld.RowId)) {
                return;
            }

            if (target.TargetContentId != 0 && target.TargetContentId == Services.PlayerState.ContentId) {
                return;
            }

            if (ContextInviter.TargetOf(target.TargetName, target.TargetHomeWorld) is not { } invitee) {
                return;
            }

            var offers = this._inviter.OffersFor(invitee);
            if (offers.Count == 0) {
                return;
            }

            args.AddMenuItem(new MenuItem {
                Name = ContextInvites.MenuLabel,
                PrefixChar = 'L',
                PrefixColor = NoticeColours.Blue,
                IsSubmenu = true,
                OnClicked = clicked => clicked.OpenSubmenu(offers.Select(offer => (IMenuItem) new MenuItem {
                    Name = new SeStringBuilder().AddUiForeground(offer.Tag, offer.Colour ?? ChatOutput.TagColour).AddText(offer.Rest).Build(),
                    IsEnabled = offer.Available,
                    OnClicked = offer.Available ? _ => this._inviter.Invite(invitee, offer) : null,
                }).ToList()),
            });
        } catch (Exception ex) {
            Services.Log.Warning(ex, "Couldn't add the invite item to a menu");
        }
    }

    public void Dispose() {
        Services.ContextMenu.OnMenuOpened -= this.OnMenuOpened;
    }
}

/// <summary>
/// The same item in ChatTwo's right-click menu on a player's name, under its "Integrations" submenu, through ChatTwo's
/// context menu IPC (its <c>ipc.md</c> and <c>IpcManager.cs</c>): <c>ChatTwo.Register</c> gives an ID,
/// <c>ChatTwo.Invoke</c> asks each registered plugin to draw its items (with the ID, the message's sender, its content ID,
/// the payload right-clicked, and the sender's and the message's text), <c>ChatTwo.Unregister</c> drops the ID, and
/// <c>ChatTwo.Available</c> says ChatTwo (re)loaded, which forgets every ID, so LookingGlass registers again. Only a
/// right-clicked name (a <see cref="PlayerPayload"/>) gets the item. ChatTwo calls it while drawing, inside an ImGui menu.
/// </summary>
internal sealed class ChatTwoContextMenuInvites : IDisposable {
    private readonly ContextInviter _inviter;
    private readonly ICallGateSubscriber<string> _register;
    private readonly ICallGateSubscriber<string, object?> _unregister;
    private readonly ICallGateSubscriber<object?> _available;
    private readonly ICallGateSubscriber<string, PlayerPayload?, ulong, Payload?, SeString?, SeString?, object?> _invoke;
    private volatile string? _id;

    public ChatTwoContextMenuInvites(ContextInviter inviter) {
        this._inviter = inviter;
        var plugin = Services.PluginInterface;
        this._register = plugin.GetIpcSubscriber<string>("ChatTwo.Register");
        this._unregister = plugin.GetIpcSubscriber<string, object?>("ChatTwo.Unregister");
        this._available = plugin.GetIpcSubscriber<object?>("ChatTwo.Available");
        this._invoke = plugin.GetIpcSubscriber<string, PlayerPayload?, ulong, Payload?, SeString?, SeString?, object?>("ChatTwo.Invoke");
        this._available.Subscribe(this.Register);
        this._invoke.Subscribe(this.Draw);
        // ChatTwo may be loaded already; if not, it says so when it is.
        this.Register();
    }

    private void Register() {
        // An earlier ID first, so a reload ChatTwo didn't forget never shows the item twice.
        this.Unregister();
        try {
            this._id = this._register.InvokeFunc();
        } catch (IpcNotReadyError) {
            // ChatTwo isn't loaded.
            this._id = null;
        } catch (Exception ex) {
            this._id = null;
            Services.Log.Warning(ex, "Couldn't add LookingGlass to ChatTwo's menu");
        }
    }

    private void Draw(string id, PlayerPayload? sender, ulong contentId, Payload? payload, SeString? senderText, SeString? content) {
        if (id != this._id || payload is not PlayerPayload player) {
            return;
        }

        try {
            if (ContextInviter.TargetOf(player.PlayerName, player.World) is not { } invitee) {
                return;
            }

            var offers = this._inviter.OffersFor(invitee);
            if (offers.Count == 0 || !ImGui.BeginMenu(ContextInvites.MenuLabel)) {
                return;
            }

            try {
                foreach (var offer in offers) {
                    var colour = ChannelPalette.ChatColourOf(offer.Colour);
                    bool picked;
                    ImGui.BeginDisabled(!offer.Available);
                    try {
                        if (colour != null) {
                            ImGui.PushStyleColor(ImGuiCol.Text, colour.Value);
                        }

                        try {
                            picked = ImGui.Selectable($"{WithoutIdMarks(offer.Label)}##lg-invite-{offer.ChannelId}");
                        } finally {
                            if (colour != null) {
                                ImGui.PopStyleColor();
                            }
                        }
                    } finally {
                        ImGui.EndDisabled();
                    }

                    if (picked && offer.Available) {
                        this._inviter.Invite(invitee, offer);
                    }
                }
            } finally {
                ImGui.EndMenu();
            }
        } catch (Exception ex) {
            Services.Log.Warning(ex, "Couldn't draw the invite item in ChatTwo's menu");
        }
    }

    /// <summary>
    /// A label with no "##" left in it: ImGui reads everything after one as the item's ID, so another member's "##" in a
    /// channel's name would cut the label short. Each becomes "# #", which reads the same.
    /// </summary>
    private static string WithoutIdMarks(string label) {
        while (label.Contains("##", StringComparison.Ordinal)) {
            label = label.Replace("##", "# #", StringComparison.Ordinal);
        }

        return label;
    }

    /// <summary>Drops the ID ChatTwo gave, if any.</summary>
    private void Unregister() {
        if (this._id is not { } id) {
            return;
        }

        this._id = null;
        try {
            this._unregister.InvokeAction(id);
        } catch (Exception) {
            // ChatTwo isn't loaded (it went first, or is reloading): it has forgotten the ID already.
        }
    }

    public void Dispose() {
        this._invoke.Unsubscribe(this.Draw);
        this._available.Unsubscribe(this.Register);
        this.Unregister();
    }
}
