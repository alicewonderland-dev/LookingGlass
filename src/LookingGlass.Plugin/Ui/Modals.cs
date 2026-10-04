using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using LookingGlass.Core.Client;

namespace LookingGlass.Plugin.Ui;

/// <summary>
/// The main window's modal dialogs: confirming destructive actions, and comparing fingerprints.
/// Asked for from anywhere in the window (menus, rows); opened and drawn at the window's root,
/// where their IDs don't depend on which child or row asked.
/// </summary>
internal sealed class Modals(UiActions actions) {
    private const string ConfirmId = "###lg-confirm";
    private const string CompareId = "Compare fingerprints###lg-compare";

    private Confirmation? _confirm;
    private bool _openConfirm;
    private (string ChannelId, long UserId)? _compare;
    private bool _openCompare;

    /// <summary>Asks before running <paramref name="action"/>.</summary>
    public void Confirm(string title, string text, string button, Action action) {
        this._confirm = new Confirmation(title, text, button, action);
        this._openConfirm = true;
    }

    /// <summary>Shows your fingerprint next to a channel member's, to compare and mark verified.</summary>
    public void CompareFingerprints(string channelId, long userId) {
        this._compare = (channelId, userId);
        this._openCompare = true;
    }

    public void Draw(SessionSnapshot snapshot, ClientSession? session) {
        this.DrawConfirm();
        this.DrawCompare(snapshot, session);
    }

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

    private void DrawCompare(SessionSnapshot snapshot, ClientSession? session) {
        if (this._compare is not { } compare) {
            return;
        }

        if (this._openCompare) {
            this._openCompare = false;
            ImGui.OpenPopup(CompareId);
        }

        var open = true;
        if (!ImGui.BeginPopupModal(CompareId, ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings)) {
            if (!open || !ImGui.IsPopupOpen(CompareId)) {
                this._compare = null;
            }

            return;
        }

        var member = snapshot.FindChannel(compare.ChannelId)?.Members.FirstOrDefault(m => m.User.UserId == compare.UserId);
        var wrap = ImGui.GetFontSize() * 26;
        ImGui.PushTextWrapPos(wrap);
        if (member == null || session == null) {
            ImGui.TextUnformatted("They're no longer in this channel.");
        } else {
            DrawComparison(member, snapshot, session, actions);
        }

        ImGui.PopTextWrapPos();
        ImGui.Spacing();
        if (ImGui.Button("Close") || ImGui.IsKeyPressed(ImGuiKey.Escape)) {
            this._compare = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private static void DrawComparison(MemberView member, SessionSnapshot snapshot, ClientSession session, UiActions actions) {
        var name = $"{member.User.Name}@{member.User.WorldName}";

        // "Mark verified" vouches for the fingerprint shown, and only that. For someone who registered again, the
        // warning is about their new key, so that is the one shown and marked; the key the log binds them to can't
        // be compared any more (they no longer have it).
        var theirs = member.KeyReplaced ? member.NewFingerprint : member.Fingerprint;

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
            if (Widgets.IconButton(copyId, FontAwesomeIcon.Copy, "Copy")) {
                ImGui.SetClipboardText(fingerprint);
            }
        }
    }

    private sealed record Confirmation(string Title, string Text, string Button, Action Action);
}
