using static LookingGlass.Core.SelfTest.SelfTestSteps;
using static LookingGlass.Core.SelfTest.Suites.PairSteps;

namespace LookingGlass.Core.SelfTest.Suites;

/// <summary>
/// "Chat history, with a partner": chat-log-checklist.md 2, 4, 6, 7, 10, 11, 19 and 29. As the one-player history suite,
/// every check here runs only when the host's chat history was off and empty before the run (it records every channel then,
/// and the clean-up deletes it whole); the partner's side never touches its own history.
/// </summary>
public static class PairHistorySuite {
    public const string Id = "pair-history";

    public static SelfTestSuite Create() => new(Id, "Chat history, with a partner", [OffByDefault(), KeptAndShown(), Missed(), Blocking(), TextFiles()]) {
        Changes = [SelfTestSetting.KeepHistory, SelfTestSetting.KeepHistoryUnencrypted, SelfTestSetting.ChannelWindows],
        Planned = new Dictionary<SelfTestAction, int> {
            [SelfTestAction.ChannelMessage] = 4, [SelfTestAction.Rename] = 2, [SelfTestAction.Reconnect] = 2, [SelfTestAction.Invite] = 2, [SelfTestAction.Lookup] = 1,
            [SelfTestAction.PartnerMessage] = 24,
        },
        Note = "2 relogs; only if your chat history is off and empty",
    };

    private const SelfTestNeeds Needs = PairSteps.Needs | SelfTestNeeds.HistoryOffAndEmpty;

    /// <summary>A window showing the test channel (opened if need be): how many lines it shows.</summary>
    private static async Task<int> WindowLinesAsync(StepContext context, string channel) {
        if (await context.WindowOfAsync(channel, TimeSpan.Zero).ConfigureAwait(false) == null) {
            await context.Host.OpenWindowAsync(channel).ConfigureAwait(false);
        }

        return (await context.WindowOfAsync(channel, WindowOpens).ConfigureAwait(false))?.Lines ?? -1;
    }

    // ---------------------------------------------------------------- chat-log 2

    private static SelfTestCheck OffByDefault() => new() {
        Id = "chat-log-2", Title = "Off by default", Steps = "chat-log 2", Mode = CheckMode.Automatic, Needs = Needs,
        StepList = [
            Auto("Lines with your partner keep nothing", "2", async context => {
                await context.ChangeSettingAsync(SelfTestSetting.KeepHistory, false).ConfigureAwait(false);
                await SayInChannelAsync(context, context.Partner.ChannelId!).ConfigureAwait(false);
                await PartnerSendsAsync(context, 1).ConfigureAwait(false);
                await context.DelayAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                var history = await context.Host.HistoryAsync().ConfigureAwait(false);
                var environment = await context.EnvironmentAsync().ConfigureAwait(false);
                return StepResult.All((!history.On, "the chat history was on"), (environment.HistoryEmpty, "a chat history folder appeared"));
            }, types: true),
        ],
    };

    // ---------------------------------------------------------------- chat-log 4, 6-7

    private static SelfTestCheck KeptAndShown() => new() {
        Id = "chat-log-4-7", Title = "Your partner's lines are kept and shown later", Steps = "chat-log 4, 6-7 (B's parts)", Mode = CheckMode.Guided, Needs = Needs,
        StepList = [
            Auto("Lines both ways, a leave and rejoin, a rename", "4", async context => {
                await HistorySuite.HistoryOnAsync(context, unencrypted: false).ConfigureAwait(false);
                var channel = context.Partner.ChannelId!;
                await SayInChannelAsync(context, channel).ConfigureAwait(false);
                var (_, arrived) = await PartnerSendsAsync(context, 2, link: "map").ConfigureAwait(false);
                var rejoined = await PartnerLeavesAsync(context, channel).ConfigureAwait(false) && await context.Partner.InviteAndWaitAsync(context, channel).ConfigureAwait(false);
                await context.ServerAsync(SelfTestAction.Rename, () => context.Host.RenameChannelAsync(channel, SelfTestRun.ChannelName(context.Code, 0) + " r")).ConfigureAwait(false);
                // And back: the partner's plugin knows the host's channels by their names (an invite back is accepted only to them).
                await context.ServerAsync(SelfTestAction.Rename, () => context.Host.RenameChannelAsync(channel, SelfTestRun.ChannelName(context.Code, 0))).ConfigureAwait(false);
                var history = await context.Host.HistoryAsync().ConfigureAwait(false);
                return StepResult.All((arrived == 2, "your partner's lines didn't arrive"), (rejoined, "your partner didn't leave and join again"), (history.Size > 0, "nothing was kept"));
            }, types: true),
            CarryOn(Relog, "6"),
            Guided("Show older messages", "6-7", async context => {
                await WindowLinesAsync(context, context.Partner.ChannelId!).ConfigureAwait(false);
                return await context.AskForResultAsync(new GuidedPrompt(
                    "In the test channel's window, click Show older messages: are your partner's lines there, under an \"Earlier\" line, the map link a link, and their leave and " +
                    "rejoin and the rename as dimmed lines, with no warnings?", PromptKind.YesNo) { Short = "older lines" }).ConfigureAwait(false);
            }),
        ],
    };

    // ---------------------------------------------------------------- chat-log 10-11

    private static SelfTestCheck Missed() => new() {
        Id = "chat-log-10-11", Title = "Missed messages, and channels kept apart", Steps = "chat-log 10-11", Mode = CheckMode.AutomaticAndGuided, Needs = Needs,
        StepList = [
            Auto("Disconnect; your partner sends two; Connect: once in the window", "10", async context => {
                await HistorySuite.HistoryOnAsync(context, unencrypted: false).ConfigureAwait(false);
                var channel = context.Partner.ChannelId!;
                var before = await WindowLinesAsync(context, channel).ConfigureAwait(false);
                var mark = context.Host.Mark();
                var number = await context.Partner.SendAsync(context, PartnerVerb.SendWhileAway, "2").ConfigureAwait(false);
                await context.DisconnectAsync().ConfigureAwait(false);
                await context.Host.Delay(TimeSpan.FromSeconds(20), context.Token).ConfigureAwait(false);
                await context.ConnectAsync().ConfigureAwait(false);
                var answer = await context.Partner.AnswerOfAsync(context, mark, number, PartnerVerb.SendWhileAway).ConfigureAwait(false);
                var sent = answer.Int("sent") ?? 0;
                await context.WaitUntilAsync(() => Task.FromResult(FromPartner(context, mark, channel).Count(line => line.CaughtUp) >= sent), Soon).ConfigureAwait(false);
                var caught = FromPartner(context, mark, channel).Count(line => line.CaughtUp);
                var after = (await context.WindowOfAsync(channel, WindowOpens).ConfigureAwait(false))?.Lines ?? -1;
                return sent == 0
                    ? StepResult.Skip("your partner didn't see you go offline, so nothing was sent while you were away")
                    : StepResult.All((caught == sent, "the missed lines weren't caught up, once each"), (after >= before + sent && after <= before + sent + 2, "the window didn't show them once"));
            }),
            Auto("Each test channel's window only its own lines", "11", async context => {
                var other = await ChannelWithPartnerAsync(context, 1).ConfigureAwait(false);
                var lines = await WindowLinesAsync(context, other).ConfigureAwait(false);
                var counts = await context.Host.WindowsAsync().ConfigureAwait(false);
                return StepResult.All((lines <= 3, "the other test channel's window showed the first one's lines"),
                    (counts.ListCounts.GetValueOrDefault(other) == 0, "an unread count counted older lines"));
            }),
            CarryOn(Relog, "10"),
            Ask("Not again among the older ones", "10", new GuidedPrompt(
                "In the test channel's window, click Show older messages: are your partner's two missed lines there only once (not again among the older ones)?", PromptKind.YesNo) {
                Short = "once",
            }),
        ],
    };

    // ---------------------------------------------------------------- chat-log 19

    private static SelfTestCheck Blocking() => new() {
        Id = "chat-log-19", Title = "Blocking hides old lines", Steps = "chat-log 19", Mode = CheckMode.Automatic, Needs = Needs,
        StepList = [
            Auto("Blocked: their older lines go from the window; unblocked: back", "19", async context => {
                if (context.Data.OtherWasBlocked) {
                    return StepResult.Skip(SelfTestWords.PartnerWasBlocked);
                }

                await HistorySuite.HistoryOnAsync(context, unencrypted: false).ConfigureAwait(false);
                var channel = context.Partner.ChannelId!;
                await PartnerSendsAsync(context, 1).ConfigureAwait(false);
                var before = await WindowLinesAsync(context, channel).ConfigureAwait(false);
                await context.BlockPartnerAsync(true).ConfigureAwait(false);
                var hidden = await context.WaitUntilAsync(async () => (await context.WindowOfAsync(channel, TimeSpan.Zero).ConfigureAwait(false))?.Lines < before).ConfigureAwait(false);
                await context.BlockPartnerAsync(false).ConfigureAwait(false);
                var back = await context.WaitUntilAsync(async () => (await context.WindowOfAsync(channel, TimeSpan.Zero).ConfigureAwait(false))?.Lines == before).ConfigureAwait(false);
                return StepResult.All((hidden, "their lines stayed while they were blocked"), (back, "their lines didn't come back once unblocked"));
            }),
        ],
    };

    // ---------------------------------------------------------------- chat-log 29

    private static SelfTestCheck TextFiles() => new() {
        Id = "chat-log-29", Title = "Text files: your partner's joins and missed messages", Steps = "chat-log 29", Mode = CheckMode.Automatic, Needs = Needs,
        StepList = [
            Auto("Their leave and rejoin, in everyday words", "29", async context => {
                await HistorySuite.HistoryOnAsync(context, unencrypted: true).ConfigureAwait(false);
                var channel = context.Partner.ChannelId!;
                await SayInChannelAsync(context, channel).ConfigureAwait(false);
                var before = (await HistorySuite.WaitForLinesAsync(context, channel, found => found.Length > 0).ConfigureAwait(false)).Length;
                if (!await PartnerLeavesAsync(context, channel).ConfigureAwait(false) || !await context.Partner.InviteAndWaitAsync(context, channel).ConfigureAwait(false)) {
                    return StepResult.Fail("your partner didn't leave and join again");
                }

                var lines = await HistorySuite.WaitForLinesAsync(context, channel, found => found.Length >= before + 2).ConfigureAwait(false);
                var added = lines.Skip(before).ToList();
                return StepResult.All((added.Count >= 2, "the leave and rejoin aren't in the text file"),
                    (added.All(line => SelfTestWords.JargonIn(line).Count == 0), "the leave or rejoin line has a technical word"));
            }, types: true),
            Auto("Disconnect, two lines, Connect: the missed lines' format", "29", async context => {
                var channel = context.Partner.ChannelId!;
                var before = (await HistorySuite.WaitForLinesAsync(context, channel, found => found.Length > 0).ConfigureAwait(false)).Length;
                var mark = context.Host.Mark();
                var number = await context.Partner.SendAsync(context, PartnerVerb.SendWhileAway, "2").ConfigureAwait(false);
                await context.DisconnectAsync().ConfigureAwait(false);
                await context.Host.Delay(TimeSpan.FromSeconds(20), context.Token).ConfigureAwait(false);
                await context.ConnectAsync().ConfigureAwait(false);
                var sent = (await context.Partner.AnswerOfAsync(context, mark, number, PartnerVerb.SendWhileAway).ConfigureAwait(false)).Int("sent") ?? 0;
                if (sent == 0) {
                    return StepResult.Skip("your partner didn't see you go offline, so nothing was sent while you were away");
                }

                var lines = await HistorySuite.WaitForLinesAsync(context, channel, found => found.Length >= before + 1 + sent).ConfigureAwait(false);
                var added = lines.Skip(before).ToList();
                var arrived = added.Count(line => HistorySuite.LineFormat().IsMatch(line) && line.Contains(", arrived ", StringComparison.Ordinal));
                return StepResult.All((added.Any(line => line.Contains("while you were away", StringComparison.Ordinal)), "no line said messages were sent while you were away"),
                    (arrived == sent, "the missed lines don't say when they were sent and arrived"),
                    (added.All(line => !line.Contains("Not sent", StringComparison.Ordinal)), "a Not sent line is in the file"));
            }),
        ],
    };
}
