using Google.Protobuf;
using WonderlandChat.Core.Client;
using WonderlandChat.Core.Crypto;
using WonderlandChat.Protocol;
using static WonderlandChat.Tests.Harness;

namespace WonderlandChat.Tests;

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
        var real = ChannelCrypto.NewEpochKey();
        var forAlice = ChannelCrypto.SealEpochKey(ChannelCrypto.NewEpochKey(), channelId, 2, bobKeys, bob.UserId, alice.UserId, alice.LoadIdentity().AgreementPublicKey);
        forAlice.KeyCommitment = ByteString.CopyFrom(ChannelCrypto.KeyCommitment(channelId, 2, real));
        ChannelCrypto.SignEpochKey(forAlice, channelId, 2, bobKeys, bob.UserId);
        var request = new SubmitRekey {
            ChannelId = channelId,
            NewEpoch = 2,
            KeyCommitment = ByteString.CopyFrom(ChannelCrypto.KeyCommitment(channelId, 2, real)),
            Name = ChannelCrypto.EncryptName("Wedged", real, channelId, 2, bobKeys, bob.UserId),
        };
        request.Keys.Add(forAlice);
        request.Keys.Add(ChannelCrypto.SealEpochKey(real, channelId, 2, bobKeys, bob.UserId, bob.UserId, bobKeys.AgreementPublicKey));
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
        var real = ChannelCrypto.NewEpochKey();
        var request = new SubmitRekey {
            ChannelId = channelId,
            NewEpoch = 2,
            KeyCommitment = ByteString.CopyFrom(ChannelCrypto.KeyCommitment(channelId, 2, real)),
            Name = ChannelCrypto.EncryptName("Commitments", real, channelId, 2, bobKeys, bob.UserId),
        };
        // Honestly sealed and signed, but a different key (and commitment) for Alice.
        request.Keys.Add(ChannelCrypto.SealEpochKey(ChannelCrypto.NewEpochKey(), channelId, 2, bobKeys, bob.UserId, alice.UserId, alice.LoadIdentity().AgreementPublicKey));
        request.Keys.Add(ChannelCrypto.SealEpochKey(real, channelId, 2, bobKeys, bob.UserId, bob.UserId, bobKeys.AgreementPublicKey));

        var error = await Assert.ThrowsAsync<ServerErrorException>(() => bob.Session.SendRawAsync(new ClientFrame { SubmitRekey = request }, Ct));
        Assert.Equal(ErrorCode.InvalidRequest, error.Code);
        Assert.Equal(1UL, this._server.Database.GetChannel(channelId)!.Epoch);
    }
}
