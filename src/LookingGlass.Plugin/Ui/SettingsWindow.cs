using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using LookingGlass.Core.Client;

namespace LookingGlass.Plugin.Ui;

/// <summary>Server, chat, identity and blocked users. Opened from the main window's gear, or Dalamud's plugin settings button.</summary>
public sealed class SettingsWindow : Window {
    private static readonly XivChatType[] ChatTypes = [XivChatType.Debug, XivChatType.Echo, XivChatType.Notice, XivChatType.SystemMessage];

    private readonly Configuration _config;
    private readonly SessionManager _sessions;
    private readonly UiActions _actions;
    private readonly Modals _modals;
    private string _serverUrl;

    // Changing the server address: the check with the current server (in the background), then what to offer.
    private Task<MoveOffer>? _moveCheck;
    private MoveOffer? _moveOffer;
    private bool _openMoveOffer;
    private string? _moveError;

    // A backup of the identity the user may restore, looked for in the background, and for whom.
    private Task<SecretsBackup?>? _backupCheck;
    private (ulong ContentId, string ServerUrl)? _backupFor;

    // The user said the server is gone for good (or won't accept their login): reset without leaving their channels first.
    private bool _resetWithoutLeaving;

    public SettingsWindow(Configuration config, SessionManager sessions, UiActions actions) : base("LookingGlass settings###lookingglass-settings") {
        this._config = config;
        this._sessions = sessions;
        this._actions = actions;
        this._modals = new Modals(actions);
        this._serverUrl = config.ServerUrl;
        this.Size = new Vector2(440, 520);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints {
            MinimumSize = new Vector2(380, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void OnOpen() {
        // Start from what is saved, not a half-typed URL from last time.
        this._serverUrl = this._config.ServerUrl;
        this._moveError = null;
        // Look again: files may have changed since.
        this._backupCheck = null;
    }

    public override void OnClose() {
        // Closing the window drops an address change still being checked, rather than applying it unseen later.
        this._moveCheck = null;
        this._moveOffer = null;
    }

    public override void Draw() {
        this.DrawServer();
        this.DrawChat();
        this.DrawIdentity();
        this.DrawBlockedUsers();
        ImGui.Spacing();
        this._actions.DrawStatus();
        this._modals.Draw(this._sessions.Snapshot, this._sessions.Session);
        this.DrawMoveOffer();
    }

    /// <summary>
    /// The server address is changing. If some character has an identity for the current address and none for the new
    /// one, ask the current server (over the current address) whether the new one is its own, and the new one whether it
    /// agrees (see ServerMove), before anything changes.
    /// </summary>
    /// <param name="confirmed">
    /// The user already chose to keep their identity: this asks the servers again, since the dialog may have been open
    /// for a while, and the identity is only carried over if they still agree (otherwise the dialog says what changed).
    /// </param>
    private void StartMoveCheck(string oldUrl, string newUrl, bool confirmed = false) {
        this._moveError = null;
        this._moveOffer = null;
        this._moveCheck = Task.Run(async () => {
            var characters = ProtectedSecretStore.CharactersToMove(oldUrl, newUrl);
            if (characters.Count == 0) {
                return new MoveOffer(oldUrl, newUrl, null, characters, confirmed);
            }

            return new MoveOffer(oldUrl, newUrl, await ServerMove.CheckAsync(oldUrl, newUrl), characters, confirmed);
        });
    }

    /// <summary>Once the check is done (framework thread): change the address straight away, or ask the user first.</summary>
    private void FinishMoveCheck() {
        if (this._moveCheck is not { IsCompleted: true } done) {
            return;
        }

        this._moveCheck = null;
        if (!done.IsCompletedSuccessfully) {
            this._moveError = $"Couldn't check the new address: {done.Exception?.InnerException?.Message}";
            return;
        }

        var offer = done.Result;
        if (offer.OldUrl != this._config.ServerUrl) {
            // The address changed meanwhile; this check is about another move.
            return;
        }

        if (offer.Check == null) {
            // No identity there to keep (or the new address has its own): as before, a plain change.
            this.ChangeServer(offer, keepIdentity: false);
            return;
        }

        if (offer.Confirmed && offer.Check.Verdict == ServerMoveVerdict.SameServer) {
            // Asked again after the user chose to keep their identity, and the servers still agree.
            this.ChangeServer(offer, keepIdentity: true);
            return;
        }

        this._moveOffer = offer;
        this._openMoveOffer = true;
    }

    private void ChangeServer(MoveOffer offer, bool keepIdentity) {
        var work = this._sessions.ChangeServer(offer.NewUrl, keepIdentity ? offer.Check : null, offer.Characters);
        this._actions.Run(keepIdentity ? "Keeping your identity at the new address" : "Changing the server address", () => work);
    }

    private void DrawMoveOffer() {
        if (this._moveOffer is not { Check: { } check } offer) {
            return;
        }

        const string id = "Server address###lg-move";
        if (this._openMoveOffer) {
            this._openMoveOffer = false;
            ImGui.OpenPopup(id);
        }

        var open = true;
        if (!ImGui.BeginPopupModal(id, ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings)) {
            if (!open || !ImGui.IsPopupOpen(id)) {
                this._moveOffer = null;
            }

            return;
        }

        var who = offer.Characters.Count == 1 ? "your character" : $"your {offer.Characters.Count} characters";
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 26);
        var close = false;
        if (check.Verdict == ServerMoveVerdict.SameServer) {
            ImGui.TextUnformatted("This is the same server. Keep your identity?");
            ImGui.Spacing();
            ImGui.TextColored(Widgets.Muted, check.Message);
            ImGui.TextColored(Widgets.Muted,
                $"Keeping it copies the identity of {who} registered there (keys, login and channels) to the new address: no registering again, " +
                "and you stay in your channels. The identity for the old address is kept too, so switching back works.");
            ImGui.Spacing();
            if (ImGui.Button("Keep my identity")) {
                // Never on what the servers said when the dialog opened: ask them again, and keep it only if they still agree.
                this.StartMoveCheck(offer.OldUrl, offer.NewUrl, confirmed: true);
                close = true;
            }

            ImGui.SameLine();
            if (ImGui.Button("Start afresh there")) {
                this.ChangeServer(offer, keepIdentity: false);
                close = true;
            }

            Widgets.Tooltip("Treat the new address as a different server: register there through the Lodestone, with new keys.");
        } else {
            ImGui.TextUnformatted("LookingGlass can't confirm this is the same server");
            ImGui.Spacing();
            ImGui.TextColored(Widgets.Muted, check.Message);
            var remedy = check.Verdict == ServerMoveVerdict.NotSecure
                ? "If it is the same server, apply its wss:// address instead (ask whoever runs it for one, and to list it in LookingGlass:PublicUrls)."
                : "If it is the same server, ask whoever runs it to list both addresses in LookingGlass:PublicUrls, then apply it again.";
            ImGui.TextColored(Widgets.Muted,
                "So the new address counts as a different server: there you'd register through the Lodestone with new keys, and start " +
                $"without channels. The identity of {who} for {offer.OldUrl} is kept, so switching back restores it. {remedy}");
            ImGui.Spacing();
            if (ImGui.Button("Use the new address anyway")) {
                this.ChangeServer(offer, keepIdentity: false);
                close = true;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel") || ImGui.IsKeyPressed(ImGuiKey.Escape)) {
            close = true;
        }

        ImGui.PopTextWrapPos();
        if (close) {
            this._moveOffer = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    /// <param name="Check">The current server's answer, or null if no character has an identity to carry over.</param>
    /// <param name="Confirmed">Asked again after the user chose to keep their identity (see <see cref="StartMoveCheck"/>).</param>
    private sealed record MoveOffer(string OldUrl, string NewUrl, ServerMoveCheck? Check, IReadOnlyList<ulong> Characters, bool Confirmed);

    private void DrawServer() {
        Widgets.Heading("Server");

        ImGui.TextUnformatted("Server URL");
        var apply = Widgets.ButtonWidth("Apply");
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - apply - ImGui.GetStyle().ItemSpacing.X);
        var enter = ImGui.InputTextWithHint("##server-url", "ws://host:5180/ws", ref this._serverUrl, 256, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        var changed = this._serverUrl.Trim() != this._config.ServerUrl;
        var checking = this._moveCheck is { IsCompleted: false };
        ImGui.BeginDisabled(!changed || checking || this._actions.Busy);
        if ((ImGui.Button("Apply") || enter) && changed && !checking && !this._actions.Busy) {
            this.StartMoveCheck(this._config.ServerUrl, this._serverUrl.Trim());
        }

        ImGui.EndDisabled();
        Widgets.Tooltip("Saves the URL and reconnects. If you have an identity on the current server, it first asks that server, and the new address, whether they are the same server, to keep your identity.");
        ImGui.PushTextWrapPos();
        if (checking) {
            ImGui.TextColored(Widgets.Muted, "Asking the current server and the new address whether they are the same server...");
        } else if (this._moveError is { } error) {
            ImGui.TextColored(Widgets.Error, error);
        }

        // What the server said on connecting: it lists its addresses, and not this one.
        if (this._sessions.Session != null && this._sessions.Snapshot.AddressNotListed is { } addressHint) {
            Widgets.IconText(FontAwesomeIcon.ExclamationTriangle, "The server doesn't accept this address", Widgets.Warning);
            ImGui.TextColored(Widgets.Warning, addressHint);
        }

        ImGui.TextColored(Widgets.Muted, "For example ws://my-vm:5180/ws over Tailscale, or wss://chat.example.com/ws.");
        ImGui.PopTextWrapPos();
        this.FinishMoveCheck();
        ImGui.Spacing();

        var autoConnect = this._config.AutoConnect;
        if (ImGui.Checkbox("Connect automatically when logging in", ref autoConnect)) {
            this._config.AutoConnect = autoConnect;
            this._config.Save();
        }

        var session = this._sessions.Session;
        if (this._sessions.Player != null) {
            ImGui.BeginDisabled(this._actions.Busy);
            if (session == null) {
                if (ImGui.Button("Connect now")) {
                    this._sessions.Connect();
                }
            } else if (ImGui.Button("Disconnect")) {
                this._sessions.Disconnect();
            }

            ImGui.EndDisabled();
        }
    }

    private void DrawChat() {
        Widgets.Heading("Chat");

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Show messages in the chat channel");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Math.Max(120 * Widgets.Scale, ImGui.GetContentRegionAvail().X));
        if (ImGui.BeginCombo("##chat-type", this._config.ChatType.ToString())) {
            foreach (var type in ChatTypes) {
                if (ImGui.Selectable(type.ToString(), type == this._config.ChatType)) {
                    this._config.ChatType = type;
                    this._config.Save();
                }
            }

            ImGui.EndCombo();
        }

        Widgets.Tooltip("Which of the game's chat channels LookingGlass messages appear in, so your chat tabs can show or hide them.");

        var wholeLine = this._config.ColourWholeLine;
        if (ImGui.Checkbox("Colour the whole line in a channel's colour", ref wholeLine)) {
            this._config.ColourWholeLine = wholeLine;
            this._config.Save();
        }

        Widgets.Tooltip("Off: only the tag ([LGC1] or [nickname]) takes the channel's colour. Channels without a colour of their own always colour only the tag.");

        var nicknameTags = this._config.NicknameTags;
        if (ImGui.Checkbox("Show nicknames in chat tags", ref nicknameTags)) {
            this._config.NicknameTags = nicknameTags;
            this._config.Save();
        }

        Widgets.Tooltip("On: a channel with a nickname is tagged [nickname] in chat, as in [sky]. Off: always by its number, as in [LGC1].");
    }

    private void DrawIdentity() {
        Widgets.Heading("Your identity");

        ImGui.TextUnformatted("Your fingerprint");
        var fingerprint = this._sessions.Snapshot.MyFingerprint;
        if (fingerprint == null) {
            ImGui.TextColored(Widgets.Muted, "Not known until you're connected and registered.");
        } else {
            Modals.Fingerprint(fingerprint, "##copy-fingerprint");
            ImGui.PushTextWrapPos();
            ImGui.TextColored(Widgets.Muted, "Others compare this with what LookingGlass shows them for you, to be sure it's really you.");
            ImGui.PopTextWrapPos();
        }

        ImGui.Spacing();
        ImGui.PushTextWrapPos();
        ImGui.TextColored(Widgets.Muted, $"Keys are stored with: {ProtectedSecretStore.Protection}");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        var player = this._sessions.Player;
        // Worked out again every frame, so it follows the channels as the user deals with them.
        var plan = this._sessions.ResetPlan();
        if (!plan.CanOverride) {
            this._resetWithoutLeaving = false;
        }

        var allowed = plan.Readiness == ResetReadiness.Ready || (plan.CanOverride && this._resetWithoutLeaving);
        ImGui.BeginDisabled(this._actions.Busy || player == null || !allowed);
        if (ImGui.Button("Reset my identity...") && player != null) {
            var serverUrl = this._config.ServerUrl;
            var withoutLeaving = plan.Readiness != ResetReadiness.Ready;
            this._modals.Confirm("Reset my identity", ResetText(player.Name, serverUrl, plan), "Reset my identity", () => {
                // On the framework thread (the dialog's button); the returned task finishes the reset.
                var reset = this._sessions.ResetIdentity(withoutLeaving);
                this._actions.Run("Resetting your identity", () => reset);
                // The backup, if any, no longer holds the old identity afterwards: look again once done.
                this._backupFor = (player.ContentId, serverUrl);
                this._backupCheck = reset.ContinueWith(_ => ProtectedSecretStore.FindBackup(player.ContentId, serverUrl), TaskScheduler.Default);
            });
        }

        ImGui.EndDisabled();
        Widgets.Tooltip("New identity keys for this character on this server. Only if your key was lost or may have been stolen.",
            allowed ? null : "Unavailable for now: see below.");
        DrawResetBlock(plan, ref this._resetWithoutLeaving);
        this.DrawBackup(player);
    }

    /// <summary>Why "Reset my identity" is unavailable, if it is, and (offline) the explicit way past it.</summary>
    private static void DrawResetBlock(IdentityResetPlan plan, ref bool withoutLeaving) {
        if (plan.Readiness == ResetReadiness.Ready) {
            return;
        }

        ImGui.Spacing();
        ImGui.PushTextWrapPos();
        ImGui.TextColored(plan.Readiness == ResetReadiness.Loading ? Widgets.Muted : Widgets.Warning, plan.Explanation);
        foreach (var channel in plan.AdminOf) {
            ImGui.BulletText(channel.DisplayName);
        }

        ImGui.PopTextWrapPos();
        if (plan.CanOverride) {
            ImGui.Checkbox(plan.Readiness == ResetReadiness.Offline
                    ? "I can't connect to this server any more: reset without leaving my channels"
                    : "The server doesn't accept my login: reset without leaving my channels",
                ref withoutLeaving);
            Widgets.Tooltip("Your channels aren't checked or left: any you're the admin of are left without one for good, and the others " +
                            "stay in your list under your old key, to remove with \"Remove from my list\".");
        }
    }

    /// <summary>
    /// A backup of the identity (an old-style secrets file kept when it was moved) for an address that has none of its
    /// own: offered, never restored without asking (see ServerSecretFiles). Looked for in the background, again whenever
    /// the window opens, the character or address changes, or the identity is reset or restored.
    /// </summary>
    private void DrawBackup(PlayerInfo? player) {
        if (player == null) {
            return;
        }

        var serverUrl = this._config.ServerUrl;
        if (this._backupCheck == null || this._backupFor != (player.ContentId, serverUrl)) {
            this._backupFor = (player.ContentId, serverUrl);
            this._backupCheck = Task.Run(() => ProtectedSecretStore.FindBackup(player.ContentId, serverUrl));
        }

        if (this._backupCheck is not { IsCompletedSuccessfully: true, Result: { } backup }) {
            return;
        }

        var saved = backup.SavedAt.ToLocalTime().ToString("g");
        ImGui.Spacing();
        ImGui.PushTextWrapPos();
        ImGui.TextColored(Widgets.Warning, $"A backup of your identity from {saved} exists. Restore it?");
        ImGui.PopTextWrapPos();
        ImGui.BeginDisabled(this._actions.Busy);
        if (ImGui.Button("Restore the backup...")) {
            this._modals.Confirm("Restore your identity", RestoreText(player.Name, serverUrl, saved), "Restore it", () => {
                var restore = this._sessions.RestoreBackup();
                this._actions.Run("Restoring your identity", () => restore);
                this._backupFor = (player.ContentId, serverUrl);
                this._backupCheck = restore.ContinueWith(_ => ProtectedSecretStore.FindBackup(player.ContentId, serverUrl), TaskScheduler.Default);
            });
        }

        ImGui.EndDisabled();
        Widgets.Tooltip("Your identity for this server is gone (no keys, no login), but LookingGlass kept a copy when it moved your keys to a new file.");
    }

    private static string RestoreText(string name, string serverUrl, string saved) =>
        $"This restores the identity LookingGlass kept for {name} on {serverUrl}, as it was on {saved}: its keys, login and channel keys.\n\n" +
        "It may be older than you think:\n" +
        "- Its login may no longer work. If the server still knows its key, it signs you in with that.\n" +
        "- Channel keys and changes since then are missing; channels you're still in catch up from the server.\n" +
        "- If you have reset your identity on this server since, its key no longer counts there: you'd have to reset again.\n\n" +
        "The backup file itself is kept.";

    private static string ResetText(string name, string serverUrl, IdentityResetPlan plan) {
        var text = new StringBuilder();
        text.Append($"This makes new identity keys for {name} on {serverUrl}. Only do this if your key was lost or may have been stolen. ")
            .Append("If you just can't sign in, you don't need it: registering again keeps your key.\n\n");

        if (plan.Readiness == ResetReadiness.Ready) {
            var leave = plan.ToLeave.ToList();
            text.Append("First, LookingGlass leaves your channels with your old key, while it still exists, so the others see you go");
            text.Append(leave.Count == 0 ? " (you're in none right now)" : $": {string.Join(", ", leave.Select(channel => $"\"{channel.DisplayName}\""))}");
            text.Append(plan.Declines.IsEmpty ? ".\n" : $", and declines your {plan.Declines.Length} invite{(plan.Declines.Length == 1 ? "" : "s")}.\n");
            var kept = plan.Kept.Select(step => $"\"{step.Channel.DisplayName}\"").ToList();
            if (kept.Count > 0) {
                text.Append($"These can't be left (your current key isn't a member), so they stay in your list: {string.Join(", ", kept)}. ")
                    .Append("Remove them afterwards with \"Remove from my list\" in their menu.\n");
            }

            text.Append("If leaving one fails, you're told, and it stays in your list to remove the same way.\n\n")
                .Append("Then LookingGlass tells the server to retire your old key: from then on it can't sign in or be registered again there, ")
                .Append("and every login made with it stops working.\n\n");
        } else {
            text.Append(plan.Explanation)
                .Append("\n\nYou chose to reset anyway: your channels aren't left, and the server isn't told to retire your old key, so it and ")
                .Append("its logins keep working there until you register again. Channels you were in stay in your list, under your old key: ")
                .Append("remove them with \"Remove from my list\".\n\n");
        }

        text.Append("After a reset:\n")
            .Append("- You register again through the Lodestone.\n")
            .Append("- To get back into a channel, someone must invite your new key.\n")
            .Append("- Everyone who knows you sees a \"key changed\" warning for you.\n")
            .Append("- Copies of the old identity kept for this server's other addresses, and in backups, are removed too.\n\n")
            .Append("Your identity on other servers isn't affected.");
        return text.ToString();
    }

    private void DrawBlockedUsers() {
        Widgets.Heading("Blocked users");

        if (this._sessions.Session is not { } session) {
            ImGui.TextColored(Widgets.Muted, "Connect to see who you've blocked.");
            return;
        }

        var blocked = this._sessions.Snapshot.BlockedUsers;
        if (blocked.IsEmpty) {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(Widgets.Muted, "Nobody. Block someone from an invite, or from a member's menu in a channel.");
            ImGui.PopTextWrapPos();
            return;
        }

        foreach (var user in blocked) {
            ImGui.PushID(user.UserId.ToString());
            var unblock = Widgets.ButtonWidth("Unblock");
            ImGui.AlignTextToFramePadding();
            Widgets.Icon(FontAwesomeIcon.Ban, Widgets.Muted);
            ImGui.SameLine();
            var name = string.IsNullOrEmpty(user.WorldName) ? user.Name : $"{user.Name}@{user.WorldName}";
            Widgets.TextEllipsis(name, ImGui.GetContentRegionAvail().X - unblock - ImGui.GetStyle().ItemSpacing.X);
            ImGui.SameLine(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - unblock);
            ImGui.BeginDisabled(this._actions.Busy);
            if (Widgets.GhostButton("Unblock", "Show their messages and invites again.")) {
                this._actions.Run($"Unblocking {user.Name}", () => session.UnblockUser(user.UserId));
            }

            ImGui.EndDisabled();
            ImGui.PopID();
        }
    }
}
