using LookingGlass.Core.Client;
using static LookingGlass.Core.SelfTest.SelfTestSteps;

namespace LookingGlass.Core.SelfTest.Suites;

/// <summary>
/// "Party finder links" (one player, with a recruitment of their own): chat-links-checklist.md 24 and 25 (20 to 23 and 30
/// passed in the owner's first run, 2026-10-13, and are no longer offered). The tester sends the recruitment's link; the
/// runner sees that one party finder link went to the test channel with the cross-world mark the listing has.
/// </summary>
public static class PartyFinderSuite {
    public const string Id = "party-finder";

    public static SelfTestSuite Create() => new(Id, "Party finder links", [HomeWorld()]) {
        Changes = [SelfTestSetting.WindowsOnly],
        Planned = new Dictionary<SelfTestAction, int> { [SelfTestAction.ChannelMessage] = 4, [SelfTestAction.CreateChannel] = 1 },
        Note = "needs a recruitment of your own",
    };

    private const SelfTestNeeds Needs = SelfTestNeeds.Connection | SelfTestNeeds.TestChannel | SelfTestNeeds.Typing | SelfTestNeeds.ChatTwoOff;

    /// <summary>A recruitment of the tester's own, asked once per check: without one the check is skipped.</summary>
    private static SelfTestStep Recruitment() => SetupGuided("A recruitment of your own", null, async context => {
        // The test channel's lines in game chat, where the runner sees them.
        await context.ChangeSettingAsync(SelfTestSetting.WindowsOnly, false).ConfigureAwait(false);
        await context.Host.SetShowInGameChatAsync(await context.TestChannelAsync().ConfigureAwait(false), true).ConfigureAwait(false);
        var answer = await context.AskAsync(new GuidedPrompt(
            "Start a recruitment of your own in the Party Finder (any duty; a password keeps strangers out), if you haven't, and keep it up. Is it up?",
            PromptKind.YesNo)).ConfigureAwait(false);
        context.State["recruiting"] = answer is GuidedAnswer.Yes or GuidedAnswer.Done;
        return answer is GuidedAnswer.Yes or GuidedAnswer.Done ? StepResult.Pass() : StepResult.Skip("needs a recruitment of your own");
    });

    private static bool Recruiting(StepContext context) => context.State.TryGetValue("recruiting", out var yes) && (bool) yes;

    /// <summary>Waits for the tester to send a party finder link to the test channel; the link, or null.</summary>
    private static async Task<(StepResult Result, ChatLink.PartyFinder? Link)> SentAsync(StepContext context, string instruction, string shortName) {
        var channel = await context.TestChannelAsync().ConfigureAwait(false);
        var mark = context.Host.Mark();
        ChatLink.PartyFinder? link = null;
        var result = await context.AskForResultAsync(new GuidedPrompt(instruction) {
            Short = shortName,
            TesterTypes = true,
            Watch = StepContextExtensions.Until(() => {
                link = context.Host.PrintedSince(mark).Where(line => line.Kind == TapKind.Message && line.Own && line.ChannelId == channel)
                    .SelectMany(line => line.Links).OfType<ChatLink.PartyFinder>().FirstOrDefault();
                return Task.FromResult(link != null);
            }, "no party finder link sent to the test channel yet", "a party finder link was sent"),
        }).ConfigureAwait(false);
        var count = context.Host.PrintedSince(mark).Where(line => line.Kind == TapKind.Message && line.Own && line.ChannelId == channel)
            .Sum(line => line.Links.OfType<ChatLink.PartyFinder>().Count());
        if (result.Outcome == StepOutcome.Passed && link == null) {
            return (StepResult.Fail("no party finder link reached the test channel"), null);
        }

        if (result.Outcome == StepOutcome.Passed && count > 1) {
            return (StepResult.Fail("more than one party finder link was sent"), link);
        }

        var inSay = context.Host.GameChatSince(mark).Count(line => line.FromTester && line.Channel is GameChatKind.Say or GameChatKind.Party);
        return (inSay > 0 ? StepResult.Fail("something reached Say or Party") : result, link);
    }

    // ---------------------------------------------------------------- chat-links 24-25

    private static SelfTestCheck HomeWorld() => new() {
        Id = "chat-links-24-25", Title = "Home world only, and talking in the channel", Steps = "chat-links 24-25", Mode = CheckMode.Guided, Needs = Needs,
        StepList = [
            Recruitment(),
            Guided("Home world only: no mark", "24", async context => {
                if (!Recruiting(context)) {
                    return StepResult.Skip("needs a recruitment of your own");
                }

                var command = await context.CommandOfAsync(await context.TestChannelAsync().ConfigureAwait(false)).ConfigureAwait(false);
                var (result, link) = await SentAsync(context, $"Edit your recruitment so it's limited to your own world, open it again, then type {command} join , click its chat button " +
                                                              "and press Enter.", "home world").ConfigureAwait(false);
                return result.Outcome == StepOutcome.Passed && link is { HomeWorldOnly: false } ? StepResult.Fail("the link had the cross-world mark for a home-world listing") : result;
            }),
            Guided("The whole data centre: the mark is back", "24", async context => {
                if (!Recruiting(context)) {
                    return StepResult.Skip("needs a recruitment of your own");
                }

                var command = await context.CommandOfAsync(await context.TestChannelAsync().ConfigureAwait(false)).ConfigureAwait(false);
                var (result, link) = await SentAsync(context, $"Set your recruitment back to the whole data centre, open it again, then type {command} join , click its chat button " +
                                                              "and press Enter.", "data centre").ConfigureAwait(false);
                return result.Outcome == StepOutcome.Passed && link is { HomeWorldOnly: true } ? StepResult.Fail("the link had no cross-world mark for a data centre listing") : result;
            }),
            Guided("Talking in the channel: join and the button", "25", async context => {
                if (!Recruiting(context)) {
                    return StepResult.Skip("needs a recruitment of your own");
                }

                var channel = await context.TestChannelAsync().ConfigureAwait(false);
                await context.TypeAsync(await context.CommandOfAsync(channel).ConfigureAwait(false)).ConfigureAwait(false);
                await context.TalkingInAsync(channel).ConfigureAwait(false);
                return (await SentAsync(context, "You're talking in the test channel. Type join , click your recruitment's chat button, and press Enter.", "talking").ConfigureAwait(false)).Result;
            }),
            Guided("Talking in the channel: the button alone", "25", async context => {
                if (!Recruiting(context)) {
                    return StepResult.Skip("needs a recruitment of your own");
                }

                return (await SentAsync(context, "Still talking in the test channel: click the chat button alone, and press Enter.", "button alone").ConfigureAwait(false)).Result;
            }),
            SetupAuto("Back to Say", "25", async context => {
                await context.StopTalkingAsync().ConfigureAwait(false);
                return StepResult.Pass("checking the mark after looking at another player's listing (24), and /lgc with the nickname (25)");
            }),
        ],
    };
}
