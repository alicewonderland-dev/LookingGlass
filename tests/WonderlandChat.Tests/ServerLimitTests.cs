using Google.Protobuf;
using WonderlandChat.Core.Client;
using WonderlandChat.Core.Crypto;
using WonderlandChat.Protocol;
using WonderlandChat.Server.Realtime;
using static WonderlandChat.Tests.Harness;

namespace WonderlandChat.Tests;

/// <summary>Invite abuse, per-user throttles and bounded responses, against an in-process server.</summary>
public sealed class ServerLimitTests : IAsyncLifetime {
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
    public async Task InvitesMustBeSmallAndCorrectlySigned() {
        var alice = await this._server.RegisterAsync("Alice Invite Checks");
        var bob = await this._server.RegisterAsync("Bob Invite Checks");
        var channelId = await alice.Session.CreateChannelAsync("Checks", Ct);
        using var aliceKeys = alice.LoadIdentity();
        var bobAgreement = bob.LoadIdentity().AgreementPublicKey;

        var huge = ChannelCrypto.SealInvite(new string('x', 200), channelId, bob.UserId, bobAgreement, aliceKeys, alice.UserId);
        var error = await Assert.ThrowsAsync<ServerErrorException>(() => alice.Session.SendRawAsync(Invite(channelId, bob.UserId, huge), Ct));
        Assert.Equal(ErrorCode.TooLarge, error.Code);

        using var otherKeys = IdentityKeys.Generate();
        var forged = ChannelCrypto.SealInvite("Checks", channelId, bob.UserId, bobAgreement, otherKeys, alice.UserId);
        error = await Assert.ThrowsAsync<ServerErrorException>(() => alice.Session.SendRawAsync(Invite(channelId, bob.UserId, forged), Ct));
        Assert.Equal(ErrorCode.InvalidRequest, error.Code);

        Assert.Empty(this._server.Database.GetInvitesForUser(bob.UserId));
    }

    [Fact]
    public async Task PendingInvitesPerInviteeAreCapped() {
        var alice = await this._server.RegisterAsync("Alice Invite Cap");
        var bob = await this._server.RegisterAsync("Bob Invite Cap");
        foreach (var seeded in this.SeedChannels(alice, RequestHandler.MaxPendingInvitesPerUser)) {
            this.SeedInvite(alice, seeded, bob);
        }

        var channelId = await alice.Session.CreateChannelAsync("One Too Many", Ct);
        var error = await Assert.ThrowsAsync<ServerErrorException>(() => alice.Session.InviteAsync(channelId, bob.Name, ProtocolInfo.DebugWorldName, Ct));
        Assert.Equal(ErrorCode.LimitReached, error.Code);
    }

    [Fact]
    public async Task ChannelListReturnsABoundedNumberOfInvites() {
        var alice = await this._server.RegisterAsync("Alice Many Invites");
        var bob = await this._server.RegisterAsync("Bob Many Invites");
        // Seeded straight into the database, past every cap, as an old or hostile server state.
        foreach (var seeded in this.SeedChannels(alice, 30)) {
            this.SeedInvite(alice, seeded, bob);
        }

        await bob.Session.RefreshAsync(Ct);
        Assert.Equal(RequestHandler.MaxPendingInvitesPerUser, bob.Session.Snapshot.Invites.Length);
    }

    [Fact]
    public async Task InvitesToOnePersonAreRateLimited() {
        var alice = await this._server.RegisterAsync("Alice Invite Rate");
        var bob = await this._server.RegisterAsync("Bob Invite Rate");
        var channels = this.SeedChannels(alice, 12);
        var invitee = new Invitee(bob.UserId, bob.LoadIdentity().AgreementPublicKey);

        var limited = await this.InviteUntilLimitedAsync(alice, channels.Select(channelId => (channelId, invitee)).ToList());
        Assert.Equal(10, limited);
    }

    [Fact]
    public async Task InvitesFromOnePersonAreRateLimited() {
        var alice = await this._server.RegisterAsync("Alice Invite Spam");
        var channelId = await alice.Session.CreateChannelAsync("Spam", Ct);
        var strangers = Enumerable.Range(0, 22).Select(i => this.SeedUser($"Stranger {i}")).ToList();

        var limited = await this.InviteUntilLimitedAsync(alice, strangers.Select(stranger => (channelId, stranger)).ToList());
        Assert.Equal(20, limited);
    }

    [Fact]
    public async Task InviteCancelLoopsAreRateLimited() {
        var alice = await this._server.RegisterAsync("Alice Invite Loop");
        var bob = await this._server.RegisterAsync("Bob Invite Loop");
        var channelId = await alice.Session.CreateChannelAsync("Loop", Ct);
        using var aliceKeys = alice.LoadIdentity();
        var bobAgreement = bob.LoadIdentity().AgreementPublicKey;

        ServerErrorException? limited = null;
        for (var i = 0; i < 15 && limited == null; i++) {
            try {
                await alice.Session.SendRawAsync(Invite(channelId, bob.UserId, ChannelCrypto.SealInvite("Loop", channelId, bob.UserId, bobAgreement, aliceKeys, alice.UserId)), Ct);
            } catch (ServerErrorException ex) {
                limited = ex;
                break;
            }

            await alice.Session.KickAsync(channelId, bob.UserId, Ct);
        }

        Assert.Equal(ErrorCode.RateLimited, limited?.Code);
        // Cancelling invites never needed a rekey.
        Assert.Equal(0UL, this._server.Database.GetChannel(channelId)!.Epoch);
    }

    [Fact]
    public async Task CreateRenameAndDisbandAreRateLimited() {
        var alice = await this._server.RegisterAsync("Alice Busy Admin");
        var created = new List<string>();
        var error = await Assert.ThrowsAsync<ServerErrorException>(async () => {
            for (var i = 0; i < 12; i++) {
                created.Add(await alice.Session.CreateChannelAsync($"Channel {i}", Ct));
            }
        });
        Assert.Equal(ErrorCode.RateLimited, error.Code);
        Assert.Equal(10, created.Count);

        error = await Assert.ThrowsAsync<ServerErrorException>(async () => {
            for (var i = 0; i < 12; i++) {
                await alice.Session.RenameAsync(created[0], $"Renamed {i}", Ct);
            }
        });
        Assert.Equal(ErrorCode.RateLimited, error.Code);

        var disbanded = 0;
        error = await Assert.ThrowsAsync<ServerErrorException>(async () => {
            foreach (var channelId in created) {
                await alice.Session.DisbandAsync(channelId, Ct);
                disbanded++;
            }
        });
        Assert.Equal(ErrorCode.RateLimited, error.Code);
        Assert.Equal(5, disbanded);
    }

    [Fact]
    public async Task ReadsShareAPerUserBudget() {
        var alice = await this._server.RegisterAsync("Alice Reader");
        var channelId = await alice.Session.CreateChannelAsync("Reading", Ct);

        ServerErrorException? limited = null;
        for (var i = 0; i < 400 && limited == null; i++) {
            ClientFrame frame = (i % 3) switch {
                0 => new ClientFrame { ListChannels = new ListChannels() },
                1 => new ClientFrame { GetIdentities = new GetIdentities { UserIds = { alice.UserId } } },
                _ => new ClientFrame { FetchEpochKeys = new FetchEpochKeys { ChannelId = channelId } },
            };

            try {
                await alice.Session.SendRawAsync(frame, Ct);
            } catch (ServerErrorException ex) {
                limited = ex;
            }
        }

        Assert.Equal(ErrorCode.RateLimited, limited?.Code);
    }

    [Fact]
    public async Task SettingTheSameRankChangesAndAnnouncesNothing() {
        var alice = await this._server.RegisterAsync("Alice Ranks");
        var bob = await this._server.RegisterAsync("Bob Ranks");
        var channelId = await alice.Session.CreateChannelAsync("Ranks", Ct);
        await AddMemberAsync(alice, channelId, bob);

        await alice.Session.SetRankAsync(channelId, bob.UserId, Rank.Member, Ct);
        await alice.Session.SetRankAsync(channelId, bob.UserId, Rank.Moderator, Ct);

        await WaitFor(() => bob.Notices.FirstOrDefault(n => n.Text.Contains("is now moderator")));
        Assert.DoesNotContain(bob.Notices, n => n.Text.Contains("is now member"));
    }

    // ---------------------------------------------------------------- helpers

    private static ClientFrame Invite(string channelId, long inviteeId, (SealedBox SealedName, byte[] Signature) sealedInvite) => new() {
        InviteMember = new InviteMember {
            ChannelId = channelId,
            UserId = inviteeId,
            SealedName = sealedInvite.SealedName,
            Signature = ByteString.CopyFrom(sealedInvite.Signature),
        },
    };

    private sealed record Invitee(long UserId, byte[] AgreementKey);

    /// <returns>How many invites succeeded before the server said RATE_LIMITED.</returns>
    private async Task<int> InviteUntilLimitedAsync(TestClient inviter, List<(string ChannelId, Invitee Invitee)> invites) {
        using var keys = inviter.LoadIdentity();
        var sent = 0;
        foreach (var (channelId, invitee) in invites) {
            var sealedInvite = ChannelCrypto.SealInvite("Seeded", channelId, invitee.UserId, invitee.AgreementKey, keys, inviter.UserId);
            try {
                await inviter.Session.SendRawAsync(Invite(channelId, invitee.UserId, sealedInvite), Ct);
            } catch (ServerErrorException ex) when (ex.Code == ErrorCode.RateLimited) {
                return sent;
            }

            sent++;
        }

        return sent;
    }

    /// <summary>Channels with <paramref name="admin"/> as admin, created in the database directly (no create limit).</summary>
    private List<string> SeedChannels(TestClient admin, int count) {
        using var keys = admin.LoadIdentity();
        var channels = new List<string>();
        for (var i = 0; i < count; i++) {
            var channelId = Guid.NewGuid().ToString("N");
            var key = ChannelCrypto.NewEpochKey();
            this._server.Database.CreateChannel(channelId, admin.UserId,
                ChannelCrypto.SealEpochKey(key, channelId, 0, keys, admin.UserId, admin.UserId, keys.AgreementPublicKey),
                ChannelCrypto.EncryptName($"Seeded {i}", key, channelId, 0, keys, admin.UserId));
            channels.Add(channelId);
        }

        return channels;
    }

    private void SeedInvite(TestClient inviter, string channelId, TestClient invitee) {
        using var keys = inviter.LoadIdentity();
        var (sealedName, signature) = ChannelCrypto.SealInvite("Seeded", channelId, invitee.UserId, invitee.LoadIdentity().AgreementPublicKey, keys, inviter.UserId);
        this._server.Database.AddInvite(channelId, invitee.UserId, inviter.UserId, sealedName, signature);
    }

    /// <summary>A registered user with no client, straight in the database.</summary>
    private Invitee SeedUser(string name) {
        using var keys = IdentityKeys.Generate();
        var (user, _) = this._server.Database.RegisterUser(RequestHandler.DebugUserId(name), name, 0, ProtocolInfo.DebugWorldName, keys.ToBundle(), true);
        return new Invitee(user.UserId, keys.AgreementPublicKey);
    }
}
