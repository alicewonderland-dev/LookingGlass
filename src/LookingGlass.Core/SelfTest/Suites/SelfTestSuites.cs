namespace LookingGlass.Core.SelfTest.Suites;

/// <summary>
/// The suites, in the order the start screen lists and a run runs them (docs/self-test-plan.md, "Coverage"): the
/// one-player suites; the two-player suites a host runs with a partner (group 2); and the partner's own.
/// </summary>
public static class SelfTestSuites {
    /// <summary>
    /// The one-player suites, made afresh (their checks hold no state between runs). Colours after Windows (its last check
    /// leaves the run's third test channel), the history after both (it may turn the history on), ChatTwo last. (Settings 1-9
    /// and device-notices 1 passed in the owner's first run, 2026-10-13; talking in a channel, local-chat 1-3 and 29, in the
    /// owner's fifth run, plugin 0.2.19: those suites are gone.)
    /// </summary>
    public static IReadOnlyList<SelfTestSuite> All() => [
        WindowsSuite.Create(),
        ColoursSuite.Create(),
        InvitesSuite.Create(),
        PartyFinderSuite.Create(),
        HistorySuite.Create(),
        ChatTwoSuite.Create(),
    ];

    /// <summary>
    /// The two-player suites a host chooses from (pairing always first, then local chat, which needs the least of the
    /// channels; the windows before colours and invites, which add the run's second and third test channels; the history
    /// after them; ChatTwo with a party last).
    /// </summary>
    public static IReadOnlyList<SelfTestSuite> Pairs() => [
        LocalTogetherSuite.Create(),
        PairMessagesSuite.Create(),
        PairWindowsSuite.Create(),
        PairColoursSuite.Create(),
        PairInvitesSuite.Create(),
        PairPartyFinderSuite.Create(),
        PairHistorySuite.Create(),
        PairChatTwoSuite.Create(),
    ];

    /// <summary>A host's run: pairing first, then the two-player suites chosen.</summary>
    public static IReadOnlyList<SelfTestSuite> ForHost(IEnumerable<SelfTestSuite> chosen) => [PairingSuite.Create(), .. chosen.Where(suite => suite.Id != PairingSuite.Id)];

    /// <summary>Every suite a run file may name, to carry a run on after a reload.</summary>
    public static IReadOnlyList<SelfTestSuite> Every() => [.. All(), PairingSuite.Create(), .. Pairs(), PartnerSuite.Create()];
}
