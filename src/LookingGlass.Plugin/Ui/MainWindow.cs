using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using LookingGlass.Core.Client;
using LookingGlass.Protocol;

namespace LookingGlass.Plugin.Ui;

/// <summary>
/// The main window: a top bar (connection, character, invites, settings), the channel list on the
/// left, the selected channel on the right, and a status line. Before there are channels it shows
/// what to do instead: log in, connect, register, or create the first channel. Reads only
/// immutable session snapshots; slow work runs through <see cref="UiActions"/>.
/// </summary>
public sealed class MainWindow : Window {
    private const string Title = "LookingGlass";
    private const string Id = "###lookingglass-main";
    private const float DefaultSidebarWidth = 200;
    private const float MinSidebarWidth = 150;
    private const float MinDetailWidth = 380;

    private readonly Configuration _config;
    private readonly SessionManager _sessions;
    private readonly UiActions _actions;
    private readonly Action _toggleSettings;
    private readonly Modals _modals;
    private readonly ChannelPane _pane;

    private string? _selectedChannel;
    private volatile string? _selectAfterCreate;
    private string _newChannelName = "";
    private float _sidebarWidth = DefaultSidebarWidth;

    public MainWindow(Configuration config, SessionManager sessions, UiActions actions, Action toggleSettings) : base(Title + Id) {
        this._config = config;
        this._sessions = sessions;
        this._actions = actions;
        this._toggleSettings = toggleSettings;
        this._modals = new Modals(actions);
        this._pane = new ChannelPane(sessions, actions, this._modals);
        this._pane.Closed += () => this._selectedChannel = null;

        // Laid out to fit: the panes scroll, the window doesn't.
        this.Flags |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        this.Size = new Vector2(760, 500);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints {
            MinimumSize = new Vector2(MinSidebarWidth + MinDetailWidth + 40, 380),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Update() {
        var unread = this._sessions.Unread.Total;
        this.WindowName = unread > 0 ? $"{Title} ({UnreadCounter.Format(unread)}){Id}" : Title + Id;
    }

    public override void OnClose() {
        this._sessions.Unread.Viewing(null);
    }

    public override void Draw() {
        var snapshot = this._sessions.Snapshot;
        var session = this._sessions.Session;

        this.DrawTopBar(snapshot, session);
        ImGui.Separator();

        // The body takes what the separator and status line below leave (the separator is a pixel high, plus spacing).
        var footer = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y * 2 + 2;
        if (ImGui.BeginChild("##body", new Vector2(0, -footer), false)) {
            this.DrawBody(snapshot, session);
        }

        ImGui.EndChild();
        ImGui.Separator();
        this._actions.DrawStatusBar();

        this._modals.Draw(snapshot, session);
    }

    // ================================================================ top bar

    private void DrawTopBar(SessionSnapshot snapshot, ClientSession? session) {
        var player = this._sessions.Player;
        var style = ImGui.GetStyle();
        var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        var buttons = Widgets.IconButtonWidth(FontAwesomeIcon.Envelope) + Widgets.IconButtonWidth(FontAwesomeIcon.Cog) + style.ItemSpacing.X;

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(Title);
        ImGui.SameLine(0, style.ItemSpacing.X * 2);

        var (state, colour) = ConnectionLabel(snapshot.State, session != null, player != null);
        Widgets.Pill(state, colour, snapshot.StatusText ?? (session == null && player != null ? "Not connected. Press Connect, or check the server in Settings." : null));

        if (session == null && player != null) {
            ImGui.SameLine();
            if (ImGui.Button("Connect")) {
                this._sessions.Connect();
            }
        }

        if (player != null) {
            ImGui.SameLine(0, style.ItemSpacing.X * 2);
            ImGui.AlignTextToFramePadding();
            var width = right - buttons - style.ItemSpacing.X * 2 - ImGui.GetCursorPosX();
            if (width > 40 * Widgets.Scale) {
                Widgets.TextEllipsis($"{player.Name} @ {player.HomeWorldName}", width, Widgets.Muted);
            }
        }

        ImGui.SameLine(right - buttons);
        this.DrawInvitesButton(snapshot, session);
        ImGui.SameLine();
        if (Widgets.IconButton("##settings", FontAwesomeIcon.Cog, "Settings")) {
            this._toggleSettings();
        }
    }

    private static (string Text, Vector4 Colour) ConnectionLabel(ConnectionState state, bool hasSession, bool loggedIn) {
        if (!loggedIn) {
            return ("Not logged in", ImGuiColors.DalamudGrey);
        }

        if (!hasSession) {
            return ("Stopped", ImGuiColors.DalamudGrey);
        }

        return state switch {
            ConnectionState.Ready => ("Connected", ImGuiColors.HealerGreen),
            ConnectionState.Connecting => ("Connecting", ImGuiColors.DalamudOrange),
            ConnectionState.Reconnecting => ("Reconnecting", ImGuiColors.DalamudOrange),
            ConnectionState.Unregistered or ConnectionState.Registering => ("Not registered", ImGuiColors.DalamudRed),
            _ => ("Stopped", ImGuiColors.DalamudGrey),
        };
    }

    private void DrawInvitesButton(SessionSnapshot snapshot, ClientSession? session) {
        var count = snapshot.State == ConnectionState.Ready ? snapshot.Invites.Length : 0;
        if (Widgets.IconButton("##invites", FontAwesomeIcon.Envelope, count switch {
                0 => "Invites: none waiting",
                1 => "1 invite waiting",
                _ => $"{count} invites waiting",
            })) {
            ImGui.OpenPopup("invites");
        }

        if (count > 0) {
            var max = ImGui.GetItemRectMax();
            var min = ImGui.GetItemRectMin();
            Widgets.Badge(new Vector2(max.X + 3 * Widgets.Scale, min.Y + ImGui.GetTextLineHeight() * 0.35f), count > 9 ? "9+" : count.ToString(), ImGuiColors.DalamudRed);
        }

        if (!ImGui.BeginPopup("invites")) {
            return;
        }

        ImGui.TextUnformatted("Invites");
        ImGui.Separator();
        if (count == 0 || session == null) {
            ImGui.TextColored(Widgets.Muted, "No invites waiting.");
        } else {
            var first = true;
            foreach (var invite in snapshot.Invites) {
                if (!first) {
                    ImGui.Separator();
                }

                first = false;
                this.DrawInvite(invite, session);
            }
        }

        ImGui.EndPopup();
    }

    private void DrawInvite(InviteView invite, ClientSession session) {
        ImGui.PushID(invite.ChannelId);
        var width = 360 * Widgets.Scale;
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);

        var name = invite.ChannelName ?? "(couldn't verify)";
        ImGui.Spacing();
        ImGui.TextUnformatted(Widgets.Ellipsize(name, width));
        ImGui.TextColored(Widgets.Muted, $"from {invite.Inviter.Name}@{invite.Inviter.WorldName} · {Ago(invite.Created)}");

        if (!invite.Verified) {
            Widgets.IconText(FontAwesomeIcon.ExclamationTriangle, "Couldn't verify this invite", Widgets.Warning);
            ImGui.TextColored(Widgets.Muted, "It isn't signed by the inviter's current key, so it can't be accepted. Decline it, or ask them to invite you again.");
        } else if (invite.InviterKeyChanged) {
            // An inviter whose key changed may not be who they were: make the user check first.
            Widgets.IconText(FontAwesomeIcon.ExclamationTriangle, "Their key changed", Widgets.Warning);
            ImGui.TextColored(Widgets.Muted,
                "Their identity key changed, or this name now belongs to a different account. Compare fingerprints with them over /tell before accepting.");
            ImGui.TextColored(Widgets.Muted, "Their fingerprint");
            Modals.Fingerprint(invite.InviterFingerprint, null);
            ImGui.TextColored(Widgets.Muted, "Yours");
            Modals.Fingerprint(this._sessions.Snapshot.MyFingerprint, "##copy-mine");
            ImGui.BeginDisabled(this._actions.Busy || invite.InviterFingerprint == null);
            if (ImGui.Button("Mark verified") && invite.InviterFingerprint is { } shown) {
                // The fingerprint shown above, and only that.
                this._actions.Run("Marking verified", () => session.AcknowledgeKeyChange(invite.Inviter.UserId, shown));
            }

            ImGui.EndDisabled();
        }

        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        ImGui.BeginDisabled(this._actions.Busy || !invite.Verified || invite.InviterKeyChanged);
        if (ImGui.Button("Accept")) {
            this._actions.Run($"Joining {name}", async () => {
                await session.RespondToInviteAsync(invite.ChannelId, true);
                this._selectAfterCreate = invite.ChannelId;
            });
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(this._actions.Busy);
        if (ImGui.Button("Decline")) {
            this._actions.Run("Declining", () => session.RespondToInviteAsync(invite.ChannelId, false));
        }

        ImGui.SameLine();
        if (ImGui.Button("Block")) {
            this._actions.Run($"Blocking {invite.Inviter.Name}", () => session.BlockUser(invite.Inviter.UserId));
        }

        Widgets.Tooltip("Decline, silently decline their future invites, and hide their messages.\nUndo in Settings > Blocked users.");
        ImGui.EndDisabled();
        ImGui.Spacing();
        ImGui.PopID();
    }

    private static string Ago(DateTimeOffset time) {
        var age = DateTimeOffset.Now - time;
        return age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? $"{(int) age.TotalMinutes} min ago"
            : age.TotalDays < 1 ? $"{(int) age.TotalHours} h ago"
            : time.ToLocalTime().ToString("g");
    }

    // ================================================================ body

    private void DrawBody(SessionSnapshot snapshot, ClientSession? session) {
        var player = this._sessions.Player;
        if (player == null) {
            Widgets.Centred(FontAwesomeIcon.UserCircle, "Not logged in", "Log in to a character to use LookingGlass.");
            return;
        }

        if (session == null) {
            Widgets.Centred(FontAwesomeIcon.Plug, "Not connected", $"Server: {this._config.ServerUrl}");
            var width = ImGui.GetContentRegionAvail().X;
            Widgets.CentreNext(Widgets.ButtonWidth("Connect") + Widgets.ButtonWidth("Settings") + ImGui.GetStyle().ItemSpacing.X, width);
            if (ImGui.Button("Connect")) {
                this._sessions.Connect();
            }

            ImGui.SameLine();
            if (ImGui.Button("Settings")) {
                this._toggleSettings();
            }

            return;
        }

        switch (snapshot.State) {
            case ConnectionState.Unregistered:
            case ConnectionState.Registering:
                this.DrawRegistration(snapshot, session, player);
                break;
            case ConnectionState.Ready when snapshot.Channels.IsEmpty:
                this.DrawNoChannels(snapshot, session);
                break;
            case ConnectionState.Ready:
                this.DrawChannels(snapshot, session);
                break;
            case ConnectionState.Reconnecting:
                Widgets.Centred(FontAwesomeIcon.Sync, "Reconnecting...", snapshot.StatusText ?? "The connection to the server dropped. Trying again.");
                break;
            default:
                Widgets.Centred(FontAwesomeIcon.Sync, "Connecting...", snapshot.StatusText ?? "Waiting for the server.");
                break;
        }
    }

    private void DrawRegistration(SessionSnapshot snapshot, ClientSession session, PlayerInfo player) {
        var width = Math.Min(ImGui.GetContentRegionAvail().X, 520 * Widgets.Scale);
        var indent = Math.Max(0, (ImGui.GetContentRegionAvail().X - width) / 2);
        ImGui.Indent(indent);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
        ImGui.Spacing();
        ImGui.TextUnformatted("Register this character");
        ImGui.TextColored(Widgets.Muted, "LookingGlass checks that the character is yours with a short code you put in your Lodestone profile for a few minutes.");
        ImGui.Spacing();
        ImGui.Spacing();

        var challenge = snapshot.PendingChallenge;
        var character = new Character { Name = player.Name, WorldId = player.HomeWorldId, WorldName = player.HomeWorldName };

        // 1. The character.
        BeginStep(1, challenge != null, false);
        ImGui.TextUnformatted($"{player.Name} @ {player.HomeWorldName}");
        if (challenge == null) {
            ImGui.TextColored(Widgets.Muted, "Detected from the game. Not you? Log in to the character you want to register.");
            ImGui.BeginDisabled(this._actions.Busy);
            if (ImGui.Button("Get a code")) {
                this._actions.Run("Starting registration", () => session.StartRegistrationAsync(character));
            }

            ImGui.EndDisabled();
        } else {
            ImGui.TextColored(Widgets.Muted, "Detected from the game.");
        }

        EndStep();

        // 2. The code.
        BeginStep(2, false, challenge == null);
        ImGui.TextUnformatted("Copy this code");
        if (challenge != null) {
            ImGui.AlignTextToFramePadding();
            using (Services.PluginInterface.UiBuilder.MonoFontHandle.Push()) {
                ImGui.TextColored(ImGuiColors.TankBlue, challenge.Code);
            }

            ImGui.SameLine();
            if (Widgets.IconButton("##copy-code", FontAwesomeIcon.Copy, "Copy the code")) {
                ImGui.SetClipboardText(challenge.Code);
            }

            ImGui.TextColored(Widgets.Muted, $"It expires at {DateTimeOffset.FromUnixTimeSeconds(challenge.ExpiresUnix).ToLocalTime():t}.");
        }

        EndStep();

        // 3. The Lodestone.
        BeginStep(3, false, challenge == null);
        ImGui.TextUnformatted("Paste it into your Lodestone profile");
        ImGui.TextColored(Widgets.Muted, "Anywhere in your character profile's text, then save. You can delete it again once you're registered.");
        EndStep();

        // 4. Verify.
        BeginStep(4, false, challenge == null);
        ImGui.TextUnformatted("Verify");
        if (challenge != null) {
            ImGui.TextColored(Widgets.Muted, "The Lodestone can take a minute to show changes.");
            ImGui.BeginDisabled(this._actions.Busy);
            if (ImGui.Button("Verify")) {
                this._actions.Run("Verifying", () => session.CompleteRegistrationAsync());
            }

            ImGui.SameLine();
            if (ImGui.Button("Get a new code")) {
                this._actions.Run("Starting registration", () => session.StartRegistrationAsync(character));
            }

            ImGui.EndDisabled();
        }

        EndStep();
        ImGui.PopTextWrapPos();
        ImGui.Unindent(indent);
    }

    /// <summary>A numbered circle (a check once done), with the step's lines beside it until <see cref="EndStep"/>. Later steps are greyed.</summary>
    private static void BeginStep(int number, bool done, bool later) {
        var size = ImGui.GetFrameHeight();
        var pos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(size, size));
        var drawList = ImGui.GetWindowDrawList();
        var centre = pos + new Vector2(size / 2, size / 2);
        var colour = done ? Widgets.Success : later ? Widgets.Muted : ImGuiColors.TankBlue;
        drawList.AddCircleFilled(centre, size / 2, ImGui.GetColorU32(colour with { W = colour.W * (later ? 0.4f : 0.9f) }));
        if (done) {
            // The icon font's check mark: the default font may not have one.
            using (Services.PluginInterface.UiBuilder.IconFontHandle.Push()) {
                var icon = FontAwesomeIcon.Check.ToIconString();
                drawList.AddText(centre - ImGui.CalcTextSize(icon) / 2, 0xFFFFFFFF, icon);
            }
        } else {
            var text = number.ToString();
            drawList.AddText(centre - ImGui.CalcTextSize(text) / 2, 0xFFFFFFFF, text);
        }

        ImGui.SameLine(0, 10 * Widgets.Scale);
        ImGui.BeginGroup();
        ImGui.BeginDisabled(later);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + ImGui.GetStyle().FramePadding.Y);
    }

    private static void EndStep() {
        ImGui.EndDisabled();
        ImGui.EndGroup();
        ImGui.Spacing();
        ImGui.Spacing();
    }
    private void DrawNoChannels(SessionSnapshot snapshot, ClientSession session) {
        this._sessions.Unread.Viewing(null);
        Widgets.Centred(FontAwesomeIcon.Comments, "Create your first channel",
            "A channel is an end-to-end encrypted group chat. Create one, then invite friends by character name and home world.");
        var width = ImGui.GetContentRegionAvail().X;
        Widgets.CentreNext(Widgets.ButtonWidth("Create a channel"), width);
        if (ImGui.Button("Create a channel")) {
            this._newChannelName = "";
            ImGui.OpenPopup("new-channel");
        }

        this.DrawNewChannelPopup(session);
        if (!snapshot.Invites.IsEmpty) {
            ImGui.Spacing();
            var hint = snapshot.Invites.Length == 1 ? "Or accept the invite waiting for you (the envelope above)." : "Or accept one of the invites waiting for you (the envelope above).";
            Widgets.CentreNext(ImGui.CalcTextSize(hint).X, width);
            ImGui.TextColored(Widgets.Muted, hint);
        }
    }

    // ================================================================ channels

    private void DrawChannels(SessionSnapshot snapshot, ClientSession session) {
        if (this._selectAfterCreate is { } created && snapshot.FindChannel(created) != null) {
            this._selectedChannel = created;
            this._selectAfterCreate = null;
        }

        var selected = (this._selectedChannel != null ? snapshot.FindChannel(this._selectedChannel) : null) ?? snapshot.Channels[0];
        this._selectedChannel = selected.Id;

        var scale = Widgets.Scale;
        var available = ImGui.GetContentRegionAvail();
        var splitter = 6 * scale;
        var maxSidebar = Math.Max(MinSidebarWidth, (available.X - splitter) / scale - MinDetailWidth);
        this._sidebarWidth = Math.Clamp(this._sidebarWidth, MinSidebarWidth, maxSidebar);

        this.DrawSidebar(snapshot, session, this._sidebarWidth * scale);

        // A handle between the panes to resize the channel list.
        ImGui.SameLine(0, 0);
        ImGui.InvisibleButton("##splitter", new Vector2(splitter, available.Y));
        if (ImGui.IsItemHovered() || ImGui.IsItemActive()) {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
        }

        if (ImGui.IsItemActive()) {
            this._sidebarWidth = Math.Clamp(this._sidebarWidth + ImGui.GetIO().MouseDelta.X / scale, MinSidebarWidth, maxSidebar);
        }

        ImGui.SameLine(0, 0);
        if (ImGui.BeginChild("##channel", Vector2.Zero, false)) {
            // Shown in an open (drawn) window: read, and what arrives meanwhile doesn't count.
            this._sessions.Unread.Viewing(selected.Id);
            this._pane.Draw(selected, session, snapshot);
        }

        ImGui.EndChild();
    }

    private void DrawSidebar(SessionSnapshot snapshot, ClientSession session, float width) {
        if (!ImGui.BeginChild("##sidebar", new Vector2(width, 0), true)) {
            ImGui.EndChild();
            return;
        }

        ImGui.TextColored(Widgets.Muted, "Channels");
        ImGui.SameLine();
        ImGui.TextColored(Widgets.Muted, $"· {snapshot.Channels.Length}");
        ImGui.Spacing();

        if (ImGui.BeginChild("##channel-list", new Vector2(0, -ImGui.GetFrameHeightWithSpacing()), false)) {
            foreach (var channel in snapshot.Channels) {
                this.DrawChannelRow(channel);
            }
        }

        ImGui.EndChild();

        ImGui.BeginDisabled(this._actions.Busy);
        if (ImGui.Button("+ New channel", new Vector2(-1, 0))) {
            this._newChannelName = "";
            ImGui.OpenPopup("new-channel");
        }

        ImGui.EndDisabled();
        this.DrawNewChannelPopup(session);
        ImGui.EndChild();
    }

    private void DrawChannelRow(ChannelView channel) {
        var scale = Widgets.Scale;
        var style = ImGui.GetStyle();
        var slot = this._sessions.SlotOf(channel.Id);
        var nickname = this._sessions.NicknameOf(channel.Id);
        var colour = this._sessions.ColourOf(channel.Id) is { } row ? ChannelPalette.ColourOf(row) : null;
        var unread = this._sessions.Unread.CountOf(channel.Id);
        var attention = ChannelAttention.Of(channel);

        var height = ImGui.GetFrameHeight();
        if (ImGui.Selectable($"##row-{channel.Id}", this._selectedChannel == channel.Id, ImGuiSelectableFlags.None, new Vector2(0, height))) {
            this._selectedChannel = channel.Id;
        }

        var hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        var textY = min.Y + (height - ImGui.GetTextLineHeight()) / 2;
        var textColour = ImGui.GetColorU32(ImGuiCol.Text);
        var mutedColour = ImGui.GetColorU32(Widgets.Muted);

        // The channel's colour as an accent along the left edge.
        if (colour is { } accent) {
            drawList.AddRectFilled(min with { X = min.X + 1 * scale }, new Vector2(min.X + 4 * scale, max.Y), ImGui.GetColorU32(accent), 2 * scale);
        }

        // From the right: unread count, attention icon, nickname.
        var x = max.X - style.FramePadding.X;
        if (unread > 0) {
            x -= Widgets.Badge(new Vector2(x, min.Y + height / 2), UnreadCounter.Format(unread), ImGuiColors.TankBlue) + 4 * scale;
        }

        if (attention.Level != AttentionLevel.None) {
            var icon = attention.Level == AttentionLevel.Warning ? FontAwesomeIcon.ExclamationTriangle : FontAwesomeIcon.HourglassHalf;
            var iconColour = attention.Level == AttentionLevel.Warning ? Widgets.Warning : Widgets.Muted;
            using (Services.PluginInterface.UiBuilder.IconFontHandle.Push()) {
                var text = icon.ToIconString();
                var size = ImGui.CalcTextSize(text);
                x -= size.X;
                drawList.AddText(new Vector2(x, min.Y + (height - size.Y) / 2), ImGui.GetColorU32(iconColour), text);
                x -= 4 * scale;
            }
        }

        // The number, then the name, then the nickname (muted) if there's room.
        var left = min.X + 8 * scale;
        var number = slot is { } s ? s.ToString() : "-";
        var numberWidth = ImGui.CalcTextSize("50").X;
        drawList.AddText(new Vector2(left + numberWidth - ImGui.CalcTextSize(number).X, textY), mutedColour, number);
        left += numberWidth + 8 * scale;

        var room = x - left - 4 * scale;
        var nameWidth = ImGui.CalcTextSize(channel.DisplayName).X;
        if (nickname != null && room > 0) {
            var nicknameRoom = Math.Max(0, Math.Min(room * 0.45f, room - nameWidth - 8 * scale));
            var shownNickname = nicknameRoom > ImGui.CalcTextSize("...").X ? Widgets.Ellipsize(nickname, nicknameRoom) : null;
            if (shownNickname != null) {
                var nicknameWidth = ImGui.CalcTextSize(shownNickname).X;
                drawList.AddText(new Vector2(x - 4 * scale - nicknameWidth, textY), mutedColour, shownNickname);
                room -= nicknameWidth + 8 * scale;
            }
        }

        var name = Widgets.Ellipsize(channel.DisplayName, Math.Max(room, 0));
        drawList.AddText(new Vector2(left, textY), textColour, name);

        if (hovered) {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28);
            ImGui.TextUnformatted(channel.DisplayName);
            var commands = slot is { } n ? $"{CommandSlots.Prefix}{n}" : "no number";
            if (nickname != null) {
                commands += $"  or  {CommandSlots.Prefix} {nickname}";
            }

            ImGui.TextColored(Widgets.Muted, commands);
            if (unread > 0) {
                ImGui.TextUnformatted(unread == 1 ? "1 unread message" : $"{unread} unread messages");
            }

            foreach (var reason in attention.Reasons) {
                ImGui.TextColored(attention.Level == AttentionLevel.Warning ? Widgets.Warning : Widgets.Muted, reason);
            }

            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    private void DrawNewChannelPopup(ClientSession session) {
        if (!ImGui.BeginPopup("new-channel")) {
            return;
        }

        ImGui.TextUnformatted("New channel");
        ImGui.TextColored(Widgets.Muted, "You'll be its admin. Members see its name.");
        ImGui.Spacing();
        if (ImGui.IsWindowAppearing()) {
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.SetNextItemWidth(260 * Widgets.Scale);
        var enter = ImGui.InputTextWithHint("##new-channel-name", "Channel name", ref this._newChannelName, 256, ImGuiInputTextFlags.EnterReturnsTrue);
        var name = this._newChannelName.Trim();
        var problem = ChannelPane.ChannelNameProblem(name);
        if (problem != null && name.Length > 0) {
            ImGui.TextColored(Widgets.Error, problem);
        }

        var ready = problem == null && !this._actions.Busy;
        ImGui.BeginDisabled(!ready);
        if ((ImGui.Button("Create") || enter) && ready) {
            this._newChannelName = "";
            this._actions.Run($"Creating {name}", async () => {
                this._selectAfterCreate = await session.CreateChannelAsync(name);
            });
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }
}
