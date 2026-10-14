using LookingGlass.Core.Client;
using static LookingGlass.Core.SelfTest.SelfTestSteps;

namespace LookingGlass.Core.SelfTest.Suites;

/// <summary>
/// "Right-click invites" (one player): context-invites-checklist.md 9a, 11, 15 and 18 (7 to 9 and 10 passed in the owner's
/// first run, 2026-10-13, and are no longer offered). The menus are the tester's to look at. Inviting someone who isn't
/// registered costs one lookup and sends nothing to them.
/// </summary>
public static class InvitesSuite {
    public const string Id = "invites";

    public static SelfTestSuite Create() => new(Id, "Right-click invites", [FcLoginLine(), NoChannelsOfYourOwn(), NotRegistered()]) {
        Changes = [SelfTestSetting.AdvancedMode],
        Planned = new Dictionary<SelfTestAction, int> { [SelfTestAction.Lookup] = 3, [SelfTestAction.Invite] = 2, [SelfTestAction.CreateChannel] = 1 },
    };

    // ---------------------------------------------------------------- context-invites 9a

    private static SelfTestCheck FcLoginLine() => new() {
        Id = "context-invites-9a", Title = "Where it must not show: an FC login line in ChatTwo", Steps = "context-invites 9a", Mode = CheckMode.Guided,
        Needs = SelfTestNeeds.Connection | SelfTestNeeds.ChatTwo,
        StepList = [
            SetupGuided("A Free Company member logging in or out", "9a", async context => await context.AskAsync(new GuidedPrompt(
                    "Is there a Free Company member's login or logout line in ChatTwo (\"... has logged in.\"), or will one come soon?", PromptKind.YesNo)).ConfigureAwait(false) is GuidedAnswer.Yes
                ? StepResult.Pass()
                : StepResult.SkipCheck("needs a Free Company member's login or logout line in ChatTwo")),
            Ask("The login line, and an ordinary line", "9a", new GuidedPrompt(
                "In ChatTwo, right-click the name in that login or logout line: is there no Invite to LookingGlass? Then right-click the same player's name in an ordinary " +
                "line of theirs, if there is one: is it there?", PromptKind.YesNo) { Short = "FC login line" }),
        ],
    };

    // ---------------------------------------------------------------- context-invites 11

    private static SelfTestCheck NoChannelsOfYourOwn() => new() {
        Id = "context-invites-11", Title = "No item when it can't work: no channels of your own", Steps = "context-invites 11", Mode = CheckMode.Guided,
        Needs = SelfTestNeeds.Connection,
        StepList = [
            SetupGuided("Another character with no channels of its own", "11", async context => await context.AskAsync(new GuidedPrompt(
                    "Do you have another character, registered with LookingGlass, that isn't admin or moderator of any channel (or has none)?", PromptKind.YesNo))
                .ConfigureAwait(false) is GuidedAnswer.Yes
                ? StepResult.Pass()
                : StepResult.SkipCheck(SelfTestWords.NeedsAnotherCharacter)),
            Ask("Right-click a player there", "11", new GuidedPrompt(
                "Come in with that character (the self-test waits, and does nothing meanwhile), right-click a player: is there no Invite to LookingGlass? " +
                "Then come back in with this character, and answer.", PromptKind.YesNo) { Short = "no channels of its own" }),
        ],
    };

    // ---------------------------------------------------------------- context-invites 15, 18

    private static SelfTestCheck NotRegistered() => new() {
        Id = "context-invites-15-18", Title = "Inviting someone who isn't registered", Steps = "context-invites 15, 18 (that part)", Mode = CheckMode.AutomaticAndGuided,
        Needs = SelfTestNeeds.Connection | SelfTestNeeds.TestChannel,
        StepList = [
            SetupGuided("Who to try", "15", async context => {
                var (answer, input) = await context.AskForInputAsync(new GuidedPrompt(
                    "Type the name@world of someone you know who doesn't use LookingGlass (for example: Alice Liddell@Twintania), then press Done. " +
                    "LookingGlass looks them up; nothing is ever sent to them, and their name stays out of the report.") { Input = "Name@World" }).ConfigureAwait(false);
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
            Auto("They aren't registered (looked up first)", "15", async context => {
                if (Ended(context)) {
                    return StepResult.Skip("no name@world was given");
                }

                // Never an invite to someone registered: a solo run sends nothing to anyone.
                var registered = await context.ServerAsync(SelfTestAction.Lookup,
                    () => context.Host.IsRegisteredAsync((string) context.State["name"], (string) context.State["world"])).ConfigureAwait(false);
                if (registered != false) {
                    context.State["ended"] = true;
                    return StepResult.Skip(registered == true ? SelfTestWords.Registered : "they couldn't be looked up");
                }

                return StepResult.Pass();
            }),
            Auto("Simple mode: the whole line", "15", context => InviteAsync(context, advanced: false)),
            Auto("Advanced mode: it ends at \"on this server.\"", "15", context => InviteAsync(context, advanced: true)),
            Rest("18", "the same from ChatTwo's menu (18, see With ChatTwo)"),
        ],
    };

    private static bool Ended(StepContext context) => context.State.TryGetValue("ended", out var ended) && (bool) ended;

    private static async Task<StepResult> InviteAsync(StepContext context, bool advanced) {
        if (Ended(context)) {
            return StepResult.Skip(SelfTestWords.Registered);
        }

        await context.ChangeSettingAsync(SelfTestSetting.AdvancedMode, advanced).ConfigureAwait(false);
        var channel = await context.TestChannelAsync().ConfigureAwait(false);
        var mark = context.Host.Mark();
        // The invite looks them up again (a lookup that failed isn't remembered): both paced; a refusal stops the run.
        await context.ServerAsync(SelfTestAction.Lookup, () => Task.CompletedTask).ConfigureAwait(false);
        await context.ServerAsync(SelfTestAction.Invite, () => context.Host.InviteAsync(channel, (string) context.State["name"], (string) context.State["world"]))
            .ConfigureAwait(false);
        var line = await context.PrintedAsync(mark, line => line.Kind == TapKind.Notice && line.Text.StartsWith("Couldn't invite", StringComparison.Ordinal)
            || line.Text.StartsWith("Invited", StringComparison.Ordinal), TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (line == null) {
            return StepResult.Fail("no line said how the invite went");
        }

        if (line.Text.StartsWith("Invited", StringComparison.Ordinal)) {
            // Registered after all (since the lookup): no second invite; the clean-up disbands the channel, invite and all.
            context.State["ended"] = true;
            return StepResult.Skip(SelfTestWords.Registered);
        }

        return StepResult.All((line.Tone == NoticeTone.Info, "the line wasn't blue"),
            (line.Text.Contains("isn't registered with LookingGlass on this server", StringComparison.Ordinal), "the line didn't say they aren't registered"),
            (advanced ? line.Text.EndsWith("on this server.", StringComparison.Ordinal) : line.Text.Contains("install it and register", StringComparison.Ordinal),
                advanced ? "advanced mode's line didn't end at \"on this server.\"" : "simple mode's line didn't say what they need to do"));
    }
}