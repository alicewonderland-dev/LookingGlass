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

    /// <summary>
    /// A key the account has replaced (here by registering again with new keys, as after "Reset my identity") is never
    /// registered again: a copy of the old identity left anywhere ("Register again" keeps the key) would otherwise take
    /// the account back and revoke the new identity's logins. Refused when registering starts, and when it completes
    /// for one that started before the key was replaced.
    /// </summary>
    [Fact]
    public async Task AReplacedKeyCannotBeRegisteredAgain() {
        var oldStore = new InMemorySecretStore();
        var alice = await this._server.RegisterAsync("Alice Replaced", oldStore);
        await alice.Session.DisposeAsync();
        using var oldKeys = alice.LoadIdentity();
        var character = new Character { Name = alice.Name, WorldName = ProtocolInfo.DebugWorldName };

        // Registering again with the key it has is fine while that key is the account's...
        await using var early = await this._server.ConnectRawAsync();
        var challenge = (await early.SendAsync(new ClientFrame { StartRegistration = new StartRegistration { Character = character, Identity = oldKeys.ToBundle() } }))
            .RegistrationChallenge!;

        // ...but then the account registers new keys (a reset elsewhere).
        var renewed = await this._server.RegisterAsync(alice.Name, new InMemorySecretStore());
        Assert.NotEqual(alice.Keys(), renewed.Keys());

        // A registration of the old key, started before or after, is refused, and says what to do.
        var url = this._server.ServerUri.AbsoluteUri;
        var late = await early.SendAsync(new ClientFrame {
            CompleteRegistration = new CompleteRegistration {
                ServerUrl = url, Signature = ByteString.CopyFrom(RegistrationProof.Sign(oldKeys, challenge.Nonce.Span, challenge.LodestoneId, url)),
            },
        });
        Assert.Equal(ErrorCode.RegistrationFailed, late.Error?.Code);
        Assert.Contains("Reset my identity", late.Error!.Message);
        await using var again = await this._server.ConnectRawAsync();
        var refused = await again.SendAsync(new ClientFrame { StartRegistration = new StartRegistration { Character = character, Identity = oldKeys.ToBundle() } });
        Assert.Equal(ErrorCode.RegistrationFailed, refused.Error?.Code);
        Assert.Contains("Reset my identity", refused.Error!.Message);

        // The new identity keeps its login and its key.
        Assert.Equal(renewed.Keys().SigningKeyArray(), this._server.Database.GetUser(alice.UserId)!.SigningKey);
        await using var check = await this._server.ConnectRawAsync();
        Assert.NotNull((await check.SendAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = renewed.Store.Load().DeviceToken } })).AuthenticateOk);

        // The plugin's own path: a client holding the old key registers again, and is told why it can't.
        var stale = this._server.StartClient(alice.Name, oldStore);
        await WaitFor(() => stale.Session.Snapshot.State == ConnectionState.LoginNotRecognized ? new object() : null);
        var error = await Assert.ThrowsAsync<ServerErrorException>(() => stale.Session.StartRegistrationAsync(character, Ct));
        Assert.Contains("Reset my identity", error.ServerMessage);
    }

    /// <summary>
    /// "Reset my identity", first step, while connected: the client asks the server to retire the current key. Every
    /// login of the account is revoked at once (this connection's too), the key can't sign in or be registered again, and
    /// the account waits for the new keys' registration through the Lodestone, as one with no valid login does. Until
    /// then others still see the old key, as they would until anyone registers again.
    /// </summary>
    [Fact]
    public async Task RetiringTheIdentityShutsTheOldKeyAndLoginsOutAtOnce() {
        var store = new InMemorySecretStore();
        var alice = await this._server.RegisterAsync("Alice Retires", store);
        var bob = await this._server.RegisterAsync("Bob Sees Old Key");
        var channelId = await alice.Session.CreateChannelAsync("Still Listed", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var token = store.Load().DeviceToken!;
        using var oldKeys = alice.LoadIdentity();
        var url = this._server.ServerUri.AbsoluteUri;
        var userId = alice.UserId;

        // A second device, signed in with the key.
        await using var second = await this._server.ConnectRawAsync();
        var otherToken = (await KeyLoginAsync(second, oldKeys, userId, url)).KeyLoginComplete.DeviceToken;

        await alice.Session.RetireIdentityAsync(Ct);
        Assert.Equal(0, this._server.Database.CountDevices(userId));

        // Both logins are gone, the key can't sign in or register, and this connection is logged out.
        await using var raw = await this._server.ConnectRawAsync();
        Assert.Equal(ErrorCode.NotAuthenticated, (await raw.SendAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = token } })).Error?.Code);
        Assert.Equal(ErrorCode.NotAuthenticated, (await raw.SendAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = otherToken } })).Error?.Code);
        Assert.Equal(ErrorCode.NotAuthenticated, (await KeyLoginAsync(raw, oldKeys, userId, url)).Error?.Code);
        var register = await raw.SendAsync(new ClientFrame {
            StartRegistration = new StartRegistration { Character = new Character { Name = alice.Name, WorldName = ProtocolInfo.DebugWorldName }, Identity = oldKeys.ToBundle() },
        });
        Assert.Equal(ErrorCode.RegistrationFailed, register.Error?.Code);
        await Assert.ThrowsAsync<ServerErrorException>(() => alice.Session.RefreshAsync(Ct));

        // Bob still sees the key the channel's log admitted Alice with: nothing changes for others until she registers again.
        await bob.Session.RefreshAsync(Ct);
        Assert.False(bob.Session.Snapshot.FindChannel(channelId)!.Members.Single(m => m.User.UserId == userId).KeyReplaced);
        Assert.Equal(oldKeys.SigningPublicKey, this._server.Database.GetUser(userId)!.SigningKey);
        await alice.Session.DisposeAsync();

        // New keys register through the Lodestone as usual, and sign in.
        var secrets = store.Load();
        secrets.ResetIdentity();
        store.Save(secrets);
        var reset = this._server.StartClient(alice.Name, store);
        await WaitFor(() => reset.Session.Snapshot.State == ConnectionState.Unregistered ? new object() : null);
        await reset.Session.StartRegistrationAsync(new Character { Name = alice.Name, WorldName = ProtocolInfo.DebugWorldName }, Ct);
        await reset.Session.CompleteRegistrationAsync(Ct);
        await WaitFor(() => reset.Session.Snapshot.State == ConnectionState.Ready ? new object() : null);
        using var newKeys = reset.LoadIdentity();
        await using var after = await this._server.ConnectRawAsync();
        Assert.NotNull((await KeyLoginAsync(after, newKeys, userId, url)).KeyLoginComplete);
    }

    /// <summary>
    /// Retiring needs the identity key, not just a login: a stolen device token alone mustn't be enough to wreck the
    /// owner's identity. The signature covers the login the connection used, so one made for another login (or by
    /// another key) is refused, and nothing changes.
    /// </summary>
    [Fact]
    public async Task RetiringNeedsTheKeyForThisLogin() {
        var alice = await this._server.RegisterAsync("Alice Guarded");
        using var keys = alice.LoadIdentity();
        var token = alice.Store.Load().DeviceToken!;
        var url = this._server.ServerUri.AbsoluteUri;
        await alice.Session.DisposeAsync();

        await using var second = await this._server.ConnectRawAsync();
        var otherToken = (await KeyLoginAsync(second, keys, alice.UserId, url)).KeyLoginComplete.DeviceToken;

        await using var thief = await this._server.ConnectRawAsync();
        Assert.NotNull((await thief.SendAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = token } })).AuthenticateOk);
        using var otherKeys = IdentityKeys.Generate();
        foreach (var signature in new[] {
                     RetireIdentityProof.Sign(otherKeys, alice.UserId, token),
                     RetireIdentityProof.Sign(keys, alice.UserId, otherToken),
                     RetireIdentityProof.Sign(keys, alice.UserId + 1, token),
                     new byte[64],
                 }) {
            var refused = await thief.SendAsync(new ClientFrame { RetireIdentity = new RetireIdentity { Signature = ByteString.CopyFrom(signature) } });
            Assert.Equal(ErrorCode.Forbidden, refused.Error?.Code);
        }

        // Nothing was retired: the logins and the key still work.
        Assert.Equal(2, this._server.Database.CountDevices(alice.UserId));
        await using var raw = await this._server.ConnectRawAsync();
        Assert.NotNull((await KeyLoginAsync(raw, keys, alice.UserId, url)).KeyLoginComplete);

        // Not before logging in either.
        await using var anonymous = await this._server.ConnectRawAsync();
        var early = await anonymous.SendAsync(new ClientFrame { RetireIdentity = new RetireIdentity { Signature = ByteString.CopyFrom(RetireIdentityProof.Sign(keys, alice.UserId, token)) } });
        Assert.Equal(ErrorCode.NotAuthenticated, early.Error?.Code);
    }

    /// <summary>
    /// A retirement that lands after a key login was checked and before its device is added wins: no device for a
    /// retired key. (As registering again with new keys does: see KeyLoginTests.RegisteringAgainDuringAKeyLoginWins.)
    /// </summary>
    [Fact]
    public async Task RetiringDuringAKeyLoginWins() {
        var alice = await this._server.RegisterAsync("Alice Retired Mid Login");
        using var keys = alice.LoadIdentity();
        var url = this._server.ServerUri.AbsoluteUri;
        var user = this._server.Database.GetUser(alice.UserId)!;
        await alice.Session.DisposeAsync();

        await using var raw = await this._server.ConnectRawAsync();
        var challenge = (await raw.SendAsync(new ClientFrame { StartKeyLogin = new StartKeyLogin { UserId = alice.UserId } })).KeyLoginChallenge!.Challenge.ToByteArray();
        this._server.Handler.BeforeKeyLoginDeviceAddedForTests = () => Assert.True(this._server.Database.RetireIdentity(alice.UserId, user.SigningKey, user.KeyVersion));
        var response = await raw.SendAsync(new ClientFrame {
            CompleteKeyLogin = new CompleteKeyLogin {
                Challenge = ByteString.CopyFrom(challenge), ServerUrl = url, Signature = ByteString.CopyFrom(KeyLoginProof.Sign(keys, challenge, alice.UserId, url)),
            },
        });

        Assert.Equal(ErrorCode.NotAuthenticated, response.Error?.Code);
        Assert.Equal(0, this._server.Database.CountDevices(alice.UserId));
    }

    /// <summary>
    /// A login checked just before a retirement (or a registration, which revokes every device too) lands, and put online
    /// just after it disconnected the account, would stay logged in with a deleted login until it closed. It is checked
    /// again once online, and refused.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ALoginCheckedJustBeforeItsDeviceIsRevokedIsRefused(bool retire) {
        var alice = await this._server.RegisterAsync(retire ? "Alice Late Retire" : "Alice Late Register");
        var token = alice.Store.Load().DeviceToken!;
        var user = this._server.Database.GetUser(alice.UserId)!;
        await alice.Session.DisposeAsync();

        await using var raw = await this._server.ConnectRawAsync();
        this._server.Handler.BeforeAuthenticateSetOnlineForTests = () => {
            // As the other request would, all of it, between this login's checks and its going online.
            if (retire) {
                Assert.True(this._server.Database.RetireIdentity(user.UserId, user.SigningKey, user.KeyVersion));
            } else {
                using var newKeys = IdentityKeys.Generate();
                this._server.Database.RegisterUser(user.UserId, user.Name, 0, ProtocolInfo.DebugWorldName, newKeys.ToBundle(), true);
            }

            this._server.Registry.Disconnect(user.UserId, "Revoked");
        };

        var response = await raw.SendAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = token } });
        Assert.Equal(ErrorCode.NotAuthenticated, response.Error?.Code);
        Assert.Null(response.AuthenticateOk);
        Assert.False(this._server.Registry.IsOnline(user.UserId));
        // And the connection is still logged out: requests that need a login are refused.
        Assert.Equal(ErrorCode.NotAuthenticated, (await raw.SendAsync(new ClientFrame { ListChannels = new ListChannels() })).Error?.Code);
    }

    [Fact]
    public void TheRetireSignatureCoversTheUserAndTheLogin() {
        using var keys = IdentityKeys.Generate();
        const string token = "lgt_example";
        var hash = RetireIdentityProof.TokenHash(token);
        // The hash the server stores for a device token.
        Assert.Equal(LookingGlass.Server.Realtime.RequestHandler.HashToken(token), hash);

        var signature = RetireIdentityProof.Sign(keys, 1234, token);
        Assert.True(RetireIdentityProof.Verify(keys.SigningPublicKey, 1234, hash, signature));
        Assert.False(RetireIdentityProof.Verify(keys.SigningPublicKey, 1235, hash, signature));
        Assert.False(RetireIdentityProof.Verify(keys.SigningPublicKey, 1234, RetireIdentityProof.TokenHash("lgt_other"), signature));
        using var other = IdentityKeys.Generate();
        Assert.False(RetireIdentityProof.Verify(other.SigningPublicKey, 1234, hash, signature));
        // Its own domain: the same fields signed for anything else (a key login, say) don't count.
        Assert.False(RetireIdentityProof.Verify(keys.SigningPublicKey, 1234, hash,
            keys.Sign(new SigningPayload(Domains.KeyLogin).Add(1234L).Add(hash).ToArray())));
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
