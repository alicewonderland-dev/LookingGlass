using LookingGlass.Core.Client;
using static LookingGlass.Core.SelfTest.SelfTestSteps;
using static LookingGlass.Core.SelfTest.Suites.PairSteps;

namespace LookingGlass.Core.SelfTest.Suites;

/// <summary>
/// "Local chat together" (two players, both inside the same house or apartment, friends): local-chat-checklist.md 3d, 4 to
/// 11, 14 (first part), 15 to 23, 25 (players) and 47. The host types its /lgl lines; the partner's plugin sends its own
/// when asked, and says whether the host's arrived and where. What only eyes can judge (a colour, the line's look) is
/// asked.
/// </summary>
public static class LocalTogetherSuite {
    public const string Id = "pair-local";

    public static SelfTestSuite Create() => new(Id, "Local chat together", [
        BeforeAccepting(), FriendsNotLoaded(), LongFriendsList(), RepliesAndRange(), NoLookingGlass(), Blocking(), PlainWords(), Colours(), TheLog(), TalkingToAFriend(),
        PartyWhileTalking(),
    ]) {
        Changes = [SelfTestSetting.LocalPrivacyAccepted, SelfTestSetting.LocalChatColour, SelfTestSetting.PartnerNameColour, SelfTestSetting.WindowsOnly, SelfTestSetting.AdvancedMode],
        Planned = new Dictionary<SelfTestAction, int> { [SelfTestAction.LocalMessage] = 18, [SelfTestAction.PartnerMessage] = 50 },
        Note = "both inside the same house or apartment, friends; 2 relogs",
    };

    private const SelfTestNeeds Needs = PairSteps.Needs | SelfTestNeeds.Typing | SelfTestNeeds.LocalChat;

    private static Wording NotSentNobodyNear => LocalChatWords.Refusal(LocalChatWords.NobodyNear, null);

    /// <summary>The partner saw the host's local line (its plugin printed it, as local chat).</summary>
    private static bool Arrived(PartnerAnswer saw) => saw.Is("printed", "yes") && !saw.Is("in", "say");

    // ---------------------------------------------------------------- local-chat 3d

    private static SelfTestCheck BeforeAccepting() => new() {
        Id = "local-chat-3d", Title = "Receiving before accepting", Steps = "local-chat 3d", Mode = CheckMode.Automatic, Needs = Needs,
        StepList = [
            Together(),
            SetupAuto("Your partner hasn't accepted local chat", "3d", async context => {
                await context.Partner.AskAsync(context, PartnerVerb.WithdrawLocal).ConfigureAwait(false);
                return StepResult.Pass();
            }),
            Auto("Your local line reaches them all the same", "3d", async context => {
                var marker = await SayLocalAsync(context).ConfigureAwait(false);
                return StepResult.Expect(Arrived(await PartnerSawAsync(context, marker).ConfigureAwait(false)), "your partner didn't get your local line before accepting");
            }, types: true),
            SetupAuto("Their acceptance back", "3d", async context => {
                await context.Partner.AskAsync(context, PartnerVerb.RestoreLocal).ConfigureAwait(false);
                return StepResult.Pass();
            }),
        ],
    };

    // ---------------------------------------------------------------- local-chat 4-7

    private static SelfTestCheck FriendsNotLoaded() => new() {
        Id = "local-chat-4-7", Title = "Friends list not loaded yet", Steps = "local-chat 4-7", Mode = CheckMode.Guided, Needs = Needs,
        StepList = [
            Together(),
            CarryOn("Go to the title screen, then come back in with this character. Don't open your friends list afterwards.", "4"),
            SetupGuided("Your partner relogs too", "4", context => context.Partner.AskPlayerAsync(context, PartnerAsk.Relog, "partner relogged")),
            Auto("Before either friends list is open", "4", async context => {
                var mark = context.Host.Mark();
                var marker = await SayLocalAsync(context).ConfigureAwait(false);
                var openList = await ToldAsync(context, mark, LocalChatWords.Refusal(LocalChatWords.OpenFriendsList, null)).ConfigureAwait(false);
                var saw = await PartnerSawAsync(context, marker).ConfigureAwait(false);
                context.State["reached"] = Arrived(saw);
                // Either is right (the checklist asks which): it is recorded.
                return Arrived(saw)
                    ? StepResult.Pass() with { Did = "before the friends lists were open, your local line reached your partner" }
                    : StepResult.Expect(openList, "your line didn't reach your partner, and you weren't told to open your friends list") with {
                        Did = "before the friends lists were open, you were told to open yours",
                    };
            }, types: true),
            Guided("Their one blue line", "5", async context => context.State.TryGetValue("reached", out var reached) && (bool) reached
                ? StepResult.Pass("the partner's blue line (5): your line reached them")
                : await context.AskForResultAsync(new GuidedPrompt(
                    "Ask your partner: if your line was sent after all, did they get one blue line (once only) saying a player near sent a local message, and to open the friends list?",
                    PromptKind.YesNo) { Short = "their blue line" }).ConfigureAwait(false)),
            Guided("Both open the friends list", "6", async context => {
                var mine = await context.AskForResultAsync(new GuidedPrompt("Open your friends list (Social, Friend List), then close it again.") { Short = "your list" })
                    .ConfigureAwait(false);
                return mine.Outcome != StepOutcome.Passed ? mine : await context.Partner.AskPlayerAsync(context, PartnerAsk.OpenFriends, "their list").ConfigureAwait(false);
            }),
            Auto("Now it reaches them", "7", async context => {
                var mark = context.Host.Mark();
                var marker = await SayLocalAsync(context).ConfigureAwait(false);
                var own = await context.PrintedAsync(mark, line => line.Kind == TapKind.LocalMessage && line.Own && line.Text.Contains(marker, StringComparison.Ordinal)).ConfigureAwait(false);
                var saw = await PartnerSawAsync(context, marker).ConfigureAwait(false);
                return StepResult.All((Arrived(saw), "your partner didn't get your local line"), (saw.Is("tag", "Local"), "your partner's line didn't have the [Local] tag"),
                    (own?.Tag == LocalChat.Tag, "your own line wasn't shown with [Local]"));
            }, types: true),
            Ask("How it looks", "7", new GuidedPrompt(
                "In game chat, is your line [Local] <your name> followed by it, with [Local] in LookingGlass blue? Ask your partner whether theirs looks the same.", PromptKind.YesNo) {
                Short = "[Local] lines",
            }),
        ],
    };

    // ---------------------------------------------------------------- local-chat 7a

    private static SelfTestCheck LongFriendsList() => new() {
        Id = "local-chat-7a", Title = "A long friends list", Steps = "local-chat 7a", Mode = CheckMode.Guided, Needs = Needs,
        StepList = [
            Together(),
            SetupGuided("Only with a long list", "7a", async context => await context.AskAsync(new GuidedPrompt(
                    "Does your friends list have more than one page, with your partner near its end?", PromptKind.YesNo)).ConfigureAwait(false) is GuidedAnswer.Yes
                ? StepResult.Pass()
                : StepResult.SkipCheck("needs a friends list longer than one page, with your partner near its end")),
            CarryOn("Go to the title screen, then come back in with this character. Then open your friends list and close it at once, before it scrolls.", "7a"),
            Auto("Right after opening it", "7a", async context => {
                var marker = await SayLocalAsync(context).ConfigureAwait(false);
                var reached = Arrived(await PartnerSawAsync(context, marker).ConfigureAwait(false));
                return StepResult.Pass() with { Did = reached ? "the friend near the list's end got it at once" : "the friend near the list's end didn't get it at once" };
            }, types: true),
            SetupAsk("Scroll through the list", "7a", new GuidedPrompt("Open your friends list again and scroll through all of it, then close it.") { Short = "scrolled" }),
            Auto("After scrolling", "7a", async context => {
                var marker = await SayLocalAsync(context).ConfigureAwait(false);
                return StepResult.Expect(Arrived(await PartnerSawAsync(context, marker).ConfigureAwait(false)), "after scrolling the whole list, your partner still didn't get it");
            }, types: true),
        ],
    };

    // ---------------------------------------------------------------- local-chat 8-11

    private static SelfTestCheck RepliesAndRange() => new() {
        Id = "local-chat-8-11", Title = "Replies, links and range", Steps = "local-chat 8-11", Mode = CheckMode.AutomaticAndGuided, Needs = Needs,
        StepList = [
            Together(),
            Auto("Your partner replies", "8", async context => {
                var (sent, arrived) = await PartnerLocalAsync(context, 1).ConfigureAwait(false);
                return StepResult.Expect(sent == 1 && arrived == 1, "your partner's local line didn't show for you");
            }),
            SetupAsk("Target your partner", "8", new GuidedPrompt("Target your partner (click their character).") { Short = "targeted" }),
            Auto("An item link and <t>", "8", async context => {
                await context.Partner.AskAsync(context, PartnerVerb.ReportReceived).ConfigureAwait(false);
                await SayLocalAsync(context, TestLink.ItemInMiddle).ConfigureAwait(false);
                await SayLocalAsync(context, insert: " <t>").ConfigureAwait(false);
                await context.DelayAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                var got = await context.Partner.AskAsync(context, PartnerVerb.ReportReceived).ConfigureAwait(false);
                return StepResult.All((got.Int("got") == 2, "your partner didn't get both lines"), (HasLink(got, "item"), "the item didn't arrive as an item link"),
                    ((got.Int("you") ?? 0) >= 1, "<t> didn't arrive as your partner's name"));
            }, types: true),
            Guided("Out of range", "9", async context => {
                var walked = await context.Partner.AskPlayerAsync(context, PartnerAsk.WalkAway, "walked away").ConfigureAwait(false);
                if (walked.Outcome != StepOutcome.Passed) {
                    return walked;
                }

                var (distance, _) = await context.Host.OtherAsync().ConfigureAwait(false);
                var mark = context.Host.Mark();
                var marker = await SayLocalAsync(context).ConfigureAwait(false);
                var refused = await ToldAsync(context, mark, NotSentNobodyNear).ConfigureAwait(false);
                var saw = await PartnerSawAsync(context, marker).ConfigureAwait(false);
                await context.Partner.AskPlayerAsync(context, PartnerAsk.ComeBack).ConfigureAwait(false);
                return distance is < 20
                    ? StepResult.Skip("your partner was still within 20 yalms")
                    : StepResult.All((refused, "you weren't told nobody is near enough"), (!Arrived(saw), "your partner got it from out of range")) with {
                        Did = distance is { } yalms ? $"out of range at {(int) Math.Round(yalms)} yalms" : "out of sight",
                    };
            }),
            Rest("10-11", "the exact distances (10), and another zone (11): your partner would have to leave the house"),
        ],
    };

    // ---------------------------------------------------------------- local-chat 14

    private static SelfTestCheck NoLookingGlass() => new() {
        Id = "local-chat-14", Title = "A friend without LookingGlass", Steps = "local-chat 14 (first part)", Mode = CheckMode.Automatic, Needs = Needs,
        StepList = [
            Together(),
            Auto("Your partner disconnects LookingGlass: you're told", "14", async context => {
                var mark = context.Host.Mark();
                var number = await context.Partner.SendAsync(context, PartnerVerb.Disconnect, "60").ConfigureAwait(false);
                var offline = await context.WaitUntilAsync(async () => (await context.Host.MembersAsync(context.Partner.ChannelId!).ConfigureAwait(false))
                    .Any(member => member.UserId == context.Partner.UserId && !member.Online), TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                if (!offline) {
                    await context.Partner.AnswerOfAsync(context, mark, number, PartnerVerb.Disconnect).ConfigureAwait(false);
                    return StepResult.Skip("your partner didn't show as disconnected");
                }

                var told = context.Host.Mark();
                await SayLocalAsync(context).ConfigureAwait(false);
                var nobody = await ToldAsync(context, told, LocalChatWords.Refusal(LocalChatWords.NobodyUsesIt, null)).ConfigureAwait(false);
                // Back: their answer comes once they're connected again.
                await context.Partner.AnswerOfAsync(context, mark, number, PartnerVerb.Disconnect).ConfigureAwait(false);
                return StepResult.Expect(nobody, "you weren't told none of your friends near you uses LookingGlass");
            }, types: true, timeout: TimeSpan.FromMinutes(5)),
            Rest("14", "with a friend without LookingGlass and your partner both near (three players)"),
        ],
    };

    // ---------------------------------------------------------------- local-chat 15-16

    private static SelfTestCheck Blocking() => new() {
        Id = "local-chat-15-16", Title = "Blocking", Steps = "local-chat 15-16", Mode = CheckMode.Automatic, Needs = Needs,
        CantRun = null,
        StepList = [
            Together(),
            Auto("Blocked: their line doesn't show", "15", async context => {
                if (context.Data.OtherWasBlocked) {
                    return StepResult.SkipCheck(SelfTestWords.PartnerWasBlocked);
                }

                await context.BlockPartnerAsync(true).ConfigureAwait(false);
                var (sent, arrived) = await PartnerLocalAsync(context, 1).ConfigureAwait(false);
                return StepResult.Expect(sent == 0 || arrived == 0, "your partner's line showed while they were blocked");
            }),
            Auto("Blocked: yours doesn't reach them", "16", async context => {
                var marker = await SayLocalAsync(context).ConfigureAwait(false);
                return StepResult.Expect(!Arrived(await PartnerSawAsync(context, marker).ConfigureAwait(false)), "your partner got your line while blocked");
            }, types: true),
            Auto("Unblocked: their line shows again", "15", async context => {
                await context.BlockPartnerAsync(false).ConfigureAwait(false);
                var (_, arrived) = await PartnerLocalAsync(context, 1).ConfigureAwait(false);
                return StepResult.Expect(arrived == 1, "your partner's line didn't show after unblocking");
            }),
        ],
    };

    // ---------------------------------------------------------------- local-chat 17-18

    private static SelfTestCheck PlainWords() => new() {
        Id = "local-chat-17-18", Title = "Plain words", Steps = "local-chat 17-18", Mode = CheckMode.Automatic, Needs = Needs,
        StepList = [
            Together(),
            Auto("Simple mode: every line on both sides", "17", async context => {
                await context.ChangeSettingAsync(SelfTestSetting.AdvancedMode, false).ConfigureAwait(false);
                var mark = context.Host.Mark();
                await SayLocalAsync(context).ConfigureAwait(false);
                await PartnerLocalAsync(context, 1).ConfigureAwait(false);
                var mine = context.Host.PrintedSince(0).Count(line => line.Kind is TapKind.LocalMessage or TapKind.Notice && !line.Advanced && SelfTestWords.JargonIn(line.Text).Count > 0);
                var theirs = await context.Partner.AskAsync(context, PartnerVerb.ReportLog).ConfigureAwait(false);
                return StepResult.All((mine == 0, "a line you were shown in simple mode had a technical word"),
                    (theirs.Int("jargon") == 0, "a line your partner was shown in simple mode had a technical word"),
                    (context.Host.PrintedSince(mark).Any(line => line.Kind == TapKind.LocalMessage), "no local line was shown to check"));
            }, types: true),
            Auto("Advanced mode: the same", "18", async context => {
                await context.ChangeSettingAsync(SelfTestSetting.AdvancedMode, true).ConfigureAwait(false);
                var mark = context.Host.Mark();
                await SayLocalAsync(context).ConfigureAwait(false);
                await PartnerLocalAsync(context, 1).ConfigureAwait(false);
                var technical = context.Host.PrintedSince(mark).Count(line => line.Kind == TapKind.LocalMessage && SelfTestWords.JargonIn(line.Text).Count > 0);
                await context.ChangeSettingAsync(SelfTestSetting.AdvancedMode, false).ConfigureAwait(false);
                return StepResult.Expect(technical == 0, "a local line had a technical word in advanced mode");
            }, types: true),
        ],
    };

    // ---------------------------------------------------------------- local-chat 19-23

    private static SelfTestCheck Colours() => new() {
        Id = "local-chat-19-23", Title = "Colours and where local lines go", Steps = "local-chat 19-23", Mode = CheckMode.Guided, Needs = Needs,
        StepList = [
            Together(),
            ColourStep("A swatch", "row:45", 0x5AC85A, "Is your partner's local line in this green (the whole line, or only [Local] with Colour the whole line off)?"),
            ColourStep("A custom colour", "custom:FF66CC", 0xFF66CC, "Is your partner's local line in exactly this pink?"),
            ColourStep("The default", null, null, "Is only [Local] coloured, in LookingGlass blue, and the rest in the chat channel's own colour?"),
            Guided("Your partner's name colour", "20", async context => {
                await SetPartnerColourAsync(context, 0x33DDAA).ConfigureAwait(false);
                await PartnerLocalAsync(context, 1).ConfigureAwait(false);
                return await context.AskForResultAsync(new GuidedPrompt("Is your partner's name in their local line in this teal?", PromptKind.YesNo) {
                    Swatch = 0x33DDAA, Short = "their name colour",
                }).ConfigureAwait(false);
            }),
            Auto("Windows only: local lines still in game chat", "21", async context => {
                await context.ChangeSettingAsync(SelfTestSetting.WindowsOnly, true).ConfigureAwait(false);
                var before = (await context.Host.WindowsAsync().ConfigureAwait(false)).Windows.Count;
                var (_, arrived) = await PartnerLocalAsync(context, 1).ConfigureAwait(false);
                await context.DelayAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                var after = (await context.Host.WindowsAsync().ConfigureAwait(false)).Windows.Count;
                await context.ChangeSettingAsync(SelfTestSetting.WindowsOnly, false).ConfigureAwait(false);
                return StepResult.All((arrived == 1, "the local line wasn't in game chat with windows only on"), (after == before, "a window opened for a local line"));
            }),
            Ask("The chat channel it goes to", "22", new GuidedPrompt(
                "In Settings, Show messages in names a chat channel. Is your partner's local line in that channel (a chat tab filtering it out hides it)?", PromptKind.YesNo) {
                Short = "the chat channel",
            }),
            Auto("While talking in a channel, /lgl still goes to local chat", "23", async context => {
                var channel = await context.TestChannelAsync().ConfigureAwait(false);
                await context.StopTalkingAsync().ConfigureAwait(false);
                await context.TypeAsync(await context.CommandOfAsync(channel).ConfigureAwait(false)).ConfigureAwait(false);
                await context.TalkingInAsync(channel).ConfigureAwait(false);
                var marker = await SayLocalAsync(context).ConfigureAwait(false);
                var still = (await context.Host.StickyAsync().ConfigureAwait(false)).ChannelId == channel;
                var saw = await PartnerSawAsync(context, marker).ConfigureAwait(false);
                await context.StopTalkingAsync().ConfigureAwait(false);
                return StepResult.All((Arrived(saw), "the /lgl line didn't reach your partner"), (still, "talking in the channel ended"));
            }, types: true),
        ],
    };

    private static SelfTestStep ColourStep(string title, string? colour, uint? swatch, string question) => Guided(title, "19", async context => {
        await context.ChangeSettingAsync(SelfTestSetting.LocalChatColour, colour).ConfigureAwait(false);
        var (_, arrived) = await PartnerLocalAsync(context, 1).ConfigureAwait(false);
        if (arrived == 0) {
            return StepResult.Fail("your partner's local line didn't show");
        }

        return await context.AskForResultAsync(new GuidedPrompt(question, PromptKind.YesNo) { Swatch = swatch, Short = title.ToLowerInvariant() }).ConfigureAwait(false);
    });

    // ---------------------------------------------------------------- local-chat 25

    private static SelfTestCheck TheLog() => new() {
        Id = "local-chat-25", Title = "Logs keep names out (player side)", Steps = "local-chat 25 (players)", Mode = CheckMode.Automatic, Needs = PairSteps.Needs,
        StepList = [
            Auto("Counts only, on both sides", "25", async context => {
                if (context.Host.LocalSends == 0) {
                    return StepResult.Skip("needs the local chat checks above");
                }

                var theirs = await context.Partner.AskAsync(context, PartnerVerb.ReportLog).ConfigureAwait(false);
                return StepResult.All((context.Host.LogCounts().ForbiddenHits == 0, "your /xllog held a name or a test line"),
                    (theirs.Int("forbidden") == 0, "your partner's /xllog held a name or a test line"));
            }),
            Rest("25", "the server's side (the owner checks it)"),
        ],
    };

    // -{64} local-chat 32 (/p brb)

    private static SelfTestCheck PartyWhileTalking() => new() {
        Id = "local-chat-32-party", Title = "/p while talking in local chat", Steps = "local-chat 32 (/p brb)", Mode = CheckMode.AutomaticAndGuided, Needs = Needs,
        StepList = [
            PartyOfTwo("32"),
            SetupAuto("Talking in [Local]", "32", async context => StepResult.Expect(await context.TalkInLocalAsync().ConfigureAwait(false), "couldn't start talking in [Local]"),
                types: true),
            Guided("/p goes to Party once, and local chat goes on", "32", async context => {
                var mark = context.Host.Mark();
                var marker = context.NextMarker();
                var answer = await context.AskAsync(new GuidedPrompt($"Type in the game's chat box: /p {marker}") {
                    TesterTypes = true, Holds = () => PartyOfTwoAsync(context), HoldsWhy = SelfTestWords.WaitingPartyOfTwo, Short = "/p",
                }).ConfigureAwait(false);
                if (answer is not (GuidedAnswer.Done or GuidedAnswer.Seen)) {
                    return StepContext.Judge(answer, null);
                }

                var saw = await PartnerSawAsync(context, marker).ConfigureAwait(false);
                var inParty = context.Host.GameChatSince(mark).Count(line => line.Channel == GameChatKind.Party && line.FromTester && line.Text.Contains(marker, StringComparison.Ordinal));
                var still = await context.TalkingInAsync(StickyChannel.LocalId).ConfigureAwait(false);
                return StepResult.All((saw.Is("in", "party"), "your partner didn't see it in Party"), (inParty <= 1, "it went to Party more than once"),
                    (still, "local chat ended after /p"));
            }),
            SetupAuto("Back to Say", "32", async context => {
                await context.StopTalkingAsync().ConfigureAwait(false);
                return StepResult.Pass();
            }),
        ],
    };

    // ---------------------------------------------------------------- local-chat 47

    private static SelfTestCheck TalkingToAFriend() => new() {
        Id = "local-chat-47", Title = "Talking to a friend near you", Steps = "local-chat 47", Mode = CheckMode.AutomaticAndGuided, Needs = Needs,
        StepList = [
            Together(),
            Auto("/lgl alone, then lines", "47", async context => {
                if (!await context.TalkInLocalAsync().ConfigureAwait(false)) {
                    return StepResult.Fail("talking in local chat didn't start");
                }

                var mark = context.Host.Mark();
                var marker = context.NextMarker();
                await context.TypeAsync(marker, spends: SelfTestAction.LocalMessage).ConfigureAwait(false);
                var saw = await PartnerSawAsync(context, marker).ConfigureAwait(false);
                await context.Partner.AskAsync(context, PartnerVerb.ReportReceived).ConfigureAwait(false);
                await context.TypeAsync("", TestLink.ItemOnly, SelfTestAction.LocalMessage).ConfigureAwait(false);
                await context.TypeAsync(Insert(context.NextMarker(), " <t>"), spends: SelfTestAction.LocalMessage).ConfigureAwait(false);
                await context.DelayAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                var got = await context.Partner.AskAsync(context, PartnerVerb.ReportReceived).ConfigureAwait(false);
                var inSay = context.Host.GameChatSince(mark).Count(StepContextExtensions.AnySay);
                return StepResult.All((Arrived(saw) && saw.Is("tag", "Local"), "your partner didn't get it as [Local]"), (inSay == 0, "something went to Say"),
                    (HasLink(got, "item"), "the item alone didn't arrive as a link"), ((got.Int("you") ?? 0) >= 1, "<t> didn't arrive as your partner's name"));
            }, types: true),
            Guided("Out of range, still nothing in Say", "47", async context => {
                var walked = await context.Partner.AskPlayerAsync(context, PartnerAsk.WalkAway, "walked away").ConfigureAwait(false);
                if (walked.Outcome != StepOutcome.Passed) {
                    return walked;
                }

                var mark = context.Host.Mark();
                await context.TypeAsync(context.NextMarker(), spends: SelfTestAction.LocalMessage).ConfigureAwait(false);
                var refused = await ToldAsync(context, mark, LocalChatWords.Refusal(LocalChatWords.NobodyNear, LocalChat.Tag)).ConfigureAwait(false);
                var inSay = context.Host.GameChatSince(mark).Count(StepContextExtensions.AnySay);
                await context.Partner.AskPlayerAsync(context, PartnerAsk.ComeBack).ConfigureAwait(false);
                return StepResult.All((refused, "you weren't told nobody is near enough"), (inSay == 0, "something went to Say"));
            }),
            Auto("/s alone, then a line: Say", "47", async context => {
                await context.TypeAsync("/s").ConfigureAwait(false);
                await context.TalkingInAsync(null).ConfigureAwait(false);
                var marker = context.NextMarker();
                await context.TypeAsync(marker).ConfigureAwait(false);
                var saw = await PartnerSawAsync(context, marker).ConfigureAwait(false);
                return StepResult.All((saw.Is("in", "say"), "your partner didn't see it in Say"), (saw.Is("printed", "no"), "it went to local chat too"));
            }, types: true),
        ],
    };
}
