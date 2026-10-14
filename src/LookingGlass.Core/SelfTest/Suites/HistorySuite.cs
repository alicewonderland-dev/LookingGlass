using System.Text.RegularExpressions;
using LookingGlass.Core.Client;
using static LookingGlass.Core.SelfTest.SelfTestSteps;

namespace LookingGlass.Core.SelfTest.Suites;

/// <summary>
/// "Chat history and text files" (one player): chat-log-checklist.md 8 and 9 (with simulated lines: owner, 2026-10-13), 24
/// to 28 and 30 to 35. Every check here runs only if the chat history was off with nothing in it before the run (the
/// owner's rule after review, 2026-10-13): turning it on, or its text files, records every channel, the tester's real ones
/// too, so only then is everything in it the test's, and the clean-up (or the next start, after a crash) deletes it whole.
/// The runner reads only the test channels' own text files.
/// </summary>
public static partial class HistorySuite {
    public const string Id = "history";

    public static SelfTestSuite Create() => new(Id, "Chat history and text files", [OpenFolder(), RenamesOffDelete()]) {
        Changes = [SelfTestSetting.KeepHistory, SelfTestSetting.KeepHistoryUnencrypted],
        Planned = new Dictionary<SelfTestAction, int> { [SelfTestAction.CreateChannel] = 1, [SelfTestAction.Rename] = 1 },
        Note = "Only if your chat history is off and empty",
    };

    /// <summary>Every check here records to the history: only when it was off and empty before the run (the owner's rule, see Decisions).</summary>
    private const SelfTestNeeds Needs = SelfTestNeeds.Connection | SelfTestNeeds.TestChannel | SelfTestNeeds.HistoryOffAndEmpty;

    /// <summary>
    /// The history on (every check here runs only if it was off and empty before the run, so everything in it is the test's,
    /// real channels' lines recorded meanwhile too, and the clean-up deletes it whole).
    /// </summary>
    internal static async Task HistoryOnAsync(StepContext context, bool unencrypted) {
        await context.ChangeSettingAsync(SelfTestSetting.KeepHistory, true).ConfigureAwait(false);
        await context.ChangeSettingAsync(SelfTestSetting.KeepHistoryUnencrypted, unencrypted).ConfigureAwait(false);
    }

    /// <summary>A test channel's text folder now (found by its ID's hash, whatever its name), or null.</summary>
    internal static async Task<string?> FolderOfAsync(StepContext context, string channelId) {
        var root = (await context.Host.HistoryAsync().ConfigureAwait(false)).TextFolder;
        if (root == null || !Directory.Exists(root)) {
            return null;
        }

        var hash = ChatLogText.ChannelHash(channelId);
        return Directory.EnumerateDirectories(root).FirstOrDefault(folder => Path.GetFileName(folder).EndsWith(" " + hash, StringComparison.Ordinal));
    }

    /// <summary>This month's file of a test channel, or null.</summary>
    internal static async Task<string?> FileOfAsync(StepContext context, string channelId) =>
        await FolderOfAsync(context, channelId).ConfigureAwait(false) is { } folder ? Path.Combine(folder, ChatLogText.MonthOf(context.Host.Now, context.Host.TimeZone) + ".txt") : null;

    /// <summary>A file's lines, read as Notepad would (not in the way of the plugin writing it).</summary>
    internal static string[] LinesOf(string path) {
        try {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return [];
        }
    }

    [GeneratedRegex(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}(, arrived \d{2}:\d{2})?\] \[[^\]]+\] ")]
    internal static partial Regex LineFormat();

    internal static async Task<string[]> WaitForLinesAsync(StepContext context, string channelId, Func<string[], bool> enough) {
        string[] lines = [];
        await context.WaitUntilAsync(async () => await FileOfAsync(context, channelId).ConfigureAwait(false) is { } file && enough(lines = LinesOf(file)), TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        return lines;
    }

    // ---------------------------------------------------------------- chat-log 26 (Open folder)

    private static SelfTestCheck OpenFolder() => new() {
        Id = "chat-log-26-open", Title = "Open folder", Steps = "chat-log 26 (pressing Open folder)", Mode = CheckMode.Guided,
        Needs = SelfTestNeeds.Connection | SelfTestNeeds.HistoryOffAndEmpty,
        StepList = [
            SetupAuto("The history on, and its text files", "26", async context => {
                await HistoryOnAsync(context, unencrypted: true).ConfigureAwait(false);
                await context.Host.ShowSettingsAsync(true).ConfigureAwait(false);
                return StepResult.Pass();
            }),
            Ask("Pressing Open folder", "26", new GuidedPrompt(
                "In Settings, under Chat history, press Open folder (under Keep my chat history unencrypted): does the file manager open a text folder inside this " +
                "character's chat history folder?", PromptKind.YesNo) { Short = "Open folder" }),
        ],
    };

    // ---------------------------------------------------------------- chat-log 30-35, 31b

    private static SelfTestCheck RenamesOffDelete() => new() {
        Id = "chat-log-30-35", Title = "Renames, turning it off, deleting", Steps = "chat-log 30-35, 31b", Mode = CheckMode.AutomaticAndGuided, Needs = Needs,
        StepList = [
            Auto("A rename: the folder follows, same code at the end", "30", async context => {
                var sky = await context.TestChannelAsync(0).ConfigureAwait(false);
                await HistoryOnAsync(context, unencrypted: true).ConfigureAwait(false);
                await context.Host.SimulateIncomingAsync(sky, context.NextMarker()).ConfigureAwait(false);
                await WaitForLinesAsync(context, sky, lines => lines.Length >= 1).ConfigureAwait(false);
                var before = LinesOf((await FileOfAsync(context, sky).ConfigureAwait(false))!).Length;
                var renamed = $"{SelfTestRun.ChannelName(context.Code, 0)} r";
                await context.ServerAsync(SelfTestAction.Rename, () => context.Host.RenameChannelAsync(sky, renamed)).ConfigureAwait(false);
                await context.WaitUntilAsync(async () => (await context.Host.ChannelsAsync().ConfigureAwait(false)).Any(channel => channel.Id == sky && channel.Name == renamed))
                    .ConfigureAwait(false);
                await context.Host.SimulateIncomingAsync(sky, context.NextMarker()).ConfigureAwait(false);
                await WaitForLinesAsync(context, sky, lines => lines.Length >= before + 1).ConfigureAwait(false);
                var folder = await FolderOfAsync(context, sky).ConfigureAwait(false);
                var lines = folder == null ? [] : LinesOf((await FileOfAsync(context, sky).ConfigureAwait(false))!);
                var result = StepResult.All((folder != null && Path.GetFileName(folder).StartsWith(renamed, StringComparison.Ordinal), "the folder wasn't renamed"),
                    (lines.Length == before + 1, "the older lines weren't kept in its file"));
                return result.Outcome == StepOutcome.Passed ? StepResult.Pass("a second channel with the same name (30)") : result;
            }),
            Auto("Its size grows with the files", "31", async context => {
                var sky = await context.TestChannelAsync(0).ConfigureAwait(false);
                var before = (await context.Host.HistoryAsync().ConfigureAwait(false)).Size;
                for (var i = 0; i < 5; i++) {
                    await context.Host.SimulateIncomingAsync(sky, context.NextMarker()).ConfigureAwait(false);
                }

                var grew = await context.WaitUntilAsync(async () => (await context.Host.HistoryAsync().ConfigureAwait(false)).Size > before, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                var result = StepResult.Expect(grew, "the history's size didn't grow");
                return result.Outcome == StepOutcome.Passed
                    ? StepResult.Pass("the 5 MB limit (31) and a file held by another program (31b): too slow for a run, and covered by the unit tests")
                    : result;
            }),
            Auto("Off: the text files stop changing", "32", async context => {
                var sky = await context.TestChannelAsync(0).ConfigureAwait(false);
                var file = (await FileOfAsync(context, sky).ConfigureAwait(false))!;
                await context.ChangeSettingAsync(SelfTestSetting.KeepHistoryUnencrypted, false).ConfigureAwait(false);
                var before = LinesOf(file).Length;
                await context.Host.SimulateIncomingAsync(sky, context.NextMarker()).ConfigureAwait(false);
                await context.DelayAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                return StepResult.All((LinesOf(file).Length == before, "the text file changed after turning it off"), (File.Exists(file), "the text file was deleted"));
            }),
            Guided("Delete my chat history", "33", async context => {
                var sky = await context.TestChannelAsync(0).ConfigureAwait(false);
                await context.ChangeSettingAsync(SelfTestSetting.KeepHistoryUnencrypted, true).ConfigureAwait(false);
                await context.Host.SimulateIncomingAsync(sky, context.NextMarker()).ConfigureAwait(false);
                await WaitForLinesAsync(context, sky, lines => lines.Length >= 1).ConfigureAwait(false);
                await context.Host.ShowSettingsAsync(true).ConfigureAwait(false);
                var answer = await context.AskForResultAsync(new GuidedPrompt(
                    "In Settings, press Delete my chat history...: does the confirmation say the text files go too? Confirm it.") {
                    Short = "Delete",
                    Watch = StepContextExtensions.Until(async () => await FolderOfAsync(context, sky).ConfigureAwait(false) == null, "still there", "deleted"),
                }).ConfigureAwait(false);
                if (answer.Outcome != StepOutcome.Passed) {
                    return answer;
                }

                var last = context.NextMarker();
                await context.Host.SimulateIncomingAsync(sky, last).ConfigureAwait(false);
                var fresh = await WaitForLinesAsync(context, sky, lines => lines.Length >= 1).ConfigureAwait(false);
                return StepResult.Expect(fresh.Length == 1 && fresh[0].Contains(last, StringComparison.Ordinal), "after deleting, the next line didn't start a new text file alone");
            }),
            Auto("/xllog names no channel, file or person", "35", context => {
                var result = StepResult.Expect(context.Host.LogCounts().ForbiddenHits == 0, "/xllog held a name, a channel's name or a test line");
                return Task.FromResult(result.Outcome == StepOutcome.Passed ? StepResult.Pass("another character's own folder (34)") : result);
            }),
        ],
    };

}
