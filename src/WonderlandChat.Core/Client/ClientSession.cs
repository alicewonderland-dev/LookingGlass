using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Google.Protobuf;
using WonderlandChat.Core.Crypto;
using WonderlandChat.Protocol;

namespace WonderlandChat.Core.Client;

/// <summary>
/// A client's connection to a WonderlandChat server for one character.
///
/// Threading: all mutable state lives behind <see cref="_lock"/> and is only
/// touched in short synchronous sections, never across an await. After every
/// change an immutable <see cref="SessionSnapshot"/> is published; UI code
/// reads only snapshots. Network I/O happens in <see cref="Connection"/>.
/// Chat messages and epoch changes are processed in order by one inbox task.
/// </summary>
public sealed class ClientSession : IAsyncDisposable {
    private const int KeptEpochsPerChannel = 4;
    private const int SeenMessageCapacity = 2048;
    private static readonly TimeSpan MaxMessageClockSkew = TimeSpan.FromMinutes(10);
    // How far a sender's messages may arrive out of order before they count as replays.
    private static readonly TimeSpan MessageReorderAllowance = TimeSpan.FromMinutes(2);
    // How long messages under an older epoch are accepted after a newer key arrives
    // (only those in flight during the rekey are legitimate; the server rejects new ones).
    private static readonly TimeSpan OldEpochGrace = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReplayStateSaveInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RegistrationRequestTimeout = TimeSpan.FromSeconds(60);

    private readonly ClientSessionOptions _options;
    private readonly ISecretStore _store;
    private readonly Lock _lock = new();
    private readonly Lock _saveLock = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _channelLocks = new();
    private readonly Channel<Event> _inbox = Channel.CreateUnbounded<Event>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentQueue<TraceEntry> _trace = new();
    private readonly ConcurrentDictionary<Task, byte> _background = new();

    // ---- state guarded by _lock
    private readonly ClientSecrets _secrets;
    private IdentityKeys? _identity;
    private ConnectionState _state = ConnectionState.Stopped;
    private string? _status;
    private User? _me;
    private Limits? _limits;
    private bool _debugAccountsEnabled;
    private RegistrationChallenge? _challenge;
    private readonly Dictionary<string, ChannelState> _channels = new();
    private readonly Dictionary<string, InviteState> _invites = new();
    private readonly Dictionary<long, UserIdentity> _identities = new();
    private readonly HashSet<string> _seenMessages = new();
    private readonly Queue<string> _seenOrder = new();
    private long _secretsVersion;
    // NewestMessageTimes changed but hasn't been saved; the next save includes it.
    private bool _replayStateDirty;
    private DateTimeOffset _replayStateSavedAt = DateTimeOffset.MinValue;
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
        this._secrets = store.Load();

        if (this._secrets.SigningPrivateKey != null && this._secrets.AgreementPrivateKey != null) {
            this._identity = IdentityKeys.Import(this._secrets.SigningPrivateKey, this._secrets.AgreementPrivateKey);
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

    /// <summary>Clears the "key changed" warning for a user after their new fingerprint has been checked.</summary>
    public void AcknowledgeKeyChange(long userId) {
        lock (this._lock) {
            if (this._secrets.PinnedIdentities.TryGetValue(userId, out var pinned) && pinned.KeyChangeUnacknowledged) {
                pinned.KeyChangeUnacknowledged = false;
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
                await this.RequestAsync(new ClientFrame { RespondToInvite = new RespondToInvite { ChannelId = channelId, Accept = false } }, ct);
            } catch (Exception ex) when (ex is ServerErrorException or SessionDisconnectedException) {
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
        var key = ChannelCrypto.NewEpochKey();

        var response = await this.RequestAsync(new ClientFrame {
            CreateChannel = new CreateChannel {
                ChannelId = channelId,
                CreatorKey = ChannelCrypto.SealEpochKey(key, channelId, 0, identity, me.UserId, me.UserId, identity.AgreementPublicKey),
                Name = ChannelCrypto.EncryptName(name, key, channelId, 0, identity, me.UserId),
            },
        }, ct);

        var info = response.Channel ?? throw Unexpected(response);
        if (info.ChannelId != channelId) {
            throw Unexpected(response);
        }

        lock (this._lock) {
            this.StoreEpochKey(channelId, 0, key);
            var channel = this.ApplyChannelInfo(info);
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
        var invitee = this.AcceptIdentities([found]).FirstOrDefault()
            ?? throw new InvalidOperationException($"{name}@{worldName} has an invalid identity key.");

        var (sealedName, signature) = ChannelCrypto.SealInvite(channelName, channelId, invitee.User.UserId, invitee.Identity.AgreementPublicKey.Span, identity, me.UserId);
        await this.RequestAsync(new ClientFrame {
            InviteMember = new InviteMember {
                ChannelId = channelId,
                UserId = invitee.User.UserId,
                SealedName = sealedName,
                Signature = ByteString.CopyFrom(signature),
            },
        }, ct);
    }

    public async Task RespondToInviteAsync(string channelId, bool accept, CancellationToken ct = default) {
        var response = await this.RequestAsync(new ClientFrame {
            RespondToInvite = new RespondToInvite { ChannelId = channelId, Accept = accept },
        }, ct);

        lock (this._lock) {
            this._invites.Remove(channelId, out var invite);
            if (accept && response.Channel != null && response.Channel.ChannelId == channelId) {
                var channel = this.ApplyChannelInfo(response.Channel);
                channel.Name ??= invite?.Name;
            }
        }

        this.Publish();

        if (accept) {
            await this.EnsureChannelReadyAsync(channelId, ct);
            if (!this.Read(() => this.HasCurrentKey(channelId))) {
                this.RaiseNotice(NoticeLevel.Info, "Joined. Waiting for a member to share the channel key.", channelId);
            }
        }
    }

    public async Task LeaveAsync(string channelId, CancellationToken ct = default) {
        await this.RequestAsync(new ClientFrame { LeaveChannel = new LeaveChannel { ChannelId = channelId } }, ct);
        this.RemoveChannel(channelId);
    }

    public async Task KickAsync(string channelId, long userId, CancellationToken ct = default) {
        var (epochBefore, wasMember) = this.Read(() => (
            this.NewestEpochOf(channelId),
            this._channels.GetValueOrDefault(channelId)?.Members.Any(member => member.User.UserId == userId && member.Rank >= Rank.Member) == true));
        await this.RequestAsync(new ClientFrame { KickMember = new KickMember { ChannelId = channelId, UserId = userId } }, ct);
        if (this.AfterKickRequestForTests is { } hook) {
            await hook();
        }

        lock (this._lock) {
            if (this._channels.TryGetValue(channelId, out var channel)) {
                channel.Members.RemoveAll(member => member.User.UserId == userId);
                // Cancelling an invite needs no rekey: the invitee never had a key. And the server
                // asks a member to rekey before it answers, so a background rekey may already be
                // done; asking again would rekey a second time for nothing.
                if (wasMember && this.NewestEpochOf(channelId) == epochBefore) {
                    channel.RekeyPending = true;
                }
            }
        }

        this.Publish();
        // The kicker rotates the key straight away so the kicked member is cut off.
        await this.RekeyAsync(channelId, ct);
    }

    public async Task SetRankAsync(string channelId, long userId, Rank rank, CancellationToken ct = default) {
        await this.RequestAsync(new ClientFrame { SetMemberRank = new SetMemberRank { ChannelId = channelId, UserId = userId, Rank = rank } }, ct);
    }

    public async Task DisbandAsync(string channelId, CancellationToken ct = default) {
        await this.RequestAsync(new ClientFrame { DisbandChannel = new DisbandChannel { ChannelId = channelId } }, ct);
        this.RemoveChannel(channelId);
    }

    public async Task RenameAsync(string channelId, string newName, CancellationToken ct = default) {
        newName = ValidateChannelName(newName);
        var (identity, me) = this.RequireIdentityAndUser();
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
                && this._identities.TryGetValue(offered.AuthorId, out var author)
                && ChannelCrypto.VerifyName(offered, channelId, author.Identity.SigningPublicKey.Span)) {
                current = Math.Max(current, offered.Revision);
            }

            return (keyEpoch, this.GetEpochKey(channelId, keyEpoch)!, current + 1);
        });

        var name = ChannelCrypto.EncryptName(newName, key, channelId, epoch, identity, me.UserId, revision);
        await this.RequestAsync(new ClientFrame { RenameChannel = new RenameChannel { ChannelId = channelId, Name = name } }, ct);

        lock (this._lock) {
            if (this._channels.TryGetValue(channelId, out var channel)) {
                channel.EncryptedName = name;
                channel.Name = newName;
                this.SetNameVersion(channelId, new NameVersion(epoch, revision));
            }
        }

        this.SaveSecrets();
        this.Publish();
    }

    /// <summary>Re-fetches channels, invites, identities and keys from the server.</summary>
    public Task RefreshAsync(CancellationToken ct = default) => this.RefreshAsync(this.RequireConnection(), ct);

    // ================================================================ rekeying

    /// <summary>
    /// Moves the channel to a new epoch: a fresh key sealed to every current
    /// member. Safe to call concurrently; the server accepts one rekey per epoch.
    /// </summary>
    /// <param name="force">Rekey even if no membership change is pending (debug tool).</param>
    public async Task RekeyAsync(string channelId, CancellationToken ct = default, bool force = false) {
        var gate = this._channelLocks.GetOrAdd(channelId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try {
            for (var attempt = 0; attempt < 3; attempt++) {
                var (identity, me) = this.RequireIdentityAndUser();
                var (serverEpoch, name, memberIds, pending) = this.Read(() => {
                    var channel = this._channels.GetValueOrDefault(channelId) ?? throw new InvalidOperationException("Unknown channel.");
                    var ids = channel.Members.Where(member => member.Rank >= Rank.Member).Select(member => member.User.UserId).ToList();
                    return (channel.ServerEpoch, channel.Name, ids, channel.RekeyPending);
                });

                // Someone else (or an earlier call) may have rekeyed while we waited for the gate.
                if (!pending && !force) {
                    return;
                }

                if (name == null) {
                    throw new InvalidOperationException("You don't have this channel's key yet, so you can't rekey it. Another member needs to.");
                }

                // Always seal to fresh identities: a member may have re-registered with new keys.
                var identities = await this.EnsureIdentitiesAsync(memberIds, ct, refresh: true);
                // The server only accepts its current epoch + 1. If its hint is wrong, it answers EPOCH_STALE.
                var newEpoch = serverEpoch + 1;
                var key = ChannelCrypto.NewEpochKey();
                var request = new SubmitRekey {
                    ChannelId = channelId,
                    NewEpoch = newEpoch,
                    Name = ChannelCrypto.EncryptName(name, key, channelId, newEpoch, identity, me.UserId),
                    KeyCommitment = ByteString.CopyFrom(ChannelCrypto.KeyCommitment(channelId, newEpoch, key)),
                };

                foreach (var memberId in memberIds) {
                    if (!identities.TryGetValue(memberId, out var memberIdentity)) {
                        throw new InvalidOperationException($"No identity key for member {memberId}.");
                    }

                    request.Keys.Add(ChannelCrypto.SealEpochKey(key, channelId, newEpoch, identity, me.UserId, memberId, memberIdentity.Identity.AgreementPublicKey.Span));
                }

                try {
                    await this.RequestAsync(new ClientFrame { SubmitRekey = request }, ct);
                } catch (ServerErrorException ex) when (ex.Code is ErrorCode.Conflict or ErrorCode.EpochStale) {
                    // Someone else rekeyed first, or membership changed meanwhile.
                    await this.RefreshAsync(ct);
                    force = false;
                    continue;
                }

                lock (this._lock) {
                    this.StoreEpochKey(channelId, newEpoch, key);
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
                this.Log(NoticeLevel.Debug, $"Rekeyed {channelId} to epoch {newEpoch} for {memberIds.Count} members");
                return;
            }

            throw new InvalidOperationException("Rekeying kept conflicting with other changes; try again.");
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
            var (pending, rank) = this.Read(() => {
                var channel = this._channels.GetValueOrDefault(channelId) ?? throw new InvalidOperationException("You're not in that channel.");
                return (channel.RekeyPending, channel.MyRank);
            });

            if (rank < Rank.Member) {
                throw new InvalidOperationException("You haven't joined that channel.");
            }

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

                if (this.Read(() => this._channels.GetValueOrDefault(channelId)?.RekeyPending == true)) {
                    continue;
                }
            }

            // Always the newest key accepted, never an epoch the server merely claims.
            var (epoch, key) = this.Read(() => {
                var held = this.KeyEpochOf(channelId) ?? throw new InvalidOperationException("You don't have this channel's key yet.");
                return (held, this.GetEpochKey(channelId, held)!);
            });

            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var message = ChannelCrypto.EncryptMessage(content, key, channelId, epoch, identity, me.UserId, timestamp);
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
                await this.RefreshAsync(ct);
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
            this._secrets.UserId = ok.User.UserId;
            this._state = ConnectionState.Ready;
            this._status = $"Connected as {ok.User.Name}@{ok.User.WorldName}";
        }

        this.Publish();
        await this.RefreshAsync(connection, ct);
    }

    private async Task RefreshAsync(Connection connection, CancellationToken ct) {
        var response = await this.RequestAsync(connection, new ClientFrame { ListChannels = new ListChannels() }, ct);
        var list = response.ChannelList ?? throw Unexpected(response);
        // Channel IDs are bound into signatures and shown in the UI, so only canonical ones are accepted.
        var channels = list.Channels.Where(channel => IsValidChannelId(channel.ChannelId)).ToList();
        var invites = list.Invites.Where(invite => IsValidChannelId(invite.ChannelId) && invite.Inviter != null).ToList();
        var blocked = this.Read(() => invites.Where(invite => this.IsBlocked(invite.Inviter.UserId)).Select(invite => invite.ChannelId).ToList());
        invites.RemoveAll(invite => blocked.Contains(invite.ChannelId));
        foreach (var channelId in blocked) {
            this.DeclineQuietly(channelId);
        }

        var userIds = new HashSet<long>();
        lock (this._lock) {
            var listed = channels.Select(channel => channel.ChannelId).ToHashSet();
            foreach (var stale in this._channels.Keys.Where(id => !listed.Contains(id)).ToList()) {
                this._channels.Remove(stale);
            }

            foreach (var info in channels) {
                this.ApplyChannelInfo(info);
                foreach (var member in info.Members) {
                    userIds.Add(member.User.UserId);
                }

                if (info.Name != null) {
                    userIds.Add(info.Name.AuthorId);
                }
            }

            this._invites.Clear();
            foreach (var invite in invites) {
                this._invites[invite.ChannelId] = new InviteState(invite);
                userIds.Add(invite.Inviter.UserId);
            }
        }

        this.Publish();
        await this.EnsureIdentitiesAsync(userIds, ct, connection);

        foreach (var channelId in channels.Select(channel => channel.ChannelId)) {
            if (!this.Read(() => this.HasCurrentKey(channelId))) {
                await this.FetchEpochKeysAsync(channelId, ct, connection);
            }

            lock (this._lock) {
                this.TryDecryptName(channelId);
            }
        }

        foreach (var invite in invites) {
            this.OpenInvite(invite.ChannelId);
        }

        this.SaveSecrets();
        this.Publish();
    }

    // ================================================================ events

    private void OnEvent(Event ev) {
        try {
            if (ev.KindCase != Event.KindOneofCase.Announcement && !IsValidChannelId(ChannelIdOf(ev))) {
                this.Log(NoticeLevel.Debug, $"Ignored {ev.KindCase} with an invalid channel ID");
                return;
            }

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
                case Event.KindOneofCase.MemberChanged:
                    this.OnMemberChanged(ev.MemberChanged);
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
                    var name = this.Read(() => this._channels.GetValueOrDefault(ev.ChannelRemoved.ChannelId)?.DisplayName);
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
                case Event.KindOneofCase.ChatMessage:
                    // Ordered: a new epoch must be processed before messages encrypted under it.
                    this._inbox.Writer.TryWrite(ev);
                    break;
            }
        } catch (Exception ex) {
            this.Log(NoticeLevel.Error, $"Error handling {ev.KindCase}: {ex}");
        }
    }

    private static string? ChannelIdOf(Event ev) => ev.KindCase switch {
        Event.KindOneofCase.InviteReceived => ev.InviteReceived.Invite?.ChannelId,
        Event.KindOneofCase.InviteRevoked => ev.InviteRevoked.ChannelId,
        Event.KindOneofCase.MemberChanged => ev.MemberChanged.ChannelId,
        Event.KindOneofCase.RekeyNeeded => ev.RekeyNeeded.ChannelId,
        Event.KindOneofCase.EpochAdvanced => ev.EpochAdvanced.ChannelId,
        Event.KindOneofCase.ChatMessage => ev.ChatMessage.ChannelId,
        Event.KindOneofCase.ChannelRenamed => ev.ChannelRenamed.ChannelId,
        Event.KindOneofCase.ChannelRemoved => ev.ChannelRemoved.ChannelId,
        _ => null,
    };

    /// <summary>Channel IDs from the server must already be in canonical form (32 lowercase hex digits).</summary>
    private static bool IsValidChannelId(string? id) => id != null && ProtocolInfo.NormaliseChannelId(id) == id;

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
        }

        this.Publish();
        this.RunBackground("Reading an invite", async ct => {
            // Fresh, so an inviter who re-registered shows as "key changed" rather than as a forged invite.
            await this.EnsureIdentitiesAsync([invite.Inviter.UserId], ct, refresh: true);
            var view = this.OpenInvite(invite.ChannelId);
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

    private void OnMemberChanged(MemberChanged change) {
        string? message = null;
        var isMe = false;
        lock (this._lock) {
            isMe = change.User.UserId == this._me?.UserId;
            if (this._channels.TryGetValue(change.ChannelId, out var channel)) {
                var existing = channel.Members.FindIndex(member => member.User.UserId == change.User.UserId);
                switch (change.Kind) {
                    case MemberChangeKind.Invited:
                    case MemberChangeKind.Joined:
                    case MemberChangeKind.RankChanged: {
                        var member = new Member { User = change.User, Rank = change.Rank };
                        if (existing >= 0) {
                            channel.Members[existing] = member;
                        } else {
                            channel.Members.Add(member);
                        }

                        if (isMe) {
                            channel.MyRank = change.Rank;
                        }

                        break;
                    }
                    default:
                        if (existing >= 0) {
                            channel.Members.RemoveAt(existing);
                        }

                        break;
                }

                if (change.Kind is MemberChangeKind.Joined or MemberChangeKind.Left or MemberChangeKind.Kicked) {
                    channel.RekeyPending = true;
                }

                var who = $"{change.User.Name}@{change.User.WorldName}";
                message = change.Kind switch {
                    MemberChangeKind.Invited => $"{who} was invited to {channel.DisplayName}.",
                    MemberChangeKind.Joined => $"{who} joined {channel.DisplayName}.",
                    MemberChangeKind.Declined => $"{who} declined the invite to {channel.DisplayName}.",
                    MemberChangeKind.Left => $"{who} left {channel.DisplayName}.",
                    MemberChangeKind.Kicked => $"{who} was removed from {channel.DisplayName}.",
                    MemberChangeKind.RankChanged => $"{who} is now {RankName(change.Rank)} in {channel.DisplayName}.",
                    MemberChangeKind.InviteCancelled => $"The invite for {who} to {channel.DisplayName} was cancelled.",
                    _ => null,
                };
            }
        }

        this.Publish();
        if (message != null) {
            this.RaiseNotice(NoticeLevel.Info, message, change.ChannelId);
        }

        if (change.Kind == MemberChangeKind.Joined && !isMe) {
            this.RunBackground("Fetching a new member's identity", ct => this.EnsureIdentitiesAsync([change.User.UserId], ct));
        }
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
            designated = rekey.DesignatedUserId == this._me?.UserId;
        }

        this.Publish();
        if (designated && this._options.AutoRekeyWhenDesignated) {
            this.RunBackground("Rekeying a channel", ct => this.RekeyAsync(rekey.ChannelId, ct));
        }
    }

    private async Task InboxLoop(CancellationToken ct) {
        try {
            await foreach (var ev in this._inbox.Reader.ReadAllAsync(ct)) {
                try {
                    if (ev.KindCase == Event.KindOneofCase.EpochAdvanced) {
                        await this.ProcessEpochAdvancedAsync(ev.EpochAdvanced, ct);
                    } else if (ev.KindCase == Event.KindOneofCase.ChatMessage) {
                        await this.ProcessChatMessageAsync(ev.ChatMessage, ct);
                    }
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

    private async Task ProcessEpochAdvancedAsync(EpochAdvanced advanced, CancellationToken ct) {
        var identities = await this.EnsureIdentitiesAsync([advanced.AuthorId], ct);
        if (!identities.TryGetValue(advanced.AuthorId, out var author)) {
            this.RaiseNotice(NoticeLevel.Warning, "Rejected a new key for a channel: its author isn't anyone you share a channel with.", advanced.ChannelId);
            return;
        }

        string? rejected = null;
        BadKey? bad = null;
        lock (this._lock) {
            if (this._identity == null || this._me == null || !this._channels.TryGetValue(advanced.ChannelId, out var channel)) {
                return;
            }

            byte[]? key = null;
            var check = advanced.MyKey == null
                ? EpochKeyCheck.BadSignature
                : ChannelCrypto.TryOpenEpochKey(advanced.MyKey, advanced.ChannelId, advanced.Epoch, advanced.AuthorId, author.Identity.SigningPublicKey.Span, this._identity, this._me.UserId, out key);
            var alreadyHeld = key != null && this.GetEpochKey(advanced.ChannelId, advanced.Epoch) is { } existing && existing.AsSpan().SequenceEqual(key);

            // The same key may already have been fetched while this event waited in the queue: not a replay.
            if (!alreadyHeld) {
                rejected = this.CheckEpochKeyAuthor(channel, advanced.Epoch, advanced.AuthorId);
                if (rejected == null && key == null) {
                    bad = this.FlagBadEpochKey(channel, advanced.Epoch, author.User, check);
                    rejected = "it failed signature or decryption checks";
                }
            }

            if (rejected == null) {
                if (!alreadyHeld) {
                    this.StoreEpochKey(advanced.ChannelId, advanced.Epoch, key!);
                }

                channel.ServerEpoch = Math.Max(channel.ServerEpoch, advanced.Epoch);
                channel.RekeyPending = false;
                if (advanced.Name != null) {
                    channel.EncryptedName = advanced.Name;
                }

                this.TryDecryptName(channel.Id);
            }
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
    /// An epoch key is only acceptable from a current member, and only for an
    /// epoch newer than any key already held. With no key held, it may be at
    /// most one epoch behind the server's. Call inside the lock.
    /// </summary>
    /// <returns>Null if acceptable, otherwise the reason it isn't.</returns>
    private string? CheckEpochKeyAuthor(ChannelState channel, ulong epoch, long authorId) {
        if (!channel.Members.Any(member => member.User.UserId == authorId && member.Rank >= Rank.Member)) {
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
        var (channelKnown, senderIsMember, seen, channelName, blocked) = this.Read(() => {
            var channel = this._channels.GetValueOrDefault(message.ChannelId);
            return (channel != null,
                channel?.Members.Any(member => member.User.UserId == message.SenderId && member.Rank >= Rank.Member) == true,
                this._seenMessages.Contains(messageId),
                channel?.DisplayName,
                this.IsBlocked(message.SenderId));
        });

        if (!channelKnown || seen || blocked) {
            return;
        }

        if (!senderIsMember) {
            // A removed member (with the server's help) could otherwise keep posting with an old key.
            var who = this.Read(() => this._identities.TryGetValue(message.SenderId, out var known) ? $"{known.User.Name}@{known.User.WorldName}" : "someone");
            this.RaiseNotice(NoticeLevel.Warning, $"Dropped a message in {channelName} from {who}, who isn't a member of it.", message.ChannelId);
            return;
        }

        var identities = await this.EnsureIdentitiesAsync([message.SenderId], ct);
        if (!identities.TryGetValue(message.SenderId, out var sender)) {
            this.RaiseNotice(NoticeLevel.Warning, "Dropped a message from an unknown sender.", message.ChannelId);
            return;
        }

        var key = this.Read(() => this.GetEpochKey(message.ChannelId, message.Epoch));
        if (key == null) {
            await this.FetchEpochKeysAsync(message.ChannelId, ct);
            key = this.Read(() => this.GetEpochKey(message.ChannelId, message.Epoch));
        }

        if (key == null) {
            this.RaiseNotice(NoticeLevel.Warning, $"Couldn't decrypt a message from {sender.User.Name}: no key for that epoch yet.", message.ChannelId);
            return;
        }

        var now = this._options.TimeProvider.GetUtcNow();
        if (this.Read(() => this.IsPastOldEpochGrace(message.ChannelId, message.Epoch, now))) {
            this.RaiseNotice(NoticeLevel.Warning, $"Dropped a message from {sender.User.Name}: it uses an older key that was replaced a while ago.", message.ChannelId);
            return;
        }

        var content = ChannelCrypto.DecryptMessage(message, key, sender.Identity.SigningPublicKey.Span);
        if (content == null) {
            this.RaiseNotice(NoticeLevel.Warning, $"Dropped a message claiming to be from {sender.User.Name}: it failed signature or decryption checks.", message.ChannelId);
            return;
        }

        // The timestamp is signed, so an old message can't be replayed as new
        // once it falls outside this window (the seen-set covers the window itself).
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(message.TimestampUnixMs);
        if ((now - timestamp).Duration() > MaxMessageClockSkew) {
            this.RaiseNotice(NoticeLevel.Warning, $"Dropped a message from {sender.User.Name} dated {timestamp.ToLocalTime():g}: too far from the current time (replayed, or a wrong clock).", message.ChannelId);
            return;
        }

        // The seen-set is lost on restart; this persisted high-water mark isn't.
        var newest = this.Read(() => this._secrets.NewestMessageTimes.TryGetValue(message.ChannelId, out var senders)
                                     && senders.TryGetValue(message.SenderId, out var time) ? time : (long?) null);
        if (newest is { } newestMs && message.TimestampUnixMs < newestMs - (long) MessageReorderAllowance.TotalMilliseconds) {
            this.RaiseNotice(NoticeLevel.Warning, $"Dropped a message from {sender.User.Name} dated {timestamp.ToLocalTime():g}: it's older than messages already received from them (replayed?).", message.ChannelId);
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
            ? new IncomingMessage(message.ChannelId, channelName, sender.User, isOwn, content.Text.Text, false, timestamp)
            : new IncomingMessage(message.ChannelId, channelName, sender.User, isOwn, null, true, timestamp));
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
        var userIds = this.Read(() => {
            var ids = new List<long>();
            if (this._channels.TryGetValue(channelId, out var channel)) {
                ids.AddRange(channel.Members.Select(member => member.User.UserId));
                if (channel.EncryptedName != null) {
                    ids.Add(channel.EncryptedName.AuthorId);
                }
            }

            return ids;
        });

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
        var authors = keys.Keys.Select(key => key.AuthorId).ToList();
        if (keys.Name != null) {
            authors.Add(keys.Name.AuthorId);
        }

        var identities = await this.EnsureIdentitiesAsync(authors, ct, connection);

        BadKey? bad = null;
        lock (this._lock) {
            if (this._identity == null || this._me == null || !this._channels.TryGetValue(channelId, out var channel)) {
                return;
            }

            foreach (var entry in keys.Keys.OrderBy(entry => entry.Epoch)) {
                if (!identities.TryGetValue(entry.AuthorId, out var author) || entry.Key == null
                    || this.GetEpochKey(channelId, entry.Epoch) != null) {
                    continue;
                }

                if (this.CheckEpochKeyAuthor(channel, entry.Epoch, entry.AuthorId) is { } reason) {
                    this.Log(NoticeLevel.Warning, $"Ignored epoch {entry.Epoch} key for {channelId}: {reason}");
                    continue;
                }

                var check = ChannelCrypto.TryOpenEpochKey(entry.Key, channelId, entry.Epoch, entry.AuthorId, author.Identity.SigningPublicKey.Span, this._identity, this._me.UserId, out var key);
                if (key != null) {
                    this.StoreEpochKey(channelId, entry.Epoch, key);
                } else {
                    this.Log(NoticeLevel.Warning, $"Epoch {entry.Epoch} key for {channelId} failed verification ({check})");
                    bad ??= this.FlagBadEpochKey(channel, entry.Epoch, author.User, check);
                }
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
        if (bad != null) {
            this.HandleBadEpochKey(bad);
        }
    }

    /// <summary>Returns verified identities for the given users, fetching any that aren't cached (or all of them, with <paramref name="refresh"/>).</summary>
    private async Task<Dictionary<long, UserIdentity>> EnsureIdentitiesAsync(IEnumerable<long> userIds, CancellationToken ct, Connection? connection = null, bool refresh = false) {
        var wanted = userIds.Distinct().ToList();
        var missing = refresh ? wanted : this.Read(() => wanted.Where(id => !this._identities.ContainsKey(id)).ToList());

        if (missing.Count > 0) {
            var request = new GetIdentities();
            request.UserIds.AddRange(missing);
            var response = await this.RequestAsync(connection ?? this.RequireConnection(), new ClientFrame { GetIdentities = request }, ct);
            this.AcceptIdentities(response.Identities?.Identities_ ?? []);
        }

        return this.Read(() => wanted
            .Where(this._identities.ContainsKey)
            .ToDictionary(id => id, id => this._identities[id]));
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
                var who = $"{user.Name}@{user.WorldName}";
                var signing = identity.Identity.SigningPublicKey.ToByteArray();
                var agreement = identity.Identity.AgreementPublicKey.ToByteArray();
                var fingerprint = IdentityKeys.FingerprintOf(signing, agreement);
                var changed = false;

                if (this._secrets.PinnedIdentities.TryGetValue(user.UserId, out var pinned)) {
                    if (!pinned.SigningPublicKey.AsSpan().SequenceEqual(signing) || !pinned.AgreementPublicKey.AsSpan().SequenceEqual(agreement)) {
                        pinned.SigningPublicKey = signing;
                        pinned.AgreementPublicKey = agreement;
                        pinned.KeyChangeUnacknowledged = true;
                        changed = true;
                        warnings.Add($"{who}'s identity key changed (they may have re-registered). Compare fingerprints over /tell before trusting it: {fingerprint}");
                    }

                    if (pinned.Name != user.Name || pinned.WorldName != user.WorldName) {
                        if (pinned.Name.Length > 0) {
                            warnings.Add($"{pinned.Name}@{pinned.WorldName} is now shown as {who} (a rename or world transfer). Their keys are unchanged.");
                        }

                        pinned.Name = user.Name;
                        pinned.WorldName = user.WorldName;
                        changed = true;
                    }

                    if (pinned.KeyVersion != identity.KeyVersion) {
                        pinned.KeyVersion = identity.KeyVersion;
                        changed = true;
                    }
                } else {
                    var previousOwner = this._secrets.PinnedIdentities.FirstOrDefault(pair =>
                        string.Equals(pair.Value.Name, user.Name, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(pair.Value.WorldName, user.WorldName, StringComparison.OrdinalIgnoreCase));
                    if (previousOwner.Value != null) {
                        warnings.Add($"{who} now belongs to a different account than the one you saw before. Compare fingerprints over /tell before trusting it: {fingerprint}");
                    }

                    this._secrets.PinnedIdentities[user.UserId] = new PinnedIdentity {
                        SigningPublicKey = signing,
                        AgreementPublicKey = agreement,
                        KeyVersion = identity.KeyVersion,
                        Name = user.Name,
                        WorldName = user.WorldName,
                        KeyChangeUnacknowledged = previousOwner.Value != null,
                    };
                    changed = true;
                }

                if (changed) {
                    this._secretsVersion++;
                }

                this._identities[user.UserId] = identity;
                accepted.Add(identity);
            }
        }

        this.SaveSecrets();
        foreach (var warning in warnings) {
            this.RaiseNotice(NoticeLevel.Warning, warning);
        }

        return accepted;
    }

    private InviteView? OpenInvite(string channelId) {
        lock (this._lock) {
            if (!this._invites.TryGetValue(channelId, out var invite)
                || this._identity == null
                || this._me == null
                || !this._identities.TryGetValue(invite.Info.Inviter.UserId, out var inviter)) {
                return null;
            }

            invite.Name = ChannelCrypto.OpenInvite(invite.Info, inviter.Identity.SigningPublicKey.Span, this._identity, this._me.UserId);
            invite.Verified = invite.Name != null;
            return this.ToView(invite);
        }
    }

    /// <summary>Call inside the lock.</summary>
    private InviteView ToView(InviteState invite) {
        var inviterId = invite.Info.Inviter.UserId;
        var fingerprint = this._identities.TryGetValue(inviterId, out var identity)
            ? IdentityKeys.FingerprintOf(identity.Identity.SigningPublicKey.Span, identity.Identity.AgreementPublicKey.Span)
            : null;
        var keyChanged = this._secrets.PinnedIdentities.TryGetValue(inviterId, out var pinned) && pinned.KeyChangeUnacknowledged;
        return new InviteView(invite.Info.ChannelId, invite.Info.Inviter, invite.Name, invite.Verified,
            DateTimeOffset.FromUnixTimeSeconds(invite.Info.CreatedUnix), keyChanged, fingerprint);
    }

    // ================================================================ state helpers (call inside _lock)

    private ChannelState ApplyChannelInfo(ChannelInfo info) {
        if (!this._channels.TryGetValue(info.ChannelId, out var channel)) {
            channel = new ChannelState(info.ChannelId);
            this._channels[info.ChannelId] = channel;
        }

        // Only a hint: it decides when to fetch keys or rekey, never which key is used.
        channel.ServerEpoch = info.Epoch;
        channel.RekeyPending = info.RekeyPending;
        channel.MyRank = info.MyRank;
        channel.Members = info.Members.ToList();
        if (info.Name != null) {
            channel.EncryptedName = info.Name;
        }

        this.TryDecryptName(info.ChannelId);
        return channel;
    }

    /// <summary>
    /// Shows the name the server offered, if it is encrypted under the key epoch
    /// in use and is not older than the name already accepted. Otherwise a
    /// server could replay an old, validly signed name.
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
        if (key == null || !this._identities.TryGetValue(offered.AuthorId, out var author)) {
            return;
        }

        var name = ChannelCrypto.DecryptName(offered, channelId, key, author.Identity.SigningPublicKey.Span);
        if (name != null) {
            channel.Name = name;
            this.SetNameVersion(channelId, version);
        }
    }

    private void SetNameVersion(string channelId, NameVersion version) {
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

    private void StoreEpochKey(string channelId, ulong epoch, byte[] key) {
        if (!this._secrets.EpochKeys.TryGetValue(channelId, out var keys)) {
            keys = new Dictionary<ulong, byte[]>();
            this._secrets.EpochKeys[channelId] = keys;
        }

        keys[epoch] = key;
        if (this._channels.TryGetValue(channelId, out var channel)) {
            channel.KeyAcceptedAt[epoch] = this._options.TimeProvider.GetUtcNow();
        }

        var newest = keys.Keys.Max();
        foreach (var old in keys.Keys.Where(e => e + KeptEpochsPerChannel <= newest).ToList()) {
            keys.Remove(old);
        }

        this._secretsVersion++;
    }

    private void RemoveChannel(string channelId) {
        lock (this._lock) {
            this._channels.Remove(channelId);
            this._secrets.EpochKeys.Remove(channelId);
            this._secrets.ChannelNameVersions.Remove(channelId);
            this._secrets.NewestMessageTimes.Remove(channelId);
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

    private IdentityKeys EnsureIdentity() {
        lock (this._lock) {
            if (this._identity != null) {
                return this._identity;
            }

            this._identity = IdentityKeys.Generate();
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

    private T Read<T>(Func<T> read) {
        lock (this._lock) {
            return read();
        }
    }

    private void SetState(ConnectionState state, string? status) {
        lock (this._lock) {
            this._state = state;
            this._status = status;
            if (state is ConnectionState.Connecting or ConnectionState.Reconnecting or ConnectionState.Stopped) {
                this._challenge = null;
            }
        }

        this.Publish();
    }

    private void Publish() {
        SessionSnapshot snapshot;
        lock (this._lock) {
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
                    .ToImmutableArray());
            this._snapshot = snapshot;
        }

        this.InvokeSafely(this.SnapshotChanged, snapshot);
    }

    private ChannelView ToView(ChannelState channel) {
        var members = channel.Members
            .OrderByDescending(member => member.Rank)
            .ThenBy(member => member.User.Name, StringComparer.OrdinalIgnoreCase)
            .Select(member => {
                var fingerprint = this._identities.TryGetValue(member.User.UserId, out var identity)
                    ? IdentityKeys.FingerprintOf(identity.Identity.SigningPublicKey.Span, identity.Identity.AgreementPublicKey.Span)
                    : null;
                var keyChanged = this._secrets.PinnedIdentities.TryGetValue(member.User.UserId, out var pinned) && pinned.KeyChangeUnacknowledged;
                return new MemberView(member.User, member.Rank, fingerprint, keyChanged);
            })
            .ToImmutableArray();

        var keyEpoch = this.KeyEpochOf(channel.Id);
        return new ChannelView(channel.Id, channel.Name, keyEpoch ?? channel.ServerEpoch, channel.ServerEpoch,
            this.HasCurrentKey(channel.Id), channel.RekeyPending, channel.MyRank, members);
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
        public Rank MyRank { get; set; }
        public List<Member> Members { get; set; } = [];

        /// <summary>The newest name the server offered. Only shown once <see cref="TryDecryptName"/> accepts it.</summary>
        public EncryptedName? EncryptedName { get; set; }
        public string? Name { get; set; }

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
    }
}
