namespace LookingGlass.Core.Client;

/// <summary>
/// What Settings and channel windows say about the chat log on this computer, in both modes' words (see
/// <see cref="Wording"/>). Simple mode calls it the player's "chat history": "log" and "encrypted" are words it doesn't use.
/// </summary>
public static class ChatLogWords {
    public static readonly Wording KeepIt = new(NoticeKind.General,
        "Keep a chat log on this computer",
        "Keep a chat history on this computer");

    /// <param name="protection">How the secrets file is protected ("Windows DPAPI", "local key file"), for advanced mode.</param>
    public static Wording Explanation(string protection) => new(NoticeKind.General,
        "Saves every channel's messages (yours too, and LookingGlass's lines about who joined or left) so channel windows can " +
        "show them after you log out: scroll up in a window, or click \"Show older messages\". Encrypted at rest with its own " +
        $"key, kept as your identity keys are ({protection}). It stays on this computer, and is never uploaded or shared. " +
        "When it reaches its size limit, the oldest messages are deleted first.",
        "Saves every channel's messages (yours too, and who joined or left) so channel windows can show them again the next " +
        "time you play: scroll up in a window, or click \"Show older messages\". It's kept scrambled, like your LookingGlass setup, " +
        "stays on this computer, and is never uploaded or shared. When it's full, the oldest messages are deleted first.");

    public static readonly Wording SizeLimit = Wording.Same("Largest size (MB)");

    public static readonly Wording SizeLimitTooltip = Wording.Same(
        $"From {ChatLogLimits.MinMegabytes} MB to {ChatLogLimits.MaxMegabytes} MB (1 GB), for each character on each server. " +
        "Lowering it deletes the oldest messages straight away.");

    /// <summary>"Your chat log uses 12.3 MB on this computer."</summary>
    public static Wording Uses(long bytes) => new(NoticeKind.General,
        $"Your chat log uses {ChatLogLimits.Describe(bytes)} on this computer.",
        $"Your chat history uses {ChatLogLimits.Describe(bytes)} on this computer.");

    public static readonly Wording Delete = new(NoticeKind.General, "Delete my chat log", "Delete my chat history");

    /// <summary>What the confirmation of <see cref="Delete"/> says.</summary>
    public static Wording DeleteConfirm(long bytes) => new(NoticeKind.General,
        $"This deletes the chat log LookingGlass keeps on this computer, for every character and server ({ChatLogLimits.Describe(bytes)}). " +
        "It can't be undone. Your channels and their members aren't affected, and what came since you logged in stays in " +
        "channel windows until you log out.",
        $"This deletes the chat history LookingGlass keeps on this computer, for every character and server ({ChatLogLimits.Describe(bytes)}). " +
        "It can't be undone. Your channels and their members aren't affected, and what came since you logged in stays in " +
        "channel windows until you leave the game or change character.");

    /// <summary>Asked when the setting is turned off.</summary>
    public static Wording TurnedOff(long bytes) => new(NoticeKind.General,
        $"LookingGlass no longer adds to your chat log. Delete what it kept so far ({ChatLogLimits.Describe(bytes)}) too? " +
        "Cancel keeps it: you can delete it in Settings at any time.",
        $"LookingGlass no longer adds to your chat history. Delete what it kept so far ({ChatLogLimits.Describe(bytes)}) too? " +
        "Cancel keeps it: you can delete it in Settings at any time.");

    /// <summary>The log on disk can't be unlocked on this computer (see <see cref="ChatLogState.Unreadable"/>).</summary>
    public static Wording Unreadable(string? problem) => new(NoticeKind.General,
        $"Your chat log on this computer can't be read here ({problem ?? "its key can't be unlocked"}), so nothing is added to it. " +
        "It may have been copied from another computer or Windows account. Delete it to start a new one.",
        "Your chat history on this computer can't be opened here, so nothing is added to it. It may have been copied from " +
        "another computer or Windows account. Delete it to start a new one.");

    /// <summary>A channel window's button for the next older page.</summary>
    public static readonly Wording ShowOlder = Wording.Same("Show older messages");

    public static readonly Wording Loading = Wording.Same("Loading older messages...");

    /// <summary>A window has shown everything the log holds for the channel.</summary>
    public static readonly Wording NothingOlder = new(NoticeKind.General,
        "That's everything in your chat log for this channel.",
        "That's everything in your chat history for this channel.");

    /// <summary>The dimmed line above a day's older lines in a channel window: "Earlier: Tuesday 6 October 2026".</summary>
    public static Wording Earlier(DateTimeOffset day, TimeZoneInfo? zone = null) {
        var local = TimeZoneInfo.ConvertTime(day, zone ?? TimeZoneInfo.Local);
        return Wording.Same("Earlier: " + local.ToString("dddd d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>The dimmed line between the older lines and those since login.</summary>
    public static readonly Wording SinceLogin = Wording.Same("Since you logged in");

    /// <summary>Every wording above, with examples for those that take something: for the plain-language tests.</summary>
    public static IEnumerable<Wording> Examples() => [
        KeepIt, Explanation("Windows DPAPI"), SizeLimit, SizeLimitTooltip, Uses(12_900_000), Delete, DeleteConfirm(52_428_800),
        TurnedOff(800_000), Unreadable("the file that unlocks it is missing"), Unreadable(null), ShowOlder, Loading, NothingOlder,
        Earlier(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero)), SinceLogin,
    ];
}
