using System.Text.Json;
using LookingGlass.Core.Client;
using static LookingGlass.Core.SelfTest.SelfTestSteps;

namespace LookingGlass.Core.SelfTest.Suites;

/// <summary>
/// "Colours" (one player): what is left of name-colours-checklist.md 21 and custom-colours-checklist.md 15, another of the
/// tester's characters (custom-colours 3 to 9, 15 to 17 and name-colours 21 and 23 passed in the owner's fourth run,
/// 2026-10-13, plugin 0.2.18, and are no longer offered; the parts that need a partner are in the two-player colours
/// suite). The runner sets the colours; the other character is the tester's to come in with, and to say.
/// </summary>
public static class ColoursSuite {
    public const string Id = "colours";

    private const uint Pink = 0xFF66CC;
    private const uint Orange = 0xFFAA00;

    /// <summary>A swatch from the picker's rows (a green).</summary>
    private const ushort Swatch = 45;

    public static SelfTestSuite Create() => new(Id, "Colours", [NameColourOtherCharacter(), ChannelColoursOtherCharacter()]) {
        Changes = [SelfTestSetting.OwnNameColour],
        Planned = new Dictionary<SelfTestAction, int> { [SelfTestAction.CreateChannel] = 2 },
        Note = "needs another of your characters",
    };

    /// <summary>Another of the tester's characters, registered: asked first, the check skipped without one.</summary>
    private static SelfTestStep AnotherCharacter(string checklist, string which) => SetupGuided("Another of your characters", checklist, async context =>
        await context.AskAsync(new GuidedPrompt($"Do you have another character, registered with LookingGlass, {which}?", PromptKind.YesNo)).ConfigureAwait(false) is GuidedAnswer.Yes
            ? StepResult.Pass()
            : StepResult.SkipCheck(SelfTestWords.NeedsAnotherCharacter));

    // ---------------------------------------------------------------- name-colours 21 (another character)

    private static SelfTestCheck NameColourOtherCharacter() => new() {
        Id = "name-colours-21-other", Title = "The colour is kept: another of your characters", Steps = "name-colours 21 (another character)", Mode = CheckMode.Guided,
        Needs = SelfTestNeeds.Connection,
        StepList = [
            AnotherCharacter("21", "that shares a channel with this one"),
            SetupAuto("Your own name in orange", "21", async context => {
                var before = await context.Host.ReadSettingAsync(SelfTestSetting.OwnNameColour).ConfigureAwait(false);
                await context.ChangeSettingAsync(SelfTestSetting.OwnNameColour, before.Deserialize<OwnNameColourValue>()! with { Rgb = Orange }).ConfigureAwait(false);
                return await context.SettledAsync(async () => StepResult.Expect(await context.Host.OwnNameColourAsync().ConfigureAwait(false) == Orange, "your name colour wasn't set"))
                    .ConfigureAwait(false);
            }),
            Ask("Seen from the other character", "21", new GuidedPrompt(
                "Come in with that other character (the self-test waits, and does nothing meanwhile), and open a channel both share: is this character's name in this orange? " +
                "Then come back in with this character, and answer.", PromptKind.YesNo) { Swatch = Orange, Short = "another character" }),
        ],
    };

    // ---------------------------------------------------------------- custom-colours 15 (another character)

    private static SelfTestCheck ChannelColoursOtherCharacter() => new() {
        Id = "custom-colours-15-other", Title = "Colours are kept: another character's own", Steps = "custom-colours 15 (another character)", Mode = CheckMode.Guided,
        Needs = SelfTestNeeds.Connection | SelfTestNeeds.TestChannel,
        StepList = [
            AnotherCharacter("15", "with channels of its own"),
            SetupAuto("A swatch on one test channel, a custom colour on another", "15", async context => {
                var (first, second) = (await context.TestChannelAsync(0).ConfigureAwait(false), await context.TestChannelAsync(1).ConfigureAwait(false));
                await context.Host.SetChannelColourAsync(first, ChannelColour.OfRow(Swatch)).ConfigureAwait(false);
                await context.Host.SetChannelColourAsync(second, ChannelColour.Custom(Pink)).ConfigureAwait(false);
                return StepResult.Pass();
            }),
            Ask("The other character's own colours", "15", new GuidedPrompt(
                "Come in with that other character (the self-test waits, and does nothing meanwhile): are its channels' colours its own, as you left them, not this one's? " +
                "Then come back in with this character, and answer.", PromptKind.YesNo) { Short = "its own colours" }),
        ],
    };
}
