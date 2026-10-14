using System.Text.Json;
using LookingGlass.Core.Client;
using static LookingGlass.Core.SelfTest.SelfTestSteps;

namespace LookingGlass.Core.SelfTest.Suites;

/// <summary>
/// "Colours" (one player): name-colours-checklist.md 21 and 23 with the tester's own name, and custom-colours-checklist.md
/// 3 to 17 with the test channels. The runner sets the colours and reads back what is saved; whether game chat shows them
/// right is the tester's to say, against a swatch.
/// </summary>
public static class ColoursSuite {
    public const string Id = "colours";

    private const uint Pink = 0xFF66CC;
    private const uint Orange = 0xFFAA00;

    public static SelfTestSuite Create() => new(Id, "Colours", [ColourTest(), Picker(), PinkEverywhere(), NameColourKept(), NameColourSaved(), ChannelColoursKept(), LeavingDropsIt()]) {
        Changes = [SelfTestSetting.OwnNameColour, SelfTestSetting.ColourWholeLine, SelfTestSetting.SayWhenTalking],
        Planned = new Dictionary<SelfTestAction, int> { [SelfTestAction.ChannelMessage] = 2, [SelfTestAction.CreateChannel] = 3 },
        Note = "2 reloads, 2 relogs",
    };

    // ---------------------------------------------------------------- custom-colours 3

    private static SelfTestCheck ColourTest() => new() {
        Id = "custom-colours-3", Title = "Colour test command, odd inputs", Steps = "custom-colours 3", Mode = CheckMode.Automatic,
        StepList = [
            Auto("Two colours: six lines", "3", async context => {
                var mark = context.Host.Mark();
                await context.Host.RunPluginCommandAsync("/lgdebug", "colours #3FA7D6 00ff00").ConfigureAwait(false);
                await context.PrintedAsync(mark, line => line.Kind == TapKind.ColourSample).ConfigureAwait(false);
                var samples = await context.CountPrintedAsync(mark, line => line.Kind == TapKind.ColourSample).ConfigureAwait(false);
                return StepResult.Expect(samples == 6, $"it printed {samples} lines, not six");
            }),
            Auto("A wrong code: only a usage line", "3", async context => {
                var mark = context.Host.Mark();
                await context.Host.RunPluginCommandAsync("/lgdebug", "colours #nope").ConfigureAwait(false);
                await context.PrintedAsync(mark, line => line.Kind == TapKind.Notice).ConfigureAwait(false);
                var lines = await context.CountPrintedAsync(mark, _ => true).ConfigureAwait(false);
                var usage = context.Host.PrintedSince(mark).Count(line => line.Kind == TapKind.Notice && line.Text.StartsWith("Usage:", StringComparison.Ordinal));
                return StepResult.All((usage == 1, "no usage line"), (lines == 1, "more than a usage line was printed"));
            }),
            Auto("Plain /lgdebug still opens the debug window", "3", async context => {
                var before = await context.Host.DebugWindowOpenAsync().ConfigureAwait(false);
                await context.Host.RunPluginCommandAsync("/lgdebug", "").ConfigureAwait(false);
                var toggled = await context.WaitUntilAsync(async () => await context.Host.DebugWindowOpenAsync().ConfigureAwait(false) != before).ConfigureAwait(false);
                if (before) {
                    // It was open (the self-test was started from it): open it again.
                    await context.Host.RunPluginCommandAsync("/lgdebug", "").ConfigureAwait(false);
                }

                return StepResult.Expect(toggled, "plain /lgdebug didn't open (or close) the debug window");
            }),
        ],
    };

    // ---------------------------------------------------------------- custom-colours 4-8

    private static SelfTestCheck Picker() => new() {
        Id = "custom-colours-4-8", Title = "The colour picker", Steps = "custom-colours 4-8", Mode = CheckMode.Guided, Needs = SelfTestNeeds.Connection | SelfTestNeeds.TestChannel,
        StepList = [
            Ask("Swatches, Default and Custom...", "4", async context => {
                await context.TestChannelAsync().ConfigureAwait(false);
                return new GuidedPrompt("Open /lookingglass, then the test channel's ⋮ menu > Colour.... Are there 40 swatches, Default and Custom...? Does Custom... open a colour wheel, " +
                                        "a Colour code box, a Preview line on a dark background, the closest game colour's swatch, Use this colour and Back?", PromptKind.YesNo) { Short = "the picker" };
            }),
            Ask("Typing codes", "5", new GuidedPrompt(
                "Turn the wheel: does the code follow? Type #ff66cc, then FF66CC: does the wheel follow, and the code read #FF66CC once you leave the box? " +
                "Type #FF66C, then #GG66CC: does a line say what a code looks like, with Use this colour greyed out?", PromptKind.YesNo) { Short = "codes" }),
            Ask("The preview and the closest colour", "6", new GuidedPrompt(
                "Does the preview show the test channel's tag (and the line, with Colour the whole line on) in the colour, changing as you turn the wheel? Does hovering the closest-colour " +
                "swatch say \"The closest game colour.\"?", PromptKind.YesNo) { Short = "preview" }),
            Ask("A dark colour", "7", new GuidedPrompt(
                "Type #1A2B6D: does a line say the colour is very dark and may be hard to read, while Use this colour still works? Type #FF66CC: is that line gone?",
                PromptKind.YesNo) { Short = "dark note" }),
            Ask("The recent swatch", "8", new GuidedPrompt(
                "Use #FF66CC. Open Colour... again: is no swatch outlined, and an outlined pink swatch beside Custom..., its tooltip naming #FF66CC? Does clicking it open the wheel on #FF66CC? " +
                "Do a swatch, then Default, still work?", PromptKind.YesNo) { Swatch = Pink, Short = "recent swatch" }),
        ],
    };

    // ---------------------------------------------------------------- custom-colours 9

    private static SelfTestCheck PinkEverywhere() => new() {
        Id = "custom-colours-9", Title = "Pink everywhere, game chat (no ChatTwo)", Steps = "custom-colours 9", Mode = CheckMode.Guided,
        Needs = SelfTestNeeds.Connection | SelfTestNeeds.TestChannel | SelfTestNeeds.Typing | SelfTestNeeds.ChatTwoOff,
        StepList = [
            SetupAuto("The test channel pink, a line with an item link in the middle", "9", async context => {
                var channel = await context.TestChannelAsync().ConfigureAwait(false);
                await context.ChangeSettingAsync(SelfTestSetting.ColourWholeLine, true).ConfigureAwait(false);
                await context.ChangeSettingAsync(SelfTestSetting.SayWhenTalking, true).ConfigureAwait(false);
                await context.Host.SetChannelColourAsync(channel, ChannelColour.Custom(Pink)).ConfigureAwait(false);
                await context.Host.SetShowInGameChatAsync(channel, true).ConfigureAwait(false);
                await context.StopTalkingAsync().ConfigureAwait(false);
                var mark = context.Host.Mark();
                await context.TypeAsync($"{await context.CommandOfAsync(channel).ConfigureAwait(false)} {context.NextMarker()}", TestLink.ItemInMiddle, SelfTestAction.ChannelMessage)
                    .ConfigureAwait(false);
                var shown = await context.PrintedAsync(mark, line => line.Kind == TapKind.Message && line.ChannelId == channel && line.Links.Count == 1, TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);
                return StepResult.Expect(shown != null, "the line with the item link didn't show in game chat");
            }, types: true),
            Ask("The line, after the link too", "9", new GuidedPrompt(
                "Look at the test line just printed, with an item link in the middle: is the tag, your name and the text pink, like this swatch, and the text after the link still pink?",
                PromptKind.YesNo) { Swatch = Pink, Short = "the line" }),
            Ask("Talking in it", "9", async context => {
                var channel = await context.TestChannelAsync().ConfigureAwait(false);
                await context.TypeAsync(await context.CommandOfAsync(channel).ConfigureAwait(false)).ConfigureAwait(false);
                await context.TalkingInAsync(channel).ConfigureAwait(false);
                return new GuidedPrompt("Is the tag in \"Now talking in [...]\" pink within the blue line, and does the server info bar show \"LG [...]\" in pink?", PromptKind.YesNo) {
                    Swatch = Pink, Short = "talking in it",
                };
            }),
            Ask("The channel list and its window", "9", async context => {
                var channel = await context.TestChannelAsync().ConfigureAwait(false);
                await context.StopTalkingAsync().ConfigureAwait(false);
                if (await context.WindowOfAsync(channel, TimeSpan.Zero).ConfigureAwait(false) == null) {
                    await context.Host.OpenWindowAsync(channel).ConfigureAwait(false);
                }

                return new GuidedPrompt("Open /lookingglass: are the test channel's bar and dot in the channel list pink? Is its window's tab pink?", PromptKind.YesNo) {
                    Swatch = Pink, Short = "list and window",
                };
            }),
            Rest("9", "the invite submenu (needs another player) and the caught-up message (needs a partner)"),
        ],
    };

    // ---------------------------------------------------------------- name-colours 21

    private static SelfTestCheck NameColourKept() => new() {
        Id = "name-colours-21", Title = "The colour is kept (name colours)", Steps = "name-colours 21", Mode = CheckMode.AutomaticAndGuided, Needs = SelfTestNeeds.Connection,
        StepList = [
            Auto("Your own name in orange", "21", async context => {
                var before = await context.Host.ReadSettingAsync(SelfTestSetting.OwnNameColour).ConfigureAwait(false);
                await context.ChangeSettingAsync(SelfTestSetting.OwnNameColour, before.Deserialize<OwnNameColourValue>()! with { Rgb = Orange }).ConfigureAwait(false);
                return StepResult.Expect(await context.Host.OwnNameColourAsync().ConfigureAwait(false) == Orange, "your name colour wasn't set");
            }),
            CarryOn(Reload, "21"),
            Auto("Kept after the reload", "21", async context =>
                StepResult.Expect(await context.Host.OwnNameColourAsync().ConfigureAwait(false) == Orange, "your name colour was lost when the plugin was turned off and on")),
            CarryOn(Relog, "21"),
            Auto("Kept after the relog", "21", async context => {
                var kept = await context.Host.OwnNameColourAsync().ConfigureAwait(false) == Orange;
                return kept ? StepResult.Pass(SelfTestWords.NeedsAnotherCharacter + " (another character sees it too)") : StepResult.Fail("your name colour was lost after the relog");
            }),
        ],
    };

    // ---------------------------------------------------------------- name-colours 23

    private static SelfTestCheck NameColourSaved() => new() {
        Id = "name-colours-23", Title = "What's saved in the settings file", Steps = "name-colours 23 (first part)", Mode = CheckMode.Automatic, Needs = SelfTestNeeds.Connection,
        StepList = [
            Auto("Under NameColours, keyed by a number only", "23", async context => {
                var own = (await context.Host.ReadSettingAsync(SelfTestSetting.OwnNameColour).ConfigureAwait(false)).Deserialize<OwnNameColourValue>()!;
                await context.ChangeSettingAsync(SelfTestSetting.OwnNameColour, own with { Rgb = Orange }).ConfigureAwait(false);
                await context.DelayAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                var saved = await context.Host.SavedNameColoursAsync().ConfigureAwait(false);
                return StepResult.All((saved.TryGetValue(own.Key, out var rgb) && (rgb & 0xFFFFFF) == Orange, "the colour isn't saved under NameColours"),
                    (saved.Keys.All(key => key.All(char.IsAsciiDigit) || key.Contains('@')), "a name colour is saved under something other than a number"),
                    (own.Key.All(char.IsAsciiDigit), "your colour isn't keyed by a number only"));
            }),
        ],
    };

    // ---------------------------------------------------------------- custom-colours 15-16

    private static SelfTestCheck ChannelColoursKept() => new() {
        Id = "custom-colours-15-16", Title = "Colours are kept", Steps = "custom-colours 15-16", Mode = CheckMode.AutomaticAndGuided,
        Needs = SelfTestNeeds.Connection | SelfTestNeeds.TestChannel,
        StepList = [
            Auto("A swatch on one test channel, a custom colour on another", "15", async context => {
                var (first, second) = (await context.TestChannelAsync(0).ConfigureAwait(false), await context.TestChannelAsync(1).ConfigureAwait(false));
                await context.Host.SetChannelColourAsync(first, ChannelColour.OfRow(Swatch)).ConfigureAwait(false);
                await context.Host.SetChannelColourAsync(second, ChannelColour.Custom(Pink)).ConfigureAwait(false);
                return await KeptAsync(context, "").ConfigureAwait(false);
            }),
            CarryOn(Reload, "15"),
            Auto("Kept after the reload", "15", context => KeptAsync(context, " after the reload")),
            CarryOn(Relog, "15"),
            Auto("Kept after the relog", "15", async context => {
                var result = await KeptAsync(context, " after the relog").ConfigureAwait(false);
                return result.Outcome == StepOutcome.Passed ? StepResult.Pass("another character's own colours, and a colour from before 0.2.8 (16)") : result;
            }),
        ],
    };

    /// <summary>A swatch from the picker's rows (a green).</summary>
    private const ushort Swatch = 45;

    private static async Task<StepResult> KeptAsync(StepContext context, string when) {
        var (first, second) = (await context.TestChannelAsync(0).ConfigureAwait(false), await context.TestChannelAsync(1).ConfigureAwait(false));
        var one = await context.Host.SavedChannelColourAsync(first).ConfigureAwait(false);
        var two = await context.Host.SavedChannelColourAsync(second).ConfigureAwait(false);
        return StepResult.All((one == ChannelColour.OfRow(Swatch), $"the swatch colour wasn't kept{when}"), (two == ChannelColour.Custom(Pink), $"the custom colour wasn't kept{when}"));
    }

    // ---------------------------------------------------------------- custom-colours 17

    private static SelfTestCheck LeavingDropsIt() => new() {
        Id = "custom-colours-17", Title = "Leaving drops the colour", Steps = "custom-colours 17", Mode = CheckMode.Automatic, Needs = SelfTestNeeds.Connection | SelfTestNeeds.TestChannel,
        StepList = [
            Auto("A custom colour on the third test channel, then leave it", "17", async context => {
                // The run's third test channel: no check after this one uses it.
                var channel = await context.TestChannelAsync(2).ConfigureAwait(false);
                await context.Host.SetChannelColourAsync(channel, ChannelColour.Custom(Pink)).ConfigureAwait(false);
                var set = await context.WaitUntilAsync(async () => await context.Host.SavedChannelColourAsync(channel).ConfigureAwait(false) != null).ConfigureAwait(false);
                await context.Host.LeaveChannelAsync(channel).ConfigureAwait(false);
                var gone = await context.WaitUntilAsync(async () => await context.Host.SavedChannelColourAsync(channel).ConfigureAwait(false) == null, TimeSpan.FromSeconds(30))
                    .ConfigureAwait(false);
                var result = StepResult.All((set, "the colour wasn't saved"), (gone, "the colour stayed in the settings file after leaving"));
                return result.Outcome == StepOutcome.Passed ? StepResult.Pass("being invited back (needs a partner)") : result;
            }),
        ],
    };
}
