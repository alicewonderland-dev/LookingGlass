using LookingGlass.Core.Client;
using static LookingGlass.Core.SelfTest.SelfTestSteps;

namespace LookingGlass.Core.SelfTest.Suites;

/// <summary>
/// "With ChatTwo" (one player, ChatTwo on): local-chat-checklist.md 42 to 46, custom-colours-checklist.md 11 to 14, and
/// context-invites-checklist.md 18 (someone not registered, from ChatTwo's menu) and 19 (ChatTwo off and on). What passed
/// in the owner's fifth run (2026-10-13, plugin 0.2.19) is no longer offered: sticky-channel 28, chat-links 26, and
/// context-invites 20 to 21 (LookingGlass off and on). ChatTwo's window, input and menus are the tester's to type in and
/// look at; the runner sets things up, gives the test lines to type, and watches where each line went (local chat, Say,
/// the test channel).
/// </summary>
public static class ChatTwoSuite {
    public const string Id = "chattwo";

    private const uint Pink = 0xFF66CC;

    public static SelfTestSuite Create() => new(Id, "With ChatTwo", [LocalChatInChatTwo(), PinkInChatTwo(), NotRegisteredFromChatTwo(), ChatTwoOffAndOn()]) {
        Changes = [SelfTestSetting.LocalPrivacyAccepted, SelfTestSetting.SayWhenTalking, SelfTestSetting.WindowsOnly, SelfTestSetting.AdvancedMode],
        Planned = new Dictionary<SelfTestAction, int> {
            [SelfTestAction.ChannelMessage] = 6, [SelfTestAction.CreateChannel] = 1, [SelfTestAction.Lookup] = 2, [SelfTestAction.Invite] = 1,
        },
        Note = "needs ChatTwo",
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

    // ---------------------------------------------------------------- context-invites 18 (from ChatTwo's menu)

    /// <summary>
    /// Inviting someone who isn't registered, from ChatTwo's menu: what is left of context-invites 15 and 18 after the owner's
    /// fifth run (2026-10-13, plugin 0.2.19). The tester names someone they know who doesn't use LookingGlass and whose name
    /// is in a line in ChatTwo; the runner looks them up first (someone registered is never invited: a solo run sends nothing
    /// to anyone), then the tester invites them from ChatTwo's menu, and the runner checks the line, in simple mode.
    /// </summary>
    private static SelfTestCheck NotRegisteredFromChatTwo() => new() {
        Id = "context-invites-18-chattwo", Title = "Inviting someone who isn't registered, from ChatTwo's menu", Steps = "context-invites 18 (from ChatTwo's menu)",
        Mode = CheckMode.AutomaticAndGuided, Needs = SelfTestNeeds.Connection | SelfTestNeeds.ChatTwo | SelfTestNeeds.TestChannel,
        StepList = [
            SetupGuided("Who to try", "18", async context => {
                var (answer, input) = await context.AskForInputAsync(new GuidedPrompt(
                    "Type the name@world of someone you know who doesn't use LookingGlass and whose name is in a line in ChatTwo (for example: Alice Liddell@Twintania), " +
                    "then press Done. LookingGlass looks them up first; nothing is ever sent to them, and their name stays out of the report.") { Input = "Name@World" })
                    .ConfigureAwait(false);
                var at = input?.IndexOf('@') ?? -1;
                if (answer == GuidedAnswer.Skip || input == null || at <= 0 || at == input.Length - 1) {
                    context.State["ended"] = true;
                    return StepResult.Skip(answer == GuidedAnswer.Skip ? SelfTestWords.YouSkipped : "no name@world was given");
                }

                context.Host.Forbid(input);
                context.State["name"] = input[..at].Trim();
                context.State["world"] = input[(at + 1)..].Trim();
                return StepResult.Pass();
            }),
            // Setting up only: a lookup that passed is no pass of the check by itself.
            SetupAuto("They aren't registered (looked up first)", "18", async context => {
                if (Ended(context)) {
                    return StepResult.Skip("no name@world was given");
                }

                var registered = await context.ServerAsync(SelfTestAction.Lookup,
                    () => context.Host.IsRegisteredAsync((string) context.State["name"], (string) context.State["world"])).ConfigureAwait(false);
                if (registered != false) {
                    context.State["ended"] = true;
                    return StepResult.Skip(registered == true ? SelfTestWords.Registered : "they couldn't be looked up");
                }

                return StepResult.Pass();
            }),
            Guided("From ChatTwo's menu: the whole line", "18", async context => {
                if (Ended(context)) {
                    return StepResult.Skip(SelfTestWords.Registered);
                }

                await context.ChangeSettingAsync(SelfTestSetting.AdvancedMode, false).ConfigureAwait(false);
                var channel = await context.TestChannelAsync().ConfigureAwait(false);
                var tag = await context.TagOfAsync(channel).ConfigureAwait(false);
                // ChatTwo's menu looks them up and invites, as the game's does: both paced first.
                await context.ServerAsync(SelfTestAction.Lookup, () => Task.CompletedTask).ConfigureAwait(false);
                await context.ServerAsync(SelfTestAction.Invite, () => Task.CompletedTask).ConfigureAwait(false);
                var mark = context.Host.Mark();
                var answer = await context.AskAsync(new GuidedPrompt(
                    $"In ChatTwo, right-click that person's name in one of their lines, then Integrations > Invite to LookingGlass > {tag}. Only that person.") {
                    Watch = StepContextExtensions.Until(async () => await InviteLineAsync(context, mark, TimeSpan.Zero).ConfigureAwait(false) != null,
                        "no line about the invite yet", "a line said how it went"),
                }).ConfigureAwait(false);
                if (answer != GuidedAnswer.Seen && StepContext.Judge(answer, null) is { Outcome: StepOutcome.Skipped } skipped) {
                    return skipped;
                }

                var line = await InviteLineAsync(context, mark, answer == GuidedAnswer.Seen ? TimeSpan.Zero : TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                if (line == null) {
                    return StepResult.Fail("no line said how the invite went");
                }

                if (line.Text.StartsWith("Invited", StringComparison.Ordinal)) {
                    // Registered after all (since the lookup): never again; the clean-up disbands the channel, invite and all.
                    return StepResult.Skip(SelfTestWords.Registered);
                }

                return StepResult.All((line.Tone == NoticeTone.Info, "the line wasn't blue"),
                    (line.Text.Contains("isn't registered with LookingGlass on this server", StringComparison.Ordinal), "the line didn't say they aren't registered"),
                    (line.Text.Contains("install it and register", StringComparison.Ordinal), "simple mode's line didn't say what they need to do"));
            }),
        ],
    };

    private static bool Ended(StepContext context) => context.State.TryGetValue("ended", out var ended) && (bool) ended;

    /// <summary>The plugin's line about an invite since a mark: "Couldn't invite ..." or "Invited ...".</summary>
    private static Task<TapLine?> InviteLineAsync(StepContext context, long mark, TimeSpan within) => context.PrintedAsync(mark,
        line => line.Kind == TapKind.Notice && (line.Text.StartsWith("Couldn't invite", StringComparison.Ordinal) || line.Text.StartsWith("Invited", StringComparison.Ordinal)), within);

    // ---------------------------------------------------------------- context-invites 19

    /// <summary>
    /// ChatTwo turned off and on: what is left of context-invites 19 to 21 after the owner's fifth run (2026-10-13, plugin
    /// 0.2.19), where the tester skipped this part; LookingGlass off and on (20 to 21) passed.
    /// </summary>
    private static SelfTestCheck ChatTwoOffAndOn() => new() {
        Id = "context-invites-19", Title = "ChatTwo turned off and on", Steps = "context-invites 19 (ChatTwo off and on)", Mode = CheckMode.Guided, Needs = Needs,
        StepList = [
            Ask("ChatTwo off and on", "19", new GuidedPrompt(
                "In /xlplugins, turn ChatTwo off, then on again. Right-click a player's name in ChatTwo: is there one Invite to LookingGlass (not two)?", PromptKind.YesNo) {
                Short = "ChatTwo off and on",
            }),
        ],
    };
}
