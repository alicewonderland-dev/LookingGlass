using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using LookingGlass.Core.Client;
using LookingGlass.Core.Util;

namespace LookingGlass.Plugin.Ui;

/// <summary>
/// A channel window (pop-out chat): one tab per channel, each with the channel's warnings, its messages since login
/// (from <see cref="SessionManager.History"/>), above them older ones from the chat log on this computer if the player keeps
/// one (<see cref="SessionManager.ChatLog"/>, a page at a time, with a dimmed line for each day), and an input box that
/// sends only to that channel, through the same path as /lgc (<see cref="ChannelSender"/>). What is typed here never goes
/// near the game's chat box. Opened, remembered and closed by <see cref="ChannelWindows"/>; its tabs follow its
/// <see cref="ChannelWindowLayout"/>.
/// </summary>
public sealed class ChannelWindow : Window {
    /// <summary>The game's own limit on a chat line, in characters.</summary>
    public const int MaxLength = 500;

    /// <summary>From how many characters the input shows how many are left.</summary>
    private const int CounterFrom = 400;

    private const string IdPrefix = "###lookingglass-channel-";

    private readonly ChannelWindows _windows;
    private readonly Dictionary<string, TabState> _tabs = new();
    private string? _selectRequest;
    private WindowPlace? _restorePlace;
    private bool _placeAtMouse;
    private string _tabsSeen = "";
    private int _tabsSteadyFrames;
    // A message that wasn't sent, to put into the input box's own text this frame (see OnInput).
    private string? _reload;

    /// <param name="opened">Opened by the player just now (else reopened at login): it appears by the mouse, and may take the focus.</param>
    public ChannelWindow(ChannelWindows windows, ChannelWindowLayout layout, bool opened) : base("LookingGlass" + IdPrefix + layout.Id) {
        this._windows = windows;
        this.Layout = layout;
        this.Viewer = "window:" + layout.Id;
        this._selectRequest = layout.Selected;

        // Escape gives the keyboard back to the game (and keeps what was typed); it never closes a channel window.
        this.RespectCloseHotkey = false;
        // The messages scroll, the window doesn't.
        this.Flags |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        // Where it is and how big is kept by the plugin, per character and server (ChannelWindowLayout.Place), not in ImGui's file.
        this.Flags |= ImGuiWindowFlags.NoSavedSettings;
        this.SizeConstraints = new WindowSizeConstraints {
            MinimumSize = new Vector2(260, 180),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        if (!opened && layout.Place is { } place) {
            this._restorePlace = place;
        } else {
            // Dalamud scales this by the global scale itself.
            this.Size = new Vector2(440, 340);
            this.SizeCondition = ImGuiCond.FirstUseEver;
            this._placeAtMouse = opened;
        }

        if (!opened) {
            // Reopened at login: it doesn't take the focus from the game.
            this.Flags |= ImGuiWindowFlags.NoFocusOnAppearing;
        }

        // A Dalamud window starts closed: one exists here only to be shown (opened, or reopened at login).
        this.IsOpen = true;
    }

    public ChannelWindowLayout Layout { get; }

    /// <summary>Its name as one of the windows that read channels (see <see cref="UnreadCounter.Viewing(string, string?)"/>).</summary>
    public string Viewer { get; }

    /// <summary>Set before the window is taken away for the session ending: closing it then doesn't forget it.</summary>
    internal bool Detaching { get; set; }

    /// <summary>Its tabs' names, for the menus that list windows.</summary>
    public string MenuName {
        get {
            var names = string.Join(", ", this.Layout.Tabs.Select(this.ShortName));
            return Visible(names.Length > 40 ? names[..37].TrimEnd() + "..." : names);
        }
    }

    private SessionManager Sessions => this._windows.Sessions;

    /// <summary>Brings the window to the front with a channel's tab selected.</summary>
    public void Show(string channelId) {
        this._selectRequest = channelId;
        this.IsOpen = true;
        this.Flags &= ~ImGuiWindowFlags.NoFocusOnAppearing;
        this.BringToFront();
    }

    public override void Update() {
        // The selected channel's name: the window says which channel its input box sends to. "##" can't end the title early.
        var title = this.Layout.Selected is { } selected ? this.LongName(selected) : "LookingGlass";
        this.WindowName = Visible(title) + IdPrefix + this.Layout.Id;
    }

    public override void PreDraw() {
        if (this._restorePlace is { } place) {
            ImGui.SetNextWindowPos(new Vector2(place.X, place.Y), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(place.Width, place.Height), ImGuiCond.Always);
            this._restorePlace = null;
        } else if (this._placeAtMouse) {
            ImGui.SetNextWindowPos(ImGui.GetMousePos() + new Vector2(12, 12) * Widgets.Scale, ImGuiCond.Always);
            this._placeAtMouse = false;
        }
    }

    public override void OnClose() {
        this.Sessions.Unread.Viewing(this.Viewer, null);
        if (!this.Detaching) {
            // The player closed it: it isn't opened again at the next login.
            this._windows.Forget(this);
        }
    }

    public override void Draw() {
        var snapshot = this.Sessions.Snapshot;
        var advanced = this.Sessions.AdvancedMode;
        var history = this.Sessions.History;
        var focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        string? shown = null;
        var closing = new List<string>();
        var order = new List<(float X, string Id)>();
        var openAdd = false;

        if (ImGui.BeginTabBar("##tabs", ImGuiTabBarFlags.Reorderable | ImGuiTabBarFlags.FittingPolicyScroll)) {
            foreach (var channelId in this.Layout.Tabs.ToList()) {
                var state = this.Tab(channelId);
                var channel = snapshot.FindChannel(channelId);
                ImGui.PushID(channelId);

                // Name in the channel's colour; on a tab not shown, how many messages came since it last was.
                var unread = this.Layout.Selected == channelId ? 0 : history.FromOthersAfter(channelId, state.SeenSeq);
                var label = Visible(this.ShortName(channelId)) + (unread > 0 ? $"  ({UnreadCounter.Format(unread)})" : "");
                var colour = this.ColourOf(channelId);
                if (colour is { } c) {
                    ImGui.PushStyleColor(ImGuiCol.Text, c);
                }

                var open = true;
                var flags = this._selectRequest == channelId ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
                var visible = ImGui.BeginTabItem($"{label}###tab", ref open, flags);
                if (colour != null) {
                    ImGui.PopStyleColor();
                }

                order.Add((ImGui.GetItemRectMin().X, channelId));
                this.TabTooltip(channelId, channel, unread, advanced);
                if (ImGui.BeginPopupContextItem("##tab-menu")) {
                    var inGameChat = this.Sessions.ShowsInGameChat(channelId);
                    if (ImGui.MenuItem("Also show in game chat", "", inGameChat)) {
                        this._windows.SetShowInGameChat(channelId, !inGameChat);
                    }

                    if (ImGui.MenuItem("Close tab")) {
                        closing.Add(channelId);
                    }

                    ImGui.EndPopup();
                }

                if (visible) {
                    shown = channelId;
                    this.DrawTab(channelId, channel, snapshot, state, advanced);
                    ImGui.EndTabItem();
                }

                if (!open) {
                    closing.Add(channelId);
                }

                ImGui.PopID();
            }

            if (ImGui.TabItemButton("+##add", ImGuiTabItemFlags.Trailing | ImGuiTabItemFlags.NoTooltip)) {
                openAdd = true;
            }

            Widgets.Tooltip("Add a channel to this window.");
            ImGui.EndTabBar();
        }

        if (openAdd) {
            ImGui.OpenPopup("add-tab");
        }

        this.DrawAddPopup(snapshot, advanced);

        if (shown != null) {
            // The tab shown is read here; in the channel list too while this window has the focus.
            this.Tab(shown).SeenSeq = history.LastSeq(shown);
            if (this._selectRequest == shown) {
                this._selectRequest = null;
            }
        }

        if (this._selectRequest != null && !this.Layout.Tabs.Contains(this._selectRequest)) {
            this._selectRequest = null;
        }

        this.Sessions.Unread.Viewing(this.Viewer, focused ? shown : null);
        this.Remember(shown, order, closing);
    }

    // ================================================================ layout

    /// <summary>What changed this frame, into the layout: the tab selected, the tabs' order after a drag, closed tabs, the place.</summary>
    private void Remember(string? shown, List<(float X, string Id)> order, List<string> closing) {
        var changed = false;
        if (shown != null && this._selectRequest == null && this.Layout.Selected != shown) {
            this.Layout.Selected = shown;
            changed = true;
        }

        // ImGui reorders tabs as they are dragged, and doesn't say so: read the order off where they are, once the mouse
        // is let go, and only once the tabs have been the same for a few frames (a tab added just now has no place yet).
        var seen = string.Join("\n", this.Layout.Tabs);
        this._tabsSteadyFrames = seen == this._tabsSeen ? this._tabsSteadyFrames + 1 : 0;
        this._tabsSeen = seen;
        if (this._tabsSteadyFrames > 3 && ImGui.IsMouseReleased(ImGuiMouseButton.Left)
            && ChannelWindowLayouts.Reorder(this.Layout, order.OrderBy(tab => tab.X).Select(tab => tab.Id).ToList())) {
            changed = true;
        }

        foreach (var channelId in closing.Distinct()) {
            this._tabs.Remove(channelId);
            if (ChannelWindowLayouts.CloseTab(this.Layout, channelId)) {
                // The last tab: the window goes with it.
                this._windows.Forget(this);
                return;
            }

            changed = true;
        }

        // Saved once a move or resize is over, not every frame of it.
        var place = new WindowPlace(ImGui.GetWindowPos().X, ImGui.GetWindowPos().Y, ImGui.GetWindowSize().X, ImGui.GetWindowSize().Y);
        if (!place.Near(this.Layout.Place) && !ImGui.IsMouseDown(ImGuiMouseButton.Left)) {
            this.Layout.Place = place;
            changed = true;
        }

        if (changed) {
            this._windows.Config.Save();
        }
    }

    private void DrawAddPopup(SessionSnapshot snapshot, bool advanced) {
        if (!ImGui.BeginPopup("add-tab")) {
            return;
        }

        ImGui.TextColored(Widgets.Muted, "Add a channel to this window");
        ImGuiHelpers.ScaledDummy(2);
        var addable = ChannelWindowLayouts.Addable(this.Layout, Ordered(snapshot, this.Sessions)
            .Where(channel => !channel.OldKeyMembership).Select(channel => channel.Id)).ToList();
        if (addable.Count == 0) {
            ImGui.TextUnformatted("All your channels are in this window.");
        }

        foreach (var channelId in addable) {
            var colour = this.ColourOf(channelId);
            if (colour is { } c) {
                ImGui.PushStyleColor(ImGuiCol.Text, c);
            }

            var chosen = ImGui.Selectable($"{Visible(Widgets.Ellipsize(this.LongName(channelId), 300 * Widgets.Scale))}##{channelId}");
            if (colour != null) {
                ImGui.PopStyleColor();
            }

            if (chosen) {
                ChannelWindowLayouts.AddTab(this.Layout, channelId);
                this._selectRequest = channelId;
                this._windows.Config.Save();
            }
        }

        ImGui.EndPopup();
    }

    private void TabTooltip(string channelId, ChannelView? channel, int unread, bool advanced) {
        if (!ImGui.IsItemHovered()) {
            return;
        }

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30);
        ImGui.TextUnformatted(Visible(channel?.DisplayNameFor(advanced) ?? this.LongName(channelId)));
        var slot = this.Sessions.SlotOf(channelId);
        var nickname = this.Sessions.NicknameOf(channelId);
        var commands = slot is { } n ? $"{CommandSlots.Prefix}{n}" : "no number";
        if (nickname != null) {
            commands += $"  or  {CommandSlots.Prefix} {nickname}";
        }

        ImGui.TextColored(Widgets.Muted, commands);
        if (unread > 0) {
            ImGui.TextUnformatted(unread == 1 ? "1 new message" : $"{unread} new messages");
        }

        if (!this.Sessions.ShowsInGameChat(channelId)) {
            ImGui.TextColored(Widgets.Muted, "Not shown in game chat: right-click to change.");
        }

        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    // ================================================================ a tab

    private void DrawTab(string channelId, ChannelView? channel, SessionSnapshot snapshot, TabState state, bool advanced) {
        if (channel == null) {
            ImGui.TextColored(Widgets.Muted, snapshot.ChannelsLoaded ? "You're no longer in this channel." : "Loading your channels...");
            return;
        }

        // The channel's warnings, as the channel pane shows them.
        if (channel.WarningFor(advanced) is { } warning) {
            Widgets.IconText(FontAwesomeIcon.ExclamationTriangle, ChannelPane.WarningTitle(channel, advanced), Widgets.Warning);
            Widgets.WrappedColoured(Widgets.Warning, warning);
            ImGuiHelpers.ScaledDummy(2);
        }

        if (ChannelPane.Problem(channel, advanced) is var (text, tooltip, icon)) {
            Widgets.IconText(icon, text, Widgets.Warning);
            Widgets.Tooltip(tooltip);
            ImGuiHelpers.ScaledDummy(2);
        }

        if (snapshot.State != ConnectionState.Ready) {
            Widgets.IconText(FontAwesomeIcon.Sync, "Not connected right now: messages can't be sent until LookingGlass reconnects.", Widgets.Muted);
        }

        var footer = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y;
        if (ImGui.BeginChild("##messages", new Vector2(0, -footer), false)) {
            this.DrawLines(channelId, state, advanced);
        }

        ImGui.EndChild();
        this.DrawInput(channelId, state);
    }

    private void DrawLines(string channelId, TabState state, bool advanced) {
        var lines = this.Sessions.History.LinesOf(channelId);
        // Older lines from the chat log on this computer, if the player keeps one: a page at a time, above these.
        var earlier = this.Sessions.ChatLog?.Earlier(channelId);
        // Never from someone blocked since, as live messages from them aren't shown.
        var older = earlier?.ShownWith(lines, this.Sessions.Snapshot.BlockedUsers) ?? ImmutableArray<HistoryLine>.Empty;
        var width = ImGui.GetContentRegionAvail().X;
        if (state.Advanced != advanced) {
            // Notices say other words now.
            state.Shown.Clear();
            state.Heights.Clear();
            state.Advanced = advanced;
        }

        if (!ReferenceEquals(state.Earlier, earlier)) {
            // Another log (or none): what was drawn of the old one's lines goes.
            foreach (var seq in state.Shown.Keys.Where(seq => seq < 0).ToList()) {
                state.Shown.Remove(seq);
                state.Heights.Remove(seq);
            }

            state.Earlier = earlier;
        }

        // Following the newest line unless scrolled up (as at the last frame), or reading older lines.
        var atBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 1;
        if (state.ReadingOlder && atBottom && ImGui.GetScrollMaxY() > 0) {
            state.ReadingOlder = false;
        }

        var follow = state.ScrollToBottom || (atBottom && !state.ReadingOlder);
        if (earlier != null) {
            DrawOlderControls(earlier, older, state, advanced);
        }

        var colour = this.ColourOf(channelId);
        var top = (Seq: 0L, Y: 0f);
        var first = true;
        DateTime? day = null;
        foreach (var line in older) {
            // A dimmed line above each day's older lines.
            var shownAt = (line.CaughtUp ? line.SentAt : line.Time).ToLocalTime();
            if (shownAt.Date != day) {
                day = shownAt.Date;
                DrawDivider(ChatLogWords.Earlier(shownAt).For(advanced));
            }

            this.DrawOne(line, state, advanced, colour, width, follow, ref first, ref top);
        }

        if (!older.IsEmpty) {
            DrawDivider(ChatLogWords.SinceLogin.For(advanced));
        }

        if (lines.IsEmpty) {
            ImGui.TextColored(Widgets.Muted, "No messages since you logged in. New ones show here.");
        }

        foreach (var line in lines) {
            this.DrawOne(line, state, advanced, colour, width, follow, ref first, ref top);
        }

        state.TopSeq = top.Seq;
        state.TopY = top.Y;

        if (ImGui.BeginPopup("line-menu")) {
            if (ImGui.MenuItem("Copy") && state.MenuText != null) {
                ImGui.SetClipboardText(state.MenuText);
            }

            ImGui.EndPopup();
        }

        // Scrolling up at the top of what is shown reads the next older page.
        if (earlier is { HasMore: true, Loading: false } && ImGui.GetScrollY() <= 0.5f && ImGui.IsWindowHovered() && ImGui.GetIO().MouseWheel > 0) {
            state.ReadingOlder = true;
            _ = earlier.LoadMoreAsync();
        }

        var last = lines.IsEmpty ? 0 : lines[^1].Seq;
        if (follow) {
            ImGui.SetScrollHereY(1f);
            state.BottomSeq = last;
            state.ScrollToBottom = false;
            state.ReadingOlder = false;
        } else if (last > state.BottomSeq) {
            this.DrawNewMessagesButton(state);
        }

        // What fell out of the history goes from here too (older lines from the log stay while shown).
        if (!lines.IsEmpty && state.Shown.Count > lines.Length + older.Length + 50) {
            var oldest = lines[0].Seq;
            foreach (var seq in state.Shown.Keys.Where(seq => seq > 0 && seq < oldest).ToList()) {
                state.Shown.Remove(seq);
                state.Heights.Remove(seq);
            }
        }
    }

    /// <summary>
    /// Draws one line. The line that was at the top at the last frame keeps its place on screen when older lines arrive
    /// above it, so reading on up doesn't jump.
    /// </summary>
    private void DrawOne(HistoryLine line, TabState state, bool advanced, Vector4? colour, float width, bool follow, ref bool first,
        ref (long Seq, float Y) top) {
        var y = ImGui.GetCursorPosY();
        if (first) {
            top = (line.Seq, y);
        } else if (line.Seq == state.TopSeq && y > state.TopY + 0.5f && !follow) {
            ImGui.SetScrollY(ImGui.GetScrollY() + (y - state.TopY));
        }

        first = false;

        // A line out of view whose height is known takes its room without being laid out. Lines in view are laid out,
        // and measured again, every frame: after a resize, the others catch up as they come into view.
        if (state.Heights.TryGetValue(line.Seq, out var height) && !ImGui.IsRectVisible(new Vector2(width, height))) {
            ImGui.Dummy(new Vector2(1, height));
            return;
        }

        if (!state.Shown.TryGetValue(line.Seq, out var shown)) {
            shown = this.Build(line, advanced);
            state.Shown[line.Seq] = shown;
        }

        ImGui.BeginGroup();
        DrawLine(shown, colour);
        ImGui.EndGroup();
        state.Heights[line.Seq] = ImGui.GetItemRectSize().Y;
        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) {
            state.MenuText = shown.CopyText;
            ImGui.OpenPopup("line-menu");
        }
    }

    /// <summary>Above the older lines: "Show older messages", or that they're being read, or that there are no more.</summary>
    private static void DrawOlderControls(EarlierLines earlier, ImmutableArray<HistoryLine> older, TabState state, bool advanced) {
        if (earlier.Loading) {
            ImGui.TextColored(Widgets.Muted, ChatLogWords.Loading.For(advanced));
        } else if (earlier.HasMore) {
            if (Widgets.GhostButton(ChatLogWords.ShowOlder.For(advanced) + "##older")) {
                state.ReadingOlder = true;
                _ = earlier.LoadMoreAsync();
            }
        } else if (!older.IsEmpty) {
            ImGui.TextColored(Widgets.Muted, ChatLogWords.NothingOlder.For(advanced));
        }
    }

    /// <summary>A dimmed line across the messages with words in the middle: a day of older lines, or where this session's start.</summary>
    private static void DrawDivider(string text) {
        var width = ImGui.GetContentRegionAvail().X;
        var size = ImGui.CalcTextSize(text);
        var start = ImGui.GetCursorScreenPos();
        var indent = Math.Max(0, (width - size.X) / 2);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + indent);
        ImGui.TextColored(Widgets.Muted, text);
        var pad = 6 * Widgets.Scale;
        if (indent > pad * 2) {
            var y = MathF.Floor(start.Y + size.Y / 2);
            var colour = ImGui.GetColorU32(Widgets.Muted with { W = Widgets.Muted.W * 0.5f });
            var drawList = ImGui.GetWindowDrawList();
            drawList.AddLine(new Vector2(start.X, y), new Vector2(start.X + indent - pad, y), colour);
            drawList.AddLine(new Vector2(start.X + indent + size.X + pad, y), new Vector2(start.X + width, y), colour);
        }
    }

    /// <summary>Over the bottom of the messages while scrolled up and something new came: back down.</summary>
    private void DrawNewMessagesButton(TabState state) {
        const string label = "New messages";
        var size = new Vector2(ImGuiComponents.GetIconButtonWithTextWidth(FontAwesomeIcon.ArrowDown, label), ImGui.GetFrameHeight());
        ImGui.SetCursorPos(new Vector2(
            ImGui.GetScrollX() + Math.Max(0, (ImGui.GetWindowWidth() - size.X) / 2),
            ImGui.GetScrollY() + ImGui.GetWindowHeight() - size.Y - 6 * Widgets.Scale));
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.ArrowDown, label)) {
            state.ScrollToBottom = true;
        }
    }

    private void DrawInput(string channelId, TabState state) {
        // Not sent: what was typed comes back, to send again, once the box is empty (not over a line being typed). While
        // the box is active ImGui edits its own copy of the text, so it is put there too (in OnInput).
        this._reload = null;
        if (state.PutBack is { } putBack && state.Draft.Length == 0) {
            state.Draft = putBack;
            state.PutBack = null;
            this._reload = putBack;
        }

        var scale = Widgets.Scale;
        var style = ImGui.GetStyle();
        var counter = state.Draft.Length >= CounterFrom ? $"{state.Draft.Length}/{MaxLength}" : null;
        var counterWidth = counter != null ? ImGui.CalcTextSize($"{MaxLength}/{MaxLength}").X + style.ItemSpacing.X : 0;
        var send = Widgets.GhostIconButtonWidth;

        ImGui.SetNextItemWidth(Math.Max(40 * scale, ImGui.GetContentRegionAvail().X - send - counterWidth - style.ItemSpacing.X));
        var before = state.Draft;
        var hint = $"Message {Visible(this.ShortName(channelId))}";
        // Room for the longest line in any script (UTF-8); the limit itself is in characters, as the game's, and kept by
        // OnInput as the text is edited, so the box never holds more than is sent.
        var enter = ImGui.InputTextWithHint("##input", hint, ref state.Draft, MaxLength * 4 + 1,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CallbackAlways | ImGuiInputTextFlags.CallbackEdit, this.OnInput);
        this._reload = null;
        if (enter && state.Draft.Trim().Length > 0) {
            // Ready for the next line, as game chat is (right after the input: it means the item before). Enter on an
            // empty line gives the keyboard back instead.
            ImGui.SetKeyboardFocusHere(-1);
        }

        if (ImGui.IsItemDeactivated() && ImGui.IsKeyPressed(ImGuiKey.Escape)) {
            // Escape gives the keyboard back to the game. ImGui would also undo the typing: keep it.
            state.Draft = before;
        }

        if (counter != null) {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(state.Draft.Length >= MaxLength ? Widgets.Warning : Widgets.Muted, counter);
            Widgets.Tooltip($"A message can be up to {MaxLength} characters, as in game chat.");
        }

        ImGui.SameLine();
        var clicked = Widgets.GhostIconButton("##send", FontAwesomeIcon.PaperPlane, $"Send to {this.ShortName(channelId)} (Enter)");
        if (enter || clicked) {
            this.Send(channelId, state);
        }
    }

    /// <summary>
    /// The input box's own text, while it is active: puts a message that wasn't sent back into it (<see cref="_reload"/>),
    /// and keeps it to <see cref="MaxLength"/> characters as it is edited (typing or pasting past it stops there, in view).
    /// </summary>
    private unsafe int OnInput(ImGuiInputTextCallbackDataPtr data) {
        if (this._reload is { } reload && data.EventFlag == ImGuiInputTextFlags.CallbackAlways) {
            this._reload = null;
            data.DeleteChars(0, data.BufTextLen);
            data.InsertChars(0, reload);
            return 0;
        }

        if (data.EventFlag == ImGuiInputTextFlags.CallbackEdit) {
            var text = Encoding.UTF8.GetString(data.Buf, data.BufTextLen);
            if (text.Length > MaxLength) {
                var keep = Encoding.UTF8.GetByteCount(Cut(text, MaxLength));
                data.DeleteChars(keep, data.BufTextLen - keep);
            }
        }

        return 0;
    }

    /// <summary>Sends what was typed to this tab's channel, as /lgc would. A failure shows in the tab, in blue.</summary>
    private void Send(string channelId, TabState state) {
        var text = state.Draft.Trim();
        if (text.Length == 0) {
            return;
        }

        state.Draft = "";
        state.ScrollToBottom = true;
        var history = this.Sessions.History;
        // A "Not sent" that comes after a relog belongs to the session it was typed in, and is dropped.
        var generation = history.Generation;
        var sender = this._windows.Sender;
        _ = Services.Framework.RunOnFrameworkThread(() => {
            try {
                sender.Send(channelId, TypedLine.Plain(text), tell: (tone, said) => history.AddFeedback(channelId, tone, said, generation),
                    notSent: () => state.PutBack = text);
            } catch (Exception ex) {
                Services.Log.Error(ex, "Couldn't send from a channel window");
                history.AddFeedback(channelId, NoticeTone.Warning, $"Not sent: {StickyMessages.SomethingWentWrongReason}", generation);
                state.PutBack = text;
            }
        });
    }

    // ================================================================ lines

    /// <summary>A line as drawn: its time, who sent it, and its words, worked out once (links checked against the game's data).</summary>
    private sealed record ShownLine(string Time, string? Sender, string? SenderDetail, IReadOnlyList<Piece> Pieces, Vector4? Colour, string CopyText);

    /// <summary>A run of text, or a link (its name in brackets, checked against the player's own game data).</summary>
    private sealed record Piece(string Text, ChatLink? Link = null, string? Tooltip = null);

    private ShownLine Build(HistoryLine line, bool advanced) {
        // A message caught up from while the player was away shows when it was sent, and one from the chat log when it
        // was shown then; both with the day, if not today.
        var time = line.CaughtUp || line.FromLog
            ? CatchUpChat.TimeLabel(line.CaughtUp ? line.SentAt : line.Time, DateTimeOffset.Now)
            : line.Time.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        if (line.Kind != HistoryLineKind.Message) {
            var text = TextSanitizer.Clean(line.TextFor(advanced));
            // LookingGlass's own lines: notices dimmed, warnings in their colour; feedback (Not sent) in LookingGlass blue.
            Vector4? colour = line.Kind == HistoryLineKind.Notice && line.Tone == NoticeTone.Info
                ? null
                : ChannelPalette.ColourOf(NoticeColours.Of(line.Tone)) ?? Widgets.Warning;
            return new ShownLine(time, null, null, [new Piece(text)], colour ?? Widgets.Muted, $"[{time}] {text}");
        }

        var name = TextSanitizer.Name(line.Sender?.Name);
        var world = TextSanitizer.Name(line.Sender?.WorldName);
        var pieces = new List<Piece>();
        if (line.Unsupported || line.Message == null) {
            pieces.Add(new Piece("(a message type this version can't show)"));
        } else {
            foreach (var part in line.Message.ShownParts()) {
                switch (part) {
                    case MessagePart.Text text:
                        pieces.Add(new Piece(text.Value));
                        break;
                    case MessagePart.Link link when GameLinks.SheetName(link.Target) is { } linkName:
                        pieces.Add(new Piece($"[{TextSanitizer.Name(linkName)}]", link.Target, LinkTooltip(link.Target, TextSanitizer.Name(linkName))));
                        break;
                    case MessagePart.Link link:
                        pieces.Add(new Piece(link.Fallback));
                        break;
                }
            }
        }

        var body = string.Concat(pieces.Select(piece => piece.Text));
        return new ShownLine(time, name, $"{name}@{world}" + (line.IsOwn ? " (you)" : ""), pieces, line.Unsupported ? Widgets.Muted : null,
            $"[{time}] {name}@{world}: {body}");
    }

    private static string LinkTooltip(ChatLink link, string name) => link switch {
        ChatLink.Item item => ChatLinks.ItemParts(item.RawId)?.Kind switch {
            ItemLinkKind.HighQuality => $"{name}\nHigh quality",
            ItemLinkKind.Collectable => $"{name}\nCollectable",
            ItemLinkKind.EventItem => $"{name}\nKey item",
            _ => name,
        },
        ChatLink.MapFlag => $"{name}\nClick to open the map there.",
        _ => name,
    };

    /// <summary>
    /// The time (muted), the sender (in the channel's colour), then the words, wrapped to the window: continuation lines
    /// start under the sender, so the times stay a column.
    /// </summary>
    private static void DrawLine(ShownLine line, Vector4? channelColour) {
        var scale = Widgets.Scale;
        ImGui.TextColored(Widgets.Muted, line.Time);
        ImGui.SameLine(0, 6 * scale);
        var start = ImGui.GetCursorPosX();
        if (line.Sender != null) {
            ImGui.TextColored(channelColour ?? Widgets.Text, line.Sender);
            if (line.SenderDetail != null) {
                Widgets.Tooltip(line.SenderDetail);
            }

            ImGui.SameLine(0, 6 * scale);
        }

        var right = ImGui.GetWindowContentRegionMax().X;
        var first = true;
        foreach (var piece in line.Pieces) {
            foreach (var word in piece.Link != null ? [piece.Text] : Words(piece.Text)) {
                var size = ImGui.CalcTextSize(word).X;
                if (!first) {
                    ImGui.SameLine(0, 0);
                }

                first = false;
                if (ImGui.GetCursorPosX() + size > right && ImGui.GetCursorPosX() > start + 1) {
                    ImGui.NewLine();
                    ImGui.SetCursorPosX(start);
                }

                if (piece.Link is { } link) {
                    DrawLink(word, link, piece.Tooltip);
                } else if (size > right - start) {
                    // A word longer than a line (an address): ImGui breaks it where it must.
                    ImGui.PushTextWrapPos(0);
                    ImGui.TextColored(line.Colour ?? Widgets.Text, word);
                    ImGui.PopTextWrapPos();
                } else {
                    ImGui.TextColored(line.Colour ?? Widgets.Text, word);
                }
            }
        }

        if (first) {
            // Nothing after the sender: end the line.
            ImGui.NewLine();
        }
    }

    /// <summary>A link: hover for what it is; a map flag opens the map at it when clicked, as the game's own link does.</summary>
    private static void DrawLink(string text, ChatLink link, string? tooltip) {
        var colour = link switch {
            ChatLink.Item => ImGuiColors.DalamudYellow,
            ChatLink.MapFlag => ImGuiColors.TankBlue,
            _ => ImGuiColors.DalamudViolet,
        };
        ImGui.TextColored(colour, text);
        if (!ImGui.IsItemHovered()) {
            return;
        }

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        ImGui.GetWindowDrawList().AddLine(new Vector2(min.X, max.Y), max, ImGui.GetColorU32(colour), Math.Max(1, Widgets.Scale));
        if (tooltip != null) {
            Widgets.Tooltip(tooltip);
        }

        if (link is ChatLink.MapFlag map) {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) {
                _ = Services.Framework.RunOnFrameworkThread(() => GameLinks.OpenMap(map));
            }
        }
    }

    /// <summary>Text cut into words, each with the spaces after it, to wrap at.</summary>
    private static IEnumerable<string> Words(string text) {
        var start = 0;
        for (var at = 0; at < text.Length; at++) {
            if (text[at] == ' ' && (at + 1 == text.Length || text[at + 1] != ' ')) {
                yield return text[start..(at + 1)];
                start = at + 1;
            }
        }

        if (start < text.Length) {
            yield return text[start..];
        }
    }

    // ================================================================ helpers

    private TabState Tab(string channelId) {
        if (!this._tabs.TryGetValue(channelId, out var state)) {
            // Nothing in it has been seen in this window yet: a tab not selected (one reopened at login behind another) counts
            // what is already there from others; a tab selected reads it at once.
            state = new TabState();
            this._tabs[channelId] = state;
        }

        return state;
    }

    /// <summary>A tab's name: the channel's nickname, else its name, shortened.</summary>
    private string ShortName(string channelId) {
        if (this.Sessions.NicknameOf(channelId) is { } nickname) {
            return nickname;
        }

        var name = this.LongName(channelId);
        return name.Length > 24 ? name[..22].TrimEnd() + "..." : name;
    }

    private string LongName(string channelId) {
        var advanced = this.Sessions.AdvancedMode;
        return this.Sessions.Snapshot.FindChannel(channelId)?.DisplayNameFor(advanced)
               ?? (advanced ? ChannelView.PlaceholderName(channelId) : ChannelView.PlainPlaceholderName(channelId));
    }

    private Vector4? ColourOf(string channelId) => this.Sessions.ColourOf(channelId) is { } row ? ChannelPalette.ColourOf(row) : null;

    /// <summary>The channels in the order of the channel list: by number, then by name.</summary>
    internal static IEnumerable<ChannelView> Ordered(SessionSnapshot snapshot, SessionManager sessions) =>
        snapshot.Channels
            .OrderBy(channel => sessions.SlotOf(channel.Id) ?? int.MaxValue)
            .ThenBy(channel => channel.DisplayName, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>Remote text as a window title or label: sanitised, and with no "##", which ImGui would read as the start of an ID.</summary>
    private static string Visible(string text) => TextSanitizer.Name(text).Replace("##", "# #", StringComparison.Ordinal);

    /// <summary>The first <paramref name="max"/> characters, without splitting a surrogate pair.</summary>
    private static string Cut(string text, int max) {
        var cut = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..cut];
    }

    /// <summary>What a tab remembers while the window is open.</summary>
    private sealed class TabState {
        public string Draft = "";

        /// <summary>The newest line in the history when the tab was last shown: the newer ones from others are its unread count.</summary>
        public long SeenSeq;

        /// <summary>The newest line when the messages were last scrolled to the bottom.</summary>
        public long BottomSeq;

        public bool ScrollToBottom = true;

        /// <summary>A message that wasn't sent, to put back in the input box. Set from the framework thread.</summary>
        public volatile string? PutBack;

        public string? MenuText;
        public bool Advanced;

        /// <summary>The chat log's older lines drawn for this tab (see <see cref="EarlierLines"/>); another means another log.</summary>
        public EarlierLines? Earlier;

        /// <summary>The player asked for older lines: the tab doesn't follow the newest until scrolled back down.</summary>
        public bool ReadingOlder;

        /// <summary>The line at the top at the last frame, and where: it keeps its place when older lines arrive above it.</summary>
        public long TopSeq;

        public float TopY;
        public readonly Dictionary<long, float> Heights = new();
        public readonly Dictionary<long, ShownLine> Shown = new();
    }
}
