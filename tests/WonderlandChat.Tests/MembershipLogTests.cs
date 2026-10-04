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

    /// <summary>
    /// Acceptance test 4, against a server that doesn't admit to an older log: after hiding the removal,
    /// it claims a head past the remover's that it won't show, or her head's position with another hash.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    public async Task HiddenRemovalIsDetectedWhateverHeadTheServerClaims(int claimedAfterHiddenSeq) {
        var alice = await this._server.RegisterAsync("Alice Lied To");
        var carol = await this._server.RegisterAsync("Carol Lied About");
        var channelId = await alice.Session.CreateChannelAsync("Lies", Ct);
        await AddMemberAsync(alice, channelId, carol);
        await alice.Session.DisposeAsync();
        alice = await this._server.RestartAsync(alice, this._server.Options(autoRekey: false));
        var before = this._server.Database.GetMembershipCheckpoint(channelId)!;
        var carolKeys = carol.Keys();

        alice.Session.AfterKickRequestForTests = () => {
            this._server.ExecuteSql("""
                DELETE FROM membership_log WHERE channel_id = $channel AND seq > $seq;
                UPDATE channels SET log_seq = $claimed, log_hash = $junk, rekey_pending = 0 WHERE channel_id = $channel;
                INSERT INTO members (channel_id, user_id, rank, joined_at, signing_key, agreement_key) VALUES ($channel, $carol, 2, 0, $signing, $agreement);
                """,
                ("$channel", channelId), ("$seq", (long) before.Seq), ("$claimed", (long) before.Seq + claimedAfterHiddenSeq),
                ("$junk", new byte[32]), ("$carol", carol.UserId),
                ("$signing", carolKeys.SigningKeyArray()), ("$agreement", carolKeys.AgreementKeyArray()));
            return Task.CompletedTask;
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => alice.Session.KickAsync(channelId, carol.UserId, Ct));
        Assert.Contains("hiding", error.Message);
        await WaitFor(() => alice.Notices.FirstOrDefault(n => n.Level == NoticeLevel.Warning && n.Text.Contains("hiding a change")));
        Assert.NotNull(alice.Session.Snapshot.FindChannel(channelId)!.MembershipWarning);
    }

    /// <summary>
    /// Acceptance test 4, against a server that keeps the removal in its log (so it can show the remover everything she
    /// verified) but keeps refusing her rekey: with CONFLICT (it still counts Carol as a member), or with another error
    /// (here FORBIDDEN). Either way the removal hasn't taken effect for the others, who still share the old key with
    /// Carol, and the remover is warned until a rekey after the removal goes through.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemoverIsWarnedWhileTheServerRefusesHerRekey(bool asConflict) {
        var alice = await this._server.RegisterAsync("Alice Refused " + asConflict);
        var bob = await this._server.RegisterAsync("Bob Still Sharing " + asConflict);
        var carol = await this._server.RegisterAsync("Carol Not Gone " + asConflict);
        var channelId = await alice.Session.CreateChannelAsync("Refused", Ct);
        await AddMemberAsync(alice, channelId, bob);
        await AddMemberAsync(alice, channelId, carol);
        var epoch = alice.Session.Snapshot.FindChannel(channelId)!.Epoch;
        // Alice doesn't rekey in the background when the server asks, so the only rekey is the one her kick makes.
        await alice.Session.DisposeAsync();
        alice = await this._server.RestartAsync(alice, this._server.Options(autoRekey: false));
        var aliceKeys = alice.Keys();
        var carolKeys = carol.Keys();

        // The server stores the removal, then refuses every rekey.
        void Refuse() {
            if (asConflict) {
                this._server.ExecuteSql("""
                    INSERT INTO members (channel_id, user_id, rank, joined_at, signing_key, agreement_key) VALUES ($channel, $carol, 2, 0, $signing, $agreement);
                    """, ("$channel", channelId), ("$carol", carol.UserId), ("$signing", carolKeys.SigningKeyArray()), ("$agreement", carolKeys.AgreementKeyArray()));
            } else {
                this._server.ExecuteSql("UPDATE members SET signing_key = $junk WHERE channel_id = $channel AND user_id = $alice;",
                    ("$channel", channelId), ("$alice", alice.UserId), ("$junk", new byte[32]));
            }
        }

        alice.Session.AfterKickRequestForTests = () => {
            Refuse();
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<Exception>(() => alice.Session.KickAsync(channelId, carol.UserId, Ct));
        Assert.Equal(epoch, this._server.Database.GetChannel(channelId)!.Epoch);

        var warning = alice.Session.Snapshot.FindChannel(channelId)!.MembershipWarning;
        Assert.Contains("hasn't taken effect", warning);
        Assert.Contains(alice.Notices, n => n.Level == NoticeLevel.Warning && n.Text == warning);

        // Once the server takes a rekey made after the removal (here Bob's: Alice has used up the server's rekey
        // allowance for now), the removal has taken effect and the warning goes.
        if (asConflict) {
            this._server.ExecuteSql("DELETE FROM members WHERE channel_id = $channel AND user_id = $carol;", ("$channel", channelId), ("$carol", carol.UserId));
        } else {
            this._server.ExecuteSql("UPDATE members SET signing_key = $signing WHERE channel_id = $channel AND user_id = $alice;",
                ("$channel", channelId), ("$alice", alice.UserId), ("$signing", aliceKeys.SigningKeyArray()));
        }

        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { } c && c.Members.All(m => m.User.UserId != carol.UserId) ? c : null);
        await bob.Session.RekeyAsync(channelId, Ct);
        Assert.Equal(epoch + 1, this._server.Database.GetChannel(channelId)!.Epoch);
        await WaitFor(() => alice.Session.Snapshot.FindChannel(channelId) is { MembershipWarning: null, Epoch: var e } c && e == epoch + 1 ? c : null);
    }

    /// <summary>
    /// Acceptance test 4, against a server that shows different members different logs (it equivocates) rather
    /// than rolling its log back for everyone. Alice removes Carol and rekeys to e+1 for the membership after it.
    /// The server shows Bob the log without the removal, and takes his rekey to e+2 for the membership before it;
    /// to Alice it keeps showing her own log, so asking it to show what she verified proves nothing. But an honest
    /// server only takes a rekey at its log's head and at the next epoch, so a key with a newer epoch than hers,
    /// made for an older membership, shows that the server is showing someone another log: she is warned.
    /// </summary>
    /// <param name="asEvent">Bob's key reaches Alice as it is made (EpochAdvanced), or when she fetches keys.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HiddenRemovalIsDetectedWhenTheServerShowsOthersAnotherLog(bool asEvent) {
        var alice = await this._server.RegisterAsync("Alice Equivocated " + asEvent);
        var bob = await this._server.RegisterAsync("Bob Shown Another " + asEvent);
        var carol = await this._server.RegisterAsync("Carol Removed " + asEvent);
        var channelId = await alice.Session.CreateChannelAsync("Two Logs", Ct);
        await AddMemberAsync(alice, channelId, bob);
        await AddMemberAsync(alice, channelId, carol);
        var epoch = alice.Session.Snapshot.FindChannel(channelId)!.Epoch;
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { HasKey: true, RekeyPending: false } c && c.Epoch == epoch
                            && c.Members.Any(m => m.User.UserId == carol.UserId && m.Rank == Rank.Member) ? c : null);
        var withCarol = this._server.Database.GetChannel(channelId)!;
        var carolKeys = carol.Keys();

        // Bob is offline while Alice removes Carol and rekeys, honestly so far.
        await bob.Session.DisposeAsync();
        await alice.Session.KickAsync(channelId, carol.UserId, Ct);
        var afterRemoval = this._server.Database.GetChannel(channelId)!;
        Assert.Equal(epoch + 1, afterRemoval.Epoch);
        var removal = this._server.Database.GetLogEntries(channelId, afterRemoval.LogHead.Seq, 1).Single();
        await alice.Session.DisposeAsync();

        // To Bob, the server shows the log as it was before the removal (keeping the epoch, but not his copy of Alice's key)...
        this._server.ExecuteSql("""
            DELETE FROM membership_log WHERE channel_id = $channel AND seq > $seq;
            UPDATE channels SET log_seq = $seq, log_hash = $hash, rekey_pending = 0,
                name_epoch = $nameEpoch, name_revision = $nameRevision, name_author = $nameAuthor, name_ciphertext = $nameCiphertext,
                name_signature = $nameSignature, name_source_epoch = $sourceEpoch, name_source_revision = $sourceRevision, name_log_seq = $nameSeq, name_log_hash = $nameHash
            WHERE channel_id = $channel;
            INSERT INTO members (channel_id, user_id, rank, joined_at, signing_key, agreement_key) VALUES ($channel, $carol, 2, 0, $signing, $agreement);
            DELETE FROM epoch_keys WHERE channel_id = $channel AND epoch > $epoch AND recipient_id = $bob;
            """,
            ("$channel", channelId), ("$seq", (long) withCarol.LogHead.Seq), ("$hash", withCarol.LogHead.Hash.ToByteArray()),
            ("$nameEpoch", (long) withCarol.Name!.Epoch), ("$nameRevision", (long) withCarol.Name.Revision), ("$nameAuthor", withCarol.Name.AuthorId),
            ("$nameCiphertext", withCarol.Name.Ciphertext.ToByteArray()), ("$nameSignature", withCarol.Name.Signature.ToByteArray()),
            ("$nameSeq", (long) withCarol.Name.LogPosition!.Seq), ("$nameHash", withCarol.Name.LogPosition.Hash.ToByteArray()),
            ("$sourceEpoch", withCarol.Name.CarriedFrom == null ? DBNull.Value : (object) (long) withCarol.Name.CarriedFrom.Epoch),
            ("$sourceRevision", withCarol.Name.CarriedFrom == null ? DBNull.Value : (object) (long) withCarol.Name.CarriedFrom.Revision),
            ("$carol", carol.UserId), ("$signing", carolKeys.SigningKeyArray()), ("$agreement", carolKeys.AgreementKeyArray()),
            ("$epoch", (long) epoch), ("$bob", bob.UserId));

        // ...so Bob, who never saw the removal, rekeys to e+2 for the members he knows, Carol included, and the server takes it.
        var bobAgain = await this._server.RestartAsync(bob, this._server.Options(autoRekey: false));
        await bobAgain.Session.RekeyAsync(channelId, Ct, force: true);
        Assert.Equal(epoch + 2, this._server.Database.GetChannel(channelId)!.Epoch);
        Assert.Single(this._server.Database.GetEpochKeys(channelId, carol.UserId, epoch + 2));

        // To Alice, it shows her own log again, removal and all.
        this._server.ExecuteSql("""
            INSERT INTO membership_log (channel_id, seq, hash, entry) VALUES ($channel, $seq, $hash, $entry);
            UPDATE channels SET log_seq = $seq, log_hash = $hash WHERE channel_id = $channel;
            DELETE FROM members WHERE channel_id = $channel AND user_id = $carol;
            """,
            ("$channel", channelId), ("$seq", (long) removal.Seq), ("$hash", MembershipEntries.Hash(removal)), ("$entry", removal.ToByteArray()),
            ("$carol", carol.UserId));
        EpochKeyForMe? bobsKey = null;
        if (asEvent) {
            // (Held back from her key fetch when she connects, to arrive as the event instead.)
            bobsKey = this._server.Database.GetEpochKeys(channelId, alice.UserId, epoch + 2).Single();
            this._server.ExecuteSql("""
                DELETE FROM epoch_keys WHERE channel_id = $channel AND epoch = $newer AND recipient_id = $alice;
                UPDATE channels SET epoch = $epoch WHERE channel_id = $channel;
                """, ("$channel", channelId), ("$newer", (long) epoch + 2), ("$alice", alice.UserId), ("$epoch", (long) epoch + 1));
        }

        var aliceAgain = await this._server.RestartAsync(alice, this._server.Options(autoRekey: false));
        if (bobsKey != null) {
            await this._server.SendAndSettleAsync(aliceAgain, new Event {
                EpochAdvanced = new EpochAdvanced { ChannelId = channelId, Epoch = epoch + 2, AuthorId = bobAgain.UserId, MyKey = bobsKey.Key },
            });
        }

        // The server can show Alice everything she verified, and still she is warned, and doesn't take the key.
        var view = aliceAgain.Session.Snapshot.FindChannel(channelId)!;
        Assert.Contains("hiding", view.MembershipWarning);
        Assert.Contains(aliceAgain.Notices, n => n.Level == NoticeLevel.Warning && n.Text == view.MembershipWarning);
        Assert.Equal(epoch + 1, view.Epoch);
        Assert.False(aliceAgain.Store.Load().EpochKeys[channelId].ContainsKey(epoch + 2));
    }

    /// <summary>
    /// The quiet side of the above: a key for an older membership, but with no key made for the current one
    /// held, is what an honest server sends now and then (made just before a change this client already
    /// applied). It is refused, but nobody is blamed while the server can show what was verified.
    /// </summary>
    [Fact]
    public async Task AKeyMadeJustBeforeARemovalIsRefusedQuietly() {
        var alice = await this._server.RegisterAsync("Alice Raced");
        var bob = await this._server.RegisterAsync("Bob Raced");
        var carol = await this._server.RegisterAsync("Carol Raced");
        var channelId = await alice.Session.CreateChannelAsync("Raced", Ct);
        await AddMemberAsync(alice, channelId, bob);
        await AddMemberAsync(alice, channelId, carol);
        var epoch = bob.Session.Snapshot.FindChannel(channelId)!.Epoch;
        var withCarol = PositionOf(bob, channelId);
        // From now on Alice doesn't rekey in the background when the server asks.
        await alice.Session.DisposeAsync();
        alice = await this._server.RestartAsync(alice, this._server.Options(autoRekey: false));

        // Alice removes Carol, and drops offline before she can rekey. Bob applies the removal; his key was made before it.
        alice.Session.AfterKickRequestForTests = () => Task.FromException(new InvalidOperationException("Gone offline."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => alice.Session.KickAsync(channelId, carol.UserId, Ct));
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { } c && c.Members.All(m => m.User.UserId != carol.UserId) ? c : null);
        Assert.Equal(epoch, bob.Session.Snapshot.FindChannel(channelId)!.Epoch);

        // A rekey Alice made just before the removal (for the membership with Carol) reaches him now.
        using var aliceKeys = alice.LoadIdentity();
        var key = ChannelCrypto.SealEpochKey(ChannelCrypto.NewEpochKey(), channelId, epoch + 1, withCarol, aliceKeys, alice.UserId, bob.UserId, bob.Keys().AgreementPublicKey);
        await this._server.SendAndSettleAsync(bob, new Event { EpochAdvanced = new EpochAdvanced { ChannelId = channelId, Epoch = epoch + 1, AuthorId = alice.UserId, MyKey = key } });

        Assert.False(bob.Store.Load().EpochKeys[channelId].ContainsKey(epoch + 1));
        Assert.Null(bob.Session.Snapshot.FindChannel(channelId)!.MembershipWarning);
        Assert.DoesNotContain(bob.Notices, n => n.Level == NoticeLevel.Warning);
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

    /// <summary>
    /// Acceptance test 5, against a server that first spends the client's fork check on a junk claim (another hash
    /// at her head, which checks out fine), then within the minute shows her a genuinely forked, validly signed entry.
    /// </summary>
    [Fact]
    public async Task ForkedEntryIsReportedRightAfterAJunkClaim() {
        var (carol, channelId, forkPoint, other) = await this.ForkableChannelAsync("Junk");
        int LogFetches() => carol.Session.GetTrace().Count(entry => entry.Outgoing && entry.Summary.EndsWith(" FetchMembershipLog"));

        // The server claims another hash at Carol's head. She fetches its whole log to look into it: nothing wrong.
        var fetches = LogFetches();
        this.ClaimJunkHead(channelId);
        await carol.Session.RefreshAsync(Ct);
        Assert.True(LogFetches() > fetches);
        Assert.Null(carol.Session.Snapshot.FindChannel(channelId)!.MembershipWarning);

        // Moments later it shows her an entry at a position she verified (the one after the fork point), after the
        // same entry as hers there, but a different one: validly signed by Alice, so a fork in itself.
        await this._server.SendAndSettleAsync(carol, new Event { LogEntryAdded = new LogEntryAdded { ChannelId = channelId, Entry = other } });
        Assert.Contains(carol.Notices, n => n.Level == NoticeLevel.Warning && n.Text.Contains("two different versions"));
        Assert.Contains("two different versions", carol.Session.Snapshot.FindChannel(channelId)!.MembershipWarning);
    }

    /// <summary>
    /// Acceptance test 5: a claim that can only be looked into by fetching the log, made too soon after the last
    /// check, isn't dropped but checked once the interval has passed.
    /// </summary>
    [Fact]
    public async Task AForkClaimMadeTooSoonAfterTheLastCheckIsCheckedLater() {
        var interval = TimeSpan.FromSeconds(3);
        var (carol, channelId, forkPoint, other) = await this.ForkableChannelAsync("Later", this._server.Options(forkCheckInterval: interval));

        // A junk claim uses up the check...
        this.ClaimJunkHead(channelId);
        await carol.Session.RefreshAsync(Ct);
        Assert.Null(carol.Session.Snapshot.FindChannel(channelId)!.MembershipWarning);

        // ...then the server swaps its log for the other version, and Carol sees its head differ from hers.
        this._server.ExecuteSql("""
            DELETE FROM membership_log WHERE channel_id = $channel AND seq > $seq;
            DELETE FROM invites WHERE channel_id = $channel;
            UPDATE channels SET log_seq = $seq, log_hash = $hash WHERE channel_id = $channel;
            """, ("$channel", channelId), ("$seq", (long) forkPoint.Seq), ("$hash", forkPoint.Hash.ToByteArray()));
        Assert.True(this._server.Database.AppendEntry(channelId, other, SomeBox(), new byte[64]));
        await carol.Session.RefreshAsync(Ct);
        // Too soon to fetch the whole log again...
        Assert.Null(carol.Session.Snapshot.FindChannel(channelId)!.MembershipWarning);

        // ...but it is looked into once the interval has passed.
        await WaitFor(() => carol.Notices.FirstOrDefault(n => n.Level == NoticeLevel.Warning && n.Text.Contains("two different versions")));
    }

    /// <summary>
    /// Alice, with Carol, invites Dave at the entry after <c>ForkPoint</c>; Carol has verified it. <c>Other</c> is a
    /// different entry at that position, validly signed by Alice (inviting Erin), for a server to show.
    /// </summary>
    private async Task<(TestClient Carol, string ChannelId, LogPosition ForkPoint, MembershipEntry Other)> ForkableChannelAsync(string suffix, ClientSessionOptions? carolOptions = null) {
        var alice = await this._server.RegisterAsync("Alice " + suffix);
        var carol = await this._server.RegisterAsync("Carol " + suffix, options: carolOptions);
        var dave = await this._server.RegisterAsync("Dave " + suffix);
        var erin = await this._server.RegisterAsync("Erin " + suffix);
        var channelId = await alice.Session.CreateChannelAsync(suffix, Ct);
        await AddMemberAsync(alice, channelId, carol);
        var forkPoint = this._server.Database.GetChannel(channelId)!.LogHead;
        await alice.Session.InviteAsync(channelId, dave.Name, ProtocolInfo.DebugWorldName, Ct);
        await WaitFor(() => carol.Session.Snapshot.FindChannel(channelId)?.Members.FirstOrDefault(m => m.User.UserId == dave.UserId));
        var other = this._server.ForgeEntry(channelId, alice, MembershipEntryKind.Invite, erin.UserId, erin.Keys(), after: forkPoint);
        return (carol, channelId, forkPoint, other);
    }

    /// <summary>The server reports its log's head with a hash that belongs to no entry.</summary>
    private void ClaimJunkHead(string channelId) {
        this._server.ExecuteSql("UPDATE channels SET log_hash = $junk WHERE channel_id = $channel;", ("$channel", channelId), ("$junk", new byte[32]));
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
        // Changed for M1: this used to pass quietly ("an honest server sends such a key now and then"). But Bob holds
        // a key made for the current membership, and this one has a newer epoch: an honest server can't have taken
        // it, as it only takes a rekey at its log's head, which never goes back. So Bob is warned, though the
        // server can show the log he verified. (AKeyMadeJustBeforeARemovalIsRefusedQuietly is the honest case.)
        await this._server.SendAndSettleAsync(bob, new Event { EpochAdvanced = new EpochAdvanced { ChannelId = channelId, Epoch = current.Epoch + 1, AuthorId = alice.UserId, MyKey = stale } });
        Assert.Equal(current.Epoch, bob.Session.Snapshot.FindChannel(channelId)!.Epoch);
        Assert.False(bob.Store.Load().EpochKeys[channelId].ContainsKey(current.Epoch + 1));
        Assert.Contains("hiding", bob.Session.Snapshot.FindChannel(channelId)!.MembershipWarning);

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
        await WaitFor(() => bob.Notices.FirstOrDefault(n => n.Text.StartsWith("Rejected a new key") && n.Text.Contains("hasn't shown you")));
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

    /// <summary>
    /// The last member leaving deletes the channel. An invitee accepting at that moment must not be appended
    /// in between and then deleted with the channel, a member nobody tells.
    /// </summary>
    [Fact]
    public async Task LastMemberLeavingDoesNotDeleteAChannelSomeoneJustJoined() {
        var alice = await this._server.RegisterAsync("Alice Last Out");
        var bob = await this._server.RegisterAsync("Bob Just In");
        var channelId = await alice.Session.CreateChannelAsync("Last Out", Ct);
        await alice.Session.InviteAsync(channelId, bob.Name, ProtocolInfo.DebugWorldName, Ct);
        await WaitFor(() => bob.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == channelId && i.ChannelName != null));

        // Bob's accept is stored after the server checked Alice's leave, and before it acts on it.
        var accept = this._server.NextEntry(channelId, bob, MembershipEntryKind.Accept, bob.UserId);
        this._server.Handler.BeforeAbandonedChannelDeletedForTests = () => Assert.True(this._server.Database.AppendEntry(channelId, accept));

        // Alice is no longer the last member, so, as the admin, she may not leave Bob behind.
        var refused = await Assert.ThrowsAsync<MembershipException>(() => alice.Session.LeaveAsync(channelId, Ct));
        Assert.Equal(MembershipVerdictKind.Forbidden, refused.Verdict.Kind);
        Assert.NotNull(this._server.Database.GetChannel(channelId));
        Assert.Equal(Rank.Admin, this._server.Database.GetRank(channelId, alice.UserId));
        Assert.Equal(Rank.Member, this._server.Database.GetRank(channelId, bob.UserId));
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
        var (sealedName, signature) = ChannelCrypto.SealInvite("Ghosts welcome", channelId, MembershipEntries.PositionOf(entry), invitee.UserId, invitee.Keys().AgreementPublicKey, keys, inviter.UserId);
        return new ClientFrame {
            InviteMember = new InviteMember { ChannelId = channelId, Entry = entry, SealedName = sealedName, Signature = ByteString.CopyFrom(signature) },
        };
    }

    private static SealedBox SomeBox() => new() { EphemeralPublicKey = ByteString.CopyFrom(new byte[32]), Ciphertext = ByteString.CopyFrom(new byte[48]) };
}
