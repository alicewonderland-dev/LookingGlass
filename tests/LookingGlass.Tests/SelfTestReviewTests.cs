using System.Text.Json;
using LookingGlass.Core.SelfTest;
using LookingGlass.Core.SelfTest.Suites;

namespace LookingGlass.Tests;

/// <summary>
/// The self-test review's findings (2026-10-13), each as a test: lines the tester is asked to type go through the typing
/// gate and come off the screen when it closes; ChatTwo is never left on Party; the history suite never keeps the tester's
/// real channels' lines; the game's chat channel comes back after a reload, never as a tell; a clean-up while disconnected
/// keeps what it couldn't do; a registered person is never invited; another character is never acted on; a reload keeps the
/// run's pace and its steps' results; the report's fixed words aren't scrubbed by a channel's name.
/// </summary>
public sealed class SelfTestReviewTests : IDisposable {
    private readonly FakeSelfTestHost _host = new();

    public void Dispose() => this._host.Dispose();

    private SelfTestRun NewRun(SelfTestRunData? resume, params SelfTestSuite[] suites) {
        var data = resume ?? SelfTestRun.NewData("K7Q2", 42, "wss://example.test/ws", this._host.Now, suites);
        return new SelfTestRun(this._host, suites, new SelfTestPacer(() => this._host.Now, this._host.Delay, this._host.File.ReadSpent(), data.Spent), this._host.File,
            data, resume != null);
    }

    private static SelfTestSuite Suite(params SelfTestCheck[] checks) => new("suite", "Suite", checks);

    private static SelfTestCheck Check(string id, params SelfTestStep[] steps) => new() { Id = id, Title = id, Steps = $"test {id}", Mode = CheckMode.Automatic, StepList = steps };

    private static SelfTestStep Auto(string title, Func<StepContext, Task<StepResult>> run, bool types = false) =>
        new() { Title = title, Kind = StepKind.Automatic, Types = types, Run = run };

    private static SelfTestStep TypeStep() => Auto("type", async context => {
        await context.TypeAsync(context.NextMarker());
        return StepResult.Pass();
    }, types: true);

    private static readonly SelfTestStep Reload = new() { Title = "Turn LookingGlass off and on", Kind = StepKind.CarryOnAfter, Run = _ => Task.FromResult(StepResult.Pass()) };

    // ---------------------------------------------------------------- H1: guided typing

    [Fact]
    public async Task NoLineToTypeIsShownWhileSomeoneIsNearInAHouse() {
        // The reviewer's probe: the ChatTwo suite in a house, with a visitor near for a while.
        this._host.Place = FakeSelfTestHost.House;
        this._host.Others = 1;
        this._host.ChatTwo = true;
        var run = this.NewRun(null, ChatTwoSuite.Create());
        var shownWhileNear = new List<string>();
        var shownTyping = 0;
        var ticks = 0;
        this._host.OnTick = () => {
            if (++ticks == 200) {
                this._host.Others = 0;
            }

            var view = run.View;
            if (view.State == SelfTestState.WaitingToCarryOn) {
                this._host.Logins++;
            }

            if (view is { State: SelfTestState.NeedsYou, Prompt: { } waiting }) {
                if (waiting.Prompt.TesterTypes) {
                    shownTyping++;
                    if (this._host.Others > 0) {
                        shownWhileNear.Add(waiting.Prompt.Text);
                    }
                }

                run.Answer(waiting.Prompt.Kind == PromptKind.YesNo ? GuidedAnswer.Yes : GuidedAnswer.Done);
            }
        };

        await run.RunAsync();

        Assert.Empty(shownWhileNear);
        Assert.True(shownTyping > 0);
        Assert.Equal(SelfTestState.Done, run.View.State);
    }

    [Fact]
    public async Task LeavingWhileALineIsAskedForTakesItOffTheScreenUntilBack() {
        var prompt = new GuidedPrompt("In the game's chat box, type: /s LG self-test K7Q2 1, please ignore") { TesterTypes = true };
        SelfTestRun? run = null;
        run = this.NewRun(null, Suite(Check("a", SelfTestSteps.Ask("type it", null, prompt))));
        var phase = 0;
        var pausedWithoutPrompt = false;
        this._host.GameChannel = 6;
        this._host.OnTick = () => {
            var view = run.View;
            switch (phase) {
                case 0 when view is { State: SelfTestState.NeedsYou, Prompt: not null }:
                    // Say first, before the line is asked for.
                    Assert.Equal(SelfTestGameChannels.Say, this._host.GameChannel);
                    this._host.Place = FakeSelfTestHost.Ward;
                    phase = 1;
                    break;
                case 1 when view.State == SelfTestState.Paused:
                    pausedWithoutPrompt = view.Prompt == null && view.Pause == SelfTestWords.PausedLeft;
                    this._host.Place = FakeSelfTestHost.InnRoom;
                    phase = 2;
                    break;
                case 2 when view is { State: SelfTestState.NeedsYou, Prompt: not null }:
                    run.Answer(GuidedAnswer.Done);
                    phase = 3;
                    break;
            }
        };

        await run.RunAsync();

        Assert.True(pausedWithoutPrompt, "The line to type stayed on the screen outside the private place.");
        Assert.Equal(3, phase);
        Assert.Equal(CheckOutcome.Passed, run.Data.Results.Single().Outcome);
        // Put back at the end.
        Assert.Equal(6, this._host.GameChannel);
    }

    [Fact]
    public async Task ChatTwoIsAskedOntoSayFirstAndNeverLeftOffIt() {
        this._host.ChatTwo = true;
        this._host.ChatTwoInput = 14;
        var run = this.NewRun(null, ChatTwoSuite.Create());
        var typedOffSay = 0;
        var askedOntoSay = 0;
        var backToSay = 0;
        var moved = 0;
        this._host.OnTick = () => {
            var view = run.View;
            if (view.State == SelfTestState.WaitingToCarryOn) {
                this._host.Logins++;
            }

            if (view is not { State: SelfTestState.NeedsYou, Prompt: { } waiting }) {
                return;
            }

            var text = waiting.Prompt.Text;
            if (text == SelfTestWords.ChatTwoToSay) {
                askedOntoSay++;
                this._host.ChatTwoInput = SelfTestGameChannels.ChatTwoSay;
                return;
            }

            if (text.StartsWith("Put ChatTwo's input back on Say", StringComparison.Ordinal)) {
                backToSay++;
                this._host.ChatTwoInput = SelfTestGameChannels.ChatTwoSay;
                return;
            }

            if (waiting.Prompt is { TesterTypes: true, ChatTwoOnSay: true } && this._host.ChatTwoInput != SelfTestGameChannels.ChatTwoSay) {
                typedOffSay++;
            }

            // The tester does as asked: Party, a tell, or another channel in the picker.
            if (text.Contains("to Party", StringComparison.Ordinal) || text.StartsWith("Pick Party", StringComparison.Ordinal)) {
                this._host.ChatTwoInput = 14;
                moved++;
            } else if (text.Contains("on a tell", StringComparison.Ordinal)) {
                this._host.ChatTwoInput = 12;
                moved++;
            }

            run.Answer(waiting.Prompt.Kind == PromptKind.YesNo ? GuidedAnswer.Yes : GuidedAnswer.Done);
        };

        await run.RunAsync();

        Assert.Equal(0, typedOffSay);
        Assert.True(askedOntoSay >= 1);
        // Every time a step had it moved off Say (even a step that failed), it was asked back before anything else.
        Assert.True(moved >= 1);
        Assert.Equal(moved, backToSay);
        Assert.Equal(SelfTestGameChannels.ChatTwoSay, this._host.ChatTwoInput);
    }

    // ---------------------------------------------------------------- H2, M1: the chat history

    private void AnswerEverything(SelfTestRun run) => this._host.OnTick = () => {
        var view = run.View;
        if (view.State == SelfTestState.WaitingToCarryOn) {
            this._host.Logins++;
        }

        if (view is { State: SelfTestState.NeedsYou, Prompt: { } waiting }) {
            run.Answer(waiting.Prompt.Kind == PromptKind.YesNo ? GuidedAnswer.Yes : GuidedAnswer.Done);
        }
    };

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task TheHistorySuiteDoesNothingToAHistoryThatWasntOffAndEmpty(bool on, bool empty) {
        this._host.HistoryOn = on;
        this._host.HistoryEmpty = empty;
        var run = this.NewRun(null, HistorySuite.Create());
        this.AnswerEverything(run);

        await run.RunAsync();

        Assert.All(run.Data.Results, result => Assert.Equal((CheckOutcome.Skipped, SelfTestWords.HistoryWasYours), (result.Outcome, result.Why)));
        Assert.DoesNotContain("write:KeepHistory", this._host.Ops);
        Assert.DoesNotContain("write:KeepHistoryUnencrypted", this._host.Ops);
        Assert.Equal(0, this._host.Deleted);
    }

    [Fact]
    public async Task OffAndEmptyTheWholeHistoryIsDeletedOnceItIsOffAgain() {
        this._host.HistoryEmpty = true;
        var run = this.NewRun(null, HistorySuite.Create());
        this.AnswerEverything(run);

        await run.RunAsync();

        Assert.Contains("write:KeepHistory", this._host.Ops);
        // Deleted after the setting is back off, so nothing more is recorded; and the test's lines aren't forgotten one by one.
        Assert.True(this._host.Ops.LastIndexOf("write:KeepHistory") < this._host.Ops.IndexOf("delete-history"));
        Assert.Equal(1, this._host.Deleted);
        Assert.Empty(this._host.Forgotten);
        Assert.False(this._host.File.Exists);
    }

    [Fact]
    public void AfterACrashAnOffAndEmptyHistoryIsDeletedAtTheNextStart() {
        var lastActive = this._host.Now;
        var data = new SelfTestRunData { Code = "K7Q2", ContentId = 42, ServerUrl = "x", HistoryWasOffAndEmpty = true, TestChannels = ["c1"], LastActive = lastActive };
        data.Settings[SelfTestSetting.KeepHistory] = JsonSerializer.SerializeToElement(false);
        var settings = new MapSettings();
        settings.Values[SelfTestSetting.KeepHistory] = JsonSerializer.SerializeToElement(true);
        var deleted = 0;
        IReadOnlyList<(string, DateTimeOffset)> folders = [("chatlog-a", lastActive.AddMinutes(-5))];

        var done = SelfTestRecovery.PutBack(data, settings, () => folders, _ => deleted++);

        Assert.Equal(1, deleted);
        Assert.False(settings.Values[SelfTestSetting.KeepHistory].GetBoolean());
        Assert.False(data.HistoryWasOffAndEmpty);
        // The test channel is left once connected.
        Assert.False(done);

        // A history that was the tester's is never deleted.
        var theirs = new SelfTestRunData { Code = "K7Q2", ContentId = 42, ServerUrl = "x" };
        Assert.True(SelfTestRecovery.PutBack(theirs, settings, () => folders, _ => deleted++));
        Assert.Equal(1, deleted);
    }

    [Fact]
    public void ADamagedFileThatLostWhoseItWasPutsBackNoWindows() {
        var data = new SelfTestRunData();
        data.Settings[SelfTestSetting.ChannelWindows] = JsonSerializer.SerializeToElement(Array.Empty<object>());
        data.Settings[SelfTestSetting.WindowsOnly] = JsonSerializer.SerializeToElement(false);
        var settings = new MapSettings();

        SelfTestRecovery.PutBack(data, settings, () => [], _ => { });

        Assert.True(SelfTestRecovery.Unknown(data));
        Assert.False(settings.Values.ContainsKey(SelfTestSetting.ChannelWindows));
        Assert.True(settings.Values.ContainsKey(SelfTestSetting.WindowsOnly));
    }

    private sealed class MapSettings : ISelfTestSettings {
        public Dictionary<SelfTestSetting, JsonElement> Values { get; } = new();

        public JsonElement Read(SelfTestSetting setting) => this.Values[setting];

        public void Write(SelfTestSetting setting, JsonElement value) => this.Values[setting] = value.Clone();
    }

    // ---------------------------------------------------------------- M2: the game's chat channel

    [Fact]
    public async Task TheChatChannelComesBackAfterAReload() {
        // The reviewer's probe.
        this._host.GameChannel = 6;
        var suite = Suite(Check("a", TypeStep(), Reload, TypeStep()));
        var run = this.NewRun(null, suite);
        this._host.OnTick = () => {
            if (run.View.State == SelfTestState.WaitingToCarryOn) {
                run.Abandon();
            }
        };
        await run.RunAsync();
        Assert.Equal(SelfTestGameChannels.Say, this._host.GameChannel);

        var saved = this._host.File.Read(out _)!;
        Assert.Equal(6, saved.GameChannelBefore);
        this._host.OnTick = null;
        await this.NewRun(saved, suite).RunAsync();

        Assert.Equal(6, this._host.GameChannel);
    }

    [Fact]
    public async Task ATellIsNeverPutBackItStaysOnSayAndSaysSo() {
        this._host.GameChannel = 17;
        var run = this.NewRun(null, Suite(Check("a", TypeStep())));

        await run.RunAsync();

        Assert.Equal(SelfTestGameChannels.Say, this._host.GameChannel);
        Assert.Contains("left on Say", run.View.CleanUp);
    }

    [Fact]
    public void LinkshellsAreSwitchedToByTheirIndex() {
        Assert.Equal(0u, SelfTestGameChannels.LinkshellIndex(19));
        Assert.Equal(7u, SelfTestGameChannels.LinkshellIndex(26));
        Assert.Equal(0u, SelfTestGameChannels.LinkshellIndex(9));
        Assert.Equal(7u, SelfTestGameChannels.LinkshellIndex(16));
        Assert.Equal(0u, SelfTestGameChannels.LinkshellIndex(6));
        Assert.All(new[] { 0, 17, 18 }, tell => Assert.True(SelfTestGameChannels.IsTell(tell)));
        Assert.False(SelfTestGameChannels.IsTell(1));
    }

    // ---------------------------------------------------------------- M3: a clean-up while disconnected

    private static SelfTestCheck DisconnectedThenAsked() => Check("a",
        Auto("make", async context => {
            await context.TestChannelAsync();
            return StepResult.Pass();
        }),
        Auto("disconnect", async context => {
            await context.DisconnectAsync();
            return StepResult.Pass();
        }),
        SelfTestSteps.Ask("wait", null, new GuidedPrompt("Look")));

    [Fact]
    public async Task StoppedWhileTheRunHadDisconnectedItConnectsAgainAndCleansUp() {
        SelfTestRun? run = null;
        run = this.NewRun(null, Suite(DisconnectedThenAsked()));
        this._host.OnTick = () => {
            if (run.View.State == SelfTestState.NeedsYou) {
                run.Stop();
            }
        };

        await run.RunAsync();

        Assert.True(this._host.Ready);
        Assert.Contains("connect", this._host.Ops);
        Assert.Single(this._host.Left);
        Assert.False(this._host.File.Exists);
    }

    [Fact]
    public async Task NotConnectedAtTheEndTheChannelsAndTheFileStay() {
        this._host.ConnectFails = true;
        SelfTestRun? run = null;
        run = this.NewRun(null, Suite(DisconnectedThenAsked()));
        this._host.OnTick = () => {
            if (run.View.State == SelfTestState.NeedsYou) {
                run.Stop();
            }
        };

        await run.RunAsync();

        Assert.Empty(this._host.Left);
        Assert.True(this._host.File.Exists);
        var saved = this._host.File.Read(out _)!;
        Assert.Single(saved.TestChannels);
        Assert.Equal(SelfTestPhase.Running, saved.Phase);
        Assert.Contains("left once you're connected again", run.View.CleanUp);
        // Settings are this computer's: put back anyway.
        Assert.Empty(saved.Settings);
    }

    [Fact]
    public async Task AHistoryThatCouldntBeClearedKeepsTheFile() {
        this._host.ForgetFails = new IOException("busy");
        var run = this.NewRun(null, Suite(Check("a", Auto("make", async context => {
            await context.TestChannelAsync();
            return StepResult.Pass();
        }))));

        await run.RunAsync();

        Assert.True(this._host.File.Exists);
        Assert.Single(this._host.File.Read(out _)!.TestChannels);
    }

    // ---------------------------------------------------------------- M4: invites

    private async Task<SelfTestRun> InviteRunAsync() {
        this._host.Settings[SelfTestSetting.AdvancedMode] = JsonSerializer.SerializeToElement(false);
        var suite = new SelfTestSuite("invites", "Invites", [InvitesSuite.Create().Checks.Single(check => check.Id == "context-invites-15-18")]);
        var run = this.NewRun(null, suite);
        this._host.OnTick = () => {
            if (run.View is { State: SelfTestState.NeedsYou, Prompt: { } waiting }) {
                run.Answer(GuidedAnswer.Done, input: waiting.Prompt.Input != null ? "Alice Liddell@Twintania" : null);
            }
        };
        await run.RunAsync();
        return run;
    }

    [Fact]
    public async Task SomeoneRegisteredIsLookedUpAndNeverInvited() {
        this._host.Registered = true;

        var run = await this.InviteRunAsync();

        Assert.Empty(this._host.Invited);
        Assert.Equal(1, this._host.Lookups);
        Assert.Contains(run.Data.Results.Single().NotRun, part => part.EndsWith(SelfTestWords.Registered, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SomeoneNotRegisteredGetsTheLineInBothModes() {
        var run = await this.InviteRunAsync();

        Assert.Equal(2, this._host.Invited.Count);
        Assert.Equal(CheckOutcome.Passed, run.Data.Results.Single().Outcome);
        Assert.Empty(this._host.Disbanded);
    }

    [Fact]
    public async Task AnInviteThatWentAnywayIsNotRepeatedAndTheChannelIsDisbanded() {
        // Registered between the lookup and the invite.
        this._host.Settings[SelfTestSetting.AdvancedMode] = JsonSerializer.SerializeToElement(false);
        var suite = new SelfTestSuite("invites", "Invites", [InvitesSuite.Create().Checks.Single(check => check.Id == "context-invites-15-18")]);
        var run = this.NewRun(null, suite);
        this._host.OnTick = () => {
            if (run.View is { State: SelfTestState.NeedsYou, Prompt: { } waiting }) {
                run.Answer(GuidedAnswer.Done, input: "Alice Liddell@Twintania");
            }

            if (this._host.Lookups == 1) {
                this._host.Registered = true;
            }
        };

        await run.RunAsync();

        Assert.Single(this._host.Invited);
        Assert.Single(this._host.Disbanded);
        Assert.Empty(this._host.Left);
    }

    // ---------------------------------------------------------------- M5: another character

    [Fact]
    public async Task AfterARelogOntoAnotherCharacterNothingIsDoneUntilTheRunsOwnIsBack() {
        var acted = new List<ulong?>();
        var relog = new SelfTestStep { Title = SelfTestSteps.Relog, Kind = StepKind.CarryOnAfter, Run = _ => Task.FromResult(StepResult.Pass()) };
        var run = this.NewRun(null, Suite(Check("a", relog, Auto("act", async context => {
            acted.Add(this._host.Character);
            await context.ChangeSettingAsync(SelfTestSetting.WindowsOnly, true);
            return StepResult.Pass();
        }))));
        var paused = false;
        var ticks = 0;
        this._host.OnTick = () => {
            var view = run.View;
            if (view.State == SelfTestState.WaitingToCarryOn) {
                this._host.Character = 99;
                this._host.Logins++;
            } else if (view.Pause == SelfTestWords.PausedOtherCharacter) {
                paused = true;
                if (++ticks == 20) {
                    this._host.Character = 42;
                }
            }
        };

        await run.RunAsync();

        Assert.True(paused);
        Assert.Equal([(ulong?) 42], acted);
        Assert.Equal((42UL, "wss://example.test/ws"), this._host.RanFor);
    }

    // ---------------------------------------------------------------- lows

    [Fact]
    public async Task AReloadKeepsTheRunsPaceItsMostAndItsStepsResults() {
        var suite = Suite(Check("a",
            Auto("make three", async context => {
                await context.TestChannelAsync(0);
                await context.TestChannelAsync(1);
                await context.TestChannelAsync(2);
                return StepResult.Pass();
            }),
            Auto("a part not run", _ => Task.FromResult(StepResult.Skip(SelfTestWords.NeedsStranger))),
            Reload,
            Auto("one more channel", async context => {
                await context.ServerAsync(SelfTestAction.CreateChannel, () => Task.CompletedTask);
                return StepResult.Pass();
            })));
        var run = this.NewRun(null, suite);
        this._host.OnTick = () => {
            if (run.View.State == SelfTestState.WaitingToCarryOn) {
                run.Abandon();
            }
        };
        await run.RunAsync();
        var saved = this._host.File.Read(out _)!;
        Assert.Equal(3, saved.Spent.Count(spend => spend.Action == SelfTestAction.CreateChannel));
        Assert.Equal(2, saved.InProgress.Count);

        this._host.OnTick = null;
        var resumed = this.NewRun(saved, suite);
        await resumed.RunAsync();

        var result = resumed.Data.Results.Single();
        // Still at its most after the reload: the fourth channel isn't asked for.
        Assert.Contains($"one more channel: {SelfTestWords.RunSpent}", result.NotRun);
        // The step before the reload kept its own result.
        Assert.Contains($"a part not run: {SelfTestWords.NeedsStranger}", result.NotRun);
    }

    [Fact]
    public async Task LeavingInTheMiddleOfAnAutomaticStepPausesIt() {
        var sawPause = false;
        var run = this.NewRun(null, Suite(Check("a", Auto("wait", async context => {
            var held = await context.WaitUntilAsync(() => Task.FromResult(sawPause && this._host.Place == FakeSelfTestHost.InnRoom), TimeSpan.FromSeconds(20));
            return StepResult.Expect(held, "it didn't come back");
        }))));
        var ticks = 0;
        this._host.OnTick = () => {
            if (++ticks == 3) {
                this._host.Place = FakeSelfTestHost.Ward;
            }

            if (run.View is { State: SelfTestState.Paused, Pause: SelfTestWords.PausedLeft }) {
                sawPause = true;
                // Gone a long while: the wait doesn't count it.
                if (ticks > 200) {
                    this._host.Place = FakeSelfTestHost.InnRoom;
                }
            }
        };

        await run.RunAsync();

        Assert.True(sawPause);
        Assert.Equal(CheckOutcome.Passed, run.Data.Results.Single().Outcome);
    }

    [Fact]
    public void TheReportScrubsChannelNamesFromNotesOnly() {
        var check = Check("a", Auto("x", _ => Task.FromResult(StepResult.Pass())));
        var record = new CheckRecord { Check = "a", Outcome = CheckOutcome.Skipped, Why = SelfTestWords.HistoryWasYours, Notes = ["the chat channel Sky Pirates flickered"] };

        var report = SelfTestReport.Build(new SelfTestReportSetup("1", false, false, PlaceKind.InnRoom), this._host.Now, null, this._host.TimeZone, [(check, record)], null,
            ["Alice Liddell"], ["chat", "Sky Pirates"]);

        // A channel called "chat" doesn't touch the fixed words ("chat history"), but no channel's name is in a note.
        Assert.Contains("chat history", report);
        Assert.DoesNotContain("Sky Pirates", report);
        Assert.Contains($"your note: \"the {SelfTestReport.Hidden} channel {SelfTestReport.Hidden} flickered\"", report);
    }
}
