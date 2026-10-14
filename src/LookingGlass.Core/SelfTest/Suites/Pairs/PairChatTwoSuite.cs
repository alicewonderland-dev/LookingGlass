using static LookingGlass.Core.SelfTest.SelfTestSteps;
using static LookingGlass.Core.SelfTest.Suites.PairSteps;

namespace LookingGlass.Core.SelfTest.Suites;

/// <summary>
/// "Talking in a channel with ChatTwo, with a partner": sticky-channel-checklist.md 30, 30b and 32b. The partner is the
/// whole party, next to the host; the tester types each line in ChatTwo as told (behind the typing gate, and, while ChatTwo
/// is on Party, only while the party is exactly the two of them: checked before the request shows and at every poll); the
/// partner's plugin says where each landed: Party, Say, or LookingGlass. After each step, ChatTwo's input is asked back
/// on Say, whatever the step's result.
/// </summary>
public static class PairChatTwoSuite {
    public const string Id = "pair-chattwo";

    public static SelfTestSuite Create() => new(Id, "Talking in a channel with ChatTwo, with a partner", [ShortCommands()]) {
        Planned = new Dictionary<SelfTestAction, int> { [SelfTestAction.ChannelMessage] = 4, [SelfTestAction.PartnerMessage] = 10 },
        Note = "your partner in your party",
    };

    /// <summary>
    /// The tester puts ChatTwo on Party and types a line in it (the gate first, and the party of two held throughout); where
    /// the partner saw it, and whether the host still talks in the channel.
    /// </summary>
    private static async Task<(PartnerAnswer? Saw, bool Still, StepResult? Answer)> TypedInChatTwoAsync(StepContext context, string line, string marker) {
        var channel = context.Partner.ChannelId!;
        var started = context.Host.Now;
        var answer = await context.AskAsync(new GuidedPrompt($"Put ChatTwo's input on Party (just the two of you in it), then type in it: {line}") {
            TesterTypes = true,
            Holds = () => PartyOfTwoAsync(context),
            HoldsWhy = SelfTestWords.WaitingPartyOfTwo,
        }).ConfigureAwait(false);
        if (answer is not (GuidedAnswer.Done or GuidedAnswer.Seen)) {
            return (null, false, StepContext.Judge(answer, null));
        }

        var saw = await PartnerSawAsync(context, marker).ConfigureAwait(false);
        var still = (await context.Host.StickyAsync().ConfigureAwait(false)).ChannelId == channel;
        context.State[$"took-{marker}"] = (int) (context.Host.Now - started).TotalSeconds;
        return (saw, still, null);
    }

    private static SelfTestCheck ShortCommands() => new() {
        Id = "sticky-channel-30-32b", Title = "Short commands while ChatTwo is on Party", Steps = "sticky-channel 30, 30b, 32b", Mode = CheckMode.Guided,
        Needs = PairSteps.Needs | SelfTestNeeds.ChatTwo | SelfTestNeeds.Typing,
        StepList = [
            Together(friends: false),
            PartyOfTwo("30"),
            SetupAuto("Talking in the test channel", "30", async context => {
                var channel = context.Partner.ChannelId!;
                await context.StopTalkingAsync().ConfigureAwait(false);
                await context.TypeAsync(await context.CommandOfAsync(channel).ConfigureAwait(false)).ConfigureAwait(false);
                return StepResult.Expect(await context.TalkingInAsync(channel).ConfigureAwait(false), "talking in the test channel didn't start");
            }, types: true),
            ChatTwoSuite.ThenBackToSay("/p goes to Party once", "30", async context => {
                var marker = context.NextMarker();
                var (saw, still, answer) = await TypedInChatTwoAsync(context, $"/p {marker}", marker).ConfigureAwait(false);
                return answer ?? StepResult.All((saw!.Is("in", "party"), "your partner didn't see it in Party"), (still, "you stopped talking in the test channel"));
            }),
            ChatTwoSuite.ThenBackToSay("A plain line: only LookingGlass", "30", async context => {
                var marker = context.NextMarker();
                var (saw, still, answer) = await TypedInChatTwoAsync(context, marker, marker).ConfigureAwait(false);
                return answer ?? StepResult.All((saw!.Is("in", "lookingglass"), "it didn't reach your partner only through LookingGlass"), (still, "you stopped talking in the test channel"));
            }),
            ChatTwoSuite.ThenBackToSay("Three spaces after it: still only LookingGlass", "30b", async context => {
                var marker = context.NextMarker();
                var (saw, still, answer) = await TypedInChatTwoAsync(context, $"{marker}    (with three spaces after it)", marker).ConfigureAwait(false);
                var took = context.State.TryGetValue($"took-{marker}", out var seconds) ? (int) seconds : 0;
                return answer ?? StepResult.All((saw!.Is("in", "lookingglass"), "it didn't reach your partner only through LookingGlass"), (still, "you stopped talking in the test channel"))
                    with { Did = $"30b typed within {took} s (for /xllog's lines)" };
            }),
            ChatTwoSuite.ThenBackToSay("/s with a space after it: Say once", "32b", async context => {
                var marker = context.NextMarker();
                var (saw, still, answer) = await TypedInChatTwoAsync(context, $"/s {marker}   (with a space after it)", marker).ConfigureAwait(false);
                return answer ?? StepResult.All((saw!.Is("in", "say"), "your partner didn't see it in Say"), (saw.Is("printed", "no"), "it reached the test channel too"),
                    (still, "you stopped talking in the test channel"));
            }),
            SetupAuto("Back to Say", "32b", async context => {
                await context.StopTalkingAsync().ConfigureAwait(false);
                await context.Partner.AskPlayerAsync(context, PartnerAsk.LeaveParty).ConfigureAwait(false);
                return StepResult.Pass("/xllog's lines for 30b (the owner reads them by the time above)");
            }),
        ],
    };
}
