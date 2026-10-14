using LookingGlass.Core.SelfTest;
using LookingGlass.Core.SelfTest.Suites;

namespace LookingGlass.Tests;

/// <summary>
/// When a check passed (owner, 2026-10-13): only if at least one of its real steps passed. Setup steps (making a test
/// channel, a recruitment, a relog) and "the rest" notes never make it pass; a check whose real steps were all skipped is
/// "not run", with why, in the report, the window and the log line, and is offered again where running it again could help.
/// </summary>
public sealed class SelfTestOutcomeTests : IDisposable {
    private readonly FakeSelfTestHost _host = new();

    public void Dispose() => this._host.Dispose();

    private static SelfTestStep Real(string title, StepResult result) =>
        new() { Title = title, Kind = StepKind.Automatic, Checklist = "1", Run = _ => Task.FromResult(result) };

    private static SelfTestStep Setup(string title, StepResult result) => Real(title, result) with { Setup = true };

    private async Task<CheckRecord> RunAsync(params SelfTestStep[] steps) {
        var suite = new SelfTestSuite("s", "S", [new SelfTestCheck { Id = "a", Title = "a", Steps = "test 1-2", Mode = CheckMode.Automatic, StepList = steps }]);
        var run = new SelfTestRun(this._host, [suite], new SelfTestPacer(() => this._host.Now, this._host.Delay), this._host.File,
            SelfTestRun.NewData("K7Q2", 1, "x", this._host.Now, [suite]));
        await run.RunAsync();
        return run.Data.Results.Single();
    }

    [Fact]
    public async Task EveryRealStepSkippedIsNotRunWithWhyNeverPassed() {
        var record = await this.RunAsync(
            Setup("Who to try", StepResult.Pass()),
            Real("They aren't registered", StepResult.Skip(SelfTestWords.Registered)),
            Real("Simple mode", StepResult.Skip(SelfTestWords.Registered)),
            SelfTestSteps.Rest("2", "the same from ChatTwo's menu"));

        Assert.Equal(CheckOutcome.NotRun, record.Outcome);
        Assert.Equal(SelfTestWords.Registered, record.Why);
        Assert.Equal($"test 1-2: not run: {SelfTestWords.Registered}", SelfTestReport.Line("test 1-2", record, TimeZoneInfo.Utc));
        Assert.Equal("[selftest] test 1-2: not run", SelfTestReport.LogLine("test 1-2", record));
        Assert.Contains("[selftest] test 1-2: not run", this._host.Logged);
    }

    [Fact]
    public async Task OneRealStepPassedIsAPass() {
        var record = await this.RunAsync(
            Setup("A recruitment of your own", StepResult.Pass()),
            Real("One", StepResult.Pass()),
            Real("Two", StepResult.Skip(SelfTestWords.YouSkipped)));

        Assert.Equal(CheckOutcome.Passed, record.Outcome);
        Assert.Contains($"two: {SelfTestWords.YouSkipped}", record.NotRun);
    }

    [Fact]
    public async Task ASetupThatEndsTheCheckGivesItsWhy() {
        var record = await this.RunAsync(
            Setup("A recruitment of your own", StepResult.SkipCheck("needs a recruitment of your own")),
            Real("One", StepResult.Pass()));

        Assert.Equal(CheckOutcome.NotRun, record.Outcome);
        Assert.Equal("needs a recruitment of your own", record.Why);
    }

    [Fact]
    public async Task DifferentReasonsAreAllGiven() {
        var record = await this.RunAsync(Real("One", StepResult.Skip(SelfTestWords.YouSkipped)), Real("Two", StepResult.Skip(SelfTestWords.TimedOut)));

        Assert.Equal(CheckOutcome.NotRun, record.Outcome);
        Assert.Equal($"{SelfTestWords.YouSkipped}; {SelfTestWords.TimedOut}", record.Why);
    }

    [Fact]
    public async Task AFailureIsStillAFailure() {
        var record = await this.RunAsync(Setup("Set up", StepResult.Pass()), Real("One", StepResult.Fail("it went to Say")));

        Assert.Equal(CheckOutcome.Failed, record.Outcome);
    }

    [Fact]
    public void NotRunIsOfferedAgainWhereRunningItAgainCouldHelp() {
        CheckRecord Of(CheckOutcome outcome, string? why = null) => new() { Check = "a", Outcome = outcome, Why = why };

        Assert.True(SelfTestReport.WorthRunningAgain(Of(CheckOutcome.Failed, "x")));
        Assert.True(SelfTestReport.WorthRunningAgain(Of(CheckOutcome.Stopped, SelfTestWords.StoppedByYou)));
        Assert.True(SelfTestReport.WorthRunningAgain(Of(CheckOutcome.NotRun, SelfTestWords.Registered)));
        Assert.True(SelfTestReport.WorthRunningAgain(Of(CheckOutcome.NotRun, $"{SelfTestWords.NeedsChatTwo}; {SelfTestWords.YouSkipped}")));
        // Nothing a second run changes by itself.
        Assert.False(SelfTestReport.WorthRunningAgain(Of(CheckOutcome.NotRun, SelfTestWords.NeedsChatTwo)));
        Assert.False(SelfTestReport.WorthRunningAgain(Of(CheckOutcome.NotRun, SelfTestWords.HistoryWasYours)));
        Assert.False(SelfTestReport.WorthRunningAgain(Of(CheckOutcome.Passed)));
        Assert.False(SelfTestReport.WorthRunningAgain(Of(CheckOutcome.Skipped, SelfTestWords.NeedsChatTwo)));
    }

    [Fact]
    public async Task InvitingSomeoneRegisteredIsNotRunNotPassed() {
        // The owner's first run: the name given uses LookingGlass, so nothing was sent and nothing was tested.
        this._host.Registered = true;
        var suites = new[] { InvitesSuite.Create() };
        var data = SelfTestRun.NewData("K7Q2", 1, "x", this._host.Now, suites, ["context-invites-15-18"]);
        var run = new SelfTestRun(this._host, suites, new SelfTestPacer(() => this._host.Now, this._host.Delay), this._host.File, data);
        this._host.OnTick = () => {
            if (run.View.Prompt is { } prompt) {
                run.Answer(GuidedAnswer.Done, input: prompt.Prompt.Input != null ? "Alice Liddell@Twintania" : null);
            }
        };

        await run.RunAsync();

        var record = run.Data.Results.Single();
        Assert.Equal(CheckOutcome.NotRun, record.Outcome);
        Assert.Equal(SelfTestWords.Registered, record.Why);
    }

    [Fact]
    public void EverySuiteHasARealStepInEachCheck() {
        foreach (var check in SelfTestSuites.Every().SelectMany(suite => suite.Checks)) {
            Assert.True(check.StepList.Any(step => !step.Setup && step.Kind != StepKind.CarryOnAfter), $"{check.Id} has nothing but setup steps");
        }
    }
}
