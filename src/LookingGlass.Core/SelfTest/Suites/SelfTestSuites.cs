namespace LookingGlass.Core.SelfTest.Suites;

/// <summary>
/// Every one-player suite, in the order the start screen lists and a run runs them (docs/self-test-plan.md, "Coverage").
/// The two-player suites (group 2) come with a partner's runner, later: the start screen says so.
/// </summary>
public static class SelfTestSuites {
    /// <summary>
    /// The suites, made afresh (their checks hold no state between runs). Talking in a channel first (it needs the least set
    /// up), Colours after Windows (its last check leaves the run's third test channel), the history after both (it may turn
    /// the history on), Settings and ChatTwo last.
    /// </summary>
    public static IReadOnlyList<SelfTestSuite> All() => [
        TalkSuite.Create(),
        WindowsSuite.Create(),
        ColoursSuite.Create(),
        InvitesSuite.Create(),
        PartyFinderSuite.Create(),
        HistorySuite.Create(),
        SettingsSuite.Create(),
        ChatTwoSuite.Create(),
    ];
}
