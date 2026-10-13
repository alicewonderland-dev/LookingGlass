using System.Text.Json;
using LookingGlass.Core.Client;
using LookingGlass.Core.SelfTest;

namespace LookingGlass.Tests;

/// <summary>
/// The self-test's second review (2026-10-13): a stale run file never deletes the tester's own chat history (only the
/// test's, while the history is still off and nothing in it is newer than the run; otherwise the tester is asked); a step
/// that fails while the run had disconnected connects again; a line to type comes off the screen when Say goes; renamed
/// test channels are listed as leftovers.
/// </summary>
public sealed class SelfTestSecondReviewTests : IDisposable {
    private readonly FakeSelfTestHost _host = new();
    private readonly string _config = Path.Combine(Path.GetTempPath(), "lgt-selftesthistory-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset RunEnd = new(2026, 10, 13, 20, 0, 0, TimeSpan.Zero);

    public void Dispose() {
        this._host.Dispose();
        if (Directory.Exists(this._config)) {
            Directory.Delete(TestFolders.CheckDeletable(this._config), true);
        }
    }

    // ---------------------------------------------------------------- N1: the test's history only

    [Fact]
    public void TheTestsHistoryIsDeletedOnlyWhileOffAndNothingIsNewer() {
        IReadOnlyList<(string, DateTimeOffset)> old = [("a", RunEnd.AddMinutes(-10)), ("b", RunEnd.AddMinutes(1))];

        // Off, nothing newer than the run (with its last minute or two): every folder is the test's.
        Assert.Equal(("a,b", false), Flatten(SelfTestRecovery.HistoryCleanup(false, old, RunEnd)));
        // Turned on since: never deleted by itself, asked.
        Assert.Equal(("", true), Flatten(SelfTestRecovery.HistoryCleanup(true, old, RunEnd)));
        // Something newer: never deleted by itself, asked.
        Assert.Equal(("", true), Flatten(SelfTestRecovery.HistoryCleanup(false, [.. old, ("c", RunEnd.AddHours(3))], RunEnd)));
        // Nothing there: nothing to do or ask.
        Assert.Equal(("", false), Flatten(SelfTestRecovery.HistoryCleanup(false, [], RunEnd)));
    }

    private static (string, bool) Flatten((IReadOnlyList<string> Delete, bool Ask) decision) => (string.Join(",", decision.Delete), decision.Ask);

    private sealed class MapSettings : ISelfTestSettings {
        public Dictionary<SelfTestSetting, JsonElement> Values { get; } = new();

        public JsonElement Read(SelfTestSetting setting) => this.Values[setting];

        public void Write(SelfTestSetting setting, JsonElement value) => this.Values[setting] = value.Clone();
    }

    private static SelfTestRunData Stale() => new() { Code = "K7Q2", ContentId = 42, ServerUrl = "x", HistoryWasOffAndEmpty = true, LastActive = RunEnd };

    [Fact]
    public void AStaleFileNeverDeletesAHistoryTurnedOnSinceAndAsksInstead() {
        // The run's own deletion failed; the tester then turned the history on and used it.
        var data = Stale();
        var settings = new MapSettings();
        settings.Values[SelfTestSetting.KeepHistory] = JsonSerializer.SerializeToElement(true);
        var deleted = new List<string>();
        IReadOnlyList<(string, DateTimeOffset)> folders = [("theirs", RunEnd.AddDays(2))];

        var done = SelfTestRecovery.PutBack(data, settings, () => folders, deleted.AddRange);

        Assert.Empty(deleted);
        Assert.False(done);
        Assert.True(data.HistoryNeedsAsking);
        // Never by itself again, whatever comes later.
        Assert.False(data.HistoryWasOffAndEmpty);
        settings.Values[SelfTestSetting.KeepHistory] = JsonSerializer.SerializeToElement(false);
        SelfTestRecovery.PutBack(data, settings, () => folders, deleted.AddRange);
        Assert.Empty(deleted);
    }

    [Fact]
    public void AStaleFileNeverDeletesNewerLinesEvenWithTheHistoryOff() {
        var data = Stale();
        var settings = new MapSettings();
        settings.Values[SelfTestSetting.KeepHistory] = JsonSerializer.SerializeToElement(false);
        var deleted = new List<string>();

        SelfTestRecovery.PutBack(data, settings, () => [("test", RunEnd), ("used since", RunEnd.AddHours(5))], deleted.AddRange);

        Assert.Empty(deleted);
        Assert.True(data.HistoryNeedsAsking);
    }

    [Fact]
    public void WhenTheTesterChoosesOnlyFoldersUntouchedSinceTheTestGoAndNeverTheOpenOne() {
        IReadOnlyList<(string, DateTimeOffset)> folders = [
            (Path.Combine(this._config, "a"), RunEnd.AddMinutes(-3)), (Path.Combine(this._config, "b"), RunEnd.AddDays(1)), (Path.Combine(this._config, "c"), RunEnd),
        ];

        var untouched = SelfTestRecovery.UntouchedSince(folders, RunEnd, open: Path.Combine(this._config, "c"));

        Assert.Equal([Path.Combine(this._config, "a")], untouched);
    }

    [Fact]
    public void FolderTimesAndDeletingOnlyTheChatLogFoldersAsked() {
        var test = ChatLogFiles.Folder(this._config, 1, "wss://example.test/ws");
        var theirs = ChatLogFiles.Folder(this._config, 2, "wss://example.test/ws");
        var other = Path.Combine(this._config, "pluginstuff");
        foreach (var folder in new[] { test, theirs, other }) {
            Directory.CreateDirectory(Path.Combine(folder, "text"));
            File.WriteAllText(Path.Combine(folder, "text", "2026-10.txt"), "line");
        }

        var newest = RunEnd.AddDays(1).UtcDateTime;
        File.SetLastWriteTimeUtc(Path.Combine(theirs, "text", "2026-10.txt"), newest);

        var times = ChatLogFiles.FoldersWithTimes(this._config);
        Assert.Equal(2, times.Count);
        Assert.True(times.Single(entry => entry.Folder == theirs).Newest >= new DateTimeOffset(newest));

        ChatLogFiles.DeleteFolders(this._config, [test, other]);

        Assert.False(Directory.Exists(test));
        Assert.True(Directory.Exists(theirs));
        // Not a chat log folder: never deleted, even if asked.
        Assert.True(Directory.Exists(other));
    }

    [Fact]
    public async Task ARunWhoseHistorySettingCantGoBackDeletesNothing() {
        this._host.FailWriteBack = SelfTestSetting.KeepHistory;
        var check = new SelfTestCheck {
            Id = "a", Title = "a", Steps = "test a", Mode = CheckMode.Automatic,
            StepList = [new SelfTestStep {
                Title = "history on", Kind = StepKind.Automatic, Run = async context => {
                    context.HistoryWasOffAndEmpty();
                    await context.ChangeSettingAsync(SelfTestSetting.KeepHistory, true);
                    return StepResult.Pass();
                },
            }],
        };
        var suite = new SelfTestSuite("s", "S", [check]);
        var run = new SelfTestRun(this._host, [suite], new SelfTestPacer(() => this._host.Now, this._host.Delay), this._host.File,
            SelfTestRun.NewData("K7Q2", 42, "x", this._host.Now, [suite]));

        await run.RunAsync();

        Assert.Equal(0, this._host.Deleted);
        Assert.True(this._host.File.Exists);
        Assert.True(this._host.File.Read(out _)!.HistoryWasOffAndEmpty);
    }

    // ---------------------------------------------------------------- lows

    [Fact]
    public async Task AStepThatFailsWhileDisconnectedConnectsAgainBeforeTheNext() {
        var checks = new List<bool>();
        var suite = new SelfTestSuite("s", "S", [
            new SelfTestCheck {
                Id = "a", Title = "a", Steps = "test a", Mode = CheckMode.Automatic,
                StepList = [new SelfTestStep {
                    Title = "disconnect, then fail", Kind = StepKind.Automatic, Run = async context => {
                        await context.DisconnectAsync();
                        throw new InvalidOperationException("broken");
                    },
                }],
            },
            new SelfTestCheck {
                Id = "b", Title = "b", Steps = "test b", Mode = CheckMode.Automatic,
                StepList = [new SelfTestStep {
                    Title = "connected", Kind = StepKind.Automatic, Run = context => {
                        checks.Add(this._host.Ready);
                        return Task.FromResult(StepResult.Pass());
                    },
                }],
            },
        ]);
        var run = new SelfTestRun(this._host, [suite], new SelfTestPacer(() => this._host.Now, this._host.Delay), this._host.File,
            SelfTestRun.NewData("K7Q2", 42, "x", this._host.Now, [suite]));

        await run.RunAsync();

        Assert.Equal([true], checks);
        Assert.Equal(CheckOutcome.Failed, run.Data.Results.Single(result => result.Check == "a").Outcome);
        Assert.Equal(CheckOutcome.Passed, run.Data.Results.Single(result => result.Check == "b").Outcome);
        Assert.False(run.Data.DisconnectedByRun);
    }

    [Fact]
    public async Task ALineToTypeComesOffTheScreenWhenSayGoes() {
        this._host.ChatTwo = true;
        var prompt = new GuidedPrompt("In ChatTwo's main input, type: LG self-test K7Q2 1, please ignore") { TesterTypes = true, ChatTwoOnSay = true };
        var suite = new SelfTestSuite("s", "S", [new SelfTestCheck {
            Id = "a", Title = "a", Steps = "test a", Mode = CheckMode.Guided, StepList = [SelfTestSteps.Ask("type it", null, prompt)],
        }]);
        var run = new SelfTestRun(this._host, [suite], new SelfTestPacer(() => this._host.Now, this._host.Delay), this._host.File,
            SelfTestRun.NewData("K7Q2", 42, "x", this._host.Now, [suite]));
        var phase = 0;
        var hidden = new List<string?>();
        this._host.OnTick = () => {
            var view = run.View;
            var shown = view is { State: SelfTestState.NeedsYou, Prompt.Prompt: var current } && ReferenceEquals(current, prompt);
            switch (phase) {
                case 0 when shown:
                    // ChatTwo's input moved to Party while the line is asked for.
                    this._host.ChatTwoInput = 14;
                    phase = 1;
                    break;
                case 1 when !shown:
                    hidden.Add(view.Prompt?.Prompt.Text ?? view.Pause);
                    this._host.ChatTwoInput = SelfTestGameChannels.ChatTwoSay;
                    // Then talking in local chat over another game channel: the switch would end it, so it waits.
                    this._host.Sticky = StickyChannel.LocalId;
                    this._host.GameChannel = 2;
                    phase = 2;
                    break;
                case 2 when view.Pause == SelfTestWords.WaitingSay:
                    hidden.Add(view.Pause);
                    this._host.GameChannel = SelfTestGameChannels.Say;
                    phase = 3;
                    break;
                case 3 when shown:
                    run.Answer(GuidedAnswer.Done);
                    phase = 4;
                    break;
            }
        };

        await run.RunAsync();

        Assert.Equal(4, phase);
        Assert.Equal([SelfTestWords.ChatTwoToSay, SelfTestWords.WaitingSay], hidden);
        Assert.Equal(CheckOutcome.Passed, run.Data.Results.Single().Outcome);
    }

    [Fact]
    public void RenamedTestChannelsAreLeftoversToo() {
        Assert.True(SelfTestRun.IsTestChannelName("LG self-test K7Q2"));
        Assert.True(SelfTestRun.IsTestChannelName("LG self-test K7Q2 b"));
        Assert.True(SelfTestRun.IsTestChannelName("LG self-test K7Q2 r"));
        Assert.False(SelfTestRun.IsTestChannelName("LG self-test"));
        Assert.False(SelfTestRun.IsTestChannelName("Sky Pirates"));
        Assert.False(SelfTestRun.IsTestChannelName("LG self-test k7q2"));
        Assert.False(SelfTestRun.IsTestChannelName(null));
    }
}
