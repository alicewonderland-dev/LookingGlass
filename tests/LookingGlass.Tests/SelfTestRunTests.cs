using System.Text.Json;
using LookingGlass.Core.Client;
using LookingGlass.Core.SelfTest;
using LookingGlass.Protocol;

namespace LookingGlass.Tests;

/// <summary>
/// The self-test runner (docs/self-test-plan.md, "How it is tested"), against a fake host with a virtual clock: the order,
/// guided steps, Skip, Stop and time-outs, a check that throws, the clean-up always running and in order, a run carried on
/// from its file, the private-area check before every step that types (pausing before it, never after), the stop rule.
/// </summary>
public sealed class SelfTestRunTests : IDisposable {
    private readonly FakeSelfTestHost _host = new();

    public void Dispose() => this._host.Dispose();

    private static SelfTestStep Auto(string title, Func<StepContext, Task<StepResult>> run, bool types = false, string? checklist = null) =>
        new() { Title = title, Kind = StepKind.Automatic, Types = types, Run = run, Checklist = checklist };

    private static SelfTestStep Guided(string title, Func<StepContext, Task<StepResult>> run) => new() { Title = title, Kind = StepKind.Guided, Run = run };

    private static SelfTestCheck Check(string id, params SelfTestStep[] steps) =>
        new() { Id = id, Title = id, Steps = $"test {id}", Mode = CheckMode.Automatic, StepList = steps };

    private SelfTestRun NewRun(params SelfTestCheck[] checks) => this.NewRun(null, checks);

    private SelfTestRun NewRun(SelfTestRunData? resume, params SelfTestCheck[] checks) {
        var suite = new SelfTestSuite("suite", "Suite", checks);
        var pacer = new SelfTestPacer(() => this._host.Now, this._host.Delay);
        var data = resume ?? SelfTestRun.NewData("K7Q2", 42, "wss://example.test/ws", this._host.Now, [suite]);
        return new SelfTestRun(this._host, [suite], pacer, this._host.File, data, resume != null);
    }

    private static Task<StepResult> Pass(StepContext _) => Task.FromResult(StepResult.Pass());

    [Fact]
    public async Task ChecksRunInOrderThenTheCleanUp() {
        var order = new List<string>();
        var run = this.NewRun(
            Check("a", Auto("one", _ => { order.Add("a1"); return Task.FromResult(StepResult.Pass()); }), Auto("two", _ => { order.Add("a2"); return Task.FromResult(StepResult.Pass()); })),
            Check("b", Auto("one", _ => { order.Add("b1"); return Task.FromResult(StepResult.Pass()); })));

        await run.RunAsync();

        Assert.Equal(["a1", "a2", "b1"], order);
        Assert.All(run.Data.Results, result => Assert.Equal(CheckOutcome.Passed, result.Outcome));
        Assert.Equal(SelfTestState.Done, run.View.State);
        Assert.NotNull(run.View.CleanUp);
        Assert.False(this._host.File.Exists);
        Assert.Contains("[selftest] test a: passed", this._host.Logged);
    }

    [Fact]
    public async Task AFailedStepEndsItsCheckAndTheRunGoesOn() {
        var ran = new List<string>();
        var run = this.NewRun(
            Check("a", Auto("one", _ => Task.FromResult(StepResult.Fail("it went to Say")), checklist: "32"), Auto("two", _ => { ran.Add("a2"); return Task.FromResult(StepResult.Pass()); })),
            Check("b", Auto("one", _ => { ran.Add("b1"); return Task.FromResult(StepResult.Pass()); })));

        await run.RunAsync();

        Assert.Equal(["b1"], ran);
        var failed = run.Data.Results.Single(result => result.Check == "a");
        Assert.Equal(CheckOutcome.Failed, failed.Outcome);
        Assert.Equal("it went to Say", failed.Why);
        Assert.Equal("32", failed.Step);
        Assert.Contains("[selftest] test a: failed (step 32)", this._host.Logged);
    }

    [Fact]
    public async Task ACheckThatThrowsFailsAloneWithFixedWords() {
        var run = this.NewRun(
            Check("a", Auto("one", _ => throw new InvalidOperationException("Alice Liddell@Twintania said hello"))),
            Check("b", Auto("one", Pass)));

        await run.RunAsync();

        var thrown = run.Data.Results.Single(result => result.Check == "a");
        Assert.Equal(CheckOutcome.Failed, thrown.Outcome);
        Assert.Equal(SelfTestWords.SomethingWentWrong, thrown.Why);
        Assert.Equal(CheckOutcome.Passed, run.Data.Results.Single(result => result.Check == "b").Outcome);
        // Never the exception's message: it may hold a name.
        Assert.DoesNotContain(this._host.Logged, line => line.Contains("Alice") || line.Contains("hello"));
    }

    [Fact]
    public async Task AStepThatTakesTooLongTimesOut() {
        var slow = new SelfTestStep {
            Title = "slow", Kind = StepKind.Automatic, Timeout = TimeSpan.FromSeconds(5),
            Run = async context => {
                while (true) {
                    await context.DelayAsync(TimeSpan.FromSeconds(1));
                }
            },
        };
        var run = this.NewRun(Check("a", slow), Check("b", Auto("one", Pass)));

        await run.RunAsync();

        Assert.Equal(SelfTestWords.TimedOut, run.Data.Results.Single(result => result.Check == "a").Why);
        Assert.Equal(CheckOutcome.Passed, run.Data.Results.Single(result => result.Check == "b").Outcome);
    }

    [Fact]
    public async Task AGuidedStepWaitsForTheTesterAndTakesTheirNote() {
        var run = this.NewRun(Check("a", Guided("look", context => context.AskForResultAsync(new GuidedPrompt("Is it pink?", PromptKind.YesNo) { Short = "pink" }))));
        var asked = 0;
        this._host.OnTick = () => {
            if (run.View is { State: SelfTestState.NeedsYou, Prompt: { } prompt }) {
                asked++;
                Assert.Equal("Is it pink?", prompt.Prompt.Text);
                run.Answer(GuidedAnswer.Yes, "  slightly darker  ");
            }
        };

        await run.RunAsync();

        Assert.True(asked >= 1);
        var result = run.Data.Results.Single();
        Assert.Equal(CheckOutcome.Passed, result.Outcome);
        Assert.Equal(["pink"], result.Confirmed);
        Assert.Equal(["slightly darker"], result.Notes);
    }

    [Fact]
    public async Task AGuidedStepGoesOnByItselfWhenTheRunnerSeesTheChange() {
        var pressed = false;
        var prompt = new GuidedPrompt("Press Tab") {
            Short = "Tab",
            Watch = () => Task.FromResult(new WatchUpdate(pressed ? "stopped" : "still talking in [Local]", pressed)),
        };
        var run = this.NewRun(Check("a", Guided("tab", context => context.AskForResultAsync(prompt))));
        var ticks = 0;
        string? watching = null;
        this._host.OnTick = () => {
            watching ??= run.View.Prompt?.Watching;
            if (++ticks == 5) {
                pressed = true;
            }
        };

        await run.RunAsync();

        Assert.Equal("still talking in [Local]", watching);
        Assert.Equal(CheckOutcome.Passed, run.Data.Results.Single().Outcome);
        Assert.Equal(["Tab"], run.Data.Results.Single().Confirmed);
    }

    [Theory]
    [InlineData(GuidedAnswer.Skip, CheckOutcome.NotRun)]
    [InlineData(GuidedAnswer.DidntWork, CheckOutcome.Failed)]
    [InlineData(GuidedAnswer.No, CheckOutcome.Failed)]
    public async Task SkipAndNoAreKept(GuidedAnswer answer, CheckOutcome outcome) {
        var run = this.NewRun(Check("a", Guided("look", context => context.AskForResultAsync(new GuidedPrompt("Look", PromptKind.YesNo)))));
        this._host.OnTick = () => {
            if (run.View.State == SelfTestState.NeedsYou) {
                run.Answer(answer);
            }
        };

        await run.RunAsync();

        Assert.Equal(outcome, run.Data.Results.Single().Outcome);
        Assert.Equal(answer == GuidedAnswer.Skip ? SelfTestWords.YouSkipped : SelfTestWords.YouSaidNo, run.Data.Results.Single().Why);
    }

    [Fact]
    public async Task AStepSkippedInAPassingCheckIsListedAsNotRun() {
        var run = this.NewRun(Check("a",
            Auto("Next to a stranger", _ => Task.FromResult(StepResult.Skip(SelfTestWords.NeedsStranger))),
            Auto("alone", _ => Task.FromResult(StepResult.Pass("the /p part")))));

        await run.RunAsync();

        var result = run.Data.Results.Single();
        Assert.Equal(CheckOutcome.Passed, result.Outcome);
        Assert.Contains("the /p part", result.NotRun);
        Assert.Contains($"next to a stranger: {SelfTestWords.NeedsStranger}", result.NotRun);
    }

    [Fact]
    public async Task StopAbandonsTheStepAndCleansUp() {
        var after = false;
        SelfTestRun? run = null;
        var check = Check("a",
            Auto("setting", async context => {
                await context.ChangeSettingAsync(SelfTestSetting.WindowsOnly, true);
                await context.TestChannelAsync();
                return StepResult.Pass();
            }),
            Guided("wait", context => context.AskForResultAsync(new GuidedPrompt("Look"))),
            Auto("after", _ => { after = true; return Task.FromResult(StepResult.Pass()); }));
        run = this.NewRun(check, Check("b", Auto("never", _ => { after = true; return Task.FromResult(StepResult.Pass()); })));
        this._host.OnTick = () => {
            if (run.View.State == SelfTestState.NeedsYou) {
                run.Stop();
            }
        };

        await run.RunAsync();

        Assert.False(after);
        var stopped = run.Data.Results.Single();
        Assert.Equal(CheckOutcome.Stopped, stopped.Outcome);
        Assert.Equal(SelfTestWords.StoppedByYou, stopped.Why);
        // The clean-up: the channel left, the setting back, the file gone.
        Assert.Single(this._host.Left);
        Assert.Equal("WindowsOnly before", this._host.Settings[SelfTestSetting.WindowsOnly].GetString());
        Assert.False(this._host.File.Exists);
        Assert.Equal(SelfTestState.Done, run.View.State);
    }

    [Fact]
    public async Task EveryCleanUpStepRunsWhenOneBeforeItFails() {
        var run = this.NewRun(Check("a", Auto("make", async context => {
            await context.TestChannelAsync();
            await context.ChangeSettingAsync(SelfTestSetting.SayWhenTalking, true);
            return StepResult.Pass();
        })));
        this._host.LeaveFails = new IOException("gone");

        await run.RunAsync();

        Assert.Empty(this._host.Left);
        // Settings are put back anyway, and the test's history lines removed.
        Assert.Equal("SayWhenTalking before", this._host.Settings[SelfTestSetting.SayWhenTalking].GetString());
        Assert.Single(this._host.Forgotten);
        // The file stays, so the next start leaves the channel.
        Assert.True(this._host.File.Exists);
        Assert.Single(this._host.File.Read(out _)!.TestChannels);
        Assert.Contains("couldn't be left", run.View.CleanUp);
    }

    [Fact]
    public async Task ASettingIsSnapshottedOnceAndPutBackAsItWasBeforeTheRun() {
        var run = this.NewRun(Check("a", Auto("change", async context => {
            await context.ChangeSettingAsync(SelfTestSetting.ColourWholeLine, false);
            // The snapshot is in the file before the first change is written.
            Assert.True(new SelfTestRunFile(context.Host.ConfigFolder).Read(out _)!.Settings.ContainsKey(SelfTestSetting.ColourWholeLine));
            await context.ChangeSettingAsync(SelfTestSetting.ColourWholeLine, true);
            // The tester changes it by hand meanwhile.
            await context.Host.WriteSettingAsync(SelfTestSetting.ColourWholeLine, JsonSerializer.SerializeToElement("by hand"));
            return StepResult.Pass();
        })));

        await run.RunAsync();

        Assert.Equal("ColourWholeLine before", this._host.Settings[SelfTestSetting.ColourWholeLine].GetString());
        Assert.Empty(run.Data.Settings);
    }

    [Fact]
    public async Task TheTestChannelIsInTheFileBeforeAnythingIsSentAndOnAFreeNumberFromFiftyDown() {
        this._host.Slots["mine50"] = 50;
        this._host.Slots["mine49"] = 1;
        var run = this.NewRun(Check("a", Auto("send", async context => {
            var id = await context.TestChannelAsync();
            Assert.Contains(id, new SelfTestRunFile(context.Host.ConfigFolder).Read(out _)!.TestChannels);
            Assert.Equal(49, await context.Host.SlotOfAsync(id));
            Assert.Equal(id, await context.TestChannelAsync());
            await context.ServerAsync(SelfTestAction.ChannelMessage, () => context.Host.SendAsync(id, context.NextMarker()));
            return StepResult.Pass();
        })));

        await run.RunAsync();

        Assert.Equal(1, this._host.Creates);
        Assert.Equal(["LG self-test K7Q2"], this._host.CreatedNames);
        Assert.Equal("LG self-test K7Q2 1, please ignore", this._host.Sent.Single().Text);
    }

    [Fact]
    public async Task TheFirstRefusalStopsTheRunAndNothingIsAskedAgain() {
        var sends = 0;
        var later = false;
        this._host.SendFails = new ServerErrorException(ErrorCode.RateLimited, "Slow down.");
        var run = this.NewRun(
            Check("a", Auto("send", async context => {
                var id = await context.TestChannelAsync();
                sends++;
                await context.ServerAsync(SelfTestAction.ChannelMessage, () => context.Host.SendAsync(id, "x"));
                sends++;
                await context.ServerAsync(SelfTestAction.ChannelMessage, () => context.Host.SendAsync(id, "y"));
                return StepResult.Pass();
            })),
            Check("b", Auto("later", _ => { later = true; return Task.FromResult(StepResult.Pass()); })));
        var started = this._host.Now;

        await run.RunAsync();

        Assert.Equal(1, sends);
        Assert.Empty(this._host.Sent);
        Assert.False(later);
        Assert.True(run.Pacer.Refused);
        var stopped = run.Data.Results.Single();
        Assert.Equal(CheckOutcome.Stopped, stopped.Outcome);
        Assert.Equal(SelfTestWords.ServerSlowDown, stopped.Why);
        // It waited a minute, then cleaned up.
        Assert.True(this._host.Now - started >= SelfTestRun.AfterRefusal);
        Assert.Single(this._host.Left);
        await Assert.ThrowsAsync<SelfTestRefusedException>(() => run.Pacer.WaitAsync(SelfTestAction.ChannelMessage, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARefusalSeenByThePluginStopsTheRunToo() {
        SelfTestRun? run = null;
        run = this.NewRun(Check("a", Guided("wait", context => context.AskForResultAsync(new GuidedPrompt("Look")))));
        this._host.OnTick = () => run.ReportRefusal();

        await run.RunAsync();

        Assert.Equal(SelfTestWords.ServerSlowDown, run.Data.Results.Single().Why);
        Assert.True(run.Pacer.Refused);
    }

    [Fact]
    public async Task NotInAPrivatePlaceTheRunWaitsBeforeTheStepNeverAfter() {
        var seenPaused = false;
        SelfTestRun? run = null;
        var check = Check("a",
            Auto("leave", context => {
                this._host.Place = FakeSelfTestHost.Ward;
                return Task.FromResult(StepResult.Pass());
            }),
            Auto("type", async context => {
                await context.TypeAsync("/s " + context.NextMarker());
                return StepResult.Pass();
            }, types: true));
        run = this.NewRun(check);
        var ticks = 0;
        this._host.OnTick = () => {
            if (run.View.State == SelfTestState.Paused && this._host.Place == FakeSelfTestHost.Ward) {
                seenPaused = true;
                Assert.Equal(SelfTestWords.PausedLeft, run.View.Pause);
                Assert.Empty(this._host.Typed);
                if (++ticks == 10) {
                    this._host.Place = FakeSelfTestHost.InnRoom;
                }
            }
        };

        await run.RunAsync();

        Assert.True(seenPaused);
        var typed = Assert.Single(this._host.Typed);
        Assert.Equal(PlaceKind.InnRoom, typed.Place);
        Assert.Equal(CheckOutcome.Passed, run.Data.Results.Single().Outcome);
    }

    [Fact]
    public async Task APlaceThatChangesRightBeforeTypingIsCaughtAndNothingIsTyped() {
        // The runner's own check passed; the place changes before the host types: the host refuses, and the runner waits.
        var changed = 0;
        this._host.BeforeType = () => {
            if (changed++ == 0) {
                this._host.Place = FakeSelfTestHost.Ward;
            }
        };
        SelfTestRun? run = null;
        run = this.NewRun(Check("a", Auto("type", async context => {
            await context.TypeAsync(context.NextMarker());
            return StepResult.Pass();
        }, types: true)));
        var back = 0;
        this._host.OnTick = () => {
            if (this._host.Place == FakeSelfTestHost.Ward && ++back == 20) {
                this._host.Place = FakeSelfTestHost.InnRoom;
            }
        };

        await run.RunAsync();

        var typed = Assert.Single(this._host.Typed);
        Assert.Equal(PlaceKind.InnRoom, typed.Place);
    }

    [Fact]
    public async Task EveryStepTypesOnlyInAPrivatePlaceWhateverTheWalkDoes() {
        // A tester wandering in and out at random: no line is ever typed anywhere but a private place, by the runner's
        // check alone (the fake records what its own check would have refused).
        var random = new Random(7);
        var places = new[] { FakeSelfTestHost.InnRoom, FakeSelfTestHost.Ward, FakeSelfTestHost.House, PlaceFacts.Unreadable, FakeSelfTestHost.InnRoom with { Territory = 0 } };
        this._host.OnTick = () => this._host.Place = places[random.Next(places.Length)];
        this._host.BeforeType = () => this._host.RecordIfOutside("line");
        var steps = Enumerable.Range(0, 20).Select(i => Auto($"type {i}", async context => {
            await context.TypeAsync(context.NextMarker());
            return StepResult.Pass();
        }, types: true)).ToArray();
        var run = this.NewRun(Check("a", steps));

        await run.RunAsync();

        Assert.Empty(this._host.TypedOutside);
        Assert.All(this._host.Typed, typed => Assert.True(PrivateArea.IsPrivate(typed.Place)));
        Assert.Equal(20, this._host.Typed.Count);
    }

    [Fact]
    public async Task InAHouseATypingStepWaitsWhileSomeoneIsNear() {
        this._host.Place = FakeSelfTestHost.House;
        this._host.Others = 1;
        SelfTestRun? run = null;
        var waited = false;
        run = this.NewRun(Check("a",
            Auto("no typing", Pass),
            Auto("type", async context => {
                await context.TypeAsync(context.NextMarker());
                return StepResult.Pass();
            }, types: true)));
        var ticks = 0;
        this._host.OnTick = () => {
            if (run.View.Pause == SelfTestWords.WaitingSomeoneNear) {
                waited = true;
                Assert.Empty(this._host.Typed);
                if (++ticks == 5) {
                    this._host.Others = 0;
                }
            }
        };

        await run.RunAsync();

        Assert.True(waited);
        Assert.Single(this._host.Typed);
    }

    [Fact]
    public async Task AnInnRoomNeedsNoCheckForOthers() {
        this._host.Others = 3;
        var run = this.NewRun(Check("a", Auto("type", async context => {
            await context.TypeAsync(context.NextMarker());
            return StepResult.Pass();
        }, types: true)));

        await run.RunAsync();

        Assert.Single(this._host.Typed);
    }

    [Fact]
    public async Task TypingSetsSayFirstAndTheClean_upPutsTheChannelBack() {
        this._host.GameChannel = 6;
        var run = this.NewRun(Check("a", Auto("type", async context => {
            await context.TypeAsync(context.NextMarker());
            Assert.Equal(SelfTestGameChannels.Say, this._host.GameChannel);
            return StepResult.Pass();
        }, types: true)));

        await run.RunAsync();

        Assert.Single(this._host.Typed);
        Assert.Equal(6, this._host.GameChannel);
    }

    [Fact]
    public async Task WhileTalkingInAChannelOverAnotherChatChannelNothingIsTyped() {
        this._host.GameChannel = 6;
        this._host.Sticky = StickyChannel.LocalId;
        var run = this.NewRun(Check("a", Auto("type", async context => {
            await context.TypeAsync(context.NextMarker());
            return StepResult.Pass();
        }, types: true)));

        await run.RunAsync();

        Assert.Empty(this._host.Typed);
        Assert.Equal(SelfTestWords.NotSay, run.Data.Results.Single().Why);
    }

    [Fact]
    public async Task FiveMinutesWithoutAConnectionStopsTheRun() {
        this._host.Ready = false;
        var run = this.NewRun(Check("a", Auto("one", Pass)));

        await run.RunAsync();

        Assert.Equal(CheckOutcome.Stopped, run.Data.Results.Single().Outcome);
        Assert.Equal(SelfTestWords.Disconnected, run.Data.Results.Single().Why);
        Assert.False(this._host.File.Exists);
    }

    [Fact]
    public async Task ACheckThatCantRunHereIsSkippedWithWhy() {
        var check = Check("a", Auto("one", Pass)) with { Needs = SelfTestNeeds.ChatTwo };
        var partner = Check("b", Auto("one", Pass)) with { Needs = SelfTestNeeds.Partner };
        var run = this.NewRun(check, partner);

        await run.RunAsync();

        Assert.Equal(SelfTestWords.NeedsChatTwo, run.Data.Results.Single(result => result.Check == "a").Why);
        Assert.Equal(SelfTestWords.NeedsPartner, run.Data.Results.Single(result => result.Check == "b").Why);
        Assert.All(run.Data.Results, result => Assert.Equal(CheckOutcome.Skipped, result.Outcome));
    }

    [Fact]
    public async Task ARunCarriedOnFromItsFileStartsAtTheRightStep() {
        var ran = new List<string>();
        SelfTestStep Step(string name) => Auto(name, _ => { ran.Add(name); return Task.FromResult(StepResult.Pass()); });
        var checks = new[] { Check("a", Step("a1")), Check("b", Step("b1"), Step("b2"), Step("b3")), Check("c", Step("c1")) };
        var suite = new SelfTestSuite("suite", "Suite", checks);
        var data = SelfTestRun.NewData("K7Q2", 42, "wss://example.test/ws", this._host.Now, [suite]);
        data.Results.Add(new CheckRecord { Check = "a", Outcome = CheckOutcome.Passed });
        data.Phase = SelfTestPhase.WaitingToCarryOn;
        data.NextCheck = "b";
        data.NextStep = 2;
        var run = this.NewRun(data, checks);

        await run.RunAsync();

        Assert.Equal(["b3", "c1"], ran);
        Assert.Equal(3, run.Data.Results.Count);
        Assert.All(run.Data.Results, result => Assert.Equal(CheckOutcome.Passed, result.Outcome));
    }

    [Fact]
    public async Task ARelogStepWaitsInTheFileAndCarriesOnAfterTheRelog() {
        SelfTestRun? run = null;
        var after = false;
        var relog = new SelfTestStep { Title = "Go to the title screen, then back in", Kind = StepKind.CarryOnAfter, Run = Pass };
        run = this.NewRun(Check("a", relog, Auto("after", _ => { after = true; return Task.FromResult(StepResult.Pass()); })));
        var ticks = 0;
        this._host.OnTick = () => {
            if (run.View.State == SelfTestState.WaitingToCarryOn && ++ticks == 3) {
                var saved = this._host.File.Read(out _)!;
                Assert.Equal(SelfTestPhase.WaitingToCarryOn, saved.Phase);
                Assert.Equal("a", saved.NextCheck);
                Assert.Equal(1, saved.NextStep);
                this._host.Logins++;
            }
        };

        await run.RunAsync();

        Assert.True(after);
        Assert.Equal(CheckOutcome.Passed, run.Data.Results.Single().Outcome);
    }

    [Fact]
    public async Task ThePluginGoingAwayWhileWaitingKeepsTheFileForTheNextStart() {
        SelfTestRun? run = null;
        var reload = new SelfTestStep { Title = "Turn LookingGlass off and on", Kind = StepKind.CarryOnAfter, Run = Pass };
        run = this.NewRun(Check("a",
            Auto("change", async context => {
                await context.ChangeSettingAsync(SelfTestSetting.AdvancedMode, true);
                await context.TestChannelAsync();
                return StepResult.Pass();
            }),
            reload,
            Auto("after", Pass)));
        this._host.OnTick = () => {
            if (run.View.State == SelfTestState.WaitingToCarryOn) {
                run.Abandon();
            }
        };

        await run.RunAsync();

        // Nothing was cleaned up: the next start carries on.
        Assert.Empty(this._host.Left);
        Assert.Equal(JsonValueKind.True, this._host.Settings[SelfTestSetting.AdvancedMode].ValueKind);
        var saved = this._host.File.Read(out var damaged)!;
        Assert.False(damaged);
        Assert.Equal(SelfTestPhase.WaitingToCarryOn, saved.Phase);
        Assert.Equal(2, saved.NextStep);
        Assert.Single(saved.TestChannels);
        Assert.True(saved.Settings.ContainsKey(SelfTestSetting.AdvancedMode));
        Assert.Empty(saved.Results);
    }

    [Fact]
    public async Task StoppedWhileWaitingToCarryOnItCleansUpAndNoLongerWaits() {
        SelfTestRun? run = null;
        var reload = new SelfTestStep { Title = "Turn LookingGlass off and on", Kind = StepKind.CarryOnAfter, Run = Pass };
        run = this.NewRun(Check("a", Auto("make", async context => {
            await context.TestChannelAsync();
            return StepResult.Pass();
        }), reload));
        this._host.LeaveFails = new IOException("busy");
        this._host.OnTick = () => {
            if (run.View.State == SelfTestState.WaitingToCarryOn) {
                run.Stop();
            }
        };

        await run.RunAsync();

        // The file stays for the channel that couldn't be left, but no longer says to carry on.
        var saved = this._host.File.Read(out _)!;
        Assert.Equal(SelfTestPhase.Running, saved.Phase);
        Assert.Null(saved.NextCheck);
    }

    [Fact]
    public async Task HistoryOffAndEmptyBeforeIsDeletedWholeOtherwiseOnlyTheTestsLines() {
        var run = this.NewRun(Check("a", Auto("history", async context => {
            context.HistoryWasOffAndEmpty();
            await context.TestChannelAsync();
            return StepResult.Pass();
        })));

        await run.RunAsync();

        Assert.Equal(1, this._host.Deleted);
        Assert.Empty(this._host.Forgotten);

        using var other = new FakeSelfTestHost();
        var suite = new SelfTestSuite("s", "S", [Check("a", Auto("make", async context => {
            await context.TestChannelAsync();
            return StepResult.Pass();
        }))]);
        var second = new SelfTestRun(other, [suite], new SelfTestPacer(() => other.Now, other.Delay), other.File,
            SelfTestRun.NewData("ABCD", 1, "x", other.Now, [suite]));
        await second.RunAsync();
        Assert.Equal(0, other.Deleted);
        Assert.Single(other.Forgotten);
    }

    [Fact]
    public void CodesAreFourLettersAndDigitsWithoutLookalikes() {
        for (var i = 0; i < 200; i++) {
            var code = SelfTestRun.NewCode();
            Assert.Matches("^[A-HJ-NP-Z2-9]{4}$", code);
        }

        Assert.Equal("LG self-test K7Q2", SelfTestRun.ChannelName("K7Q2", 0));
        Assert.Equal("LG self-test K7Q2 b", SelfTestRun.ChannelName("K7Q2", 1));
        Assert.Equal("LG self-test K7Q2 c", SelfTestRun.ChannelName("K7Q2", 2));
    }
}
