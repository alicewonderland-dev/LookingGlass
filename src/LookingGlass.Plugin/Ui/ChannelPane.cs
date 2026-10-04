using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using LookingGlass.Core.Client;
using LookingGlass.Protocol;

namespace LookingGlass.Plugin.Ui;

/// <summary>
/// The right-hand side of the main window: one channel's header and menu, the commands to talk
/// in it, its colour, its key state, and its members with their actions.
/// </summary>
internal sealed class ChannelPane(SessionManager sessions, UiActions actions, Modals modals) {
    private const int MaxChannelNameBytes = 64;

    private string? _channelId;
    private string _renameTo = "";
    private bool _editingNickname;
    private string _nickname = "";
    private string? _nicknameError;
    private bool _nicknameFocus;
    private string _inviteName = "";
    private string _inviteWorld = "";

    /// <summary>Called when the channel is gone (left, disbanded) so the window can pick another.</summary>
    public event Action? Closed;

    public void Draw(ChannelView channel, ClientSession session, SessionSnapshot snapshot) {
        if (this._channelId != channel.Id) {
            // Another channel: drop half-finished edits of the last one.
            this._channelId = channel.Id;
            this._editingNickname = false;
            this._nicknameError = null;
            this._inviteName = "";
            this._inviteWorld = "";
        }

        ImGui.PushID(channel.Id);
        this.DrawHeader(channel, session);
        if (channel.MembershipWarning is { } warning) {
            ImGui.Spacing();
            Widgets.IconText(FontAwesomeIcon.ExclamationTriangle, "Check this channel's members", Widgets.Warning);
            Widgets.WrappedColoured(Widgets.Warning, warning);
        }

        ImGui.Spacing();
        this.DrawCommands(channel, snapshot);
        this.DrawKeyState(channel);
        ImGui.Spacing();
        ImGui.Spacing();
        this.DrawMembers(channel, session, snapshot);
        ImGui.PopID();
    }

    // ================================================================ header

    private void DrawHeader(ChannelView channel, ClientSession session) {
        var admin = channel.MyRank == Rank.Admin;
        var style = ImGui.GetStyle();
        var menuWidth = Widgets.IconButtonWidth(FontAwesomeIcon.EllipsisH);
        var rank = RankLabel(channel.MyRank);
        var rankWidth = ImGui.CalcTextSize(rank).X + 16 * Widgets.Scale + ImGui.GetFrameHeight() * 0.32f + 6 * Widgets.Scale;
        var editWidth = admin ? Widgets.IconButtonWidth(FontAwesomeIcon.PencilAlt) + style.ItemSpacing.X : 0;
        var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;

        if (sessions.ColourOf(channel.Id) is { } row && ChannelPalette.ColourOf(row) is { } colour) {
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (ImGui.GetFrameHeight() - ImGui.GetTextLineHeight()) / 2);
            Widgets.Swatch(colour, ImGui.GetTextLineHeight());
            ImGui.SameLine();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() - (ImGui.GetFrameHeight() - ImGui.GetTextLineHeight()) / 2);
        }

        ImGui.AlignTextToFramePadding();
        var nameWidth = right - ImGui.GetCursorPosX() - editWidth - rankWidth - menuWidth - style.ItemSpacing.X * 3;
        Widgets.TextEllipsis(channel.DisplayName, Math.Max(nameWidth, 40 * Widgets.Scale));

        if (admin) {
            ImGui.SameLine();
            if (Widgets.IconButton("##rename", FontAwesomeIcon.PencilAlt, "Rename the channel")) {
                this._renameTo = channel.Name ?? "";
                ImGui.OpenPopup("rename");
            }
        }

        ImGui.SameLine();
        Widgets.Pill(rank, RankColour(channel.MyRank), channel.MyRank switch {
            Rank.Admin => "You are an admin of this channel: you can invite, remove, promote and rename.",
            Rank.Moderator => "You are a moderator of this channel: you can invite and remove members.",
            Rank.Member => "You are a member of this channel.",
            _ => "You aren't a member of this channel under your current keys.",
        });

        ImGui.SameLine(right - menuWidth);
        if (Widgets.IconButton("##channel-menu", FontAwesomeIcon.EllipsisH, "Channel options")) {
            ImGui.OpenPopup("channel-menu");
        }

        this.DrawRenamePopup(channel, session);
        this.DrawChannelMenu(channel, session, admin);
    }

    private void DrawRenamePopup(ChannelView channel, ClientSession session) {
        if (!ImGui.BeginPopup("rename")) {
            return;
        }

        ImGui.TextUnformatted("Rename the channel");
        ImGui.TextColored(Widgets.Muted, "Every member sees the new name.");
        ImGui.Spacing();
        if (ImGui.IsWindowAppearing()) {
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.SetNextItemWidth(260 * Widgets.Scale);
        var enter = ImGui.InputText("##rename-to", ref this._renameTo, 256, ImGuiInputTextFlags.EnterReturnsTrue);
        var name = this._renameTo.Trim();
        var problem = ChannelNameProblem(name);
        if (problem != null && name.Length > 0) {
            ImGui.TextColored(Widgets.Error, problem);
        }

        var unchanged = name == channel.Name;
        ImGui.BeginDisabled(actions.Busy || problem != null || unchanged);
        if ((ImGui.Button("Rename") || enter) && !actions.Busy && problem == null && !unchanged) {
            actions.Run("Renaming", () => session.RenameAsync(channel.Id, name));
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawChannelMenu(ChannelView channel, ClientSession session, bool admin) {
        if (!ImGui.BeginPopup("channel-menu")) {
            return;
        }

        var name = channel.DisplayName;
        if (admin && ImGui.MenuItem("Rename...", false, !actions.Busy)) {
            this._renameTo = channel.Name ?? "";
            // Opened from here, so the rename popup sits on top of this menu, which closes it.
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            ImGui.OpenPopup("rename");
            return;
        }

        if (ImGui.MenuItem("Leave channel...", false, !actions.Busy)) {
            modals.Confirm("Leave channel", $"Leave \"{name}\"? You'll need a new invite to come back.", "Leave", () => {
                actions.Run($"Leaving {name}", () => session.LeaveAsync(channel.Id));
                this.Closed?.Invoke();
            });
        }

        if (admin) {
            ImGui.Separator();
            ImGui.PushStyleColor(ImGuiCol.Text, Widgets.Error);
            var disband = ImGui.MenuItem("Disband channel...", false, !actions.Busy);
            ImGui.PopStyleColor();
            if (disband) {
                modals.Confirm("Disband channel", $"Disband \"{name}\"? Everyone is removed and the channel is gone for good.", "Disband", () => {
                    actions.Run($"Disbanding {name}", () => session.DisbandAsync(channel.Id));
                    this.Closed?.Invoke();
                });
            }
        }

        ImGui.EndPopup();
    }

    // ================================================================ commands, colour, key

    private void DrawCommands(ChannelView channel, SessionSnapshot snapshot) {
        var slot = sessions.SlotOf(channel.Id);
        var nickname = sessions.NicknameOf(channel.Id);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Widgets.Muted, "Talk here with");
        ImGui.SameLine();

        var slotLabel = slot is { } s ? $"{CommandSlots.Prefix}{s}" : "Pick a number";
        if (ImGui.Button($"{slotLabel}##slot")) {
            ImGui.OpenPopup("slots");
        }

        Widgets.Tooltip(slot is { } n
            ? $"Type {CommandSlots.Prefix}{n} <message> in chat to talk here.\nClick to choose another number; picking one another channel has swaps the two."
            : "Choose a number for this channel, to talk here with /lgc<number> <message>.");
        this.DrawSlotPopup(channel, snapshot, slot ?? 0);

        if (this._editingNickname) {
            ImGui.Spacing();
            this.DrawNicknameEditor(channel, nickname ?? "");
        } else {
            var nicknameLabel = nickname != null ? $"{CommandSlots.Prefix} {nickname}" : "+ Nickname";
            var orWidth = ImGui.CalcTextSize("or").X + ImGui.GetStyle().ItemSpacing.X;
            Widgets.SameLineIfFits(orWidth + Widgets.ButtonWidth(nicknameLabel));
            ImGui.TextColored(Widgets.Muted, "or");
            ImGui.SameLine();
            if (ImGui.Button($"{nicknameLabel}##nickname")) {
                this._editingNickname = true;
                this._nicknameFocus = true;
                this._nickname = nickname ?? "";
                this._nicknameError = null;
            }

            Widgets.Tooltip(nickname != null
                ? $"Type {CommandSlots.Prefix} {nickname} <message> in chat to talk here.\nClick to change or clear the nickname. Only you see it."
                : $"Give this channel a nickname, to talk here with {CommandSlots.Prefix} <nickname> <message>. Only you see it.");
        }

        this.DrawColour(channel);
    }

    private void DrawSlotPopup(ChannelView channel, SessionSnapshot snapshot, int slot) {
        if (!ImGui.BeginPopup("slots")) {
            return;
        }

        ImGui.TextColored(Widgets.Muted, "Choose this channel's number");
        // Fifty is a long list: it scrolls, and says which channel has each number.
        var width = 280 * Widgets.Scale;
        if (ImGui.BeginChild("##slot-list", new Vector2(width, ImGui.GetTextLineHeightWithSpacing() * 14), false)) {
            var slots = sessions.Slots;
            var nameColumn = ImGui.CalcTextSize($"{CommandSlots.Prefix}50").X + 16 * Widgets.Scale;
            for (var i = 1; i <= Configuration.SlotCount; i++) {
                if (ImGui.Selectable($"{CommandSlots.Prefix}{i}", i == slot, ImGuiSelectableFlags.DontClosePopups)) {
                    sessions.AssignSlot(channel.Id, i);
                    ImGui.CloseCurrentPopup();
                }

                if (i == slot && ImGui.IsWindowAppearing()) {
                    ImGui.SetScrollHereY();
                }

                if (CommandSlots.ChannelIn(slots, i) is { } other && other != channel.Id) {
                    ImGui.SameLine(nameColumn);
                    Widgets.TextEllipsis(snapshot.FindChannel(other)?.DisplayName ?? "(another channel)", width - nameColumn - 20 * Widgets.Scale, Widgets.Muted);
                }
            }
        }

        ImGui.EndChild();
        ImGui.EndPopup();
    }

    private void DrawNicknameEditor(ChannelView channel, string current) {
        // Nickname, for /lgc <nickname> <message>. Only this character has it; it never goes to the server.
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Widgets.Muted, $"{CommandSlots.Prefix}");
        ImGui.SameLine();
        if (this._nicknameFocus) {
            this._nicknameFocus = false;
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.SetNextItemWidth(140 * Widgets.Scale);
        var enter = ImGui.InputTextWithHint("##nickname-input", "nickname", ref this._nickname, 32, ImGuiInputTextFlags.EnterReturnsTrue);
        if (ImGui.IsItemEdited()) {
            this._nicknameError = null;
        }

        var wanted = this._nickname.Trim();
        var problem = wanted.Length == 0 ? null : ChannelNicknames.Check(sessions.Nicknames, channel.Id, wanted);
        var canSet = problem == null && wanted != current;

        ImGui.SameLine();
        ImGui.BeginDisabled(!canSet);
        if ((ImGui.Button(wanted.Length == 0 && current.Length > 0 ? "Clear" : "Set") || (enter && canSet)) && canSet) {
            this._nicknameError = sessions.SetNickname(channel.Id, wanted);
            if (this._nicknameError == null) {
                this._editingNickname = false;
            }
        }

        ImGui.EndDisabled();
        if (current.Length > 0 && wanted.Length > 0) {
            ImGui.SameLine();
            if (ImGui.Button("Clear")) {
                this._nicknameError = sessions.SetNickname(channel.Id, "");
                if (this._nicknameError == null) {
                    this._editingNickname = false;
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel") || ImGui.IsKeyPressed(ImGuiKey.Escape)) {
            this._editingNickname = false;
            this._nicknameError = null;
        }

        if ((problem ?? this._nicknameError) is { } error) {
            Widgets.WrappedColoured(Widgets.Error, error);
        } else {
            ImGui.TextColored(Widgets.Muted, $"Up to {ChannelNicknames.MaxLength} letters, digits, - or _. Only you see it.");
        }
    }


    private void DrawColour(ChannelView channel) {
        var row = sessions.ColourOf(channel.Id);
        var size = ImGui.GetFrameHeight();
        Widgets.SameLineIfFits(size + ImGui.CalcTextSize("Colour").X + ImGui.GetStyle().ItemSpacing.X);
        ImGui.TextColored(Widgets.Muted, "Colour");
        ImGui.SameLine();

        var colour = ChannelPalette.ChatColourOf(row) ?? Widgets.Muted;
        if (ImGui.ColorButton("##colour", colour, ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoAlpha, new Vector2(size, size))) {
            ImGui.OpenPopup("colours");
        }

        Widgets.Tooltip(row == null
            ? "Default: only the [LGC] tag is coloured in chat. Click to give this channel a colour."
            : "This channel's colour in chat and in the channel list. Click to change it.");

        if (!ImGui.BeginPopup("colours")) {
            return;
        }

        ImGui.TextColored(Widgets.Muted, "Channel colour, for chat and the channel list");
        ImGui.Spacing();
        var swatch = 22 * Widgets.Scale;
        var swatches = ChannelPalette.Swatches;
        for (var i = 0; i < swatches.Count; i++) {
            var (swatchRow, swatchColour) = swatches[i];
            if (i % ChannelPalette.Columns != 0) {
                ImGui.SameLine();
            }

            var selected = swatchRow == row;
            if (ImGui.ColorButton($"##swatch{swatchRow}", swatchColour, ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoAlpha, new Vector2(swatch, swatch))) {
                sessions.SetColour(channel.Id, swatchRow);
                ImGui.CloseCurrentPopup();
            }

            if (selected) {
                var min = ImGui.GetItemRectMin();
                var max = ImGui.GetItemRectMax();
                ImGui.GetWindowDrawList().AddRect(min - new Vector2(2, 2), max + new Vector2(2, 2), ImGui.GetColorU32(ImGuiCol.Text), 3 * Widgets.Scale, ImDrawFlags.None, 2 * Widgets.Scale);
            }
        }

        ImGui.Spacing();
        if (ImGui.Button(row == null ? "Default (selected)" : "Default")) {
            sessions.SetColour(channel.Id, null);
            ImGui.CloseCurrentPopup();
        }

        Widgets.Tooltip("Only the [LGC] tag is coloured, in the usual colour.");
        ImGui.EndPopup();
    }

    private void DrawKeyState(ChannelView channel) {
        if (channel.RekeyPending) {
            Widgets.IconText(FontAwesomeIcon.HourglassHalf, "A new channel key is pending.", Widgets.Warning);
            Widgets.Tooltip("Someone's membership changed and a new key is being made. Messages wait until it's ready.");
        } else if (!channel.HasKey) {
            Widgets.IconText(FontAwesomeIcon.HourglassHalf, "Waiting for the channel key.", Widgets.Warning);
            Widgets.Tooltip("Messages can't be read or sent here until a member shares the key with you.");
        } else {
            Widgets.IconText(FontAwesomeIcon.Lock, $"End-to-end encrypted · key {channel.Epoch}", Widgets.Muted);
            Widgets.Tooltip("Only the members below can read this channel. The key changes whenever someone leaves or is removed.");
        }
    }

    // ================================================================ members

    private void DrawMembers(ChannelView channel, ClientSession session, SessionSnapshot snapshot) {
        var members = channel.Members.Count(m => m.Rank >= Rank.Member);
        var invited = channel.Members.Count(m => m.Rank == Rank.Invited);
        var canInvite = channel.MyRank >= Rank.Moderator;

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Members");
        ImGui.SameLine();
        ImGui.TextColored(Widgets.Muted, invited > 0 ? $"· {members}  ({invited} invited)" : $"· {members}");

        if (canInvite) {
            var inviteWidth = ImGuiComponents.GetIconButtonWithTextWidth(FontAwesomeIcon.UserPlus, "Invite");
            ImGui.SameLine(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - inviteWidth);
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.UserPlus, "Invite")) {
                ImGui.OpenPopup("invite");
            }

            Widgets.Tooltip("Invite someone to this channel by character name and home world.");
            this.DrawInvitePopup(channel, session, snapshot);
        }

        ImGui.Separator();

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.PadOuterX;
        if (!ImGui.BeginTable("##members", 4, flags, new Vector2(0, Math.Max(ImGui.GetContentRegionAvail().Y, ImGui.GetFrameHeight() * 3)))) {
            return;
        }

        ImGui.TableSetupColumn("Member", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Rank", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("Moderator").X);
        ImGui.TableSetupColumn("Verification", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("Registered again").X + Widgets.IconSize(FontAwesomeIcon.ExclamationTriangle).X + 6 * Widgets.Scale);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, Widgets.IconButtonWidth(FontAwesomeIcon.EllipsisH));

        foreach (var member in channel.Members.OrderByDescending(m => m.Rank).ThenBy(m => m.User.Name, StringComparer.OrdinalIgnoreCase)) {
            this.DrawMember(channel, member, session, snapshot);
        }

        ImGui.EndTable();
    }

    private void DrawMember(ChannelView channel, MemberView member, ClientSession session, SessionSnapshot snapshot) {
        var isMe = member.User.UserId == snapshot.Me?.UserId;
        ImGui.PushID(member.User.UserId.ToString());
        ImGui.TableNextRow(ImGuiTableRowFlags.None, ImGui.GetFrameHeight());

        // Name.
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        var name = $"{member.User.Name}@{member.User.WorldName}";
        var you = isMe ? " (you)" : "";
        var youWidth = isMe ? ImGui.CalcTextSize(you).X : 0;
        var tooltip = member is { KeyReplaced: true, NewFingerprint: { } newFingerprint }
            ? $"{name}\nFingerprint in this channel: {member.Fingerprint ?? "-"}\nThe key they registered again with: {newFingerprint}"
            : $"{name}\nFingerprint: {member.Fingerprint ?? "-"}";
        Widgets.TextEllipsis(name, Math.Max(ImGui.GetContentRegionAvail().X - youWidth, 20 * Widgets.Scale), null, tooltip);
        if (isMe) {
            ImGui.SameLine(0, 0);
            ImGui.TextColored(Widgets.Muted, you);
        }

        // Rank, quietly.
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Widgets.Muted, RankLabel(member.Rank));

        // Verification.
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        DrawVerification(member, isMe);
        if (!isMe && ImGui.IsItemClicked()) {
            modals.CompareFingerprints(channel.Id, member.User.UserId);
        }

        // Actions.
        ImGui.TableNextColumn();
        if (!isMe) {
            if (Widgets.IconButton("##member-menu", FontAwesomeIcon.EllipsisH, $"Actions for {member.User.Name}")) {
                ImGui.OpenPopup("member-menu");
            }

            this.DrawMemberMenu(channel, member, session, snapshot);
        }

        ImGui.PopID();
    }

    private static void DrawVerification(MemberView member, bool isMe) {
        if (isMe) {
            ImGui.TextColored(Widgets.Muted, "-");
            return;
        }

        // Each is one item (a group), so hovering or clicking anywhere on it works.
        ImGui.BeginGroup();
        if (member.KeyReplaced) {
            Widgets.IconText(FontAwesomeIcon.ExclamationTriangle, "Registered again", Widgets.Warning);
        } else if (member.KeyChanged) {
            Widgets.IconText(FontAwesomeIcon.ExclamationTriangle, "Key changed", Widgets.Warning);
        } else if (member.FingerprintCompared) {
            Widgets.IconText(FontAwesomeIcon.CheckCircle, "Verified", Widgets.Success);
        } else {
            Widgets.IconText(FontAwesomeIcon.QuestionCircle, "Not compared", Widgets.Muted);
        }

        ImGui.EndGroup();
        Widgets.Tooltip(member.KeyReplaced
            ? "They registered again with a new identity key, which isn't a member of this channel.\nCompare fingerprints over /tell, then remove them and invite them again to let it in.\nClick to compare."
            : member.KeyChanged
                ? "Their identity key changed, or this name now belongs to a different account.\nCompare fingerprints with them over /tell, then mark it verified. Click to compare."
                : member.FingerprintCompared
                    ? "You compared fingerprints with them and marked them verified."
                    : "Their keys are trusted on first use: whoever invited them got them from the server.\nCompare fingerprints with them over /tell, then mark them verified. Click to compare.");
    }

    private void DrawMemberMenu(ChannelView channel, MemberView member, ClientSession session, SessionSnapshot snapshot) {
        if (!ImGui.BeginPopup("member-menu")) {
            return;
        }

        var user = member.User;
        var name = $"{user.Name}@{user.WorldName}";
        var enabled = !actions.Busy;
        ImGui.TextColored(Widgets.Muted, name);
        ImGui.Separator();

        if (ImGui.MenuItem("Compare fingerprints...")) {
            modals.CompareFingerprints(channel.Id, user.UserId);
        }

        // The same permissions as the server's: admins promote and demote; moderators and admins remove lower ranks.
        if (channel.MyRank == Rank.Admin && member.Rank is Rank.Member && ImGui.MenuItem("Make moderator", false, enabled)) {
            actions.Run($"Promoting {user.Name}", () => session.SetRankAsync(channel.Id, user.UserId, Rank.Moderator));
        }

        if (channel.MyRank == Rank.Admin && member.Rank is Rank.Moderator && ImGui.MenuItem("Make member", false, enabled)) {
            actions.Run($"Demoting {user.Name}", () => session.SetRankAsync(channel.Id, user.UserId, Rank.Member));
        }

        if (channel.MyRank >= Rank.Moderator && member.Rank < channel.MyRank) {
            if (member.Rank == Rank.Invited) {
                if (ImGui.MenuItem("Cancel invite", false, enabled)) {
                    actions.Run($"Cancelling the invite for {user.Name}", () => session.KickAsync(channel.Id, user.UserId));
                }
            } else if (ImGui.MenuItem("Remove from channel...", false, enabled)) {
                modals.Confirm("Remove member",
                    $"Remove {name} from \"{channel.DisplayName}\"? They'll need a new invite to come back, and a new key is made that they don't get.",
                    "Remove", () => actions.Run($"Removing {user.Name}", () => session.KickAsync(channel.Id, user.UserId)));
            }
        }

        ImGui.Separator();
        var blocked = snapshot.BlockedUsers.Any(b => b.UserId == user.UserId);
        if (blocked) {
            if (ImGui.MenuItem("Unblock", false, enabled)) {
                actions.Run($"Unblocking {user.Name}", () => session.UnblockUser(user.UserId));
            }
        } else if (ImGui.MenuItem("Block", false, enabled)) {
            actions.Run($"Blocking {user.Name}", () => session.BlockUser(user.UserId));
        }

        Widgets.Tooltip(blocked
            ? "Show their messages and invites again."
            : "Hide their messages, and silently decline their invites. Undo in Settings > Blocked users.");
        ImGui.EndPopup();
    }

    private void DrawInvitePopup(ChannelView channel, ClientSession session, SessionSnapshot snapshot) {
        if (!ImGui.BeginPopup("invite")) {
            return;
        }

        var width = 260 * Widgets.Scale;
        ImGui.TextUnformatted("Invite someone");
        ImGui.TextColored(Widgets.Muted, $"to {Widgets.Ellipsize(channel.DisplayName, width)}");
        ImGui.Spacing();

        ImGui.TextUnformatted("Character name");
        if (ImGui.IsWindowAppearing()) {
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.SetNextItemWidth(width);
        ImGui.InputTextWithHint("##invite-name", "First Last", ref this._inviteName, 32);
        ImGui.TextUnformatted("Home world");
        ImGui.SetNextItemWidth(width);
        var enter = ImGui.InputTextWithHint("##invite-world", sessions.Player?.HomeWorldName is { } home ? $"e.g. {home}" : "World", ref this._inviteWorld, 32,
            ImGuiInputTextFlags.EnterReturnsTrue);

        if (snapshot.DebugAccountsEnabled) {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
            ImGui.TextColored(Widgets.Muted, $"Test server: \"Echo Bot\" on world \"{ProtocolInfo.DebugWorldName}\" answers.");
            ImGui.PopTextWrapPos();
            if (ImGui.SmallButton("Fill in Echo Bot")) {
                this._inviteName = "Echo Bot";
                this._inviteWorld = ProtocolInfo.DebugWorldName;
            }
        }

        ImGui.Spacing();
        var ready = !actions.Busy && !string.IsNullOrWhiteSpace(this._inviteName) && !string.IsNullOrWhiteSpace(this._inviteWorld);
        ImGui.BeginDisabled(!ready);
        if ((ImGui.Button("Invite") || enter) && ready) {
            var name = this._inviteName.Trim();
            var world = this._inviteWorld.Trim();
            this._inviteName = "";
            actions.Run($"Inviting {name}", () => session.InviteAsync(channel.Id, name, world));
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    // ================================================================ helpers

    public static string? ChannelNameProblem(string name) {
        if (name.Length == 0) {
            return "A channel needs a name.";
        }

        return Encoding.UTF8.GetByteCount(name) > MaxChannelNameBytes ? "That name is too long." : null;
    }

    private static string RankLabel(Rank rank) => rank switch {
        Rank.Admin => "Admin",
        Rank.Moderator => "Moderator",
        Rank.Member => "Member",
        Rank.Invited => "Invited",
        _ => "Not a member",
    };

    private static Vector4 RankColour(Rank rank) => rank switch {
        Rank.Admin => Dalamud.Interface.Colors.ImGuiColors.DalamudViolet,
        Rank.Moderator => Dalamud.Interface.Colors.ImGuiColors.TankBlue,
        Rank.Member => Widgets.Muted,
        _ => Widgets.Warning,
    };
}
