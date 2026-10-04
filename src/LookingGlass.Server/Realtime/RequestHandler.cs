using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using LookingGlass.Core.Crypto;
using LookingGlass.Core.Membership;
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

    /// <summary>Pending invites one user can have at once, across all channels.</summary>
    public const int MaxPendingInvitesPerUser = 20;

    // Channel names are at most 64 UTF-8 bytes; sealing adds a 16-byte tag. The cap keeps
    // invites (which strangers can send) from bloating the invitee's channel list.
    private const int MaxSealedNameBytes = 128;

    private readonly WindowCounter _registrations = new(options.Value.Limits.RegistrationsPerHourPerIp, TimeSpan.FromHours(1));
    // Key login: challenges per address; failures per address, where a challenge counts as one from when it is issued
    // until it is answered correctly (so asking and never answering is limited like failing); and failed answers per
    // account, by anyone. Each challenge allows one attempt, so these bound attempts per connection and account too.
    //
    // Only failed answers count against an account, never challenges or successes: anyone can ask for challenges for
    // any account, and counting those let one address keep an account's key login locked. The account's allowance is
    // sized from the per-address one so no single address can empty it (twice as many at once, refilling three times as
    // fast), so keeping an account locked takes the whole failure allowance of several addresses, each of which is then
    // blocked from key login itself.
    private readonly WindowCounter _keyLoginsPerIp = new(options.Value.Limits.KeyLoginsPerHourPerIp, TimeSpan.FromHours(1));
    private readonly WindowCounter _keyLoginFailuresPerIp = new(options.Value.Limits.KeyLoginFailuresPerHourPerIp, TimeSpan.FromHours(1));
    private readonly UserRateLimits _keyLoginFailuresPerUser = new(
        perSecond: Math.Max(1, options.Value.Limits.KeyLoginFailuresPerHourPerIp) * 3 / 3600.0,
        burst: Math.Max(1, options.Value.Limits.KeyLoginFailuresPerHourPerIp) * 2);
    private readonly IReadOnlyList<ServerOrigin> _publicOrigins = ParsePublicUrls(options.Value.PublicUrls);
    private readonly KeyLoginOrigins _keyLoginOrigins = ChooseKeyLoginOrigins(ParsePublicUrls(options.Value.PublicUrls), environment?.IsDevelopment() == true);
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
                ClientFrame.BodyOneofCase.CompleteRegistration => await this.CompleteRegistration(connection, ct),
                ClientFrame.BodyOneofCase.Authenticate => this.Authenticate(connection, frame.Authenticate),
                ClientFrame.BodyOneofCase.StartKeyLogin => this.StartKeyLogin(connection, frame.StartKeyLogin),
                ClientFrame.BodyOneofCase.CompleteKeyLogin => this.CompleteKeyLogin(connection, frame.CompleteKeyLogin),
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
        return new Response { Welcome = welcome };
    }

    private async Task<Response> StartRegistration(ClientConnection connection, StartRegistration request, CancellationToken ct) {
        var character = request.Character ?? throw new RequestException(ErrorCode.InvalidRequest, "Missing character.");
        var name = character.Name.Trim();
        var worldName = character.WorldName.Trim();
        if (name.Length is 0 or > 32 || worldName.Length is 0 or > 32) {
            throw new RequestException(ErrorCode.InvalidRequest, "Invalid character name or world.");
        }

        if (!IdentityKeys.IsValidBundle(request.Identity)) {
            throw new RequestException(ErrorCode.InvalidRequest, "Invalid identity keys.");
        }

        var minutes = options.Value.Lodestone.ChallengeMinutes;
        if (ProtocolInfo.IsDebugWorld(worldName)) {
            if (!options.Value.Dev.AllowDebugAccounts) {
                throw new RequestException(ErrorCode.RegistrationFailed, "Debug accounts are disabled on this server.");
            }

            connection.PendingRegistration = new PendingRegistration(
                DebugUserId(name), name, 0, ProtocolInfo.DebugWorldName, request.Identity, "", DateTimeOffset.UtcNow.AddMinutes(minutes), true);
            return new Response {
                RegistrationChallenge = new RegistrationChallenge {
                    Code = "",
                    ExpiresUnix = connection.PendingRegistration.Expires.ToUnixTimeSeconds(),
                    LodestoneId = connection.PendingRegistration.UserId,
                    VerificationSkipped = true,
                },
            };
        }

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

        var code = "LGC-" + RandomCode(8);
        connection.PendingRegistration = new PendingRegistration(
            found.Id, found.Name, character.WorldId, found.WorldName, request.Identity, code, DateTimeOffset.UtcNow.AddMinutes(minutes), false);
        connection.VerifyAttempts = 0;

        return new Response {
            RegistrationChallenge = new RegistrationChallenge {
                Code = code,
                ExpiresUnix = connection.PendingRegistration.Expires.ToUnixTimeSeconds(),
                LodestoneId = found.Id,
            },
        };
    }

    private async Task<Response> CompleteRegistration(ClientConnection connection, CancellationToken ct) {
        var pending = connection.PendingRegistration
            ?? throw new RequestException(ErrorCode.RegistrationFailed, "Start registration on this connection first.");

        if (pending.Expires < DateTimeOffset.UtcNow) {
            connection.PendingRegistration = null;
            throw new RequestException(ErrorCode.RegistrationFailed, "The challenge expired; start again.");
        }

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
                    throw new RequestException(ErrorCode.RegistrationFailed, $"{pending.Code} isn't in your Lodestone profile yet. The Lodestone can take a minute to update.");
            }
        }

        connection.PendingRegistration = null;
        // New keys don't change any channel's members (the log binds them to the old ones), so nothing needs a rekey.
        var (user, _) = db.RegisterUser(pending.UserId, pending.Name, pending.WorldId, pending.WorldName, pending.Identity, pending.IsDebug);

        var token = NewDeviceToken();
        db.AddDevice(user.UserId, HashToken(token));

        // Old devices were revoked; drop any session still using one.
        registry.Disconnect(user.UserId, "This character registered again");
        logger.LogInformation("Registered {User} ({Kind})", user.UserId, pending.IsDebug ? "debug" : "verified");
        return new Response { RegistrationComplete = new RegistrationComplete { DeviceToken = token, User = user.ToProto() } };
    }

    private Response Authenticate(ClientConnection connection, Authenticate request) {
        if (connection.User != null) {
            // Switching users would leave the first one registered as online on this connection.
            throw new RequestException(ErrorCode.InvalidRequest, "Already logged in on this connection.");
        }

        var userId = string.IsNullOrEmpty(request.DeviceToken) ? null : db.FindDevice(HashToken(request.DeviceToken));
        var user = userId == null ? null : db.GetUser(userId.Value);
        if (user == null) {
            throw new RequestException(ErrorCode.NotAuthenticated, "Unknown or revoked device token.");
        }

        if (user.IsDebug && !options.Value.Dev.AllowDebugAccounts) {
            throw new RequestException(ErrorCode.NotAuthenticated, "Debug accounts are disabled on this server.");
        }

        connection.User = user;
        registry.SetOnline(user.UserId, connection);
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

        // Checked, not taken: only a failed answer uses up the account's allowance.
        if (!this._keyLoginFailuresPerUser.HasToken(request.UserId)) {
            logger.LogDebug("Key login for {User} from {Address} refused: too many failures for this account", request.UserId, address);
            throw new RequestException(ErrorCode.RateLimited, "Too many key login attempts for this account; try again later.");
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

        var refusal = this.CheckKeyLogin(connection, pending, request, out var user);
        string? token = null;
        if (refusal == null) {
            token = NewDeviceToken();
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
                // The address's failure was counted with the challenge; this is the account's.
                this._keyLoginFailuresPerUser.TryTake(pending.UserId);
            }

            logger.LogInformation("Key login for {User} from {Address} refused: {Reason}", pending?.UserId, connection.RemoteAddress, refusal);
            throw new RequestException(ErrorCode.NotAuthenticated, KeyLoginFailed);
        }

        // Answered correctly: the failure counted for the address when the challenge was issued didn't happen.
        this._keyLoginFailuresPerIp.Refund(connection.RemoteAddress);
        logger.LogInformation("Key login for {User} from {Address}: new device", user!.UserId, connection.RemoteAddress);
        return new Response { KeyLoginComplete = new KeyLoginComplete { DeviceToken = token, User = user.ToProto() } };
    }

    /// <returns>Why the key login is refused (for the log only, never the client), or null if it may go ahead.</returns>
    private string? CheckKeyLogin(ClientConnection connection, PendingKeyLogin? pending, CompleteKeyLogin request, out UserRow? user) {
        user = null;
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
        var signed = ServerOrigin.FromUrl(request.ServerUrl);
        var ours = this._keyLoginOrigins switch {
            KeyLoginOrigins.PublicUrls => signed != null && this._publicOrigins.Contains(signed),
            KeyLoginOrigins.HostHeader => signed != null && signed == connection.RequestOrigin,
            _ => false,
        };
        if (!ours) {
            return $"signed for {signed?.ToString() ?? "an invalid address"}, which isn't this server ("
                   + this._keyLoginOrigins switch {
                       KeyLoginOrigins.PublicUrls => string.Join(", ", this._publicOrigins),
                       KeyLoginOrigins.HostHeader => $"Host header: {connection.RequestOrigin?.ToString() ?? "unknown address"}",
                       _ => "key login is off: no PublicUrls",
                   } + ")";
        }

        user = db.GetUser(pending.UserId);
        if (user == null) {
            return "no such account";
        }

        if (user.IsDebug && !options.Value.Dev.AllowDebugAccounts) {
            return "debug accounts are disabled";
        }

        // Against the account's current key only: keys replaced by registering again can't sign in.
        if (!KeyLoginProof.Verify(user.SigningKey, pending.Challenge, pending.UserId, request.ServerUrl, request.Signature.Span)) {
            return "the signature isn't by the account's identity key";
        }

        return null;
    }

    /// <summary>Which server addresses a key login signature may name.</summary>
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

        /// <summary>None configured, outside Development: there is no address to trust, so key login is refused.</summary>
        Off,
    }

    internal static KeyLoginOrigins ChooseKeyLoginOrigins(IReadOnlyList<ServerOrigin> publicOrigins, bool development) {
        return publicOrigins.Count > 0 ? KeyLoginOrigins.PublicUrls : development ? KeyLoginOrigins.HostHeader : KeyLoginOrigins.Off;
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

    private static string RandomCode(int length) {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return string.Create(length, alphabet, (span, chars) => {
            for (var i = 0; i < span.Length; i++) {
                span[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
            }
        });
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
