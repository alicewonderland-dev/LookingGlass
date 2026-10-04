using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using WonderlandChat.Core.Crypto;
using WonderlandChat.Protocol;
using WonderlandChat.Server.Data;
using WonderlandChat.Server.Services;

namespace WonderlandChat.Server.Realtime;

/// <summary>A request failed in a way the client should be told about.</summary>
public sealed class RequestException(ErrorCode code, string message) : Exception(message) {
    public ErrorCode Code { get; } = code;
}

/// <summary>Handles every client request. Errors become typed responses; they never drop the connection.</summary>
public sealed class RequestHandler(
    Database db,
    ConnectionRegistry registry,
    LodestoneClient lodestone,
    IOptions<ServerOptions> options,
    ILogger<RequestHandler> logger) {
    private static readonly string ServerVersion = typeof(RequestHandler).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    private static readonly TimeSpan VerifyCooldown = TimeSpan.FromSeconds(10);
    private const int MaxVerifyAttempts = 10;

    /// <summary>Pending invites one user can have at once, across all channels.</summary>
    public const int MaxPendingInvitesPerUser = 20;

    // Channel names are at most 64 UTF-8 bytes; sealing adds a 16-byte tag. The cap keeps
    // invites (which strangers can send) from bloating the invitee's channel list.
    private const int MaxSealedNameBytes = 128;

    private readonly WindowCounter _registrations = new(options.Value.Limits.RegistrationsPerHourPerIp, TimeSpan.FromHours(1));
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

    public async Task<Response> HandleAsync(ClientConnection connection, ClientFrame frame, CancellationToken ct) {
        try {
            if (!connection.HelloDone && frame.BodyCase != ClientFrame.BodyOneofCase.Hello) {
                throw new RequestException(ErrorCode.InvalidRequest, "Send Hello first.");
            }

            return frame.BodyCase switch {
                ClientFrame.BodyOneofCase.Hello => this.Hello(connection, frame.Hello),
                ClientFrame.BodyOneofCase.Ping => new Response { Pong = new Pong { ServerTimeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() } },
                ClientFrame.BodyOneofCase.StartRegistration => await this.StartRegistration(connection, frame.StartRegistration, ct),
                ClientFrame.BodyOneofCase.CompleteRegistration => await this.CompleteRegistration(connection, ct),
                ClientFrame.BodyOneofCase.Authenticate => this.Authenticate(connection, frame.Authenticate),
                ClientFrame.BodyOneofCase.GetIdentities => this.GetIdentities(connection, frame.GetIdentities),
                ClientFrame.BodyOneofCase.LookupUser => this.LookupUser(connection, frame.LookupUser),
                ClientFrame.BodyOneofCase.ListChannels => this.ListChannels(connection),
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

        var code = "WCL-" + RandomCode(8);
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
        var (user, keysChanged) = db.RegisterUser(pending.UserId, pending.Name, pending.WorldId, pending.WorldName, pending.Identity, pending.IsDebug);

        var token = "wct_" + Base64Url(RandomNumberGenerator.GetBytes(32));
        db.AddDevice(user.UserId, HashToken(token));

        // Old devices were revoked; drop any session still using one.
        registry.Disconnect(user.UserId, "This character registered again");
        logger.LogInformation("Registered {User} ({Kind})", user.UserId, pending.IsDebug ? "debug" : "verified");

        if (keysChanged) {
            foreach (var channel in db.GetChannelsForUser(user.UserId)) {
                this.RequestRekey(channel.ChannelId, preferred: null, excluding: user.UserId);
            }
        }

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

    private Response ListChannels(ClientConnection connection) {
        var me = RequireUser(connection);
        this.RequireReadBudget(me);

        // Bounded, so nobody can make this response too big for a client to receive and lock
        // them out: at most 50 channels of at most 500 members, and 20 small invites (about
        // 2 MB at worst, where clients accept 4 MB).
        var list = new ChannelList();
        list.Channels.AddRange(db.GetChannelsForUser(me.UserId, (int) this.Limits.MaxChannelsPerUser).Select(channel => this.BuildChannelInfo(channel, me.UserId)));
        list.Invites.AddRange(db.GetInvitesForUser(me.UserId, MaxPendingInvitesPerUser).Select(ToInviteInfo));
        return new Response { ChannelList = list };
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

        if (request.CreatorKey == null || request.CreatorKey.RecipientId != me.UserId || request.CreatorKey.Box == null
            || request.CreatorKey.KeyCommitment.Length != ChannelCrypto.KeyCommitmentSize
            || !ChannelCrypto.VerifyEpochKey(request.CreatorKey, channelId, 0, me.UserId, me.SigningKey)) {
            throw new RequestException(ErrorCode.InvalidRequest, "The creator's epoch key is missing or wrongly signed.");
        }

        this.ValidateName(request.Name, channelId, 0, me);
        RequireFirstRevision(request.Name);
        db.CreateChannel(channelId, me.UserId, request.CreatorKey, request.Name);
        logger.LogDebug("User {User} created channel {Channel}", me.UserId, channelId);
        return new Response { Channel = this.BuildChannelInfo(db.GetChannel(channelId)!, me.UserId) };
    }

    private Response InviteMember(ClientConnection connection, InviteMember request) {
        var me = RequireUser(connection);
        var channelId = RequireChannelId(request.ChannelId);
        this.RequireAllowed(channelId, me, ChannelAction.Invite);
        if (!this._invitesSent.TryTake(me.UserId)) {
            throw new RequestException(ErrorCode.RateLimited, "You're sending invites too quickly; try again later.");
        }

        var invitee = db.GetUser(request.UserId) ?? throw new RequestException(ErrorCode.NotFound, "That user isn't registered.");
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
        if (!ChannelCrypto.VerifyInvite(channelId, invitee.UserId, me.UserId, request.SealedName, request.Signature.Span, me.SigningKey)) {
            throw new RequestException(ErrorCode.InvalidRequest, "The invite is wrongly signed.");
        }

        if (db.CountInvitesForUser(invitee.UserId) >= MaxPendingInvitesPerUser) {
            throw new RequestException(ErrorCode.LimitReached, $"{invitee.Name} has too many pending invites.");
        }

        if (!this._invitesReceived.TryTake(invitee.UserId)) {
            throw new RequestException(ErrorCode.RateLimited, $"{invitee.Name} has been sent too many invites recently; try again later.");
        }

        db.AddInvite(channelId, invitee.UserId, me.UserId, request.SealedName, request.Signature.ToByteArray());
        var invite = db.GetInvite(channelId, invitee.UserId)!;

        registry.Send(invitee.UserId, new Event { InviteReceived = new InviteReceived { Invite = ToInviteInfo(invite) } });
        this.BroadcastMemberChange(channelId, invitee, MemberChangeKind.Invited, Rank.Invited, me);
        return Ack();
    }

    private Response RespondToInvite(ClientConnection connection, RespondToInvite request) {
        var me = RequireUser(connection);
        var channelId = RequireChannelId(request.ChannelId);
        if (db.GetInvite(channelId, me.UserId) == null) {
            throw new RequestException(ErrorCode.NotFound, "No such invite.");
        }

        if (!request.Accept) {
            db.DeleteInvite(channelId, me.UserId);
            this.BroadcastMemberChange(channelId, me, MemberChangeKind.Declined, Rank.Unspecified, me);
            return Ack();
        }

        if (db.CountChannelsForUser(me.UserId) >= this.Limits.MaxChannelsPerUser) {
            throw new RequestException(ErrorCode.LimitReached, $"You're already in {this.Limits.MaxChannelsPerUser} channels.");
        }

        if (!db.AcceptInvite(channelId, me.UserId)) {
            throw new RequestException(ErrorCode.NotFound, "No such invite.");
        }

        this.BroadcastMemberChange(channelId, me, MemberChangeKind.Joined, Rank.Member, me, except: me.UserId);
        this.RequestRekey(channelId, preferred: null, excluding: me.UserId);
        return new Response { Channel = this.BuildChannelInfo(db.GetChannel(channelId)!, me.UserId) };
    }

    private Response LeaveChannel(ClientConnection connection, LeaveChannel request) {
        var me = RequireUser(connection);
        var channelId = RequireChannelId(request.ChannelId);
        var rank = this.RequireAllowed(channelId, me, ChannelAction.Leave);
        var members = db.GetMembers(channelId);

        if (members.Count == 1) {
            db.DeleteChannel(channelId);
            return Ack();
        }

        if (rank == Rank.Admin) {
            throw new RequestException(ErrorCode.Forbidden, "Make someone else admin before leaving, or disband the channel.");
        }

        db.RemoveMember(channelId, me.UserId);
        this.BroadcastMemberChange(channelId, me, MemberChangeKind.Left, Rank.Unspecified, me);
        this.RequestRekey(channelId, preferred: null, excluding: null);
        return Ack();
    }

    private Response KickMember(ClientConnection connection, KickMember request) {
        var me = RequireUser(connection);
        var channelId = RequireChannelId(request.ChannelId);
        var myRank = this.RequireAllowed(channelId, me, ChannelAction.Kick);
        var target = db.GetUser(request.UserId) ?? throw new RequestException(ErrorCode.NotFound, "No such user.");
        var targetRank = db.GetRank(channelId, target.UserId) ?? throw new RequestException(ErrorCode.NotFound, $"{target.Name} isn't in this channel.");

        if (!Policy.CanKick(myRank, targetRank)) {
            throw new RequestException(ErrorCode.Forbidden, "You can only remove members ranked below you.");
        }

        if (targetRank == Rank.Invited) {
            db.DeleteInvite(channelId, target.UserId);
            registry.Send(target.UserId, new Event { InviteRevoked = new InviteRevoked { ChannelId = channelId } });
            this.BroadcastMemberChange(channelId, target, MemberChangeKind.InviteCancelled, Rank.Unspecified, me);
            return Ack();
        }

        db.RemoveMember(channelId, target.UserId);
        registry.Send(target.UserId, new Event { ChannelRemoved = new ChannelRemoved { ChannelId = channelId, Reason = RemovalReason.Kicked } });
        this.BroadcastMemberChange(channelId, target, MemberChangeKind.Kicked, Rank.Unspecified, me);
        this.RequestRekey(channelId, preferred: me.UserId, excluding: null);
        return Ack();
    }

    private Response SetMemberRank(ClientConnection connection, SetMemberRank request) {
        var me = RequireUser(connection);
        var channelId = RequireChannelId(request.ChannelId);
        this.RequireAllowed(channelId, me, ChannelAction.SetRank);

        if (request.UserId == me.UserId) {
            throw new RequestException(ErrorCode.Forbidden, "You can't change your own rank.");
        }

        if (!Policy.IsAssignableRank(request.Rank)) {
            throw new RequestException(ErrorCode.InvalidRequest, "Invalid rank.");
        }

        var target = db.GetUser(request.UserId) ?? throw new RequestException(ErrorCode.NotFound, "No such user.");
        var targetRank = db.GetRank(channelId, target.UserId);
        if (targetRank is null or Rank.Invited) {
            throw new RequestException(ErrorCode.NotFound, $"{target.Name} isn't a member of this channel.");
        }

        if (request.Rank == Rank.Admin) {
            if (!db.TransferAdmin(channelId, me.UserId, target.UserId)) {
                throw new RequestException(ErrorCode.Conflict, "Membership changed; try again.");
            }

            this.BroadcastMemberChange(channelId, target, MemberChangeKind.RankChanged, Rank.Admin, me);
            this.BroadcastMemberChange(channelId, me, MemberChangeKind.RankChanged, Rank.Moderator, me);
        } else if (db.SetRank(channelId, target.UserId, request.Rank)) {
            this.BroadcastMemberChange(channelId, target, MemberChangeKind.RankChanged, request.Rank, me);
        } else if (db.GetRank(channelId, target.UserId) != request.Rank) {
            // Not a no-op: they left (or the admin changed) since the checks above.
            throw new RequestException(ErrorCode.Conflict, "Membership changed; try again.");
        }

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

        this.ValidateName(request.Name, channelId, channel.Epoch, me);
        if (request.Name.Revision > ProtocolInfo.MaxNameRevision) {
            throw new RequestException(ErrorCode.InvalidRequest, "The name's revision is out of range.");
        }

        if (channel.Name is { } current && current.Epoch == request.Name.Epoch && request.Name.Revision <= current.Revision) {
            // Clients refuse a name that isn't newer than theirs, so storing it would hide later renames.
            throw new RequestException(ErrorCode.Conflict, "The name's revision must be newer than the current one; refresh and try again.");
        }

        if (!db.RenameChannel(channelId, request.Name)) {
            throw new RequestException(ErrorCode.Conflict, "The channel changed while renaming; try again.");
        }
        var members = db.GetMembers(channelId).Select(member => member.User.UserId);
        registry.SendToAll(members, new Event { ChannelRenamed = new ChannelRenamed { ChannelId = channelId, Name = request.Name } }, except: me.UserId);
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

        foreach (var key in request.Keys) {
            if (key.Box == null || !ChannelCrypto.VerifyEpochKey(key, channelId, request.NewEpoch, me.UserId, me.SigningKey)) {
                throw new RequestException(ErrorCode.InvalidRequest, "A key in the rekey is wrongly signed.");
            }
        }

        this.ValidateName(request.Name, channelId, request.NewEpoch, me);
        RequireFirstRevision(request.Name);

        switch (db.ApplyRekey(channelId, request.NewEpoch, me.UserId, request.Keys, request.Name)) {
            case RekeyResult.EpochStale:
                throw new RequestException(ErrorCode.EpochStale, "The channel is no longer at that epoch.");
            case RekeyResult.MembershipChanged:
                throw new RequestException(ErrorCode.Conflict, "Membership changed; rekey for the current members.");
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
        if (!ChannelCrypto.VerifyMessage(message, me.SigningKey)) {
            throw new RequestException(ErrorCode.InvalidRequest, "Message signature is invalid.");
        }

        var members = db.GetMembers(channelId).Select(member => member.User.UserId);
        registry.SendToAll(members, new Event { ChatMessage = message }, except: me.UserId);
        return Ack();
    }

    // ================================================================ helpers

    private ChannelInfo BuildChannelInfo(ChannelRow channel, long viewerId) {
        var info = new ChannelInfo {
            ChannelId = channel.ChannelId,
            Epoch = channel.Epoch,
            RekeyPending = channel.RekeyPending,
            Name = channel.Name,
            MyRank = db.GetRank(channel.ChannelId, viewerId) ?? Rank.Unspecified,
        };

        foreach (var member in db.GetMembers(channel.ChannelId).Concat(db.GetInvitees(channel.ChannelId))) {
            info.Members.Add(new Member { User = member.User.ToProto(), Rank = member.Rank });
        }

        return info;
    }

    /// <summary>
    /// Marks the channel as needing a rekey and asks one online member to do
    /// it: <paramref name="preferred"/> if possible, otherwise the
    /// highest-ranked online member. Everyone online is told.
    /// </summary>
    private void RequestRekey(string channelId, long? preferred, long? excluding) {
        var channel = db.GetChannel(channelId);
        if (channel == null) {
            return;
        }

        var members = db.GetMembers(channelId);
        var online = members.Where(member => registry.IsOnline(member.User.UserId)).ToList();
        var candidates = online.Where(member => member.User.UserId != excluding).ToList();
        if (candidates.Count == 0) {
            candidates = online;
        }

        var designated = candidates.FirstOrDefault(member => member.User.UserId == preferred)
                         ?? candidates.OrderByDescending(member => member.Rank).ThenBy(member => member.User.UserId).FirstOrDefault();

        var ev = new Event {
            RekeyNeeded = new RekeyNeeded {
                ChannelId = channelId,
                CurrentEpoch = channel.Epoch,
                DesignatedUserId = designated?.User.UserId ?? 0,
            },
        };
        ev.RekeyNeeded.MemberIds.AddRange(members.Select(member => member.User.UserId));
        registry.SendToAll(online.Select(member => member.User.UserId), ev);
    }

    private void BroadcastMemberChange(string channelId, UserRow user, MemberChangeKind kind, Rank rank, UserRow actor, long? except = null) {
        var recipients = db.GetMembers(channelId).Select(member => member.User.UserId).ToList();
        registry.SendToAll(recipients, new Event {
            MemberChanged = new MemberChanged {
                ChannelId = channelId,
                User = user.ToProto(),
                Kind = kind,
                Rank = rank,
                Actor = actor.ToProto(),
            },
        }, except);
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

    private void ValidateName(EncryptedName? name, string channelId, ulong epoch, UserRow author) {
        if (name == null || name.Epoch != epoch || name.AuthorId != author.UserId
            || name.Ciphertext.Length is 0 or > 512
            || !ChannelCrypto.VerifyName(name, channelId, author.SigningKey)) {
            throw new RequestException(ErrorCode.InvalidRequest, "The encrypted channel name is missing or wrongly signed.");
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
    };

    private static Response Ack() => new() { Ack = new Ack() };

    private static Response Error(ErrorCode code, string message) => new() { Error = new Protocol.Error { Code = code, Message = message } };

    /// <summary>Debug accounts get stable negative IDs derived from their name.</summary>
    internal static long DebugUserId(string name) {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name.Trim().ToLowerInvariant()));
        return -((BitConverter.ToInt64(hash, 0) & 0x001F_FFFF_FFFF_FFFF) + 1);
    }

    internal static byte[] HashToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

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
