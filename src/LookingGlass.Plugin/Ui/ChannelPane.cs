using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using LookingGlass.Core.Client;
using LookingGlass.Protocol;

namespace LookingGlass.Plugin.Ui;

/// <summary>
/// The right-hand side of the main window: one channel's name (in the header font) with a line
/// about it underneath, the commands to talk in it, and its members with their actions. Its
/// colour, rename, leave and disband are in the channel menu.
/// </summary>
internal sealed class ChannelPane(SessionManager sessions, UiActions actions, Modals modals, UiFonts fonts) {
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

    private enum Open {
        Nothing,
        Rename,
        Colours,
    }

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
            ImGuiHelpers.ScaledDummy(4);
            Widgets.IconText(FontAwesomeIcon.ExclamationTriangle, "Check this channel's members", Widgets.Warning);
            Widgets.WrappedColoured(Widgets.Warning, warning);
        }

        ImGuiHelpers.ScaledDummy(6);
        this.DrawCommands(channel, snapshot);
        ImGuiHelpers.ScaledDummy(12);
        this.DrawMembers(channel, session, snapshot);
        ImGui.PopID();
    }

    // ================================================================ header

    private void DrawHeader(ChannelView channel, ClientSession session) {
        var scale = Widgets.Scale;
        var style = ImGui.GetStyle();
        var admin = channel.MyRank == Rank.Admin;
        var button = Widgets.GhostIconButtonWidth;
        var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        var top = ImGui.GetCursorPosY();
        var open = Open.Nothing;

        float nameHeight;
        using (fonts.Header.Push()) {
            nameHeight = ImGui.GetTextLineHeight();
        }

        var lineHeight = Math.Max(nameHeight, ImGui.GetFrameHeight());
        var buttonY = top + MathF.Round((lineHeight - button) / 2);

        // The channel's colour, once: a dot before its name (and the bar in the channel list).
        if (sessions.ColourOf(channel.Id) is { } row && ChannelPalette.ColourOf(row) is { } colour) {
            var radius = MathF.Round(ImGui.GetFontSize() * 0.28f);
            var pos = ImGui.GetCursorScreenPos();
            if (ImGui.InvisibleButton("##colour-dot", new Vector2(radius * 2, lineHeight))) {
                open = Open.Colours;
            }

            ImGui.GetWindowDrawList().AddCircleFilled(new Vector2(pos.X + radius, pos.Y + lineHeight / 2), radius, ImGui.GetColorU32(colour));
            Widgets.Tooltip("This channel's colour, in chat and in the channel list. Only you see it.", "Click to change it.");
            ImGui.SameLine(0, 8 * scale);
        }

        // The name, as large as the header font. Until that font is built, it is the normal one.
        var nameRoom = right - ImGui.GetCursorPosX() - button - style.ItemSpacing.X * 2 - (admin ? button + 2 * scale : 0);
        string shown;
        using (fonts.Header.Push()) {
            shown = Widgets.Ellipsize(channel.DisplayName, Math.Max(nameRoom, 40 * scale));
            ImGui.SetCursorPosY(top + MathF.Round((lineHeight - ImGui.GetTextLineHeight()) / 2));
            ImGui.TextUnformatted(shown);
        }

        if (!ReferenceEquals(shown, channel.DisplayName)) {
            Widgets.Tooltip(channel.DisplayName);
        }

        if (admin) {
            ImGui.SameLine(0, 2 * scale);
            ImGui.SetCursorPosY(buttonY);
            if (Widgets.GhostIconButton("##rename", FontAwesomeIcon.PencilAlt, "Rename the channel")) {
                open = Open.Rename;
            }
        }

        ImGui.SameLine(right - button);
        ImGui.SetCursorPosY(buttonY);
        if (Widgets.GhostIconButton("##channel-menu", FontAwesomeIcon.EllipsisV, "Channel options")) {
            ImGui.OpenPopup("channel-menu");
        }

        var chosen = this.DrawChannelMenu(channel, session, admin);
        if (chosen != Open.Nothing) {
            open = chosen;
        }

        this.DrawSummary(channel);

        // Opened here, at the pane's level, where they're drawn: also when asked for from the menu, which closes.
        switch (open) {
            case Open.Rename:
                this._renameTo = channel.Name ?? "";
                ImGui.OpenPopup("rename");
                break;
            case Open.Colours:
                ImGui.OpenPopup("colours");
                break;
        }

        this.DrawRenamePopup(channel, session);
        this.DrawColourPopup(channel);
    }

    /// <summary>One muted line under the name: your rank, the members, and the key.</summary>
    private void DrawSummary(ChannelView channel) {
        var first = true;
        Segment(ref first, RankLabel(channel.MyRank), channel.MyRank >= Rank.Member ? Widgets.Muted : Widgets.Warning, channel.MyRank switch {
            Rank.Admin => "You are an admin of this channel: you can invite, remove, promote and rename.",
            Rank.Moderator => "You are a moderator of this channel: you can invite and remove members.",
            Rank.Member => "You are a member of this channel.",
            _ => "You aren't a member of this channel under your current keys.",
        });

        var members = channel.Members.Count(m => m.Rank >= Rank.Member);
        var invited = channel.Members.Count(m => m.Rank == Rank.Invited);
        Segment(ref first, members == 1 ? "1 member" : $"{members} members", Widgets.Muted,
            invited switch {
                0 => members == 1 ? "Only you, so far." : $"{members} members, you included.",
                1 => "Besides them, 1 invite is waiting for an answer.",
                _ => $"Besides them, {invited} invites are waiting for an answer.",
            });

        if (channel.RekeyPending) {
            Segment(ref first, "New key pending", Widgets.Warning,
                "Someone joined or left since the key in use was made, so the channel needs a new one before anyone sends. A member makes it automatically.",
                FontAwesomeIcon.HourglassHalf);
        } else if (!channel.HasKey) {
            Segment(ref first, "Waiting for the key", Widgets.Warning,
                "Messages can't be read or sent here until a member shares the channel key with you.",
                FontAwesomeIcon.HourglassHalf);
        } else {
            Segment(ref first, $"End-to-end encrypted, key {channel.Epoch}", Widgets.Muted,
                "Only the members below hold this channel's key. A new key is made when someone joins or leaves.",
                FontAwesomeIcon.Lock);
        }
    }

    /// <summary>A part of the summary line, after a dot; on the next line instead if it doesn't fit.</summary>
    private static void Segment(ref bool first, string text, Vector4 colour, string tooltip, FontAwesomeIcon? icon = null) {
        const string separator = "  ·  ";
        var gap = 4 * Widgets.Scale;
        var width = (icon is { } i ? Widgets.IconSize(i).X + gap : 0) + ImGui.CalcTextSize(text).X;
        if (!first) {
            var right = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
            if (ImGui.GetItemRectMax().X + ImGui.CalcTextSize(separator).X + width <= right) {
                ImGui.SameLine(0, 0);
                ImGui.TextColored(Widgets.Muted, separator);
                ImGui.SameLine(0, 0);
            }
        }

        first = false;
        ImGui.BeginGroup();
        if (icon is { } shown) {
            Widgets.Icon(shown, colour);
            ImGui.SameLine(0, gap);
        }

        ImGui.TextColored(colour, text);
        ImGui.EndGroup();
        Widgets.Tooltip(tooltip);
    }

    private Open DrawChannelMenu(ChannelView channel, ClientSession session, bool admin) {
        if (!ImGui.BeginPopup("channel-menu")) {
            return Open.Nothing;
        }

        var open = Open.Nothing;
        var name = channel.DisplayName;
        var enabled = !actions.Busy;
        if (admin && Widgets.MenuItem(FontAwesomeIcon.PencilAlt, "Rename...", enabled)) {
            open = Open.Rename;
        }

        if (Widgets.MenuItem(FontAwesomeIcon.Palette, "Colour...")) {
            open = Open.Colours;
        }

        Widgets.Tooltip("This channel's colour in chat and in the channel list. Only you see it.");
        ImGui.Separator();

        if (Widgets.MenuItem(FontAwesomeIcon.SignOutAlt, "Leave channel...", enabled)) {
            modals.Confirm("Leave channel", $"Leave \"{name}\"? You'll need a new invite to come back.", "Leave", () => {
                actions.Run($"Leaving {name}", () => session.LeaveAsync(channel.Id));
                this.Closed?.Invoke();
            });
        }

        if (admin && Widgets.MenuItem(FontAwesomeIcon.Trash, "Disband channel...", enabled, Widgets.Error)) {
            modals.Confirm("Disband channel", $"Disband \"{name}\"? Everyone is removed and the channel is gone for good.", "Disband", () => {
                actions.Run($"Disbanding {name}", () => session.DisbandAsync(channel.Id));
                this.Closed?.Invoke();
            });
        }

        ImGui.EndPopup();
        return open;
    }

    private void DrawRenamePopup(ChannelView channel, ClientSession session) {
        if (!ImGui.BeginPopup("rename")) {
            return;
        }

        ImGui.TextUnformatted("Rename the channel");
        ImGui.TextColored(Widgets.Muted, "Every member sees the new name.");
        ImGuiHelpers.ScaledDummy(4);
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
        if (Widgets.GhostButton("Cancel")) {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawColourPopup(ChannelView channel) {
        if (!ImGui.BeginPopup("colours")) {
            return;
        }

        var row = sessions.ColourOf(channel.Id);
        ImGui.TextUnformatted("Channel colour");
        ImGui.TextColored(Widgets.Muted, "For its lines in chat and its place in the channel list.");
        ImGuiHelpers.ScaledDummy(4);
        var swatch = 22 * Widgets.Scale;
        var swatches = ChannelPalette.Swatches;
        for (var i = 0; i < swatches.Count; i++) {
            var (swatchRow, swatchColour) = swatches[i];
            if (i % ChannelPalette.Columns != 0) {
                ImGui.SameLine();
            }

            if (ImGui.ColorButton($"##swatch{swatchRow}", swatchColour, ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoAlpha, new Vector2(swatch, swatch))) {
                sessions.SetColour(channel.Id, swatchRow);
                ImGui.CloseCurrentPopup();
            }

            if (swatchRow == row) {
                var outline = 2 * Widgets.Scale;
                ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin() - new Vector2(outline), ImGui.GetItemRectMax() + new Vector2(outline),
                    ImGui.GetColorU32(ImGuiCol.Text), 3 * Widgets.Scale, ImDrawFlags.None, outline);
            }
        }

        ImGuiHelpers.ScaledDummy(4);
        if (Widgets.GhostButton(row == null ? "Default (selected)" : "Default", "Only the tag is coloured, in the usual colour.")) {
            sessions.SetColour(channel.Id, null);
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    // ================================================================ commands

    private void DrawCommands(ChannelView channel, SessionSnapshot snapshot) {
        var slot = sessions.SlotOf(channel.Id);
        var nickname = sessions.NicknameOf(channel.Id);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Widgets.Muted, "Talk here with");
        ImGui.SameLine();

        if (slot is { } s) {
            if (Widgets.Chip("##slot", $"{CommandSlots.Prefix}{s}", true)) {
                ImGui.OpenPopup("slots");
            }

            Widgets.Tooltip($"Type {CommandSlots.Prefix}{s} <message> in chat to talk here.",
                "Click to choose another number. Picking one another channel has swaps the two.");
        } else {
            if (Widgets.Chip("##slot", "+ number", false, true)) {
                ImGui.OpenPopup("slots");
            }

            Widgets.Tooltip($"Choose a number for this channel, to talk here with {CommandSlots.Prefix}<number> <message>.");
        }

        if (this._editingNickname) {
            ImGuiHelpers.ScaledDummy(2);
            this.DrawNicknameEditor(channel, nickname ?? "");
        } else {
            this.DrawNicknameChip(nickname);
        }

        // After the line: drawing a popup can change which item was last.
        this.DrawSlotPopup(channel, snapshot, slot ?? 0);
    }

    private void DrawNicknameChip(string? nickname) {
        var label = nickname != null ? $"{CommandSlots.Prefix} {nickname}" : "+ nickname";
        float chipWidth;
        using (nickname != null ? Widgets.MonoFont.Push() : null) {
            chipWidth = ImGui.CalcTextSize(label).X + 14 * Widgets.Scale;
        }

        Widgets.SameLineIfFits(ImGui.CalcTextSize("or").X + ImGui.GetStyle().ItemSpacing.X + chipWidth);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Widgets.Muted, "or");
        ImGui.SameLine();
        if (Widgets.Chip("##nickname", label, nickname != null, nickname == null)) {
            this._editingNickname = true;
            this._nicknameFocus = true;
            this._nickname = nickname ?? "";
            this._nicknameError = null;
        }

        if (nickname != null) {
            Widgets.Tooltip($"Type {CommandSlots.Prefix} {nickname} <message> in chat to talk here.", "Click to change or clear the nickname. Only you see it.");
        } else {
            Widgets.Tooltip($"Give this channel a nickname, to talk here with {CommandSlots.Prefix} <nickname> <message>.", "Only you see it. It also tags the channel's lines in chat.");
        }
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
        using (Widgets.MonoFont.Push()) {
            ImGui.TextColored(Widgets.Muted, CommandSlots.Prefix);
        }

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
            if (Widgets.GhostButton("Clear")) {
                this._nicknameError = sessions.SetNickname(channel.Id, "");
                if (this._nicknameError == null) {
                    this._editingNickname = false;
                }
            }
        }

        ImGui.SameLine();
        if (Widgets.GhostButton("Cancel") || ImGui.IsKeyPressed(ImGuiKey.Escape)) {
            this._editingNickname = false;
            this._nicknameError = null;
        }

        if ((problem ?? this._nicknameError) is { } error) {
            Widgets.WrappedColoured(Widgets.Error, error);
        } else {
            ImGui.TextColored(Widgets.Muted, $"Up to {ChannelNicknames.MaxLength} letters, digits, - or _. Only you see it.");
        }
    }

    // ================================================================ members

    private void DrawMembers(ChannelView channel, ClientSession session, SessionSnapshot snapshot) {
        var invited = channel.Members.Count(m => m.Rank == Rank.Invited);
        var canInvite = channel.MyRank >= Rank.Moderator;

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Members");
        if (invited > 0) {
            ImGui.SameLine();
            ImGui.TextColored(Widgets.Muted, invited == 1 ? "· 1 invited" : $"· {invited} invited");
        }

        if (canInvite) {
            // The pane's one filled button: its main action.
            var inviteWidth = ImGuiComponents.GetIconButtonWithTextWidth(FontAwesomeIcon.UserPlus, "Invite");
            ImGui.SameLine(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - inviteWidth);
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.UserPlus, "Invite")) {
                ImGui.OpenPopup("invite");
            }

            Widgets.Tooltip("Invite someone to this channel by character name and home world.");
            this.DrawInvitePopup(channel, session, snapshot);
        }

        ImGuiHelpers.ScaledDummy(2);
        if (ImGui.BeginChild("##members", Vector2.Zero, false)) {
            foreach (var member in channel.Members.OrderByDescending(m => m.Rank).ThenBy(m => m.User.Name, StringComparer.OrdinalIgnoreCase)) {
                this.DrawMember(channel, member, session, snapshot);
            }
        }

        ImGui.EndChild();
    }

    /// <summary>
    /// One member: their verification icon (click it to compare fingerprints), name, rank on the
    /// right, and a menu. The whole row lights up on hover.
    /// </summary>
    private void DrawMember(ChannelView channel, MemberView member, ClientSession session, SessionSnapshot snapshot) {
        var scale = Widgets.Scale;
        var style = ImGui.GetStyle();
        var isMe = member.User.UserId == snapshot.Me?.UserId;
        ImGui.PushID(member.User.UserId.ToString());

        var height = ImGui.GetFrameHeight();
        var button = Widgets.GhostIconButtonWidth;
        var pos = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var drawList = ImGui.GetWindowDrawList();

        // The highlight reaches halfway into the spacing between rows, so hovered rows have no gap.
        var rowMin = pos with { Y = pos.Y - style.ItemSpacing.Y / 2 };
        var rowMax = new Vector2(pos.X + width, pos.Y + height + style.ItemSpacing.Y / 2);
        if ((ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(rowMin, rowMax)) || ImGui.IsPopupOpen("member-menu")) {
            var hover = style.Colors[(int) ImGuiCol.HeaderHovered];
            drawList.AddRectFilled(rowMin, rowMax, ImGui.GetColorU32(hover with { W = hover.W * 0.6f }), 4 * scale);
        }

        // Verification, as an icon: click it to compare fingerprints.
        var (icon, iconColour, title, explanation) = Verification(member, isMe);
        ImGui.SetCursorScreenPos(pos with { X = pos.X + 2 * scale });
        if (ImGui.InvisibleButton("##verification", new Vector2(button, height)) && !isMe) {
            modals.CompareFingerprints(channel.Id, member.User.UserId);
        }

        Widgets.DrawIcon(drawList, icon, (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) / 2, ImGui.GetColorU32(iconColour));
        Widgets.Tooltip($"{title}\n{explanation}", isMe ? "Your fingerprint is in Settings." : "Click to compare fingerprints.");

        // On the right: the menu (none for yourself, but its space kept so ranks line up), then the rank.
        var right = pos.X + width - 2 * scale;
        var menuX = right - button;
        var (rankIcon, rankColour) = RankIcon(member.Rank);
        var rank = RankLabel(member.Rank);
        var gap = 4 * scale;
        // A fixed column, wide enough for the widest rank, with the icon's slot always kept, so
        // every rank starts at the same place whether or not it has an icon.
        var iconSlot = Widgets.FixedIconWidth(FontAwesomeIcon.Crown);
        var rankWidth = iconSlot + gap + ImGui.CalcTextSize(RankLabel(Rank.Moderator)).X;
        var rankX = menuX - style.ItemSpacing.X - rankWidth;

        // The name, cut to what's left.
        var name = $"{member.User.Name}@{member.User.WorldName}";
        var you = isMe ? "you" : null;
        var youWidth = you != null ? ImGui.CalcTextSize(you).X + style.ItemSpacing.X : 0;
        ImGui.SameLine(0, 4 * scale);
        ImGui.AlignTextToFramePadding();
        var nameRoom = rankX - ImGui.GetCursorScreenPos().X - style.ItemSpacing.X - youWidth;
        var tooltip = member is { KeyReplaced: true, NewFingerprint: { } newFingerprint }
            ? $"{name}\nFingerprint in this channel: {member.Fingerprint ?? "-"}\nThe key they registered again with: {newFingerprint}"
            : $"{name}\nFingerprint: {member.Fingerprint ?? "-"}";
        Widgets.TextEllipsis(name, Math.Max(nameRoom, 20 * scale), null, tooltip);
        if (you != null) {
            ImGui.SameLine();
            ImGui.TextColored(Widgets.Muted, you);
        }

        // The rank, quietly, with a coloured crown or shield for admins and moderators.
        ImGui.SameLine(rankX - ImGui.GetWindowPos().X + ImGui.GetScrollX());
        ImGui.BeginGroup();
        var at = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(iconSlot, height));
        if (rankIcon is { } shownIcon) {
            Widgets.DrawIcon(drawList, shownIcon, new Vector2(at.X + iconSlot / 2, at.Y + height / 2), ImGui.GetColorU32(rankColour));
        }

        ImGui.SameLine(0, gap);
        ImGui.TextColored(Widgets.Muted, rank);
        ImGui.EndGroup();
        Widgets.Tooltip(RankDescription(member.Rank));

        if (!isMe) {
            ImGui.SameLine(menuX - ImGui.GetWindowPos().X + ImGui.GetScrollX());
            if (Widgets.GhostIconButton("##member-menu", FontAwesomeIcon.EllipsisV, $"Actions for {member.User.Name}")) {
                ImGui.OpenPopup("member-menu");
            }

            this.DrawMemberMenu(channel, member, session, snapshot);
        }

        ImGui.PopID();
    }

    private static (FontAwesomeIcon Icon, Vector4 Colour, string Title, string Explanation) Verification(MemberView member, bool isMe) {
        if (isMe) {
            return (FontAwesomeIcon.User, Widgets.Muted, "You", "Others compare your fingerprint with you to verify you.");
        }

        if (member.KeyReplaced) {
            return (FontAwesomeIcon.ExclamationTriangle, Widgets.Warning, "Registered again",
                "They registered again with a new identity key, which isn't a member of this channel. Compare fingerprints over /tell, then remove them and invite them again to let it in.");
        }

        if (member.KeyChanged) {
            return (FontAwesomeIcon.ExclamationTriangle, Widgets.Warning, "Key changed",
                "Their identity key changed, or this name now belongs to a different account. Compare fingerprints with them over /tell, then mark it verified.");
        }

        if (member.FingerprintCompared) {
            return (FontAwesomeIcon.CheckCircle, Widgets.Success, "Verified", "You compared fingerprints with them and marked them verified.");
        }

        return (FontAwesomeIcon.QuestionCircle, Widgets.Muted, "Not compared",
            "Their keys are trusted on first use: whoever invited them got them from the server. Compare fingerprints with them over /tell, then mark them verified.");
    }

    private void DrawMemberMenu(ChannelView channel, MemberView member, ClientSession session, SessionSnapshot snapshot) {
        if (!ImGui.BeginPopup("member-menu")) {
            return;
        }

        var user = member.User;
        var name = $"{user.Name}@{user.WorldName}";
        var enabled = !actions.Busy;
        ImGui.TextColored(Widgets.Muted, name);
        ImGuiHelpers.ScaledDummy(2);

        if (Widgets.MenuItem(FontAwesomeIcon.Fingerprint, "Compare fingerprints...")) {
            modals.CompareFingerprints(channel.Id, user.UserId);
        }

        // The same permissions as the server's: admins promote and demote; moderators and admins remove lower ranks.
        if (channel.MyRank == Rank.Admin && member.Rank is Rank.Member && Widgets.MenuItem(FontAwesomeIcon.ArrowUp, "Make moderator", enabled)) {
            actions.Run($"Promoting {user.Name}", () => session.SetRankAsync(channel.Id, user.UserId, Rank.Moderator));
        }

        if (channel.MyRank == Rank.Admin && member.Rank is Rank.Moderator && Widgets.MenuItem(FontAwesomeIcon.ArrowDown, "Make member", enabled)) {
            actions.Run($"Demoting {user.Name}", () => session.SetRankAsync(channel.Id, user.UserId, Rank.Member));
        }

        if (channel.MyRank >= Rank.Moderator && member.Rank < channel.MyRank) {
            if (member.Rank == Rank.Invited) {
                // Undone by inviting them again, and nobody's key changes: no need to ask.
                if (Widgets.MenuItem(FontAwesomeIcon.Times, "Cancel invite", enabled)) {
                    actions.Run($"Cancelling the invite for {user.Name}", () => session.KickAsync(channel.Id, user.UserId));
                }
            } else {
                // Asked for by holding Ctrl rather than with a dialog. The menu redraws every frame, so pressing Ctrl with it open works.
                var ctrl = ImGui.GetIO().KeyCtrl;
                if (Widgets.MenuItem(FontAwesomeIcon.UserMinus, "Remove from channel", enabled && ctrl, Widgets.Error)) {
                    actions.Run($"Removing {user.Name}", () => session.KickAsync(channel.Id, user.UserId));
                }

                Widgets.Tooltip(ctrl ? $"Remove {name}. They'll need a new invite to come back, and a new key is made that they don't get." : "Hold Ctrl to remove.",
                    ctrl ? null : "They'd need a new invite to come back.");
            }
        }

        ImGui.Separator();
        var blocked = snapshot.BlockedUsers.Any(b => b.UserId == user.UserId);
        if (blocked) {
            if (Widgets.MenuItem(FontAwesomeIcon.Undo, "Unblock", enabled)) {
                actions.Run($"Unblocking {user.Name}", () => session.UnblockUser(user.UserId));
            }
        } else if (Widgets.MenuItem(FontAwesomeIcon.Ban, "Block", enabled)) {
            actions.Run($"Blocking {user.Name}", () => session.BlockUser(user.UserId));
        }

        Widgets.Tooltip(blocked
            ? "Show their messages and invites again."
            : "Hide their messages, and silently decline their invites.", blocked ? null : "Undo in Settings > Blocked users.");
        ImGui.EndPopup();
    }

    private void DrawInvitePopup(ChannelView channel, ClientSession session, SessionSnapshot snapshot) {
        if (!ImGui.BeginPopup("invite")) {
            return;
        }

        var width = 260 * Widgets.Scale;
        ImGui.TextUnformatted("Invite someone");
        ImGui.TextColored(Widgets.Muted, $"to {Widgets.Ellipsize(channel.DisplayName, width)}");
        ImGuiHelpers.ScaledDummy(4);

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
            ImGuiHelpers.ScaledDummy(2);
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
            ImGui.TextColored(Widgets.Muted, $"Test server: \"Echo Bot\" on world \"{ProtocolInfo.DebugWorldName}\" answers.");
            ImGui.PopTextWrapPos();
            if (ImGui.SmallButton("Fill in Echo Bot")) {
                this._inviteName = "Echo Bot";
                this._inviteWorld = ProtocolInfo.DebugWorldName;
            }
        }

        ImGuiHelpers.ScaledDummy(4);
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
        if (Widgets.GhostButton("Cancel")) {
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

    private static string RankDescription(Rank rank) => rank switch {
        Rank.Admin => "Admin: can invite, remove, promote and rename.",
        Rank.Moderator => "Moderator: can invite and remove members.",
        Rank.Member => "Member.",
        Rank.Invited => "Invited: hasn't answered yet.",
        _ => "Not a member.",
    };

    private static (FontAwesomeIcon? Icon, Vector4 Colour) RankIcon(Rank rank) => rank switch {
        Rank.Admin => (FontAwesomeIcon.Crown, ImGuiColors.DalamudViolet),
        Rank.Moderator => (FontAwesomeIcon.ShieldAlt, ImGuiColors.TankBlue),
        Rank.Invited => (FontAwesomeIcon.Envelope, Widgets.Muted),
        _ => (null, Widgets.Muted),
    };
}
