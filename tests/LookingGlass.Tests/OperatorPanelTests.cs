using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using LookingGlass.Core.Client;
using LookingGlass.Core.Crypto;
using LookingGlass.Protocol;
using LookingGlass.Server.Data;
using LookingGlass.Server.Realtime;
using LookingGlass.Server.Services;
using static LookingGlass.Tests.Harness;
using OperatorView = LookingGlass.Protocol.OperatorView;

namespace LookingGlass.Tests;

/// <summary>
/// The plugin's operator panel, over the protocol (capability "operator.v1"; see "The plugin's operator panel" in
/// docs/operator-tools-plan.md): only a key-pinned operator's own login is told it is one; only it reads the lists (addresses
/// shortened) and bans or lifts, on characters only, each signed by its current key, for this login, this server and a nonce
/// of this connection's, once; everyone else is refused in the same words, and counted towards flagging.
/// </summary>
public sealed class OperatorPanelTests {
    private const long Alice = 31337;
    private const string AliceName = "Alice Operator";
    private const string AliceWorld = "Gilgamesh";

    /// <summary>Listed as an operator too, but never registered: the plugin can't ban it.</summary>
    private const long OtherOperator = 55555;

    /// <summary>A server listing Alice (a real character, through a fake Lodestone) and <see cref="OtherOperator"/> as operators.</summary>
    private static Harness Server(FakeLodestone lodestone, FakeDiscord? discord = null, TimeProvider? time = null, CapturingLoggerProvider? logs = null, params (string Key, string Value)[] more) =>
        new(lodestone: lodestone, discord: discord, serverTime: time, logs: logs, settings: [
            ("LookingGlass:Operators:Characters:0", Alice.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("LookingGlass:Operators:Characters:1", OtherOperator.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("LookingGlass:PublicUrls:0", "ws://localhost/ws"), .. more,
        ]);

    private static FakeLodestone AliceOnTheLodestone() => new() { Name = AliceName, World = AliceWorld, CharacterId = Alice };

    /// <summary>Registers Alice through the (fake) Lodestone, with a new secrets store: a new computer, if she had one.</summary>
    private static async Task<TestClient> RegisterAliceAsync(Harness server, FakeLodestone lodestone, ClientSessionOptions? options = null) {
        var alice = server.StartClient(AliceName, new InMemorySecretStore(), options);
        await WaitFor(() => alice.Session.Snapshot.State == ConnectionState.Unregistered ? new object() : null);
        var challenge = await alice.Session.StartRegistrationAsync(new Character { Name = AliceName, WorldName = AliceWorld }, Ct);
        lodestone.Profile = challenge.Code;
        await alice.Session.CompleteRegistrationAsync(Ct);
        await WaitFor(() => alice.Session.Snapshot.State == ConnectionState.Ready ? new object() : null);
        return alice;
    }

    /// <summary>Pins Alice's current keys, as the operator does on the web page.</summary>
    private static void PinAlice(Harness server) {
        var actions = server.Factory.Services.GetRequiredService<OperatorActions>();
        Assert.True(actions.PinShownKeys(new OperatorActor(OperatorTool.WebPage, "alice@github"), Alice, server.Database.GetUser(Alice)!.Keys.Hash).Done);
    }

    /// <summary>Alice registered and pinned, logged in again so her login is told; her session, ready.</summary>
    private static async Task<TestClient> PinnedAliceAsync(Harness server, FakeLodestone lodestone) {
        var alice = await RegisterAliceAsync(server, lodestone);
        PinAlice(server);
        await alice.Session.DisposeAsync();
        alice = await server.RestartAsync(alice);
        await WaitFor(() => alice.Session.Snapshot.Operator == OperatorStanding.Operator ? new object() : null);
        return alice;
    }

    /// <summary>Alice's login and keys, taken from her plugin (which is stopped, so a raw connection can use the login alone).</summary>
    private static async Task<(string Token, IdentityKeys Keys)> TakeOverAsync(TestClient client) {
        await client.Session.DisposeAsync();
        return (client.Store.Load().DeviceToken!, client.LoadIdentity());
    }

    /// <summary>A raw connection logged in with <paramref name="token"/>, offering "operator.v1" (or not).</summary>
    private static async Task<(RawConnection Raw, Welcome Welcome, AuthenticateOk Ok)> LogInAsync(Harness server, string token, bool capability = true) {
        var raw = await server.ConnectRawAsync(hello: false);
        var hello = new Hello();
        hello.ProtocolVersions.Add(ProtocolInfo.CurrentVersion);
        if (capability) {
            hello.Capabilities.Add(ProtocolInfo.Capabilities.Operator);
        }

        var welcome = (await raw.SendAsync(new ClientFrame { Hello = hello })).Welcome;
        var ok = (await raw.SendAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = token } })).AuthenticateOk;
        Assert.NotNull(ok);
        return (raw, welcome, ok);
    }

    private static async Task<OperatorView> ViewAsync(RawConnection raw, long about = 0) {
        var response = await raw.SendAsync(new ClientFrame { GetOperatorView = new GetOperatorView { AboutUserId = about } });
        return response.OperatorView ?? throw new InvalidOperationException($"No view: {response.Error}");
    }

    private static ClientFrame BanFrame(IdentityKeys keys, string token, long target, uint days, string reason, ByteString nonce, string serverUrl, long me = Alice) =>
        new() {
            OperatorBan = new OperatorBan {
                UserId = target, Days = days, Reason = reason, ServerUrl = serverUrl, Nonce = nonce,
                Signature = ByteString.CopyFrom(OperatorActionProof.Sign(keys, OperatorActionProof.Actions.Ban, me, target, days, reason, token, serverUrl, nonce.Span)),
            },
        };

    private static ClientFrame LiftFrame(IdentityKeys keys, string token, long target, ByteString nonce, string serverUrl) => new() {
        OperatorUnban = new OperatorUnban {
            UserId = target, ServerUrl = serverUrl, Nonce = nonce,
            Signature = ByteString.CopyFrom(OperatorActionProof.Sign(keys, OperatorActionProof.Actions.Lift, Alice, target, 0, "", token, serverUrl, nonce.Span)),
        },
    };

    private static int Counted(Harness server) => server.Factory.Services.GetRequiredService<AbuseMonitor>().TrackedKeys;

    private static IReadOnlyList<BanRow> BansInForce(Harness server) => server.Database.GetActiveBans(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    // ================================================================ who is told

    /// <summary>A pinned operator's own login is told so, and its plugin reads the lists: empty, its 10 bans an hour, itself among the operators.</summary>
    [Fact]
    public async Task AnOperatorsOwnLoginIsToldAndItsPluginReadsTheLists() {
        var lodestone = AliceOnTheLodestone();
        await using var server = Server(lodestone);
        var alice = await PinnedAliceAsync(server, lodestone);

        Assert.True(alice.Session.IsOperator);
        Assert.Null(alice.Session.Snapshot.OperatorLists);
        var lists = await alice.Session.RefreshOperatorListsAsync(ct: Ct);
        Assert.Empty(lists.Flags);
        Assert.Empty(lists.InForce);
        Assert.Equal(0, lists.FlagsTotal);
        Assert.Equal(OperatorPanel.BansPerHour, lists.BansLeftThisHour);
        Assert.True(lists.IsOperatorCharacter(AliceName, AliceWorld));
        Assert.Null(lists.PageUrl);
        Assert.Same(lists, alice.Session.Snapshot.OperatorLists);
        Assert.DoesNotContain(alice.Notices, notice => notice.Kind == NoticeKind.NotOperatorNow);
    }

    /// <summary>
    /// Registering again with new keys (a new computer) drops it at once: the new login is told its keys changed, with the pinned
    /// fingerprint, said once in chat however often it logs in again, and every operator request is refused.
    /// </summary>
    [Fact]
    public async Task AListedCharacterWithOtherKeysIsToldOnceAndRefused() {
        var lodestone = AliceOnTheLodestone();
        await using var server = Server(lodestone);
        var alice = await PinnedAliceAsync(server, lodestone);
        var pinned = alice.Session.Snapshot.MyFingerprint;
        await alice.Session.DisposeAsync();

        var logins = 0;
        var newComputer = await RegisterAliceAsync(server, lodestone, server.Options(wrap: socket => new RewritingWebSocket(socket, frame => frame, sent => {
            if (sent.BodyCase == ClientFrame.BodyOneofCase.Authenticate) {
                Interlocked.Increment(ref logins);
            }
        })));
        var snapshot = await WaitFor(() => newComputer.Session.Snapshot is { Operator: OperatorStanding.KeysChanged } s ? s : null);
        Assert.Equal(pinned, snapshot.OperatorPinnedFingerprint);
        Assert.NotEqual(pinned, snapshot.MyFingerprint);
        var told = await WaitFor(() => newComputer.Notices.FirstOrDefault(notice => notice.Kind == NoticeKind.NotOperatorNow));
        Assert.Equal(NoticeLevel.Warning, told.Level);
        Assert.Contains(pinned!, told.Text);
        Assert.Contains(snapshot.MyFingerprint!, told.Text);
        Assert.DoesNotContain(pinned!, told.TextFor(advanced: false));
        Assert.Null(snapshot.OperatorLists);
        await Assert.ThrowsAsync<InvalidOperationException>(() => newComputer.Session.RefreshOperatorListsAsync(ct: Ct));

        // Once a session: a reconnect logs in again, and isn't told again.
        var before = Volatile.Read(ref logins);
        newComputer.Session.Reconnect();
        await WaitFor(() => Volatile.Read(ref logins) > before && newComputer.Session.Snapshot is { State: ConnectionState.Ready, Operator: OperatorStanding.KeysChanged }
            ? new object()
            : null);
        await newComputer.Session.RefreshAsync(Ct);
        Assert.Single(newComputer.Notices, notice => notice.Kind == NoticeKind.NotOperatorNow);

        // And the server refuses its requests, however the plugin asks.
        var (token, keys) = await TakeOverAsync(newComputer);
        var (raw, _, ok) = await LogInAsync(server, token);
        Assert.Equal(OperatorLoginStatus.KeysChanged, ok.OperatorStatus);
        Assert.Equal(server.Database.GetOperatorPin(Alice)!.KeyHash, ok.OperatorPinnedKeyHash.ToByteArray());
        var refused = (await raw.SendAsync(new ClientFrame { GetOperatorView = new GetOperatorView() })).Error;
        Assert.Equal((ErrorCode.Forbidden, RequestHandler.OperatorsOnly), (refused.Code, refused.Message));
        refused = (await raw.SendAsync(BanFrame(keys, token, 4242, 7, "", ByteString.CopyFrom(new byte[32]), server.ServerUri.AbsoluteUri))).Error;
        Assert.Equal((ErrorCode.Forbidden, RequestHandler.OperatorsOnly), (refused.Code, refused.Message));
        Assert.Empty(BansInForce(server));
    }

    /// <summary>A listed character whose keys were never pinned is told so (nothing is pinned by itself), and refused.</summary>
    [Fact]
    public async Task AListedCharacterNotPinnedYetIsToldAndRefused() {
        var lodestone = AliceOnTheLodestone();
        await using var server = Server(lodestone);
        var alice = await RegisterAliceAsync(server, lodestone);
        await alice.Session.DisposeAsync();
        alice = await server.RestartAsync(alice);

        var snapshot = await WaitFor(() => alice.Session.Snapshot is { Operator: OperatorStanding.NotPinned } s ? s : null);
        Assert.Null(snapshot.OperatorPinnedFingerprint);
        var told = await WaitFor(() => alice.Notices.FirstOrDefault(notice => notice.Kind == NoticeKind.NotOperatorNow));
        Assert.Contains(snapshot.MyFingerprint!, told.Text);
        Assert.Contains("hasn't approved", told.TextFor(advanced: false));
        Assert.Empty(server.Database.GetOperatorPins());

        var (token, _) = await TakeOverAsync(alice);
        var (raw, _, ok) = await LogInAsync(server, token);
        Assert.Equal(OperatorLoginStatus.NotPinned, ok.OperatorStatus);
        Assert.True(ok.OperatorPinnedKeyHash.IsEmpty);
        Assert.Equal(ErrorCode.Forbidden, (await raw.SendAsync(new ClientFrame { GetOperatorView = new GetOperatorView() })).Error.Code);
    }

    /// <summary>
    /// Only the character's own login, with the capability, is told: a character not listed is "not listed", and so is the
    /// operator itself on a connection that didn't offer "operator.v1" (an older plugin, which shows nothing).
    /// </summary>
    [Fact]
    public async Task OnlyTheCharactersOwnLoginWithTheCapabilityIsTold() {
        var lodestone = AliceOnTheLodestone();
        await using var server = Server(lodestone);
        var bob = await server.RegisterAsync("Bob Hatter");
        Assert.Equal(OperatorStanding.None, bob.Session.Snapshot.Operator);
        var alice = await PinnedAliceAsync(server, lodestone);
        await alice.Session.DisposeAsync();

        // An older plugin.
        var older = await server.RestartAsync(alice, server.Options(offerOperator: false));
        Assert.Equal(OperatorStanding.None, older.Session.Snapshot.Operator);
        Assert.False(older.Session.IsOperator);
        Assert.Null(OperatorPanel.MenuItem(older.Session.Snapshot, new InviteTarget("Bob Hatter", ProtocolInfo.DebugWorldName)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => older.Session.RefreshOperatorListsAsync(ct: Ct));

        var (token, _) = await TakeOverAsync(older);
        var (raw, welcome, ok) = await LogInAsync(server, token, capability: false);
        Assert.DoesNotContain(ProtocolInfo.Capabilities.Operator, welcome.Capabilities);
        Assert.Equal(OperatorLoginStatus.NotListed, ok.OperatorStatus);
        Assert.True(ok.OperatorPinnedKeyHash.IsEmpty);
        // Without the capability, the requests are unknown, as to an older server; not counted.
        var unknown = (await raw.SendAsync(new ClientFrame { GetOperatorView = new GetOperatorView() })).Error;
        Assert.Equal((ErrorCode.InvalidRequest, "Unknown request."), (unknown.Code, unknown.Message));

        var (bobToken, _) = await TakeOverAsync(bob);
        var (_, bobWelcome, bobOk) = await LogInAsync(server, bobToken);
        Assert.Contains(ProtocolInfo.Capabilities.Operator, bobWelcome.Capabilities);
        Assert.Equal(OperatorLoginStatus.NotListed, bobOk.OperatorStatus);
        Assert.True(bobOk.OperatorPinnedKeyHash.IsEmpty);
    }

    // ================================================================ what the server refuses

    /// <summary>Someone who isn't an operator is refused every operator request, in the same words, and each refusal counts towards flagging.</summary>
    [Fact]
    public async Task ANonOperatorIsRefusedAndCounted() {
        var lodestone = AliceOnTheLodestone();
        await using var server = Server(lodestone);
        var bob = await server.RegisterAsync("Bob Hatter");
        var carol = await server.RegisterAsync("Carol Queen");
        await Assert.ThrowsAsync<InvalidOperationException>(() => bob.Session.OperatorBanAsync(carol.UserId, 7, "", Ct));
        var (token, keys) = await TakeOverAsync(bob);
        var (raw, _, _) = await LogInAsync(server, token);
        var before = Counted(server);

        var url = server.ServerUri.AbsoluteUri;
        foreach (var frame in new[] {
                     new ClientFrame { GetOperatorView = new GetOperatorView { AboutUserId = carol.UserId } },
                     BanFrame(keys, token, carol.UserId, 7, "Spam", ByteString.CopyFrom(new byte[32]), url, bob.UserId),
                     new ClientFrame { OperatorUnban = new OperatorUnban { UserId = carol.UserId, ServerUrl = url } },
                 }) {
            var error = (await raw.SendAsync(frame)).Error;
            Assert.Equal((ErrorCode.Forbidden, RequestHandler.OperatorsOnly), (error.Code, error.Message));
        }

        Assert.True(Counted(server) > before, "A non-operator's operator requests weren't counted towards flagging.");
        Assert.Empty(BansInForce(server));
        // Nothing reached the operator actions, so nothing is in their audit trail.
        Assert.Empty(server.Database.GetOperatorLog(0, 10));
    }

    /// <summary>
    /// A ban must be signed by the account's current identity key, for this login: random keys, another login's signature, and
    /// (after the operator registered again and was pinned again) its old keys' are refused, and nothing is banned.
    /// </summary>
    [Fact]
    public async Task ABanMustBeSignedByTheCurrentKeyForThisLogin() {
        var lodestone = AliceOnTheLodestone();
        await using var server = Server(lodestone);
        var bob = await server.RegisterAsync("Bob Hatter");
        var alice = await PinnedAliceAsync(server, lodestone);
        var (token, oldKeys) = await TakeOverAsync(alice);
        var url = server.ServerUri.AbsoluteUri;
        var (raw, _, _) = await LogInAsync(server, token);

        using var stranger = IdentityKeys.Generate();
        var view = await ViewAsync(raw);
        var error = (await raw.SendAsync(BanFrame(stranger, token, bob.UserId, 7, "", view.Nonce, url))).Error;
        Assert.Equal(ErrorCode.Forbidden, error.Code);
        Assert.Contains("isn't signed with this character's identity key", error.Message);

        // Signed for another login of the account (its token's hash is what is signed).
        error = (await raw.SendAsync(BanFrame(oldKeys, DeviceTokens.New(), bob.UserId, 7, "", view.Nonce, url))).Error;
        Assert.Equal(ErrorCode.Forbidden, error.Code);
        // The days and the reason are signed: changed on the way, refused.
        var tampered = BanFrame(oldKeys, token, bob.UserId, 7, "Spam", view.Nonce, url);
        tampered.OperatorBan.Days = 36500;
        Assert.Equal(ErrorCode.Forbidden, (await raw.SendAsync(tampered)).Error.Code);
        tampered = BanFrame(oldKeys, token, bob.UserId, 7, "Spam", view.Nonce, url);
        tampered.OperatorBan.Reason = "Something else";
        Assert.Equal(ErrorCode.Forbidden, (await raw.SendAsync(tampered)).Error.Code);
        Assert.Empty(BansInForce(server));

        // A new computer, pinned again: the old keys can't sign any more.
        await raw.DisposeAsync();
        var newComputer = await RegisterAliceAsync(server, lodestone);
        PinAlice(server);
        var (newToken, newKeys) = await TakeOverAsync(newComputer);
        (raw, _, _) = await LogInAsync(server, newToken);
        view = await ViewAsync(raw);
        error = (await raw.SendAsync(BanFrame(oldKeys, newToken, bob.UserId, 7, "", view.Nonce, url))).Error;
        Assert.Equal(ErrorCode.Forbidden, error.Code);
        Assert.Empty(BansInForce(server));

        // The same nonce, signed right, works: a bad signature didn't use it up.
        Assert.NotNull((await raw.SendAsync(BanFrame(newKeys, newToken, bob.UserId, 7, "", view.Nonce, url))).OperatorView);
        Assert.Contains(BansInForce(server), ban => ban.UserId == bob.UserId);
        oldKeys.Dispose();
        newKeys.Dispose();
    }

    /// <summary>A nonce is good once, on the connection it was given on, for a few minutes.</summary>
    [Fact]
    public async Task ANonceIsGoodOnceOnItsOwnConnectionForAFewMinutes() {
        var lodestone = AliceOnTheLodestone();
        var clock = new ManualClock();
        await using var server = Server(lodestone, time: clock);
        var bob = await server.RegisterAsync("Bob Hatter");
        var carol = await server.RegisterAsync("Carol Queen");
        var (token, keys) = await TakeOverAsync(await PinnedAliceAsync(server, lodestone));
        var url = server.ServerUri.AbsoluteUri;
        var (first, _, _) = await LogInAsync(server, token);

        var view = await ViewAsync(first);
        Assert.Equal(OperatorActionProof.NonceSize, view.Nonce.Length);
        var banned = (await first.SendAsync(BanFrame(keys, token, bob.UserId, 1, "", view.Nonce, url))).OperatorView;
        Assert.NotNull(banned);
        // Used: not again.
        var error = (await first.SendAsync(LiftFrame(keys, token, bob.UserId, view.Nonce, url))).Error;
        Assert.Equal((ErrorCode.InvalidRequest, "Refresh the operator lists, then try again."), (error.Code, error.Message));

        // Another connection's nonce (the login logs in again elsewhere) is no use here.
        var (second, _, _) = await LogInAsync(server, token);
        error = (await second.SendAsync(LiftFrame(keys, token, bob.UserId, banned.Nonce, url))).Error;
        Assert.Equal(ErrorCode.InvalidRequest, error.Code);

        // An answer's own nonce works for the next action; one older than a few minutes doesn't.
        view = await ViewAsync(second);
        clock.Offset = RequestHandler.OperatorNonceLifetime + TimeSpan.FromSeconds(1);
        Assert.Equal(ErrorCode.InvalidRequest, (await second.SendAsync(BanFrame(keys, token, carol.UserId, 1, "", view.Nonce, url))).Error.Code);
        view = await ViewAsync(second);
        var lifted = (await second.SendAsync(LiftFrame(keys, token, bob.UserId, view.Nonce, url))).OperatorView;
        Assert.NotNull(lifted);
        Assert.NotNull((await second.SendAsync(BanFrame(keys, token, carol.UserId, 1, "", lifted.Nonce, url))).OperatorView);
        Assert.DoesNotContain(server.Database.GetActiveBans(clock.GetUtcNow().ToUnixTimeSeconds()), ban => ban.UserId == bob.UserId);
        keys.Dispose();
    }

    /// <summary>A ban signed for another server's address (as a relaying server would bring it) is refused before its signature is looked at.</summary>
    [Fact]
    public async Task ABanSignedForAnotherServerIsRefused() {
        var lodestone = AliceOnTheLodestone();
        await using var server = Server(lodestone);
        var bob = await server.RegisterAsync("Bob Hatter");
        var (token, keys) = await TakeOverAsync(await PinnedAliceAsync(server, lodestone));
        var (raw, _, _) = await LogInAsync(server, token);
        var view = await ViewAsync(raw);

        var error = (await raw.SendAsync(BanFrame(keys, token, bob.UserId, 7, "", view.Nonce, "wss://elsewhere.example.com/ws"))).Error;
        Assert.Equal(ErrorCode.Forbidden, error.Code);
        Assert.Contains("doesn't accept the address wss://elsewhere.example.com/ws", error.Message);
        // Signed for the other server, sent naming this one: the signature doesn't hold.
        var relayed = BanFrame(keys, token, bob.UserId, 7, "", view.Nonce, "wss://elsewhere.example.com/ws");
        relayed.OperatorBan.ServerUrl = server.ServerUri.AbsoluteUri;
        Assert.Contains("isn't signed", (await raw.SendAsync(relayed)).Error.Message);
        Assert.Empty(BansInForce(server));
        keys.Dispose();
    }

    /// <summary>
    /// The plugin bans characters only: it can't name an address at all, user 0 names nobody, the layer refuses an address from
    /// it anyway, and it can't ban a character listed as an operator, itself included. Refusals are in the audit trail, as
    /// made in the plugin by the operator character.
    /// </summary>
    [Fact]
    public async Task ThePluginBansCharactersOnlyAndNeverAnOperator() {
        var lodestone = AliceOnTheLodestone();
        await using var server = Server(lodestone);
        var (token, keys) = await TakeOverAsync(await PinnedAliceAsync(server, lodestone));
        var url = server.ServerUri.AbsoluteUri;
        var (raw, _, _) = await LogInAsync(server, token);

        var view = await ViewAsync(raw);
        Assert.Equal(ErrorCode.InvalidRequest, (await raw.SendAsync(BanFrame(keys, token, 0, 7, "", view.Nonce, url))).Error.Code);
        foreach (var target in new[] { OtherOperator, Alice }) {
            view = await ViewAsync(raw);
            var error = (await raw.SendAsync(BanFrame(keys, token, target, 7, "", view.Nonce, url))).Error;
            Assert.Equal(ErrorCode.Forbidden, error.Code);
            Assert.Contains("is listed as an operator", error.Message);
        }

        Assert.Empty(BansInForce(server));
        IReadOnlyList<OperatorLogRow> trail = server.Database.GetOperatorLog(0, 10);
        // User 0 names nobody: refused by the layer too, and recorded (the web page's pin is there too).
        trail = trail.Where(entry => entry.Tool == "plugin").ToList();
        Assert.Equal(3, trail.Count);
        Assert.All(trail, entry => Assert.Equal(("plugin", $"{AliceName}@{AliceWorld}", (long?) Alice, false), (entry.Tool, entry.Who, entry.ActorUserId, entry.Done)));

        // The protocol has no way to name an address, and the layer refuses one from the plugin anyway.
        var plugin = new OperatorActor(OperatorTool.Plugin, $"{AliceName}@{AliceWorld}", UserId: Alice);
        var actions = server.Factory.Services.GetRequiredService<OperatorActions>();
        Assert.Equal(OperatorRefusal.AddressesNotHere, actions.Ban(plugin, "203.0.113.9", 7, null).Refusal);
        Assert.Equal(OperatorRefusal.AddressesNotHere, actions.Lift(plugin, "203.0.113.9").Refusal);
        keys.Dispose();
    }

    /// <summary>An operator character makes at most 10 bans an hour from the plugin (lifts are not counted); past them it is told to use the web page.</summary>
    [Fact]
    public async Task ThePluginMakesTenBansAnHour() {
        var lodestone = AliceOnTheLodestone();
        await using var server = Server(lodestone);
        var bob = await server.RegisterAsync("Bob Hatter");
        var carol = await server.RegisterAsync("Carol Queen");
        var (token, keys) = await TakeOverAsync(await PinnedAliceAsync(server, lodestone));
        var url = server.ServerUri.AbsoluteUri;
        var (raw, _, _) = await LogInAsync(server, token);

        // Banning the same character again replaces its ban, and is a ban made all the same.
        var nonce = (await ViewAsync(raw)).Nonce;
        for (var i = 0; i < OperatorPolicy.PluginBansPerHour; i++) {
            var view = (await raw.SendAsync(BanFrame(keys, token, bob.UserId, 1, "", nonce, url))).OperatorView;
            Assert.NotNull(view);
            Assert.Equal((uint) (OperatorPolicy.PluginBansPerHour - i - 1), view.BansLeftThisHour);
            nonce = view.Nonce;
        }

        var lifted = (await raw.SendAsync(LiftFrame(keys, token, bob.UserId, nonce, url))).OperatorView;
        Assert.NotNull(lifted);
        var error = (await raw.SendAsync(BanFrame(keys, token, carol.UserId, 1, "", lifted.Nonce, url))).Error;
        Assert.Equal((ErrorCode.LimitReached, OperatorRefusalReason.HourLimit), (error.Code, error.OperatorRefusal));
        Assert.Contains("use the web page", error.Message);
        Assert.DoesNotContain(BansInForce(server), ban => ban.UserId == carol.UserId);
        keys.Dispose();
    }

    /// <summary>Reading the lists has its own limit, per user: 20 at once, then 1 every 3 seconds. Bans and lifts take from it too.</summary>
    [Fact]
    public async Task ReadingTheListsIsLimited() {
        var lodestone = AliceOnTheLodestone();
        var clock = new StoppedClock();
        await using var server = Server(lodestone, time: clock);
        var (token, keys) = await TakeOverAsync(await PinnedAliceAsync(server, lodestone));
        var (raw, _, _) = await LogInAsync(server, token);

        for (var i = 0; i < 20; i++) {
            await ViewAsync(raw);
        }

        var error = (await raw.SendAsync(new ClientFrame { GetOperatorView = new GetOperatorView() })).Error;
        Assert.Equal(ErrorCode.RateLimited, error.Code);
        clock.Advance(TimeSpan.FromSeconds(3));
        await ViewAsync(raw);
        Assert.Equal(ErrorCode.RateLimited, (await raw.SendAsync(LiftFrame(keys, token, 4242, ByteString.CopyFrom(new byte[32]), server.ServerUri.AbsoluteUri))).Error.Code);
        keys.Dispose();
    }

    /// <summary>
    /// The plugin's view never carries a full address: address bans and flags (IPv4 and IPv6) are shortened, as in Discord, in the
    /// lists and wherever else; characters are named; only the plugin's operator character is named as who made a ban.
    /// </summary>
    [Fact]
    public async Task TheViewNeverCarriesAFullAddress() {
        var lodestone = AliceOnTheLodestone();
        await using var server = Server(lodestone);
        var bob = await server.RegisterAsync("Bob Hatter");
        var (token, _) = await TakeOverAsync(await PinnedAliceAsync(server, lodestone));
        var actions = server.Factory.Services.GetRequiredService<OperatorActions>();
        var web = new OperatorActor(OperatorTool.WebPage, "alice@github", "100.101.102.103");
        Assert.True(actions.Ban(web, "203.0.113.9", 1, "Flooding").Done);
        Assert.True(actions.Ban(web, "2001:db8:ab12:3456::7", 1, null).Done);
        Assert.True(actions.Ban(new OperatorActor(OperatorTool.CommandLine, "alice"), bob.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture), 7, "Spamming").Done);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        server.Database.SaveFlag(new FlagRow("address 198.51.100.77", null, "198.51.100.77", now - 60, now, "ConnectionsPerIp, LookupBurst", "refused by 4 different limits within 10 minutes"));
        server.Database.SaveFlag(new FlagRow($"user {bob.UserId}", bob.UserId, null, now - 60, now, "InviteBurstPerPair", "refused by limits in 30 of the last 60 minutes"));

        var (raw, _, _) = await LogInAsync(server, token);
        var view = await ViewAsync(raw, about: bob.UserId);
        var bytes = view.ToByteArray();
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        foreach (var full in new[] { "203.0.113.9", "198.51.100.77", "2001:db8:ab12:3456", "100.101.102.103", "alice@github" }) {
            Assert.DoesNotContain(full, text);
        }

        Assert.Contains(view.InForce, ban => ban.Address == "203.0.113.x" && ban.UserId == 0 && ban.Reason == "Flooding" && ban.MadeVia == "web page" && ban.MadeBy == "");
        Assert.Contains(view.InForce, ban => ban.Address == "2001:db8:ab00::/40");
        Assert.Contains(view.Flags, flag => flag.Address == "198.51.100.x" && flag.Limits == "ConnectionsPerIp, LookupBurst");
        var bobsBan = Assert.Single(view.InForce, ban => ban.UserId == bob.UserId);
        Assert.Equal(("Bob Hatter", "command line", ""), (bobsBan.User.Name, bobsBan.MadeVia, bobsBan.MadeBy));
        Assert.True(Assert.Single(view.Flags, flag => flag.UserId == bob.UserId).Banned);
        Assert.Equal((3u, 2u, 0u), (view.InForceTotal, view.FlagsTotal, view.BlockedAutomatically));
        Assert.Equal(bob.UserId, view.About.UserId);
        Assert.Equal("Spamming", view.About.Ban.Reason);
        Assert.False(view.About.ListedOperator);
        Assert.Equal("refused by limits in 30 of the last 60 minutes", view.About.Flag.Why);

        // The plugin's model of it says the same.
        var lists = OperatorLists.From(view, DateTimeOffset.UtcNow);
        Assert.Equal("address 203.0.113.x", Assert.Single(lists.InForce, ban => ban.Address == "203.0.113.x").Who);
        Assert.Equal("Bob Hatter@Debug", lists.About!.Ban!.Who);
    }

    /// <summary>
    /// End to end, through the plugin's session: a ban by name@world applies at once (the player's connection closes, and the
    /// player is told it is blocked), shows in the lists as made in the plugin by this character, and is posted to Discord
    /// naming the character; lifting it likewise.
    /// </summary>
    [Fact]
    public async Task ABanAndALiftFromThePluginApplyAtOnceAndArePosted() {
        var lodestone = AliceOnTheLodestone();
        var discord = new FakeDiscord();
        await using var server = Server(lodestone, discord, more: [FakeDiscord.On, ("LookingGlass:Alerts:Restarts", "false")]);
        // Alerts that wait for others to go with them wait a moment here, rather than a minute.
        server.Factory.Services.GetRequiredService<ServerAlerts>().Poster!.BatchWindow = TimeSpan.FromMilliseconds(200);
        var bob = await server.RegisterAsync("Bob Hatter");
        var alice = await PinnedAliceAsync(server, lodestone);

        var found = await alice.Session.FindPlayerAsync("bob hatter", "debug", Ct);
        Assert.Equal(bob.UserId, found!.UserId);
        Assert.Null(await alice.Session.FindPlayerAsync("Nobody Here", "Debug", Ct));
        var about = (await alice.Session.RefreshOperatorListsAsync(bob.UserId, Ct)).About!;
        Assert.Equal(("Bob Hatter", false, null), (about.User!.Name, about.ListedOperator, about.Ban));

        var lists = await alice.Session.OperatorBanAsync(bob.UserId, 7, "  Spamming invites  ", Ct);
        var ban = Assert.Single(lists.InForce);
        Assert.Equal(("plugin", $"{AliceName}@{AliceWorld}", "Spamming invites"), (ban.MadeVia, ban.MadeBy, ban.Reason));
        Assert.Equal("made in the plugin by you", OperatorWords.MadeWords(ban, $"{AliceName}@{AliceWorld}"));
        Assert.Equal(OperatorPanel.BansPerHour - 1, lists.BansLeftThisHour);
        await WaitFor(() => bob.Session.Snapshot.State == ConnectionState.Blocked ? new object() : null);
        // (Perhaps in one message with the pin's alert, which came a moment before.)
        await discord.WaitForPost(posted => posted.Content.Contains($"**Ban made** in the plugin by {AliceName}@", StringComparison.Ordinal) && posted.Content.Contains("Bob Hatter"));

        lists = await alice.Session.OperatorLiftAsync(bob.UserId, Ct);
        Assert.Empty(lists.InForce);
        var history = Assert.Single(lists.History);
        Assert.Equal(("plugin", $"{AliceName}@{AliceWorld}"), (history.LiftedVia, history.LiftedBy));
        await discord.WaitForPost(posted => posted.Content.Contains($"**Ban lifted** in the plugin by {AliceName}@", StringComparison.Ordinal) && posted.Content.Contains("Bob Hatter"));

        // Nothing to lift now: why, from the server.
        var refused = await Assert.ThrowsAsync<ServerErrorException>(() => alice.Session.OperatorLiftAsync(bob.UserId, Ct));
        Assert.Equal((ErrorCode.NotFound, OperatorRefusalReason.NothingToLift), (refused.Code, refused.OperatorRefusal));
        Assert.Equal("Couldn't lift the ban on Bob Hatter@Debug: There's no ban on that character in force.", OperatorWords.NotLifted("Bob Hatter@Debug", refused, advanced: false));
        Assert.Contains("The server says: There is no ban", OperatorWords.NotLifted("Bob Hatter@Debug", refused, advanced: true));
        await Assert.ThrowsAsync<ArgumentException>(() => alice.Session.OperatorBanAsync(bob.UserId, 7, new string('x', OperatorPanel.MaxReasonLength + 1), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => alice.Session.OperatorBanAsync(bob.UserId, 7, "two\nlines", Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => alice.Session.OperatorBanAsync(bob.UserId, OperatorPanel.MaxDays + 1, "", Ct));
    }

    /// <summary>
    /// A server that doesn't agree to "operator.v1" (an older one) is never believed about it: even an answer saying the
    /// character is an operator shows nothing, and the plugin asks it for nothing.
    /// </summary>
    [Fact]
    public async Task AnOlderServerShowsNothing() {
        var lodestone = AliceOnTheLodestone();
        await using var server = Server(lodestone);
        var alice = await PinnedAliceAsync(server, lodestone);
        await alice.Session.DisposeAsync();

        var asked = new List<ClientFrame.BodyOneofCase>();
        var older = await server.RestartAsync(alice, server.Options(wrap: socket => new RewritingWebSocket(socket, frame => {
            // The capability left out of Welcome, as an older server does; the login's answer left saying it is an operator.
            frame.Response?.Welcome?.Capabilities.Remove(ProtocolInfo.Capabilities.Operator);
            return frame;
        }, sent => {
            lock (asked) {
                asked.Add(sent.BodyCase);
            }
        })));

        Assert.Equal(OperatorStanding.None, older.Session.Snapshot.Operator);
        Assert.Null(OperatorPanel.MenuItem(older.Session.Snapshot, new InviteTarget("Bob Hatter", "Lich")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => older.Session.RefreshOperatorListsAsync(ct: Ct));
        lock (asked) {
            Assert.DoesNotContain(ClientFrame.BodyOneofCase.GetOperatorView, asked);
        }
    }

    // ================================================================ after the review (2026-10-11)

    /// <summary>
    /// A ban on a Lodestone ID nobody registered (made on the web page or the command line) can be lifted from the plugin, and
    /// the dialog offers that; the plugin can't ban such an ID itself (the owner's default: the web page does), and is told why
    /// in its own words.
    /// </summary>
    [Fact]
    public async Task AnUnregisteredIdsBanIsLiftedButNotMadeFromThePlugin() {
        var lodestone = AliceOnTheLodestone();
        await using var server = Server(lodestone);
        var alice = await PinnedAliceAsync(server, lodestone);
        var actions = server.Factory.Services.GetRequiredService<OperatorActions>();
        Assert.True(actions.Ban(new OperatorActor(OperatorTool.CommandLine, "alice"), "4242", null, "Ban evasion").Done);

        var about = (await alice.Session.RefreshOperatorListsAsync(4242, Ct)).About!;
        Assert.Null(about.User);
        Assert.Equal("user 4242", about.Ban!.Who);
        Assert.Equal(OperatorDialog.LiftOnly, OperatorPanel.DialogFor(about, alice.UserId));

        var refused = await Assert.ThrowsAsync<ServerErrorException>(() => alice.Session.OperatorBanAsync(4242, 7, "", Ct));
        Assert.Equal((ErrorCode.NotFound, OperatorRefusalReason.NotRegistered), (refused.Code, refused.OperatorRefusal));
        Assert.Equal("Couldn't ban user 4242: That character isn't registered on this server: ban its Lodestone ID on the web page.",
            OperatorWords.NotBanned("user 4242", refused, advanced: false));
        Assert.Single(BansInForce(server), ban => ban.UserId == 4242 && ban.Reason == "Ban evasion");

        var lists = await alice.Session.OperatorLiftAsync(4242, Ct);
        Assert.Empty(lists.InForce);
        Assert.Equal(OperatorDialog.NotRegistered, OperatorPanel.DialogFor(lists.About, alice.UserId));
    }

    /// <summary>
    /// The plugin neither bans nor lifts bans on a character listed as an operator: one the command line banned with --force
    /// stays banned, and the dialog says it is an operator.
    /// </summary>
    [Fact]
    public async Task AnOperatorCharactersBanIsNotLiftedFromThePlugin() {
        var lodestone = AliceOnTheLodestone();
        await using var server = Server(lodestone);
        var actions = server.Factory.Services.GetRequiredService<OperatorActions>();
        Assert.True(actions.Ban(new OperatorActor(OperatorTool.CommandLine, "alice"), OtherOperator.ToString(System.Globalization.CultureInfo.InvariantCulture), 1, null, force: true).Done);
        var alice = await PinnedAliceAsync(server, lodestone);

        var about = (await alice.Session.RefreshOperatorListsAsync(OtherOperator, Ct)).About!;
        Assert.True(about.ListedOperator);
        Assert.NotNull(about.Ban);
        Assert.Equal(OperatorDialog.OperatorCharacter, OperatorPanel.DialogFor(about, alice.UserId));

        var refused = await Assert.ThrowsAsync<ServerErrorException>(() => alice.Session.OperatorLiftAsync(OtherOperator, Ct));
        Assert.Equal((ErrorCode.Forbidden, OperatorRefusalReason.OperatorCharacter), (refused.Code, refused.OperatorRefusal));
        Assert.Single(BansInForce(server), ban => ban.UserId == OtherOperator);
        Assert.Contains(server.Database.GetOperatorLog(0, 10), entry => entry is { Tool: "plugin", Action: "lift", Done: false });
    }

    /// <summary>
    /// A ban or lift is checked once more where it is made, with the keys its signature was checked with: keys registered (and
    /// even pinned) while it was on its way, or a pin taken away, change nothing.
    /// </summary>
    [Fact]
    public async Task AChangeIsCheckedAgainWithTheKeysItWasSignedWith() {
        var lodestone = AliceOnTheLodestone();
        await using var server = Server(lodestone);
        var bob = await server.RegisterAsync("Bob Hatter");
        var alice = await PinnedAliceAsync(server, lodestone);
        var actions = server.Factory.Services.GetRequiredService<OperatorActions>();
        var oldKeys = server.Database.GetUser(Alice)!.Keys.Hash;
        OperatorActor Plugin(byte[] keys) => new(OperatorTool.Plugin, $"{AliceName}@{AliceWorld}", "", Alice, keys);
        var whom = bob.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(actions.Ban(Plugin(oldKeys), whom, 1, null).Done);

        await alice.Session.DisposeAsync();
        await RegisterAliceAsync(server, lodestone);
        PinAlice(server);
        Assert.Equal(OperatorRefusal.NotAnOperator, actions.Lift(Plugin(oldKeys), whom).Refusal);
        Assert.Equal(OperatorRefusal.NotAnOperator, actions.Ban(Plugin(oldKeys), whom, 7, null).Refusal);
        var newKeys = server.Database.GetUser(Alice)!.Keys.Hash;
        server.ExecuteSql("DELETE FROM operator_pins;");
        Assert.Equal(OperatorRefusal.NotAnOperator, actions.Lift(Plugin(newKeys), whom).Refusal);
        Assert.Single(BansInForce(server), ban => ban.UserId == bob.UserId);
        Assert.Equal(3, server.Database.GetOperatorLog(0, 20).Count(entry => entry is { Tool: "plugin", Done: false }));
    }

    /// <summary>
    /// The plugin signs the nonce of the lists it last fetched; one the server no longer holds is refused for that reason (not
    /// by its words), and the plugin asks for fresh lists and tries exactly once more: one ban, never two.
    /// </summary>
    [Fact]
    public async Task AStaleNonceIsTriedAgainOnceAndBansOnce() {
        var lodestone = AliceOnTheLodestone();
        var clock = new ManualClock();
        await using var server = Server(lodestone, time: clock);
        var bob = await server.RegisterAsync("Bob Hatter");
        var alice = await PinnedAliceAsync(server, lodestone);
        await alice.Session.DisposeAsync();
        var sent = new System.Collections.Concurrent.ConcurrentQueue<ClientFrame.BodyOneofCase>();
        alice = await server.RestartAsync(alice, server.Options(wrap: socket => new RewritingWebSocket(socket, frame => frame, frame => sent.Enqueue(frame.BodyCase))));
        await WaitFor(() => alice.Session.IsOperator ? new object() : null);

        await alice.Session.RefreshOperatorListsAsync(ct: Ct);
        // Past the server's few minutes, within the plugin's own.
        clock.Offset = RequestHandler.OperatorNonceLifetime + TimeSpan.FromSeconds(30);
        sent.Clear();
        var lists = await alice.Session.OperatorBanAsync(bob.UserId, 1, "Once", Ct);

        Assert.Single(lists.InForce, ban => ban.UserId == bob.UserId);
        Assert.Equal([ClientFrame.BodyOneofCase.OperatorBan, ClientFrame.BodyOneofCase.GetOperatorView, ClientFrame.BodyOneofCase.OperatorBan],
            sent.Where(kind => kind is ClientFrame.BodyOneofCase.OperatorBan or ClientFrame.BodyOneofCase.GetOperatorView).ToArray());
        Assert.Single(server.Database.GetOperatorLog(0, 20), entry => entry is { Action: "ban", Done: true });
    }

    /// <summary>
    /// A ban or lift from an operator's own login without its key's signature is posted to Discord as urgent, with the ping (a
    /// copy of the login may be in use), at most once in ten minutes for each character.
    /// </summary>
    [Fact]
    public async Task ABadSignatureFromAnOperatorsLoginIsPostedOnceInTenMinutes() {
        const string role = "424242424242424242";
        var lodestone = AliceOnTheLodestone();
        var discord = new FakeDiscord();
        await using var server = Server(lodestone, discord, more: [FakeDiscord.On, ("LookingGlass:Alerts:Restarts", "false"), ("LookingGlass:Alerts:PingRoleId", role)]);
        server.Factory.Services.GetRequiredService<ServerAlerts>().Poster!.BatchWindow = TimeSpan.FromMilliseconds(200);
        var bob = await server.RegisterAsync("Bob Hatter");
        var (token, keys) = await TakeOverAsync(await PinnedAliceAsync(server, lodestone));
        var (raw, _, _) = await LogInAsync(server, token);
        using var stranger = IdentityKeys.Generate();

        for (var i = 0; i < 2; i++) {
            var view = await ViewAsync(raw);
            var error = (await raw.SendAsync(BanFrame(stranger, token, bob.UserId, 1, "", view.Nonce, server.ServerUri.AbsoluteUri))).Error;
            Assert.Equal(OperatorRefusalReason.BadSignature, error.OperatorRefusal);
        }

        var posted = await discord.WaitForPost(message => message.Content.Contains("without its identity key's signature"));
        Assert.Contains($"<@&{role}>", posted.Content);
        Assert.Contains("Alice Operator", posted.Content);
        Assert.Equal(role, Assert.Single(posted.Roles));
        await Task.Delay(500, Ct);
        Assert.Single(discord.Posts, message => message.Content.Contains("without its identity key's signature"));
        keys.Dispose();
    }

    /// <summary>A connection holds the nonces of its last four views: the fifth view's pushes the first out.</summary>
    [Fact]
    public async Task OnlyTheLastFourNoncesAreGood() {
        var lodestone = AliceOnTheLodestone();
        await using var server = Server(lodestone);
        var bob = await server.RegisterAsync("Bob Hatter");
        var (token, keys) = await TakeOverAsync(await PinnedAliceAsync(server, lodestone));
        var (raw, _, _) = await LogInAsync(server, token);
        var url = server.ServerUri.AbsoluteUri;

        var nonces = new List<ByteString>();
        for (var i = 0; i < 5; i++) {
            nonces.Add((await ViewAsync(raw)).Nonce);
        }

        var error = (await raw.SendAsync(BanFrame(keys, token, bob.UserId, 1, "", nonces[0], url))).Error;
        Assert.Equal((ErrorCode.InvalidRequest, OperatorRefusalReason.NonceStale), (error.Code, error.OperatorRefusal));
        Assert.NotNull((await raw.SendAsync(BanFrame(keys, token, bob.UserId, 1, "", nonces[1], url))).OperatorView);
        keys.Dispose();
    }

    /// <summary>
    /// The address signed for is checked by its origin, as every signed request's: this server's (ws or http, any path, any case)
    /// is accepted; another port, TLS where the server has none, nothing, or not an address, refused before the signature.
    /// </summary>
    [Fact]
    public async Task TheAddressSignedForIsCheckedByItsOrigin() {
        var lodestone = AliceOnTheLodestone();
        await using var server = Server(lodestone);
        var bob = await server.RegisterAsync("Bob Hatter");
        var (token, keys) = await TakeOverAsync(await PinnedAliceAsync(server, lodestone));
        var (raw, _, _) = await LogInAsync(server, token);

        var nonce = (await ViewAsync(raw)).Nonce;
        foreach (var url in new[] { "ws://localhost/ws", "http://localhost/ws", "ws://LOCALHOST/another/path" }) {
            var view = (await raw.SendAsync(BanFrame(keys, token, bob.UserId, 1, "", nonce, url))).OperatorView;
            Assert.NotNull(view);
            nonce = view.Nonce;
        }

        foreach (var url in new[] { "ws://localhost:5180/ws", "wss://localhost/ws", "", "not an address" }) {
            var error = (await raw.SendAsync(BanFrame(keys, token, bob.UserId, 1, "", nonce, url))).Error;
            Assert.Equal((ErrorCode.Forbidden, OperatorRefusalReason.WrongAddress), (error.Code, error.OperatorRefusal));
        }

        keys.Dispose();
    }

    /// <summary>If how a character stands can't be read (the database failing), its login still works, and is told nothing.</summary>
    [Fact]
    public async Task ALoginIsToldNothingWhenItsStandingCantBeRead() {
        var lodestone = AliceOnTheLodestone();
        var logs = new CapturingLoggerProvider();
        await using var server = Server(lodestone, logs: logs);
        var (token, _) = await TakeOverAsync(await PinnedAliceAsync(server, lodestone));
        server.ExecuteSql("ALTER TABLE operator_pins RENAME TO operator_pins_gone;");
        try {
            var (_, _, ok) = await LogInAsync(server, token);
            Assert.Equal(OperatorLoginStatus.NotListed, ok.OperatorStatus);
            Assert.Equal(Alice, ok.User.UserId);
            Assert.Contains(logs.AtLeast(Microsoft.Extensions.Logging.LogLevel.Warning), line => line.Contains($"Couldn't tell whether user {Alice} is an operator"));
        } finally {
            server.ExecuteSql("ALTER TABLE operator_pins_gone RENAME TO operator_pins;");
        }
    }

    /// <summary>Each login drops the lists the last connection showed, so Settings asks for them again (a reconnect included).</summary>
    [Fact]
    public async Task TheListsAreDroppedAtEachLogin() {
        var lodestone = AliceOnTheLodestone();
        await using var server = Server(lodestone);
        var alice = await PinnedAliceAsync(server, lodestone);
        await alice.Session.RefreshOperatorListsAsync(ct: Ct);
        Assert.NotNull(alice.Session.Snapshot.OperatorLists);

        alice.Session.Reconnect();
        await WaitFor(() => alice.Session.Snapshot is { State: ConnectionState.Ready, Operator: OperatorStanding.Operator, OperatorLists: null } ? new object() : null);
        await alice.Session.RefreshOperatorListsAsync(ct: Ct);
        Assert.NotNull(alice.Session.Snapshot.OperatorLists);
    }
}