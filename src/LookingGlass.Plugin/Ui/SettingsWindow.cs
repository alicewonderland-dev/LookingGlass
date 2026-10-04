using System.Numerics;
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
    private string _serverUrl;

    public SettingsWindow(Configuration config, SessionManager sessions, UiActions actions) : base("LookingGlass settings###lookingglass-settings") {
        this._config = config;
        this._sessions = sessions;
        this._actions = actions;
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
    }

    public override void Draw() {
        this.DrawServer();
        this.DrawChat();
        this.DrawIdentity();
        this.DrawBlockedUsers();
        ImGui.Spacing();
        this._actions.DrawStatus();
    }

    private void DrawServer() {
        Widgets.Heading("Server");

        ImGui.TextUnformatted("Server URL");
        var apply = Widgets.ButtonWidth("Apply");
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - apply - ImGui.GetStyle().ItemSpacing.X);
        var enter = ImGui.InputTextWithHint("##server-url", "ws://host:5180/ws", ref this._serverUrl, 256, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        var changed = this._serverUrl.Trim() != this._config.ServerUrl;
        ImGui.BeginDisabled(!changed);
        if ((ImGui.Button("Apply") || enter) && changed) {
            this._config.ServerUrl = this._serverUrl.Trim();
            this._config.Save();
            this._sessions.Restart();
        }

        ImGui.EndDisabled();
        Widgets.Tooltip("Saves the URL and reconnects.");
        ImGui.TextColored(Widgets.Muted, "For example ws://my-vm:5180/ws over Tailscale, or wss://chat.example.com/ws.");
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
            if (ImGui.Button("Unblock")) {
                this._actions.Run($"Unblocking {user.Name}", () => session.UnblockUser(user.UserId));
            }

            ImGui.EndDisabled();
            ImGui.PopID();
        }
    }
}
