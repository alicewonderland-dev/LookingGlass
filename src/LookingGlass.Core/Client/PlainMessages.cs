namespace LookingGlass.Core.Client;

/// <summary>
/// What the user is told, in both modes (see <see cref="Wording"/>): the technical words advanced mode shows (keys,
/// fingerprints, the membership log), and the everyday words of simple mode, the default. Every warning has both, so
/// simple mode says what happened and what to do without the jargon, and never leaves a warning out.
///
/// In simple mode a user's identity keys are "their LookingGlass" or "setup": a key that changed without explanation is
/// "set up LookingGlass again (new computer or reset), or someone else may be using their name", a key recovered entry is "set up
/// LookingGlass again (new computer or reset)", and what to do is nearly always "check with them over /tell".
/// </summary>
public static class PlainMessages {
    // ================================================================ someone re-verifying their character, and the user's own places

    /// <summary>After someone's name, in their channels: a key recovered entry moved their place to a new key.</summary>
    public const string ReVerified = "re-verified their character and has a new key.";

    /// <summary>
    /// The name a channel gets when the user, back with new keys, is its only member: nobody can tell them its name (it was
    /// encrypted under keys their old identity held). The admin can rename it.
    /// </summary>
    public const string RestoredChannelName = "Restored channel";

    /// <summary>Told after registering, when the account's places moved to the new key.</summary>
    public static string PlacesRestored(uint count) => PlacesRestoredWording(count).Technical;

    /// <inheritdoc cref="PlacesRestored"/>
    public static Wording PlacesRestoredWording(uint count) => new(NoticeKind.PlacesRestored,
        $"Welcome back: your channels and invites here ({count}) were restored with your new key, ranks and all, because you re-verified your " +
        "character through the Lodestone. Their members are told. Each channel works again as soon as a member who is online shares its new " +
        "key with you.",
        $"Welcome back: your channels and invites here ({count}) were restored, ranks and all, because you registered your character again " +
        "through the Lodestone. Their members are told. Each channel works again as soon as another member who is online lets you back in, " +
        "which happens by itself.");

    /// <summary>
    /// After someone's name, when their place moving replaced a key pinned for them that the user had compared over /tell: the
    /// comparison was for the old key.
    /// </summary>
    public const string ComparedBefore = "You had compared fingerprints with them before; that was for their old key, so compare the new one over /tell.";

    /// <summary>
    /// Someone's place moved to new keys (a key recovered entry): they re-verified their character, as the server says.
    /// </summary>
    /// <param name="who">Their name@world.</param>
    /// <param name="wasCompared">The user had compared (marked verified) the keys this replaces.</param>
    public static Wording ReVerifiedWording(string who, bool wasCompared) => new(NoticeKind.ReVerified,
        $"{who} {ReVerified}" + (wasCompared ? $" {ComparedBefore}" : ""),
        $"{who} set up LookingGlass again (new computer or reset). " + (wasCompared
            ? "You had confirmed it was really them before, so check with them again over /tell."
            : "If you didn't expect that, check with them over /tell."));

    /// <summary>
    /// What to do if a re-verification wasn't the user's own: "Reset my identity" makes new keys and registers them through
    /// the Lodestone, which moves every place of theirs to those (see <see cref="ReVerifiedElsewhere"/>). Plain words already.
    /// </summary>
    private const string IfItWasntYou =
        "If that wasn't you, use \"Reset my identity\" in Settings: it re-verifies you through the Lodestone and takes your channels back.";

    /// <summary>
    /// Told when a channel's log moves this user's own place away from the identity key this client holds: their character
    /// was re-verified through the Lodestone with another key. Normally that was them, on another computer.
    /// </summary>
    public const string ReVerifiedElsewhere =
        "Your character was re-verified through the Lodestone with a different identity key, probably on another computer, so your channels " +
        "here moved to that key and this computer's key can't use them any more. " + IfItWasntYou;

    /// <inheritdoc cref="ReVerifiedElsewhere"/>
    public static readonly Wording ReVerifiedElsewhereWording = new(NoticeKind.ReVerifiedElsewhere, ReVerifiedElsewhere,
        "Your character was registered with LookingGlass again somewhere else, probably on another computer, so your channels here moved " +
        "there and this computer can't use them any more. " + IfItWasntYou);

    /// <summary>Shown on a channel where that happened (see <see cref="ChannelView.KeyMovedAway"/>).</summary>
    public const string KeyMovedAwayChannel =
        "Your place in this channel moved to a different identity key: your character was re-verified through the Lodestone with it, probably " +
        "on another computer. This computer's key can't read or send here any more. " + IfItWasntYou;

    /// <inheritdoc cref="KeyMovedAwayChannel"/>
    public static readonly Wording KeyMovedAwayWording = new(NoticeKind.KeyMovedAway, KeyMovedAwayChannel,
        "Your place in this channel moved to your LookingGlass somewhere else: your character was registered again, probably on another " +
        "computer. This computer can't read or send here any more. " + IfItWasntYou);

    /// <summary>
    /// Added where the server refuses this client's login and identity key: the usual reason, since registering through the
    /// Lodestone with new keys moves the account's channels and shuts the old key out.
    /// </summary>
    public const string LoginMaybeReplaced =
        "If this character was re-verified through the Lodestone on another computer, this computer's identity key was replaced and your " +
        "channels moved to the new one. " + IfItWasntYou;

    /// <inheritdoc cref="LoginMaybeReplaced"/>
    public static readonly Wording LoginMaybeReplacedWording = new(NoticeKind.LoginNotRecognized, LoginMaybeReplaced,
        "If this character was registered again through the Lodestone on another computer, your channels moved there and this computer " +
        "can't sign in any more. " + IfItWasntYou);

    /// <summary>The session's status while the server refuses its saved login and identity key.</summary>
    public static readonly Wording LoginNotRecognized = new(NoticeKind.LoginNotRecognized,
        "This server doesn't recognise your login or your identity key. If you changed the server address, check it in Settings. " +
        "You only need to register again (through the Lodestone) if your identity key was lost or replaced, or this server has never known your account; " +
        "otherwise your login is tried again by itself. " + LoginMaybeReplaced,
        "This server doesn't recognise your login. If you changed the server address, check it in Settings. " +
        "You only need to register again (through the Lodestone) if your LookingGlass was reset or its files were lost, or this server has never " +
        "known you; otherwise your login is tried again by itself. " + LoginMaybeReplacedWording.Plain);

    /// <summary>"Reset my identity" is done, and the server retired the old key first.</summary>
    public static Wording IdentityReset(string serverUrl) => Plainly(
        $"Your identity on {serverUrl} was reset, and the server no longer accepts your old key or any login made with it. " +
        "Register again (through the Lodestone) to use your new key: your channels, ranks and invites come along with it.",
        $"Your identity on {serverUrl} was reset, and the server no longer accepts your old LookingGlass setup. " +
        "Register again (through the Lodestone): your channels, ranks and invites come along.");

    /// <summary>"Reset my identity" is done here, but the server couldn't be told to retire the old key.</summary>
    /// <param name="why">Why not.</param>
    public static Wording IdentityResetNotRetired(string serverUrl, string why) => new(NoticeKind.IdentityRetired,
        $"Your identity on {serverUrl} was reset here, but the server couldn't be told ({why}), so your old key and login " +
        "keep working there until you register again with the new key, which brings your channels along. Register as soon as you can.",
        $"Your identity on {serverUrl} was reset here, but the server couldn't be told ({why}), so your old LookingGlass setup keeps " +
        "working there until you register again, which brings your channels along. Register as soon as you can.");

    /// <summary>The server retired the old key, but making new keys here failed.</summary>
    public static Wording IdentityResetFailed(string serverUrl, string why) => new(NoticeKind.IdentityRetired,
        $"The server at {serverUrl} retired your old key, but resetting your keys here failed ({why}). " +
        "Try \"Reset my identity\" again: until you do, you can't register or sign in there.",
        $"The server at {serverUrl} no longer accepts your old LookingGlass setup, but resetting it here failed ({why}). " +
        "Try \"Reset my identity\" again: until you do, you can't register or sign in there.");

    /// <summary>The secrets file couldn't be read.</summary>
    public static Wording CouldntLoadKeys(string why) => Plainly($"Couldn't load your keys: {why}", $"Couldn't load your LookingGlass setup: {why}");

    /// <summary>The session's status after the server retired this client's identity key ("Reset my identity", first step).</summary>
    public static readonly Wording IdentityRetired = new(NoticeKind.IdentityRetired,
        "Your identity key was retired on this server. Reset your identity, then register the new keys.",
        "This server no longer accepts your old LookingGlass setup. Reset your identity, then register again.");

    /// <summary>Shown on a channel whose place belongs to the old key (see <see cref="ChannelView.OldKeyMembership"/>).</summary>
    public const string OldKeyChannel =
        "Your place in this channel belongs to an identity key you no longer have, from before registering again brought your channels along " +
        "to the new key. Your current key isn't a member, so you can't read, send or leave here. Use \"Remove from my list\" in the channel's " +
        "menu to take it off your list. To come back, a moderator can remove your old key and invite you again; or reset your identity " +
        "(Settings) and register again, which brings every channel you're still listed in along to your new key, this one too.";

    /// <inheritdoc cref="OldKeyChannel"/>
    public static readonly Wording OldKeyChannelWording = new(NoticeKind.OldKeyPlace, OldKeyChannel,
        "Your place in this channel belongs to your old LookingGlass setup, from before you registered again, so you can't read, send or " +
        "leave here. Use \"Remove from my list\" in the channel's menu to take it off your list. To come back, ask a moderator to remove " +
        "your old place and invite you again; or reset your identity (Settings) and register again, which brings along every channel " +
        "you're still listed in, this one too.");

    /// <summary>Why Leave can't work there.</summary>
    public const string CantLeaveOldKeyChannel =
        "You can't leave this channel: your place in it belongs to an identity key you no longer have, and only that key could sign leaving. " +
        "Use \"Remove from my list\" in the channel's menu instead.";

    /// <inheritdoc cref="CantLeaveOldKeyChannel"/>
    public static readonly Wording CantLeaveOldKeyWording = new(NoticeKind.CantLeaveOldKeyPlace, CantLeaveOldKeyChannel,
        "You can't leave this channel: your place in it belongs to your old LookingGlass setup, and only that could leave. " +
        "Use \"Remove from my list\" in the channel's menu instead.");

    /// <summary>Why nothing else that changes the members can work there either.</summary>
    public const string OldKeyCantChangeMembers =
        "Your place in this channel belongs to an identity key you no longer have, so your current key can't change anything here. " +
        "Use \"Remove from my list\" in the channel's menu to take it off your list.";

    /// <inheritdoc cref="OldKeyCantChangeMembers"/>
    public static readonly Wording OldKeyCantChangeMembersWording = new(NoticeKind.OldKeyCantChangeMembers, OldKeyCantChangeMembers,
        "Your place in this channel belongs to your old LookingGlass setup, so you can't change anything here. " +
        "Use \"Remove from my list\" in the channel's menu to take it off your list.");

    /// <summary>Accepting or declining an invite made for keys this user no longer has.</summary>
    public static readonly Wording InviteForOldKey = new(NoticeKind.InviteForOldKey,
        "That invite was made for an identity key you no longer have. Ask to be invited again.",
        "That invite was for your old LookingGlass setup, from before you registered again. Ask to be invited again.");

    /// <summary>"Remove from my list" on a channel this user is a member of under their current keys.</summary>
    public static readonly Wording ForgetMembership = Plainly(
        "You're a member of this channel with your current identity key: leave it instead.",
        "You're a member of this channel: leave it instead.");

    /// <summary>Inviting someone who is a member under keys they no longer have.</summary>
    public static Wording MemberUnderOldKey(string who) => new(NoticeKind.OldKeyPlace,
        $"{who} is a member under an identity key they no longer have. Remove them, then invite them again.",
        $"{who} set up LookingGlass again, and their old place is still in this channel. Remove them, then invite them again.");

    // ================================================================ other people's keys

    /// <summary>Someone's keys changed without the channel's membership saying why.</summary>
    /// <param name="who">Their name@world.</param>
    /// <param name="fingerprint">Of the new keys.</param>
    public static Wording KeyChanged(string who, string fingerprint) => new(NoticeKind.KeyChanged,
        $"{who}'s identity key changed (they may have re-registered). Compare fingerprints over /tell before trusting it: {fingerprint}",
        $"{who} set up LookingGlass again (new computer or reset), or someone else may be using their name. If you didn't expect that, check with " +
        "them over /tell before trusting them.");

    /// <summary>A name and world first seen with another account.</summary>
    public static Wording NameNowAnotherAccount(string who, string fingerprint) => new(NoticeKind.NameNowAnotherAccount,
        $"{who} now belongs to a different account than the one you saw before. Compare fingerprints over /tell before trusting it: {fingerprint}",
        $"{who} is now a different LookingGlass account from the one you saw before: they may have set it up again, or someone else may be " +
        "using the name. If you didn't expect that, check with them over /tell before trusting them.");

    /// <summary>A rename or world transfer, with the same keys.</summary>
    public static Wording Renamed(string before, string who) => new(NoticeKind.Renamed,
        $"{before} is now shown as {who} (a rename or world transfer). Their keys are unchanged.",
        $"{before} is now shown as {who} (a rename or world transfer). It's still the same account.");

    /// <summary>A short warning about members whose keys changed, for a channel at a glance.</summary>
    /// <param name="names">Their names, joined.</param>
    public static Wording KeysChangedIn(string names) => new(NoticeKind.KeyChanged,
        $"Key changed: {names}. Compare fingerprints.",
        $"{names}: set up LookingGlass again (new computer or reset), or someone else may be using the name. Check with them over /tell.");

    /// <summary>A short warning about members who registered again, for a channel at a glance.</summary>
    public static Wording RegisteredAgainIn(string names) => new(NoticeKind.RegisteredAgain,
        $"Registered again: {names}. Remove them and invite them again to let their new key in.",
        $"Set up LookingGlass again: {names}. They can't read or send here until a moderator removes them and invites them again.");

    /// <summary>An invite from someone whose key changed.</summary>
    /// <param name="channel">The channel's name, quoted, or "a channel".</param>
    public static Wording InviteFromChangedKey(string who, string channel) => new(NoticeKind.InviteFromChangedKey,
        $"{who} invited you to {channel}, but their identity key changed. Compare fingerprints over /tell before accepting.",
        $"{who} invited you to {channel}, but they set up LookingGlass again (new computer or reset), or someone else may be using their name. " +
        "Check with them over /tell before accepting.");

    /// <summary>An invite that didn't check out.</summary>
    public static Wording InviteUnverified(string who) => new(NoticeKind.InviteUnverified,
        $"{who} sent an invite that failed verification.",
        $"{who} sent an invite that couldn't be checked as really theirs, so it can't be accepted.");

    /// <summary>A channel's name changed in a rekey, which any member makes; only the admin renames.</summary>
    public static Wording NameChangedWhileRekeying(string who, string before, string after) => new(NoticeKind.NameChangedWhileRekeying,
        $"{who} changed the channel name from \"{before}\" to \"{after}\" while rekeying.",
        $"{who} changed the name of this channel from \"{before}\" to \"{after}\". Normally only the admin renames a channel, so if you " +
        "didn't expect this, check with them over /tell.");

    /// <summary>A member sent this user a channel key that's no use.</summary>
    /// <param name="problem">What is wrong with it, in technical words.</param>
    /// <param name="rekey">This client makes a new key now.</param>
    public static Wording BadChannelKey(string author, string channel, ulong epoch, string problem, bool rekey) => new(NoticeKind.BadChannelKey,
        $"{author} sent you a key for {channel} (epoch {epoch}) that {problem}. They may be trying to cut you off or split the channel{(rekey ? "; rekeying it now." : ".")}",
        $"{author} sent you an update for {channel} that doesn't work for you. They may be trying to cut you off or split the channel" +
        (rekey ? "; LookingGlass is putting it right now." : ". If it keeps happening, check with the other members over /tell."));

    /// <summary>A channel key was refused.</summary>
    /// <param name="reason">Why, in technical words.</param>
    /// <param name="channel">The channel's name.</param>
    public static Wording ChannelKeyRejected(string reason, string channel) => new(NoticeKind.ChannelKeyRejected,
        $"Rejected a new key for a channel: {reason}.",
        $"LookingGlass refused an update to {channel} that didn't check out. If this keeps happening, the server or one of its members may " +
        "be up to something: check with the other members over /tell.");

    /// <summary>A rekey can't seal the key to a member.</summary>
    public static Wording CantSealTo(string who, string? detail) => new(NoticeKind.CantSealTo,
        $"Can't rekey: the key couldn't be sealed to {who}'s identity key ({detail}). They need to be removed.",
        $"The channel can't be updated: {who}'s LookingGlass setup is broken. A moderator needs to remove them.");

    /// <summary>Marking someone verified, after the keys held for them changed since they were shown.</summary>
    public static readonly Wording VerifiedKeyChanged = new(NoticeKind.VerifiedKeyChanged,
        "That fingerprint isn't the identity key held for them now: they registered again, or their key changed since it was shown. Nothing was marked verified.",
        "Their LookingGlass changed again since this was shown, so nothing was confirmed. Check with them again.");

    // ================================================================ messages that were dropped

    public static Wording MessageFromNonMember(string channel, string who) =>
        Wording.Same($"Dropped a message in {channel} from {who}, who isn't a member of it.", NoticeKind.MessageFromNonMember);

    public static Wording MessageWithoutKey(string sender) => new(NoticeKind.MessageWithoutKey,
        $"Couldn't decrypt a message from {sender}: no key for that epoch yet.",
        $"Couldn't read a message from {sender}: this channel isn't up to date for you yet.");

    public static Wording MessageTooLate(string sender) => new(NoticeKind.MessageTooLate,
        $"Dropped a message from {sender}: it uses an older key that was replaced a while ago.",
        $"Dropped a message from {sender}: it was sent before a change to the channel, and arrived too late.");

    public static Wording MessageFromNewSetup(string sender, string? channel) => new(NoticeKind.MessageFromNewSetup,
        $"Dropped a message from {sender}: it's signed with the identity key they registered again with, which isn't a member of {channel} " +
        "until a moderator removes them and invites them again.",
        $"Dropped a message from {sender}: they set up LookingGlass again, and their new setup isn't a member of {channel} until a moderator " +
        "removes them and invites them again.");

    public static Wording MessageFailedChecks(string sender) => new(NoticeKind.MessageFailedChecks,
        $"Dropped a message claiming to be from {sender}: it failed signature or decryption checks.",
        $"Dropped a message claiming to be from {sender}: it couldn't be checked as really theirs.");

    public static Wording MessageClockSkew(string sender, string dated) => new(NoticeKind.MessageClockSkew,
        $"Dropped a message from {sender} dated {dated}: too far from the current time (replayed, or a wrong clock).",
        $"Dropped a message from {sender} dated {dated}: that's too far from the current time (an old message sent again, or a wrong clock).");

    public static Wording MessageReplayed(string sender, string dated) => new(NoticeKind.MessageReplayed,
        $"Dropped a message from {sender} dated {dated}: it's older than messages already received from them (replayed?).",
        $"Dropped a message from {sender} dated {dated}: it's older than messages you already have from them, so it may be an old message sent again.");

    /// <summary>
    /// Message catch-up: some of the messages the server sent from while the user was away didn't pass the checks every
    /// message must (from someone who wasn't a member, under a key this client never held, or not really from who it says).
    /// Said once per channel and catch-up, however many, rather than once per message.
    /// </summary>
    public static Wording MessagesNotCaughtUp(string channel, int count) => new(NoticeKind.MessagesNotCaughtUp,
        count == 1
            ? $"1 message the server sent from while you were away, in {channel}, was dropped: it failed signature or decryption checks, was under a key you never held, or isn't from a member."
            : $"{count} messages the server sent from while you were away, in {channel}, were dropped: they failed signature or decryption checks, were under keys you never held, or aren't from members.",
        count == 1
            ? $"1 message from while you were away, in {channel}, isn't shown: it couldn't be checked as really from a member who could send it then."
            : $"{count} messages from while you were away, in {channel}, aren't shown: they couldn't be checked as really from members who could send them then.");

    /// <summary>
    /// Message catch-up left out messages that passed every check, because their senders' keys have stopped since (they
    /// left, were removed, or set up LookingGlass again) and nothing that neither they nor the server chose says when, so
    /// it can't be told those messages came before. Information, not a warning: said once per channel and login.
    /// </summary>
    /// <param name="senders">Each sender (name@world) and the kind of entry that stopped their keys.</param>
    public static Wording MessagesNotConfirmed(string channel, IReadOnlyList<(string Who, Protocol.MembershipEntryKind How)> senders) {
        if (senders.Count == 1) {
            var (who, how) = senders[0];
            var (technical, plain) = how switch {
                Protocol.MembershipEntryKind.Leave => ("they left", $"before leaving {channel}"),
                Protocol.MembershipEntryKind.Remove => ("they were removed", $"before being removed from {channel}"),
                _ => ("they re-verified their character with new keys", $"in {channel} before setting up LookingGlass again"),
            };
            return new Wording(NoticeKind.MessagesNotConfirmed,
                $"Some messages {who} sent in {channel} while you were away were left out: the keys they were signed with stopped being theirs ({technical}), " +
                "and nothing that neither they nor the server chose says when, so it can't be confirmed they were sent before.",
                $"Some messages {who} sent {plain} couldn't be confirmed, so they weren't restored.");
        }

        var names = string.Join(", ", senders.Take(senders.Count - 1).Select(sender => sender.Who)) + " and " + senders[^1].Who;
        return new Wording(NoticeKind.MessagesNotConfirmed,
            $"Some messages {names} sent in {channel} while you were away were left out: the keys they were signed with have stopped being theirs " +
            "(they left, were removed, or re-verified with new keys), and nothing that neither they nor the server chose says when.",
            $"Some messages {names} sent in {channel} before leaving or setting up LookingGlass again couldn't be confirmed, so they weren't restored.");
    }

    /// <summary>
    /// A membership change verified as it happened is dated further ahead of this computer's clock than a live message may
    /// be: whoever dated it (its signer, or the server for a re-verification) is misdating it, perhaps so that old keys seem
    /// to be allowed to speak for longer. It is dated by when it was seen instead.
    /// </summary>
    public static Wording MembershipChangeDatedAhead(string channel) => new(NoticeKind.MembershipChangeDatedAhead,
        $"A membership change in {channel} is dated in the future (its signer, or the server for a re-verification, misdated it). " +
        "LookingGlass dates it by when you saw it, so it can't let a replaced key's messages from while you were away pass for longer.",
        $"A change to who is in {channel} is dated in the future, which can't be right: it may be a mistake, or someone trying to make " +
        "messages look older or newer than they are. LookingGlass goes by when you saw the change instead.");

    // ================================================================ servers

    /// <summary>A server that lists its own addresses, without the one this client uses.</summary>
    public static Wording AddressNotListed(string listed, string address) => new(NoticeKind.AddressNotListed,
        $"This server's addresses are {listed}, and the one you connect to, {address}, isn't one of them, " +
        "so registering, signing in with your identity key and \"Reset my identity\" won't work through it. " +
        "Set the server address in Settings to one of those (ask the server's operator if none works for you).",
        $"This server's addresses are {listed}, and the one you connect to, {address}, isn't one of them, " +
        "so registering, signing back in and \"Reset my identity\" won't work through it. " +
        "Set the server address in Settings to one of those (ask the server's operator if none works for you).");

    /// <summary>A server that sent another server's registration code (see <see cref="RelayedRegistrationCodeException"/>). Plain words already.</summary>
    public static readonly Wording RelayedRegistrationCode = Wording.Same(
        "This server sent a registration code that doesn't belong to it. It may be passing on another server's code. " +
        "Don't put it in your Lodestone profile.", NoticeKind.RelayedRegistrationCode);

    public static readonly Wording IdentitiesMissing = new(NoticeKind.IdentitiesMissing,
        "The server didn't send some members' identity keys, so key changes may go unnoticed for now.",
        "The server didn't send everything needed to check your channels' members, so a warning about someone may be missed for now.");

    // What to do about a channel's membership the server may be hiding something about, in plain words.
    private const string CheckMembers = "Check with other members over /tell before trusting who's in it";

    // The ones below are formats, {0} the channel's name, without a capital or full stop: see ClientSession.WarnAboutMembership.
    private const string HidingSuffix = "It may be hiding a change (such as someone's removal) from other members";

    /// <summary>The server won't show the membership as this client verified it. A format: {0} is the channel.</summary>
    public static readonly Wording MembershipHidden = new(NoticeKind.MembershipHidden,
        "the server won't show the membership of {0} as you have verified it. " + HidingSuffix,
        "the server isn't showing the members of {0} the way you last saw them. " + HidingSuffix + ". " + CheckMembers);

    /// <summary>The server says the log goes further than it shows. A format: {0} is the channel.</summary>
    public static readonly Wording MembershipNotShown = new(NoticeKind.MembershipHidden,
        "the server says the membership of {0} has changed further than it will show you. " + HidingSuffix,
        "the server says the members of {0} have changed, but won't show you how. " + HidingSuffix + ". " + CheckMembers);

    /// <summary>The server shows an older log than this client verified. A format: {0} is the channel.</summary>
    public static readonly Wording MembershipOlder = new(NoticeKind.MembershipHidden,
        "the server shows an older version of the membership of {0} than you have already seen. " + HidingSuffix,
        "the server is showing an older member list for {0} than you have already seen. " + HidingSuffix + ". " + CheckMembers);

    /// <summary>A log entry that breaks the membership rules. A format: {0} is the channel.</summary>
    /// <param name="reason">Which rule, in technical words.</param>
    public static Wording MembershipChangeRefused(string reason) => new(NoticeKind.MembershipChangeRefused,
        $"the server sent a change to the membership of {{0}} that doesn't check out ({reason}), so it is ignored",
        "the server sent a change to the members of {0} that doesn't check out, so LookingGlass ignored it. If this keeps happening, " +
        "the server may be up to something. " + CheckMembers);

    /// <summary>Two validly signed versions of a channel's membership. A format: {0} is the channel.</summary>
    public static Wording MembershipForked(ulong forkAt) => new(NoticeKind.MembershipForked,
        $"the server has shown you two different versions of the membership of {{0}}, both validly signed, that differ at or before entry #{forkAt}. " +
        "Someone may be seeing a different member list from you: compare it with other members over /tell before trusting it",
        "the server has shown you two different member lists for {0}. Someone may be seeing different members from you. " + CheckMembers);

    /// <summary>A key shows the server shows members different versions of the membership. A format: {0} is the channel.</summary>
    /// <param name="detail">What gives the server away, in technical words.</param>
    public static Wording MembersShownDifferently(string detail) => new(NoticeKind.MembersShownDifferently,
        $"the server delivered a key for {{0}} {detail}. An honest server never takes such a key, so it is showing members " +
        "different versions of the membership. " + HidingSuffix,
        "the server is showing members of {0} different member lists. " + HidingSuffix + ". " + CheckMembers);

    /// <summary>A key offered for an older membership than the one verified. A format: {0} is the channel.</summary>
    public static Wording StaleKeyOffered(ulong? seq) => new(NoticeKind.StaleKeyOffered,
        $"the server offered a key for {{0}} made for an older membership (entry #{seq}) than you have verified. It may be hiding a change from someone",
        "the server sent an update for {0} that belongs to an older member list than the one you have. It may be hiding a change from " +
        "someone. " + CheckMembers);

    /// <summary>The server refused a new key, and won't show the newest change this client verified.</summary>
    public static readonly Wording ServerRefusesKey = new(NoticeKind.ServerRefusesKey,
        "The server refuses the new key, and won't show the newest membership change you have verified, or what it says came after it. " +
        "It may be hiding a change from the other members.",
        "The server refused to update this channel, and won't show its latest change to the members. It may be hiding a change from the " +
        "other members.");

    /// <summary>A removal (or leave) the other members still don't have the new key for.</summary>
    public static Wording RemovalNotInEffect(string channel, ulong seq) => new(NoticeKind.RemovalNotInEffect,
        $"A removal from {channel} (or someone leaving it, at membership log entry #{seq}) hasn't taken effect for the other members yet: " +
        "the server didn't take the new key that leaves them out, so the others still share the old key with them. Rekey the channel to try again. " +
        "If it keeps failing, the server may be hiding the change from the other members.",
        $"Removing someone from {channel} (or someone leaving it) hasn't taken effect for the other members yet: the server didn't accept " +
        "the update that shuts them out, so they may still be able to read new messages. Sending a message in the channel tries again. " +
        "If it keeps failing, the server may be hiding the change from the other members.");

    // ================================================================ channel keys, in the normal run of things

    public static readonly Wording JoinedMakingKey = new(NoticeKind.Joined,
        "Joined. No other member is online, so you're making the channel a new key.",
        "Joined. No other member is online, so LookingGlass is getting the channel ready for you.");

    public static readonly Wording JoinedNobodyToShareKey = new(NoticeKind.Joined,
        "Joined. No other member is online to share the channel key; rekey the channel, or send a message, to make a new one.",
        "Joined. No other member is online to let you in; send a message to get the channel ready.");

    public static readonly Wording JoinedWaitingForKey = new(NoticeKind.Joined,
        "Joined. Waiting for a member to share the channel key.",
        "Joined. You can read and send here once another member who is online lets you in, which happens by itself.");

    /// <summary>For a channel at a glance: it needs a new key before anyone sends, which a member makes by itself.</summary>
    public static readonly Wording NewKeyPending = new(NoticeKind.ChannelNotReady,
        "A new channel key is pending.",
        "Being updated after a change to its members.");

    /// <summary>For a channel at a glance: this client holds no key for it yet.</summary>
    public static readonly Wording WaitingForKey = new(NoticeKind.ChannelNotReady,
        "Waiting for the channel key.",
        "Waiting for another member to let you in.");

    public static readonly Wording NoKeyToRename = new(NoticeKind.ChannelNotReady,
        "This channel's key isn't available yet.",
        "This channel isn't ready for you yet: wait for another member who is online to let you in.");

    public static readonly Wording RenamedTooOften = new(NoticeKind.ChannelNotReady,
        "This channel can't be renamed again until its key changes. Rekey it, then rename it.",
        "This channel can't be renamed again for now. Try again after someone joins or leaves.");

    public static readonly Wording NoKeyToSend = new(NoticeKind.ChannelNotReady,
        "You don't have this channel's key yet. A member who is online will share it.",
        "This channel isn't ready for you yet: another member who is online lets you in, which happens by itself.");

    public static readonly Wording NoKeyToRekey = new(NoticeKind.ChannelNotReady,
        "You don't have this channel's key yet, so you can't rekey it. Another member needs to.",
        "This channel isn't ready for you yet: another member needs to come online first.");

    public static readonly Wording OldKeyCantRekey = new(NoticeKind.OldKeyCantChangeMembers,
        "You can't rekey this channel: your place in it belongs to an identity key you no longer have. A moderator must remove you and invite you again.",
        "You can't update this channel: your place in it belongs to your old LookingGlass setup. A moderator must remove you and invite you again.");

    public static readonly Wording RekeyConflicts = Plainly(
        "Rekeying kept conflicting with other changes; try again.",
        "The channel kept changing while it was being updated; try again.");

    /// <summary>Someone being invited whose keys aren't valid.</summary>
    public static Wording InvalidKeys(string who) => Plainly(
        $"{who} has an invalid identity key.",
        $"{who}'s LookingGlass setup is broken, so they can't be invited.");

    /// <summary>Someone being invited who has no account on this server (they may use LookingGlass on another one).</summary>
    public static Wording NotRegisteredHere(string who) => Plainly(
        $"{who} isn't registered with LookingGlass on this server.",
        $"{who} isn't registered with LookingGlass on this server: they need to install it and register their character first.");

    /// <summary>What a session does in the background, for "... failed".</summary>
    public static readonly Wording Rekeying = Plainly("Rekeying a channel", "Updating a channel");

    /// <inheritdoc cref="Rekeying"/>
    public static readonly Wording CheckingMembership = Plainly("Checking a channel's membership", "Checking a channel's members");

    /// <inheritdoc cref="Rekeying"/>
    public static readonly Wording FetchingIdentities = Plainly("Fetching new identity keys", "Checking members' LookingGlass");

    // ================================================================ choosing the words

    // Where an exception made by Failure keeps its plain words and kind.
    private const string PlainKey = "LookingGlass.Plain";
    private const string KindKey = "LookingGlass.Kind";

    /// <summary>Words with nothing to warn about, in both modes.</summary>
    private static Wording Plainly(string technical, string plain) => new(NoticeKind.General, technical, plain);

    /// <summary>
    /// An error to throw for the user to read: its message is the technical words, and <see cref="MessageOf"/> gives the plain
    /// ones in simple mode. Still an <see cref="InvalidOperationException"/>, as before.
    /// </summary>
    public static InvalidOperationException Failure(Wording wording, Exception? inner = null) {
        var failure = new InvalidOperationException(wording.Technical, inner);
        failure.Data[PlainKey] = wording.Plain;
        failure.Data[KindKey] = wording.Kind;
        return failure;
    }

    /// <summary>An exception's message for a mode: the plain words of one made by <see cref="Failure"/> in simple mode, else its message.</summary>
    public static string MessageOf(Exception ex, bool advanced) =>
        !advanced && ex.Data[PlainKey] is string plain ? plain : ex.Message;

    /// <summary>What an exception made by <see cref="Failure"/> is about; <see cref="NoticeKind.General"/> for any other.</summary>
    public static NoticeKind KindOf(Exception ex) => ex.Data[KindKey] is NoticeKind kind ? kind : NoticeKind.General;

    /// <summary>Something that failed, as "{what} failed: {why}", in both modes.</summary>
    /// <remarks>Of the kind of what went wrong, if the exception says (see <see cref="Failure"/>), else <see cref="NoticeKind.BackgroundFailure"/>.</remarks>
    public static Wording Failed(Wording what, Exception ex) {
        var kind = KindOf(ex);
        return new Wording(kind == NoticeKind.General ? NoticeKind.BackgroundFailure : kind,
            Of($"{what.Technical} failed: {ex.Message}"),
            Of($"{what.Plain} failed: {MessageOf(ex, false)}", advanced: false));
    }

    // What the membership rules say when an entry isn't signed by the key the log knows its author by, and what a server says
    // when someone acts with keys the log doesn't know them by.
    private static readonly string[] OldKeyReasons = [
        "It isn't signed with the key the log knows its author by.",
        "Your place in this channel belongs to an identity key your account no longer has. A moderator must remove you and invite you again.",
    ];

    /// <summary>A message to show, with technical ones about old keys replaced by what they mean for the user, in a mode's words.</summary>
    public static string Of(string message, bool advanced = true) {
        foreach (var reason in OldKeyReasons) {
            var at = message.IndexOf(reason, StringComparison.Ordinal);
            if (at >= 0) {
                // What was being done ("Leaving ... failed: ") stays; whatever the server or the rules added after it goes.
                var before = message[..at];
                if (before.EndsWith("Invalid membership log entry: ", StringComparison.Ordinal)) {
                    before = before[..^"Invalid membership log entry: ".Length];
                }

                return before + OldKeyCantChangeMembersWording.For(advanced);
            }
        }

        return message;
    }

    /// <summary>
    /// Every wording above, with made-up names, for tests: each kind of warning must have plain words in simple mode.
    /// </summary>
    internal static IEnumerable<Wording> Examples() {
        const string who = "Bob Hatter@Lich";
        const string fingerprint = "12345 67890 12345 67890 12345";
        const string channel = "\"Tea party\"";
        yield return PlacesRestoredWording(3);
        yield return ReVerifiedWording(who, false);
        yield return ReVerifiedWording(who, true);
        yield return ReVerifiedElsewhereWording;
        yield return KeyMovedAwayWording;
        yield return LoginMaybeReplacedWording;
        yield return LoginNotRecognized;
        yield return IdentityRetired;
        yield return IdentityReset("wss://chat.example.com/ws");
        yield return IdentityResetNotRetired("wss://chat.example.com/ws", "you weren't connected and logged in");
        yield return IdentityResetFailed("wss://chat.example.com/ws", "Access is denied.");
        yield return CouldntLoadKeys("The secrets file is truncated.");
        yield return OldKeyChannelWording;
        yield return CantLeaveOldKeyWording;
        yield return OldKeyCantChangeMembersWording;
        yield return InviteForOldKey;
        yield return ForgetMembership;
        yield return MemberUnderOldKey(who);
        yield return KeyChanged(who, fingerprint);
        yield return NameNowAnotherAccount(who, fingerprint);
        yield return Renamed("Bob Hatter@Odin", who);
        yield return KeysChangedIn(who);
        yield return RegisteredAgainIn(who);
        yield return InviteFromChangedKey(who, channel);
        yield return InviteUnverified(who);
        yield return NameChangedWhileRekeying(who, "Tea party", "Coffee morning");
        yield return BadChannelKey(who, "Tea party", 7, "can't be opened with your identity key", true);
        yield return BadChannelKey(who, "Tea party", 7, "isn't the key they committed to giving everyone else", false);
        yield return ChannelKeyRejected("it failed signature or decryption checks", "Tea party");
        yield return CantSealTo(who, "invalid point");
        yield return VerifiedKeyChanged;
        yield return MessageFromNonMember("Tea party", who);
        yield return MessageWithoutKey("Bob Hatter");
        yield return MessageTooLate("Bob Hatter");
        yield return MessageFromNewSetup("Bob Hatter", "Tea party");
        yield return MessageFailedChecks("Bob Hatter");
        yield return MessageClockSkew("Bob Hatter", "04/10/2026 12:00");
        yield return MessagesNotCaughtUp("Tea party", 1);
        yield return MessagesNotCaughtUp("Tea party", 12);
        yield return MembershipChangeDatedAhead("Tea party");
        yield return MessagesNotConfirmed("Tea party", [("Carol Queen@Odin", Protocol.MembershipEntryKind.Leave)]);
        yield return MessagesNotConfirmed("Tea party", [("Carol Queen@Odin", Protocol.MembershipEntryKind.Remove)]);
        yield return MessagesNotConfirmed("Tea party", [("Carol Queen@Odin", Protocol.MembershipEntryKind.KeyRecovered)]);
        yield return MessagesNotConfirmed("Tea party", [("Carol Queen@Odin", Protocol.MembershipEntryKind.Leave), ("Bob Hatter@Lich", Protocol.MembershipEntryKind.KeyRecovered)]);
        yield return MessageReplayed("Bob Hatter", "04/10/2026 12:00");
        yield return AddressNotListed("wss://chat.example.com/ws", "ws://203.0.113.5:5180/ws");
        yield return RelayedRegistrationCode;
        yield return IdentitiesMissing;
        yield return MembershipHidden.Format(channel);
        yield return MembershipNotShown.Format(channel);
        yield return MembershipOlder.Format(channel);
        yield return MembershipChangeRefused("It isn't signed by its actor.").Format(channel);
        yield return MembershipForked(12).Format(channel);
        yield return MembersShownDifferently("(epoch 4) made for a version of membership log entry #9 other than the one you verified").Format(channel);
        yield return StaleKeyOffered(9).Format(channel);
        yield return ServerRefusesKey;
        yield return RemovalNotInEffect("Tea party", 9);
        yield return JoinedMakingKey;
        yield return JoinedNobodyToShareKey;
        yield return JoinedWaitingForKey;
        yield return NewKeyPending;
        yield return WaitingForKey;
        yield return NoKeyToRename;
        yield return RenamedTooOften;
        yield return NoKeyToSend;
        yield return NoKeyToRekey;
        yield return OldKeyCantRekey;
        yield return RekeyConflicts;
        yield return InvalidKeys(who);
        yield return Rekeying;
        yield return CheckingMembership;
        yield return FetchingIdentities;
        yield return Failed(Rekeying, Failure(CantSealTo(who, "invalid point")));
        yield return Failed(Rekeying, new TimeoutException("The server didn't answer in time."));
    }
}
