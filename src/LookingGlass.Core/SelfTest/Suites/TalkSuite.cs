using LookingGlass.Core.Client;
using static LookingGlass.Core.SelfTest.SelfTestSteps;

namespace LookingGlass.Core.SelfTest.Suites;

/// <summary>
/// "Talking in a channel and local chat" (one player): local-chat-checklist.md steps 1 to 3 and 29 (3a to 3c, 30, 32 to 33
/// but /p, and 34 to 41 passed in the owner's second run, 2026-10-13, and are no longer offered; 31 needs a stranger, and
/// /p brb a partner: see the two-player suites), judged by the runner from sticky mode's state and what the plugin printed,
/// with the steps only the tester can do guided.
/// Alone in a private place, local chat always finds nobody near, so nothing is ever sent to anyone.
/// </summary>
public static class TalkSuite {
    public const string Id = "talk";

    private const string Local = LocalChat.Tag;

    public static SelfTestSuite Create() => new(Id, "Talking in a channel and local chat", [Free(), Starting()]) {
        Changes = [SelfTestSetting.LocalPrivacyAccepted, SelfTestSetting.LocalTalkNoteShown, SelfTestSetting.SayWhenTalking],
        Note = "1 reload",
    };

    private const SelfTestNeeds Typing = SelfTestNeeds.Connection | SelfTestNeeds.Typing | SelfTestNeeds.LocalChat;

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
            CarryOn("In /xlplugins, turn LookingGlass off. Type /lgl: the game should say there is no such command. Then turn LookingGlass on again. The self-test carries on by itself.", "1",
                types: true),
            Ask("The game had no /lgl", "1", new GuidedPrompt("While LookingGlass was off, did the game say /lgl isn't a command?", PromptKind.YesNo) { Short = "off and on" }),
            Ask("The Colour \"?\" in Settings", "3", async context => {
                await context.Host.ShowSettingsAsync(true).ConfigureAwait(false);
                return new GuidedPrompt("In Settings, under Local chat, click the ? after Colour. Does its bubble say that /lgl message talks to friends near you, and /lgl on its own keeps talking there until /s?",
                    PromptKind.YesNo) { Short = "Colour ?" };
            }),
        ],
    };

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
                var environment = await context.EnvironmentAsync().ConfigureAwait(false);
                var colour = SelfTestValues.Colour((await context.Host.ReadSettingAsync(SelfTestSetting.LocalChatColour).ConfigureAwait(false)).ToString()) ?? ColouredText.DefaultTag;
                // The game renames its chat box a frame or more later, and the info bar follows: waited for, a few seconds at most.
                return await context.SettledAsync(async () => {
                    var sticky = await context.Host.StickyAsync().ConfigureAwait(false);
                    return StepResult.All(
                        (talking, "/lgl alone didn't start talking in [Local]"),
                        (started is { Tone: NoticeTone.Info, Tag: Local }, "no blue \"Now talking in [Local].\""),
                        (note != null, "the once-only line wasn't said"),
                        (!environment.ChatInputReadable || sticky.ChatInputLabel == Local, "the chat input doesn't name its channel [Local]"),
                        (sticky.InfoBar?.Contains($"LG {Local}", StringComparison.Ordinal) == true, "the server info bar doesn't show \"LG [Local]\""),
                        (sticky.ShownColour == colour, "the labels aren't in local chat's colour"));
                }).ConfigureAwait(false);
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
}
