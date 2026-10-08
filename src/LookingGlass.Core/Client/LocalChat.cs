using LookingGlass.Core.Util;

namespace LookingGlass.Core.Client;

/// <summary>A player near this one in the game, as the plugin reads them from the object table.</summary>
/// <param name="Name">Their character's name, as the game shows it.</param>
/// <param name="WorldName">Their home world's name.</param>
/// <param name="Distance">How far they are from this player, in yalms.</param>
/// <param name="IsFriend">On this player's in-game friends list (as the game marks them, or as the list says).</param>
public sealed record NearbyPlayer(string Name, string WorldName, float Distance, bool IsFriend);

/// <summary>What the plugin sees around the player at one moment: the players near them, and whether the game has loaded their friends list.</summary>
/// <param name="FriendsListLoaded">
/// The game holds the player's friends list. It may only fill it in once the Friends window has been opened in a session;
/// until then, a friend near the player is known as one only if the game marks them so.
/// </param>
public sealed record LocalSurroundings(IReadOnlyList<NearbyPlayer> Players, bool FriendsListLoaded) {
    public static readonly LocalSurroundings Nobody = new([], false);
}

/// <summary>Whether a local message that opened and verified is shown, and if not, why (for the diagnostic log's counts).</summary>
public enum LocalVerdict {
    Show,

    /// <summary>The sender's character isn't near this player (or isn't in the same place at all).</summary>
    NotNear,

    /// <summary>The sender is near, but not on this player's friends list.</summary>
    NotFriend,

    /// <summary>The sender is near and not marked as a friend, but the friends list isn't loaded, so it can't be told.</summary>
    FriendsListNotLoaded,
}

/// <summary>
/// Local chat (friends only): a /say-like chat among players who stand near each other and both use LookingGlass. The
/// sender's plugin sends to the friends near them (<see cref="Recipients"/>); the receiving plugin shows a message only if
/// the sender is near and a friend (<see cref="Judge"/>), after the session opened it and checked who signed it. The
/// rules are here, without the game; the plugin reads the object table and the friends list. See "Local chat (friends
/// only)" in docs/design.md.
/// </summary>
public static class LocalChat {
    /// <summary>The command that sends to friends near the player.</summary>
    public const string Command = "/lgl";

    /// <summary>The tag in front of local chat's lines in game chat.</summary>
    public const string Tag = "[Local]";

    /// <summary>
    /// What /lgl does first: until the player has accepted what local chat tells the server (see
    /// <see cref="LocalChatWords.PrivacyNotice"/>), ask, and look nobody up; once accepted, send.
    /// </summary>
    public static LocalChatStep FirstStep(bool privacyAccepted) => privacyAccepted ? LocalChatStep.Send : LocalChatStep.AskFirst;

    /// <summary>
    /// Whether /lgl with these arguments starts talking in local chat (nothing after it, as /lgc3 alone talks in a channel),
    /// rather than sending them. A link alone is something to send.
    /// </summary>
    public static bool StartsTalking(string arguments) => arguments.Trim().Length == 0;

    /// <summary>How far a message reaches, in yalms: about as far as the game's /say (to be checked in game).</summary>
    public const float SayRange = 20f;

    /// <summary>
    /// How far the sender may be from a recipient when it arrives: a little more than <see cref="SayRange"/>, as either may
    /// have moved while it travelled (a sprinting character covers about ten yalms a second).
    /// </summary>
    public const float ReceiveRange = 30f;

    /// <summary>The most friends one message goes to: the closest. The server may allow fewer (Limits.max_local_recipients).</summary>
    public const int MaxRecipients = 50;

    /// <summary>
    /// Whom a message goes to: the friends within <see cref="SayRange"/>, closest first, each name and world once (the
    /// closest of any repeats), at most <paramref name="max"/>.
    /// </summary>
    public static IReadOnlyList<NearbyPlayer> Recipients(LocalSurroundings around, int max) =>
        around.Players
            .Where(player => player.IsFriend && player.Distance <= SayRange && player.Name.Length > 0)
            .OrderBy(player => player.Distance)
            .DistinctBy(player => (player.Name.ToUpperInvariant(), player.WorldName.ToUpperInvariant()))
            .Take(Math.Max(0, max))
            .ToList();

    /// <summary>
    /// Whether a message from <paramref name="senderName"/>@<paramref name="senderWorld"/> (as the session has them) is shown:
    /// only if they are within <see cref="ReceiveRange"/> and on this player's friends list. FFXIV friendships are mutual, so
    /// the sender's own check and this one agree.
    /// </summary>
    public static LocalVerdict Judge(LocalSurroundings around, string senderName, string senderWorld) {
        var sender = around.Players
            .Where(player => player.Distance <= ReceiveRange
                             && string.Equals(player.Name, senderName, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(player.WorldName, senderWorld, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(player => player.IsFriend)
            .FirstOrDefault();
        if (sender == null) {
            return LocalVerdict.NotNear;
        }

        return sender.IsFriend ? LocalVerdict.Show
            : around.FriendsListLoaded ? LocalVerdict.NotFriend
            : LocalVerdict.FriendsListNotLoaded;
    }

    /// <summary>
    /// Local chat's colour from its two settings, as a channel's is kept: a custom colour (0xRRGGBB) wins over a UIColor row
    /// (row 0 is none); neither is the default, which colours only the tag, in LookingGlass blue.
    /// </summary>
    public static ChannelColour? ColourOf(ushort row, uint? custom) =>
        custom is { } rgb ? ChannelColour.Custom(rgb) : row != 0 ? ChannelColour.OfRow(row) : null;

    /// <summary>
    /// Why there is nobody to send to, or null if there is someone (see <see cref="Recipients"/>): nobody near, nobody near
    /// on the friends list, or, with the friends list not loaded and only players not marked as friends near, to open it once.
    /// </summary>
    public static Wording? NobodyToSendTo(LocalSurroundings around) {
        if (Recipients(around, 1).Count > 0) {
            return null;
        }

        if (!around.Players.Any(player => player.Distance <= SayRange)) {
            return LocalChatWords.NobodyNear;
        }

        return around.FriendsListLoaded ? LocalChatWords.NoFriendsNear : LocalChatWords.OpenFriendsList;
    }
}

/// <summary>How a local message went: how many friends got a copy, how many don't use LookingGlass here, and how many couldn't be checked.</summary>
public sealed record LocalSendResult(int Sent, int NotUsingIt, int CouldntCheck);

/// <summary>Why a local message couldn't be checked, and so wasn't shown (see <see cref="LocalUnchecked"/>).</summary>
public enum LocalUncheckedReason {
    /// <summary>It came with other keys than those held for its sender: perhaps they set up LookingGlass again.</summary>
    KeysChanged,

    /// <summary>It checked out, but the sender's keys are held under another name or world: perhaps a rename or a world transfer.</summary>
    Renamed,

    /// <summary>It came from an account other than the one held under the name it gives.</summary>
    NameHeldByAnother,
}

/// <summary>
/// A local message that couldn't be checked against what is held for its sender, so it isn't shown: the sender as the server
/// names them, and why. Never its content. The plugin says so once a session per name, and only if they are near and a
/// friend (<see cref="LocalHints"/>). The name is the server's word: a hint never tells the player to accept new keys.
/// </summary>
public sealed record LocalUnchecked(Protocol.User Sender, LocalUncheckedReason Reason) {
    /// <summary>
    /// For <see cref="LocalUncheckedReason.KeysChanged"/>: the name and world held for the account (as "Name@World"), if any, so
    /// <see cref="ClientSession.ForgetLookupAfterHint"/> can tell whether the server gave that very name.
    /// </summary>
    internal string? HeldAs { get; init; }
}

/// <summary>What <see cref="ClientSession.ConfirmLocalSender"/> says about showing a local message.</summary>
public enum LocalConfirmation {
    /// <summary>Show it.</summary>
    Show,

    /// <summary>It was shown already, or is older than one shown from its sender: drop it.</summary>
    Replayed,

    /// <summary>Other keys than the ones it was checked against are held for its sender by now: hint, don't show.</summary>
    OtherKeysHeld,

    /// <summary>Another account is held under the name it gives: hint, don't show, and hold nothing for it.</summary>
    NameHeldByAnother,
}

/// <summary>
/// The hints about local messages that couldn't be checked (see <see cref="LocalUnchecked"/>): one line per name a session,
/// at most <see cref="MaxPerSession"/>, and only for a sender near the player and on their friends list, so a stranger (or a server naming anyone it
/// likes) gets nothing said. Kept by the plugin for a session, on the framework thread.
/// </summary>
public sealed class LocalHints {
    /// <summary>The most hints a session: a server naming one friend after another can't fill the chat with them.</summary>
    public const int MaxPerSession = 5;

    // The names (Name@World, upper case) hinted at this session. By name, not by account: the name is what is shown and judged,
    // and it is the server's word, so other accounts under one name get one hint.
    private readonly HashSet<string> _told = new();

    /// <summary>The line to show, or null if nothing is said (not near, not a friend, said already this session, or enough said).</summary>
    public Wording? For(LocalUnchecked unchecked_, LocalVerdict verdict) {
        var who = $"{TextSanitizer.Name(unchecked_.Sender.Name)}@{TextSanitizer.Name(unchecked_.Sender.WorldName)}";
        return verdict == LocalVerdict.Show && this._told.Count < MaxPerSession && this._told.Add(who.ToUpperInvariant())
            ? LocalChatWords.Unchecked(who, unchecked_.Reason)
            : null;
    }

    /// <summary>A new session: every sender may be hinted at again.</summary>
    public void Clear() => this._told.Clear();
}

/// <summary>
/// What local chat says, in both modes' words (see <see cref="Wording"/>). Nothing in it is technical, so each is the same in
/// both. Everything is information (LookingGlass blue): nothing went anywhere it shouldn't.
/// </summary>
/// <summary>See <see cref="LocalChat.FirstStep"/>.</summary>
public enum LocalChatStep {
    /// <summary>Show the privacy notice, and send nothing.</summary>
    AskFirst,

    /// <summary>Look the friends near up and send.</summary>
    Send,
}

public static class LocalChatWords {
    private const string Range = "about 20 yalms, as far as /say";

    /// <summary>The privacy notice's title (see <see cref="PrivacyNotice"/>).</summary>
    public const string PrivacyTitle = "Local chat: what the server learns";

    /// <summary>
    /// Shown once, before the first /lgl looks anyone up, to accept or not (owner decision, 2026-10-08): to find which
    /// friends near the player use LookingGlass, the server is asked about them by name, so it learns who was near, friends
    /// who don't use LookingGlass too. Paragraphs are separated by a blank line.
    /// </summary>
    public static readonly Wording PrivacyNotice = new(
        NoticeKind.General,
        Technical: $"To find which of your friends near you use LookingGlass, {LocalChat.Command} asks the LookingGlass server " +
                   "about them by name (each friend at most once every 10 minutes), and fetches the identity keys of those who do.\n\n" +
                   "So the server learns the names of friends near you when you use it, including friends who don't use " +
                   "LookingGlass, and who you send local messages to and when.\n\n" +
                   "It never sees what you say: each message is encrypted for each friend. LookingGlass's server keeps no record " +
                   "of these lookups, but whoever runs a server could change that.\n\n" +
                   "Receiving local messages from friends needs none of this.",
        Plain: $"To find which of your friends near you use LookingGlass, {LocalChat.Command} asks the LookingGlass server " +
               "about them by name (each friend at most once every 10 minutes).\n\n" +
               "So the server learns the names of friends near you when you use it, including friends who don't use " +
               "LookingGlass, and who you send local messages to and when.\n\n" +
               "It never sees what you say: only your friends can read it. LookingGlass's server keeps no record of these " +
               "lookups, but whoever runs a server could change that.\n\n" +
               "Receiving local messages from friends needs none of this.");

    /// <summary>Said where /lgl was typed while the privacy notice hasn't been accepted: nothing was sent.</summary>
    public static readonly Wording PrivacyAsked = Wording.Same(
        "Not sent: local chat first asks you to accept what it tells the LookingGlass server. See the window that opened.");

    /// <summary>
    /// Said where /lgl alone was typed while the privacy notice hasn't been accepted: talking in local chat didn't start
    /// (see <see cref="StickyChannel.Enter"/>).
    /// </summary>
    public static readonly Wording PrivacyAskedToTalk = Wording.Same(
        $"Local chat first asks you to accept what it tells the LookingGlass server. See the window that opened, then type {LocalChat.Command} again.");

    /// <summary>
    /// Said once the privacy notice is accepted, asked by /lgl with a message (which wasn't kept, so it is sent again) or
    /// by /lgl alone (which didn't start talking in local chat).
    /// </summary>
    public static readonly Wording PrivacyAccepted = Wording.Same(
        $"Local chat is on. Type {LocalChat.Command} <message> again to send your message, or {LocalChat.Command} alone to talk in local chat.");

    private const string OpenItOnce = "open your friends list once (Social menu, Friend List)";

    /// <summary>How to use /lgl: Dalamud's command help and Settings show it.</summary>
    public static readonly Wording Usage = Wording.Same(
        $"{LocalChat.Command} <message> talks to your friends near you who use LookingGlass ({Range}). Only players on your friends " +
        $"list get it, and only they can read it. {LocalChat.Command} alone sends everything you type there, until you type /s " +
        "(or another channel) on its own.");

    /// <summary>
    /// Said once ever, the first time talking in local chat starts (with /lgl alone, which used to explain itself): where
    /// typing goes now, and how to stop.
    /// </summary>
    public static readonly Wording TalkingNote = Wording.Same(
        $"Typing now goes to {LocalChat.Tag}, your friends near you who use LookingGlass, and never to game chat. Type /s (or another " +
        "channel) on its own to stop.");

    /// <summary>The note to add when talking in local chat starts, or null: only if never shown before.</summary>
    public static Wording? TalkingNoteFor(bool shownBefore) => shownBefore ? null : TalkingNote;

    public static readonly Wording NobodyNear = Wording.Same($"Not sent: nobody is near enough to hear you ({Range}).");

    public static readonly Wording NoFriendsNear = Wording.Same("Not sent: none of the players near you is on your friends list. Local chat only goes to friends.");

    public static readonly Wording OpenFriendsList = Wording.Same(
        $"Not sent: nobody near you shows as a friend. The game may not have loaded your friends list yet: {OpenItOnce}, then try again.");

    /// <summary>Said once a session, when a local message from a player near was dropped because the friends list isn't loaded.</summary>
    public static readonly Wording OpenFriendsListToReceive = Wording.Same(
        "A player near you sent you a local message, but the game hasn't loaded your friends list yet, so LookingGlass can't tell " +
        $"whether they're your friend. To see local messages from friends, {OpenItOnce}.");

    public static readonly Wording NobodyUsesIt = Wording.Same("Not sent: none of your friends near you uses LookingGlass on this server.");

    public static readonly Wording NotOnThisServer = Wording.Same(
        "Local chat isn't available on this server: it may be an older version, or its operator turned it off.");

    /// <summary>What to say once a message has gone to the server, if anything: null when every friend near got it.</summary>
    public static Wording? Sent(LocalSendResult result) => result switch {
        { Sent: 0, CouldntCheck: 0 } => NobodyUsesIt,
        { Sent: 0 } => Wording.Same("Not sent: LookingGlass couldn't check whether your friends near you use it. Wait a moment and try again."),
        { CouldntCheck: > 0 } => Wording.Same(
            $"Sent, but {result.CouldntCheck} of your friends near you couldn't be checked and didn't get it. Wait a moment and try again."),
        _ => null,
    };

    /// <summary>
    /// A local message from <paramref name="who"/> wasn't shown because it couldn't be checked (see <see cref="LocalUnchecked"/>):
    /// information, with what may have happened and what to do. Advanced mode names the keys.
    /// </summary>
    public static Wording Unchecked(string who, LocalUncheckedReason reason) => reason switch {
        LocalUncheckedReason.KeysChanged => new Wording(NoticeKind.General,
            $"{who} sent you a local message under other identity keys than the ones held for them, so it wasn't shown: they may have " +
            "registered again, or someone (the server, even) may be passing themselves off as them. Check with them over /tell before " +
            "trusting new keys for them.",
            $"{who} sent you a local message that couldn't be checked: they may have set up LookingGlass again, or someone else may be " +
            "using their name. Check with them over /tell before you trust it."),
        LocalUncheckedReason.Renamed => new Wording(NoticeKind.General,
            $"{who} sent you a local message, but their identity keys are held under another name or world, so it wasn't shown: they may " +
            "have changed their name or world. Send them a local message with /lgl, or share a channel, to update the name held.",
            $"{who} sent you a local message that couldn't be checked: they may have changed their name or world. Talk to them with /lgl, " +
            "or share a channel, to update it."),
        _ => new Wording(NoticeKind.General,
            $"A local message from {who} wasn't shown: it came from another account than the one whose keys are held under that name. " +
            "If you think it was them, check with them over /tell.",
            $"A local message from {who} couldn't be checked: LookingGlass knows someone else by that name. If you think it was them, " +
            "check with them over /tell."),
    };

    /// <summary>Every wording here, for the check that none uses jargon.</summary>
    internal static IEnumerable<Wording> All() {
        yield return Usage;
        yield return TalkingNote;
        yield return PrivacyAskedToTalk;
        yield return PrivacyAccepted;
        yield return NobodyNear;
        yield return NoFriendsNear;
        yield return OpenFriendsList;
        yield return OpenFriendsListToReceive;
        yield return NobodyUsesIt;
        yield return NotOnThisServer;
        yield return Sent(new LocalSendResult(0, 0, 3))!;
        yield return Sent(new LocalSendResult(2, 0, 1))!;
        foreach (var reason in Enum.GetValues<LocalUncheckedReason>()) {
            yield return Unchecked("Bob Hatter@Lich", reason);
        }
    }
}
