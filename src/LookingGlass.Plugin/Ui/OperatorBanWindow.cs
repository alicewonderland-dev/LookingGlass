using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using LookingGlass.Core.Client;

namespace LookingGlass.Plugin.Ui;

/// <summary>
/// "Ban from server" (see "The plugin's operator panel" in docs/operator-tools-plan.md, whose mockup it follows): opened from
/// the right-click menus, a flag's or a ban's line in Settings, or "Ban someone". It first finds the character (a name@world
/// with LookupUser, so the dialog shows exactly whom the ban is for), then asks how it stands (banned already, flagged, listed
/// as an operator); then how long, and the reason. Only for one of the server's operators; the server checks everything again.
/// What was done, or why not, is said in chat, in LookingGlass blue.
/// </summary>
public sealed class OperatorBanWindow : Window {
    private readonly Configuration _config;
    private readonly SessionManager _sessions;
    private readonly ChatOutput _chat;

    // Whom it is about: as shown, its user ID once known, and the session it was opened in.
    private string _who = "";
    private long _userId;
    private ClientSession? _session;
    // Finding it (and how it stands), and the answer.
    private Task<PanelSubject?>? _finding;
    private PanelSubject? _subject;
    private bool _notRegistered;
    private string? _problem;
    // The form.
    private OperatorDuration _duration = OperatorDuration.SevenDays;
    private string _customDays = "";
    private string _reason = "";
    // A ban or lift under way.
    private Task? _acting;

    public OperatorBanWindow(Configuration config, SessionManager sessions, ChatOutput chat) : base($"{OperatorWords.DialogTitle}###lookingglass-operator-ban") {
        this._config = config;
        this._sessions = sessions;
        this._chat = chat;
        this.Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse;
    }

    /// <summary>Opens it for a player right-clicked in a menu.</summary>
    public void Open(InviteTarget target) => this.Start(target.Who, session => FindAsync(session, target.Name, target.WorldName));

    /// <summary>Opens it for what "Ban someone" names: a name@world, or a Lodestone ID.</summary>
    public void Open(OperatorTarget target) => this.Start(target.Who, session => target.UserId != 0
        ? AboutAsync(session, target.UserId)
        : FindAsync(session, target.Name!, target.WorldName!));

    /// <summary>Opens it for a character on a line of the lists (to ban, or to lift its ban).</summary>
    public void Open(long userId, string who) => this.Start(who, session => AboutAsync(session, userId));

    private void Start(string who, Func<ClientSession, Task<PanelSubject?>> find) {
        if (this._acting is { IsCompleted: false }) {
            // A ban or lift is on its way: what it is about stays shown until it is answered.
            this.BringToFront();
            return;
        }

        this._who = who;
        this._userId = 0;
        this._subject = null;
        this._notRegistered = false;
        this._problem = null;
        this._duration = OperatorDuration.SevenDays;
        this._customDays = "";
        this._reason = "";
        this._session = this._sessions.Session;
        this._finding = this._session is { } session ? Task.Run(() => find(session)) : null;
        if (this._session == null) {
            this._problem = OperatorWords.NotAvailable.For(this._config.AdvancedMode);
        }

        this.IsOpen = true;
        this.BringToFront();
    }

    /// <summary>A name@world as the server knows it, then how it stands; null if nobody is registered by that name.</summary>
    private static async Task<PanelSubject?> FindAsync(ClientSession session, string name, string worldName) {
        var user = await session.FindPlayerAsync(name, worldName);
        return user == null ? null : await AboutAsync(session, user.UserId);
    }

    /// <summary>How a character stands (its User null if it isn't registered here).</summary>
    private static async Task<PanelSubject?> AboutAsync(ClientSession session, long userId) =>
        (await session.RefreshOperatorListsAsync(userId)).About ?? new PanelSubject(userId, null, false, null, null);

    public override void OnClose() {
        // What was found goes; a ban under way still says how it went in chat.
        this._finding = null;
        this._subject = null;
    }

    public override void Draw() {
        var advanced = this._config.AdvancedMode;
        this.TakeFound(advanced);
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28);
        try {
            this.DrawBody(advanced);
        } finally {
            ImGui.PopTextWrapPos();
        }
    }

    private void TakeFound(bool advanced) {
        if (this._finding is not { IsCompleted: true } finding) {
            return;
        }

        this._finding = null;
        if (!finding.IsCompletedSuccessfully) {
            var error = finding.Exception?.InnerException ?? finding.Exception;
            this._problem = error == null ? null : PlainMessages.Of(PlainMessages.MessageOf(error, advanced), advanced);
            return;
        }

        // A character listed as an operator says so, registered or not; any other must be registered to be banned here.
        if (finding.Result is not { } subject || (subject.User == null && !subject.ListedOperator)) {
            this._notRegistered = true;
            return;
        }

        this._subject = subject;
        this._userId = subject.UserId;
        if (subject.User is { } user) {
            this._who = $"{user.Name}@{user.WorldName}";
        }
    }

    private void DrawBody(bool advanced) {
        ImGui.TextUnformatted(this._who);
        ImGui.Spacing();
        if (this._finding != null) {
            ImGui.TextColored(Widgets.Muted, OperatorWords.Looking);
            this.DrawClose();
            return;
        }

        if (this._problem is { } problem && this._subject == null) {
            ImGui.TextColored(Widgets.Error, problem);
            this.DrawClose();
            return;
        }

        if (this._notRegistered) {
            ImGui.TextUnformatted(OperatorWords.NotRegistered(this._who));
            this.DrawClose();
            return;
        }

        if (this._subject is not { } subject) {
            this.DrawClose();
            return;
        }

        var me = this._session?.Snapshot.Me;
        var now = DateTimeOffset.UtcNow;
        if (me != null && me.UserId == subject.UserId) {
            ImGui.TextUnformatted(OperatorWords.NotYourself);
            this.DrawClose();
            return;
        }

        if (subject.ListedOperator) {
            ImGui.TextUnformatted(OperatorWords.IsAnOperator(this._who));
            this.DrawClose();
            return;
        }

        if (subject.Flag is { } flag) {
            ImGui.TextColored(Widgets.Warning, OperatorWords.FlaggedLine(flag, now));
            ImGui.Spacing();
        }

        var myName = me == null ? null : $"{me.Name}@{me.WorldName}";
        if (subject.Ban is { } ban) {
            Widgets.IconText(FontAwesomeIcon.Ban, OperatorWords.AlreadyBanned(ban, myName, now), Widgets.Warning);
            ImGui.Spacing();
        }

        var days = this.DrawHowLong();
        var reasonFits = this.DrawReason();
        ImGui.Spacing();
        ImGui.TextColored(Widgets.Muted, OperatorWords.WhatABanDoes);
        if (this._problem is { } failed) {
            ImGui.Spacing();
            ImGui.TextColored(Widgets.Error, failed);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        var busy = this._acting is { IsCompleted: false };
        if (ImGui.Button(OperatorWords.Cancel) || ImGui.IsKeyPressed(ImGuiKey.Escape)) {
            this.IsOpen = false;
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(busy || days == null || !reasonFits);
        if (ImGui.Button((subject.Ban != null ? OperatorWords.ReplaceBan : OperatorWords.BanFor(this._who)) + "###lg-operator-do-ban") && days is { } count && !busy) {
            this.Ban(count, this._reason.Trim(), advanced);
        }

        ImGui.EndDisabled();
        if (subject.Ban != null) {
            ImGui.SameLine();
            ImGui.BeginDisabled(busy);
            if (ImGui.Button(OperatorWords.LiftBan) && !busy) {
                this.Lift(advanced);
            }

            ImGui.EndDisabled();
        }
    }

    /// <summary>How long: 1, 7 or 30 days, until lifted, or a number of days. Returns the days (0: until lifted), or null if a typed number isn't one.</summary>
    private int? DrawHowLong() {
        Widgets.Label(OperatorWords.HowLong);
        ImGui.Indent();
        foreach (var choice in new[] { OperatorDuration.OneDay, OperatorDuration.SevenDays, OperatorDuration.ThirtyDays }) {
            Widgets.SameLineIfFits(ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(OperatorWords.NameOf(choice)).X);
            if (ImGui.RadioButton(OperatorWords.NameOf(choice), this._duration == choice)) {
                this._duration = choice;
            }
        }

        if (ImGui.RadioButton(OperatorWords.NameOf(OperatorDuration.UntilLifted), this._duration == OperatorDuration.UntilLifted)) {
            this._duration = OperatorDuration.UntilLifted;
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("##lg-operator-custom", this._duration == OperatorDuration.Custom)) {
            this._duration = OperatorDuration.Custom;
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(60 * Widgets.Scale);
        if (ImGui.InputText("##lg-operator-days", ref this._customDays, 6, ImGuiInputTextFlags.CharsDecimal)) {
            this._duration = OperatorDuration.Custom;
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(OperatorWords.Days);
        var days = OperatorPanel.DaysOf(this._duration, this._customDays);
        if (days == null) {
            ImGui.TextColored(Widgets.Warning, OperatorWords.DaysProblem);
        }

        ImGui.Unindent();
        return days;
    }

    /// <summary>The reason, the player is shown it, with its length against the limit. Returns whether it can be sent.</summary>
    private bool DrawReason() {
        Widgets.Label(OperatorWords.Reason);
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 26);
        // A little room past the limit, so going over it shows (and is refused) rather than cutting what was pasted.
        ImGui.InputText("##lg-operator-reason", ref this._reason, OperatorPanel.MaxReasonLength + 50);
        var fits = OperatorPanel.ReasonFits(this._reason);
        ImGui.TextColored(Widgets.Muted, OperatorWords.PlayerSeesIt);
        var counter = OperatorWords.Counter(this._reason.Trim().Length);
        Widgets.SameLineRightIfFits(ImGui.CalcTextSize(counter).X);
        ImGui.TextColored(fits ? Widgets.Muted : Widgets.Warning, counter);
        if (!fits) {
            ImGui.TextColored(Widgets.Warning, OperatorWords.ReasonProblem);
        }

        return fits;
    }

    private void DrawClose() {
        ImGui.Spacing();
        if (ImGui.Button("Close") || ImGui.IsKeyPressed(ImGuiKey.Escape)) {
            this.IsOpen = false;
        }
    }

    private void Ban(int days, string reason, bool advanced) {
        if (this._session is not { } session) {
            return;
        }

        var who = this._who;
        var userId = this._userId;
        this._problem = null;
        this._acting = Task.Run(async () => {
            try {
                await session.OperatorBanAsync(userId, days, reason);
                this._chat.Notice(NoticeTone.Info, OperatorWords.Banned(who, days));
                _ = Services.Framework.RunOnFrameworkThread(() => this.IsOpen = false);
            } catch (Exception ex) {
                // Names stay out of the plugin's log.
                Services.Log.Debug($"A ban from the operator panel failed: {ex.GetType().Name}");
                var line = OperatorWords.NotBanned(who, ex, advanced);
                this._problem = line;
                this._chat.Notice(NoticeTone.Info, line);
            }
        });
    }

    private void Lift(bool advanced) {
        if (this._session is not { } session) {
            return;
        }

        var who = this._who;
        var userId = this._userId;
        this._problem = null;
        this._acting = Task.Run(async () => {
            try {
                await session.OperatorLiftAsync(userId);
                this._chat.Notice(NoticeTone.Info, OperatorWords.Lifted(who));
                _ = Services.Framework.RunOnFrameworkThread(() => this.IsOpen = false);
            } catch (Exception ex) {
                Services.Log.Debug($"Lifting a ban from the operator panel failed: {ex.GetType().Name}");
                var line = OperatorWords.NotLifted(who, ex, advanced);
                this._problem = line;
                this._chat.Notice(NoticeTone.Info, line);
            }
        });
    }
}
