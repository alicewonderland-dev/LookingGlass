using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using LookingGlass.Core.Client;
using LookingGlass.Core.Util;

namespace LookingGlass.Plugin.Ui;

/// <summary>
/// /lgdebug: connection details, protocol trace, recent notices, and tools to
/// test without a second person (simulated messages, forced rekeys, pings).
/// </summary>
public sealed class DebugWindow : Window {
    private readonly SessionManager _sessions;
    private readonly ChatOutput _chat;
    private readonly UiActions _actions = new();
    private string _testMessage = "test message";
    private string _testColours = "";
    private string? _channelId;

    public DebugWindow(SessionManager sessions, ChatOutput chat) : base("LookingGlass debug###lookingglass-debug") {
        this._sessions = sessions;
        this._chat = chat;
        this.SizeConstraints = new WindowSizeConstraints {
            MinimumSize = new Vector2(420, 300),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw() {
        var session = this._sessions.Session;
        var snapshot = this._sessions.Snapshot;

        ImGui.TextUnformatted($"State: {snapshot.State}");
        ImGui.TextDisabled(snapshot.StatusText ?? "");
        ImGui.TextUnformatted($"Server: {session?.ServerUri.ToString() ?? "-"}");
        ImGui.TextUnformatted($"Me: {(snapshot.Me is { } me ? $"{me.Name}@{me.WorldName} (id {me.UserId})" : "-")}");
        ImGui.TextUnformatted($"Fingerprint: {snapshot.MyFingerprint ?? "-"}");
        ImGui.TextUnformatted($"Debug accounts on server: {(snapshot.DebugAccountsEnabled ? "yes" : "no")}");
        if (snapshot.Limits is { } limits) {
            ImGui.TextDisabled($"Limits: message {limits.MaxMessageBytes} B, frame {limits.MaxFrameBytes} B, {limits.MessagesPerSecond}/s burst {limits.MessageBurst}, {limits.MaxMembersPerChannel} members, {limits.MaxChannelsPerUser} channels");
            ImGui.TextDisabled(limits.MessageKeepDays > 0
                ? $"Message catch-up: the server keeps messages {limits.MessageKeepDays} days, at most {limits.MaxStoredMessagesPerChannel} per channel"
                : "Message catch-up: the server keeps no messages (or is older)");
        }

        ImGui.Separator();
        ImGui.BeginDisabled(session == null || this._actions.Busy);
        if (ImGui.Button("Ping") && session != null) {
            this._actions.Run("Ping", async () => {
                var rtt = await session.PingAsync();
                Services.Log.Information($"LookingGlass ping: {rtt.TotalMilliseconds:0} ms");
                throw new PingResult(rtt);
            });
        }

        ImGui.SameLine();
        if (ImGui.Button("Reconnect")) {
            session?.Reconnect();
        }

        ImGui.SameLine();
        if (ImGui.Button("Refresh") && session != null) {
            this._actions.Run("Refreshing", () => session.RefreshAsync());
        }

        ImGui.SameLine();
        if (UiActions.ConfirmButton("Forget account") && session != null) {
            this._actions.Run("Forgetting the account", session.ForgetAccount);
        }

        ImGui.EndDisabled();
        this._actions.DrawStatus();

        this.DrawChannelTools(session, snapshot);
        this.DrawColourTest();
        this.DrawNotices();
        this.DrawTrace(session);
    }

    private void DrawChannelTools(ClientSession? session, SessionSnapshot snapshot) {
        if (!ImGui.CollapsingHeader("Channel tools", ImGuiTreeNodeFlags.DefaultOpen)) {
            return;
        }

        if (snapshot.Channels.IsEmpty) {
            ImGui.TextDisabled("No channels.");
            return;
        }

        var selected = this._channelId != null ? snapshot.FindChannel(this._channelId) : null;
        selected ??= snapshot.Channels[0];
        this._channelId = selected.Id;

        ImGui.SetNextItemWidth(260);
        if (ImGui.BeginCombo("Channel", selected.DisplayName)) {
            foreach (var channel in snapshot.Channels) {
                if (ImGui.Selectable($"{channel.DisplayName}##{channel.Id}", channel.Id == selected.Id)) {
                    this._channelId = channel.Id;
                }
            }

            ImGui.EndCombo();
        }

        ImGui.TextDisabled($"id {selected.Id}  key epoch {selected.Epoch}  server epoch {selected.ServerEpoch}  current key {(selected.HasKey ? "yes" : "no")}  rekey pending {(selected.RekeyPending ? "yes" : "no")}  log entry #{selected.LogHead?.Seq.ToString() ?? "-"}");

        ImGui.SetNextItemWidth(260);
        ImGui.InputText("##test-message", ref this._testMessage, 400);

        if (ImGui.Button("Simulate incoming (local only)")) {
            this._sessions.SimulateIncoming(selected.Id, this._testMessage);
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(session == null || this._actions.Busy);
        if (ImGui.Button("Send for real") && session != null) {
            var text = this._testMessage;
            var id = selected.Id;
            this._actions.Run("Sending", () => session.SendTextAsync(id, text));
        }

        ImGui.SameLine();
        if (ImGui.Button("Force rekey") && session != null) {
            var id = selected.Id;
            this._actions.Run("Rekeying", () => session.RekeyAsync(id, force: true));
        }

        ImGui.EndDisabled();
        ImGui.TextDisabled("Echo bot commands (send them for real): !ping, !rekey, !leave");
    }

    /// <summary>
    /// Custom colours in game chat, without changing any channel (also "/lgdebug colours #RRGGBB ..."): for each colour, a
    /// line layered as a channel's is, one in only its closest game colour, and one in only the exact colour, to compare in
    /// the game's chat and in ChatTwo. See docs/testing/custom-colours-checklist.md.
    /// </summary>
    private void DrawColourTest() {
        if (!ImGui.CollapsingHeader("Colour test")) {
            return;
        }

        ImGui.SetNextItemWidth(260);
        ImGui.InputTextWithHint("##test-colours", "#FF66CC #33DDAA (empty: five samples)", ref this._testColours, 80);
        var colours = ColouredText.TestColoursFrom(this._testColours);
        ImGui.SameLine();
        ImGui.BeginDisabled(colours == null);
        if (ImGui.Button("Print colour samples to game chat") && colours != null) {
            this._chat.ColourSamples(colours);
        }

        ImGui.EndDisabled();
        if (colours == null) {
            ImGui.TextColored(Widgets.Error, "Colour codes are # and six digits or letters A to F, separated by spaces.");
            return;
        }

        foreach (var rgb in colours) {
            var nearest = ChannelPalette.Nearest(rgb);
            ImGui.ColorButton($"##exact{rgb}", ChannelPalette.OfRgb(rgb), ImGuiColorEditFlags.NoAlpha);
            ImGui.SameLine();
            if (nearest is { } row && ChannelPalette.ColourOf(row) is { } fallback) {
                ImGui.ColorButton($"##nearest{rgb}", fallback, ImGuiColorEditFlags.NoAlpha);
                ImGui.SameLine();
            }

            ImGui.TextUnformatted($"{HexColour.Format(rgb)} -> UIColor {nearest?.ToString() ?? "none"}" +
                                  (ColourMatch.HardToRead(rgb) ? $"  (hard to read: contrast {ColourMatch.Contrast(rgb, ColourMatch.TypicalChatBackground):0.0})" : ""));
        }
    }

    private void DrawNotices() {
        if (!ImGui.CollapsingHeader("Recent notices")) {
            return;
        }

        if (ImGui.BeginChild("notices", new Vector2(0, 140), true)) {
            foreach (var notice in this._sessions.RecentNotices.Reverse()) {
                ImGui.TextWrapped($"[{notice.Level}] {notice.Text}");
            }
        }

        ImGui.EndChild();
    }

    private void DrawTrace(ClientSession? session) {
        if (!ImGui.CollapsingHeader("Protocol trace")) {
            return;
        }

        ImGui.TextDisabled("Frame types only; contents are never logged.");
        if (ImGui.BeginChild("trace", new Vector2(0, 200), true)) {
            foreach (var entry in (session?.GetTrace() ?? []).Reverse()) {
                ImGui.TextUnformatted($"{entry.Time:HH:mm:ss.fff} {(entry.Outgoing ? "->" : "<-")} {entry.Summary}");
            }
        }

        ImGui.EndChild();
    }

    /// <summary>Lets the ping result reuse the action status line.</summary>
    private sealed class PingResult(TimeSpan rtt) : Exception($"round trip {rtt.TotalMilliseconds:0} ms");
}
