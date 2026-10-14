using LookingGlass.Core.Client;
using static LookingGlass.Core.SelfTest.SelfTestSteps;
using static LookingGlass.Core.SelfTest.Suites.PairSteps;

namespace LookingGlass.Core.SelfTest.Suites;

/// <summary>
/// "Messages and check codes" (two players): chat-links-checklist.md 16, 17 and 19. Lines both ways, with and without an
/// item link, while the partner leaves and is invited back; no warning on either side; both check codes the same, before
/// and after the change.
/// </summary>
public static class PairMessagesSuite {
    public const string Id = "pair-messages";

    public static SelfTestSuite Create() => new(Id, "Messages and check codes", [Unchanged(), CheckCodes()]) {
        Changes = [SelfTestSetting.AdvancedMode],
        Planned = new Dictionary<SelfTestAction, int> { [SelfTestAction.ChannelMessage] = 6, [SelfTestAction.Invite] = 2, [SelfTestAction.Lookup] = 1, [SelfTestAction.PartnerMessage] = 16 },
    };

    private const SelfTestNeeds Needs = PairSteps.Needs | SelfTestNeeds.Typing;

    // ---------------------------------------------------------------- chat-links 16-17

    private static SelfTestCheck Unchanged() => new() {
        Id = "chat-links-16-17", Title = "Normal chat is unchanged", Steps = "chat-links 16-17", Mode = CheckMode.Automatic, Needs = Needs,
        StepList = [
            Auto("Lines both ways, with and without an item link", "16", async context => {
                var channel = context.Partner.ChannelId!;
                var mark = context.Host.Mark();
                await context.Partner.AskAsync(context, PartnerVerb.ReportReceived).ConfigureAwait(false);
                await SayInChannelAsync(context, channel).ConfigureAwait(false);
                await SayInChannelAsync(context, channel, TestLink.ItemInMiddle).ConfigureAwait(false);
                var (sent, arrived) = await PartnerSendsAsync(context, 1).ConfigureAwait(false);
                var (linkSent, linkArrived) = await PartnerSendsAsync(context, 1, link: "item").ConfigureAwait(false);
                var withLink = FromPartner(context, mark, channel).Any(line => line.Links.OfType<ChatLink.Item>().Any());
                var theirs = await context.Partner.AskAsync(context, PartnerVerb.ReportReceived).ConfigureAwait(false);
                return StepResult.All((sent + linkSent == 2 && arrived + linkArrived == 2, "your partner's lines didn't all arrive"), (withLink, "their item link didn't arrive as one"),
                    (theirs.Int("got") == 2, "your lines didn't all reach your partner"), (HasLink(theirs, "item"), "your item link didn't reach them as one"),
                    (NoWarnings(context, mark), "a warning showed for you"), (theirs.Int("warn") == 0, "a warning showed for your partner"));
            }, types: true),
            Auto("Your partner leaves and is invited back while both keep sending", "17", async context => {
                var channel = context.Partner.ChannelId!;
                var mark = context.Host.Mark();
                await context.Partner.AskAsync(context, PartnerVerb.ReportReceived).ConfigureAwait(false);
                if (!await PartnerLeavesAsync(context, channel).ConfigureAwait(false)) {
                    return StepResult.Fail("your partner didn't leave the test channel");
                }

                await context.Partner.InviteAsync(context, channel).ConfigureAwait(false);
                await SayInChannelAsync(context, channel).ConfigureAwait(false);
                var back = await context.Partner.WaitMemberAsync(context, channel, member: true).ConfigureAwait(false);
                await SayInChannelAsync(context, channel).ConfigureAwait(false);
                var (_, arrived) = await PartnerSendsAsync(context, 1).ConfigureAwait(false);
                var theirs = await context.Partner.AskAsync(context, PartnerVerb.ReportReceived).ConfigureAwait(false);
                return StepResult.All((back, "your partner didn't join again"), (arrived == 1, "your partner's line after joining again didn't arrive"),
                    ((theirs.Int("got") ?? 0) >= 1, "your line after they joined didn't reach them"),
                    (NoWarnings(context, mark), "a warning showed for you"), (theirs.Int("warn") == 0, "a warning showed for your partner"));
            }, types: true),
        ],
    };

    // ---------------------------------------------------------------- chat-links 19

    private static SelfTestCheck CheckCodes() => new() {
        Id = "chat-links-19", Title = "Check codes match", Steps = "chat-links 19", Mode = CheckMode.Automatic, Needs = PairSteps.Needs,
        StepList = [
            Auto("Both see the same check code", "19", async context => {
                var channel = context.Partner.ChannelId!;
                var (same, mine) = await SameCodeAsync(context, channel).ConfigureAwait(false);
                context.State["code"] = mine ?? "";
                return StepResult.All((mine != null, "you have no check code for the test channel"), (same, "your partner's check code is different"),
                    (PlainMessages.CheckCodeLabel.Technical == "Log head" && PlainMessages.CheckCodeLabel.Plain == "Check code", "the label isn't Log head in advanced mode"));
            }),
            Auto("After a change, both move on to the same new one", "19", async context => {
                var channel = context.Partner.ChannelId!;
                if (!await PartnerLeavesAsync(context, channel).ConfigureAwait(false) || !await context.Partner.InviteAndWaitAsync(context, channel).ConfigureAwait(false)) {
                    return StepResult.Fail("your partner didn't leave and join again");
                }

                var before = (string) context.State["code"];
                string? mine = null;
                var same = false;
                await context.WaitUntilAsync(async () => {
                    (same, mine) = await SameCodeAsync(context, channel).ConfigureAwait(false);
                    return same && mine != before;
                }, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                var members = (await context.Host.MembersAsync(channel).ConfigureAwait(false)).Count(member => !member.Invited);
                return StepResult.All((same, "after the change, your partner's check code is different"), (mine != before, "the check code didn't move on"),
                    (Seq(mine) > Seq(before), "the number at the start didn't go up"), (members == 2, "the member list isn't the two of you"));
            }),
            Rest("19", "hovering the check code (its words are checked with the settings)"),
        ],
    };

    /// <summary>Both check codes for a channel, as answer fields: the same? and the host's.</summary>
    private static async Task<(bool Same, string? Mine)> SameCodeAsync(StepContext context, string channel) {
        var mine = SelfTestProtocol.CheckCodeField(await context.Host.CheckCodeAsync(channel).ConfigureAwait(false));
        var theirs = await context.Partner.AskAsync(context, PartnerVerb.ReportCheckCode).ConfigureAwait(false);
        return (mine != null && theirs.Is("check", mine), mine);
    }

    private static long Seq(string? field) => field != null && long.TryParse(field.Split(':')[0], out var seq) ? seq : -1;
}

/// <summary>
/// "Party finder links, with a partner": chat-links-checklist.md 27 to 29. The host sends a link to its own recruitment
/// (by hand); the partner's plugin says it arrived as a party finder link, and its player clicks it.
/// </summary>
public static class PairPartyFinderSuite {
    public const string Id = "pair-party-finder";

    public static SelfTestSuite Create() => new(Id, "Party finder links, with a partner", [TheyOpenIt(), Ended(), OtherDataCentre()]) {
        Changes = [SelfTestSetting.WindowsOnly],
        Planned = new Dictionary<SelfTestAction, int> { [SelfTestAction.ChannelMessage] = 3, [SelfTestAction.PartnerMessage] = 12 },
        Note = "needs a recruitment of your own",
    };

    private const SelfTestNeeds Needs = PairSteps.Needs | SelfTestNeeds.Typing | SelfTestNeeds.ChatTwoOff;

    /// <summary>The host sends its listing to the test channel by hand: whether a party finder link went.</summary>
    private static SelfTestStep SendIt(string checklist) => SetupGuided("Send your recruitment's link", checklist, async context => {
        var channel = context.Partner.ChannelId!;
        await context.ChangeSettingAsync(SelfTestSetting.WindowsOnly, false).ConfigureAwait(false);
        var up = await context.AskAsync(new GuidedPrompt(
            "Start a recruitment of your own in the Party Finder (any duty; a password keeps strangers out), if you haven't, and keep it up. Is it up?", PromptKind.YesNo)).ConfigureAwait(false);
        if (up is not (GuidedAnswer.Yes or GuidedAnswer.Done)) {
            return StepResult.SkipCheck("needs a recruitment of your own");
        }

        await context.Partner.AskAsync(context, PartnerVerb.ReportReceived).ConfigureAwait(false);
        var command = await context.CommandOfAsync(channel).ConfigureAwait(false);
        var mark = context.Host.Mark();
        return await context.AskForResultAsync(new GuidedPrompt($"In the game's chat box type {command} join  (with a space after it), click your recruitment's chat button, then press Enter.") {
            Short = "sent", TesterTypes = true,
            Watch = StepContextExtensions.Until(() => Task.FromResult(context.Host.PrintedSince(mark).Any(line => line.Kind == TapKind.Message && line.Own && line.ChannelId == channel
                && line.Links.OfType<ChatLink.PartyFinder>().Any())), "no party finder link sent yet", "a party finder link was sent"),
        }).ConfigureAwait(false);
    });

    // ---------------------------------------------------------------- chat-links 27

    private static SelfTestCheck TheyOpenIt() => new() {
        Id = "chat-links-27", Title = "Your partner opens your listing", Steps = "chat-links 27", Mode = CheckMode.Guided, Needs = Needs,
        StepList = [
            SendIt("27"),
            Auto("It arrives as a party finder link", "27", async context => {
                await context.DelayAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                var got = await context.Partner.AskAsync(context, PartnerVerb.ReportReceived).ConfigureAwait(false);
                return StepResult.Expect(HasLink(got, "pf"), "it didn't reach your partner as a party finder link");
            }),
            Guided("They click it, in chat and in their window", "27", context => context.Partner.AskPlayerAsync(context, PartnerAsk.ClickLink, "they opened it")),
        ],
    };

    // ---------------------------------------------------------------- chat-links 28

    private static SelfTestCheck Ended() => new() {
        Id = "chat-links-28", Title = "A listing that has ended", Steps = "chat-links 28", Mode = CheckMode.Guided, Needs = Needs,
        StepList = [
            SendIt("28"),
            SetupAsk("End it", "28", new GuidedPrompt("End your recruitment now.") { Short = "ended" }),
            Guided("They click the old link: no error", "28", async context => {
                var before = await context.Partner.AskAsync(context, PartnerVerb.ReportLog).ConfigureAwait(false);
                var clicked = await context.Partner.AskPlayerAsync(context, PartnerAsk.ClickLink, "the game's own answer").ConfigureAwait(false);
                var after = await context.Partner.AskAsync(context, PartnerVerb.ReportLog).ConfigureAwait(false);
                return clicked.Outcome != StepOutcome.Passed ? clicked : StepResult.All((after.Int("errors") == before.Int("errors"), "your partner's plugin wrote an error"),
                    (after.Int("warnings") == before.Int("warnings"), "your partner's plugin wrote a warning"));
            }),
        ],
    };

    // ---------------------------------------------------------------- chat-links 29

    private static SelfTestCheck OtherDataCentre() => new() {
        Id = "chat-links-29", Title = "Another data centre", Steps = "chat-links 29", Mode = CheckMode.Guided, Needs = Needs,
        StepList = [
            SetupAuto("Your partner is on another data centre", "29", async context => {
                var mine = (await context.EnvironmentAsync().ConfigureAwait(false)).DataCentre;
                return context.Partner.Hello.TryGetValue("dc", out var theirs) && theirs != "0" && mine != 0 && theirs != mine.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    ? StepResult.Pass()
                    : StepResult.SkipCheck(SelfTestWords.NeedsOtherDataCentre);
            }),
            SendIt("29"),
            Guided("They click it: the game's own answer, no error", "29", async context => {
                var before = await context.Partner.AskAsync(context, PartnerVerb.ReportLog).ConfigureAwait(false);
                var clicked = await context.Partner.AskPlayerAsync(context, PartnerAsk.ClickLink, "another data centre").ConfigureAwait(false);
                var after = await context.Partner.AskAsync(context, PartnerVerb.ReportLog).ConfigureAwait(false);
                return clicked.Outcome != StepOutcome.Passed ? clicked : StepResult.All((after.Int("errors") == before.Int("errors"), "your partner's plugin wrote an error"),
                    (after.Int("warnings") == before.Int("warnings"), "your partner's plugin wrote a warning"));
            }),
        ],
    };
}
