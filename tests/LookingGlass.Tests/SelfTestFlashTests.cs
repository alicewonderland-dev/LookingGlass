using LookingGlass.Core.Client;
using LookingGlass.Core.SelfTest;
using LookingGlass.Core.SelfTest.Suites;

namespace LookingGlass.Tests;

/// <summary>
/// The flash checks (channel-windows 37c-37f): a "Ready?" before each flash says what is about to happen and where to
/// look, and the question after it can show the same flash again, as often as wanted, on the same window, sending nothing.
/// </summary>
public sealed class SelfTestFlashTests : IDisposable {
    private readonly FakeSelfTestHost _host = new();

    public void Dispose() => this._host.Dispose();

    [Fact]
    public async Task AReadyFirstThenTheFlashCanBeShownAgain() {
        var suites = new[] { WindowsSuite.Create() };
        var run = new SelfTestRun(this._host, suites, new SelfTestPacer(() => this._host.Now, this._host.Delay), this._host.File,
            SelfTestRun.NewData("K7Q2", 1, "x", this._host.Now, suites, ["channel-windows-37c-37f"]));
        // The window that opens by itself for the first test channel, flashing three times.
        this._host.OpenWindows = () => this._host.Created.Count == 0
            ? new WindowsView([], new Dictionary<string, int>(), new Dictionary<string, int>())
            : new WindowsView([new WindowView("w1", [this._host.Created[0]], this._host.Created[0], null, WindowFlash.OpenedPulses, false, 1)],
                new Dictionary<string, int>(), new Dictionary<string, int>());
        var asked = new List<string>();
        this._host.OnTick = () => {
            if (run.View.Prompt is not { } prompt || asked.LastOrDefault() == prompt.Prompt.Text) {
                return;
            }

            asked.Add(prompt.Prompt.Text);
            if (prompt.Prompt.Again != null) {
                // Shown again twice, then answered.
                run.ShowAgain();
                run.ShowAgain();
            }

            run.Answer(prompt.Prompt.Kind == PromptKind.YesNo ? GuidedAnswer.Yes : GuidedAnswer.Done);
        };

        await run.RunAsync();

        Assert.StartsWith("Ready?", asked[0], StringComparison.Ordinal);
        Assert.Contains("three blinks", asked[0], StringComparison.Ordinal);
        Assert.Contains("three distinct blinks", asked[1], StringComparison.Ordinal);
        Assert.Contains("border a few pixels wide", asked[1], StringComparison.Ordinal);
        // The same window, flashed again, three blinks each time; nothing sent, no new window.
        Assert.Equal([(this._host.Created[0], WindowFlash.OpenedPulses), (this._host.Created[0], WindowFlash.OpenedPulses)], this._host.FlashedAgain);
        Assert.Empty(this._host.Sent);
        // The tab step is next: its own "Ready?", saying one blink.
        Assert.Contains(asked, text => text.StartsWith("Ready?", StringComparison.Ordinal) && text.Contains("one blink", StringComparison.Ordinal));
    }
}
