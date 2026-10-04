using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Google.Protobuf;
using WonderlandChat.Core.Crypto;
using WonderlandChat.Core.Membership;
using WonderlandChat.Protocol;

namespace WonderlandChat.Core.Client;

/// <summary>
/// A client's connection to a WonderlandChat server for one character.
///
/// Threading: all mutable state lives behind <see cref="_lock"/> and is only
/// touched in short synchronous sections, never across an await. After every
/// change an immutable <see cref="SessionSnapshot"/> is published; UI code
/// reads only snapshots. Network I/O happens in <see cref="Connection"/>.
/// Server events are processed in order by one inbox task.
///
/// Trust: who is in a channel, with what rank and keys, comes only from the
/// channel's membership log as this client verified it (<see cref="IMembershipProvider"/>),
/// never from the server's word. Keys are sealed to, and signatures checked
/// against, the keys in that log.
/// </summary>
public sealed class ClientSession : IAsyncDisposable {
    private const int KeptEpochsPerChannel = 4;
    private const int SeenMessageCapacity = 2048;
    // Identities per GetIdentities when the server doesn't say (older servers allow 500),
    // and the most asked for at once whatever it says.
    private const int DefaultIdentityBatch = 100;
    private const int MaxIdentityBatch = 1000;
    // Pages of log entries fetched in one go, at most: a channel with a longer log catches up over several.
    private const int MaxLogPagesPerSync = 40;
    private static readonly TimeSpan MaxMessageClockSkew = TimeSpan.FromMinutes(10);
    // How far a sender's messages may arrive out of order before they count as replays.
    private static readonly TimeSpan MessageReorderAllowance = TimeSpan.FromMinutes(2);
    // How long messages under an older epoch are accepted after a newer key arrives
    // (only those in flight during the rekey are legitimate; the server rejects new ones).
    private static readonly TimeSpan OldEpochGrace = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReplayStateSaveInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RegistrationRequestTimeout = TimeSpan.FromSeconds(60);
    // How often one user's identity may be fetched again because something they signed didn't verify.
    private static readonly TimeSpan IdentityRefetchInterval = TimeSpan.FromMinutes(1);
    // How often a channel's whole log may be fetched again to look into a possible fork.
    private static readonly TimeSpan ForkCheckInterval = TimeSpan.FromMinutes(1);

    private readonly ClientSessionOptions _options;
    private readonly ISecretStore _store;
    private readonly IMembershipProvider _membership;
    private readonly IGroupKeyProvider _groupKeys;
    private readonly Lock _lock = new();
    private readonly Lock _saveLock = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _channelLocks = new();
    // One log sync per channel at a time, so entries are checked in order against one state.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _logLocks = new();
    private readonly Channel<Event> _inbox = Channel.CreateUnbounded<Event>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentQueue<TraceEntry> _trace = new();
    private readonly ConcurrentDictionary<Task, byte> _background = new();

    // ---- state guarded by _lock
    private readonly ClientSecrets _secrets;
    private IdentityKeys? _identity;
    private MemberKeys? _myKeys;
    private ConnectionState _state = ConnectionState.Stopped;
    private string? _status;
    private User? _me;
    private Limits? _limits;
    private bool _debugAccountsEnabled;
    private RegistrationChallenge? _challenge;
    private readonly Dictionary<string, ChannelState> _channels = new();
    // _channels holds the server's complete list, fetched on the current connection.
    private bool _channelsLoaded;
    private readonly Dictionary<string, InviteState> _invites = new();
    // Verified membership per channel (and per channel invited to). Saved in _secrets.Memberships.
    private readonly Dictionary<string, IChannelMembership> _memberships = new();
    private readonly Dictionary<long, UserIdentity> _identities = new();
    // Names and worlds of users seen in channel lists and log events, for display only.
    private readonly Dictionary<long, User> _users = new();
    // When each user's identity was last fetched again after a failed check (entries expire).
    private readonly Dictionary<long, DateTimeOffset> _identityRefetchedAt = new();
    // Those fetches still under way, so another check that fails meanwhile waits for the same one.
    private readonly Dictionary<long, Task> _identityRefetches = new();
    // Authors of names that failed verification, and the keys they failed against; Publish has their identities fetched again.
    private readonly Dictionary<long, MemberKeys> _staleNameAuthors = new();
    // Channels with a name made at a log position this client hasn't reached; Publish has their logs fetched.
    private readonly Dictionary<string, ulong> _namePositionsAhead = new();
    private readonly Dictionary<string, DateTimeOffset> _forkCheckedAt = new();
    private readonly HashSet<string> _seenMessages = new();
    private readonly Queue<string> _seenOrder = new();
    private long _secretsVersion;
    // NewestMessageTimes changed but hasn't been saved; the next save includes it.
    private bool _replayStateDirty;
    private DateTimeOffset _replayStateSavedAt = DateTimeOffset.MinValue;
    // Notices found while holding the lock; Publish raises them.
    private readonly List<SessionNotice> _pendingNotices = new();
    // ----

    private long _savedSecretsVersion;
    private volatile SessionSnapshot _snapshot = SessionSnapshot.Empty;
    private volatile Connection? _connection;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private Task? _inboxTask;
    private volatile bool _reconnectImmediately;
    private int _disposed;

    public ClientSession(ClientSessionOptions options, ISecretStore store) {
        this._options = options;
        this._store = store;
        this._membership = options.Membership;
        this._groupKeys = options.GroupKeys;
        this._secrets = store.Load();

        if (this._secrets.SigningPrivateKey != null && this._secrets.AgreementPrivateKey != null) {
            this._identity = IdentityKeys.Import(this._secrets.SigningPrivateKey, this._secrets.AgreementPrivateKey);
            this._myKeys = MemberKeys.Of(this._identity);
        }

        this.Publish();
    }

    // ================================================================ public surface

    public SessionSnapshot Snapshot => this._snapshot;

    /// <summary>Raised on a background thread after every state change.</summary>
    public event Action<SessionSnapshot>? SnapshotChanged;

    /// <summary>Raised on a background thread for every decrypted message, including your own once the server accepts it.</summary>
    public event Action<IncomingMessage>? MessageReceived;

    /// <summary>Raised on a background thread for things the user should be told about.</summary>
    public event Action<SessionNotice>? Notice;

    /// <summary>Raised on a background thread when an invite has been verified and decrypted.</summary>
    public event Action<InviteView>? InviteReceived;

    public Uri ServerUri => this._options.ServerUri;

    public IReadOnlyList<TraceEntry> GetTrace() => this._trace.ToArray();

    public void Start() {
        if (this._runTask != null) {
            return;
        }

        this._runCts = new CancellationTokenSource();
        this._inboxTask = Task.Run(() => this.InboxLoop(this._runCts.Token));
        this._runTask = Task.Run(() => this.RunLoop(this._runCts.Token));
    }

    /// <summary>Drops the current connection and reconnects straight away.</summary>
    public void Reconnect() {
        this._reconnectImmediately = true;
        this._connection?.Abort("Reconnect requested");
    }

    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref this._disposed, 1) == 1) {
            return;
        }

        this._runCts?.Cancel();
        this._inbox.Writer.TryComplete();
        this._connection?.Abort("Session stopped");

        try {
            if (this._runTask != null) {
                await this._runTask;
            }

            if (this._inboxTask != null) {
                await this._inboxTask;
            }

            // Background work was cancelled above; wait briefly so none of it
            // touches the identity or secrets after they're released.
            await Task.WhenAll(this._background.Keys).WaitAsync(TimeSpan.FromSeconds(5));
        } catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) {
            // Expected while stopping.
        }

        this.SaveSecrets();

        lock (this._lock) {
            this._identity?.Dispose();
            this._identity = null;
        }

        this._runCts?.Dispose();
    }

    /// <summary>Round-trip time to the server.</summary>
    public async Task<TimeSpan> PingAsync(CancellationToken ct = default) {
        var started = DateTimeOffset.UtcNow;
        await this.RequestAsync(new ClientFrame { Ping = new Ping() }, ct);
        return DateTimeOffset.UtcNow - started;
    }

    /// <summary>Sends a request exactly as given, bypassing every client check. Only for tests that play a misbehaving client.</summary>
    internal Task<Response> SendRawAsync(ClientFrame frame, CancellationToken ct = default) => this.RequestAsync(frame, ct);

    /// <summary>Runs between a kick's request and its bookkeeping, so tests can force the race with a background rekey.</summary>
    internal Func<Task>? AfterKickRequestForTests { get; set; }

    /// <summary>This client's verified membership of a channel, for tests that forge what a member could sign.</summary>
    internal IChannelMembership MembershipForTests(string channelId) => this.Read(() => this.MembershipOf(channelId));

    // ================================================================ registration

    public async Task<RegistrationChallenge> StartRegistrationAsync(Character character, CancellationToken ct = default) {
        var identity = this.EnsureIdentity();
        // Lodestone lookups are queued server-side, so allow more time than usual.
        var response = await this.RequestAsync(this.RequireConnection(), new ClientFrame {
            StartRegistration = new StartRegistration { Character = character, Identity = identity.ToBundle() },
        }, ct, RegistrationRequestTimeout);

        var challenge = response.RegistrationChallenge ?? throw Unexpected(response);
        lock (this._lock) {
            this._challenge = challenge;
            this._state = ConnectionState.Registering;
            this._status = challenge.VerificationSkipped
                ? "Debug account: no Lodestone verification needed."
                : $"Put {challenge.Code} in your Lodestone profile, then verify.";
        }

        this.Publish();
        return challenge;
    }

    public async Task CompleteRegistrationAsync(CancellationToken ct = default) {
        var connection = this.RequireConnection();
        var response = await this.RequestAsync(connection, new ClientFrame { CompleteRegistration = new CompleteRegistration() }, ct, RegistrationRequestTimeout);
        var complete = response.RegistrationComplete ?? throw Unexpected(response);

        lock (this._lock) {
            this._secrets.DeviceToken = complete.DeviceToken;
            this._secrets.UserId = complete.User.UserId;
            this._challenge = null;
            this._secretsVersion++;
        }

        this.SaveSecrets();
        await this.AuthenticateAsync(connection, ct);
    }

    /// <summary>
    /// Marks a user's current keys verified, after comparing fingerprints with them over /tell:
    /// clears the "key changed" warning and the "fingerprint not compared" state.
    /// </summary>
    public void AcknowledgeKeyChange(long userId) {
        lock (this._lock) {
            if (this._secrets.PinnedIdentities.TryGetValue(userId, out var pinned) && (pinned.KeyChangeUnacknowledged || !pinned.Compared)) {
                pinned.KeyChangeUnacknowledged = false;
                pinned.Compared = true;
                this._secretsVersion++;
            }
        }

        this.SaveSecrets();
        this.Publish();
    }

    /// <summary>
    /// Blocks a user: their invites are declined without being shown, and their
    /// messages are hidden. Pending invites from them are declined now.
    /// </summary>
    public void BlockUser(long userId) {
        List<string> declined;
        lock (this._lock) {
            if (userId == this._me?.UserId) {
                throw new InvalidOperationException("You can't block yourself.");
            }

            if (this._secrets.BlockedUsers.Add(userId)) {
                this._secretsVersion++;
            }

            declined = this._invites.Values.Where(invite => invite.Info.Inviter.UserId == userId).Select(invite => invite.Info.ChannelId).ToList();
            foreach (var channelId in declined) {
                this._invites.Remove(channelId);
            }
        }

        this.SaveSecrets();
        this.Publish();
        foreach (var channelId in declined) {
            this.DeclineQuietly(channelId);
        }
    }

    public void UnblockUser(long userId) {
        lock (this._lock) {
            if (this._secrets.BlockedUsers.Remove(userId)) {
                this._secretsVersion++;
            }
        }

        this.SaveSecrets();
        this.Publish();
    }

    /// <summary>Declines an invite without telling the user (it came from someone they blocked).</summary>
    private void DeclineQuietly(string channelId) {
        this.RunBackground("Declining an invite", async ct => {
            try {
                await this.AnswerInviteAsync(channelId, accept: false, ct);
            } catch (Exception ex) when (ex is ServerErrorException or SessionDisconnectedException or InvalidOperationException) {
                // Already gone, or offline: it is declined again on the next refresh.
            }
        });
    }

    private bool IsBlocked(long userId) => this._secrets.BlockedUsers.Contains(userId);

    /// <summary>Forgets the device token, for example to register again.</summary>
    public void ForgetAccount() {
        lock (this._lock) {
            this._secrets.DeviceToken = null;
            this._secrets.UserId = null;
            this._secretsVersion++;
        }

        this.SaveSecrets();
        this.Reconnect();
    }

    // ================================================================ channels

    public async Task<string> CreateChannelAsync(string name, CancellationToken ct = default) {
        name = ValidateChannelName(name);
        var (identity, me) = this.RequireIdentityAndUser();
        var channelId = Guid.NewGuid().ToString("N");

        // The log starts with this client as admin; the first key and name are made for that position.
        var genesis = this._membership.CreateGenesis(channelId, identity, me.UserId, this.NowMs());
        var membership = this._membership.Empty(channelId).Apply(genesis);
        var position = membership.Head!;
        var key = this._groupKeys.NewEpochKey();
        var creatorKey = this._groupKeys.SealToMembers(key, channelId, 0, position, membership.Members, identity, me.UserId).Keys.Single();

        var response = await this.RequestAsync(new ClientFrame {
            CreateChannel = new CreateChannel {
                ChannelId = channelId,
                Genesis = genesis,
                CreatorKey = creatorKey,
                Name = this._groupKeys.EncryptName(name, key, channelId, 0, position, identity, me.UserId),
            },
        }, ct);

        var info = response.Channel ?? throw Unexpected(response);
        if (info.ChannelId != channelId) {
            throw Unexpected(response);
        }

        lock (this._lock) {
            this.SetMembership(channelId, membership, []);
            var channel = this.ApplyChannelInfo(info);
            this.StoreEpochKey(channelId, 0, key, position);
            channel.Name = name;
            this.SetNameVersion(channelId, new NameVersion(0, 0));
        }

        this.SaveSecrets();
        this.Publish();
        return channelId;
    }

    public async Task InviteAsync(string channelId, string name, string worldName, CancellationToken ct = default) {
        var (identity, me) = this.RequireIdentityAndUser();
        var channelName = this.Read(() => this._channels.GetValueOrDefault(channelId)?.Name)
            ?? throw new InvalidOperationException("The channel name isn't known yet, so it can't be shared with an invitee.");

        var response = await this.RequestAsync(new ClientFrame {
            LookupUser = new LookupUser { Name = name.Trim(), WorldName = worldName.Trim() },
        }, ct);

        var found = response.Identities?.Identities_.FirstOrDefault() ?? throw new InvalidOperationException($"{name}@{worldName} isn't registered with WonderlandChat.");
        // Trusted on first use: the server says these are their keys (see "fingerprint not compared").
        var invitee = this.AcceptIdentities([found]).FirstOrDefault()
            ?? throw new InvalidOperationException($"{name}@{worldName} has an invalid identity key.");
        var inviteeKeys = MemberKeys.Of(invitee.Identity);
        var who = $"{invitee.User.Name}@{invitee.User.WorldName}";

        await this.AppendEntryAsync(channelId, ct, membership => {
            if (membership.FindMember(invitee.User.UserId) is { } existing) {
                throw new InvalidOperationException(existing.Keys == inviteeKeys
                    ? $"{who} is already a member."
                    : $"{who} is a member under the identity key they had before registering again. Remove them, then invite them again.");
            }

            var entry = membership.Create(MembershipEntryKind.Invite, invitee.User.UserId, identity, me.UserId, this.NowMs(), inviteeKeys);
            var (sealedName, signature) = this._groupKeys.SealInvite(channelName, channelId, invitee.User.UserId, inviteeKeys.AgreementPublicKey, identity, me.UserId);
            return (entry, new ClientFrame {
                InviteMember = new InviteMember {
                    ChannelId = channelId,
                    Entry = entry,
                    SealedName = sealedName,
                    Signature = ByteString.CopyFrom(signature),
                },
            });
        });
    }

    public async Task RespondToInviteAsync(string channelId, bool accept, CancellationToken ct = default) {
        Response response;
        try {
            response = await this.AnswerInviteAsync(channelId, accept, ct);
        } catch (Exception ex) when (ex is ServerErrorException { Code: ErrorCode.NotFound } or MembershipException) {
            // The invite (or its channel) is gone: stop offering it.
            lock (this._lock) {
                this._invites.Remove(channelId);
            }

            this.Publish();
            throw;
        }

        var designated = false;
        lock (this._lock) {
            this._invites.Remove(channelId, out var invite);
            if (accept && response.Channel != null && response.Channel.ChannelId == channelId) {
                var channel = this.ApplyChannelInfo(response.Channel);
                channel.Name ??= invite?.Name;
                designated = response.Channel.RekeyDesignated;
            }
        }

        this.Publish();

        if (accept) {
            await this.EnsureChannelReadyAsync(channelId, ct);
            var (hasKey, pending, nameKnown) = this.Read(() => {
                var channel = this._channels.GetValueOrDefault(channelId);
                return (this.HasCurrentKey(channelId), channel != null && this.NeedsRekey(channel), channel?.Name != null);
            });
            if (designated && pending && nameKnown) {
                // Nobody else is online to share the key. (The server's RekeyNeeded arrived before
                // this response, while the channel was unknown, so it was ignored.)
                if (this._options.AutoRekeyWhenDesignated) {
                    this.RaiseNotice(NoticeLevel.Info, "Joined. No other member is online, so you're making the channel a new key.", channelId);
                    this.RunBackground("Rekeying a channel", rekeyCt => this.RekeyAsync(channelId, rekeyCt));
                } else {
                    this.RaiseNotice(NoticeLevel.Info, "Joined. No other member is online to share the channel key; rekey the channel, or send a message, to make a new one.", channelId);
                }
            } else if (!hasKey) {
                this.RaiseNotice(NoticeLevel.Info, "Joined. Waiting for a member to share the channel key.", channelId);
            }
        }
    }

    /// <summary>Signs and sends an accept or decline for the open invite in the channel's verified log.</summary>
    private async Task<Response> AnswerInviteAsync(string channelId, bool accept, CancellationToken ct) {
        var (identity, me) = this.RequireIdentityAndUser();
        return await this.AppendEntryAsync(channelId, ct, membership => {
            var invited = membership.FindInvitee(me.UserId)
                          ?? throw new MembershipException(new MembershipVerdict(MembershipVerdictKind.Conflict, "That invite is no longer open."));
            if (invited.Keys != MemberKeys.Of(identity)) {
                throw new InvalidOperationException("That invite was made for the identity key you had before you registered again. Ask to be invited again.");
            }

            var entry = membership.Create(accept ? MembershipEntryKind.Accept : MembershipEntryKind.Decline, me.UserId, identity, me.UserId, this.NowMs());
            return (entry, new ClientFrame { RespondToInvite = new RespondToInvite { ChannelId = channelId, Entry = entry } });
        });
    }

    public async Task LeaveAsync(string channelId, CancellationToken ct = default) {
        var (identity, me) = this.RequireIdentityAndUser();
        await this.AppendEntryAsync(channelId, ct, membership => {
            var entry = membership.Create(MembershipEntryKind.Leave, me.UserId, identity, me.UserId, this.NowMs());
            return (entry, new ClientFrame { LeaveChannel = new LeaveChannel { ChannelId = channelId, Entry = entry } });
        });
        this.RemoveChannel(channelId);
    }

    public async Task KickAsync(string channelId, long userId, CancellationToken ct = default) {
        var (identity, me) = this.RequireIdentityAndUser();
        var removed = false;
        await this.AppendEntryAsync(channelId, ct, membership => {
            // Cancelling an invite needs no rekey: the invitee never had a key.
            removed = membership.FindInvitee(userId) == null;
            var entry = membership.Create(removed ? MembershipEntryKind.Remove : MembershipEntryKind.CancelInvite, userId, identity, me.UserId, this.NowMs());
            return (entry, new ClientFrame { KickMember = new KickMember { ChannelId = channelId, Entry = entry } });
        });

        if (this.AfterKickRequestForTests is { } hook) {
            await hook();
        }

        this.Publish();
        if (removed) {
            // The remover rotates the key straight away, so the removed member is cut off. (The server
            // asks a member to rekey before it answers, so a background rekey may already be done.)
            await this.RekeyAsync(channelId, ct);
        }
    }

    public async Task SetRankAsync(string channelId, long userId, Rank rank, CancellationToken ct = default) {
        var (identity, me) = this.RequireIdentityAndUser();
        var membership = await this.SyncLogAsync(channelId, ct);
        if (membership.FindMember(userId)?.Rank == rank) {
            return;
        }

        await this.AppendEntryAsync(channelId, ct, current => {
            var entry = rank == Rank.Admin
                ? current.Create(MembershipEntryKind.TransferAdmin, userId, identity, me.UserId, this.NowMs())
                : current.Create(MembershipEntryKind.SetRank, userId, identity, me.UserId, this.NowMs(), rank: rank);
            return (entry, new ClientFrame { SetMemberRank = new SetMemberRank { ChannelId = channelId, Entry = entry } });
        });
    }

    public async Task DisbandAsync(string channelId, CancellationToken ct = default) {
        await this.RequestAsync(new ClientFrame { DisbandChannel = new DisbandChannel { ChannelId = channelId } }, ct);
        this.RemoveChannel(channelId);
    }

    public async Task RenameAsync(string channelId, string newName, CancellationToken ct = default) {
        newName = ValidateChannelName(newName);
        var (identity, me) = this.RequireIdentityAndUser();
        for (var attempt = 0; ; attempt++) {
            var membership = await this.SyncLogAsync(channelId, ct, fetch: attempt == 0 ? LogFetch.IfBehind : LogFetch.Always);
            var (epoch, key, revision) = this.Read(() => {
                var channel = this._channels.GetValueOrDefault(channelId) ?? throw new InvalidOperationException("Unknown channel.");
                var keyEpoch = this.KeyEpochOf(channelId) ?? throw new InvalidOperationException("This channel's key isn't available yet.");

                // The server and other members refuse a name that isn't newer than the
                // current one, so beat both the name accepted here and the one offered.
                ulong current = 0;
                if (this._secrets.ChannelNameVersions.TryGetValue(channelId, out var held) && held.Epoch == keyEpoch) {
                    current = held.Revision;
                }

                if (channel.EncryptedName is { } offered && offered.Epoch == keyEpoch
                    && membership.FindMember(offered.AuthorId) is { } author
                    && this._groupKeys.VerifyName(offered, channelId, author.Keys.SigningPublicKey)) {
                    current = Math.Max(current, offered.Revision);
                }

                // Only reachable if someone set a huge revision on purpose. It resets with the next epoch.
                if (current >= ProtocolInfo.MaxNameRevision) {
                    throw new InvalidOperationException("This channel can't be renamed again until its key changes. Rekey it, then rename it.");
                }

                return (keyEpoch, this.GetEpochKey(channelId, keyEpoch)!, current + 1);
            });

            var name = this._groupKeys.EncryptName(newName, key, channelId, epoch, membership.Head!, identity, me.UserId, revision);
            try {
                await this.RequestAsync(new ClientFrame { RenameChannel = new RenameChannel { ChannelId = channelId, Name = name } }, ct);
            } catch (ServerErrorException ex) when (ex.Code == ErrorCode.Conflict && attempt < 2) {
                // The log moved on (or the name did): catch up and try again.
                continue;
            }

            lock (this._lock) {
                if (this._channels.TryGetValue(channelId, out var channel)) {
                    channel.EncryptedName = name;
                    channel.Name = newName;
                    this.SetNameVersion(channelId, new NameVersion(epoch, revision));
                }
            }

            this.SaveSecrets();
            this.Publish();
            return;
        }
    }

    /// <summary>Re-fetches channels, invites, identities and keys from the server.</summary>
    public Task RefreshAsync(CancellationToken ct = default) => this.RefreshAsync(this.RequireConnection(), ct, refreshIdentities: true);

    /// <summary>
    /// Makes a membership log entry against the channel's verified log, sends it, and applies it.
    /// If the log moved on meanwhile (the server says CONFLICT), catches up and makes it again.
    /// </summary>
    /// <param name="make">Makes the entry and the request carrying it from the current membership; throws if the change isn't allowed.</param>
    private async Task<Response> AppendEntryAsync(string channelId, CancellationToken ct, Func<IChannelMembership, (MembershipEntry Entry, ClientFrame Request)> make) {
        for (var attempt = 0; ; attempt++) {
            var membership = await this.SyncLogAsync(channelId, ct, fetch: attempt == 0 ? LogFetch.IfBehind : LogFetch.Always);
            MembershipEntry entry;
            ClientFrame request;
            try {
                (entry, request) = make(membership);
            } catch (MembershipException) when (attempt == 0) {
                // Not allowed by this client's copy of the log, which may be behind (say, after the
                // server stored an entry made elsewhere): catch up, and decide on that.
                continue;
            }

            Response response;
            try {
                response = await this.RequestAsync(request, ct);
            } catch (ServerErrorException ex) when (ex.Code == ErrorCode.Conflict && attempt < 2) {
                continue;
            }

            // The server has stored it: ours to apply too (checked like any other entry).
            await this.SyncLogAsync(channelId, ct, offered: [entry], fetch: LogFetch.Never);
            return response;
        }
    }

    // ================================================================ rekeying

    /// <summary>
    /// Moves the channel to a new epoch: a fresh key sealed to exactly the members in the
    /// verified log, at its head. Safe to call concurrently; the server accepts one rekey per
    /// epoch, and only for the log's head.
    /// </summary>
    /// <param name="force">Rekey even if no membership change is pending (debug tool).</param>
    public async Task RekeyAsync(string channelId, CancellationToken ct = default, bool force = false) {
        var gate = this._channelLocks.GetOrAdd(channelId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try {
            var behindServer = false;
            for (var attempt = 0; attempt < 3; attempt++) {
                var (identity, me) = this.RequireIdentityAndUser();
                var membership = await this.SyncLogAsync(channelId, ct, fetch: attempt == 0 ? LogFetch.IfBehind : LogFetch.Always);
                var (serverEpoch, name, nameVersion, pending) = this.Read(() => {
                    var channel = this._channels.GetValueOrDefault(channelId) ?? throw new InvalidOperationException("Unknown channel.");
                    return (channel.ServerEpoch, channel.Name, channel.NameVersion, this.NeedsRekey(channel));
                });

                // Someone else (or an earlier call) may have rekeyed while we waited for the gate.
                if (!pending && !force) {
                    return;
                }

                if (name == null) {
                    throw new InvalidOperationException("You don't have this channel's key yet, so you can't rekey it. Another member needs to.");
                }

                if (membership.FindMember(me.UserId)?.Keys != MemberKeys.Of(identity)) {
                    throw new InvalidOperationException("You can't rekey this channel: your place in it belongs to an identity key you no longer have. A moderator must remove you and invite you again.");
                }

                var position = membership.Head!;
                // The server only accepts its current epoch + 1. If its hint is wrong, it answers EPOCH_STALE.
                var newEpoch = serverEpoch + 1;
                var key = this._groupKeys.NewEpochKey();
                // Say which name is carried over, so members who know it can tell it wasn't changed.
                // A name known only from the invite has no version to name.
                var source = nameVersion == null ? null : new NameSource { Epoch = nameVersion.Epoch, Revision = nameVersion.Revision };

                EpochRekey sealedKeys;
                try {
                    sealedKeys = this._groupKeys.SealToMembers(key, channelId, newEpoch, position, membership.Members, identity, me.UserId);
                } catch (SealingFailedException ex) {
                    // Name the member at fault: otherwise one bad key leaves everyone guessing why the channel is stuck.
                    var who = this.Read(() => this.UserOf(ex.Member.UserId));
                    throw new InvalidOperationException($"Can't rekey: the key couldn't be sealed to {who.Name}@{who.WorldName}'s identity key ({ex.InnerException?.Message}). They need to be removed.", ex);
                }

                var request = new SubmitRekey {
                    ChannelId = channelId,
                    NewEpoch = newEpoch,
                    Name = this._groupKeys.EncryptName(name, key, channelId, newEpoch, position, identity, me.UserId, carriedFrom: source),
                    KeyCommitment = sealedKeys.KeyCommitment,
                    LogPosition = position.Clone(),
                };
                request.Keys.AddRange(sealedKeys.Keys);

                try {
                    await this.RequestAsync(new ClientFrame { SubmitRekey = request }, ct);
                } catch (ServerErrorException ex) when (ex.Code is ErrorCode.Conflict or ErrorCode.EpochStale) {
                    // Someone else rekeyed first, or membership changed meanwhile.
                    await this.RefreshAsync(this.RequireConnection(), ct);
                    behindServer = this.Read(() => this._channels.GetValueOrDefault(channelId)?.LogHead is { } head && head.Seq < position.Seq);
                    force = false;
                    continue;
                }

                lock (this._lock) {
                    this.StoreEpochKey(channelId, newEpoch, key, position);
                    if (this._channels.TryGetValue(channelId, out var channel)) {
                        channel.ServerEpoch = Math.Max(channel.ServerEpoch, newEpoch);
                        channel.RekeyPending = false;
                        if (this.KeyEpochOf(channelId) == newEpoch) {
                            channel.EncryptedName = request.Name;
                            channel.Name = name;
                            this.SetNameVersion(channelId, new NameVersion(newEpoch, 0));
                        }
                    }
                }

                this.SaveSecrets();
                this.Publish();
                this.Log(NoticeLevel.Debug, $"Rekeyed {channelId} to epoch {newEpoch} for {sealedKeys.Keys.Count} members at log entry {position.Seq}");
                return;
            }

            throw new InvalidOperationException(behindServer
                ? "The server refuses the new key: it says the channel's membership is older than the change you made. It may be hiding that change from the other members."
                : "Rekeying kept conflicting with other changes; try again.");
        } finally {
            gate.Release();
        }
    }

    // ================================================================ messages

    public async Task SendTextAsync(string channelId, string text, CancellationToken ct = default) {
        if (string.IsNullOrWhiteSpace(text)) {
            throw new ArgumentException("Message is empty.", nameof(text));
        }

        var content = new Content { Text = new TextContent { Text = text } };
        for (var attempt = 0; attempt < 4; attempt++) {
            var (identity, me) = this.RequireIdentityAndUser();
            var pending = this.Read(() => {
                var channel = this._channels.GetValueOrDefault(channelId) ?? throw new InvalidOperationException("You're not in that channel.");
                if (!this.IsMember(channelId)) {
                    throw new InvalidOperationException(this.MembershipOf(channelId).FindMember(me.UserId) != null
                        ? "Your place in that channel belongs to an identity key you no longer have. A moderator must remove you and invite you again."
                        : "You haven't joined that channel.");
                }

                return this.NeedsRekey(channel);
            });

            if (pending) {
                await this.RekeyAsync(channelId, ct);
                continue;
            }

            if (!this.Read(() => this.HasCurrentKey(channelId))) {
                // Behind the server: fetch the newer key. If there is none for us, the fetch marks
                // the channel for a rekey, and the next attempt moves it to a key we hold.
                await this.FetchEpochKeysAsync(channelId, ct);
                if (this.Read(() => this.KeyEpochOf(channelId)) == null) {
                    throw new InvalidOperationException("You don't have this channel's key yet. A member who is online will share it.");
                }

                if (this.Read(() => this._channels.GetValueOrDefault(channelId) is { } channel && this.NeedsRekey(channel))) {
                    continue;
                }
            }

            // Always the newest key accepted, never an epoch the server merely claims.
            var (epoch, key) = this.Read(() => {
                var held = this.KeyEpochOf(channelId) ?? throw new InvalidOperationException("You don't have this channel's key yet.");
                return (held, this.GetEpochKey(channelId, held)!);
            });

            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var message = this._groupKeys.EncryptMessage(content, key, channelId, epoch, identity, me.UserId, timestamp);
            var maxBytes = this.Read(() => this._limits?.MaxMessageBytes ?? 4096);
            if (message.Ciphertext.Length > maxBytes) {
                throw new InvalidOperationException("That message is too long.");
            }

            try {
                await this.RequestAsync(new ClientFrame { SendMessage = message }, ct);
            } catch (ServerErrorException ex) when (ex.Code == ErrorCode.RekeyRequired) {
                lock (this._lock) {
                    if (this._channels.TryGetValue(channelId, out var channel)) {
                        channel.RekeyPending = true;
                    }
                }

                continue;
            } catch (ServerErrorException ex) when (ex.Code == ErrorCode.EpochStale) {
                await this.RefreshAsync(this.RequireConnection(), ct);
                continue;
            }

            lock (this._lock) {
                this.MarkSeen(Convert.ToHexString(message.MessageId.Span));
            }

            this.RaiseMessage(new IncomingMessage(
                channelId,
                this.Read(() => this._channels.GetValueOrDefault(channelId)?.Name),
                me, true, text, false,
                DateTimeOffset.FromUnixTimeMilliseconds(timestamp)));
            return;
        }

        throw new InvalidOperationException("The channel kept changing while sending; try again.");
    }

    // ================================================================ connection lifecycle

    private async Task RunLoop(CancellationToken ct) {
        var delay = this._options.ReconnectMinDelay;
        while (!ct.IsCancellationRequested) {
            this.SetState(ConnectionState.Connecting, $"Connecting to {this._options.ServerUri}");
            Connection? connection = null;
            try {
                var socket = await this.ConnectSocketAsync(ct);
                connection = new Connection(socket, this._options.MaxReceiveBytes, this._options.RequestTimeout, this.OnEvent, this.AddTrace);
                this._connection = connection;
                connection.Start();

                await this.HandshakeAsync(connection, ct);
                delay = this._options.ReconnectMinDelay;
                await connection.Closed.WaitAsync(ct);
                this.Log(NoticeLevel.Info, connection.CloseReason);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                break;
            } catch (Exception ex) {
                this.Log(NoticeLevel.Warning, $"Connection failed: {ex.Message}");
                lock (this._lock) {
                    this._status = $"Connection failed: {ex.Message}";
                }
            } finally {
                this._connection = null;
                if (connection != null) {
                    await connection.DisposeAsync();
                }
            }

            if (ct.IsCancellationRequested) {
                break;
            }

            if (this._reconnectImmediately) {
                this._reconnectImmediately = false;
                continue;
            }

            this.SetState(ConnectionState.Reconnecting, $"Reconnecting in {delay.TotalSeconds:0} s");
            try {
                await Task.Delay(delay, ct);
            } catch (OperationCanceledException) {
                break;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, this._options.ReconnectMaxDelay.Ticks));
        }

        this.SetState(ConnectionState.Stopped, "Stopped");
    }

    private async Task<WebSocket> ConnectSocketAsync(CancellationToken ct) {
        if (this._options.Connect != null) {
            return await this._options.Connect(this._options.ServerUri, ct);
        }

        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try {
            await socket.ConnectAsync(this._options.ServerUri, timeout.Token);
        } catch {
            socket.Dispose();
            throw;
        }

        return socket;
    }

    private async Task HandshakeAsync(Connection connection, CancellationToken ct) {
        var hello = new Hello { ClientVersion = this._options.ClientVersion };
        hello.ProtocolVersions.Add(ProtocolInfo.CurrentVersion);
        hello.Capabilities.Add(ProtocolInfo.Capabilities.Chat);

        var response = await this.RequestAsync(connection, new ClientFrame { Hello = hello }, ct);
        var welcome = response.Welcome ?? throw Unexpected(response);

        lock (this._lock) {
            this._limits = welcome.Limits;
            this._debugAccountsEnabled = welcome.DebugAccountsEnabled;
        }

        if (!string.IsNullOrWhiteSpace(welcome.Announcement)) {
            this.RaiseNotice(NoticeLevel.Info, welcome.Announcement);
        }

        var hasToken = this.Read(() => this._secrets.DeviceToken != null);
        if (!hasToken) {
            this.SetState(ConnectionState.Unregistered, "Not registered on this server.");
            return;
        }

        try {
            await this.AuthenticateAsync(connection, ct);
        } catch (ServerErrorException ex) when (ex.Code == ErrorCode.NotAuthenticated) {
            lock (this._lock) {
                this._secrets.DeviceToken = null;
                this._secretsVersion++;
            }

            this.SaveSecrets();
            this.SetState(ConnectionState.Unregistered, "This device's login was revoked or expired; register again.");
        }
    }

    private async Task AuthenticateAsync(Connection connection, CancellationToken ct) {
        var token = this.Read(() => this._secrets.DeviceToken) ?? throw new InvalidOperationException("Not registered.");
        var response = await this.RequestAsync(connection, new ClientFrame { Authenticate = new Authenticate { DeviceToken = token } }, ct);
        var ok = response.AuthenticateOk ?? throw Unexpected(response);

        lock (this._lock) {
            this._me = ok.User;
            this._users[ok.User.UserId] = ok.User;
            this._secrets.UserId = ok.User.UserId;
            this._state = ConnectionState.Ready;
            // Ready, but the channel list is only complete once RefreshAsync has fetched it.
            this._channelsLoaded = false;
            this._status = $"Connected as {ok.User.Name}@{ok.User.WorldName}";
        }

        this.Publish();
        // Identities cached before a disconnect may be stale: someone may have registered again meanwhile.
        await this.RefreshAsync(connection, ct, refreshIdentities: true, rekeyIfDesignated: true);
    }

    /// <param name="refreshIdentities">Fetch every identity again, not only those not cached yet.</param>
    /// <param name="rekeyIfDesignated">
    /// Rekey channels the server asks this client to (see <see cref="ClientSessionOptions.AutoRekeyWhenDesignated"/>): its
    /// RekeyNeeded only reaches members who are online, so one who just connected wasn't told.
    /// </param>
    private async Task RefreshAsync(Connection connection, CancellationToken ct, bool refreshIdentities = false, bool rekeyIfDesignated = false) {
        // Say how much of each log is already verified, so the server only sends what's new.
        var request = new ListChannels();
        request.Known.AddRange(this.Read(() => this._secrets.Memberships.Keys.Union(this._memberships.Keys).ToList()
            .Select(channelId => (channelId, head: this.MembershipOf(channelId).Head))
            .Where(known => known.head != null)
            .Select(known => new KnownLog { ChannelId = known.channelId, NextSeq = known.head!.Seq + 1 })
            .ToList()));

        var response = await this.RequestAsync(connection, new ClientFrame { ListChannels = request }, ct);
        var list = response.ChannelList ?? throw Unexpected(response);
        // Channel IDs are bound into signatures and shown in the UI, so only canonical ones are accepted.
        var channels = list.Channels.Where(channel => IsValidChannelId(channel.ChannelId)).ToList();
        var invites = list.Invites.Where(invite => IsValidChannelId(invite.ChannelId) && invite.Inviter != null).ToList();
        var blocked = this.Read(() => invites.Where(invite => this.IsBlocked(invite.Inviter.UserId)).Select(invite => invite.ChannelId).ToList());
        invites.RemoveAll(invite => blocked.Contains(invite.ChannelId));
        foreach (var channelId in blocked) {
            this.DeclineQuietly(channelId);
        }

        lock (this._lock) {
            // A list fetched on a connection that has since dropped says nothing about the next one.
            this._channelsLoaded = connection == this._connection && this._state == ConnectionState.Ready;
            var listed = channels.Select(channel => channel.ChannelId).ToHashSet();
            foreach (var stale in this._channels.Keys.Where(id => !listed.Contains(id)).ToList()) {
                this._channels.Remove(stale);
            }

            foreach (var info in channels) {
                this.ApplyChannelInfo(info);
            }

            this._invites.Clear();
            foreach (var invite in invites) {
                this._invites[invite.ChannelId] = new InviteState(invite);
                this._users[invite.Inviter.UserId] = invite.Inviter;
            }
        }

        this.Publish();

        // Membership first: keys and names are checked against it. Failures here are not worth
        // dropping the connection over: channels whose log can't be verified yet simply wait.
        foreach (var info in channels) {
            try {
                await this.SyncLogAsync(info.ChannelId, ct, info.Log, info.LogHead, connection: connection);
            } catch (ServerErrorException ex) {
                this.Log(NoticeLevel.Warning, $"Couldn't fetch the membership log of {info.ChannelId}: {ex.Message}");
            }
        }

        // For key-change warnings, and to see who registered again; signatures are checked against the logs.
        var userIds = this.Read(() => {
            var ids = new HashSet<long>();
            foreach (var info in channels) {
                var membership = this.MembershipOf(info.ChannelId);
                ids.UnionWith(membership.Members.Select(member => member.UserId));
                ids.UnionWith(membership.Invitees.Select(invitee => invitee.UserId));
            }

            ids.UnionWith(invites.Select(invite => invite.Inviter.UserId));
            return ids;
        });

        try {
            await this.EnsureIdentitiesAsync(userIds, ct, connection, refreshIdentities);
        } catch (ServerErrorException ex) {
            this.Log(NoticeLevel.Warning, $"Couldn't fetch identities: {ex.Message}");
            this.RaiseNotice(NoticeLevel.Warning, "The server didn't send some members' identity keys, so key changes may go unnoticed for now.");
        }

        foreach (var channelId in channels.Select(channel => channel.ChannelId)) {
            if (this.Read(() => this.IsMember(channelId) && !this.HasCurrentKey(channelId))) {
                try {
                    await this.FetchEpochKeysAsync(channelId, ct, connection);
                } catch (ServerErrorException ex) {
                    this.Log(NoticeLevel.Warning, $"Couldn't fetch keys for {channelId}: {ex.Message}");
                }
            }

            lock (this._lock) {
                this.TryDecryptName(channelId);
            }
        }

        foreach (var invite in invites) {
            try {
                await this.VerifyInviteAsync(invite.ChannelId, ct, connection);
            } catch (ServerErrorException ex) {
                this.Log(NoticeLevel.Warning, $"Couldn't check the invite to {invite.ChannelId}: {ex.Message}");
            }
        }

        this.SaveSecrets();
        this.Publish();

        if (rekeyIfDesignated && this._options.AutoRekeyWhenDesignated) {
            // Without the name (say, after registering again with new keys) this client can't rekey; another member must.
            var designated = this.Read(() => channels
                .Where(info => info.RekeyPending && info.RekeyDesignated && this.IsMember(info.ChannelId)
                               && this._channels.GetValueOrDefault(info.ChannelId)?.Name != null)
                .Select(info => info.ChannelId)
                .ToList());
            foreach (var channelId in designated) {
                this.RunBackground("Rekeying a channel", rekeyCt => this.RekeyAsync(channelId, rekeyCt));
            }
        }
    }

    // ================================================================ the membership log

    private enum LogFetch {
        /// <summary>Use what was offered; fetch only if the server is known to be further on.</summary>
        IfBehind,

        /// <summary>Ask the server for anything newer, whatever it said before.</summary>
        Always,

        /// <summary>Only apply what was offered (an entry this client just had stored).</summary>
        Never,
    }

    /// <summary>
    /// Brings a channel's verified membership up to the server's log, checking every entry not seen
    /// before against the rules (<see cref="IChannelMembership.Check"/>). Entries already offered (in
    /// a channel list or an event) are used first, and the rest fetched. Never moves backwards: a
    /// server that shows an older log, or a different one, is reported, and what was verified is kept.
    /// </summary>
    /// <param name="serverHead">The newest position the server just reported, if any.</param>
    /// <returns>The channel's verified membership afterwards.</returns>
    private async Task<IChannelMembership> SyncLogAsync(string channelId, CancellationToken ct, IReadOnlyList<MembershipEntry>? offered = null,
        LogPosition? serverHead = null, LogFetch fetch = LogFetch.IfBehind, Connection? connection = null) {
        var gate = this._logLocks.GetOrAdd(channelId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        MembershipEntry? conflicting = null;
        var headDiffers = false;
        string? problem = null;
        List<MembershipEntry> applied = [];
        try {
            var start = this.Read(() => this.MembershipOf(channelId));
            var state = start;
            var pending = new Queue<MembershipEntry>(offered ?? []);
            // A head the server reported for this call; a hint saved earlier may be out of date.
            var freshHead = serverHead;
            var knownHead = serverHead ?? this.Read(() => this._channels.GetValueOrDefault(channelId)?.LogHead);
            var mustFetch = fetch == LogFetch.Always || (fetch == LogFetch.IfBehind && state.Head == null);
            var pages = 0;

            while (true) {
                while (problem == null && conflicting == null && pending.TryDequeue(out var entry)) {
                    if (state.Head != null && entry.Seq <= state.Head.Seq) {
                        // A position already verified: the same entry again, or a second one there.
                        if (state.HashAt(entry.Seq) is not { } known || !known.AsSpan().SequenceEqual(MembershipEntries.Hash(entry))) {
                            conflicting = entry;
                        }

                        continue;
                    }

                    if (entry.Seq != (state.Head == null ? 0 : state.Head.Seq + 1)) {
                        // A gap: fetch what's missing.
                        pending.Clear();
                        mustFetch = fetch != LogFetch.Never;
                        break;
                    }

                    var verdict = state.Check(entry);
                    if (verdict.Kind == MembershipVerdictKind.NotNext) {
                        // Chained to a different entry than the one verified at that position.
                        conflicting = entry;
                    } else if (!verdict.IsValid) {
                        problem = $"the server sent a change to the membership of {{0}} that doesn't check out ({verdict.Reason}), so it is ignored";
                    } else {
                        state = state.Apply(entry);
                        applied.Add(entry);
                    }
                }

                if (problem != null || conflicting != null || fetch == LogFetch.Never || pages >= MaxLogPagesPerSync) {
                    break;
                }

                var behind = knownHead != null && (state.Head == null || knownHead.Seq > state.Head.Seq);
                // Before saying the server shows an older log, ask it again: the head it gave may predate an entry of ours.
                var lookAgain = freshHead != null && state.Head != null && freshHead.Seq < state.Head.Seq && pages == 0;
                if (!mustFetch && !behind && !lookAgain) {
                    break;
                }

                var response = await this.RequestAsync(connection ?? this.RequireConnection(), new ClientFrame {
                    FetchMembershipLog = new FetchMembershipLog { ChannelId = channelId, FromSeq = state.Head == null ? 0 : state.Head.Seq + 1 },
                }, ct);
                var log = response.MembershipLog ?? throw Unexpected(response);
                pages++;
                mustFetch = false;
                freshHead = knownHead = log.Head;
                if (log.Entries.Count == 0) {
                    break;
                }

                foreach (var entry in log.Entries) {
                    pending.Enqueue(entry);
                }
            }

            if (problem == null && conflicting == null && freshHead is { Hash.Length: > 0 } && state.Head != null) {
                if (freshHead.Seq < state.Head.Seq) {
                    problem = "the server shows an older version of the membership of {0} than you have already seen. It may be hiding a change (such as someone's removal) from other members";
                } else if (freshHead.Seq == state.Head.Seq && !MembershipEntries.SamePosition(freshHead, state.Head)) {
                    headDiffers = true;
                }
            }

            lock (this._lock) {
                if (!ReferenceEquals(state, start)) {
                    this.SetMembership(channelId, state, applied);
                }

                if (freshHead != null && this._channels.TryGetValue(channelId, out var channel)) {
                    channel.LogHead = freshHead;
                }
            }
        } finally {
            gate.Release();
        }

        if (applied.Count > 0) {
            this.SaveSecrets();
            this.Publish();
        }

        if (problem != null) {
            this.WarnAboutMembership(channelId, problem);
        }

        if (conflicting != null || headDiffers) {
            await this.CheckForkAsync(channelId, conflicting, ct, connection);
        }

        return this.Read(() => this.MembershipOf(channelId));
    }

    /// <summary>
    /// The server showed an entry, or a head, that differs from the log this client verified at
    /// the same position. Fetches the server's whole log and replays it: if it is valid, and it
    /// (or the offered entry) differs from what was verified here, two validly signed versions
    /// of the log exist, and the user is told. At most once a minute per channel.
    /// </summary>
    private async Task CheckForkAsync(string channelId, MembershipEntry? candidate, CancellationToken ct, Connection? connection) {
        lock (this._lock) {
            var now = this._options.TimeProvider.GetUtcNow();
            if (this._forkCheckedAt.TryGetValue(channelId, out var last) && now - last < ForkCheckInterval) {
                return;
            }

            this._forkCheckedAt[channelId] = now;
        }

        var chain = this._membership.Empty(channelId);
        var hashes = new List<byte[]>();
        IChannelMembership? beforeCandidate = candidate is { Seq: 0 } ? chain : null;
        var valid = true;
        for (var pages = 0; valid && pages < MaxLogPagesPerSync; pages++) {
            var response = await this.RequestAsync(connection ?? this.RequireConnection(), new ClientFrame {
                FetchMembershipLog = new FetchMembershipLog { ChannelId = channelId, FromSeq = (ulong) hashes.Count },
            }, ct);
            var log = response.MembershipLog ?? throw Unexpected(response);
            if (log.Entries.Count == 0) {
                break;
            }

            foreach (var entry in log.Entries) {
                if (!chain.Check(entry).IsValid) {
                    valid = false;
                    break;
                }

                chain = chain.Apply(entry);
                hashes.Add(MembershipEntries.Hash(entry));
                if (candidate != null && entry.Seq + 1 == candidate.Seq) {
                    beforeCandidate = chain;
                }
            }
        }

        ulong? forkAt = null;
        lock (this._lock) {
            var ours = this.MembershipOf(channelId);
            // Both versions are valid from the start, so where they differ, two members' signatures (or
            // one member's twice) made different entries at the same position.
            if (ours.Head != null && ours.Head.Seq < (ulong) hashes.Count && !hashes[(int) ours.Head.Seq].AsSpan().SequenceEqual(ours.Head.Hash.Span)) {
                forkAt = ours.Head.Seq;
            }

            if (candidate != null && beforeCandidate != null && beforeCandidate.Check(candidate).IsValid) {
                var candidateHash = MembershipEntries.Hash(candidate);
                var theirs = candidate.Seq < (ulong) hashes.Count ? hashes[(int) candidate.Seq] : null;
                var mine = ours.HashAt(candidate.Seq);
                if ((theirs != null && !theirs.AsSpan().SequenceEqual(candidateHash)) || (mine != null && !mine.AsSpan().SequenceEqual(candidateHash))) {
                    forkAt = forkAt == null ? candidate.Seq : Math.Min(forkAt.Value, candidate.Seq);
                }
            }
        }

        if (forkAt != null) {
            this.Log(NoticeLevel.Warning, $"Membership log of {channelId} forked at or before entry {forkAt}");
            this.WarnAboutMembership(channelId,
                $"the server has shown you two different versions of the membership of {{0}}, both validly signed, that differ at or before entry #{forkAt}. " +
                "Someone may be seeing a different member list from you: compare it with other members over /tell before trusting it");
        }
    }

    /// <summary>Tells the user (once per problem) that a channel's membership can't be trusted as shown, and shows it on the channel.</summary>
    /// <param name="format">The problem, with {0} for the channel's name.</param>
    private void WarnAboutMembership(string channelId, string format) {
        string text;
        lock (this._lock) {
            var channel = this._channels.GetValueOrDefault(channelId);
            text = string.Format(format, channel?.DisplayName ?? this._invites.GetValueOrDefault(channelId)?.Name ?? ChannelView.PlaceholderName(channelId));
            text = char.ToUpperInvariant(text[0]) + text[1..] + ".";
            if (channel != null) {
                if (channel.MembershipWarning == text) {
                    return;
                }

                channel.MembershipWarning = text;
            }
        }

        this.Publish();
        this.RaiseNotice(NoticeLevel.Warning, text, channelId);
    }

    private async Task ProcessLogEntryAsync(LogEntryAdded added, CancellationToken ct) {
        if (added.Entry == null) {
            return;
        }

        lock (this._lock) {
            foreach (var user in new[] { added.Subject, added.Actor }) {
                if (user != null && !this._identities.ContainsKey(user.UserId)) {
                    this._users[user.UserId] = user;
                }
            }

            // Not a channel this client is in (yet): an accept's answer will bring the log.
            if (!this._channels.ContainsKey(added.ChannelId)) {
                return;
            }
        }

        var before = this.Read(() => this.MembershipOf(added.ChannelId).Head?.Seq);
        var after = await this.SyncLogAsync(added.ChannelId, ct, [added.Entry]);
        var entry = added.Entry;
        var isNew = (before == null || entry.Seq > before) && after.HashAt(entry.Seq) is { } hash && hash.AsSpan().SequenceEqual(MembershipEntries.Hash(entry));
        if (!isNew) {
            return;
        }

        string? message;
        var removedMe = false;
        lock (this._lock) {
            var channel = this._channels.GetValueOrDefault(added.ChannelId);
            var name = channel?.DisplayName ?? ChannelView.PlaceholderName(added.ChannelId);
            var subject = this.UserOf(entry.Subject.UserId);
            var actor = this.UserOf(entry.ActorId);
            var who = $"{subject.Name}@{subject.WorldName}";
            removedMe = entry.Kind == MembershipEntryKind.Remove && entry.Subject.UserId == this._me?.UserId;
            message = entry.Kind switch {
                MembershipEntryKind.Invite => $"{who} was invited to {name}.",
                MembershipEntryKind.Accept => $"{who} joined {name}.",
                MembershipEntryKind.Decline => $"{who} declined the invite to {name}.",
                MembershipEntryKind.CancelInvite => $"The invite for {who} to {name} was cancelled.",
                MembershipEntryKind.Remove => $"{who} was removed from {name}.",
                MembershipEntryKind.Leave => $"{who} left {name}.",
                MembershipEntryKind.SetRank => $"{who} is now {RankName(entry.Rank)} in {name}.",
                MembershipEntryKind.TransferAdmin => $"{who} is now admin in {name}, and {actor.Name}@{actor.WorldName} a moderator.",
                _ => null,
            };
        }

        this.Publish();
        if (removedMe) {
            // The server also says so (ChannelRemoved), with the notice.
            this.RemoveChannel(added.ChannelId);
            return;
        }

        if (message != null) {
            this.RaiseNotice(NoticeLevel.Info, message, added.ChannelId);
        }
    }

    /// <summary>
    /// Checks an invite against the channel's log: it must be an open invite there, for this
    /// client's current keys, made by the inviter it names. Only then is the channel name sealed in
    /// it opened, with the key the log says the inviter signed with.
    /// </summary>
    private async Task<InviteView?> VerifyInviteAsync(string channelId, CancellationToken ct, Connection? connection = null) {
        var membership = await this.SyncLogAsync(channelId, ct, fetch: LogFetch.Always, connection: connection);
        lock (this._lock) {
            if (!this._invites.TryGetValue(channelId, out var invite) || this._identity == null || this._me == null) {
                return null;
            }

            var invited = membership.FindInvitee(this._me.UserId);
            var entry = invite.Info.Entry;
            if (invited == null || invited.Keys != this._myKeys || entry == null || invited.InviterId != invite.Info.Inviter.UserId
                || !MembershipEntries.SamePosition(MembershipEntries.PositionOf(entry), invited.Invite)) {
                invite.Name = null;
                invite.Verified = false;
                invite.InviterKeys = null;
            } else {
                invite.InviterKeys = invited.InviterKeys;
                invite.Name = this._groupKeys.OpenInvite(invite.Info, invited.InviterKeys.SigningPublicKey, this._identity, this._me.UserId);
                invite.Verified = invite.Name != null;
            }

            return this.ToView(invite);
        }
    }

    // ================================================================ events

    private void OnEvent(Event ev) {
        if (ev.KindCase != Event.KindOneofCase.Announcement && !IsValidChannelId(ChannelIdOf(ev))) {
            this.Log(NoticeLevel.Debug, $"Ignored {ev.KindCase} with an invalid channel ID");
            return;
        }

        // Everything in order: an entry must be checked before a key made for it, and a key
        // before messages encrypted under it.
        this._inbox.Writer.TryWrite(ev);
    }

    private static string? ChannelIdOf(Event ev) => ev.KindCase switch {
        Event.KindOneofCase.InviteReceived => ev.InviteReceived.Invite?.ChannelId,
        Event.KindOneofCase.InviteRevoked => ev.InviteRevoked.ChannelId,
        Event.KindOneofCase.LogEntryAdded => ev.LogEntryAdded.ChannelId,
        Event.KindOneofCase.RekeyNeeded => ev.RekeyNeeded.ChannelId,
        Event.KindOneofCase.EpochAdvanced => ev.EpochAdvanced.ChannelId,
        Event.KindOneofCase.ChatMessage => ev.ChatMessage.ChannelId,
        Event.KindOneofCase.ChannelRenamed => ev.ChannelRenamed.ChannelId,
        Event.KindOneofCase.ChannelRemoved => ev.ChannelRemoved.ChannelId,
        _ => null,
    };

    /// <summary>Channel IDs from the server must already be in canonical form (32 lowercase hex digits).</summary>
    private static bool IsValidChannelId(string? id) => id != null && ProtocolInfo.NormaliseChannelId(id) == id;

    private async Task InboxLoop(CancellationToken ct) {
        try {
            await foreach (var ev in this._inbox.Reader.ReadAllAsync(ct)) {
                try {
                    await this.HandleEventAsync(ev, ct);
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    return;
                } catch (Exception ex) {
                    this.Log(NoticeLevel.Warning, $"Couldn't process {ev.KindCase}: {ex.Message}");
                }
            }
        } catch (OperationCanceledException) {
            // Stopping.
        }
    }

    private async Task HandleEventAsync(Event ev, CancellationToken ct) {
        switch (ev.KindCase) {
            case Event.KindOneofCase.Announcement:
                this.RaiseNotice(NoticeLevel.Info, ev.Announcement.Text);
                break;
            case Event.KindOneofCase.InviteReceived:
                this.OnInviteReceived(ev.InviteReceived.Invite);
                break;
            case Event.KindOneofCase.InviteRevoked:
                lock (this._lock) {
                    this._invites.Remove(ev.InviteRevoked.ChannelId);
                }

                this.Publish();
                break;
            case Event.KindOneofCase.LogEntryAdded:
                await this.ProcessLogEntryAsync(ev.LogEntryAdded, ct);
                break;
            case Event.KindOneofCase.RekeyNeeded:
                this.OnRekeyNeeded(ev.RekeyNeeded);
                break;
            case Event.KindOneofCase.ChannelRenamed:
                lock (this._lock) {
                    // TryDecryptName refuses older names, so a replayed one is ignored.
                    if (this._channels.TryGetValue(ev.ChannelRenamed.ChannelId, out var channel)
                        && ev.ChannelRenamed.Name is { } renamed) {
                        channel.EncryptedName = renamed;
                        this.TryDecryptName(channel.Id);
                    }
                }

                this.SaveSecrets();
                this.Publish();
                break;
            case Event.KindOneofCase.ChannelRemoved: {
                var name = this.Read(() => this._channels.GetValueOrDefault(ev.ChannelRemoved.ChannelId)?.DisplayName
                                           ?? this._invites.GetValueOrDefault(ev.ChannelRemoved.ChannelId)?.Name);
                // Invitees are told too, when the channel they're invited to is disbanded.
                lock (this._lock) {
                    this._invites.Remove(ev.ChannelRemoved.ChannelId);
                }

                this.RemoveChannel(ev.ChannelRemoved.ChannelId);
                var why = ev.ChannelRemoved.Reason switch {
                    RemovalReason.Kicked => "You were removed from",
                    RemovalReason.Disbanded => "The admin disbanded",
                    _ => "You left",
                };
                this.RaiseNotice(NoticeLevel.Info, $"{why} {name ?? "a channel"}.");
                break;
            }
            case Event.KindOneofCase.EpochAdvanced:
                await this.ProcessEpochAdvancedAsync(ev.EpochAdvanced, ct);
                break;
            case Event.KindOneofCase.ChatMessage:
                await this.ProcessChatMessageAsync(ev.ChatMessage, ct);
                break;
        }
    }

    private void OnInviteReceived(InviteInfo invite) {
        if (invite.Inviter == null) {
            return;
        }

        if (this.Read(() => this.IsBlocked(invite.Inviter.UserId))) {
            this.DeclineQuietly(invite.ChannelId);
            return;
        }

        lock (this._lock) {
            this._invites[invite.ChannelId] = new InviteState(invite);
            this._users[invite.Inviter.UserId] = invite.Inviter;
        }

        this.Publish();
        this.RunBackground("Reading an invite", async ct => {
            // Fresh, so an inviter who re-registered shows as "key changed".
            await this.EnsureIdentitiesAsync([invite.Inviter.UserId], ct, refresh: true);
            var view = await this.VerifyInviteAsync(invite.ChannelId, ct);
            this.Publish();
            if (view != null) {
                this.InvokeSafely(this.InviteReceived, view);
                var who = $"{view.Inviter.Name}@{view.Inviter.WorldName}";
                this.RaiseNotice(NoticeLevel.Info,
                    !view.Verified ? $"{who} sent an invite that failed verification."
                    : view.InviterKeyChanged ? $"{who} invited you to \"{view.ChannelName}\", but their identity key changed. Compare fingerprints over /tell before accepting."
                    : $"{who} invited you to \"{view.ChannelName}\".",
                    invite.ChannelId);
            }
        });
    }

    private void OnRekeyNeeded(RekeyNeeded rekey) {
        bool designated;
        lock (this._lock) {
            if (!this._channels.TryGetValue(rekey.ChannelId, out var channel)
                || rekey.CurrentEpoch < Math.Max(channel.ServerEpoch, this.KeyEpochOf(channel.Id) ?? 0)) {
                // Unknown channel, or a request that a newer epoch has already answered.
                return;
            }

            channel.ServerEpoch = Math.Max(channel.ServerEpoch, rekey.CurrentEpoch);
            channel.RekeyPending = true;
            designated = rekey.DesignatedUserId == this._me?.UserId && this.IsMember(rekey.ChannelId);
        }

        this.Publish();
        if (designated && this._options.AutoRekeyWhenDesignated) {
            this.RunBackground("Rekeying a channel", ct => this.RekeyAsync(rekey.ChannelId, ct));
        }
    }

    private async Task ProcessEpochAdvancedAsync(EpochAdvanced advanced, CancellationToken ct) {
        // A key made for a log position this client hasn't reached: fetch and check the log first.
        var position = advanced.MyKey?.LogPosition;
        if (position != null && this.Read(() => this._channels.ContainsKey(advanced.ChannelId)
                                                && this.MembershipOf(advanced.ChannelId).Head is { } head && position.Seq > head.Seq)) {
            await this.SyncLogAsync(advanced.ChannelId, ct, fetch: LogFetch.Always);
        }

        string? rejected = null;
        BadKey? bad = null;
        MemberKeys? failedSigner = null;
        lock (this._lock) {
            if (this._identity == null || this._me == null || !this._channels.TryGetValue(advanced.ChannelId, out var channel)
                || !this.IsMember(advanced.ChannelId)) {
                return;
            }

            var membership = this.MembershipOf(advanced.ChannelId);
            var author = membership.FindMember(advanced.AuthorId);
            byte[]? key = null;
            var check = advanced.MyKey == null || author == null
                ? EpochKeyCheck.BadSignature
                : this._groupKeys.OpenEpochKey(advanced.MyKey, advanced.ChannelId, advanced.Epoch, advanced.AuthorId, author.Keys.SigningPublicKey, this._identity, this._me.UserId, out key);
            var alreadyHeld = key != null && this.GetEpochKey(advanced.ChannelId, advanced.Epoch) is { } existing && existing.AsSpan().SequenceEqual(key);

            // The same key may already have been fetched while this event waited in the queue: not a replay.
            if (!alreadyHeld) {
                rejected = this.CheckEpochKeyAuthor(channel, advanced.Epoch, author);
                if (rejected == null && check == EpochKeyCheck.BadSignature) {
                    rejected = "it failed signature or decryption checks";
                    failedSigner = author?.Keys;
                } else if (rejected == null && !membership.IsCurrent(position)) {
                    // Validly signed by a member, but sealed to the members of another time.
                    rejected = $"it was made for the channel's membership at entry #{position?.Seq}, not the current one (entry #{membership.Head?.Seq}). The server may be hiding a change from someone";
                } else if (rejected == null && key == null) {
                    bad = this.FlagBadEpochKey(channel, advanced.Epoch, this.UserOf(advanced.AuthorId), check);
                    rejected = "it failed signature or decryption checks";
                }
            }

            if (rejected == null) {
                if (!alreadyHeld) {
                    this.StoreEpochKey(advanced.ChannelId, advanced.Epoch, key!, position);
                }

                channel.ServerEpoch = Math.Max(channel.ServerEpoch, advanced.Epoch);
                channel.RekeyPending = false;
                if (advanced.Name != null) {
                    channel.EncryptedName = advanced.Name;
                }

                this.TryDecryptName(channel.Id);
            }
        }

        if (failedSigner != null) {
            // Perhaps they registered again; then say so, rather than leave it at "failed checks".
            await this.RefetchIdentityAsync(advanced.AuthorId, failedSigner, ct);
        }

        if (bad != null) {
            this.HandleBadEpochKey(bad);
            return;
        }

        if (rejected != null) {
            this.RaiseNotice(NoticeLevel.Warning, $"Rejected a new key for a channel: {rejected}.", advanced.ChannelId);
            return;
        }

        this.SaveSecrets();
        this.Publish();
    }

    private sealed record BadKey(string ChannelId, string Message, bool Rekey);

    /// <summary>
    /// A member signed an epoch key that is no use to us: it doesn't open, or
    /// it isn't the key they committed to for everyone. Either they are trying
    /// to cut us off or split the channel, or they sealed it to an outdated
    /// identity. The fix is the same: a fresh key from us. Call inside the lock.
    /// </summary>
    /// <returns>What to tell the user and whether to rekey, or null if the key was fine or merely forged.</returns>
    private BadKey? FlagBadEpochKey(ChannelState channel, ulong epoch, User author, EpochKeyCheck check) {
        var problem = check switch {
            EpochKeyCheck.CommitmentMismatch => "isn't the key they committed to giving everyone else",
            EpochKeyCheck.Unreadable => "can't be opened with your identity key",
            // A bad signature proves nothing about the author: the server could have made it up.
            _ => null,
        };

        if (problem == null) {
            return null;
        }

        channel.ServerEpoch = Math.Max(channel.ServerEpoch, epoch);
        channel.RekeyPending = true;
        // One automatic rekey per epoch, so two clients can't keep rekeying each other.
        var rekey = channel.BadKeyRekeyEpoch != epoch;
        channel.BadKeyRekeyEpoch = epoch;

        return new BadKey(channel.Id,
            $"{author.Name}@{author.WorldName} sent you a key for {channel.DisplayName} (epoch {epoch}) that {problem}. " +
            $"They may be trying to cut you off or split the channel{(rekey ? "; rekeying it now." : ".")}",
            rekey);
    }

    private void HandleBadEpochKey(BadKey bad) {
        this.Publish();
        this.RaiseNotice(NoticeLevel.Warning, bad.Message, bad.ChannelId);
        if (bad.Rekey) {
            this.RunBackground("Rekeying a channel", ct => this.RekeyAsync(bad.ChannelId, ct));
        }
    }

    /// <summary>
    /// An epoch key is only acceptable from a member in the verified log, and only for an
    /// epoch newer than any key already held. With no key held, it may be at
    /// most one epoch behind the server's. Call inside the lock.
    /// </summary>
    /// <returns>Null if acceptable, otherwise the reason it isn't.</returns>
    private string? CheckEpochKeyAuthor(ChannelState channel, ulong epoch, ChannelMember? author) {
        if (author == null) {
            return "its author isn't a member of the channel";
        }

        var newest = this.KeyEpochOf(channel.Id);
        if (newest is { } held && epoch <= held) {
            return $"it's for epoch {epoch}, but you already have epoch {held}";
        }

        // Otherwise a server could hand someone (re)joining a long-superseded key, perhaps
        // one an earlier member still has, and they would start sending under it.
        if (newest == null && epoch + 1 < channel.ServerEpoch) {
            return $"it's for epoch {epoch}, but the channel is already at epoch {channel.ServerEpoch}";
        }

        return null;
    }

    private async Task ProcessChatMessageAsync(ChatMessage message, CancellationToken ct) {
        var messageId = Convert.ToHexString(message.MessageId.Span);

        // Cheap checks first. Nothing is marked seen until the message has been verified,
        // so junk with made-up IDs can't push genuine ones out of the seen-set.
        var (channelKnown, sender, seen, channelName, blocked) = this.Read(() => {
            var channel = this._channels.GetValueOrDefault(message.ChannelId);
            // Not a member under this identity key (say, after registering again): nothing here can be read.
            return (channel != null && this.IsMember(message.ChannelId),
                channel == null ? null : this.MembershipOf(message.ChannelId).FindMember(message.SenderId),
                this._seenMessages.Contains(messageId),
                channel?.DisplayName,
                this.IsBlocked(message.SenderId));
        });

        if (!channelKnown || seen || blocked) {
            return;
        }

        var senderUser = this.Read(() => this.UserOf(message.SenderId));
        if (sender == null) {
            // A removed member (with the server's help) could otherwise keep posting with an old key.
            var who = this.Read(() => this._users.ContainsKey(message.SenderId) || this._identities.ContainsKey(message.SenderId)
                ? $"{senderUser.Name}@{senderUser.WorldName}"
                : "someone");
            this.RaiseNotice(NoticeLevel.Warning, $"Dropped a message in {channelName} from {who}, who isn't a member of it.", message.ChannelId);
            return;
        }

        var key = this.Read(() => this.GetEpochKey(message.ChannelId, message.Epoch));
        if (key == null) {
            await this.FetchEpochKeysAsync(message.ChannelId, ct);
            key = this.Read(() => this.GetEpochKey(message.ChannelId, message.Epoch));
        }

        if (key == null) {
            this.RaiseNotice(NoticeLevel.Warning, $"Couldn't decrypt a message from {senderUser.Name}: no key for that epoch yet.", message.ChannelId);
            return;
        }

        var now = this._options.TimeProvider.GetUtcNow();
        if (this.Read(() => this.IsPastOldEpochGrace(message.ChannelId, message.Epoch, now))) {
            this.RaiseNotice(NoticeLevel.Warning, $"Dropped a message from {senderUser.Name}: it uses an older key that was replaced a while ago.", message.ChannelId);
            return;
        }

        // Signed by the keys the log admitted them with, not whatever the server says their keys are now.
        var content = this._groupKeys.DecryptMessage(message, key, sender.Keys.SigningPublicKey);
        if (content == null && !this._groupKeys.VerifyMessage(message, sender.Keys.SigningPublicKey)) {
            // Awaited here, in the inbox, so later messages still wait their turn.
            if (await this.RefetchIdentityAsync(message.SenderId, sender.Keys, ct) is { } current
                && this._groupKeys.VerifyMessage(message, current.Identity.SigningPublicKey.Span)) {
                this.RaiseNotice(NoticeLevel.Warning,
                    $"Dropped a message from {senderUser.Name}: it's signed with the identity key they registered again with, which isn't a member of {channelName} " +
                    "until a moderator removes them and invites them again.", message.ChannelId);
                return;
            }

            this.RaiseNotice(NoticeLevel.Warning, $"Dropped a message claiming to be from {senderUser.Name}: it failed signature or decryption checks.", message.ChannelId);
            return;
        }

        if (content == null) {
            this.RaiseNotice(NoticeLevel.Warning, $"Dropped a message claiming to be from {senderUser.Name}: it failed signature or decryption checks.", message.ChannelId);
            return;
        }

        // The timestamp is signed, so an old message can't be replayed as new
        // once it falls outside this window (the seen-set covers the window itself).
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(message.TimestampUnixMs);
        if ((now - timestamp).Duration() > MaxMessageClockSkew) {
            this.RaiseNotice(NoticeLevel.Warning, $"Dropped a message from {senderUser.Name} dated {timestamp.ToLocalTime():g}: too far from the current time (replayed, or a wrong clock).", message.ChannelId);
            return;
        }

        // The seen-set is lost on restart; this persisted high-water mark isn't.
        var newest = this.Read(() => this._secrets.NewestMessageTimes.TryGetValue(message.ChannelId, out var senders)
                                     && senders.TryGetValue(message.SenderId, out var time) ? time : (long?) null);
        if (newest is { } newestMs && message.TimestampUnixMs < newestMs - (long) MessageReorderAllowance.TotalMilliseconds) {
            this.RaiseNotice(NoticeLevel.Warning, $"Dropped a message from {senderUser.Name} dated {timestamp.ToLocalTime():g}: it's older than messages already received from them (replayed?).", message.ChannelId);
            return;
        }

        bool saveNow;
        lock (this._lock) {
            if (!this.MarkSeen(messageId)) {
                return;
            }

            if (!this._secrets.NewestMessageTimes.TryGetValue(message.ChannelId, out var senders)) {
                senders = new Dictionary<long, long>();
                this._secrets.NewestMessageTimes[message.ChannelId] = senders;
            }

            if (!senders.TryGetValue(message.SenderId, out var previous) || message.TimestampUnixMs > previous) {
                senders[message.SenderId] = message.TimestampUnixMs;
                this._replayStateDirty = true;
            }

            // Saved with the next other change, and at least every few minutes; never once per message.
            saveNow = this._replayStateDirty && now - this._replayStateSavedAt > ReplayStateSaveInterval;
            channelName = this._channels.GetValueOrDefault(message.ChannelId)?.Name;
        }

        if (saveNow) {
            this.SaveSecrets();
        }

        var isOwn = message.SenderId == this.Read(() => this._me?.UserId);
        this.RaiseMessage(content.KindCase == Content.KindOneofCase.Text
            ? new IncomingMessage(message.ChannelId, channelName, senderUser, isOwn, content.Text.Text, false, timestamp)
            : new IncomingMessage(message.ChannelId, channelName, senderUser, isOwn, null, true, timestamp));
    }

    /// <summary>
    /// True if <paramref name="epoch"/> is older than the key epoch and a newer key
    /// was accepted more than <see cref="OldEpochGrace"/> ago (or before this
    /// session started). Call inside the lock.
    /// </summary>
    private bool IsPastOldEpochGrace(string channelId, ulong epoch, DateTimeOffset now) {
        if (this.KeyEpochOf(channelId) is not { } held || epoch >= held || !this._channels.TryGetValue(channelId, out var channel)) {
            return false;
        }

        var replacedAt = channel.KeyAcceptedAt.Where(entry => entry.Key > epoch).Select(entry => entry.Value).DefaultIfEmpty(DateTimeOffset.MinValue).Min();
        return now - replacedAt > OldEpochGrace;
    }

    // ================================================================ keys and identities

    /// <summary>Fetches the identities and keys needed to use a channel just joined.</summary>
    private async Task EnsureChannelReadyAsync(string channelId, CancellationToken ct) {
        var userIds = this.Read(() => this.MembershipOf(channelId).Members.Select(member => member.UserId).ToList());
        await this.EnsureIdentitiesAsync(userIds, ct);
        await this.FetchEpochKeysAsync(channelId, ct);
    }

    private async Task FetchEpochKeysAsync(string channelId, CancellationToken ct, Connection? connection = null) {
        var fromEpoch = this.Read(() => {
            var epoch = this._channels.GetValueOrDefault(channelId)?.ServerEpoch ?? 0;
            return epoch > 0 ? epoch - 1 : 0;
        });

        var response = await this.RequestAsync(connection ?? this.RequireConnection(), new ClientFrame {
            FetchEpochKeys = new FetchEpochKeys { ChannelId = channelId, FromEpoch = fromEpoch },
        }, ct);

        var keys = response.EpochKeys ?? throw Unexpected(response);

        // Keys made for a log position this client hasn't reached: fetch and check the log first.
        var furthest = keys.Keys.Select(entry => entry.Key?.LogPosition?.Seq ?? 0).DefaultIfEmpty(0UL).Max();
        if (this.Read(() => this.MembershipOf(channelId).Head is { } head && furthest > head.Seq)) {
            await this.SyncLogAsync(channelId, ct, fetch: LogFetch.Always, connection: connection);
        }

        BadKey? bad = null;
        EpochKeyForMe? staleNewest = null;
        lock (this._lock) {
            if (this._identity == null || this._me == null || !this._channels.TryGetValue(channelId, out var channel) || !this.IsMember(channelId)) {
                return;
            }

            var membership = this.MembershipOf(channelId);
            foreach (var entry in keys.Keys.OrderBy(entry => entry.Epoch)) {
                if (entry.Key == null || this.GetEpochKey(channelId, entry.Epoch) != null) {
                    continue;
                }

                var author = membership.FindMember(entry.AuthorId);
                if (this.CheckEpochKeyAuthor(channel, entry.Epoch, author) is { } reason) {
                    this.Log(NoticeLevel.Warning, $"Ignored epoch {entry.Epoch} key for {channelId}: {reason}");
                    continue;
                }

                var check = this._groupKeys.OpenEpochKey(entry.Key, channelId, entry.Epoch, entry.AuthorId, author!.Keys.SigningPublicKey, this._identity, this._me.UserId, out var key);
                if (check == EpochKeyCheck.BadSignature) {
                    this.Log(NoticeLevel.Warning, $"Epoch {entry.Epoch} key for {channelId} failed verification ({check})");
                    continue;
                }

                if (!membership.IsCurrent(entry.Key.LogPosition)) {
                    // Sealed to the members of another time. Only worth a warning if no newer key replaces it.
                    this.Log(NoticeLevel.Warning, $"Ignored epoch {entry.Epoch} key for {channelId}: made at log entry {entry.Key.LogPosition?.Seq}, not the current membership");
                    staleNewest = entry;
                    continue;
                }

                if (key != null) {
                    this.StoreEpochKey(channelId, entry.Epoch, key, entry.Key.LogPosition);
                } else {
                    this.Log(NoticeLevel.Warning, $"Epoch {entry.Epoch} key for {channelId} failed verification ({check})");
                    bad ??= this.FlagBadEpochKey(channel, entry.Epoch, this.UserOf(entry.AuthorId), check);
                }
            }

            if (staleNewest != null && this.KeyEpochOf(channelId) is { } heldNow && heldNow >= staleNewest.Epoch) {
                staleNewest = null;
            }

            // TryDecryptName only accepts it if it isn't older than the name already held.
            if (keys.Name != null) {
                channel.EncryptedName = keys.Name;
            }

            // A key newer than the epoch the server last reported means a rekey happened since,
            // and it settled any membership change the server had flagged. (Its EpochAdvanced
            // can be missed, for example when it arrives while an invite is being accepted.)
            if (this.KeyEpochOf(channelId) is { } newest && newest > channel.ServerEpoch) {
                channel.ServerEpoch = newest;
                channel.RekeyPending = false;
            }

            this.TryDecryptName(channelId);

            // The server is ahead and has no newer key for us (never sent, or sealed so we
            // can't open it): rekey to the server's epoch + 1 rather than stay stuck.
            if (this.KeyEpochOf(channelId) is { } held && held < channel.ServerEpoch) {
                channel.RekeyPending = true;
            }
        }

        this.SaveSecrets();
        this.Publish();
        if (staleNewest != null) {
            this.WarnAboutMembership(channelId,
                $"the server offered a key for {{0}} made for an older membership (entry #{staleNewest.Key.LogPosition?.Seq}) than you have verified. It may be hiding a change from someone");
        }

        if (bad != null) {
            this.HandleBadEpochKey(bad);
        }
    }

    /// <summary>Returns verified identities for the given users, fetching any that aren't cached (or all of them, with <paramref name="refresh"/>).</summary>
    private async Task<Dictionary<long, UserIdentity>> EnsureIdentitiesAsync(IEnumerable<long> userIds, CancellationToken ct, Connection? connection = null, bool refresh = false) {
        var wanted = userIds.Distinct().ToList();
        var missing = refresh ? wanted : this.Read(() => wanted.Where(id => !this._identities.ContainsKey(id)).ToList());

        if (missing.Count > 0) {
            // The server refuses lookups bigger than it advertises; someone in many big channels needs several.
            var batch = this.Read(() => this._limits?.MaxIdentitiesPerRequest is > 0 and var advertised
                ? (int) Math.Min(advertised, MaxIdentityBatch)
                : DefaultIdentityBatch);
            foreach (var chunk in missing.Chunk(batch)) {
                var request = new GetIdentities();
                request.UserIds.AddRange(chunk);
                var response = await this.RequestAsync(connection ?? this.RequireConnection(), new ClientFrame { GetIdentities = request }, ct);
                this.AcceptIdentities(response.Identities?.Identities_ ?? []);
            }
        }

        return this.Read(() => wanted
            .Where(this._identities.ContainsKey)
            .ToDictionary(id => id, id => this._identities[id]));
    }

    /// <summary>
    /// Something signed by <paramref name="userId"/> didn't verify against <paramref name="checkedAgainst"/>,
    /// the keys the membership log binds them to. The log decides what counts, so this can't make it
    /// count; but they may have registered again, and then the user should be told their key changed
    /// rather than see only "failed checks". So their identity is fetched again: at most once a minute
    /// per user, so a stream of forgeries can't make this client flood the server, and a fetch already
    /// under way is waited for rather than repeated. The result is pinned like any other identity.
    /// </summary>
    /// <returns>Their identity if it differs from <paramref name="checkedAgainst"/>, otherwise null.</returns>
    private async Task<UserIdentity?> RefetchIdentityAsync(long userId, MemberKeys checkedAgainst, CancellationToken ct, Connection? connection = null) {
        TaskCompletionSource? fetch = null;
        Task? running = null;
        lock (this._lock) {
            var cached = this._identities.GetValueOrDefault(userId);
            if (cached != null && MemberKeys.Of(cached.Identity) != checkedAgainst) {
                // Already known (fetched for something else they signed).
                return cached;
            }

            if (!this._identityRefetches.TryGetValue(userId, out running)) {
                var now = this._options.TimeProvider.GetUtcNow();
                if (this._identityRefetchedAt.TryGetValue(userId, out var last) && now - last < IdentityRefetchInterval) {
                    return null;
                }

                foreach (var expired in this._identityRefetchedAt.Where(entry => now - entry.Value >= IdentityRefetchInterval).Select(entry => entry.Key).ToList()) {
                    this._identityRefetchedAt.Remove(expired);
                }

                this._identityRefetchedAt[userId] = now;
                fetch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                this._identityRefetches[userId] = fetch.Task;
            }
        }

        if (fetch != null) {
            try {
                await this.EnsureIdentitiesAsync([userId], ct, connection, refresh: true);
            } catch (Exception ex) when (ex is ServerErrorException or SessionDisconnectedException or TimeoutException) {
                this.Log(NoticeLevel.Warning, $"Couldn't fetch user {userId}'s identity again: {ex.Message}");
            } finally {
                lock (this._lock) {
                    this._identityRefetches.Remove(userId);
                }

                fetch.SetResult();
            }
        } else if (running != null) {
            await running.WaitAsync(ct);
        }

        UserIdentity? current;
        lock (this._lock) {
            if (!this._identities.TryGetValue(userId, out current) || MemberKeys.Of(current.Identity) == checkedAgainst) {
                return null;
            }
        }

        this.Publish();
        return current;
    }

    /// <summary>Fetches again the identities of names' authors that <see cref="TryDecryptName"/> couldn't verify, and logs that names were made for.</summary>
    private void FollowUpNames() {
        List<KeyValuePair<long, MemberKeys>> authors;
        List<string> logs;
        lock (this._lock) {
            if (this._connection == null || (this._staleNameAuthors.Count == 0 && this._namePositionsAhead.Count == 0)) {
                return;
            }

            authors = [.. this._staleNameAuthors];
            this._staleNameAuthors.Clear();
            logs = [.. this._namePositionsAhead.Keys];
            this._namePositionsAhead.Clear();
        }

        foreach (var (authorId, checkedAgainst) in authors) {
            this.RunBackground("Checking a channel name", ct => this.RefetchIdentityAsync(authorId, checkedAgainst, ct));
        }

        foreach (var channelId in logs) {
            this.RunBackground("Checking a channel name", async ct => {
                await this.SyncLogAsync(channelId, ct, fetch: LogFetch.Always);
                lock (this._lock) {
                    this.TryDecryptName(channelId);
                }

                this.SaveSecrets();
                this.Publish();
            });
        }
    }

    /// <summary>
    /// Validates identities from the server and pins them: trust on first
    /// use, a persistent warning when a user's keys change, and a warning
    /// when a name@world moves to a different account.
    /// </summary>
    private List<UserIdentity> AcceptIdentities(IEnumerable<UserIdentity> identities) {
        var accepted = new List<UserIdentity>();
        var warnings = new List<string>();

        lock (this._lock) {
            foreach (var identity in identities) {
                if (identity.User == null || !IdentityKeys.IsValidBundle(identity.Identity)) {
                    this.Log(NoticeLevel.Warning, $"Rejected an invalid identity for user {identity.User?.UserId}");
                    continue;
                }

                var user = identity.User;
                if (this.Pin(user.UserId, MemberKeys.Of(identity.Identity), user, identity.KeyVersion) is { } warning) {
                    warnings.Add(warning);
                }

                this._identities[user.UserId] = identity;
                this._users[user.UserId] = user;
                accepted.Add(identity);
            }
        }

        this.SaveSecrets();
        foreach (var warning in warnings) {
            this.RaiseNotice(NoticeLevel.Warning, warning);
        }

        return accepted;
    }

    /// <summary>
    /// Pins <paramref name="keys"/> for a user, from the server's identities or a membership log:
    /// trust on first use, and a persistent "key changed" warning if they differ from what was
    /// pinned. Call inside the lock.
    /// </summary>
    /// <param name="user">Their name and world, if known.</param>
    /// <param name="keyVersion">From the server's identity; null from a log.</param>
    /// <returns>A warning for the user, if any.</returns>
    private string? Pin(long userId, MemberKeys keys, User? user, uint? keyVersion = null) {
        string? warning = null;
        var changed = false;
        var who = user != null ? $"{user.Name}@{user.WorldName}" : null;

        if (this._secrets.PinnedIdentities.TryGetValue(userId, out var pinned)) {
            who ??= pinned.Name.Length > 0 ? $"{pinned.Name}@{pinned.WorldName}" : $"user {userId}";
            if (!pinned.SigningPublicKey.AsSpan().SequenceEqual(keys.SigningPublicKey) || !pinned.AgreementPublicKey.AsSpan().SequenceEqual(keys.AgreementPublicKey)) {
                pinned.SigningPublicKey = keys.SigningKeyArray();
                pinned.AgreementPublicKey = keys.AgreementKeyArray();
                pinned.KeyChangeUnacknowledged = true;
                pinned.Compared = false;
                changed = true;
                warning = $"{who}'s identity key changed (they may have re-registered). Compare fingerprints over /tell before trusting it: {keys.Fingerprint}";
            }

            if (user != null && (pinned.Name != user.Name || pinned.WorldName != user.WorldName)) {
                if (pinned.Name.Length > 0 && warning == null) {
                    warning = $"{pinned.Name}@{pinned.WorldName} is now shown as {who} (a rename or world transfer). Their keys are unchanged.";
                }

                pinned.Name = user.Name;
                pinned.WorldName = user.WorldName;
                changed = true;
            }

            if (keyVersion is { } version && pinned.KeyVersion != version) {
                pinned.KeyVersion = version;
                changed = true;
            }
        } else {
            var previousOwner = user == null ? default : this._secrets.PinnedIdentities.FirstOrDefault(pair =>
                string.Equals(pair.Value.Name, user.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(pair.Value.WorldName, user.WorldName, StringComparison.OrdinalIgnoreCase));
            if (previousOwner.Value != null) {
                warning = $"{who} now belongs to a different account than the one you saw before. Compare fingerprints over /tell before trusting it: {keys.Fingerprint}";
            }

            this._secrets.PinnedIdentities[userId] = new PinnedIdentity {
                SigningPublicKey = keys.SigningKeyArray(),
                AgreementPublicKey = keys.AgreementKeyArray(),
                KeyVersion = keyVersion ?? 0,
                Name = user?.Name ?? "",
                WorldName = user?.WorldName ?? "",
                KeyChangeUnacknowledged = previousOwner.Value != null,
            };
            changed = true;
        }

        if (changed) {
            this._secretsVersion++;
        }

        return warning;
    }

    /// <summary>Call inside the lock.</summary>
    private InviteView ToView(InviteState invite) {
        var inviterId = invite.Info.Inviter.UserId;
        var keyChanged = this._secrets.PinnedIdentities.TryGetValue(inviterId, out var pinned) && pinned.KeyChangeUnacknowledged;
        return new InviteView(invite.Info.ChannelId, invite.Info.Inviter, invite.Name, invite.Verified,
            DateTimeOffset.FromUnixTimeSeconds(invite.Info.CreatedUnix), keyChanged, invite.InviterKeys?.Fingerprint);
    }

    // ================================================================ state helpers (call inside _lock)

    private ChannelState ApplyChannelInfo(ChannelInfo info) {
        if (!this._channels.TryGetValue(info.ChannelId, out var channel)) {
            channel = new ChannelState(info.ChannelId);
            this._channels[info.ChannelId] = channel;
        }

        // Only hints: they decide when to fetch keys, logs or rekey, never which key is used or who is a member.
        channel.ServerEpoch = info.Epoch;
        channel.RekeyPending = info.RekeyPending;
        if (info.LogHead != null) {
            channel.LogHead = info.LogHead;
        }

        foreach (var member in info.Members) {
            if (member.User != null && !this._identities.ContainsKey(member.User.UserId)) {
                this._users[member.User.UserId] = member.User;
            }
        }

        if (info.Name != null) {
            channel.EncryptedName = info.Name;
        }

        this.TryDecryptName(info.ChannelId);
        return channel;
    }

    /// <summary>The channel's verified membership: from memory, else as saved, else not yet seen.</summary>
    private IChannelMembership MembershipOf(string channelId) {
        if (this._memberships.TryGetValue(channelId, out var membership)) {
            return membership;
        }

        membership = this._secrets.Memberships.TryGetValue(channelId, out var checkpoint)
            ? this._membership.Restore(checkpoint)
            : this._membership.Empty(channelId);
        this._memberships[channelId] = membership;
        return membership;
    }

    /// <summary>
    /// Records a newly verified membership. Keys the log just admitted someone with are pinned (a change
    /// is warned about). Saved if this user is in it, or it was saved before.
    /// </summary>
    private void SetMembership(string channelId, IChannelMembership membership, IReadOnlyList<MembershipEntry> applied) {
        this._memberships[channelId] = membership;
        var me = this._me?.UserId;
        if (this._secrets.Memberships.ContainsKey(channelId)
            || (me != null && (membership.FindMember(me.Value) != null || membership.FindInvitee(me.Value) != null))) {
            this._secrets.Memberships[channelId] = membership.ToCheckpoint();
            this._secretsVersion++;
        }

        // Only keys still in use: an old invite replayed from history says nothing about today.
        foreach (var entry in applied.Where(entry => entry.Kind is MembershipEntryKind.Genesis or MembershipEntryKind.Invite)) {
            var userId = entry.Subject.UserId;
            var keys = MemberKeys.FromProto(entry.Subject)!;
            var current = membership.FindMember(userId)?.Keys ?? membership.FindInvitee(userId)?.Keys;
            if (userId != me && current == keys && this.Pin(userId, keys, this.KnownUser(userId)) is { } warning) {
                this._pendingNotices.Add(new SessionNotice(NoticeLevel.Warning, warning, channelId));
            }
        }

        if (this._channels.TryGetValue(channelId, out var channel)) {
            this.TryDecryptName(channel.Id);
        }
    }

    /// <summary>True if this user is a member of the channel under their current identity keys.</summary>
    private bool IsMember(string channelId) {
        return this._me != null && this._myKeys != null && this.MembershipOf(channelId).FindMember(this._me.UserId)?.Keys == this._myKeys;
    }

    /// <summary>
    /// The channel needs a new key before anyone sends: the server says so, or the newest key held
    /// was made for an older membership than the verified log's (someone joined or left since).
    /// </summary>
    private bool NeedsRekey(ChannelState channel) {
        if (channel.RekeyPending) {
            return true;
        }

        return this.KeyEpochOf(channel.Id) is { } held && !this.MembershipOf(channel.Id).IsCurrent(this.KeyPositionOf(channel.Id, held));
    }

    /// <summary>
    /// Shows the name the server offered, if it is encrypted under the key epoch in use, signed by a
    /// member with the keys the log has for them, made for the current membership, and not older
    /// than the name already accepted. Otherwise a server could replay an old, validly signed name.
    /// </summary>
    private void TryDecryptName(string channelId) {
        if (!this._channels.TryGetValue(channelId, out var channel) || channel.EncryptedName is not { } offered) {
            return;
        }

        var version = new NameVersion(offered.Epoch, offered.Revision);
        if (offered.Epoch != this.KeyEpochOf(channelId)
            || (this._secrets.ChannelNameVersions.TryGetValue(channelId, out var held) && version.CompareTo(held) < 0)) {
            return;
        }

        var key = this.GetEpochKey(channelId, offered.Epoch);
        var membership = this.MembershipOf(channelId);
        if (key == null || offered.LogPosition is not { } position || membership.FindMember(offered.AuthorId) is not { } author) {
            return;
        }

        if (membership.Head is { } head && position.Seq > head.Seq) {
            // Made after the newest entry this client has: fetch the log, then try again.
            if (!channel.NameLogFetchedFor.HasValue || channel.NameLogFetchedFor < position.Seq) {
                channel.NameLogFetchedFor = position.Seq;
                this._namePositionsAhead[channelId] = position.Seq;
            }

            return;
        }

        // Made for an older membership: refused. Unlike a key, a name made so many entries back that its
        // hash is no longer remembered is still shown if nobody joined or left since; otherwise a long run
        // of invites would leave restarted clients without the name, which they need to rekey.
        var tooOldToCheck = position.Seq >= membership.MembersChangedAt && membership.HashAt(position.Seq) == null;
        if (!membership.IsCurrent(position) && !tooOldToCheck) {
            return;
        }

        var name = this._groupKeys.DecryptName(offered, channelId, key, author.Keys.SigningPublicKey);
        if (name == null) {
            if (!this._groupKeys.VerifyName(offered, channelId, author.Keys.SigningPublicKey)) {
                // Perhaps signed with keys they registered since; they're told if so.
                this._staleNameAuthors[offered.AuthorId] = author.Keys;
            }

            return;
        }

        // A rekey carries a name into the new epoch as revision 0, saying which version it carried;
        // only the admin renames, as later revisions. But any member can rekey, so if this client
        // knows that version (or a newer one) under another name, say who changed it. If it missed
        // renames since, it can't tell, and the name is taken as the admin's.
        if (offered is { Revision: 0, CarriedFrom: { } source } && channel.NameVersion is { } known && channel.Name is { } previous && previous != name
            && new NameVersion(source.Epoch, source.Revision).CompareTo(known) <= 0) {
            var who = this.UserOf(offered.AuthorId);
            this._pendingNotices.Add(new SessionNotice(NoticeLevel.Warning,
                $"{who.Name}@{who.WorldName} changed the channel name from \"{previous}\" to \"{name}\" while rekeying.", channelId));
        }

        channel.Name = name;
        this.SetNameVersion(channelId, version);
    }

    /// <summary>Records the version of the name just accepted for a channel. Call inside the lock.</summary>
    private void SetNameVersion(string channelId, NameVersion version) {
        if (this._channels.TryGetValue(channelId, out var channel)) {
            channel.NameVersion = version;
        }

        if (!this._secrets.ChannelNameVersions.TryGetValue(channelId, out var held) || held != version) {
            this._secrets.ChannelNameVersions[channelId] = version;
            this._secretsVersion++;
        }
    }

    /// <summary>The newest epoch this client holds a key for: the one it sends with.</summary>
    private ulong? KeyEpochOf(string channelId) {
        return this._secrets.EpochKeys.TryGetValue(channelId, out var keys) && keys.Count > 0 ? keys.Keys.Max() : null;
    }

    /// <summary>The newer of the server's epoch and the key epoch, or null for an unknown channel.</summary>
    private ulong? NewestEpochOf(string channelId) {
        return this._channels.TryGetValue(channelId, out var channel) ? Math.Max(channel.ServerEpoch, this.KeyEpochOf(channelId) ?? 0) : null;
    }

    /// <summary>True if this client holds a key at least as new as the server's epoch.</summary>
    private bool HasCurrentKey(string channelId) {
        return this._channels.TryGetValue(channelId, out var channel) && this.KeyEpochOf(channelId) is { } held && held >= channel.ServerEpoch;
    }

    private byte[]? GetEpochKey(string channelId, ulong epoch) {
        return this._secrets.EpochKeys.TryGetValue(channelId, out var keys) && keys.TryGetValue(epoch, out var key) ? key : null;
    }

    /// <summary>The log position an epoch key held was made for, if known.</summary>
    private LogPosition? KeyPositionOf(string channelId, ulong epoch) {
        return this._secrets.EpochKeyPositions.TryGetValue(channelId, out var positions) && positions.TryGetValue(epoch, out var position)
            ? new LogPosition { Seq = position.Seq, Hash = ByteString.CopyFrom(position.Hash) }
            : null;
    }

    private void StoreEpochKey(string channelId, ulong epoch, byte[] key, LogPosition? position) {
        if (!this._secrets.EpochKeys.TryGetValue(channelId, out var keys)) {
            keys = new Dictionary<ulong, byte[]>();
            this._secrets.EpochKeys[channelId] = keys;
        }

        if (!this._secrets.EpochKeyPositions.TryGetValue(channelId, out var positions)) {
            positions = new Dictionary<ulong, KeyPosition>();
            this._secrets.EpochKeyPositions[channelId] = positions;
        }

        keys[epoch] = key;
        if (position != null) {
            positions[epoch] = new KeyPosition { Seq = position.Seq, Hash = position.Hash.ToByteArray() };
        }

        if (this._channels.TryGetValue(channelId, out var channel)) {
            channel.KeyAcceptedAt[epoch] = this._options.TimeProvider.GetUtcNow();
        }

        var newest = keys.Keys.Max();
        foreach (var old in keys.Keys.Where(e => e + KeptEpochsPerChannel <= newest).ToList()) {
            keys.Remove(old);
            positions.Remove(old);
        }

        this._secretsVersion++;
    }

    private void RemoveChannel(string channelId) {
        lock (this._lock) {
            this._channels.Remove(channelId);
            this._secrets.EpochKeys.Remove(channelId);
            this._secrets.EpochKeyPositions.Remove(channelId);
            // The name version, message times and verified membership stay (they aren't secret): otherwise
            // a server could fake a removal, list the channel again and replay older names, messages or logs.
            this._secretsVersion++;
        }

        this.SaveSecrets();
        this.Publish();
    }

    /// <summary>Remembers a verified message's ID. Call inside the lock.</summary>
    /// <returns>False if the message was already seen.</returns>
    private bool MarkSeen(string id) {
        if (!this._seenMessages.Add(id)) {
            return false;
        }

        this._seenOrder.Enqueue(id);
        while (this._seenOrder.Count > SeenMessageCapacity) {
            this._seenMessages.Remove(this._seenOrder.Dequeue());
        }

        return true;
    }

    /// <summary>A user's name and world for display: as the server last listed them, or as pinned. Call inside the lock.</summary>
    private User UserOf(long userId) => this.KnownUser(userId) ?? new User { UserId = userId, Name = $"user {userId}" };

    private User? KnownUser(long userId) {
        if (this._identities.TryGetValue(userId, out var identity)) {
            return identity.User;
        }

        if (this._users.TryGetValue(userId, out var user)) {
            return user;
        }

        return this._secrets.PinnedIdentities.TryGetValue(userId, out var pinned) && pinned.Name.Length > 0
            ? new User { UserId = userId, Name = pinned.Name, WorldName = pinned.WorldName }
            : null;
    }

    private IdentityKeys EnsureIdentity() {
        lock (this._lock) {
            if (this._identity != null) {
                return this._identity;
            }

            this._identity = IdentityKeys.Generate();
            this._myKeys = MemberKeys.Of(this._identity);
            var (signing, agreement) = this._identity.ExportPrivateKeys();
            this._secrets.SigningPrivateKey = signing;
            this._secrets.AgreementPrivateKey = agreement;
            this._secretsVersion++;
        }

        this.SaveSecrets();
        this.Publish();
        return this._identity;
    }

    private (IdentityKeys Identity, User Me) RequireIdentityAndUser() {
        lock (this._lock) {
            if (this._identity == null || this._me == null || this._state != ConnectionState.Ready) {
                throw new InvalidOperationException("Not connected and logged in.");
            }

            return (this._identity, this._me);
        }
    }

    private long NowMs() => this._options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private T Read<T>(Func<T> read) {
        lock (this._lock) {
            return read();
        }
    }

    private void SetState(ConnectionState state, string? status) {
        lock (this._lock) {
            this._state = state;
            this._status = status;
            if (state != ConnectionState.Ready) {
                this._channelsLoaded = false;
            }

            if (state is ConnectionState.Connecting or ConnectionState.Reconnecting or ConnectionState.Stopped) {
                this._challenge = null;
            }
        }

        this.Publish();
    }

    private void Publish() {
        SessionSnapshot snapshot;
        List<SessionNotice> notices;
        lock (this._lock) {
            notices = [.. this._pendingNotices];
            this._pendingNotices.Clear();
            snapshot = new SessionSnapshot(
                this._state,
                this._status,
                this._me,
                this._identity?.Fingerprint,
                this._channels.Values
                    .OrderBy(channel => channel.Name ?? channel.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(this.ToView)
                    .ToImmutableArray(),
                this._invites.Values.Select(this.ToView).ToImmutableArray(),
                this._limits,
                this._debugAccountsEnabled,
                this._challenge,
                this._secrets.BlockedUsers
                    .Select(id => this._secrets.PinnedIdentities.TryGetValue(id, out var pinned)
                        ? new User { UserId = id, Name = pinned.Name, WorldName = pinned.WorldName }
                        : new User { UserId = id, Name = $"user {id}" })
                    .OrderBy(user => user.Name, StringComparer.OrdinalIgnoreCase)
                    .ToImmutableArray(),
                this._channelsLoaded && this._state == ConnectionState.Ready);
            this._snapshot = snapshot;
        }

        this.InvokeSafely(this.SnapshotChanged, snapshot);
        foreach (var notice in notices) {
            this.InvokeSafely(this.Notice, notice);
        }

        // Every change that can try a name ends here, so this is where names that couldn't be shown are followed up.
        this.FollowUpNames();
    }

    private ChannelView ToView(ChannelState channel) {
        var membership = this.MembershipOf(channel.Id);
        var invitees = membership.Invitees.Select(invitee => (invitee.UserId, invitee.Keys, Rank: Rank.Invited));
        var members = membership.Members.Select(member => (member.UserId, member.Keys, member.Rank))
            .Concat(invitees)
            .Select(member => {
                var user = this.UserOf(member.UserId);
                var pinned = this._secrets.PinnedIdentities.GetValueOrDefault(member.UserId);
                var isMe = member.UserId == this._me?.UserId && member.Keys == this._myKeys;
                var compared = isMe || (pinned is { Compared: true } && pinned.SigningPublicKey.AsSpan().SequenceEqual(member.Keys.SigningPublicKey)
                                                                     && pinned.AgreementPublicKey.AsSpan().SequenceEqual(member.Keys.AgreementPublicKey));
                var replaced = this._identities.TryGetValue(member.UserId, out var identity) && MemberKeys.Of(identity.Identity) != member.Keys;
                return new MemberView(user, member.Rank, member.Keys.Fingerprint, pinned is { KeyChangeUnacknowledged: true }, compared, replaced);
            })
            .OrderByDescending(member => member.Rank)
            .ThenBy(member => member.User.Name, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        var mine = this._me == null ? null : membership.FindMember(this._me.UserId);
        var warning = channel.MembershipWarning;
        if (warning == null && mine != null && mine.Keys != this._myKeys) {
            warning = "Your place in this channel belongs to the identity key you had before you registered again. A moderator must remove you and invite you again.";
        }

        var keyEpoch = this.KeyEpochOf(channel.Id);
        return new ChannelView(channel.Id, channel.Name, keyEpoch ?? channel.ServerEpoch, channel.ServerEpoch,
            this.HasCurrentKey(channel.Id), this.NeedsRekey(channel), mine != null && mine.Keys == this._myKeys ? mine.Rank : Rank.Unspecified,
            members, membership.Head?.Clone(), warning);
    }

    // ================================================================ plumbing

    private Connection RequireConnection() {
        return this._connection ?? throw new SessionDisconnectedException("Not connected to the server.");
    }

    private Task<Response> RequestAsync(ClientFrame frame, CancellationToken ct) {
        return this.RequestAsync(this.RequireConnection(), frame, ct);
    }

    private async Task<Response> RequestAsync(Connection connection, ClientFrame frame, CancellationToken ct, TimeSpan? timeout = null) {
        var response = await connection.RequestAsync(frame, ct, timeout);
        if (response.Error != null) {
            throw new ServerErrorException(response.Error.Code, response.Error.Message);
        }

        return response;
    }

    private void SaveSecrets() {
        ClientSecrets copy;
        long version;
        lock (this._lock) {
            // Message timestamps ride along with whatever else is being saved.
            if (this._replayStateDirty) {
                this._replayStateDirty = false;
                this._replayStateSavedAt = this._options.TimeProvider.GetUtcNow();
                this._secretsVersion++;
            }

            version = this._secretsVersion;
            if (version == Interlocked.Read(ref this._savedSecretsVersion)) {
                return;
            }

            copy = this._secrets.Clone();
        }

        lock (this._saveLock) {
            if (version <= this._savedSecretsVersion) {
                return;
            }

            try {
                this._store.Save(copy);
                Interlocked.Exchange(ref this._savedSecretsVersion, version);
            } catch (Exception ex) {
                this.Log(NoticeLevel.Error, $"Couldn't save keys: {ex.Message}");
            }
        }
    }

    /// <summary>Runs work in the background; <see cref="DisposeAsync"/> waits for it to finish.</summary>
    private void RunBackground(string what, Func<CancellationToken, Task> work) {
        if (Volatile.Read(ref this._disposed) == 1) {
            return;
        }

        var ct = this._runCts?.Token ?? CancellationToken.None;
        var task = Task.Run(async () => {
            try {
                await work(ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                // Stopping.
            } catch (Exception ex) when (Volatile.Read(ref this._disposed) == 0) {
                this.RaiseNotice(NoticeLevel.Warning, $"{what} failed: {ex.Message}");
            } catch {
                // Shutting down; nobody to tell.
            }
        }, ct);

        this._background[task] = 0;
        task.ContinueWith(done => this._background.TryRemove(done, out _), TaskScheduler.Default);
    }

    private void AddTrace(bool outgoing, string summary) {
        this._trace.Enqueue(new TraceEntry(DateTimeOffset.Now, outgoing, summary));
        while (this._trace.Count > this._options.TraceCapacity && this._trace.TryDequeue(out _)) {
        }
    }

    private void RaiseMessage(IncomingMessage message) => this.InvokeSafely(this.MessageReceived, message);

    /// <summary>
    /// Tells the user something. Notices often contain names, channel names or
    /// server text, so they go only to <see cref="Notice"/>, never to the diagnostic log.
    /// </summary>
    private void RaiseNotice(NoticeLevel level, string text, string? channelId = null) {
        this.InvokeSafely(this.Notice, new SessionNotice(level, text, channelId));
    }

    private void Log(NoticeLevel level, string text) {
        try {
            this._options.Log?.Invoke(level, text);
        } catch {
            // Never let logging break the session.
        }
    }

    private void InvokeSafely<T>(Action<T>? handler, T value) {
        if (handler == null) {
            return;
        }

        foreach (var single in handler.GetInvocationList().Cast<Action<T>>()) {
            try {
                single(value);
            } catch (Exception ex) {
                this.Log(NoticeLevel.Error, $"Event handler threw: {ex}");
            }
        }
    }

    private static InvalidOperationException Unexpected(Response response) {
        return new InvalidOperationException($"Unexpected response from server: {response.ResultCase}");
    }

    private static string ValidateChannelName(string name) {
        name = name.Trim();
        if (name.Length == 0 || Encoding.UTF8.GetByteCount(name) > 64) {
            throw new ArgumentException("Channel names must be 1 to 64 bytes long.");
        }

        return name;
    }

    public static string RankName(Rank rank) => rank switch {
        Rank.Admin => "admin",
        Rank.Moderator => "moderator",
        Rank.Member => "member",
        Rank.Invited => "invited",
        _ => "unknown",
    };

    private sealed class ChannelState(string id) {
        public string Id { get; } = id;

        /// <summary>The epoch the server last reported. A hint for fetching keys and rekeying; the key epoch is <see cref="KeyEpochOf"/>.</summary>
        public ulong ServerEpoch { get; set; }
        public bool RekeyPending { get; set; }

        /// <summary>The newest membership log position the server reported. A hint for when to fetch the log.</summary>
        public LogPosition? LogHead { get; set; }

        /// <summary>A fork or hidden change seen in the channel's membership, to keep showing.</summary>
        public string? MembershipWarning { get; set; }

        /// <summary>The newest name the server offered. Only shown once <see cref="TryDecryptName"/> accepts it.</summary>
        public EncryptedName? EncryptedName { get; set; }
        public string? Name { get; set; }

        /// <summary>The version <see cref="Name"/> was accepted at; null if it came from an invite.</summary>
        public NameVersion? NameVersion { get; set; }

        /// <summary>The newest log position a name asked this client to fetch the log for, so it asks once.</summary>
        public ulong? NameLogFetchedFor { get; set; }

        /// <summary>The last epoch whose unusable key made this client rekey automatically.</summary>
        public ulong? BadKeyRekeyEpoch { get; set; }

        /// <summary>When this session accepted each epoch key; keys loaded from disk aren't listed.</summary>
        public Dictionary<ulong, DateTimeOffset> KeyAcceptedAt { get; } = new();
        public string DisplayName => this.Name ?? ChannelView.PlaceholderName(this.Id);
    }

    private sealed class InviteState(InviteInfo info) {
        public InviteInfo Info { get; } = info;
        public string? Name { get; set; }
        public bool Verified { get; set; }

        /// <summary>The keys the verified log says the inviter signed the invite with.</summary>
        public MemberKeys? InviterKeys { get; set; }
    }
}
