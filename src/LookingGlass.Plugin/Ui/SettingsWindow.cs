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

    // The chat log: how much room every one on this computer takes (looked at in the background, now and then while the
    // window is open), and, just after it was turned off, the size to offer deleting.
    private Task<long>? _logSize;
    private long _knownLogSize;
    private DateTime _logSizeAt;
    private Task<long>? _offerDelete;
    private int? _logMegabytes;

    public SettingsWindow(Configuration config, SessionManager sessions, UiActions actions) : base("LookingGlass settings###lookingglass-settings") {
        this._config = config;
        this._sessions = sessions;
        this._actions = actions;
        this._modals = new Modals(actions, () => config.AdvancedMode);
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
        this._logSize = null;
    }

    public override void OnClose() {
        // Closing the window drops an address change still being checked, rather than applying it unseen later.
        this._moveCheck = null;
        this._moveOffer = null;
    }

    public override void Draw() {
        this.DrawServer();
        this.DrawChat();
        this.DrawChatLog();
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
        var advanced = this._config.AdvancedMode;
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 26);
        var close = false;
        if (check.Verdict == ServerMoveVerdict.SameServer) {
            ImGui.TextUnformatted("This is the same server. Keep your identity?");
            ImGui.Spacing();
            ImGui.TextColored(Widgets.Muted, check.Message);
            ImGui.TextColored(Widgets.Muted,
                $"Keeping it copies the identity of {who} registered there ({(advanced ? "keys, login and channels" : "login and channels")}) to the new address: no registering again, " +
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

            Widgets.Tooltip(advanced
                ? "Treat the new address as a different server: register there through the Lodestone, with new keys."
                : "Treat the new address as a different server: register there through the Lodestone, and start afresh.");
        } else {
            ImGui.TextUnformatted("LookingGlass can't confirm this is the same server");
            ImGui.Spacing();
            ImGui.TextColored(Widgets.Muted, check.Message);
            var remedy = check.Verdict == ServerMoveVerdict.NotSecure
                ? "If it is the same server, apply its wss:// address instead (ask whoever runs it for one, and to list it in LookingGlass:PublicUrls)."
                : "If it is the same server, ask whoever runs it to list both addresses in LookingGlass:PublicUrls, then apply it again.";
            ImGui.TextColored(Widgets.Muted,
                $"So the new address counts as a different server: there you'd register through the Lodestone{(advanced ? " with new keys" : "")}, and start " +
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
            // Closing the window drops the change (OnClose), and asking a server that's gone can take a while.
            ImGui.TextColored(Widgets.Muted, "Asking the current server and the new address whether they are the same server... Keep this window open.");
        } else if (this._moveError is { } error) {
            ImGui.TextColored(Widgets.Error, error);
        } else if (changed && this._moveOffer == null) {
            // Typed but not applied: closing the window forgets it (OnOpen starts from what is saved). A tester took a typed
            // address for a saved one and stayed on a server that was later turned off.
            Widgets.IconText(FontAwesomeIcon.ExclamationTriangle, "Not saved yet: press Apply (or Enter) to use this address.", Widgets.Warning);
        }

        // What the server said on connecting: it lists its addresses, and not this one.
        if (this._sessions.Session != null && this._sessions.Snapshot.AddressNotListedFor(this._config.AdvancedMode) is { } addressHint) {
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

        var verbose = this._config.VerboseChannelMessages;
        if (ImGui.Checkbox("Verbose channel messages", ref verbose)) {
            this._config.VerboseChannelMessages = verbose;
            this._config.Save();
        }

        ImGui.PushTextWrapPos();
        ImGui.TextColored(Widgets.Muted, "Say in chat when you start or stop talking in a channel (the server info bar and the chat box label always show it). " +
                                         "Stops you didn't choose, like a disconnect, are always said.");
        ImGui.PopTextWrapPos();
    }

    /// <summary>
    /// "Keep a chat log on this computer" (off by default), its size limit, how much room it takes, and "Delete my chat log",
    /// there whenever any log exists, on or off. Turning it off offers to delete what was kept.
    /// </summary>
    private void DrawChatLog() {
        var advanced = this._config.AdvancedMode;
        Widgets.Heading(advanced ? "Chat log" : "Chat history");

        // How much room they take: looked at again every few seconds while the window is open, never on this thread.
        if (this._logSize == null || (this._logSize.IsCompleted && DateTime.UtcNow - this._logSizeAt > TimeSpan.FromSeconds(3))) {
            this.MeasureLog();
        }

        var keep = this._config.KeepChatLog;
        if (ImGui.Checkbox(ChatLogWords.KeepIt.For(advanced) + "###keep-chat-log", ref keep)) {
            this._sessions.SetKeepChatLog(keep);
            if (!keep) {
                // Once closed, how much there is to offer deleting.
                this._offerDelete = this._sessions.ChatLogSize();
            }

            this.MeasureLog();
        }

        ImGui.PushTextWrapPos();
        ImGui.TextColored(Widgets.Muted, ChatLogWords.Explanation(ProtectedSecretStore.Protection).For(advanced));
        ImGui.PopTextWrapPos();

        if (keep) {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(ChatLogWords.SizeLimit.For(advanced));
            ImGui.SameLine();
            ImGui.SetNextItemWidth(Math.Max(120 * Widgets.Scale, ImGui.GetContentRegionAvail().X * 0.5f));
            // A slider (Ctrl+click to type a number); applied once let go, not at every step of a drag.
            var megabytes = this._logMegabytes ?? ChatLogLimits.ClampMegabytes(this._config.ChatLogMegabytes);
            ImGui.SliderInt("##chat-log-megabytes", ref megabytes, ChatLogLimits.MinMegabytes, ChatLogLimits.MaxMegabytes, "%d MB",
                ImGuiSliderFlags.Logarithmic | ImGuiSliderFlags.AlwaysClamp);
            this._logMegabytes = ImGui.IsItemActive() ? megabytes : null;
            if (ImGui.IsItemDeactivatedAfterEdit()) {
                this._sessions.SetChatLogMegabytes(megabytes);
                this.MeasureLog();
            }

            Widgets.Tooltip(ChatLogWords.SizeLimitTooltip.For(advanced));
        }

        if (this._sessions.ChatLog is { State: ChatLogState.Unreadable } unreadable) {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(Widgets.Warning, ChatLogWords.Unreadable(unreadable.Problem).For(advanced));
            ImGui.PopTextWrapPos();
        }

        // The last size known, so nothing flickers while it is looked at again.
        if (this._logSize is { IsCompletedSuccessfully: true } measured) {
            this._knownLogSize = measured.Result;
        }

        var size = this._knownLogSize;
        if (size > 0) {
            ImGui.TextColored(Widgets.Muted, ChatLogWords.Uses(size).For(advanced));
            ImGui.BeginDisabled(this._actions.Busy);
            if (ImGui.Button(ChatLogWords.Delete.For(advanced) + "...###delete-chat-log")) {
                this.ConfirmDelete(ChatLogWords.DeleteConfirm(size).For(advanced), advanced);
            }

            ImGui.EndDisabled();
        }

        // Just turned off: offer to delete what was kept, if anything was.
        if (this._offerDelete is { IsCompleted: true } offer) {
            this._offerDelete = null;
            if (offer.IsCompletedSuccessfully && offer.Result > 0 && !this._config.KeepChatLog) {
                this.ConfirmDelete(ChatLogWords.TurnedOff(offer.Result).For(advanced), advanced);
            }
        }
    }

    private void ConfirmDelete(string text, bool advanced) {
        var title = ChatLogWords.Delete.For(advanced);
        this._modals.Confirm(title, text, title, () => {
            var deleting = this._sessions.DeleteChatLogs();
            this._actions.Run(advanced ? "Deleting your chat log" : "Deleting your chat history", () => deleting);
            this._logSize = deleting.ContinueWith(_ => this._sessions.ChatLogSize(), TaskScheduler.Default).Unwrap();
            this._logSizeAt = DateTime.UtcNow;
        });
    }

    private void MeasureLog() {
        this._logSize = this._sessions.ChatLogSize();
        this._logSizeAt = DateTime.UtcNow;
    }

    private void DrawIdentity() {
        Widgets.Heading("Your identity");

        var advanced = this._config.AdvancedMode;
        if (ImGui.Checkbox("Advanced mode: show encryption details (fingerprints, keys)", ref advanced)) {
            this._config.AdvancedMode = advanced;
            this._config.Save();
        }

        ImGui.PushTextWrapPos();
        ImGui.TextColored(Widgets.Muted, "For checking that your chats are private: compare fingerprints with people over /tell. " +
                                         "Warnings show either way; this only adds the technical details.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        if (advanced) {
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
        }

        var player = this._sessions.Player;
        ImGui.BeginDisabled(this._actions.Busy || player == null);
        if (ImGui.Button("Reset my identity...") && player != null) {
            var serverUrl = this._config.ServerUrl;
            var loggedIn = this._sessions.Snapshot.State == ConnectionState.Ready;
            this._modals.Confirm("Reset my identity", ResetText(player.Name, serverUrl, loggedIn, advanced), "Reset my identity", () => {
                // On the framework thread (the dialog's button); the returned task finishes the reset.
                var reset = this._sessions.ResetIdentity();
                this._actions.Run("Resetting your identity", () => reset);
                // The backup, if any, no longer holds the old identity afterwards: look again once done.
                this._backupFor = (player.ContentId, serverUrl);
                this._backupCheck = reset.ContinueWith(_ => ProtectedSecretStore.FindBackup(player.ContentId, serverUrl), TaskScheduler.Default);
            });
        }

        ImGui.EndDisabled();
        Widgets.Tooltip(advanced
            ? "New identity keys for this character on this server, for a key that was lost or may have been stolen. " +
              "Your channels come along when you register them."
            : "Set up LookingGlass afresh for this character on this server, if its files were lost or someone may have copied them. " +
              "Your channels come along when you register again.");
        this.DrawBackup(player, advanced);
    }

    /// <summary>
    /// A backup of the identity (an old-style secrets file kept when it was moved) for an address that has none of its
    /// own: offered, never restored without asking (see ServerSecretFiles). Looked for in the background, again whenever
    /// the window opens, the character or address changes, or the identity is reset or restored.
    /// </summary>
    private void DrawBackup(PlayerInfo? player, bool advanced) {
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
            this._modals.Confirm("Restore your identity", RestoreText(player.Name, serverUrl, saved, advanced), "Restore it", () => {
                var restore = this._sessions.RestoreBackup();
                this._actions.Run("Restoring your identity", () => restore);
                this._backupFor = (player.ContentId, serverUrl);
                this._backupCheck = restore.ContinueWith(_ => ProtectedSecretStore.FindBackup(player.ContentId, serverUrl), TaskScheduler.Default);
            });
        }

        ImGui.EndDisabled();
        Widgets.Tooltip(advanced
            ? "Your identity for this server is gone (no keys, no login), but LookingGlass kept a copy when it moved your keys to a new file."
            : "Your LookingGlass setup for this server is gone, but LookingGlass kept a copy when it moved it to a new file.");
    }

    private static string RestoreText(string name, string serverUrl, string saved, bool advanced) => advanced
        ? $"This restores the identity LookingGlass kept for {name} on {serverUrl}, as it was on {saved}: its keys, login and channel keys.\n\n" +
          "It may be older than you think:\n" +
          "- Its login may no longer work. If the server still knows its key, it signs you in with that.\n" +
          "- Channel keys and changes since then are missing; channels you're still in catch up from the server.\n" +
          "- If you have reset your identity on this server since, its key no longer counts there: you'd have to reset again.\n\n" +
          "The backup file itself is kept."
        : $"This restores the LookingGlass setup kept for {name} on {serverUrl}, as it was on {saved}.\n\n" +
          "It may be older than you think:\n" +
          "- Its login may no longer work. If the server still knows it, it signs you in anyway.\n" +
          "- Changes since then are missing; channels you're still in catch up from the server.\n" +
          "- If you have reset your identity on this server since, it no longer counts there: you'd have to reset again.\n\n" +
          "The backup file itself is kept.";

    /// <param name="loggedIn">Connected and logged in now, so the server can be told to retire the old key straight away.</param>
    /// <param name="advanced">In advanced mode's words; otherwise simple mode's, with nothing about keys.</param>
    private static string ResetText(string name, string serverUrl, bool loggedIn, bool advanced) {
        var text = new StringBuilder();
        if (!advanced) {
            text.Append($"This sets up LookingGlass afresh for {name} on {serverUrl}. Only do this if its files were lost, or someone may ")
                .Append("have copied them. If you just can't sign in, you don't need it: registering again is enough.\n\n");

            text.Append(loggedIn
                ? "First, LookingGlass tells the server to stop accepting your old setup: from then on, nobody can sign in with it there.\n\n"
                : "You're not logged in, so the server can't be told to stop accepting your old setup now: it keeps working there until you register again.\n\n");

            text.Append("After a reset:\n")
                .Append("- You register again through the Lodestone. Until then you can't sign in there.\n")
                .Append("- Registering brings your channels, ranks (admin too) and invites along. Each channel works again ")
                .Append("once another member who is online lets you back in.\n")
                .Append("- The members of your channels are told that you set up LookingGlass again.\n")
                .Append("- Copies of the old setup kept for this server's other addresses, and in backups, are removed too.\n\n")
                .Append("Your identity on other servers isn't affected.");
            return text.ToString();
        }

        text.Append($"This makes new identity keys for {name} on {serverUrl}. Only do this if your key was lost or may have been stolen. ")
            .Append("If you just can't sign in, you don't need it: registering again keeps your key.\n\n");

        text.Append(loggedIn
            ? "First, LookingGlass tells the server to retire your old key: from then on it can't sign in or be registered again there, and every login made with it stops working.\n\n"
            : "You're not logged in, so the server can't be told to retire your old key now: it and its logins keep working there until you register the new one.\n\n");

        text.Append("After a reset:\n")
            .Append("- You register again through the Lodestone. Until then you can't sign in there.\n")
            .Append("- Registering brings your channels, ranks (admin too) and invites along to your new key. Each channel works again ")
            .Append("once a member who is online shares its new key with you.\n")
            .Append("- The members of your channels are told that you re-verified your character and have a new key.\n")
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
