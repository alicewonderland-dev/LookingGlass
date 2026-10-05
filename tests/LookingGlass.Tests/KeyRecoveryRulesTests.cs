using Google.Protobuf;
using LookingGlass.Core.Crypto;
using LookingGlass.Core.Membership;
using LookingGlass.Protocol;

namespace LookingGlass.Tests;

/// <summary>
/// Key recovered entries on their own: the server appends one when a user re-verifies their character through the Lodestone
/// with new identity keys, and every client replaying the log moves the user's place (rank kept, or their invite) to the
/// new keys. Clients can't check the Lodestone, so they take that on the server's word, but only in a well-formed entry
/// for someone in the channel, signed by the new keys themselves, with keys nobody else there has.
/// </summary>
public sealed class KeyRecoveryRulesTests : IDisposable {
    private const string ChannelId = "0123456789abcdef0123456789abcdef";
    private static readonly IMembershipProvider Provider = SignedLogMembershipProvider.Instance;

    private readonly IdentityKeys _alice = IdentityKeys.Generate();
    private readonly IdentityKeys _bob = IdentityKeys.Generate();
    private readonly IdentityKeys _carol = IdentityKeys.Generate();
    private readonly IdentityKeys _aliceNew = IdentityKeys.Generate();
    private long _clock = 1_000;

    private const long Alice = 1, Bob = 2, Carol = 3;

    public void Dispose() {
        this._alice.Dispose();
        this._bob.Dispose();
        this._carol.Dispose();
        this._aliceNew.Dispose();
    }

    /// <summary>The admin's place moves to her new keys with its rank: they sign what the admin may, and the old ones nothing.</summary>
    [Fact]
    public void AMembersPlaceMovesToTheNewKeysWithItsRank() {
        var state = this.WithMember(this.Genesis(), Bob, this._bob);
        var before = state.Head!;

        state = state.Apply(Recover(state, Alice, this._aliceNew));

        var alice = state.FindMember(Alice)!;
        Assert.Equal(Rank.Admin, alice.Rank);
        Assert.Equal(MemberKeys.Of(this._aliceNew), alice.Keys);
        Assert.Equal(2, state.Members.Count);
        // The members' keys changed, so keys and names made before it are for another membership: the channel is rekeyed.
        Assert.Equal(state.Head!.Seq, state.MembersChangedAt);
        Assert.False(state.IsCurrent(before));

        // The new keys act as the admin; the old ones sign nothing that counts.
        state = state.Apply(state.Create(MembershipEntryKind.SetRank, Bob, this._aliceNew, Alice, this.Now(), rank: Rank.Moderator));
        Assert.Equal(Rank.Moderator, state.FindMember(Bob)!.Rank);
        var oldKick = Forge(state, MembershipEntryKind.Remove, Alice, Bob, this._alice, MemberKeys.Of(this._bob));
        Assert.Equal(MembershipVerdictKind.Invalid, state.Check(oldKick).Kind);
        state = state.Apply(state.Create(MembershipEntryKind.TransferAdmin, Bob, this._aliceNew, Alice, this.Now()));
        Assert.Equal(Rank.Admin, state.FindMember(Bob)!.Rank);
    }

    /// <summary>An open invite moves too: the new keys answer it, the old ones can't. Nobody's keys for the channel change.</summary>
    [Fact]
    public void AnOpenInviteMovesToTheNewKeys() {
        var state = this.Genesis();
        state = state.Apply(state.Create(MembershipEntryKind.Invite, Bob, this._alice, Alice, this.Now(), MemberKeys.Of(this._bob)));
        var invite = state.FindInvitee(Bob)!;
        using var bobNew = IdentityKeys.Generate();
        var membersChangedAt = state.MembersChangedAt;

        state = state.Apply(Recover(state, Bob, bobNew));

        var moved = state.FindInvitee(Bob)!;
        Assert.Equal(MemberKeys.Of(bobNew), moved.Keys);
        Assert.True(MembershipEntries.SamePosition(invite.Invite, moved.Invite));
        Assert.Equal(invite.InviterKeys, moved.InviterKeys);
        // Invitees hold no key, so the members' keys stand.
        Assert.Equal(membersChangedAt, state.MembersChangedAt);

        var oldAccept = Forge(state, MembershipEntryKind.Accept, Bob, Bob, this._bob, MemberKeys.Of(this._bob), invite.Invite);
        Assert.Equal(MembershipVerdictKind.Invalid, state.Check(oldAccept).Kind);
        state = state.Apply(state.Create(MembershipEntryKind.Accept, Bob, bobNew, Bob, this.Now()));
        Assert.Equal(MemberKeys.Of(bobNew), state.FindMember(Bob)!.Keys);
    }

    /// <summary>
    /// What a client refuses, whoever sends it: an entry about someone not in the channel, or naming keys their place
    /// isn't under, or moving it to the keys it already has or to another member's or invitee's, or not signed by the new
    /// keys (the old ones, someone else's, for another user), or with fields that don't fit the kind.
    /// </summary>
    [Fact]
    public void MalformedRecoveriesAreRefused() {
        var state = this.WithMember(this.Genesis(), Bob, this._bob);
        state = state.Apply(state.Create(MembershipEntryKind.Invite, Carol, this._alice, Alice, this.Now(), MemberKeys.Of(this._carol)));
        using var stranger = IdentityKeys.Generate();
        var good = Recover(state, Alice, this._aliceNew);
        Assert.True(state.Check(good).IsValid);

        // Someone who isn't a member or invited.
        Assert.Equal(MembershipVerdictKind.Conflict, state.Check(Recover(state, 9, stranger, subjectKeys: MemberKeys.Of(this._bob))).Kind);
        // Naming keys that aren't the ones her place is under.
        Assert.Equal(MembershipVerdictKind.Invalid, state.Check(Recover(state, Alice, this._aliceNew, subjectKeys: MemberKeys.Of(stranger))).Kind);
        // Moving her to the keys she has.
        Assert.Equal(MembershipVerdictKind.Invalid, state.Check(Recover(state, Alice, this._alice)).Kind);
        // Moving her to keys a member or an invitee already has.
        Assert.Equal(MembershipVerdictKind.Conflict, state.Check(Recover(state, Alice, this._bob)).Kind);
        Assert.Equal(MembershipVerdictKind.Conflict, state.Check(Recover(state, Alice, this._carol)).Kind);

        // Not signed by the new keys: by her old ones, by someone else, or for another user.
        Assert.Equal(MembershipVerdictKind.Invalid, state.Check(Resign(good, KeyRecoveryProof.Sign(this._alice, Alice))).Kind);
        Assert.Equal(MembershipVerdictKind.Invalid, state.Check(Resign(good, stranger.Sign(KeyRecoveryProof.Payload(Alice, this._aliceNew.SigningPublicKey, this._aliceNew.AgreementPublicKey)))).Kind);
        Assert.Equal(MembershipVerdictKind.Invalid, state.Check(Resign(good, KeyRecoveryProof.Sign(this._aliceNew, Bob))).Kind);
        // Signed like any other entry, over the entry by the new keys: not what a recovery is signed over.
        var overEntry = good.Clone();
        MembershipEntries.Sign(overEntry, this._aliceNew);
        Assert.Equal(MembershipVerdictKind.Invalid, state.Check(overEntry).Kind);

        // Fields that don't fit: another actor, an actor key hash not the new keys', new keys for someone else, none, a rank, an invite.
        Assert.Equal(MembershipVerdictKind.Invalid, state.Check(With(good, e => e.ActorId = Bob)).Kind);
        Assert.Equal(MembershipVerdictKind.Invalid, state.Check(With(good, e => e.ActorKeyHash = ByteString.CopyFrom(MemberKeys.Of(this._alice).Hash))).Kind);
        Assert.Equal(MembershipVerdictKind.Invalid, state.Check(With(good, e => e.NewKeys.UserId = Bob)).Kind);
        Assert.Equal(MembershipVerdictKind.Invalid, state.Check(With(good, e => e.NewKeys = null)).Kind);
        Assert.Equal(MembershipVerdictKind.Invalid, state.Check(With(good, e => e.Rank = Rank.Admin)).Kind);
        Assert.Equal(MembershipVerdictKind.Invalid, state.Check(With(good, e => e.Invite = state.Head!.Clone())).Kind);
        Assert.Equal(MembershipVerdictKind.Invalid, state.Check(With(good, e => e.NewKeys.AgreementPublicKey = ByteString.CopyFrom(new byte[16]))).Kind);

        // New keys an agreement can't be made with (all zeros): signed by them all the same, refused.
        var lowOrder = good.Clone();
        lowOrder.NewKeys.AgreementPublicKey = ByteString.CopyFrom(new byte[32]);
        lowOrder.ActorKeyHash = ByteString.CopyFrom(new MemberKeys(this._aliceNew.SigningPublicKey, new byte[32]).Hash);
        lowOrder.Signature = ByteString.CopyFrom(this._aliceNew.Sign(KeyRecoveryProof.Payload(Alice, this._aliceNew.SigningPublicKey, new byte[32])));
        Assert.Equal(MembershipVerdictKind.Invalid, state.Check(lowOrder).Kind);

        // Other kinds can't carry new keys.
        var invite = state.Create(MembershipEntryKind.SetRank, Bob, this._alice, Alice, this.Now(), rank: Rank.Moderator);
        invite.NewKeys = MemberKeys.Of(stranger).ToProto(Bob);
        MembershipEntries.Sign(invite, this._alice);
        Assert.Equal(MembershipVerdictKind.Invalid, state.Check(invite).Kind);

        // Making one that doesn't fit is refused as well.
        Assert.Throws<MembershipException>(() => state.CreateKeyRecovered(Alice, MemberKeys.Of(this._bob), KeyRecoveryProof.Sign(this._bob, Alice), this.Now()));
        Assert.Throws<MembershipException>(() => state.CreateKeyRecovered(9, MemberKeys.Of(stranger), KeyRecoveryProof.Sign(stranger, 9), this.Now()));
    }

    /// <summary>
    /// A recovery isn't signed at a position in the log, so it proves nothing about a fork: the keys that a fork needs two
    /// signatures of are a member's, never the server's word.
    /// </summary>
    [Fact]
    public void ARecoveryIsntSignedByKeysTheLogKnows() {
        var state = this.Genesis();
        var entry = Recover(state, Alice, this._aliceNew);
        Assert.False(state.IsSignedByKnownKeys(entry));
        Assert.False(state.Apply(entry).IsSignedByKnownKeys(entry));
    }

    /// <summary>
    /// The keys someone had at an earlier position, for what they signed there (a name or a key made before they recovered),
    /// replayed or restored from a checkpoint alike; only the last few recoveries of each, and nothing of anyone gone.
    /// </summary>
    [Fact]
    public void KeysAtAnEarlierPositionAreTheOnesTheyHadThere() {
        var state = this.WithMember(this.Genesis(), Bob, this._bob);
        var beforeFirst = state.Head!.Seq;
        state = state.Apply(Recover(state, Alice, this._aliceNew));
        var beforeSecond = state.Head!.Seq;
        using var newest = IdentityKeys.Generate();
        state = state.Apply(Recover(state, Alice, newest));

        foreach (var restored in new[] { state, Provider.Restore(System.Text.Json.JsonSerializer.Deserialize<MembershipCheckpoint>(System.Text.Json.JsonSerializer.Serialize(state.ToCheckpoint()))!) }) {
            Assert.Equal(MemberKeys.Of(this._alice), restored.KeysAt(Alice, beforeFirst));
            Assert.Equal(MemberKeys.Of(this._alice), restored.KeysAt(Alice, 0));
            Assert.Equal(MemberKeys.Of(this._aliceNew), restored.KeysAt(Alice, beforeSecond));
            Assert.Equal(MemberKeys.Of(newest), restored.KeysAt(Alice, restored.Head!.Seq));
            Assert.Equal(MemberKeys.Of(this._bob), restored.KeysAt(Bob, 0));
            Assert.Null(restored.KeysAt(Carol, 0));
        }

        // Only the last few.
        for (var i = 0; i < SignedLogMembership.MaxKeyChangesKept + 2; i++) {
            using var next = IdentityKeys.Generate();
            state = state.Apply(Recover(state, Bob, next));
        }

        var bobNow = state.FindMember(Bob)!.Keys;
        Assert.NotEqual(MemberKeys.Of(this._bob), state.KeysAt(Bob, beforeFirst));
        Assert.NotEqual(bobNow, state.KeysAt(Bob, beforeFirst));

        // Gone with them.
        state = state.Apply(state.Create(MembershipEntryKind.Remove, Bob, newest, Alice, this.Now()));
        Assert.Null(state.KeysAt(Bob, beforeFirst));
        Assert.DoesNotContain(state.ToCheckpoint().KeyChanges, change => change.UserId == Bob);
    }

    /// <summary>Entries without new keys sign and hash as they always did, so logs written before stay valid.</summary>
    [Fact]
    public void OtherEntriesSignAsBefore() {
        var genesis = Provider.CreateGenesis(ChannelId, this._alice, Alice, 5);
        var expected = new SigningPayload(Domains.MembershipEntry)
            .Add(genesis.ChannelId).Add(genesis.Seq).Add(genesis.PreviousHash.Span).Add((long) genesis.Kind).Add(genesis.ActorId).Add(genesis.ActorKeyHash.Span)
            .Add(1L).Add(genesis.Subject.UserId).Add(genesis.Subject.SigningPublicKey.Span).Add(genesis.Subject.AgreementPublicKey.Span)
            .Add((long) genesis.Rank).Add(genesis.TimestampUnixMs).Add((LogPosition?) null)
            .ToArray();
        Assert.Equal(expected, MembershipEntries.SigningPayload(genesis));

        // And a recovery's new keys are in what its hash covers.
        var state = this.Genesis();
        var entry = Recover(state, Alice, this._aliceNew);
        using var other = IdentityKeys.Generate();
        Assert.NotEqual(MembershipEntries.Hash(entry), MembershipEntries.Hash(With(entry, e => e.NewKeys = MemberKeys.Of(other).ToProto(Alice))));
    }

    // ---------------------------------------------------------------- helpers

    private long Now() => this._clock++;

    private IChannelMembership Genesis() => Provider.Empty(ChannelId).Apply(Provider.CreateGenesis(ChannelId, this._alice, Alice, this.Now()));

    private IChannelMembership WithMember(IChannelMembership state, long userId, IdentityKeys keys) {
        state = state.Apply(state.Create(MembershipEntryKind.Invite, userId, this._alice, Alice, this.Now(), MemberKeys.Of(keys)));
        return state.Apply(state.Create(MembershipEntryKind.Accept, userId, keys, userId, this.Now()));
    }

    /// <summary>
    /// The key recovered entry the server would make for <paramref name="userId"/>, moving to <paramref name="newKeys"/>, with none
    /// of the rules checked; <paramref name="subjectKeys"/> by default the keys the log has for them.
    /// </summary>
    private static MembershipEntry Recover(IChannelMembership state, long userId, IdentityKeys newKeys, MemberKeys? subjectKeys = null) {
        var keys = MemberKeys.Of(newKeys);
        return new MembershipEntry {
            ChannelId = state.ChannelId,
            Seq = state.Head!.Seq + 1,
            PreviousHash = state.Head.Hash,
            Kind = MembershipEntryKind.KeyRecovered,
            ActorId = userId,
            ActorKeyHash = ByteString.CopyFrom(keys.Hash),
            Subject = (subjectKeys ?? state.FindMember(userId)?.Keys ?? state.FindInvitee(userId)?.Keys ?? keys).ToProto(userId),
            NewKeys = keys.ToProto(userId),
            TimestampUnixMs = 7,
            Signature = ByteString.CopyFrom(KeyRecoveryProof.Sign(newKeys, userId)),
        };
    }

    private static MembershipEntry Resign(MembershipEntry entry, byte[] signature) => With(entry, e => e.Signature = ByteString.CopyFrom(signature));

    private static MembershipEntry With(MembershipEntry entry, Action<MembershipEntry> change) {
        var copy = entry.Clone();
        change(copy);
        return copy;
    }

    private static MembershipEntry Forge(IChannelMembership state, MembershipEntryKind kind, long actorId, long subjectId, IdentityKeys signer, MemberKeys subjectKeys,
        LogPosition? invite = null) {
        var entry = new MembershipEntry {
            ChannelId = state.ChannelId,
            Seq = state.Head!.Seq + 1,
            PreviousHash = state.Head.Hash,
            Kind = kind,
            ActorId = actorId,
            ActorKeyHash = ByteString.CopyFrom(MemberKeys.Of(signer).Hash),
            Subject = subjectKeys.ToProto(subjectId),
            TimestampUnixMs = 1,
            Invite = invite,
        };
        MembershipEntries.Sign(entry, signer);
        return entry;
    }
}
