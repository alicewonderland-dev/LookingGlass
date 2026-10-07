using System.Net;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using LookingGlass.Core.Crypto;
using LookingGlass.Core.Membership;
using LookingGlass.Protocol;
using LookingGlass.Server;
using LookingGlass.Server.Data;
using LookingGlass.Server.Hosting;
using LookingGlass.Server.Realtime;
using LookingGlass.Server.Services;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// Calls <see cref="RequestHandler"/> directly, with a stub Lodestone, for the
/// registration and login paths an in-process client can't reach (real worlds).
/// </summary>
public sealed class RequestHandlerTests : IDisposable {
    private const string World = "Gilgamesh";
    private const long LodestoneId = 31337;

    // The address the clients here sign for, which the handler lists as its own unless a test says otherwise.
    private const string Url = "ws://localhost/ws";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lgt-handler-" + Guid.NewGuid().ToString("N"));
    private readonly Database _db;
    private readonly ConnectionRegistry _registry;
    private readonly StubLodestone _lodestone = new();
    private readonly CapturingLoggerProvider _logs = new();
    private RequestHandler _handler;

    public RequestHandlerTests() {
        Directory.CreateDirectory(this._directory);
        this._db = new Database(Path.Combine(this._directory, "test.db"));
        this._registry = new ConnectionRegistry(this._db, NullLogger<ConnectionRegistry>.Instance);
        this._handler = this.NewHandler(Url);
    }

    /// <param name="publicUrls">The server's addresses. The handler isn't in Development, so with none it trusts no address.</param>
    private RequestHandler NewHandler(params string[] publicUrls) => this.NewHandler(15, publicUrls);

    /// <param name="challengeMinutes">How long a registration challenge lives.</param>
    /// <param name="publicUrls">The server's addresses (by default the one the clients here sign for).</param>
    /// <param name="configure">Changes the settings further.</param>
    /// <param name="time">The handler's (and its Lodestone client's) clock.</param>
    private RequestHandler NewHandler(int challengeMinutes, string[]? publicUrls = null, Action<ServerOptions>? configure = null, TimeProvider? time = null) {
        var settings = new ServerOptions {
            Dev = { AllowDebugAccounts = true },
            Lodestone = { BaseUrl = "https://lodestone.test", MinDelaySeconds = 0, ChallengeMinutes = challengeMinutes },
            Limits = { RegistrationsPerHourPerIp = 3 },
            PublicUrls = publicUrls ?? [Url],
        };
        configure?.Invoke(settings);
        var options = Options.Create(settings);
        var lodestone = new LodestoneClient(new HttpClient(this._lodestone), options, NullLogger<LodestoneClient>.Instance, time);
        var logger = Microsoft.Extensions.Logging.LoggerFactory.Create(logging => logging.AddProvider(this._logs)).CreateLogger<RequestHandler>();
        return new RequestHandler(this._db, this._registry, lodestone, options, logger,
            SignedLogMembershipProvider.Instance, SealedEpochKeyProvider.Instance, time: time);
    }

    public void Dispose() => DeleteDirectory(this._directory);

    [Fact]
    public async Task RegistrationsArePerAddressAndIpv6CountsPerSlash64() {
        // Three registrations an hour from one address; both of these are in the same /64.
        var first = ClientAddresses.LimitKey(IPAddress.Parse("2001:db8:1:2::10"));
        var second = ClientAddresses.LimitKey(IPAddress.Parse("2001:db8:1:2:ffff::20"));
        for (var i = 0; i < 3; i++) {
            Assert.NotNull((await this.StartRegistrationAsync(await this.HelloAsync(i % 2 == 0 ? first : second))).RegistrationChallenge);
        }

        var refused = await this.StartRegistrationAsync(await this.HelloAsync(second));
        Assert.Equal(ErrorCode.RateLimited, refused.Error?.Code);

        // Another /64 has its own allowance.
        var elsewhere = await this.StartRegistrationAsync(await this.HelloAsync(ClientAddresses.LimitKey(IPAddress.Parse("2001:db8:1:3::10"))));
        Assert.NotNull(elsewhere.RegistrationChallenge);
    }

    [Fact]
    public async Task VerifyingHasACooldown() {
        var connection = await this.HelloAsync("203.0.113.10");
        using var keys = IdentityKeys.Generate();
        var challenge = (await this.StartRegistrationAsync(connection, keys: keys)).RegistrationChallenge!;

        // The stub profile doesn't contain the code, so the first attempt fails normally...
        var first = await this.CompleteRegistrationAsync(connection, keys, challenge);
        Assert.Equal(ErrorCode.RegistrationFailed, first.Error?.Code);
        Assert.Contains("isn't in your Lodestone profile", first.Error!.Message);

        // ...and an immediate retry is refused without asking the Lodestone again.
        var second = await this.CompleteRegistrationAsync(connection, keys, challenge);
        Assert.Equal(ErrorCode.RateLimited, second.Error?.Code);
        Assert.Contains("Wait", second.Error!.Message);
        Assert.Equal(1, connection.VerifyAttempts);
    }

    private const string NotListed =
        "Couldn't find Test Person on Gilgamesh on the Lodestone. Check that the name and home world are spelled right. A new character can take a " +
        "while to show up there, and one whose Lodestone profile is private may not show up at all: make it public in the character's privacy " +
        "settings on the Lodestone, wait a moment, then try again.";

    /// <summary>
    /// A character the Lodestone doesn't list (a typo, a character too new to be there, a private profile) is said in words
    /// that say what to do, isn't remembered (a try right after fixing it looks again), and costs none of the address's
    /// registrations.
    /// </summary>
    [Fact]
    public async Task ACharacterNotListedIsLookedUpAgainAndCostsNoRegistration() {
        var connection = await this.HelloAsync("203.0.113.80");
        this._lodestone.Listed = false;
        // More than the 3 registrations an hour this handler allows.
        for (var i = 0; i < 5; i++) {
            var missing = await this.StartRegistrationAsync(connection);
            Assert.Equal(ErrorCode.RegistrationFailed, missing.Error?.Code);
            Assert.Equal(NotListed, missing.Error!.Message);
            PlainLanguage.AssertPlain(missing.Error.Message);
        }

        // Each was asked again: none came from a cache.
        Assert.Equal(5, this._lodestone.Requests);

        // Made public: found at once, and all three registrations are still there.
        this._lodestone.Listed = true;
        for (var i = 0; i < 3; i++) {
            Assert.NotNull((await this.StartRegistrationAsync(connection)).RegistrationChallenge);
        }

        var refused = await this.StartRegistrationAsync(connection);
        Assert.Equal(ErrorCode.RateLimited, refused.Error?.Code);
        Assert.Equal("Too many registrations from your address; try again in about an hour.", refused.Error!.Message);
        PlainLanguage.AssertPlain(refused.Error.Message);
    }

    /// <summary>
    /// Lookups that fail (not listed, or the Lodestone down) have their own, looser allowance per address, so the server-wide
    /// Lodestone queue isn't spent on names that aren't there; past it, nothing more is asked of the Lodestone for an hour.
    /// </summary>
    [Fact]
    public async Task FailedLookupsHaveTheirOwnAllowancePerAddress() {
        Assert.Equal(20, new LimitOptions().RegistrationLookupFailuresPerHourPerIp);
        Assert.Equal(10, new LimitOptions().RegistrationsPerHourPerIp);
        var clock = new ManualClock();
        this._handler = this.NewHandler(15, configure: settings => settings.Limits.RegistrationLookupFailuresPerHourPerIp = 2, time: clock);
        var connection = await this.HelloAsync("203.0.113.81");
        this._lodestone.Listed = false;
        Assert.Equal(NotListed, (await this.StartRegistrationAsync(connection)).Error?.Message);
        this._lodestone.Down = true;
        var down = await this.StartRegistrationAsync(connection);
        Assert.Equal(ErrorCode.RegistrationFailed, down.Error?.Code);
        Assert.Equal("The Lodestone isn't responding right now; try again in a few minutes.", down.Error!.Message);

        var asked = this._lodestone.Requests;
        this._lodestone.Down = false;
        this._lodestone.Listed = true;
        var refused = await this.StartRegistrationAsync(connection);
        Assert.Equal(ErrorCode.RateLimited, refused.Error?.Code);
        Assert.Equal("Too many characters from your address couldn't be found on the Lodestone; check the name and home world, and try again in about an hour.",
            refused.Error!.Message);
        PlainLanguage.AssertPlain(refused.Error.Message);
        Assert.Equal(asked, this._lodestone.Requests);

        // Another address has its own; and an hour on, this one has its allowance back, its registrations untouched.
        Assert.NotNull((await this.StartRegistrationAsync(await this.HelloAsync("198.51.100.81"))).RegistrationChallenge);
        clock.Offset += TimeSpan.FromMinutes(61);
        for (var i = 0; i < 3; i++) {
            Assert.NotNull((await this.StartRegistrationAsync(connection)).RegistrationChallenge);
        }
    }

    /// <summary>
    /// A private profile is said to be private, with what to do; and Verify reads the profile afresh each time (asking for no
    /// cached copy), so it finds the code once the profile is public.
    /// </summary>
    [Fact]
    public async Task APrivateProfileIsSaidSoAndVerifyReadsItAfresh() {
        var connection = await this.HelloAsync("203.0.113.82");
        using var keys = IdentityKeys.Generate();
        var challenge = (await this.StartRegistrationAsync(connection, keys: keys)).RegistrationChallenge!;
        this._lodestone.Private = true;
        var hidden = await this.CompleteRegistrationAsync(connection, keys, challenge);
        Assert.Equal(ErrorCode.RegistrationFailed, hidden.Error?.Code);
        Assert.Equal("Your character's profile on the Lodestone is private, so the server can't see the code in it. Make it public in the character's " +
                     "privacy settings on the Lodestone, wait a moment, then press Verify again.", hidden.Error!.Message);
        PlainLanguage.AssertPlain(hidden.Error.Message);

        // Made public, with the code in it: the next Verify (past its cooldown) finds it.
        this._lodestone.Private = false;
        this._lodestone.Profile = $"My code: {challenge.Code}";
        connection.LastVerifyAttempt = DateTimeOffset.MinValue;
        Assert.NotNull((await this.CompleteRegistrationAsync(connection, keys, challenge)).RegistrationComplete);
        Assert.Equal((2, 2), this._lodestone.ProfileReads);
    }

    /// <summary>The Lodestone down while verifying is said so, and the attempt isn't counted against the challenge.</summary>
    [Fact]
    public async Task TheLodestoneDownWhileVerifyingIsSaidSo() {
        var connection = await this.HelloAsync("203.0.113.83");
        using var keys = IdentityKeys.Generate();
        var challenge = (await this.StartRegistrationAsync(connection, keys: keys)).RegistrationChallenge!;
        this._lodestone.Down = true;
        var down = await this.CompleteRegistrationAsync(connection, keys, challenge);
        Assert.Equal("The Lodestone isn't responding right now; try again in a few minutes.", down.Error?.Message);
        Assert.Equal(0, connection.VerifyAttempts);
    }

    /// <summary>
    /// A registration through the Lodestone is signed for the address the client connected to, which is checked against
    /// the server's own addresses as for key login: a malicious server the user registers with can't relay this server's
    /// challenge (code and nonce) to the user and register the user's key here. Checked before asking the Lodestone.
    /// </summary>
    [Fact]
    public async Task ARegistrationSignedForAnotherServerIsRefused() {
        this._handler = this.NewHandler("wss://chat.example.com/ws");
        var connection = await this.HelloAsync("203.0.113.60");
        using var keys = IdentityKeys.Generate();
        var challenge = (await this.StartRegistrationAsync(connection, keys: keys, url: "wss://chat.example.com/ws")).RegistrationChallenge!;
        this._lodestone.Profile = $"My code: {challenge.Code}";

        foreach (var url in new[] { "wss://evil.example/ws", "ws://localhost/ws", "wss://chat.example.com.evil.example/ws" }) {
            var refused = await this.CompleteRegistrationAsync(connection, keys, challenge, url);
            Assert.Equal(ErrorCode.RegistrationFailed, refused.Error?.Code);
            // What the user can act on: the address they used, and the ones to use instead (Welcome lists those anyway).
            Assert.Contains($"doesn't accept the address {url}", refused.Error!.Message);
            Assert.Contains("Use one of: wss://chat.example.com/ws", refused.Error.Message);
        }

        // And the operator sees each, by origin, and nothing secret.
        var warnings = this._logs.AtLeast(Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Equal(3, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("wss://evil.example:443") && w.Contains(LodestoneId.ToString()));
        Assert.Contains(warnings, w => w.Contains("ws://localhost:80"));
        Assert.All(warnings, w => Assert.DoesNotContain(Convert.ToBase64String(RegistrationProof.Sign(keys, challenge.Nonce.Span, challenge.LodestoneId, "ws://localhost/ws")), w));

        Assert.Equal(0, connection.VerifyAttempts);
        Assert.Null(this._db.GetUser(LodestoneId));

        var registered = await this.CompleteRegistrationAsync(connection, keys, challenge, "wss://chat.example.com/ws");
        Assert.NotNull(registered.RegistrationComplete);
        Assert.Equal(keys.SigningPublicKey, this._db.GetUser(LodestoneId)!.SigningKey);
    }

    /// <summary>
    /// Outside Development without PublicUrls the server doesn't start (see <c>KeyLoginTests.OutsideDevelopmentTheServerNeedsPublicUrls</c>).
    /// A handler made that way anyway has no address to check a signed one against, so it accepts none: a registration
    /// through the Lodestone is refused whatever address it names, when it starts, before the Lodestone is asked, as is
    /// key login. Debug accounts, whose registrations aren't checked against an address, still work.
    /// </summary>
    [Fact]
    public async Task WithoutAddressesNoSignedAddressIsAccepted() {
        this._handler = this.NewHandler();
        var connection = await this.HelloAsync("203.0.113.62");
        using var keys = IdentityKeys.Generate();

        foreach (var url in new[] { Url, "wss://chat.example.com/ws" }) {
            var refused = await this.StartRegistrationAsync(connection, keys: keys, url: url);
            Assert.Equal(ErrorCode.RegistrationFailed, refused.Error?.Code);
            Assert.Contains($"doesn't accept the address {url}", refused.Error!.Message);
            Assert.Contains("LookingGlass:PublicUrls", refused.Error.Message);
        }

        Assert.Null(connection.PendingRegistration);
        Assert.Equal(0, this._lodestone.Requests);
        Assert.Null(this._db.GetUser(LodestoneId));
        Assert.Equal(ErrorCode.NotAuthenticated, (await this.SendAsync(connection, new ClientFrame { StartKeyLogin = new StartKeyLogin { UserId = LodestoneId } })).Error?.Code);
        Assert.NotNull(await this.RegisterDebugAsync("No Addresses"));
    }

    /// <summary>A registration through the Lodestone is signed by the key being registered, like a debug one.</summary>
    [Fact]
    public async Task ALodestoneRegistrationNeedsASignatureByTheKey() {
        var connection = await this.HelloAsync("203.0.113.61");
        using var keys = IdentityKeys.Generate();
        using var otherKeys = IdentityKeys.Generate();
        var challenge = (await this.StartRegistrationAsync(connection, keys: keys)).RegistrationChallenge!;
        this._lodestone.Profile = $"My code: {challenge.Code}";

        Assert.Equal(ErrorCode.RegistrationFailed, (await this.SendAsync(connection, new ClientFrame { CompleteRegistration = new CompleteRegistration() })).Error?.Code);
        Assert.Equal(ErrorCode.RegistrationFailed, (await this.CompleteRegistrationAsync(connection, otherKeys, challenge)).Error?.Code);
        Assert.Equal(0, connection.VerifyAttempts);
        Assert.Null(this._db.GetUser(LodestoneId));

        Assert.NotNull((await this.CompleteRegistrationAsync(connection, keys, challenge)).RegistrationComplete);
    }

    /// <summary>
    /// The code is no random string but the one derived from the address the client connected to, the key it registers,
    /// this connection's nonce, the client's nonce and the character (see <see cref="LodestoneCode"/>), which the client
    /// checks before showing it. Debug accounts get no code.
    /// </summary>
    [Fact]
    public async Task TheCodeIsDerivedFromTheAddressKeyNonceAndCharacter() {
        this._handler = this.NewHandler("wss://chat.example.com/ws", "ws://100.64.0.1:5000/ws");
        using var keys = IdentityKeys.Generate();
        using var otherKeys = IdentityKeys.Generate();
        var codes = new List<string>();
        foreach (var (url, identity) in new[] {
                     ("wss://chat.example.com/ws", keys), ("wss://chat.example.com/ws", keys), ("ws://100.64.0.1:5000/ws", keys), ("wss://chat.example.com/ws", otherKeys),
                 }) {
            // From an address each, as the limit is three an hour.
            var clientNonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(LodestoneCode.ClientNonceSize);
            var challenge = (await this.StartRegistrationAsync(await this.HelloAsync($"203.0.113.{100 + codes.Count}"), keys: identity, url: url, clientNonce: clientNonce))
                .RegistrationChallenge!;
            Assert.Equal(LodestoneCode.Derive(Core.Client.ServerOrigin.FromUrl(url)!, identity.SigningPublicKey, challenge.Nonce.Span, clientNonce, LodestoneId), challenge.Code);
            Assert.Equal(LodestoneId, challenge.LodestoneId);
            codes.Add(challenge.Code);
        }

        // A fresh nonce each time, so never the same code twice.
        Assert.Equal(codes.Count, codes.Distinct().Count());

        var debug = (await this.StartRegistrationAsync(await this.HelloAsync("203.0.113.63"), "Debug Person", ProtocolInfo.DebugWorldName, keys)).RegistrationChallenge!;
        Assert.True(debug.VerificationSkipped);
        Assert.Equal("", debug.Code);
    }

    /// <summary>
    /// The client's nonce is exactly 32 bytes: without one (a plugin from before), registering is refused asking to
    /// update; any other length is an invalid request. Checked before the Lodestone is asked, for debug accounts too.
    /// </summary>
    [Theory]
    [InlineData(World)]
    [InlineData(ProtocolInfo.DebugWorldName)]
    public async Task TheClientNonceIsRequiredAndIs32Bytes(string world) {
        var connection = await this.HelloAsync("203.0.113.70");
        var old = await this.StartRegistrationAsync(connection, world: world, clientNonce: []);
        Assert.Equal(ErrorCode.RegistrationFailed, old.Error?.Code);
        Assert.Contains("update the plugin", old.Error!.Message);

        foreach (var length in new[] { 1, 16, 31, 33, 64 }) {
            var wrong = await this.StartRegistrationAsync(connection, world: world, clientNonce: new byte[length]);
            Assert.Equal(ErrorCode.InvalidRequest, wrong.Error?.Code);
        }

        Assert.Null(connection.PendingRegistration);
        Assert.Equal(0, this._lodestone.Requests);
        Assert.NotNull((await this.StartRegistrationAsync(connection, world: world, clientNonce: new byte[32])).RegistrationChallenge);
    }

    /// <summary>A challenge lives for ChallengeMinutes, kept between 1 and 60 even by a handler made without the startup check.</summary>
    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(15, 15)]
    [InlineData(60, 60)]
    [InlineData(10_000, 60)]
    public async Task AChallengeLivesBetweenOneAndSixtyMinutes(int configured, int minutes) {
        this._handler = this.NewHandler(challengeMinutes: configured);
        var started = DateTimeOffset.UtcNow;
        var challenge = (await this.StartRegistrationAsync(await this.HelloAsync("203.0.113.71"))).RegistrationChallenge!;
        var lifetime = DateTimeOffset.FromUnixTimeSeconds(challenge.ExpiresUnix) - started;
        Assert.InRange(lifetime.TotalMinutes, minutes - 0.1, minutes + 0.1);
    }

    /// <summary>
    /// The code is made for the address the client named (and signs for), never the one the connection's Host header
    /// names: with PublicUrls, that is whatever the connecting side sent, and need not be the address the client uses.
    /// </summary>
    [Fact]
    public async Task TheCodeIsForTheAddressTheClientNamedNotTheHostHeader() {
        this._handler = this.NewHandler("wss://chat.example.com/ws", "ws://100.64.0.1:5000/ws");
        foreach (var url in new[] { "wss://chat.example.com/ws", "ws://100.64.0.1:5000/ws" }) {
            using var keys = IdentityKeys.Generate();
            var clientNonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(LodestoneCode.ClientNonceSize);
            // Reached through a proxy that passes on another Host.
            var hostHeader = Core.Client.ServerOrigin.FromRequest("http", "localhost:5180")!;
            var connection = await this.HelloAsync("203.0.113.72", hostHeader);
            var challenge = (await this.StartRegistrationAsync(connection, keys: keys, url: url, clientNonce: clientNonce)).RegistrationChallenge!;

            Assert.Equal(LodestoneCode.Derive(Core.Client.ServerOrigin.FromUrl(url)!, keys.SigningPublicKey, challenge.Nonce.Span, clientNonce, LodestoneId), challenge.Code);
            Assert.NotEqual(LodestoneCode.Derive(hostHeader, keys.SigningPublicKey, challenge.Nonce.Span, clientNonce, LodestoneId), challenge.Code);
        }
    }

    /// <summary>
    /// A registration completes through the address it was started for, which its code was made for: another of the
    /// server's addresses is refused (before the Lodestone is asked), though it is the server's too.
    /// </summary>
    [Fact]
    public async Task ARegistrationCompletesThroughTheAddressItWasStartedFor() {
        this._handler = this.NewHandler("wss://chat.example.com/ws", "ws://100.64.0.1:5000/ws");
        using var keys = IdentityKeys.Generate();
        var connection = await this.HelloAsync("203.0.113.73");
        var challenge = (await this.StartRegistrationAsync(connection, keys: keys, url: "wss://chat.example.com/ws")).RegistrationChallenge!;
        this._lodestone.Profile = $"My code: {challenge.Code}";

        var elsewhere = await this.CompleteRegistrationAsync(connection, keys, challenge, "ws://100.64.0.1:5000/ws");
        Assert.Equal(ErrorCode.RegistrationFailed, elsewhere.Error?.Code);
        Assert.Contains("wss://chat.example.com", elsewhere.Error!.Message);
        Assert.Equal(0, connection.VerifyAttempts);
        Assert.Null(this._db.GetUser(LodestoneId));

        // Any spelling of the same origin does.
        Assert.NotNull((await this.CompleteRegistrationAsync(connection, keys, challenge, "wss://CHAT.example.com:443/other")).RegistrationComplete);
    }

    /// <summary>
    /// A registration completes only for the key its code was issued for: it is bound to the pending registration, the
    /// proof of possession must be by it, and another key's registration looks for another code.
    /// </summary>
    [Fact]
    public async Task CompletingWithAnotherKeyThanTheCodeWasIssuedForIsRefused() {
        using var keys = IdentityKeys.Generate();
        using var otherKeys = IdentityKeys.Generate();
        var connection = await this.HelloAsync("203.0.113.64");
        var challenge = (await this.StartRegistrationAsync(connection, keys: keys)).RegistrationChallenge!;
        this._lodestone.Profile = $"My code: {challenge.Code}";

        // Signed by another key over this connection's challenge: refused before the Lodestone is asked.
        var refused = await this.CompleteRegistrationAsync(connection, otherKeys, challenge);
        Assert.Equal(ErrorCode.RegistrationFailed, refused.Error?.Code);
        Assert.Contains("isn't signed with the identity key being registered", refused.Error!.Message);
        Assert.Equal(0, connection.VerifyAttempts);

        // The other key's own registration has its own code, which isn't the one in the profile.
        var elsewhere = await this.HelloAsync("203.0.113.65");
        var other = (await this.StartRegistrationAsync(elsewhere, keys: otherKeys)).RegistrationChallenge!;
        Assert.NotEqual(challenge.Code, other.Code);
        var notFound = await this.CompleteRegistrationAsync(elsewhere, otherKeys, other);
        Assert.Equal(ErrorCode.RegistrationFailed, notFound.Error?.Code);
        // Without the code: the client knows its own, and a server's words are no place for one (see LodestoneCode.Redact).
        Assert.Contains("isn't in your Lodestone profile", notFound.Error!.Message);
        Assert.DoesNotContain(other.Code, notFound.Error.Message);
        Assert.DoesNotContain("LGC-", notFound.Error.Message);
        Assert.Null(this._db.GetUser(LodestoneId));

        // The key the code was issued for registers.
        Assert.NotNull((await this.CompleteRegistrationAsync(connection, keys, challenge)).RegistrationComplete);
        Assert.Equal(keys.SigningPublicKey, this._db.GetUser(LodestoneId)!.SigningKey);
    }

    /// <summary>
    /// The code is made for the address the client says it connected to, so that address is checked when registering
    /// starts, as when it completes: one that isn't this server's is refused before the Lodestone is asked, without using
    /// up the address's registrations, and logged. Debug accounts aren't checked (the echo bot connects to a local address).
    /// </summary>
    [Fact]
    public async Task StartingARegistrationForAnotherServersAddressIsRefused() {
        this._handler = this.NewHandler("wss://chat.example.com/ws");
        using var keys = IdentityKeys.Generate();
        foreach (var url in new[] { "wss://evil.example/ws", Url, "wss://chat.example.com.evil.example/ws", "not an address" }) {
            var connection = await this.HelloAsync("203.0.113.66");
            var refused = await this.StartRegistrationAsync(connection, keys: keys, url: url);
            Assert.Equal(ErrorCode.RegistrationFailed, refused.Error?.Code);
            Assert.Contains($"doesn't accept the address {url}", refused.Error!.Message);
            Assert.Contains("Use one of: wss://chat.example.com/ws", refused.Error.Message);
            Assert.Null(connection.PendingRegistration);
        }

        Assert.Equal(0, this._lodestone.Requests);
        var warnings = this._logs.AtLeast(Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Equal(4, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("wss://evil.example:443"));

        // The address's three registrations an hour are all still there.
        for (var i = 0; i < 3; i++) {
            Assert.NotNull((await this.StartRegistrationAsync(await this.HelloAsync("203.0.113.66"), keys: keys, url: "wss://chat.example.com/ws")).RegistrationChallenge);
        }

        Assert.NotNull((await this.StartRegistrationAsync(await this.HelloAsync("203.0.113.66"), "Debug Person", ProtocolInfo.DebugWorldName, keys, "ws://127.0.0.1:5186/ws"))
            .RegistrationChallenge);
    }

    /// <summary>
    /// A name or world with characters that aren't text (line breaks, control and format characters, line separators)
    /// is no character's, and is refused before anything is looked up or logged: a refused registration logs both, and
    /// a line break there could forge log lines.
    /// </summary>
    [Theory]
    [InlineData("Test\nPerson", World)]
    [InlineData("Test\r\n[WRN] Forged", World)]
    [InlineData("Test\u0002Person", World)]
    [InlineData("Test\u202EPerson", World)]
    [InlineData("Test\u200BPerson", World)]
    [InlineData("Test\u2028Person", World)]
    [InlineData("Test\uFFFEPerson", World)]
    [InlineData("Test Person", "Gilga\nmesh")]
    [InlineData("Test Person", "Gilga\u0085mesh")]
    [InlineData("Test\nPerson", ProtocolInfo.DebugWorldName)]
    public async Task ANameOrWorldThatIsntPlainTextIsRefusedBeforeAnythingIsLogged(string name, string world) {
        this._handler = this.NewHandler("wss://chat.example.com/ws");
        var connection = await this.HelloAsync("203.0.113.67");

        // For another server's address, which would be logged with the name and world.
        var refused = await this.StartRegistrationAsync(connection, name, world, url: "wss://evil.example/ws");
        Assert.Equal(ErrorCode.InvalidRequest, refused.Error?.Code);
        Assert.Null(connection.PendingRegistration);
        Assert.Empty(this._logs.AtLeast(Microsoft.Extensions.Logging.LogLevel.Information));
        Assert.Equal(0, this._lodestone.Requests);

        // And for this one's, which would ask the Lodestone.
        Assert.Equal(ErrorCode.InvalidRequest, (await this.StartRegistrationAsync(connection, name, world, url: "wss://chat.example.com/ws")).Error?.Code);
        Assert.Equal(0, this._lodestone.Requests);
    }

    /// <summary>
    /// Starting a registration for an address that isn't this server's costs no registration and no Lodestone request,
    /// but logs a warning, so those are counted on their own, per address (10 an hour): past that, they are refused
    /// without a warning. Refused completions count too. Other addresses, and real registrations, aren't affected.
    /// </summary>
    [Fact]
    public async Task RegistrationsForAnotherServersAddressAreLimitedPerAddress() {
        this._handler = this.NewHandler("wss://chat.example.com/ws");
        using var keys = IdentityKeys.Generate();
        var connection = await this.HelloAsync("203.0.113.68");
        for (var i = 0; i < 9; i++) {
            Assert.Equal(ErrorCode.RegistrationFailed, (await this.StartRegistrationAsync(connection, keys: keys, url: "wss://evil.example/ws")).Error?.Code);
        }

        // A completion for another address counts too: the tenth.
        var started = (await this.StartRegistrationAsync(connection, keys: keys, url: "wss://chat.example.com/ws")).RegistrationChallenge!;
        Assert.Equal(ErrorCode.RegistrationFailed, (await this.CompleteRegistrationAsync(connection, keys, started, "wss://evil.example/ws")).Error?.Code);
        Assert.Equal(10, this._logs.AtLeast(Microsoft.Extensions.Logging.LogLevel.Warning).Count);

        var limited = await this.StartRegistrationAsync(connection, keys: keys, url: "wss://evil.example/ws");
        Assert.Equal(ErrorCode.RateLimited, limited.Error?.Code);
        Assert.Equal(ErrorCode.RateLimited, (await this.CompleteRegistrationAsync(connection, keys, started, "wss://evil.example/ws")).Error?.Code);
        Assert.Equal(10, this._logs.AtLeast(Microsoft.Extensions.Logging.LogLevel.Warning).Count);

        // Another address is counted on its own.
        var other = await this.StartRegistrationAsync(await this.HelloAsync("203.0.113.69"), keys: keys, url: "wss://evil.example/ws");
        Assert.Equal(ErrorCode.RegistrationFailed, other.Error?.Code);
        Assert.Equal(11, this._logs.AtLeast(Microsoft.Extensions.Logging.LogLevel.Warning).Count);

        // The real registration started above is still there, and two more of the address's three an hour.
        for (var i = 0; i < 2; i++) {
            Assert.NotNull((await this.StartRegistrationAsync(await this.HelloAsync("203.0.113.68"), keys: keys, url: "wss://chat.example.com/ws")).RegistrationChallenge);
        }
    }

    [Fact]
    public async Task SecondAuthenticateOnAConnectionIsRejected() {
        var firstToken = await this.RegisterDebugAsync("Reauth One");
        var secondToken = await this.RegisterDebugAsync("Reauth Two");
        var firstId = RequestHandler.DebugUserId("Reauth One");

        var connection = await this.HelloAsync("203.0.113.20");
        Assert.NotNull((await this.SendAsync(connection, new ClientFrame { Authenticate = new Authenticate { DeviceToken = firstToken } })).AuthenticateOk);

        var again = await this.SendAsync(connection, new ClientFrame { Authenticate = new Authenticate { DeviceToken = secondToken } });
        Assert.Equal(ErrorCode.InvalidRequest, again.Error?.Code);
        Assert.Equal(firstId, connection.User!.UserId);
        Assert.True(this._registry.IsOnline(firstId));
        Assert.False(this._registry.IsOnline(RequestHandler.DebugUserId("Reauth Two")));
    }

    [Fact]
    public async Task RegistrationWithAnAllZeroAgreementKeyIsRejected() {
        // Validly bound to the signing key, but nothing can be sealed to it, so it would block every rekey.
        using var keys = IdentityKeys.Generate();
        var connection = await this.HelloAsync("203.0.113.40");
        var response = await this.SendAsync(connection, new ClientFrame {
            StartRegistration = new StartRegistration {
                Character = new Character { Name = "Zero Key", WorldName = ProtocolInfo.DebugWorldName },
                Identity = CryptoTests.BundleWithAgreementKey(keys, new byte[32]), ServerUrl = Url, ClientNonce = NewClientNonce(),
            },
        });

        Assert.Equal(ErrorCode.InvalidRequest, response.Error?.Code);
        Assert.Null(connection.PendingRegistration);
        Assert.Null(this._db.GetUser(RequestHandler.DebugUserId("Zero Key")));
    }

    /// <summary>
    /// v0.2 changed the wire protocol (log positions in signatures, entries on membership requests), so a
    /// 0.1 plugin, which only offers protocol version 1, must be turned away at Hello with a clear message,
    /// not let in to fail confusingly later. So must one offering version 2 only: it would take a key
    /// recovered entry (version 3) for a membership log it can't verify.
    /// </summary>
    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    public async Task HelloOfferingOnlyAnOlderProtocolVersionIsAskedToUpdate(uint version) {
        var connection = new ClientConnection(new ClosedWebSocket(), "203.0.113.50", 128 * 1024, 64, NullLogger.Instance);
        var hello = new Hello { ClientVersion = "0.1.0" };
        hello.ProtocolVersions.Add(version);

        var response = await this.SendAsync(connection, new ClientFrame { Hello = hello });

        Assert.Null(response.Welcome);
        Assert.Equal(ErrorCode.UnsupportedVersion, response.Error?.Code);
        Assert.Contains("Please update the plugin", response.Error!.Message);
        Assert.False(connection.HelloDone);
    }

    // ---------------------------------------------------------------- helpers

    private Task<Response> SendAsync(ClientConnection connection, ClientFrame frame) => this._handler.HandleAsync(connection, frame, Ct);

    /// <param name="hostHeader">Where the connection says it was made to (its Host header and scheme).</param>
    private async Task<ClientConnection> HelloAsync(string address, Core.Client.ServerOrigin? hostHeader = null) {
        var connection = new ClientConnection(new ClosedWebSocket(), address, 128 * 1024, 64, NullLogger.Instance) { RequestOrigin = hostHeader };
        var hello = new Hello();
        hello.ProtocolVersions.Add(ProtocolInfo.CurrentVersion);
        Assert.NotNull((await this.SendAsync(connection, new ClientFrame { Hello = hello })).Welcome);
        return connection;
    }

    /// <param name="keys">The identity to register (by default new keys, thrown away).</param>
    /// <param name="url">The address the client says it connected to.</param>
    /// <param name="clientNonce">The client's nonce (by default a new one).</param>
    private Task<Response> StartRegistrationAsync(ClientConnection connection, string name = "Test Person", string world = World, IdentityKeys? keys = null, string url = Url,
        byte[]? clientNonce = null) {
        using var generated = keys == null ? IdentityKeys.Generate() : null;
        return this.SendAsync(connection, new ClientFrame {
            StartRegistration = new StartRegistration {
                Character = new Character { Name = name, WorldName = world }, Identity = (keys ?? generated!).ToBundle(), ServerUrl = url,
                ClientNonce = clientNonce == null ? NewClientNonce() : Google.Protobuf.ByteString.CopyFrom(clientNonce),
            },
        });
    }

    /// <summary>Completes a registration as an honest client does: signed by <paramref name="keys"/>, for <paramref name="url"/>.</summary>
    private Task<Response> CompleteRegistrationAsync(ClientConnection connection, IdentityKeys keys, RegistrationChallenge challenge, string url = Url) {
        return this.SendAsync(connection, new ClientFrame {
            CompleteRegistration = new CompleteRegistration {
                ServerUrl = url,
                Signature = Google.Protobuf.ByteString.CopyFrom(RegistrationProof.Sign(keys, challenge.Nonce.Span, challenge.LodestoneId, url)),
            },
        });
    }

    /// <returns>A device token for a new debug account.</returns>
    private async Task<string> RegisterDebugAsync(string name) {
        var connection = await this.HelloAsync("203.0.113.30");
        using var keys = IdentityKeys.Generate();
        var challenge = (await this.StartRegistrationAsync(connection, name, ProtocolInfo.DebugWorldName, keys)).RegistrationChallenge!;
        var complete = await this.CompleteRegistrationAsync(connection, keys, challenge);
        return complete.RegistrationComplete!.DeviceToken;
    }

    /// <summary>Answers every search with "Test Person" on Gilgamesh, and every profile with <see cref="Profile"/>.</summary>
    private sealed class StubLodestone : HttpMessageHandler {
        private int _requests;
        private int _profileReads;
        private int _freshProfileReads;

        /// <summary>The profile's text (by default without any code).</summary>
        public string Profile { get; set; } = "Nothing to see here.";

        /// <summary>Whether a search finds the character (else it finds nobody, as for a typo or a character not on the Lodestone yet).</summary>
        public bool Listed { get; set; } = true;

        /// <summary>Whether the character's profile is private: its page says so, and shows no profile text.</summary>
        public bool Private { get; set; }

        /// <summary>Whether the Lodestone is down (every request answered 503).</summary>
        public bool Down { get; set; }

        /// <summary>Searches and profile reads asked for.</summary>
        public int Requests => Volatile.Read(ref this._requests);

        /// <summary>Profile reads asked for, and how many of them asked for a fresh page (no cached copy).</summary>
        public (int All, int Fresh) ProfileReads => (Volatile.Read(ref this._profileReads), Volatile.Read(ref this._freshProfileReads));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Interlocked.Increment(ref this._requests);
            if (this.Down) {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            string html;
            if (request.RequestUri!.AbsolutePath.TrimEnd('/') == "/lodestone/character") {
                html = this.Listed
                    ? $"""<a href="/lodestone/character/{LodestoneId}/" class="entry__link"><p class="entry__name">Test Person</p><p class="entry__world">{World} [Aether]</p></a>"""
                    : """<p class="parts__zero">Your search yielded no results.</p>""";
            } else {
                Interlocked.Increment(ref this._profileReads);
                if (request.Headers.CacheControl?.NoCache == true) {
                    Interlocked.Increment(ref this._freshProfileReads);
                }

                html = this.Private
                    ? """<div class="character__content"><p class="parts__zero">This character's profile is private.</p></div>"""
                    : $"""<div class="character__selfintroduction">{this.Profile}</div>""";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) });
        }
    }
}

/// <summary>A WebSocket that is already closed. Enough for a <see cref="ClientConnection"/> that is never run, or runs to an immediate close.</summary>
public sealed class ClosedWebSocket : WebSocket {
    public override WebSocketCloseStatus? CloseStatus => WebSocketCloseStatus.NormalClosure;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => WebSocketState.Closed;
    public override string? SubProtocol => null;

    public override void Abort() {
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

    public override void Dispose() {
    }

    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) {
        return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, null));
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
}
