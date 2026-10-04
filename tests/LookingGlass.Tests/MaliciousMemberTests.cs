using Google.Protobuf;
using LookingGlass.Core.Client;
using LookingGlass.Core.Crypto;
using LookingGlass.Core.Membership;
using LookingGlass.Protocol;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// A member (sometimes with the server's help) misbehaves: hands out keys
/// that don't match, or keeps posting after being removed. The test sends
/// raw requests as that member, or pushes events as the server.
/// </summary>
public sealed class MaliciousMemberTests : IAsyncLifetime {
    private Harness _server = null!;

    public ValueTask InitializeAsync() {
        this._server = new Harness();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() {
        await this._server.DisposeAsync();
        DeleteDirectory(this._server.DataDirectory);
    }

    [Fact]
    public async Task JunkEpochKeyIsDetectedAndTheChannelRekeyed() {
        var alice = await this._server.RegisterAsync("Alice Junk");
        var bob = await this._server.RegisterAsync("Bob Junk");
        var channelId = await alice.Session.CreateChannelAsync("Wedged", Ct);
        await AddMemberAsync(alice, channelId, bob);
        Assert.Equal(1UL, alice.Session.Snapshot.FindChannel(channelId)!.Epoch);

        // Bob rekeys to epoch 2, but seals Alice a different key than the one he commits to,
        // which would leave her unable to read the channel (or split it in two).
        using var bobKeys = bob.LoadIdentity();
        var position = PositionOf(bob, channelId);
        var real = ChannelCrypto.NewEpochKey();
        var forAlice = ChannelCrypto.SealEpochKey(ChannelCrypto.NewEpochKey(), channelId, 2, position, bobKeys, bob.UserId, alice.UserId, alice.LoadIdentity().AgreementPublicKey);
        forAlice.KeyCommitment = ByteString.CopyFrom(ChannelCrypto.KeyCommitment(channelId, 2, real));
        ChannelCrypto.SignEpochKey(forAlice, channelId, 2, bobKeys, bob.UserId);
        var request = new SubmitRekey {
            ChannelId = channelId,
            NewEpoch = 2,
            LogPosition = position,
            KeyCommitment = ByteString.CopyFrom(ChannelCrypto.KeyCommitment(channelId, 2, real)),
            Name = ChannelCrypto.EncryptName("Wedged", real, channelId, 2, position, bobKeys, bob.UserId),
        };
        request.Keys.Add(forAlice);
        request.Keys.Add(ChannelCrypto.SealEpochKey(real, channelId, 2, position, bobKeys, bob.UserId, bob.UserId, bobKeys.AgreementPublicKey));
        await bob.Session.SendRawAsync(new ClientFrame { SubmitRekey = request }, Ct);

        // Alice is told who did it, and rekeys on her own (not through the debug-only force path).
        await WaitFor(() => alice.Notices.FirstOrDefault(n => n.Level == NoticeLevel.Warning && n.Text.StartsWith("Bob Junk@Debug") && n.Text.Contains("committed")));
        await WaitFor(() => alice.Session.Snapshot.FindChannel(channelId) is { Epoch: 3, HasKey: true, RekeyPending: false } c ? c : null);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { Epoch: 3, HasKey: true } c ? c : null);

        await alice.Session.SendTextAsync(channelId, "recovered", Ct);
        await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == "recovered"));
    }

    [Fact]
    public async Task KickedMembersMessageIsDropped() {
        var alice = await this._server.RegisterAsync("Alice Kicker");
        var bob = await this._server.RegisterAsync("Bob Stays");
        var carol = await this._server.RegisterAsync("Carol Kicked");
        var channelId = await alice.Session.CreateChannelAsync("Kicked", Ct);
        await AddMemberAsync(alice, channelId, bob);
        await AddMemberAsync(alice, channelId, carol);
        var oldEpoch = bob.Session.Snapshot.FindChannel(channelId)!.Epoch;
        // Her client forgets the key once she's removed, but she could have kept a copy.
        var fromCarol = carol.ForgeMessage(channelId, oldEpoch, "still here", DateTimeOffset.UtcNow);

        await alice.Session.KickAsync(channelId, carol.UserId, Ct);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { Epoch: var e } c && e > oldEpoch ? c : null);

        // A server that helps her delivers her message straight after the rekey.
        this._server.Registry.Send(bob.UserId, new Event { ChatMessage = fromCarol });
        await WaitFor(() => bob.Notices.FirstOrDefault(n => n.Text.Contains("isn't a member")));
        Assert.DoesNotContain(bob.Messages, m => m.Text == "still here");
    }

    [Fact]
    public async Task ServerRejectsRekeyWithInconsistentCommitments() {
        var alice = await this._server.RegisterAsync("Alice Commit");
        var bob = await this._server.RegisterAsync("Bob Commit");
        var channelId = await alice.Session.CreateChannelAsync("Commitments", Ct);
        await AddMemberAsync(alice, channelId, bob);

        using var bobKeys = bob.LoadIdentity();
        var position = PositionOf(bob, channelId);
        var real = ChannelCrypto.NewEpochKey();
        var request = new SubmitRekey {
            ChannelId = channelId,
            NewEpoch = 2,
            LogPosition = position,
            KeyCommitment = ByteString.CopyFrom(ChannelCrypto.KeyCommitment(channelId, 2, real)),
            Name = ChannelCrypto.EncryptName("Commitments", real, channelId, 2, position, bobKeys, bob.UserId),
        };
        // Honestly sealed and signed, but a different key (and commitment) for Alice.
        request.Keys.Add(ChannelCrypto.SealEpochKey(ChannelCrypto.NewEpochKey(), channelId, 2, position, bobKeys, bob.UserId, alice.UserId, alice.LoadIdentity().AgreementPublicKey));
        request.Keys.Add(ChannelCrypto.SealEpochKey(real, channelId, 2, position, bobKeys, bob.UserId, bob.UserId, bobKeys.AgreementPublicKey));

        var error = await Assert.ThrowsAsync<ServerErrorException>(() => bob.Session.SendRawAsync(new ClientFrame { SubmitRekey = request }, Ct));
        Assert.Equal(ErrorCode.InvalidRequest, error.Code);
        Assert.Equal(1UL, this._server.Database.GetChannel(channelId)!.Epoch);
    }

    [Fact]
    public async Task NamesOfNewChannelsAndEpochsMustStartAtRevisionZero() {
        var alice = await this._server.RegisterAsync("Alice Revision");
        var bob = await this._server.RegisterAsync("Bob Revision");
        var channelId = await alice.Session.CreateChannelAsync("Revisions", Ct);
        await AddMemberAsync(alice, channelId, bob);

        // A member's rekey with the highest revision would block every rename by the admin.
        using var bobKeys = bob.LoadIdentity();
        var request = this.Rekey(channelId, 2, bob, bobKeys, alice, "Revisions", ulong.MaxValue);
        var error = await Assert.ThrowsAsync<ServerErrorException>(() => bob.Session.SendRawAsync(new ClientFrame { SubmitRekey = request }, Ct));
        Assert.Equal(ErrorCode.InvalidRequest, error.Code);
        Assert.Equal(1UL, this._server.Database.GetChannel(channelId)!.Epoch);

        // The same goes for a new channel (otherwise valid, so the revision is what's refused).
        var newId = Guid.NewGuid().ToString("N");
        var key = ChannelCrypto.NewEpochKey();
        var genesis = SignedLogMembershipProvider.Instance.CreateGenesis(newId, bobKeys, bob.UserId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var genesisPosition = MembershipEntries.PositionOf(genesis);
        error = await Assert.ThrowsAsync<ServerErrorException>(() => bob.Session.SendRawAsync(new ClientFrame {
            CreateChannel = new CreateChannel {
                ChannelId = newId,
                Genesis = genesis,
                CreatorKey = ChannelCrypto.SealEpochKey(key, newId, 0, genesisPosition, bobKeys, bob.UserId, bob.UserId, bobKeys.AgreementPublicKey),
                Name = ChannelCrypto.EncryptName("Created", key, newId, 0, genesisPosition, bobKeys, bob.UserId, revision: 5),
            },
        }, Ct));
        Assert.Equal(ErrorCode.InvalidRequest, error.Code);
        Assert.Null(this._server.Database.GetChannel(newId));

        // Alice can still rename.
        await alice.Session.RenameAsync(channelId, "Still Mine", Ct);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId)?.Name == "Still Mine" ? new object() : null);
    }

    [Fact]
    public async Task RenameThroughARekeyIsAnnounced() {
        var alice = await this._server.RegisterAsync("Alice Announce");
        var bob = await this._server.RegisterAsync("Bob Announce");
        var channelId = await alice.Session.CreateChannelAsync("Proper Name", Ct);
        await AddMemberAsync(alice, channelId, bob);

        // Bob isn't allowed to rename, but rekeys with a different name, claiming to carry over
        // the version Alice holds (epoch 1, revision 0) under the proper name.
        using var bobKeys = bob.LoadIdentity();
        var source = new NameSource { Epoch = 1, Revision = 0 };
        await bob.Session.SendRawAsync(new ClientFrame { SubmitRekey = this.Rekey(channelId, 2, bob, bobKeys, alice, "Bob's Name", 0, source) }, Ct);

        var notice = await WaitFor(() => alice.Notices.FirstOrDefault(n => n.Text.Contains("while rekeying")));
        Assert.Equal(NoticeLevel.Warning, notice.Level);
        Assert.Equal("Bob Announce@Debug changed the channel name from \"Proper Name\" to \"Bob's Name\" while rekeying.", notice.Text);
        Assert.Equal("Bob's Name", alice.Session.Snapshot.FindChannel(channelId)!.Name);

        // An ordinary rekey, which keeps the name, says nothing.
        await alice.Session.RekeyAsync(channelId, Ct, force: true);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { Epoch: 3 } c ? c : null);
        Assert.Single(alice.Notices, n => n.Text.Contains("while rekeying"));
        Assert.DoesNotContain(bob.Notices, n => n.Text.Contains("while rekeying"));
    }

    [Fact]
    public async Task OnlyARekeyCarriesANameOverFromAnEarlierEpoch() {
        var alice = await this._server.RegisterAsync("Alice Source");
        var bob = await this._server.RegisterAsync("Bob Source");
        var channelId = await alice.Session.CreateChannelAsync("Sources", Ct);
        await AddMemberAsync(alice, channelId, bob);

        using var bobKeys = bob.LoadIdentity();
        var fromTheFuture = this.Rekey(channelId, 2, bob, bobKeys, alice, "Sources", 0, new NameSource { Epoch = 2 });
        var error = await Assert.ThrowsAsync<ServerErrorException>(() => bob.Session.SendRawAsync(new ClientFrame { SubmitRekey = fromTheFuture }, Ct));
        Assert.Equal(ErrorCode.InvalidRequest, error.Code);

        using var aliceKeys = alice.LoadIdentity();
        var renamed = ChannelCrypto.EncryptName("Renamed", alice.LoadEpochKey(channelId, 1), channelId, 1, PositionOf(alice, channelId), aliceKeys, alice.UserId, 1, new NameSource { Epoch = 0 });
        error = await Assert.ThrowsAsync<ServerErrorException>(() => alice.Session.SendRawAsync(new ClientFrame { RenameChannel = new RenameChannel { ChannelId = channelId, Name = renamed } }, Ct));
        Assert.Equal(ErrorCode.InvalidRequest, error.Code);

        // The client's own rekeys name the version they carry over.
        await alice.Session.RekeyAsync(channelId, Ct, force: true);
        var stored = this._server.Database.GetChannel(channelId)!.Name!;
        Assert.Equal(new NameSource { Epoch = 1, Revision = 0 }, stored.CarriedFrom);
    }

    [Fact]
    public async Task RenameAtTheRevisionLimitAsksForARekey() {
        var alice = await this._server.RegisterAsync("Alice Limit");
        var channelId = await alice.Session.CreateChannelAsync("Limit", Ct);

        // Beyond what the server can store.
        using var aliceKeys = alice.LoadIdentity();
        var key = alice.LoadEpochKey(channelId, 0);
        var error = await Assert.ThrowsAsync<ServerErrorException>(() => alice.Session.SendRawAsync(new ClientFrame {
            RenameChannel = new RenameChannel { ChannelId = channelId, Name = ChannelCrypto.EncryptName("Too Far", key, channelId, 0, PositionOf(alice, channelId), aliceKeys, alice.UserId, ulong.MaxValue) },
        }, Ct));
        Assert.Equal(ErrorCode.InvalidRequest, error.Code);

        // At the highest revision, the client refuses to rename (rather than wrap around to 0)...
        await alice.Session.SendRawAsync(new ClientFrame {
            RenameChannel = new RenameChannel { ChannelId = channelId, Name = ChannelCrypto.EncryptName("At The Limit", key, channelId, 0, PositionOf(alice, channelId), aliceKeys, alice.UserId, ProtocolInfo.MaxNameRevision) },
        }, Ct);
        await alice.Session.RefreshAsync(Ct);
        Assert.Equal("At The Limit", alice.Session.Snapshot.FindChannel(channelId)!.Name);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => alice.Session.RenameAsync(channelId, "One More", Ct));
        Assert.Contains("Rekey", refused.Message);

        // ...until a rekey starts the next epoch's revisions from 0.
        await alice.Session.RekeyAsync(channelId, Ct, force: true);
        await alice.Session.RenameAsync(channelId, "One More", Ct);
        Assert.Equal(1UL, this._server.Database.GetChannel(channelId)!.Name!.Revision);
    }

    /// <summary>A correctly sealed and signed rekey by <paramref name="author"/> for the two members, with any name, revision and source.</summary>
    private SubmitRekey Rekey(string channelId, ulong epoch, TestClient author, IdentityKeys authorKeys, TestClient other, string name, ulong revision, NameSource? source = null) {
        var key = ChannelCrypto.NewEpochKey();
        var position = PositionOf(author, channelId);
        var request = new SubmitRekey {
            ChannelId = channelId,
            NewEpoch = epoch,
            LogPosition = position,
            KeyCommitment = ByteString.CopyFrom(ChannelCrypto.KeyCommitment(channelId, epoch, key)),
            Name = ChannelCrypto.EncryptName(name, key, channelId, epoch, position, authorKeys, author.UserId, revision, source),
        };
        request.Keys.Add(ChannelCrypto.SealEpochKey(key, channelId, epoch, position, authorKeys, author.UserId, other.UserId, other.LoadIdentity().AgreementPublicKey));
        request.Keys.Add(ChannelCrypto.SealEpochKey(key, channelId, epoch, position, authorKeys, author.UserId, author.UserId, authorKeys.AgreementPublicKey));
        return request;
    }
}
