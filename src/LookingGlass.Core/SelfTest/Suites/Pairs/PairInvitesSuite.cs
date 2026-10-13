using static LookingGlass.Core.SelfTest.SelfTestSteps;
using static LookingGlass.Core.SelfTest.Suites.PairSteps;

namespace LookingGlass.Core.SelfTest.Suites;

/// <summary>
/// "Right-click invites, with a partner": context-invites-checklist.md 1 to 6, 12 to 14, 16 and 18. The partner's player
/// says a test line in Say (their plugin asks them, behind its gate); the tester right-clicks and invites through the menu;
/// the runner checks each line and the partner's invite arriving (their plugin accepts invites to the host's test channels).
/// </summary>
public static class PairInvitesSuite {
    public const string Id = "pair-invites";

    public static SelfTestSuite Create() => new(Id, "Right-click invites, with a partner", [ChatTargetLists(), PartyAndFriends(), InChatTwo(), Inviting()]) {
        Planned = new Dictionary<SelfTestAction, int> { [SelfTestAction.CreateChannel] = 2, [SelfTestAction.PartnerMessage] = 14 },
        Note = "a party with your partner for one check",
    };

    private const SelfTestNeeds Needs = PairSteps.Needs;

    /// <summary>The partner's player says a test line in Say (asked by their plugin), and the host sees it.</summary>
    private static SelfTestStep TheySayHello(string checklist, PartnerAsk ask = PartnerAsk.SayHello) => Guided("Your partner says a line in Say", checklist, async context => {
        var mark = context.Host.GameChatSince(0).Count;
        var said = await context.Partner.AskAsync(context, PartnerVerb.Ask, SelfTestProtocol.NameOf(ask)).ConfigureAwait(false);
        if (said.Outcome != PartnerOutcome.Yes || !said.Fields.TryGetValue("line", out var line)) {
            return StepResult.SkipCheck("your partner didn't say a line");
        }

        var marker = $"{context.MarkerPrefix} {line}, please ignore";
        var seen = await context.WaitUntilAsync(() => Task.FromResult(context.Host.GameChatSince(0).Skip(mark)
            .Any(found => !found.FromTester && found.Text.Contains(marker, StringComparison.Ordinal))), Soon).ConfigureAwait(false);
        return seen ? StepResult.Pass() : StepResult.SkipCheck("your partner's line in Say didn't show for you");
    });

    // ---------------------------------------------------------------- context-invites 1, 3, 5

    private static SelfTestCheck ChatTargetLists() => new() {
        Id = "context-invites-1-3-5", Title = "Where the invite shows: chat, target, other lists", Steps = "context-invites 1, 3, 5", Mode = CheckMode.Guided, Needs = Needs | SelfTestNeeds.ChatTwoOff,
        StepList = [
            Auto("Another player gets the item", "1", async context => {
                await context.TestChannelAsync(1).ConfigureAwait(false);
                return StepResult.Expect(await context.Host.InviteOffersForAsync(RightClickKind.AnotherPlayer).ConfigureAwait(false) > 0, "another player wouldn't get the invite item");
            }),
            TheySayHello("1"),
            Ask("Their name in chat", "1", new GuidedPrompt(
                "Right-click your partner's name in their Say line: below the game's own items, Invite to LookingGlass with a blue L and an arrow, listing your test channels, " +
                "the one they're in greyed out?", PromptKind.YesNo) { Short = "chat" }),
            Ask("Their character and the target bar", "3", new GuidedPrompt("Target your partner, right-click their character, then the target bar's name: the same item and list?",
                PromptKind.YesNo) { Short = "target" }),
            Ask("Other lists", "5", new GuidedPrompt("If they're in a list you have at hand (a linkshell, the Free Company): the same there? (Skip if none.)", PromptKind.YesNo) {
                Short = "other lists",
            }),
        ],
    };

    // ---------------------------------------------------------------- context-invites 2, 4

    private static SelfTestCheck PartyAndFriends() => new() {
        Id = "context-invites-2-4", Title = "Where the invite shows: party and friends lists", Steps = "context-invites 2, 4", Mode = CheckMode.Guided, Needs = Needs,
        StepList = [
            PartyOfTwo("2"),
            Ask("The party list", "2", new GuidedPrompt("Right-click your partner in the party list, and in the Party Members window: the Invite to LookingGlass item and list?",
                PromptKind.YesNo) { Short = "party list" }),
            Guided("The friends list", "4", async context => {
                var (_, friend) = await context.Host.OtherAsync().ConfigureAwait(false);
                return friend != true
                    ? StepResult.Skip(SelfTestWords.NeedsFriends)
                    : await context.AskForResultAsync(new GuidedPrompt("Right-click your partner in your friends list (Social, Friend List): the same item and list?", PromptKind.YesNo) {
                        Short = "friends list",
                    }).ConfigureAwait(false);
            }),
            Guided("The party ends", "2", context => context.Partner.AskPlayerAsync(context, PartnerAsk.LeaveParty)),
        ],
    };

    // ---------------------------------------------------------------- context-invites 6

    private static SelfTestCheck InChatTwo() => new() {
        Id = "context-invites-6", Title = "Where the invite shows: ChatTwo", Steps = "context-invites 6 (group 1)", Mode = CheckMode.Guided, Needs = Needs | SelfTestNeeds.ChatTwo,
        StepList = [
            TheySayHello("6"),
            Ask("Their name in ChatTwo", "6", new GuidedPrompt(
                "Right-click your partner's name in their line in ChatTwo: at the bottom, Integrations, Invite to LookingGlass, with your test channels each in its colour? " +
                "Right-clicking the text (not the name) shows no LookingGlass item?", PromptKind.YesNo) { Short = "ChatTwo" }),
        ],
    };

    // ---------------------------------------------------------------- context-invites 12-14, 16, 18

    private static SelfTestCheck Inviting() => new() {
        Id = "context-invites-12-18", Title = "Inviting, and the greyed-out entries", Steps = "context-invites 12-14, 16, 18", Mode = CheckMode.Guided, Needs = Needs,
        StepList = [
            Auto("Two test channels they aren't in", "12", async context => {
                await context.TestChannelAsync(2).ConfigureAwait(false);
                foreach (var channel in context.TestChannels.Skip(1)) {
                    if ((await context.Host.MembersAsync(channel).ConfigureAwait(false)).Any(member => member.UserId == context.Partner.UserId)) {
                        await PartnerLeavesAsync(context, channel).ConfigureAwait(false);
                    }
                }

                return StepResult.Pass();
            }),
            InviteStep("Invite them through the menu", "12", 1),
            Ask("Greyed out", "13", new GuidedPrompt(
                "Right-click your partner again: is that channel greyed out (already a member) and can't be picked, while the other can?", PromptKind.YesNo) { Short = "greyed out" }),
            InviteStep("The other one", "14", 2),
            Guided("Blocked: every channel greyed out", "16", async context => {
                if (context.Data.OtherWasBlocked) {
                    return StepResult.Skip(SelfTestWords.PartnerWasBlocked);
                }

                await context.BlockPartnerAsync(true).ConfigureAwait(false);
                try {
                    return await context.AskForResultAsync(new GuidedPrompt("Right-click your partner: does every channel say (you blocked them), greyed out?", PromptKind.YesNo) {
                        Short = "blocked",
                    }).ConfigureAwait(false);
                } finally {
                    await context.BlockPartnerAsync(false).ConfigureAwait(false);
                }
            }),
            Ask("Unblocked: back to normal", "16", new GuidedPrompt("Right-click your partner again: back to normal?", PromptKind.YesNo) { Short = "unblocked" }),
            Guided("From ChatTwo's menu", "18", async context => (await context.EnvironmentAsync().ConfigureAwait(false)).ChatTwoLoaded
                ? await context.AskForResultAsync(new GuidedPrompt(
                    "Ask your partner to say something in Say. In ChatTwo, right-click their name: Integrations, Invite to LookingGlass: are the channels they're in greyed out and can't be clicked?",
                    PromptKind.YesNo) { Short = "ChatTwo" }).ConfigureAwait(false)
                : StepResult.Skip(SelfTestWords.NeedsChatTwo)),
        ],
    };

    /// <summary>The tester invites the partner to a test channel by hand; the runner sees the blue line and the partner join.</summary>
    private static SelfTestStep InviteStep(string title, string checklist, int index) => Guided(title, checklist, async context => {
        var channel = context.TestChannels[index];
        var tag = await context.TagOfAsync(channel).ConfigureAwait(false);
        var mark = context.Host.Mark();
        var answer = await context.AskAsync(new GuidedPrompt($"Right-click your partner (their name, or their character) and choose Invite to LookingGlass, then {tag}.") {
            Short = title.ToLowerInvariant(),
            Watch = StepContextExtensions.Until(() => Task.FromResult(context.Host.PrintedSince(mark).Any(line => line.Kind == TapKind.Notice && line.Text.StartsWith("Invited", StringComparison.Ordinal))),
                "no invite yet", "invited"),
        }).ConfigureAwait(false);
        if (answer != GuidedAnswer.Seen) {
            return StepContext.Judge(answer, null) is { Outcome: StepOutcome.Skipped } skipped ? skipped : StepResult.Fail("no \"Invited\" line showed");
        }

        // Their plugin accepts an invite to the host's own test channels by itself.
        return StepResult.Expect(await context.Partner.WaitMemberAsync(context, channel, member: true).ConfigureAwait(false), "your partner's plugin didn't get the invite");
    });
}
