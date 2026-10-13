using LookingGlass.Core.Client;
using static LookingGlass.Core.SelfTest.SelfTestSteps;
using static LookingGlass.Core.SelfTest.Suites.PairSteps;

namespace LookingGlass.Core.SelfTest.Suites;

/// <summary>
/// "Windows and counts, with a partner": channel-windows-checklist.md 17b, 17d, 33c, 33f, 36b and windows-only-checklist.md
/// 3, 4, 6, 10, 15b, 18, 19, 22, 25, with the partner's real lines (and leaves, and its one channel's invite) where the
/// one-player suite could only simulate them.
/// </summary>
public static class PairWindowsSuite {
    public const string Id = "pair-windows";

    public static SelfTestSuite Create() => new(Id, "Windows and counts, with a partner", [
        TabsAndNewWindows(), MissedOpenAWindow(), InformationLine(), NoCountInGameChat(), OnlyInAWindow(), WhileAway(), Cutscenes(), TurningOff(),
    ]) {
        Changes = [SelfTestSetting.WindowsOnly, SelfTestSetting.NewChannelsOpen, SelfTestSetting.ChannelWindows],
        Planned = new Dictionary<SelfTestAction, int> {
            [SelfTestAction.CreateChannel] = 2, [SelfTestAction.Invite] = 5, [SelfTestAction.Lookup] = 1, [SelfTestAction.PartnerMessage] = 45,
        },
        Note = "3 relogs; a cutscene for one check",
    };

    private const SelfTestNeeds Needs = PairSteps.Needs;

    private static Task WindowsOnlyAsync(StepContext context, bool on) => context.ChangeSettingAsync(SelfTestSetting.WindowsOnly, on);

    private static Task NewWindowsAsync(StepContext context, bool newWindow) =>
        context.ChangeSettingAsync(SelfTestSetting.NewChannelsOpen, newWindow ? (int) WindowOpening.NewWindow : (int) WindowOpening.AddToLastUsed);

    private static async Task<WindowsView> WindowsAsync(StepContext context) => await context.Host.WindowsAsync().ConfigureAwait(false);

    /// <summary>None of the channel's lines printed in game chat since the mark.</summary>
    private static bool NotInGameChat(StepContext context, long mark, string channelId) =>
        !context.Host.PrintedSince(mark).Any(line => line.Kind == TapKind.Message && line.ChannelId == channelId);

    // ---------------------------------------------------------------- channel-windows 17b, 17d; windows-only 3, 6

    private static SelfTestCheck TabsAndNewWindows() => new() {
        Id = "channel-windows-17b-17d", Title = "Messages from your partner in tabs and new windows", Steps = "channel-windows 17b, 17d; windows-only 3, 6",
        Mode = CheckMode.AutomaticAndGuided, Needs = Needs,
        StepList = [
            Auto("Three tabs: counts on the ones not selected", "17b", async context => {
                await WindowsOnlyAsync(context, false).ConfigureAwait(false);
                await CloseAllWindowsAsync(context).ConfigureAwait(false);
                var a = context.Partner.ChannelId!;
                var b = await ChannelWithPartnerAsync(context, 1).ConfigureAwait(false);
                var c = await context.TestChannelAsync(2).ConfigureAwait(false);
                await context.Host.OpenWindowAsync(c).ConfigureAwait(false);
                var window = await context.WindowOfAsync(c, WindowOpens).ConfigureAwait(false);
                if (window == null) {
                    return StepResult.Fail("no window opened");
                }

                await context.Host.AddTabAsync(window.LayoutId, a).ConfigureAwait(false);
                await context.Host.AddTabAsync(window.LayoutId, b).ConfigureAwait(false);
                await context.Host.SelectTabAsync(window.LayoutId, c).ConfigureAwait(false);
                await PartnerSendsAsync(context, 1, a).ConfigureAwait(false);
                await PartnerSendsAsync(context, 2, b).ConfigureAwait(false);
                var counted = await context.WaitUntilAsync(async () => (await WindowsAsync(context).ConfigureAwait(false)).TabCounts is var counts
                                                                       && counts.GetValueOrDefault(a) == 1 && counts.GetValueOrDefault(b) == 2).ConfigureAwait(false);
                context.State["layout"] = window.LayoutId;
                return StepResult.Expect(counted, "the tabs behind didn't count 1 and 2");
            }),
            Auto("Someone joining adds nothing", "17b", async context => {
                var b = context.TestChannels[1];
                if (!await PartnerLeavesAsync(context, b).ConfigureAwait(false) || !await context.Partner.InviteAndWaitAsync(context, b).ConfigureAwait(false)) {
                    return StepResult.Fail("your partner didn't leave and join again");
                }

                await context.DelayAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                return StepResult.Expect((await WindowsAsync(context).ConfigureAwait(false)).TabCounts.GetValueOrDefault(b) == 2, "a join changed the tab's count");
            }),
            Auto("Windows only: a window opens for their line, flashing three times", "windows-only 3", async context => {
                await WindowsOnlyAsync(context, true).ConfigureAwait(false);
                await CloseAllWindowsAsync(context).ConfigureAwait(false);
                var a = context.Partner.ChannelId!;
                var mark = context.Host.Mark();
                await PartnerSendsAsync(context, 1, a).ConfigureAwait(false);
                var window = await context.WindowOfAsync(a, WindowOpens).ConfigureAwait(false);
                return StepResult.All((window != null, "no window opened"), (NotInGameChat(context, mark, a), "it showed in game chat"),
                    (window?.Tabs.Count == 1, "the window had more than its one tab"), (window?.FlashPulses == 3, "it didn't flash three times"),
                    (window?.TookFocus == false, "it took the keyboard"));
            }),
            Auto("They leave and are invited back: a tab behind, one flash", "windows-only 6", async context => {
                var a = context.Partner.ChannelId!;
                var b = context.TestChannels[1];
                if (!await PartnerLeavesAsync(context, b).ConfigureAwait(false) || !await context.Partner.InviteAndWaitAsync(context, b).ConfigureAwait(false)) {
                    return StepResult.Fail("your partner didn't leave and join again");
                }

                WindowView? window = null;
                await context.WaitUntilAsync(async () => (window = (await WindowsAsync(context).ConfigureAwait(false)).Showing(a)) is { } found && found.Tabs.Contains(b), WindowOpens)
                    .ConfigureAwait(false);
                return StepResult.All((window?.Tabs.Contains(b) == true, "no tab was added for the other test channel"), (window?.Selected == a, "the selected tab changed"),
                    (window?.FlashPulses == 1, "the window didn't flash once"));
            }),
            .. RelogWhileThePartnerSends("17d", 2, context => Task.FromResult(context.TestChannels[1])),
            Auto("The tab behind counts what came since login", "17d", async context => {
                var b = context.TestChannels[1];
                var sent = (int) context.State["away"];
                var counted = await context.WaitUntilAsync(async () => (await WindowsAsync(context).ConfigureAwait(false)) is var windows
                                                                       && (windows.TabCounts.GetValueOrDefault(b) == sent || windows.ListCounts.GetValueOrDefault(b) == sent),
                    TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                return StepResult.Expect(counted, "the tab didn't count the messages from while you were away");
            }),
            Ask("Selecting it clears it", "17d", context => Task.FromResult(new GuidedPrompt("Click the tab with the count.") {
                Short = "cleared",
                Watch = StepContextExtensions.Until(async () => (await WindowsAsync(context).ConfigureAwait(false)).TabCounts.GetValueOrDefault(context.TestChannels[1]) == 0,
                    "the count is still there", "the count went"),
            })),
        ],
    };

    // ---------------------------------------------------------------- channel-windows 33c (second part)

    private static SelfTestCheck MissedOpenAWindow() => new() {
        Id = "channel-windows-33c", Title = "Missed messages open a window for a channel off game chat", Steps = "channel-windows 33c (second part)", Mode = CheckMode.Guided,
        Needs = Needs,
        StepList = [
            Auto("The test channel off game chat, no window", "33c", async context => {
                await WindowsOnlyAsync(context, false).ConfigureAwait(false);
                await context.Host.SetShowInGameChatAsync(context.Partner.ChannelId!, false).ConfigureAwait(false);
                await CloseAllWindowsAsync(context).ConfigureAwait(false);
                return StepResult.Pass();
            }),
            .. RelogWhileThePartnerSends("33c", 2),
            Auto("Not in game chat; a window with them", "33c", async context => {
                var a = context.Partner.ChannelId!;
                var window = await context.WindowOfAsync(a, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                var mark = (long) context.State["away-mark"];
                await context.Host.SetShowInGameChatAsync(a, true).ConfigureAwait(false);
                return StepResult.All((window != null, "no window opened for the missed messages"), (NotInGameChat(context, mark, a), "they showed in game chat"),
                    (CaughtUp(context) >= 1, "nothing was caught up"));
            }),
        ],
    };

    // ---------------------------------------------------------------- channel-windows 33f (second part)

    private static SelfTestCheck InformationLine() => new() {
        Id = "channel-windows-33f", Title = "An information line alone opens a window for a channel off game chat", Steps = "channel-windows 33f (second part)",
        Mode = CheckMode.Automatic, Needs = Needs,
        StepList = [
            Auto("Your partner leaves: a window opens, flashing", "33f", async context => {
                var a = context.Partner.ChannelId!;
                await WindowsOnlyAsync(context, false).ConfigureAwait(false);
                await context.Host.SetShowInGameChatAsync(a, false).ConfigureAwait(false);
                await CloseAllWindowsAsync(context).ConfigureAwait(false);
                var mark = context.Host.Mark();
                var left = await PartnerLeavesAsync(context, a).ConfigureAwait(false);
                var window = await context.WindowOfAsync(a, WindowOpens).ConfigureAwait(false);
                var inChat = context.Host.PrintedSince(mark).Any(line => line.Kind == TapKind.Notice && line.ChannelId == a);
                await context.Host.SetShowInGameChatAsync(a, true).ConfigureAwait(false);
                var back = await context.Partner.InviteAndWaitAsync(context, a).ConfigureAwait(false);
                return StepResult.All((left, "your partner didn't leave"), (window != null, "no window opened for the line"), (window?.FlashPulses > 0, "it didn't flash"),
                    (!inChat, "the line showed in game chat"), (back, "your partner didn't join again"));
            }),
        ],
    };

    // ---------------------------------------------------------------- channel-windows 36b (group 1)

    private static SelfTestCheck NoCountInGameChat() => new() {
        Id = "channel-windows-36b", Title = "No count for channels shown in game chat", Steps = "channel-windows 36b (group 1)", Mode = CheckMode.Automatic, Needs = Needs,
        StepList = [
            Auto("Shown in game chat: no count", "36b", async context => {
                var a = context.Partner.ChannelId!;
                await WindowsOnlyAsync(context, false).ConfigureAwait(false);
                await context.Host.SetShowInGameChatAsync(a, true).ConfigureAwait(false);
                await CloseAllWindowsAsync(context).ConfigureAwait(false);
                await PartnerSendsAsync(context, 1, a).ConfigureAwait(false);
                await context.DelayAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                return StepResult.Expect((await WindowsAsync(context).ConfigureAwait(false)).ListCounts.GetValueOrDefault(a) == 0, "the channel list counted a channel shown in game chat");
            }),
            Auto("Off game chat, window closed: counted, and a window opens", "36b", async context => {
                var a = context.Partner.ChannelId!;
                await context.Host.SetShowInGameChatAsync(a, false).ConfigureAwait(false);
                await PartnerSendsAsync(context, 1, a).ConfigureAwait(false);
                var counted = await context.WaitUntilAsync(async () => (await WindowsAsync(context).ConfigureAwait(false)) is var windows
                                                                       && (windows.ListCounts.GetValueOrDefault(a) > 0 || windows.Showing(a) != null), WindowOpens).ConfigureAwait(false);
                await context.Host.SetShowInGameChatAsync(a, true).ConfigureAwait(false);
                return StepResult.Expect(counted, "it wasn't counted, and no window opened");
            }),
        ],
    };

    // ---------------------------------------------------------------- windows-only 3-4, 6, 10

    private static SelfTestCheck OnlyInAWindow() => new() {
        Id = "windows-only-3-10", Title = "Messages from your partner go only to a window", Steps = "windows-only 3-4, 6, 10", Mode = CheckMode.AutomaticAndGuided, Needs = Needs,
        StepList = [
            Auto("Their line: only a window", "3", async context => {
                var a = context.Partner.ChannelId!;
                await WindowsOnlyAsync(context, true).ConfigureAwait(false);
                await NewWindowsAsync(context, false).ConfigureAwait(false);
                await CloseAllWindowsAsync(context).ConfigureAwait(false);
                var mark = context.Host.Mark();
                await PartnerSendsAsync(context, 1, a).ConfigureAwait(false);
                var window = await context.WindowOfAsync(a, WindowOpens).ConfigureAwait(false);
                return StepResult.All((window != null, "no window opened"), (NotInGameChat(context, mark, a), "it showed in game chat"));
            }),
            Ask("It doesn't take the keyboard", "3", new GuidedPrompt(
                "Walk with the movement keys now, and keep walking: your partner's next line opens nothing new, and what you press never goes into the window. Did you keep walking?",
                PromptKind.YesNo) { Short = "kept walking" }),
            Auto("Again: no second window or tab", "4", async context => {
                var a = context.Partner.ChannelId!;
                var before = await WindowsAsync(context).ConfigureAwait(false);
                await PartnerSendsAsync(context, 1, a).ConfigureAwait(false);
                await context.DelayAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                var after = await WindowsAsync(context).ConfigureAwait(false);
                return StepResult.Expect(after.Windows.Count == before.Windows.Count && after.Showing(a)?.Tabs.Count == before.Showing(a)?.Tabs.Count, "another window or tab opened");
            }),
            Auto("They leave and are invited back: a tab, not in game chat", "6", async context => {
                var a = context.Partner.ChannelId!;
                var b = await ChannelWithPartnerAsync(context, 1).ConfigureAwait(false);
                var mark = context.Host.Mark();
                if (!await PartnerLeavesAsync(context, b).ConfigureAwait(false) || !await context.Partner.InviteAndWaitAsync(context, b).ConfigureAwait(false)) {
                    return StepResult.Fail("your partner didn't leave and join again");
                }

                WindowView? window = null;
                await context.WaitUntilAsync(async () => (window = (await WindowsAsync(context).ConfigureAwait(false)).Showing(a)) is { } found && found.Tabs.Contains(b), WindowOpens)
                    .ConfigureAwait(false);
                var inChat = context.Host.PrintedSince(mark).Any(line => line.ChannelId == b);
                return StepResult.All((window?.Tabs.Contains(b) == true, "no tab was added"), (window?.Selected == a, "the selected tab changed"), (!inChat, "the lines showed in game chat"));
            }),
            Auto("Their invite to a channel of theirs shows in game chat", "10", async context => {
                var mark = context.Host.Mark();
                await context.Partner.AskAsync(context, PartnerVerb.InviteMe).ConfigureAwait(false);
                var told = await context.PrintedAsync(mark, line => line.Kind == TapKind.Notice && line.Text.Contains("invited you", StringComparison.Ordinal), TimeSpan.FromSeconds(30))
                    .ConfigureAwait(false);
                // Its one invite: accepted (only it), and left at the end.
                var joined = await context.Partner.AcceptOwnChannelAsync(context).ConfigureAwait(false);
                return StepResult.All((told != null, "the invite didn't show in game chat"), (joined != null, "their invite didn't arrive"));
            }),
        ],
    };

    // ---------------------------------------------------------------- windows-only 15b, 25

    private static SelfTestCheck WhileAway() => new() {
        Id = "windows-only-15b-25", Title = "Messages from while you were away", Steps = "windows-only 15b, 25", Mode = CheckMode.Guided, Needs = Needs,
        StepList = [
            Auto("In a new window, every window closed", "15b", async context => {
                await ChannelWithPartnerAsync(context, 1).ConfigureAwait(false);
                await WindowsOnlyAsync(context, true).ConfigureAwait(false);
                await NewWindowsAsync(context, true).ConfigureAwait(false);
                await CloseAllWindowsAsync(context).ConfigureAwait(false);
                return StepResult.Pass();
            }),
            .. RelogWhileThePartnerSends("15b", 1, also: "b"),
            Auto("One window with both as tabs, nothing in game chat", "15b", async context => {
                var a = context.Partner.ChannelId!;
                var b = context.TestChannels[1];
                WindowsView? windows = null;
                await context.WaitUntilAsync(async () => (windows = await WindowsAsync(context).ConfigureAwait(false)).Showing(a) is { } found && found.Tabs.Contains(b),
                    TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                var mark = (long) context.State["away-mark"];
                return StepResult.All((windows?.Windows.Count == 1, "not one window"), (windows?.Showing(a)?.Tabs.Contains(b) == true, "the window didn't have both as tabs"),
                    (NotInGameChat(context, mark, a) && NotInGameChat(context, mark, b), "a caught-up message showed in game chat"));
            }),
            Ask("Back where they were", "25", new GuidedPrompt("Did the setting stay on, and is the window where windows opened before?", PromptKind.YesNo) { Short = "kept" }),
        ],
    };

    // ---------------------------------------------------------------- windows-only 18-19

    private static SelfTestCheck Cutscenes() => new() {
        Id = "windows-only-18-19", Title = "Nothing pops up in cutscenes or loading screens", Steps = "windows-only 18-19", Mode = CheckMode.Guided, Needs = Needs,
        StepList = [
            Guided("A cutscene: nothing opens until it ends", "18", async context => {
                var a = context.Partner.ChannelId!;
                await WindowsOnlyAsync(context, true).ConfigureAwait(false);
                await CloseAllWindowsAsync(context).ConfigureAwait(false);
                var started = await context.AskAsync(new GuidedPrompt(
                    "Watch a cutscene: in an inn room, the Unending Journey (or any quest cutscene). The self-test sees it start, and your partner sends a line then.") {
                    Watch = StepContextExtensions.Until(() => context.Host.BusyAsync(), "no cutscene yet", "a cutscene"),
                }).ConfigureAwait(false);
                if (started != GuidedAnswer.Seen) {
                    return StepContext.Judge(started, null) is { Outcome: StepOutcome.Skipped } skipped ? skipped : StepResult.Skip("the cutscene wasn't seen");
                }

                await PartnerSendsAsync(context, 1, a).ConfigureAwait(false);
                var openedDuring = false;
                await context.WaitUntilAsync(async () => {
                    var busy = await context.Host.BusyAsync().ConfigureAwait(false);
                    openedDuring |= busy && (await WindowsAsync(context).ConfigureAwait(false)).Showing(a) != null;
                    return !busy;
                }, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
                var window = await context.WindowOfAsync(a, WindowOpens).ConfigureAwait(false);
                return StepResult.All((!openedDuring, "a window opened during the cutscene"), (window != null, "no window opened once it ended"));
            }),
            Auto("The rest", "19", _ => Task.FromResult(StepResult.Pass("a loading screen (a door between private places, or a teleport, which leaves them)"))),
        ],
    };

    // ---------------------------------------------------------------- windows-only 22

    private static SelfTestCheck TurningOff() => new() {
        Id = "windows-only-22", Title = "Turning the setting off", Steps = "windows-only 22", Mode = CheckMode.Automatic, Needs = Needs,
        StepList = [
            Auto("Unticked: their next line in game chat at once, and in its window", "22", async context => {
                var a = context.Partner.ChannelId!;
                await WindowsOnlyAsync(context, true).ConfigureAwait(false);
                await context.Host.SetShowInGameChatAsync(a, true).ConfigureAwait(false);
                var shown = await context.Host.ShowsInGameChatAsync(a).ConfigureAwait(false);
                if (await context.WindowOfAsync(a, TimeSpan.Zero).ConfigureAwait(false) == null) {
                    await context.Host.OpenWindowAsync(a).ConfigureAwait(false);
                }

                await WindowsOnlyAsync(context, false).ConfigureAwait(false);
                var mark = context.Host.Mark();
                await PartnerSendsAsync(context, 1, a).ConfigureAwait(false);
                var inChat = await context.WaitUntilAsync(() => Task.FromResult(PrintedFromPartner(context, mark, a).Count == 1), Soon).ConfigureAwait(false);
                var window = await context.WindowOfAsync(a, WindowOpens).ConfigureAwait(false);
                return StepResult.All((inChat, "their line didn't show in game chat"), (window != null, "their line's window wasn't there"),
                    (await context.Host.ShowsInGameChatAsync(a).ConfigureAwait(false) == shown, "the channel's own Show in game chat changed"));
            }),
        ],
    };
}
