using LookingGlass.Core.Crypto;
using LookingGlass.Protocol;

namespace LookingGlass.Core.Membership;

/// <summary>
/// The membership layer: who is in a channel, with which rank and identity keys, and the
/// signed changes that alter that. Clients and the server only reach membership through
/// this and <see cref="IChannelMembership"/>, so the v0.2 signed log
/// (<see cref="SignedLogMembership"/>) can later be replaced by MLS (RFC 9420) without
/// touching chat, the UI or the server's routing.
/// </summary>
public interface IMembershipProvider {
    /// <summary>A channel whose log hasn't been seen yet. Only a genesis entry applies to it.</summary>
    IChannelMembership Empty(string channelId);

    /// <summary>The state saved by <see cref="IChannelMembership.ToCheckpoint"/>, without replaying the log.</summary>
    IChannelMembership Restore(MembershipCheckpoint checkpoint);

    /// <summary>The first entry of a new channel's log, which makes <paramref name="creatorId"/> its admin.</summary>
    MembershipEntry CreateGenesis(string channelId, IdentityKeys creator, long creatorId, long timestampMs);
}

/// <summary>
/// One channel's membership as verified up to <see cref="Head"/>. Immutable: applying an
/// entry gives a new state. Every check here is the client's own; nothing depends on
/// the server having checked anything.
/// </summary>
public interface IChannelMembership {
    string ChannelId { get; }

    /// <summary>The newest entry verified, or null before the genesis entry.</summary>
    LogPosition? Head { get; }

    /// <summary>The sequence number of the newest entry that changed who is a member.</summary>
    ulong MembersChangedAt { get; }

    /// <summary>
    /// The sequence number of the newest entry by which someone stopped being a member (a removal or a leave),
    /// or null if nobody has (or it isn't known: a state saved before this was kept).
    /// </summary>
    ulong? MembersLeftAt { get; }

    IReadOnlyCollection<ChannelMember> Members { get; }
    IReadOnlyCollection<ChannelInvitee> Invitees { get; }

    ChannelMember? FindMember(long userId);
    ChannelInvitee? FindInvitee(long userId);

    /// <summary>
    /// True if keys or names made at <paramref name="position"/> were made for today's members:
    /// it is in this log, and nobody has joined or left since.
    /// </summary>
    bool IsCurrent(LogPosition? position);

    /// <summary>The hash of entry <paramref name="seq"/>, if it is recent enough to be remembered.</summary>
    byte[]? HashAt(ulong seq);

    /// <summary>Whether <paramref name="entry"/> is a valid next entry.</summary>
    MembershipVerdict Check(MembershipEntry entry);

    /// <summary>
    /// Whether <paramref name="entry"/> is signed by the keys this state knows its actor by (as a member or
    /// invitee), wherever it is in the log and whatever the rules say of it. Two such entries at one position
    /// show that whoever holds those keys signed two versions of the log there.
    /// </summary>
    bool IsSignedByKnownKeys(MembershipEntry entry);

    /// <exception cref="MembershipException">The entry isn't a valid next entry.</exception>
    IChannelMembership Apply(MembershipEntry entry);

    /// <summary>
    /// Makes and signs the next entry: <paramref name="kind"/> by <paramref name="actorId"/> about
    /// <paramref name="subjectId"/>. The subject's keys (and, for answers, the invite) come from this
    /// state, except for an invite, which names <paramref name="inviteeKeys"/>.
    /// </summary>
    /// <exception cref="MembershipException">The change isn't allowed at this point, with why.</exception>
    MembershipEntry Create(MembershipEntryKind kind, long subjectId, IdentityKeys actor, long actorId, long timestampMs,
        MemberKeys? inviteeKeys = null, Rank rank = Rank.Unspecified);

    MembershipCheckpoint ToCheckpoint();
}

public sealed record ChannelMember(long UserId, MemberKeys Keys, Rank Rank);

/// <param name="Invite">The invite entry: an accept or decline must name it.</param>
/// <param name="InviterKeys">The keys the inviter signed the invite with, which also sign the sealed channel name.</param>
public sealed record ChannelInvitee(long UserId, MemberKeys Keys, LogPosition Invite, long InviterId, MemberKeys InviterKeys);

public enum MembershipVerdictKind {
    Valid,

    /// <summary>Not the entry after the head: the log moved on (or the entry is old).</summary>
    NotNext,

    /// <summary>Signed correctly, but the actor's rank doesn't allow it.</summary>
    Forbidden,

    /// <summary>Doesn't fit the current members or invites (already a member, no such invite, ...).</summary>
    Conflict,

    /// <summary>Malformed, or not signed by the key the log knows the actor by.</summary>
    Invalid,
}

public sealed record MembershipVerdict(MembershipVerdictKind Kind, string Reason) {
    public static readonly MembershipVerdict Valid = new(MembershipVerdictKind.Valid, "");

    public bool IsValid => this.Kind == MembershipVerdictKind.Valid;
}

public sealed class MembershipException(MembershipVerdict verdict) : InvalidOperationException(verdict.Reason) {
    public MembershipVerdict Verdict { get; } = verdict;
}
