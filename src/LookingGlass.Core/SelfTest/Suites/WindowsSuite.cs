using LookingGlass.Core.Client;
using static LookingGlass.Core.SelfTest.SelfTestSteps;

namespace LookingGlass.Core.SelfTest.Suites;

/// <summary>
/// "Windows and counts" (one player): windows-only-checklist.md and channel-windows-checklist.md, with "a message arrives"
/// made by a simulated incoming line in a test channel (local only, as the checklists say to do with /lgdebug). Judged
/// from the windows the plugin has open, their tabs, places, flashes and counts, and what it printed; what only the
/// tester's eyes can tell (a greyed menu item, a flash's look) is guided. The tester's own windows are closed for a while
/// and opened again where they were.
/// </summary>
public static class WindowsSuite {
    public const string Id = "windows";

    private static readonly TimeSpan Open = TimeSpan.FromSeconds(8);

    public static SelfTestSuite Create() => new(Id, "Windows and counts", [
        TheSetting(), OwnLines(), Warnings(), WhichWindow(), Combat(), Locked(), TurningOff(), Counts(), OffGameChat(), ListCounts(), Flash(), NoErrors(),
    ]) {
        Changes = [SelfTestSetting.WindowsOnly, SelfTestSetting.NewChannelsOpen, SelfTestSetting.SayWhenTalking, SelfTestSetting.ChannelWindows],
        Planned = new Dictionary<SelfTestAction, int> { [SelfTestAction.ChannelMessage] = 8, [SelfTestAction.CreateChannel] = 3, [SelfTestAction.Reconnect] = 1 },
        Note = "2 relogs, a striking dummy for one check",
    };

    private const SelfTestNeeds Needs = SelfTestNeeds.Connection | SelfTestNeeds.TestChannel;

    /// <summary>Closes every channel window (the tester's come back at the end, where they were).</summary>
    private static async Task CloseAllAsync(StepContext context) {
        await context.ChangeSettingAsync(SelfTestSetting.ChannelWindows, Array.Empty<ChannelWindowLayout>()).ConfigureAwait(false);
        await context.WaitUntilAsync(async () => (await context.Host.WindowsAsync().ConfigureAwait(false)).Windows.Count == 0).ConfigureAwait(false);
    }

    private static Task WindowsOnlyAsync(StepContext context, bool on) => context.ChangeSettingAsync(SelfTestSetting.WindowsOnly, on);

    private static Task NewWindowsAsync(StepContext context, bool newWindow) => context.ChangeSettingAsync(SelfTestSetting.NewChannelsOpen, newWindow ? (int) WindowOpening.NewWindow : (int) WindowOpening.AddToLastUsed);

    /// <summary>A message arrives in a test channel: a simulated incoming line, local only.</summary>
    private static Task ArriveAsync(StepContext context, string channelId) => context.Host.SimulateIncomingAsync(channelId, context.NextMarker());

    /// <summary>Opens a test channel in a new window, as the channel list's menu does, and waits for it.</summary>
    private static async Task<WindowView?> OpenAsync(StepContext context, string channelId) {
        var before = (await context.Host.WindowsAsync().ConfigureAwait(false)).Windows.Select(window => window.LayoutId).ToHashSet();
        await context.Host.OpenWindowAsync(channelId).ConfigureAwait(false);
        WindowView? opened = null;
        await context.WaitUntilAsync(async () => (opened = (await context.Host.WindowsAsync().ConfigureAwait(false)).Windows
            .FirstOrDefault(window => !before.Contains(window.LayoutId) && window.Tabs.Contains(channelId))) != null).ConfigureAwait(false);
        return opened;
    }

    private static async Task<int> TabCountAsync(StepContext context, string channelId) =>
        (await context.Host.WindowsAsync().ConfigureAwait(false)).TabCounts.GetValueOrDefault(channelId);

    private static async Task<int> ListCountAsync(StepContext context, string channelId) =>
        (await context.Host.WindowsAsync().ConfigureAwait(false)).ListCounts.GetValueOrDefault(channelId);

    private static Task<bool> TabCountIsAsync(StepContext context, string channelId, int count) =>
        context.WaitUntilAsync(async () => await TabCountAsync(context, channelId).ConfigureAwait(false) == count);

    private static Task<bool> ListCountIsAsync(StepContext context, string channelId, int count) =>
        context.WaitUntilAsync(async () => await ListCountAsync(context, channelId).ConfigureAwait(false) == count);

    /// <summary>No message of the channel printed in game chat since the mark.</summary>
    private static async Task<bool> NotInGameChatAsync(StepContext context, long mark, string channelId) =>
        await context.CountPrintedAsync(mark, line => line.Kind == TapKind.Message && line.ChannelId == channelId).ConfigureAwait(false) == 0;

    // ---------------------------------------------------------------- windows-only 1-2

    private static SelfTestCheck TheSetting() => new() {
        Id = "windows-only-1-2", Title = "The setting itself", Steps = "windows-only 1-2", Mode = CheckMode.Guided,
        StepList = [
            Auto("The setting off, no windows open", null, async context => {
                await WindowsOnlyAsync(context, false).ConfigureAwait(false);
                await NewWindowsAsync(context, false).ConfigureAwait(false);
                await CloseAllAsync(context).ConfigureAwait(false);
                await context.Host.ShowSettingsAsync(true).ConfigureAwait(false);
                return StepResult.Expect(SettingsWords.Help(SettingHelp.WindowsOnly, "").Plain == WindowsOnly.HelpText, "the \"?\" words aren't windows only's");
            }),
            Ask("The setting and its \"?\"", "1", new GuidedPrompt(
                "In Settings, under Chat, after Say when I start or stop talking in a channel: is Show LookingGlass only in windows unticked, with a ? whose bubble says " +
                "messages appear only in windows while warnings and replies still show in game chat, and under it, greyed out, New channels open with As a tab and In a new window?",
                PromptKind.YesNo) { Short = "the setting" }),
            Ask("Ticking it", "2", context => Task.FromResult(new GuidedPrompt("Tick Show LookingGlass only in windows.") {
                Short = "ticking it", Watch = StepContextExtensions.Until(() => context.SettingIsAsync(SelfTestSetting.WindowsOnly), "unticked", "ticked"),
            })),
            Ask("The choices", "2", new GuidedPrompt("Are As a tab and In a new window usable now, no longer greyed out?", PromptKind.YesNo) { Short = "the choices" }),
        ],
    };

    // ---------------------------------------------------------------- windows-only 5, 7, 8, 9, 12

    private static SelfTestCheck OwnLines() => new() {
        Id = "windows-only-5-12", Title = "Your own lines, and LookingGlass's replies", Steps = "windows-only 5, 7, 8, 9, 12", Mode = CheckMode.AutomaticAndGuided,
        Needs = Needs | SelfTestNeeds.Typing,
        StepList = [
            Auto("/lgc with text goes to the tab, not game chat", "5", async context => {
                await WindowsOnlyAsync(context, true).ConfigureAwait(false);
                await context.ChangeSettingAsync(SelfTestSetting.SayWhenTalking, true).ConfigureAwait(false);
                await CloseAllAsync(context).ConfigureAwait(false);
                var channel = await context.TestChannelAsync().ConfigureAwait(false);
                var command = await context.CommandOfAsync(channel).ConfigureAwait(false);
                var mark = context.Host.Mark();
                await context.TypeAsync($"{command} {context.NextMarker()}", spends: SelfTestAction.ChannelMessage).ConfigureAwait(false);
                var window = await context.WindowOfAsync(channel, Open).ConfigureAwait(false);
                return StepResult.All((await NotInGameChatAsync(context, mark, channel).ConfigureAwait(false), "the line showed in game chat"),
                    (window != null, "no window showed the test channel"));
            }, types: true),
            Ask("From the window's own box", "5", async context => {
                var channel = await context.TestChannelAsync().ConfigureAwait(false);
                var before = (await context.WindowOfAsync(channel).ConfigureAwait(false))?.Lines ?? 0;
                return new GuidedPrompt("In the test channel's window, type a line in its own box (anything, such as: test) and press Enter.") {
                    Short = "the window's box",
                    Watch = StepContextExtensions.Until(async () => (await context.WindowOfAsync(channel, TimeSpan.Zero).ConfigureAwait(false))?.Lines > before,
                        "nothing new in the window", "your line is in the window"),
                };
            }),
            Auto("Replies to what you type still show in game chat", "7", async context => {
                var used = await context.Host.SlotsInUseAsync().ConfigureAwait(false);
                var free = Enumerable.Range(1, SelfTestEnvironment.MaxChannels).First(slot => !used.Contains(slot));
                var mark = context.Host.Mark();
                await context.TypeAsync($"{CommandSlots.Prefix}{free} {context.NextMarker()}").ConfigureAwait(false);
                var empty = await context.PrintedAsync(mark, line => line is { Kind: TapKind.Notice, Tone: NoticeTone.Info }).ConfigureAwait(false);
                mark = context.Host.Mark();
                await context.TypeAsync(CommandSlots.Prefix).ConfigureAwait(false);
                var alone = await context.PrintedAsync(mark, line => line is { Kind: TapKind.Notice, Tone: NoticeTone.Info }).ConfigureAwait(false);
                return StepResult.All((empty != null, "/lgc on a free number had no blue answer in game chat"), (alone != null, "/lgc alone had no blue answer in game chat"));
            }, types: true),
            Auto("Talking in the channel: the lines in game chat, what you type in the tab", "8", async context => {
                var channel = await context.TestChannelAsync().ConfigureAwait(false);
                var command = await context.CommandOfAsync(channel).ConfigureAwait(false);
                var tag = await context.TagOfAsync(channel).ConfigureAwait(false);
                await context.StopTalkingAsync().ConfigureAwait(false);
                var mark = context.Host.Mark();
                await context.TypeAsync(command).ConfigureAwait(false);
                var started = await context.NoticeAsync(mark, StickyMessages.Entered(tag)).ConfigureAwait(false);
                await context.TypeAsync(context.NextMarker(), spends: SelfTestAction.ChannelMessage).ConfigureAwait(false);
                var inTab = await context.WindowOfAsync(channel, Open).ConfigureAwait(false);
                await context.TypeAsync("/s").ConfigureAwait(false);
                var stopped = await context.NoticeAsync(mark, StickyMessages.Ended(tag, StickyEnd.ChannelSwitched)).ConfigureAwait(false);
                return StepResult.All((started != null, "\"Now talking in\" wasn't in game chat"), (await NotInGameChatAsync(context, mark, channel).ConfigureAwait(false),
                    "what was typed showed in game chat"), (inTab != null, "no tab showed the test channel"), (stopped != null, "\"Stopped talking in\" wasn't in game chat"));
            }, types: true),
            Auto("Disconnected: \"Not connected\" in game chat", "9", async context => {
                var channel = await context.TestChannelAsync().ConfigureAwait(false);
                var command = await context.CommandOfAsync(channel).ConfigureAwait(false);
                await context.DisconnectAsync().ConfigureAwait(false);
                var mark = context.Host.Mark();
                await context.TypeAsync($"{command} {context.NextMarker()}", whileDisconnected: true).ConfigureAwait(false);
                var said = await context.NoticeAsync(mark, "Not connected to LookingGlass. Open /lookingglass to check.").ConfigureAwait(false);
                var back = await context.ConnectAsync().ConfigureAwait(false);
                var result = StepResult.All((said != null, "\"Not connected\" wasn't in game chat"), (back, "couldn't connect again"));
                return result.Outcome == StepOutcome.Passed ? StepResult.Pass("the invite line (step 12, see Right-click invites)") : result;
            }, types: true),
        ],
    };

    // ---------------------------------------------------------------- windows-only 11

    private static SelfTestCheck Warnings() => new() {
        Id = "windows-only-11", Title = "Warnings still show in game chat", Steps = "windows-only 11", Mode = CheckMode.AutomaticAndGuided, Needs = Needs,
        StepList = [
            Auto("A warning about the test channel", "11", async context => {
                await WindowsOnlyAsync(context, true).ConfigureAwait(false);
                var channel = await context.TestChannelAsync().ConfigureAwait(false);
                var window = await context.WindowOfAsync(channel, TimeSpan.Zero).ConfigureAwait(false) ?? await OpenAsync(context, channel).ConfigureAwait(false);
                if (window != null && window.Selected != channel) {
                    await context.Host.SelectTabAsync(window.LayoutId, channel).ConfigureAwait(false);
                }

                var lines = (await context.WindowOfAsync(channel).ConfigureAwait(false))?.Lines ?? 0;
                var mark = context.Host.Mark();
                await context.Host.SimulateWarningAsync(channel).ConfigureAwait(false);
                var warned = await context.PrintedAsync(mark, line => line.Kind == TapKind.Notice && line.Tone == NoticeTone.Warning
                    && line.Text.Contains(SelfTestWords.SimulatedWarning.Plain, StringComparison.Ordinal)).ConfigureAwait(false);
                var inTab = await context.WaitUntilAsync(async () => (await context.WindowOfAsync(channel, TimeSpan.Zero).ConfigureAwait(false))?.Lines > lines).ConfigureAwait(false);
                return StepResult.All((warned != null, "the warning wasn't in game chat as a warning"), (inTab, "the warning wasn't added to the tab"));
            }),
            Ask("Light red", "11", new GuidedPrompt(
                "Look at game chat: is the line \"A test warning from the LookingGlass self-test. Nothing is wrong.\" light red, starting with the test channel's tag after [LookingGlass]?",
                PromptKind.YesNo) { Short = "light red" }),
        ],
    };

    // ---------------------------------------------------------------- windows-only 13-16

    private static SelfTestCheck WhichWindow() => new() {
        Id = "windows-only-13-16", Title = "Which window a new channel lands in", Steps = "windows-only 13-16", Mode = CheckMode.Automatic, Needs = Needs,
        StepList = [
            Auto("A tab in the window used last", "13", async context => {
                await WindowsOnlyAsync(context, true).ConfigureAwait(false);
                await NewWindowsAsync(context, false).ConfigureAwait(false);
                await CloseAllAsync(context).ConfigureAwait(false);
                var (sky, fc, raid) = (await context.TestChannelAsync(0).ConfigureAwait(false), await context.TestChannelAsync(1).ConfigureAwait(false),
                    await context.TestChannelAsync(2).ConfigureAwait(false));
                var fcWindow = await OpenAsync(context, fc).ConfigureAwait(false);
                var skyWindow = await OpenAsync(context, sky).ConfigureAwait(false);
                if (fcWindow == null || skyWindow == null) {
                    return StepResult.Fail("a window couldn't be opened");
                }

                await context.Host.UseWindowAsync(skyWindow.LayoutId).ConfigureAwait(false);
                await ArriveAsync(context, raid).ConfigureAwait(false);
                var landed = await context.WindowOfAsync(raid, Open).ConfigureAwait(false);
                context.State["fc"] = fcWindow.LayoutId;
                context.State["sky"] = skyWindow.LayoutId;
                return StepResult.All((landed?.LayoutId == skyWindow.LayoutId, "the new tab wasn't added to the window used last"),
                    (landed?.Selected == sky, "the new tab was selected"));
            }),
            Auto("Closing it: the window opened last", "14", async context => {
                var sky = await context.TestChannelAsync(0).ConfigureAwait(false);
                await context.Host.CloseWindowAsync((string) context.State["sky"]).ConfigureAwait(false);
                await context.WaitUntilAsync(async () => (await context.Host.WindowsAsync().ConfigureAwait(false)).Showing(sky) == null).ConfigureAwait(false);
                await ArriveAsync(context, sky).ConfigureAwait(false);
                var landed = await context.WindowOfAsync(sky, Open).ConfigureAwait(false);
                return StepResult.Expect(landed?.LayoutId == (string) context.State["fc"], "the tab wasn't added to the window opened last");
            }),
            Auto("In a new window: a step down and right, on the screen", "15", async context => {
                await NewWindowsAsync(context, true).ConfigureAwait(false);
                await CloseAllAsync(context).ConfigureAwait(false);
                var channels = new[] { await context.TestChannelAsync(0).ConfigureAwait(false), await context.TestChannelAsync(1).ConfigureAwait(false),
                    await context.TestChannelAsync(2).ConfigureAwait(false) };
                var screen = await context.Host.ScreenAsync().ConfigureAwait(false);
                var places = new List<WindowPlace>();
                foreach (var channel in channels) {
                    await ArriveAsync(context, channel).ConfigureAwait(false);
                    if (await context.WindowOfAsync(channel, Open).ConfigureAwait(false) is not { Place: { } place, TookFocus: var focus }) {
                        return StepResult.Fail("a new channel got no window of its own");
                    }

                    if (focus) {
                        return StepResult.Fail("a window that opened by itself took the keyboard");
                    }

                    places.Add(place);
                }

                for (var i = 1; i < places.Count; i++) {
                    if (places[i].X <= places[i - 1].X || places[i].Y <= places[i - 1].Y) {
                        return StepResult.Fail("a new window wasn't a step below and right of the one before");
                    }
                }

                if (!places.All(place => OnScreen(place, screen))) {
                    return StepResult.Fail("a new window was off the screen");
                }

                // The window used last near the bottom right corner: the next one stays on the screen.
                var last = (await context.WindowOfAsync(channels[2]).ConfigureAwait(false))!;
                await context.Host.MoveWindowAsync(last.LayoutId, screen.X + screen.Width - places[2].Width - 10, screen.Y + screen.Height - places[2].Height - 10).ConfigureAwait(false);
                await context.Host.UseWindowAsync(last.LayoutId).ConfigureAwait(false);
                var first = (await context.WindowOfAsync(channels[0]).ConfigureAwait(false))!;
                await context.Host.CloseWindowAsync(first.LayoutId).ConfigureAwait(false);
                await context.WaitUntilAsync(async () => (await context.Host.WindowsAsync().ConfigureAwait(false)).Showing(channels[0]) == null).ConfigureAwait(false);
                await ArriveAsync(context, channels[0]).ConfigureAwait(false);
                var corner = await context.WindowOfAsync(channels[0], Open).ConfigureAwait(false);
                return StepResult.Expect(corner?.Place is { } near && OnScreen(near, screen), "a new window opened from the corner was off the screen");
            }),
            Auto("As a tab: one window for two channels", "16", async context => {
                await NewWindowsAsync(context, false).ConfigureAwait(false);
                await CloseAllAsync(context).ConfigureAwait(false);
                var (sky, fc) = (await context.TestChannelAsync(0).ConfigureAwait(false), await context.TestChannelAsync(1).ConfigureAwait(false));
                await ArriveAsync(context, sky).ConfigureAwait(false);
                await ArriveAsync(context, fc).ConfigureAwait(false);
                await context.WindowOfAsync(fc, Open).ConfigureAwait(false);
                // A tab is added at the window's next draw: waited for.
                return await context.SettledAsync(async () => {
                    var windows = (await context.Host.WindowsAsync().ConfigureAwait(false)).Windows;
                    return StepResult.All((windows.Count == 1, "more than one window opened"),
                        (windows.FirstOrDefault() is { } window && window.Tabs.SequenceEqual([sky, fc]) && window.Selected == sky, "the first channel wasn't selected with the second behind it"));
                }).ConfigureAwait(false);
            }),
        ],
    };

    private static bool OnScreen(WindowPlace place, WindowPlace screen) =>
        place.X >= screen.X - 1 && place.Y >= screen.Y - 1 && place.X + place.Width <= screen.X + screen.Width + 1 && place.Y + place.Height <= screen.Y + screen.Height + 1;

    // ---------------------------------------------------------------- windows-only 17

    private static SelfTestCheck Combat() => new() {
        Id = "windows-only-17", Title = "Nothing pops up during combat", Steps = "windows-only 17", Mode = CheckMode.Guided, Needs = Needs,
        StepList = [
            SetupGuided("A striking dummy", "17", async context => {
                var answer = await context.AskAsync(new GuidedPrompt("Is a striking dummy at hand here (a house's striking dummy furnishing)?", PromptKind.YesNo)).ConfigureAwait(false);
                context.State["dummy"] = answer is GuidedAnswer.Yes or GuidedAnswer.Done;
                return answer is GuidedAnswer.Yes or GuidedAnswer.Done ? StepResult.Pass() : StepResult.Skip(SelfTestWords.NeedsStrikingDummy);
            }),
            SetupGuided("Into combat", "17", async context => {
                if (!(bool) context.State["dummy"]) {
                    return StepResult.Skip(SelfTestWords.NeedsStrikingDummy);
                }

                await WindowsOnlyAsync(context, true).ConfigureAwait(false);
                await CloseAllAsync(context).ConfigureAwait(false);
                await context.TestChannelAsync().ConfigureAwait(false);
                return await context.AskForResultAsync(new GuidedPrompt("Attack the striking dummy, and keep at it until the self-test says.") {
                    Short = "combat", Watch = StepContextExtensions.Until(() => context.Host.InCombatAsync(), "not in combat yet", "in combat"),
                }).ConfigureAwait(false);
            }),
            Auto("No window during combat", "17", async context => {
                if (!(bool) context.State["dummy"]) {
                    return StepResult.Skip(SelfTestWords.NeedsStrikingDummy);
                }

                var channel = await context.TestChannelAsync().ConfigureAwait(false);
                await ArriveAsync(context, channel).ConfigureAwait(false);
                await context.DelayAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
                if (!await context.Host.InCombatAsync().ConfigureAwait(false)) {
                    return StepResult.Skip("combat ended too soon");
                }

                return StepResult.Expect((await context.Host.WindowsAsync().ConfigureAwait(false)).Showing(channel) == null, "a window opened during combat");
            }),
            Guided("It opens after combat", "17", async context => {
                if (!(bool) context.State["dummy"]) {
                    return StepResult.Skip(SelfTestWords.NeedsStrikingDummy);
                }

                var channel = await context.TestChannelAsync().ConfigureAwait(false);
                return await context.AskForResultAsync(new GuidedPrompt("Stop attacking, and wait until combat ends.") {
                    Short = "after combat",
                    Watch = StepContextExtensions.Until(async () => !await context.Host.InCombatAsync().ConfigureAwait(false)
                        && (await context.Host.WindowsAsync().ConfigureAwait(false)).Showing(channel) != null, "still in combat, no window", "a window opened"),
                }).ConfigureAwait(false);
            }),
        ],
    };

    // ---------------------------------------------------------------- windows-only 20-21

    private static SelfTestCheck Locked() => new() {
        Id = "windows-only-20-21", Title = "Show in game chat is locked while the setting is on", Steps = "windows-only 20-21", Mode = CheckMode.AutomaticAndGuided, Needs = Needs,
        StepList = [
            Auto("A channel off game chat, then the setting on", "20", async context => {
                await WindowsOnlyAsync(context, false).ConfigureAwait(false);
                await CloseAllAsync(context).ConfigureAwait(false);
                var channel = await context.TestChannelAsync().ConfigureAwait(false);
                await context.Host.SetShowInGameChatAsync(channel, false).ConfigureAwait(false);
                await WindowsOnlyAsync(context, true).ConfigureAwait(false);
                return StepResult.Expect(await context.WindowOfAsync(channel, Open).ConfigureAwait(false) != null, "turning a channel off game chat opened no window for it");
            }),
            Ask("The greyed red cross", "20", new GuidedPrompt(
                "Open /lookingglass. In the test channel's ⋮ menu, and in its tab's right-click menu in its window: is Show in game chat a red cross, greyed out, that can't be clicked, " +
                "with a tooltip saying Show LookingGlass only in windows is on and to change it in Settings, under Chat?", PromptKind.YesNo) { Short = "greyed cross" }),
            Ask("The tab's hover", "20", new GuidedPrompt("Hover the test channel's tab in its window: does it say it isn't shown in game chat, as only windows show messages?",
                PromptKind.YesNo) { Short = "tab hover" }),
            Auto("Closing its window says nothing, and it stays off", "21", async context => {
                var channel = await context.TestChannelAsync().ConfigureAwait(false);
                var mark = context.Host.Mark();
                if (await context.WindowOfAsync(channel).ConfigureAwait(false) is { } window) {
                    await context.Host.CloseWindowAsync(window.LayoutId).ConfigureAwait(false);
                }

                var again = await context.CountPrintedAsync(mark, line => line.Text.Contains("shows in game chat again", StringComparison.Ordinal)).ConfigureAwait(false);
                return StepResult.All((again == 0, "a \"shows in game chat again\" line was said"),
                    (!await context.Host.ShowsInGameChatAsync(channel).ConfigureAwait(false), "the channel went back to game chat"));
            }),
        ],
    };

    // ---------------------------------------------------------------- windows-only 23, 23b, 23c, 24

    private static SelfTestCheck TurningOff() => new() {
        Id = "windows-only-23-24", Title = "Turning the setting off", Steps = "windows-only 23, 23b, 23c, 24", Mode = CheckMode.AutomaticAndGuided, Needs = Needs,
        StepList = [
            Auto("Off: a channel off game chat still opens a window by itself", "23", async context => {
                await WindowsOnlyAsync(context, true).ConfigureAwait(false);
                await CloseAllAsync(context).ConfigureAwait(false);
                var channel = await context.TestChannelAsync().ConfigureAwait(false);
                await context.Host.SetShowInGameChatAsync(channel, false).ConfigureAwait(false);
                await CloseAllAsync(context).ConfigureAwait(false);
                await WindowsOnlyAsync(context, false).ConfigureAwait(false);
                await context.DelayAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                var early = (await context.Host.WindowsAsync().ConfigureAwait(false)).Showing(channel);
                var mark = context.Host.Mark();
                await ArriveAsync(context, channel).ConfigureAwait(false);
                var window = await context.WindowOfAsync(channel, Open).ConfigureAwait(false);
                var result = StepResult.All((early == null, "a window opened as soon as the setting was turned off"),
                    (await NotInGameChatAsync(context, mark, channel).ConfigureAwait(false), "the message showed in game chat"),
                    (window != null, "no window opened by itself"), (window?.FlashPulses > 0, "the window didn't flash"), (window is { TookFocus: false }, "the window took the keyboard"));
                return result.Outcome == StepOutcome.Passed ? StepResult.Pass("the combat parts (23b, 23c: need a striking dummy)") : result;
            }),
            SetupAuto("On again, its window closed, for the relog", "24", async context => {
                await WindowsOnlyAsync(context, true).ConfigureAwait(false);
                await CloseAllAsync(context).ConfigureAwait(false);
                return StepResult.Pass();
            }),
            CarryOn("Go to the title screen. There, open LookingGlass's Settings (/lookingglass, the gear) and untick Show LookingGlass only in windows. " +
                    "Then come back in with this character. The self-test carries on by itself.", "24"),
            Auto("Nothing opens at login; its next message does", "24", async context => {
                var channel = await context.TestChannelAsync().ConfigureAwait(false);
                await context.DelayAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                var early = (await context.Host.WindowsAsync().ConfigureAwait(false)).Showing(channel);
                var unticked = !await context.SettingIsAsync(SelfTestSetting.WindowsOnly).ConfigureAwait(false);
                await ArriveAsync(context, channel).ConfigureAwait(false);
                var window = await context.WindowOfAsync(channel, Open).ConfigureAwait(false);
                return StepResult.All((unticked, "the setting was still on"), (early == null, "a window opened at login"), (window != null, "its next message opened no window"));
            }),
        ],
    };

    // ---------------------------------------------------------------- channel-windows 17, 17b, 17c, 36, 37

    private static SelfTestCheck Counts() => new() {
        Id = "channel-windows-17-37", Title = "Counts on every tab not selected", Steps = "channel-windows 17, 17b, 17c, 36, 37", Mode = CheckMode.Automatic,
        Needs = Needs | SelfTestNeeds.Typing,
        StepList = [
            Auto("Counts behind the selected tab", "17b", async context => {
                await WindowsOnlyAsync(context, true).ConfigureAwait(false);
                await NewWindowsAsync(context, false).ConfigureAwait(false);
                await CloseAllAsync(context).ConfigureAwait(false);
                var (sky, fc, raid) = (await context.TestChannelAsync(0).ConfigureAwait(false), await context.TestChannelAsync(1).ConfigureAwait(false),
                    await context.TestChannelAsync(2).ConfigureAwait(false));
                if (await OpenAsync(context, raid).ConfigureAwait(false) is not { } window) {
                    return StepResult.Fail("a window couldn't be opened");
                }

                await context.Host.AddTabAsync(window.LayoutId, sky).ConfigureAwait(false);
                await context.Host.AddTabAsync(window.LayoutId, fc).ConfigureAwait(false);
                await context.Host.SelectTabAsync(window.LayoutId, raid).ConfigureAwait(false);
                await context.WaitUntilAsync(async () => (await context.WindowOfAsync(raid).ConfigureAwait(false))?.Selected == raid).ConfigureAwait(false);
                await context.DelayAsync(TimeSpan.FromSeconds(1.5)).ConfigureAwait(false);
                context.State["window"] = window.LayoutId;
                await ArriveAsync(context, sky).ConfigureAwait(false);
                await ArriveAsync(context, fc).ConfigureAwait(false);
                await ArriveAsync(context, fc).ConfigureAwait(false);
                await ArriveAsync(context, raid).ConfigureAwait(false);
                return StepResult.All((await TabCountIsAsync(context, sky, 1).ConfigureAwait(false), "the tab behind didn't count one message"),
                    (await TabCountIsAsync(context, fc, 2).ConfigureAwait(false), "the tab behind didn't count two messages"),
                    (await ListCountIsAsync(context, fc, 2).ConfigureAwait(false), "the channel list didn't count a tab not on screen (36)"),
                    (await ListCountAsync(context, raid).ConfigureAwait(false) == 0, "the channel list counted the tab on screen (36)"));
            }),
            Auto("Selecting a tab clears its count", "17, 37", async context => {
                var sky = await context.TestChannelAsync(0).ConfigureAwait(false);
                await context.Host.SelectTabAsync((string) context.State["window"], sky).ConfigureAwait(false);
                return StepResult.All((await TabCountIsAsync(context, sky, 0).ConfigureAwait(false), "selecting the tab didn't clear its count"),
                    (await ListCountIsAsync(context, sky, 0).ConfigureAwait(false), "selecting the tab didn't clear the channel list's count"));
            }),
            Auto("Talking in a channel reads it", "17b", async context => {
                var fc = await context.TestChannelAsync(1).ConfigureAwait(false);
                var command = await context.CommandOfAsync(fc).ConfigureAwait(false);
                await context.TypeAsync($"{command} {context.NextMarker()}", spends: SelfTestAction.ChannelMessage).ConfigureAwait(false);
                return StepResult.All((await TabCountIsAsync(context, fc, 0).ConfigureAwait(false), "sending in the channel didn't clear its tab's count"),
                    (await ListCountIsAsync(context, fc, 0).ConfigureAwait(false), "sending in the channel didn't clear the channel list's count"));
            }, types: true),
            Auto("A second window showing it: no count", "17c", async context => {
                var (sky, fc, raid) = (await context.TestChannelAsync(0).ConfigureAwait(false), await context.TestChannelAsync(1).ConfigureAwait(false),
                    await context.TestChannelAsync(2).ConfigureAwait(false));
                var first = (string) context.State["window"];
                await context.Host.SelectTabAsync(first, raid).ConfigureAwait(false);
                if (await OpenAsync(context, sky).ConfigureAwait(false) is not { } second) {
                    return StepResult.Fail("a second window couldn't be opened");
                }

                await context.DelayAsync(TimeSpan.FromSeconds(1.5)).ConfigureAwait(false);
                await ArriveAsync(context, sky).ConfigureAwait(false);
                await context.DelayAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                var whileShown = await TabCountAsync(context, sky).ConfigureAwait(false);
                await context.Host.AddTabAsync(second.LayoutId, fc).ConfigureAwait(false);
                await context.DelayAsync(TimeSpan.FromSeconds(1.5)).ConfigureAwait(false);
                await ArriveAsync(context, sky).ConfigureAwait(false);
                var counted = await TabCountIsAsync(context, sky, 1).ConfigureAwait(false);
                await context.Host.SelectTabAsync(second.LayoutId, sky).ConfigureAwait(false);
                var cleared = await TabCountIsAsync(context, sky, 0).ConfigureAwait(false);
                var result = StepResult.All((whileShown == 0, "a tab counted a message another window showed"), (counted, "no window showing it, the tab didn't count it"),
                    (cleared, "showing it in the other window didn't clear the count"));
                return result.Outcome == StepOutcome.Passed ? StepResult.Pass("someone joining (needs a partner); collapsing a window (36)") : result;
            }),
        ],
    };

    // ---------------------------------------------------------------- channel-windows 33, 33b-33f

    private static SelfTestCheck OffGameChat() => new() {
        Id = "channel-windows-33", Title = "A channel off game chat opens a window again", Steps = "channel-windows 33, 33b, 33c, 33d, 33e, 33f", Mode = CheckMode.AutomaticAndGuided,
        Needs = Needs | SelfTestNeeds.Typing,
        StepList = [
            Auto("A message opens a window by itself", "33", async context => {
                await WindowsOnlyAsync(context, false).ConfigureAwait(false);
                await CloseAllAsync(context).ConfigureAwait(false);
                var channel = await context.TestChannelAsync().ConfigureAwait(false);
                await context.Host.SetShowInGameChatAsync(channel, false).ConfigureAwait(false);
                await CloseAllAsync(context).ConfigureAwait(false);
                var mark = context.Host.Mark();
                await ArriveAsync(context, channel).ConfigureAwait(false);
                var window = await context.WindowOfAsync(channel, Open).ConfigureAwait(false);
                await context.DelayAsync(TimeSpan.FromSeconds(1.5)).ConfigureAwait(false);
                return StepResult.All((await NotInGameChatAsync(context, mark, channel).ConfigureAwait(false), "the message showed in game chat"),
                    (window is { Selected: var selected } && selected == channel, "no window opened with it selected"),
                    (window?.FlashPulses == WindowFlash.OpenedPulses, "the window didn't flash three times"), (window is { TookFocus: false }, "the window took the keyboard"),
                    (await ListCountAsync(context, channel).ConfigureAwait(false) == 0, "the channel list counted a message on screen"));
            }),
            Auto("Your own /lgc line opens one too", "33f", async context => {
                var channel = await context.TestChannelAsync().ConfigureAwait(false);
                var command = await context.CommandOfAsync(channel).ConfigureAwait(false);
                await CloseAllAsync(context).ConfigureAwait(false);
                var mark = context.Host.Mark();
                await context.TypeAsync($"{command} {context.NextMarker()}", spends: SelfTestAction.ChannelMessage).ConfigureAwait(false);
                var window = await context.WindowOfAsync(channel, Open).ConfigureAwait(false);
                return StepResult.All((await NotInGameChatAsync(context, mark, channel).ConfigureAwait(false), "your line showed in game chat"),
                    (window != null, "no window opened with your line"));
            }, types: true),
            SetupAuto("Its window open, for the relog", "33b", async context => {
                var channel = await context.TestChannelAsync().ConfigureAwait(false);
                context.State["mark"] = context.Host.Mark();
                return StepResult.Expect(await context.WindowOfAsync(channel).ConfigureAwait(false) != null, "its window isn't open");
            }),
            CarryOn(Relog, "33b"),
            Auto("After the relog: back, and still off", "33b", async context => {
                var channel = await context.TestChannelAsync().ConfigureAwait(false);
                var window = await context.WindowOfAsync(channel, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                var again = context.Host.PrintedSince((long) context.State["mark"]).Count(line => line.Text.Contains("shows in game chat again", StringComparison.Ordinal));
                var result = StepResult.All((window != null, "the window didn't come back after the relog"),
                    (!await context.Host.ShowsInGameChatAsync(channel).ConfigureAwait(false), "the channel went back to game chat"), (again == 0, "a \"shows in game chat again\" line was said"));
                return result.Outcome == StepOutcome.Passed
                    ? StepResult.Pass("missed messages and an information line (33c, 33f: need a partner); the combat parts (33d, 33e)")
                    : result;
            }),
        ],
    };

    // ---------------------------------------------------------------- channel-windows 37b, windows-only 6b

    private static SelfTestCheck ListCounts() => new() {
        Id = "channel-windows-37b", Title = "Channel list counts for channels seen only in windows", Steps = "channel-windows 37b; windows-only 6b", Mode = CheckMode.AutomaticAndGuided,
        Needs = Needs | SelfTestNeeds.Typing,
        StepList = [
            Auto("A count for a tab not on screen", "6b", async context => {
                await WindowsOnlyAsync(context, true).ConfigureAwait(false);
                await CloseAllAsync(context).ConfigureAwait(false);
                var (sky, raid) = (await context.TestChannelAsync(0).ConfigureAwait(false), await context.TestChannelAsync(2).ConfigureAwait(false));
                if (await OpenAsync(context, raid).ConfigureAwait(false) is not { } window) {
                    return StepResult.Fail("a window couldn't be opened");
                }

                await context.Host.AddTabAsync(window.LayoutId, sky).ConfigureAwait(false);
                await context.Host.SelectTabAsync(window.LayoutId, raid).ConfigureAwait(false);
                await context.DelayAsync(TimeSpan.FromSeconds(1.5)).ConfigureAwait(false);
                context.State["window"] = window.LayoutId;
                await ArriveAsync(context, sky).ConfigureAwait(false);
                return StepResult.Expect(await ListCountIsAsync(context, sky, 1).ConfigureAwait(false), "the channel list didn't count the message");
            }),
            Ask("Selecting it in the channel list", "37b", context => Task.FromResult(new GuidedPrompt(
                "Open /lookingglass and click the test channel (\"LG self-test ...\") in its channel list, then press Done.") { Short = "the channel list" })),
            Auto("The count stays; the tab clears it", "37b", async context => {
                var sky = await context.TestChannelAsync(0).ConfigureAwait(false);
                var stayed = await ListCountAsync(context, sky).ConfigureAwait(false) == 1;
                await context.Host.SelectTabAsync((string) context.State["window"], sky).ConfigureAwait(false);
                return StepResult.All((stayed, "selecting it in the channel list cleared the count"), (await ListCountIsAsync(context, sky, 0).ConfigureAwait(false), "the tab didn't clear it"));
            }),
            Auto("Sending in it clears it too", "6b", async context => {
                var (sky, raid) = (await context.TestChannelAsync(0).ConfigureAwait(false), await context.TestChannelAsync(2).ConfigureAwait(false));
                await context.Host.SelectTabAsync((string) context.State["window"], raid).ConfigureAwait(false);
                await context.DelayAsync(TimeSpan.FromSeconds(1.5)).ConfigureAwait(false);
                await ArriveAsync(context, sky).ConfigureAwait(false);
                var counted = await ListCountIsAsync(context, sky, 1).ConfigureAwait(false);
                await context.TypeAsync($"{await context.CommandOfAsync(sky).ConfigureAwait(false)} {context.NextMarker()}", spends: SelfTestAction.ChannelMessage).ConfigureAwait(false);
                var result = StepResult.All((counted, "the channel list didn't count the message"), (await ListCountIsAsync(context, sky, 0).ConfigureAwait(false), "sending didn't clear the count"));
                return result.Outcome == StepOutcome.Passed ? StepResult.Pass("a channel shown in game chat losing its count (37b)") : result;
            }, types: true),
        ],
    };

    // ---------------------------------------------------------------- channel-windows 37c-37f

    private static SelfTestCheck Flash() => new() {
        Id = "channel-windows-37c-37f", Title = "The flash", Steps = "channel-windows 37c-37f", Mode = CheckMode.Guided, Needs = Needs,
        StepList = [
            Auto("Three pulses for a window that opens by itself", "37c", async context => {
                await WindowsOnlyAsync(context, false).ConfigureAwait(false);
                await NewWindowsAsync(context, false).ConfigureAwait(false);
                await CloseAllAsync(context).ConfigureAwait(false);
                var channel = await context.TestChannelAsync(0).ConfigureAwait(false);
                await context.Host.SetShowInGameChatAsync(channel, false).ConfigureAwait(false);
                await CloseAllAsync(context).ConfigureAwait(false);
                await ArriveAsync(context, channel).ConfigureAwait(false);
                var window = await context.WindowOfAsync(channel, Open).ConfigureAwait(false);
                await context.DelayAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                window = await context.WindowOfAsync(channel).ConfigureAwait(false);
                return StepResult.All((window?.FlashPulses == WindowFlash.OpenedPulses, "it didn't flash three times"), (window is { TookFocus: false }, "it took the keyboard"));
            }),
            Ask("How it looked", "37c", new GuidedPrompt(
                "Did that window's title bar and border pulse three times in the test channel's colour (LookingGlass blue) over about a second and a half, " +
                "then look as usual, without taking the keyboard?", PromptKind.YesNo) { Short = "three pulses" }),
            Auto("Once for a tab added by itself", "37d", async context => {
                var (sky, fc) = (await context.TestChannelAsync(0).ConfigureAwait(false), await context.TestChannelAsync(1).ConfigureAwait(false));
                await context.Host.SetShowInGameChatAsync(fc, false).ConfigureAwait(false);
                if (await context.WindowOfAsync(fc, TimeSpan.Zero).ConfigureAwait(false) is { } own) {
                    await context.Host.CloseWindowAsync(own.LayoutId).ConfigureAwait(false);
                }

                if (await context.WindowOfAsync(sky).ConfigureAwait(false) is not { } window) {
                    return StepResult.Fail("the window from before closed");
                }

                await context.Host.UseWindowAsync(window.LayoutId).ConfigureAwait(false);
                await context.DelayAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                await ArriveAsync(context, fc).ConfigureAwait(false);
                var added = await context.WindowOfAsync(fc, Open).ConfigureAwait(false);
                await context.DelayAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                added = await context.WindowOfAsync(fc).ConfigureAwait(false);
                return StepResult.All((added?.LayoutId == window.LayoutId, "the channel wasn't added as a tab"), (added?.FlashPulses == WindowFlash.TabAddedPulses, "the window didn't flash once"));
            }),
            Ask("The single flash", "37d", new GuidedPrompt("Did that window flash once, in the new channel's colour, and stay where it was?", PromptKind.YesNo) { Short = "one flash" }),
            Auto("No flash for a window you open", "37e", async context => {
                var raid = await context.TestChannelAsync(2).ConfigureAwait(false);
                var window = await OpenAsync(context, raid).ConfigureAwait(false);
                await context.DelayAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                window = await context.WindowOfAsync(raid).ConfigureAwait(false);
                return StepResult.Expect(window is { FlashPulses: 0 }, "a window opened from the channel list flashed");
            }),
            Guided("Reduced motion (optional)", "37f", async context => {
                var answer = await context.AskAsync(new GuidedPrompt(
                    "Optional: in Dalamud's settings, turn on Reduce motions (under Look & Feel or Experimental), then press Done. Or Skip this step.")).ConfigureAwait(false);
                if (answer == GuidedAnswer.Skip) {
                    return StepResult.Skip(SelfTestWords.YouSkipped);
                }

                var channel = await context.TestChannelAsync(0).ConfigureAwait(false);
                await CloseAllAsync(context).ConfigureAwait(false);
                await ArriveAsync(context, channel).ConfigureAwait(false);
                await context.WindowOfAsync(channel, Open).ConfigureAwait(false);
                return await context.AskForResultAsync(new GuidedPrompt(
                    "A window just opened by itself: instead of pulsing, did it show a steady, softer tint and border for about a second and a half? (Turn Reduce motions off again after.)",
                    PromptKind.YesNo) { Short = "reduced motion" }).ConfigureAwait(false);
            }),
        ],
    };

    // ---------------------------------------------------------------- windows-only 26

    private static SelfTestCheck NoErrors() => new() {
        Id = "windows-only-26", Title = "No errors in the log", Steps = "windows-only 26", Mode = CheckMode.Automatic,
        StepList = [
            Auto("No errors from LookingGlass", "26", context => Task.FromResult(StepResult.Expect(context.Host.LogCounts().Errors == 0,
                "LookingGlass wrote errors to /xllog during the run (look at the times above)"))),
        ],
    };
}
