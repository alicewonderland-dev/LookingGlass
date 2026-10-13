using LookingGlass.Core.Client;
using static LookingGlass.Core.SelfTest.SelfTestSteps;

namespace LookingGlass.Core.SelfTest.Suites;

/// <summary>
/// "Talking in a channel and local chat" (one player): local-chat-checklist.md steps 1 to 3c and 29 to 41, judged by the
/// runner from sticky mode's state, what the plugin printed, the game's chat (Say, Emote) and the log tap, with the steps
/// only the tester can do (Tab, the info bar, Send Tell, a reload, a relog) guided. Alone in a private place, local chat
/// always finds nobody near, so nothing is ever sent to anyone; the test channel gets one line.
/// </summary>
public static class TalkSuite {
    public const string Id = "talk";

    private const string Local = LocalChat.Tag;

    private static readonly TimeSpan Short = TimeSpan.FromSeconds(3);

    public static SelfTestSuite Create() => new(Id, "Talking in a channel and local chat", [
        Free(), AskFirst(), Starting(), NeverSay(), OneOffs(), Ending(), Refused(), LogAndWords(),
    ]) {
        Changes = [SelfTestSetting.LocalPrivacyAccepted, SelfTestSetting.LocalTalkNoteShown, SelfTestSetting.SayWhenTalking],
        Planned = new Dictionary<SelfTestAction, int> { [SelfTestAction.ChannelMessage] = 2, [SelfTestAction.CreateChannel] = 1, [SelfTestAction.Reconnect] = 3 },
        Note = "2 reloads, 1 relog",
    };

    private const SelfTestNeeds Typing = SelfTestNeeds.Connection | SelfTestNeeds.Typing | SelfTestNeeds.LocalChat;

    /// <summary>The refusal said for a line typed while talking in local chat, alone.</summary>
    private static Wording NobodyNearHere => LocalChatWords.Refusal(LocalChatWords.NobodyNear, Local);

    // ---------------------------------------------------------------- local-chat 1-3

    private static SelfTestCheck Free() => new() {
        Id = "local-chat-1-3", Title = "Is /lgl free, and what does it say", Steps = "local-chat 1-3", Mode = CheckMode.AutomaticAndGuided,
        StepList = [
            Auto("/lgl is LookingGlass's, once, and explains itself", "1-2", async context => {
                var command = await context.Host.CommandAsync(LocalChat.Command).ConfigureAwait(false);
                return StepResult.All(
                    (command.Registered, "/lgl isn't registered"),
                    (command.Ours, "/lgl is registered by another plugin"),
                    (command.ShownInHelp, "/lgl isn't listed in /xlhelp"),
                    (command.Help == LocalChatWords.Usage.Plain, "/lgl's line in /xlhelp isn't the expected words"),
                    (SettingsWords.Help(SettingHelp.LocalChat, "").Plain.Contains(LocalChat.Command, StringComparison.Ordinal), "the Colour \"?\" doesn't explain /lgl"));
            }),
            Ask("/xlhelp lists it", "1-2", new GuidedPrompt(
                "Type /xlhelp. Is /lgl listed once, by LookingGlass, saying it talks to your friends near you who use LookingGlass (about 20 yalms), and that /lgl alone keeps talking there until /s?",
                PromptKind.YesNo) { Short = "/xlhelp" }),
            CarryOn("In /xlplugins, turn LookingGlass off. Type /lgl: the game should say there is no such command. Then turn LookingGlass on again. The self-test carries on by itself.", "1"),
            Ask("The game had no /lgl", "1", new GuidedPrompt("While LookingGlass was off, did the game say /lgl isn't a command?", PromptKind.YesNo) { Short = "off and on" }),
            Ask("The Colour \"?\" in Settings", "3", async context => {
                await context.Host.ShowSettingsAsync(true).ConfigureAwait(false);
                return new GuidedPrompt("In Settings, under Local chat, click the ? after Colour. Does its bubble say that /lgl message talks to friends near you, and /lgl on its own keeps talking there until /s?",
                    PromptKind.YesNo) { Short = "Colour ?" };
            }),
        ],
    };

    // ---------------------------------------------------------------- local-chat 3a-3c

    private static SelfTestCheck AskFirst() => new() {
        Id = "local-chat-3a-3c", Title = "The \"ask first\" window", Steps = "local-chat 3a-3c", Mode = CheckMode.AutomaticAndGuided, Needs = Typing,
        StepList = [
            Auto("/lgl with a message asks first", "3a", async context => {
                await context.StopTalkingAsync().ConfigureAwait(false);
                await context.ChangeSettingAsync(SelfTestSetting.LocalPrivacyAccepted, false).ConfigureAwait(false);
                return await AsksFirstAsync(context).ConfigureAwait(false);
            }, types: true),
            Ask("Not now", "3b", context => Task.FromResult(new GuidedPrompt("Click Not now in the local chat window.") {
                Short = "Not now",
                Watch = StepContextExtensions.Until(async () => !await context.Host.PrivacyWindowOpenAsync().ConfigureAwait(false)
                    && !await context.SettingIsAsync(SelfTestSetting.LocalPrivacyAccepted).ConfigureAwait(false), "the window is open", "closed, not accepted"),
            })),
            Auto("It asks again", "3b", AsksFirstAsync, types: true),
            Ask("Accept", "3c", context => Task.FromResult(new GuidedPrompt("Click Accept and use local chat.") {
                Short = "Accept",
                Watch = StepContextExtensions.Until(() => context.SettingIsAsync(SelfTestSetting.LocalPrivacyAccepted), "not accepted yet", "accepted"),
            })),
            Auto("Told to send it again", "3c", async context => {
                var mark = (long) context.State["mark"];
                var said = await context.NoticeAsync(mark, LocalChatWords.PrivacyAccepted).ConfigureAwait(false);
                var open = await context.Host.PrivacyWindowOpenAsync().ConfigureAwait(false);
                return StepResult.All((said != null, "game chat didn't say to send the message again"), (!open, "the window didn't close"));
            }),
            Ask("Settings says it", "3c", async context => {
                await context.Host.ShowSettingsAsync(true).ConfigureAwait(false);
                return new GuidedPrompt("In Settings, under Local chat: does it say Privacy notice accepted, with a ?, Read it and Withdraw, all visible?", PromptKind.YesNo) {
                    Short = "Settings",
                };
            }),
        ],
    };

    /// <summary>/lgl with a message, not accepted: nothing sent, the blue line, the window. Keeps the mark for the steps after.</summary>
    private static async Task<StepResult> AsksFirstAsync(StepContext context) {
        var mark = context.Host.Mark();
        var sends = context.Host.LocalSends;
        await context.TypeAsync($"{LocalChat.Command} {context.NextMarker()}").ConfigureAwait(false);
        var said = await context.NoticeAsync(mark, LocalChatWords.PrivacyAsked).ConfigureAwait(false);
        var open = await context.WaitUntilAsync(() => context.Host.PrivacyWindowOpenAsync()).ConfigureAwait(false);
        context.State["mark"] = context.Host.Mark();
        return StepResult.All(
            (said is { Tone: NoticeTone.Info }, "no blue line said local chat asks first"),
            (open, "the privacy window didn't open"),
            (context.Host.LocalSends == sends, "something was sent before accepting"));
    }

    // ---------------------------------------------------------------- local-chat 29

    private static SelfTestCheck Starting() => new() {
        Id = "local-chat-29", Title = "Starting, and the once-only line", Steps = "local-chat 29", Mode = CheckMode.Automatic, Needs = Typing,
        StepList = [
            Auto("/lgl alone, the first time ever", "29", async context => {
                await context.StopTalkingAsync().ConfigureAwait(false);
                await context.ChangeSettingAsync(SelfTestSetting.LocalPrivacyAccepted, true).ConfigureAwait(false);
                await context.ChangeSettingAsync(SelfTestSetting.LocalTalkNoteShown, false).ConfigureAwait(false);
                await context.ChangeSettingAsync(SelfTestSetting.SayWhenTalking, true).ConfigureAwait(false);
                var mark = context.Host.Mark();
                await context.TypeAsync(LocalChat.Command).ConfigureAwait(false);
                var talking = await context.TalkingInAsync(StickyChannel.LocalId).ConfigureAwait(false);
                var started = await context.NoticeAsync(mark, StickyMessages.Entered(Local)).ConfigureAwait(false);
                var note = await context.NoticeAsync(mark, LocalChatWords.TalkingNote).ConfigureAwait(false);
                var sticky = await context.Host.StickyAsync().ConfigureAwait(false);
                var environment = await context.EnvironmentAsync().ConfigureAwait(false);
                var colour = SelfTestValues.Colour((await context.Host.ReadSettingAsync(SelfTestSetting.LocalChatColour).ConfigureAwait(false)).ToString()) ?? ColouredText.DefaultTag;
                return StepResult.All(
                    (talking, "/lgl alone didn't start talking in [Local]"),
                    (started is { Tone: NoticeTone.Info, Tag: Local }, "no blue \"Now talking in [Local].\""),
                    (note != null, "the once-only line wasn't said"),
                    (!environment.ChatInputReadable || sticky.ChatInputLabel == Local, "the chat input doesn't name its channel [Local]"),
                    (sticky.InfoBar?.Contains($"LG {Local}", StringComparison.Ordinal) == true, "the server info bar doesn't show \"LG [Local]\""),
                    (sticky.ShownColour == colour, "the labels aren't in local chat's colour"));
            }, types: true),
            Auto("/lgl alone again: only the first line", "29", async context => {
                var mark = context.Host.Mark();
                await context.TypeAsync(LocalChat.Command).ConfigureAwait(false);
                var started = await context.NoticeAsync(mark, StickyMessages.Entered(Local)).ConfigureAwait(false);
                var notes = await context.CountPrintedAsync(mark, line => line.Text == LocalChatWords.TalkingNote.Plain).ConfigureAwait(false);
                return StepResult.All((started != null, "no \"Now talking in [Local].\" the second time"), (notes == 0, "the once-only line came again"));
            }, types: true),
        ],
    };

    // ---------------------------------------------------------------- local-chat 30-31

    private static SelfTestCheck NeverSay() => new() {
        Id = "local-chat-30-31", Title = "Typing never reaches Say", Steps = "local-chat 30-31", Mode = CheckMode.Automatic, Needs = Typing,
        StepList = [
            Auto("A line, alone", "30", async context => {
                if (!await context.TalkInLocalAsync().ConfigureAwait(false)) {
                    return StepResult.Fail("couldn't start talking in [Local]");
                }

                var mark = context.Host.Mark();
                var marker = context.NextMarker();
                await context.TypeAsync(marker).ConfigureAwait(false);
                var refused = await context.NoticeAsync(mark, NobodyNearHere).ConfigureAwait(false);
                var inSay = await context.CountInGameChatAsync(mark, StepContextExtensions.AnySay).ConfigureAwait(false);
                return StepResult.All((refused != null, "no \"Not sent to [Local] or game chat: nobody is near enough\""), (inSay == 0, "the line reached Say"));
            }, types: true),
            Auto("A line with only an item link", "30", async context => {
                var mark = context.Host.Mark();
                await context.TypeAsync("", TestLink.ItemOnly).ConfigureAwait(false);
                var refused = await context.NoticeAsync(mark, NobodyNearHere).ConfigureAwait(false);
                var usage = await context.CountPrintedAsync(mark, line => line.Text == LocalChatWords.Usage.Plain, TimeSpan.Zero).ConfigureAwait(false);
                var inSay = await context.CountInGameChatAsync(mark, StepContextExtensions.AnySay).ConfigureAwait(false);
                var result = StepResult.All((refused != null, "a line with only a link didn't get the nobody-near line"),
                    (usage == 0, "a line with only a link was told how to use /lgl"), (inSay == 0, "the link reached Say"));
                return result.Outcome == StepOutcome.Passed ? StepResult.Pass("the part next to a stranger (step 31)") : result;
            }, types: true),
        ],
    };

    // ---------------------------------------------------------------- local-chat 32-33

    private static SelfTestCheck OneOffs() => new() {
        Id = "local-chat-32-33", Title = "Commands, one-offs and moving between channels", Steps = "local-chat 32-33", Mode = CheckMode.Automatic,
        Needs = Typing | SelfTestNeeds.TestChannel,
        StepList = [
            Auto("/s with text goes to Say once", "32", context => OneOffAsync(context, "/s", line => line.Channel == GameChatKind.Say && line.FromTester, "/s with text"), types: true),
            Auto("/em goes to emotes once", "32", context => OneOffAsync(context, "/em", line => line.Channel == GameChatKind.Emote && line.FromTester, "/em"), types: true),
            Auto("/lgc with text goes to the test channel once", "32", async context => {
                var channel = await context.TestChannelAsync().ConfigureAwait(false);
                // Its lines in game chat, where the runner sees them.
                await context.ChangeSettingAsync(SelfTestSetting.WindowsOnly, false).ConfigureAwait(false);
                await context.Host.SetShowInGameChatAsync(channel, true).ConfigureAwait(false);
                var command = await context.CommandOfAsync(channel).ConfigureAwait(false);
                if (!await context.TalkInLocalAsync().ConfigureAwait(false)) {
                    return StepResult.Fail("couldn't start talking in [Local]");
                }

                var mark = context.Host.Mark();
                var marker = context.NextMarker();
                await context.TypeAsync($"{command} {marker}", spends: SelfTestAction.ChannelMessage).ConfigureAwait(false);
                await context.PrintedAsync(mark, line => line.Kind == TapKind.Message && line.ChannelId == channel && line.Text.Contains(marker, StringComparison.Ordinal),
                    TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                var shown = await context.CountPrintedAsync(mark, line => line.Kind == TapKind.Message && line.ChannelId == channel && line.Text.Contains(marker, StringComparison.Ordinal))
                    .ConfigureAwait(false);
                var still = (await context.Host.StickyAsync().ConfigureAwait(false)).TalkingInLocal;
                return StepResult.All((shown == 1, shown == 0 ? "/lgc with text didn't reach the test channel" : "/lgc with text reached the test channel more than once"),
                    (still, "local chat ended after /lgc with text"));
            }, types: true),
            Auto("/lgl with text is local chat, once", "32", async context => {
                var mark = context.Host.Mark();
                await context.TypeAsync($"{LocalChat.Command} {context.NextMarker()}").ConfigureAwait(false);
                var refused = await context.CountPrintedAsync(mark, line => line.Text == LocalChatWords.NobodyNear.Plain).ConfigureAwait(false);
                var still = (await context.Host.StickyAsync().ConfigureAwait(false)).TalkingInLocal;
                return StepResult.All((refused == 1, "/lgl with text didn't get one nobody-near line"), (still, "local chat ended after /lgl with text"));
            }, types: true),
            Auto("/lgc alone moves to the test channel", "33", async context => {
                var channel = await context.TestChannelAsync().ConfigureAwait(false);
                var command = await context.CommandOfAsync(channel).ConfigureAwait(false);
                var tag = await context.TagOfAsync(channel).ConfigureAwait(false);
                var mark = context.Host.Mark();
                await context.TypeAsync(command).ConfigureAwait(false);
                var moved = await context.TalkingInAsync(channel).ConfigureAwait(false);
                var said = await context.NoticeAsync(mark, StickyMessages.Entered(tag)).ConfigureAwait(false);
                var sticky = await context.Host.StickyAsync().ConfigureAwait(false);
                return StepResult.All((moved, "/lgc alone didn't move to the test channel"), (said != null, "no \"Now talking in\" the test channel"),
                    (sticky.InfoBar?.Contains(tag, StringComparison.Ordinal) == true, "the info bar doesn't name the test channel"));
            }, types: true),
            Auto("/lgl with text there, once, and the channel goes on", "33", async context => {
                var channel = await context.TestChannelAsync().ConfigureAwait(false);
                var mark = context.Host.Mark();
                await context.TypeAsync($"{LocalChat.Command} {context.NextMarker()}").ConfigureAwait(false);
                var refused = await context.CountPrintedAsync(mark, line => line.Text == LocalChatWords.NobodyNear.Plain).ConfigureAwait(false);
                var still = await context.TalkingInAsync(channel, TimeSpan.Zero).ConfigureAwait(false);
                return StepResult.All((refused == 1, "/lgl with text in the channel didn't go to local chat once"), (still, "the test channel ended after /lgl with text"));
            }, types: true),
            Auto("/lgl alone moves back to [Local]", "33", async context => {
                var mark = context.Host.Mark();
                await context.TypeAsync(LocalChat.Command).ConfigureAwait(false);
                var back = await context.TalkingInAsync(StickyChannel.LocalId).ConfigureAwait(false);
                var said = await context.NoticeAsync(mark, StickyMessages.Entered(Local)).ConfigureAwait(false);
                return StepResult.All((back, "/lgl alone didn't move back to [Local]"), (said != null, "no \"Now talking in [Local].\""));
            }, types: true),
            Auto("Nicknaming a channel Local is refused", "33", async context => {
                var channel = await context.TestChannelAsync().ConfigureAwait(false);
                var upper = await context.Host.SetNicknameAsync(channel, "Local").ConfigureAwait(false);
                var lower = await context.Host.SetNicknameAsync(channel, "local").ConfigureAwait(false);
                var nickname = await context.Host.NicknameOfAsync(channel).ConfigureAwait(false);
                var result = StepResult.All((upper != null && lower != null, "the nickname Local was accepted"),
                    (nickname == null || !nickname.Equals("local", StringComparison.OrdinalIgnoreCase), "the test channel is nicknamed Local"));
                return result.Outcome == StepOutcome.Passed ? StepResult.Pass("/p brb (needs a partner as the whole party)") : result;
            }),
        ],
    };

    /// <summary>A one-off while talking in local chat: reaches where it should, once, and local chat goes on.</summary>
    private static async Task<StepResult> OneOffAsync(StepContext context, string command, Func<GameChatLine, bool> where, string what) {
        if (!await context.TalkInLocalAsync().ConfigureAwait(false)) {
            return StepResult.Fail("couldn't start talking in [Local]");
        }

        var mark = context.Host.Mark();
        var marker = context.NextMarker();
        await context.TypeAsync($"{command} {marker}").ConfigureAwait(false);
        await context.InGameChatAsync(mark, line => where(line) && line.Text.Contains(marker, StringComparison.Ordinal)).ConfigureAwait(false);
        var count = await context.CountInGameChatAsync(mark, line => where(line) && line.Text.Contains(marker, StringComparison.Ordinal)).ConfigureAwait(false);
        var still = (await context.Host.StickyAsync().ConfigureAwait(false)).TalkingInLocal;
        return StepResult.All((count >= 1, $"{what} didn't arrive where it should"), (count <= 1, $"{what} arrived more than once"), (still, $"local chat ended after {what}"));
    }

    // ---------------------------------------------------------------- local-chat 34-36

    private static SelfTestCheck Ending() => new() {
        Id = "local-chat-34-36", Title = "Ending it", Steps = "local-chat 34-36", Mode = CheckMode.AutomaticAndGuided, Needs = Typing,
        StepList = [
            Auto("/s alone ends it, and says so", "34", async context => {
                await context.ChangeSettingAsync(SelfTestSetting.SayWhenTalking, true).ConfigureAwait(false);
                if (!await context.TalkInLocalAsync().ConfigureAwait(false)) {
                    return StepResult.Fail("couldn't start talking in [Local]");
                }

                var mark = context.Host.Mark();
                await context.TypeAsync("/s").ConfigureAwait(false);
                var ended = await context.TalkingInAsync(null).ConfigureAwait(false);
                var said = await context.NoticeAsync(mark, StickyMessages.Ended(Local, StickyEnd.ChannelSwitched)).ConfigureAwait(false);
                return StepResult.All((ended, "/s alone didn't end it"), (said != null, "no \"Stopped talking in [Local].\""));
            }, types: true),
            Ask("Tab ends it", "34", async context => {
                await context.TalkInLocalAsync().ConfigureAwait(false);
                return new GuidedPrompt("Click in the game's chat box and press Tab.") { Short = "Tab", Watch = StoppedWatch(context) };
            }),
            Ask("The info bar ends it", "34", async context => {
                await context.TalkInLocalAsync().ConfigureAwait(false);
                return new GuidedPrompt("Click LookingGlass's entry in the server info bar (\"LG [Local]\").") { Short = "info bar", Watch = StoppedWatch(context) };
            }),
            Ask("Send Tell ends it", "34", async context => {
                await context.TalkInLocalAsync().ConfigureAwait(false);
                return new GuidedPrompt("Right-click a name in your friends list (or in chat), choose Send Tell, then press Escape without sending.") {
                    Short = "Send Tell", Watch = StoppedWatch(context),
                };
            }),
            Auto("Disconnect ends it, and says so even quietly", "35", async context => {
                await context.ChangeSettingAsync(SelfTestSetting.SayWhenTalking, false).ConfigureAwait(false);
                if (!await context.TalkInLocalAsync().ConfigureAwait(false)) {
                    return StepResult.Fail("couldn't start talking in [Local]");
                }

                var mark = context.Host.Mark();
                await context.DisconnectAsync().ConfigureAwait(false);
                var ended = await context.TalkingInAsync(null).ConfigureAwait(false);
                var said = await context.NoticeAsync(mark, StickyMessages.Ended(Local, StickyEnd.Disconnected)).ConfigureAwait(false);
                var back = await context.ConnectAsync().ConfigureAwait(false);
                return StepResult.All((ended, "Disconnect didn't end it"), (said != null, "no \"Stopped talking in [Local]: disconnected.\""), (back, "couldn't connect again"));
            }, types: true),
            Auto("Quiet, the labels still change", "36", async context => {
                await context.ChangeSettingAsync(SelfTestSetting.SayWhenTalking, false).ConfigureAwait(false);
                await context.StopTalkingAsync().ConfigureAwait(false);
                var mark = context.Host.Mark();
                await context.TypeAsync(LocalChat.Command).ConfigureAwait(false);
                var started = await context.TalkingInAsync(StickyChannel.LocalId).ConfigureAwait(false);
                var during = await context.Host.StickyAsync().ConfigureAwait(false);
                await context.TypeAsync("/s").ConfigureAwait(false);
                var stopped = await context.TalkingInAsync(null).ConfigureAwait(false);
                var after = await context.Host.StickyAsync().ConfigureAwait(false);
                var lines = await context.CountPrintedAsync(mark, line => line.Tag == Local).ConfigureAwait(false);
                return StepResult.All((started, "/lgl alone didn't start it"), (during.InfoBar != null, "the info bar didn't show [Local]"), (stopped, "/s alone didn't end it"),
                    (after.InfoBar == null && after.ChatInputLabel != Local, "a label still says [Local]"), (lines == 0, "a start or stop line was said with it off"));
            }, types: true),
            CarryOn("Type /lgl to talk in local chat, then in /xlplugins turn LookingGlass off, then on again. The self-test carries on by itself.", "35"),
            Ask("Turned off, it said so", "35", new GuidedPrompt(
                "When LookingGlass was turned off, did game chat say \"Stopped talking in [Local]: LookingGlass was turned off.\", and the chat box name Say again?",
                PromptKind.YesNo) { Short = "turned off" }),
            Auto("Talking in [Local] for the relog", "35", async context => StepResult.Expect(await context.TalkInLocalAsync().ConfigureAwait(false), "couldn't start talking in [Local]"),
                types: true),
            CarryOn(Relog, "35"),
            Auto("Nothing on after the relog", "35", async context => {
                var sticky = await context.Host.StickyAsync().ConfigureAwait(false);
                return StepResult.All((sticky.ChannelId == null, "still talking in [Local] after the relog"), (sticky.InfoBar == null, "the info bar still shows [Local]"));
            }),
        ],
    };

    private static Func<Task<WatchUpdate>> StoppedWatch(StepContext context) =>
        StepContextExtensions.Until(async () => (await context.Host.StickyAsync().ConfigureAwait(false)).ChannelId == null, "still talking in [Local]", "stopped talking in [Local]");

    // ---------------------------------------------------------------- local-chat 37-39

    private static SelfTestCheck Refused() => new() {
        Id = "local-chat-37-39", Title = "Refused to start, and the privacy notice", Steps = "local-chat 37-39", Mode = CheckMode.AutomaticAndGuided, Needs = Typing,
        StepList = [
            Auto("Not connected: refused", "37", async context => {
                await context.StopTalkingAsync().ConfigureAwait(false);
                await context.DisconnectAsync().ConfigureAwait(false);
                var mark = context.Host.Mark();
                await context.TypeAsync(LocalChat.Command, whileDisconnected: true).ConfigureAwait(false);
                var said = await context.NoticeAsync(mark, StickyMessages.NotConnected(Local)).ConfigureAwait(false);
                var sticky = await context.Host.StickyAsync().ConfigureAwait(false);
                var back = await context.ConnectAsync().ConfigureAwait(false);
                return StepResult.All((said != null, "no \"Can't switch to [Local]: not connected to LookingGlass.\""), (sticky.ChannelId == null && sticky.InfoBar == null, "it started anyway"),
                    (back, "couldn't connect again"));
            }, types: true),
            Auto("Withdrawn: it asks, and nothing starts", "38", async context => {
                await context.ChangeSettingAsync(SelfTestSetting.LocalPrivacyAccepted, false).ConfigureAwait(false);
                var mark = context.Host.Mark();
                await context.TypeAsync(LocalChat.Command).ConfigureAwait(false);
                var said = await context.NoticeAsync(mark, LocalChatWords.PrivacyAskedToTalk).ConfigureAwait(false);
                var open = await context.WaitUntilAsync(() => context.Host.PrivacyWindowOpenAsync()).ConfigureAwait(false);
                var sticky = await context.Host.StickyAsync().ConfigureAwait(false);
                context.State["mark"] = context.Host.Mark();
                return StepResult.All((said != null, "it didn't say to accept first"), (open, "the privacy window didn't open"), (sticky.ChannelId == null, "it started anyway"));
            }, types: true),
            Ask("Accept", "38", context => Task.FromResult(new GuidedPrompt("Click Accept and use local chat.") {
                Short = "Accept",
                Watch = StepContextExtensions.Until(() => context.SettingIsAsync(SelfTestSetting.LocalPrivacyAccepted), "not accepted yet", "accepted"),
            })),
            Auto("Told to type /lgl again, and it starts", "38", async context => {
                var said = await context.NoticeAsync((long) context.State["mark"], LocalChatWords.PrivacyAcceptedFor(LocalPrivacyAsked.ByTalking, talkingInLocal: false)!).ConfigureAwait(false);
                await context.TypeAsync(LocalChat.Command).ConfigureAwait(false);
                var started = await context.TalkingInAsync(StickyChannel.LocalId).ConfigureAwait(false);
                return StepResult.All((said != null, "it didn't say to type /lgl again"), (started, "/lgl alone didn't start it after accepting"));
            }, types: true),
            Auto("Withdrawn while talking: it stops, and typing goes to Say", "39", async context => {
                if (!await context.TalkInLocalAsync().ConfigureAwait(false)) {
                    return StepResult.Fail("couldn't start talking in [Local]");
                }

                await context.ChangeSettingAsync(SelfTestSetting.SayWhenTalking, false).ConfigureAwait(false);
                var mark = context.Host.Mark();
                await context.ChangeSettingAsync(SelfTestSetting.LocalPrivacyAccepted, false).ConfigureAwait(false);
                var stopped = await context.TalkingInAsync(null).ConfigureAwait(false);
                var said = await context.NoticeAsync(mark, StickyMessages.Ended(Local, StickyEnd.PrivacyWithdrawn)).ConfigureAwait(false);
                var marker = context.NextMarker();
                await context.TypeAsync(marker).ConfigureAwait(false);
                var inSay = await context.InGameChatAsync(mark, line => StepContextExtensions.SaidInSay(line, marker)).ConfigureAwait(false);
                return StepResult.All((stopped, "withdrawing didn't stop it"), (said != null, "no \"Stopped talking in [Local]: you withdrew the privacy notice.\""),
                    (inSay != null, "typing didn't go to Say afterwards"));
            }, types: true),
            Ask("Withdrawn from its window", "39", async context => {
                await context.TalkInLocalAsync().ConfigureAwait(false);
                await context.Host.OpenPrivacyWindowAsync().ConfigureAwait(false);
                return new GuidedPrompt("In the local chat window that opened, click Withdraw.") {
                    Short = "Withdraw",
                    Watch = StepContextExtensions.Until(async () => (await context.Host.StickyAsync().ConfigureAwait(false)).ChannelId == null
                        && !await context.SettingIsAsync(SelfTestSetting.LocalPrivacyAccepted).ConfigureAwait(false), "still talking in [Local]", "stopped, and withdrawn"),
                };
            }),
        ],
    };

    // ---------------------------------------------------------------- local-chat 40-41

    private static SelfTestCheck LogAndWords() => new() {
        Id = "local-chat-40-41", Title = "Log and plain words", Steps = "local-chat 40-41", Mode = CheckMode.Automatic,
        StepList = [
            Auto("/xllog's lines name local chat, never a name or a line", "40", context => {
                var counts = context.Host.LogCounts();
                if (counts.StickyLocalLines == 0) {
                    return Task.FromResult(StepResult.Skip("needs the local chat checks above"));
                }

                return Task.FromResult(StepResult.Expect(counts.ForbiddenHits == 0, "/xllog held a name, a channel's name or a test line"));
            }),
            Auto("Everything said is in plain words", "41", async context => {
                var names = (await context.Host.ChannelsAsync().ConfigureAwait(false)).Select(channel => channel.Name).OfType<string>().ToList();
                var simple = context.Host.PrintedSince(0).Where(line => line.Kind == TapKind.Notice && !line.Advanced).ToList();
                if (simple.Count == 0) {
                    return StepResult.Skip("nothing was said in simple mode");
                }

                var jargon = simple.Select(line => names.Aggregate(line.Text, (text, name) => text.Replace(name, "", StringComparison.Ordinal)))
                    .Any(text => SelfTestWords.JargonIn(text).Count > 0);
                return StepResult.Expect(!jargon, "a line said in simple mode had a technical word");
            }),
        ],
    };
}
