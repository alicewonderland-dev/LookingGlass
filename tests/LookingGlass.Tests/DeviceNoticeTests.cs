using Google.Protobuf;
using LookingGlass.Core.Client;
using LookingGlass.Core.Crypto;
using LookingGlass.Protocol;
using LookingGlass.Server.Data;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// Other computers signing in (see "Other computers signing in" in docs/design.md): a player is told when their account gets
/// a new login (a key login, or registering again through the Lodestone) on another computer, at once if they are online and
/// otherwise at their next login, but never about their own computer or a new account's first; Settings lists the account's
/// computers; and "Sign out everywhere else" revokes every other login and stops the identity key signing in again until the
/// character is registered through the Lodestone. Older plugins and servers never see any of it.
/// </summary>
public sealed class DeviceNoticeTests : IAsyncLifetime {
    private Harness _server = null!;

    public ValueTask InitializeAsync() {
        this._server = new Harness();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() {
        await this._server.DisposeAsync();
    }

    private static IReadOnlyList<SessionNotice> SignedInElsewhere(TestClient client) =>
        client.Notices.Where(notice => notice.Kind == NoticeKind.SignedInElsewhere).ToList();

    /// <summary>Waits until the client has fetched its account's devices since logging in (and has <paramref name="count"/> of them).</summary>
    private static Task<SessionSnapshot> DevicesShown(TestClient client, int count = 1) =>
        WaitFor(() => client.Session.Snapshot is { State: ConnectionState.Ready, DevicesAvailable: true } snapshot && snapshot.Devices.Length == count ? snapshot : null);

    // ================================================================ being told

    /// <summary>
    /// Someone signs in with the player's identity key from another computer while the player is online: told at once, as a
    /// warning, in either mode's words, with what to do. Fetching the list again doesn't tell it twice.
    /// </summary>
    [Fact]
    public async Task AKeyLoginElsewhereIsToldAtOnceToTheComputerThatIsOnline() {
        var alice = await this._server.RegisterAsync("Alice Online");
        await DevicesShown(alice);

        await using var elsewhere = await this._server.ConnectRawAsync();
        using var keys = alice.LoadIdentity();
        Assert.NotNull((await this.KeyLoginAsync(elsewhere, keys, alice.UserId)).KeyLoginComplete);

        var notice = await WaitFor(() => SignedInElsewhere(alice).FirstOrDefault());
        Assert.Equal(NoticeLevel.Warning, notice.Level);
        Assert.Equal(NoticeTone.Warning, NoticeColours.ToneOf(notice.Level, notice.Kind));
        Assert.StartsWith("Your LookingGlass character signed in from another computer on ", notice.TextFor(advanced: false));
        Assert.Contains("If that wasn't you, use \"Sign out everywhere else\" in Settings", notice.TextFor(advanced: false));
        Assert.Contains("identity key", notice.TextFor(advanced: true));
        PlainLanguage.AssertShownInBothModes(notice);

        // The list now shows the other computer, which isn't told again.
        await alice.Session.RefreshDevicesAsync(Ct);
        var devices = (await DevicesShown(alice, 2)).Devices;
        Assert.Single(devices, device => device.ThisDevice);
        await Task.Delay(100, Ct);
        Assert.Single(SignedInElsewhere(alice));
        // Still the player's session: a key login alone logs nobody in (or out).
        Assert.Equal(ConnectionState.Ready, alice.Session.Snapshot.State);
    }

    /// <summary>A computer that wasn't online is told at its next login, once: the next login after that says nothing.</summary>
    [Fact]
    public async Task AComputerThatWasAwayIsToldAtItsNextLoginOnce() {
        var store = new InMemorySecretStore();
        var alice = await this._server.RegisterAsync("Alice Away", store);
        await DevicesShown(alice);
        await alice.Session.DisposeAsync();

        await using (var elsewhere = await this._server.ConnectRawAsync()) {
            using var keys = alice.LoadIdentity();
            Assert.NotNull((await this.KeyLoginAsync(elsewhere, keys, alice.UserId)).KeyLoginComplete);
        }

        var back = await this._server.RestartAsync(alice);
        var notice = await WaitFor(() => SignedInElsewhere(back).FirstOrDefault());
        var added = (await DevicesShown(back, 2)).Devices.Single(device => !device.ThisDevice).Added;
        Assert.Contains(added.ToLocalTime().ToString("g"), notice.TextFor(advanced: false));
        Assert.Single(SignedInElsewhere(back));

        await back.Session.DisposeAsync();
        var again = await this._server.RestartAsync(back);
        await DevicesShown(again, 2);
        await Task.Delay(100, Ct);
        Assert.Empty(SignedInElsewhere(again));
    }

    /// <summary>
    /// A new account's first computer isn't another computer; nor is a computer's own new login, after the server lost its
    /// old one and it signed in with its key.
    /// </summary>
    [Fact]
    public async Task NobodyIsWarnedAboutTheirOwnComputerOrANewAccountsFirst() {
        var alice = await this._server.RegisterAsync("Alice Alone");
        var device = Assert.Single((await DevicesShown(alice)).Devices);
        Assert.True(device.ThisDevice);
        await alice.Session.DisposeAsync();

        this._server.ExecuteSql("DELETE FROM devices WHERE user_id = $id;", ("$id", alice.UserId));
        var back = await this._server.RestartAsync(alice);
        Assert.Contains(back.Session.GetTrace(), entry => entry.Outgoing && entry.Summary.EndsWith(" CompleteKeyLogin"));
        Assert.True(Assert.Single((await DevicesShown(back)).Devices).ThisDevice);
        await Task.Delay(100, Ct);
        Assert.Empty(SignedInElsewhere(alice));
        Assert.Empty(SignedInElsewhere(back));
    }

    /// <summary>
    /// Registering again through the Lodestone on another computer (with the same identity, so the first one signs back in
    /// with its key) is a new login too: the first computer is told.
    /// </summary>
    [Fact]
    public async Task RegisteringAgainElsewhereIsToldToTheOtherComputer() {
        var store = new InMemorySecretStore();
        var alice = await this._server.RegisterAsync("Alice Registers Twice", store);
        await DevicesShown(alice);
        await alice.Session.DisposeAsync();

        // The other computer has the same identity but no login of its own, and registers.
        var other = await this._server.RegisterAsync("Alice Registers Twice", CopyOf(store, token: null));
        Assert.Single((await DevicesShown(other)).Devices);
        Assert.Empty(SignedInElsewhere(other));
        await other.Session.DisposeAsync();

        // The first computer's login was revoked by that; it signs in with its key, and hears of the registration.
        var back = await this._server.RestartAsync(alice);
        await WaitFor(() => SignedInElsewhere(back).FirstOrDefault());
        Assert.Equal(2, (await DevicesShown(back, 2)).Devices.Length);
    }

    /// <summary>The list in Settings: each computer, when it was added and last used, and which is this one.</summary>
    [Fact]
    public async Task TheListShowsWhenEachComputerWasAddedAndUsedAndWhichIsThisOne() {
        var alice = await this._server.RegisterAsync("Alice Lists");
        await using var elsewhere = await this._server.ConnectRawAsync();
        using var keys = alice.LoadIdentity();
        Assert.NotNull((await this.KeyLoginAsync(elsewhere, keys, alice.UserId)).KeyLoginComplete);
        await alice.Session.RefreshDevicesAsync(Ct);

        var devices = (await DevicesShown(alice, 2)).Devices;
        var now = DateTimeOffset.UtcNow;
        Assert.Single(devices, device => device.ThisDevice);
        Assert.All(devices, device => {
            Assert.InRange(device.Added, now.AddMinutes(-1), now.AddMinutes(1));
            Assert.True(device.LastUsed >= device.Added);
        });
        // Oldest first: this computer registered before the other signed in.
        Assert.True(devices[0].ThisDevice);
        Assert.Equal("This computer: added just now, in use now", DeviceWords.Line(devices[0], now));
        Assert.Equal("Another computer: added just now, used just now", DeviceWords.Line(devices[1], now));
    }

    // ================================================================ signing out everywhere else

    /// <summary>
    /// "Sign out everywhere else" revokes every other login of the account, and stops its identity key signing in, so a copy
    /// of it can't sign straight back in; this computer stays signed in, its login still works, and registering again
    /// through the Lodestone lets the key sign in again.
    /// </summary>
    [Fact]
    public async Task SigningOutEverywhereElseRevokesTheOthersAndStopsTheKeySigningIn() {
        var store = new InMemorySecretStore();
        var alice = await this._server.RegisterAsync("Alice Signs Out", store);
        using var keys = alice.LoadIdentity();
        string otherToken;
        await using (var elsewhere = await this._server.ConnectRawAsync()) {
            otherToken = (await this.KeyLoginAsync(elsewhere, keys, alice.UserId)).KeyLoginComplete.DeviceToken;
        }

        Assert.Equal(1, await alice.Session.SignOutOtherDevicesAsync(Ct));
        Assert.Equal(ConnectionState.Ready, alice.Session.Snapshot.State);
        Assert.True(Assert.Single(alice.Session.Snapshot.Devices).ThisDevice);
        Assert.Equal(1, this._server.Database.CountDevices(alice.UserId));
        Assert.Contains(alice.Notices, notice => notice.Text.Contains("signed out", StringComparison.OrdinalIgnoreCase));

        await using var raw = await this._server.ConnectRawAsync();
        Assert.Equal(ErrorCode.NotAuthenticated, (await raw.SendAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = otherToken } })).Error?.Code);
        var refused = (await this.KeyLoginAsync(raw, keys, alice.UserId)).Error;
        Assert.Equal(ErrorCode.NotAuthenticated, refused?.Code);
        Assert.True(refused!.SignedOut);
        Assert.Equal(1, this._server.Database.CountDevices(alice.UserId));

        // Someone who doesn't hold the key isn't told: a wrong signature is refused as ever.
        await using var stranger = await this._server.ConnectRawAsync();
        using var notTheirs = IdentityKeys.Generate();
        var wrong = (await this.KeyLoginAsync(stranger, notTheirs, alice.UserId)).Error;
        Assert.Equal(ErrorCode.NotAuthenticated, wrong?.Code);
        Assert.False(wrong!.SignedOut);

        // This computer's own login still works, after reconnecting too.
        alice.Session.Reconnect();
        await WaitFor(() => alice.Session.GetTrace().Count(entry => entry.Outgoing && entry.Summary.EndsWith(" Hello")) >= 2
                            && alice.Session.Snapshot.State == ConnectionState.Ready ? new object() : null);

        // Registering again through the Lodestone (here a debug account's, which needs no code) lets the key sign in again.
        await alice.Session.DisposeAsync();
        var registered = await this._server.RegisterAsync("Alice Signs Out", CopyOf(store, token: null));
        await registered.Session.DisposeAsync();
        await using var again = await this._server.ConnectRawAsync();
        Assert.NotNull((await this.KeyLoginAsync(again, keys, alice.UserId)).KeyLoginComplete);
    }

    /// <summary>
    /// The computer that was signed out says so (not just "login not recognised"), and what to do; trying again doesn't
    /// sign it back in. Its login is kept, as any refused one is.
    /// </summary>
    [Fact]
    public async Task AComputerThatWasSignedOutSaysSo() {
        var store = new InMemorySecretStore();
        var alice = await this._server.RegisterAsync("Alice Two Computers", store);
        await DevicesShown(alice);
        await alice.Session.DisposeAsync();

        // The other computer, with the same identity, signs in with the key (its login isn't one the server knows).
        var otherStore = CopyOf(store, token: "lgt_not-a-login-the-server-knows");
        var other = this._server.StartClient("Alice Two Computers", otherStore);
        await DevicesShown(other, 2);
        await other.Session.DisposeAsync();

        var back = await this._server.RestartAsync(alice);
        await WaitFor(() => SignedInElsewhere(back).FirstOrDefault());
        Assert.Equal(1, await back.Session.SignOutOtherDevicesAsync(Ct));
        await back.Session.DisposeAsync();

        var signedOutToken = otherStore.Load().DeviceToken;
        var signedOut = this._server.StartClient("Alice Two Computers", otherStore);
        var snapshot = await WaitFor(() => signedOut.Session.Snapshot is { State: ConnectionState.LoginNotRecognized, SignedOutElsewhere: true } s ? s : null);
        Assert.True(snapshot.LoginRejected);
        Assert.Equal(DeviceWords.SignedOutElsewhere.Plain, snapshot.StatusFor(advanced: false));
        Assert.Equal(DeviceWords.SignedOutElsewhere.Technical, snapshot.StatusFor(advanced: true));
        Assert.Equal(signedOutToken, otherStore.Load().DeviceToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => signedOut.Session.RetryLoginAsync(Ct));
        Assert.True(signedOut.Session.Snapshot.SignedOutElsewhere);
        Assert.Equal(1, this._server.Database.CountDevices(alice.UserId));
    }

    // ================================================================ older plugins and servers

    /// <summary>Only a connection that agreed to "devices.v1" is told about a new device: an older plugin never sees the event.</summary>
    [Fact]
    public async Task OnlyConnectionsThatAgreedAreToldOfANewDevice() {
        var alice = await this._server.RegisterAsync("Alice Raw");
        var token = alice.Store.Load().DeviceToken!;
        using var keys = alice.LoadIdentity();
        await alice.Session.DisposeAsync();

        // An older plugin's connection: it doesn't offer the capability.
        await using var old = await this.LogInRawAsync(token, offerDevices: false);
        await using (var elsewhere = await this._server.ConnectRawAsync()) {
            Assert.NotNull((await this.KeyLoginAsync(elsewhere, keys, alice.UserId)).KeyLoginComplete);
        }

        await old.SendAsync(new ClientFrame { Ping = new Ping() });
        Assert.DoesNotContain(old.Events, ev => ev.KindCase == Event.KindOneofCase.DeviceAdded);

        // A newer one is told, with when it was added and which device it is.
        await using var newer = await this.LogInRawAsync(token, offerDevices: true);
        await using (var elsewhere = await this._server.ConnectRawAsync()) {
            Assert.NotNull((await this.KeyLoginAsync(elsewhere, keys, alice.UserId)).KeyLoginComplete);
        }

        await newer.SendAsync(new ClientFrame { Ping = new Ping() });
        var added = Assert.Single(newer.Events, ev => ev.KindCase == Event.KindOneofCase.DeviceAdded).DeviceAdded;
        Assert.InRange(added.AddedUnix, DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(), DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds());
        Assert.Equal(8, added.Id.Length);
        var listed = (await newer.SendAsync(new ClientFrame { ListDevices = new ListDevices() })).Devices;
        Assert.Equal(3, listed.List.Count);
        Assert.Equal(added.Id, listed.List[^1].Id);
        Assert.True(Assert.Single(listed.List, device => device.ThisDevice).AddedUnix <= added.AddedUnix);
    }

    /// <summary>A plugin from before this (it doesn't offer the capability) works as it did: nothing asked, nothing told.</summary>
    [Fact]
    public async Task AnOlderPluginIsUnaffected() {
        var alice = await this._server.RegisterAsync("Alice Old Plugin", options: this._server.Options(offerDevices: false));
        await using var elsewhere = await this._server.ConnectRawAsync();
        using var keys = alice.LoadIdentity();
        Assert.NotNull((await this.KeyLoginAsync(elsewhere, keys, alice.UserId)).KeyLoginComplete);
        await alice.Session.PingAsync(Ct);
        await Task.Delay(100, Ct);

        Assert.False(alice.Session.Snapshot.DevicesAvailable);
        Assert.Empty(alice.Session.Snapshot.Devices);
        Assert.Empty(SignedInElsewhere(alice));
        Assert.DoesNotContain(alice.Session.GetTrace(), entry => entry.Summary.Contains("Device"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => alice.Session.SignOutOtherDevicesAsync(Ct));
    }

    /// <summary>A server from before this (it doesn't agree to the capability) is never asked.</summary>
    [Fact]
    public async Task AnOlderServerIsNeverAsked() {
        var options = this._server.Options(wrap: socket => new RewritingWebSocket(socket, frame => {
            if (frame.Response?.Welcome is { } welcome) {
                var agreed = welcome.Capabilities.Where(capability => capability != ProtocolInfo.Capabilities.Devices).ToList();
                welcome.Capabilities.Clear();
                welcome.Capabilities.AddRange(agreed);
            }

            return frame;
        }));
        var alice = await this._server.RegisterAsync("Alice Old Server", options: options);
        await alice.Session.RefreshAsync(Ct);

        Assert.False(alice.Session.Snapshot.DevicesAvailable);
        Assert.DoesNotContain(alice.Session.GetTrace(), entry => entry.Outgoing && entry.Summary.EndsWith(" ListDevices"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => alice.Session.RefreshDevicesAsync(Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => alice.Session.SignOutOtherDevicesAsync(Ct));
    }

    // ================================================================ a server that misbehaves

    /// <summary>
    /// What a server says about a new device is shown as the server's word, but nothing it sends breaks the session: a time
    /// no clock has is "recently", an ID that isn't one is ignored, and the same device is never told twice.
    /// </summary>
    [Fact]
    public async Task NothingAServerSaysAboutDevicesBreaksTheSession() {
        var alice = await this._server.RegisterAsync("Alice Odd Server");
        await DevicesShown(alice);
        static Event Added(long when, byte[] id) => new() { DeviceAdded = new DeviceAdded { AddedUnix = when, Id = ByteString.CopyFrom(id) } };

        await this._server.SendAndSettleAsync(alice,
            Added(long.MaxValue, [1, 1, 1, 1, 1, 1, 1, 1]),
            Added(-5, [2, 2, 2, 2, 2, 2, 2, 2]),
            Added(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), []),
            Added(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), new byte[1000]),
            Added(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), [1, 1, 1, 1, 1, 1, 1, 1]));

        var told = SignedInElsewhere(alice);
        Assert.Equal(2, told.Count);
        Assert.All(told, notice => Assert.Contains("recently", notice.TextFor(advanced: false)));
        Assert.Equal(ConnectionState.Ready, alice.Session.Snapshot.State);
        await alice.Session.RefreshDevicesAsync(Ct);
        Assert.Equal(2, SignedInElsewhere(alice).Count);
    }

    // ================================================================ limits

    /// <summary>The new requests have limits of their own, need a login, and a refusal by them counts towards flagging.</summary>
    [Fact]
    public async Task DeviceRequestsAreLimitedAndCountTowardsFlagging() {
        await using var server = new Harness(settings: [("LookingGlass:Abuse:FlagAfterLimits", "2")]);
        var alice = await server.RegisterAsync("Alice Limited");
        var token = alice.Store.Load().DeviceToken!;
        await alice.Session.DisposeAsync();

        await using var anonymous = await server.ConnectRawAsync();
        Assert.Equal(ErrorCode.NotAuthenticated, (await anonymous.SendAsync(new ClientFrame { ListDevices = new ListDevices() })).Error?.Code);
        Assert.Equal(ErrorCode.NotAuthenticated, (await anonymous.SendAsync(new ClientFrame { SignOutOtherDevices = new SignOutOtherDevices() })).Error?.Code);

        await using var raw = await server.ConnectRawAsync();
        Assert.NotNull((await raw.SendAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = token } })).AuthenticateOk);
        var lists = new List<Response>();
        for (var i = 0; i < 30; i++) {
            lists.Add(await raw.SendAsync(new ClientFrame { ListDevices = new ListDevices() }));
        }

        Assert.NotNull(lists[0].Devices);
        Assert.Equal(ErrorCode.RateLimited, lists[^1].Error?.Code);
        var signOuts = new List<Response>();
        for (var i = 0; i < 10; i++) {
            signOuts.Add(await raw.SendAsync(new ClientFrame { SignOutOtherDevices = new SignOutOtherDevices() }));
        }

        Assert.NotNull(signOuts[0].Devices);
        Assert.Equal(ErrorCode.RateLimited, signOuts[^1].Error?.Code);

        var flag = await WaitFor(() => server.Database.GetFlag($"user {alice.UserId}"));
        Assert.Contains(nameof(ClientFrame.BodyOneofCase.ListDevices), flag.Limits);
        Assert.Contains(nameof(ClientFrame.BodyOneofCase.SignOutOtherDevices), flag.Limits);
    }

    // ================================================================ the database

    /// <summary>
    /// Signing out everywhere else keeps only the asker's device, and turns key login off for the account (no device is added
    /// for a key then) until the account is registered again; it does nothing for a device that is gone already.
    /// </summary>
    [Fact]
    public void SigningOutKeepsOnlyTheAskersDeviceUntilTheAccountRegistersAgain() {
        var db = this._server.Database;
        using var keys = IdentityKeys.Generate();
        var user = db.RegisterUser(424242, "Db Person", 0, ProtocolInfo.DebugWorldName, keys.ToBundle(), true).User;
        byte[] mine = RandomHash(), theirs = RandomHash();
        Assert.True(db.AddDeviceForKey(user.UserId, user.SigningKey, user.KeyVersion, mine));
        Assert.True(db.AddDeviceForKey(user.UserId, user.SigningKey, user.KeyVersion, theirs));
        Assert.Equal(2, db.GetDevices(user.UserId).Count);
        Assert.False(db.IsKeyLoginOff(user.UserId));

        Assert.Equal(1, db.SignOutOtherDevices(user.UserId, mine));
        Assert.Equal(mine, Assert.Single(db.GetDevices(user.UserId)).TokenHash);
        Assert.True(db.IsKeyLoginOff(user.UserId));
        Assert.False(db.AddDeviceForKey(user.UserId, user.SigningKey, user.KeyVersion, RandomHash()));
        Assert.Null(db.SignOutOtherDevices(user.UserId, theirs));

        var again = db.RegisterUser(user.UserId, "Db Person", 0, ProtocolInfo.DebugWorldName, keys.ToBundle(), true).User;
        Assert.False(db.IsKeyLoginOff(user.UserId));
        Assert.True(db.AddDeviceForKey(again.UserId, again.SigningKey, again.KeyVersion, RandomHash()));
    }

    /// <summary>A database from before (schema 10) gains the switch in place, off for everyone, and opening it again changes nothing more.</summary>
    [Fact]
    public void Schema10DatabaseGainsTheKeyLoginSwitch() {
        var directory = Path.Combine(Path.GetTempPath(), "lgt-devices-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            var path = Path.Combine(directory, "test.db");
            var db = new Database(path);
            using var keys = IdentityKeys.Generate();
            var user = db.RegisterUser(77, "Kept User", 0, ProtocolInfo.DebugWorldName, keys.ToBundle(), true).User;
            var token = RandomHash();
            Assert.True(db.AddDeviceForKey(77, user.SigningKey, user.KeyVersion, token));
            Execute(path, "ALTER TABLE users DROP COLUMN key_login_off; DELETE FROM schema_version WHERE version >= 11;");
            Database.ReleasePooledConnections(path);

            var migrated = new Database(path);
            Assert.Equal((long) Database.SchemaVersion, Query(path, "SELECT MAX(version) FROM schema_version;"));
            Assert.False(migrated.IsKeyLoginOff(77));
            Assert.Equal(77, migrated.FindDevice(token));
            Assert.Equal(0, migrated.SignOutOtherDevices(77, token));
            Assert.True(migrated.IsKeyLoginOff(77));
            Database.ReleasePooledConnections(path);

            var again = new Database(path);
            Assert.Equal(1L, Query(path, "SELECT COUNT(*) FROM schema_version WHERE version = 11;"));
            Assert.True(again.IsKeyLoginOff(77));
            Database.ReleasePooledConnections(path);
        } finally {
            DeleteDirectory(directory);
        }
    }

    // ================================================================ words

    [Fact]
    public void TimesAreShownRelativeWhenRecentAndAsALocalDateOtherwise() {
        var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("just now", DeviceWords.Ago(now.AddSeconds(-20), now));
        Assert.Equal("just now", DeviceWords.Ago(now.AddSeconds(20), now));
        Assert.Equal("5 min ago", DeviceWords.Ago(now.AddMinutes(-5), now));
        Assert.Equal("3 h ago", DeviceWords.Ago(now.AddHours(-3), now));
        Assert.Equal(now.AddDays(-2).ToLocalTime().ToString("g"), DeviceWords.Ago(now.AddDays(-2), now));
        Assert.Equal("This computer: added 3 h ago, in use now", DeviceWords.Line(new DeviceView(now.AddHours(-3), now, true), now));

        // The label stays short; what it means is said in a sentence or two, for a "?" beside it, in either mode's words.
        Assert.True(DeviceWords.Label.Length <= 30);
        Assert.True(DeviceWords.SignOutButton.Length <= 30);
        PlainLanguage.AssertPlain(DeviceWords.Explanation.Plain);
        PlainLanguage.AssertPlain(DeviceWords.ConfirmText("Alice Liddell", "wss://chat.example.com/ws").Plain);
        Assert.Contains("Reset my identity", DeviceWords.ConfirmText("Alice Liddell", "wss://chat.example.com/ws").Plain);
    }

    // ================================================================ helpers

    /// <summary>The same identity in another store, as on another computer with a copy of the files, with its own login (or none).</summary>
    private static InMemorySecretStore CopyOf(ISecretStore store, string? token) {
        var secrets = store.Load();
        secrets.DeviceToken = token;
        secrets.KnownDevices = null;
        var copy = new InMemorySecretStore();
        copy.Save(secrets);
        return copy;
    }

    private async Task<RawConnection> LogInRawAsync(string token, bool offerDevices) {
        var raw = await this._server.ConnectRawAsync(hello: false);
        var hello = new Hello();
        hello.ProtocolVersions.Add(ProtocolInfo.CurrentVersion);
        hello.Capabilities.Add(ProtocolInfo.Capabilities.Chat);
        if (offerDevices) {
            hello.Capabilities.Add(ProtocolInfo.Capabilities.Devices);
        }

        var welcome = (await raw.SendAsync(new ClientFrame { Hello = hello })).Welcome;
        Assert.Equal(offerDevices, welcome.Capabilities.Contains(ProtocolInfo.Capabilities.Devices));
        Assert.NotNull((await raw.SendAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = token } })).AuthenticateOk);
        return raw;
    }

    /// <summary>Asks for a challenge and answers it as an honest client would.</summary>
    private async Task<Response> KeyLoginAsync(RawConnection raw, IdentityKeys keys, long userId) {
        var url = this._server.ServerUri.AbsoluteUri;
        var response = await raw.SendAsync(new ClientFrame { StartKeyLogin = new StartKeyLogin { UserId = userId } });
        var challenge = response.KeyLoginChallenge?.Challenge.ToByteArray() ?? throw new InvalidOperationException($"No challenge: {response}");
        return await raw.SendAsync(new ClientFrame {
            CompleteKeyLogin = new CompleteKeyLogin {
                Challenge = ByteString.CopyFrom(challenge),
                ServerUrl = url,
                Signature = ByteString.CopyFrom(KeyLoginProof.Sign(keys, challenge, userId, url)),
            },
        });
    }

    private static byte[] RandomHash() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    private static void Execute(string path, string sql) {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Query(string path, string sql) {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
