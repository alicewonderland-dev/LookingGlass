using Google.Protobuf;
using LookingGlass.Core.Client;
using LookingGlass.Core.Crypto;
using LookingGlass.Protocol;
using LookingGlass.Server.Realtime;
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

        // The key login itself didn't drop the session using the other device.
        await Task.Delay(200, Ct);
        Assert.Equal(ConnectionState.Ready, alice.Session.Snapshot.State);
        Assert.Single(alice.Session.GetTrace(), entry => entry.Outgoing && entry.Summary.EndsWith(" Hello"));

        // The new token logs in; the old one still does. (Each login replaces the user's previous connection, one per
        // user, so Alice's session is dropped here and reconnects with its own token, which still works.)
        var ok = await raw.SendAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = complete.DeviceToken } });
        Assert.Equal(alice.UserId, ok.AuthenticateOk?.User.UserId);
        await using var other = await this._server.ConnectRawAsync();
        Assert.NotNull((await other.SendAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = first } })).AuthenticateOk);
        await WaitFor(() => alice.Session.Snapshot.State == ConnectionState.Ready ? new object() : null);
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

    /// <summary>
    /// Outside Development the Host header proves nothing (a relay sets it to whatever the user signed for), so without
    /// configured PublicUrls the server has no address to check the signed ones against: a malicious server could pass on
    /// a registration made on it and get a login to the character's account here. Such a server refuses to start, and
    /// says what to set; it starts once they are set (see <see cref="OutsideDevelopmentKeyLoginWorksForThePublicUrls"/>).
    /// </summary>
    [Fact]
    public async Task OutsideDevelopmentTheServerNeedsPublicUrls() {
        var logs = new CapturingLoggerProvider();
        await ExitCodeGate.WaitAsync(Ct);
        var exitCode = Environment.ExitCode;
        var directory = Path.Combine(Path.GetTempPath(), "lgt-" + Guid.NewGuid().ToString("N"));
        try {
            Exception? failed = null;
            try {
                await using var server = new Harness(directory, environment: "Production", logs: logs);
                await using var raw = await server.ConnectRawAsync();
            } catch (Exception ex) {
                failed = ex;
            }

            // Nothing answers: the server stopped before it began listening...
            Assert.NotNull(failed);
            // ...and said why, and what to set.
            var critical = Assert.Single(logs.AtLeast(Microsoft.Extensions.Logging.LogLevel.Critical));
            Assert.Equal(RequestHandler.PublicUrlsRequired, critical);
            Assert.Contains("LookingGlass__PublicUrls__0=wss://", critical);
            Assert.Equal(1, Environment.ExitCode);
        } finally {
            // The server's exit code, which the test process doesn't share.
            Environment.ExitCode = exitCode;
            ExitCodeGate.Release();
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// The address checks (signed URLs, and the Lodestone code derived from the origin) only tell servers apart when each
    /// listed address is this server's alone. One that other servers can have too isn't: a short name (MagicDNS on
    /// another tailnet, a LAN name), a private, CGNAT, loopback or link-local IP, or plain ws:// (whoever answers at the
    /// name on the user's network). Those are named, with why; an address with TLS and a fully qualified name isn't.
    /// </summary>
    [Theory]
    [InlineData("wss://chat.example.com/ws")]
    [InlineData("wss://lookingglasschat.tail1234.ts.net/ws")]
    [InlineData("https://chat.example.com:8443/ws")]
    [InlineData("wss://203.0.113.7/ws")]
    [InlineData("wss://[2001:db8::7]/ws")]
    public void AnAddressUniqueToThisServerIsntWarnedAbout(string url) {
        Assert.Empty(RequestHandler.WhyNotUnique(ServerOrigin.FromUrl(url)!));
    }

    [Theory]
    [InlineData("ws://chat.example.com/ws", "ws://")]
    [InlineData("wss://lookingglasschat/ws", "single-label")]
    [InlineData("wss://localhost:5180/ws", "single-label")]
    [InlineData("wss://nas.local/ws", "local")]
    [InlineData("wss://chat.home.arpa/ws", "local")]
    [InlineData("wss://chat.lan/ws", "local")]
    [InlineData("wss://127.0.0.1/ws", "loopback")]
    [InlineData("wss://[::1]/ws", "loopback")]
    [InlineData("wss://10.1.2.3/ws", "private")]
    [InlineData("wss://172.20.0.5/ws", "private")]
    [InlineData("wss://192.168.1.10/ws", "private")]
    [InlineData("wss://[fd12:3456::1]/ws", "private")]
    [InlineData("wss://[::ffff:192.168.1.10]/ws", "private")]
    [InlineData("wss://100.64.0.1/ws", "CGNAT")]
    [InlineData("wss://100.127.255.254/ws", "CGNAT")]
    [InlineData("wss://169.254.1.1/ws", "link-local")]
    [InlineData("wss://[fe80::1]/ws", "link-local")]
    public void AnAddressOtherServersCanHaveIsWarnedAbout(string url, string why) {
        var reasons = RequestHandler.WhyNotUnique(ServerOrigin.FromUrl(url)!);
        Assert.Contains(reasons, reason => reason.Contains(why));
    }

    /// <summary>The server warns, when it starts, about each listed address that isn't unique to it, and says what to list instead.</summary>
    [Fact]
    public async Task TheServerWarnsAboutListedAddressesOtherServersCanHave() {
        var logs = new CapturingLoggerProvider();
        await using var server = new Harness(logs: logs, settings: [
            ("LookingGlass:PublicUrls:0", "wss://chat.example.com/ws"),
            ("LookingGlass:PublicUrls:1", "ws://lookingglasschat:5180/ws"),
            ("LookingGlass:PublicUrls:2", "wss://100.101.102.103/ws"),
        ]);
        try {
            await using var raw = await server.ConnectRawAsync();
            var warnings = logs.AtLeast(Microsoft.Extensions.Logging.LogLevel.Warning).Where(w => w.Contains("LookingGlass:PublicUrls")).ToList();
            Assert.Equal(2, warnings.Count);
            Assert.Contains(warnings, w => w.Contains("ws://lookingglasschat:5180/ws") && w.Contains("ws://") && w.Contains("single-label"));
            Assert.Contains(warnings, w => w.Contains("wss://100.101.102.103/ws") && w.Contains("CGNAT"));
            Assert.All(warnings, w => Assert.Contains("fully qualified", w));
            Assert.DoesNotContain(warnings, w => w.Contains("chat.example.com"));
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
    }

    /// <summary>Outside Development, with PublicUrls set, key login works for exactly those addresses.</summary>
    [Fact]
    public async Task OutsideDevelopmentKeyLoginWorksForThePublicUrls() {
        // The address the in-process clients connect to, as an operator would list theirs.
        await using var server = new Harness(environment: "Production", settings: ("LookingGlass:PublicUrls:0", "ws://localhost/ws"));
        try {
            var alice = await server.RegisterAsync("Alice Listed");
            await alice.Session.DisposeAsync();
            server.ExecuteSql("DELETE FROM devices WHERE user_id = $id;", ("$id", alice.UserId));
            var restarted = server.StartClient(alice.Name, alice.Store);
            var snapshot = await WaitFor(() => restarted.Session.Snapshot is { State: ConnectionState.Ready } s ? s : null);
            Assert.Equal(alice.UserId, snapshot.Me!.UserId);
            Assert.Contains(restarted.Session.GetTrace(), entry => entry.Outgoing && entry.Summary.EndsWith(" CompleteKeyLogin"));

            // A Host header naming another address doesn't make that address acceptable.
            using var keys = alice.LoadIdentity();
            await using var spoofed = await server.ConnectRawAsync(host: "evil.example");
            Assert.Equal(ErrorCode.NotAuthenticated, (await this.KeyLoginAsync(spoofed, keys, alice.UserId, "ws://evil.example/ws")).Error?.Code);
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

    /// <summary>
    /// Every answer that gets as far as the account checks one signature, whether or not the account exists (or may sign
    /// in at all), so how long a failure takes doesn't tell who is registered. Unknown and refused accounts are checked
    /// against a fixed key nobody holds.
    /// </summary>
    [Fact]
    public async Task EveryAnswerChecksOneSignatureWhetherOrNotTheAccountExists() {
        var directory = Path.Combine(Path.GetTempPath(), "lgt-" + Guid.NewGuid().ToString("N"));
        try {
            long debugUser;
            byte[] debugKey;
            await using (var enabled = new Harness(directory)) {
                var debug = await enabled.RegisterAsync("Debug Timing");
                debugUser = debug.UserId;
                using var keys = debug.LoadIdentity();
                debugKey = keys.SigningPublicKey;
                await debug.Session.DisposeAsync();
            }

            await using var server = new Harness(directory, allowDebugAccounts: false);
            var url = server.ServerUri.AbsoluteUri;
            using var someone = IdentityKeys.Generate();
            Assert.NotEqual(debugKey, someone.SigningPublicKey);

            foreach (var userId in new[] { 4242424242L, debugUser }) {
                await using var raw = await server.ConnectRawAsync();
                var before = server.Handler.KeyLoginSignatureChecks;
                var challenge = (await raw.SendAsync(new ClientFrame { StartKeyLogin = new StartKeyLogin { UserId = userId } })).KeyLoginChallenge!.Challenge.ToByteArray();
                var response = await this.CompleteAsync(raw, challenge, url, KeyLoginProof.Sign(someone, challenge, userId, url));
                Assert.Equal(ErrorCode.NotAuthenticated, response.Error?.Code);
                Assert.Equal(before + 1, server.Handler.KeyLoginSignatureChecks);
            }

            // As for an account that exists and may sign in (a verified character), with the wrong key...
            using var aliceKeys = IdentityKeys.Generate();
            var (alice, _) = server.Database.RegisterUser(31337, "Alice Timing", 21, "Gilgamesh", aliceKeys.ToBundle(), false);
            await using var known = await server.ConnectRawAsync();
            var checks = server.Handler.KeyLoginSignatureChecks;
            var aliceChallenge = await this.ChallengeAsync(known, alice.UserId);
            Assert.Equal(ErrorCode.NotAuthenticated, (await this.CompleteAsync(known, aliceChallenge, url, KeyLoginProof.Sign(someone, aliceChallenge, alice.UserId, url))).Error?.Code);
            Assert.Equal(checks + 1, server.Handler.KeyLoginSignatureChecks);

            // ...and with the right one.
            aliceChallenge = await this.ChallengeAsync(known, alice.UserId);
            Assert.NotNull((await this.CompleteAsync(known, aliceChallenge, url, KeyLoginProof.Sign(aliceKeys, aliceChallenge, alice.UserId, url))).KeyLoginComplete);
            Assert.Equal(checks + 2, server.Handler.KeyLoginSignatureChecks);
        } finally {
            DeleteDirectory(directory);
        }
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

            // Two failures from one address (for two accounts, so the per-account limit for the address isn't what stops
            // it), and it gets no more challenges, for any account...
            await using var first = await server.ConnectRawAsync(remoteAddress: "203.0.113.1");
            foreach (var userId in new[] { alice.UserId, 4242424242L }) {
                var challenge = await this.ChallengeAsync(first, userId);
                Assert.Equal(ErrorCode.NotAuthenticated, (await this.CompleteAsync(first, challenge, url, new byte[64])).Error?.Code);
            }

            await using var again = await server.ConnectRawAsync(remoteAddress: "203.0.113.1");
            Assert.Equal(ErrorCode.RateLimited, (await again.SendAsync(new ClientFrame { StartKeyLogin = new StartKeyLogin { UserId = alice.UserId } })).Error?.Code);

            // ...while another address can still sign in.
            await using var elsewhere = await server.ConnectRawAsync(remoteAddress: "203.0.113.2");
            Assert.NotNull((await this.KeyLoginAsync(elsewhere, keys, alice.UserId)).KeyLoginComplete);

            // Challenges are counted per address too, failed or not: here all four work (unanswered ones would count as failures).
            await using var busy = await server.ConnectRawAsync(remoteAddress: "203.0.113.3");
            await using var busier = await server.ConnectRawAsync(remoteAddress: "203.0.113.3");
            for (var i = 0; i < 4; i++) {
                Assert.NotNull((await this.KeyLoginAsync(i < 2 ? busy : busier, keys, alice.UserId, url)).KeyLoginComplete);
            }

            await using var busiest = await server.ConnectRawAsync(remoteAddress: "203.0.113.3");
            Assert.Equal(ErrorCode.RateLimited, (await busiest.SendAsync(new ClientFrame { StartKeyLogin = new StartKeyLogin { UserId = alice.UserId } })).Error?.Code);
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
    }

    /// <summary>
    /// Failed key logins for an account are counted per address too, and one address that keeps failing for an account
    /// is refused challenges for it, below its own limit: for that account only, from that address only. Successful
    /// ones don't count.
    /// </summary>
    [Fact]
    public async Task FailedKeyLoginsAreLimitedPerAccountAndAddress() {
        var alice = await this._server.RegisterAsync("Alice Targeted");
        var bob = await this._server.RegisterAsync("Bob Untargeted");
        using var keys = alice.LoadIdentity();
        var url = this._server.ServerUri.AbsoluteUri;

        // More successful key logins from one address than it may fail: none of them is held against the account.
        for (var i = 0; i < 12; i++) {
            await using var honest = await this._server.ConnectRawAsync(remoteAddress: "192.0.2.1");
            Assert.NotNull((await this.KeyLoginAsync(honest, keys, alice.UserId)).KeyLoginComplete);
        }

        // The default limits: 10 failures per address, and so 5 per account from one address.
        await using var attacker = await this._server.ConnectRawAsync(remoteAddress: "198.51.100.1");
        for (var i = 0; i < 5; i++) {
            await using var raw = await this._server.ConnectRawAsync(remoteAddress: "198.51.100.1");
            var challenge = await this.ChallengeAsync(raw, alice.UserId);
            Assert.Equal(ErrorCode.NotAuthenticated, (await this.CompleteAsync(raw, challenge, url, new byte[64])).Error?.Code);
        }

        var refused = (await attacker.SendAsync(new ClientFrame { StartKeyLogin = new StartKeyLogin { UserId = alice.UserId } })).Error;
        Assert.Equal(ErrorCode.RateLimited, refused?.Code);
        Assert.Contains("account", refused!.Message);

        // The address isn't out of key logins (Bob's still work from it), and Alice's still work from elsewhere.
        using var bobKeys = bob.LoadIdentity();
        Assert.NotNull((await this.KeyLoginAsync(attacker, bobKeys, bob.UserId)).KeyLoginComplete);
        await using var own = await this._server.ConnectRawAsync(remoteAddress: "192.0.2.1");
        Assert.NotNull((await this.KeyLoginAsync(own, keys, alice.UserId)).KeyLoginComplete);
    }

    /// <summary>
    /// Failures from other addresses never lock an account out of key login from its own: however many addresses fail
    /// for it (each up to its own limit), the owner's address still signs in. An Ed25519 signature can't be guessed, so
    /// there is nothing to protect by refusing everyone; each address that fails is limited on its own.
    /// </summary>
    [Fact]
    public async Task FailuresFromOtherAddressesDoNotLockTheAccountOut() {
        var alice = await this._server.RegisterAsync("Alice Besieged Widely");
        using var keys = alice.LoadIdentity();
        var url = this._server.ServerUri.AbsoluteUri;

        // Ten addresses fail for Alice until each is refused (by its own limits): far more than any one address may.
        var failures = 0;
        for (var address = 1; address <= 10; address++) {
            while (true) {
                await using var raw = await this._server.ConnectRawAsync(remoteAddress: $"198.51.100.{address}");
                var response = await raw.SendAsync(new ClientFrame { StartKeyLogin = new StartKeyLogin { UserId = alice.UserId } });
                if (response.Error?.Code == ErrorCode.RateLimited) {
                    break;
                }

                var challenge = response.KeyLoginChallenge.Challenge.ToByteArray();
                Assert.Equal(ErrorCode.NotAuthenticated, (await this.CompleteAsync(raw, challenge, url, new byte[64])).Error?.Code);
                failures++;
            }
        }

        Assert.True(failures >= 40, $"Only {failures} failures were allowed");

        // Alice, from her own address, still signs in with her key.
        await using var own = await this._server.ConnectRawAsync(remoteAddress: "203.0.113.7");
        Assert.NotNull((await this.KeyLoginAsync(own, keys, alice.UserId)).KeyLoginComplete);
    }

    /// <summary>
    /// Plugins whose logins a server no longer knows (it was reset, say) try key login on every connection: about three
    /// times an hour each, as the server closes connections that stay logged out. Several behind one address (a household,
    /// a shared NAT) used to use up its failures within the hour, and nobody there could sign in with their key. An account
    /// failing again from the same address counts once against the address.
    /// </summary>
    [Fact]
    public async Task PluginsThatKeepFailingDoNotLockTheirAddressOut() {
        var alice = await this._server.RegisterAsync("Alice Behind The Nat");
        using var keys = alice.LoadIdentity();
        var url = this._server.ServerUri.AbsoluteUri;

        // Four accounts the server doesn't know, three tries each: twelve failures, more than the address's ten.
        for (var attempt = 0; attempt < 3; attempt++) {
            foreach (var stale in new[] { 9_000_001L, 9_000_002L, 9_000_003L, 9_000_004L }) {
                await using var raw = await this._server.ConnectRawAsync(remoteAddress: "203.0.113.80");
                var challenge = await this.ChallengeAsync(raw, stale);
                Assert.Equal(ErrorCode.NotAuthenticated, (await this.CompleteAsync(raw, challenge, url, new byte[64])).Error?.Code);
            }
        }

        // Alice, at the same address, still signs in with her key.
        await using var own = await this._server.ConnectRawAsync(remoteAddress: "203.0.113.80");
        Assert.NotNull((await this.KeyLoginAsync(own, keys, alice.UserId)).KeyLoginComplete);

        // Ten different failing accounts still use up the address, as before.
        for (var stale = 0; stale < 6; stale++) {
            await using var raw = await this._server.ConnectRawAsync(remoteAddress: "203.0.113.80");
            var challenge = await this.ChallengeAsync(raw, 9_100_000L + stale);
            Assert.Equal(ErrorCode.NotAuthenticated, (await this.CompleteAsync(raw, challenge, url, new byte[64])).Error?.Code);
        }

        await using var refused = await this._server.ConnectRawAsync(remoteAddress: "203.0.113.80");
        Assert.Equal(ErrorCode.RateLimited, (await refused.SendAsync(new ClientFrame { StartKeyLogin = new StartKeyLogin { UserId = alice.UserId } })).Error?.Code);
    }

    /// <summary>
    /// Connections that don't log in are closed after a few minutes, and a plugin with a login the server doesn't know
    /// reconnects and asks for a challenge each time. One account asking again from the same address counts once against
    /// the address's challenges (up to the same number for that account), so such plugins don't use up their address.
    /// </summary>
    [Fact]
    public async Task OneAccountAskingAgainCountsOnceAgainstTheAddressesChallenges() {
        await using var server = new Harness(settings: ("LookingGlass:Limits:KeyLoginsPerHourPerIp", "3"));
        try {
            var users = new List<(TestClient Client, IdentityKeys Keys)>();
            foreach (var name in new[] { "Alice Asks Again", "Bob Asks Again", "Carol Asks Again", "Dave Asks Again" }) {
                var client = await server.RegisterAsync(name);
                users.Add((client, client.LoadIdentity()));
            }

            var url = server.ServerUri.AbsoluteUri;
            // Alice and Bob three times each: six challenges, two accounts.
            foreach (var (client, keys) in users.Take(2)) {
                for (var i = 0; i < 3; i++) {
                    await using var raw = await server.ConnectRawAsync(remoteAddress: "203.0.113.90");
                    var challenge = await this.ChallengeAsync(raw, client.UserId);
                    Assert.NotNull((await this.CompleteAsync(raw, challenge, url, KeyLoginProof.Sign(keys, challenge, client.UserId, url))).KeyLoginComplete);
                }
            }

            // Alice once more is past her own three.
            await using (var again = await server.ConnectRawAsync(remoteAddress: "203.0.113.90")) {
                Assert.Equal(ErrorCode.RateLimited, (await again.SendAsync(new ClientFrame { StartKeyLogin = new StartKeyLogin { UserId = users[0].Client.UserId } })).Error?.Code);
            }

            // A third account is the address's third; a fourth is one too many.
            await using (var third = await server.ConnectRawAsync(remoteAddress: "203.0.113.90")) {
                var challenge = await this.ChallengeAsync(third, users[2].Client.UserId);
                Assert.NotNull((await this.CompleteAsync(third, challenge, url, KeyLoginProof.Sign(users[2].Keys, challenge, users[2].Client.UserId, url))).KeyLoginComplete);
            }

            await using var fourth = await server.ConnectRawAsync(remoteAddress: "203.0.113.90");
            Assert.Equal(ErrorCode.RateLimited, (await fourth.SendAsync(new ClientFrame { StartKeyLogin = new StartKeyLogin { UserId = users[3].Client.UserId } })).Error?.Code);
            foreach (var (_, keys) in users) {
                keys.Dispose();
            }
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
    }

    /// <summary>
    /// An address that asks for challenges for someone else's account and never answers them can't use up that
    /// account's key logins: only failed answers count against an account.
    /// </summary>
    [Fact]
    public async Task UnansweredChallengesDoNotLockOutTheAccount() {
        await using var server = new Harness(settings: [
            ("LookingGlass:Limits:KeyLoginsPerHourPerIp", "1000"),
            ("LookingGlass:Limits:KeyLoginFailuresPerHourPerIp", "1000"),
        ]);
        try {
            var alice = await server.RegisterAsync("Alice Besieged");
            using var keys = alice.LoadIdentity();
            var url = server.ServerUri.AbsoluteUri;

            // One address asks for 60 challenges for Alice (more than the old per-account limit allowed in an hour), answering none.
            for (var connection = 0; connection < 20; connection++) {
                await using var attacker = await server.ConnectRawAsync(remoteAddress: "203.0.113.66");
                for (var i = 0; i < 3; i++) {
                    Assert.NotNull((await attacker.SendAsync(new ClientFrame { StartKeyLogin = new StartKeyLogin { UserId = alice.UserId } })).KeyLoginChallenge);
                }
            }

            // Alice, from her own address, still signs in with her key.
            await using var own = await server.ConnectRawAsync(remoteAddress: "203.0.113.7");
            var challenge = await this.ChallengeAsync(own, alice.UserId);
            Assert.NotNull((await this.CompleteAsync(own, challenge, url, KeyLoginProof.Sign(keys, challenge, alice.UserId, url))).KeyLoginComplete);
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
    }

    /// <summary>
    /// A challenge counts as a failure for its address until it is answered correctly, so an address that only asks
    /// for challenges is stopped as soon as one that fails is; one whose key logins work isn't.
    /// </summary>
    [Fact]
    public async Task UnansweredChallengesCountAsFailuresForTheAddress() {
        await using var server = new Harness(settings: [
            ("LookingGlass:Limits:KeyLoginsPerHourPerIp", "1000"),
            ("LookingGlass:Limits:KeyLoginFailuresPerHourPerIp", "3"),
        ]);
        try {
            var alice = await server.RegisterAsync("Alice Asked About");
            using var keys = alice.LoadIdentity();

            // Three unanswered challenges (one replaced on its connection, two left open as the connections close)...
            await using (var first = await server.ConnectRawAsync(remoteAddress: "203.0.113.20")) {
                for (var i = 0; i < 2; i++) {
                    Assert.NotNull((await first.SendAsync(new ClientFrame { StartKeyLogin = new StartKeyLogin { UserId = alice.UserId } })).KeyLoginChallenge);
                }
            }

            await using (var second = await server.ConnectRawAsync(remoteAddress: "203.0.113.20")) {
                Assert.NotNull((await second.SendAsync(new ClientFrame { StartKeyLogin = new StartKeyLogin { UserId = alice.UserId } })).KeyLoginChallenge);
            }

            // ...and the address gets no more.
            await using var third = await server.ConnectRawAsync(remoteAddress: "203.0.113.20");
            Assert.Equal(ErrorCode.RateLimited, (await third.SendAsync(new ClientFrame { StartKeyLogin = new StartKeyLogin { UserId = alice.UserId } })).Error?.Code);

            // An address whose key logins work keeps going well past that.
            for (var i = 0; i < 6; i++) {
                await using var honest = await server.ConnectRawAsync(remoteAddress: "203.0.113.21");
                Assert.NotNull((await this.KeyLoginAsync(honest, keys, alice.UserId, server.ServerUri.AbsoluteUri)).KeyLoginComplete);
            }
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
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

    // ================================================================ regression guards

    /// <summary>
    /// The race <see cref="ADeviceIsOnlyAddedForTheAccountsCurrentKey"/> checks in the database, end to end through the
    /// handler: the account registers again with new keys after the key login was checked and before its device is
    /// added. The registration wins: no device, no token, and the old key can't sign in afterwards. (Would fail if the
    /// handler added the device unconditionally, as AddDevice does: the device would outlive the revocation.)
    /// </summary>
    [Fact]
    public async Task RegisteringAgainDuringAKeyLoginWins() {
        var alice = await this._server.RegisterAsync("Alice Mid Login");
        using var oldKeys = alice.LoadIdentity();
        using var newKeys = IdentityKeys.Generate();
        var url = this._server.ServerUri.AbsoluteUri;
        await alice.Session.DisposeAsync();

        await using var raw = await this._server.ConnectRawAsync();
        var challenge = await this.ChallengeAsync(raw, alice.UserId);
        this._server.Handler.BeforeKeyLoginDeviceAddedForTests = () =>
            this._server.Database.RegisterUser(alice.UserId, alice.Name, 0, ProtocolInfo.DebugWorldName, newKeys.ToBundle(), true);
        var response = await this.CompleteAsync(raw, challenge, url, KeyLoginProof.Sign(oldKeys, challenge, alice.UserId, url));
        Assert.Null(this._server.Handler.BeforeKeyLoginDeviceAddedForTests);

        Assert.Equal(ErrorCode.NotAuthenticated, response.Error?.Code);
        Assert.Null(response.KeyLoginComplete);
        Assert.Equal(0, this._server.Database.CountDevices(alice.UserId));

        // The old key stays out; the new one is the account's.
        await using var again = await this._server.ConnectRawAsync();
        Assert.Equal(ErrorCode.NotAuthenticated, (await this.KeyLoginAsync(again, oldKeys, alice.UserId)).Error?.Code);
        Assert.NotNull((await this.KeyLoginAsync(again, newKeys, alice.UserId)).KeyLoginComplete);
    }

    /// <summary>
    /// A debug account on a server that has since disabled them can't sign in with its key either, even with the right
    /// signature, and is told exactly what an unknown account is.
    /// </summary>
    [Fact]
    public async Task ADebugAccountCannotKeyLoginWhenDebugAccountsAreDisabled() {
        var directory = Path.Combine(Path.GetTempPath(), "lgt-" + Guid.NewGuid().ToString("N"));
        try {
            TestClient debug;
            await using (var enabled = new Harness(directory)) {
                debug = await enabled.RegisterAsync("Debug Keyed");
                await debug.Session.DisposeAsync();
            }

            await using var server = new Harness(directory, allowDebugAccounts: false);
            var url = server.ServerUri.AbsoluteUri;
            using var keys = debug.LoadIdentity();
            await using var raw = await server.ConnectRawAsync();
            var challenge = await this.ChallengeAsync(raw, debug.UserId);
            var refused = await this.CompleteAsync(raw, challenge, url, KeyLoginProof.Sign(keys, challenge, debug.UserId, url));
            Assert.Equal(ErrorCode.NotAuthenticated, refused.Error?.Code);
            Assert.Null(refused.KeyLoginComplete);

            challenge = await this.ChallengeAsync(raw, 4242424242);
            var unknown = await this.CompleteAsync(raw, challenge, url, KeyLoginProof.Sign(keys, challenge, 4242424242, url));
            Assert.Equal(unknown.Error!.Message, refused.Error!.Message);
        } finally {
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// While the server doesn't recognise the login, the client tries the token again now and then, but signs in with its
    /// key only on connecting and when the user asks: the automatic retries never ask for a challenge.
    /// </summary>
    [Fact]
    public async Task AutomaticRetriesNeverSignInWithTheKey() {
        var store = new InMemorySecretStore();
        var alice = await this._server.RegisterAsync("Alice Retried", store);
        var userId = alice.UserId;
        await alice.Session.DisposeAsync();
        this.DeleteDevices(userId);
        this.ReplaceSigningKey(userId, RandomKey());

        var restarted = this._server.StartClient(alice.Name, store, this._server.Options(loginRetryDelay: TimeSpan.FromMilliseconds(30)));
        await WaitFor(() => restarted.Session.Snapshot.State == ConnectionState.LoginNotRecognized ? new object() : null);
        // Several automatic retries of the token...
        await WaitFor(() => restarted.Session.GetTrace().Count(entry => entry.Outgoing && entry.Summary.EndsWith(" Authenticate")) >= 5 ? new object() : null);

        // ...and still only the one key login made on connecting.
        var trace = restarted.Session.GetTrace();
        Assert.Single(trace, entry => entry.Outgoing && entry.Summary.EndsWith(" StartKeyLogin"));
        Assert.Single(trace, entry => entry.Outgoing && entry.Summary.EndsWith(" Hello"));
    }

    /// <summary>
    /// A challenge fetched before logging in can't be answered after: a logged-in connection has nothing to sign in to,
    /// and must not collect a device for whatever account the challenge was for.
    /// </summary>
    [Fact]
    public async Task AKeyLoginCannotBeCompletedAfterLoggingIn() {
        var alice = await this._server.RegisterAsync("Alice Late Answer");
        var bob = await this._server.RegisterAsync("Bob Late Answer");
        using var keys = alice.LoadIdentity();
        var url = this._server.ServerUri.AbsoluteUri;
        var devices = this._server.Database.CountDevices(alice.UserId);

        await using var raw = await this._server.ConnectRawAsync();
        var challenge = await this.ChallengeAsync(raw, alice.UserId);
        Assert.NotNull((await raw.SendAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = bob.Store.Load().DeviceToken } })).AuthenticateOk);

        var late = await this.CompleteAsync(raw, challenge, url, KeyLoginProof.Sign(keys, challenge, alice.UserId, url));
        Assert.Equal(ErrorCode.InvalidRequest, late.Error?.Code);
        Assert.Null(late.KeyLoginComplete);
        Assert.Equal(devices, this._server.Database.CountDevices(alice.UserId));
    }

    // ================================================================ the pieces

    /// <summary>
    /// The device is added only while the account still has the key that signed: registering again with new keys
    /// (which revokes every device) can land between the signature check and the insert, and must win.
    /// </summary>
    [Fact]
    public async Task ADeviceIsOnlyAddedForTheAccountsCurrentKey() {
        var alice = await this._server.RegisterAsync("Alice Raced");
        var db = this._server.Database;
        var checkedUser = db.GetUser(alice.UserId)!;

        using var newKeys = IdentityKeys.Generate();
        db.RegisterUser(alice.UserId, alice.Name, 0, ProtocolInfo.DebugWorldName, newKeys.ToBundle(), true);
        var token = RandomKey();
        Assert.False(db.AddDeviceForKey(alice.UserId, checkedUser.SigningKey, checkedUser.KeyVersion, token));
        Assert.Null(db.FindDevice(token));

        var current = db.GetUser(alice.UserId)!;
        Assert.True(db.AddDeviceForKey(alice.UserId, current.SigningKey, current.KeyVersion, token));
        Assert.Equal(alice.UserId, db.FindDevice(token));
        Assert.False(db.AddDeviceForKey(4242424242, current.SigningKey, current.KeyVersion, RandomKey()));
    }

    [Theory]
    [InlineData("ws://LookingGlassChat:5180/ws", false, "lookingglasschat", 5180)]
    [InlineData("wss://name.tail1234.ts.net/ws", true, "name.tail1234.ts.net", 443)]
    [InlineData("wss://name.tail1234.ts.net:443/other/path", true, "name.tail1234.ts.net", 443)]
    [InlineData("https://chat.example.com./ws", true, "chat.example.com", 443)]
    [InlineData("ws://127.0.0.1:5180/ws", false, "127.0.0.1", 5180)]
    [InlineData("ws://[::1]:5180/ws", false, "[::1]", 5180)]
    [InlineData("http://bücher.example/ws", false, "xn--bcher-kva.example", 80)]
    [InlineData("ws://someone@chat.example.com/ws", false, "chat.example.com", 80)]
    public void ServerAddressesAreComparedByOrigin(string url, bool secure, string host, int port) {
        Assert.Equal(new ServerOrigin(secure, host, port), ServerOrigin.FromUrl(url));
    }

    [Theory]
    [InlineData("ftp://chat.example.com/ws")]
    [InlineData("file:///C:/ws")]
    [InlineData("/ws")]
    [InlineData("chat.example.com")]
    [InlineData("")]
    [InlineData(null)]
    public void OnlyWebSocketAndHttpAddressesHaveAnOrigin(string? url) {
        Assert.Null(ServerOrigin.FromUrl(url));
    }

    [Fact]
    public void TheRequestOriginComesFromTheSchemeAndHostHeader() {
        Assert.Equal(new ServerOrigin(false, "lookingglasschat", 5180), ServerOrigin.FromRequest("http", "LookingGlassChat:5180"));
        Assert.Equal(new ServerOrigin(true, "name.tail1234.ts.net", 443), ServerOrigin.FromRequest("https", "name.tail1234.ts.net"));
        Assert.Equal(new ServerOrigin(false, "[::1]", 5180), ServerOrigin.FromRequest("http", "[::1]:5180"));
        Assert.Null(ServerOrigin.FromRequest("http", ""));
        Assert.Null(ServerOrigin.FromRequest("http", null));
        Assert.Null(ServerOrigin.FromRequest("http", "evil.example/@chat.example.com"));
        Assert.Null(ServerOrigin.FromRequest("http", "chat.example.com@evil.example"));
    }

    [Fact]
    public void PublicUrlsMustBeServerAddresses() {
        Assert.Equal([new ServerOrigin(true, "chat.example.com", 443)],
            RequestHandler.ParsePublicUrls(["wss://chat.example.com/ws", " https://CHAT.example.com:443/ ", ""]));
        Assert.Throws<InvalidOperationException>(() => RequestHandler.ParsePublicUrls(["chat.example.com"]));
    }

    [Fact]
    public void TheKeyLoginSignatureCoversTheChallengeUserAndAddress() {
        using var keys = IdentityKeys.Generate();
        var challenge = RandomKey();
        const string url = "wss://chat.example.com/ws";
        var signature = KeyLoginProof.Sign(keys, challenge, 1234, url);

        Assert.True(KeyLoginProof.Verify(keys.SigningPublicKey, challenge, 1234, url, signature));
        Assert.False(KeyLoginProof.Verify(keys.SigningPublicKey, RandomKey(), 1234, url, signature));
        Assert.False(KeyLoginProof.Verify(keys.SigningPublicKey, challenge, 1235, url, signature));
        Assert.False(KeyLoginProof.Verify(keys.SigningPublicKey, challenge, 1234, "wss://chat.example.org/ws", signature));
        Assert.False(KeyLoginProof.Verify(keys.SigningPublicKey, challenge[..16], 1234, url, keys.Sign(KeyLoginProof.Payload(challenge[..16], 1234, url))));
        using var other = IdentityKeys.Generate();
        Assert.False(KeyLoginProof.Verify(other.SigningPublicKey, challenge, 1234, url, signature));
        // Its own domain: the same fields signed for anything else don't count.
        Assert.False(KeyLoginProof.Verify(keys.SigningPublicKey, challenge, 1234, url,
            keys.Sign(new SigningPayload(Domains.Message).Add(challenge).Add(1234L).Add(url).ToArray())));
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
