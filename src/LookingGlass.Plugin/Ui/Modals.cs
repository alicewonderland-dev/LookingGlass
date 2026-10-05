using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using LookingGlass.Core.Client;

namespace LookingGlass.Plugin.Ui;

/// <summary>
/// The main window's modal dialogs: confirming destructive actions, and checking a channel member (comparing
/// fingerprints in advanced mode; in simple mode, what to do about a warning). Asked for from anywhere in the window
/// (menus, rows); opened and drawn at the window's root, where their IDs don't depend on which child or row asked.
/// </summary>
/// <param name="advancedMode">Whether advanced mode is on (see <see cref="Configuration.AdvancedMode"/>), asked every frame.</param>
internal sealed class Modals(UiActions actions, Func<bool> advancedMode) {
    private const string ConfirmId = "###lg-confirm";
    private const string CheckId = "###lg-compare";

    private Confirmation? _confirm;
    private bool _openConfirm;
    // The member being checked, and the fingerprint shown when the dialog opened: what "Mark verified" or "It's really them" vouches
    // for, and only that, so a key that changes again while it is open is refused rather than marked unseen.
    private (string ChannelId, long UserId, string? Shown)? _check;
    private bool _openCheck;

    /// <summary>Asks before running <paramref name="action"/>.</summary>
    public void Confirm(string title, string text, string button, Action action) {
        this._confirm = new Confirmation(title, text, button, action);
        this._openConfirm = true;
    }

    /// <summary>
    /// In advanced mode, shows your fingerprint next to a channel member's, to compare and mark verified. In simple mode,
    /// what a warning about them means and what to do, with a button to clear it once they've confirmed it was them.
    /// </summary>
    public void CheckMember(string channelId, MemberView member) {
        this._check = (channelId, member.User.UserId, ShownFingerprintOf(member));
        this._openCheck = true;
    }

    /// <summary>
    /// The fingerprint to show for a member, and to mark verified: "Mark verified" vouches for the fingerprint shown, and
    /// only that. For someone who registered again, the warning is about their new key, so that is the one shown and
    /// marked; the key the log binds them to can't be compared any more (they no longer have it).
    /// </summary>
    private static string? ShownFingerprintOf(MemberView member) => member.KeyReplaced ? member.NewFingerprint : member.Fingerprint;

    public void Draw(SessionSnapshot snapshot, ClientSession? session) {
        this.DrawConfirm();
        this.DrawCheck(snapshot, session);
    }

    // ================================================================ simple mode's words about a member

    /// <summary>In simple mode, a member there's something to check with: a warning, or a hint that they set up LookingGlass again.</summary>
    public static bool HasSomethingToCheck(MemberView member) => member.KeyChanged || member.KeyReplaced || member.KeyRecovered;

    /// <summary>Someone's keys changed without explanation, in simple mode's words (as <see cref="PlainMessages.KeyChanged"/> starts).</summary>
    public static string ChangedText(string name) => $"{name} set up LookingGlass again (new computer or reset), or someone else may be using their name.";

    /// <summary>Someone registered again with keys that aren't a member here, in simple mode's words.</summary>
    public static string ReplacedText(string name) =>
        $"{name} set up LookingGlass again, and their new setup isn't a member of this channel: they can't read or send here until a " +
        "moderator removes them and invites them again. If you didn't expect that, check with them over /tell.";

    /// <summary>Someone re-verified their character with new keys (a key recovered entry), in simple mode's words.</summary>
    public static string RecoveredText(string name) =>
        $"{name} set up LookingGlass again (new computer or reset). If you didn't expect that, check with them over /tell.";

    // ================================================================ dialogs

    private void DrawConfirm() {
        if (this._confirm is not { } confirm) {
            return;
        }

        var id = confirm.Title + ConfirmId;
        if (this._openConfirm) {
            this._openConfirm = false;
            ImGui.OpenPopup(id);
        }

        var open = true;
        if (!ImGui.BeginPopupModal(id, ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings)) {
            if (!open || !ImGui.IsPopupOpen(id)) {
                this._confirm = null;
            }

            return;
        }

        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 24);
        ImGui.TextUnformatted(confirm.Text);
        ImGui.PopTextWrapPos();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.BeginDisabled(actions.Busy);
        ImGui.PushStyleColor(ImGuiCol.Button, Widgets.Error with { W = 0.6f });
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Widgets.Error with { W = 0.8f });
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Widgets.Error);
        if (ImGui.Button(confirm.Button)) {
            confirm.Action();
            this._confirm = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.PopStyleColor(3);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel") || ImGui.IsKeyPressed(ImGuiKey.Escape)) {
            this._confirm = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawCheck(SessionSnapshot snapshot, ClientSession? session) {
        if (this._check is not { } check) {
            return;
        }

        // The title follows the mode; the ID after ### doesn't, so switching modes leaves the dialog open.
        var advanced = advancedMode();
        var id = (advanced ? "Compare fingerprints" : "Check it's really them") + CheckId;
        if (this._openCheck) {
            this._openCheck = false;
            ImGui.OpenPopup(id);
        }

        var open = true;
        if (!ImGui.BeginPopupModal(id, ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings)) {
            if (!open || !ImGui.IsPopupOpen(id)) {
                this._check = null;
            }

            return;
        }

        var member = snapshot.FindChannel(check.ChannelId)?.Members.FirstOrDefault(m => m.User.UserId == check.UserId);
        var wrap = ImGui.GetFontSize() * 26;
        ImGui.PushTextWrapPos(wrap);
        if (member == null || session == null) {
            ImGui.TextUnformatted("They're no longer in this channel.");
        } else if (advanced) {
            DrawComparison(member, check.Shown, snapshot, session, actions);
        } else {
            DrawPlainCheck(member, check.Shown, session, actions);
        }

        ImGui.PopTextWrapPos();
        ImGui.Spacing();
        if (ImGui.Button("Close") || ImGui.IsKeyPressed(ImGuiKey.Escape)) {
            this._check = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private static void DrawComparison(MemberView member, string? theirs, SessionSnapshot snapshot, ClientSession session, UiActions actions) {
        var name = $"{member.User.Name}@{member.User.WorldName}";
        ImGui.TextUnformatted($"Compare fingerprints with {name}");
        ImGui.Spacing();
        ImGui.TextColored(Widgets.Muted, "Compare these over /tell or in person. If theirs matches what they see as their own fingerprint, mark them verified.");
        ImGui.Spacing();

        ImGui.TextColored(Widgets.Muted, "Your fingerprint");
        Fingerprint(snapshot.MyFingerprint, "##copy-mine");
        ImGui.Spacing();

        ImGui.TextColored(Widgets.Muted, member.KeyReplaced ? $"{member.User.Name}'s fingerprint (the new key they registered with)" : $"{member.User.Name}'s fingerprint");
        Fingerprint(theirs, null);
        ImGui.Spacing();

        var needsVerifying = member.KeyReplaced ? member.KeyChanged : member.KeyChanged || !member.FingerprintCompared;
        if (member.KeyReplaced) {
            Widgets.IconText(FontAwesomeIcon.ExclamationTriangle, "Registered again", Widgets.Warning);
            ImGui.TextColored(Widgets.Muted,
                "Their new key isn't a member of this channel. Even once it's verified, a moderator must remove them and invite them again to let it in.");
        } else if (member.KeyChanged) {
            Widgets.IconText(FontAwesomeIcon.ExclamationTriangle, "Key changed", Widgets.Warning);
            ImGui.TextColored(Widgets.Muted, "Their identity key changed, or this name now belongs to a different account.");
        } else if (member.KeyRecovered) {
            Widgets.IconText(FontAwesomeIcon.Redo, "New key");
            ImGui.TextColored(Widgets.Muted,
                "They re-verified their character through the Lodestone and have a new key. That it's them is the server's word: compare to be sure.");
        }

        if (!needsVerifying) {
            Widgets.IconText(FontAwesomeIcon.CheckCircle, "You marked this fingerprint verified.", Widgets.Success);
            return;
        }

        ImGui.Spacing();
        ImGui.BeginDisabled(actions.Busy || theirs == null);
        if (ImGui.Button(member.KeyReplaced ? "Mark new key verified" : "Mark verified") && theirs is { } shown) {
            // The fingerprint shown above, and only that.
            actions.Run("Marking verified", () => session.AcknowledgeKeyChange(member.User.UserId, shown));
        }

        ImGui.EndDisabled();
    }

    /// <summary>
    /// Simple mode's check: what the warning or hint about them means and what to do, without fingerprints. "It's really
    /// them" clears it as "Mark verified" does, for the keys advanced mode would show (the ones held for them now).
    /// </summary>
    private static void DrawPlainCheck(MemberView member, string? theirs, ClientSession session, UiActions actions) {
        ImGui.TextUnformatted($"{member.User.Name}@{member.User.WorldName}");
        ImGui.Spacing();

        if (member.KeyReplaced) {
            Widgets.IconText(FontAwesomeIcon.ExclamationTriangle, "Set up LookingGlass again", Widgets.Warning);
            ImGui.TextColored(Widgets.Muted, ReplacedText(member.User.Name));
        } else if (member.KeyChanged) {
            Widgets.IconText(FontAwesomeIcon.ExclamationTriangle, "Set up again, or someone else?", Widgets.Warning);
            ImGui.TextColored(Widgets.Muted, ChangedText(member.User.Name) + " If you didn't expect that, check with them over /tell before trusting them.");
        } else if (member.KeyRecovered) {
            Widgets.IconText(FontAwesomeIcon.Redo, "Set up LookingGlass again");
            ImGui.TextColored(Widgets.Muted, RecoveredText(member.User.Name));
        } else {
            ImGui.TextColored(Widgets.Muted, "Nothing to check: there are no warnings about them.");
            return;
        }

        // When "Mark verified" would clear something: someone who registered again stays flagged until they're invited again.
        if (!member.KeyChanged && (member.KeyReplaced || !member.KeyRecovered)) {
            return;
        }

        ImGui.Spacing();
        ImGui.BeginDisabled(actions.Busy || theirs == null);
        if (ImGui.Button("It's really them") && theirs is { } shown) {
            // The keys held for them now, and only those.
            actions.Run("Confirming it's them", () => session.AcknowledgeKeyChange(member.User.UserId, shown, compared: false));
        }

        ImGui.EndDisabled();
        Widgets.Tooltip("Once they've told you over /tell that it was them. Clears this.");
    }

    /// <summary>A fingerprint in the monospaced font, with a Copy button when given an ID for it.</summary>
    public static void Fingerprint(string? fingerprint, string? copyId) {
        if (fingerprint == null) {
            ImGui.TextColored(Widgets.Muted, "(not known yet)");
            return;
        }

        ImGui.AlignTextToFramePadding();
        using (Services.PluginInterface.UiBuilder.MonoFontHandle.Push()) {
            ImGui.TextUnformatted(fingerprint);
        }

        if (copyId != null) {
            ImGui.SameLine();
            if (Widgets.GhostIconButton(copyId, FontAwesomeIcon.Copy, "Copy")) {
                ImGui.SetClipboardText(fingerprint);
            }
        }
    }

    private sealed record Confirmation(string Title, string Text, string Button, Action Action);
}
