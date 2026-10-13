using LookingGlass.Core.Client;
using static LookingGlass.Core.SelfTest.SelfTestSteps;

namespace LookingGlass.Core.SelfTest.Suites;

/// <summary>
/// "Party finder links" (one player, with a recruitment of their own): chat-links-checklist.md 20 to 25 and 30. The tester
/// clicks the recruitment's chat button and sends; the runner sees the link kind put in the chat input (from the
/// "[sticky]" line, a number only), and that one party finder link went to the test channel with the cross-world mark the
/// listing has. What it looks like, and clicking it, is the tester's to say.
/// </summary>
public static class PartyFinderSuite {
    public const string Id = "party-finder";

    public static SelfTestSuite Create() => new(Id, "Party finder links", [ChatButton(), Sending(), HomeWorld(), TheLog()]) {
        Planned = new Dictionary<SelfTestAction, int> { [SelfTestAction.ChannelMessage] = 8, [SelfTestAction.CreateChannel] = 1 },
        Note = "needs a recruitment of your own",
    };

    private const SelfTestNeeds Needs = SelfTestNeeds.Connection | SelfTestNeeds.TestChannel | SelfTestNeeds.Typing | SelfTestNeeds.ChatTwoOff;

    /// <summary>A recruitment of the tester's own, asked once per check: without one the check is skipped.</summary>
    private static SelfTestStep Recruitment() => Guided("A recruitment of your own", null, async context => {
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

    // ---------------------------------------------------------------- chat-links 20

    private static SelfTestCheck ChatButton() => new() {
        Id = "chat-links-20", Title = "What the chat button puts in the input", Steps = "chat-links 20", Mode = CheckMode.Guided, Needs = Needs,
        StepList = [
            Recruitment(),
            Guided("The chat button while talking in the channel", "20", async context => {
                if (!Recruiting(context)) {
                    return StepResult.Skip("needs a recruitment of your own");
                }

                var channel = await context.TestChannelAsync().ConfigureAwait(false);
                await context.StopTalkingAsync().ConfigureAwait(false);
                await context.TypeAsync(await context.CommandOfAsync(channel).ConfigureAwait(false)).ConfigureAwait(false);
                await context.TalkingInAsync(channel).ConfigureAwait(false);
                var before = context.Host.LogCounts().LastLinkKind;
                var answer = await context.AskAsync(new GuidedPrompt(
                    "You're talking in the test channel now. Open your recruitment window and click its chat button (the speech bubble). Does <pfinder> appear in the chat input? " +
                    "Don't press Enter yet.", PromptKind.YesNo) { Short = "<pfinder>" }).ConfigureAwait(false);
                var kind = context.Host.LogCounts().LastLinkKind;
                var judged = StepContext.Judge(answer, "<pfinder>");
                if (judged.Outcome != StepOutcome.Passed) {
                    return judged;
                }

                return StepResult.Expect(kind is 1120 or 1121 && kind != before, $"the link kind seen was {kind?.ToString() ?? "none"}, not 1120 or 1121");
            }),
        ],
    };

    // ---------------------------------------------------------------- chat-links 21-23

    private static SelfTestCheck Sending() => new() {
        Id = "chat-links-21-23", Title = "Sending one, and clicking it", Steps = "chat-links 21-23", Mode = CheckMode.Guided, Needs = Needs,
        StepList = [
            Recruitment(),
            Guided("Sending it with /lgc", "21", async context => {
                if (!Recruiting(context)) {
                    return StepResult.Skip("needs a recruitment of your own");
                }

                await context.StopTalkingAsync().ConfigureAwait(false);
                var command = await context.CommandOfAsync(await context.TestChannelAsync().ConfigureAwait(false)).ConfigureAwait(false);
                var (result, link) = await SentAsync(context, $"In the game's chat box type {command} join  (with a space after it), click your recruitment's chat button, then press Enter.",
                    "sent").ConfigureAwait(false);
                if (link != null) {
                    context.State["homeWorldOnly"] = link.HomeWorldOnly;
                }

                return result;
            }),
            Ask("How it looks", "21", new GuidedPrompt(
                "In game chat, is the test channel's line \"join \" then one link: the link arrow, a party finder icon, \"Looking for Party (your name)\", and the cross-world mark if your " +
                "listing is open to other worlds? Compare with the game's own: type /e , click the chat button, Enter.", PromptKind.YesNo) { Short = "the link" }),
            Ask("Clicking it in game chat", "22", new GuidedPrompt("Click the link in game chat: does the Party Finder open on your listing?", PromptKind.YesNo) { Short = "click in chat" }),
            Ask("In its window", "23", async context => {
                var channel = await context.TestChannelAsync().ConfigureAwait(false);
                if (await context.WindowOfAsync(channel, TimeSpan.Zero).ConfigureAwait(false) == null) {
                    await context.Host.OpenWindowAsync(channel).ConfigureAwait(false);
                }

                return new GuidedPrompt("In the test channel's window, is the listing an orange link with a small people icon before it? Does hovering say it's a party finder listing " +
                                        "named by whoever sent it, and clicking it open the listing?", PromptKind.YesNo) { Short = "in the window" };
            }),
        ],
    };

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
            Auto("Back to Say", "25", async context => {
                await context.StopTalkingAsync().ConfigureAwait(false);
                return StepResult.Pass("checking the mark after looking at another player's listing (24), and /lgc with the nickname (25)");
            }),
        ],
    };

    // ---------------------------------------------------------------- chat-links 30

    private static SelfTestCheck TheLog() => new() {
        Id = "chat-links-30", Title = "No names in the log", Steps = "chat-links 30", Mode = CheckMode.Automatic,
        StepList = [
            Auto("The [sticky] lines hold counts only", "30", context => {
                var counts = context.Host.LogCounts();
                return Task.FromResult(counts.StickyLines == 0
                    ? StepResult.Skip("needs the party finder checks above")
                    : StepResult.Expect(counts.ForbiddenHits == 0, "/xllog held a name, a channel's name or a test line"));
            }),
        ],
    };
}
