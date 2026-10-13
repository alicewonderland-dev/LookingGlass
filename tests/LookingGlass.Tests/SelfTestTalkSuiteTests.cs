using System.Text.Json;
using LookingGlass.Core.Client;
using LookingGlass.Core.SelfTest;
using LookingGlass.Core.SelfTest.Suites;

namespace LookingGlass.Tests;

/// <summary>
/// The talking suite (local-chat-checklist.md 1 to 41) against the fake host's stand-in for the plugin: its automatic
/// checks pass when the plugin behaves as the checklist says, and a one-off that goes to the wrong place fails with the
/// step and why. The "tester" here does what each question asks.
/// </summary>
public sealed class SelfTestTalkSuiteTests : IDisposable {
    private readonly FakeSelfTestHost _host = new() { Simulate = true };

    public SelfTestTalkSuiteTests() {
        foreach (var setting in new[] { SelfTestSetting.LocalPrivacyAccepted, SelfTestSetting.LocalTalkNoteShown, SelfTestSetting.SayWhenTalking, SelfTestSetting.WindowsOnly }) {
            this._host.Settings[setting] = JsonSerializer.SerializeToElement(false);
        }

        this._host.Settings[SelfTestSetting.LocalChatColour] = JsonSerializer.SerializeToElement((string?) null);
    }

    public void Dispose() => this._host.Dispose();

    /// <summary>A tester who does what is asked: clicks, presses Tab, relogs, and answers yes.</summary>
    private void Tester(SelfTestRun run) {
        var view = run.View;
        if (view.State == SelfTestState.WaitingToCarryOn) {
            // A reload or relog: talking in a channel ends with it.
            if (view.Prompt?.Prompt.Text.Contains("/xlplugins", StringComparison.Ordinal) == true && this._host.Sticky != null) {
                this._host.Print(new TapLine(TapKind.Notice, NoticeTone.Info, StickyMessages.Ended(LocalChat.Tag, StickyEnd.Unloading), LocalChat.Tag));
            }

            this._host.Sticky = null;
            this._host.Logins++;
            return;
        }

        if (view is not { State: SelfTestState.NeedsYou, Prompt: { } prompt }) {
            return;
        }

        var text = prompt.Prompt.Text;
        if (text.StartsWith("Click Not now", StringComparison.Ordinal)) {
            this._host.PrivacyOpen = false;
        } else if (text.StartsWith("Click Accept", StringComparison.Ordinal)) {
            var talking = this._host.Sticky != null;
            this._host.Settings[SelfTestSetting.LocalPrivacyAccepted] = JsonSerializer.SerializeToElement(true);
            this._host.PrivacyOpen = false;
            // What the privacy window says once accepted: asked by /lgl alone, or by a message.
            var asked = this._host.PrintedSince(0).LastOrDefault(line => line.Text == LocalChatWords.PrivacyAskedToTalk.Plain || line.Text == LocalChatWords.PrivacyAsked.Plain);
            var said = asked?.Text == LocalChatWords.PrivacyAskedToTalk.Plain ? LocalChatWords.PrivacyAcceptedFor(LocalPrivacyAsked.ByTalking, talking)! : LocalChatWords.PrivacyAccepted;
            this._host.Print(new TapLine(TapKind.Notice, NoticeTone.Info, said.Plain));
        } else if (text.Contains("press Tab", StringComparison.Ordinal) || text.Contains("server info bar", StringComparison.Ordinal) || text.Contains("Send Tell", StringComparison.Ordinal)) {
            this._host.Sticky = null;
        } else if (text.Contains("click Withdraw", StringComparison.Ordinal)) {
            this._host.Settings[SelfTestSetting.LocalPrivacyAccepted] = JsonSerializer.SerializeToElement(false);
            this._host.Sticky = null;
        } else {
            run.Answer(prompt.Prompt.Kind == PromptKind.YesNo ? GuidedAnswer.Yes : GuidedAnswer.Done);
        }
    }

    private async Task<SelfTestRun> RunAsync() {
        var suites = new[] { TalkSuite.Create() };
        var run = new SelfTestRun(this._host, suites, new SelfTestPacer(() => this._host.Now, this._host.Delay), this._host.File,
            SelfTestRun.NewData("K7Q2", 42, "wss://example.test/ws", this._host.Now, suites));
        this._host.OnTick = () => this.Tester(run);
        await run.RunAsync();
        return run;
    }

    [Fact]
    public async Task WhenThePluginBehavesTheChecksPass() {
        var run = await this.RunAsync();

        var results = run.Data.Results.ToDictionary(result => result.Check);
        foreach (var id in new[] { "local-chat-1-3", "local-chat-3a-3c", "local-chat-29", "local-chat-30-31", "local-chat-32-33", "local-chat-34-36", "local-chat-37-39" }) {
            Assert.True(results[id].Outcome == CheckOutcome.Passed, $"{id}: {results[id].Outcome} {results[id].Why} (step {results[id].Step})");
        }

        Assert.Contains("the part next to a stranger (step 31)", results["local-chat-30-31"].NotRun);
        Assert.Contains("Tab", results["local-chat-34-36"].Confirmed);
        // The log tap is the plugin's: the fake counts nothing, so that step isn't run; the plain words step is.
        Assert.Equal(CheckOutcome.Passed, results["local-chat-40-41"].Outcome);
        Assert.Contains(results["local-chat-40-41"].NotRun, part => part.EndsWith("needs the local chat checks above", StringComparison.Ordinal));
        // Nothing reached Say but the one-offs and the line typed after withdrawing.
        Assert.All(this._host.GameChatSince(0).Where(line => line.Channel == GameChatKind.Say),
            line => Assert.StartsWith(run.MarkerPrefix, line.Text, StringComparison.Ordinal));
        Assert.Equal(2, this._host.GameChatSince(0).Count(line => line.Channel == GameChatKind.Say));
    }

    [Fact]
    public async Task AOneOffThatGoesToTheWrongPlaceFailsWithItsStep() {
        this._host.OneOffsBroken = true;

        var run = await this.RunAsync();

        var failed = run.Data.Results.Single(result => result.Check == "local-chat-32-33");
        Assert.Equal(CheckOutcome.Failed, failed.Outcome);
        Assert.Equal("32", failed.Step);
        Assert.Equal("/s with text didn't arrive where it should", failed.Why);
        // The run goes on.
        Assert.Equal(CheckOutcome.Passed, run.Data.Results.Single(result => result.Check == "local-chat-37-39").Outcome);
    }
}
