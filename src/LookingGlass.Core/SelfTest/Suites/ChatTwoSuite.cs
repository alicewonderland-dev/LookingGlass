using LookingGlass.Core.Client;
using static LookingGlass.Core.SelfTest.SelfTestSteps;

namespace LookingGlass.Core.SelfTest.Suites;

/// <summary>
/// "With ChatTwo" (one player, ChatTwo on): local-chat-checklist.md 42 to 46, sticky-channel-checklist.md 28,
/// custom-colours-checklist.md 11 to 14, context-invites-checklist.md 19 to 21 and chat-links-checklist.md 26. ChatTwo's
/// window, input and menus are the tester's to type in and look at; the runner sets things up, gives the test lines to
/// type, and watches where each line went (local chat, Say, the test channel).
/// </summary>
public static class ChatTwoSuite {
    public const string Id = "chattwo";

    private const uint Pink = 0xFF66CC;

    public static SelfTestSuite Create() => new(Id, "With ChatTwo", [LocalChatInChatTwo(), ChatTwoNote(), PinkInChatTwo(), ChatTwoReloaded(), PartyFinderInChatTwo()]) {
        Changes = [SelfTestSetting.LocalPrivacyAccepted, SelfTestSetting.ChatTwoNoteShown, SelfTestSetting.SayWhenTalking, SelfTestSetting.WindowsOnly],
        Planned = new Dictionary<SelfTestAction, int> { [SelfTestAction.ChannelMessage] = 6, [SelfTestAction.CreateChannel] = 1 },
        Note = "needs ChatTwo, 1 reload",
    };

    private const SelfTestNeeds Needs = SelfTestNeeds.Connection | SelfTestNeeds.ChatTwo | SelfTestNeeds.Typing;

    /// <summary>The test channel shows in game chat (and so in ChatTwo), where the runner sees its lines.</summary>
    private static async Task<string> ShownChannelAsync(StepContext context) {
        var channel = await context.TestChannelAsync().ConfigureAwait(false);
        await context.ChangeSettingAsync(SelfTestSetting.WindowsOnly, false).ConfigureAwait(false);
        await context.Host.SetShowInGameChatAsync(channel, true).ConfigureAwait(false);
        return channel;
    }

    // ---------------------------------------------------------------- local-chat 42-46

    private static SelfTestCheck LocalChatInChatTwo() => new() {
        Id = "local-chat-42-46", Title = "Local chat with ChatTwo", Steps = "local-chat 42-46", Mode = CheckMode.Guided, Needs = Needs | SelfTestNeeds.LocalChat,
        StepList = [
            Ask("/lgl in ChatTwo", "42", async context => {
                await context.StopTalkingAsync().ConfigureAwait(false);
                await context.ChangeSettingAsync(SelfTestSetting.LocalPrivacyAccepted, true).ConfigureAwait(false);
                await context.ChangeSettingAsync(SelfTestSetting.SayWhenTalking, true).ConfigureAwait(false);
                // ChatTwo draws the label in its own text colour whatever it's sent; the colour goes on what is typed (ChatTwoOverrideColour).
                return new GuidedPrompt("Type /lgl in ChatTwo's input. Does ChatTwo's label read \"LookingGlass [Local]\" (perhaps with ChatTwo's \"(Warning: Say)\" " +
                                        "after it; ChatTwo draws the label in its own text colour)? Then type a few letters in ChatTwo's input without pressing Enter: are " +
                                        "they in local chat's colour (LookingGlass blue, unless you changed it)? Delete them before answering.", PromptKind.YesNo) {
                    Short = "the label and typed text", TesterTypes = true, ChatTwoOnSay = true,
                };
            }),
            Guided("A line goes to local chat, not Say", "43", async context => {
                var mark = context.Host.Mark();
                var marker = context.NextMarker();
                var answer = await context.AskAsync(new GuidedPrompt($"In ChatTwo's main input, type: {marker}") {
                    TesterTypes = true, ChatTwoOnSay = true,
                    Watch = StepContextExtensions.Until(async () => await context.NoticeAsync(mark, LocalChatWords.Refusal(LocalChatWords.NobodyNear, LocalChat.Tag), TimeSpan.Zero)
                        .ConfigureAwait(false) != null, "nothing yet", "local chat said nobody is near"),
                }).ConfigureAwait(false);
                if (answer != GuidedAnswer.Seen && StepContext.Judge(answer, null) is { Outcome: not StepOutcome.Passed } judged) {
                    return judged;
                }

                var inSay = await context.CountInGameChatAsync(mark, line => StepContextExtensions.SaidInSay(line, marker)).ConfigureAwait(false);
                return StepResult.Expect(answer == GuidedAnswer.Seen && inSay == 0, inSay > 0 ? "the line reached Say" : "the line didn't reach local chat");
            }),
            Guided("/s with text goes to Say once, and local chat goes on", "43", async context => {
                var mark = context.Host.Mark();
                var marker = context.NextMarker();
                var answer = await context.AskAsync(new GuidedPrompt($"In ChatTwo's main input, type: /s {marker}") {
                    TesterTypes = true, ChatTwoOnSay = true,
                    Watch = StepContextExtensions.Until(async () => await context.InGameChatAsync(mark, line => StepContextExtensions.SaidInSay(line, marker), TimeSpan.Zero)
                        .ConfigureAwait(false) != null, "not in Say yet", "it reached Say"),
                }).ConfigureAwait(false);
                if (answer != GuidedAnswer.Seen) {
                    return StepContext.Judge(answer, null) is { Outcome: StepOutcome.Skipped } skipped ? skipped : StepResult.Fail("/s with text didn't reach Say");
                }

                var count = await context.CountInGameChatAsync(mark, line => StepContextExtensions.SaidInSay(line, marker)).ConfigureAwait(false);
                var still = (await context.Host.StickyAsync().ConfigureAwait(false)).TalkingInLocal;
                var result = StepResult.All((count == 1, "/s with text reached Say more than once"), (still, "local chat ended"));
                return result.Outcome == StepOutcome.Passed ? StepResult.Pass("the Party parts (43, 44: need a partner as the whole party)") : result;
            }),
            ThenBackToSay("ChatTwo's picker ends it", "45", context => context.AskForResultAsync(new GuidedPrompt(
                "Pick Party in ChatTwo's channel picker (or click a ChatTwo tab with another channel). Don't type anything there.") {
                Short = "the picker",
                Watch = StepContextExtensions.Until(async () => (await context.Host.StickyAsync().ConfigureAwait(false)).ChannelId == null, "still talking in [Local]", "stopped"),
            })),
            ThenBackToSay("On a tell, /lgl is refused", "46", async context => {
                var mark = context.Host.Mark();
                var answer = await context.AskAsync(new GuidedPrompt("Put ChatTwo's input on a tell (to a friend), then type /lgl alone in it, nothing else.") {
                    TesterTypes = true, ChatTwoOnSay = true,
                    Watch = StepContextExtensions.Until(async () => await context.PrintedAsync(mark,
                        line => line.Kind == TapKind.Notice && line.Text.StartsWith("Switch ChatTwo off the tell first", StringComparison.Ordinal), TimeSpan.Zero).ConfigureAwait(false) != null,
                        "nothing yet", "refused"),
                }).ConfigureAwait(false);
                var talking = (await context.Host.StickyAsync().ConfigureAwait(false)).ChannelId != null;
                return answer == GuidedAnswer.Seen && !talking ? StepResult.Pass() : StepContext.Judge(answer, null) is { Outcome: StepOutcome.Skipped } skipped ? skipped
                    : StepResult.Fail(talking ? "it started on a tell" : "it wasn't refused on a tell");
            }),
        ],
    };

    /// <summary>
    /// A guided step that moves ChatTwo's input off Say (to Party, a tell, another channel), then, whatever its result (a
    /// failure ends the check), asks for ChatTwo's input back on Say: never left there, where a slip of the keyboard would
    /// reach other players.
    /// </summary>
    internal static SelfTestStep ThenBackToSay(string title, string? checklist, Func<StepContext, Task<StepResult>> run) => Guided(title, checklist, async context => {
        var result = await run(context).ConfigureAwait(false);
        if (await context.Host.ChatTwoInputAsync().ConfigureAwait(false) != SelfTestGameChannels.ChatTwoSay) {
            await context.AskAsync(new GuidedPrompt("Put ChatTwo's input back on Say (pick Say in its channel picker).") {
                Watch = StepContextExtensions.Until(async () => await context.Host.ChatTwoInputAsync().ConfigureAwait(false) == SelfTestGameChannels.ChatTwoSay,
                    "ChatTwo's input isn't on Say", "on Say"),
            }).ConfigureAwait(false);
        }

        // Not a check of LookingGlass: whatever the answer there, the step keeps its own result (and the next line to type asks again).
        return result;
    });

    // ---------------------------------------------------------------- sticky-channel 28

    private static SelfTestCheck ChatTwoNote() => new() {
        Id = "sticky-channel-28", Title = "The ChatTwo note when you start talking in a channel", Steps = "sticky-channel 28", Mode = CheckMode.Guided,
        Needs = Needs | SelfTestNeeds.TestChannel,
        StepList = [
            ThenBackToSay("The note, once", "28", async context => {
                var channel = await ShownChannelAsync(context).ConfigureAwait(false);
                var command = await context.CommandOfAsync(channel).ConfigureAwait(false);
                var tag = await context.TagOfAsync(channel).ConfigureAwait(false);
                await context.StopTalkingAsync().ConfigureAwait(false);
                await context.ChangeSettingAsync(SelfTestSetting.ChatTwoNoteShown, false).ConfigureAwait(false);
                var mark = context.Host.Mark();
                var answer = await context.AskAsync(new GuidedPrompt($"Switch ChatTwo's input to Party, then type {command} alone in it, nothing else.") {
                    TesterTypes = true, ChatTwoOnSay = true,
                    Watch = StepContextExtensions.Until(() => context.TalkingInAsync(channel, TimeSpan.Zero), "not talking in the test channel yet", "talking in the test channel"),
                }).ConfigureAwait(false);
                if (answer != GuidedAnswer.Seen) {
                    return StepContext.Judge(answer, null) is { Outcome: StepOutcome.Skipped } skipped ? skipped : StepResult.Fail("it didn't start talking in the test channel");
                }

                var note = await context.CountPrintedAsync(mark, line => line.Text == StickyMessages.ChatTwoNote(tag)).ConfigureAwait(false);
                // The info bar follows a frame or more later: waited for.
                return await context.SettledAsync(async () => StepResult.All((note == 1, "the ChatTwo line didn't follow once"),
                    ((await context.Host.StickyAsync().ConfigureAwait(false)).InfoBar?.Contains(tag, StringComparison.Ordinal) == true, "the info bar doesn't name the channel")))
                    .ConfigureAwait(false);
            }),
            Ask("ChatTwo's label", "28", new GuidedPrompt("Does ChatTwo's input label read \"LookingGlass [...]\" with the test channel's tag?", PromptKind.YesNo) { Short = "the label" }),
            SetupAuto("Back to Say", null, async context => {
                await context.StopTalkingAsync().ConfigureAwait(false);
                return StepResult.Pass();
            }),
        ],
    };

    // ---------------------------------------------------------------- custom-colours 11-14

    private static SelfTestCheck PinkInChatTwo() => new() {
        Id = "custom-colours-11-14", Title = "Pink everywhere, ChatTwo", Steps = "custom-colours 11-14", Mode = CheckMode.Guided, Needs = Needs | SelfTestNeeds.TestChannel,
        StepList = [
            SetupAuto("The test channel pink, a line with an item link", "11", async context => {
                var channel = await ShownChannelAsync(context).ConfigureAwait(false);
                await context.Host.SetChannelColourAsync(channel, ChannelColour.Custom(Pink)).ConfigureAwait(false);
                await context.StopTalkingAsync().ConfigureAwait(false);
                var mark = context.Host.Mark();
                await context.TypeAsync($"{await context.CommandOfAsync(channel).ConfigureAwait(false)} {context.NextMarker()}", TestLink.ItemInMiddle, SelfTestAction.ChannelMessage)
                    .ConfigureAwait(false);
                return StepResult.Expect(await context.PrintedAsync(mark, line => line.Kind == TapKind.Message && line.ChannelId == channel, TimeSpan.FromSeconds(10)).ConfigureAwait(false) != null,
                    "the line didn't show");
            }, types: true),
            Ask("In ChatTwo's window", "11", new GuidedPrompt(
                "In ChatTwo's window, is the test line's tag (and, with Colour the whole line on, the whole line) pink like this swatch, the text after the item link still pink? " +
                "With Colour the whole line off, is only the tag pink?", PromptKind.YesNo) { Swatch = Pink, Short = "the line" }),
            Ask("ChatTwo's label", "12", async context => {
                var channel = await context.TestChannelAsync().ConfigureAwait(false);
                await context.TypeAsync(await context.CommandOfAsync(channel).ConfigureAwait(false)).ConfigureAwait(false);
                await context.TalkingInAsync(channel).ConfigureAwait(false);
                return new GuidedPrompt("Does ChatTwo's input label read \"LookingGlass [...]\" (in ChatTwo's own text colour)? Then type a few letters in ChatTwo's input " +
                                        "without pressing Enter: are they in this pink? Delete them before answering.", PromptKind.YesNo) {
                    Swatch = Pink, Short = "the label and typed text",
                };
            }),
            Ask("Anything different", "14", async context => {
                await context.StopTalkingAsync().ConfigureAwait(false);
                return new GuidedPrompt("Does ChatTwo show everything as the game's own chat does? Yes if all is the same; No, with a note, if not.", PromptKind.YesNo) {
                    Short = "the same as game chat",
                };
            }),
            Rest("13", "the invite submenu in ChatTwo (13: needs another player's name)"),
        ],
    };

    // ---------------------------------------------------------------- context-invites 19-21

    private static SelfTestCheck ChatTwoReloaded() => new() {
        Id = "context-invites-19-21", Title = "ChatTwo turned off and on", Steps = "context-invites 19-21", Mode = CheckMode.Guided, Needs = Needs,
        StepList = [
            Ask("ChatTwo off and on", "19", new GuidedPrompt(
                "In /xlplugins, turn ChatTwo off, then on again. Right-click a player's name in ChatTwo: is there one Invite to LookingGlass (not two)?", PromptKind.YesNo) {
                Short = "ChatTwo off and on",
            }),
            CarryOn(Reload, "20"),
            Ask("LookingGlass off and on", "20-21", new GuidedPrompt(
                "Right-click a player's name in ChatTwo again: is there one Invite to LookingGlass (not two, not none)?", PromptKind.YesNo) { Short = "LookingGlass off and on" }),
        ],
    };

    // ---------------------------------------------------------------- chat-links 26

    private static SelfTestCheck PartyFinderInChatTwo() => new() {
        Id = "chat-links-26", Title = "Party finder: with ChatTwo", Steps = "chat-links 26", Mode = CheckMode.Guided, Needs = Needs | SelfTestNeeds.TestChannel,
        StepList = [
            SetupGuided("A recruitment of your own", null, async context => {
                await ShownChannelAsync(context).ConfigureAwait(false);
                var answer = await context.AskAsync(new GuidedPrompt("Is a recruitment of your own up in the Party Finder?", PromptKind.YesNo)).ConfigureAwait(false);
                context.State["recruiting"] = answer is GuidedAnswer.Yes or GuidedAnswer.Done;
                return answer is GuidedAnswer.Yes or GuidedAnswer.Done ? StepResult.Pass() : StepResult.Skip("needs a recruitment of your own");
            }),
            Guided("Sending it from ChatTwo", "26", async context => {
                if (!(bool) context.State["recruiting"]) {
                    return StepResult.Skip("needs a recruitment of your own");
                }

                var channel = await context.TestChannelAsync().ConfigureAwait(false);
                var command = await context.CommandOfAsync(channel).ConfigureAwait(false);
                var mark = context.Host.Mark();
                var answer = await context.AskAsync(new GuidedPrompt(
                    $"Click into ChatTwo's input, type {command} join  (with a space after it), click your recruitment's chat button, and press Enter. Note what the button put in ChatTwo's input.") {
                    TesterTypes = true, ChatTwoOnSay = true,
                    Watch = StepContextExtensions.Until(() => Task.FromResult(context.Host.PrintedSince(mark).Any(line => line.Kind == TapKind.Message && line.Own && line.ChannelId == channel
                        && line.Links.OfType<ChatLink.PartyFinder>().Any())), "no party finder link sent yet", "a party finder link was sent"),
                }).ConfigureAwait(false);
                return answer == GuidedAnswer.Seen ? StepResult.Pass() : StepContext.Judge(answer, null) is { Outcome: StepOutcome.Skipped } skipped ? skipped
                    : StepResult.Fail("no party finder link reached the test channel");
            }),
            Ask("Clicking it in ChatTwo", "26", new GuidedPrompt("Click the link in ChatTwo's window: does the Party Finder open on your listing?", PromptKind.YesNo) { Short = "click in ChatTwo" }),
        ],
    };
}
