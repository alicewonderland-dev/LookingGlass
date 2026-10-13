using System.Text.RegularExpressions;
using LookingGlass.Core.Client;

namespace LookingGlass.Core.SelfTest;

/// <summary>
/// The self-test's own words (docs/self-test-plan.md, "Words"): short and plain in both modes, and the same in both but
/// where a check is about something advanced mode shows. Simple mode never says key, fingerprint, log, signature and the
/// like ("encrypted" is allowed: owner, 2026-10-10). The checklists' own names and step numbers are kept in the report,
/// since that is how the owner files results.
/// </summary>
public static partial class SelfTestWords {
    /// <summary>The longest a button or a result word may be.</summary>
    public const int MaxButtonLength = 28;

    /// <summary>The longest a reason (why a step failed or was skipped) may be: one short line.</summary>
    public const int MaxReasonLength = 120;

    // ---- The window
    public const string WindowTitle = "LookingGlass self-test";
    public const string ReportTitle = "LookingGlass self-test: report";
    public const string OpenButton = "Self-test...";
    public const string Start = "Start";
    public const string StopAndCleanUp = "Stop and clean up";
    public const string NeedsYou = "Needs you";
    public const string Done = "Done";
    public const string DidntWork = "It didn't work";
    public const string SkipStep = "Skip this step";
    public const string Yes = "Yes";
    public const string No = "No";
    public const string CopyReport = "Copy report";
    public const string RunFailedAgain = "Run the failed ones again";
    public const string CheckAgain = "Check again";
    public const string CarryOn = "Carry on";
    public const string Close = "Close";
    public const string TwoPlayers = "Two players...";
    public const string WhichOnes = "Which ones?";
    public const string Remove = "Remove";
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
    public const string StoppedWord = "stopped";
    public const string NoteLabel = "Note (optional, no names)";

    /// <summary>The start screen's consent: what a run does, before anything happens.</summary>
    public static readonly IReadOnlyList<string> Consent = [
        "Makes a test channel, only for this test, and sends test lines in it, slowly. Your own channels aren't used.",
        "Types some test lines for you, such as \"/s LG self-test 4, please ignore\". Some go to Say, where only people in this room could see them.",
        "Changes some settings for a while, and puts them back at the end, even if the game closes.",
        "Closes your channel windows for a while, and opens them again where they were.",
        "Don't type in chat while it runs, unless it asks you to.",
    ];

    public const string Intro = "Checks as much of the testing list as it can by itself, and tells you what to do for the rest.";
    public const string NoNamesInReport = "The report has no names and nothing anyone typed.";
    public const string StayHere = "Stay here until it's done. You can stop at any time.";
    public const string HouseVisitors = "In a house, people who come in could see a test line in Say. It waits while anyone else is near.";
    public const string HandChanges = "If you change one of these settings during the run, the end puts back what it was before.";
    public const string ClosingDoesntStop = "Closing this window doesn't stop the run. Type /lgdebug test to see it again.";
    public const string TwoPlayersLater = "Two-player checks need a partner. They are coming in a later version.";
    public const string NoteReminder = "Don't put names in a note: it is copied into the report as you type it.";
    public const string LeftoverHeading = "Left over from an earlier test?";
    public const string LeftoverExplained = "Channels named like a self-test's that no run knows about. Remove one only if you're sure it is a test channel.";

    /// <summary>"The self-test only runs in a private place": the refusal, with the places that count.</summary>
    public static readonly IReadOnlyList<string> PrivatePlaces = [
        "an inn room",
        "inside an apartment (not the lobby)",
        "inside a house: any floor, private chambers, the workshop",
    ];

    public const string OnlyInPrivate = "The self-test only runs in a private place, so its test lines can't reach other players:";

    /// <summary>Paused because the player left the private place.</summary>
    public const string PausedLeft = "Paused: you left the private place. Go back inside to carry on, or stop.";

    /// <summary>Waiting because someone else is near in a house or apartment.</summary>
    public const string WaitingSomeoneNear = "Waiting: someone else is nearby, and could see a test line in Say. It carries on when nobody else is near.";

    /// <summary>Paused while disconnected.</summary>
    public const string PausedDisconnected = "Paused: not connected to LookingGlass. It carries on once connected again.";

    /// <summary>Waiting for a loading screen, or for the place to settle.</summary>
    public const string WaitingPlace = "Checking where you are...";

    /// <summary>Waiting for the tester to log out and in, or turn the plugin off and on.</summary>
    public const string WaitingToCarryOn = "Waiting for you. The self-test carries on by itself afterwards.";

    /// <summary>The blue line after a run that didn't finish, at the next start.</summary>
    public const string InterruptedRun = "A LookingGlass self-test didn't finish. Your settings are as they were, and its test channel was removed.";

    /// <summary>The same, when the test channel couldn't be removed.</summary>
    public const string InterruptedRunChannelLeft =
        "A LookingGlass self-test didn't finish. Your settings are as they were. Its test channel couldn't be removed: see /lgdebug test.";

    /// <summary>Why a run can't start for a while: the last hour's runs used most of what a run may use.</summary>
    public static string WaitBeforeStart(TimeSpan wait) =>
        $"Wait about {Math.Max(1, (int) Math.Ceiling(wait.TotalMinutes))} minutes: the last run used most of what the self-test may use in an hour.";

    /// <summary>Names a place kind for the window and the report (never the place's own name).</summary>
    public static string Place(PlaceKind kind) => kind switch {
        PlaceKind.InnRoom => "an inn room",
        PlaceKind.House => "inside a house",
        PlaceKind.Chambers => "private chambers",
        PlaceKind.Workshop => "a company workshop",
        PlaceKind.Apartment => "inside an apartment",
        PlaceKind.ApartmentLobby => "an apartment lobby",
        PlaceKind.Outdoor => "a ward or yard",
        PlaceKind.Loading => "a loading screen",
        PlaceKind.Unknown => "a place the self-test can't read",
        _ => "somewhere public",
    };

    // ---- Why a step failed or was skipped: fixed words, never what anyone typed.
    public const string YouSkipped = "you chose Skip";
    public const string YouSaidNo = "you said it didn't work";
    public const string NeedsChatTwo = "needs ChatTwo, which is off";
    public const string NeedsChatTwoOff = "needs ChatTwo to be off";
    public const string NeedsPartner = "needs a partner (coming in a later version)";
    public const string NeedsStranger = "needs a stranger";
    public const string NeedsAnotherCharacter = "needs another of your characters";
    public const string NeedsStrikingDummy = "needs a striking dummy";
    public const string NeedsConnection = "needs a connection to LookingGlass";
    public const string NeedsChannelRoom = "you have too many channels for a test channel (at most 47)";
    public const string NeedsGateHooks = "the game's chat box couldn't be watched in this game version";
    public const string HistoryWasYours = "only runs when the chat history was off and empty before the test (it would cost you history)";
    public const string ServerSlowDown = "stopped: the server asked to slow down";
    public const string StoppedByYou = "stopped: you stopped the run";
    public const string StoppedLeft = "stopped: you left the private place";
    public const string RunSpent = "skipped: the run used all it may of this";
    public const string SomethingWentWrong = "something went wrong in the self-test itself";
    public const string TimedOut = "didn't happen in time";
    public const string NotSay = "the game's chat channel wasn't Say, so nothing was typed";
    public const string NotPrivate = "you weren't in a private place, so nothing was typed";
    public const string Disconnected = "stopped: not connected for five minutes";
    public const string GaveUpWaiting = "stopped: the relog or reload didn't come";

    // ---- Clean-up
    public const string CleanUpTitle = "Clean-up";

    /// <summary>The report's last line: what the clean-up did.</summary>
    public static string CleanUp(int channelsLeft, int channelsNotLeft, int settings, int settingsFailed, bool windowsBack, bool historyCleared) {
        var parts = new List<string>();
        if (channelsLeft > 0) {
            parts.Add(channelsLeft == 1 ? "test channel left (deleted)" : $"{channelsLeft} test channels left (deleted)");
        }

        if (channelsNotLeft > 0) {
            parts.Add(channelsNotLeft == 1 ? "a test channel couldn't be left" : $"{channelsNotLeft} test channels couldn't be left");
        }

        parts.Add(settings == 1 ? "1 setting put back" : $"{settings} settings put back");
        if (settingsFailed > 0) {
            parts.Add($"{settingsFailed} couldn't be put back");
        }

        if (windowsBack) {
            parts.Add("your channel windows opened again");
        }

        if (historyCleared) {
            parts.Add("the test's lines removed from the chat history");
        }

        return $"{CleanUpTitle}: {string.Join("; ", parts)}.";
    }

    /// <summary>Every fixed text above, for the tests.</summary>
    public static IReadOnlyList<string> All() => [
        WindowTitle, ReportTitle, OpenButton, Start, StopAndCleanUp, NeedsYou, Done, DidntWork, SkipStep, Yes, No, CopyReport, RunFailedAgain,
        CheckAgain, CarryOn, Close, TwoPlayers, WhichOnes, Remove, Passed, Failed, Skipped, StoppedWord, NoteLabel, .. Consent, Intro,
        NoNamesInReport, StayHere, HouseVisitors, HandChanges, ClosingDoesntStop, TwoPlayersLater, NoteReminder, LeftoverHeading,
        LeftoverExplained, .. PrivatePlaces, OnlyInPrivate, PausedLeft, WaitingSomeoneNear, PausedDisconnected, WaitingPlace,
        WaitingToCarryOn, InterruptedRun, InterruptedRunChannelLeft, WaitBeforeStart(TimeSpan.FromMinutes(20)),
        .. Enum.GetValues<PlaceKind>().Select(Place), .. Reasons(), CleanUp(1, 1, 7, 1, true, true),
    ];

    /// <summary>Every reason, for the tests (lengths).</summary>
    public static IReadOnlyList<string> Reasons() => [
        YouSkipped, YouSaidNo, NeedsChatTwo, NeedsChatTwoOff, NeedsPartner, NeedsStranger, NeedsAnotherCharacter, NeedsStrikingDummy,
        NeedsConnection, NeedsChannelRoom, NeedsGateHooks, HistoryWasYours, ServerSlowDown, StoppedByYou, StoppedLeft, RunSpent,
        SomethingWentWrong, TimedOut, NotSay, NotPrivate, Disconnected, GaveUpWaiting,
    ];

    /// <summary>The words simple mode never uses (the plain-language tests' list): to check what the plugin printed during a run.</summary>
    [GeneratedRegex(@"\b(fingerprints?|keys?|epochs?|re-?key\w*|forks?|forked|logs?|pinned|pinning|signatures?|signed(?!\s+(?:in|out)\b)|cipher\w*|decrypt\w*|sealed|sealing)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Jargon();

    /// <summary>The technical words in a line simple mode printed, if any (names in it left out by the caller).</summary>
    public static IReadOnlyList<string> JargonIn(string text) => Jargon().Matches(text).Select(match => match.Value).ToList();

    /// <summary>Wording for something a check is about that advanced mode names differently.</summary>
    public static readonly Wording CheckCode = new(NoticeKind.General, "log head (check code)", "check code");
}
