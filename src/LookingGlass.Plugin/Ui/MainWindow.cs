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
/// The main window: a line with the connection and your character (and the invites envelope) on
/// top, the channel list on the left, the selected channel on the right, and a status line. Before
/// there are channels it shows what to do instead: log in, connect, register, or create the first
/// channel. Settings are behind the gear in the title bar. Reads only immutable session snapshots;
/// slow work runs through <see cref="UiActions"/>.
/// </summary>
public sealed class MainWindow : Window {
    private const string Title = "LookingGlass";
    private const string Id = "###lookingglass-main";
    // Unscaled pixels: the window's sizes are scaled by Dalamud, the panes' by Widgets.Scale.
    private const float DefaultSidebarWidth = 210;
    private const float MinSidebarWidth = 150;
    private const float MinDetailWidth = 380;
    private const float SplitterWidth = 9;

    private readonly Configuration _config;
    private readonly SessionManager _sessions;
    private readonly UiActions _actions;
    private readonly Action _toggleSettings;
    private readonly Action _openSettings;
    private readonly Modals _modals;
    private readonly ChannelPane _pane;
    private readonly ChannelWindows _windows;

    private string? _selectedChannel;
    // The registration code last scrolled into view, so a new code is scrolled to once, not every frame.
    private string? _codeScrolledTo;
    private volatile string? _selectAfterCreate;
    private string _newChannelName = "";
    private float _sidebarWidth = DefaultSidebarWidth;

    // Per invite (channel ID), the inviter's fingerprint as first shown since the invites popup opened: what "Mark verified"
    // or "It's really them" vouches for, so a key that changes again meanwhile is refused rather than marked unseen.
    private readonly Dictionary<string, string?> _inviterShown = new();

    /// <param name="toggleSettings">Opens the settings window, or closes it if open (the gear).</param>
    /// <param name="openSettings">Opens the settings window, and brings it to the front.</param>
    public MainWindow(Configuration config, SessionManager sessions, UiActions actions, UiFonts fonts, ChannelWindows windows, Action toggleSettings,
        Action openSettings) : base(Title + Id) {
        this._config = config;
        this._sessions = sessions;
        this._actions = actions;
        this._toggleSettings = toggleSettings;
        this._openSettings = openSettings;
        this._modals = new Modals(actions, () => config.AdvancedMode);
        this._windows = windows;
        this._pane = new ChannelPane(sessions, actions, this._modals, fonts, windows);
        this._pane.Closed += () => this._selectedChannel = null;

        // Laid out to fit: the panes scroll, the window doesn't.
        this.Flags |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        // Dalamud scales these by the global scale itself.
        this.Size = new Vector2(780, 520);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints {
            MinimumSize = new Vector2(MinSidebarWidth + SplitterWidth + MinDetailWidth + 20, 380),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        this.TitleBarButtons.Add(new TitleBarButton {
            Icon = FontAwesomeIcon.Cog,
            IconOffset = new Vector2(1.5f, 1),
            Click = _ => toggleSettings(),
            ShowTooltip = () => ImGui.SetTooltip("Settings"),
        });
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

        this.DrawTopLine(snapshot, session);
        ImGuiHelpers.ScaledDummy(2);

        // The body takes what the status line below leaves.
        var footer = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y;
        if (ImGui.BeginChild("##body", new Vector2(0, -footer), false)) {
            this.DrawBody(snapshot, session);
        }

        ImGui.EndChild();
        this._actions.DrawStatusBar();

        this._modals.Draw(snapshot, session);
    }

    // ================================================================ top line

    /// <summary>
    /// The connection as a coloured dot and a word (the whole status in its tooltip), the character
    /// in muted text, and on the right the invites envelope with a count on its corner.
    /// </summary>
    private void DrawTopLine(SessionSnapshot snapshot, ClientSession? session) {
        var player = this._sessions.Player;
        var scale = Widgets.Scale;
        var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        var envelope = session != null ? Widgets.GhostIconButtonWidth + 6 * scale : 0;

        var (state, colour, detail) = ConnectionLabel(snapshot, session != null, player != null, this._config.AdvancedMode);
        ImGui.AlignTextToFramePadding();
        ImGui.BeginGroup();
        Widgets.Dot(colour, ImGui.GetFrameHeight());
        ImGui.SameLine(0, 6 * scale);
        ImGui.TextUnformatted(state);
        ImGui.EndGroup();
        Widgets.Tooltip(detail, $"Server: {this._config.ServerUrl}");

        if (player != null) {
            ImGui.SameLine(0, 12 * scale);
            var width = right - envelope - ImGui.GetStyle().ItemSpacing.X - ImGui.GetCursorPosX();
            if (width > 40 * scale) {
                Widgets.TextEllipsis($"{player.Name} @ {player.HomeWorldName}", width, Widgets.Muted, "The character you're logged in as.");
            }
        }

        // Invites only come over a connection.
        if (session != null) {
            ImGui.SameLine(right - envelope + 6 * scale);
            this.DrawInvitesButton(snapshot, session);
        }
    }

    private static (string Text, Vector4 Colour, string Detail) ConnectionLabel(SessionSnapshot snapshot, bool hasSession, bool loggedIn, bool advanced) {
        var status = snapshot.StatusFor(advanced);
        if (!loggedIn) {
            return ("Not logged in", ImGuiColors.DalamudGrey, "Log in to a character to use LookingGlass.");
        }

        if (!hasSession) {
            return ("Not connected", ImGuiColors.DalamudGrey, status ?? "Not connected. Connect below, or check the server in Settings (the gear in the title bar).");
        }

        return snapshot.State switch {
            ConnectionState.Ready => ("Connected", ImGuiColors.HealerGreen, status ?? "Connected to the server."),
            ConnectionState.Connecting => ("Connecting...", ImGuiColors.DalamudOrange, status ?? "Waiting for the server."),
            ConnectionState.Reconnecting => ("Reconnecting...", ImGuiColors.DalamudOrange, status ?? "The connection to the server dropped. Trying again."),
            ConnectionState.LoginNotRecognized => ("Login not recognised", Widgets.Warning, status ?? PlainMessages.LoginNotRecognized.For(advanced)),
            ConnectionState.Blocked => ("Blocked by the server", Widgets.Warning, status ?? "This server's operator has blocked you from it."),
            ConnectionState.Registering when snapshot.LoginRejected => ("Registering again", Widgets.Warning,
                status ?? "The server didn't recognise your login, so you're registering again. Follow the steps below."),
            ConnectionState.Unregistered or ConnectionState.Registering => ("Not registered", ImGuiColors.DalamudOrange,
                status ?? "Connected, but this character isn't registered yet. Register it below."),
            _ => ("Stopped", ImGuiColors.DalamudGrey, status ?? "Not connected."),
        };
    }

    private void DrawInvitesButton(SessionSnapshot snapshot, ClientSession session) {
        var count = snapshot.State == ConnectionState.Ready ? snapshot.Invites.Length : 0;
        if (Widgets.GhostIconButton("##invites", FontAwesomeIcon.Envelope, count switch {
                0 => "Invites: none waiting",
                1 => "1 invite waiting",
                _ => $"{count} invites waiting",
            }, count > 0 ? Widgets.Text : null)) {
            ImGui.OpenPopup("invites");
        }

        if (count > 0) {
            Widgets.CornerBadge(count > 9 ? "9+" : count.ToString(), ImGuiColors.DalamudRed);
        }

        if (!ImGui.BeginPopup("invites")) {
            return;
        }

        if (ImGui.IsWindowAppearing()) {
            this._inviterShown.Clear();
        }

        ImGui.TextUnformatted("Invites");
        ImGuiHelpers.ScaledDummy(2);
        if (count == 0) {
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

        // A verified invite without a name was made for the key you had before re-verifying your character.
        var name = invite.ChannelName ?? (invite.Verified ? "(name shows once you join)" : "(couldn't verify)");
        ImGui.Spacing();
        ImGui.TextUnformatted(Widgets.Ellipsize(name, width));
        ImGui.TextColored(Widgets.Muted, $"from {invite.Inviter.Name}@{invite.Inviter.WorldName} · {Ago(invite.Created)}");

        var advanced = this._config.AdvancedMode;
        string? inviterShown = null;
        if (invite.InviterKeyChanged && !this._inviterShown.TryGetValue(invite.ChannelId, out inviterShown)) {
            inviterShown = this._inviterShown[invite.ChannelId] = invite.InviterFingerprint;
        }

        if (invite is { Verified: true, ChannelName: null }) {
            ImGui.TextColored(Widgets.Muted, advanced
                ? "This invite came with you to your new key; its channel's name was sealed to your old one, so it shows once you've joined."
                : "This invite is from before you set up LookingGlass again, so its channel's name shows once you've joined.");
        }

        if (!invite.Verified) {
            Widgets.IconText(FontAwesomeIcon.ExclamationTriangle, advanced ? "Couldn't verify this invite" : "Couldn't check this invite", Widgets.Warning);
            ImGui.TextColored(Widgets.Muted, advanced
                ? "It isn't signed by the inviter's current key, so it can't be accepted. Decline it, or ask them to invite you again."
                : "It couldn't be checked as really from them, so it can't be accepted. Decline it, or ask them to invite you again.");
        } else if (invite.InviterKeyChanged && advanced) {
            // An inviter whose key changed may not be who they were: make the user check first.
            Widgets.IconText(FontAwesomeIcon.ExclamationTriangle, "Their key changed", Widgets.Warning);
            ImGui.TextColored(Widgets.Muted,
                "Their identity key changed, or this name now belongs to a different account. Compare fingerprints with them over /tell before accepting.");
            ImGui.TextColored(Widgets.Muted, "Their fingerprint");
            Modals.Fingerprint(inviterShown, null);
            ImGui.TextColored(Widgets.Muted, "Yours");
            Modals.Fingerprint(this._sessions.Snapshot.MyFingerprint, "##copy-mine");
            ImGui.BeginDisabled(this._actions.Busy || inviterShown == null);
            if (ImGui.Button("Mark verified") && inviterShown is { } shown) {
                // The fingerprint shown above, and only that.
                this._actions.Run("Marking verified", () => session.AcknowledgeKeyChange(invite.Inviter.UserId, shown));
            }

            ImGui.EndDisabled();
        } else if (invite.InviterKeyChanged) {
            // The same check in simple mode's words: the user confirms over /tell that it's them, rather than comparing fingerprints.
            Widgets.IconText(FontAwesomeIcon.ExclamationTriangle, "Check it's really them", Widgets.Warning);
            ImGui.TextColored(Widgets.Muted, Modals.ChangedText(invite.Inviter.Name) + " Check with them over /tell before accepting.");
            ImGui.BeginDisabled(this._actions.Busy || inviterShown == null);
            if (ImGui.Button("It's really them") && inviterShown is { } shown) {
                // The keys shown when this warning first showed, and only those. Not a comparison: advanced mode still says "not compared".
                this._actions.Run("Confirming it's them", () => session.AcknowledgeKeyChange(invite.Inviter.UserId, shown, compared: false));
            }

            ImGui.EndDisabled();
            Widgets.Tooltip("Once they've told you over /tell that they set up LookingGlass again. Clears this warning.");
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
        if (Widgets.GhostButton("Decline")) {
            this._actions.Run("Declining", () => session.RespondToInviteAsync(invite.ChannelId, false));
        }

        ImGui.SameLine();
        if (Widgets.GhostButton("Block")) {
            this._actions.Run($"Blocking {invite.Inviter.Name}", () => session.BlockUser(invite.Inviter.UserId));
        }

        Widgets.Tooltip("Decline, silently decline their future invites, and hide their messages.", "Undo in Settings > Blocked users.");
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
            if (Widgets.GhostButton("Settings", "Server, chat and identity settings. Also behind the gear in the title bar.")) {
                this._toggleSettings();
            }

            return;
        }

        switch (snapshot.State) {
            case ConnectionState.Unregistered:
            case ConnectionState.Registering:
            case ConnectionState.LoginNotRecognized:
                this.DrawRegistration(snapshot, session, player);
                break;
            case ConnectionState.Ready when snapshot.Channels.IsEmpty:
                this.DrawNoChannels(snapshot, session);
                break;
            case ConnectionState.Ready:
                this.DrawChannels(snapshot, session);
                break;
            case ConnectionState.Reconnecting:
                Widgets.Centred(FontAwesomeIcon.Sync, "Reconnecting...", snapshot.StatusFor(this._config.AdvancedMode) ?? "The connection to the server dropped. Trying again.");
                break;
            case ConnectionState.Blocked: {
                // The operator's decision, said plainly (with their reason, if they gave one); trying again now is all there is to do here.
                Widgets.Centred(FontAwesomeIcon.Ban, "Blocked by this server", snapshot.StatusFor(this._config.AdvancedMode) ?? "This server's operator has blocked you from it.");
                var width = ImGui.GetContentRegionAvail().X;
                Widgets.CentreNext(Widgets.ButtonWidth("Try again now"), width);
                if (ImGui.Button("Try again now")) {
                    session.Reconnect();
                }

                break;
            }
            default:
                Widgets.Centred(FontAwesomeIcon.Sync, "Connecting...", snapshot.StatusFor(this._config.AdvancedMode) ?? "Waiting for the server.");
                break;
        }
    }

    private void DrawRegistration(SessionSnapshot snapshot, ClientSession session, PlayerInfo player) {
        var width = Math.Min(ImGui.GetContentRegionAvail().X, 520 * Widgets.Scale);
        var indent = Math.Max(0, (ImGui.GetContentRegionAvail().X - width) / 2);
        ImGui.Indent(indent);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
        ImGui.Spacing();

        var advanced = this._config.AdvancedMode;

        // The server doesn't list the address in use, so it would refuse the registration (and key login): say so before anything else.
        if (snapshot.AddressNotListedFor(advanced) is { } addressHint) {
            this.DrawAddressNotListed(addressHint);
        }

        // A login the server refused: first what may be wrong and what to try, then registering again as the last resort.
        var rejected = snapshot.State == ConnectionState.LoginNotRecognized || snapshot.LoginRejected;
        var challenge = snapshot.PendingChallenge;
        // Once a code is out, registering is what the player chose: the notice would only push the code and Verify out of
        // sight (below the window's bottom at its usual size, which testers took for no Verify button at all).
        if (rejected && challenge == null) {
            this.DrawLoginNotRecognised(session, advanced);
        }

        const string howItChecks = "LookingGlass checks that the character is yours with a short code you put in your Lodestone profile for a few minutes.";
        ImGui.TextUnformatted(rejected ? "Register again" : "Register this character");
        ImGui.TextColored(Widgets.Muted, !rejected ? howItChecks
            : advanced ? "Only needed if your identity key was lost or replaced, or this server has never known your account; it replaces your login but keeps the identity key the plugin has, so your channels keep working. " + howItChecks
            : "Only needed if your LookingGlass was reset or its files were lost, or this server has never known you; it replaces your login, and your channels keep working. " + howItChecks);
        if (!rejected && snapshot.NewIdentity) {
            // No keys this server knows (a new computer, a lost file, a reset): registering is how the account comes back.
            ImGui.Spacing();
            Widgets.IconText(FontAwesomeIcon.InfoCircle, "Been here before?", ImGuiColors.TankBlue);
            ImGui.TextUnformatted(advanced
                ? "If this character used LookingGlass on this server before (on another computer, or before its keys were lost or reset), " +
                  "registering brings back its channels, invites and ranks, admin included, with a new key. The members are told that you " +
                  "re-verified your character and have a new key."
                : "If this character used LookingGlass on this server before (on another computer, or before it was reset), registering " +
                  "brings back its channels, invites and ranks, admin included. The members are told that you set up LookingGlass again.");
        }

        ImGui.Spacing();
        ImGui.Spacing();

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
            if (Widgets.GhostIconButton("##copy-code", FontAwesomeIcon.Copy, "Copy the code")) {
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
            // A new code: scroll so the steps down to Verify are in sight, in a window too short to show them all.
            if (this._codeScrolledTo != challenge.Code) {
                this._codeScrolledTo = challenge.Code;
                ImGui.SetScrollHereY(1.0f);
            }
        }

        EndStep();
        ImGui.PopTextWrapPos();
        ImGui.Unindent(indent);
    }

    /// <summary>
    /// Above the registration steps when the server lists its addresses without the one in use: which address to use
    /// instead, and a button to the setting.
    /// </summary>
    private void DrawAddressNotListed(string hint) {
        Widgets.IconText(FontAwesomeIcon.ExclamationTriangle, "This server doesn't accept the address you use", Widgets.Warning);
        ImGui.TextUnformatted(hint);
        if (Widgets.GhostButton("Change the server address", "Opens Settings, where the server address is. Also behind the gear in the title bar.")) {
            this._openSettings();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Spacing();
    }

    /// <summary>
    /// Above the registration steps when the server refused the saved login and the identity key: what may be wrong, that
    /// the login is kept and tried again, and buttons to check the server address and to try again now.
    /// </summary>
    private void DrawLoginNotRecognised(ClientSession session, bool advanced) {
        Widgets.IconText(FontAwesomeIcon.ExclamationTriangle, "This server doesn't recognise your login", Widgets.Warning);
        if (advanced) {
            ImGui.TextUnformatted("It didn't accept your saved login or your identity key. If you changed the server address, check it in Settings.");
            ImGui.TextUnformatted("Register again (below) only if your identity key was lost or replaced, or this server has never known your account.");
        } else {
            ImGui.TextUnformatted("It didn't accept your saved login. If you changed the server address, check it in Settings.");
            ImGui.TextUnformatted("Register again (below) only if your LookingGlass was reset or its files were lost, or this server has never known you.");
        }

        ImGui.TextUnformatted(PlainMessages.LoginMaybeReplacedWording.For(advanced));
        ImGui.TextColored(Widgets.Muted, "Your login is kept and tried again every minute or so, so it works again by itself once the server knows it.");
        ImGui.TextColored(Widgets.Muted, $"Server: {this._config.ServerUrl}");
        ImGui.Spacing();
        ImGui.BeginDisabled(this._actions.Busy);
        if (ImGui.Button("Retry now")) {
            this._actions.Run("Trying your login again", () => session.RetryLoginAsync());
        }

        ImGui.EndDisabled();
        Widgets.Tooltip(advanced ? "Try your saved login on this server again now, then your identity key." : "Try signing in to this server again now.");
        ImGui.SameLine();
        if (Widgets.GhostButton("Open settings", "Check the server address. Also behind the gear in the title bar.")) {
            this._openSettings();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Spacing();
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
            this._config.AdvancedMode
                ? "A channel is an end-to-end encrypted group chat. Create one, then invite friends by character name and home world."
                : "A channel is a private group chat. Create one, then invite friends by character name and home world.");
        var width = ImGui.GetContentRegionAvail().X;
        Widgets.CentreNext(Widgets.ButtonWidth("Create a channel"), width);
        if (ImGui.Button("Create a channel")) {
            this._newChannelName = "";
            ImGui.OpenPopup("new-channel");
        }

        this.DrawNewChannelPopup(session);
        if (!snapshot.Invites.IsEmpty) {
            ImGui.Spacing();
            var hint = snapshot.Invites.Length == 1 ? "Or accept the invite waiting for you (the envelope at the top right)." : "Or accept one of the invites waiting for you (the envelope at the top right).";
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
        var splitter = SplitterWidth * scale;
        var maxSidebar = Math.Max(MinSidebarWidth, (available.X - splitter) / scale - MinDetailWidth);
        this._sidebarWidth = Math.Clamp(this._sidebarWidth, MinSidebarWidth, maxSidebar);

        this.DrawSidebar(snapshot, session, this._sidebarWidth * scale);

        // A handle between the panes to resize the channel list, drawn as a thin line.
        ImGui.SameLine(0, 0);
        ImGui.InvisibleButton("##splitter", new Vector2(splitter, available.Y));
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        if (hovered || active) {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
        }

        if (active) {
            this._sidebarWidth = Math.Clamp(this._sidebarWidth + ImGui.GetIO().MouseDelta.X / scale, MinSidebarWidth, maxSidebar);
        }

        var line = MathF.Floor((ImGui.GetItemRectMin().X + ImGui.GetItemRectMax().X) / 2);
        var lineColour = active ? ImGuiCol.SeparatorActive : hovered ? ImGuiCol.SeparatorHovered : ImGuiCol.Separator;
        ImGui.GetWindowDrawList().AddLine(new Vector2(line, ImGui.GetItemRectMin().Y), new Vector2(line, ImGui.GetItemRectMax().Y),
            ImGui.GetColorU32(lineColour), Math.Max(1, MathF.Round(scale)));

        // The channel gets the window's padding on its sides, but none on top, to line up with the list.
        ImGui.SameLine(0, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8 * scale, 0));
        var visible = ImGui.BeginChild("##channel", Vector2.Zero, false, ImGuiWindowFlags.AlwaysUseWindowPadding);
        ImGui.PopStyleVar();
        if (visible) {
            // Shown in an open (drawn) window: read, and what arrives meanwhile doesn't count.
            this._sessions.Unread.Viewing(selected.Id);
            this._pane.Draw(selected, session, snapshot);
        }

        ImGui.EndChild();
    }

    /// <summary>The channel list, without a border: a muted heading, the rows, and "New channel" at the bottom.</summary>
    private void DrawSidebar(SessionSnapshot snapshot, ClientSession session, float width) {
        if (ImGui.BeginChild("##sidebar", new Vector2(width, 0), false)) {
            ImGui.TextColored(Widgets.Muted, $"Channels · {snapshot.Channels.Length}");
            ImGuiHelpers.ScaledDummy(2);

            if (ImGui.BeginChild("##channel-list", new Vector2(0, -ImGui.GetFrameHeightWithSpacing()), false)) {
                // In command order, as you type them; channels without a number last, by name.
                foreach (var channel in snapshot.Channels
                             .OrderBy(channel => this._sessions.SlotOf(channel.Id) ?? int.MaxValue)
                             .ThenBy(channel => channel.DisplayName, StringComparer.CurrentCultureIgnoreCase)) {
                    this.DrawChannelRow(channel);
                }
            }

            ImGui.EndChild();

            ImGui.BeginDisabled(this._actions.Busy);
            if (Widgets.GhostRow("##new-channel", FontAwesomeIcon.Plus, "New channel", "Create a channel. You'll be its admin.")) {
                this._newChannelName = "";
                ImGui.OpenPopup("new-channel");
            }

            ImGui.EndDisabled();
            this.DrawNewChannelPopup(session);
        }

        ImGui.EndChild();
    }

    /// <summary>
    /// One channel: the whole row highlights on hover and when selected; its colour as a bar on the
    /// left; its number (muted), name, and nickname (muted, on the right); then an attention icon
    /// and the unread count, right-aligned.
    /// </summary>
    private void DrawChannelRow(ChannelView channel) {
        var scale = Widgets.Scale;
        var style = ImGui.GetStyle();
        var slot = this._sessions.SlotOf(channel.Id);
        var nickname = this._sessions.NicknameOf(channel.Id);
        var colour = this._sessions.ColourOf(channel.Id) is { } own ? ChannelPalette.ColourOf(own) : null;
        var unread = this._sessions.Unread.CountOf(channel.Id);
        var attention = ChannelAttention.Of(channel, this._config.AdvancedMode);
        var displayName = channel.DisplayNameFor(this._config.AdvancedMode);

        var height = MathF.Round(ImGui.GetFrameHeight() + 4 * scale);
        if (ImGui.Selectable($"##row-{channel.Id}", this._selectedChannel == channel.Id, ImGuiSelectableFlags.None, new Vector2(0, height))) {
            this._selectedChannel = channel.Id;
        }

        var hovered = ImGui.IsItemHovered();
        // Right-click: channel windows (pop-out chat), where what is typed only ever goes to that channel.
        if (!channel.OldKeyMembership && ImGui.BeginPopupContextItem($"##row-menu-{channel.Id}")) {
            ImGui.TextColored(Widgets.Muted, Widgets.Ellipsize(displayName, 240 * scale));
            ImGui.Separator();
            this._windows.DrawChannelMenuItems(channel.Id);
            ImGui.EndPopup();
        }

        // A selectable's rectangle reaches half the item spacing past the list's edges, where it's
        // clipped, so anything drawn at its very edge (the colour bar, the right-hand items) would be
        // cut off or touch the divider. Keep the drawing inside the list's visible area.
        var windowPos = ImGui.GetWindowPos();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        min.X = Math.Max(min.X, windowPos.X + ImGui.GetWindowContentRegionMin().X);
        max.X = Math.Min(max.X, windowPos.X + ImGui.GetWindowContentRegionMax().X);
        var drawList = ImGui.GetWindowDrawList();
        var middle = (min.Y + max.Y) / 2;
        var textY = MathF.Floor(middle - ImGui.GetTextLineHeight() / 2);
        var textColour = ImGui.GetColorU32(ImGuiCol.Text);
        var mutedColour = ImGui.GetColorU32(Widgets.Muted);

        // The channel's colour, as a bar along the left edge.
        if (colour is { } accent) {
            var inset = 4 * scale;
            drawList.AddRectFilled(new Vector2(min.X, min.Y + inset), new Vector2(min.X + 3 * scale, max.Y - inset), ImGui.GetColorU32(accent), 1.5f * scale);
        }

        // From the right: unread count, attention icon, nickname, clear of the divider.
        var x = max.X - style.FramePadding.X - 4 * scale;
        if (unread > 0) {
            x -= Widgets.Badge(new Vector2(x, middle), UnreadCounter.Format(unread), ImGuiColors.TankBlue) + 6 * scale;
        }

        if (attention.Level != AttentionLevel.None) {
            var icon = attention.Level == AttentionLevel.Warning ? FontAwesomeIcon.ExclamationTriangle : FontAwesomeIcon.HourglassHalf;
            var iconColour = attention.Level == AttentionLevel.Warning ? Widgets.Warning : Widgets.Muted;
            var iconWidth = Widgets.FixedIconWidth(icon);
            Widgets.DrawIcon(drawList, icon, new Vector2(x - iconWidth / 2, middle), ImGui.GetColorU32(iconColour));
            x -= iconWidth + 6 * scale;
        }

        // The number (right-aligned in its column), then the name, then the nickname if there's room.
        var left = min.X + 9 * scale;
        var number = slot is { } s ? s.ToString() : "-";
        var numberWidth = ImGui.CalcTextSize("50").X;
        drawList.AddText(new Vector2(MathF.Floor(left + numberWidth - ImGui.CalcTextSize(number).X), textY), mutedColour, number);
        left += numberWidth + 8 * scale;

        var room = x - left;
        var nameWidth = ImGui.CalcTextSize(displayName).X;
        if (nickname != null && room > 0) {
            var nicknameRoom = Math.Max(0, Math.Min(room * 0.45f, room - nameWidth - 8 * scale));
            var shownNickname = nicknameRoom > ImGui.CalcTextSize("...").X ? Widgets.Ellipsize(nickname, nicknameRoom) : null;
            if (shownNickname != null) {
                var nicknameWidth = ImGui.CalcTextSize(shownNickname).X;
                drawList.AddText(new Vector2(MathF.Floor(x - nicknameWidth), textY), mutedColour, shownNickname);
                room -= nicknameWidth + 8 * scale;
            }
        }

        var name = Widgets.Ellipsize(displayName, Math.Max(room, 0));
        drawList.AddText(new Vector2(MathF.Floor(left), textY), textColour, name);

        if (hovered) {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35);
            ImGui.TextUnformatted(displayName);
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
