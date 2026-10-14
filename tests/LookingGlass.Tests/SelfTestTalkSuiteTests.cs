using System.Text.Json;
using LookingGlass.Core.Client;
using LookingGlass.Core.SelfTest;
using LookingGlass.Core.SelfTest.Suites;

namespace LookingGlass.Tests;

/// <summary>
/// The talking suite (local-chat-checklist.md 1 to 3 and 29, what is left of it) against the fake host's stand-in for the
/// plugin: its checks pass when the plugin behaves as the checklist says. The "tester" here does what each question asks.
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
        Assert.Equal(["local-chat-1-3", "local-chat-29"], results.Keys.Order());
        foreach (var (id, result) in results) {
            Assert.True(result.Outcome == CheckOutcome.Passed, $"{id}: {result.Outcome} {result.Why} (step {result.Step})");
        }

        // Alone, nothing reached Say.
        Assert.DoesNotContain(this._host.GameChatSince(0), line => line.Channel == GameChatKind.Say);
    }
}
