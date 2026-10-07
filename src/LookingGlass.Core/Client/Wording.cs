namespace LookingGlass.Core.Client;

/// <summary>
/// What something the user is told is about. Everything with a kind other than <see cref="General"/> has its own wording in
/// both modes (see <see cref="PlainMessages"/>): the technical one for advanced mode, and one in everyday words for the default,
/// simple mode. A warning is never left out of either: simple mode only says it differently.
/// </summary>
public enum NoticeKind {
    /// <summary>Nothing technical in it, so the same words in both modes (or a message from elsewhere, such as the server's).</summary>
    General,

    // ---- Other people
    /// <summary>Someone's keys changed without the channel's membership explaining why.</summary>
    KeyChanged,
    /// <summary>A name and world now belong to another account than the one seen before.</summary>
    NameNowAnotherAccount,
    /// <summary>Someone was renamed or moved world; their keys are the same.</summary>
    Renamed,
    /// <summary>Someone re-verified their character through the Lodestone and has a new key (a key recovered entry).</summary>
    ReVerified,
    /// <summary>Someone registered again with keys that aren't a member of a channel (their place there is under their old ones).</summary>
    RegisteredAgain,
    /// <summary>An invite from someone whose key changed.</summary>
    InviteFromChangedKey,
    /// <summary>An invite that didn't check out.</summary>
    InviteUnverified,
    /// <summary>A channel's name changed while it was rekeyed, by someone who may not be its admin.</summary>
    NameChangedWhileRekeying,
    /// <summary>A member sent a channel key that doesn't open, or isn't the one they promised everyone.</summary>
    BadChannelKey,
    /// <summary>A channel key was refused (made for another membership, not by a member, or failing its checks).</summary>
    ChannelKeyRejected,
    /// <summary>A member's keys can't be sealed to, so the channel can't be rekeyed until they're removed.</summary>
    CantSealTo,

    // ---- Messages that were dropped
    /// <summary>From someone who isn't a member.</summary>
    MessageFromNonMember,
    /// <summary>Under a key this client doesn't hold (yet).</summary>
    MessageWithoutKey,
    /// <summary>Under a key replaced too long ago.</summary>
    MessageTooLate,
    /// <summary>Signed with keys the sender registered again with, which aren't a member.</summary>
    MessageFromNewSetup,
    /// <summary>Failing its signature or decryption.</summary>
    MessageFailedChecks,
    /// <summary>Dated too far from now.</summary>
    MessageClockSkew,
    /// <summary>Older than messages already received from its sender.</summary>
    MessageReplayed,
    /// <summary>Some of the messages caught up from while the user was away didn't pass the checks (said once, with how many).</summary>
    MessagesNotCaughtUp,

    // ---- The user's own identity
    /// <summary>After registering, the account's channels and invites moved to the new keys.</summary>
    PlacesRestored,
    /// <summary>A channel's log moved this user's own place away from the keys this client holds.</summary>
    ReVerifiedElsewhere,
    /// <summary>On a channel where that happened.</summary>
    KeyMovedAway,
    /// <summary>A place that belongs to keys this user no longer has.</summary>
    OldKeyPlace,
    /// <summary>Leaving such a place can't work.</summary>
    CantLeaveOldKeyPlace,
    /// <summary>Nothing that changes the members can work from such a place.</summary>
    OldKeyCantChangeMembers,
    /// <summary>An invite made for keys this user no longer has.</summary>
    InviteForOldKey,
    /// <summary>The server refused the saved login and the identity key.</summary>
    LoginNotRecognized,
    /// <summary>The server retired this client's identity key ("Reset my identity", first step).</summary>
    IdentityRetired,
    /// <summary>Marking someone verified didn't work: the keys held for them changed since they were shown.</summary>
    VerifiedKeyChanged,

    // ---- Servers
    /// <summary>The server lists its addresses, without the one this client uses.</summary>
    AddressNotListed,
    /// <summary>The server sent a registration code that isn't its own.</summary>
    RelayedRegistrationCode,
    /// <summary>The server didn't send members' identities.</summary>
    IdentitiesMissing,
    /// <summary>The server won't show the membership as this client verified it.</summary>
    MembershipHidden,
    /// <summary>The server sent a membership change that breaks the rules.</summary>
    MembershipChangeRefused,
    /// <summary>The server showed two validly signed versions of a channel's membership.</summary>
    MembershipForked,
    /// <summary>A key shows the server is showing members different versions of the membership.</summary>
    MembersShownDifferently,
    /// <summary>The server offered a key made for an older membership than the one verified.</summary>
    StaleKeyOffered,
    /// <summary>The server refused a new key and won't show the newest change this client verified.</summary>
    ServerRefusesKey,
    /// <summary>A removal (or leave) hasn't taken effect for the other members: its rekey wasn't taken.</summary>
    RemovalNotInEffect,

    // ---- Channel keys, in the normal run of things
    /// <summary>Just joined; the channel's key comes from another member, or this client makes it.</summary>
    Joined,
    /// <summary>The channel's key isn't held (yet), so something can't be done.</summary>
    ChannelNotReady,
    /// <summary>Something done in the background failed.</summary>
    BackgroundFailure,
}

/// <summary>
/// Something to tell the user, in both modes: <see cref="Technical"/> for advanced mode (keys, fingerprints, the membership
/// log), and <see cref="Plain"/>, in everyday words, for simple mode. Both say what happened and what to do; a warning is a
/// warning in both.
/// </summary>
public sealed record Wording(NoticeKind Kind, string Technical, string Plain) {
    /// <summary>The words for a mode.</summary>
    public string For(bool advanced) => advanced ? this.Technical : this.Plain;

    /// <summary>Something with nothing technical in it: the same words in both modes.</summary>
    public static Wording Same(string text, NoticeKind kind = NoticeKind.General) => new(kind, text, text);

    /// <summary>Both texts, each formatted with <paramref name="args"/>.</summary>
    public Wording Format(params object?[] args) =>
        this with { Technical = string.Format(this.Technical, args), Plain = string.Format(this.Plain, args) };

    /// <summary>Both texts changed the same way.</summary>
    public Wording Map(Func<string, string> change) => this with { Technical = change(this.Technical), Plain = change(this.Plain) };
}
