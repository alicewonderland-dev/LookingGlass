using LookingGlass.Core.Client;
using LookingGlass.Core.SelfTest;
using LookingGlass.Core.SelfTest.Suites;

namespace LookingGlass.Tests;

/// <summary>
/// The history suite's rename check (chat-log 30) against the plugin's real chat history and text files: a tester's run
/// (0.2.18, 2026-10-13) failed it with "the older lines weren't kept in its file" while the folder had been renamed and
/// every line was there. The check counted one new line, but a rename adds its own ("The channel is now called ..."), as
/// information lines always are; it now compares the lines themselves. A folder in use can't be renamed on Windows: the
/// check says so, rather than that lines were lost.
/// </summary>
public sealed class SelfTestHistoryRenameTests : IDisposable {
    private readonly FakeSelfTestHost _host = new() { HistoryEmpty = true };

    public SelfTestHistoryRenameTests() => this._host.UseRealHistory();

    public void Dispose() => this._host.Dispose();

    /// <summary>Only the rename step (the rest of its check asks the tester).</summary>
    private SelfTestRun NewRun() {
        var history = HistorySuite.Create();
        var full = history.Checks.Single(check => check.Id == "chat-log-30-35");
        var suite = new SelfTestSuite(HistorySuite.Id, history.Title, [full with { Mode = CheckMode.Automatic, StepList = [full.StepList[0]] }]) {
            Changes = history.Changes, Planned = history.Planned,
        };
        var data = SelfTestRun.NewData("K7Q2", 42, "wss://example.test/ws", this._host.Now, [suite]);
        return new SelfTestRun(this._host, [suite], new SelfTestPacer(() => this._host.Now, this._host.Delay, this._host.File.ReadSpent(), data.Spent), this._host.File,
            data, false);
    }

    private string Hash => ChatLogText.ChannelHash(this._host.Created[0]);

    /// <summary>The test channel's folders now (by its code at the end).</summary>
    private string[] FoldersOfTheChannel() =>
        Directory.GetDirectories(this._host.ChatLog!.TextFolder).Where(folder => folder.EndsWith(" " + this.Hash, StringComparison.Ordinal)).ToArray();

    private string[] LinesOfTheChannel() {
        var folder = Assert.Single(this.FoldersOfTheChannel());
        return HistorySuite.LinesOf(Path.Combine(folder, ChatLogText.MonthOf(this._host.Now, this._host.TimeZone) + ".txt"));
    }

    [Fact]
    public async Task ARenamedFolderWithItsOlderLinesAndTheRenamesOwnLinePasses() {
        var run = this.NewRun();

        await run.RunAsync();

        var result = run.Data.Results.Single();
        Assert.True(result.Outcome == CheckOutcome.Passed, result.Why);
        Assert.StartsWith(SelfTestRun.ChannelName("K7Q2", 0) + " r", Path.GetFileName(Assert.Single(this.FoldersOfTheChannel())));
        // What the tester's run saw: the line before, the rename's own line, then the line after.
        var lines = this.LinesOfTheChannel();
        Assert.Equal(3, lines.Length);
        Assert.EndsWith(ChannelHistory.Renamed(SelfTestRun.ChannelName("K7Q2", 0) + " r"), lines[1]);
    }

    [Fact]
    public async Task AFolderInUseIsSaidSoAndNoLineIsLostOrSplit() {
        // A file in the channel's folder held open from before the rename to the end (a program that keeps it open).
        FileStream? held = null;
        this._host.BeforeRename = () => held ??= new FileStream(Path.Combine(Assert.Single(this.FoldersOfTheChannel()), ChatLogText.MonthOf(this._host.Now, this._host.TimeZone) + ".txt"),
            FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var run = this.NewRun();

        try {
            await run.RunAsync();
        } finally {
            held?.Dispose();
        }

        var result = run.Data.Results.Single();
        Assert.Equal((CheckOutcome.Failed, "the folder couldn't be renamed yet (in use)"), (result.Outcome, result.Why));
        // Its lines all went on in the folder it has.
        Assert.StartsWith(SelfTestRun.ChannelName("K7Q2", 0) + " (LGC", Path.GetFileName(Assert.Single(this.FoldersOfTheChannel())));
        var lines = this.LinesOfTheChannel();
        Assert.Equal(4, lines.Length);
        Assert.EndsWith(ChannelHistory.Renamed(SelfTestRun.ChannelName("K7Q2", 0) + " r"), lines[1]);
    }

    [Fact]
    public async Task AFolderLetGoOfWhileTheCheckWaitsIsRenamedWithTheNextLineAndPasses() {
        FileStream? held = null;
        var letGoAt = DateTimeOffset.MaxValue;
        this._host.BeforeRename = () => {
            // Held for a few seconds, as a virus scanner may right after a write.
            held = new FileStream(Path.Combine(Assert.Single(this.FoldersOfTheChannel()), ChatLogText.MonthOf(this._host.Now, this._host.TimeZone) + ".txt"),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            letGoAt = this._host.Now.AddSeconds(4);
        };
        this._host.OnTick = () => {
            if (this._host.Now >= letGoAt) {
                held?.Dispose();
            }
        };
        var run = this.NewRun();

        try {
            await run.RunAsync();
        } finally {
            held?.Dispose();
        }

        var result = run.Data.Results.Single();
        Assert.True(result.Outcome == CheckOutcome.Passed, result.Why);
        Assert.StartsWith(SelfTestRun.ChannelName("K7Q2", 0) + " r", Path.GetFileName(Assert.Single(this.FoldersOfTheChannel())));
    }

    [Fact]
    public async Task OlderLinesThatAreGoneStillFail() {
        // A product that lost the older lines at the rename: the check must still catch it.
        this._host.BeforeRename = () => File.Delete(Path.Combine(Assert.Single(this.FoldersOfTheChannel()), ChatLogText.MonthOf(this._host.Now, this._host.TimeZone) + ".txt"));
        var run = this.NewRun();

        await run.RunAsync();

        var result = run.Data.Results.Single();
        Assert.Equal((CheckOutcome.Failed, "the older lines weren't kept in its file"), (result.Outcome, result.Why));
    }
}
