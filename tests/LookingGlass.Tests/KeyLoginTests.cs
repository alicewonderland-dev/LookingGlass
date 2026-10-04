using Google.Protobuf;
using LookingGlass.Core.Client;
using LookingGlass.Core.Crypto;
using LookingGlass.Protocol;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// Key login: Lodestone proves who a character is once; after that a client the server knows signs in with its
/// identity key (say, after the server lost its device token), and only falls back to registering again through the
/// Lodestone when the key is lost or the server has never seen the account.
/// </summary>
public sealed class KeyLoginTests : IAsyncLifetime {
    private Harness _server = null!;

    public ValueTask InitializeAsync() {
        this._server = new Harness();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() {
        await this._server.DisposeAsync();
        DeleteDirectory(this._server.DataDirectory);
    }

    // ================================================================ the client

    [Fact]
    public async Task ALostLoginSignsBackInWithTheIdentityKey() {
        var store = new InMemorySecretStore();
        var alice = await this._server.RegisterAsync("Alice Keyed", store);
        var userId = alice.UserId;
        var channelId = await alice.Session.CreateChannelAsync("Kept Channel", Ct);
        var old = store.Load().DeviceToken;
        await alice.Session.DisposeAsync();

        // The server loses the device token, but still knows the account and its key.
        this.DeleteDevices(userId);
        var restarted = this._server.StartClient(alice.Name, store);
        var snapshot = await WaitFor(() => restarted.Session.Snapshot is { State: ConnectionState.Ready, ChannelsLoaded: true } s ? s : null);

        Assert.Equal(userId, snapshot.Me!.UserId);
        Assert.False(snapshot.LoginRejected);
        Assert.Equal("Kept Channel", snapshot.FindChannel(channelId)?.Name);
        var trace = restarted.Session.GetTrace();
        Assert.Contains(trace, entry => entry.Outgoing && entry.Summary.EndsWith(" CompleteKeyLogin"));
        Assert.DoesNotContain(trace, entry => entry.Outgoing && entry.Summary.EndsWith(" StartRegistration"));
        Assert.Single(trace, entry => entry.Outgoing && entry.Summary.EndsWith(" Hello"));

        // The new login is saved, and the next session uses it without signing in with the key again.
        var token = store.Load().DeviceToken;
        Assert.NotNull(token);
        Assert.NotEqual(old, token);
        await restarted.Session.DisposeAsync();
        var again = await this._server.RestartAsync(restarted);
        Assert.Equal(userId, again.UserId);
        Assert.DoesNotContain(again.Session.GetTrace(), entry => entry.Outgoing && entry.Summary.EndsWith(" StartKeyLogin"));
        Assert.Equal(token, store.Load().DeviceToken);
    }

    [Fact]
    public async Task RetryNowSignsInWithTheKeyOnceTheServerKnowsItAgain() {
        var store = new InMemorySecretStore();
        var alice = await this._server.RegisterAsync("Alice Retries Key", store);
        var userId = alice.UserId;
        await alice.Session.DisposeAsync();

        // A server that knows neither the login nor the key (as on the wrong database).
        this.DeleteDevices(userId);
        var realKey = this.ReplaceSigningKey(userId, RandomKey());
        var restarted = this._server.StartClient(alice.Name, store, this._server.Options(loginRetryDelay: TimeSpan.FromHours(1)));
        await WaitFor(() => restarted.Session.Snapshot.State == ConnectionState.LoginNotRecognized ? new object() : null);
        Assert.Contains(restarted.Session.GetTrace(), entry => entry.Outgoing && entry.Summary.EndsWith(" CompleteKeyLogin"));

        // It knows the key again (but still not the login): "Retry now" signs in with the key.
        this.ReplaceSigningKey(userId, realKey);
        await restarted.Session.RetryLoginAsync(Ct);
        await WaitFor(() => restarted.Session.Snapshot is { State: ConnectionState.Ready, LoginRejected: false } s ? s : null);
        Assert.Equal(userId, restarted.Session.Snapshot.Me!.UserId);
        Assert.DoesNotContain(restarted.Session.GetTrace(), entry => entry.Outgoing && entry.Summary.EndsWith(" StartRegistration"));
    }

    [Fact]
    public async Task OldKeysCannotSignInAfterRegisteringAgainWithNewOnes() {
        var oldStore = new InMemorySecretStore();
        var first = await this._server.RegisterAsync("Alice Rekeyed", oldStore);
        var userId = first.UserId;
        var oldToken = oldStore.Load().DeviceToken!;
        await first.Session.DisposeAsync();

        // Registering again with new keys (a new PC, say) revokes the old login...
        var renewed = await this._server.RegisterAsync("Alice Rekeyed", new InMemorySecretStore());
        Assert.Equal(userId, renewed.UserId);
        Assert.NotEqual(first.Keys(), renewed.Keys());

        // ...and the old keys can't get a new one.
        var stale = this._server.StartClient("Alice Rekeyed", oldStore);
        var snapshot = await WaitFor(() => stale.Session.Snapshot is { State: ConnectionState.LoginNotRecognized } s ? s : null);
        Assert.True(snapshot.LoginRejected);
        Assert.Contains(stale.Session.GetTrace(), entry => entry.Outgoing && entry.Summary.EndsWith(" CompleteKeyLogin"));
        Assert.Equal(oldToken, oldStore.Load().DeviceToken);
        // The status says Lodestone registration is needed only if the key is lost or the account unknown.
        Assert.Contains("register again", snapshot.StatusText);
        Assert.Contains("Lodestone", snapshot.StatusText);
        Assert.Contains("only", snapshot.StatusText);

        // The old login stays revoked, and the old keys are refused however they're presented.
        await using var raw = await this._server.ConnectRawAsync();
        var refused = await raw.SendAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = oldToken } });
        Assert.Equal(ErrorCode.NotAuthenticated, refused.Error?.Code);
        using var oldKeys = first.LoadIdentity();
        Assert.Equal(ErrorCode.NotAuthenticated, (await this.KeyLoginAsync(raw, oldKeys, userId)).Error?.Code);

        // The new registration is untouched.
        Assert.Equal(ConnectionState.Ready, renewed.Session.Snapshot.State);
    }

    [Fact]
    public async Task ANewClientRegistersThroughTheLodestoneWithoutTryingKeyLogin() {
        var fresh = this._server.StartClient("Alice Fresh");
        await WaitFor(() => fresh.Session.Snapshot.State == ConnectionState.Unregistered ? new object() : null);
        await Task.Delay(300, Ct);
        Assert.DoesNotContain(fresh.Session.GetTrace(), entry => entry.Outgoing && entry.Summary.EndsWith(" StartKeyLogin"));

        await fresh.Session.StartRegistrationAsync(new Character { Name = "Alice Fresh", WorldName = ProtocolInfo.DebugWorldName }, Ct);
        await fresh.Session.CompleteRegistrationAsync(Ct);
        Assert.Equal(ConnectionState.Ready, fresh.Session.Snapshot.State);
    }

    [Fact]
    public async Task AClientWhoseKeysAreGoneFallsBackToRegistering() {
        var store = new InMemorySecretStore();
        var alice = await this._server.RegisterAsync("Alice Lost Keys", store);
        var userId = alice.UserId;
        await alice.Session.DisposeAsync();

        // The login is lost on the server, and the keys on the client: nothing to sign in with.
        this.DeleteDevices(userId);
        var saved = store.Load();
        var withoutKeys = new InMemorySecretStore();
        withoutKeys.Save(new ClientSecrets { DeviceToken = saved.DeviceToken, UserId = saved.UserId });
        var keyless = this._server.StartClient(alice.Name, withoutKeys);
        var snapshot = await WaitFor(() => keyless.Session.Snapshot is { State: ConnectionState.LoginNotRecognized } s ? s : null);
        Assert.DoesNotContain(keyless.Session.GetTrace(), entry => entry.Outgoing && entry.Summary.EndsWith(" StartKeyLogin"));
        Assert.Contains("register again", snapshot.StatusText);

        // Registering again through the Lodestone (a debug account here) works as before.
        await keyless.Session.StartRegistrationAsync(new Character { Name = alice.Name, WorldName = ProtocolInfo.DebugWorldName }, Ct);
        await keyless.Session.CompleteRegistrationAsync(Ct);
        Assert.Equal(ConnectionState.Ready, keyless.Session.Snapshot.State);
        Assert.Equal(userId, keyless.Session.Snapshot.Me!.UserId);

        // And with the secrets file gone entirely, the client starts unregistered and registers.
        await keyless.Session.DisposeAsync();
        var fresh = await this._server.RegisterAsync(alice.Name);
        Assert.Equal(userId, fresh.UserId);
    }

    // ================================================================ the server's checks

    [Fact]
    public async Task KeyLoginGivesANewDeviceAndKeepsTheOthers() {
        var alice = await this._server.RegisterAsync("Alice Two Devices");
        var first = alice.Store.Load().DeviceToken!;

        await using var raw = await this._server.ConnectRawAsync();
        using var keys = alice.LoadIdentity();
        var complete = (await this.KeyLoginAsync(raw, keys, alice.UserId)).KeyLoginComplete;
        Assert.NotNull(complete);
        Assert.Equal(alice.UserId, complete.User.UserId);
        Assert.NotEqual(first, complete.DeviceToken);

        // The new token logs in; the old one still does, and its session wasn't dropped.
        var ok = await raw.SendAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = complete.DeviceToken } });
        Assert.Equal(alice.UserId, ok.AuthenticateOk?.User.UserId);
        await using var other = await this._server.ConnectRawAsync();
        Assert.NotNull((await other.SendAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = first } })).AuthenticateOk);
        await Task.Delay(200, Ct);
        Assert.Equal(ConnectionState.Ready, alice.Session.Snapshot.State);
    }

    /// <summary>
    /// A malicious server the user connects to could ask this server for a challenge for the user, have the user
    /// sign it, and replay the signature here. The signature names the server it was made for, so it fails.
    /// </summary>
    [Fact]
    public async Task ASignatureMadeForAnotherServerIsRejected() {
        var alice = await this._server.RegisterAsync("Alice Relayed");
        using var keys = alice.LoadIdentity();

        // Signed for the relaying server's address, and sent with it.
        await using var relay = await this._server.ConnectRawAsync();
        var challenge = await this.ChallengeAsync(relay, alice.UserId);
        var relayed = await this.CompleteAsync(relay, challenge, "wss://evil.example/ws", KeyLoginProof.Sign(keys, challenge, alice.UserId, "wss://evil.example/ws"));
        Assert.Equal(ErrorCode.NotAuthenticated, relayed.Error?.Code);
        Assert.Null(relayed.KeyLoginComplete);

        // Signed for the relaying server's address, but sent with this one's: the signature doesn't cover it.
        challenge = await this.ChallengeAsync(relay, alice.UserId);
        var swapped = await this.CompleteAsync(relay, challenge, this._server.ServerUri.AbsoluteUri,
            KeyLoginProof.Sign(keys, challenge, alice.UserId, "wss://evil.example/ws"));
        Assert.Equal(ErrorCode.NotAuthenticated, swapped.Error?.Code);

        // Signed for this server: accepted.
        await using var honest = await this._server.ConnectRawAsync();
        Assert.NotNull((await this.KeyLoginAsync(honest, keys, alice.UserId)).KeyLoginComplete);
    }

    [Theory]
    [InlineData("http://localhost/ws", true)]
    [InlineData("ws://localhost/ws", true)]
    [InlineData("WS://LocalHost:80/ws", true)]
    [InlineData("ws://localhost./ws", true)]
    [InlineData("ws://localhost:81/ws", false)]
    [InlineData("wss://localhost/ws", false)]
    [InlineData("ws://localhost.evil.example/ws", false)]
    [InlineData("ws://evil.example/ws?localhost", false)]
    [InlineData("localhost", false)]
    [InlineData("", false)]
    public async Task TheSignedAddressMustBeThisServer(string url, bool accepted) {
        var alice = await this._server.RegisterAsync("Alice Address");
        using var keys = alice.LoadIdentity();
        await using var raw = await this._server.ConnectRawAsync();
        var response = await this.KeyLoginAsync(raw, keys, alice.UserId, url);
        Assert.Equal(accepted, response.KeyLoginComplete != null);
    }

    /// <summary>
    /// Behind <c>tailscale serve</c> (or another proxy on the same machine), the client connects with wss to the
    /// tailnet name; the proxy keeps the Host header and says the original scheme in X-Forwarded-Proto.
    /// </summary>
    [Fact]
    public async Task BehindALocalHttpsProxyTheAddressTheClientUsedIsAccepted() {
        var alice = await this._server.RegisterAsync("Alice Proxied");
        using var keys = alice.LoadIdentity();
        const string host = "lookingglasschat.tail1234.ts.net";

        await using var proxied = await this._server.ConnectRawAsync(host, remoteAddress: "127.0.0.1", forwardedFor: ("100.101.102.103", "https"));
        Assert.NotNull((await this.KeyLoginAsync(proxied, keys, alice.UserId, $"wss://{host}/ws")).KeyLoginComplete);

        // The same headers from a proxy that isn't trusted say nothing: the connection was plain HTTP.
        await using var untrusted = await this._server.ConnectRawAsync(host, remoteAddress: "198.51.100.7", forwardedFor: ("100.101.102.103", "https"));
        Assert.Equal(ErrorCode.NotAuthenticated, (await this.KeyLoginAsync(untrusted, keys, alice.UserId, $"wss://{host}/ws")).Error?.Code);
    }

    /// <summary>
    /// The Host header comes from whoever opened the connection, so a relaying server could send the victim's view of
    /// its own address. With <c>PublicUrls</c> set, the server goes by its configured addresses instead.
    /// </summary>
    [Fact]
    public async Task WithPublicUrlsOnlyThoseAddressesAreAccepted() {
        await using var server = new Harness(settings: ("LookingGlass:PublicUrls:0", "wss://chat.example.com/ws"));
        try {
            var alice = await server.RegisterAsync("Alice Public");
            using var keys = alice.LoadIdentity();

            // A relay that sends a Host header matching the address the user signed for gets nowhere...
            await using var spoofed = await server.ConnectRawAsync(host: "evil.example");
            Assert.Equal(ErrorCode.NotAuthenticated, (await this.KeyLoginAsync(spoofed, keys, alice.UserId, "ws://evil.example/ws")).Error?.Code);

            // ...nor does this connection's own address, which isn't listed...
            await using var local = await server.ConnectRawAsync();
            Assert.Equal(ErrorCode.NotAuthenticated, (await this.KeyLoginAsync(local, keys, alice.UserId, server.ServerUri.AbsoluteUri)).Error?.Code);

            // ...while the public address works, whatever Host the proxy in front passes on.
            await using var proxied = await server.ConnectRawAsync();
            Assert.NotNull((await this.KeyLoginAsync(proxied, keys, alice.UserId, "wss://chat.example.com/ws")).KeyLoginComplete);
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
    }

    [Fact]
    public async Task AChallengeCanBeUsedOnce() {
        var alice = await this._server.RegisterAsync("Alice Once");
        using var keys = alice.LoadIdentity();
        await using var raw = await this._server.ConnectRawAsync();

        var challenge = await this.ChallengeAsync(raw, alice.UserId);
        var url = this._server.ServerUri.AbsoluteUri;
        var signature = KeyLoginProof.Sign(keys, challenge, alice.UserId, url);
        Assert.NotNull((await this.CompleteAsync(raw, challenge, url, signature)).KeyLoginComplete);
        Assert.Equal(ErrorCode.NotAuthenticated, (await this.CompleteAsync(raw, challenge, url, signature)).Error?.Code);

        // A failed answer uses it up too.
        challenge = await this.ChallengeAsync(raw, alice.UserId);
        Assert.Equal(ErrorCode.NotAuthenticated, (await this.CompleteAsync(raw, challenge, url, new byte[64])).Error?.Code);
        Assert.Equal(ErrorCode.NotAuthenticated, (await this.CompleteAsync(raw, challenge, url, KeyLoginProof.Sign(keys, challenge, alice.UserId, url))).Error?.Code);
    }

    [Fact]
    public async Task AChallengeExpires() {
        var clock = new ManualClock();
        await using var server = new Harness(serverTime: clock);
        try {
            var alice = await server.RegisterAsync("Alice Late");
            using var keys = alice.LoadIdentity();
            await using var raw = await server.ConnectRawAsync();
            var url = server.ServerUri.AbsoluteUri;

            var challenge = await this.ChallengeAsync(raw, alice.UserId);
            clock.Offset += TimeSpan.FromSeconds(61);
            Assert.Equal(ErrorCode.NotAuthenticated, (await this.CompleteAsync(raw, challenge, url, KeyLoginProof.Sign(keys, challenge, alice.UserId, url))).Error?.Code);

            // A fresh one, answered in time, works.
            challenge = await this.ChallengeAsync(raw, alice.UserId);
            clock.Offset += TimeSpan.FromSeconds(50);
            Assert.NotNull((await this.CompleteAsync(raw, challenge, url, KeyLoginProof.Sign(keys, challenge, alice.UserId, url))).KeyLoginComplete);
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
    }

    [Fact]
    public async Task AChallengeOnlyWorksOnTheConnectionItWasIssuedOn() {
        var alice = await this._server.RegisterAsync("Alice Elsewhere");
        using var keys = alice.LoadIdentity();
        var url = this._server.ServerUri.AbsoluteUri;
        await using var issued = await this._server.ConnectRawAsync();
        await using var other = await this._server.ConnectRawAsync();

        var challenge = await this.ChallengeAsync(issued, alice.UserId);
        var signature = KeyLoginProof.Sign(keys, challenge, alice.UserId, url);
        Assert.Equal(ErrorCode.NotAuthenticated, (await this.CompleteAsync(other, challenge, url, signature)).Error?.Code);

        // Not even on a connection waiting for a challenge of its own.
        var own = await this.ChallengeAsync(other, alice.UserId);
        Assert.NotEqual(own, challenge);
        Assert.Equal(ErrorCode.NotAuthenticated, (await this.CompleteAsync(other, challenge, url, signature)).Error?.Code);

        // On its own connection it still works.
        Assert.NotNull((await this.CompleteAsync(issued, challenge, url, signature)).KeyLoginComplete);
    }

    [Fact]
    public async Task WrongOrUnknownUsersGetTheSameErrorAsABadSignature() {
        var alice = await this._server.RegisterAsync("Alice Generic");
        var bob = await this._server.RegisterAsync("Bob Generic");
        using var aliceKeys = alice.LoadIdentity();
        await using var raw = await this._server.ConnectRawAsync();
        var url = this._server.ServerUri.AbsoluteUri;

        // A user nobody registered still gets a challenge, so asking doesn't tell who is registered.
        var unknown = await raw.SendAsync(new ClientFrame { StartKeyLogin = new StartKeyLogin { UserId = 4242424242 } });
        Assert.Equal(KeyLoginProof.ChallengeSize, unknown.KeyLoginChallenge?.Challenge.Length);
        var unknownChallenge = unknown.KeyLoginChallenge!.Challenge.ToByteArray();
        var errors = new List<Error> {
            (await this.CompleteAsync(raw, unknownChallenge, url, KeyLoginProof.Sign(aliceKeys, unknownChallenge, 4242424242, url))).Error!,
        };

        // Bob's account, with Alice's key.
        await using var second = await this._server.ConnectRawAsync();
        var bobChallenge = await this.ChallengeAsync(second, bob.UserId);
        errors.Add((await this.CompleteAsync(second, bobChallenge, url, KeyLoginProof.Sign(aliceKeys, bobChallenge, bob.UserId, url))).Error!);

        // Alice's account, signed for Bob's ID.
        var aliceChallenge = await this.ChallengeAsync(second, alice.UserId);
        errors.Add((await this.CompleteAsync(second, aliceChallenge, url, KeyLoginProof.Sign(aliceKeys, aliceChallenge, bob.UserId, url))).Error!);

        // And a garbage signature.
        await using var third = await this._server.ConnectRawAsync();
        var garbage = await this.ChallengeAsync(third, alice.UserId);
        errors.Add((await this.CompleteAsync(third, garbage, url, new byte[64])).Error!);

        Assert.All(errors, error => Assert.Equal(ErrorCode.NotAuthenticated, error.Code));
        Assert.Single(errors.Select(error => error.Message).Distinct());
    }

    [Fact]
    public async Task ChallengesAreLimitedPerConnection() {
        var alice = await this._server.RegisterAsync("Alice Many");
        await using var raw = await this._server.ConnectRawAsync();
        for (var i = 0; i < 3; i++) {
            Assert.NotNull((await raw.SendAsync(new ClientFrame { StartKeyLogin = new StartKeyLogin { UserId = alice.UserId } })).KeyLoginChallenge);
        }

        Assert.Equal(ErrorCode.RateLimited, (await raw.SendAsync(new ClientFrame { StartKeyLogin = new StartKeyLogin { UserId = alice.UserId } })).Error?.Code);
    }

    [Fact]
    public async Task ChallengesAndFailuresAreLimitedPerAddress() {
        await using var server = new Harness(settings: [
            ("LookingGlass:Limits:KeyLoginsPerHourPerIp", "4"),
            ("LookingGlass:Limits:KeyLoginFailuresPerHourPerIp", "2"),
        ]);
        try {
            var alice = await server.RegisterAsync("Alice Address Limit");
            using var keys = alice.LoadIdentity();
            var url = server.ServerUri.AbsoluteUri;

            // Two failures from one address, and it gets no more challenges...
            await using var first = await server.ConnectRawAsync(remoteAddress: "203.0.113.1");
            for (var i = 0; i < 2; i++) {
                var challenge = await this.ChallengeAsync(first, alice.UserId);
                Assert.Equal(ErrorCode.NotAuthenticated, (await this.CompleteAsync(first, challenge, url, new byte[64])).Error?.Code);
            }

            await using var again = await server.ConnectRawAsync(remoteAddress: "203.0.113.1");
            Assert.Equal(ErrorCode.RateLimited, (await again.SendAsync(new ClientFrame { StartKeyLogin = new StartKeyLogin { UserId = alice.UserId } })).Error?.Code);

            // ...while another address can still sign in.
            await using var elsewhere = await server.ConnectRawAsync(remoteAddress: "203.0.113.2");
            Assert.NotNull((await this.KeyLoginAsync(elsewhere, keys, alice.UserId)).KeyLoginComplete);

            // Challenges are counted per address too, failed or not.
            await using var busy = await server.ConnectRawAsync(remoteAddress: "203.0.113.3");
            await using var busier = await server.ConnectRawAsync(remoteAddress: "203.0.113.3");
            for (var i = 0; i < 4; i++) {
                Assert.NotNull((await (i < 2 ? busy : busier).SendAsync(new ClientFrame { StartKeyLogin = new StartKeyLogin { UserId = alice.UserId } })).KeyLoginChallenge);
            }

            await using var busiest = await server.ConnectRawAsync(remoteAddress: "203.0.113.3");
            Assert.Equal(ErrorCode.RateLimited, (await busiest.SendAsync(new ClientFrame { StartKeyLogin = new StartKeyLogin { UserId = alice.UserId } })).Error?.Code);
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
    }

    [Fact]
    public async Task ChallengesAreLimitedPerUser() {
        var alice = await this._server.RegisterAsync("Alice Targeted");
        var bob = await this._server.RegisterAsync("Bob Untargeted");
        var refused = false;
        // Many addresses (each within its own limits) asking about one user.
        for (var address = 1; address <= 8 && !refused; address++) {
            await using var raw = await this._server.ConnectRawAsync(remoteAddress: $"198.51.100.{address}");
            for (var i = 0; i < 3 && !refused; i++) {
                var response = await raw.SendAsync(new ClientFrame { StartKeyLogin = new StartKeyLogin { UserId = alice.UserId } });
                refused = response.Error?.Code == ErrorCode.RateLimited;
            }
        }

        Assert.True(refused);
        // Another user isn't affected.
        await using var other = await this._server.ConnectRawAsync(remoteAddress: "198.51.100.200");
        Assert.NotNull((await other.SendAsync(new ClientFrame { StartKeyLogin = new StartKeyLogin { UserId = bob.UserId } })).KeyLoginChallenge);
    }

    /// <summary>Before logging in, a connection can say hello, ping, register, log in, or sign in with its key, and nothing else.</summary>
    [Fact]
    public async Task BeforeLoggingInOnlyLoginRequestsAreAllowed() {
        ClientFrame.BodyOneofCase[] allowed = [
            ClientFrame.BodyOneofCase.Hello, ClientFrame.BodyOneofCase.Ping,
            ClientFrame.BodyOneofCase.StartRegistration, ClientFrame.BodyOneofCase.CompleteRegistration,
            ClientFrame.BodyOneofCase.Authenticate,
            ClientFrame.BodyOneofCase.StartKeyLogin, ClientFrame.BodyOneofCase.CompleteKeyLogin,
        ];

        await using var raw = await this._server.ConnectRawAsync();
        foreach (var field in ClientFrame.Descriptor.Oneofs.Single().Fields) {
            var frame = new ClientFrame();
            field.Accessor.SetValue(frame, (IMessage) Activator.CreateInstance(field.MessageType.ClrType)!);
            var response = await raw.SendAsync(frame);
            if (allowed.Contains(frame.BodyCase)) {
                continue;
            }

            Assert.True(response.Error?.Code == ErrorCode.NotAuthenticated, $"{frame.BodyCase} before logging in: {response}");
        }

        // Key login itself is open before logging in.
        var alice = await this._server.RegisterAsync("Alice Gate");
        await using var gate = await this._server.ConnectRawAsync();
        Assert.NotNull((await gate.SendAsync(new ClientFrame { StartKeyLogin = new StartKeyLogin { UserId = alice.UserId } })).KeyLoginChallenge);

        // And closed after: a logged-in connection has nothing to sign in to.
        var loggedIn = await gate.SendAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = alice.Store.Load().DeviceToken } });
        Assert.NotNull(loggedIn.AuthenticateOk);
        var after = await gate.SendAsync(new ClientFrame { StartKeyLogin = new StartKeyLogin { UserId = alice.UserId } });
        Assert.Equal(ErrorCode.InvalidRequest, after.Error?.Code);
    }

    // ================================================================ helpers

    private async Task<byte[]> ChallengeAsync(RawConnection raw, long userId) {
        var response = await raw.SendAsync(new ClientFrame { StartKeyLogin = new StartKeyLogin { UserId = userId } });
        Assert.True(response.KeyLoginChallenge != null, $"No challenge: {response}");
        return response.KeyLoginChallenge.Challenge.ToByteArray();
    }

    private Task<Response> CompleteAsync(RawConnection raw, byte[] challenge, string url, byte[] signature) {
        return raw.SendAsync(new ClientFrame {
            CompleteKeyLogin = new CompleteKeyLogin {
                Challenge = ByteString.CopyFrom(challenge),
                ServerUrl = url,
                Signature = ByteString.CopyFrom(signature),
            },
        });
    }

    /// <summary>Asks for a challenge and answers it as an honest client would, for <paramref name="url"/> (by default this server's address).</summary>
    private async Task<Response> KeyLoginAsync(RawConnection raw, IdentityKeys keys, long userId, string? url = null) {
        url ??= this._server.ServerUri.AbsoluteUri;
        var challenge = await this.ChallengeAsync(raw, userId);
        return await this.CompleteAsync(raw, challenge, url, KeyLoginProof.Sign(keys, challenge, userId, url));
    }

    /// <summary>The server loses a user's device tokens (as when restored from a backup made before they logged in on this device).</summary>
    private void DeleteDevices(long userId) {
        this._server.ExecuteSql("DELETE FROM devices WHERE user_id = $id;", ("$id", userId));
    }

    /// <returns>The key it had.</returns>
    private byte[] ReplaceSigningKey(long userId, byte[] key) {
        var old = this._server.Database.GetUser(userId)!.SigningKey;
        this._server.ExecuteSql("UPDATE users SET signing_key = $key WHERE user_id = $id;", ("$key", key), ("$id", userId));
        return old;
    }

    private static byte[] RandomKey() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
}
