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
    internal static async Task<string?> FolderOfAsync(StepContext context, string channelId) =>
        (await FoldersOfAsync(context, channelId).ConfigureAwait(false)).FirstOrDefault();

    /// <summary>Every text folder of a test channel now (one, if all is well), by its ID's hash.</summary>
    internal static async Task<List<string>> FoldersOfAsync(StepContext context, string channelId) {
        var root = (await context.Host.HistoryAsync().ConfigureAwait(false)).TextFolder;
        if (root == null || !Directory.Exists(root)) {
            return [];
        }

        var hash = ChatLogText.ChannelHash(channelId);
        try {
            return Directory.EnumerateDirectories(root).Where(folder => Path.GetFileName(folder).EndsWith(" " + hash, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return [];
        }
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
            Auto("A rename: the folder follows, same code at the end", "30", RenameAsync),
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

    /// <summary>
    /// chat-log 30: a line, a rename, a line. Its folder (found by the code at its end) is renamed, and is its only one; its
    /// month file still starts with the lines it had, word for word (each keeps the tag it had then), and after them has
    /// the new line once and the rename's own information line once ("The channel is now called ...", kept as information
    /// lines are), nothing else. Not a count of lines: the rename's line made one more than the first build expected
    /// (tester's run, 0.2.18, 2026-10-13), and a count can't see lines lost and others added. Windows can't rename a folder
    /// while a file in it is open in a program; the plugin then goes on writing to it as it is and tries again with each
    /// line, so the check sends one more line, and if the folder still has its old name with every line in it, says so.
    /// </summary>
    private static async Task<StepResult> RenameAsync(StepContext context) {
        var sky = await context.TestChannelAsync(0).ConfigureAwait(false);
        await HistoryOnAsync(context, unencrypted: true).ConfigureAwait(false);
        var first = context.NextMarker();
        await context.Host.SimulateIncomingAsync(sky, first).ConfigureAwait(false);
        var older = await WaitForLinesAsync(context, sky, lines => lines.Any(line => line.Contains(first, StringComparison.Ordinal))).ConfigureAwait(false);
        if (!older.Any(line => line.Contains(first, StringComparison.Ordinal))) {
            return StepResult.Fail("the line before the rename didn't reach its file");
        }

        var renamed = $"{SelfTestRun.ChannelName(context.Code, 0)} r";
        await context.ServerAsync(SelfTestAction.Rename, () => context.Host.RenameChannelAsync(sky, renamed)).ConfigureAwait(false);
        await context.WaitUntilAsync(async () => (await context.Host.ChannelsAsync().ConfigureAwait(false)).Any(channel => channel.Id == sky && channel.Name == renamed))
            .ConfigureAwait(false);
        var after = new List<string> { context.NextMarker() };
        await context.Host.SimulateIncomingAsync(sky, after[0]).ConfigureAwait(false);
        var result = await AfterRenameAsync(context, sky, renamed, older, after).ConfigureAwait(false);
        if (result.Why == RenamePutOff) {
            // Put off while a file in it was open: tried again with its next line.
            after.Add(context.NextMarker());
            await context.Host.SimulateIncomingAsync(sky, after[^1]).ConfigureAwait(false);
            result = await AfterRenameAsync(context, sky, renamed, older, after).ConfigureAwait(false);
        }

        return result.Outcome == StepOutcome.Passed ? StepResult.Pass("a second channel with the same name (30)") : result;
    }

    internal const string RenamePutOff = "the folder couldn't be renamed yet (in use)";

    /// <summary>What a rename left in a channel's text files, as <see cref="RenameAsync"/> checks it, waiting up to 10 seconds for it all to be there.</summary>
    private static async Task<StepResult> AfterRenameAsync(StepContext context, string channelId, string renamed, string[] older, List<string> after) {
        var notice = " " + ChannelHistory.Renamed(renamed);
        var result = StepResult.Fail(SelfTestWords.TimedOut);
        await context.WaitUntilAsync(async () => {
            var folders = await FoldersOfAsync(context, channelId).ConfigureAwait(false);
            var lines = folders.Count == 1 ? LinesOf(Path.Combine(folders[0], ChatLogText.MonthOf(context.Host.Now, context.Host.TimeZone) + ".txt")) : [];
            var kept = lines.Length >= older.Length && lines.AsSpan(0, older.Length).SequenceEqual(older);
            var added = kept ? lines[older.Length..] : [];
            var allThere = kept && after.All(marker => added.Count(line => line.Contains(marker, StringComparison.Ordinal)) == 1);
            var isRenamed = folders.Count == 1 && Path.GetFileName(folders[0]).StartsWith(renamed + " ", StringComparison.Ordinal);
            result = StepResult.All(
                (folders.Count == 1, folders.Count == 0 ? "the channel's folder is gone" : "its lines went to two folders"),
                (isRenamed, allThere ? RenamePutOff : "the folder wasn't renamed"),
                (kept, "the older lines weren't kept in its file"),
                (allThere, "the line sent after the rename isn't in its file once"),
                (added.Count(line => line.EndsWith(notice, StringComparison.Ordinal)) == 1, "the line saying the channel's new name isn't in its file once"),
                (added.All(line => line.EndsWith(notice, StringComparison.Ordinal) || after.Any(marker => line.Contains(marker, StringComparison.Ordinal))),
                    "its file has lines that aren't the test's"));
            return result.Outcome == StepOutcome.Passed;
        }, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        return result;
    }
}
