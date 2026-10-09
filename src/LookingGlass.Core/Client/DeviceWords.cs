using System.Globalization;

namespace LookingGlass.Core.Client;

/// <summary>
/// What the player is told about the computers signed in to their account (its devices, each with a login of its own; see
/// "Other computers signing in" in docs/design.md), in both modes' words: the notice when another computer signs in, the list
/// in Settings, and "Sign out everywhere else".
/// </summary>
public static class DeviceWords {
    /// <summary>The list's short label in Settings (under "Your identity").</summary>
    public const string Label = "Signed-in computers";

    /// <summary>The button that signs out every other computer.</summary>
    public const string SignOutButton = "Sign out everywhere else";

    /// <summary>The confirmation's title.</summary>
    public const string SignOutTitle = "Sign out everywhere else";

    /// <summary>
    /// What <see cref="Label"/> means, in a sentence or two: for a "?" beside it (or its tooltip until Settings has those).
    /// </summary>
    public static readonly Wording Explanation = new(NoticeKind.General,
        "Every device with a login to this character on this server: registering, or signing in with your identity key, adds one. " +
        "If you don't recognise one, use \"Sign out everywhere else\", then reset your identity.",
        "Every computer where this character is signed in to LookingGlass on this server. " +
        "If you don't recognise one, use \"Sign out everywhere else\", then reset your identity.");

    /// <summary>Shown instead of the list while it can't be had: not logged in, or a server from before it.</summary>
    public static readonly Wording NotAvailable = Wording.Same("Not available: connect and log in to a server that keeps this list.");

    /// <summary>
    /// The account got a new login on another computer: a key login with this character's identity key, or a registration
    /// through the Lodestone. Told to every other computer of the account, at once if it is online, otherwise at its next login.
    /// </summary>
    /// <param name="added">When the server says it was added; null if what it said isn't a time ("recently").</param>
    public static Wording SignedInElsewhere(DateTimeOffset? added) {
        var when = added is { } time ? "on " + time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "recently";
        return new Wording(NoticeKind.SignedInElsewhere,
            $"Your LookingGlass account got a new login from another computer {when}: someone signed in with your identity key, or registered " +
            "your character again through the Lodestone. If that wasn't you, use \"Sign out everywhere else\" in Settings and reset your " +
            "identity: until then, a copy of your identity key can read your channels.",
            $"Your LookingGlass character signed in from another computer {when}. If that wasn't you, use \"Sign out everywhere else\" in " +
            "Settings and reset your identity.");
    }

    /// <summary>The status of a computer whose login another one signed out, and whose identity key the server now refuses.</summary>
    public static readonly Wording SignedOutElsewhere = new(NoticeKind.SignedOutElsewhere,
        "Another computer used \"Sign out everywhere else\": the server revoked this computer's login, and your identity key can't sign in " +
        "again until this character is registered again through the Lodestone. Register again (below) to use LookingGlass here. If it " +
        "wasn't you, someone else has your identity key: use \"Reset my identity\" in Settings instead, which re-verifies you with a new " +
        "key and takes your channels back.",
        "You were signed out from another computer (with \"Sign out everywhere else\"). To use LookingGlass here again, register again " +
        "through the Lodestone (below). If that wasn't you, someone else has a copy of your LookingGlass files: use \"Reset my identity\" " +
        "in Settings instead, which takes your channels back.");

    /// <summary>The heading over <see cref="SignedOutElsewhere"/> in the main window.</summary>
    public const string SignedOutTitle = "You were signed out from another computer";

    /// <summary>Told once "Sign out everywhere else" is done.</summary>
    /// <param name="count">How many other computers' logins it revoked.</param>
    public static Wording SignedOutOthers(int count) {
        var others = count == 1 ? "1 other device" : $"{count} other devices";
        var computers = count == 1 ? "1 other computer" : $"{count} other computers";
        return new Wording(NoticeKind.General,
            $"Signed out everywhere else ({others}). Your identity key can't sign in anywhere else until this character is registered again " +
            "through the Lodestone. If you didn't recognise a device, reset your identity too.",
            $"Signed out everywhere else ({computers}). They can only sign in again by registering again through the Lodestone. If you " +
            "didn't recognise one, use \"Reset my identity\" in Settings too.");
    }

    /// <summary>What "Sign out everywhere else" asks before it does it.</summary>
    public static Wording ConfirmText(string name, string serverUrl) => new(NoticeKind.General,
        $"Sign out every other device with a login to {name} on {serverUrl}? This computer stays signed in.\n\n" +
        "The server revokes their logins, and your identity key can't sign in again anywhere else until this character is registered " +
        "again through the Lodestone, so a copy of the key can't sign straight back in.\n\n" +
        "If you didn't recognise one, also use \"Reset my identity\": registering again with this key would let a copy of it back in.",
        $"Sign out every other computer where {name} uses LookingGlass on {serverUrl}? This computer stays signed in.\n\n" +
        "The others can only sign in again by registering again through the Lodestone.\n\n" +
        "If you didn't recognise one, also use \"Reset my identity\": registering again with your current setup would let a copy of it back in.");

    /// <summary>One computer in the list, on one short line: "This computer: added 3 h ago, in use now".</summary>
    public static string Line(DeviceView device, DateTimeOffset now) => device.ThisDevice
        ? $"This computer: added {Ago(device.Added, now)}, in use now"
        : $"Another computer: added {Ago(device.Added, now)}, used {Ago(device.LastUsed, now)}";

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
        yield return SignedInElsewhere(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
        yield return SignedInElsewhere(null);
        yield return SignedOutElsewhere;
        yield return SignedOutOthers(0);
        yield return SignedOutOthers(1);
        yield return SignedOutOthers(3);
        yield return Explanation;
        yield return ConfirmText("Alice Liddell", "wss://chat.example.com/ws");
    }
}
