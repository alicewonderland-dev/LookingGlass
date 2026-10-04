using Google.Protobuf;
using WonderlandChat.Core.Client;
using WonderlandChat.Core.Crypto;
using WonderlandChat.Core.Membership;
using WonderlandChat.Protocol;
using static WonderlandChat.Tests.Harness;

namespace WonderlandChat.Tests;

/// <summary>
/// Authenticated membership (design doc, "Authenticated membership (v0.2, revised)"): the
/// server plays a malicious part, and clients must work out the members from the signed log.
/// </summary>
public sealed class MembershipLogTests : IAsyncLifetime {
    private Harness _server = null!;

    public ValueTask InitializeAsync() {
        this._server = new Harness();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() {
        await this._server.DisposeAsync();
        DeleteDirectory(this._server.DataDirectory);
    }

    /// <summary>Acceptance test 1.</summary>
    [Fact]
    public async Task ServerInsertedGhostNeverReceivesAKey() {
        var alice = await this._server.RegisterAsync("Alice Ghostly");
        var bob = await this._server.RegisterAsync("Bob Ghostly");
        var ghost = await this._server.RegisterAsync("Ghost Inserted");
        var channelId = await alice.Session.CreateChannelAsync("Haunted", Ct);
        await AddMemberAsync(alice, channelId, bob);

        // The server adds the ghost to its member list, with no invite or accept from anyone.
        this._server.ExecuteSql("INSERT INTO members (channel_id, user_id, rank, joined_at) VALUES ($channel, $ghost, 2, 0);",
            ("$channel", channelId), ("$ghost", ghost.UserId));
        await alice.Session.RefreshAsync(Ct);

        // Alice rekeys. A server that insists on the ghost may refuse her rekey, but it never gets the ghost a key.
        try {
            await alice.Session.RekeyAsync(channelId, Ct, force: true);
        } catch (Exception ex) when (ex is InvalidOperationException or ServerErrorException) {
            // Refused: the channel stalls, which a malicious server can always do.
        }

        Assert.Empty(this._server.Database.GetEpochKeys(channelId, ghost.UserId, 0));
        Assert.False(ghost.Store.Load().EpochKeys.ContainsKey(channelId));
        Assert.DoesNotContain(alice.Session.Snapshot.FindChannel(channelId)!.Members, m => m.User.UserId == ghost.UserId);
    }

    /// <summary>Acceptance test 2.</summary>
    [Fact]
    public async Task FormerMembersInviteIsRejected() {
        var alice = await this._server.RegisterAsync("Alice Former");
        var bob = await this._server.RegisterAsync("Bob Former");
        var carol = await this._server.RegisterAsync("Carol Former");
        var ghost = await this._server.RegisterAsync("Ghost Former");
        var channelId = await alice.Session.CreateChannelAsync("Formerly", Ct);
        await AddMemberAsync(alice, channelId, bob);
        await AddMemberAsync(alice, channelId, carol);
        await alice.Session.SetRankAsync(channelId, bob.UserId, Rank.Moderator, Ct);
        await alice.Session.KickAsync(channelId, bob.UserId, Ct);
        await WaitFor(() => carol.Session.Snapshot.FindChannel(channelId) is { RekeyPending: false } c && c.Members.All(m => m.User.UserId != bob.UserId) ? c : null);

        // Bob was a moderator and still has his keys. He signs an invite for a ghost; the server refuses it...
        var invite = this._server.ForgeEntry(channelId, bob, MembershipEntryKind.Invite, ghost.UserId, ghost.Keys());
        var error = await Assert.ThrowsAsync<ServerErrorException>(() => bob.Session.SendRawAsync(this.InviteRequest(channelId, bob, ghost, invite), Ct));
        Assert.Equal(ErrorCode.NotFound, error.Code);

        // ...so a malicious server stores it anyway, has the ghost accept, and tells everyone.
        await this.ServerAppendsAsync(channelId, invite, alice, carol);
        var accept = this._server.ForgeEntry(channelId, ghost, MembershipEntryKind.Accept, ghost.UserId, ghost.Keys(), invite: MembershipEntries.PositionOf(invite));
        await this.ServerAppendsAsync(channelId, accept, alice, carol);

        // Bob's signature no longer counts, so the ghost is neither invited nor a member, and gets no key.
        foreach (var client in new[] { alice, carol }) {
            await WaitFor(() => client.Notices.FirstOrDefault(n => n.Level == NoticeLevel.Warning && n.Text.Contains("doesn't check out")));
            Assert.DoesNotContain(client.Session.Snapshot.FindChannel(channelId)!.Members, m => m.User.UserId == ghost.UserId);
        }

        try {
            await alice.Session.RekeyAsync(channelId, Ct, force: true);
        } catch (Exception ex) when (ex is InvalidOperationException or ServerErrorException) {
            // The server wants a key for its ghost and refuses the rekey: the channel stalls, but leaks nothing.
        }

        Assert.Empty(this._server.Database.GetEpochKeys(channelId, ghost.UserId, 0));
        Assert.False(ghost.Store.Load().EpochKeys.ContainsKey(channelId));
    }

    /// <summary>Acceptance test 3.</summary>
    [Fact]
    public async Task NonModeratorsInviteIsRejected() {
        var alice = await this._server.RegisterAsync("Alice Ordinary");
        var bob = await this._server.RegisterAsync("Bob Ordinary");
        var ghost = await this._server.RegisterAsync("Ghost Ordinary");
        var channelId = await alice.Session.CreateChannelAsync("Ordinary", Ct);
        await AddMemberAsync(alice, channelId, bob);

        // Bob is an ordinary member. His own client refuses to invite...
        var refused = await Assert.ThrowsAsync<MembershipException>(() => bob.Session.InviteAsync(channelId, ghost.Name, ProtocolInfo.DebugWorldName, Ct));
        Assert.Equal(MembershipVerdictKind.Forbidden, refused.Verdict.Kind);

        // ...and so does the server, even with a signed entry...
        var invite = this._server.ForgeEntry(channelId, bob, MembershipEntryKind.Invite, ghost.UserId, ghost.Keys());
        var error = await Assert.ThrowsAsync<ServerErrorException>(() => bob.Session.SendRawAsync(this.InviteRequest(channelId, bob, ghost, invite), Ct));
        Assert.Equal(ErrorCode.Forbidden, error.Code);

        // ...and a server that stores it anyway convinces nobody: rank is part of the signed log.
        await this.ServerAppendsAsync(channelId, invite, alice);
        await WaitFor(() => alice.Notices.FirstOrDefault(n => n.Level == NoticeLevel.Warning && n.Text.Contains("doesn't check out")));
        Assert.DoesNotContain(alice.Session.Snapshot.FindChannel(channelId)!.Members, m => m.User.UserId == ghost.UserId);
        Assert.Equal(Rank.Member, alice.Session.Snapshot.FindChannel(channelId)!.Members.Single(m => m.User.UserId == bob.UserId).Rank);
    }

    /// <summary>Acceptance test 4.</summary>
    [Fact]
    public async Task HiddenRemovalIsDetectedByTheRemover() {
        var alice = await this._server.RegisterAsync("Alice Remover");
        var bob = await this._server.RegisterAsync("Bob Unaware");
        var carol = await this._server.RegisterAsync("Carol Removed");
        var channelId = await alice.Session.CreateChannelAsync("Hidden", Ct);
        await AddMemberAsync(alice, channelId, bob);
        await AddMemberAsync(alice, channelId, carol);
        var epoch = alice.Session.Snapshot.FindChannel(channelId)!.Epoch;

        // From now on Alice doesn't rekey in the background when the server asks, so the only rekey is
        // the one her kick makes. Bob is offline while she removes Carol.
        await alice.Session.DisposeAsync();
        alice = await this._server.RestartAsync(alice, this._server.Options(autoRekey: false));
        await bob.Session.DisposeAsync();
        var before = this._server.Database.GetMembershipCheckpoint(channelId)!;
        var carolKeys = carol.Keys();

        // The server stores the removal, answers Alice, then undoes it, so nobody else would ever see it.
        alice.Session.AfterKickRequestForTests = () => {
            this._server.ExecuteSql("""
                DELETE FROM membership_log WHERE channel_id = $channel AND seq > $seq;
                UPDATE channels SET log_seq = $seq, log_hash = $hash, rekey_pending = 0 WHERE channel_id = $channel;
                INSERT INTO members (channel_id, user_id, rank, joined_at, signing_key, agreement_key) VALUES ($channel, $carol, 2, 0, $signing, $agreement);
                """,
                ("$channel", channelId), ("$seq", (long) before.Seq), ("$hash", before.Hash), ("$carol", carol.UserId),
                ("$signing", carolKeys.SigningKeyArray()), ("$agreement", carolKeys.AgreementKeyArray()));
            return Task.CompletedTask;
        };

        // Alice rekeys straight away; the server can't accept a rekey for a removal it hides, and she is told.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => alice.Session.KickAsync(channelId, carol.UserId, Ct));
        Assert.Contains("hiding", error.Message);
        await WaitFor(() => alice.Notices.FirstOrDefault(n => n.Level == NoticeLevel.Warning && n.Text.Contains("hiding a change")));
        Assert.NotNull(alice.Session.Snapshot.FindChannel(channelId)!.MembershipWarning);
        Assert.DoesNotContain(alice.Session.Snapshot.FindChannel(channelId)!.Members, m => m.User.UserId == carol.UserId);
        Assert.Equal(epoch, this._server.Database.GetChannel(channelId)!.Epoch);

        // Bob comes back, never told of the removal, and rekeys for the members he knows: Carol included.
        // That key reaches Alice, who refuses it: it was made for the membership before the removal.
        var bobAgain = await this._server.RestartAsync(bob);
        await bobAgain.Session.RekeyAsync(channelId, Ct, force: true);
        var rejected = await WaitFor(() => alice.Notices.FirstOrDefault(n => n.Text.StartsWith("Rejected a new key") && n.Text.Contains("hiding a change")));
        Assert.Equal(NoticeLevel.Warning, rejected.Level);
        Assert.Equal(epoch, alice.Session.Snapshot.FindChannel(channelId)!.Epoch);
        Assert.False(alice.Store.Load().EpochKeys[channelId].ContainsKey(epoch + 1));
    }

    /// <summary>Acceptance test 5.</summary>
    [Fact]
    public async Task ForkedLogIsReported() {
        var alice = await this._server.RegisterAsync("Alice Forked");
        var bob = await this._server.RegisterAsync("Bob Forked");
        var carol = await this._server.RegisterAsync("Carol Forked");
        var dave = await this._server.RegisterAsync("Dave Forked");
        var erin = await this._server.RegisterAsync("Erin Forked");
        var channelId = await alice.Session.CreateChannelAsync("Forked", Ct);
        await AddMemberAsync(alice, channelId, bob);
        await AddMemberAsync(alice, channelId, carol);
        var forkPoint = this._server.Database.GetChannel(channelId)!.LogHead;

        // Alice invites Dave; everyone sees that entry.
        await alice.Session.InviteAsync(channelId, dave.Name, ProtocolInfo.DebugWorldName, Ct);
        foreach (var client in new[] { bob, carol }) {
            await WaitFor(() => client.Session.Snapshot.FindChannel(channelId)?.Members.FirstOrDefault(m => m.User.UserId == dave.UserId));
        }

        // The server also has Alice sign a different entry at the same position (say, it holds her
        // keys, or shows her another log) and shows that version to Carol.
        var other = this._server.ForgeEntry(channelId, alice, MembershipEntryKind.Invite, erin.UserId, erin.Keys(), after: forkPoint);
        await this._server.SendAndSettleAsync(carol, new Event { LogEntryAdded = new LogEntryAdded { ChannelId = channelId, Entry = other } });
        var notice = await WaitFor(() => carol.Notices.FirstOrDefault(n => n.Text.Contains("two different versions")));
        Assert.Equal(NoticeLevel.Warning, notice.Level);
        var view = carol.Session.Snapshot.FindChannel(channelId)!;
        Assert.Contains("two different versions", view.MembershipWarning);
        Assert.Contains(view.Members, m => m.User.UserId == dave.UserId);
        Assert.DoesNotContain(view.Members, m => m.User.UserId == erin.UserId);

        // Or it swaps its own log for the other version, and Bob notices when he next looks.
        this._server.ExecuteSql("""
            DELETE FROM membership_log WHERE channel_id = $channel AND seq > $seq;
            DELETE FROM invites WHERE channel_id = $channel;
            UPDATE channels SET log_seq = $seq, log_hash = $hash WHERE channel_id = $channel;
            """, ("$channel", channelId), ("$seq", (long) forkPoint.Seq), ("$hash", forkPoint.Hash.ToByteArray()));
        Assert.True(this._server.Database.AppendEntry(channelId, other, SomeBox(), new byte[64]));
        await bob.Session.RefreshAsync(Ct);
        await WaitFor(() => bob.Notices.FirstOrDefault(n => n.Level == NoticeLevel.Warning && n.Text.Contains("two different versions")));
        Assert.Contains(bob.Session.Snapshot.FindChannel(channelId)!.Members, m => m.User.UserId == dave.UserId);
    }

    // ---------------------------------------------------------------- further cases

    [Fact]
    public async Task KeysAndNamesForAnOlderLogPositionAreRejected() {
        var alice = await this._server.RegisterAsync("Alice Older");
        var bob = await this._server.RegisterAsync("Bob Older");
        var carol = await this._server.RegisterAsync("Carol Older");
        var channelId = await alice.Session.CreateChannelAsync("Older", Ct);
        await AddMemberAsync(alice, channelId, bob);
        await AddMemberAsync(alice, channelId, carol);
        var withCarol = PositionOf(bob, channelId);

        await alice.Session.KickAsync(channelId, carol.UserId, Ct);
        var current = await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { RekeyPending: false, HasKey: true } c
                                          && c.Members.All(m => m.User.UserId != carol.UserId) ? c : null);

        // Keys and names validly signed by Alice, but made for the membership that still had Carol in it.
        using var aliceKeys = alice.LoadIdentity();
        var bobAgreement = bob.LoadIdentity().AgreementPublicKey;
        var stale = ChannelCrypto.SealEpochKey(ChannelCrypto.NewEpochKey(), channelId, current.Epoch + 1, withCarol, aliceKeys, alice.UserId, bob.UserId, bobAgreement);
        this._server.Registry.Send(bob.UserId, new Event { EpochAdvanced = new EpochAdvanced { ChannelId = channelId, Epoch = current.Epoch + 1, AuthorId = alice.UserId, MyKey = stale } });
        await WaitFor(() => bob.Notices.FirstOrDefault(n => n.Text.StartsWith("Rejected a new key") && n.Text.Contains("not the current one")));
        Assert.Equal(current.Epoch, bob.Session.Snapshot.FindChannel(channelId)!.Epoch);

        var oldName = ChannelCrypto.EncryptName("Carol Was Here", bob.LoadEpochKey(channelId, current.Epoch), channelId, current.Epoch, withCarol, aliceKeys, alice.UserId, revision: 5);
        await this._server.SendAndSettleAsync(bob, new Event { ChannelRenamed = new ChannelRenamed { ChannelId = channelId, Name = oldName } });
        Assert.Equal("Older", bob.Session.Snapshot.FindChannel(channelId)!.Name);

        // The same made for the current membership is fine.
        var fresh = ChannelCrypto.EncryptName("Still Older", bob.LoadEpochKey(channelId, current.Epoch), channelId, current.Epoch, PositionOf(bob, channelId), aliceKeys, alice.UserId, revision: 5);
        await this._server.SendAndSettleAsync(bob, new Event { ChannelRenamed = new ChannelRenamed { ChannelId = channelId, Name = fresh } });
        Assert.Equal("Still Older", bob.Session.Snapshot.FindChannel(channelId)!.Name);
    }

    [Fact]
    public async Task ClientFetchesAndChecksTheLogWhenItSeesANewerPosition() {
        var alice = await this._server.RegisterAsync("Alice Newer");
        var bob = await this._server.RegisterAsync("Bob Newer");
        var dave = await this._server.RegisterAsync("Dave Newer");
        var channelId = await alice.Session.CreateChannelAsync("Newer", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var current = bob.Session.Snapshot.FindChannel(channelId)!;
        int LogFetches() => bob.Session.GetTrace().Count(entry => entry.Outgoing && entry.Summary.EndsWith(" FetchMembershipLog"));
        var fetchesBefore = LogFetches();

        // An invite by Alice reaches the server's log, but Bob isn't told of it.
        var invite = this._server.NextEntry(channelId, alice, MembershipEntryKind.Invite, dave.UserId, dave.Keys());
        Assert.True(this._server.Database.AppendEntry(channelId, invite, SomeBox(), new byte[64]));
        var newer = MembershipEntries.PositionOf(invite);

        // Then a name made at that newer position arrives: Bob fetches the log, checks it, and accepts the name.
        using var aliceKeys = alice.LoadIdentity();
        var name = ChannelCrypto.EncryptName("Newer Still", bob.LoadEpochKey(channelId, current.Epoch), channelId, current.Epoch, newer, aliceKeys, alice.UserId, revision: 1);
        this._server.Registry.Send(bob.UserId, new Event { ChannelRenamed = new ChannelRenamed { ChannelId = channelId, Name = name } });
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId)?.Name == "Newer Still" ? new object() : null);
        Assert.True(MembershipEntries.SamePosition(newer, bob.Session.Snapshot.FindChannel(channelId)!.LogHead));
        Assert.Contains(bob.Session.Snapshot.FindChannel(channelId)!.Members, m => m.User.UserId == dave.UserId && m.Rank == Rank.Invited);
        Assert.True(LogFetches() > fetchesBefore);

        // A key made at a position the server never shows is refused rather than trusted.
        var unseen = new LogPosition { Seq = newer.Seq + 5, Hash = ByteString.CopyFrom(new byte[32]) };
        var key = ChannelCrypto.SealEpochKey(ChannelCrypto.NewEpochKey(), channelId, current.Epoch + 1, unseen, aliceKeys, alice.UserId, bob.UserId, bob.Keys().AgreementPublicKey);
        this._server.Registry.Send(bob.UserId, new Event { EpochAdvanced = new EpochAdvanced { ChannelId = channelId, Epoch = current.Epoch + 1, AuthorId = alice.UserId, MyKey = key } });
        await WaitFor(() => bob.Notices.FirstOrDefault(n => n.Text.StartsWith("Rejected a new key") && n.Text.Contains("not the current one")));
        Assert.Equal(current.Epoch, bob.Session.Snapshot.FindChannel(channelId)!.Epoch);
    }

    [Fact]
    public async Task RanksAndAdminTransferComeFromTheLog() {
        var alice = await this._server.RegisterAsync("Alice Ranks Log");
        var bob = await this._server.RegisterAsync("Bob Ranks Log");
        var carol = await this._server.RegisterAsync("Carol Ranks Log");
        var dave = await this._server.RegisterAsync("Dave Ranks Log");
        var channelId = await alice.Session.CreateChannelAsync("Ranks Log", Ct);
        await AddMemberAsync(alice, channelId, bob);
        await AddMemberAsync(alice, channelId, carol);

        // Members start out uncompared: whoever invited them took their keys from the server.
        var view = carol.Session.Snapshot.FindChannel(channelId)!;
        Assert.False(view.Members.Single(m => m.User.UserId == bob.UserId).FingerprintCompared);
        Assert.True(view.Members.Single(m => m.User.UserId == carol.UserId).FingerprintCompared);

        // The admin makes Bob a moderator, who may then invite.
        await alice.Session.SetRankAsync(channelId, bob.UserId, Rank.Moderator, Ct);
        await WaitFor(() => carol.Session.Snapshot.FindChannel(channelId)?.Members.FirstOrDefault(m => m.User.UserId == bob.UserId && m.Rank == Rank.Moderator));
        await bob.Session.InviteAsync(channelId, dave.Name, ProtocolInfo.DebugWorldName, Ct);
        await WaitFor(() => carol.Session.Snapshot.FindChannel(channelId)?.Members.FirstOrDefault(m => m.User.UserId == dave.UserId && m.Rank == Rank.Invited));

        // The admin hands the channel to Bob, and is a moderator afterwards.
        await alice.Session.SetRankAsync(channelId, bob.UserId, Rank.Admin, Ct);
        await WaitFor(() => carol.Session.Snapshot.FindChannel(channelId)?.Members.FirstOrDefault(m => m.User.UserId == bob.UserId && m.Rank == Rank.Admin));
        Assert.Equal(Rank.Moderator, carol.Session.Snapshot.FindChannel(channelId)!.Members.Single(m => m.User.UserId == alice.UserId).Rank);
        await WaitFor(() => alice.Session.Snapshot.FindChannel(channelId) is { MyRank: Rank.Moderator } c ? c : null);

        // Alice can't change ranks any more, from her client or with a signed entry.
        var refused = await Assert.ThrowsAsync<MembershipException>(() => alice.Session.SetRankAsync(channelId, carol.UserId, Rank.Moderator, Ct));
        Assert.Equal(MembershipVerdictKind.Forbidden, refused.Verdict.Kind);
        var promote = this._server.ForgeEntry(channelId, alice, MembershipEntryKind.SetRank, carol.UserId, carol.Keys(), rank: Rank.Moderator);
        var error = await Assert.ThrowsAsync<ServerErrorException>(() => alice.Session.SendRawAsync(new ClientFrame {
            SetMemberRank = new SetMemberRank { ChannelId = channelId, Entry = promote },
        }, Ct));
        Assert.Equal(ErrorCode.Forbidden, error.Code);

        // A server that rewrites its rank table changes nothing anyone sees.
        this._server.ExecuteSql("UPDATE members SET rank = 4 WHERE channel_id = $channel AND user_id = $carol;", ("$channel", channelId), ("$carol", carol.UserId));
        await carol.Session.RefreshAsync(Ct);
        await bob.Session.RefreshAsync(Ct);
        Assert.Equal(Rank.Member, carol.Session.Snapshot.FindChannel(channelId)!.MyRank);
        Assert.Equal(Rank.Member, bob.Session.Snapshot.FindChannel(channelId)!.Members.Single(m => m.User.UserId == carol.UserId).Rank);
    }

    [Fact]
    public async Task TheLastAdminCannotLeaveOthersBehind() {
        var alice = await this._server.RegisterAsync("Alice Last Admin");
        var bob = await this._server.RegisterAsync("Bob Last Admin");
        var channelId = await alice.Session.CreateChannelAsync("Last Admin", Ct);
        await AddMemberAsync(alice, channelId, bob);

        var refused = await Assert.ThrowsAsync<MembershipException>(() => alice.Session.LeaveAsync(channelId, Ct));
        Assert.Equal(MembershipVerdictKind.Forbidden, refused.Verdict.Kind);
        var leave = this._server.ForgeEntry(channelId, alice, MembershipEntryKind.Leave, alice.UserId, alice.Keys());
        var error = await Assert.ThrowsAsync<ServerErrorException>(() => alice.Session.SendRawAsync(new ClientFrame {
            LeaveChannel = new LeaveChannel { ChannelId = channelId, Entry = leave },
        }, Ct));
        Assert.Equal(ErrorCode.Forbidden, error.Code);
        Assert.Equal(Rank.Admin, this._server.Database.GetRank(channelId, alice.UserId));

        // Once Bob is admin, Alice may go.
        await alice.Session.SetRankAsync(channelId, bob.UserId, Rank.Admin, Ct);
        await alice.Session.LeaveAsync(channelId, Ct);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { } c && c.Members.All(m => m.User.UserId != alice.UserId) ? c : null);
        Assert.Null(this._server.Database.GetRank(channelId, alice.UserId));
    }

    [Fact]
    public async Task AnOlderLogIsNoticedAfterARestart() {
        var alice = await this._server.RegisterAsync("Alice Rollback");
        var bob = await this._server.RegisterAsync("Bob Rollback");
        var dave = await this._server.RegisterAsync("Dave Rollback");
        var channelId = await alice.Session.CreateChannelAsync("Rollback", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var before = this._server.Database.GetChannel(channelId)!.LogHead;
        await alice.Session.InviteAsync(channelId, dave.Name, ProtocolInfo.DebugWorldName, Ct);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId)?.Members.FirstOrDefault(m => m.User.UserId == dave.UserId));

        // While Bob is away, the server drops the invite from its log, as if it never happened.
        await bob.Session.DisposeAsync();
        this._server.ExecuteSql("""
            DELETE FROM membership_log WHERE channel_id = $channel AND seq > $seq;
            DELETE FROM invites WHERE channel_id = $channel;
            UPDATE channels SET log_seq = $seq, log_hash = $hash WHERE channel_id = $channel;
            """, ("$channel", channelId), ("$seq", (long) before.Seq), ("$hash", before.Hash.ToByteArray()));

        // What Bob verified was saved, so the older log gives the server away.
        var bobAgain = await this._server.RestartAsync(bob);
        await WaitFor(() => bobAgain.Notices.FirstOrDefault(n => n.Level == NoticeLevel.Warning && n.Text.Contains("older version of the membership")));
        Assert.Contains(bobAgain.Session.Snapshot.FindChannel(channelId)!.Members, m => m.User.UserId == dave.UserId);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The server stores an entry without checking it, and tells the given members as it would any other.</summary>
    private async Task ServerAppendsAsync(string channelId, MembershipEntry entry, params TestClient[] told) {
        Assert.True(this._server.Database.AppendEntry(channelId, entry, SomeBox(), new byte[64]));
        foreach (var client in told) {
            await this._server.SendAndSettleAsync(client, new Event { LogEntryAdded = new LogEntryAdded { ChannelId = channelId, Entry = entry } });
        }
    }

    private ClientFrame InviteRequest(string channelId, TestClient inviter, TestClient invitee, MembershipEntry entry) {
        using var keys = inviter.LoadIdentity();
        var (sealedName, signature) = ChannelCrypto.SealInvite("Ghosts welcome", channelId, invitee.UserId, invitee.Keys().AgreementPublicKey, keys, inviter.UserId);
        return new ClientFrame {
            InviteMember = new InviteMember { ChannelId = channelId, Entry = entry, SealedName = sealedName, Signature = ByteString.CopyFrom(signature) },
        };
    }

    private static SealedBox SomeBox() => new() { EphemeralPublicKey = ByteString.CopyFrom(new byte[32]), Ciphertext = ByteString.CopyFrom(new byte[48]) };
}
