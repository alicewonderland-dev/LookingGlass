using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using LookingGlass.Core.Client;

namespace LookingGlass.Plugin.Ui;

/// <summary>
/// What local chat tells the server (see <see cref="LocalChatWords.PrivacyNotice"/>), to accept or not: opened by the first
/// /lgl (which sends nothing), and from Settings. Accepting is saved for every character; Settings can withdraw it.
/// </summary>
public sealed class LocalChatPrivacyWindow : Window {
    private readonly Configuration _config;
    private readonly ChatOutput _chat;
    // Opened by /lgl: say in game chat, once accepted, to send the message again (it wasn't kept).
    private bool _fromCommand;

    public LocalChatPrivacyWindow(Configuration config, ChatOutput chat) : base($"{LocalChatWords.PrivacyTitle}###lookingglass-local-privacy") {
        this._config = config;
        this._chat = chat;
        this.Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse;
    }

    /// <summary>Opens it in front, from /lgl (<paramref name="fromCommand"/>) or from Settings.</summary>
    public void Ask(bool fromCommand) {
        this._fromCommand = fromCommand;
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
            ImGui.TextColored(Widgets.Muted, "You've accepted this. Withdraw it to stop sending local messages until you accept again " +
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
            if (this._fromCommand) {
                this._chat.Notice(NoticeTone.Info, LocalChatWords.PrivacyAccepted.For(advanced));
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Not now")) {
            this.IsOpen = false;
        }

        ImGui.TextColored(Widgets.Muted, "Until you accept, /lgl sends nothing and asks nobody about your friends.");
    }

    public override void OnClose() => this._fromCommand = false;
}
