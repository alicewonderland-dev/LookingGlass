using LookingGlass.Core.SelfTest;
using LookingGlass.Core.SelfTest.Suites;

namespace LookingGlass.Tests;

/// <summary>
/// The one-player suites (docs/self-test-plan.md, "Coverage"), run whole against a fake plugin that does nothing much, with
/// a tester who answers every question: every check ends with a result and the run cleans up, no line is typed outside a
/// private place, every typed line is a test line or a command, a name typed in for a step stays out of the report, and
/// everything the window would show is in plain words.
/// </summary>
public sealed class SelfTestSuitesTests : IDisposable {
    private readonly FakeSelfTestHost _host = new();

    public void Dispose() => this._host.Dispose();

    /// <summary>Every one-player task the plan covers, by its checklist steps, as the report names them.</summary>
    private static readonly string[] Covered = [
        "local-chat 1-3", "local-chat 3a-3c", "local-chat 29", "local-chat 30-31", "local-chat 32-33", "local-chat 34-36", "local-chat 37-39", "local-chat 40-41",
        "windows-only 1-2", "windows-only 5, 7, 8, 9, 12", "windows-only 11", "windows-only 13-16", "windows-only 17", "windows-only 20-21", "windows-only 23, 23b, 23c, 24",
        "channel-windows 17, 17b, 17c, 36, 37", "channel-windows 33, 33b, 33c, 33d, 33e, 33f", "channel-windows 37b; windows-only 6b", "channel-windows 37c-37f", "windows-only 26",
        "custom-colours 3", "custom-colours 4-8", "custom-colours 9", "name-colours 21", "name-colours 23 (first part)", "custom-colours 15-16", "custom-colours 17",
        "context-invites 7-9a", "context-invites 10-11", "context-invites 15, 18 (that part)",
        "chat-links 20", "chat-links 21-23", "chat-links 24-25", "chat-links 30",
        "chat-log 24-25", "chat-log 26-28", "chat-log 30-35, 31b", "chat-log 8-9 (group 2)",
        "settings 1-9", "device-notices 1",
        "local-chat 42-46", "sticky-channel 28", "custom-colours 11-14", "context-invites 19-21", "chat-links 26",
    ];

    [Fact]
    public void EveryCoveredTaskHasACheckAndEachCheckIsWellFormed() {
        var suites = SelfTestSuites.All();
        var checks = suites.SelectMany(suite => suite.Checks).ToList();

        Assert.Equal(Covered.Order(), checks.Select(check => check.Steps).Order());
        Assert.Equal(checks.Count, checks.Select(check => check.Id).Distinct().Count());
        Assert.Equal(suites.Count, suites.Select(suite => suite.Id).Distinct().Count());
        Assert.All(checks, check => Assert.NotEmpty(check.StepList));
        Assert.All(suites, suite => Assert.All(suite.Planned, planned => Assert.True(planned.Value <= SelfTestPacer.Rules[planned.Key].RunMost)));
        // At most three test channels a run.
        Assert.True(SelfTestPacer.Rules[SelfTestAction.CreateChannel].RunMost == 3);
    }

    [Fact]
    public async Task EverySuiteRunsToTheEndTypingOnlyTestLinesInAPrivatePlace() {
        var suites = SelfTestSuites.All();
        var pacer = new SelfTestPacer(() => this._host.Now, this._host.Delay);
        var data = SelfTestRun.NewData("K7Q2", 42, "wss://example.test/ws", this._host.Now, suites);
        var run = new SelfTestRun(this._host, suites, pacer, this._host.File, data);
        var shown = new HashSet<string>();
        this._host.BeforeType = () => this._host.RecordIfOutside("line");
        this._host.OnTick = () => {
            var view = run.View;
            if (view.Prompt is { } prompt) {
                shown.Add(prompt.Prompt.Text);
            }

            if (view.Pause is { } pause) {
                shown.Add(pause);
            }

            switch (view.State) {
                case SelfTestState.WaitingToCarryOn:
                    this._host.Logins++;
                    break;
                case SelfTestState.NeedsYou when view.Prompt is { } waiting:
                    run.Answer(waiting.Prompt.Kind == PromptKind.YesNo ? GuidedAnswer.Yes : GuidedAnswer.Done, input: waiting.Prompt.Input != null ? "Alice Liddell@Twintania" : null);
                    break;
            }
        };

        await run.RunAsync();

        Assert.Equal(SelfTestState.Done, run.View.State);
        Assert.True(suites.Sum(suite => suite.Checks.Count) == run.Data.Results.Count,
            string.Join("; ", run.Data.Results.Select(result => $"{result.Check}: {result.Outcome} {result.Why}")));
        Assert.Empty(this._host.TypedOutside);
        Assert.All(this._host.Typed, typed => Assert.True(PrivateArea.IsPrivate(typed.Place)));
        Assert.All(this._host.Typed, typed => Assert.True(typed.Line.Length == 0 || typed.Line.StartsWith('/') || typed.Line.StartsWith(run.MarkerPrefix, StringComparison.Ordinal),
            $"Typed something that isn't a test line or a command: {typed.Line}"));
        // Nothing typed to a channel no test should reach.
        Assert.DoesNotContain(this._host.Typed, typed => typed.Line.StartsWith("/sh ", StringComparison.Ordinal) || typed.Line.StartsWith("/fc ", StringComparison.Ordinal)
            || typed.Line.StartsWith("/y ", StringComparison.Ordinal) || typed.Line.StartsWith("/t ", StringComparison.Ordinal) || typed.Line.StartsWith("/p ", StringComparison.Ordinal));
        Assert.Contains("Alice Liddell@Twintania", this._host.Forbidden);

        // The clean-up: the test channels left, every setting back.
        Assert.False(this._host.File.Exists);
        Assert.All(Enum.GetValues<SelfTestSetting>(), setting => Assert.Equal(this._host.Before[setting], this._host.Settings[setting].GetRawText()));
        Assert.Empty(this._host.Channels);

        // The report has no name, even one typed in for a step.
        var report = SelfTestReport.Build(new SelfTestReportSetup("0.2.17", false, false, PlaceKind.InnRoom), run.View.StartedAt, run.View.EndedAt, this._host.TimeZone,
            run.View.Checks.Where(check => check.Result != null).Select(check => (check.Check, check.Result!)), run.View.CleanUp,
            [.. this._host.Forbidden, run.MarkerPrefix, "K7Q2"]);
        Assert.DoesNotContain("Alice", report);
        Assert.DoesNotContain("Twintania", report);
        Assert.DoesNotContain("K7Q2", report);

        // Plain words: every question, every pause, and every reason and part not run.
        var words = shown.Concat(run.Data.Results.SelectMany(result => result.NotRun.Append(result.Why ?? "")))
            .Concat(suites.SelectMany(suite => suite.Checks).SelectMany(check => check.StepList).Where(step => step.Kind != StepKind.CarryOnAfter).Select(step => step.Title))
            .Concat(suites.Select(suite => suite.Title)).Where(text => text.Length > 0);
        Assert.All(words, text => Assert.True(PlainLanguage.Jargon(text).Count == 0, $"Not plain: {text}"));
        Assert.All(shown, text => Assert.DoesNotContain("{", text));
    }

    [Fact]
    public async Task ASuiteThatCantRunHereIsSkippedWithWhy() {
        var suites = new[] { ChatTwoSuite.Create() };
        this._host.ChatTwo = false;
        var run = new SelfTestRun(this._host, suites, new SelfTestPacer(() => this._host.Now, this._host.Delay), this._host.File,
            SelfTestRun.NewData("ABCD", 1, "x", this._host.Now, suites));

        await run.RunAsync();

        Assert.All(run.Data.Results, result => Assert.Equal((CheckOutcome.Skipped, SelfTestWords.NeedsChatTwo), (result.Outcome, result.Why)));
        Assert.Empty(this._host.Typed);
    }

    [Fact]
    public async Task OnlyTheFailedOnesRunAgain() {
        var suites = new[] { ColoursSuite.Create() };
        var data = SelfTestRun.NewData("ABCD", 1, "x", this._host.Now, suites, ["custom-colours-3"]);
        var run = new SelfTestRun(this._host, suites, new SelfTestPacer(() => this._host.Now, this._host.Delay), this._host.File, data);

        await run.RunAsync();

        Assert.Equal(["custom-colours-3"], run.Data.Results.Select(result => result.Check));
        Assert.Single(run.View.Checks);
    }
}
