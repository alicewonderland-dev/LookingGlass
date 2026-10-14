using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using LookingGlass.Core.SelfTest;
using LookingGlass.Core.SelfTest.Suites;
using LookingGlass.Plugin.SelfTest;

namespace LookingGlass.Plugin.Ui;

/// <summary>
/// The self-test's window (docs/self-test-plan.md, "Mockups"): the start screen with what it does (the consent) and the
/// suites, or why it can't run here; a run, with "Needs you" when a step waits on the tester; and the report. Closing it
/// doesn't stop a run (<c>/lgdebug test</c> brings it back). Draw thread.
/// </summary>
internal sealed class SelfTestWindow : Window {
    private readonly SelfTestService _service;
    private readonly HashSet<string> _ticked;
    private SelfTestEnvironment? _environment;
    private Task<SelfTestEnvironment>? _reading;
    private DateTimeOffset _readAt;
    private bool _showSettings;
    private bool _startScreen = true;
    // The two-player screen instead of the start screen; as the host (or the partner).
    private bool _twoPlayers;
    private bool _asPartner;
    private string _partner = "";
    private string _host = "";
    private string _code = "";
    private readonly HashSet<string> _pairTicked;
    private string _note = "";
    private string _input = "";
    private string? _copied;
    private string? _error;
    // The report, made again only when the run's view changes.
    private (SelfTestView View, string Text)? _report;

    public SelfTestWindow(SelfTestService service) : base($"{SelfTestWords.WindowTitle}###lookingglass-selftest") {
        this._service = service;
        this._ticked = service.Suites.Select(suite => suite.Id).ToHashSet();
        this._pairTicked = service.PairSuites.Select(suite => suite.Id).ToHashSet();
        this.SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(480, 360), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
        this.Size = new Vector2(560, 620);
        this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>Opens the window (<c>/lgdebug test</c>, the debug window's button): a run going shows itself.</summary>
    public void Open() {
        this.IsOpen = true;
        this.BringToFront();
    }

    public override void OnOpen() {
        // A run going (or just done) is shown; otherwise the start screen.
        this._startScreen = this._service.Run == null;
    }

    public override void Draw() {
        try {
            var run = this._service.Run;
            if (run is { View.State: not SelfTestState.Done }) {
                this._startScreen = false;
                this.DrawRun(run);
            } else if (run != null && !this._startScreen) {
                this.DrawReport(run);
            } else if (this._twoPlayers) {
                this.DrawTwoPlayers();
            } else {
                this.DrawStart();
            }
        } catch (Exception ex) {
            this._error = ex.GetType().Name;
            Services.Log.Error(ex, "[selftest] the window couldn't be drawn");
        }
    }

    // ================================================================ the start screen

    private SelfTestEnvironment? Environment() {
        if (this._reading is { IsCompletedSuccessfully: true } done) {
            this._environment = done.Result;
            this._reading = null;
        }

        if (this._reading == null && DateTimeOffset.Now - this._readAt > TimeSpan.FromSeconds(1)) {
            this._readAt = DateTimeOffset.Now;
            this._reading = this._service.EnvironmentAsync();
        }

        return this._environment;
    }

    private void DrawStart() {
        var place = SelfTestService.PlaceNow(out var placeName);
        if (!place.Private) {
            this.DrawNotPrivate(place, placeName);
            return;
        }

        ImGui.TextWrapped(SelfTestWords.Intro);
        ImGui.Spacing();
        ImGui.TextUnformatted("What it does:");
        foreach (var line in SelfTestWords.Consent) {
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextWrapped(line);
        }

        if (ImGui.SmallButton(SelfTestWords.WhichOnes)) {
            this._showSettings = !this._showSettings;
        }

        var chosen = this._service.Suites.Where(suite => this._ticked.Contains(suite.Id)).ToList();
        if (this._showSettings) {
            ImGui.Indent();
            foreach (var setting in chosen.SelectMany(suite => suite.Changes).Distinct()) {
                ImGui.TextWrapped($"- {SelfTestWords.SettingName(setting)}: put back at the end.");
            }

            ImGui.TextDisabled(SelfTestWords.HandChanges);
            ImGui.Unindent();
        }

        ImGui.TextWrapped(SelfTestWords.NoNamesInReport);
        ImGui.Spacing();
        ImGui.TextColored(Widgets.Success, $"You're in: {SelfTestWords.Place(place.Kind)}.  OK");
        ImGui.TextWrapped(SelfTestWords.StayHere);
        if (place.NeedsNobodyNear) {
            Widgets.WrappedColoured(Widgets.Warning, SelfTestWords.HouseVisitors);
        }

        ImGui.Separator();
        var environment = this.Environment();
        foreach (var suite in this._service.Suites) {
            var lacks = SuiteLacks(suite, environment);
            var ticked = this._ticked.Contains(suite.Id) && lacks == null;
            ImGui.BeginDisabled(lacks != null);
            if (ImGui.Checkbox($"{suite.Title}##{suite.Id}", ref ticked)) {
                if (ticked) {
                    this._ticked.Add(suite.Id);
                } else {
                    this._ticked.Remove(suite.Id);
                }
            }

            ImGui.EndDisabled();
            ImGui.SameLine();
            var detail = $"{suite.Checks.Count} checks" + (suite.Note is { } note ? $", {note}" : "") + (lacks is { } why ? $"  ({why})" : "");
            ImGui.TextDisabled(detail);
        }

        chosen = this._service.Suites.Where(suite => this._ticked.Contains(suite.Id) && SuiteLacks(suite, environment) == null).ToList();
        var planned = SelfTestService.Planned(chosen);
        ImGui.TextDisabled($"At most {planned.GetValueOrDefault(SelfTestAction.ChannelMessage) + planned.GetValueOrDefault(SelfTestAction.LocalMessage)} test lines sent, slowly.");
        ImGui.TextDisabled(SelfTestWords.TwoPlayersLater);

        this.DrawLeftovers();
        if (this._service.AskHistory) {
            Widgets.WrappedColoured(Widgets.Warning, SelfTestWords.HistoryAsk);
            if (UiActions.ConfirmButton(SelfTestWords.HistoryDelete)) {
                this._service.AnswerHistory(delete: true);
            }

            ImGui.SameLine();
            if (ImGui.Button(SelfTestWords.HistoryKeep)) {
                this._service.AnswerHistory(delete: false);
            }
        } else if (this._service.FileUnknown) {
            Widgets.WrappedColoured(Widgets.Warning, "The last test's file is damaged: it doesn't say which character it was for, so it can't finish by itself. " +
                                                     "Its settings were put back. Clearing it forgets it; a test channel it left shows above, to remove yourself.");
            if (UiActions.ConfirmButton("Clear it")) {
                this._service.ClearUnknownFile();
            }
        } else if (this._service.CleanUpLeft) {
            Widgets.WrappedColoured(Widgets.Warning, "The last test's clean-up didn't finish (a test channel may be left). It finishes once you're connected with that character.");
            if (ImGui.Button("Clean up again")) {
                this._service.RetryCleanUp();
            }
        }

        ImGui.Separator();
        var cant = this._service.CantStart(chosen);
        ImGui.BeginDisabled(cant != null);
        if (ImGui.Button(SelfTestWords.Start)) {
            try {
                this._service.Start(chosen);
                this._startScreen = false;
                this._note = "";
            } catch (Exception ex) {
                this._error = ex.Message;
            }
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button(SelfTestWords.TwoPlayers)) {
            this._twoPlayers = true;
            this._error = null;
            this._service.NewHostCode();
        }

        if (ImGui.IsItemHovered()) {
            ImGui.SetTooltip(SelfTestWords.TwoPlayersLater);
        }

        ImGui.SameLine();
        if (ImGui.Button(SelfTestWords.Close)) {
            this.IsOpen = false;
        }

        if (cant != null) {
            ImGui.TextDisabled(cant);
        }

        if (this._error != null) {
            Widgets.WrappedColoured(Widgets.Error, this._error);
        }
    }

    /// <summary>Why none of a suite's checks can run now (it is unticked then), or null.</summary>
    private static string? SuiteLacks(SelfTestSuite suite, SelfTestEnvironment? environment) {
        if (environment == null) {
            return null;
        }

        var lacks = suite.Checks.Select(check => environment.Lacks(check.Needs)).ToList();
        return lacks.All(why => why != null) ? lacks[0] : null;
    }

    // ================================================================ two players

    /// <summary>The two-player screen (docs/self-test-plan.md, "Two players: the host" and "the partner"): invite a partner, or be one.</summary>
    private void DrawTwoPlayers() {
        var place = SelfTestService.PlaceNow(out var placeName);
        if (!place.Private) {
            this.DrawNotPrivate(place, placeName);
            return;
        }

        ImGui.TextUnformatted(SelfTestWords.TwoPlayersTitle);
        if (ImGui.RadioButton(SelfTestWords.InviteAPartner, !this._asPartner)) {
            this._asPartner = false;
        }

        ImGui.SameLine();
        if (ImGui.RadioButton(SelfTestWords.BeAPartner, this._asPartner)) {
            this._asPartner = true;
        }

        ImGui.Separator();
        if (this._asPartner) {
            this.DrawBeAPartner();
        } else {
            this.DrawInviteAPartner(place);
        }

        if (this._error != null) {
            Widgets.WrappedColoured(Widgets.Error, this._error);
        }
    }

    private void DrawInviteAPartner(PlaceVerdict place) {
        ImGui.TextWrapped(SelfTestWords.HostIntro);
        ImGui.Spacing();
        ImGui.SetNextItemWidth(-Widgets.ButtonWidth(SelfTestWords.UseMyTarget) - (16 * Widgets.Scale));
        // The name stays in this box and the run file only, while the run lasts: never logged or reported.
        ImGui.InputTextWithHint("##partner", SelfTestWords.PartnerLabel, ref this._partner, 80);
        ImGui.SameLine();
        if (ImGui.Button(SelfTestWords.UseMyTarget) && SelfTestService.TargetName() is { } target) {
            this._partner = target;
        }

        ImGui.TextColored(Widgets.Warning, SelfTestWords.TellThemCode(this._service.HostCode));
        ImGui.TextWrapped(SelfTestWords.HostStarts);
        ImGui.Spacing();
        ImGui.TextUnformatted("What it does:");
        foreach (var line in SelfTestWords.Consent) {
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextWrapped(line);
        }

        ImGui.TextWrapped(SelfTestWords.NoNamesInReport);
        ImGui.TextColored(Widgets.Success, $"You're in: {SelfTestWords.Place(place.Kind)}.  OK");
        if (place.NeedsNobodyNear) {
            Widgets.WrappedColoured(Widgets.Warning, SelfTestWords.HouseVisitors);
        }

        ImGui.Separator();
        var environment = this.Environment();
        foreach (var suite in this._service.PairSuites) {
            var lacks = PairSuiteLacks(suite, environment);
            var ticked = this._pairTicked.Contains(suite.Id) && lacks == null;
            ImGui.BeginDisabled(lacks != null);
            if (ImGui.Checkbox($"{suite.Title}##{suite.Id}", ref ticked)) {
                if (ticked) {
                    this._pairTicked.Add(suite.Id);
                } else {
                    this._pairTicked.Remove(suite.Id);
                }
            }

            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.TextDisabled($"{suite.Checks.Count} checks" + (suite.Note is { } note ? $", {note}" : "") + (lacks is { } why ? $"  ({why})" : ""));
        }

        var chosen = this._service.PairSuites.Where(suite => this._pairTicked.Contains(suite.Id) && PairSuiteLacks(suite, environment) == null).ToList();
        ImGui.Separator();
        var cant = chosen.Count == 0 ? "Tick at least one." : this._service.CantStart(SelfTestSuites.ForHost(chosen), SelfTestRole.Host) ?? this._service.CantPair(this._partner);
        ImGui.BeginDisabled(cant != null);
        if (ImGui.Button(SelfTestWords.Start)) {
            try {
                this._service.StartHost(chosen, this._partner.Trim());
                this._startScreen = false;
                this._twoPlayers = false;
                this._note = "";
                this._error = null;
            } catch (Exception ex) {
                this._error = ex.Message;
            }
        }

        ImGui.EndDisabled();
        this.DrawPairButtons(cant);
    }

    private void DrawBeAPartner() {
        ImGui.SetNextItemWidth(260 * Widgets.Scale);
        ImGui.InputTextWithHint("##host", SelfTestWords.HostLabel, ref this._host, 80);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80 * Widgets.Scale);
        ImGui.InputTextWithHint("##code", SelfTestWords.CodeLabel, ref this._code, 4, ImGuiInputTextFlags.CharsUppercase | ImGuiInputTextFlags.CharsNoBlank);
        ImGui.Spacing();
        ImGui.TextWrapped("While it runs, your plugin answers their test by itself, in their test channel only:");
        foreach (var line in SelfTestWords.PartnerConsent) {
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextWrapped(line);
        }

        ImGui.TextWrapped(SelfTestWords.NoNamesInReport);
        ImGui.Separator();
        var suites = new[] { PartnerSuite.Create() };
        var cant = this._service.CantStart(suites, SelfTestRole.Partner) ?? this._service.CantPair(this._host)
                   ?? (SelfTestRun.IsCode(this._code.Trim().ToUpperInvariant()) ? null : "Type their four-letter code.");
        ImGui.BeginDisabled(cant != null);
        if (ImGui.Button(SelfTestWords.Start)) {
            try {
                this._service.StartPartner(this._host.Trim(), this._code);
                this._startScreen = false;
                this._twoPlayers = false;
                this._note = "";
                this._error = null;
            } catch (Exception ex) {
                this._error = ex.Message;
            }
        }

        ImGui.EndDisabled();
        this.DrawPairButtons(cant);
    }

    private void DrawPairButtons(string? cant) {
        ImGui.SameLine();
        if (ImGui.Button("Back")) {
            this._twoPlayers = false;
            this._error = null;
        }

        ImGui.SameLine();
        if (ImGui.Button(SelfTestWords.Close)) {
            this.IsOpen = false;
        }

        if (cant != null) {
            ImGui.TextDisabled(cant);
        }
    }

    /// <summary>Why none of a two-player suite's checks can run here (the partner aside: pairing comes first), or null.</summary>
    private static string? PairSuiteLacks(SelfTestSuite suite, SelfTestEnvironment? environment) {
        if (environment == null) {
            return null;
        }

        var lacks = suite.Checks.Select(check => environment.Lacks(check.Needs & ~SelfTestNeeds.Partner)).ToList();
        return lacks.All(why => why != null) ? lacks[0] : null;
    }

    private void DrawNotPrivate(PlaceVerdict place, string? placeName) {
        ImGui.TextWrapped(SelfTestWords.OnlyInPrivate);
        foreach (var line in SelfTestWords.PrivatePlaces) {
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextUnformatted(line);
        }

        ImGui.Spacing();
        // The place's own name, for the tester only: never logged or reported.
        ImGui.TextWrapped($"You're in: {(place.Kind is PlaceKind.Elsewhere && !string.IsNullOrEmpty(placeName) ? placeName : SelfTestWords.Place(place.Kind))}.");
        ImGui.Spacing();
        this.DrawLeftovers();
        if (ImGui.Button(SelfTestWords.CheckAgain)) {
            // Read again at the next frame anyway.
            this._readAt = DateTimeOffset.MinValue;
        }

        ImGui.SameLine();
        if (ImGui.Button(SelfTestWords.Close)) {
            this.IsOpen = false;
        }
    }

    private void DrawLeftovers() {
        var leftovers = this._service.Leftovers();
        if (leftovers.Count == 0) {
            return;
        }

        ImGui.Separator();
        ImGui.TextUnformatted(SelfTestWords.LeftoverHeading);
        ImGui.TextDisabled(SelfTestWords.LeftoverExplained);
        foreach (var channel in leftovers) {
            ImGui.TextUnformatted(channel.DisplayName);
            ImGui.SameLine();
            if (UiActions.ConfirmButton($"{SelfTestWords.Remove}##{channel.Id}")) {
                this._service.RemoveLeftover(channel.Id);
            }
        }
    }

    // ================================================================ a run

    private void DrawRun(SelfTestRun run) {
        var view = run.View;
        var elapsed = DateTimeOffset.Now - view.StartedAt;
        ImGui.TextUnformatted($"Run {view.Code}   {view.Finished} of {view.Checks.Count}   {(int) elapsed.TotalMinutes}:{elapsed.Seconds:00}");
        ImGui.SameLine();
        Widgets.SameLineRightIfFits(Widgets.ButtonWidth(SelfTestWords.StopAndCleanUp));
        ImGui.BeginDisabled(view.State == SelfTestState.CleaningUp);
        if (ImGui.Button(SelfTestWords.StopAndCleanUp)) {
            this._service.Stop();
        }

        ImGui.EndDisabled();
        ImGui.TextDisabled(SelfTestWords.ClosingDoesntStop);
        if (view.PairLine is { } pair) {
            // Two players: the other tester's facts, or what this partner answered (fixed words, never a name).
            ImGui.TextWrapped(pair);
        }

        if (view.State == SelfTestState.CleaningUp) {
            Widgets.WrappedColoured(Widgets.Warning, view.Pause == SelfTestWords.ServerSlowDown ? $"{SelfTestWords.ServerSlowDown}. Cleaning up in a minute..." : "Cleaning up...");
        } else if (view.State == SelfTestState.Paused && view.Pause != null) {
            Widgets.WrappedColoured(Widgets.Warning, view.Pause);
        }

        ImGui.Separator();
        var promptHeight = view.Prompt != null ? 230 * Widgets.Scale : 0;
        if (ImGui.BeginChild("##checks", new Vector2(0, -promptHeight), true)) {
            string? suite = null;
            foreach (var check in view.Checks) {
                if (check.Suite != suite) {
                    suite = check.Suite;
                    ImGui.TextUnformatted(suite);
                }

                var (word, colour) = check.Result?.Outcome switch {
                    CheckOutcome.Passed => (SelfTestWords.Passed, Widgets.Success),
                    CheckOutcome.Failed => ("FAILED", Widgets.Error),
                    CheckOutcome.Skipped => (SelfTestWords.Skipped, Widgets.Muted),
                    CheckOutcome.NotRun => (SelfTestWords.NotRunWord, Widgets.Muted),
                    CheckOutcome.Stopped => (SelfTestWords.StoppedWord, Widgets.Warning),
                    _ => check.Current ? (">", Widgets.Text) : (".", Widgets.Muted),
                };
                ImGui.TextColored(colour, $"  {word,-8}");
                ImGui.SameLine();
                ImGui.TextUnformatted(check.Check.Title);
                ImGui.SameLine();
                ImGui.TextDisabled(check.Check.Steps);
                if (check.Result is { Outcome: CheckOutcome.Failed or CheckOutcome.Skipped or CheckOutcome.NotRun or CheckOutcome.Stopped, Why: { } why }) {
                    ImGui.Indent(40 * Widgets.Scale);
                    ImGui.TextWrapped(why);
                    if (check.Result.NotReached.Count > 0) {
                        ImGui.TextDisabled($"Not asked: {string.Join(", ", check.Result.NotReached)}");
                    }
                    ImGui.Unindent(40 * Widgets.Scale);
                }
            }
        }

        ImGui.EndChild();
        if (view.Prompt is { } prompt) {
            this.DrawPrompt(run, view, prompt);
        }
    }

    private void DrawPrompt(SelfTestRun run, SelfTestView view, PromptView prompt) {
        var waiting = view.State == SelfTestState.WaitingToCarryOn;
        ImGui.TextColored(Widgets.Warning, $"{SelfTestWords.NeedsYou} (step {prompt.Step} of {prompt.Steps})");
        ImGui.TextWrapped(prompt.Prompt.Text);
        if (prompt.Prompt.Swatch is { } rgb) {
            ImGui.ColorButton("##swatch", ChannelPalette.OfRgb(rgb), ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoTooltip, new Vector2(48, 20) * Widgets.Scale);
        }

        if (prompt.Watching is { Length: > 0 } watching) {
            ImGui.TextDisabled($"Watching: {watching}");
        }

        if (prompt.Prompt.Input is { } label) {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##input", label, ref this._input, 100);
        }

        if (prompt.Prompt.Kind == PromptKind.SkipOnly) {
            if (ImGui.Button(SelfTestWords.SkipStep)) {
                this.Answer(run, GuidedAnswer.Skip);
            }
        } else if (waiting) {
            if (ImGui.Button(SelfTestWords.CarryOn)) {
                run.CarriedOn();
            }

            ImGui.SameLine();
            if (ImGui.Button(SelfTestWords.SkipStep)) {
                this.Answer(run, GuidedAnswer.Skip);
            }
        } else if (prompt.Prompt.Kind == PromptKind.YesNo) {
            if (ImGui.Button(SelfTestWords.Yes)) {
                this.Answer(run, GuidedAnswer.Yes);
            }

            ImGui.SameLine();
            if (ImGui.Button(SelfTestWords.No)) {
                this.Answer(run, GuidedAnswer.No);
            }

            ImGui.SameLine();
            if (ImGui.Button(SelfTestWords.SkipStep)) {
                this.Answer(run, GuidedAnswer.Skip);
            }
        } else {
            if (ImGui.Button(SelfTestWords.Done)) {
                this.Answer(run, GuidedAnswer.Done);
            }

            ImGui.SameLine();
            if (ImGui.Button(SelfTestWords.DidntWork)) {
                this.Answer(run, GuidedAnswer.DidntWork);
            }

            ImGui.SameLine();
            if (ImGui.Button(SelfTestWords.SkipStep)) {
                this.Answer(run, GuidedAnswer.Skip);
            }
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##note", SelfTestWords.NoteLabel, ref this._note, 200);
        ImGui.TextDisabled(SelfTestWords.NoteReminder);
    }

    private void Answer(SelfTestRun run, GuidedAnswer answer) {
        run.Answer(answer, this._note, this._input);
        this._note = "";
        this._input = "";
    }

    // ================================================================ the report

    private void DrawReport(SelfTestRun run) {
        var view = run.View;
        var results = view.Checks.Select(check => check.Result).OfType<CheckRecord>().ToList();
        ImGui.TextUnformatted($"Run {view.Code}: {results.Count(result => result.Outcome == CheckOutcome.Passed)} passed, " +
                              $"{results.Count(result => result.Outcome == CheckOutcome.Failed)} failed, {results.Count(result => result.Outcome == CheckOutcome.NotRun)} not run, {results.Count(result => result.Outcome == CheckOutcome.Skipped)} skipped." +
                              (view.CleanUp != null ? " Cleaned up." : ""));
        if (this._report is not { } cached || !ReferenceEquals(cached.View, view)) {
            cached = (view, this._service.Report() ?? "");
            this._report = cached;
        }

        var report = cached.Text;
        if (ImGui.Button(SelfTestWords.CopyReport)) {
            ImGui.SetClipboardText(report);
            this._copied = "Copied.";
        }

        ImGui.SameLine();
        // The failed and stopped ones, and those not run where a second run could help (see SelfTestReport.WorthRunningAgain).
        var failed = results.Where(SelfTestReport.WorthRunningAgain).Select(result => result.Check).ToList();
        // Two players: the host runs its failed ones again with the same partner (paired afresh); a partner has nothing to run again.
        var host = view.Role == SelfTestRole.Host;
        var suites = (host ? this._service.PairSuites : this._service.Suites).Where(suite => suite.Checks.Any(check => failed.Contains(check.Id))).ToList();
        var cant = view.Role == SelfTestRole.Partner ? "Your partner starts their run again."
            : failed.Count == 0 ? "Nothing to run again."
            : host ? this._service.CantStart(SelfTestSuites.ForHost(suites), SelfTestRole.Host) ?? this._service.CantPair(this._partner)
            : this._service.CantStart(suites);
        ImGui.BeginDisabled(cant != null);
        if (ImGui.Button(SelfTestWords.RunFailedAgain)) {
            try {
                if (host) {
                    // The name the tester typed (the run forgot it at its end).
                    this._service.StartHost(suites, this._partner.Trim(), failed);
                } else {
                    this._service.Start(suites, failed);
                }
            } catch (Exception ex) {
                this._error = ex.Message;
            }
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
            ImGui.SetTooltip("The checks that failed or stopped, and those not run where another try could help.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Back to the start")) {
            this._startScreen = true;
            this._twoPlayers = false;
            this._copied = null;
        }

        ImGui.SameLine();
        if (ImGui.Button(SelfTestWords.Close)) {
            this.IsOpen = false;
        }

        if (this._copied != null) {
            ImGui.SameLine();
            ImGui.TextDisabled(this._copied);
        }

        if (this._error != null) {
            Widgets.WrappedColoured(Widgets.Error, this._error);
        }

        ImGui.Separator();
        if (ImGui.BeginChild("##report", Vector2.Zero, true)) {
            ImGui.TextWrapped(report);
        }

        ImGui.EndChild();
    }
}
