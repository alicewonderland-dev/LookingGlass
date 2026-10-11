namespace LookingGlass.Core.Client;

/// <summary>
/// A setting whose name alone doesn't explain it: the Settings window puts a small round "?" after its label, which opens a
/// bubble with <see cref="SettingsWords.Help"/>. A setting its name explains has none.
/// </summary>
public enum SettingHelp {
    ServerAddress,
    ShowMessagesIn,
    WindowsOnly,
    LocalChat,
    LocalPrivacy,
    ChatHistory,
    UnencryptedHistory,
    AdvancedMode,
    ResetIdentity,
    SignedInComputers,
}

/// <summary>
/// The Settings window's words (owner-approved design, 2026-10-09): a short label for each setting, and for those its label
/// doesn't explain, a few sentences in the bubble of its "?" (see <see cref="SettingHelp"/>), instead of tooltips and dimmed
/// paragraphs. Both are shown in simple mode, so they are in its words (see <see cref="Wording"/>). Windows only's name and
/// the chat history's words are with the rest of theirs (<see cref="WindowsOnly"/>, <see cref="ChatLogWords"/>).
/// </summary>
public static class SettingsWords {
    /// <summary>The longest a label may be, in characters, so its row fits the window at its narrowest.</summary>
    public const int MaxLabelLength = 48;

    /// <summary>The longest a "?" bubble may be, in characters: a few short sentences, read at a glance.</summary>
    public const int MaxHelpLength = 180;

    // ---- Server
    public const string ServerAddress = "Server address";
    public const string ConnectAutomatically = "Connect automatically";

    // ---- Chat
    public const string ShowMessagesIn = "Show messages in";
    public const string ColourWholeLine = "Colour the whole line";
    public const string NicknameTags = "Use nicknames in tags";

    /// <summary>Saying "Now talking in" and every "Stopped talking in" in chat (the setting VerboseChannelMessages).</summary>
    public const string SayWhenTalking = "Say when I start or stop talking in a channel";

    /// <summary>Under windows only (<see cref="WindowsOnly.SettingName"/>): how a channel no window shows gets one.</summary>
    public const string NewChannelsOpen = "New channels open";

    // ---- Local chat
    public const string LocalChatHeading = "Local chat";
    public const string LocalColour = "Colour";
    public const string PrivacyAccepted = "Privacy notice accepted";
    public const string PrivacyNotAccepted = "Privacy notice not accepted yet";
    public const string ReadPrivacy = "Read it";
    public const string WithdrawPrivacy = "Withdraw";

    // ---- Your identity
    public const string AdvancedMode = "Advanced mode";
    public const string ResetIdentity = "Reset my identity";

    // ---- Blocked users
    public const string NobodyBlocked = "Nobody blocked. Block someone from a member's menu in a channel.";

    /// <summary>Every label simple mode shows, for the tests: those above, and windows only's and the chat history's.</summary>
    public static IReadOnlyList<string> Labels() => [
        ServerAddress, ConnectAutomatically, ShowMessagesIn, ColourWholeLine, NicknameTags, SayWhenTalking, WindowsOnly.SettingName,
        NewChannelsOpen, WindowsOnly.NameOf(WindowOpening.AddToLastUsed), WindowsOnly.NameOf(WindowOpening.NewWindow), LocalChatHeading,
        LocalColour, PrivacyAccepted, PrivacyNotAccepted, ReadPrivacy, WithdrawPrivacy, ChatLogWords.KeepIt.Plain,
        ChatLogWords.SizeLimit.Plain, ChatLogWords.KeepUnencrypted.Plain, ChatLogWords.OpenFolder.Plain, AdvancedMode, ResetIdentity, DeviceWords.Label, DeviceWords.SignOutButton,
    ];

    /// <summary>What a setting's "?" bubble says, in both modes' words.</summary>
    /// <param name="protection">How the chat history is protected ("Windows DPAPI", "local key file"), for advanced mode.</param>
    public static Wording Help(SettingHelp setting, string protection) => setting switch {
        SettingHelp.ServerAddress => Wording.Same(
            "The LookingGlass server you connect to. Only change it if you were given a different address. Your channels come " +
            "with you if it's the same server."),
        SettingHelp.ShowMessagesIn => Wording.Same(
            "The game chat channel LookingGlass uses, so your chat tabs can show or hide its lines."),
        SettingHelp.WindowsOnly => Wording.Same(WindowsOnly.HelpText),
        SettingHelp.LocalChat => Wording.Same(
            $"{LocalChat.Command} message talks to friends near you who use LookingGlass. {LocalChat.Command} on its own keeps " +
            "talking there until you type /s."),
        SettingHelp.LocalPrivacy => Wording.Same(
            $"To find friends near you, the server learns their names when you use {LocalChat.Command}. It never sees what you say."),
        SettingHelp.ChatHistory => ChatLogWords.Explanation(protection),
        SettingHelp.UnencryptedHistory => ChatLogWords.UnencryptedExplanation,
        // The one bubble simple mode shows that names advanced mode's words: it says what turning it on shows.
        SettingHelp.AdvancedMode => Wording.Same(
            "Shows encryption details, like fingerprints to compare with friends, and technical wording in warnings."),
        SettingHelp.ResetIdentity => Wording.Same(
            "Sets LookingGlass up again for this character, if its files were lost or someone may have copied them. Your " +
            "channels come back when you register again."),
        SettingHelp.SignedInComputers => DeviceWords.Explanation,
        _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, null),
    };
}
