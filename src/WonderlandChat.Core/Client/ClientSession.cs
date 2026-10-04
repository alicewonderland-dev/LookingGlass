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

    private readonly ClientSessionOptions _options;
    private readonly ISecretStore _store;
    private readonly Lock _lock = new();
    private readonly Lock _saveLock = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _channelLocks = new();
    private readonly Channel<Event> _inbox = Channel.CreateUnbounded<Event>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentQueue<TraceEntry> _trace = new();

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
    private readonly HashSet<long> _keyChanged = new();
    private readonly HashSet<string> _seenMessages = new();
    private readonly Queue<string> _seenOrder = new();
    private long _secretsVersion;
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
        } catch (OperationCanceledException) {
            // Expected.
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

    // ================================================================ registration

    public async Task<RegistrationChallenge> StartRegistrationAsync(Character character, CancellationToken ct = default) {
        var identity = this.EnsureIdentity();
        var response = await this.RequestAsync(new ClientFrame {
            StartRegistration = new StartRegistration { Character = character, Identity = identity.ToBundle() },
        }, ct);

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
        var response = await this.RequestAsync(connection, new ClientFrame { CompleteRegistration = new CompleteRegistration() }, ct);
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
        lock (this._lock) {
            this.StoreEpochKey(channelId, 0, key);
            var channel = this.ApplyChannelInfo(info);
            channel.Name = name;
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
            if (accept && response.Channel != null) {
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
        await this.RequestAsync(new ClientFrame { KickMember = new KickMember { ChannelId = channelId, UserId = userId } }, ct);
        lock (this._lock) {
            if (this._channels.TryGetValue(channelId, out var channel)) {
                channel.Members.RemoveAll(member => member.User.UserId == userId);
                channel.RekeyPending = true;
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
        var (epoch, key) = this.Read(() => {
            var channel = this._channels.GetValueOrDefault(channelId) ?? throw new InvalidOperationException("Unknown channel.");
            return (channel.Epoch, this.GetEpochKey(channelId, channel.Epoch));
        });

        if (key == null) {
            throw new InvalidOperationException("This channel's key isn't available yet.");
        }

        var name = ChannelCrypto.EncryptName(newName, key, channelId, epoch, identity, me.UserId);
        await this.RequestAsync(new ClientFrame { RenameChannel = new RenameChannel { ChannelId = channelId, Name = name } }, ct);

        lock (this._lock) {
            if (this._channels.TryGetValue(channelId, out var channel)) {
                channel.EncryptedName = name;
                channel.Name = newName;
            }
        }

        this.Publish();
    }

    /// <summary>Re-fetches channels, invites, identities and keys from the server.</summary>
    public Task RefreshAsync(CancellationToken ct = default) => this.RefreshAsync(this.RequireConnection(), ct);

    // ================================================================ rekeying

    /// <summary>
    /// Moves the channel to a new epoch: a fresh key sealed to every current
    /// member. Safe to call concurrently; the server accepts one rekey per epoch.
    /// </summary>
    public async Task RekeyAsync(string channelId, CancellationToken ct = default) {
        var gate = this._channelLocks.GetOrAdd(channelId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try {
            for (var attempt = 0; attempt < 3; attempt++) {
                var (identity, me) = this.RequireIdentityAndUser();
                var (epoch, name, memberIds) = this.Read(() => {
                    var channel = this._channels.GetValueOrDefault(channelId) ?? throw new InvalidOperationException("Unknown channel.");
                    var ids = channel.Members.Where(member => member.Rank >= Rank.Member).Select(member => member.User.UserId).ToList();
                    return (channel.Epoch, channel.Name, ids);
                });

                if (name == null) {
                    throw new InvalidOperationException("Can't rekey a channel whose name isn't known yet.");
                }

                var identities = await this.EnsureIdentitiesAsync(memberIds, ct);
                var newEpoch = epoch + 1;
                var key = ChannelCrypto.NewEpochKey();
                var request = new SubmitRekey {
                    ChannelId = channelId,
                    NewEpoch = newEpoch,
                    Name = ChannelCrypto.EncryptName(name, key, channelId, newEpoch, identity, me.UserId),
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
                    if (!this.Read(() => this._channels.GetValueOrDefault(channelId)?.RekeyPending ?? false)) {
                        return;
                    }

                    continue;
                }

                lock (this._lock) {
                    this.StoreEpochKey(channelId, newEpoch, key);
                    if (this._channels.TryGetValue(channelId, out var channel) && channel.Epoch < newEpoch) {
                        channel.Epoch = newEpoch;
                        channel.RekeyPending = false;
                        channel.EncryptedName = request.Name;
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
        for (var attempt = 0; attempt < 3; attempt++) {
            var (identity, me) = this.RequireIdentityAndUser();
            var (epoch, pending, rank) = this.Read(() => {
                var channel = this._channels.GetValueOrDefault(channelId) ?? throw new InvalidOperationException("You're not in that channel.");
                return (channel.Epoch, channel.RekeyPending, channel.MyRank);
            });

            if (rank < Rank.Member) {
                throw new InvalidOperationException("You haven't joined that channel.");
            }

            if (pending) {
                await this.RekeyAsync(channelId, ct);
                continue;
            }

            var key = this.Read(() => this.GetEpochKey(channelId, epoch));
            if (key == null) {
                await this.FetchEpochKeysAsync(channelId, ct);
                key = this.Read(() => this.GetEpochKey(channelId, epoch))
                    ?? throw new InvalidOperationException("You don't have this channel's key yet. A member who is online will share it.");
            }

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

            this.MarkSeen(message.MessageId);
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

        var userIds = new HashSet<long>();
        lock (this._lock) {
            var listed = list.Channels.Select(channel => channel.ChannelId).ToHashSet();
            foreach (var stale in this._channels.Keys.Where(id => !listed.Contains(id)).ToList()) {
                this._channels.Remove(stale);
            }

            foreach (var info in list.Channels) {
                this.ApplyChannelInfo(info);
                foreach (var member in info.Members) {
                    userIds.Add(member.User.UserId);
                }

                if (info.Name != null) {
                    userIds.Add(info.Name.AuthorId);
                }
            }

            this._invites.Clear();
            foreach (var invite in list.Invites) {
                this._invites[invite.ChannelId] = new InviteState(invite);
                userIds.Add(invite.Inviter.UserId);
            }
        }

        this.Publish();
        await this.EnsureIdentitiesAsync(userIds, ct, connection);

        foreach (var channelId in list.Channels.Select(channel => channel.ChannelId)) {
            if (!this.Read(() => this.HasCurrentKey(channelId))) {
                await this.FetchEpochKeysAsync(channelId, ct, connection);
            }

            lock (this._lock) {
                this.TryDecryptName(channelId);
            }
        }

        foreach (var invite in list.Invites) {
            this.OpenInvite(invite.ChannelId);
        }

        this.Publish();
    }

    // ================================================================ events

    private void OnEvent(Event ev) {
        try {
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
                        if (this._channels.TryGetValue(ev.ChannelRenamed.ChannelId, out var channel)) {
                            channel.EncryptedName = ev.ChannelRenamed.Name;
                            this.TryDecryptName(channel.Id);
                        }
                    }

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

    private void OnInviteReceived(InviteInfo invite) {
        lock (this._lock) {
            this._invites[invite.ChannelId] = new InviteState(invite);
        }

        this.Publish();
        this.RunBackground("Reading an invite", async ct => {
            await this.EnsureIdentitiesAsync([invite.Inviter.UserId], ct);
            var view = this.OpenInvite(invite.ChannelId);
            this.Publish();
            if (view != null) {
                this.InvokeSafely(this.InviteReceived, view);
                this.RaiseNotice(NoticeLevel.Info,
                    view.Verified
                        ? $"{view.Inviter.Name}@{view.Inviter.WorldName} invited you to \"{view.ChannelName}\"."
                        : $"{view.Inviter.Name}@{view.Inviter.WorldName} sent an invite that failed verification.",
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
            if (this._channels.TryGetValue(rekey.ChannelId, out var channel)) {
                channel.RekeyPending = true;
            }

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
            this.Log(NoticeLevel.Warning, $"Unknown rekey author {advanced.AuthorId}");
            return;
        }

        lock (this._lock) {
            if (this._identity == null || this._me == null) {
                return;
            }

            var key = advanced.MyKey == null
                ? null
                : ChannelCrypto.OpenEpochKey(advanced.MyKey, advanced.ChannelId, advanced.Epoch, advanced.AuthorId, author.Identity.SigningPublicKey.Span, this._identity, this._me.UserId);

            if (key == null) {
                this.Log(NoticeLevel.Warning, $"Epoch {advanced.Epoch} key for {advanced.ChannelId} failed verification");
                return;
            }

            this.StoreEpochKey(advanced.ChannelId, advanced.Epoch, key);
            if (this._channels.TryGetValue(advanced.ChannelId, out var channel) && channel.Epoch <= advanced.Epoch) {
                channel.Epoch = advanced.Epoch;
                channel.RekeyPending = false;
                if (advanced.Name != null) {
                    channel.EncryptedName = advanced.Name;
                }

                this.TryDecryptName(channel.Id);
            }
        }

        this.SaveSecrets();
        this.Publish();
    }

    private async Task ProcessChatMessageAsync(ChatMessage message, CancellationToken ct) {
        if (!this.MarkSeen(message.MessageId)) {
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

        var content = ChannelCrypto.DecryptMessage(message, key, sender.Identity.SigningPublicKey.Span);
        if (content == null) {
            this.RaiseNotice(NoticeLevel.Warning, $"Dropped a message claiming to be from {sender.User.Name}: it failed signature or decryption checks.", message.ChannelId);
            return;
        }

        var channelName = this.Read(() => this._channels.GetValueOrDefault(message.ChannelId)?.Name);
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(message.TimestampUnixMs);
        var isOwn = message.SenderId == this.Read(() => this._me?.UserId);
        this.RaiseMessage(content.KindCase == Content.KindOneofCase.Text
            ? new IncomingMessage(message.ChannelId, channelName, sender.User, isOwn, content.Text.Text, false, timestamp)
            : new IncomingMessage(message.ChannelId, channelName, sender.User, isOwn, null, true, timestamp));
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
            var epoch = this._channels.GetValueOrDefault(channelId)?.Epoch ?? 0;
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

        lock (this._lock) {
            if (this._identity == null || this._me == null) {
                return;
            }

            foreach (var entry in keys.Keys) {
                if (!identities.TryGetValue(entry.AuthorId, out var author) || entry.Key == null) {
                    continue;
                }

                var key = ChannelCrypto.OpenEpochKey(entry.Key, channelId, entry.Epoch, entry.AuthorId, author.Identity.SigningPublicKey.Span, this._identity, this._me.UserId);
                if (key != null) {
                    this.StoreEpochKey(channelId, entry.Epoch, key);
                } else {
                    this.Log(NoticeLevel.Warning, $"Epoch {entry.Epoch} key for {channelId} failed verification");
                }
            }

            if (keys.Name != null && this._channels.TryGetValue(channelId, out var channel)) {
                channel.EncryptedName = keys.Name;
                this.TryDecryptName(channelId);
            }
        }

        this.SaveSecrets();
        this.Publish();
    }

    /// <summary>Returns verified identities for the given users, fetching any that aren't cached.</summary>
    private async Task<Dictionary<long, UserIdentity>> EnsureIdentitiesAsync(IEnumerable<long> userIds, CancellationToken ct, Connection? connection = null) {
        var wanted = userIds.Distinct().ToList();
        var missing = this.Read(() => wanted.Where(id => !this._identities.ContainsKey(id)).ToList());

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

    /// <summary>Validates identities from the server and pins them (trust on first use, warn on change).</summary>
    private List<UserIdentity> AcceptIdentities(IEnumerable<UserIdentity> identities) {
        var accepted = new List<UserIdentity>();
        var warnings = new List<string>();

        lock (this._lock) {
            foreach (var identity in identities) {
                if (identity.User == null || !IdentityKeys.IsValidBundle(identity.Identity)) {
                    this.Log(NoticeLevel.Warning, $"Rejected an invalid identity for user {identity.User?.UserId}");
                    continue;
                }

                var userId = identity.User.UserId;
                var signing = identity.Identity.SigningPublicKey.ToByteArray();
                var agreement = identity.Identity.AgreementPublicKey.ToByteArray();

                if (this._secrets.PinnedIdentities.TryGetValue(userId, out var pinned)) {
                    if (!pinned.SigningPublicKey.AsSpan().SequenceEqual(signing) || !pinned.AgreementPublicKey.AsSpan().SequenceEqual(agreement)) {
                        this._keyChanged.Add(userId);
                        warnings.Add($"{identity.User.Name}@{identity.User.WorldName}'s identity key changed (they may have re-registered). " +
                                     $"Compare fingerprints over /tell before trusting it: {IdentityKeys.FingerprintOf(signing, agreement)}");
                    }
                }

                this._secrets.PinnedIdentities[userId] = new PinnedIdentity {
                    SigningPublicKey = signing,
                    AgreementPublicKey = agreement,
                    KeyVersion = identity.KeyVersion,
                };
                this._secretsVersion++;
                this._identities[userId] = identity;
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
            return invite.ToView();
        }
    }

    // ================================================================ state helpers (call inside _lock)

    private ChannelState ApplyChannelInfo(ChannelInfo info) {
        if (!this._channels.TryGetValue(info.ChannelId, out var channel)) {
            channel = new ChannelState(info.ChannelId);
            this._channels[info.ChannelId] = channel;
        }

        channel.Epoch = Math.Max(channel.Epoch, info.Epoch);
        channel.RekeyPending = info.RekeyPending;
        channel.MyRank = info.MyRank;
        channel.Members = info.Members.ToList();
        if (info.Name != null) {
            channel.EncryptedName = info.Name;
        }

        this.TryDecryptName(info.ChannelId);
        return channel;
    }

    private void TryDecryptName(string channelId) {
        if (!this._channels.TryGetValue(channelId, out var channel) || channel.EncryptedName == null) {
            return;
        }

        var key = this.GetEpochKey(channelId, channel.EncryptedName.Epoch);
        if (key == null || !this._identities.TryGetValue(channel.EncryptedName.AuthorId, out var author)) {
            return;
        }

        var name = ChannelCrypto.DecryptName(channel.EncryptedName, channelId, key, author.Identity.SigningPublicKey.Span);
        if (name != null) {
            channel.Name = name;
        }
    }

    private bool HasCurrentKey(string channelId) {
        return this._channels.TryGetValue(channelId, out var channel) && this.GetEpochKey(channelId, channel.Epoch) != null;
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
            this._secretsVersion++;
        }

        this.SaveSecrets();
        this.Publish();
    }

    /// <returns>False if the message was already seen.</returns>
    private bool MarkSeen(ByteString messageId) {
        var id = Convert.ToHexString(messageId.Span);
        lock (this._lock) {
            if (!this._seenMessages.Add(id)) {
                return false;
            }

            this._seenOrder.Enqueue(id);
            while (this._seenOrder.Count > SeenMessageCapacity) {
                this._seenMessages.Remove(this._seenOrder.Dequeue());
            }

            return true;
        }
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
                this._invites.Values.Select(invite => invite.ToView()).ToImmutableArray(),
                this._limits,
                this._debugAccountsEnabled,
                this._challenge);
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
                return new MemberView(member.User, member.Rank, fingerprint, this._keyChanged.Contains(member.User.UserId));
            })
            .ToImmutableArray();

        return new ChannelView(channel.Id, channel.Name, channel.Epoch, this.GetEpochKey(channel.Id, channel.Epoch) != null, channel.RekeyPending, channel.MyRank, members);
    }

    // ================================================================ plumbing

    private Connection RequireConnection() {
        return this._connection ?? throw new SessionDisconnectedException("Not connected to the server.");
    }

    private Task<Response> RequestAsync(ClientFrame frame, CancellationToken ct) {
        return this.RequestAsync(this.RequireConnection(), frame, ct);
    }

    private async Task<Response> RequestAsync(Connection connection, ClientFrame frame, CancellationToken ct) {
        var response = await connection.RequestAsync(frame, ct);
        if (response.Error != null) {
            throw new ServerErrorException(response.Error.Code, response.Error.Message);
        }

        return response;
    }

    private void SaveSecrets() {
        ClientSecrets copy;
        long version;
        lock (this._lock) {
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

    private void RunBackground(string what, Func<CancellationToken, Task> work) {
        var ct = this._runCts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () => {
            try {
                await work(ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                // Stopping.
            } catch (Exception ex) {
                this.RaiseNotice(NoticeLevel.Warning, $"{what} failed: {ex.Message}");
            }
        }, ct);
    }

    private void AddTrace(bool outgoing, string summary) {
        this._trace.Enqueue(new TraceEntry(DateTimeOffset.Now, outgoing, summary));
        while (this._trace.Count > this._options.TraceCapacity && this._trace.TryDequeue(out _)) {
        }
    }

    private void RaiseMessage(IncomingMessage message) => this.InvokeSafely(this.MessageReceived, message);

    private void RaiseNotice(NoticeLevel level, string text, string? channelId = null) {
        this.Log(level, text);
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
        public ulong Epoch { get; set; }
        public bool RekeyPending { get; set; }
        public Rank MyRank { get; set; }
        public List<Member> Members { get; set; } = [];
        public EncryptedName? EncryptedName { get; set; }
        public string? Name { get; set; }
        public string DisplayName => this.Name ?? $"(encrypted channel {this.Id[..8]})";
    }

    private sealed class InviteState(InviteInfo info) {
        public InviteInfo Info { get; } = info;
        public string? Name { get; set; }
        public bool Verified { get; set; }

        public InviteView ToView() => new(
            this.Info.ChannelId,
            this.Info.Inviter,
            this.Name,
            this.Verified,
            DateTimeOffset.FromUnixTimeSeconds(this.Info.CreatedUnix));
    }
}
