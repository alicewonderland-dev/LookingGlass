using static LookingGlass.Core.SelfTest.SelfTestSteps;

namespace LookingGlass.Core.SelfTest.Suites;

/// <summary>
/// Pairing, the host's first check in a two-player run (docs/self-test-plan.md, "Pairing"): one lookup, the test channel,
/// one invite, and the partner's hello within two minutes; then where the partner is, for the window. Every check after it
/// that needs the partner is skipped, with why, if it didn't pair.
/// </summary>
public static class PairingSuite {
    public const string Id = "pairing";

    public static SelfTestSuite Create() => new(Id, "Pairing with your partner", [Pairing()]) {
        Planned = new Dictionary<SelfTestAction, int> {
            [SelfTestAction.Lookup] = 2, [SelfTestAction.Invite] = 1, [SelfTestAction.CreateChannel] = 1,
            // Its hello, and the "alive" lines a run may send while its tester is busy.
            [SelfTestAction.PartnerMessage] = 3 + SelfTestProtocol.MostKeepAlives,
        },
    };

    private static SelfTestCheck Pairing() => new() {
        Id = "pairing", Title = "Your partner joins", Steps = "two players: pairing", Mode = CheckMode.Automatic,
        Needs = SelfTestNeeds.Connection | SelfTestNeeds.TestChannel,
        StepList = [
            Auto("Invite your partner, and wait for them to join", null, async context => {
                var why = await context.Partner.PairAsync(context, left => context.ShowPairLine(SelfTestWords.WaitingForPartner(left))).ConfigureAwait(false);
                if (why != null) {
                    context.ShowPairLine(SelfTestWords.PartnerFacts(false, null, null, null, null, null));
                    return StepResult.Skip(why);
                }

                return StepResult.Pass();
            }, timeout: TimeSpan.FromMinutes(5)),
            SetupAuto("Where your partner is", null, async context => {
                if (!context.Partner.Paired) {
                    return StepResult.Skip(context.Partner.Lacks ?? SelfTestWords.PartnerDidntJoin);
                }

                await ShowFactsAsync(context).ConfigureAwait(false);
                return StepResult.Pass();
            }),
        ],
    };

    /// <summary>The partner's facts for the window: their hello, how far they are, friends, party.</summary>
    public static async Task ShowFactsAsync(StepContext context) {
        var hello = context.Partner.Hello;
        var (distance, friend) = await context.Host.OtherAsync().ConfigureAwait(false);
        var (_, inParty) = await context.Host.PartyAsync().ConfigureAwait(false);
        context.ShowPairLine(SelfTestWords.PartnerFacts(true, hello.TryGetValue("private", out var place) ? place == "yes" : null, distance, friend, inParty,
            hello.TryGetValue("chattwo", out var chatTwo) ? chatTwo == "on" : null));
    }
}

/// <summary>
/// Being a partner (docs/self-test-plan.md, "Two players: the partner"): one check, whose steps wait for the host's
/// invite and then answer the host's requests until "bye". Its report says what it was asked and what it did.
/// </summary>
public static class PartnerSuite {
    public const string Id = "partner";

    public static SelfTestSuite Create() => new(Id, "Being a partner", [Partner()]) {
        Changes = [SelfTestSetting.LocalPrivacyAccepted],
        Planned = new Dictionary<SelfTestAction, int> {
            [SelfTestAction.ChannelMessage] = 80, [SelfTestAction.PartnerMessage] = 150, [SelfTestAction.LocalMessage] = 20, [SelfTestAction.Reconnect] = 3,
            [SelfTestAction.CreateChannel] = 1, [SelfTestAction.Lookup] = 1, [SelfTestAction.Invite] = SelfTestProtocol.MostInvitesToHost,
        },
    };

    private static PartnerSide Side(StepContext context) {
        if (!context.State.TryGetValue("side", out var side)) {
            side = new PartnerSide(context);
            context.State["side"] = side;
        }

        ((PartnerSide) side).Use(context);
        return (PartnerSide) side;
    }

    private static SelfTestCheck Partner() => new() {
        Id = "partner", Title = "Answering your partner's test", Steps = "two players: partner", Mode = CheckMode.Automatic, Needs = SelfTestNeeds.Connection,
        StepList = [
            Auto("Wait for their invite, and join", null, context => Side(context).JoinAsync(), timeout: PartnerSide.InviteWithin + TimeSpan.FromMinutes(5)),
            Auto("Answer their requests", null, context => Side(context).AnswerAllAsync(), timeout: TimeSpan.FromHours(8)),
        ],
    };
}
