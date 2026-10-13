using LookingGlass.Core.Client;
using static LookingGlass.Core.SelfTest.SelfTestSteps;

namespace LookingGlass.Core.SelfTest.Suites;

/// <summary>
/// "Right-click invites" (one player): context-invites-checklist.md 7 to 11, 15 and 18. The runner checks what the menus
/// would offer for each kind of thing (from the same rules the menus use); the menus themselves are the tester's to look
/// at. Inviting someone who isn't registered costs one lookup and sends nothing to them.
/// </summary>
public static class InvitesSuite {
    public const string Id = "invites";

    public static SelfTestSuite Create() => new(Id, "Right-click invites", [NotShown(), NoItem(), NotRegistered()]) {
        Changes = [SelfTestSetting.AdvancedMode],
        Planned = new Dictionary<SelfTestAction, int> { [SelfTestAction.Lookup] = 3, [SelfTestAction.Invite] = 2, [SelfTestAction.Reconnect] = 1, [SelfTestAction.CreateChannel] = 1 },
    };

    // ---------------------------------------------------------------- context-invites 7-9a

    private static SelfTestCheck NotShown() => new() {
        Id = "context-invites-7-9a", Title = "Where it must not show", Steps = "context-invites 7-9a", Mode = CheckMode.Guided,
        Needs = SelfTestNeeds.Connection | SelfTestNeeds.TestChannel,
        StepList = [
            Auto("The rules offer nothing but for another player", "7-9", async context => {
                await context.TestChannelAsync().ConfigureAwait(false);
                return StepResult.All(
                    (await context.Host.InviteOffersForAsync(RightClickKind.Yourself).ConfigureAwait(false) == 0, "your own name would get the invite item"),
                    (await context.Host.InviteOffersForAsync(RightClickKind.OneWordName).ConfigureAwait(false) == 0, "a one-word name would get the invite item"),
                    (await context.Host.InviteOffersForAsync(RightClickKind.ItemMenu).ConfigureAwait(false) == 0, "an item's menu would get the invite item"),
                    (await context.Host.InviteOffersForAsync(RightClickKind.AnotherPlayer).ConfigureAwait(false) > 0, "another player wouldn't get the invite item"));
            }),
            Ask("Your own name and character", "7", new GuidedPrompt(
                "Right-click your own name (in chat, or your character): is there no Invite to LookingGlass?", PromptKind.YesNo) { Short = "yourself" }),
            Ask("Not players", "8", new GuidedPrompt(
                "Right-click what you have at hand that isn't a player: a minion, an NPC or retainer (at a summoning bell), a Trust or duty support NPC in the party list. " +
                "Is there no Invite to LookingGlass on any of them? (Skip if none are at hand.)", PromptKind.YesNo) { Short = "not players" }),
            Ask("Items", "9", new GuidedPrompt(
                "Right-click an item in your inventory, and an item link in chat: is there no Invite to LookingGlass?", PromptKind.YesNo) { Short = "items" }),
            Auto("The rest", "9a", _ => Task.FromResult(StepResult.Pass("the Free Company login line in ChatTwo (9a)"))),
        ],
    };

    // ---------------------------------------------------------------- context-invites 10-11

    private static SelfTestCheck NoItem() => new() {
        Id = "context-invites-10-11", Title = "No item when it can't work", Steps = "context-invites 10-11", Mode = CheckMode.Guided, Needs = SelfTestNeeds.Connection,
        StepList = [
            // One step: it connects again before it ends, whatever the answer (the next step waits for a connection).
            Guided("Disconnected: no item", "10", async context => {
                await context.DisconnectAsync().ConfigureAwait(false);
                try {
                    return await context.AskForResultAsync(new GuidedPrompt(
                        "LookingGlass is disconnected for a moment. Right-click a player (in your friends list, or a name in chat): is Invite to LookingGlass gone?",
                        PromptKind.YesNo) { Short = "disconnected" }).ConfigureAwait(false);
                } finally {
                    await context.ConnectAsync().ConfigureAwait(false);
                }
            }),
            Ask("Connected: it is back", "10", new GuidedPrompt("Connected again. Right-click the same player: is Invite to LookingGlass back?", PromptKind.YesNo) { Short = "connected" }),
            Auto("The rest", "11", _ => Task.FromResult(StepResult.Pass("a character with no channels of its own (11)"))),
        ],
    };

    // ---------------------------------------------------------------- context-invites 15, 18

    private static SelfTestCheck NotRegistered() => new() {
        Id = "context-invites-15-18", Title = "Inviting someone who isn't registered", Steps = "context-invites 15, 18 (that part)", Mode = CheckMode.AutomaticAndGuided,
        Needs = SelfTestNeeds.Connection | SelfTestNeeds.TestChannel,
        StepList = [
            Guided("Who to try", "15", async context => {
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
            Auto("The rest", "18", _ => Task.FromResult(StepResult.Pass("the same from ChatTwo's menu (18, see With ChatTwo)"))),
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