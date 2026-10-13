using System.Text.RegularExpressions;
using LookingGlass.Core.Client;
using static LookingGlass.Core.SelfTest.SelfTestSteps;

namespace LookingGlass.Core.SelfTest.Suites;

/// <summary>
/// "Chat history and text files" (one player): chat-log-checklist.md 8 and 9 (with simulated lines: owner, 2026-10-13), 24
/// to 28 and 30 to 35, with the test channels' lines only. The runner reads the test channels' own text files (never
/// another channel's) and the history's size. If the history was on before the run, only the test's lines are removed at
/// the end; the checks that would cost the tester real history (deleting it) run only if it was off and empty before.
/// </summary>
public static partial class HistorySuite {
    public const string Id = "history";

    public static SelfTestSuite Create() => new(Id, "Chat history and text files", [TheSetting(), NewLines(), RenamesOffDelete(), LotsOfHistory()]) {
        Changes = [SelfTestSetting.KeepHistory, SelfTestSetting.KeepHistoryUnencrypted, SelfTestSetting.ChannelWindows],
        Planned = new Dictionary<SelfTestAction, int> { [SelfTestAction.ChannelMessage] = 3, [SelfTestAction.CreateChannel] = 2, [SelfTestAction.Rename] = 1 },
        Note = "1 relog",
    };

    private const SelfTestNeeds Needs = SelfTestNeeds.Connection | SelfTestNeeds.TestChannel;

    /// <summary>The history on, noting first whether it was off and empty before (then everything in it at the end is the test's).</summary>
    private static async Task HistoryOnAsync(StepContext context, bool unencrypted) {
        if (!context.Snapshotted(SelfTestSetting.KeepHistory)) {
            var environment = await context.EnvironmentAsync().ConfigureAwait(false);
            if (!environment.HistoryOn && environment.HistoryEmpty) {
                context.HistoryWasOffAndEmpty();
            }
        }

        await context.ChangeSettingAsync(SelfTestSetting.KeepHistory, true).ConfigureAwait(false);
        await context.ChangeSettingAsync(SelfTestSetting.KeepHistoryUnencrypted, unencrypted).ConfigureAwait(false);
    }

    /// <summary>A test channel's text folder now (found by its ID's hash, whatever its name), or null.</summary>
    private static async Task<string?> FolderOfAsync(StepContext context, string channelId) {
        var root = (await context.Host.HistoryAsync().ConfigureAwait(false)).TextFolder;
        if (root == null || !Directory.Exists(root)) {
            return null;
        }

        var hash = ChatLogText.ChannelHash(channelId);
        return Directory.EnumerateDirectories(root).FirstOrDefault(folder => Path.GetFileName(folder).EndsWith(" " + hash, StringComparison.Ordinal));
    }

    /// <summary>This month's file of a test channel, or null.</summary>
    private static async Task<string?> FileOfAsync(StepContext context, string channelId) =>
        await FolderOfAsync(context, channelId).ConfigureAwait(false) is { } folder ? Path.Combine(folder, ChatLogText.MonthOf(context.Host.Now, context.Host.TimeZone) + ".txt") : null;

    /// <summary>A file's lines, read as Notepad would (not in the way of the plugin writing it).</summary>
    private static string[] LinesOf(string path) {
        try {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return [];
        }
    }

    [GeneratedRegex(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}(, arrived \d{2}:\d{2})?\] \[[^\]]+\] ")]
    private static partial Regex LineFormat();

    private static async Task<string[]> WaitForLinesAsync(StepContext context, string channelId, Func<string[], bool> enough) {
        string[] lines = [];
        await context.WaitUntilAsync(async () => await FileOfAsync(context, channelId).ConfigureAwait(false) is { } file && enough(lines = LinesOf(file)), TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        return lines;
    }

    // ---------------------------------------------------------------- chat-log 24-25

    private static SelfTestCheck TheSetting() => new() {
        Id = "chat-log-24-25", Title = "The setting, and the warning first", Steps = "chat-log 24-25", Mode = CheckMode.Guided, Needs = SelfTestNeeds.Connection,
        StepList = [
            Auto("The history on, kept unencrypted off", "24", async context => {
                await HistoryOnAsync(context, unencrypted: false).ConfigureAwait(false);
                await context.Host.ShowSettingsAsync(true).ConfigureAwait(false);
                return StepResult.All((SelfTestWords.JargonIn(ChatLogWords.KeepUnencrypted.Plain).Count == 0, "the setting's name in simple mode isn't plain"),
                    (ChatLogWords.KeepUnencrypted.Technical.Contains("log", StringComparison.Ordinal), "advanced mode doesn't use its technical name"));
            }),
            Ask("The setting and its \"?\"", "24", new GuidedPrompt(
                "In Settings, under Chat history, after Size limit: is Keep my chat history unencrypted unticked, with a ? whose bubble says it also writes new messages to text files " +
                "any program can open, one per channel per month, and that anything that can read your files can read them?", PromptKind.YesNo) { Short = "the setting" }),
            Guided("It disappears while the history is off", "24", async context => {
                if (!context.HistoryOffAndEmptyBefore) {
                    return StepResult.Skip(SelfTestWords.HistoryWasYours);
                }

                var seenOff = false;
                return await context.AskForResultAsync(new GuidedPrompt(
                    "Untick Keep chat history on this computer: does Keep my chat history unencrypted disappear with the size slider? Press Cancel on the offer to delete, then tick it again.") {
                    Short = "hidden while off",
                    Watch = async () => {
                        var on = await context.SettingIsAsync(SelfTestSetting.KeepHistory).ConfigureAwait(false);
                        seenOff |= !on;
                        return new WatchUpdate(on ? (seenOff ? "on again" : "still on") : "off", seenOff && on);
                    },
                }).ConfigureAwait(false);
            }),
            Ask("The warning first, and Cancel", "25", new GuidedPrompt(
                "Tick Keep my chat history unencrypted: a window asks first. Does it say the files aren't encrypted, that anything that can read your files can read them (backup and " +
                "cloud-sync tools too), that nothing kept before is copied, that they count towards the size limit, and that Delete my chat history deletes them too? Press Cancel: " +
                "is it still unticked?", PromptKind.YesNo) { Short = "the warning" }),
            Auto("Cancel left it off", "25", async context =>
                StepResult.Expect(!await context.SettingIsAsync(SelfTestSetting.KeepHistoryUnencrypted).ConfigureAwait(false), "Cancel left it ticked")),
            Ask("Turn on", "25", context => Task.FromResult(new GuidedPrompt("Tick it again and press Turn on.") {
                Short = "Turn on", Watch = StepContextExtensions.Until(() => context.SettingIsAsync(SelfTestSetting.KeepHistoryUnencrypted), "not ticked yet", "ticked"),
            })),
            Ask("Open folder", "25", new GuidedPrompt("Is Open folder under it now?", PromptKind.YesNo) { Short = "Open folder" }),
        ],
    };

    // ---------------------------------------------------------------- chat-log 26-28

    private static SelfTestCheck NewLines() => new() {
        Id = "chat-log-26-28", Title = "Only new lines, one file per channel per month", Steps = "chat-log 26-28", Mode = CheckMode.Automatic,
        Needs = Needs | SelfTestNeeds.Typing,
        StepList = [
            Auto("Nothing kept before is converted", "26", async context => {
                var (sky, fc) = (await context.TestChannelAsync(0).ConfigureAwait(false), await context.TestChannelAsync(1).ConfigureAwait(false));
                var hadUnencrypted = await context.SettingIsAsync(SelfTestSetting.KeepHistoryUnencrypted).ConfigureAwait(false);
                await HistoryOnAsync(context, unencrypted: true).ConfigureAwait(false);
                if (hadUnencrypted) {
                    return StepResult.Pass("nothing converted (26): the text files were on before this check");
                }

                var converted = await FolderOfAsync(context, sky).ConfigureAwait(false) != null || await FolderOfAsync(context, fc).ConfigureAwait(false) != null;
                return converted ? StepResult.Fail("lines kept before turning it on were written to the text files") : StepResult.Pass("pressing Open folder (the runner reads the folder it opens)");
            }),
            Auto("A folder and a file per channel, lines in the format", "27", async context => {
                var (sky, fc) = (await context.TestChannelAsync(0).ConfigureAwait(false), await context.TestChannelAsync(1).ConfigureAwait(false));
                await context.StopTalkingAsync().ConfigureAwait(false);
                var withLink = context.NextMarker();
                await context.TypeAsync($"{await context.CommandOfAsync(sky).ConfigureAwait(false)} {withLink}", TestLink.ItemInMiddle, SelfTestAction.ChannelMessage).ConfigureAwait(false);
                var simulated = context.NextMarker();
                await context.Host.SimulateIncomingAsync(sky, simulated).ConfigureAwait(false);
                var other = context.NextMarker();
                await context.TypeAsync($"{await context.CommandOfAsync(fc).ConfigureAwait(false)} {other}", spends: SelfTestAction.ChannelMessage).ConfigureAwait(false);
                var skyLines = await WaitForLinesAsync(context, sky, lines => lines.Length >= 2).ConfigureAwait(false);
                var fcLines = await WaitForLinesAsync(context, fc, lines => lines.Length >= 1).ConfigureAwait(false);
                var name = (await context.Host.ChannelsAsync().ConfigureAwait(false)).FirstOrDefault(channel => channel.Id == sky)?.Name;
                var slot = await context.Host.SlotOfAsync(sky).ConfigureAwait(false);
                var folder = await FolderOfAsync(context, sky).ConfigureAwait(false);
                var linkLine = skyLines.FirstOrDefault(line => line.Contains(context.MarkerPrefix, StringComparison.Ordinal) && Regex.Matches(line, @"\[[^\]]+\]").Count >= 3);
                return StepResult.All(
                    (folder != null && Path.GetFileName(folder) == ChatLogText.ChannelFolderName(sky, name, slot), "the channel's folder isn't named by its name, number and code"),
                    (skyLines.Length >= 2 && skyLines.All(line => LineFormat().IsMatch(line)), "the channel's file doesn't have its lines in the format"),
                    (skyLines.Count(line => line.Contains(simulated, StringComparison.Ordinal)) == 1, "the simulated line isn't in the file once"),
                    (linkLine != null, "the line with an item link doesn't show the item as [name]"),
                    (fcLines.Length >= 1 && fcLines.All(line => !line.Contains(simulated, StringComparison.Ordinal)), "the second channel's file is missing, or has the first channel's lines"));
            }, types: true),
            Auto("Appending while open in another program; a deleted file starts again", "28", async context => {
                var sky = await context.TestChannelAsync(0).ConfigureAwait(false);
                var file = await FileOfAsync(context, sky).ConfigureAwait(false);
                if (file == null || !File.Exists(file)) {
                    return StepResult.Fail("the channel's file isn't there");
                }

                var before = LinesOf(file).Length;
                string[] after;
                // Held open as Notepad holds a file: reading, sharing it with writers.
                using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                    await context.Host.SimulateIncomingAsync(sky, context.NextMarker()).ConfigureAwait(false);
                    await context.Host.SimulateIncomingAsync(sky, context.NextMarker()).ConfigureAwait(false);
                    after = await WaitForLinesAsync(context, sky, lines => lines.Length >= before + 2).ConfigureAwait(false);
                }

                File.Delete(file);
                var last = context.NextMarker();
                await context.Host.SimulateIncomingAsync(sky, last).ConfigureAwait(false);
                var fresh = await WaitForLinesAsync(context, sky, lines => lines.Length >= 1).ConfigureAwait(false);
                return StepResult.All((after.Length == before + 2, "lines written while the file was open didn't reach it"),
                    (fresh.Length == 1 && fresh[0].Contains(last, StringComparison.Ordinal), "a deleted file didn't start again with the new line only"));
            }),
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
                if (!context.HistoryOffAndEmptyBefore) {
                    return StepResult.Skip(SelfTestWords.HistoryWasYours);
                }

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

    // ---------------------------------------------------------------- chat-log 8-9

    private static SelfTestCheck LotsOfHistory() => new() {
        Id = "chat-log-8-9", Title = "Lots of history, and new messages while reading", Steps = "chat-log 8-9 (group 2)", Mode = CheckMode.Guided, Needs = Needs,
        StepList = [
            Auto("250 simulated lines (nothing is sent)", "8", async context => {
                await HistoryOnAsync(context, unencrypted: await context.SettingIsAsync(SelfTestSetting.KeepHistoryUnencrypted).ConfigureAwait(false)).ConfigureAwait(false);
                var sky = await context.TestChannelAsync(0).ConfigureAwait(false);
                // Off game chat, so the lines don't fill it.
                await context.Host.SetShowInGameChatAsync(sky, false).ConfigureAwait(false);
                for (var i = 0; i < 250; i++) {
                    await context.Host.SimulateIncomingAsync(sky, context.NextMarker()).ConfigureAwait(false);
                }

                return StepResult.Pass();
            }),
            CarryOn(Relog, "8"),
            Ask("Scrolling up loads the next lines", "8", async context => {
                var sky = await context.TestChannelAsync(0).ConfigureAwait(false);
                if (await context.WindowOfAsync(sky, TimeSpan.FromSeconds(2)).ConfigureAwait(false) == null) {
                    await context.Host.OpenWindowAsync(sky).ConfigureAwait(false);
                }

                return new GuidedPrompt("In the test channel's window, scroll up with the mouse wheel to the top, and on: does each 200 load above without the line you read jumping? " +
                                        "At the start, does it say \"That's everything in your chat history for this channel.\"?", PromptKind.YesNo) { Short = "scrolling up" };
            }),
            Ask("Reading older lines", "9", new GuidedPrompt("Scroll up a little, so you're reading older lines, then press Done.") { Short = "reading" }),
            Ask("A new line while reading", "9", async context => {
                await context.Host.SimulateIncomingAsync(await context.TestChannelAsync(0).ConfigureAwait(false), context.NextMarker()).ConfigureAwait(false);
                return new GuidedPrompt("A new line just arrived: did the window stay where you were reading, with New messages showing? Click New messages: does it go to the bottom " +
                                        "and follow new lines again?", PromptKind.YesNo) { Short = "New messages" };
            }),
        ],
    };
}
