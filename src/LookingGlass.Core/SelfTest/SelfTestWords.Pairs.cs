namespace LookingGlass.Core.SelfTest;

/// <summary>The two-player words (docs/self-test-plan.md, "Mockups": the host and the partner): never a name.</summary>
public static partial class SelfTestWords {
    // ---- The host's screen
    public const string TwoPlayersTitle = "Two players";
    public const string BeAPartner = "Be a partner";
    public const string InviteAPartner = "Invite a partner";
    public const string UseMyTarget = "Use my target";
    public const string PartnerLabel = "Partner (Name Surname@World)";

    public const string HostIntro =
        "Your partner needs LookingGlass, and has to be in a private place too. For the local chat checks, both of you need to be inside the same house or apartment " +
        "(either one's), and on each other's friends list.";

    public const string HostStarts =
        "Start invites them to the test channel. They choose Be a partner in their self-test window, with your name and this code, and their plugin answers by itself.";

    /// <summary>The code the host tells the partner (by voice, or a tell).</summary>
    public static string TellThemCode(string code) => $"Tell them your name and this code: {code}";

    /// <summary>While pairing: how long it waits still.</summary>
    public static string WaitingForPartner(TimeSpan left) =>
        $"Waiting for your partner to join... {(int) Math.Max(0, left.TotalMinutes)}:{Math.Max(0, left.Seconds):00} left. (They choose Be a partner in their self-test window.)";

    // ---- The partner's screen
    public const string HostLabel = "Their name (Name Surname@World)";
    public const string CodeLabel = "Their code";

    /// <summary>What the partner's plugin does while it runs: the start screen's consent.</summary>
    public static readonly IReadOnlyList<string> PartnerConsent = [
        "Joins their test channel when they invite you. No other invite is accepted for them.",
        "Sends test lines there, or to local chat, when asked: slowly, at most 80.",
        "Says what it received: how many lines, which links, the check code, where a line showed up.",
        "Leaves and joins again when asked.",
        "It never types in game chat by itself: when a step needs that, it asks you. Some settings change for a while and are put back.",
    ];

    /// <summary>A line to type with /p (or into ChatTwo on Party) waits until the party is just the two testers.</summary>
    public const string WaitingPartyOfTwo = "Waiting: the party isn't just the two of you. It carries on once it is.";

    public const string PartnerWaiting = "Waiting for their invite. It is accepted only from them, to their test channel.";
    public const string PartnerJoined = "Joined their test channel. Answering their requests.";

    /// <summary>The partner's line while the host is quiet: what it waits for, and when it gives up.</summary>
    public static string PartnerWaitingOnHost(bool hostOnline) => hostOnline
        ? "Waiting for your partner's next request: they may be doing a step of their own. Nothing to do here meanwhile."
        : "Waiting for your partner: they show offline (relogging, perhaps). This stops by itself if they stay offline for 45 minutes.";

    /// <summary>The partner's run line: how many requests it answered.</summary>
    public static string PartnerAnswered(int answered) => answered == 1 ? "Answered 1 request." : $"Answered {answered} requests.";

    // ---- The partner's facts, as the host's window shows them
    public static string PartnerFacts(bool joined, bool? privatePlace, double? distance, bool? friends, bool? party, bool? chatTwo) {
        if (!joined) {
            return "Partner: not joined yet.";
        }

        static string YesNo(bool? value) => value switch { true => "yes", false => "no", null => "can't tell" };
        var near = distance is { } yalms ? $"yes ({(int) Math.Round(yalms)} yalms)" : "no (not in sight)";
        return $"Partner joined. In a private place: {YesNo(privatePlace)}. Near you: {near}. Friends: {YesNo(friends)}. In your party: {YesNo(party)}. " +
               $"ChatTwo on their side: {(chatTwo == true ? "on" : chatTwo == false ? "off" : "can't tell")}.";
    }

    // ---- What a partner's player is asked to do (each a fixed step; see PartnerAsk)
    public static string PartnerAskText(PartnerAsk ask, string marker) => ask switch {
        PartnerAsk.SayHello => $"Type in the game's chat box: /s {marker}  (Your partner right-clicks your name in their chat.)",
        PartnerAsk.SayHelloChatTwo => $"Type in ChatTwo's input: /s {marker}  (Your partner right-clicks your name in their ChatTwo.)",
        PartnerAsk.WalkAway => "Walk away from your partner, more than 20 yalms (another floor, or the far end of the house), then press Done.",
        PartnerAsk.ComeBack => "Come back next to your partner, then press Done.",
        PartnerAsk.OpenFriends => "Open your friends list (Social, Friend List), then close it again, and press Done.",
        PartnerAsk.JoinParty => "Your partner invites you to a party: accept it, then press Done. Nobody else may be in it.",
        PartnerAsk.LeaveParty => "Leave the party, then press Done.",
        PartnerAsk.ClickLink => "Click the party finder link your partner sent, once in game chat and once in the test channel's window. " +
                                "Does the Party Finder open on their recruitment (or say it has ended, if they ended it), with nothing else going wrong?",
        PartnerAsk.Relog => SelfTestSteps.Relog,
        _ => ask.ToString(),
    };

    // ---- Why, for two players: fixed words
    public const string PartnerSilent = "your partner didn't answer";
    public const string PartnerGone = "your partner has left the test";
    public const string PartnerDidntJoin = "your partner didn't join within two minutes";
    public const string PartnerNotRegistered = "your partner isn't registered with LookingGlass on this server";
    public const string NoPartnerName = "no partner name@world was given";
    public const string PartnerRefused = "your partner's plugin said no (past what it may do in a run)";
    public const string Meddling = "stopped: someone else wrote the test's own lines in its channel";
    public const string HostSilent = "stopped: no word from the other tester for an hour";
    public const string HostAway = "stopped: the other tester was offline for 45 minutes";
    public const string HostLeft = "stopped: the other tester's test channel is gone";
    public const string NoInvite = "no invite came from the other tester";
    public const string NeedsSamePlace = "needs both of you inside the same house or apartment";
    public const string NeedsFriends = "needs you on each other's friends list";
    public const string NeedsPartyOfTwo = "needs a party of just the two of you";
    public const string NeedsOtherDataCentre = "needs a partner on another data centre";
    public const string PartnerWasBlocked = "you had blocked your partner before the test";
    public const string PartnerNotPrivate = "your partner isn't in a private place";
    public const string PartnerNeedsChatTwo = "needs ChatTwo on your partner's side";

    private static IReadOnlyList<string> PairWords() => [
        TwoPlayersTitle, BeAPartner, InviteAPartner, UseMyTarget, PartnerLabel, HostIntro, HostStarts, TellThemCode("K7Q2"), WaitingForPartner(TimeSpan.FromSeconds(102)),
        HostLabel, CodeLabel, .. PartnerConsent, PartnerWaiting, WaitingPartyOfTwo, PartnerJoined, PartnerWaitingOnHost(true), PartnerWaitingOnHost(false), PartnerAnswered(1), PartnerAnswered(23),
        PartnerFacts(false, null, null, null, null, null), PartnerFacts(true, true, 12, true, false, false), PartnerFacts(true, null, null, null, null, null),
        .. Enum.GetValues<PartnerAsk>().Select(ask => PartnerAskText(ask, "LG self-test K7Q2 p4, please ignore")),
    ];

    private static IReadOnlyList<string> PairReasons() => [
        PartnerSilent, PartnerGone, PartnerDidntJoin, PartnerNotRegistered, NoPartnerName, PartnerRefused, Meddling, HostSilent, HostAway, HostLeft, NoInvite, NeedsSamePlace,
        NeedsFriends, NeedsPartyOfTwo, NeedsOtherDataCentre, PartnerWasBlocked, PartnerNotPrivate, PartnerNeedsChatTwo,
    ];
}
