using System.Text.Json;
using LookingGlass.Core.Client;
using static LookingGlass.Core.SelfTest.SelfTestSteps;
using static LookingGlass.Core.SelfTest.Suites.PairSteps;

namespace LookingGlass.Core.SelfTest.Suites;

/// <summary>
/// "Colours, with a partner": name-colours-checklist.md 1 to 20 and 22, and custom-colours-checklist.md 9's caught-up part.
/// The partner sends as asked (with an item link); the tester sets the colours through the menus and looks; the runner
/// checks what was saved, and that a colour stays with the partner when they leave a channel.
/// </summary>
public static class PairColoursSuite {
    public const string Id = "pair-colours";

    private const uint Teal = 0x33DDAA;
    private const uint Pink = 0xFF66CC;
    private const uint Orange = 0xFFAA00;

    public static SelfTestSuite Create() => new(Id, "Colours, with a partner", [Setting(), InGameChat(), InChatTwo(), BackToDefault(), Rejoining(), CaughtUpPink()]) {
        Changes = [SelfTestSetting.PartnerNameColour, SelfTestSetting.OwnNameColour, SelfTestSetting.ColourWholeLine],
        Planned = new Dictionary<SelfTestAction, int> { [SelfTestAction.CreateChannel] = 1, [SelfTestAction.Invite] = 2, [SelfTestAction.Lookup] = 1, [SelfTestAction.PartnerMessage] = 30 },
        Note = "2 relogs",
    };

    private const SelfTestNeeds Needs = PairSteps.Needs;

    /// <summary>The partner's colour as saved now (the settings file), or null.</summary>
    private static async Task<uint?> SavedPartnerColourAsync(StepContext context) {
        var key = (await context.Host.ReadSettingAsync(SelfTestSetting.PartnerNameColour).ConfigureAwait(false)).Deserialize<OwnNameColourValue>()?.Key;
        var saved = await context.Host.SavedNameColoursAsync().ConfigureAwait(false);
        return key != null && saved.TryGetValue(key, out var rgb) ? rgb & 0xFFFFFF : null;
    }

    /// <summary>Both test channels with the partner: the first teal and nicknamed, the second default. Settings snapshotted.</summary>
    private static async Task<(string Teal, string Plain)> SetUpAsync(StepContext context) {
        var first = context.Partner.ChannelId!;
        var second = await ChannelWithPartnerAsync(context, 1).ConfigureAwait(false);
        await context.Host.SetChannelColourAsync(first, ChannelColour.Custom(Teal)).ConfigureAwait(false);
        await context.Host.SetChannelColourAsync(second, null).ConfigureAwait(false);
        await context.Host.SetNicknameAsync(first, "teal").ConfigureAwait(false);
        await context.ChangeSettingAsync(SelfTestSetting.ColourWholeLine, true).ConfigureAwait(false);
        // Snapshotted before the tester changes them by hand: put back at the end, whatever they choose.
        if (!context.Snapshotted(SelfTestSetting.PartnerNameColour)) {
            await context.ChangeSettingAsync(SelfTestSetting.PartnerNameColour, await context.Host.ReadSettingAsync(SelfTestSetting.PartnerNameColour).ConfigureAwait(false))
                .ConfigureAwait(false);
        }

        if (!context.Snapshotted(SelfTestSetting.OwnNameColour)) {
            await context.ChangeSettingAsync(SelfTestSetting.OwnNameColour, await context.Host.ReadSettingAsync(SelfTestSetting.OwnNameColour).ConfigureAwait(false))
                .ConfigureAwait(false);
        }

        return (first, second);
    }

    // ---------------------------------------------------------------- name-colours 1-6

    private static SelfTestCheck Setting() => new() {
        Id = "name-colours-1-6", Title = "Setting a colour", Steps = "name-colours 1-6 (group 1)", Mode = CheckMode.Guided, Needs = Needs,
        StepList = [
            SetupAuto("Your partner in two test channels, one teal", "1", async context => {
                await SetUpAsync(context).ConfigureAwait(false);
                return StepResult.Pass();
            }),
            Ask("The menu", "1", new GuidedPrompt(
                "Open /lookingglass and select the test channel (teal). Right-click your partner's row in the member list: is there a small menu with their name and Name colour..., " +
                "and the same in their ⋮ menu?", PromptKind.YesNo) { Short = "the menu" }),
            Ask("The picker", "2", new GuidedPrompt(
                "Choose Name colour...: is there a colour wheel, a Colour code field, a Preview with their name, the closest game colour, and Use this colour, Default and Cancel? " +
                "Does the wheel start on teal?", PromptKind.YesNo) { Short = "the picker" }),
            Ask("Codes it refuses", "3", new GuidedPrompt(
                "Type #FF66C: is there a line about colour codes, and Use this colour greyed out? Type #1A2B6D: a line about very dark colours, and Use this colour still works?",
                PromptKind.YesNo) { Short = "refused codes" }),
            Ask("Pink for your partner", "3-4", context => Task.FromResult(new GuidedPrompt("Type #FF66CC and choose Use this colour. Then select the other test channel.") {
                Short = "pink",
                Watch = StepContextExtensions.Until(async () => await SavedPartnerColourAsync(context).ConfigureAwait(false) == Pink, "not saved yet", "saved: pink"),
            })),
            Ask("In both member lists", "4", new GuidedPrompt("Is your partner's name pink in both test channels' member lists?", PromptKind.YesNo) { Swatch = Pink, Short = "both lists" }),
            Ask("Orange for you", "5", context => Task.FromResult(new GuidedPrompt("Right-click your own row in the member list, and give yourself #FFAA00 the same way.") {
                Short = "orange",
                Watch = StepContextExtensions.Until(async () => await context.Host.OwnNameColourAsync().ConfigureAwait(false) == Orange, "not yet", "your name is orange"),
            })),
            Ask("Cancel changes nothing", "6", new GuidedPrompt(
                "Open Name colour... for your partner again, pick another colour, and press Cancel (then again with Escape, and by clicking outside it). Is their name still pink?",
                PromptKind.YesNo) { Swatch = Pink, Short = "cancel" }),
            Auto("Still pink, as saved", "6", async context => StepResult.Expect(await SavedPartnerColourAsync(context).ConfigureAwait(false) == Pink, "Cancel changed the saved colour")),
        ],
    };

    // ---------------------------------------------------------------- name-colours 7-15

    private static SelfTestCheck InGameChat() => new() {
        Id = "name-colours-7-15", Title = "Name colours in game chat and windows", Steps = "name-colours 7-15", Mode = CheckMode.Guided, Needs = Needs | SelfTestNeeds.ChatTwoOff,
        StepList = [
            Auto("Your partner's colour, and yours", "7", async context => {
                await SetUpAsync(context).ConfigureAwait(false);
                await SetPartnerColourAsync(context, Pink).ConfigureAwait(false);
                return StepResult.Pass();
            }),
            LooksStep("In the teal channel", "7", 1, null, "In game chat, is your partner's line [teal] teal, their name exactly this pink, and the message teal again?"),
            LooksStep("In the default channel", "8", 1, 1, "Is their line in the other test channel [LGC..] in LookingGlass blue, their name pink, the rest in the chat channel's own colour?"),
            LooksStep("With an item link", "9", 1, null, "Is their name pink, the text before and after the item link teal, and the link a working item link?", "item"),
            Guided("Colour the whole line off", "11", async context => {
                await context.ChangeSettingAsync(SelfTestSetting.ColourWholeLine, false).ConfigureAwait(false);
                await PartnerSendsAsync(context, 1).ConfigureAwait(false);
                var result = await context.AskForResultAsync(new GuidedPrompt("With Colour the whole line off: is only [teal] teal and the name pink, the rest in the chat channel's colour?",
                    PromptKind.YesNo) { Short = "whole line off" }).ConfigureAwait(false);
                await context.ChangeSettingAsync(SelfTestSetting.ColourWholeLine, true).ConfigureAwait(false);
                return result;
            }),
            .. RelogWhileThePartnerSends("13", 1),
            Ask("A caught-up message", "13", new GuidedPrompt("Is the caught-up line [teal] teal, the time uncoloured, your partner's name pink, the message teal?", PromptKind.YesNo) {
                Swatch = Pink, Short = "caught up",
            }),
            Guided("In a channel window", "14", async context => {
                var channel = context.Partner.ChannelId!;
                if (await context.WindowOfAsync(channel, TimeSpan.Zero).ConfigureAwait(false) == null) {
                    await context.Host.OpenWindowAsync(channel).ConfigureAwait(false);
                }

                return await context.AskForResultAsync(new GuidedPrompt("In the test channel's window, is your partner's name pink on their lines?", PromptKind.YesNo) {
                    Swatch = Pink, Short = "window",
                }).ConfigureAwait(false);
            }),
            Ask("From a line's menu", "15", new GuidedPrompt(
                "Right-click one of your partner's lines in the window: Copy and Name colour...? Pick #8844FF: does their name change at once in the window and the member lists?",
                PromptKind.YesNo) { Short = "the line's menu" }),
        ],
    };

    /// <summary>The partner sends (in the first test channel, or the second), and the tester looks.</summary>
    private static SelfTestStep LooksStep(string title, string checklist, int count, int? index, string question, string? link = null) => Guided(title, checklist, async context => {
        var channel = index is { } at ? context.TestChannels[at] : context.Partner.ChannelId!;
        var (_, arrived) = await PartnerSendsAsync(context, count, channel, link).ConfigureAwait(false);
        if (arrived == 0) {
            return StepResult.Fail("your partner's line didn't arrive");
        }

        return await context.AskForResultAsync(new GuidedPrompt(question, PromptKind.YesNo) { Swatch = Pink, Short = title.ToLowerInvariant() }).ConfigureAwait(false);
    });

    // ---------------------------------------------------------------- name-colours 16-19

    private static SelfTestCheck InChatTwo() => new() {
        Id = "name-colours-16-19", Title = "Name colours in ChatTwo", Steps = "name-colours 16-19", Mode = CheckMode.Guided, Needs = Needs | SelfTestNeeds.ChatTwo,
        StepList = [
            SetupAuto("Your partner's colour", "16", async context => {
                await SetUpAsync(context).ConfigureAwait(false);
                await SetPartnerColourAsync(context, Pink).ConfigureAwait(false);
                return StepResult.Pass();
            }),
            LooksStep("In ChatTwo", "16", 1, null, "In ChatTwo, is your partner's name exactly pink, and the message teal after it (and after an item link)? Nothing runs on into the next line?", "item"),
            LooksStep("The default channel in ChatTwo", "17", 1, 1, "In the other test channel: the name pink, the rest in the chat channel's colour?"),
            Guided("Whole line off in ChatTwo", "18", async context => {
                await context.ChangeSettingAsync(SelfTestSetting.ColourWholeLine, false).ConfigureAwait(false);
                await PartnerSendsAsync(context, 1).ConfigureAwait(false);
                var result = await context.AskForResultAsync(new GuidedPrompt("Only the tag and the name coloured?", PromptKind.YesNo) { Short = "whole line off" }).ConfigureAwait(false);
                await context.ChangeSettingAsync(SelfTestSetting.ColourWholeLine, true).ConfigureAwait(false);
                return result;
            }),
            Ask("Anything different", "19", new GuidedPrompt("Does anything look different in ChatTwo from the game's chat log? Put it in the note if so.", PromptKind.DoThis) { Short = "noted" }),
        ],
    };

    // ---------------------------------------------------------------- name-colours 20

    private static SelfTestCheck BackToDefault() => new() {
        Id = "name-colours-20", Title = "Back to default", Steps = "name-colours 20", Mode = CheckMode.Guided, Needs = Needs,
        StepList = [
            SetupAuto("Your partner pink", "20", async context => {
                await SetUpAsync(context).ConfigureAwait(false);
                await SetPartnerColourAsync(context, Pink).ConfigureAwait(false);
                return StepResult.Pass();
            }),
            Ask("Default", "20", context => Task.FromResult(new GuidedPrompt("Right-click your partner in the member list, Name colour..., Default.") {
                Short = "default",
                Watch = StepContextExtensions.Until(async () => await SavedPartnerColourAsync(context).ConfigureAwait(false) == null, "still saved", "no colour saved"),
            })),
            LooksStep("Their next line", "20", 1, null, "Is your partner's next line exactly as before name colours (the name in the line's colour)?"),
        ],
    };

    // ---------------------------------------------------------------- name-colours 22

    private static SelfTestCheck Rejoining() => new() {
        Id = "name-colours-22", Title = "Leaving and rejoining", Steps = "name-colours 22", Mode = CheckMode.Automatic, Needs = Needs,
        StepList = [
            Auto("They leave one channel: the colour stays", "22", async context => {
                var (_, second) = await SetUpAsync(context).ConfigureAwait(false);
                await SetPartnerColourAsync(context, Pink).ConfigureAwait(false);
                if (!await PartnerLeavesAsync(context, second).ConfigureAwait(false)) {
                    return StepResult.Fail("your partner didn't leave");
                }

                return StepResult.Expect(await context.Host.NameColourOfAsync(context.Partner.UserId).ConfigureAwait(false) == Pink, "the colour went when they left a channel");
            }),
            Auto("Invited back: the same colour", "22", async context => {
                if (!await context.Partner.InviteAndWaitAsync(context, context.TestChannels[1]).ConfigureAwait(false)) {
                    return StepResult.Fail("your partner didn't join again");
                }

                return StepResult.All((await context.Host.NameColourOfAsync(context.Partner.UserId).ConfigureAwait(false) == Pink, "the colour wasn't the same"),
                    (await SavedPartnerColourAsync(context).ConfigureAwait(false) == Pink, "the saved colour changed"));
            }),
        ],
    };

    // ---------------------------------------------------------------- custom-colours 9 (caught up)

    private static SelfTestCheck CaughtUpPink() => new() {
        Id = "custom-colours-9-caught-up", Title = "Missed messages are pink too", Steps = "custom-colours 9 (caught-up part)", Mode = CheckMode.Guided, Needs = Needs,
        StepList = [
            SetupAuto("The test channel pink", "9", async context => {
                await context.Host.SetChannelColourAsync(context.Partner.ChannelId!, ChannelColour.Custom(Pink)).ConfigureAwait(false);
                await context.ChangeSettingAsync(SelfTestSetting.ColourWholeLine, true).ConfigureAwait(false);
                return StepResult.Pass();
            }),
            .. RelogWhileThePartnerSends("9", 1),
            Ask("The caught-up line", "9", new GuidedPrompt("Is the caught-up line's tag exactly this pink, the time uncoloured, the rest pink?", PromptKind.YesNo) {
                Swatch = Pink, Short = "caught up",
            }),
        ],
    };
}
