using static LookingGlass.Core.SelfTest.SelfTestSteps;

namespace LookingGlass.Core.SelfTest.Suites;

/// <summary>
/// "Right-click invites" (one player): context-invites-checklist.md 11, with another of the tester's characters. 7 to 9 and
/// 10 passed in the owner's first run (2026-10-13); 9a and 15 (and 18 but ChatTwo's menu) in the owner's fifth run
/// (2026-10-13, plugin 0.2.19): no longer offered. Inviting someone who isn't registered from ChatTwo's menu (18) is in the
/// ChatTwo suite. The menus are the tester's to look at.
/// </summary>
public static class InvitesSuite {
    public const string Id = "invites";

    public static SelfTestSuite Create() => new(Id, "Right-click invites", [NoChannelsOfYourOwn()]) {
        Note = "needs another of your characters",
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
            AskAcrossRelog("Right-click a player there", "11", new GuidedPrompt(
                "Come in with that character (the self-test waits, and does nothing meanwhile), right-click a player: is there no Invite to LookingGlass? " +
                "Then come back in with this character, and answer.", PromptKind.YesNo) { Short = "no channels of its own" }),
        ],
    };
}
