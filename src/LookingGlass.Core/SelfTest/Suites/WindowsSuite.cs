using LookingGlass.Core.Client;
using static LookingGlass.Core.SelfTest.SelfTestSteps;

namespace LookingGlass.Core.SelfTest.Suites;

/// <summary>
/// "Windows and counts" (one player): windows-only-checklist.md and channel-windows-checklist.md, with "a message arrives"
/// made by a simulated incoming line in a test channel (local only, as the checklists say to do with /lgdebug). Judged
/// from the windows the plugin has open, their tabs, places, flashes and counts, and what it printed; what only the
/// tester's eyes can tell (a greyed menu item, a flash's look) is guided. The tester's own windows are closed for a while
/// and opened again where they were. What passed in the owner's third run (2026-10-13, plugin 0.2.18) is no longer
/// offered: windows-only 1 to 2, 5, 7 to 9, 11, 13 to 16, 23, 24 and 26; channel-windows 17, 17b, 17c, 36 and 37 but a
/// collapsed window, 33 to 33f, 6b and 37b but a channel shown in game chat. No combat checks: no striking dummy can be
/// placed in a private place (owner, 2026-10-13).
/// </summary>
public static class WindowsSuite {
    public const string Id = "windows";

    private static readonly TimeSpan Open = TimeSpan.FromSeconds(8);

    public static SelfTestSuite Create() => new(Id, "Windows and counts", [Locked(), Collapsed(), ShownLosesItsCount(), Flash()]) {
        Changes = [SelfTestSetting.WindowsOnly, SelfTestSetting.NewChannelsOpen, SelfTestSetting.SayWhenTalking, SelfTestSetting.ChannelWindows],
        Planned = new Dictionary<SelfTestAction, int> { [SelfTestAction.CreateChannel] = 1 },
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

    private static async Task<int> ListCountAsync(StepContext context, string channelId) =>
        (await context.Host.WindowsAsync().ConfigureAwait(false)).ListCounts.GetValueOrDefault(channelId);

    private static Task<bool> ListCountIsAsync(StepContext context, string channelId, int count) =>
        context.WaitUntilAsync(async () => await ListCountAsync(context, channelId).ConfigureAwait(false) == count);

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
            Ask("The tab's hover", "20", new GuidedPrompt("Hover the test channel's tab in its window (it shows even with a menu open): does it say it isn't shown in game chat, as only windows show messages?",
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

    // ---------------------------------------------------------------- channel-windows 36 (a collapsed window)

    private static SelfTestCheck Collapsed() => new() {
        Id = "channel-windows-36-collapsed", Title = "A collapsed window counts again", Steps = "channel-windows 36 (collapsing a window)", Mode = CheckMode.AutomaticAndGuided,
        Needs = Needs,
        StepList = [
            SetupAuto("The test channel in a window of its own", "36", async context => {
                await WindowsOnlyAsync(context, true).ConfigureAwait(false);
                await CloseAllAsync(context).ConfigureAwait(false);
                return StepResult.Expect(await OpenAsync(context, await context.TestChannelAsync().ConfigureAwait(false)).ConfigureAwait(false) != null,
                    "a window couldn't be opened");
            }),
            SetupAsk("Collapse it", "36", new GuidedPrompt("Double-click the test channel's window's title bar, so it collapses to its title bar.") { Short = "collapsed" }),
            Auto("What arrives in its selected channel counts again", "36", async context => {
                var channel = await context.TestChannelAsync().ConfigureAwait(false);
                await ArriveAsync(context, channel).ConfigureAwait(false);
                return StepResult.Expect(await ListCountIsAsync(context, channel, 1).ConfigureAwait(false), "a message in a collapsed window's channel wasn't counted");
            }),
        ],
    };

    // ---------------------------------------------------------------- channel-windows 37b (a channel shown in game chat)

    private static SelfTestCheck ShownLosesItsCount() => new() {
        Id = "channel-windows-37b-shown", Title = "A channel shown in game chat loses its count when selected", Steps = "channel-windows 37b (that part)", Mode = CheckMode.Guided,
        Needs = SelfTestNeeds.Connection,
        StepList = [
            Ask("Selecting it in the channel list", "37b", new GuidedPrompt(
                "Open /lookingglass. If one of your own channels that shows in game chat has a count in the channel list, click it: does its count go? " +
                "(Skip if none has a count.)", PromptKind.YesNo) { Short = "shown in game chat" }),
        ],
    };

    /// <summary>The flash shown again on the window showing test channel <paramref name="index"/>: no new window, nothing sent.</summary>
    private static async Task FlashAgainAsync(StepContext context, int index, int pulses) =>
        await context.Host.FlashAgainAsync(await context.TestChannelAsync(index).ConfigureAwait(false), pulses).ConfigureAwait(false);

    // ---------------------------------------------------------------- channel-windows 37c-37f

    private static SelfTestCheck Flash() => new() {
        Id = "channel-windows-37c-37f", Title = "The flash", Steps = "channel-windows 37c-37f", Mode = CheckMode.Guided, Needs = Needs,
        StepList = [
            SetupAsk("Ready? A window opens", "37c", new GuidedPrompt(
                "Ready? A window will open by itself for the test channel. Watch its title bar and border: three blinks in the channel's colour (LookingGlass blue). " +
                "Press Done when you're watching.") { Short = "ready" }),
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
            Ask("How it looked", "37c", context => Task.FromResult(new GuidedPrompt(
                "Did that window flash with three distinct blinks: the title bar turns mostly the test channel's colour (LookingGlass blue), with a border a few pixels wide, " +
                "then looks as usual, without taking the keyboard? Show it again to see it once more.", PromptKind.YesNo) {
                Short = "three blinks", Again = () => FlashAgainAsync(context, 0, WindowFlash.OpenedPulses),
            })),
            SetupAsk("Ready? A tab is added", "37d", new GuidedPrompt(
                "Ready? Another test channel will be added as a tab to that window. Watch the window: one blink, in the new channel's colour. " +
                "Press Done when you're watching.") { Short = "ready" }),
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
            Ask("The single flash", "37d", context => Task.FromResult(new GuidedPrompt(
                "Did that window blink once, in the new channel's colour, and stay where it was? Show it again to see it once more.", PromptKind.YesNo) {
                Short = "one blink", Again = () => FlashAgainAsync(context, 1, WindowFlash.TabAddedPulses),
            })),
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
}
