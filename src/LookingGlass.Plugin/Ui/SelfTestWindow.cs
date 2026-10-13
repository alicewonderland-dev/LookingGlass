using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using LookingGlass.Core.SelfTest;
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
    private string _note = "";
    private string _input = "";
    private string? _copied;
    private string? _error;

    public SelfTestWindow(SelfTestService service) : base($"{SelfTestWords.WindowTitle}###lookingglass-selftest") {
        this._service = service;
        this._ticked = service.Suites.Select(suite => suite.Id).ToHashSet();
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
        ImGui.TextWrapped(SelfTestWords.TwoPlayersLater);

        this.DrawLeftovers();
        if (this._service.CleanUpLeft) {
            Widgets.WrappedColoured(Widgets.Warning, "The last test's clean-up didn't finish (a test channel may be left).");
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
        ImGui.BeginDisabled();
        ImGui.Button(SelfTestWords.TwoPlayers);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
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
                    CheckOutcome.Stopped => (SelfTestWords.StoppedWord, Widgets.Warning),
                    _ => check.Current ? (">", Widgets.Text) : (".", Widgets.Muted),
                };
                ImGui.TextColored(colour, $"  {word,-8}");
                ImGui.SameLine();
                ImGui.TextUnformatted(check.Check.Title);
                ImGui.SameLine();
                ImGui.TextDisabled(check.Check.Steps);
                if (check.Result is { Outcome: CheckOutcome.Failed or CheckOutcome.Skipped or CheckOutcome.Stopped, Why: { } why }) {
                    ImGui.Indent(40 * Widgets.Scale);
                    ImGui.TextWrapped(why);
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

        if (waiting) {
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
                              $"{results.Count(result => result.Outcome == CheckOutcome.Failed)} failed, {results.Count(result => result.Outcome == CheckOutcome.Skipped)} skipped." +
                              (view.CleanUp != null ? " Cleaned up." : ""));
        var report = this._service.Report() ?? "";
        if (ImGui.Button(SelfTestWords.CopyReport)) {
            ImGui.SetClipboardText(report);
            this._copied = "Copied.";
        }

        ImGui.SameLine();
        var failed = results.Where(result => result.Outcome is CheckOutcome.Failed or CheckOutcome.Stopped).Select(result => result.Check).ToList();
        var suites = this._service.Suites.Where(suite => suite.Checks.Any(check => failed.Contains(check.Id))).ToList();
        var cant = failed.Count == 0 ? "Nothing failed." : this._service.CantStart(suites);
        ImGui.BeginDisabled(cant != null);
        if (ImGui.Button(SelfTestWords.RunFailedAgain)) {
            try {
                this._service.Start(suites, failed);
            } catch (Exception ex) {
                this._error = ex.Message;
            }
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Back to the start")) {
            this._startScreen = true;
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
