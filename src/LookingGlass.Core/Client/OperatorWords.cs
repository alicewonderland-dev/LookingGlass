using System.Globalization;
using LookingGlass.Core.Crypto;
using LookingGlass.Protocol;

namespace LookingGlass.Core.Client;

/// <summary>
/// The operator panel's words (see "The plugin's operator panel" in docs/operator-tools-plan.md, whose mockups they follow):
/// the Operator section at the end of Settings, the notice in its place when this character's keys aren't the pinned ones,
/// the "Ban from server..." menu item and dialog, and the lines said in chat after a ban or lift. Only an operator sees any of
/// it; both modes' words, tested as the Settings words are (each "?" at most <see cref="SettingsWords.MaxHelpLength"/>
/// characters and three sentences, plain in simple mode).
/// </summary>
public static class OperatorWords {
    // ---- the section
    public const string Heading = "Operator";
    public const string Refresh = "Refresh";
    public const string FlaggedHeading = "Flagged";
    public const string InForceHeading = "Bans in force";
    public const string HistoryHeading = "Lifted or ended lately";
    public const string BanSomeone = "Ban someone";
    public const string BanSomeoneHint = "name@world or Lodestone ID";
    public const string BanButton = "Ban...";
    public const string LiftButton = "Lift...";
    public const string CopyPageAddress = "Copy the web page's address";
    public const string NobodyFlagged = "Nobody flagged.";
    public const string NoBans = "No bans in force.";
    public const string NoHistory = "Nothing lifted or ended lately.";
    public const string Asking = "Asking the server...";

    /// <summary>Under the lists.</summary>
    public const string AddressesShortened = "Addresses are shortened here. Address bans, and lifting them, are on the web page.";

    /// <summary>"Ban someone" was given something that is neither.</summary>
    public const string NotATarget = "Type a character as Name Surname@World, or its Lodestone ID (a number).";

    /// <summary>An operator action asked for while it can't be done.</summary>
    public static readonly Wording NotAvailable = Wording.Same("Not available: connect to the server as one of its operators first.");

    /// <summary>The Operator section's "?" (<see cref="SettingHelp.Operator"/>).</summary>
    public static readonly Wording Help = new(NoticeKind.General,
        "You see this because the server lists this character, with its keys, as an operator. Bans you make here are on the character, and are " +
        "posted to the alerts channel.",
        "You see this because the server lists this character as one of its operators. Bans you make here are on the character, and are posted " +
        "to the alerts channel.");

    /// <summary>The "?" of the notice in the section's place (<see cref="SettingHelp.OperatorKeys"/>).</summary>
    public static readonly Wording KeysHelp = new(NoticeKind.General,
        "Operators are tied to their keys, so someone who registers your character again elsewhere doesn't become one. Only the web page can restore it.",
        "Operators are tied to their LookingGlass setup, so someone who sets up your character elsewhere doesn't become one. Only the web page can restore it.");

    /// <summary>The "?" of the notice in the section's place while no keys are pinned yet (<see cref="SettingHelp.OperatorNotPinned"/>).</summary>
    public static readonly Wording NotPinnedHelp = new(NoticeKind.General,
        "Operators are tied to their keys, so someone who registers your character elsewhere doesn't become one. Approve these keys on the web page.",
        "Operators are tied to their LookingGlass setup, so someone who sets up your character elsewhere doesn't become one. Approve it on the web page.");

    // ---- the menu and the dialog
    public const string MenuLabel = "Ban from server...";

    /// <summary>Why the menu item is greyed out for a character listed as an operator.</summary>
    public const string MenuOperatorReason = "an operator";

    public const string DialogTitle = "Ban from server";
    public const string HowLong = "How long";
    public const string Reason = "Reason";
    public const string PlayerSeesIt = "The player is shown this.";
    public const string Days = "days";
    public const string Cancel = "Cancel";
    public const string ReplaceBan = "Replace the ban";
    public const string LiftBan = "Lift the ban";
    public const string Looking = "Looking them up...";

    /// <summary>What a ban does, under the reason.</summary>
    public const string WhatABanDoes = "They can't sign in, or register this character again, until the ban ends. Their places in channels stay.";

    /// <summary>A custom number of days that isn't one.</summary>
    public static readonly string DaysProblem = $"A number of days from 1 to {OperatorPanel.MaxDays.ToString("N0", CultureInfo.InvariantCulture)}.";

    /// <summary>A reason that can't be sent.</summary>
    public static readonly string ReasonProblem = $"One line of text, at most {OperatorPanel.MaxReasonLength} characters.";

    /// <summary>The menu item tried on the player's own character (the menus don't offer it).</summary>
    public const string NotYourself = "That's your own character: it can't be banned here.";

    /// <summary>The name of each choice of how long.</summary>
    public static string NameOf(OperatorDuration duration) => duration switch {
        OperatorDuration.OneDay => "1 day",
        OperatorDuration.SevenDays => "7 days",
        OperatorDuration.ThirtyDays => "30 days",
        OperatorDuration.UntilLifted => "Until lifted",
        _ => "",
    };

    /// <summary>The reason's length against its limit, as "16 / 300".</summary>
    public static string Counter(int length) => $"{length.ToString(CultureInfo.InvariantCulture)} / {OperatorPanel.MaxReasonLength.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The dialog's button, naming whom: "Ban Bob Hatter@Lich".</summary>
    public static string BanFor(string who) => $"Ban {who}";

    /// <summary>The dialog for a character not registered here: nothing the plugin can ban.</summary>
    public static string NotRegistered(string who) =>
        $"{who} isn't registered on this server, so there's nothing to ban here. To stop the character registering, ban its Lodestone ID on the web page.";

    /// <summary>The dialog for a character not registered here, with a ban in force (on its Lodestone ID): it can be lifted here, not banned again.</summary>
    public static string LiftOnly(PanelBan ban, string? me, DateTimeOffset now) =>
        AlreadyBanned(ban, me, now) + " It isn't registered on this server, so only the web page can ban it again; you can lift this ban here.";

    /// <summary>The dialog for a character listed as an operator.</summary>
    public static string IsAnOperator(string who) => $"{who} is listed as one of this server's operators, so it can't be banned here, nor its bans lifted (the web page does that).";

    /// <summary>The dialog for a character banned already: "Banned until Oct 17 (7 days), made on the web page. Reason: Spamming invites."</summary>
    public static string AlreadyBanned(PanelBan ban, string? me, DateTimeOffset now) {
        var made = MadeWords(ban, me);
        return $"Banned {Until(ban, now)}{(made.Length > 0 ? ", " + made : "")}." + (ban.Reason.Length > 0 ? $" Reason: {ban.Reason}" : " No reason given.");
    }

    /// <summary>The flag over the dialog: "Flagged 2 hours ago: refused in 30 of the last 60 minutes (InviteBurstPerPair, LookupBurst)."</summary>
    public static string FlaggedLine(PanelFlag flag, DateTimeOffset now) =>
        $"Flagged {Ago(flag.Last, now)}: {flag.Why}{(flag.Limits.Length > 0 ? $" ({flag.Limits})" : "")}.";

    // ---- chat, after an action (LookingGlass blue)

    /// <summary>"Banned Bob Hatter@Lich for 7 days." (0 days: until lifted), the same in both modes.</summary>
    public static string Banned(string who, int days) => days == 0 ? $"Banned {who} until lifted." : $"Banned {who} for {DaysWord(days)}.";

    /// <summary>"Lifted the ban on Bob Hatter@Lich."</summary>
    public static string Lifted(string who) => $"Lifted the ban on {who}.";

    /// <summary>"Couldn't ban Bob Hatter@Lich: " and why, in the mode's words (what a server said, without its error code in simple mode).</summary>
    public static string NotBanned(string who, Exception ex, bool advanced) => $"Couldn't ban {who}: {WhyNot(ex, advanced)}";

    /// <summary>"Couldn't lift the ban on Bob Hatter@Lich: " and why, in the mode's words.</summary>
    public static string NotLifted(string who, Exception ex, bool advanced) => $"Couldn't lift the ban on {who}: {WhyNot(ex, advanced)}";

    /// <summary>
    /// Why an operator request failed, in the mode's words: a refusal the server gave a reason for (<see cref="OperatorRefusalReason"/>)
    /// in the plugin's own words, in both modes (advanced mode adds what the server said); never the server's text in simple mode.
    /// Anything else (no answer, not connected) as the plugin says it elsewhere.
    /// </summary>
    public static string WhyNot(Exception ex, bool advanced) {
        if (ex is not ServerErrorException server) {
            return LodestoneCode.Redact(PlainMessages.Of(PlainMessages.MessageOf(ex, advanced), advanced));
        }

        var words = Refusal(server.OperatorRefusal).For(advanced);
        return advanced ? LodestoneCode.Redact($"{words} The server says: {server.Message}") : words;
    }

    /// <summary>What a refusal of an operator request means, in both modes' words.</summary>
    public static Wording Refusal(OperatorRefusalReason reason) => reason switch {
        OperatorRefusalReason.NotAnOperator => new Wording(NoticeKind.General,
            "This server doesn't treat this character as an operator now (its keys changed, or it isn't listed any more).",
            "This server doesn't treat you as an operator now."),
        OperatorRefusalReason.WrongAddress => Wording.Same("The server doesn't accept the address you're connected to for this. Use one of its own addresses in Settings."),
        OperatorRefusalReason.BadSignature => new Wording(NoticeKind.General,
            "The server says it isn't signed with this character's identity key for this login.",
            "The server didn't accept it as coming from this computer's LookingGlass setup."),
        OperatorRefusalReason.NonceStale => Wording.Same("It took too long. Try again."),
        OperatorRefusalReason.TooOften => Wording.Same("You're doing this too often. Wait a minute, then try again."),
        OperatorRefusalReason.NotValid => Wording.Same("The server didn't accept the days, the reason or the character."),
        OperatorRefusalReason.OperatorCharacter => Wording.Same("That character is one of this server's operators: its bans are made and lifted on the web page."),
        OperatorRefusalReason.NotRegistered => Wording.Same("That character isn't registered on this server: ban its Lodestone ID on the web page."),
        OperatorRefusalReason.HourLimit => Wording.Same($"You've made {OperatorPanel.BansPerHour} bans in the last hour, the most the plugin allows. Use the web page for more."),
        OperatorRefusalReason.NothingToLift => Wording.Same("There's no ban on that character in force."),
        OperatorRefusalReason.NotHere => Wording.Same("The server's rules don't allow that from the plugin. Use the web page."),
        _ => Wording.Same("The server couldn't do it. Try again later."),
    };

    // ---- this character isn't an operator now

    /// <summary>
    /// Told once a session, in chat, in LookingGlass's warning colour, when the server says this character is listed as an
    /// operator but its keys aren't the pinned ones (it was set up again), or none are pinned yet.
    /// </summary>
    /// <param name="mine">This character's fingerprint now.</param>
    /// <param name="pinned">The pinned keys' fingerprint, with <see cref="OperatorStanding.KeysChanged"/>.</param>
    public static Wording NotOperatorNotice(OperatorStanding standing, string? mine, string? pinned) => standing == OperatorStanding.NotPinned
        ? new Wording(NoticeKind.NotOperatorNow,
            $"This server lists this character as an operator, but its keys aren't pinned yet (yours are {mine ?? "unknown"}). Compare the fingerprint, then " +
            "approve them on the server's operator web page.",
            "This server lists this character as an operator, but hasn't approved your LookingGlass setup yet. Approve it on the server's operator web page.")
        : new Wording(NoticeKind.NotOperatorNow,
            $"Your identity keys changed (now {mine ?? "unknown"}; the server's operator pin is {pinned ?? "unknown"}), so this server no longer treats you as an " +
            "operator. Compare the fingerprint, then restore operator status on the web page.",
            "You set up LookingGlass again on this character, so this server no longer treats you as an operator. Restore it on the server's operator web page.");

    /// <summary>What the Operator section says in its place, before the fingerprints.</summary>
    public static Wording NotOperatorSection(OperatorStanding standing) => standing == OperatorStanding.NotPinned
        ? new Wording(NoticeKind.NotOperatorNow, "This server's operator hasn't pinned (approved) your keys yet.",
            "This server's operator hasn't approved your LookingGlass setup yet.")
        : new Wording(NoticeKind.NotOperatorNow,
            "This server no longer treats you as an operator: your identity keys changed (a new computer, or a reset), so they aren't the ones pinned for you.",
            "This server no longer treats you as an operator: you set up LookingGlass again on this character (new computer, or a reset).");

    /// <summary>The label before this character's fingerprint, shown in simple mode too here (the owner compares it with the web page's).</summary>
    public static string YourFingerprint(bool advanced) => advanced ? "Your keys now" : "Your code now";

    /// <summary>The label before the pinned keys' fingerprint (advanced mode only).</summary>
    public const string PinnedFingerprint = "Pinned keys";

    /// <summary>What to do, after the fingerprints.</summary>
    public static Wording NotOperatorAdvice(OperatorStanding standing) => standing == OperatorStanding.NotPinned
        ? Wording.Same("To approve it, open the web page and check it shows these same numbers.", NoticeKind.NotOperatorNow)
        : Wording.Same("To restore it, open the web page and check it shows these same numbers.", NoticeKind.NotOperatorNow);

    // ---- the lists

    /// <summary>"2 flagged · 3 bans in force · 1 blocked automatically".</summary>
    /// <remarks>As the plan's mockup: the automatic blocks are among the bans in force, and counted again after them.</remarks>
    public static string Counts(OperatorLists lists) =>
        $"{lists.FlagsTotal} flagged · {lists.InForceTotal} {(lists.InForceTotal == 1 ? "ban" : "bans")} in force · {lists.BlockedAutomatically} blocked automatically";

    /// <summary>"and 4 more (see the web page)".</summary>
    public static string More(int more) => $"and {more.ToString(CultureInfo.InvariantCulture)} more (see the web page)";

    /// <summary>"Bans left this hour: 7 of 10".</summary>
    public static string BansLeft(int left) => $"Bans left this hour: {left} of {OperatorPanel.BansPerHour}";

    /// <summary>"Bob Hatter@Lich", "address 203.0.113.x", or "user 4242" for a character not registered here.</summary>
    public static string Who(long userId, User? user, string? address) =>
        user != null ? $"{user.Name}@{user.WorldName}"
        : userId == 0 && !string.IsNullOrEmpty(address) ? $"address {address}"
        : $"user {userId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>A ban in force, after whom: "until Oct 17 (7 days) · "Spamming invites"", or "blocked automatically, 9 minutes left".</summary>
    public static string BanDetail(PanelBan ban, DateTimeOffset now) {
        if (ban.Automatic) {
            return ban.Until is { } ends ? $"blocked automatically, {Left(ends - now)}" : "blocked automatically";
        }

        return Until(ban, now) + (ban.Reason.Length > 0 ? $" · \"{ban.Reason}\"" : "");
    }

    /// <summary>
    /// Who made a ban, on a line of its own: "made in the plugin by you", "made in the plugin by Alice Rabbit@Odin", "made on the
    /// web page", "made on the command line"; empty for an automatic block, or one whose tool wasn't recorded.
    /// </summary>
    /// <param name="me">This character as "Name@World", to say "you".</param>
    public static string MadeWords(PanelBan ban, string? me) => Via(ban.MadeVia, ban.MadeBy, me) is { Length: > 0 } via ? "made " + via : "";

    /// <summary>A ban lifted or ended: "Oct 1 to Oct 8, lifted on the web page · "Spamming invites"".</summary>
    public static string HistoryDetail(PanelBan ban, string? me, DateTimeOffset now) {
        var end = ban.Lifted ?? ban.Until;
        var how = ban.Replaced ? "replaced by a new ban"
            : ban.Lifted != null ? "lifted" + (Via(ban.LiftedVia, ban.LiftedBy, me) is { Length: > 0 } via ? " " + via : "")
            : "ended";
        return $"{Date(ban.Created, now)} to {(end is { } at ? Date(at, now) : "?")}, {how}" + (ban.Reason.Length > 0 ? $" · \"{ban.Reason}\"" : "");
    }

    /// <summary>"until Oct 17 (7 days)", "until 14:05 (2 hours)", or "until lifted".</summary>
    public static string Until(PanelBan ban, DateTimeOffset now) =>
        ban.Until is { } until ? $"until {Date(until, now)} ({Length(until - ban.Created)})" : "until lifted";

    /// <summary>"just now", "20 min ago", "2 hours ago", or a local date ("Oct 8") for a day or more.</summary>
    public static string Ago(DateTimeOffset time, DateTimeOffset now) {
        var age = now - time;
        return age < TimeSpan.FromMinutes(1) ? "just now"
            : age < TimeSpan.FromHours(1) ? $"{(int) age.TotalMinutes} min ago"
            : age < TimeSpan.FromDays(1) ? ((int) age.TotalHours == 1 ? "1 hour ago" : $"{(int) age.TotalHours} hours ago")
            : "on " + Date(time, now);
    }

    /// <summary>A local time: the hour on the same day ("14:05"), else the date ("Oct 17", with the year if it isn't this year's).</summary>
    public static string Date(DateTimeOffset time, DateTimeOffset now) {
        var local = time.ToLocalTime();
        var today = now.ToLocalTime();
        return local.Date == today.Date ? local.ToString("t", CultureInfo.CurrentCulture)
            : local.Year == today.Year ? local.ToString("MMM d", CultureInfo.CurrentCulture)
            : local.ToString("MMM d yyyy", CultureInfo.CurrentCulture);
    }

    /// <summary>How long a ban lasts: "15 minutes", "2 hours", "7 days".</summary>
    public static string Length(TimeSpan span) =>
        span < TimeSpan.FromHours(1) ? Plural((int) Math.Max(1, Math.Round(span.TotalMinutes)), "minute")
        : span < TimeSpan.FromDays(1) ? Plural((int) Math.Round(span.TotalHours), "hour")
        : DaysWord((int) Math.Round(span.TotalDays));

    /// <summary>Time left: "9 minutes left", "less than a minute left".</summary>
    private static string Left(TimeSpan left) =>
        left < TimeSpan.FromMinutes(1) ? "less than a minute left" : Length(left) + " left";

    /// <summary>"in the plugin by you", "on the web page", and so on; empty if not known.</summary>
    private static string Via(string via, string by, string? me) => via switch {
        "plugin" => by.Length == 0 ? "in the plugin"
            : string.Equals(by, me, StringComparison.OrdinalIgnoreCase) ? "in the plugin by you"
            : $"in the plugin by {by}",
        "web page" => "on the web page",
        "command line" => "on the command line",
        _ => "",
    };

    private static string DaysWord(int days) => Plural(days, "day");

    private static string Plural(int count, string unit) => count == 1 ? $"1 {unit}" : $"{count.ToString("N0", CultureInfo.InvariantCulture)} {unit}s";

    /// <summary>The section's and the dialog's labels, for the tests: short and plain.</summary>
    public static IReadOnlyList<string> Labels() => [
        Heading, Refresh, FlaggedHeading, InForceHeading, HistoryHeading, BanSomeone, CopyPageAddress, HowLong, Reason, Cancel, ReplaceBan, LiftBan,
        DialogTitle, NameOf(OperatorDuration.OneDay), NameOf(OperatorDuration.SevenDays), NameOf(OperatorDuration.ThirtyDays),
        NameOf(OperatorDuration.UntilLifted), YourFingerprint(advanced: false),
    ];

    /// <summary>Examples of every wording with a kind of its own, for tests: each must say it in plain words in simple mode.</summary>
    internal static IEnumerable<Wording> Examples() {
        const string mine = "63390 21187 50462 99805 41733";
        const string pinned = "11873 40229 95310 72646 08154";
        foreach (var standing in new[] { OperatorStanding.KeysChanged, OperatorStanding.NotPinned }) {
            yield return NotOperatorNotice(standing, mine, pinned);
            yield return NotOperatorNotice(standing, null, null);
            yield return NotOperatorSection(standing);
            yield return NotOperatorAdvice(standing);
        }

        yield return Help;
        yield return KeysHelp;
        yield return NotPinnedHelp;
        foreach (var reason in Enum.GetValues<OperatorRefusalReason>()) {
            yield return Refusal(reason);
        }

        yield return NotAvailable;
    }
}
