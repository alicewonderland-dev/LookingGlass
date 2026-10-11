namespace LookingGlass.Core.Client;

/// <summary>
/// What Settings and channel windows say about the chat log on this computer, in both modes' words (see
/// <see cref="Wording"/>). Simple mode calls it the player's "chat history": "log" is a word it doesn't use. "Encrypted" it does (the owner, 2026-10-10: a common enough word).
/// </summary>
public static class ChatLogWords {
    public static readonly Wording KeepIt = new(NoticeKind.General,
        "Keep a chat log on this computer",
        "Keep chat history on this computer");

    /// <summary>What <see cref="KeepIt"/> does, in the bubble of its "?" in Settings (see <see cref="SettingsWords"/>).</summary>
    /// <param name="protection">How the secrets file is protected ("Windows DPAPI", "local key file"), for advanced mode.</param>
    public static Wording Explanation(string protection) => new(NoticeKind.General,
        $"Lets channel windows show older messages next time you play. Stored encrypted ({protection}), never uploaded. The " +
        "oldest messages go first when it's full.",
        "Lets channel windows show older messages next time you play. Stored encrypted, never uploaded. The oldest messages " +
        "go first when it's full.");

    public static readonly Wording SizeLimit = Wording.Same("Size limit");

    /// <summary>
    /// Keeping the chat log unencrypted too, in text files (off by default, shown only while the log is on).
    /// </summary>
    public static readonly Wording KeepUnencrypted = new(NoticeKind.General,
        "Keep my chat log unencrypted",
        "Keep my chat history unencrypted");

    /// <summary>What <see cref="KeepUnencrypted"/> does, in the bubble of its "?" in Settings (see <see cref="SettingsWords"/>).</summary>
    public static readonly Wording UnencryptedExplanation = new(NoticeKind.General,
        "Also writes new lines to plain text files any program can open, one per channel per month. Anything that can read " +
        "your files can read them.",
        "Also writes new messages to text files any program can open, one per channel per month. Anything that can read " +
        "your files can read them.");

    /// <summary>Asked before <see cref="KeepUnencrypted"/> is turned on: the plain warning the owner asked for.</summary>
    public static readonly Wording UnencryptedWarning = new(NoticeKind.General,
        "From now on, LookingGlass also writes your chat log to plain text files on this computer, one per channel per month, " +
        "that Notepad and other programs can open. They aren't encrypted: anyone or anything that can read your files can " +
        "read them, including backup and cloud-sync tools. Nothing kept before is copied to them. They count towards the size " +
        "limit, and Delete my chat log deletes them too.",
        "From now on, LookingGlass also writes your chat history to text files on this computer, one per channel per month, " +
        "that Notepad and other programs can open. They aren't encrypted: anyone or anything that can read your files can " +
        "read them, including backup and cloud-sync tools. Nothing kept before is copied to them. They count towards the size " +
        "limit, and Delete my chat history deletes them too.");

    /// <summary>The button that turns <see cref="KeepUnencrypted"/> on, after <see cref="UnencryptedWarning"/>.</summary>
    public static readonly Wording TurnOn = Wording.Same("Turn on");

    /// <summary>Opens the folder with this character's text files for this server.</summary>
    public static readonly Wording OpenFolder = Wording.Same("Open folder");

    /// <summary>"Your chat log uses 12.3 MB on this computer."</summary>
    public static Wording Uses(long bytes) => new(NoticeKind.General,
        $"Your chat log uses {ChatLogLimits.Describe(bytes)} on this computer.",
        $"Your chat history uses {ChatLogLimits.Describe(bytes)} on this computer.");

    public static readonly Wording Delete = new(NoticeKind.General, "Delete my chat log", "Delete my chat history");

    /// <summary>What the confirmation of <see cref="Delete"/> says.</summary>
    public static Wording DeleteConfirm(long bytes) => new(NoticeKind.General,
        $"This deletes the chat log LookingGlass keeps on this computer, and its text files if you kept any, for every character and server ({ChatLogLimits.Describe(bytes)}). " +
        "It can't be undone. Your channels and their members aren't affected, and what came since you logged in stays in " +
        "channel windows until you log out.",
        $"This deletes the chat history LookingGlass keeps on this computer, and its text files if you kept any, for every character and server ({ChatLogLimits.Describe(bytes)}). " +
        "It can't be undone. Your channels and their members aren't affected, and what came since you logged in stays in " +
        "channel windows until you leave the game or change character.");

    /// <summary>Asked when the setting is turned off.</summary>
    public static Wording TurnedOff(long bytes) => new(NoticeKind.General,
        $"LookingGlass no longer adds to your chat log. Delete what it kept so far ({ChatLogLimits.Describe(bytes)}), its text files too? " +
        "Cancel keeps it: you can delete it in Settings at any time.",
        $"LookingGlass no longer adds to your chat history. Delete what it kept so far ({ChatLogLimits.Describe(bytes)}), its text files too? " +
        "Cancel keeps it: you can delete it in Settings at any time.");

    /// <summary>The log on disk can't be unlocked on this computer (see <see cref="ChatLogState.Unreadable"/>).</summary>
    public static Wording Unreadable(string? problem) => new(NoticeKind.General,
        $"Your chat log on this computer can't be read here ({problem ?? "its key can't be unlocked"}), so nothing is added to it. " +
        "It may have been copied from another computer or Windows account. Until it is deleted it still counts towards the size limit, " +
        "so text files only get the room left. Delete it (with its text files) to start a new one.",
        "Your chat history on this computer can't be opened here, so nothing is added to it. It may have been copied from " +
        "another computer or Windows account. Until it is deleted it still counts towards the size limit, so text files only get " +
        "the room left. Delete it (with its text files) to start a new one.");

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
        KeepIt, Explanation("Windows DPAPI"), SizeLimit, KeepUnencrypted, UnencryptedExplanation, UnencryptedWarning, TurnOn, OpenFolder,
        Uses(12_900_000), Delete, DeleteConfirm(52_428_800),
        TurnedOff(800_000), Unreadable("the file that unlocks it is missing"), Unreadable(null), ShowOlder, Loading, NothingOlder,
        Earlier(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero)), SinceLogin,
    ];
}
