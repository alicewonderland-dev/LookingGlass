using Google.Protobuf;
using LookingGlass.Core.Client;
using LookingGlass.Core.Crypto;
using LookingGlass.Protocol;
using LookingGlass.Server.Data;
using LookingGlass.Server.Hosting;
using Microsoft.Extensions.DependencyInjection;
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
        // A new login of its own isn't a copy of the old one used elsewhere either.
        Assert.DoesNotContain(back.Notices, notice => notice.Kind == NoticeKind.LoginUsedElsewhere);
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
        Assert.Equal("This computer: added just now, used just now", DeviceWords.Line(devices[0], now));
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

        await alice.Session.RefreshDevicesAsync(Ct);
        var added = (await DevicesShown(alice, 2)).Devices.Single(device => device.ThisDevice).Added;
        var oldToken = store.Load().DeviceToken!;
        Assert.Equal(1, await alice.Session.SignOutOtherDevicesAsync(Ct));
        Assert.Equal(ConnectionState.Ready, alice.Session.Snapshot.State);
        var kept = Assert.Single(alice.Session.Snapshot.Devices);
        Assert.True(kept.ThisDevice);
        Assert.Equal(added, kept.Added);
        Assert.Equal(1, this._server.Database.CountDevices(alice.UserId));
        Assert.Contains(alice.Notices, notice => notice.Text.Contains("signed out", StringComparison.OrdinalIgnoreCase));

        // This computer's login was replaced too, so a copy of it is no use any more; the new one is saved.
        var saved = store.Load();
        Assert.NotEqual(oldToken, saved.DeviceToken);
        Assert.Null(saved.PendingDeviceToken);
        await using var raw = await this._server.ConnectRawAsync();
        Assert.Equal(ErrorCode.NotAuthenticated, (await raw.SendAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = oldToken } })).Error?.Code);
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
        // It had seen the computer that did it: one of the player's own.
        Assert.Equal(SignedOutBy.YourOtherComputer, snapshot.SignedOutBy);
        Assert.StartsWith(DeviceWords.SignedOutStatus(SignedOutBy.YourOtherComputer).Plain, snapshot.StatusFor(advanced: false));
        Assert.StartsWith(DeviceWords.SignedOutStatus(SignedOutBy.YourOtherComputer).Technical, snapshot.StatusFor(advanced: true));
        Assert.Contains("The computer that did it was added on ", snapshot.StatusFor(advanced: false));
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

    // ================================================================ signing out: who may, and what it leaves

    /// <summary>
    /// A login alone (which a copy of the secrets file holds) can't sign the owner out: the request must be signed by the
    /// identity key, for this server, over a nonce the server gave this connection, once.
    /// </summary>
    [Fact]
    public async Task SigningOutNeedsTheIdentityKeyNotJustTheLogin() {
        var alice = await this._server.RegisterAsync("Alice Copied Login");
        var token = alice.Store.Load().DeviceToken!;
        using var keys = alice.LoadIdentity();
        var userId = alice.UserId;
        await alice.Session.DisposeAsync();

        await using var thief = await this.LogInRawAsync(token, offerDevices: true);
        // No nonce asked for (no list) yet.
        var noNonce = await thief.SendAsync(new ClientFrame {
            SignOutOtherDevices = new SignOutOtherDevices { ServerUrl = this._server.ServerUri.AbsoluteUri, NewDeviceToken = DeviceTokens.New() },
        });
        Assert.Equal(ErrorCode.InvalidRequest, noNonce.Error?.Code);
        // Signed with a key that isn't the account's.
        using var notTheirs = IdentityKeys.Generate();
        Assert.Equal(ErrorCode.Forbidden, (await this.SignOutRawAsync(thief, notTheirs, userId, token, DeviceTokens.New())).Error?.Code);
        // Signed for another server.
        Assert.Equal(ErrorCode.Forbidden, (await this.SignOutRawAsync(thief, keys, userId, token, DeviceTokens.New(), url: "wss://elsewhere.example/ws")).Error?.Code);
        // A new login that isn't one.
        Assert.Equal(ErrorCode.InvalidRequest, (await this.SignOutRawAsync(thief, keys, userId, token, "lgt_short")).Error?.Code);
        // A nonce is used once, whatever came of it.
        var nonce = (await thief.SendAsync(new ClientFrame { ListDevices = new ListDevices() })).Devices.SignOutNonce.ToByteArray();
        Assert.Equal(ErrorCode.Forbidden, (await this.SignOutRawAsync(thief, notTheirs, userId, token, DeviceTokens.New(), nonce: nonce)).Error?.Code);
        Assert.Equal(ErrorCode.InvalidRequest, (await this.SignOutRawAsync(thief, keys, userId, token, DeviceTokens.New(), nonce: nonce)).Error?.Code);

        Assert.Equal(1, this._server.Database.CountDevices(userId));
        Assert.False(this._server.Database.IsKeyLoginOff(userId));
        // With the key it works, and a list asked for meanwhile (in the background, say) doesn't spoil the nonce.
        var earlier = (await thief.SendAsync(new ClientFrame { ListDevices = new ListDevices() })).Devices.SignOutNonce.ToByteArray();
        Assert.NotNull((await thief.SendAsync(new ClientFrame { ListDevices = new ListDevices() })).Devices);
        Assert.NotNull((await this.SignOutRawAsync(thief, keys, userId, token, DeviceTokens.New(), nonce: earlier)).Devices);
        Assert.True(this._server.Database.IsKeyLoginOff(userId));
    }

    /// <summary>
    /// A computer whose login was still good when it logged in, while the sign-out was under way, is disconnected once the
    /// sign-out is done: it isn't left online with a login that is gone.
    /// </summary>
    [Fact]
    public async Task AComputerThatLogsInWhileSigningOutIsDisconnected() {
        var alice = await this._server.RegisterAsync("Alice Races");
        var token = alice.Store.Load().DeviceToken!;
        using var keys = alice.LoadIdentity();
        var userId = alice.UserId;
        await alice.Session.DisposeAsync();
        string otherToken;
        await using (var elsewhere = await this._server.ConnectRawAsync()) {
            otherToken = (await this.KeyLoginAsync(elsewhere, keys, userId)).KeyLoginComplete.DeviceToken;
        }

        await using var asker = await this.LogInRawAsync(token, offerDevices: true);
        RawConnection? other = null;
        Exception? failed = null;
        this._server.Handler.BeforeSignOutCommittedForTests = () => {
            try {
                other = this.LogInRawAsync(otherToken, offerDevices: true).GetAwaiter().GetResult();
            } catch (Exception ex) {
                failed = ex;
            }
        };
        var newToken = DeviceTokens.New();
        Response? answer = null;
        try {
            // The other computer's login replaced the asker's connection meanwhile, so its answer may not arrive.
            answer = await this.SignOutRawAsync(asker, keys, userId, token, newToken);
        } catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Net.WebSockets.WebSocketException) {
        }

        // The asker hears its connection close as soon as the other logs in, perhaps before that login is answered.
        await WaitFor(() => other ?? (object?) failed);
        Assert.Null(failed);
        Assert.True(answer == null || answer.Devices != null, $"The sign-out was refused: {answer}");
        await WaitFor(() => {
            try {
                other!.SendAsync(new ClientFrame { Ping = new Ping() }).GetAwaiter().GetResult();
                return null;
            } catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Net.WebSockets.WebSocketException) {
                return new object();
            }
        });
        Assert.Equal(1, this._server.Database.CountDevices(userId));
        await using var check = await this._server.ConnectRawAsync();
        Assert.Equal(ErrorCode.NotAuthenticated, (await check.SendAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = otherToken } })).Error?.Code);
        Assert.NotNull((await check.SendAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = newToken } })).AuthenticateOk);
    }

    /// <summary>
    /// The new login is saved before it is sent: when the answer is lost, the next login tries it first, and keeps it once it
    /// works, so this computer isn't shut out of its own account.
    /// </summary>
    [Fact]
    public async Task ALostAnswerStillLeavesThisComputerSignedIn() {
        string? sent = null;
        var drop = new[] { 1 };
        var options = this._server.Options(wrap: socket => new RewritingWebSocket(socket, frame => {
            if (frame.Response?.Devices is { SignedOut: > 0 } && Interlocked.Exchange(ref drop[0], 0) == 1) {
                throw new IOException("The answer was lost on the way.");
            }

            return frame;
        }, sent: frame => {
            if (frame.SignOutOtherDevices != null) {
                sent = frame.SignOutOtherDevices.NewDeviceToken;
            }
        }));
        var store = new InMemorySecretStore();
        var alice = await this._server.RegisterAsync("Alice Lost Answer", store, options);
        await using (var elsewhere = await this._server.ConnectRawAsync()) {
            using var keys = alice.LoadIdentity();
            Assert.NotNull((await this.KeyLoginAsync(elsewhere, keys, alice.UserId)).KeyLoginComplete);
        }

        var old = store.Load().DeviceToken!;
        await Assert.ThrowsAnyAsync<Exception>(() => alice.Session.SignOutOtherDevicesAsync(Ct));
        Assert.NotNull(sent);

        try {
            await WaitFor(() => alice.Session.GetTrace().Count(entry => entry.Outgoing && entry.Summary.EndsWith(" Hello")) >= 2
                                && alice.Session.Snapshot.State == ConnectionState.Ready ? new object() : null);
        } catch (TimeoutException) {
            Assert.Fail($"Not back: {alice.Session.Snapshot.State}, {alice.Session.Snapshot.StatusText}; " +
                        string.Join(" | ", alice.Session.GetTrace().Select(entry => $"{(entry.Outgoing ? ">" : "<")} {entry.Summary}")));
        }

        var saved = store.Load();
        Assert.Equal(sent, saved.DeviceToken);
        Assert.Null(saved.PendingDeviceToken);
        Assert.Equal(1, this._server.Database.CountDevices(alice.UserId));
        await using var raw = await this._server.ConnectRawAsync();
        Assert.Equal(ErrorCode.NotAuthenticated, (await raw.SendAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = old } })).Error?.Code);
    }

    /// <summary>A new login saved but never taken by the server (it refused, or never got it) is dropped once the old one works.</summary>
    [Fact]
    public async Task ANewLoginTheServerNeverTookIsDropped() {
        var store = new InMemorySecretStore();
        var alice = await this._server.RegisterAsync("Alice Pending", store);
        var old = store.Load().DeviceToken;
        await alice.Session.DisposeAsync();
        var secrets = store.Load();
        secrets.PendingDeviceToken = DeviceTokens.New();
        store.Save(secrets);

        var back = await this._server.RestartAsync(alice);
        await WaitFor(() => store.Load().PendingDeviceToken == null ? new object() : null);
        Assert.Equal(old, store.Load().DeviceToken);
        Assert.Equal(ConnectionState.Ready, back.Session.Snapshot.State);
        Assert.DoesNotContain(back.Notices, notice => notice.Level >= NoticeLevel.Warning);
    }

    /// <summary>
    /// The sign a copy of this computer's login leaves: the server says when the login was last used, and it isn't when this
    /// computer last logged in. Told gently, once; never after this computer's own logins, or a new login of its own.
    /// </summary>
    [Fact]
    public async Task ACopyOfThisComputersLoginUsedElsewhereIsNoticed() {
        var store = new InMemorySecretStore();
        var alice = await this._server.RegisterAsync("Alice Used Elsewhere", store);
        await DevicesShown(alice);
        await alice.Session.DisposeAsync();
        var again = await this._server.RestartAsync(alice);
        await DevicesShown(again);
        await again.Session.DisposeAsync();
        Assert.DoesNotContain(again.Notices, notice => notice.Kind == NoticeKind.LoginUsedElsewhere);

        // A copy of the login is used elsewhere, later.
        var token = store.Load().DeviceToken!;
        await using (var copy = await this.LogInRawAsync(token, offerDevices: true)) {
        }

        this._server.Database.SetDeviceLastUsedForTests(RetireIdentityProof.TokenHash(token), DateTimeOffset.UtcNow.AddMinutes(5));
        var back = await this._server.RestartAsync(again);
        var notice = await WaitFor(() => back.Notices.FirstOrDefault(n => n.Kind == NoticeKind.LoginUsedElsewhere));
        Assert.Equal(NoticeLevel.Warning, notice.Level);
        Assert.Contains("Sign out everywhere else", notice.TextFor(advanced: false));
        Assert.Contains("Reset my identity", notice.TextFor(advanced: false));
        PlainLanguage.AssertShownInBothModes(notice);
        await back.Session.DisposeAsync();

        var last = await this._server.RestartAsync(back);
        await DevicesShown(last);
        await Task.Delay(100, Ct);
        Assert.DoesNotContain(last.Notices, n => n.Kind == NoticeKind.LoginUsedElsewhere);
    }

    /// <summary>
    /// Signed out by a computer this one has never seen (or by a copy of this computer's own login): someone else may have its
    /// files, so it says to reset the identity, not just to register again (which keeps the key a thief may hold).
    /// </summary>
    [Fact]
    public async Task SignedOutByAComputerNeverSeenSaysToResetTheIdentity() {
        var store = new InMemorySecretStore();
        var alice = await this._server.RegisterAsync("Alice Robbed", store);
        await DevicesShown(alice);
        var token = store.Load().DeviceToken!;
        using var keys = alice.LoadIdentity();
        var userId = alice.UserId;
        await alice.Session.DisposeAsync();

        // Someone with a copy of the files signs in with the key from their computer, and signs out everywhere else.
        await using (var thief = await this._server.ConnectRawAsync()) {
            var stolen = (await this.KeyLoginAsync(thief, keys, userId)).KeyLoginComplete.DeviceToken;
            Assert.NotNull((await thief.SendAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = stolen } })).AuthenticateOk);
            Assert.NotNull((await this.SignOutRawAsync(thief, keys, userId, stolen, DeviceTokens.New())).Devices);
        }

        var back = this._server.StartClient(alice.Name, store);
        var snapshot = await WaitFor(() => back.Session.Snapshot is { SignedOutBy: SignedOutBy.UnknownComputer } s ? s : null);
        Assert.Equal(ConnectionState.LoginNotRecognized, snapshot.State);
        Assert.Contains("Reset my identity", snapshot.StatusFor(advanced: false));
        // It may be the player's own new computer: said when that one was added, for them to recognise.
        var thiefAdded = this._server.Database.GetDevices(userId).Single().AddedAt;
        var addedText = DateTimeOffset.FromUnixTimeSeconds(thiefAdded).ToLocalTime().ToString("g");
        Assert.Contains($"was added on {addedText}", snapshot.StatusFor(advanced: false));
        Assert.Contains($"was added on {addedText}", snapshot.StatusFor(advanced: true));
        Assert.Equal(token, store.Load().DeviceToken);
        await back.Session.DisposeAsync();

        // Registering again lets the key in again; then a copy of this computer's own login signs out everywhere else.
        var registeredStore = CopyOf(store, token: null);
        var registered = await this._server.RegisterAsync(alice.Name, registeredStore);
        await DevicesShown(registered);
        var own = registeredStore.Load().DeviceToken!;
        await registered.Session.DisposeAsync();
        await using (var copy = await this.LogInRawAsync(own, offerDevices: true)) {
            Assert.NotNull((await this.SignOutRawAsync(copy, keys, userId, own, DeviceTokens.New())).Devices);
        }

        var robbed = this._server.StartClient(alice.Name, registeredStore);
        await WaitFor(() => robbed.Session.Snapshot is { SignedOutBy: SignedOutBy.UnknownComputer } s ? s : null);
    }

    /// <summary>
    /// This computer signed out everywhere else, and lost its own login later (a server restored from a backup, say): it
    /// says so, not that another computer signed it out.
    /// </summary>
    [Fact]
    public async Task ThisComputerThatSignedOutTheOthersAndLostItsLoginIsntToldItWasAnother() {
        var store = new InMemorySecretStore();
        var alice = await this._server.RegisterAsync("Alice Lost Own", store);
        await DevicesShown(alice);
        Assert.Equal(0, await alice.Session.SignOutOtherDevicesAsync(Ct));
        await alice.Session.DisposeAsync();
        this._server.ExecuteSql("DELETE FROM devices WHERE user_id = $id;", ("$id", alice.UserId));

        var back = this._server.StartClient(alice.Name, store);
        var snapshot = await WaitFor(() => back.Session.Snapshot is { SignedOutBy: SignedOutBy.ThisComputer } s ? s : null);
        Assert.DoesNotContain("another computer", snapshot.StatusFor(advanced: false), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("another computer", snapshot.StatusFor(advanced: true), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Lodestone", snapshot.StatusFor(advanced: false));
    }

    /// <summary>Resetting the identity (new keys) after signing out everywhere else: the new keys sign in, the old ones never again.</summary>
    [Fact]
    public async Task ResettingTheIdentityAfterSigningOutWorks() {
        var store = new InMemorySecretStore();
        var alice = await this._server.RegisterAsync("Alice Resets", store);
        using var oldKeys = alice.LoadIdentity();
        var userId = alice.UserId;
        Assert.Equal(0, await alice.Session.SignOutOtherDevicesAsync(Ct));
        await alice.Session.DisposeAsync();

        var renewed = await this._server.RegisterAsync("Alice Resets", new InMemorySecretStore());
        Assert.NotEqual(alice.Keys(), renewed.Keys());
        Assert.False(this._server.Database.IsKeyLoginOff(userId));
        await using var raw = await this._server.ConnectRawAsync();
        var refused = (await this.KeyLoginAsync(raw, oldKeys, userId)).Error;
        Assert.Equal(ErrorCode.NotAuthenticated, refused?.Code);
        Assert.False(refused!.SignedOut);
        Assert.Equal(ConnectionState.Ready, renewed.Session.Snapshot.State);
    }

    /// <summary>
    /// "This computer did it" goes by when: a copy of this computer's login signing out everywhere else after this computer
    /// once did isn't taken for this computer's own sign-out.
    /// </summary>
    [Fact]
    public async Task AnEarlierSignOutOfThisComputersIsntTakenForALaterOne() {
        var store = new InMemorySecretStore();
        var alice = await this._server.RegisterAsync("Alice Twice Out", store);
        using var keys = alice.LoadIdentity();
        var userId = alice.UserId;
        Assert.Equal(0, await alice.Session.SignOutOtherDevicesAsync(Ct));
        await alice.Session.DisposeAsync();
        var mine = store.Load();
        Assert.NotNull(mine.SignedOutOthersAt);
        // This computer's sign-out was a while ago.
        mine.SignedOutOthersAt -= 100;
        store.Save(mine);

        // Later, a copy of this computer's login signs out everywhere else.
        await using (var copy = await this.LogInRawAsync(mine.DeviceToken!, offerDevices: true)) {
            Assert.NotNull((await this.SignOutRawAsync(copy, keys, userId, mine.DeviceToken!, DeviceTokens.New())).Devices);
        }

        var back = this._server.StartClient(alice.Name, store);
        await WaitFor(() => back.Session.Snapshot is { SignedOutBy: SignedOutBy.UnknownComputer } s ? s : null);
    }

    // ================================================================ signing out: saving first, and copies of the login

    /// <summary>
    /// The new login must be on disk before it is sent: if it can't be saved, nothing is sent (an answer lost then would leave
    /// this computer with no login the server knows), and the player is told plainly.
    /// </summary>
    [Fact]
    public async Task AFailedSaveSendsNoSignOut() {
        var store = new FailingSecretStore();
        var alice = await this._server.RegisterAsync("Alice Cant Save", store);
        await DevicesShown(alice);
        store.Fail = true;

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => alice.Session.SignOutOtherDevicesAsync(Ct));
        Assert.Contains("couldn't save", PlainMessages.MessageOf(refused, advanced: false));
        Assert.DoesNotContain(alice.Session.GetTrace(), entry => entry.Outgoing && entry.Summary.EndsWith(" SignOutOtherDevices"));
        Assert.False(this._server.Database.IsKeyLoginOff(alice.UserId));
        Assert.Equal(ConnectionState.Ready, alice.Session.Snapshot.State);

        // Once it can save again, it works.
        store.Fail = false;
        Assert.Equal(0, await alice.Session.SignOutOtherDevicesAsync(Ct));
        Assert.Null(store.Load().PendingDeviceToken);
    }

    /// <summary>A login the server refused (a ban, say) isn't a use of it: no "used elsewhere" once it is let in again.</summary>
    [Fact]
    public async Task ARefusedLoginIsntAUseOfIt() {
        var store = new InMemorySecretStore();
        var alice = await this._server.RegisterAsync("Alice Banned Once", store);
        await DevicesShown(alice);
        await alice.Session.DisposeAsync();
        var token = store.Load().DeviceToken!;
        this.LastUsedAnHourAgo(store, token);

        var bans = this._server.Factory.Services.GetRequiredService<LookingGlass.Server.Services.BanList>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        this._server.Database.AddBan(alice.UserId, null, "", null, false, now, out _);
        bans.Refresh();
        await using (var refused = await this._server.ConnectRawAsync()) {
            Assert.Equal(ErrorCode.Blocked, (await refused.SendAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = token } })).Error?.Code);
        }

        this._server.Database.LiftBans(alice.UserId, null, now);
        bans.Refresh();
        var back = await this._server.RestartAsync(alice);
        await DevicesShown(back);
        await Task.Delay(100, Ct);
        Assert.DoesNotContain(back.Notices, notice => notice.Kind == NoticeKind.LoginUsedElsewhere);
    }

    /// <summary>
    /// A login of this computer's whose answer was lost on the way is still its own: the next login isn't taken for a copy
    /// used elsewhere. One made elsewhere still is.
    /// </summary>
    [Fact]
    public async Task ALoginWhoseAnswerWasLostIsntTakenForACopy() {
        var store = new InMemorySecretStore();
        var alice = await this._server.RegisterAsync("Alice Lost Login", store);
        await DevicesShown(alice);
        await alice.Session.DisposeAsync();
        var token = store.Load().DeviceToken!;
        this.LastUsedAnHourAgo(store, token);

        var drop = new[] { 1 };
        var options = this._server.Options(wrap: socket => new RewritingWebSocket(socket, frame => {
            if (frame.Response?.AuthenticateOk != null && Interlocked.Exchange(ref drop[0], 0) == 1) {
                throw new IOException("The answer was lost on the way.");
            }

            return frame;
        }));
        var back = await this._server.RestartAsync(alice, options);
        await DevicesShown(back);
        Assert.True(back.Session.GetTrace().Count(entry => entry.Outgoing && entry.Summary.EndsWith(" Authenticate")) >= 2);
        await Task.Delay(100, Ct);
        Assert.DoesNotContain(back.Notices, notice => notice.Kind == NoticeKind.LoginUsedElsewhere);
        await back.Session.DisposeAsync();

        // A copy of the login used elsewhere (it sends no nonce of this computer's), later: told.
        this.LastUsedAnHourAgo(store, token);
        await using (var copy = await this.LogInRawAsync(token, offerDevices: true)) {
        }

        var again = await this._server.RestartAsync(back);
        await WaitFor(() => again.Notices.FirstOrDefault(notice => notice.Kind == NoticeKind.LoginUsedElsewhere));
    }

    /// <summary>
    /// This computer's login was last used an hour ago, as far as both the server and this computer know: so a use recorded
    /// now shows as one, whatever second it lands in.
    /// </summary>
    private void LastUsedAnHourAgo(InMemorySecretStore store, string token) {
        var hourAgo = DateTimeOffset.UtcNow.AddHours(-1);
        this._server.Database.SetDeviceLastUsedForTests(RetireIdentityProof.TokenHash(token), hourAgo);
        var secrets = store.Load();
        secrets.LastLoginUnix = hourAgo.ToUnixTimeSeconds();
        store.Save(secrets);
    }

    /// <summary>A store whose saves fail while <see cref="Fail"/> is set, as a full disk or a file held open would.</summary>
    private sealed class FailingSecretStore : ISecretStore {
        private readonly InMemorySecretStore _inner = new();

        public volatile bool Fail;

        public ClientSecrets Load() => this._inner.Load();

        public void Save(ClientSecrets secrets) {
            if (this.Fail) {
                throw new IOException("The disk is full.");
            }

            this._inner.Save(secrets);
        }
    }

    // ================================================================ being told: timing and many at once

    /// <summary>
    /// The usual case for a computer that is online: the other one logs in straight after its key login, which replaces this
    /// computer's connection. The notice is sent before the connection closes, not lost with it.
    /// </summary>
    [Fact]
    public async Task ANewComputerLoggingInAtOnceIsStillTold() {
        // Never connected again once replaced, so only what reached the replaced connection can tell it.
        var connects = new[] { 0 };
        var never = new TaskCompletionSource();
        var options = this._server.Options(beforeConnect: async ct => {
            if (Interlocked.Increment(ref connects[0]) > 1) {
                await never.Task.WaitAsync(ct);
            }
        });
        var alice = await this._server.RegisterAsync("Alice Replaced", options: options);
        await DevicesShown(alice);
        await using var elsewhere = await this._server.ConnectRawAsync();
        using var keys = alice.LoadIdentity();
        var token = (await this.KeyLoginAsync(elsewhere, keys, alice.UserId)).KeyLoginComplete.DeviceToken;
        Assert.NotNull((await elsewhere.SendAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = token } })).AuthenticateOk);

        await WaitFor(() => SignedInElsewhere(alice).FirstOrDefault());
    }

    /// <summary>
    /// What makes the above hold over a slow network: a connection replaced by a newer login sends what is queued for it (the
    /// new device's notice) before it closes, rather than dropping it as an abort does.
    /// </summary>
    [Fact]
    public async Task AReplacedConnectionSendsWhatIsQueuedBeforeClosing() {
        var socket = new SlowSendingWebSocket();
        var connection = new LookingGlass.Server.Realtime.ClientConnection(socket, "203.0.113.1", 1024, 8, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var running = connection.RunAsync((_, _, _) => Task.FromResult(new Response()));
        await socket.Receiving.WaitAsync(Harness.Timeout, Ct);

        connection.SendEvent(new Event { DeviceAdded = new DeviceAdded { AddedUnix = 1, Id = ByteString.CopyFrom([1, 2, 3, 4, 5, 6, 7, 8]) } });
        connection.CloseAfterQueued("Logged in from another connection");
        socket.Release();
        await running.WaitAsync(Harness.Timeout, Ct);

        Assert.Equal(["DeviceAdded", "close"], socket.Sent);
    }

    /// <summary>A client's socket on the server whose sends wait until released, and which answers a close at once.</summary>
    private sealed class SlowSendingWebSocket : System.Net.WebSockets.WebSocket {
        private readonly TaskCompletionSource _receiving = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private System.Net.WebSockets.WebSocketState _state = System.Net.WebSockets.WebSocketState.Open;

        public Task Receiving => this._receiving.Task;
        public List<string> Sent { get; } = [];
        public void Release() => this._release.TrySetResult();
        public override System.Net.WebSockets.WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override System.Net.WebSockets.WebSocketState State => this._state;
        public override string? SubProtocol => null;

        public override void Abort() => this._state = System.Net.WebSockets.WebSocketState.Aborted;

        public override Task CloseAsync(System.Net.WebSockets.WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            this.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

        public override Task CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) {
            lock (this.Sent) {
                this.Sent.Add("close");
            }

            this._state = System.Net.WebSockets.WebSocketState.CloseSent;
            this._closed.TrySetResult();
            return Task.CompletedTask;
        }

        public override void Dispose() {
        }

        public override async Task<System.Net.WebSockets.WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) {
            this._receiving.TrySetResult();
            await this._closed.Task.WaitAsync(cancellationToken);
            this._state = System.Net.WebSockets.WebSocketState.Closed;
            return new System.Net.WebSockets.WebSocketReceiveResult(0, System.Net.WebSockets.WebSocketMessageType.Close, true);
        }

        public override async Task SendAsync(ArraySegment<byte> buffer, System.Net.WebSockets.WebSocketMessageType messageType, bool endOfMessage,
            CancellationToken cancellationToken) {
            await this._release.Task.WaitAsync(cancellationToken);
            lock (this.Sent) {
                this.Sent.Add(ServerFrame.Parser.ParseFrom(buffer.AsSpan()).Event.KindCase.ToString());
            }
        }
    }

    /// <summary>Several new computers since the last login are told in one notice, not one each.</summary>
    [Fact]
    public async Task SeveralNewComputersAreToldInOneNotice() {
        var alice = await this._server.RegisterAsync("Alice Many");
        await DevicesShown(alice);
        await alice.Session.DisposeAsync();
        using (var keys = alice.LoadIdentity()) {
            for (var i = 0; i < 3; i++) {
                await using var elsewhere = await this._server.ConnectRawAsync();
                Assert.NotNull((await this.KeyLoginAsync(elsewhere, keys, alice.UserId)).KeyLoginComplete);
            }
        }

        var back = await this._server.RestartAsync(alice);
        var notice = await WaitFor(() => SignedInElsewhere(back).FirstOrDefault());
        Assert.StartsWith("Your LookingGlass character signed in from 3 other computers", notice.TextFor(advanced: false));
        await DevicesShown(back, 4);
        await Task.Delay(100, Ct);
        Assert.Single(SignedInElsewhere(back));
    }

    /// <summary>A list a server fills with new computers is still one notice.</summary>
    [Fact]
    public async Task AListFullOfNewComputersIsOneNotice() {
        var store = new InMemorySecretStore();
        var alice = await this._server.RegisterAsync("Alice Flooded", store);
        await DevicesShown(alice);
        await alice.Session.DisposeAsync();
        var options = this._server.Options(wrap: socket => new RewritingWebSocket(socket, frame => {
            if (frame.Response?.Devices is { } devices) {
                for (var i = 0; i < 30; i++) {
                    devices.List.Add(new Device {
                        AddedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), LastUsedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Id = ByteString.CopyFrom(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)),
                    });
                }
            }

            return frame;
        }));

        var back = await this._server.RestartAsync(alice, options);
        var notice = await WaitFor(() => SignedInElsewhere(back).FirstOrDefault());
        Assert.Contains("30 other computers", notice.TextFor(advanced: false));
        await Task.Delay(100, Ct);
        Assert.Single(SignedInElsewhere(back));
    }

    /// <summary>Notices of new computers one after another are held back a while, then told together.</summary>
    [Fact]
    public async Task NewComputersOneAfterAnotherAreToldTogether() {
        var alice = await this._server.RegisterAsync("Alice Burst", options: this._server.Options(deviceNoticeInterval: TimeSpan.FromMilliseconds(500)));
        await DevicesShown(alice);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var burst = Enumerable.Range(1, 10).Select(i => new Event {
            DeviceAdded = new DeviceAdded { AddedUnix = now, Id = ByteString.CopyFrom([(byte) i, 9, 9, 9, 9, 9, 9, 9]) },
        }).ToArray();

        await this._server.SendAndSettleAsync(alice, burst);
        Assert.Single(SignedInElsewhere(alice));
        var together = await WaitFor(() => SignedInElsewhere(alice) is { Count: 2 } both ? both[1] : null);
        Assert.Contains("9 other computers", together.TextFor(advanced: false));
    }

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

        // The second is held back a moment (see above), then told.
        var told = await WaitFor(() => SignedInElsewhere(alice) is { Count: 2 } both ? both : null);
        Assert.All(told, notice => Assert.Contains("recently", notice.TextFor(advanced: false)));
        Assert.Equal(ConnectionState.Ready, alice.Session.Snapshot.State);
        await alice.Session.RefreshDevicesAsync(Ct);
        await Task.Delay(300, Ct);
        Assert.Equal(2, SignedInElsewhere(alice).Count);
    }

    /// <summary>The devices seen are remembered up to a limit, those listed last first, so this computer's own is never forgotten.</summary>
    [Fact]
    public async Task TheDevicesSeenAreKeptWithinALimit() {
        var store = new InMemorySecretStore();
        var alice = await this._server.RegisterAsync("Alice Remembers", store);
        await DevicesShown(alice);
        await alice.Session.DisposeAsync();
        var secrets = store.Load();
        var mine = secrets.ThisDeviceId!;
        secrets.KnownDevices = [.. Enumerable.Range(0, 100).Select(i => $"{i:x16}")];
        store.Save(secrets);

        var back = await this._server.RestartAsync(alice);
        await DevicesShown(back);
        var known = await WaitFor(() => store.Load().KnownDevices is { } list && list[0] == mine ? list : null);
        Assert.Equal(ClientSession.MaxKnownDevices, known.Count);
        Assert.Equal("0000000000000000", known[1]);
        Assert.Empty(SignedInElsewhere(back));
    }

    /// <summary>Asking for the list in the background is spread out, so a client never runs into the server's limit by itself.</summary>
    [Fact]
    public async Task BackgroundListsAreSpreadOut() {
        var alice = await this._server.RegisterAsync("Alice Patient", options: this._server.Options(deviceListInterval: TimeSpan.FromHours(1)));
        await DevicesShown(alice);
        int Lists() => alice.Session.GetTrace().Count(entry => entry.Outgoing && entry.Summary.EndsWith(" ListDevices"));
        var before = Lists();

        for (var i = 0; i < 20; i++) {
            alice.Session.RefreshDevicesSoon();
        }

        await Task.Delay(300, Ct);
        Assert.True(Lists() - before <= 1, $"{Lists() - before} lists were asked for.");
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
        // Signing out: each try needs a nonce from a list, and is limited once it gets as far as checking a signature.
        using var notTheirs = IdentityKeys.Generate();
        var signOuts = new List<Response>();
        for (var i = 0; i < 8; i++) {
            var nonce = (await raw.SendAsync(new ClientFrame { ListDevices = new ListDevices() })).Devices.SignOutNonce.ToByteArray();
            var url = server.ServerUri.AbsoluteUri;
            var newToken = DeviceTokens.New();
            signOuts.Add(await raw.SendAsync(new ClientFrame {
                SignOutOtherDevices = new SignOutOtherDevices {
                    ServerUrl = url, NewDeviceToken = newToken, Nonce = ByteString.CopyFrom(nonce),
                    Signature = ByteString.CopyFrom(SignOutProof.Sign(notTheirs, alice.UserId, token, newToken, url, nonce)),
                },
            }));
        }

        Assert.Equal(ErrorCode.Forbidden, signOuts[0].Error?.Code);
        Assert.Equal(ErrorCode.RateLimited, signOuts[^1].Error?.Code);
        var lists = new List<Response>();
        for (var i = 0; i < 30; i++) {
            lists.Add(await raw.SendAsync(new ClientFrame { ListDevices = new ListDevices() }));
        }

        Assert.NotNull(lists[0].Devices);
        Assert.Equal(ErrorCode.RateLimited, lists[^1].Error?.Code);

        var flag = await WaitFor(() => server.Database.GetFlag($"user {alice.UserId}"));
        Assert.Contains(nameof(ClientFrame.BodyOneofCase.ListDevices), flag.Limits);
        Assert.Contains(nameof(ClientFrame.BodyOneofCase.SignOutOtherDevices), flag.Limits);
    }

    // ================================================================ the database, and the operator

    /// <summary>
    /// Signing out everywhere else gives the asker's device its new login (keeping its ID and when it was added), deletes the
    /// others, and turns key login off for the account, saying which device did (no device is added for a key then) until the
    /// account is registered again; it does nothing for a device that is gone already.
    /// </summary>
    [Fact]
    public void SigningOutKeepsOnlyTheAskersDeviceUntilTheAccountRegistersAgain() {
        var db = this._server.Database;
        using var keys = IdentityKeys.Generate();
        var user = db.RegisterUser(424242, "Db Person", 0, ProtocolInfo.DebugWorldName, keys.ToBundle(), true).User;
        byte[] mine = RandomHash(), theirs = RandomHash(), replaced = RandomHash();
        Assert.True(db.AddDeviceForKey(user.UserId, user.SigningKey, user.KeyVersion, mine));
        Assert.True(db.AddDeviceForKey(user.UserId, user.SigningKey, user.KeyVersion, theirs));
        var before = db.GetDevices(user.UserId).Single(device => device.TokenHash.SequenceEqual(mine));
        Assert.Equal(8, before.DeviceId.Length);
        Assert.False(db.IsKeyLoginOff(user.UserId));
        Assert.Null(db.SignedOutBy(user.UserId));

        Assert.Equal(1, db.SignOutOtherDevices(user.UserId, mine, replaced));
        var kept = Assert.Single(db.GetDevices(user.UserId));
        Assert.Equal(replaced, kept.TokenHash);
        Assert.Equal(before.DeviceId, kept.DeviceId);
        Assert.Equal(before.AddedAt, kept.AddedAt);
        Assert.Null(db.FindDevice(mine));
        Assert.True(db.IsKeyLoginOff(user.UserId));
        Assert.Equal(before.DeviceId, db.SignedOutBy(user.UserId));
        var signedOut = db.SignedOut(user.UserId)!;
        Assert.Equal(before.AddedAt, signedOut.DeviceAddedAt);
        Assert.InRange(signedOut.At, DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(), DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds());
        Assert.False(db.AddDeviceForKey(user.UserId, user.SigningKey, user.KeyVersion, RandomHash()));
        Assert.Null(db.SignOutOtherDevices(user.UserId, theirs, RandomHash()));

        var again = db.RegisterUser(user.UserId, "Db Person", 0, ProtocolInfo.DebugWorldName, keys.ToBundle(), true).User;
        Assert.False(db.IsKeyLoginOff(user.UserId));
        Assert.Null(db.SignedOutBy(user.UserId));
        Assert.True(db.AddDeviceForKey(again.UserId, again.SigningKey, again.KeyVersion, RandomHash()));
    }

    /// <summary>
    /// A database from before (schema 10) gains the switch in place, off for everyone, and an ID for every device; opening it
    /// again changes nothing more.
    /// </summary>
    [Fact]
    public void Schema10DatabaseGainsTheKeyLoginSwitch() {
        var directory = Path.Combine(Path.GetTempPath(), "lgt-devices-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            var path = Path.Combine(directory, "test.db");
            var db = new Database(path);
            using var keys = IdentityKeys.Generate();
            var user = db.RegisterUser(77, "Kept User", 0, ProtocolInfo.DebugWorldName, keys.ToBundle(), true).User;
            byte[] token = RandomHash(), other = RandomHash();
            Assert.True(db.AddDeviceForKey(77, user.SigningKey, user.KeyVersion, token));
            Assert.True(db.AddDeviceForKey(77, user.SigningKey, user.KeyVersion, other));
            Execute(path, "ALTER TABLE users DROP COLUMN key_login_off; ALTER TABLE users DROP COLUMN signed_out_by; " +
                          "ALTER TABLE users DROP COLUMN signed_out_by_added; ALTER TABLE users DROP COLUMN signed_out_at; " +
                          "ALTER TABLE devices DROP COLUMN device_id; ALTER TABLE devices DROP COLUMN last_login_nonce; " +
                          "DELETE FROM schema_version WHERE version >= 11;");
            Database.ReleasePooledConnections(path);

            var migrated = new Database(path);
            Assert.Equal((long) Database.SchemaVersion, Query(path, "SELECT MAX(version) FROM schema_version;"));
            Assert.False(migrated.IsKeyLoginOff(77));
            Assert.Equal(77, migrated.FindDevice(token));
            var ids = migrated.GetDevices(77).Select(device => Convert.ToHexString(device.DeviceId)).ToList();
            Assert.Equal(2, ids.Distinct().Count());
            Assert.All(ids, id => Assert.Equal(16, id.Length));
            Assert.Equal(1, migrated.SignOutOtherDevices(77, token, RandomHash()));
            Assert.True(migrated.IsKeyLoginOff(77));
            Database.ReleasePooledConnections(path);

            var again = new Database(path);
            Assert.Equal(1L, Query(path, "SELECT COUNT(*) FROM schema_version WHERE version = 11;"));
            Assert.True(again.IsKeyLoginOff(77));
            // A device an older server added meanwhile (going back to it for a while) gets an ID when listed.
            Execute(path, "UPDATE devices SET device_id = x'';");
            Assert.Equal(8, Assert.Single(again.GetDevices(77)).DeviceId.Length);
            Database.ReleasePooledConnections(path);
        } finally {
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// The operator's way back for a player locked out by "Sign out everywhere else" (who can't reach the Lodestone, say):
    /// <c>--allow-key-login</c> lets their key sign in again. Only for a character, and only where it was turned off.
    /// </summary>
    [Fact]
    public void TheOperatorCanLetAKeySignInAgain() {
        var directory = Path.Combine(Path.GetTempPath(), "lgt-devices-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            var path = Path.Combine(directory, "test.db");
            var db = new Database(path);
            using var keys = IdentityKeys.Generate();
            var user = db.RegisterUser(77, "Kept User", 0, ProtocolInfo.DebugWorldName, keys.ToBundle(), true).User;
            var token = RandomHash();
            Assert.True(db.AddDeviceForKey(77, user.SigningKey, user.KeyVersion, token));
            Assert.Equal(0, db.SignOutOtherDevices(77, token, RandomHash()));

            Assert.Throws<ArgumentException>(() => BanCommand.Parse(["--allow-key-login"]));
            Assert.Throws<ArgumentException>(() => BanCommand.Parse(["--allow-key-login", "77", "--days", "3"]));
            Assert.Throws<ArgumentException>(() => BanCommand.Parse(["--allow-key-login", "77", "--ban", "77"]));
            var request = BanCommand.Parse(["--allow-key-login", "77"])!;
            Assert.Equal(BanCommand.BanAction.AllowKeyLogin, request.Action);

            (int Code, string Output, string Errors) Run(BanCommand.Request what) {
                var output = new StringWriter();
                var errors = new StringWriter();
                var code = BanCommand.Run(what, path, new LookingGlass.Server.AbuseOptions(), output, errors);
                return (code, output.ToString(), errors.ToString());
            }

            var (code, text, _) = Run(request);
            Assert.Equal(0, code);
            Assert.Contains("Kept User", text);
            Assert.False(db.IsKeyLoginOff(77));
            Assert.Null(db.SignedOutBy(77));
            Assert.Equal(1, Run(BanCommand.Parse(["--allow-key-login", "Kept User@Debug"])!).Code);
            Assert.Equal(1, Run(BanCommand.Parse(["--allow-key-login", "203.0.113.5"])!).Code);
            Assert.Equal(1, Run(BanCommand.Parse(["--allow-key-login", "Nobody@Lich"])!).Code);
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
        // This computer's own last use too: what a copy of its login would change.
        Assert.Equal("This computer: added 3 h ago, used 5 min ago", DeviceWords.Line(new DeviceView(now.AddHours(-3), now.AddMinutes(-5), true), now));
    }

    [Fact]
    public void TheWordsSayWhatHappensAndAreShortWhereTheyMustBe() {
        // A label in Settings, with its "?", as the other settings'.
        Assert.Contains(DeviceWords.Label, SettingsWords.Labels());
        Assert.Contains(DeviceWords.SignOutButton, SettingsWords.Labels());
        Assert.Equal(DeviceWords.Explanation, SettingsWords.Help(SettingHelp.SignedInComputers, "x"));

        // Signing out also stops this computer's key signing in, should its own login be lost.
        foreach (var wording in new[] { DeviceWords.ConfirmText("Alice Liddell", "wss://chat.example.com/ws"), DeviceWords.SignedOutOthers(1) }) {
            Assert.Contains("this computer", wording.Plain, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("lost", wording.Plain);
            Assert.Contains("lost", wording.Technical);
            Assert.Contains("Lodestone", wording.Plain);
        }

        Assert.Contains("Reset my identity", DeviceWords.ConfirmText("Alice Liddell", "wss://chat.example.com/ws").Plain);
        Assert.StartsWith("Your LookingGlass character signed in from another computer on ",
            DeviceWords.SignedInElsewhere(1, new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero)).Plain);
        Assert.StartsWith("Your LookingGlass character signed in from 3 other computers, the last on ",
            DeviceWords.SignedInElsewhere(3, new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero)).Plain);
        Assert.Contains("Reset my identity", DeviceWords.SignedOutStatus(SignedOutBy.UnknownComputer).Plain);
        var added = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        Assert.EndsWith($"The computer that did it was added on {added.ToLocalTime():g}.", DeviceWords.SignedOutStatus(SignedOutBy.UnknownComputer, added).Plain);
        Assert.DoesNotContain("was added", DeviceWords.SignedOutStatus(SignedOutBy.ThisComputer, added).Plain);
        PlainLanguage.AssertPlain(DeviceWords.CouldntSaveNewLogin.Plain);
        Assert.DoesNotContain("another computer", DeviceWords.SignedOutStatus(SignedOutBy.ThisComputer).Plain, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sign out everywhere else", DeviceWords.LoginUsedElsewhere(null).Plain);
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

    /// <summary>
    /// Signs out everywhere else as a client would (asking for the list first, for its nonce, unless one is given), from a
    /// connection logged in with <paramref name="token"/>.
    /// </summary>
    private async Task<Response> SignOutRawAsync(RawConnection raw, IdentityKeys keys, long userId, string token, string newToken, string? url = null, byte[]? nonce = null) {
        url ??= this._server.ServerUri.AbsoluteUri;
        nonce ??= (await raw.SendAsync(new ClientFrame { ListDevices = new ListDevices() })).Devices.SignOutNonce.ToByteArray();
        return await raw.SendAsync(new ClientFrame {
            SignOutOtherDevices = new SignOutOtherDevices {
                ServerUrl = url,
                NewDeviceToken = newToken,
                Nonce = ByteString.CopyFrom(nonce),
                Signature = ByteString.CopyFrom(SignOutProof.Sign(keys, userId, token, newToken, url, nonce)),
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
