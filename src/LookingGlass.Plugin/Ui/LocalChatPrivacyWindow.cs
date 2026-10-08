using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using LookingGlass.Core.Client;

namespace LookingGlass.Plugin.Ui;

/// <summary>
/// What local chat tells the server (see <see cref="LocalChatWords.PrivacyNotice"/>), to accept or not: opened by the first
/// /lgl (which sends nothing, and doesn't start talking in local chat), and from Settings. Accepting is saved for every
/// character; Settings can withdraw it, which ends talking in local chat (see <see cref="StickyMode"/>).
/// </summary>
public sealed class LocalChatPrivacyWindow : Window {
    private readonly Configuration _config;
    private readonly ChatOutput _chat;
    private readonly Func<bool> _talkingInLocal;
    // What opened it: once accepted, game chat says what to type again (see LocalChatWords.PrivacyAcceptedFor).
    private LocalPrivacyAsked _asked = LocalPrivacyAsked.FromSettings;

    /// <param name="talkingInLocal">Talking in local chat now: then, once accepted, it never says to type /lgl.</param>
    public LocalChatPrivacyWindow(Configuration config, ChatOutput chat, Func<bool> talkingInLocal) : base($"{LocalChatWords.PrivacyTitle}###lookingglass-local-privacy") {
        this._config = config;
        this._chat = chat;
        this._talkingInLocal = talkingInLocal;
        this.Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse;
    }

    /// <summary>Opens it in front: from /lgl with a message or alone, or from Settings (<paramref name="why"/>).</summary>
    public void Ask(LocalPrivacyAsked why) {
        this._asked = why;
        this.IsOpen = true;
        this.BringToFront();
    }

    public override void Draw() {
        var advanced = this._config.AdvancedMode;
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30);
        foreach (var paragraph in LocalChatWords.PrivacyNotice.For(advanced).Split("\n\n")) {
            ImGui.TextUnformatted(paragraph);
            ImGui.Spacing();
        }

        ImGui.PopTextWrapPos();
        ImGui.Spacing();
        if (this._config.LocalChatPrivacyAccepted) {
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30);
            ImGui.TextColored(Widgets.Muted, "You've accepted this. Withdraw it to stop sending local messages (and talking in local chat) until you accept again " +
                                             "(also in Settings, under Chat).");
            ImGui.PopTextWrapPos();
            if (ImGui.Button("Withdraw")) {
                this._config.LocalChatPrivacyAccepted = false;
                this._config.Save();
                this.IsOpen = false;
            }

            ImGui.SameLine();
            if (ImGui.Button("Close")) {
                this.IsOpen = false;
            }

            return;
        }

        if (ImGui.Button("Accept and use local chat")) {
            this._config.LocalChatPrivacyAccepted = true;
            this._config.Save();
            this.IsOpen = false;
            if (LocalChatWords.PrivacyAcceptedFor(this._asked, this._talkingInLocal()) is { } said) {
                this._chat.Notice(NoticeTone.Info, said.For(advanced));
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Not now")) {
            this.IsOpen = false;
        }

        ImGui.TextColored(Widgets.Muted, "Until you accept, /lgl sends nothing and asks nobody about your friends.");
    }

    public override void OnClose() => this._asked = LocalPrivacyAsked.FromSettings;
}
