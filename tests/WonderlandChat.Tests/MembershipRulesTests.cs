using Google.Protobuf;
using WonderlandChat.Core.Crypto;
using WonderlandChat.Core.Membership;
using WonderlandChat.Protocol;

namespace WonderlandChat.Tests;

/// <summary>
/// The membership log's rules on their own (design doc, "Authenticated membership"): each entry
/// replayed against the state before it, with no server involved.
/// </summary>
public sealed class MembershipRulesTests : IDisposable {
    private const string ChannelId = "0123456789abcdef0123456789abcdef";
    private static readonly IMembershipProvider Provider = SignedLogMembershipProvider.Instance;

    private readonly IdentityKeys _alice = IdentityKeys.Generate();
    private readonly IdentityKeys _bob = IdentityKeys.Generate();
    private readonly IdentityKeys _carol = IdentityKeys.Generate();
    private readonly IdentityKeys _dave = IdentityKeys.Generate();
    private long _clock = 1_000;

    private const long Alice = 1, Bob = 2, Carol = 3, Dave = 4;

    public void Dispose() {
        this._alice.Dispose();
        this._bob.Dispose();
        this._carol.Dispose();
        this._dave.Dispose();
    }

    [Fact]
    public void GenesisMakesTheCreatorAdmin() {
        var state = this.Genesis();
        var admin = Assert.Single(state.Members);
        Assert.Equal((Alice, Rank.Admin), (admin.UserId, admin.Rank));
        Assert.Equal(MemberKeys.Of(this._alice), admin.Keys);
        Assert.Equal(0UL, state.Head!.Seq);

        // Only once, and only as the first entry.
        var again = Provider.CreateGenesis(ChannelId, this._bob, Bob, 1);
        Assert.Equal(MembershipVerdictKind.NotNext, state.Check(again).Kind);
    }

    [Fact]
    public void InviteAndAcceptMakeAMember() {
        var state = this.Genesis();
        var invite = state.Create(MembershipEntryKind.Invite, Bob, this._alice, Alice, this.Now(), MemberKeys.Of(this._bob));
        state = state.Apply(invite);
        Assert.Equal(Bob, Assert.Single(state.Invitees).UserId);
        Assert.Null(state.FindMember(Bob));

        state = state.Apply(state.Create(MembershipEntryKind.Accept, Bob, this._bob, Bob, this.Now()));
        Assert.Equal(Rank.Member, state.FindMember(Bob)!.Rank);
        Assert.Empty(state.Invitees);
    }

    [Fact]
    public void AcceptMustBeSignedByTheInvitedKey() {
        var state = this.Genesis();
        var genesis = state.Head!;
        state = state.Apply(state.Create(MembershipEntryKind.Invite, Bob, this._alice, Alice, this.Now(), MemberKeys.Of(this._bob)));

        // The server (or anyone) signs an accept for Bob with another key, claiming it is his.
        var forged = Forge(state, MembershipEntryKind.Accept, Bob, Bob, this._dave, MemberKeys.Of(this._bob), state.FindInvitee(Bob)!.Invite);
        forged.ActorKeyHash = ByteString.CopyFrom(MemberKeys.Of(this._bob).Hash);
        MembershipEntries.Sign(forged, this._dave);
        Assert.Equal(MembershipVerdictKind.Invalid, state.Check(forged).Kind);

        // An accept with the right key but naming another invite entry.
        var wrongInvite = Forge(state, MembershipEntryKind.Accept, Bob, Bob, this._bob, MemberKeys.Of(this._bob), genesis);
        Assert.Equal(MembershipVerdictKind.Invalid, state.Check(wrongInvite).Kind);
    }

    [Fact]
    public void OrdinaryMembersCannotInvite() {
        var state = this.WithMember(this.Genesis(), Bob, this._bob);
        var verdict = Assert.Throws<MembershipException>(() =>
            state.Create(MembershipEntryKind.Invite, Carol, this._bob, Bob, this.Now(), MemberKeys.Of(this._carol))).Verdict;
        Assert.Equal(MembershipVerdictKind.Forbidden, verdict.Kind);

        // Signed by hand, it is no more valid.
        var forged = Forge(state, MembershipEntryKind.Invite, Bob, Carol, this._bob, MemberKeys.Of(this._carol));
        Assert.Equal(MembershipVerdictKind.Forbidden, state.Check(forged).Kind);
    }

    [Fact]
    public void FormerMembersCannotSignAnything() {
        var state = this.WithMember(this.Genesis(), Bob, this._bob);
        state = state.Apply(state.Create(MembershipEntryKind.SetRank, Bob, this._alice, Alice, this.Now(), rank: Rank.Moderator));
        state = state.Apply(state.Create(MembershipEntryKind.Remove, Bob, this._alice, Alice, this.Now()));

        // Bob was a moderator, so could invite; removed, his signature no longer counts.
        var ghostInvite = Forge(state, MembershipEntryKind.Invite, Bob, Dave, this._bob, MemberKeys.Of(this._dave));
        Assert.Equal(MembershipVerdictKind.Forbidden, state.Check(ghostInvite).Kind);
    }

    [Fact]
    public void InvitesDieWithTheInvitersStanding() {
        var state = this.WithMember(this.WithMember(this.Genesis(), Bob, this._bob), Carol, this._carol);
        state = state.Apply(state.Create(MembershipEntryKind.SetRank, Bob, this._alice, Alice, this.Now(), rank: Rank.Moderator));
        state = state.Apply(state.Create(MembershipEntryKind.SetRank, Carol, this._alice, Alice, this.Now(), rank: Rank.Moderator));
        state = state.Apply(state.Create(MembershipEntryKind.Invite, Dave, this._bob, Bob, this.Now(), MemberKeys.Of(this._dave)));
        using var erin = IdentityKeys.Generate();
        state = state.Apply(state.Create(MembershipEntryKind.Invite, 5, this._carol, Carol, this.Now(), MemberKeys.Of(erin)));

        // Bob is removed, Carol demoted: what they signed as moderators no longer lets anyone in.
        state = state.Apply(state.Create(MembershipEntryKind.Remove, Bob, this._alice, Alice, this.Now()));
        state = state.Apply(state.Create(MembershipEntryKind.SetRank, Carol, this._alice, Alice, this.Now(), rank: Rank.Member));
        Assert.Empty(state.Invitees);
        Assert.Throws<MembershipException>(() => state.Create(MembershipEntryKind.Accept, Dave, this._dave, Dave, this.Now()));
    }

    [Fact]
    public void OneSetOfKeysCannotBeAdmittedTwice() {
        var state = this.WithMember(this.Genesis(), Bob, this._bob);

        // A moderator could otherwise admit their own keys under a second user ID, to keep a seat after removal.
        Assert.Equal(MembershipVerdictKind.Conflict, Assert.Throws<MembershipException>(() =>
            state.Create(MembershipEntryKind.Invite, Dave, this._alice, Alice, this.Now(), MemberKeys.Of(this._bob))).Verdict.Kind);
        state = state.Apply(state.Create(MembershipEntryKind.Invite, Carol, this._alice, Alice, this.Now(), MemberKeys.Of(this._carol)));
        Assert.Equal(MembershipVerdictKind.Conflict, Assert.Throws<MembershipException>(() =>
            state.Create(MembershipEntryKind.Invite, Dave, this._alice, Alice, this.Now(), MemberKeys.Of(this._carol))).Verdict.Kind);
    }

    [Fact]
    public void ANewKeyForAMemberIsNotAMember() {
        var state = this.WithMember(this.Genesis(), Bob, this._bob);
        using var bobsNewKeys = IdentityKeys.Generate();

        // Bob registered again: what his new key signs doesn't count...
        var leave = Forge(state, MembershipEntryKind.Leave, Bob, Bob, bobsNewKeys, MemberKeys.Of(this._bob));
        Assert.Equal(MembershipVerdictKind.Invalid, state.Check(leave).Kind);

        // ...and his new key can't simply be invited while the old one is a member.
        Assert.Equal(MembershipVerdictKind.Conflict, Assert.Throws<MembershipException>(() =>
            state.Create(MembershipEntryKind.Invite, Bob, this._alice, Alice, this.Now(), MemberKeys.Of(bobsNewKeys))).Verdict.Kind);

        // Removed and invited again, it is.
        state = state.Apply(state.Create(MembershipEntryKind.Remove, Bob, this._alice, Alice, this.Now()));
        state = state.Apply(state.Create(MembershipEntryKind.Invite, Bob, this._alice, Alice, this.Now(), MemberKeys.Of(bobsNewKeys)));
        state = state.Apply(state.Create(MembershipEntryKind.Accept, Bob, bobsNewKeys, Bob, this.Now()));
        Assert.Equal(MemberKeys.Of(bobsNewKeys), state.FindMember(Bob)!.Keys);
    }

    [Fact]
    public void RanksDecideRemovals() {
        var state = this.WithMember(this.WithMember(this.WithMember(this.Genesis(), Bob, this._bob), Carol, this._carol), Dave, this._dave);
        state = state.Apply(state.Create(MembershipEntryKind.SetRank, Bob, this._alice, Alice, this.Now(), rank: Rank.Moderator));
        state = state.Apply(state.Create(MembershipEntryKind.SetRank, Carol, this._alice, Alice, this.Now(), rank: Rank.Moderator));

        // A member can't remove anyone; a moderator can't remove another moderator or the admin.
        Assert.Equal(MembershipVerdictKind.Forbidden, state.Check(Forge(state, MembershipEntryKind.Remove, Dave, Bob, this._dave, MemberKeys.Of(this._bob))).Kind);
        Assert.Equal(MembershipVerdictKind.Forbidden, state.Check(Forge(state, MembershipEntryKind.Remove, Bob, Carol, this._bob, MemberKeys.Of(this._carol))).Kind);
        Assert.Equal(MembershipVerdictKind.Forbidden, state.Check(Forge(state, MembershipEntryKind.Remove, Bob, Alice, this._bob, MemberKeys.Of(this._alice))).Kind);

        // A moderator can remove a member, naming the keys the member joined with.
        Assert.Equal(MembershipVerdictKind.Invalid, state.Check(Forge(state, MembershipEntryKind.Remove, Bob, Dave, this._bob, MemberKeys.Of(this._carol))).Kind);
        state = state.Apply(state.Create(MembershipEntryKind.Remove, Dave, this._bob, Bob, this.Now()));
        Assert.Null(state.FindMember(Dave));
    }

    [Fact]
    public void OnlyTheAdminChangesRanksAndTransferMovesTheRole() {
        var state = this.WithMember(this.WithMember(this.Genesis(), Bob, this._bob), Carol, this._carol);
        state = state.Apply(state.Create(MembershipEntryKind.SetRank, Bob, this._alice, Alice, this.Now(), rank: Rank.Moderator));

        // A moderator can't promote anyone, nor can the admin set a rank twice or demote themselves.
        Assert.Equal(MembershipVerdictKind.Forbidden, state.Check(Forge(state, MembershipEntryKind.SetRank, Bob, Carol, this._bob, MemberKeys.Of(this._carol), rank: Rank.Moderator)).Kind);
        Assert.Equal(MembershipVerdictKind.Conflict, state.Check(Forge(state, MembershipEntryKind.SetRank, Alice, Bob, this._alice, MemberKeys.Of(this._bob), rank: Rank.Moderator)).Kind);
        Assert.Equal(MembershipVerdictKind.Invalid, state.Check(Forge(state, MembershipEntryKind.SetRank, Alice, Alice, this._alice, MemberKeys.Of(this._alice), rank: Rank.Member)).Kind);
        Assert.Equal(MembershipVerdictKind.Invalid, state.Check(Forge(state, MembershipEntryKind.SetRank, Alice, Carol, this._alice, MemberKeys.Of(this._carol), rank: Rank.Admin)).Kind);

        state = state.Apply(state.Create(MembershipEntryKind.TransferAdmin, Carol, this._alice, Alice, this.Now()));
        Assert.Equal(Rank.Admin, state.FindMember(Carol)!.Rank);
        Assert.Equal(Rank.Moderator, state.FindMember(Alice)!.Rank);

        // The old admin has lost the role.
        Assert.Equal(MembershipVerdictKind.Forbidden, state.Check(Forge(state, MembershipEntryKind.SetRank, Alice, Bob, this._alice, MemberKeys.Of(this._bob), rank: Rank.Member)).Kind);
        Assert.Equal(MembershipVerdictKind.Forbidden, state.Check(Forge(state, MembershipEntryKind.Remove, Alice, Carol, this._alice, MemberKeys.Of(this._carol))).Kind);
    }

    [Fact]
    public void TheLastAdminCannotLeaveOthersBehind() {
        var state = this.WithMember(this.Genesis(), Bob, this._bob);
        Assert.Equal(MembershipVerdictKind.Forbidden, Assert.Throws<MembershipException>(() =>
            state.Create(MembershipEntryKind.Leave, Alice, this._alice, Alice, this.Now())).Verdict.Kind);

        // Members may leave; then the admin, alone, may too, and nothing follows.
        state = state.Apply(state.Create(MembershipEntryKind.Leave, Bob, this._bob, Bob, this.Now()));
        state = state.Apply(state.Create(MembershipEntryKind.Leave, Alice, this._alice, Alice, this.Now()));
        Assert.Empty(state.Members);
        Assert.Equal(MembershipVerdictKind.Invalid, state.Check(Forge(state, MembershipEntryKind.Invite, Alice, Bob, this._alice, MemberKeys.Of(this._bob))).Kind);
    }

    [Fact]
    public void InvitesCanBeDeclinedOrCancelled() {
        var state = this.Genesis();
        state = state.Apply(state.Create(MembershipEntryKind.Invite, Bob, this._alice, Alice, this.Now(), MemberKeys.Of(this._bob)));
        state = state.Apply(state.Create(MembershipEntryKind.Invite, Carol, this._alice, Alice, this.Now(), MemberKeys.Of(this._carol)));

        // Only the invitee declines; only moderators cancel.
        Assert.Equal(MembershipVerdictKind.Invalid, state.Check(Forge(state, MembershipEntryKind.Decline, Alice, Bob, this._alice, MemberKeys.Of(this._bob), state.FindInvitee(Bob)!.Invite)).Kind);
        state = state.Apply(state.Create(MembershipEntryKind.Decline, Bob, this._bob, Bob, this.Now()));
        state = state.Apply(state.Create(MembershipEntryKind.CancelInvite, Carol, this._alice, Alice, this.Now()));
        Assert.Empty(state.Invitees);
        Assert.Throws<MembershipException>(() => state.Create(MembershipEntryKind.Accept, Bob, this._bob, Bob, this.Now()));
    }

    [Fact]
    public void TheChainCannotBeReorderedOrTamperedWith() {
        var state = this.Genesis();
        var invite = state.Create(MembershipEntryKind.Invite, Bob, this._alice, Alice, this.Now(), MemberKeys.Of(this._bob));

        var otherPrevious = invite.Clone();
        otherPrevious.PreviousHash = ByteString.CopyFrom(new byte[32]);
        Assert.Equal(MembershipVerdictKind.NotNext, state.Check(otherPrevious).Kind);

        var tampered = invite.Clone();
        tampered.Subject = MemberKeys.Of(this._carol).ToProto(Bob);
        Assert.Equal(MembershipVerdictKind.Invalid, state.Check(tampered).Kind);

        var withRank = invite.Clone();
        withRank.Rank = Rank.Moderator;
        MembershipEntries.Sign(withRank, this._alice);
        Assert.Equal(MembershipVerdictKind.Invalid, state.Check(withRank).Kind);
    }

    [Fact]
    public void InviteeKeysMustBeUsable() {
        var state = this.Genesis();
        var lowOrder = new MemberKeys(this._bob.SigningPublicKey, new byte[32]);
        Assert.Equal(MembershipVerdictKind.Invalid, Assert.Throws<MembershipException>(() =>
            state.Create(MembershipEntryKind.Invite, Bob, this._alice, Alice, this.Now(), lowOrder)).Verdict.Kind);
    }

    [Fact]
    public void PositionsStayCurrentUntilTheMembersChange() {
        var state = this.Genesis();
        var genesis = state.Head!;
        state = state.Apply(state.Create(MembershipEntryKind.Invite, Bob, this._alice, Alice, this.Now(), MemberKeys.Of(this._bob)));
        Assert.True(state.IsCurrent(genesis));
        Assert.True(state.IsCurrent(state.Head));
        Assert.False(state.IsCurrent(new LogPosition { Seq = 0, Hash = ByteString.CopyFrom(new byte[32]) }));

        var invited = state.Head!;
        state = state.Apply(state.Create(MembershipEntryKind.Accept, Bob, this._bob, Bob, this.Now()));
        Assert.False(state.IsCurrent(genesis));
        Assert.False(state.IsCurrent(invited));
        Assert.True(state.IsCurrent(state.Head));
        Assert.Equal(state.Head!.Seq, state.MembersChangedAt);
    }

    [Fact]
    public void OnlyARecentSpanOfPositionsIsRemembered() {
        var state = this.WithMember(this.Genesis(), Bob, this._bob);
        var joined = state.Head!;
        for (var i = 0; i < SignedLogMembership.MaxRecentHashes; i++) {
            state = state.Apply(state.Create(MembershipEntryKind.SetRank, Bob, this._alice, Alice, this.Now(), rank: i % 2 == 0 ? Rank.Moderator : Rank.Member));
        }

        // Nobody joined or left, but the position is too far back to check: keys made there no longer count.
        Assert.Equal(joined.Seq, state.MembersChangedAt);
        Assert.Null(state.HashAt(joined.Seq));
        Assert.False(state.IsCurrent(joined));
        Assert.True(state.IsCurrent(state.Head));

        // Restored from a checkpoint, the same.
        var restored = Provider.Restore(state.ToCheckpoint());
        Assert.False(restored.IsCurrent(joined));
        Assert.NotNull(restored.HashAt(joined.Seq + 1));
    }

    [Fact]
    public void CheckpointRestoresTheSameState() {
        var state = this.WithMember(this.Genesis(), Bob, this._bob);
        state = state.Apply(state.Create(MembershipEntryKind.Invite, Carol, this._alice, Alice, this.Now(), MemberKeys.Of(this._carol)));
        var json = System.Text.Json.JsonSerializer.Serialize(state.ToCheckpoint());
        var restored = Provider.Restore(System.Text.Json.JsonSerializer.Deserialize<MembershipCheckpoint>(json)!);

        Assert.True(MembershipEntries.SamePosition(state.Head, restored.Head));
        Assert.Equal(state.Members.OrderBy(m => m.UserId), restored.Members.OrderBy(m => m.UserId));
        Assert.Equal(state.FindInvitee(Carol), restored.FindInvitee(Carol));
        Assert.True(restored.IsCurrent(state.Head));

        // And carries on from there.
        restored = restored.Apply(restored.Create(MembershipEntryKind.Accept, Carol, this._carol, Carol, this.Now()));
        Assert.Equal(3, restored.Members.Count);
    }

    // ---------------------------------------------------------------- helpers

    private long Now() => this._clock++;

    private IChannelMembership Genesis() => Provider.Empty(ChannelId).Apply(Provider.CreateGenesis(ChannelId, this._alice, Alice, this.Now()));

    private IChannelMembership WithMember(IChannelMembership state, long userId, IdentityKeys keys) {
        var inviter = state.Members.First(member => member.Rank == Rank.Admin);
        var inviterKeys = new[] { this._alice, this._bob, this._carol, this._dave }.First(k => MemberKeys.Of(k) == inviter.Keys);
        state = state.Apply(state.Create(MembershipEntryKind.Invite, userId, inviterKeys, inviter.UserId, this.Now(), MemberKeys.Of(keys)));
        return state.Apply(state.Create(MembershipEntryKind.Accept, userId, keys, userId, this.Now()));
    }

    /// <summary>An entry by <paramref name="actorId"/> signed with <paramref name="signer"/>, without any of Create's checks.</summary>
    private static MembershipEntry Forge(IChannelMembership state, MembershipEntryKind kind, long actorId, long subjectId, IdentityKeys signer, MemberKeys subjectKeys,
        LogPosition? invite = null, Rank rank = Rank.Unspecified) {
        var signerKeys = MemberKeys.Of(signer);
        var entry = new MembershipEntry {
            ChannelId = state.ChannelId,
            Seq = state.Head!.Seq + 1,
            PreviousHash = state.Head.Hash,
            Kind = kind,
            ActorId = actorId,
            ActorKeyHash = ByteString.CopyFrom(signerKeys.Hash),
            Subject = subjectKeys.ToProto(subjectId),
            Rank = rank,
            TimestampUnixMs = 1,
            Invite = invite,
        };
        MembershipEntries.Sign(entry, signer);
        return entry;
    }
}
