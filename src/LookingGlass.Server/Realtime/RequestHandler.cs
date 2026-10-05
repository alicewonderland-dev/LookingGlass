using System.Net;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using LookingGlass.Core.Client;
using LookingGlass.Core.Crypto;
using LookingGlass.Core.Membership;
using LookingGlass.Core.Util;
using LookingGlass.Protocol;
using LookingGlass.Server.Data;
using LookingGlass.Server.Services;

namespace LookingGlass.Server.Realtime;

/// <summary>A request failed in a way the client should be told about.</summary>
public sealed class RequestException(ErrorCode code, string message) : Exception(message) {
    public ErrorCode Code { get; } = code;
}

/// <summary>
/// Handles every client request. Errors become typed responses; they never drop the connection.
/// Membership changes arrive as signed log entries, which are checked with the same rules clients
/// use (<see cref="IMembershipProvider"/>) before they are stored; clients never rely on that check.
/// </summary>
public sealed class RequestHandler(
    Database db,
    ConnectionRegistry registry,
    LodestoneClient lodestone,
    IOptions<ServerOptions> options,
    ILogger<RequestHandler> logger,
    IMembershipProvider membership,
    IGroupKeyProvider groupKeys,
    IHostEnvironment? environment = null,
    TimeProvider? time = null) {
    private static readonly string ServerVersion = typeof(RequestHandler).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    private static readonly TimeSpan VerifyCooldown = TimeSpan.FromSeconds(10);
    private const int MaxVerifyAttempts = 10;

    /// <summary>How long a key login challenge can be answered.</summary>
    internal static readonly TimeSpan KeyLoginChallengeLifetime = TimeSpan.FromSeconds(60);

    /// <summary>Key login challenges one connection may ask for. A client asks once on connecting, and again when the user retries.</summary>
    internal const int MaxKeyLoginChallengesPerConnection = 3;

    // The same for every failure, so a failed key login doesn't tell whether the account exists, or which check failed.
    private const string KeyLoginFailed = "Key login failed.";

    // Said to anyone asking, before looking at the account, on a server with key login off.
    private const string KeyLoginUnavailable = "Signing in with the identity key isn't available on this server.";

    // A plugin too old to register here: one that doesn't sign registrations, or can't check the Lodestone code it shows.
    private const string UpdateToRegister = "This server needs a newer version of LookingGlass to register: please update the plugin, then register again.";

    // Registering a key the account replaced, or retired with "Reset my identity": what the plugin says to do.
    private const string KeyRetired =
        "This identity key was replaced (by registering again with new keys, or \"Reset my identity\"), so it can't be registered again. " +
        "Use \"Reset my identity\" in Settings to make new keys, then register.";

    // Registering a key another account is registered with. With registrations signed, only the key's owner can get here,
    // registering a second character with one character's keys, which the plugin never does (it keeps keys per character).
    private const string KeyInUse =
        "This identity key is already registered to another character on this server, and each character needs its own. " +
        "Use \"Reset my identity\" in Settings to make new keys for this character, then register.";

    /// <summary>Why a server outside Development without <see cref="ServerOptions.PublicUrls"/> doesn't start, and what to set.</summary>
    internal const string PublicUrlsRequired =
        "LookingGlass:PublicUrls is not set, so the server won't start. Outside Development it must know every address clients connect to: " +
        "registrations, key logins and \"Reset my identity\" are signed for the address the plugin connected to, and only a listed address shows " +
        "that a signature was made for this server rather than passed on by another (a malicious server could otherwise relay a registration " +
        "made on it, and get a login to that character's account here). List each address, for example " +
        "LookingGlass__PublicUrls__0=wss://chat.example.com/ws (LookingGlass__PublicUrls__1=... for the next one), or a \"PublicUrls\" list " +
        "under \"LookingGlass\" in appsettings.json. For a private test server, run in Development instead (ASPNETCORE_ENVIRONMENT=Development).";

    /// <summary>Pending invites one user can have at once, across all channels.</summary>
    public const int MaxPendingInvitesPerUser = 20;

    // Channel names are at most 64 UTF-8 bytes; sealing adds a 16-byte tag. The cap keeps
    // invites (which strangers can send) from bloating the invitee's channel list.
    private const int MaxSealedNameBytes = 128;

    private readonly WindowCounter _registrations = new(options.Value.Limits.RegistrationsPerHourPerIp, TimeSpan.FromHours(1));
    // Registrations refused for naming an address that isn't this server's, when starting or completing. They cost no
    // registration and no Lodestone request (the honest client that names one is misconfigured, and is told what to
    // set), but each logs a warning, so they are counted on their own; past the limit they are refused unlogged.
    private readonly WindowCounter _refusedRegistrations = new(options.Value.Limits.RefusedRegistrationsPerHourPerIp, TimeSpan.FromHours(1));
    // Key login: challenges per address; failures per address, where a challenge counts as one from when it is issued
    // until it is answered correctly (so asking and never answering is limited like failing); and failed answers per
    // account and address. Each challenge allows one attempt, so these bound attempts per connection too.
    //
    // Nothing is counted per account alone. An Ed25519 signature can't be guessed, so limiting failures only keeps
    // addresses from spamming attempts (each costs a signature check and a log line), and every address is limited on
    // its own; a limit per account, by anyone, only let enough addresses together lock the owner out of their own
    // key login. Per account and address, only failed answers count, never challenges or successes (anyone can ask
    // for challenges for any account), and the allowance is half the address's (rounded up), so one address can't
    // spend all of its failures on one account. Both counters drop keys whose events have expired, so memory stays
    // bounded by the addresses (and accounts per address) seen within the hour.
    private readonly WindowCounter _keyLoginsPerIp = new(options.Value.Limits.KeyLoginsPerHourPerIp, TimeSpan.FromHours(1));
    private readonly WindowCounter _keyLoginFailuresPerIp = new(options.Value.Limits.KeyLoginFailuresPerHourPerIp, TimeSpan.FromHours(1));
    private readonly WindowCounter _keyLoginFailuresPerAccountAndIp = new(
        Math.Max(1, (options.Value.Limits.KeyLoginFailuresPerHourPerIp + 1) / 2), TimeSpan.FromHours(1));
    private readonly IReadOnlyList<ServerOrigin> _publicOrigins = ParsePublicUrls(options.Value.PublicUrls);
    private readonly string[] _advertisedUrls = (options.Value.PublicUrls ?? []).Where(url => !string.IsNullOrWhiteSpace(url)).Select(url => url.Trim()).Distinct().ToArray();
    private readonly KeyLoginOrigins _keyLoginOrigins =ChooseKeyLoginOrigins(ParsePublicUrls(options.Value.PublicUrls), environment?.IsDevelopment() == true);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly UserRateLimits _rekeys = new(perSecond: 0.5, burst: 5);
    private readonly UserRateLimits _lookups = new(perSecond: 0.5, burst: 10);
    private readonly UserRateLimits _messages = new(ProtocolInfo.DefaultLimits().MessagesPerSecond, ProtocolInfo.DefaultLimits().MessageBurst);
    // Invites are limited on both ends: an inviter can't spam many people, and many
    // inviters (or invite, cancel, invite loops) can't flood one person.
    private readonly UserRateLimits _invitesSent = new(perSecond: 1.0 / 15, burst: 20);
    private readonly UserRateLimits _invitesReceived = new(perSecond: 1.0 / 30, burst: 10);
    private readonly UserRateLimits _creates = new(perSecond: 1.0 / 60, burst: 10);
    private readonly UserRateLimits _renames = new(perSecond: 0.1, burst: 10);
    private readonly UserRateLimits _disbands = new(perSecond: 1.0 / 60, burst: 5);
    // One budget for the requests that read a lot from the database. A client needs about
    // two per channel when it connects, so the burst covers a full channel list.
    private readonly UserRateLimits _reads = new(perSecond: 4, burst: 120);

    public Limits Limits { get; } = BuildLimits(options.Value);

    /// <summary>
    /// Runs after a last member's leave has been checked and before their channel is deleted, so tests can
    /// have someone else's request land in between, as a concurrent one could.
    /// </summary>
    internal Action? BeforeAbandonedChannelDeletedForTests { get; set; }

    /// <summary>
    /// Runs once, after a key login has been checked and before its device is added, so tests can have the account
    /// register again with new keys in between, as a concurrent registration could.
    /// </summary>
    internal Action? BeforeKeyLoginDeviceAddedForTests { get; set; }

    /// <summary>
    /// Runs once, after a login (Authenticate) has gone online and before it is checked again, so tests can have the
    /// account's devices revoked only now, as a concurrent retirement or registration could have done just before the
    /// connection went online (its disconnect then finding nothing to close). Only a check made after going online sees it.
    /// </summary>
    internal Action? AfterAuthenticateSetOnlineForTests { get; set; }

    /// <summary>
    /// Runs once, after a retirement has been checked and before the key is retired, so tests can have another connection
    /// of the account log in meanwhile.
    /// </summary>
    internal Action? BeforeIdentityRetiredForTests { get; set; }

    public async Task<Response> HandleAsync(ClientConnection connection, ClientFrame frame, CancellationToken ct) {
        try {
            if (!connection.HelloDone && frame.BodyCase != ClientFrame.BodyOneofCase.Hello) {
                throw new RequestException(ErrorCode.InvalidRequest, "Send Hello first.");
            }

            // The one list of what an anonymous connection may do; every handler of the rest also requires a user.
            if (connection.User == null && !Policy.AllowedBeforeLogin(frame.BodyCase)) {
                throw new RequestException(ErrorCode.NotAuthenticated, "Log in first.");
            }

            return frame.BodyCase switch {
                ClientFrame.BodyOneofCase.Hello => this.Hello(connection, frame.Hello),
                ClientFrame.BodyOneofCase.Ping => new Response { Pong = new Pong { ServerTimeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() } },
                ClientFrame.BodyOneofCase.StartRegistration => await this.StartRegistration(connection, frame.StartRegistration, ct),
                ClientFrame.BodyOneofCase.CompleteRegistration => await this.CompleteRegistration(connection, frame.CompleteRegistration, ct),
                ClientFrame.BodyOneofCase.Authenticate => this.Authenticate(connection, frame.Authenticate),
                ClientFrame.BodyOneofCase.StartKeyLogin => this.StartKeyLogin(connection, frame.StartKeyLogin),
                ClientFrame.BodyOneofCase.CompleteKeyLogin => this.CompleteKeyLogin(connection, frame.CompleteKeyLogin),
                ClientFrame.BodyOneofCase.RetireIdentity => this.RetireIdentity(connection, frame.RetireIdentity),
                ClientFrame.BodyOneofCase.GetIdentities => this.GetIdentities(connection, frame.GetIdentities),
                ClientFrame.BodyOneofCase.LookupUser => this.LookupUser(connection, frame.LookupUser),
                ClientFrame.BodyOneofCase.ListChannels => this.ListChannels(connection, frame.ListChannels),
                ClientFrame.BodyOneofCase.FetchMembershipLog => this.FetchMembershipLog(connection, frame.FetchMembershipLog),
                ClientFrame.BodyOneofCase.CreateChannel => this.CreateChannel(connection, frame.CreateChannel),
                ClientFrame.BodyOneofCase.InviteMember => this.InviteMember(connection, frame.InviteMember),
                ClientFrame.BodyOneofCase.RespondToInvite => this.RespondToInvite(connection, frame.RespondToInvite),
                ClientFrame.BodyOneofCase.LeaveChannel => this.LeaveChannel(connection, frame.LeaveChannel),
                ClientFrame.BodyOneofCase.KickMember => this.KickMember(connection, frame.KickMember),
                ClientFrame.BodyOneofCase.SetMemberRank => this.SetMemberRank(connection, frame.SetMemberRank),
                ClientFrame.BodyOneofCase.DisbandChannel => this.DisbandChannel(connection, frame.DisbandChannel),
                ClientFrame.BodyOneofCase.RenameChannel => this.RenameChannel(connection, frame.RenameChannel),
                ClientFrame.BodyOneofCase.SubmitRekey => this.SubmitRekey(connection, frame.SubmitRekey),
                ClientFrame.BodyOneofCase.FetchEpochKeys => this.FetchEpochKeys(connection, frame.FetchEpochKeys),
                ClientFrame.BodyOneofCase.SendMessage => this.SendMessage(connection, frame.SendMessage),
                _ => throw new RequestException(ErrorCode.InvalidRequest, "Unknown request."),
            };
        } catch (RequestException ex) {
            return Error(ex.Code, ex.Message);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            logger.LogError(ex, "Request {Kind} failed", frame.BodyCase);
            return Error(ErrorCode.Internal, "Internal server error.");
        }
    }

    private static Limits BuildLimits(ServerOptions options) {
        var limits = ProtocolInfo.DefaultLimits();
        // Advertised so clients split big identity lookups to fit, instead of failing on connect.
        limits.MaxIdentitiesPerRequest = (uint) Math.Max(0, options.Limits.MaxIdentitiesPerRequest);
        return limits;
    }

    // ================================================================ handshake and identity

    private Response Hello(ClientConnection connection, Hello hello) {
        if (!hello.ProtocolVersions.Contains(ProtocolInfo.CurrentVersion)) {
            throw new RequestException(ErrorCode.UnsupportedVersion, $"This server speaks protocol version {ProtocolInfo.CurrentVersion}. Please update the plugin.");
        }

        connection.HelloDone = true;
        var welcome = new Welcome {
            ProtocolVersion = ProtocolInfo.CurrentVersion,
            ServerVersion = ServerVersion,
            Limits = this.Limits,
            Announcement = options.Value.Announcement ?? "",
            DebugAccountsEnabled = options.Value.Dev.AllowDebugAccounts,
        };
        welcome.Capabilities.AddRange(hello.Capabilities.Where(capability => capability == ProtocolInfo.Capabilities.Chat));
        // The operator's own addresses, which clients moving to one of them trust because this server, at the address
        // they already use, lists it. Never the Host-header fallback: that's whatever the connecting side said.
        welcome.PublicUrls.AddRange(this._advertisedUrls);
        return new Response { Welcome = welcome };
    }

    private async Task<Response> StartRegistration(ClientConnection connection, StartRegistration request, CancellationToken ct) {
        var character = request.Character ?? throw new RequestException(ErrorCode.InvalidRequest, "Missing character.");
        var name = character.Name.Trim();
        var worldName = character.WorldName.Trim();
        // Before anything is logged: they are, as the client sent them, when the registration is refused.
        if (name.Length is 0 or > 32 || worldName.Length is 0 or > 32 || !TextSanitizer.IsPlain(name) || !TextSanitizer.IsPlain(worldName)) {
            throw new RequestException(ErrorCode.InvalidRequest, "Invalid character name or world.");
        }

        if (!IdentityKeys.IsValidBundle(request.Identity)) {
            throw new RequestException(ErrorCode.InvalidRequest, "Invalid identity keys.");
        }

        if (string.IsNullOrEmpty(request.ServerUrl)) {
            // A plugin from before codes were derived from the server's address, which can't check the code it shows.
            throw new RequestException(ErrorCode.RegistrationFailed, UpdateToRegister);
        }

        var minutes = options.Value.Lodestone.ChallengeMinutes;
        if (ProtocolInfo.IsDebugWorld(worldName)) {
            if (!options.Value.Dev.AllowDebugAccounts) {
                throw new RequestException(ErrorCode.RegistrationFailed, "Debug accounts are disabled on this server.");
            }

            this.CheckKeyRegistrable(DebugUserId(name), request.Identity);
            connection.PendingRegistration = new PendingRegistration(
                DebugUserId(name), name, 0, ProtocolInfo.DebugWorldName, request.Identity, "", DateTimeOffset.UtcNow.AddMinutes(minutes), true, NewRegistrationNonce());
            return new Response {
                RegistrationChallenge = new RegistrationChallenge {
                    Code = "",
                    ExpiresUnix = connection.PendingRegistration.Expires.ToUnixTimeSeconds(),
                    LodestoneId = connection.PendingRegistration.UserId,
                    VerificationSkipped = true,
                    Nonce = ByteString.CopyFrom(connection.PendingRegistration.Nonce),
                },
            };
        }

        // The code is made for this address, and the client only accepts a code made for the address it connected to: an
        // address that isn't this server's is what a malicious server passing this server's code on to its users would
        // name (to have them accept it). Checked as when completing, before anything is counted or looked up.
        if (this.NotThisServer(connection, request.ServerUrl) is { } elsewhere) {
            this.CountRefusedRegistration(connection);
            logger.LogWarning("Registration of {Name} on {World} from {Address} refused when starting: {Reason}", name, worldName, connection.RemoteAddress, elsewhere);
            throw new RequestException(ErrorCode.RegistrationFailed,
                this.WrongAddressMessage(connection, request.ServerUrl) +
                " Nothing was started: set the server address in Settings to one this server accepts, then register again.");
        }

        // NotThisServer accepted it, so it parses.
        var origin = ServerOrigin.FromUrl(request.ServerUrl)!;

        if (!this._registrations.TryAdd(connection.RemoteAddress)) {
            throw new RequestException(ErrorCode.RateLimited, "Too many registration attempts; try again later.");
        }

        LodestoneCharacter? found;
        try {
            found = await lodestone.FindCharacterAsync(name, worldName, ct);
        } catch (LodestoneUnavailableException) {
            throw new RequestException(ErrorCode.RegistrationFailed, "The Lodestone isn't responding right now; try again in a few minutes.");
        } catch (LodestoneBusyException) {
            throw new RequestException(ErrorCode.RateLimited, "The server is busy checking other characters; try again in a minute.");
        }

        if (found == null) {
            throw new RequestException(ErrorCode.RegistrationFailed, $"Couldn't find {name} on {worldName} in the Lodestone.");
        }

        // Once the account is known (the lookup is cached, so asking again costs nothing).
        this.CheckKeyRegistrable(found.Id, request.Identity);
        // For this address, the key this registration is for (only it can complete it: see CheckRegistrationProof), a fresh
        // nonce and the character, so the client can check it was made for its own server and key before showing it.
        var nonce = NewRegistrationNonce();
        var code = LodestoneCode.Derive(origin, request.Identity.SigningPublicKey.Span, nonce, found.Id);
        connection.PendingRegistration = new PendingRegistration(
            found.Id, found.Name, character.WorldId, found.WorldName, request.Identity, code, DateTimeOffset.UtcNow.AddMinutes(minutes), false, nonce);
        connection.VerifyAttempts = 0;

        return new Response {
            RegistrationChallenge = new RegistrationChallenge {
                Code = code,
                ExpiresUnix = connection.PendingRegistration.Expires.ToUnixTimeSeconds(),
                LodestoneId = found.Id,
                Nonce = ByteString.CopyFrom(connection.PendingRegistration.Nonce),
            },
        };
    }

    /// <summary>Counts a registration refused for its address (see <see cref="_refusedRegistrations"/>), before it is logged.</summary>
    /// <exception cref="RequestException">Too many from this address: refused without a warning.</exception>
    private void CountRefusedRegistration(ClientConnection connection) {
        if (!this._refusedRegistrations.TryAdd(connection.RemoteAddress)) {
            throw new RequestException(ErrorCode.RateLimited,
                "Too many registration attempts for addresses this server doesn't accept; set the server address in Settings to one it accepts, and try again later.");
        }
    }

    private static byte[] NewRegistrationNonce() => RandomNumberGenerator.GetBytes(RegistrationProof.NonceSize);

    /// <summary>
    /// Refuses, when registering starts, a key the account replaced or retired, or one another account is registered
    /// with, so the user is told before putting a code in their profile. Checked again when it completes (see
    /// <see cref="Database.RegisterUser"/>), where it counts.
    /// </summary>
    private void CheckKeyRegistrable(long userId, IdentityBundle identity) {
        var signing = identity.SigningPublicKey.ToByteArray();
        if (db.IsKeyRetired(userId, signing)) {
            throw new RequestException(ErrorCode.RegistrationFailed, KeyRetired);
        }

        if (db.IsKeyInUseByAnother(userId, signing)) {
            throw new RequestException(ErrorCode.RegistrationFailed, KeyInUse);
        }
    }

    private async Task<Response> CompleteRegistration(ClientConnection connection, CompleteRegistration request, CancellationToken ct) {
        var pending = connection.PendingRegistration
            ?? throw new RequestException(ErrorCode.RegistrationFailed, "Start registration on this connection first.");

        if (pending.Expires < DateTimeOffset.UtcNow) {
            connection.PendingRegistration = null;
            throw new RequestException(ErrorCode.RegistrationFailed, "The challenge expired; start again.");
        }

        // Before asking the Lodestone, and before anything is stored: the client holds the key it registers.
        this.CheckRegistrationProof(connection, pending, request);

        if (!pending.IsDebug) {
            // Each attempt costs a Lodestone request, which is shared by the whole server.
            var sinceLast = DateTimeOffset.UtcNow - connection.LastVerifyAttempt;
            if (sinceLast < VerifyCooldown) {
                throw new RequestException(ErrorCode.RateLimited, $"Wait {Math.Ceiling((VerifyCooldown - sinceLast).TotalSeconds):0} seconds before verifying again.");
            }

            if (connection.VerifyAttempts >= MaxVerifyAttempts) {
                connection.PendingRegistration = null;
                throw new RequestException(ErrorCode.RateLimited, "Too many verification attempts; start registration again.");
            }

            connection.VerifyAttempts++;
            connection.LastVerifyAttempt = DateTimeOffset.UtcNow;
            ProfileCheck check;
            try {
                check = await lodestone.ProfileContainsAsync(pending.UserId, pending.Code, ct);
            } catch (LodestoneBusyException) {
                // Not the user's fault: don't count the attempt.
                connection.VerifyAttempts--;
                throw new RequestException(ErrorCode.RateLimited, "The server is busy checking other characters; try again in a minute.");
            }

            switch (check) {
                case ProfileCheck.ProfileUnavailable:
                    throw new RequestException(ErrorCode.RegistrationFailed, "Couldn't read your Lodestone profile. Is it public?");
                case ProfileCheck.CodeNotFound:
                    // Without the code: the client knows it, and shows no code a server writes into its words (see LodestoneCode.Redact).
                    throw new RequestException(ErrorCode.RegistrationFailed, "Your code isn't in your Lodestone profile yet. The Lodestone can take a minute to update.");
            }
        }

        connection.PendingRegistration = null;
        // New keys don't change any channel's members (the log binds them to the old ones), so nothing needs a rekey.
        UserRow user;
        try {
            (user, _) = db.RegisterUser(pending.UserId, pending.Name, pending.WorldId, pending.WorldName, pending.Identity, pending.IsDebug);
        } catch (KeyRetiredException) {
            // Replaced or retired since this registration started.
            throw new RequestException(ErrorCode.RegistrationFailed, KeyRetired);
        } catch (KeyInUseException) {
            // Registered by another account since this registration started.
            throw new RequestException(ErrorCode.RegistrationFailed, KeyInUse);
        }

        // Only while the key just registered is still the account's, and not retired: a retirement or another
        // registration landing in between revokes every device, and this one mustn't outlive that.
        var token = NewDeviceToken();
        if (!db.AddDeviceForKey(user.UserId, user.SigningKey, user.KeyVersion, HashToken(token))) {
            throw new RequestException(ErrorCode.RegistrationFailed, "This character's keys changed while registering; start again.");
        }

        // Old devices were revoked; drop any session still using one.
        registry.Disconnect(user.UserId, "This character registered again");
        logger.LogInformation("Registered {User} ({Kind})", user.UserId, pending.IsDebug ? "debug" : "verified");
        return new Response { RegistrationComplete = new RegistrationComplete { DeviceToken = token, User = user.ToProto() } };
    }

    /// <summary>
    /// Checks that a registration is signed by the identity key it registers, over this connection's nonce, the account
    /// and this server's address (see <see cref="RegistrationProof"/>). Anyone can fetch a user's public identity bundle,
    /// binding signature and all; without this, someone could register another user's key for their own character, then
    /// register again with new keys and so have it retired. The address is checked for registrations through the Lodestone,
    /// as for key login, so a malicious server can't relay this server's challenge to its users and register their keys
    /// here, receiving the login this hands out (the plugin's separate keys per address don't stop that: registering
    /// registers whatever key was signed with); a debug account proves nothing about who registers it anyway (anyone may register any name), and the echo bot
    /// connects to a local address that PublicUrls don't list. The key is the one the Lodestone code was derived from
    /// when registering started (<see cref="PendingRegistration.Identity"/>), so a registration completes only for the
    /// key its code was issued for.
    /// </summary>
    /// <exception cref="RequestException">Not signed, or not like that. The registration can still be completed.</exception>
    private void CheckRegistrationProof(ClientConnection connection, PendingRegistration pending, CompleteRegistration request) {
        if (request.Signature.IsEmpty) {
            // A plugin from before registrations were signed.
            throw new RequestException(ErrorCode.RegistrationFailed, UpdateToRegister);
        }

        if (!pending.IsDebug && this.NotThisServer(connection, request.ServerUrl) is { } elsewhere) {
            this.CountRefusedRegistration(connection);
            logger.LogWarning("Registration of {User} from {Address} refused: {Reason}", pending.UserId, connection.RemoteAddress, elsewhere);
            throw new RequestException(ErrorCode.RegistrationFailed,
                this.WrongAddressMessage(connection, request.ServerUrl) +
                " Nothing was registered: set the server address in Settings to one this server accepts, then register again.");
        }

        if (!RegistrationProof.Verify(pending.Identity.SigningPublicKey.Span, pending.Nonce, pending.UserId, request.ServerUrl, request.Signature.Span)) {
            logger.LogInformation("Registration of {User} from {Address} refused: not signed by the key being registered", pending.UserId, connection.RemoteAddress);
            throw new RequestException(ErrorCode.RegistrationFailed,
                "That registration isn't signed with the identity key being registered on this connection, so nothing was registered.");
        }
    }

    private Response Authenticate(ClientConnection connection, Authenticate request) {
        if (connection.User != null) {
            // Switching users would leave the first one registered as online on this connection.
            throw new RequestException(ErrorCode.InvalidRequest, "Already logged in on this connection.");
        }

        var tokenHash = string.IsNullOrEmpty(request.DeviceToken) ? null : HashToken(request.DeviceToken);
        var userId = tokenHash == null ? null : db.FindDevice(tokenHash);
        var user = userId == null ? null : db.GetUser(userId.Value);
        // A retired key has no devices (retiring deletes them, and none are added for it); checked here too, so that holds
        // whatever adds a device.
        if (user == null || db.IsKeyRetired(user.UserId, user.SigningKey)) {
            throw new RequestException(ErrorCode.NotAuthenticated, "Unknown or revoked device token.");
        }

        if (user.IsDebug && !options.Value.Dev.AllowDebugAccounts) {
            throw new RequestException(ErrorCode.NotAuthenticated, "Debug accounts are disabled on this server.");
        }

        connection.User = user;
        connection.DeviceTokenHash = tokenHash;
        registry.SetOnline(user.UserId, connection);

        if (this.AfterAuthenticateSetOnlineForTests is { } hook) {
            this.AfterAuthenticateSetOnlineForTests = null;
            hook();
        }

        // A retirement or registration (each revokes every device, then disconnects the account) that landed after the
        // checks above, and disconnected the account before this connection was online, would leave it logged in with a
        // deleted login. Checked again now that it is online: anything revoking the login from here on disconnects it.
        var current = db.FindDevice(tokenHash!) == user.UserId ? db.GetUser(user.UserId) : null;
        if (current == null || db.IsKeyRetired(current.UserId, current.SigningKey)) {
            connection.User = null;
            connection.DeviceTokenHash = null;
            registry.SetOffline(user.UserId, connection);
            throw new RequestException(ErrorCode.NotAuthenticated, "Unknown or revoked device token.");
        }

        return new Response { AuthenticateOk = new AuthenticateOk { User = user.ToProto(), KeyVersion = user.KeyVersion } };
    }

    /// <summary>
    /// The first half of a key login: a fresh challenge for this connection to sign. Issued whether or not the account
    /// exists, so asking doesn't tell who is registered. Limited per connection, address and account. Refused for
    /// everyone on a server that has no address it can check signatures against (see <see cref="KeyLoginOrigins"/>).
    /// </summary>
    private Response StartKeyLogin(ClientConnection connection, StartKeyLogin request) {
        if (connection.User != null) {
            throw new RequestException(ErrorCode.InvalidRequest, "Already logged in on this connection.");
        }

        if (this._keyLoginOrigins == KeyLoginOrigins.Off) {
            throw new RequestException(ErrorCode.NotAuthenticated, KeyLoginUnavailable);
        }

        if (connection.KeyLoginChallenges >= MaxKeyLoginChallengesPerConnection) {
            throw new RequestException(ErrorCode.RateLimited, "Too many key login attempts on this connection.");
        }

        var address = connection.RemoteAddress;
        if (this._keyLoginFailuresPerIp.IsFull(address) || this._keyLoginsPerIp.IsFull(address)) {
            logger.LogDebug("Key login from {Address} refused: too many from this address", address);
            throw new RequestException(ErrorCode.RateLimited, "Too many key login attempts; try again later.");
        }

        // Checked, not counted: only a failed answer uses up this address's allowance for the account.
        if (this._keyLoginFailuresPerAccountAndIp.IsFull(AccountAndAddress(request.UserId, address))) {
            logger.LogDebug("Key login for {User} from {Address} refused: too many failures for this account from this address", request.UserId, address);
            throw new RequestException(ErrorCode.RateLimited, "Too many failed key logins for this account from your address; try again later.");
        }

        // The challenge, and (until it is answered correctly) a failure. Both checked above, but a request on another
        // connection from the same address may have counted meanwhile.
        if (!this._keyLoginsPerIp.TryAdd(address)) {
            throw new RequestException(ErrorCode.RateLimited, "Too many key login attempts; try again later.");
        }

        if (!this._keyLoginFailuresPerIp.TryAdd(address)) {
            this._keyLoginsPerIp.Refund(address);
            throw new RequestException(ErrorCode.RateLimited, "Too many key login attempts; try again later.");
        }

        // A challenge this one replaces stays counted as a failure for the address: it was never answered.
        connection.KeyLoginChallenges++;
        var challenge = RandomNumberGenerator.GetBytes(KeyLoginProof.ChallengeSize);
        var expires = this._time.GetUtcNow() + KeyLoginChallengeLifetime;
        // Replaces any earlier one on this connection, which can't be answered any more.
        connection.PendingKeyLogin = new PendingKeyLogin(request.UserId, challenge, expires);
        return new Response {
            KeyLoginChallenge = new KeyLoginChallenge { Challenge = ByteString.CopyFrom(challenge), ExpiresUnix = expires.ToUnixTimeSeconds() },
        };
    }

    /// <summary>
    /// The second half of a key login: if this connection's challenge was signed, for this server's address, by the
    /// account's current identity key, a new device token for it. The account's other devices keep theirs. Like
    /// registration, it doesn't log the connection in; the client sends Authenticate with the token next.
    /// </summary>
    private Response CompleteKeyLogin(ClientConnection connection, CompleteKeyLogin request) {
        if (connection.User != null) {
            throw new RequestException(ErrorCode.InvalidRequest, "Already logged in on this connection.");
        }

        // Single use: whatever happens next, this challenge can't be answered again.
        var pending = connection.PendingKeyLogin;
        connection.PendingKeyLogin = null;

        var refusal = this.CheckKeyLogin(connection, pending, request, out var user, out var wrongAddress);
        string? token = null;
        if (refusal == null) {
            token = NewDeviceToken();
            if (this.BeforeKeyLoginDeviceAddedForTests is { } hook) {
                this.BeforeKeyLoginDeviceAddedForTests = null;
                hook();
            }

            // Only while the key that signed is still the account's: registering again with new keys revokes every
            // device, and must not be undone by a key login checked just before it.
            if (!db.AddDeviceForKey(user!.UserId, user.SigningKey, user.KeyVersion, HashToken(token))) {
                refusal = "the account's keys changed meanwhile";
            }
        }

        if (refusal != null) {
            if (pending == null) {
                // An answer without a challenge: nothing was counted for it yet.
                this._keyLoginFailuresPerIp.TryAdd(connection.RemoteAddress);
            } else {
                // The address's failure was counted with the challenge; this is the one for the account from this address.
                this._keyLoginFailuresPerAccountAndIp.TryAdd(AccountAndAddress(pending.UserId, connection.RemoteAddress));
            }

            // Signed for an address that isn't listed: most likely a client set up with an address the operator should list.
            logger.Log(wrongAddress ? LogLevel.Warning : LogLevel.Information,
                "Key login for {User} from {Address} refused: {Reason}", pending?.UserId, connection.RemoteAddress, refusal);
            throw new RequestException(ErrorCode.NotAuthenticated, KeyLoginFailed);
        }

        // Answered correctly: the failure counted for the address when the challenge was issued didn't happen.
        this._keyLoginFailuresPerIp.Refund(connection.RemoteAddress);
        logger.LogInformation("Key login for {User} from {Address}: new device", user!.UserId, connection.RemoteAddress);
        return new Response { KeyLoginComplete = new KeyLoginComplete { DeviceToken = token, User = user.ToProto() } };
    }

    /// <returns>Why the key login is refused (for the log only, never the client), or null if it may go ahead.</returns>
    /// <param name="wrongAddress">Refused because it was signed for an address that isn't this server's.</param>
    private string? CheckKeyLogin(ClientConnection connection, PendingKeyLogin? pending, CompleteKeyLogin request, out UserRow? user, out bool wrongAddress) {
        user = null;
        wrongAddress = false;
        if (pending == null) {
            return "no challenge on this connection";
        }

        if (request.Challenge.Length != pending.Challenge.Length || !CryptographicOperations.FixedTimeEquals(request.Challenge.Span, pending.Challenge)) {
            return "not the challenge issued on this connection";
        }

        if (this._time.GetUtcNow() >= pending.Expires) {
            return "the challenge expired";
        }

        // Before the signature: a signature made for another server is what a relay would bring.
        if (this.NotThisServer(connection, request.ServerUrl) is { } elsewhere) {
            wrongAddress = true;
            return elsewhere;
        }

        user = db.GetUser(pending.UserId);
        // Looked up for unknown accounts too (a key nobody holds is never retired), so the work doesn't tell them apart.
        var retired = db.IsKeyRetired(pending.UserId, user?.SigningKey ?? NobodysKey);
        var refused = user == null ? "no such account"
            : user.IsDebug && !options.Value.Dev.AllowDebugAccounts ? "debug accounts are disabled"
            : retired ? "the account's key was retired"
            : null;

        // Against the account's current key only: keys replaced by registering again can't sign in, and neither can a
        // current one that was retired ("Reset my identity"). An account that doesn't exist, or may not sign in, is
        // checked against a key nobody holds, so every answer that gets this far costs one verification and the time a
        // failure takes doesn't tell whether the account exists.
        var valid = KeyLoginProof.Verify(refused == null ? user!.SigningKey : NobodysKey, pending.Challenge, pending.UserId, request.ServerUrl, request.Signature.Span);
        Interlocked.Increment(ref this._keyLoginSignatureChecks);
        if (refused != null) {
            user = null;
            return refused;
        }

        return valid ? null : "the signature isn't by the account's identity key";
    }

    /// <summary>
    /// "Reset my identity", sent first while logged in: retires the account's current identity key and revokes every
    /// login of the account (see <see cref="Database.RetireIdentity"/>), this connection's included, which is logged
    /// out. The account then has no working login until new keys are registered through the Lodestone, as one whose
    /// logins were all lost; others see the old key until then, as before any registration.
    ///
    /// Signed by the key being retired, over this connection's login and this server's address, checked as for key login
    /// (see <see cref="RetireIdentityProof"/> and <see cref="NotThisServer"/>): a login
    /// alone, which a thief may hold without the key, could otherwise wreck the owner's identity. Anyone with both could
    /// already act as the owner; retiring is then what the owner wants anyway. No rate limit beyond that: it can succeed
    /// once per key, since the key can't sign in again and a new one only comes from registering through the Lodestone.
    /// </summary>
    private Response RetireIdentity(ClientConnection connection, RetireIdentity request) {
        var me = RequireUser(connection);
        // The account as it is now: it may have registered new keys since this connection logged in.
        var user = db.GetUser(me.UserId);
        if (user == null || connection.DeviceTokenHash is not { } tokenHash) {
            throw new RequestException(ErrorCode.NotAuthenticated, "Log in first.");
        }

        // Before the signature, as for key login: one made for another server is what a relay would bring.
        if (this.NotThisServer(connection, request.ServerUrl) is { } elsewhere) {
            logger.LogWarning("Retiring the identity key of {User} from {Address} refused: {Reason}", user.UserId, connection.RemoteAddress, elsewhere);
            throw new RequestException(ErrorCode.Forbidden,
                this.WrongAddressMessage(connection, request.ServerUrl) + " Nothing was retired: connect through an address this server accepts, then reset again.");
        }

        if (!RetireIdentityProof.Verify(user.SigningKey, user.UserId, tokenHash, request.ServerUrl, request.Signature.Span)) {
            throw new RequestException(ErrorCode.Forbidden, "That isn't signed with this account's identity key for this login, so nothing was retired.");
        }

        if (this.BeforeIdentityRetiredForTests is { } hook) {
            this.BeforeIdentityRetiredForTests = null;
            hook();
        }

        if (!db.RetireIdentity(user.UserId, user.SigningKey, user.KeyVersion)) {
            throw new RequestException(ErrorCode.Conflict, "This account's keys changed meanwhile, so nothing was retired.");
        }

        // This connection's login was one of the devices just revoked.
        connection.User = null;
        connection.DeviceTokenHash = null;
        registry.SetOffline(user.UserId, connection);
        // And any other connection that logged in meanwhile (only one is online per user).
        registry.Disconnect(user.UserId, "This character's identity was retired");
        logger.LogInformation("Retired the identity key of {User}", user.UserId);
        return new Response { Ack = new Ack() };
    }

    /// <summary>The key failed key logins are counted under for one account from one address.</summary>
    private static string AccountAndAddress(long userId, string address) => $"{userId} {address}";

    // A valid Ed25519 public key whose private key was thrown away when the server started.
    private static readonly byte[] NobodysKey = MakeNobodysKey();

    private static byte[] MakeNobodysKey() {
        using var keys = IdentityKeys.Generate();
        return keys.SigningPublicKey;
    }

    private int _keyLoginSignatureChecks;

    /// <summary>Key login signatures verified so far, for tests that every answer costs the same.</summary>
    internal int KeyLoginSignatureChecks => Volatile.Read(ref this._keyLoginSignatureChecks);

    /// <summary>
    /// Which server addresses a key login signature may name; registrations through the Lodestone, and retiring a key, go
    /// by the same (see <see cref="NotThisServer"/>).
    /// </summary>
    internal enum KeyLoginOrigins {
        /// <summary>The configured <see cref="ServerOptions.PublicUrls"/>: the operator says which addresses are this server's.</summary>
        PublicUrls,

        /// <summary>
        /// None configured, in Development only: the scheme and Host header each connection was made with. Whoever opens
        /// the connection chooses the Host header, so this stops nothing a relaying server does on purpose (it sends the
        /// address the user signed for); only the plugin keeping separate keys per server address does. For private test
        /// servers only.
        /// </summary>
        HostHeader,

        /// <summary>
        /// None configured, outside Development: there is no address to trust. The server refuses to start like this (see
        /// <see cref="PublicUrlsRequired"/>); a handler made so anyway accepts no signed address, so key login, and
        /// registering and retiring keys other than debug accounts', are refused.
        /// </summary>
        Off,
    }

    /// <summary>
    /// Checks the server address a client signed (for key login, registration, or retiring its key) against this server's
    /// (see <see cref="KeyLoginOrigins"/>): a signature made for another server is what a relaying server would bring.
    /// With no address to check against (<see cref="KeyLoginOrigins.Off"/>), none is this server's.
    /// </summary>
    /// <returns>Why the address isn't this server's (for the log), or null if it is.</returns>
    private string? NotThisServer(ClientConnection connection, string signedUrl) {
        var signed = ServerOrigin.FromUrl(signedUrl);
        var ours = this._keyLoginOrigins switch {
            KeyLoginOrigins.PublicUrls => signed != null && this._publicOrigins.Contains(signed),
            KeyLoginOrigins.HostHeader => signed != null && signed == connection.RequestOrigin,
            _ => false,
        };
        return ours ? null
            : $"signed for {signed?.ToString() ?? "an invalid address"}, which isn't this server ("
              + this._keyLoginOrigins switch {
                  KeyLoginOrigins.PublicUrls => string.Join(", ", this._publicOrigins),
                  KeyLoginOrigins.HostHeader => $"Host header: {connection.RequestOrigin?.ToString() ?? "unknown address"}",
                  _ => "no PublicUrls",
              } + ")";
    }

    /// <summary>
    /// What the client is told when <see cref="NotThisServer"/> refuses the address it signed for: that address, and the
    /// ones this server accepts (Welcome lists them to anyone anyway), so the user knows what to set.
    /// </summary>
    private string WrongAddressMessage(ClientConnection connection, string signedUrl) {
        // The client's own string, sent back to it; shortened and without control characters, as it is displayed.
        var used = new string(signedUrl.Trim().Where(c => !char.IsControl(c)).Take(200).ToArray());
        used = used.Length == 0 ? "(none)" : used;
        return this._keyLoginOrigins switch {
            KeyLoginOrigins.PublicUrls => $"This server doesn't accept the address {used}. Use one of: {string.Join(", ", this._advertisedUrls)}.",
            KeyLoginOrigins.HostHeader =>
                $"This server doesn't accept the address {used}: it lists no addresses of its own, so it only accepts the one this connection was made to " +
                $"({connection.RequestOrigin?.ToString() ?? "unknown"}).",
            _ => $"This server doesn't accept the address {used}: it lists no addresses of its own (its operator hasn't set LookingGlass:PublicUrls), " +
                 "so it accepts none.",
        };
    }

    internal static KeyLoginOrigins ChooseKeyLoginOrigins(IReadOnlyList<ServerOrigin> publicOrigins, bool development) {
        return publicOrigins.Count > 0 ? KeyLoginOrigins.PublicUrls : development ? KeyLoginOrigins.HostHeader : KeyLoginOrigins.Off;
    }

    // Names only a local network gives meaning to, which every other network may give to another machine.
    private static readonly string[] LocalNameSuffixes = [".local", ".lan", ".home", ".home.arpa", ".internal", ".intranet", ".localdomain", ".localhost"];

    /// <summary>
    /// Why <paramref name="origin"/>, a listed public address, may not be this server's alone, or nothing if it is. The
    /// address checks (signed URLs, and the Lodestone code derived from the origin) tell this server from another only by
    /// the address the client connected to: if another server can have the same address, a client of that server signs
    /// for (and is shown codes for) this one's, and that server can pass them on here. A short name (MagicDNS on another
    /// tailnet, a LAN name), a private, CGNAT, loopback or link-local IP, or plain ws:// (where whoever answers at the
    /// name is taken for this server) can be. Fine on a private network the operator controls.
    /// </summary>
    internal static IReadOnlyList<string> WhyNotUnique(ServerOrigin origin) {
        var reasons = new List<string>();
        if (!origin.Secure) {
            reasons.Add("it is plain ws:// (no TLS), so whatever answers at that name on a user's network is taken for this server");
        }

        if (IPAddress.TryParse(origin.Host.Trim('[', ']'), out var ip)) {
            if (ip.IsIPv4MappedToIPv6) {
                ip = ip.MapToIPv4();
            }

            var kind = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? ip.GetAddressBytes() switch {
                    [127, ..] => "loopback",
                    [10, ..] or [172, >= 16 and < 32, ..] or [192, 168, ..] => "private",
                    [100, >= 64 and < 128, ..] => "CGNAT (shared, as Tailscale's 100.x addresses are)",
                    [169, 254, ..] => "link-local",
                    _ => null,
                }
                : IPAddress.IsLoopback(ip) ? "loopback"
                : ip.IsIPv6LinkLocal ? "link-local"
                : ip.IsIPv6UniqueLocal || ip.IsIPv6SiteLocal ? "private (unique local)"
                : null;
            if (kind != null) {
                reasons.Add($"it is a {kind} IP address, which other networks use too");
            }
        } else if (!origin.Host.Contains('.')) {
            reasons.Add("it is a single-label name (such as a short MagicDNS or LAN name), which other networks can give to other machines");
        } else if (LocalNameSuffixes.Any(suffix => origin.Host.EndsWith(suffix, StringComparison.Ordinal))) {
            reasons.Add("it is a local network name, which other networks can give to other machines");
        }

        return reasons;
    }

    /// <exception cref="InvalidOperationException">An entry isn't a ws, wss, http or https URL.</exception>
    internal static IReadOnlyList<ServerOrigin> ParsePublicUrls(IEnumerable<string>? urls) {
        return (urls ?? []).Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => ServerOrigin.FromUrl(url.Trim())
                           ?? throw new InvalidOperationException($"LookingGlass:PublicUrls: \"{url}\" isn't a ws://, wss://, http:// or https:// address."))
            .Distinct()
            .ToList();
    }

    private Response GetIdentities(ClientConnection connection, GetIdentities request) {
        var me = RequireUser(connection);
        this.RequireReadBudget(me);
        if (request.UserIds.Count > options.Value.Limits.MaxIdentitiesPerRequest) {
            throw new RequestException(ErrorCode.TooLarge, "Too many identities requested at once.");
        }

        // Only people you share a channel or invite with, so this can't be used to list who uses the plugin.
        var visible = db.GetVisibleUserIds(me.UserId);
        var identities = new Identities();
        identities.Identities_.AddRange(db.GetUsers(request.UserIds.Where(visible.Contains)).Select(user => user.ToIdentity()));
        return new Response { Identities = identities };
    }

    private Response LookupUser(ClientConnection connection, LookupUser request) {
        var me = RequireUser(connection);
        if (!this._lookups.TryTake(me.UserId)) {
            throw new RequestException(ErrorCode.RateLimited, "Too many lookups; slow down.");
        }

        var user = db.FindUser(request.Name, request.WorldName)
            ?? throw new RequestException(ErrorCode.NotFound, $"{request.Name}@{request.WorldName} isn't registered.");

        var identities = new Identities();
        identities.Identities_.Add(user.ToIdentity());
        return new Response { Identities = identities };
    }

    // ================================================================ channels

    private Response ListChannels(ClientConnection connection, ListChannels request) {
        var me = RequireUser(connection);
        this.RequireReadBudget(me);

        // How far each channel's log the client has verified, so only newer entries are sent.
        var known = new Dictionary<string, ulong>();
        foreach (var entry in request.Known) {
            if (ProtocolInfo.NormaliseChannelId(entry.ChannelId) is { } id) {
                known[id] = entry.NextSeq;
            }
        }

        // Bounded, so nobody can make this response too big for a client to receive and lock
        // them out: at most 50 channels of at most 500 members and 32 log entries, and 20 small
        // invites (about 3 MB at worst, where clients accept 4 MB).
        var list = new ChannelList();
        list.Channels.AddRange(db.GetChannelsForUser(me.UserId, (int) this.Limits.MaxChannelsPerUser)
            .Select(channel => this.BuildChannelInfo(channel, me.UserId, known.TryGetValue(channel.ChannelId, out var next) ? next : 0)));
        list.Invites.AddRange(db.GetInvitesForUser(me.UserId, MaxPendingInvitesPerUser).Select(ToInviteInfo));
        return new Response { ChannelList = list };
    }

    private Response FetchMembershipLog(ClientConnection connection, FetchMembershipLog request) {
        var me = RequireUser(connection);
        var channelId = RequireChannelId(request.ChannelId);
        this.RequireReadBudget(me);
        this.RequireAllowed(channelId, me, ChannelAction.FetchLog);

        var log = new MembershipLog { ChannelId = channelId, Head = db.GetChannel(channelId)!.LogHead };
        log.Entries.AddRange(db.GetLogEntries(channelId, request.FromSeq, ProtocolInfo.MaxLogEntriesPerPage));
        return new Response { MembershipLog = log };
    }

    private Response CreateChannel(ClientConnection connection, CreateChannel request) {
        var me = RequireUser(connection);
        var channelId = RequireChannelId(request.ChannelId);
        if (!this._creates.TryTake(me.UserId)) {
            throw new RequestException(ErrorCode.RateLimited, "You're creating channels too quickly; try again later.");
        }

        if (db.CountChannelsForUser(me.UserId) >= this.Limits.MaxChannelsPerUser) {
            throw new RequestException(ErrorCode.LimitReached, $"You're already in {this.Limits.MaxChannelsPerUser} channels.");
        }

        if (db.GetChannel(channelId) != null) {
            throw new RequestException(ErrorCode.Conflict, "That channel ID is taken.");
        }

        // The log starts with the creator, under their current keys.
        var genesis = request.Genesis;
        if (genesis == null || genesis.ActorId != me.UserId || MemberKeys.FromProto(genesis.Subject) != me.Keys
            || !membership.Empty(channelId).Check(genesis).IsValid) {
            throw new RequestException(ErrorCode.InvalidRequest, "The channel's first membership entry is missing or invalid.");
        }

        var position = MembershipEntries.PositionOf(genesis);
        if (request.CreatorKey == null || request.CreatorKey.RecipientId != me.UserId || request.CreatorKey.Box == null
            || request.CreatorKey.KeyCommitment.Length != ChannelCrypto.KeyCommitmentSize
            || !MembershipEntries.SamePosition(request.CreatorKey.LogPosition, position)
            || !groupKeys.VerifyEpochKey(request.CreatorKey, channelId, 0, me.UserId, me.SigningKey)) {
            throw new RequestException(ErrorCode.InvalidRequest, "The creator's epoch key is missing or wrongly signed.");
        }

        this.ValidateName(request.Name, channelId, 0, me, position);
        RequireFirstRevision(request.Name);
        RequireNoSource(request.Name);
        db.CreateChannel(channelId, genesis, request.CreatorKey, request.Name);
        logger.LogDebug("User {User} created channel {Channel}", me.UserId, channelId);
        return new Response { Channel = this.BuildChannelInfo(db.GetChannel(channelId)!, me.UserId, genesis.Seq + 1) };
    }

    private Response InviteMember(ClientConnection connection, InviteMember request) {
        var me = RequireUser(connection);
        var channelId = RequireChannelId(request.ChannelId);
        this.RequireAllowed(channelId, me, ChannelAction.Invite);
        if (!this._invitesSent.TryTake(me.UserId)) {
            throw new RequestException(ErrorCode.RateLimited, "You're sending invites too quickly; try again later.");
        }

        var entry = RequireEntry(request.Entry, channelId, me, MembershipEntryKind.Invite);
        var invitee = db.GetUser(entry.Subject.UserId) ?? throw new RequestException(ErrorCode.NotFound, "That user isn't registered.");
        if (db.GetRank(channelId, invitee.UserId) != null) {
            throw new RequestException(ErrorCode.Conflict, $"{invitee.Name} is already a member or invited.");
        }

        if (db.CountPendingInvites(channelId) >= this.Limits.MaxPendingInvitesPerChannel) {
            throw new RequestException(ErrorCode.LimitReached, "Too many pending invites in this channel.");
        }

        if (db.CountMembers(channelId) + db.CountPendingInvites(channelId) >= this.Limits.MaxMembersPerChannel) {
            throw new RequestException(ErrorCode.LimitReached, "This channel is full.");
        }

        if (request.SealedName == null || request.SealedName.EphemeralPublicKey.Length != 32 || request.SealedName.Ciphertext.Length == 0
            || request.Signature.Length != 64) {
            throw new RequestException(ErrorCode.InvalidRequest, "Malformed invite.");
        }

        if (request.SealedName.Ciphertext.Length > MaxSealedNameBytes) {
            throw new RequestException(ErrorCode.TooLarge, "The invite's channel name is too long.");
        }

        // Only the invitee can open the name, but anyone can check who signed it.
        if (!groupKeys.VerifyInvite(channelId, MembershipEntries.PositionOf(entry), invitee.UserId, me.UserId, request.SealedName, request.Signature.Span, me.SigningKey)) {
            throw new RequestException(ErrorCode.InvalidRequest, "The invite is wrongly signed.");
        }

        // An invite for keys that aren't the invitee's could never be accepted.
        if (MemberKeys.FromProto(entry.Subject) != invitee.Keys) {
            throw new RequestException(ErrorCode.InvalidRequest, $"The invite isn't for {invitee.Name}'s current identity key.");
        }

        if (db.CountInvitesForUser(invitee.UserId) >= MaxPendingInvitesPerUser) {
            throw new RequestException(ErrorCode.LimitReached, $"{invitee.Name} has too many pending invites.");
        }

        if (!this._invitesReceived.TryTake(invitee.UserId)) {
            throw new RequestException(ErrorCode.RateLimited, $"{invitee.Name} has been sent too many invites recently; try again later.");
        }

        this.AppendEntry(channelId, me, entry, request.SealedName, request.Signature.ToByteArray());
        var invite = db.GetInvite(channelId, invitee.UserId)!;

        registry.Send(invitee.UserId, new Event { InviteReceived = new InviteReceived { Invite = ToInviteInfo(invite) } });
        this.BroadcastEntry(channelId, entry, invitee, me);
        return Ack();
    }

    private Response RespondToInvite(ClientConnection connection, RespondToInvite request) {
        var me = RequireUser(connection);
        var channelId = RequireChannelId(request.ChannelId);
        if (db.GetInvite(channelId, me.UserId) == null) {
            throw new RequestException(ErrorCode.NotFound, "No such invite.");
        }

        var entry = RequireEntry(request.Entry, channelId, me, MembershipEntryKind.Accept, MembershipEntryKind.Decline);
        if (entry.Kind == MembershipEntryKind.Decline) {
            this.AppendEntry(channelId, me, entry);
            this.BroadcastEntry(channelId, entry, me, me);
            return Ack();
        }

        if (db.CountChannelsForUser(me.UserId) >= this.Limits.MaxChannelsPerUser) {
            throw new RequestException(ErrorCode.LimitReached, $"You're already in {this.Limits.MaxChannelsPerUser} channels.");
        }

        this.AppendEntry(channelId, me, entry);
        this.BroadcastEntry(channelId, entry, me, me);
        // The members who didn't share a channel with the joiner weren't told when they came online.
        registry.AnnounceJoined(channelId, me.UserId);
        var designated = this.RequestRekey(channelId, preferred: null, excluding: me.UserId);
        // The RekeyNeeded event reaches the joiner before this response, while they don't know
        // the channel yet, so if it's theirs to do (nobody else is online), say so here.
        var info = this.BuildChannelInfo(db.GetChannel(channelId)!, me.UserId, entry.Seq + 1);
        info.RekeyDesignated = info.RekeyPending && designated == me.UserId;
        return new Response { Channel = info };
    }

    private Response LeaveChannel(ClientConnection connection, LeaveChannel request) {
        var me = RequireUser(connection);
        var channelId = RequireChannelId(request.ChannelId);
        this.RequireAllowed(channelId, me, ChannelAction.Leave);
        var entry = RequireEntry(request.Entry, channelId, me, MembershipEntryKind.Leave);

        if (db.GetMembers(channelId).Count == 1) {
            // The last member is leaving, so the channel goes, and with it any pending invites.
            this.CheckEntry(channelId, me, entry);
            if (this.BeforeAbandonedChannelDeletedForTests is { } hook) {
                this.BeforeAbandonedChannelDeletedForTests = null;
                hook();
            }

            // Only if nobody joined meanwhile (an invitee accepting right now, say): checked and deleted in one go.
            var invitees = db.DeleteAbandonedChannel(channelId, new LogPosition { Seq = entry.Seq - 1, Hash = entry.PreviousHash }, me.UserId)
                           ?? throw new RequestException(ErrorCode.Conflict, "The channel's membership changed meanwhile; refresh and try again.");
            registry.SendToAll(invitees, new Event { InviteRevoked = new InviteRevoked { ChannelId = channelId } });
            return Ack();
        }

        this.AppendEntry(channelId, me, entry);
        this.BroadcastEntry(channelId, entry, me, me);
        this.RequestRekey(channelId, preferred: null, excluding: null);
        return Ack();
    }

    private Response KickMember(ClientConnection connection, KickMember request) {
        var me = RequireUser(connection);
        var channelId = RequireChannelId(request.ChannelId);
        this.RequireAllowed(channelId, me, ChannelAction.Kick);
        var entry = RequireEntry(request.Entry, channelId, me, MembershipEntryKind.Remove, MembershipEntryKind.CancelInvite);
        var target = db.GetUser(entry.Subject.UserId) ?? throw new RequestException(ErrorCode.NotFound, "No such user.");

        // The log's rules decide who may remove whom (strictly lower ranks only).
        this.AppendEntry(channelId, me, entry);
        if (entry.Kind == MembershipEntryKind.CancelInvite) {
            registry.Send(target.UserId, new Event { InviteRevoked = new InviteRevoked { ChannelId = channelId } });
            this.BroadcastEntry(channelId, entry, target, me);
            return Ack();
        }

        registry.Send(target.UserId, new Event { ChannelRemoved = new ChannelRemoved { ChannelId = channelId, Reason = RemovalReason.Kicked } });
        this.BroadcastEntry(channelId, entry, target, me);
        this.RequestRekey(channelId, preferred: me.UserId, excluding: null);
        return Ack();
    }

    private Response SetMemberRank(ClientConnection connection, SetMemberRank request) {
        var me = RequireUser(connection);
        var channelId = RequireChannelId(request.ChannelId);
        this.RequireAllowed(channelId, me, ChannelAction.SetRank);
        var entry = RequireEntry(request.Entry, channelId, me, MembershipEntryKind.SetRank, MembershipEntryKind.TransferAdmin);
        var target = db.GetUser(entry.Subject.UserId) ?? throw new RequestException(ErrorCode.NotFound, "No such user.");

        this.AppendEntry(channelId, me, entry);
        this.BroadcastEntry(channelId, entry, target, me);
        return Ack();
    }

    private Response DisbandChannel(ClientConnection connection, DisbandChannel request) {
        var me = RequireUser(connection);
        var channelId = RequireChannelId(request.ChannelId);
        this.RequireAllowed(channelId, me, ChannelAction.Disband);
        if (!this._disbands.TryTake(me.UserId)) {
            throw new RequestException(ErrorCode.RateLimited, "You're disbanding channels too quickly; try again later.");
        }

        var everyone = db.GetMembers(channelId).Concat(db.GetInvitees(channelId)).Select(member => member.User.UserId).ToList();
        db.DeleteChannel(channelId);
        registry.SendToAll(everyone, new Event { ChannelRemoved = new ChannelRemoved { ChannelId = channelId, Reason = RemovalReason.Disbanded } }, except: me.UserId);
        return Ack();
    }

    private Response RenameChannel(ClientConnection connection, RenameChannel request) {
        var me = RequireUser(connection);
        var channelId = RequireChannelId(request.ChannelId);
        this.RequireAllowed(channelId, me, ChannelAction.Rename);
        if (!this._renames.TryTake(me.UserId)) {
            throw new RequestException(ErrorCode.RateLimited, "You're renaming too quickly; try again later.");
        }

        var channel = db.GetChannel(channelId)!;
        if (channel.RekeyPending) {
            // The current key may still be held by someone who just left.
            throw new RequestException(ErrorCode.RekeyRequired, "Membership changed; rekey the channel before renaming it.");
        }

        var name = request.Name ?? throw new RequestException(ErrorCode.InvalidRequest, "The encrypted channel name is missing.");

        // A name made at an older log position is CONFLICT (catch up and retry), not invalid.
        if (name.LogPosition != null && !MembershipEntries.SamePosition(name.LogPosition, channel.LogHead)) {
            throw new RequestException(ErrorCode.Conflict, "The channel's membership changed meanwhile; refresh and rename it again.");
        }

        this.ValidateName(name, channelId, channel.Epoch, me, channel.LogHead);
        if (name.Revision > ProtocolInfo.MaxNameRevision) {
            throw new RequestException(ErrorCode.InvalidRequest, "The name's revision is out of range.");
        }

        RequireNoSource(name);

        if (channel.Name is { } current && current.Epoch == name.Epoch && name.Revision <= current.Revision) {
            // Clients refuse a name that isn't newer than theirs, so storing it would hide later renames.
            throw new RequestException(ErrorCode.Conflict, "The name's revision must be newer than the current one; refresh and try again.");
        }

        if (!db.RenameChannel(channelId, name)) {
            throw new RequestException(ErrorCode.Conflict, "The channel changed while renaming; try again.");
        }
        var members = db.GetMembers(channelId).Select(member => member.User.UserId);
        registry.SendToAll(members, new Event { ChannelRenamed = new ChannelRenamed { ChannelId = channelId, Name = name } }, except: me.UserId);
        return Ack();
    }

    // ================================================================ epochs and messages

    private Response SubmitRekey(ClientConnection connection, SubmitRekey request) {
        var me = RequireUser(connection);
        var channelId = RequireChannelId(request.ChannelId);
        this.RequireAllowed(channelId, me, ChannelAction.Rekey);

        // Each rekey makes every member's client decrypt and save a key, so they're rate-limited.
        if (!this._rekeys.TryTake(me.UserId)) {
            throw new RequestException(ErrorCode.RateLimited, "Too many rekeys; slow down.");
        }

        if (request.Keys.Count == 0 || request.Keys.Count > this.Limits.MaxMembersPerChannel) {
            throw new RequestException(ErrorCode.InvalidRequest, "A rekey needs one key per member.");
        }

        if (request.Keys.Select(key => key.RecipientId).Distinct().Count() != request.Keys.Count) {
            throw new RequestException(ErrorCode.InvalidRequest, "Duplicate recipients in rekey.");
        }

        // Every copy must commit to the same key. The server can't check what is inside the
        // boxes, but a recipient whose copy doesn't match this commitment knows who cheated.
        if (request.KeyCommitment.Length != ChannelCrypto.KeyCommitmentSize || request.Keys.Any(key => key.KeyCommitment != request.KeyCommitment)) {
            throw new RequestException(ErrorCode.InvalidRequest, "Every key in a rekey must carry the rekey's key commitment.");
        }

        // A rekey is made for one log position, which every copy names, and which must be the head (checked when applied).
        if (request.LogPosition == null || request.Keys.Any(key => !MembershipEntries.SamePosition(key.LogPosition, request.LogPosition))) {
            throw new RequestException(ErrorCode.InvalidRequest, "Every key in a rekey must name the rekey's membership log position.");
        }

        this.RequireMembershipKeys(channelId, me);
        foreach (var key in request.Keys) {
            if (key.Box == null || !groupKeys.VerifyEpochKey(key, channelId, request.NewEpoch, me.UserId, me.SigningKey)) {
                throw new RequestException(ErrorCode.InvalidRequest, "A key in the rekey is wrongly signed.");
            }
        }

        this.ValidateName(request.Name, channelId, request.NewEpoch, me, request.LogPosition);
        RequireFirstRevision(request.Name);
        if (request.Name.CarriedFrom is { } source && (source.Epoch >= request.NewEpoch || source.Revision > ProtocolInfo.MaxNameRevision)) {
            throw new RequestException(ErrorCode.InvalidRequest, "A rekey can only carry over a name from an earlier epoch.");
        }

        switch (db.ApplyRekey(channelId, request.NewEpoch, me.UserId, request.Keys, request.Name, request.LogPosition)) {
            case RekeyResult.EpochStale:
                throw new RequestException(ErrorCode.EpochStale, "The channel is no longer at that epoch.");
            case RekeyResult.MembershipChanged:
                throw new RequestException(ErrorCode.Conflict, "Membership changed; rekey for the members at the log's head.");
        }

        foreach (var key in request.Keys.Where(key => key.RecipientId != me.UserId)) {
            registry.Send(key.RecipientId, new Event {
                EpochAdvanced = new EpochAdvanced {
                    ChannelId = channelId,
                    Epoch = request.NewEpoch,
                    AuthorId = me.UserId,
                    MyKey = key,
                    Name = request.Name,
                },
            });
        }

        logger.LogDebug("Channel {Channel} advanced to epoch {Epoch} by {User}", channelId, request.NewEpoch, me.UserId);
        return Ack();
    }

    private Response FetchEpochKeys(ClientConnection connection, FetchEpochKeys request) {
        var me = RequireUser(connection);
        var channelId = RequireChannelId(request.ChannelId);
        this.RequireReadBudget(me);
        this.RequireAllowed(channelId, me, ChannelAction.FetchKeys);

        var keys = new EpochKeys { ChannelId = channelId, Name = db.GetChannel(channelId)!.Name };
        keys.Keys.AddRange(db.GetEpochKeys(channelId, me.UserId, request.FromEpoch));
        return new Response { EpochKeys = keys };
    }

    private Response SendMessage(ClientConnection connection, SendMessage request) {
        var me = RequireUser(connection);
        var channelId = RequireChannelId(request.ChannelId);
        this.RequireAllowed(channelId, me, ChannelAction.Send);

        if (!this._messages.TryTake(me.UserId)) {
            throw new RequestException(ErrorCode.RateLimited, "You're sending messages too quickly.");
        }

        if (request.Ciphertext.Length > this.Limits.MaxMessageBytes) {
            throw new RequestException(ErrorCode.TooLarge, "Message too large.");
        }

        if (request.MessageId.Length != 16) {
            throw new RequestException(ErrorCode.InvalidRequest, "Malformed message ID.");
        }

        var channel = db.GetChannel(channelId)!;
        if (channel.RekeyPending) {
            throw new RequestException(ErrorCode.RekeyRequired, "Membership changed; rekey the channel before sending.");
        }

        if (request.Epoch != channel.Epoch) {
            throw new RequestException(ErrorCode.EpochStale, "That epoch is no longer current.");
        }

        var message = new ChatMessage {
            ChannelId = channelId,
            Epoch = request.Epoch,
            SenderId = me.UserId,
            MessageId = request.MessageId,
            TimestampUnixMs = request.TimestampUnixMs,
            Ciphertext = request.Ciphertext,
            Signature = request.Signature,
        };

        // Not needed for secrecy, but rejects garbage before it is fanned out.
        if (!groupKeys.VerifyMessage(message, me.SigningKey)) {
            throw new RequestException(ErrorCode.InvalidRequest, "Message signature is invalid.");
        }

        var members = db.GetMembers(channelId).Select(member => member.User.UserId);
        registry.SendToAll(members, new Event { ChatMessage = message }, except: me.UserId);
        return Ack();
    }

    // ================================================================ helpers

    /// <param name="knownNext">The first log entry the viewer hasn't verified; later ones (a few) are included.</param>
    private ChannelInfo BuildChannelInfo(ChannelRow channel, long viewerId, ulong knownNext) {
        var info = new ChannelInfo {
            ChannelId = channel.ChannelId,
            Epoch = channel.Epoch,
            RekeyPending = channel.RekeyPending,
            Name = channel.Name,
            LogHead = channel.LogHead,
        };

        var members = db.GetMembers(channel.ChannelId);
        foreach (var member in members.Concat(db.GetInvitees(channel.ChannelId))) {
            info.Members.Add(new Member { User = member.User.ToProto(), Rank = member.Rank });
        }

        // Filled in again, under the presence lock, as the response is sent (see ConnectionRegistry.Respond).
        registry.MarkOnline(info);

        if (knownNext <= channel.LogHead.Seq) {
            info.Log.AddRange(db.GetLogEntries(channel.ChannelId, knownNext, ProtocolInfo.MaxLogEntriesInChannelInfo));
        }

        // A member coming online may be the one to rekey now; nobody else would ask them.
        if (channel.RekeyPending) {
            var online = members.Where(member => registry.IsOnline(member.User.UserId)).ToList();
            info.RekeyDesignated = ChooseRekeyer(online, preferred: null, excluding: null) == viewerId;
        }

        return info;
    }

    /// <summary>
    /// Marks the channel as needing a rekey and asks one online member to do
    /// it: <paramref name="preferred"/> if possible, otherwise the
    /// highest-ranked online member. Everyone online is told.
    /// </summary>
    /// <returns>The member asked, or null if nobody is online.</returns>
    private long? RequestRekey(string channelId, long? preferred, long? excluding) {
        var channel = db.GetChannel(channelId);
        if (channel == null) {
            return null;
        }

        var members = db.GetMembers(channelId);
        var online = members.Where(member => registry.IsOnline(member.User.UserId)).ToList();
        var designated = ChooseRekeyer(online, preferred, excluding);
        registry.SendToAll(online.Select(member => member.User.UserId), new Event {
            RekeyNeeded = new RekeyNeeded {
                ChannelId = channelId,
                CurrentEpoch = channel.Epoch,
                DesignatedUserId = designated ?? 0,
            },
        });
        return designated;
    }

    /// <summary>
    /// Which of the <paramref name="online"/> members to ask for a rekey: <paramref name="preferred"/>
    /// if possible, otherwise the highest-ranked, avoiding <paramref name="excluding"/> unless
    /// nobody else is online.
    /// </summary>
    private static long? ChooseRekeyer(List<MemberRow> online, long? preferred, long? excluding) {
        var candidates = online.Where(member => member.User.UserId != excluding).ToList();
        if (candidates.Count == 0) {
            candidates = online;
        }

        var designated = candidates.FirstOrDefault(member => member.User.UserId == preferred)
                         ?? candidates.OrderByDescending(member => member.Rank).ThenBy(member => member.User.UserId).FirstOrDefault();
        return designated?.User.UserId;
    }

    /// <summary>Tells the channel's members (except the actor, who knows) about a new log entry. They check it themselves.</summary>
    private void BroadcastEntry(string channelId, MembershipEntry entry, UserRow subject, UserRow actor) {
        var recipients = db.GetMembers(channelId).Select(member => member.User.UserId).ToList();
        registry.SendToAll(recipients, new Event {
            LogEntryAdded = new LogEntryAdded {
                ChannelId = channelId,
                Entry = entry,
                Subject = subject.ToProto(),
                Actor = actor.ToProto(),
            },
        }, except: actor.UserId);
    }

    private static MembershipEntry RequireEntry(MembershipEntry? entry, string channelId, UserRow me, params MembershipEntryKind[] kinds) {
        if (entry == null || entry.ChannelId != channelId || entry.ActorId != me.UserId || entry.Subject == null || !kinds.Contains(entry.Kind)) {
            throw new RequestException(ErrorCode.InvalidRequest, "The request's membership log entry is missing, or isn't the right kind.");
        }

        return entry;
    }

    /// <summary>
    /// Checks an entry with the same rules clients replay the log with, against the membership as
    /// the server's tables have it. Turns the verdict into the error the client is told.
    /// </summary>
    private IChannelMembership CheckEntry(string channelId, UserRow me, MembershipEntry entry) {
        var state = membership.Restore(db.GetMembershipCheckpoint(channelId) ?? throw new RequestException(ErrorCode.NotFound, "No such channel."));

        // Say why, rather than "wrongly signed", when someone who registered again acts with their new keys.
        var bound = state.FindMember(me.UserId)?.Keys ?? state.FindInvitee(me.UserId)?.Keys;
        if (bound != null && bound != me.Keys) {
            throw new RequestException(ErrorCode.Forbidden,
                "Your place in this channel belongs to the identity key you had before you registered again. A moderator must remove you and invite you again.");
        }

        var verdict = state.Check(entry);
        return verdict.Kind switch {
            MembershipVerdictKind.Valid => state.Apply(entry),
            MembershipVerdictKind.NotNext => throw new RequestException(ErrorCode.Conflict, "The channel's membership changed meanwhile; refresh and try again."),
            MembershipVerdictKind.Forbidden => throw new RequestException(ErrorCode.Forbidden, verdict.Reason),
            MembershipVerdictKind.Conflict => throw new RequestException(ErrorCode.Conflict, verdict.Reason),
            _ => throw new RequestException(ErrorCode.InvalidRequest, $"Invalid membership log entry: {verdict.Reason}"),
        };
    }

    private void AppendEntry(string channelId, UserRow me, MembershipEntry entry, SealedBox? sealedName = null, byte[]? inviteSignature = null) {
        var invitedBefore = db.GetInvitees(channelId).Select(invitee => invitee.User.UserId).ToHashSet();
        var after = this.CheckEntry(channelId, me, entry);
        if (!db.AppendEntry(channelId, entry, sealedName, inviteSignature)) {
            throw new RequestException(ErrorCode.Conflict, "The channel's membership changed meanwhile; refresh and try again.");
        }

        // Invites that went with their inviter's removal or demotion (the subject's own is the caller's to announce).
        invitedBefore.ExceptWith(after.Invitees.Select(invitee => invitee.UserId));
        invitedBefore.Remove(entry.Subject.UserId);
        registry.SendToAll(invitedBefore, new Event { InviteRevoked = new InviteRevoked { ChannelId = channelId } });
        logger.LogDebug("Channel {Channel} log entry {Seq} ({Kind}) by {User}", channelId, entry.Seq, entry.Kind, me.UserId);
    }

    /// <summary>Someone who registered again can't rekey for a channel their new key isn't a member of.</summary>
    private void RequireMembershipKeys(string channelId, UserRow me) {
        var checkpoint = db.GetMembershipCheckpoint(channelId) ?? throw new RequestException(ErrorCode.NotFound, "No such channel.");
        var mine = checkpoint.Members.FirstOrDefault(member => member.UserId == me.UserId);
        if (mine == null || new MemberKeys(mine.SigningPublicKey, mine.AgreementPublicKey) != me.Keys) {
            throw new RequestException(ErrorCode.Forbidden,
                "Your place in this channel belongs to the identity key you had before you registered again. A moderator must remove you and invite you again.");
        }
    }

    private Rank RequireAllowed(string channelId, UserRow me, ChannelAction action) {
        if (db.GetChannel(channelId) == null) {
            throw new RequestException(ErrorCode.NotFound, "No such channel.");
        }

        var rank = db.GetRank(channelId, me.UserId);
        if (rank == null) {
            throw new RequestException(ErrorCode.NotFound, "You're not in that channel.");
        }

        if (!Policy.Can(rank, action)) {
            throw new RequestException(ErrorCode.Forbidden, $"Your rank can't {action.ToString().ToLowerInvariant()} in this channel.");
        }

        return rank.Value;
    }

    /// <param name="position">The log position the name must be made at.</param>
    private void ValidateName(EncryptedName? name, string channelId, ulong epoch, UserRow author, LogPosition position) {
        if (name == null || name.Epoch != epoch || name.AuthorId != author.UserId
            || name.Ciphertext.Length is 0 or > 512
            || !MembershipEntries.SamePosition(name.LogPosition, position)
            || !groupKeys.VerifyName(name, channelId, author.SigningKey)) {
            throw new RequestException(ErrorCode.InvalidRequest, "The encrypted channel name is missing, wrongly signed, or not made for the log's head.");
        }
    }

    /// <summary>
    /// Revisions count renames within an epoch, so a new channel's or a new epoch's name is
    /// revision 0. Renaming is the admin's alone; a member's rekey only carries the name over,
    /// and must not be able to set a revision that blocks the admin's next renames.
    /// </summary>
    private static void RequireFirstRevision(EncryptedName name) {
        if (name.Revision != 0) {
            throw new RequestException(ErrorCode.InvalidRequest, "A new epoch's name must have revision 0; only a rename can change it.");
        }
    }

    /// <summary>Only a rekey carries a name over from an earlier version.</summary>
    private static void RequireNoSource(EncryptedName name) {
        if (name.CarriedFrom != null) {
            throw new RequestException(ErrorCode.InvalidRequest, "Only a rekey's name says which name it carries over.");
        }
    }

    private void RequireReadBudget(UserRow me) {
        if (!this._reads.TryTake(me.UserId)) {
            throw new RequestException(ErrorCode.RateLimited, "Too many requests; slow down.");
        }
    }

    private static UserRow RequireUser(ClientConnection connection) {
        return connection.User ?? throw new RequestException(ErrorCode.NotAuthenticated, "Log in first.");
    }

    private static string RequireChannelId(string id) {
        var normalised = ProtocolInfo.NormaliseChannelId(id);
        if (normalised == null || normalised != id) {
            throw new RequestException(ErrorCode.InvalidRequest, "Invalid channel ID.");
        }

        return normalised;
    }

    private static InviteInfo ToInviteInfo(InviteRow invite) => new() {
        ChannelId = invite.ChannelId,
        Inviter = invite.Inviter.ToProto(),
        SealedName = invite.SealedName,
        Signature = ByteString.CopyFrom(invite.Signature),
        CreatedUnix = invite.CreatedUnix,
        Entry = invite.Entry,
    };

    private static Response Ack() => new() { Ack = new Ack() };

    private static Response Error(ErrorCode code, string message) => new() { Error = new Protocol.Error { Code = code, Message = message } };

    /// <summary>Debug accounts get stable negative IDs derived from their name.</summary>
    internal static long DebugUserId(string name) {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name.Trim().ToLowerInvariant()));
        return -((BitConverter.ToInt64(hash, 0) & 0x001F_FFFF_FFFF_FFFF) + 1);
    }

    internal static byte[] HashToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static string NewDeviceToken() => "lgt_" + Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
