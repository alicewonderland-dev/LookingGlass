using System.Collections.Immutable;
using Google.Protobuf;
using LookingGlass.Core.Crypto;
using LookingGlass.Protocol;

namespace LookingGlass.Core.Membership;

/// <summary>The v0.2 membership layer: a hash-chained log of signed entries per channel.</summary>
public sealed class SignedLogMembershipProvider : IMembershipProvider {
    public static readonly SignedLogMembershipProvider Instance = new();

    public IChannelMembership Empty(string channelId) => SignedLogMembership.Empty(channelId);

    public IChannelMembership Restore(MembershipCheckpoint checkpoint) => SignedLogMembership.Restore(checkpoint);

    public MembershipEntry CreateGenesis(string channelId, IdentityKeys creator, long creatorId, long timestampMs) {
        var keys = MemberKeys.Of(creator);
        var entry = new MembershipEntry {
            ChannelId = channelId,
            Seq = 0,
            Kind = MembershipEntryKind.Genesis,
            ActorId = creatorId,
            ActorKeyHash = ByteString.CopyFrom(keys.Hash),
            Subject = keys.ToProto(creatorId),
            Rank = Rank.Admin,
            TimestampUnixMs = timestampMs,
        };
        MembershipEntries.Sign(entry, creator);
        return entry;
    }
}

/// <summary>
/// A channel's membership, worked out by replaying its signed log. Each entry is valid only if,
/// at that point in the log, its signer was allowed to make it (design doc, "Authenticated
/// membership"), and only counts if signed by the exact keys the log knows its signer by: a
/// removed member's key signs nothing that counts, and a member who registers again with new
/// keys is not a member under them unless a key recovered entry (the server's word that they
/// re-verified their character, signed by the new keys) moves their place to them.
/// </summary>
public sealed class SignedLogMembership : IChannelMembership {
    /// <summary>
    /// How many recent entry hashes are kept: those since the members last changed, which keys
    /// and names may name as their position. A longer run of invites and rank changes without a
    /// join or leave only means keys made before it no longer count, and the channel is rekeyed.
    /// </summary>
    public const int MaxRecentHashes = 256;

    /// <summary>
    /// How many of each user's key recoveries are remembered for <see cref="KeysAt"/>. Only names and keys made before the
    /// newest are checked against older keys, and a client keeps the keys of its last few epochs at most.
    /// </summary>
    public const int MaxKeyChangesKept = 4;

    private readonly ImmutableDictionary<long, ChannelMember> _members;
    private readonly ImmutableDictionary<long, ChannelInvitee> _invitees;
    // Hashes of entries _recentFrom .. Head.Seq.
    private readonly ImmutableList<byte[]> _recent;
    private readonly ulong _recentFrom;
    // Per user in the channel, their recent key recoveries, oldest first: at Seq, their place moved away from Before.
    private readonly ImmutableDictionary<long, ImmutableList<KeyChange>> _keyChanges;

    private sealed record KeyChange(ulong Seq, MemberKeys Before);

    private SignedLogMembership(string channelId, LogPosition? head, ulong membersChangedAt, ulong? membersLeftAt,
        ImmutableDictionary<long, ChannelMember> members, ImmutableDictionary<long, ChannelInvitee> invitees,
        ImmutableList<byte[]> recent, ulong recentFrom, ImmutableDictionary<long, ImmutableList<KeyChange>> keyChanges) {
        this.ChannelId = channelId;
        this.Head = head;
        this.MembersChangedAt = membersChangedAt;
        this.MembersLeftAt = membersLeftAt;
        this._members = members;
        this._invitees = invitees;
        this._recent = recent;
        this._recentFrom = recentFrom;
        this._keyChanges = keyChanges;
    }

    public static SignedLogMembership Empty(string channelId) {
        return new SignedLogMembership(channelId, null, 0, null, ImmutableDictionary<long, ChannelMember>.Empty,
            ImmutableDictionary<long, ChannelInvitee>.Empty, ImmutableList<byte[]>.Empty, 0, ImmutableDictionary<long, ImmutableList<KeyChange>>.Empty);
    }

    public static SignedLogMembership Restore(MembershipCheckpoint checkpoint) {
        if (checkpoint.Hash.Length == 0) {
            return Empty(checkpoint.ChannelId);
        }

        var head = new LogPosition { Seq = checkpoint.Seq, Hash = ByteString.CopyFrom(checkpoint.Hash) };
        var members = checkpoint.Members.ToImmutableDictionary(
            member => member.UserId,
            member => new ChannelMember(member.UserId, new MemberKeys(member.SigningPublicKey, member.AgreementPublicKey), member.Rank));
        var invitees = checkpoint.Invitees.ToImmutableDictionary(
            invitee => invitee.UserId,
            invitee => new ChannelInvitee(invitee.UserId, new MemberKeys(invitee.SigningPublicKey, invitee.AgreementPublicKey),
                new LogPosition { Seq = invitee.InviteSeq, Hash = ByteString.CopyFrom(invitee.InviteHash) },
                invitee.InviterId, new MemberKeys(invitee.InviterSigningPublicKey, invitee.InviterAgreementPublicKey)));

        var recent = checkpoint.RecentHashes.ToImmutableList();
        var recentFrom = checkpoint.RecentFrom;
        if (recent.Count == 0 || recentFrom + (ulong) recent.Count - 1 != checkpoint.Seq || !recent[^1].AsSpan().SequenceEqual(checkpoint.Hash)) {
            // Inconsistent (edited by hand?): remember only the head.
            recent = [checkpoint.Hash];
            recentFrom = checkpoint.Seq;
        }

        var keyChanges = (checkpoint.KeyChanges ?? [])
            .Where(change => change.Seq <= checkpoint.Seq && (members.ContainsKey(change.UserId) || invitees.ContainsKey(change.UserId)))
            .GroupBy(change => change.UserId)
            .ToImmutableDictionary(
                group => group.Key,
                group => group.OrderBy(change => change.Seq).TakeLast(MaxKeyChangesKept)
                    .Select(change => new KeyChange(change.Seq, new MemberKeys(change.SigningPublicKey, change.AgreementPublicKey)))
                    .ToImmutableList());

        return new SignedLogMembership(checkpoint.ChannelId, head, checkpoint.MembersChangedAt, checkpoint.MembersLeftAt, members, invitees, recent, recentFrom,
            keyChanges);
    }

    public string ChannelId { get; }
    public LogPosition? Head { get; }
    public ulong MembersChangedAt { get; }
    public ulong? MembersLeftAt { get; }
    public IReadOnlyCollection<ChannelMember> Members => this._members.Values.ToList();
    public IReadOnlyCollection<ChannelInvitee> Invitees => this._invitees.Values.ToList();

    public ChannelMember? FindMember(long userId) => this._members.GetValueOrDefault(userId);

    public ChannelInvitee? FindInvitee(long userId) => this._invitees.GetValueOrDefault(userId);

    public MemberKeys? KeysAt(long userId, ulong seq) {
        var keys = this.FindMember(userId)?.Keys ?? this.FindInvitee(userId)?.Keys;
        if (keys != null && this._keyChanges.TryGetValue(userId, out var changes)) {
            // Newest first: each recovery after seq means they had the keys it moved them from.
            foreach (var change in changes.Reverse()) {
                if (change.Seq <= seq) {
                    break;
                }

                keys = change.Before;
            }
        }

        return keys;
    }

    public bool MovedFrom(long userId, MemberKeys keys) {
        return (this.FindMember(userId) != null || this.FindInvitee(userId) != null)
               && this._keyChanges.TryGetValue(userId, out var changes) && changes.Any(change => change.Before == keys);
    }

    public bool IsCurrent(LogPosition? position) {
        return position != null && this.Head != null
                                && position.Seq >= this.MembersChangedAt
                                && this.HashAt(position.Seq) is { } hash
                                && hash.AsSpan().SequenceEqual(position.Hash.Span);
    }

    public byte[]? HashAt(ulong seq) {
        if (this.Head == null || seq < this._recentFrom || seq > this.Head.Seq) {
            return null;
        }

        return this._recent[(int) (seq - this._recentFrom)];
    }

    public MembershipVerdict Check(MembershipEntry entry) => this.Evaluate(entry, out _);

    public bool IsSignedByKnownKeys(MembershipEntry entry) {
        // A key recovered entry is the server's word, signed by the new keys over no position at all: it says nothing
        // about who signed what where.
        var keys = entry.Kind == MembershipEntryKind.KeyRecovered ? null : this.FindMember(entry.ActorId)?.Keys ?? this.FindInvitee(entry.ActorId)?.Keys;
        return keys != null && entry.ChannelId == this.ChannelId && entry.Signature.Length == 64
               && entry.ActorKeyHash.Span.SequenceEqual(keys.Hash)
               && IdentityKeys.Verify(keys.SigningPublicKey, MembershipEntries.SigningPayload(entry), entry.Signature.Span);
    }

    public IChannelMembership Apply(MembershipEntry entry) {
        var verdict = this.Evaluate(entry, out var next);
        return verdict.IsValid ? next! : throw new MembershipException(verdict);
    }

    public MembershipEntry Create(MembershipEntryKind kind, long subjectId, IdentityKeys actor, long actorId, long timestampMs,
        MemberKeys? inviteeKeys = null, Rank rank = Rank.Unspecified) {
        MemberKeys subjectKeys;
        LogPosition? invite = null;
        switch (kind) {
            case MembershipEntryKind.Invite:
                subjectKeys = inviteeKeys ?? throw new ArgumentNullException(nameof(inviteeKeys));
                break;
            case MembershipEntryKind.Accept:
            case MembershipEntryKind.Decline:
            case MembershipEntryKind.CancelInvite:
                var invitee = this.FindInvitee(subjectId) ?? throw new MembershipException(new MembershipVerdict(MembershipVerdictKind.Conflict, "There is no open invite for them."));
                subjectKeys = invitee.Keys;
                invite = invitee.Invite.Clone();
                break;
            case MembershipEntryKind.Remove:
            case MembershipEntryKind.Leave:
            case MembershipEntryKind.SetRank:
            case MembershipEntryKind.TransferAdmin:
                subjectKeys = this.FindMember(subjectId)?.Keys ?? throw new MembershipException(new MembershipVerdict(MembershipVerdictKind.Conflict, "They aren't a member of this channel."));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Genesis entries come from IMembershipProvider.CreateGenesis, key recovered ones from CreateKeyRecovered.");
        }

        var entry = new MembershipEntry {
            ChannelId = this.ChannelId,
            Seq = this.Head == null ? 0 : this.Head.Seq + 1,
            PreviousHash = this.Head?.Hash ?? ByteString.Empty,
            Kind = kind,
            ActorId = actorId,
            ActorKeyHash = ByteString.CopyFrom(MemberKeys.Of(actor).Hash),
            Subject = subjectKeys.ToProto(subjectId),
            Rank = rank,
            TimestampUnixMs = timestampMs,
            Invite = invite,
        };
        MembershipEntries.Sign(entry, actor);

        var verdict = this.Check(entry);
        return verdict.IsValid ? entry : throw new MembershipException(verdict);
    }

    public MembershipEntry CreateKeyRecovered(long userId, MemberKeys newKeys, byte[] proof, long timestampMs) {
        var current = this.FindMember(userId)?.Keys ?? this.FindInvitee(userId)?.Keys
                      ?? throw new MembershipException(new MembershipVerdict(MembershipVerdictKind.Conflict, "They aren't a member of this channel or invited to it."));
        var entry = new MembershipEntry {
            ChannelId = this.ChannelId,
            Seq = this.Head == null ? 0 : this.Head.Seq + 1,
            PreviousHash = this.Head?.Hash ?? ByteString.Empty,
            Kind = MembershipEntryKind.KeyRecovered,
            ActorId = userId,
            ActorKeyHash = ByteString.CopyFrom(newKeys.Hash),
            Subject = current.ToProto(userId),
            NewKeys = newKeys.ToProto(userId),
            TimestampUnixMs = timestampMs,
            Signature = ByteString.CopyFrom(proof),
        };

        var verdict = this.Check(entry);
        return verdict.IsValid ? entry : throw new MembershipException(verdict);
    }

    public MembershipCheckpoint ToCheckpoint() {
        return new MembershipCheckpoint {
            ChannelId = this.ChannelId,
            Seq = this.Head?.Seq ?? 0,
            Hash = this.Head?.Hash.ToByteArray() ?? [],
            MembersChangedAt = this.MembersChangedAt,
            MembersLeftAt = this.MembersLeftAt,
            RecentHashes = [.. this._recent],
            RecentFrom = this._recentFrom,
            Members = this._members.Values.OrderBy(member => member.UserId).Select(member => new CheckpointMember {
                UserId = member.UserId,
                SigningPublicKey = member.Keys.SigningKeyArray(),
                AgreementPublicKey = member.Keys.AgreementKeyArray(),
                Rank = member.Rank,
            }).ToList(),
            Invitees = this._invitees.Values.OrderBy(invitee => invitee.UserId).Select(invitee => new CheckpointInvitee {
                UserId = invitee.UserId,
                SigningPublicKey = invitee.Keys.SigningKeyArray(),
                AgreementPublicKey = invitee.Keys.AgreementKeyArray(),
                InviteSeq = invitee.Invite.Seq,
                InviteHash = invitee.Invite.Hash.ToByteArray(),
                InviterId = invitee.InviterId,
                InviterSigningPublicKey = invitee.InviterKeys.SigningKeyArray(),
                InviterAgreementPublicKey = invitee.InviterKeys.AgreementKeyArray(),
            }).ToList(),
            KeyChanges = this._keyChanges.OrderBy(pair => pair.Key).SelectMany(pair => pair.Value.Select(change => new CheckpointKeyChange {
                UserId = pair.Key,
                Seq = change.Seq,
                SigningPublicKey = change.Before.SigningKeyArray(),
                AgreementPublicKey = change.Before.AgreementKeyArray(),
            })).ToList(),
        };
    }

    // ================================================================ the rules

    private MembershipVerdict Evaluate(MembershipEntry entry, out SignedLogMembership? next) {
        next = null;
        if (entry.ChannelId != this.ChannelId) {
            return Invalid("It belongs to another channel.");
        }

        if (this.Head == null) {
            if (entry.Kind != MembershipEntryKind.Genesis || entry.Seq != 0 || entry.PreviousHash.Length != 0) {
                return new MembershipVerdict(MembershipVerdictKind.NotNext, "The log must start with a genesis entry.");
            }
        } else {
            if (entry.Seq != this.Head.Seq + 1 || !entry.PreviousHash.Span.SequenceEqual(this.Head.Hash.Span)) {
                return new MembershipVerdict(MembershipVerdictKind.NotNext, $"It isn't the entry after #{this.Head.Seq}.");
            }

            if (entry.Kind == MembershipEntryKind.Genesis) {
                return Invalid("Only the first entry can be a genesis entry.");
            }

            if (this._members.IsEmpty) {
                return Invalid("Everyone has left this channel.");
            }
        }

        var subjectKeys = MemberKeys.FromProto(entry.Subject);
        if (subjectKeys is not { IsWellFormed: true } || entry.Signature.Length != 64 || entry.ActorKeyHash.Length != MembershipEntries.HashSize) {
            return Invalid("It is malformed.");
        }

        var subjectId = entry.Subject.UserId;
        var kind = entry.Kind;

        // Ranks and invite positions appear only where they mean something, so each entry has one reading.
        var rankFits = kind switch {
            MembershipEntryKind.Genesis => entry.Rank == Rank.Admin,
            MembershipEntryKind.SetRank => entry.Rank is Rank.Member or Rank.Moderator,
            _ => entry.Rank == Rank.Unspecified,
        };
        var answersInvite = kind is MembershipEntryKind.Accept or MembershipEntryKind.Decline or MembershipEntryKind.CancelInvite;
        var recovers = kind == MembershipEntryKind.KeyRecovered;
        if (!rankFits || answersInvite != (entry.Invite != null) || recovers != (entry.NewKeys != null)) {
            return Invalid("Its rank, invite or new keys field doesn't fit its kind.");
        }

        // Work out whose keys must have signed it: the log's, never the server's. (But for a key recovered entry, which is
        // the server's word, signed by the new keys.)
        MemberKeys actorKeys;
        ChannelMember? actor = null;
        ChannelInvitee? invitee = null;
        switch (kind) {
            case MembershipEntryKind.KeyRecovered:
                // (Present: checked above.)
                var newKeys = MemberKeys.FromProto(entry.NewKeys)!;
                if (entry.ActorId != subjectId || entry.NewKeys!.UserId != subjectId || !newKeys.IsWellFormed) {
                    return Invalid("Only the user's own new keys can take over their place.");
                }

                actorKeys = newKeys;
                break;
            case MembershipEntryKind.Genesis:
                if (entry.ActorId != subjectId) {
                    return Invalid("A channel's creator must be its first member.");
                }

                actorKeys = subjectKeys;
                break;
            case MembershipEntryKind.Accept:
            case MembershipEntryKind.Decline:
                if (entry.ActorId != subjectId) {
                    return Invalid("Only the invitee can answer their invite.");
                }

                invitee = this.FindInvitee(subjectId);
                if (invitee == null) {
                    return Conflict("There is no open invite for them.");
                }

                actorKeys = invitee.Keys;
                break;
            default:
                actor = this.FindMember(entry.ActorId);
                if (actor == null) {
                    return Forbidden("Its author isn't a member of the channel.");
                }

                actorKeys = actor.Keys;
                break;
        }

        if (!entry.ActorKeyHash.Span.SequenceEqual(actorKeys.Hash)) {
            return Invalid("It isn't signed with the key the log knows its author by.");
        }

        // A key recovered entry's signature is the new keys' consent to be this user, made once for every channel; any
        // other is over the entry itself.
        var signed = recovers
            ? KeyRecoveryProof.Payload(subjectId, actorKeys.SigningPublicKey, actorKeys.AgreementPublicKey)
            : MembershipEntries.SigningPayload(entry);
        if (!IdentityKeys.Verify(actorKeys.SigningPublicKey, signed, entry.Signature.Span)) {
            return Invalid("Its signature doesn't verify.");
        }

        var members = this._members;
        var invitees = this._invitees;
        var keyChanges = this._keyChanges;
        var membersChanged = false;
        var position = MembershipEntries.PositionOf(entry);

        switch (kind) {
            case MembershipEntryKind.KeyRecovered: {
                var member = this.FindMember(subjectId);
                var invited = member == null ? this.FindInvitee(subjectId) : null;
                if (member == null && invited == null) {
                    return Conflict("They aren't a member of this channel or invited to it.");
                }

                var bound = member?.Keys ?? invited!.Keys;
                if (subjectKeys != bound) {
                    return Invalid("It names keys their place isn't bound to.");
                }

                if (actorKeys == bound) {
                    return Invalid("Their place is under those keys already.");
                }

                // As for an invite: one set of keys has one place in a channel.
                if (members.Values.Any(other => other.Keys == actorKeys) || invitees.Values.Any(other => other.Keys == actorKeys)) {
                    return Conflict("Those keys already belong to someone in this channel.");
                }

                if (!IdentityKeys.IsUsableAgreementKey(actorKeys.AgreementPublicKey)) {
                    return Invalid("The new agreement key can't be sealed to.");
                }

                // Rank, and an invite's position and inviter, stay as they were: only the keys change.
                if (member != null) {
                    members = members.SetItem(subjectId, member with { Keys = actorKeys });
                    // Keys and names made for the old keys are for another membership now: the channel is rekeyed.
                    membersChanged = true;
                } else {
                    invitees = invitees.SetItem(subjectId, invited! with { Keys = actorKeys });
                }

                var changes = keyChanges.GetValueOrDefault(subjectId, ImmutableList<KeyChange>.Empty).Add(new KeyChange(entry.Seq, bound));
                keyChanges = keyChanges.SetItem(subjectId, changes.Count > MaxKeyChangesKept ? changes.RemoveAt(0) : changes);
                break;
            }

            case MembershipEntryKind.Genesis:
                if (!IdentityKeys.IsUsableAgreementKey(subjectKeys.AgreementPublicKey)) {
                    return Invalid("The creator's agreement key can't be sealed to.");
                }

                members = members.SetItem(subjectId, new ChannelMember(subjectId, subjectKeys, Rank.Admin));
                membersChanged = true;
                break;

            case MembershipEntryKind.Invite:
                if (actor!.Rank < Rank.Moderator) {
                    return Forbidden("Only moderators and the admin can invite.");
                }

                if (members.ContainsKey(subjectId)) {
                    return Conflict("They're already a member (perhaps under an older key: remove them first).");
                }

                if (invitees.ContainsKey(subjectId)) {
                    return Conflict("They're already invited.");
                }

                // Otherwise a moderator could admit their own keys under a second user ID, and keep a seat after removal.
                if (members.Values.Any(member => member.Keys == subjectKeys) || invitees.Values.Any(other => other.Keys == subjectKeys)) {
                    return Conflict("Those keys already belong to someone in this channel.");
                }

                if (!IdentityKeys.IsUsableAgreementKey(subjectKeys.AgreementPublicKey)) {
                    return Invalid("The invitee's agreement key can't be sealed to.");
                }

                invitees = invitees.SetItem(subjectId, new ChannelInvitee(subjectId, subjectKeys, position, actor.UserId, actor.Keys));
                break;

            case MembershipEntryKind.Accept:
            case MembershipEntryKind.Decline:
            case MembershipEntryKind.CancelInvite:
                if (kind == MembershipEntryKind.CancelInvite) {
                    if (actor!.Rank < Rank.Moderator) {
                        return Forbidden("Only moderators and the admin can cancel invites.");
                    }

                    invitee = this.FindInvitee(subjectId);
                    if (invitee == null) {
                        return Conflict("There is no open invite for them.");
                    }
                }

                if (subjectKeys != invitee!.Keys || !MembershipEntries.SamePosition(entry.Invite, invitee.Invite)) {
                    return Invalid("It doesn't answer the open invite.");
                }

                invitees = invitees.Remove(subjectId);
                if (kind == MembershipEntryKind.Accept) {
                    members = members.SetItem(subjectId, new ChannelMember(subjectId, subjectKeys, Rank.Member));
                    membersChanged = true;
                }

                break;

            case MembershipEntryKind.Remove:
            case MembershipEntryKind.Leave:
            case MembershipEntryKind.SetRank:
            case MembershipEntryKind.TransferAdmin: {
                var target = this.FindMember(subjectId);
                if (target == null) {
                    return Conflict("They aren't a member of this channel.");
                }

                if (subjectKeys != target.Keys) {
                    return Invalid("It names keys their membership isn't bound to.");
                }

                switch (kind) {
                    case MembershipEntryKind.Remove:
                        if (actor!.Rank < Rank.Moderator || target.Rank >= actor.Rank) {
                            return Forbidden("You can only remove members ranked below you.");
                        }

                        break;
                    case MembershipEntryKind.Leave:
                        if (subjectId != actor!.UserId) {
                            return Invalid("Only members themselves can leave.");
                        }

                        if (actor.Rank == Rank.Admin && members.Count > 1) {
                            return Forbidden("The admin can't leave while others remain. Make someone else admin first, or disband the channel.");
                        }

                        break;
                    default:
                        if (actor!.Rank != Rank.Admin) {
                            return Forbidden("Only the admin can change ranks.");
                        }

                        if (subjectId == actor.UserId) {
                            return Invalid("The admin can't change their own rank.");
                        }

                        if (kind == MembershipEntryKind.SetRank && target.Rank == entry.Rank) {
                            return Conflict("They already have that rank.");
                        }

                        break;
                }

                if (kind is MembershipEntryKind.Remove or MembershipEntryKind.Leave) {
                    members = members.Remove(subjectId);
                    membersChanged = true;
                } else if (kind == MembershipEntryKind.SetRank) {
                    members = members.SetItem(subjectId, target with { Rank = entry.Rank });
                } else {
                    members = members
                        .SetItem(subjectId, target with { Rank = Rank.Admin })
                        .SetItem(actor!.UserId, actor with { Rank = Rank.Moderator });
                }

                // Invites only count while whoever signed them may invite: a removed member's signature
                // signs nothing that counts after the removal, an invite waiting to be accepted included.
                if (kind is MembershipEntryKind.Remove or MembershipEntryKind.Leave || (kind == MembershipEntryKind.SetRank && entry.Rank < Rank.Moderator)) {
                    invitees = invitees.RemoveRange(invitees.Values.Where(invitee => invitee.InviterId == subjectId).Select(invitee => invitee.UserId).ToList());
                }

                break;
            }

            default:
                return Invalid("Unknown kind of entry.");
        }

        // What anyone gone signed is never checked again.
        if (!keyChanges.IsEmpty) {
            keyChanges = keyChanges.RemoveRange(keyChanges.Keys.Where(id => !members.ContainsKey(id) && !invitees.ContainsKey(id)).ToList());
        }

        var hash = position.Hash.ToByteArray();
        ImmutableList<byte[]> recent;
        ulong recentFrom;
        if (membersChanged) {
            recent = [hash];
            recentFrom = entry.Seq;
        } else {
            recent = this._recent.Add(hash);
            recentFrom = this._recentFrom;
            if (recent.Count > MaxRecentHashes) {
                recent = recent.RemoveAt(0);
                recentFrom++;
            }
        }

        var left = kind is MembershipEntryKind.Remove or MembershipEntryKind.Leave;
        next = new SignedLogMembership(this.ChannelId, position, membersChanged ? entry.Seq : this.MembersChangedAt, left ? entry.Seq : this.MembersLeftAt,
            members, invitees, recent, recentFrom, keyChanges);
        return MembershipVerdict.Valid;
    }

    private static MembershipVerdict Invalid(string reason) => new(MembershipVerdictKind.Invalid, reason);

    private static MembershipVerdict Forbidden(string reason) => new(MembershipVerdictKind.Forbidden, reason);

    private static MembershipVerdict Conflict(string reason) => new(MembershipVerdictKind.Conflict, reason);
}
