using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface.Windowing;
using WonderlandChat.Core.Client;
using WonderlandChat.Protocol;

namespace WonderlandChat.Plugin.Ui;

/// <summary>Registration, channels, invites and settings. Reads only immutable session snapshots.</summary>
public sealed class MainWindow : Window {
    private static readonly XivChatType[] ChatTypes = [XivChatType.Debug, XivChatType.Echo, XivChatType.Notice, XivChatType.SystemMessage];
    private static readonly Vector4 KeyChangedColour = new(1f, 0.7f, 0.2f, 1f);

    private readonly Configuration _config;
    private readonly SessionManager _sessions;
    private readonly UiActions _actions = new();

    private string _serverUrl;
    private string _newChannelName = "";
    private string _inviteName = "";
    private string _inviteWorld = "";
    private string _renameTo = "";
    private string? _selectedChannel;

    public MainWindow(Configuration config, SessionManager sessions) : base("WonderlandChat###wonderlandchat-main") {
        this._config = config;
        this._sessions = sessions;
        this._serverUrl = config.ServerUrl;
        this.SizeConstraints = new WindowSizeConstraints {
            MinimumSize = new Vector2(460, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw() {
        var snapshot = this._sessions.Snapshot;
        var session = this._sessions.Session;

        this.DrawStatus(snapshot, session);
        ImGui.Separator();

        if (session == null) {
            ImGui.TextWrapped(this._sessions.Player == null
                ? "Log in to a character to use WonderlandChat."
                : "Not connected. Check the server URL below, then press Connect.");
        } else {
            switch (snapshot.State) {
                case ConnectionState.Unregistered:
                case ConnectionState.Registering:
                    this.DrawRegistration(snapshot, session);
                    break;
                case ConnectionState.Ready:
                    this.DrawInvites(snapshot, session);
                    this.DrawChannels(snapshot, session);
                    break;
                default:
                    ImGui.TextDisabled("Waiting for the server...");
                    break;
            }
        }

        ImGui.Spacing();
        this._actions.DrawStatus();
        ImGui.Spacing();
        this.DrawSettings();
    }

    private void DrawStatus(SessionSnapshot snapshot, ClientSession? session) {
        var player = this._sessions.Player;
        ImGui.TextUnformatted(player == null ? "Not logged in" : $"{player.Name} @ {player.HomeWorldName}");
        ImGui.SameLine();
        ImGui.TextDisabled($"| {snapshot.State}{(snapshot.StatusText is { } status ? $": {status}" : "")}");

        if (session == null && player != null) {
            ImGui.SameLine();
            if (ImGui.Button("Connect")) {
                this._sessions.Connect();
            }
        }
    }

    private void DrawRegistration(SessionSnapshot snapshot, ClientSession session) {
        var player = this._sessions.Player;
        if (player == null) {
            return;
        }

        ImGui.TextWrapped("Register this character to use WonderlandChat. You prove it's yours by putting a short code in your Lodestone profile for a few minutes.");
        ImGui.Spacing();

        if (snapshot.PendingChallenge is not { } challenge) {
            ImGui.BeginDisabled(this._actions.Busy);
            if (ImGui.Button($"Register {player.Name} @ {player.HomeWorldName}")) {
                this._actions.Run("Starting registration", () => session.StartRegistrationAsync(new Character {
                    Name = player.Name,
                    WorldId = player.HomeWorldId,
                    WorldName = player.HomeWorldName,
                }));
            }

            ImGui.EndDisabled();
            return;
        }

        ImGui.TextUnformatted("1. Copy this code:");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.6f, 0.9f, 1f, 1f), challenge.Code);
        ImGui.SameLine();
        if (ImGui.Button("Copy")) {
            ImGui.SetClipboardText(challenge.Code);
        }

        ImGui.TextUnformatted("2. Paste it anywhere in your Lodestone character profile and save.");
        ImGui.TextUnformatted("3. Press Verify. (The Lodestone can take a minute to show changes.)");
        ImGui.TextDisabled($"The code expires at {DateTimeOffset.FromUnixTimeSeconds(challenge.ExpiresUnix).ToLocalTime():t}. You can delete it from your profile afterwards.");

        ImGui.BeginDisabled(this._actions.Busy);
        if (ImGui.Button("Verify")) {
            this._actions.Run("Verifying", () => session.CompleteRegistrationAsync());
        }

        ImGui.SameLine();
        if (ImGui.Button("Get a new code")) {
            this._actions.Run("Starting registration", () => session.StartRegistrationAsync(new Character {
                Name = player.Name,
                WorldId = player.HomeWorldId,
                WorldName = player.HomeWorldName,
            }));
        }

        ImGui.EndDisabled();
    }

    private void DrawInvites(SessionSnapshot snapshot, ClientSession session) {
        if (snapshot.Invites.IsEmpty) {
            return;
        }

        ImGui.TextUnformatted("Invites");
        foreach (var invite in snapshot.Invites) {
            ImGui.PushID(invite.ChannelId);
            var name = invite.ChannelName ?? "(couldn't verify)";
            ImGui.TextUnformatted($"\"{name}\" from {invite.Inviter.Name}@{invite.Inviter.WorldName}");

            // An inviter whose key changed may not be who they were: make the user check first.
            if (invite.Verified && invite.InviterKeyChanged) {
                ImGui.SameLine();
                ImGui.TextColored(KeyChangedColour, "key changed!");
                if (ImGui.IsItemHovered()) {
                    ImGui.SetTooltip($"Their identity key changed, or this name now belongs to a different account.\nCompare fingerprints with them over /tell before accepting: {invite.InviterFingerprint}");
                }

                ImGui.SameLine();
                if (ImGui.SmallButton("Mark verified")) {
                    session.AcknowledgeKeyChange(invite.Inviter.UserId);
                }
            }

            ImGui.SameLine();
            ImGui.BeginDisabled(this._actions.Busy || !invite.Verified || invite.InviterKeyChanged);
            if (ImGui.Button("Accept")) {
                this._actions.Run($"Joining {name}", () => session.RespondToInviteAsync(invite.ChannelId, true));
            }

            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(this._actions.Busy);
            if (ImGui.Button("Decline")) {
                this._actions.Run("Declining", () => session.RespondToInviteAsync(invite.ChannelId, false));
            }

            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Block")) {
                session.BlockUser(invite.Inviter.UserId);
            }

            if (ImGui.IsItemHovered()) {
                ImGui.SetTooltip("Decline, and silently decline their future invites and hide their messages.\nUndo under Settings > Blocked users.");
            }

            ImGui.PopID();
        }

        ImGui.Separator();
    }

    private void DrawChannels(SessionSnapshot snapshot, ClientSession session) {
        ImGui.TextDisabled($"Your fingerprint: {snapshot.MyFingerprint}");

        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##new-channel", "New channel name", ref this._newChannelName, 64);
        ImGui.SameLine();
        ImGui.BeginDisabled(this._actions.Busy || string.IsNullOrWhiteSpace(this._newChannelName));
        if (ImGui.Button("Create channel")) {
            var name = this._newChannelName;
            this._newChannelName = "";
            this._actions.Run($"Creating {name}", () => session.CreateChannelAsync(name));
        }

        ImGui.EndDisabled();

        if (snapshot.Channels.IsEmpty) {
            ImGui.TextDisabled("No channels yet. Create one, or accept an invite.");
            return;
        }

        if (ImGui.BeginTable("channels", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH)) {
            ImGui.TableSetupColumn("Command", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Channel");
            ImGui.TableSetupColumn("Members", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Key", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableHeadersRow();

            foreach (var channel in snapshot.Channels) {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var slot = this._sessions.SlotOf(channel.Id);
                ImGui.TextUnformatted(slot is { } s ? $"/wcl{s}" : "-");
                ImGui.TableNextColumn();
                if (ImGui.Selectable($"{channel.DisplayName}##{channel.Id}", this._selectedChannel == channel.Id)) {
                    this._selectedChannel = channel.Id;
                    this._renameTo = channel.Name ?? "";
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(channel.Members.Count(m => m.Rank >= Rank.Member).ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(channel.RekeyPending ? "rekey pending" : channel.HasKey ? $"epoch {channel.Epoch}" : "waiting for key");
            }

            ImGui.EndTable();
        }

        if (this._selectedChannel != null && snapshot.FindChannel(this._selectedChannel) is { } selected) {
            ImGui.Separator();
            this.DrawChannelDetails(selected, session, snapshot);
        }
    }

    private void DrawChannelDetails(ChannelView channel, ClientSession session, SessionSnapshot snapshot) {
        ImGui.PushID(channel.Id);
        ImGui.TextUnformatted($"{channel.DisplayName}  (you are {ClientSession.RankName(channel.MyRank)})");

        // Command slot.
        var slot = this._sessions.SlotOf(channel.Id) ?? 0;
        ImGui.SetNextItemWidth(90);
        if (ImGui.BeginCombo("Command", slot == 0 ? "none" : $"/wcl{slot}")) {
            for (var i = 1; i <= Configuration.SlotCount; i++) {
                if (ImGui.Selectable($"/wcl{i}", i == slot)) {
                    this._sessions.AssignSlot(channel.Id, i);
                }
            }

            ImGui.EndCombo();
        }

        // Members.
        if (ImGui.BeginTable("members", 4, ImGuiTableFlags.RowBg)) {
            ImGui.TableSetupColumn("Member");
            ImGui.TableSetupColumn("Rank", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Fingerprint", ImGuiTableColumnFlags.WidthFixed, 200);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableHeadersRow();

            foreach (var member in channel.Members) {
                var isMe = member.User.UserId == snapshot.Me?.UserId;
                ImGui.PushID(member.User.UserId.ToString());
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{member.User.Name}@{member.User.WorldName}{(isMe ? " (you)" : "")}");
                if (member.KeyChanged) {
                    ImGui.SameLine();
                    ImGui.TextColored(KeyChangedColour, "key changed!");
                    if (ImGui.IsItemHovered()) {
                        ImGui.SetTooltip("Their identity key changed, or this name now belongs to a different account.\nCompare fingerprints with them over /tell, then mark it verified.");
                    }

                    ImGui.SameLine();
                    if (ImGui.SmallButton("Mark verified")) {
                        session.AcknowledgeKeyChange(member.User.UserId);
                    }
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(ClientSession.RankName(member.Rank));
                ImGui.TableNextColumn();
                ImGui.TextDisabled(member.Fingerprint ?? "-");
                ImGui.TableNextColumn();

                if (!isMe && !this._actions.Busy) {
                    if (channel.MyRank == Rank.Admin && member.Rank is Rank.Member) {
                        if (ImGui.SmallButton("Promote")) {
                            this._actions.Run("Promoting", () => session.SetRankAsync(channel.Id, member.User.UserId, Rank.Moderator));
                        }

                        ImGui.SameLine();
                    } else if (channel.MyRank == Rank.Admin && member.Rank is Rank.Moderator) {
                        if (ImGui.SmallButton("Demote")) {
                            this._actions.Run("Demoting", () => session.SetRankAsync(channel.Id, member.User.UserId, Rank.Member));
                        }

                        ImGui.SameLine();
                    }

                    if (channel.MyRank >= Rank.Moderator && member.Rank < channel.MyRank) {
                        if (ImGui.SmallButton(member.Rank == Rank.Invited ? "Cancel" : "Kick")) {
                            this._actions.Run("Removing", () => session.KickAsync(channel.Id, member.User.UserId));
                        }
                    }
                }

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        // Invite.
        if (channel.MyRank >= Rank.Moderator) {
            if (string.IsNullOrEmpty(this._inviteWorld)) {
                this._inviteWorld = this._sessions.Player?.HomeWorldName ?? "";
            }

            ImGui.SetNextItemWidth(180);
            ImGui.InputTextWithHint("##invite-name", "Character name", ref this._inviteName, 32);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            ImGui.InputTextWithHint("##invite-world", "Home world", ref this._inviteWorld, 32);
            ImGui.SameLine();
            ImGui.BeginDisabled(this._actions.Busy || string.IsNullOrWhiteSpace(this._inviteName) || string.IsNullOrWhiteSpace(this._inviteWorld));
            if (ImGui.Button("Invite")) {
                var name = this._inviteName;
                var world = this._inviteWorld;
                this._inviteName = "";
                this._actions.Run($"Inviting {name}", () => session.InviteAsync(channel.Id, name, world));
            }

            ImGui.EndDisabled();
            if (snapshot.DebugAccountsEnabled) {
                ImGui.TextDisabled($"Test server: invite \"Echo Bot\" on world \"{ProtocolInfo.DebugWorldName}\" for a partner that answers.");
            }
        }

        // Admin tools.
        if (channel.MyRank == Rank.Admin) {
            ImGui.SetNextItemWidth(220);
            ImGui.InputText("##rename", ref this._renameTo, 64);
            ImGui.SameLine();
            ImGui.BeginDisabled(this._actions.Busy || string.IsNullOrWhiteSpace(this._renameTo) || this._renameTo == channel.Name);
            if (ImGui.Button("Rename")) {
                var name = this._renameTo;
                this._actions.Run("Renaming", () => session.RenameAsync(channel.Id, name));
            }

            ImGui.EndDisabled();
        }

        ImGui.BeginDisabled(this._actions.Busy);
        if (UiActions.ConfirmButton("Leave")) {
            this._actions.Run("Leaving", () => session.LeaveAsync(channel.Id));
            this._selectedChannel = null;
        }

        if (channel.MyRank == Rank.Admin) {
            ImGui.SameLine();
            if (UiActions.ConfirmButton("Disband")) {
                this._actions.Run("Disbanding", () => session.DisbandAsync(channel.Id));
                this._selectedChannel = null;
            }
        }

        ImGui.EndDisabled();
        ImGui.PopID();
    }

    private void DrawSettings() {
        if (!ImGui.CollapsingHeader("Settings")) {
            return;
        }

        ImGui.SetNextItemWidth(320);
        ImGui.InputText("Server URL", ref this._serverUrl, 256);
        ImGui.SameLine();
        ImGui.BeginDisabled(this._serverUrl == this._config.ServerUrl);
        if (ImGui.Button("Apply")) {
            this._config.ServerUrl = this._serverUrl.Trim();
            this._config.Save();
            this._sessions.Restart();
        }

        ImGui.EndDisabled();
        ImGui.TextDisabled("For example ws://my-vm:5180/ws over Tailscale, or wss://chat.example.com/ws.");

        var autoConnect = this._config.AutoConnect;
        if (ImGui.Checkbox("Connect automatically when logging in", ref autoConnect)) {
            this._config.AutoConnect = autoConnect;
            this._config.Save();
        }

        ImGui.SetNextItemWidth(160);
        if (ImGui.BeginCombo("Chat channel for messages", this._config.ChatType.ToString())) {
            foreach (var type in ChatTypes) {
                if (ImGui.Selectable(type.ToString(), type == this._config.ChatType)) {
                    this._config.ChatType = type;
                    this._config.Save();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.TextDisabled($"Keys are stored with: {ProtectedSecretStore.Protection}");
        this.DrawBlockedUsers();
    }

    private void DrawBlockedUsers() {
        if (this._sessions.Session is not { } session) {
            return;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Blocked users");
        var blocked = this._sessions.Snapshot.BlockedUsers;
        if (blocked.IsEmpty) {
            ImGui.TextDisabled("Nobody. Use Block next to an invite.");
            return;
        }

        foreach (var user in blocked) {
            ImGui.PushID(user.UserId.ToString());
            ImGui.TextUnformatted(string.IsNullOrEmpty(user.WorldName) ? user.Name : $"{user.Name}@{user.WorldName}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Unblock")) {
                session.UnblockUser(user.UserId);
            }

            ImGui.PopID();
        }
    }
}
