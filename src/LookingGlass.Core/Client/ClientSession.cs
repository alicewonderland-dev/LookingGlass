using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Google.Protobuf;
using LookingGlass.Core.Crypto;
using LookingGlass.Core.Membership;
using LookingGlass.Protocol;

namespace LookingGlass.Core.Client;

/// <summary>
/// A client's connection to a LookingGlass server for one character.
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

    private readonly ClientSessionOptions _options;
    private readonly ISecretStore _store;
    private readonly IMembershipProvider _membership;
    private readonly IGroupKeyProvider _groupKeys;
    private readonly Lock _lock = new();
    private readonly Lock _saveLock = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _channelLocks = new();
    // One log sync per channel at a time, so entries are checked in order against one state.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _logLocks = new();
    // Logging in (with the saved login or the identity key) and registering, one request at a time: a try of a saved
    // login the server didn't recognise never runs alongside registering again, so neither's answer can undo the other's.
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private readonly Channel<Event> _inbox = Channel.CreateUnbounded<Event>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentQueue<TraceEntry> _trace = new();
    private readonly ConcurrentDictionary<Task, byte> _background = new();

    // ---- state guarded by _lock
    private readonly ClientSecrets _secrets;
    private IdentityKeys? _identity;
    private MemberKeys? _myKeys;
    private ConnectionState _state = ConnectionState.Stopped;
    private Wording? _status;
    private User? _me;
    private Limits? _limits;
    private bool _debugAccountsEnabled;
    // What to tell the user when the server lists its addresses without the one this client uses (see AddressNotListedText).
    private Wording? _addressNotListed;
    private RegistrationChallenge? _challenge;
    // The server refused the saved login on the current connection. The login is kept, and tried again.
    private bool _loginRejected;
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
    // Users whose identity held here is older than the keys a log just moved them to; Publish has them fetched again.
    private readonly HashSet<long> _identitiesToRefresh = new();
    // The user was told a log moved their own place away from this client's keys (see PlainMessages.ReVerifiedElsewhere).
    private bool _toldKeyMovedAway;
    // When each channel's whole log was last fetched to look into a fork, the checks under way, the claims
    // still to look into (made too soon after a check, or whose check failed), and the re-checks scheduled.
    private readonly Dictionary<string, DateTimeOffset> _forkCheckedAt = new();
    private readonly HashSet<string> _forkChecksRunning = new();
    private readonly Dictionary<string, ForkClaim> _forkClaims = new();
    private readonly HashSet<string> _forkRechecksScheduled = new();
    private readonly HashSet<string> _seenMessages = new();
    // Who is online, as the server said on the current connection: the online flags of members in channel info,
    // and PresenceChanged events. Both are applied on the receive loop as they arrive, because the server queues
    // them in the order they happened (see OnResponse). Forgotten when the connection drops; never saved.
    private readonly Dictionary<long, bool> _presence = new();
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
    // The user has been told the server speaks another protocol version (once, not at every reconnect).
    private int _versionMismatchReported;
    // The last address hint the user was told about (once, not at every reconnect).
    private Wording? _addressNotListedReported;

    /// <summary>
    /// What to tell the user when the server lists its own addresses (Welcome's public_urls) and <paramref name="serverUri"/>
    /// isn't one of them (by origin: scheme, host and port, as the server compares them): it refuses registering, key login
    /// and "Reset my identity" signed for that address. Null if the address is listed, or the server lists none.
    /// </summary>
    internal static Wording? AddressNotListedText(Uri serverUri, IEnumerable<string> publicUrls) {
        // Only what parses as a server address, and not without end: the list comes from the server.
        var listed = publicUrls.Select(url => url.Trim()).Where(url => url.Length <= 200 && ServerOrigin.FromUrl(url) != null).Distinct().Take(10).ToList();
        var origin = ServerOrigin.FromUrl(serverUri.AbsoluteUri);
        if (listed.Count == 0 || (origin != null && origin.IsListedIn(listed))) {
            return null;
        }

        return PlainMessages.AddressNotListed(string.Join(", ", listed), serverUri.AbsoluteUri);
    }

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

    /// <summary>
    /// Starts registering <paramref name="character"/>: the server answers with the code to put in the Lodestone profile.
    /// The code must be the one derived from the address this client connected to, its identity key, the server's nonce,
    /// this request's fresh client nonce and the character (see <see cref="LodestoneCode"/>); any other is refused, never
    /// shown, since it was made for another server, key or request, which is what a malicious server passing on another
    /// server's code would send.
    /// </summary>
    /// <exception cref="RelayedRegistrationCodeException">The server sent a code that isn't this server's for this key.</exception>
    public async Task<RegistrationChallenge> StartRegistrationAsync(Character character, CancellationToken ct = default) {
        var identity = this.EnsureIdentity();
        var connection = this.RequireConnection();
        RegistrationChallenge challenge;
        // After any try of the saved login already under way, which may have logged in meanwhile.
        await this._loginGate.WaitAsync(ct);
        try {
            if (this.Read(() => this._state == ConnectionState.Ready)) {
                throw new InvalidOperationException("You're already logged in, so there's nothing to register.");
            }

            // Exactly the address this connection was made to, as for CompleteRegistration: the code is derived from its origin.
            var serverUrl = this._options.ServerUri.AbsoluteUri;
            // Fresh for this request, so a server can only look for a code matching another server's once asked, and only
            // until this request times out.
            var clientNonce = RandomNumberGenerator.GetBytes(LodestoneCode.ClientNonceSize);
            // Lodestone lookups are queued server-side, so allow more time than usual.
            var response = await this.RequestAsync(connection, new ClientFrame {
                StartRegistration = new StartRegistration {
                    Character = character, Identity = identity.ToBundle(), ServerUrl = serverUrl, ClientNonce = ByteString.CopyFrom(clientNonce),
                },
            }, ct, RegistrationRequestTimeout);

            challenge = response.RegistrationChallenge ?? throw Unexpected(response);
            if (CheckCode(challenge, serverUrl, identity, clientNonce) is { } wrong) {
                // Never the code itself, which is what a user mustn't paste: the log may be read out, or shared to ask for help.
                this.Log(NoticeLevel.Warning, $"Refused the registration code the server sent ({wrong}). It may be passing on another server's code; it wasn't shown or logged.");
                lock (this._lock) {
                    // The server replaced any earlier registration on this connection with this one, which is refused.
                    this._challenge = null;
                    if (this._state == ConnectionState.Registering) {
                        this._state = this._loginRejected ? ConnectionState.LoginNotRecognized : ConnectionState.Unregistered;
                        this._status = this._loginRejected ? PlainMessages.LoginNotRecognized : Wording.Same(NotRegistered);
                    }
                }

                this.Publish();
                throw new RelayedRegistrationCodeException();
            }

            lock (this._lock) {
                this._challenge = challenge;
                this._state = ConnectionState.Registering;
                this._status = Wording.Same(challenge.VerificationSkipped
                    ? "Debug account: no Lodestone verification needed."
                    : $"Put {challenge.Code} in your Lodestone profile, then verify.");
            }
        } finally {
            this._loginGate.Release();
        }

        this.Publish();
        return challenge;
    }

    /// <summary>
    /// Whether a registration challenge's code is the one this client expects: derived from the origin of the address it
    /// connected to, its own identity key, the nonce the server sent, the nonce this client sent (<paramref name="clientNonce"/>)
    /// and the character the server named. A debug account has no code (and nothing to put in a profile).
    /// </summary>
    /// <returns>
    /// Why it isn't, for the log, or null if it is: the address and the character, never the code (a code someone else
    /// may hold the registration for, which nobody should be shown, not even in a log they may share).
    /// </returns>
    internal static string? CheckCode(RegistrationChallenge challenge, string serverUrl, IdentityKeys identity, byte[] clientNonce) {
        if (challenge.VerificationSkipped && challenge.Code.Length == 0) {
            return null;
        }

        if (ServerOrigin.FromUrl(serverUrl) is not { } origin) {
            return $"for character {challenge.LodestoneId}, from an address with no origin, {serverUrl}";
        }

        if (challenge.Nonce.Length != RegistrationProof.NonceSize) {
            return $"for character {challenge.LodestoneId}, from {origin}, with a nonce of {challenge.Nonce.Length} bytes";
        }

        var expected = LodestoneCode.Derive(origin, identity.SigningPublicKey, challenge.Nonce.Span, clientNonce, challenge.LodestoneId);
        return challenge.Code == expected
            ? null
            : $"for character {challenge.LodestoneId}, from {origin}: it isn't the code for that address, this client's identity key and the nonces";
    }

    /// <summary>
    /// Finishes registering, replacing any saved login (even one the server didn't recognise), and logs in. Signed with
    /// the identity key being registered (see <see cref="RegistrationProof"/>), over the challenge and the address this
    /// client connected to, so the server knows the key is this client's and not someone else's public one. Also signed,
    /// with the same keys, is their consent to take over the account's places (see <see cref="KeyRecoveryProof"/>): if the
    /// account had other keys (a lost file, a new computer, "Reset my identity"), its channels, ranks and invites move to
    /// these, and the user is told.
    /// </summary>
    /// <exception cref="InvalidOperationException">No registration was started.</exception>
    public async Task CompleteRegistrationAsync(CancellationToken ct = default) {
        var connection = this.RequireConnection();
        uint restored;
        // Never alongside a try of the old login: its answer could otherwise land after the new login's.
        await this._loginGate.WaitAsync(ct);
        try {
            var (identity, challenge) = this.Read(() => (this._identity, this._challenge));
            if (identity == null || challenge == null) {
                throw new InvalidOperationException("Start registering first.");
            }

            // Exactly the address this connection was made to, as for key login.
            var serverUrl = this._options.ServerUri.AbsoluteUri;
            var response = await this.RequestAsync(connection, new ClientFrame {
                CompleteRegistration = new CompleteRegistration {
                    ServerUrl = serverUrl,
                    Signature = ByteString.CopyFrom(RegistrationProof.Sign(identity, challenge.Nonce.Span, challenge.LodestoneId, serverUrl)),
                    RecoverySignature = ByteString.CopyFrom(KeyRecoveryProof.Sign(identity, challenge.LodestoneId)),
                },
            }, ct, RegistrationRequestTimeout);
            var complete = response.RegistrationComplete ?? throw Unexpected(response);
            restored = complete.PlacesRestored;

            lock (this._lock) {
                this._secrets.DeviceToken = complete.DeviceToken;
                this._secrets.UserId = complete.User.UserId;
                this._challenge = null;
                this._secretsVersion++;
            }

            this.SaveSecrets();
            await this.AuthenticateAsync(connection, ct);
        } finally {
            this._loginGate.Release();
        }

        if (restored > 0) {
            this.RaiseNotice(NoticeLevel.Info, PlainMessages.PlacesRestoredWording(restored));
        }
    }

    /// <summary>
    /// Marks a user's keys verified, after comparing <paramref name="fingerprint"/> with them over /tell:
    /// clears the "key changed" warning and the "fingerprint not compared" state. Only the keys the user
    /// was shown are marked, and only while they are still the ones held for that user, never others
    /// (say, the key someone registered again with, while a row shows the one the log binds them to).
    /// </summary>
    /// <param name="fingerprint">The fingerprint the user was shown, and compared.</param>
    /// <param name="compared">
    /// The user compared fingerprints (advanced mode's "Mark verified"). False for simple mode's "It's really them", which
    /// only says they checked with the person over /tell: the warning or hint goes, but the keys stay "not compared".
    /// </param>
    /// <exception cref="InvalidOperationException">The keys held for the user now don't have that fingerprint.</exception>
    public void AcknowledgeKeyChange(long userId, string fingerprint, bool compared = true) {
        lock (this._lock) {
            if (!this._secrets.PinnedIdentities.TryGetValue(userId, out var pinned)
                || IdentityKeys.FingerprintOf(pinned.SigningPublicKey, pinned.AgreementPublicKey) != fingerprint) {
                throw PlainMessages.Failure(PlainMessages.VerifiedKeyChanged);
            }

            if (pinned.KeyChangeUnacknowledged || pinned.KeyRecovered || (compared && !pinned.Compared)) {
                pinned.KeyChangeUnacknowledged = false;
                pinned.KeyRecovered = false;
                pinned.Compared |= compared;
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

    /// <summary>
    /// Tries the saved login again now, after the server didn't recognise it (it is tried again by itself too, now
    /// and then), and if it is still refused, signing in with the identity key. While disconnected, reconnects
    /// instead, which tries both.
    /// </summary>
    /// <exception cref="InvalidOperationException">The server still doesn't recognise the login.</exception>
    public async Task RetryLoginAsync(CancellationToken ct = default) {
        if (this._connection is not { } connection) {
            this.Reconnect();
            return;
        }

        bool refused;
        try {
            refused = await this.TryRejectedLoginAsync(connection, userAsked: true, ct);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            // As when logging in on connecting fails: start afresh.
            connection.Abort($"Logging in failed: {ex.Message}");
            throw;
        }

        if (refused) {
            throw new InvalidOperationException("The server still doesn't recognise your login.");
        }
    }

    /// <summary>
    /// "Reset my identity", first step, while logged in: asks the server to retire this identity key, signed with it over
    /// this login (see <see cref="RetireIdentityProof"/>). The server revokes every login of the account (this one too)
    /// and never lets the key sign in or be registered again, so a copy of it left anywhere is useless there from now on.
    /// The saved login is dropped; the session can do nothing more with the server. Dispose it, and reset the keys next
    /// (see <see cref="ClientSecrets.ResetIdentity"/>), then register the new ones through the Lodestone.
    /// </summary>
    /// <exception cref="InvalidOperationException">Not logged in.</exception>
    /// <exception cref="ServerErrorException">The server refused, or is too old to know the request ("Unknown request.").</exception>
    public async Task RetireIdentityAsync(CancellationToken ct = default) {
        var connection = this.RequireConnection();
        // Never alongside a login: what this signs is the login the connection uses now.
        await this._loginGate.WaitAsync(ct);
        try {
            var (identity, me, token) = this.Read(() => this._state == ConnectionState.Ready && connection == this._connection
                ? (this._identity, this._me, this._secrets.DeviceToken)
                : (null, null, null));
            if (identity == null || me == null || token == null) {
                throw new InvalidOperationException("You're not logged in, so the server can't be asked to retire your key.");
            }

            // Exactly the address this connection was made to, as for key login.
            var serverUrl = this._options.ServerUri.AbsoluteUri;
            var response = await this.RequestAsync(connection, new ClientFrame {
                RetireIdentity = new RetireIdentity {
                    ServerUrl = serverUrl,
                    Signature = ByteString.CopyFrom(RetireIdentityProof.Sign(identity, me.UserId, token, serverUrl)),
                },
            }, ct);
            if (response.Ack == null) {
                throw Unexpected(response);
            }

            lock (this._lock) {
                // Revoked with the rest: nothing to try again. The key is kept until the reset replaces it.
                this._secrets.DeviceToken = null;
                this._loginRejected = false;
                this._me = null;
                this._state = ConnectionState.Unregistered;
                this._status = PlainMessages.IdentityRetired;
                this._secretsVersion++;
            }
        } finally {
            this._loginGate.Release();
        }

        this.SaveSecrets();
        this.Publish();
    }

    /// <summary>Forgets the device token, for example to register again.</summary>
    public void ForgetAccount() {
        lock (this._lock) {
            this._secrets.DeviceToken = null;
            this._secrets.UserId = null;
            this._loginRejected = false;
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

        var found = response.Identities?.Identities_.FirstOrDefault() ?? throw new InvalidOperationException($"{name}@{worldName} isn't registered with LookingGlass.");
        // Trusted on first use: the server says these are their keys (see "fingerprint not compared").
        var invitee = this.AcceptIdentities([found]).FirstOrDefault()
            ?? throw PlainMessages.Failure(PlainMessages.InvalidKeys($"{name}@{worldName}"));
        var inviteeKeys = MemberKeys.Of(invitee.Identity);
        var who = $"{invitee.User.Name}@{invitee.User.WorldName}";

        await this.AppendEntryAsync(channelId, ct, membership => {
            if (membership.FindMember(invitee.User.UserId) is { } existing) {
                throw existing.Keys == inviteeKeys
                    ? new InvalidOperationException($"{who} is already a member.")
                    : PlainMessages.Failure(PlainMessages.MemberUnderOldKey(who));
            }

            var entry = membership.Create(MembershipEntryKind.Invite, invitee.User.UserId, identity, me.UserId, this.NowMs(), inviteeKeys);
            var (sealedName, signature) = this._groupKeys.SealInvite(channelName, channelId, MembershipEntries.PositionOf(entry), invitee.User.UserId, inviteeKeys.AgreementPublicKey, identity, me.UserId);
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
        if (!accept) {
            // An invite made for an old key (before a reset) can't be declined with the new one: only that key could
            // sign it. Declining it removes it from the list instead.
            IChannelMembership? membership;
            try {
                membership = await this.SyncLogAsync(channelId, ct);
            } catch (ServerErrorException) {
                // Gone, say: declining finds out below.
                membership = null;
            }

            if (membership != null && this.Read(() => this.HoldsOldKeyPlace(membership))) {
                await this.ForgetChannelAsync(channelId, ct);
                return;
            }
        }

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
            var (hasKey, pending, canName) = this.Read(() => {
                var channel = this._channels.GetValueOrDefault(channelId);
                // An invite moved to new keys knows no name; if nobody holds the key, this client names it (see RekeyAsync).
                return (this.HasCurrentKey(channelId), channel != null && this.NeedsRekey(channel), channel?.Name != null || this.NamesNewKeyItself(channelId));
            });
            if (designated && pending && canName) {
                // Nobody else who could is online to share the key. (The server's RekeyNeeded arrived before
                // this response, while the channel was unknown, so it was ignored.)
                if (this._options.AutoRekeyWhenDesignated) {
                    this.RaiseNotice(NoticeLevel.Info, PlainMessages.JoinedMakingKey, channelId);
                    this.RunBackground(PlainMessages.Rekeying, rekeyCt => this.RekeyAsync(channelId, rekeyCt));
                } else {
                    this.RaiseNotice(NoticeLevel.Info, PlainMessages.JoinedNobodyToShareKey, channelId);
                }
            } else if (!hasKey) {
                this.RaiseNotice(NoticeLevel.Info, PlainMessages.JoinedWaitingForKey, channelId);
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
                throw PlainMessages.Failure(PlainMessages.InviteForOldKey);
            }

            var entry = membership.Create(accept ? MembershipEntryKind.Accept : MembershipEntryKind.Decline, me.UserId, identity, me.UserId, this.NowMs());
            return (entry, new ClientFrame { RespondToInvite = new RespondToInvite { ChannelId = channelId, Entry = entry } });
        });
    }

    public async Task LeaveAsync(string channelId, CancellationToken ct = default) {
        var (identity, me) = this.RequireIdentityAndUser();
        await this.AppendEntryAsync(channelId, ct, membership => {
            if (membership.FindMember(me.UserId) is { } mine && mine.Keys != MemberKeys.Of(identity)) {
                // Only the old keys could sign it. Said before anything is sent, in plain words.
                throw PlainMessages.Failure(PlainMessages.CantLeaveOldKeyWording);
            }

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
        if (removed && this.Read(() => this._channels.GetValueOrDefault(channelId)?.Name == null && !this.NamesNewKeyItself(channelId))) {
            // Back with new keys and no key for the channel yet (so not knowing its name): another member makes the new
            // key, as the server asks one who can.
            return;
        }

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

    /// <summary>
    /// "Remove from my list", for a channel (or invite) whose place belongs to identity keys this user no longer has (see
    /// <see cref="ChannelView.OldKeyMembership"/>): such a place can't be left, as only the old keys could sign that.
    /// Not a log entry (see <see cref="ForgetChannel"/> in the protocol): the server just stops listing the channel to
    /// this account, and this client forgets it (its keys; the plugin drops its slot, nickname and colour with it). The
    /// other members still see the old key as a member, until a moderator removes it.
    /// </summary>
    /// <exception cref="InvalidOperationException">This user is a member under their current keys: leave instead.</exception>
    public async Task ForgetChannelAsync(string channelId, CancellationToken ct = default) {
        this.RequireIdentityAndUser();
        if (this.Read(() => this.IsMember(channelId))) {
            throw PlainMessages.Failure(PlainMessages.ForgetMembership);
        }

        try {
            await this.RequestAsync(new ClientFrame { ForgetChannel = new ForgetChannel { ChannelId = channelId } }, ct);
        } catch (ServerErrorException ex) when (ex.Code == ErrorCode.NotFound) {
            // The server doesn't list it to this account (any more): nothing left to do there.
        }

        lock (this._lock) {
            this._invites.Remove(channelId);
        }

        this.RemoveChannel(channelId);
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
                var keyEpoch = this.KeyEpochOf(channelId) ?? throw PlainMessages.Failure(PlainMessages.NoKeyToRename);

                // The server and other members refuse a name that isn't newer than the
                // current one, so beat both the name accepted here and the one offered.
                ulong current = 0;
                if (this._secrets.ChannelNameVersions.TryGetValue(channelId, out var held) && held.Epoch == keyEpoch) {
                    current = held.Revision;
                }

                if (channel.EncryptedName is { } offered && offered.Epoch == keyEpoch
                    && SignerKeys(membership, offered.AuthorId, offered.LogPosition) is { } author
                    && this._groupKeys.VerifyName(offered, channelId, author.SigningPublicKey)) {
                    current = Math.Max(current, offered.Revision);
                }

                // Only reachable if someone set a huge revision on purpose. It resets with the next epoch.
                if (current >= ProtocolInfo.MaxNameRevision) {
                    throw PlainMessages.Failure(PlainMessages.RenamedTooOften);
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
            } catch (MembershipException) when (this.Read(() => this.HoldsOldKeyPlace(membership))) {
                // Rather than "it isn't signed with the key the log knows its author by": what that means here.
                throw PlainMessages.Failure(PlainMessages.OldKeyCantChangeMembersWording);
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

                if (name == null && this.Read(() => this.NamesNewKeyItself(channelId))) {
                    // Back with new keys where nobody else holds the key (or nobody else is in the channel): nobody can share
                    // it, or tell its name, so it gets a name of its own, which can be changed.
                    name = PlainMessages.RestoredChannelName;
                } else if (name == null) {
                    throw PlainMessages.Failure(PlainMessages.NoKeyToRekey);
                }

                if (membership.FindMember(me.UserId)?.Keys != MemberKeys.Of(identity)) {
                    throw PlainMessages.Failure(PlainMessages.OldKeyCantRekey);
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
                    throw PlainMessages.Failure(PlainMessages.CantSealTo($"{who.Name}@{who.WorldName}", ex.InnerException?.Message), ex);
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
                    force = false;
                    continue;
                }

                lock (this._lock) {
                    this.StoreEpochKey(channelId, newEpoch, key, position);
                    if (this._channels.TryGetValue(channelId, out var channel)) {
                        channel.ServerEpoch = Math.Max(channel.ServerEpoch, newEpoch);
                        // Not a request made since at this epoch (a leave just after this rekey was applied, say), which
                        // can arrive before the server's answer to it.
                        channel.SettleRekey(newEpoch);
                        channel.NoKeyHolder = false;
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

            // Whatever head the server claims, it must still have the newest entry this client verified (a removal
            // it made, say), and show everything after it. If not, it refuses keys for a change it is hiding.
            if (!await this.ConfirmHeadAsync(channelId, ct)) {
                this.WarnAboutMembership(channelId, PlainMessages.MembershipHidden);
                throw PlainMessages.Failure(PlainMessages.ServerRefusesKey);
            }

            throw PlainMessages.Failure(PlainMessages.RekeyConflicts);
        } catch (Exception ex) when (ex is not OperationCanceledException && this.Read(() => this.RemovalAwaitingRekey(channelId)) != null) {
            // This rekey was to make a removal (or a leave) take effect, and it didn't, however the server refused it.
            if (ex is ServerErrorException or TimeoutException) {
                // As after conflicts: a server that refuses keys for a change it is hiding may not show it either.
                try {
                    if (!await this.ConfirmHeadAsync(channelId, ct)) {
                        this.WarnAboutMembership(channelId, PlainMessages.MembershipHidden);
                    }
                } catch (Exception confirm) when (confirm is ServerErrorException or SessionDisconnectedException or TimeoutException) {
                    this.Log(NoticeLevel.Warning, $"Couldn't check the membership log of {channelId} after a refused rekey: {confirm.Message}");
                }
            }

            this.WarnRemovalNotInEffect(channelId);
            throw;
        } finally {
            gate.Release();
        }
    }

    /// <summary>
    /// The log position of the newest removal (or leave) that no key this client holds was made after, if any: the new
    /// key that would leave whoever went out hasn't been shared yet, so the other members still share theirs. Unknown,
    /// so null, without a key held (just joined), or for a membership saved before removals were tracked. Call inside the lock.
    /// </summary>
    private ulong? RemovalAwaitingRekey(string channelId) {
        if (this.MembershipOf(channelId).MembersLeftAt is not { } left
            || !this._secrets.EpochKeyPositions.TryGetValue(channelId, out var positions) || positions.Count == 0) {
            return null;
        }

        // Keys are only kept for positions in the verified log, so one made at or after the removal was made after it.
        return positions.Values.Any(position => position.Seq >= left) ? null : left;
    }

    /// <summary>
    /// Tells the user, and shows on the channel until a key made after it is held, that a removal (or leave) hasn't taken
    /// effect for the other members: the new key that leaves whoever went out couldn't be shared.
    /// </summary>
    private void WarnRemovalNotInEffect(string channelId) {
        Wording text;
        lock (this._lock) {
            if (!this._channels.TryGetValue(channelId, out var channel) || this.RemovalAwaitingRekey(channelId) is not { } seq) {
                return;
            }

            text = PlainMessages.RemovalNotInEffect(channel.DisplayName, seq);
            if (channel.RemovalWarning == text) {
                return;
            }

            channel.RemovalWarning = text;
        }

        this.Publish();
        this.RaiseNotice(NoticeLevel.Warning, text, channelId);
    }

    // ================================================================ messages

    public Task SendTextAsync(string channelId, string text, CancellationToken ct = default) =>
        this.SendAsync(channelId, LinkedText.Plain(text), ct);

    /// <summary>
    /// Sends a message with links (see <see cref="MessageContent"/>): its text as older clients show it, and its links,
    /// all inside the encrypted, signed plaintext. At most <see cref="ChatLinks.MaxPerMessage"/> links, each over a
    /// "[name]" in the text.
    /// </summary>
    public async Task SendAsync(string channelId, LinkedText linked, CancellationToken ct = default) {
        var text = linked.Text;
        if (string.IsNullOrWhiteSpace(text)) {
            throw new ArgumentException("Message is empty.", nameof(linked));
        }

        // What a recipient would accept, and nothing else: a link that wouldn't pass there isn't sent as one.
        var content = MessageContent.Encode(linked);
        var links = MessageContent.ValidLinks(text, content.Text.Links);
        if (links.Count != linked.Links.Count) {
            throw new ArgumentException("A link in the message isn't one LookingGlass can send.", nameof(linked));
        }

        for (var attempt = 0; attempt < 4; attempt++) {
            var (identity, me) = this.RequireIdentityAndUser();
            var pending = this.Read(() => {
                var channel = this._channels.GetValueOrDefault(channelId) ?? throw new InvalidOperationException("You're not in that channel.");
                if (!this.IsMember(channelId)) {
                    throw this.MembershipOf(channelId).FindMember(me.UserId) != null
                        ? PlainMessages.Failure(PlainMessages.OldKeyChannelWording)
                        : new InvalidOperationException("You haven't joined that channel.");
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
                    throw PlainMessages.Failure(PlainMessages.NoKeyToSend);
                }

                if (this.Read(() => this._channels.GetValueOrDefault(channelId) is { } channel && this.NeedsRekey(channel))) {
                    continue;
                }
            }

            // Always the newest key accepted, never an epoch the server merely claims.
            var (epoch, key) = this.Read(() => {
                var held = this.KeyEpochOf(channelId) ?? throw PlainMessages.Failure(PlainMessages.NoKeyToSend with { Technical = "You don't have this channel's key yet." });
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
                        channel.MarkRekeyPending(channel.ServerEpoch);
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
                DateTimeOffset.FromUnixTimeMilliseconds(timestamp)) { Links = links });
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
            string? failure = null;
            try {
                var socket = await this.ConnectSocketAsync(ct);
                connection = new Connection(socket, this._options.MaxReceiveBytes, this._options.RequestTimeout,
                    ev => this.OnEvent(connection, ev), response => this.OnResponse(connection, response), this.AddTrace);
                this._connection = connection;
                connection.Start();
                await this.HandshakeAsync(connection, ct);
                delay = this._options.ReconnectMinDelay;
                await this.RetryRejectedLoginAsync(connection, ct);
                await connection.Closed.WaitAsync(ct);
                this.Log(NoticeLevel.Info, connection.CloseReason);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                break;
            } catch (Exception ex) {
                failure = $"Connection failed: {ex.Message}";
                this.Log(NoticeLevel.Warning, failure);
                lock (this._lock) {
                    this._status = Wording.Same(failure);
                }
            } finally {
                this._connection = null;
                lock (this._lock) {
                    // What one connection was told about presence says nothing about the next one.
                    this._presence.Clear();
                }

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

            // Why, too: a connection that keeps failing (a wrong address, a redirect) should say so where the user looks.
            this.SetState(ConnectionState.Reconnecting, failure == null ? $"Reconnecting in {delay.TotalSeconds:0} s" : $"{failure} (reconnecting in {delay.TotalSeconds:0} s)");
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
        // Never following redirects (see WebSocketConnector).
        return await (this._options.Connect ?? WebSocketConnector.ConnectAsync)(this._options.ServerUri, ct);
    }

    private async Task HandshakeAsync(Connection connection, CancellationToken ct) {
        var hello = new Hello { ClientVersion = this._options.ClientVersion };
        hello.ProtocolVersions.Add(this._options.ProtocolVersion);
        hello.Capabilities.Add(ProtocolInfo.Capabilities.Chat);

        Response response;
        try {
            response = await this.RequestAsync(connection, new ClientFrame { Hello = hello }, ct);
        } catch (ServerErrorException ex) when (ex.Code == ErrorCode.UnsupportedVersion) {
            // Plugin and server must speak the same version; which one is older, only the server's message can say.
            var text = $"This plugin (protocol version {this._options.ProtocolVersion}) and the server speak different protocol versions, so it can't connect. " +
                       $"The server says: \"{ex.ServerMessage}\" If the server is the older one, its operator needs to update it.";
            if (Interlocked.Exchange(ref this._versionMismatchReported, 1) == 0) {
                this.RaiseNotice(NoticeLevel.Warning, text);
            }

            throw new InvalidOperationException(text, ex);
        }

        var welcome = response.Welcome ?? throw Unexpected(response);
        var addressNotListed = AddressNotListedText(this._options.ServerUri, welcome.PublicUrls);

        lock (this._lock) {
            this._limits = welcome.Limits;
            this._debugAccountsEnabled = welcome.DebugAccountsEnabled;
            this._addressNotListed = addressNotListed;
        }

        if (!string.IsNullOrWhiteSpace(welcome.Announcement)) {
            this.RaiseNotice(NoticeLevel.Info, welcome.Announcement);
        }

        // Before the user tries to register: once per session (and again if what the server lists changes), not on every reconnect.
        if (addressNotListed != null && Interlocked.Exchange(ref this._addressNotListedReported, addressNotListed) != addressNotListed) {
            this.RaiseNotice(NoticeLevel.Warning, addressNotListed);
        }

        await this._loginGate.WaitAsync(ct);
        try {
            bool hasToken;
            lock (this._lock) {
                // Decided afresh on every connection.
                this._loginRejected = false;
                hasToken = this._secrets.DeviceToken != null;
            }

            if (!hasToken) {
                this.SetState(ConnectionState.Unregistered, NotRegistered);
                return;
            }

            try {
                await this.AuthenticateAsync(connection, ct);
            } catch (ServerErrorException ex) when (ex.Code == ErrorCode.NotAuthenticated) {
                // A server that lost the login but knows the account and its key signs it back in, without the Lodestone.
                if (!await this.TryKeyLoginAsync(connection, ct)) {
                    this.RejectLogin(connection, ex);
                }
            }
        } finally {
            this._loginGate.Release();
        }
    }

    private const string NotRegistered = "Not registered on this server.";

    /// <summary>
    /// The server refused the saved login, and signing in with the identity key didn't work either (or there is no key).
    /// The login is kept, never discarded: this may be the wrong server, or one reset or restored from a backup, and the
    /// right one may be back soon. Only a key login, registering again (or "Forget account") replaces it.
    /// </summary>
    private void RejectLogin(Connection connection, ServerErrorException ex) {
        this.Log(NoticeLevel.Info, $"The server doesn't recognise the saved login: {ex.ServerMessage}");
        lock (this._lock) {
            if (connection != this._connection) {
                return;
            }

            this._loginRejected = true;
            // Registering again goes on (its challenge stays) whatever a try of the old login says.
            if (this._state != ConnectionState.Registering) {
                this._state = ConnectionState.LoginNotRecognized;
                this._status = PlainMessages.LoginNotRecognized;
            }
        }

        this.Publish();
    }

    /// <summary>
    /// While the server doesn't recognise the saved login, tries it again on this connection now and then, waiting
    /// longer each time (<see cref="ClientSessionOptions.LoginRetryMinDelay"/> doubling up to <see cref="ClientSessionOptions.LoginRetryMaxDelay"/>):
    /// the right server, or its database, may be back. Staying connected keeps registering again possible meanwhile.
    /// Returns once logged in, once there's nothing to try (registered again, or the login was forgotten), or once
    /// the connection closes; a reconnect tries the login again itself.
    /// </summary>
    private async Task RetryRejectedLoginAsync(Connection connection, CancellationToken ct) {
        var delay = this._options.LoginRetryMinDelay;
        while (this.Read(() => this._loginRejected && this._secrets.DeviceToken != null)) {
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                await Task.WhenAny(Task.Delay(delay, wait.Token), connection.Closed);
                await wait.CancelAsync();
            }

            ct.ThrowIfCancellationRequested();
            if (connection.Closed.IsCompleted) {
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, this._options.LoginRetryMaxDelay.Ticks));
            try {
                await this.TryRejectedLoginAsync(connection, userAsked: false, ct);
            } catch (Exception) when (connection.Closed.IsCompleted && !ct.IsCancellationRequested) {
                // Dropped meanwhile: the reconnect tries again.
                return;
            }
        }
    }

    /// <summary>
    /// Tries a saved login the server refused on this connection once more, unless something else has happened
    /// meanwhile: logged in, registered again, the login forgotten, or (unless the user asked) registering again under way.
    /// </summary>
    /// <returns>Whether it was tried and refused again.</returns>
    private async Task<bool> TryRejectedLoginAsync(Connection connection, bool userAsked, CancellationToken ct) {
        await this._loginGate.WaitAsync(ct);
        try {
            // Decided inside the gate, after any registration request: CompleteRegistration replaces the login, and logs in.
            var due = this.Read(() => connection == this._connection && this._loginRejected && this._secrets.DeviceToken != null
                                      && (this._state == ConnectionState.LoginNotRecognized
                                          || (this._state == ConnectionState.Registering && (userAsked || this.ChallengeExpired()))));
            if (!due) {
                return false;
            }

            try {
                await this.AuthenticateAsync(connection, ct);
                this.Log(NoticeLevel.Info, "The server recognises the saved login again");
                return false;
            } catch (ServerErrorException ex) when (ex.Code == ErrorCode.NotAuthenticated) {
                // Signing in with the key was tried on connecting; the user asking again may be because the server now knows it.
                if (userAsked && await this.TryKeyLoginAsync(connection, ct)) {
                    return false;
                }

                this.RejectLogin(connection, ex);
                return true;
            }
        } finally {
            this._loginGate.Release();
        }
    }

    /// <summary>
    /// Signs in with the identity key, after the server refused the saved login: a server that knows the account and its
    /// current key gives this device a new login, which replaces the saved one, and the client logs in with it. The
    /// signature names the server's address as this client connected to it, which the server checks against its configured
    /// addresses. Besides, this key is only used for this address (secrets are kept per server address, and only copied to
    /// another address the server itself lists as its own: see ServerMove), so it isn't
    /// registered on a server a relay could pass the signature to.
    /// Call inside <see cref="_loginGate"/>.
    /// </summary>
    /// <returns>
    /// Whether it worked. False without asking if there is no key or no account to sign in to (a new client, or one
    /// whose keys are gone, registers instead), and false if the server refuses: it has never known the account, the
    /// key was replaced by registering again, it is limiting attempts, or it is too old to know key login.
    /// </returns>
    private async Task<bool> TryKeyLoginAsync(Connection connection, CancellationToken ct) {
        var (identity, userId) = this.Read(() => (this._identity, this._secrets.UserId));
        if (identity == null || userId is not { } id) {
            return false;
        }

        // Exactly the address this connection was made to.
        var serverUrl = this._options.ServerUri.AbsoluteUri;
        try {
            var response = await this.RequestAsync(connection, new ClientFrame { StartKeyLogin = new StartKeyLogin { UserId = id } }, ct);
            var challenge = response.KeyLoginChallenge ?? throw Unexpected(response);
            if (challenge.Challenge.Length != KeyLoginProof.ChallengeSize) {
                this.Log(NoticeLevel.Warning, "The server sent a malformed key login challenge");
                return false;
            }

            response = await this.RequestAsync(connection, new ClientFrame {
                CompleteKeyLogin = new CompleteKeyLogin {
                    Challenge = challenge.Challenge,
                    ServerUrl = serverUrl,
                    Signature = ByteString.CopyFrom(KeyLoginProof.Sign(identity, challenge.Challenge.Span, id, serverUrl)),
                },
            }, ct);
            var complete = response.KeyLoginComplete ?? throw Unexpected(response);
            if (string.IsNullOrEmpty(complete.DeviceToken)) {
                return false;
            }

            lock (this._lock) {
                this._secrets.DeviceToken = complete.DeviceToken;
                this._secretsVersion++;
            }

            this.SaveSecrets();
        } catch (ServerErrorException ex) when (ex.Code is ErrorCode.NotAuthenticated or ErrorCode.RateLimited or ErrorCode.InvalidRequest) {
            this.Log(NoticeLevel.Info, $"Signing in with the identity key didn't work: {ex.ServerMessage}");
            return false;
        }

        this.Log(NoticeLevel.Info, "Signed in with the identity key; the server gave this device a new login");
        try {
            await this.AuthenticateAsync(connection, ct);
            return true;
        } catch (ServerErrorException ex) when (ex.Code == ErrorCode.NotAuthenticated) {
            this.Log(NoticeLevel.Warning, $"The server refused the login it just gave: {ex.ServerMessage}");
            return false;
        }
    }

    /// <summary>The registration challenge can't be completed any more (or there is none). Call inside the lock.</summary>
    private bool ChallengeExpired() {
        return this._challenge == null || this._options.TimeProvider.GetUtcNow().ToUnixTimeSeconds() >= this._challenge.ExpiresUnix;
    }

    private async Task AuthenticateAsync(Connection connection, CancellationToken ct) {
        var token = this.Read(() => this._secrets.DeviceToken) ?? throw new InvalidOperationException("Not registered.");
        var response = await this.RequestAsync(connection, new ClientFrame { Authenticate = new Authenticate { DeviceToken = token } }, ct);
        var ok = response.AuthenticateOk ?? throw Unexpected(response);

        lock (this._lock) {
            this._me = ok.User;
            this._users[ok.User.UserId] = ok.User;
            this._secrets.UserId = ok.User.UserId;
            this._loginRejected = false;
            // Logged in: a registration under way (from before a saved login worked again) is moot.
            this._challenge = null;
            this._state = ConnectionState.Ready;
            // Ready, but the channel list is only complete once RefreshAsync has fetched it.
            this._channelsLoaded = false;
            this._status = Wording.Same($"Connected as {ok.User.Name}@{ok.User.WorldName}");
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
        // Only channels this user is in or invited to, as last verified; not every channel ever left.
        request.Known.AddRange(this.Read(() => this._secrets.Memberships.Keys.Union(this._memberships.Keys).ToList()
            .Select(channelId => (channelId, membership: this.MembershipOf(channelId)))
            .Where(known => known.membership.Head != null && this._me != null
                            && (known.membership.FindMember(this._me.UserId) != null || known.membership.FindInvitee(this._me.UserId) != null))
            .Select(known => (known.channelId, head: known.membership.Head))
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
            this.RaiseNotice(NoticeLevel.Warning, PlainMessages.IdentitiesMissing);
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
            // Without the name (say, after re-verifying with new keys) this client can't rekey; another member must. Unless
            // nobody else is in the channel, or nobody else who could holds its key: then it is this client's to do, under
            // a name of its own (see RekeyAsync).
            var designated = this.Read(() => channels
                .Where(info => info.RekeyPending && this.IsMember(info.ChannelId)
                               && ((info.RekeyDesignated && (this._channels.GetValueOrDefault(info.ChannelId)?.Name != null || this.NamesNewKeyItself(info.ChannelId)))
                                   || (this.IsAloneIn(info.ChannelId) && !this.HasCurrentKey(info.ChannelId))))
                .Select(info => info.ChannelId)
                .ToList());
            foreach (var channelId in designated) {
                this.RunBackground(PlainMessages.Rekeying, rekeyCt => this.RekeyAsync(channelId, rekeyCt));
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
        // The server said its log goes further than it would show.
        var stalled = false;
        Wording? problem = null;
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
                        // A position already verified: the same entry again, or a second one there. (Too far back
                        // to remember its hash, it is let go: a fork there shows at the head too.)
                        if (state.HashAt(entry.Seq) is { } known && !known.AsSpan().SequenceEqual(MembershipEntries.Hash(entry))) {
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
                        problem = PlainMessages.MembershipChangeRefused(verdict.Reason);
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
                if (log.Head == null || (log.Entries.Count == 0 && (state.Head == null || log.Head.Seq > state.Head.Seq))) {
                    stalled = true;
                }

                if (log.Entries.Count == 0) {
                    break;
                }

                foreach (var entry in log.Entries) {
                    pending.Enqueue(entry);
                }
            }

            if (problem == null && conflicting == null && stalled) {
                problem = PlainMessages.MembershipNotShown;
            } else if (problem == null && conflicting == null && freshHead is { Hash.Length: > 0 } && state.Head != null) {
                if (freshHead.Seq < state.Head.Seq) {
                    problem = PlainMessages.MembershipOlder;
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
        } else if (this.Read(() => this._forkClaims.ContainsKey(channelId) && this.ForkCheckDelay(channelId) == TimeSpan.Zero)) {
            // A claim kept earlier (the last check was too recent, or failed) that can be looked into now.
            try {
                await this.RecheckForkClaimAsync(channelId, ct, connection);
            } catch (Exception ex) when (ex is ServerErrorException or SessionDisconnectedException or TimeoutException) {
                // Kept for the next sync; this one stands.
                this.Log(NoticeLevel.Warning, $"Couldn't look into a possible fork of {channelId}: {ex.Message}");
            }
        }

        return this.Read(() => this.MembershipOf(channelId));
    }

    /// <summary>
    /// The server showed an entry, or a head, that differs from the log this client verified at the same
    /// position. An entry validly signed by keys the log knows, after the same entry as the one verified
    /// at its position but not that one, is a fork in itself, and the user is told straight away. Anything
    /// else needs the server's whole log: it is fetched and replayed, and if it is valid and it (or the
    /// offered entry) differs from what was verified here, two validly signed versions of the log exist, and
    /// the user is told. That fetch happens at most once per <see cref="ClientSessionOptions.ForkCheckInterval"/>
    /// per channel; a claim made sooner, or whose fetch failed, is kept and looked into later.
    /// </summary>
    private async Task CheckForkAsync(string channelId, MembershipEntry? candidate, CancellationToken ct, Connection? connection) {
        if (candidate != null && this.Read(() => this.ProvesFork(channelId, candidate))) {
            this.ReportFork(channelId, candidate.Seq);
            return;
        }

        TimeSpan wait;
        lock (this._lock) {
            wait = this.ForkCheckDelay(channelId);
            if (wait > TimeSpan.Zero) {
                // Too soon: keep the claim (an entry rather than none), and look into it once the interval has passed.
                if (candidate != null || !this._forkClaims.ContainsKey(channelId)) {
                    this._forkClaims[channelId] = new ForkClaim(candidate);
                }
            } else {
                this._forkChecksRunning.Add(channelId);
                // This check covers a head claim kept earlier; an entry kept earlier too, unless this one has its own.
                this._forkClaims.Remove(channelId, out var kept);
                if (kept?.Candidate != null && candidate == null) {
                    candidate = kept.Candidate;
                } else if (kept?.Candidate != null) {
                    this._forkClaims[channelId] = kept;
                }
            }
        }

        if (wait > TimeSpan.Zero) {
            this.ScheduleForkRecheck(channelId, wait);
            return;
        }

        var completed = false;
        try {
            await this.CheckForkWithLogAsync(channelId, candidate, ct, connection);
            completed = true;
        } finally {
            lock (this._lock) {
                this._forkChecksRunning.Remove(channelId);
                if (completed) {
                    // Only a check that got the log counts against the interval.
                    this._forkCheckedAt[channelId] = this._options.TimeProvider.GetUtcNow();
                } else if (candidate != null || !this._forkClaims.ContainsKey(channelId)) {
                    // Looked into again at the next sync of this channel's log.
                    this._forkClaims[channelId] = new ForkClaim(candidate);
                }
            }
        }

        if (this.Read(() => this._forkClaims.ContainsKey(channelId))) {
            // Claims kept while this check ran.
            this.ScheduleForkRecheck(channelId, this.Read(() => this.ForkCheckDelay(channelId)));
        }
    }

    /// <summary>A claim of a fork that couldn't be looked into yet: an entry that differs from the one verified at its position, or (null) a head that does.</summary>
    private sealed record ForkClaim(MembershipEntry? Candidate);

    /// <summary>How long until the channel's whole log may be fetched to look into a fork; zero if now. Call inside the lock.</summary>
    private TimeSpan ForkCheckDelay(string channelId) {
        if (this._forkChecksRunning.Contains(channelId)) {
            return this._options.ForkCheckInterval;
        }

        if (!this._forkCheckedAt.TryGetValue(channelId, out var last)) {
            return TimeSpan.Zero;
        }

        var left = last + this._options.ForkCheckInterval - this._options.TimeProvider.GetUtcNow();
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>Looks into the channel's kept fork claim, if any, after <paramref name="wait"/>; once at a time per channel.</summary>
    private void ScheduleForkRecheck(string channelId, TimeSpan wait) {
        if (!this.Read(() => this._forkRechecksScheduled.Add(channelId))) {
            return;
        }

        this.RunBackground(PlainMessages.CheckingMembership, async ct => {
            try {
                await Task.Delay(wait, this._options.TimeProvider, ct);
            } finally {
                lock (this._lock) {
                    this._forkRechecksScheduled.Remove(channelId);
                }
            }

            try {
                await this.RecheckForkClaimAsync(channelId, ct, null);
            } catch (Exception ex) when (ex is ServerErrorException or SessionDisconnectedException or TimeoutException) {
                // Kept, and looked into at the next sync of this channel's log.
                this.Log(NoticeLevel.Warning, $"Couldn't look into a possible fork of {channelId}: {ex.Message}");
            }
        });
    }

    /// <summary>Looks into the channel's kept fork claim, if any.</summary>
    private async Task RecheckForkClaimAsync(string channelId, CancellationToken ct, Connection? connection) {
        var claim = this.Read(() => this._forkClaims.GetValueOrDefault(channelId));
        if (claim != null) {
            await this.CheckForkAsync(channelId, claim.Candidate, ct, connection);
        }
    }

    /// <summary>
    /// True if <paramref name="candidate"/> proves on its own that the log forked: it is at a position this client
    /// verified, chained to the same entry as the one verified there, but differs from it, and it is validly signed
    /// by keys the verified log knows. Call inside the lock.
    /// </summary>
    private bool ProvesFork(string channelId, MembershipEntry candidate) {
        var ours = this.MembershipOf(channelId);
        if (ours.Head == null || candidate.Seq == 0 || candidate.Seq > ours.Head.Seq
            || ours.HashAt(candidate.Seq) is not { } mine || ours.HashAt(candidate.Seq - 1) is not { } parent) {
            return false;
        }

        return !mine.AsSpan().SequenceEqual(MembershipEntries.Hash(candidate))
               && candidate.PreviousHash.Span.SequenceEqual(parent)
               && ours.IsSignedByKnownKeys(candidate);
    }

    private void ReportFork(string channelId, ulong forkAt) {
        this.Log(NoticeLevel.Warning, $"Membership log of {channelId} forked at or before entry {forkAt}");
        this.WarnAboutMembership(channelId, PlainMessages.MembershipForked(forkAt));
    }

    /// <summary>
    /// Fetches the server's whole log and replays it: if it is valid, and it (or <paramref name="candidate"/>) differs
    /// from what was verified here, two validly signed versions of the log exist, and the user is told.
    /// </summary>
    private async Task CheckForkWithLogAsync(string channelId, MembershipEntry? candidate, CancellationToken ct, Connection? connection) {
        var chain = this._membership.Empty(channelId);
        var hashes = new List<byte[]>();
        IChannelMembership? beforeCandidate = candidate is { Seq: 0 } ? chain : null;
        var valid = true;
        var complete = false;
        for (var pages = 0; valid && pages < MaxLogPagesPerSync; pages++) {
            var response = await this.RequestAsync(connection ?? this.RequireConnection(), new ClientFrame {
                FetchMembershipLog = new FetchMembershipLog { ChannelId = channelId, FromSeq = (ulong) hashes.Count },
            }, ct);
            var log = response.MembershipLog ?? throw Unexpected(response);
            if (log.Entries.Count == 0) {
                complete = true;
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
        var shortOrInvalid = false;
        lock (this._lock) {
            var ours = this.MembershipOf(channelId);
            // A log that doesn't check out, or stops before what this client verified, can't be the one it verified.
            shortOrInvalid = !valid || (complete && ours.Head != null && (ulong) hashes.Count <= ours.Head.Seq);
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
            this.ReportFork(channelId, forkAt.Value);
        } else if (shortOrInvalid) {
            this.Log(NoticeLevel.Warning, $"Membership log of {channelId} from the server is invalid or shorter than the one verified");
            this.WarnAboutMembership(channelId, PlainMessages.MembershipHidden);
        }
    }

    /// <summary>
    /// Asks the server for the newest entry this client verified and everything after it. Whatever
    /// head the server claims, an honest one can show that entry (it stored it), and the entries up
    /// to its head, which must check out. Used before blaming the server for something that an
    /// honest one does too, now and then (a key made just before a change, say).
    /// </summary>
    /// <returns>False if the server doesn't have that entry, or claims more than it shows.</returns>
    private async Task<bool> ConfirmHeadAsync(string channelId, CancellationToken ct, Connection? connection = null) {
        var head = this.Read(() => this.MembershipOf(channelId).Head);
        if (head == null) {
            return true;
        }

        MembershipLog log;
        try {
            var response = await this.RequestAsync(connection ?? this.RequireConnection(), new ClientFrame {
                FetchMembershipLog = new FetchMembershipLog { ChannelId = channelId, FromSeq = head.Seq },
            }, ct);
            log = response.MembershipLog ?? throw Unexpected(response);
        } catch (ServerErrorException ex) when (ex.Code == ErrorCode.NotFound) {
            return false;
        }

        if (log.Head == null || log.Entries.Count == 0 || !MembershipEntries.SamePosition(MembershipEntries.PositionOf(log.Entries[0]), head)) {
            return false;
        }

        var after = await this.SyncLogAsync(channelId, ct, log.Entries.Skip(1).ToList(), log.Head, connection: connection);
        // (If a join or leave since moved its hash out of memory, it was still checked on the way.)
        return after.Head != null && after.Head.Seq >= log.Head.Seq
                                  && (after.HashAt(log.Head.Seq) is not { } hash || hash.AsSpan().SequenceEqual(log.Head.Hash.Span));
    }

    /// <summary>Tells the user (once per problem) that a channel's membership can't be trusted as shown, and shows it on the channel.</summary>
    /// <param name="format">The problem, in both modes' words, with {0} for the channel's name.</param>
    private void WarnAboutMembership(string channelId, Wording format) {
        Wording text;
        lock (this._lock) {
            var channel = this._channels.GetValueOrDefault(channelId);
            text = format.Format(channel?.DisplayName ?? this._invites.GetValueOrDefault(channelId)?.Name ?? ChannelView.PlaceholderName(channelId))
                .Map(sentence => char.ToUpperInvariant(sentence[0]) + sentence[1..] + ".");
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
                // Said as the log is verified (see PinRecoveredKeys), whether the entry came as an event or not.
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
            } else if (MemberKeys.FromProto(entry.Subject) != invited.Keys) {
                // Made for keys this user had before re-verifying their character, and moved to these by the log (a key
                // recovered entry): the invite is the log's, but the name in it was sealed to the old keys. It shows once
                // they have joined and hold the channel's key.
                invite.InviterKeys = invited.InviterKeys;
                invite.Name = null;
                invite.Verified = true;
            } else {
                invite.InviterKeys = invited.InviterKeys;
                invite.Name = this._groupKeys.OpenInvite(invite.Info, invited.InviterKeys.SigningPublicKey, this._identity, this._me.UserId);
                invite.Verified = invite.Name != null;
            }

            return this.ToView(invite);
        }
    }

    // ================================================================ events

    private void OnEvent(Connection? source, Event ev) {
        if (ev.KindCase == Event.KindOneofCase.PresenceChanged) {
            // Applied as it arrives, in order with the online flags in responses (which don't go through the
            // inbox); the inbox publishes it in turn with the other events.
            lock (this._lock) {
                if (source != null && source == this._connection) {
                    this._presence[ev.PresenceChanged.UserId] = ev.PresenceChanged.Online;
                }
            }

            this._inbox.Writer.TryWrite(ev);
            return;
        }

        if (ev.KindCase != Event.KindOneofCase.Announcement && !IsValidChannelId(ChannelIdOf(ev))) {
            this.Log(NoticeLevel.Debug, $"Ignored {ev.KindCase} with an invalid channel ID");
            return;
        }

        // Everything in order: an entry must be checked before a key made for it, and a key
        // before messages encrypted under it.
        this._inbox.Writer.TryWrite(ev);
    }

    /// <summary>
    /// Takes the online flags from channel info in a response. The server fills them in as it queues the
    /// response, in order with its PresenceChanged events, so applying both as they arrive keeps the newest.
    /// </summary>
    private void OnResponse(Connection? source, Response response) {
        IEnumerable<ChannelInfo> channels = response.ResultCase switch {
            Response.ResultOneofCase.Channel => [response.Channel],
            Response.ResultOneofCase.ChannelList => response.ChannelList.Channels,
            _ => [],
        };

        lock (this._lock) {
            if (source == null || source != this._connection) {
                return;
            }

            // Every channel listed is one this user is in, so its members are theirs to see. Invitees' flags
            // are always false (their presence isn't shared), so they would only hide what another channel says.
            foreach (var member in channels.SelectMany(channel => channel.Members)) {
                if (member.User != null && member.Rank >= Rank.Member) {
                    this._presence[member.User.UserId] = member.Online;
                }
            }
        }
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
            case Event.KindOneofCase.PresenceChanged:
                // Already applied as it arrived (see OnEvent).
                this.Publish();
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
                    !view.Verified ? PlainMessages.InviteUnverified(who)
                    : view.InviterKeyChanged ? PlainMessages.InviteFromChangedKey(who, Quoted(view.ChannelName))
                    : Wording.Same($"{who} invited you to {Quoted(view.ChannelName)}."),
                    invite.ChannelId);
            }
        });
    }

    /// <summary>An invite's channel name, quoted, or what to say for one not known (an invite moved to new keys).</summary>
    private static string Quoted(string? channelName) => channelName == null ? "a channel" : $"\"{channelName}\"";

    private void OnRekeyNeeded(RekeyNeeded rekey) {
        bool designated;
        lock (this._lock) {
            if (!this._channels.TryGetValue(rekey.ChannelId, out var channel)
                || rekey.CurrentEpoch < Math.Max(channel.ServerEpoch, this.KeyEpochOf(channel.Id) ?? 0)) {
                // Unknown channel, or a request that a newer epoch has already answered.
                return;
            }

            channel.ServerEpoch = Math.Max(channel.ServerEpoch, rekey.CurrentEpoch);
            channel.MarkRekeyPending(rekey.CurrentEpoch);
            channel.NoKeyHolder = rekey.NoKeyHolder;
            designated = rekey.DesignatedUserId == this._me?.UserId && this.IsMember(rekey.ChannelId);
        }

        this.Publish();
        if (designated && this._options.AutoRekeyWhenDesignated) {
            this.RunBackground(PlainMessages.Rekeying, ct => this.RekeyAsync(rekey.ChannelId, ct));
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
        var stale = false;
        string? equivocation = null;
        BadKey? bad = null;
        MemberKeys? failedSigner = null;
        var channelName = "";
        lock (this._lock) {
            if (this._identity == null || this._me == null || !this._channels.TryGetValue(advanced.ChannelId, out var channel)
                || !this.IsMember(advanced.ChannelId)) {
                return;
            }

            channelName = channel.DisplayName;
            var membership = this.MembershipOf(advanced.ChannelId);
            var author = membership.FindMember(advanced.AuthorId);
            var authorKeys = SignerKeys(membership, advanced.AuthorId, position);
            byte[]? key = null;
            var check = advanced.MyKey == null || authorKeys == null
                ? EpochKeyCheck.BadSignature
                : this._groupKeys.OpenEpochKey(advanced.MyKey, advanced.ChannelId, advanced.Epoch, advanced.AuthorId, authorKeys.SigningPublicKey, this._identity, this._me.UserId, out key);
            var alreadyHeld = key != null && this.GetEpochKey(advanced.ChannelId, advanced.Epoch) is { } existing && existing.AsSpan().SequenceEqual(key);

            // The same key may already have been fetched while this event waited in the queue: not a replay.
            if (!alreadyHeld) {
                rejected = this.CheckEpochKeyAuthor(channel, advanced.Epoch, author);
                if (rejected == null && check == EpochKeyCheck.BadSignature) {
                    rejected = "it failed signature or decryption checks";
                    failedSigner = authorKeys;
                } else if (rejected == null && position != null && membership.Head is { } head && position.Seq > head.Seq) {
                    rejected = $"it was made at membership log entry #{position.Seq}, which the server hasn't shown you";
                } else if (rejected == null && !membership.IsCurrent(position)) {
                    // Validly signed by a member, but sealed to the members of another time.
                    rejected = $"it was made for the channel's membership at entry #{position?.Seq}, not the current one (entry #{membership.Head?.Seq}). The server may be hiding a change from someone";
                    stale = true;
                    equivocation = this.EquivocationShownBy(advanced.ChannelId, advanced.Epoch, position);
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
                channel.SettleRekey(advanced.Epoch);
                // Someone held (or made) a key: the server says again if nobody does next time.
                channel.NoKeyHolder = false;
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

        if (equivocation != null) {
            // Whatever log the server shows this client, it showed another to whoever made the key.
            this.WarnAboutMembership(advanced.ChannelId, PlainMessages.MembersShownDifferently(equivocation));
        } else if (stale && await this.ConfirmHeadAsync(advanced.ChannelId, ct)) {
            // An honest server sends such a key now and then: made just before a change this client already
            // applied (its own, say), or while it was away. Only a server that can't show what was verified is blamed.
            this.Log(NoticeLevel.Debug, $"Ignored epoch {advanced.Epoch} key for {advanced.ChannelId}: made before the latest membership change");
            return;
        }

        if (rejected != null) {
            this.RaiseNotice(NoticeLevel.Warning, PlainMessages.ChannelKeyRejected(rejected, channelName), advanced.ChannelId);
            return;
        }

        this.SaveSecrets();
        this.Publish();
    }

    private sealed record BadKey(string ChannelId, Wording Message, bool Rekey);

    /// <summary>
    /// Whether a key for <paramref name="epoch"/> made at <paramref name="position"/>, which isn't this client's current
    /// membership, shows that the server is showing members different versions of the log. An honest server takes a
    /// rekey only at its log's head and only for the next epoch, and its head never goes back. So no key it takes is
    /// made for a position that isn't in the log, nor for an earlier position than a key with an older epoch, such as
    /// one made after a change this client verified (its own removal of someone, say). The server can still show this
    /// client the log it verified: it showed another to whoever made the key. Call inside the lock.
    /// </summary>
    /// <returns>
    /// What gives the server away, or null if nothing does: a key for an older membership with no such newer key held is
    /// what an honest server sends now and then (made just before a change this client already applied).
    /// </returns>
    private string? EquivocationShownBy(string channelId, ulong epoch, LogPosition? position) {
        if (position == null || this.MembershipOf(channelId) is not { Head: { } head } membership || position.Seq > head.Seq) {
            return null;
        }

        if (membership.HashAt(position.Seq) is { } verified && !verified.AsSpan().SequenceEqual(position.Hash.Span)) {
            return $"(epoch {epoch}) made for a version of membership log entry #{position.Seq} other than the one you verified";
        }

        if (!this._secrets.EpochKeyPositions.TryGetValue(channelId, out var held)) {
            return null;
        }

        foreach (var (heldEpoch, heldPosition) in held.Where(pair => pair.Key < epoch).OrderByDescending(pair => pair.Key)) {
            if (heldPosition.Seq > position.Seq || (heldPosition.Seq == position.Seq && !heldPosition.Hash.AsSpan().SequenceEqual(position.Hash.Span))) {
                return $"for epoch {epoch}, made for an older membership (entry #{position.Seq}) than your key for epoch {heldEpoch} (entry #{heldPosition.Seq})";
            }
        }

        return null;
    }

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
        channel.MarkRekeyPending(epoch);
        // One automatic rekey per epoch, so two clients can't keep rekeying each other.
        var rekey = channel.BadKeyRekeyEpoch != epoch;
        channel.BadKeyRekeyEpoch = epoch;

        return new BadKey(channel.Id, PlainMessages.BadChannelKey($"{author.Name}@{author.WorldName}", channel.DisplayName, epoch, problem, rekey), rekey);
    }

    private void HandleBadEpochKey(BadKey bad) {
        this.Publish();
        this.RaiseNotice(NoticeLevel.Warning, bad.Message, bad.ChannelId);
        if (bad.Rekey) {
            this.RunBackground(PlainMessages.Rekeying, ct => this.RekeyAsync(bad.ChannelId, ct));
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
            this.RaiseNotice(NoticeLevel.Warning, PlainMessages.MessageFromNonMember(channelName ?? "", who), message.ChannelId);
            return;
        }

        var key = this.Read(() => this.GetEpochKey(message.ChannelId, message.Epoch));
        if (key == null) {
            await this.FetchEpochKeysAsync(message.ChannelId, ct);
            key = this.Read(() => this.GetEpochKey(message.ChannelId, message.Epoch));
        }

        if (key == null) {
            this.RaiseNotice(NoticeLevel.Warning, PlainMessages.MessageWithoutKey(senderUser.Name), message.ChannelId);
            return;
        }

        var now = this._options.TimeProvider.GetUtcNow();
        if (this.Read(() => this.IsPastOldEpochGrace(message.ChannelId, message.Epoch, now))) {
            this.RaiseNotice(NoticeLevel.Warning, PlainMessages.MessageTooLate(senderUser.Name), message.ChannelId);
            return;
        }

        // Signed by the keys the log admitted them with, not whatever the server says their keys are now.
        var content = this._groupKeys.DecryptMessage(message, key, sender.Keys.SigningPublicKey);
        if (content == null && !this._groupKeys.VerifyMessage(message, sender.Keys.SigningPublicKey)) {
            // Awaited here, in the inbox, so later messages still wait their turn.
            if (await this.RefetchIdentityAsync(message.SenderId, sender.Keys, ct) is { } current
                && this._groupKeys.VerifyMessage(message, current.Identity.SigningPublicKey.Span)) {
                this.RaiseNotice(NoticeLevel.Warning, PlainMessages.MessageFromNewSetup(senderUser.Name, channelName), message.ChannelId);
                return;
            }

            this.RaiseNotice(NoticeLevel.Warning, PlainMessages.MessageFailedChecks(senderUser.Name), message.ChannelId);
            return;
        }

        if (content == null) {
            this.RaiseNotice(NoticeLevel.Warning, PlainMessages.MessageFailedChecks(senderUser.Name), message.ChannelId);
            return;
        }

        // The timestamp is signed, so an old message can't be replayed as new
        // once it falls outside this window (the seen-set covers the window itself).
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(message.TimestampUnixMs);
        if ((now - timestamp).Duration() > MaxMessageClockSkew) {
            this.RaiseNotice(NoticeLevel.Warning, PlainMessages.MessageClockSkew(senderUser.Name, $"{timestamp.ToLocalTime():g}"), message.ChannelId);
            return;
        }

        // The seen-set is lost on restart; this persisted high-water mark isn't.
        var newest = this.Read(() => this._secrets.NewestMessageTimes.TryGetValue(message.ChannelId, out var senders)
                                     && senders.TryGetValue(message.SenderId, out var time) ? time : (long?) null);
        if (newest is { } newestMs && message.TimestampUnixMs < newestMs - (long) MessageReorderAllowance.TotalMilliseconds) {
            this.RaiseNotice(NoticeLevel.Warning, PlainMessages.MessageReplayed(senderUser.Name, $"{timestamp.ToLocalTime():g}"), message.ChannelId);
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
        // Links only as the checks in MessageContent leave them: the rest of each is its text, as an older client shows it.
        this.RaiseMessage(MessageContent.Decode(content) is { } text
            ? new IncomingMessage(message.ChannelId, channelName, senderUser, isOwn, text.Text, false, timestamp) { Links = text.Links }
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
        string? equivocation = null;
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

                var check = this._groupKeys.OpenEpochKey(entry.Key, channelId, entry.Epoch, entry.AuthorId, SignerKeys(membership, entry.AuthorId, entry.Key.LogPosition)!.SigningPublicKey,
                    this._identity, this._me.UserId, out var key);
                if (check == EpochKeyCheck.BadSignature) {
                    this.Log(NoticeLevel.Warning, $"Epoch {entry.Epoch} key for {channelId} failed verification ({check})");
                    continue;
                }

                if (!membership.IsCurrent(entry.Key.LogPosition)) {
                    // Sealed to the members of another time. Only worth a warning if no newer key replaces it, or if
                    // it gives away a server showing members different logs (whatever it shows this client).
                    this.Log(NoticeLevel.Warning, $"Ignored epoch {entry.Epoch} key for {channelId}: made at log entry {entry.Key.LogPosition?.Seq}, not the current membership");
                    staleNewest = entry;
                    equivocation ??= this.EquivocationShownBy(channelId, entry.Epoch, entry.Key.LogPosition);
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
            // and it settled any membership change the server had flagged before it. (Its EpochAdvanced
            // can be missed, for example when it arrives while an invite is being accepted.)
            if (this.KeyEpochOf(channelId) is { } newest && newest > channel.ServerEpoch) {
                channel.ServerEpoch = newest;
                channel.NoKeyHolder = false;
            }

            if (this.KeyEpochOf(channelId) is { } newestHeld) {
                channel.SettleRekey(newestHeld);
            }

            this.TryDecryptName(channelId);

            // The server is ahead and has no newer key for us (never sent, or sealed so we
            // can't open it): rekey to the server's epoch + 1 rather than stay stuck.
            if (this.KeyEpochOf(channelId) is { } held && held < channel.ServerEpoch) {
                channel.MarkRekeyPending(channel.ServerEpoch);
            }
        }

        this.SaveSecrets();
        this.Publish();
        if (equivocation != null) {
            this.WarnAboutMembership(channelId, PlainMessages.MembersShownDifferently(equivocation));
        } else if (staleNewest != null && !await this.ConfirmHeadAsync(channelId, ct, connection)) {
            // Normal while a join or leave awaits its rekey; only a server that can't show what was verified is blamed.
            this.WarnAboutMembership(channelId, PlainMessages.StaleKeyOffered(staleNewest.Key.LogPosition?.Seq));
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

    /// <summary>
    /// Fetches again the identities of names' authors that <see cref="TryDecryptName"/> couldn't verify, and of users a log moved
    /// to new keys (see <see cref="PinRecoveredKeys"/>), and logs that names were made for.
    /// </summary>
    private void FollowUpNames() {
        List<KeyValuePair<long, MemberKeys>> authors;
        List<string> logs;
        List<long> recovered;
        lock (this._lock) {
            if (this._connection == null || (this._staleNameAuthors.Count == 0 && this._namePositionsAhead.Count == 0 && this._identitiesToRefresh.Count == 0)) {
                return;
            }

            authors = [.. this._staleNameAuthors];
            this._staleNameAuthors.Clear();
            logs = [.. this._namePositionsAhead.Keys];
            this._namePositionsAhead.Clear();
            recovered = [.. this._identitiesToRefresh];
            this._identitiesToRefresh.Clear();
        }

        if (recovered.Count > 0) {
            this.RunBackground(PlainMessages.FetchingIdentities, async ct => {
                await this.EnsureIdentitiesAsync(recovered, ct, refresh: true);
                this.Publish();
            });
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
        var warnings = new List<Wording>();

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
    private Wording? Pin(long userId, MemberKeys keys, User? user, uint? keyVersion = null) {
        Wording? warning = null;
        var changed = false;
        var who = user != null ? $"{user.Name}@{user.WorldName}" : null;

        if (this._secrets.PinnedIdentities.TryGetValue(userId, out var pinned)) {
            who ??= pinned.Name.Length > 0 ? $"{pinned.Name}@{pinned.WorldName}" : $"user {userId}";
            if (!pinned.SigningPublicKey.AsSpan().SequenceEqual(keys.SigningPublicKey) || !pinned.AgreementPublicKey.AsSpan().SequenceEqual(keys.AgreementPublicKey)) {
                pinned.SigningPublicKey = keys.SigningKeyArray();
                pinned.AgreementPublicKey = keys.AgreementKeyArray();
                pinned.KeyChangeUnacknowledged = true;
                pinned.KeyRecovered = false;
                pinned.Compared = false;
                changed = true;
                warning = PlainMessages.KeyChanged(who, keys.Fingerprint);
            }

            if (user != null && (pinned.Name != user.Name || pinned.WorldName != user.WorldName)) {
                if (pinned.Name.Length > 0 && warning == null) {
                    warning = PlainMessages.Renamed($"{pinned.Name}@{pinned.WorldName}", who);
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
                warning = PlainMessages.NameNowAnotherAccount(who!, keys.Fingerprint);
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
        return new InviteView(invite.Info.ChannelId, Shown(invite.Info.Inviter), invite.Name, invite.Verified,
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
        channel.SetRekeyPendingFromServer(info.RekeyPending, info.Epoch);
        // Answered before a key this client already holds was made (its own rekey, say): that key settled it.
        if (this.KeyEpochOf(info.ChannelId) is { } held) {
            channel.SettleRekey(held);
        }

        channel.NoKeyHolder = info.RekeyPending && info.NoKeyHolder;
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
        // Entries that follow a membership already verified here happened since this client last looked; a log replayed
        // from its start is history.
        var followsOn = this.MembershipOf(channelId).Head != null;
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
                this._pendingNotices.Add(SessionNotice.Of(NoticeLevel.Warning, warning, channelId));
            }
        }

        foreach (var entry in applied.Where(entry => entry.Kind == MembershipEntryKind.KeyRecovered)) {
            if (entry.Subject.UserId != me) {
                this.PinRecoveredKeys(channelId, membership, entry, followsOn);
            } else if (this._myKeys != null && MemberKeys.FromProto(entry.Subject) == this._myKeys && MemberKeys.FromProto(entry.NewKeys) != this._myKeys) {
                // This user's own place, moved away from the keys this client holds: their character was re-verified with
                // others (normally their own, on another computer; if not, the server or someone else did it). Said once.
                if (!this._toldKeyMovedAway) {
                    this._toldKeyMovedAway = true;
                    this._pendingNotices.Add(SessionNotice.Of(NoticeLevel.Warning, PlainMessages.ReVerifiedElsewhereWording, channelId));
                }
            }
        }

        if (this._channels.TryGetValue(channelId, out var channel)) {
            this.TryDecryptName(channel.Id);
        }
    }

    /// <summary>
    /// Someone re-verified their character with new keys, and the log moved their place to them (a key recovered entry): pins
    /// the new keys as expected, not as an unexplained change ("key changed"), but as not compared yet, which stays shown
    /// until the user compares them. That the change is theirs is the server's word (it checked the Lodestone), so it is
    /// said in the channel, in plain words, whenever it happened since this client last looked, and whenever it changes
    /// what this client held for them (a key pinned before, compared or not, or a "key changed" warning it now explains),
    /// even in a log read from its start. Call inside the lock.
    /// </summary>
    /// <param name="followsOn">The entry follows a membership verified here before, rather than a log replayed from its start.</param>
    private void PinRecoveredKeys(string channelId, IChannelMembership membership, MembershipEntry entry, bool followsOn) {
        var userId = entry.Subject.UserId;
        var keys = MemberKeys.FromProto(entry.NewKeys)!;
        if ((membership.FindMember(userId)?.Keys ?? membership.FindInvitee(userId)?.Keys) != keys) {
            // Moved again (or gone) since, in the same batch: those keys aren't theirs any more.
            return;
        }

        // News whenever it follows what this client verified, or changes what it held for them; a log replayed from its start
        // to someone who never saw them is history.
        var tell = followsOn;
        var wasCompared = false;
        if (this._secrets.PinnedIdentities.TryGetValue(userId, out var pinned)) {
            var sameKeys = pinned.SigningPublicKey.AsSpan().SequenceEqual(keys.SigningPublicKey) && pinned.AgreementPublicKey.AsSpan().SequenceEqual(keys.AgreementPublicKey);
            if (!sameKeys) {
                wasCompared = pinned.Compared;
                pinned.SigningPublicKey = keys.SigningKeyArray();
                pinned.AgreementPublicKey = keys.AgreementKeyArray();
                pinned.Compared = false;
                pinned.KeyChangeUnacknowledged = false;
                pinned.KeyRecovered = true;
                this._secretsVersion++;
                tell = true;
            } else if (pinned.KeyChangeUnacknowledged) {
                // Seen first from the server's identities, as an unexplained change: the log explains it now.
                pinned.KeyChangeUnacknowledged = false;
                pinned.KeyRecovered = true;
                this._secretsVersion++;
                tell = true;
            }
        } else {
            // Trusted on first use, as anyone first seen in a log.
            this.Pin(userId, keys, this.KnownUser(userId));
        }

        // The identity held for them is the old one: fetched again, so they don't show as "registered again" meanwhile.
        if (this._identities.TryGetValue(userId, out var identity) && MemberKeys.Of(identity.Identity) != keys) {
            this._identities.Remove(userId);
            this._users[userId] = identity.User;
            this._identitiesToRefresh.Add(userId);
        }

        if (tell) {
            var who = this.UserOf(userId);
            this._pendingNotices.Add(SessionNotice.Of(NoticeLevel.Info,
                PlainMessages.ReVerifiedWording($"{who.Name}@{who.WorldName}", wasCompared), channelId));
        }
    }

    /// <summary>
    /// True if the log has this user as a member, or invited, under identity keys other than their current ones: a place
    /// only those (gone) keys could sign anything for. Call inside the lock.
    /// </summary>
    private bool HoldsOldKeyPlace(IChannelMembership membership) {
        if (this._me == null || this._myKeys == null) {
            return false;
        }

        var keys = membership.FindMember(this._me.UserId)?.Keys ?? membership.FindInvitee(this._me.UserId)?.Keys;
        return keys != null && keys != this._myKeys;
    }

    /// <summary>True if this user is a member of the channel under their current identity keys.</summary>
    private bool IsMember(string channelId) {
        return this._me != null && this._myKeys != null && this.MembershipOf(channelId).FindMember(this._me.UserId)?.Keys == this._myKeys;
    }

    /// <summary>True if this user is the channel's only member, under their current identity keys. Call inside the lock.</summary>
    private bool IsAloneIn(string channelId) => this.IsMember(channelId) && this.MembershipOf(channelId).Members.Count == 1;

    /// <summary>
    /// Without the channel's name, this client makes the channel's next key itself, under a name of its own (see
    /// <see cref="PlainMessages.RestoredChannelName"/>): it is the channel's only member, or the server says nobody who could
    /// make the key holds it (see <see cref="ChannelState.NoKeyHolder"/>), so nobody can ever share the key or tell the name.
    /// Call inside the lock.
    /// </summary>
    private bool NamesNewKeyItself(string channelId) => this.IsAloneIn(channelId) || this._channels.GetValueOrDefault(channelId)?.NoKeyHolder == true;

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
        if (key == null || offered.LogPosition is not { } position || membership.FindMember(offered.AuthorId) == null) {
            return;
        }

        // Signed with the keys its author had where it was made: a member who re-verified since signed it with their old ones.
        var authorKeys = membership.KeysAt(offered.AuthorId, position.Seq)!;

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
        // And one made with the key it is encrypted under (by that rekey) still goes with that key while a
        // later join or leave awaits its rekey: a client restarted meanwhile needs it to make that rekey.
        var madeWithItsKey = MembershipEntries.SamePosition(position, this.KeyPositionOf(channelId, offered.Epoch));
        if (!membership.IsCurrent(position) && !tooOldToCheck && !madeWithItsKey) {
            return;
        }

        var name = this._groupKeys.DecryptName(offered, channelId, key, authorKeys.SigningPublicKey);
        if (name == null) {
            if (!this._groupKeys.VerifyName(offered, channelId, authorKeys.SigningPublicKey)) {
                // Perhaps signed with keys they registered since; they're told if so.
                this._staleNameAuthors[offered.AuthorId] = authorKeys;
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
            this._pendingNotices.Add(SessionNotice.Of(NoticeLevel.Warning,
                PlainMessages.NameChangedWhileRekeying($"{who.Name}@{who.WorldName}", previous, name), channelId));
        }

        channel.Name = name;
        this.SetNameVersion(channelId, version);
    }

    /// <summary>
    /// The keys a member signed something made at <paramref name="position"/> with: the ones they had there (a member who
    /// re-verified their character since signed it with their old keys; see <see cref="IChannelMembership.KeysAt"/>), or their
    /// keys now if it names no position. Null if they aren't a member now.
    /// </summary>
    private static MemberKeys? SignerKeys(IChannelMembership membership, long userId, LogPosition? position) {
        if (membership.FindMember(userId) is not { } member) {
            return null;
        }

        return position == null ? member.Keys : membership.KeysAt(userId, position.Seq);
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
            if (channel.RemovalWarning != null && this.RemovalAwaitingRekey(channelId) == null) {
                // A key made after the removal: it has taken effect.
                channel.RemovalWarning = null;
            }
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

    private void SetState(ConnectionState state, string? status) => this.SetState(state, status == null ? null : Wording.Same(status));

    private void SetState(ConnectionState state, Wording? status) {
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
            // Status texts and notices often hold what the server said: no registration code but this client's own.
            var keep = this.ShownCode();
            notices = [.. this._pendingNotices.Select(notice => Redacted(notice, keep))];
            this._pendingNotices.Clear();
            // As the server said on this connection; nothing while there is none.
            var addressNotListed = this._state is ConnectionState.Stopped or ConnectionState.Connecting or ConnectionState.Reconnecting ? null : this._addressNotListed;
            snapshot = new SessionSnapshot(
                this._state,
                LodestoneCode.Redact(this._status?.Technical, keep),
                this._me == null ? null : Shown(this._me),
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
                        ? Shown(new User { UserId = id, Name = pinned.Name, WorldName = pinned.WorldName })
                        : new User { UserId = id, Name = $"user {id}" })
                    .OrderBy(user => user.Name, StringComparer.OrdinalIgnoreCase)
                    .ToImmutableArray(),
                this._channelsLoaded && this._state == ConnectionState.Ready,
                this._loginRejected && this._state is ConnectionState.LoginNotRecognized or ConnectionState.Registering,
                addressNotListed?.Technical,
                // Never registered here: what a registration would do is take an account over, not keep a key.
                this._secrets.UserId == null,
                LodestoneCode.Redact(this._status?.Plain, keep),
                addressNotListed?.Plain);
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
                var pinnedHere = pinned != null && pinned.SigningPublicKey.AsSpan().SequenceEqual(member.Keys.SigningPublicKey)
                                                && pinned.AgreementPublicKey.AsSpan().SequenceEqual(member.Keys.AgreementPublicKey);
                var compared = isMe || (pinnedHere && pinned!.Compared);
                var recovered = !isMe && pinnedHere && pinned!.KeyRecovered && !pinned.Compared;
                var current = this._identities.TryGetValue(member.UserId, out var identity) ? MemberKeys.Of(identity.Identity) : null;
                var replaced = current != null && current != member.Keys;
                // Invitees' presence isn't shared. You are online while logged in.
                var online = member.Rank >= Rank.Member && (member.UserId == this._me?.UserId
                    ? this._state == ConnectionState.Ready
                    : this._presence.GetValueOrDefault(member.UserId));
                return new MemberView(Shown(user), member.Rank, member.Keys.Fingerprint, pinned is { KeyChangeUnacknowledged: true }, compared, replaced,
                    replaced ? current!.Fingerprint : null, online, recovered);
            })
            .OrderByDescending(member => member.Rank)
            .ThenBy(member => member.User.Name, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        var mine = this._me == null ? null : membership.FindMember(this._me.UserId);
        var oldKey = mine != null && this._myKeys != null && mine.Keys != this._myKeys;
        // Not keys this user had before: the log moved their place (a key recovered entry) to keys this client doesn't hold, so
        // their character was re-verified elsewhere.
        var movedAway = oldKey && membership.KeysAt(mine!.UserId, 0) != mine.Keys;
        var warning = channel.MembershipWarning ?? channel.RemovalWarning;
        if (warning == null && oldKey) {
            warning = movedAway ? PlainMessages.KeyMovedAwayWording : PlainMessages.OldKeyChannelWording;
        }

        var keyEpoch = this.KeyEpochOf(channel.Id);
        return new ChannelView(channel.Id, channel.Name, keyEpoch ?? channel.ServerEpoch, channel.ServerEpoch,
            this.HasCurrentKey(channel.Id), this.NeedsRekey(channel), mine != null && mine.Keys == this._myKeys ? mine.Rank : Rank.Unspecified,
            members, membership.Head?.Clone(), warning?.Technical, oldKey, movedAway, ChannelView.PlainNames(warning?.Plain));
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
            throw new ServerErrorException(response.Error.Code, response.Error.Message, this.ShownCode());
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
    private void RunBackground(string what, Func<CancellationToken, Task> work) => this.RunBackground(Wording.Same(what), work);

    /// <inheritdoc cref="RunBackground(string, Func{CancellationToken, Task})"/>
    private void RunBackground(Wording what, Func<CancellationToken, Task> work) {
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
                this.RaiseNotice(NoticeLevel.Warning, PlainMessages.Failed(what, ex));
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

    /// <summary>
    /// The registration code this client derived and checked (see <see cref="CheckCode"/>), while a registration is under
    /// way: the only one it shows. Every other is removed from what the server says (see <see cref="LodestoneCode.Redact"/>).
    /// </summary>
    private string? ShownCode() {
        lock (this._lock) {
            return this._challenge?.Code is { Length: > 0 } code ? code : null;
        }
    }

    /// <summary>A user as the server named them, to show: a name is no place for a registration code either.</summary>
    private static User Shown(User user) {
        var name = LodestoneCode.Redact(user.Name);
        var world = LodestoneCode.Redact(user.WorldName);
        return ReferenceEquals(name, user.Name) && ReferenceEquals(world, user.WorldName)
            ? user
            : new User(user) { Name = name, WorldName = world };
    }

    private void RaiseMessage(IncomingMessage message) => this.InvokeSafely(this.MessageReceived, message with { Sender = Shown(message.Sender) });

    /// <summary>
    /// Tells the user something. Notices often contain names, channel names or
    /// server text, so they go only to <see cref="Notice"/>, never to the diagnostic log.
    /// </summary>
    private void RaiseNotice(NoticeLevel level, string text, string? channelId = null) {
        this.RaiseNotice(level, Wording.Same(text), channelId);
    }

    /// <inheritdoc cref="RaiseNotice(NoticeLevel, string, string?)"/>
    private void RaiseNotice(NoticeLevel level, Wording wording, string? channelId = null) {
        this.InvokeSafely(this.Notice, Redacted(SessionNotice.Of(level, wording, channelId), this.ShownCode()));
    }

    /// <summary>
    /// A notice with no registration code in either of its texts but <paramref name="keep"/> (see <see cref="LodestoneCode.Redact"/>),
    /// and channels whose names aren't known named in simple mode's words in its plain one.
    /// </summary>
    private static SessionNotice Redacted(SessionNotice notice, string? keep) =>
        notice with { Text = LodestoneCode.Redact(notice.Text, keep), Plain = LodestoneCode.Redact(ChannelView.PlainNames(notice.Plain), keep) };

    private void Log(NoticeLevel level, string text) {
        try {
            this._options.Log?.Invoke(level, LodestoneCode.Redact(text, this.ShownCode()));
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

        /// <summary>The channel needs a new key: see <see cref="MarkRekeyPending"/> and <see cref="SettleRekey"/>.</summary>
        public bool RekeyPending { get; private set; }

        /// <summary>
        /// The epoch the channel was at when the pending rekey was asked for (the newest such request, if several). The server
        /// only takes a rekey from its current epoch to the next, made at its log's head, so a key for a later epoch was made
        /// after the change that asked for it; one for this epoch or before was not, however late it arrives.
        /// </summary>
        public ulong RekeyPendingEpoch { get; private set; }

        /// <summary>The channel needs a key newer than <paramref name="epoch"/> (the epoch it was at when that was found).</summary>
        public void MarkRekeyPending(ulong epoch) {
            this.RekeyPendingEpoch = this.RekeyPending ? Math.Max(this.RekeyPendingEpoch, epoch) : epoch;
            this.RekeyPending = true;
        }

        /// <summary>A key for <paramref name="keyEpoch"/> is held now: it settles a pending rekey asked for at an earlier epoch only.</summary>
        public void SettleRekey(ulong keyEpoch) {
            if (this.RekeyPending && keyEpoch > this.RekeyPendingEpoch) {
                this.RekeyPending = false;
            }
        }

        /// <summary>What the server's channel info says, as of <paramref name="epoch"/>.</summary>
        public void SetRekeyPendingFromServer(bool pending, ulong epoch) {
            this.RekeyPending = pending;
            this.RekeyPendingEpoch = epoch;
        }

        /// <summary>
        /// The server last said nobody who could make the channel's next key holds its key (see <see cref="RekeyNeeded.NoKeyHolder"/>):
        /// a client that doesn't know the channel's name makes it under a name of its own. Only a hint, as the server says it.
        /// </summary>
        public bool NoKeyHolder { get; set; }

        /// <summary>The newest membership log position the server reported. A hint for when to fetch the log.</summary>
        public LogPosition? LogHead { get; set; }

        /// <summary>A fork or hidden change seen in the channel's membership, to keep showing.</summary>
        public Wording? MembershipWarning { get; set; }

        /// <summary>A removal (or leave) whose rekey the server didn't take: shown until a key made after it is held.</summary>
        public Wording? RemovalWarning { get; set; }

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
