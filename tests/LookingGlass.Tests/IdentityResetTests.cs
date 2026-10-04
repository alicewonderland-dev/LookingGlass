using Google.Protobuf;
using LookingGlass.Core.Client;
using LookingGlass.Core.Crypto;
using LookingGlass.Protocol;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// "Register again" keeps the identity key, so a character whose login a server lost gets back into its channels.
/// "Reset my identity" is the deliberate way to replace a lost or stolen key: new keys, registering again through the
/// Lodestone, and nothing of the old identity kept; the server then shuts the old keys and logins out.
/// </summary>
public sealed class IdentityResetTests : IAsyncLifetime {
    private Harness _server = null!;

    public ValueTask InitializeAsync() {
        this._server = new Harness();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() {
        await this._server.DisposeAsync();
        DeleteDirectory(this._server.DataDirectory);
    }

    /// <summary>
    /// Registering again (here on a server whose key login is off, so it's the only way back in) keeps the key, so the
    /// character's channels, and the keys it holds for them, still work.
    /// </summary>
    [Fact]
    public async Task RegisteringAgainKeepsTheKeyAndTheChannels() {
        await using var server = new Harness(environment: "Production");
        try {
            var store = new InMemorySecretStore();
            var alice = await server.RegisterAsync("Alice Again", store);
            var bob = await server.RegisterAsync("Bob Again");
            var channelId = await alice.Session.CreateChannelAsync("Survives", Ct);
            await AddMemberAsync(alice, channelId, bob);
            var fingerprint = alice.Session.Snapshot.MyFingerprint;
            await alice.Session.DisposeAsync();

            server.ExecuteSql("DELETE FROM devices WHERE user_id = $id;", ("$id", alice.UserId));
            var again = server.StartClient(alice.Name, store);
            await WaitFor(() => again.Session.Snapshot.State == ConnectionState.LoginNotRecognized ? new object() : null);
            await again.Session.StartRegistrationAsync(new Character { Name = alice.Name, WorldName = ProtocolInfo.DebugWorldName }, Ct);
            await again.Session.CompleteRegistrationAsync(Ct);

            var snapshot = await WaitFor(() => again.Session.Snapshot is { State: ConnectionState.Ready, ChannelsLoaded: true } s ? s : null);
            Assert.Equal(fingerprint, snapshot.MyFingerprint);
            var channel = await WaitFor(() => again.Session.Snapshot.FindChannel(channelId) is { HasKey: true, Name: "Survives" } c ? c : null);
            Assert.Null(channel.MembershipWarning);
            Assert.Equal(Rank.Admin, channel.MyRank);
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
    }

    [Fact]
    public async Task ResettingKeepsNothingOfTheOldIdentity() {
        var store = new InMemorySecretStore();
        var alice = await this._server.RegisterAsync("Alice Wiped", store);
        var bob = await this._server.RegisterAsync("Bob Pinned");
        var channelId = await alice.Session.CreateChannelAsync("Old Life", Ct);
        await AddMemberAsync(alice, channelId, bob);
        alice.Session.BlockUser(4242);
        await alice.Session.DisposeAsync();

        var before = store.Load();
        Assert.Contains(bob.UserId, before.PinnedIdentities.Keys);
        var secrets = store.Load();
        // The session doesn't pin your own keys; if anything ever did, they'd be the old identity's.
        secrets.PinnedIdentities[alice.UserId] = new PinnedIdentity { SigningPublicKey = alice.Keys().SigningKeyArray(), Name = alice.Name };
        secrets.ResetIdentity();

        // A new key pair, and no login.
        Assert.NotNull(secrets.SigningPrivateKey);
        Assert.NotNull(secrets.AgreementPrivateKey);
        Assert.NotEqual(before.SigningPrivateKey, secrets.SigningPrivateKey);
        Assert.NotEqual(before.AgreementPrivateKey, secrets.AgreementPrivateKey);
        Assert.Null(secrets.DeviceToken);
        Assert.Null(secrets.UserId);
        // No channel keys (sealed to the old keys, and no longer the user's to hold), and no pin of the old keys as yours.
        Assert.Empty(secrets.EpochKeys);
        Assert.Empty(secrets.EpochKeyPositions);
        Assert.DoesNotContain(alice.UserId, secrets.PinnedIdentities.Keys);

        // What is about others or about the channels, not about the old keys, stays: the keys pinned for others (so
        // a server can't swap them unnoticed now), who is blocked, and the newest verified log positions, names and
        // message times (so a server can't roll a channel back, or replay into it, if the new identity is invited again).
        Assert.Equal(before.PinnedIdentities[bob.UserId].SigningPublicKey, secrets.PinnedIdentities[bob.UserId].SigningPublicKey);
        Assert.Contains(4242L, secrets.BlockedUsers);
        Assert.Equal(before.Memberships.Keys, secrets.Memberships.Keys);
        Assert.Equal(before.ChannelNameVersions.Keys, secrets.ChannelNameVersions.Keys);
    }

    /// <summary>
    /// The whole reset, end to end: the client goes straight to registering (no login to try, no key login), registers
    /// new keys, and the server shuts out the old login and the old keys. The old channel memberships stay with the old
    /// keys, and others see the key change.
    /// </summary>
    [Fact]
    public async Task AfterAResetTheOldKeysAndLoginAreShutOut() {
        var store = new InMemorySecretStore();
        var alice = await this._server.RegisterAsync("Alice Reset", store);
        var bob = await this._server.RegisterAsync("Bob Watches");
        var channelId = await alice.Session.CreateChannelAsync("Before", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var oldToken = store.Load().DeviceToken!;
        var oldFingerprint = alice.Session.Snapshot.MyFingerprint;
        using var oldKeys = alice.LoadIdentity();
        await alice.Session.DisposeAsync();

        var secrets = store.Load();
        secrets.ResetIdentity();
        store.Save(secrets);

        var reset = this._server.StartClient(alice.Name, store);
        await WaitFor(() => reset.Session.Snapshot.State == ConnectionState.Unregistered ? new object() : null);
        Assert.NotEqual(oldFingerprint, reset.Session.Snapshot.MyFingerprint);
        Assert.DoesNotContain(reset.Session.GetTrace(), entry => entry.Outgoing && (entry.Summary.EndsWith(" Authenticate") || entry.Summary.EndsWith(" StartKeyLogin")));

        await reset.Session.StartRegistrationAsync(new Character { Name = alice.Name, WorldName = ProtocolInfo.DebugWorldName }, Ct);
        await reset.Session.CompleteRegistrationAsync(Ct);
        var snapshot = await WaitFor(() => reset.Session.Snapshot is { State: ConnectionState.Ready, ChannelsLoaded: true } s ? s : null);
        Assert.Equal(alice.UserId, snapshot.Me!.UserId);
        Assert.NotEqual(oldFingerprint, snapshot.MyFingerprint);

        // The server: the old login is revoked and the old keys can't sign in; the new keys can.
        await using var raw = await this._server.ConnectRawAsync();
        Assert.Equal(ErrorCode.NotAuthenticated, (await raw.SendAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = oldToken } })).Error?.Code);
        Assert.Equal(ErrorCode.NotAuthenticated, (await KeyLoginAsync(raw, oldKeys, alice.UserId, this._server.ServerUri.AbsoluteUri)).Error?.Code);
        using var newKeys = reset.LoadIdentity();
        Assert.NotNull((await KeyLoginAsync(raw, newKeys, alice.UserId, this._server.ServerUri.AbsoluteUri)).KeyLoginComplete);

        // The channel's place belongs to the old keys: no key for it, and a warning saying why.
        var channel = await WaitFor(() => reset.Session.Snapshot.FindChannel(channelId) is { MembershipWarning: not null } c ? c : null);
        Assert.False(channel.HasKey);
        Assert.Contains("registered again", channel.MembershipWarning);

        // Bob sees that Alice's key was replaced.
        await bob.Session.RefreshAsync(Ct);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId)?.Members.FirstOrDefault(m => m.User.UserId == alice.UserId) is { KeyReplaced: true } m ? m : null);
    }

    private static async Task<Response> KeyLoginAsync(RawConnection raw, IdentityKeys keys, long userId, string url) {
        var challenge = (await raw.SendAsync(new ClientFrame { StartKeyLogin = new StartKeyLogin { UserId = userId } })).KeyLoginChallenge!.Challenge.ToByteArray();
        return await raw.SendAsync(new ClientFrame {
            CompleteKeyLogin = new CompleteKeyLogin {
                Challenge = ByteString.CopyFrom(challenge),
                ServerUrl = url,
                Signature = ByteString.CopyFrom(KeyLoginProof.Sign(keys, challenge, userId, url)),
            },
        });
    }
}
