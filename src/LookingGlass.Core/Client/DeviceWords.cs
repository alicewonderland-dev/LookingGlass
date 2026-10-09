using System.Globalization;

namespace LookingGlass.Core.Client;

/// <summary>Who used "Sign out everywhere else", as a computer that was signed out by it can tell (see <see cref="DeviceWords.SignedOutStatus"/>).</summary>
public enum SignedOutBy {
    /// <summary>Not signed out.</summary>
    None,

    /// <summary>This computer itself, which lost its own login since.</summary>
    ThisComputer,

    /// <summary>A computer of the account this one has seen in its list: one of the player's own, most likely.</summary>
    YourOtherComputer,

    /// <summary>A computer this one has never seen, or a copy of this computer's own login: someone else may have its files.</summary>
    UnknownComputer,
}

/// <summary>
/// What the player is told about the computers signed in to their account (its devices, each with a login of its own; see
/// "Other computers signing in" in docs/design.md), in both modes' words: the notice when another computer signs in, the one
/// when this computer's own login was used elsewhere, the list in Settings, and "Sign out everywhere else".
/// </summary>
public static class DeviceWords {
    /// <summary>The list's short label in Settings (under "Your identity"), with a "?" (<see cref="Explanation"/>).</summary>
    public const string Label = "Computers signed in";

    /// <summary>The button that signs out every other computer.</summary>
    public const string SignOutButton = "Sign out everywhere else";

    /// <summary>What <see cref="Label"/> means, in a sentence or two: its "?" bubble (<see cref="SettingHelp.SignedInComputers"/>).</summary>
    public static readonly Wording Explanation = new(NoticeKind.General,
        "Each device with a login to this character here: registering, or signing in with your key, adds one. If you don't know one, " +
        "sign out everywhere else, then reset your identity.",
        "Each computer signed in to LookingGlass as this character on this server. If you don't know one, use \"Sign out everywhere " +
        "else\", then reset your identity.");

    /// <summary>Shown instead of the list while it can't be had: not logged in, or a server from before it.</summary>
    public static readonly Wording NotAvailable = Wording.Same("Not available: connect and log in to a server that keeps this list.");

    private static string When(DateTimeOffset? time) =>
        time is { } at ? "on " + at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "recently";

    /// <summary>
    /// The account got new logins on other computers: key logins with this character's identity key, or registrations
    /// through the Lodestone. Told to every other computer of the account, at once if it is online, otherwise at its next
    /// login; several at once in one notice.
    /// </summary>
    /// <param name="count">How many.</param>
    /// <param name="newest">When the server says the newest was added; null if what it said isn't a time ("recently").</param>
    public static Wording SignedInElsewhere(int count, DateTimeOffset? newest) {
        var when = When(newest);
        const string TechnicalAdvice = "If that wasn't you, use \"Sign out everywhere else\" in Settings, then reset your identity: until then, a " +
                                       "copy of your identity key can read your channels.";
        const string PlainAdvice = "If that wasn't you, use \"Sign out everywhere else\" in Settings and reset your identity.";
        return count <= 1
            ? new Wording(NoticeKind.SignedInElsewhere,
                $"Your LookingGlass account got a new login from another computer {when}: someone signed in with your identity key, or " +
                $"registered your character again through the Lodestone. {TechnicalAdvice}",
                $"Your LookingGlass character signed in from another computer {when}. {PlainAdvice}")
            : new Wording(NoticeKind.SignedInElsewhere,
                $"Your LookingGlass account got {count} new logins from other computers, the last {when}: someone signed in with your " +
                $"identity key, or registered your character again through the Lodestone. {TechnicalAdvice}",
                $"Your LookingGlass character signed in from {count} other computers, the last {when}. {PlainAdvice}");
    }

    /// <summary>
    /// This computer's own login was used elsewhere since it last logged in (the server's record of its last use isn't this
    /// computer's last login): a copy of it, from a copy of the secrets file, may be in use. Said gently: a server restored
    /// from a backup can do the same.
    /// </summary>
    /// <param name="when">When the server says it was last used; null if what it said isn't a time.</param>
    public static Wording LoginUsedElsewhere(DateTimeOffset? when) => new(NoticeKind.LoginUsedElsewhere,
        $"This computer's login (device token) was used {When(when)}, after this computer last logged in: a copy of it, from a copy of " +
        "your secrets file, may be in use elsewhere (or the server was restored from a backup). If that wasn't you, use \"Sign out " +
        "everywhere else\" in Settings, which also replaces this login; if it happens again, reset your identity.",
        $"This computer's saved LookingGlass login may have been used somewhere else {When(when)}, since you last played here. If that " +
        "wasn't you, use \"Sign out everywhere else\" in Settings; if it happens again, use \"Reset my identity\".");

    /// <summary>The status of a computer whose login and identity key the server refuses after "Sign out everywhere else".</summary>
    public static Wording SignedOutStatus(SignedOutBy by) => by switch {
        SignedOutBy.ThisComputer => new Wording(NoticeKind.SignedOutElsewhere,
            "This computer used \"Sign out everywhere else\", which stops your identity key signing in, and the server has lost this " +
            "computer's login since. Register again through the Lodestone (below): that lets the key sign in again.",
            "This computer used \"Sign out everywhere else\", and the server has lost its login since, so it can't sign itself in. " +
            "Register again through the Lodestone (below) to use LookingGlass here."),
        SignedOutBy.UnknownComputer => new Wording(NoticeKind.SignedOutElsewhere,
            "A device this computer has never seen used \"Sign out everywhere else\" (or a copy of this computer's own login did): someone " +
            "else may hold your identity key. Use \"Reset my identity\" (below): it retires the key, and registering moves your channels " +
            "to a new one. Don't just register again with this key: that would let them sign in again.",
            "You were signed out by a computer this one has never seen (with \"Sign out everywhere else\"). Someone else may have a copy " +
            "of your LookingGlass files. Use \"Reset my identity\" (below): it sets LookingGlass up afresh and takes your channels back. " +
            "Don't just register again: that would keep the setup they copied."),
        _ => new Wording(NoticeKind.SignedOutElsewhere,
            "Another of your computers used \"Sign out everywhere else\": the server revoked this computer's login, and your identity key " +
            "can't sign in again until this character is registered again through the Lodestone. Register again (below) to use " +
            "LookingGlass here. If it wasn't you, someone else has your identity key: use \"Reset my identity\" in Settings instead, " +
            "which re-verifies you with a new key and takes your channels back.",
            "You were signed out from another of your computers (with \"Sign out everywhere else\"). To use LookingGlass here again, " +
            "register again through the Lodestone (below). If that wasn't you, someone else has a copy of your LookingGlass files: use " +
            "\"Reset my identity\" in Settings instead, which takes your channels back."),
    };

    /// <summary>The heading over <see cref="SignedOutStatus"/> in the main window.</summary>
    public static string SignedOutTitle(SignedOutBy by) => by switch {
        SignedOutBy.ThisComputer => "This computer's login was lost",
        SignedOutBy.UnknownComputer => "Signed out by a computer this one doesn't know",
        _ => "You were signed out from another computer",
    };

    /// <summary>Told once "Sign out everywhere else" is done.</summary>
    /// <param name="count">How many other computers' logins it revoked.</param>
    public static Wording SignedOutOthers(int count) {
        var others = count == 1 ? "1 other device" : $"{count} other devices";
        var computers = count == 1 ? "1 other computer" : $"{count} other computers";
        return new Wording(NoticeKind.General,
            $"Signed out everywhere else ({others}), and this computer has a new login. Your identity key can't sign in anywhere until " +
            "this character is registered again through the Lodestone: not on this computer either, should its login be lost. If you " +
            "didn't recognise a device, reset your identity too.",
            $"Signed out everywhere else ({computers}), and this computer has a new login. The others can only sign in again by " +
            "registering again through the Lodestone, and so would this computer if its login were lost. If you didn't recognise one, " +
            "use \"Reset my identity\" in Settings too.");
    }

    /// <summary>What "Sign out everywhere else" asks before it does it.</summary>
    public static Wording ConfirmText(string name, string serverUrl) => new(NoticeKind.General,
        $"Sign out every other device with a login to {name} on {serverUrl}? This computer stays signed in, with a new login, so a copy " +
        "of its old one is no use either.\n\n" +
        "The server revokes their logins, and your identity key can't sign in again until this character is registered again through " +
        "the Lodestone, so a copy of the key can't sign straight back in. That holds for this computer too: should its login ever be " +
        "lost, you'd register again.\n\n" +
        "If you didn't recognise one, also use \"Reset my identity\": registering again with this key would let a copy of it back in.",
        $"Sign out every other computer where {name} uses LookingGlass on {serverUrl}? This computer stays signed in.\n\n" +
        "The others can only sign in again by registering again through the Lodestone. So could this computer, should its login ever " +
        "be lost.\n\n" +
        "If you didn't recognise one, also use \"Reset my identity\": registering again with your current setup would let a copy of it back in.");

    /// <summary>One computer in the list, on one short line: "This computer: added 3 h ago, used just now".</summary>
    public static string Line(DeviceView device, DateTimeOffset now) =>
        $"{(device.ThisDevice ? "This computer" : "Another computer")}: added {Ago(device.Added, now)}, used {Ago(device.LastUsed, now)}";

    /// <summary>A time, as "just now", "5 min ago" or "3 h ago" within a day, and as a local date and time before that.</summary>
    public static string Ago(DateTimeOffset time, DateTimeOffset now) {
        var age = now - time;
        return age.Duration() < TimeSpan.FromMinutes(1) ? "just now"
            : age < TimeSpan.Zero ? time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            : age < TimeSpan.FromHours(1) ? $"{(int) age.TotalMinutes} min ago"
            : age < TimeSpan.FromDays(1) ? $"{(int) age.TotalHours} h ago"
            : time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    }

    /// <summary>Examples of every wording above, for tests: each must say it in plain words in simple mode.</summary>
    internal static IEnumerable<Wording> Examples() {
        var when = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        yield return SignedInElsewhere(1, when);
        yield return SignedInElsewhere(1, null);
        yield return SignedInElsewhere(3, when);
        yield return LoginUsedElsewhere(when);
        yield return LoginUsedElsewhere(null);
        foreach (var by in new[] { SignedOutBy.ThisComputer, SignedOutBy.YourOtherComputer, SignedOutBy.UnknownComputer }) {
            yield return SignedOutStatus(by);
        }

        yield return SignedOutOthers(0);
        yield return SignedOutOthers(1);
        yield return SignedOutOthers(3);
        yield return Explanation;
        yield return ConfirmText("Alice Liddell", "wss://chat.example.com/ws");
    }
}
