using System.Collections.Immutable;
using WonderlandChat.Core.Client;

namespace WonderlandChat.Plugin;

/// <summary>
/// Owns the <see cref="ClientSession"/> for the logged-in character. Session
/// events arrive on background threads and are forwarded to the framework
/// thread before they touch configuration or chat.
/// </summary>
public sealed class SessionManager : IDisposable {
    private const int NoticeHistory = 100;

    private readonly Configuration _config;
    private readonly PlayerTracker _player;
    private readonly ChatOutput _chat;
    private readonly Lock _noticesLock = new();
    private readonly LinkedList<SessionNotice> _notices = new();
    private ClientSession? _session;
    private PlayerInfo? _sessionPlayer;
    private volatile ImmutableDictionary<string, int> _slots = ImmutableDictionary<string, int>.Empty;

    public SessionManager(Configuration config, PlayerTracker player, ChatOutput chat) {
        this._config = config;
        this._player = player;
        this._chat = chat;
        player.Changed += this.OnPlayerChanged;
    }

    /// <summary>The current session, or null when logged out or not connected.</summary>
    public ClientSession? Session => Volatile.Read(ref this._session);

    public SessionSnapshot Snapshot => this.Session?.Snapshot ?? SessionSnapshot.Empty;

    public PlayerInfo? Player => this._sessionPlayer ?? this._player.Current;

    public IReadOnlyList<SessionNotice> RecentNotices {
        get {
            lock (this._noticesLock) {
                return this._notices.ToList();
            }
        }
    }

    /// <summary>Call on the framework thread after changing the server URL.</summary>
    public void Restart() {
        this.Stop();
        if (this._player.Current is { } player && this._config.AutoConnect) {
            this.StartFor(player);
        }
    }

    public void Connect() {
        if (this.Session == null && this._player.Current is { } player) {
            this.StartFor(player);
        }
    }

    public void Disconnect() => this.Stop();

    /// <summary>The command slot of a channel for the current character. Safe from any thread.</summary>
    public int? SlotOf(string channelId) {
        return this._slots.TryGetValue(channelId, out var slot) ? slot : null;
    }

    /// <summary>Safe from any thread.</summary>
    public string? ChannelInSlot(int slot) {
        foreach (var (channelId, assigned) in this._slots) {
            if (assigned == slot) {
                return channelId;
            }
        }

        return null;
    }

    /// <summary>Call on the framework thread.</summary>
    public void AssignSlot(string channelId, int slot) {
        if (this._sessionPlayer is { } player) {
            this._config.ForCharacter(player.ContentId).AssignSlot(channelId, slot);
            this._config.Save();
            this.RefreshSlotCache();
        }
    }

    /// <summary>
    /// Configuration is only touched on the framework thread; other threads
    /// read this immutable copy of the current character's slots.
    /// </summary>
    private void RefreshSlotCache() {
        this._slots = this._sessionPlayer is { } player
            ? this._config.ForCharacter(player.ContentId).ChannelSlots.ToImmutableDictionary()
            : ImmutableDictionary<string, int>.Empty;
    }

    /// <summary>Prints a fake incoming message locally, to test chat output without the server.</summary>
    public void SimulateIncoming(string channelId, string text) {
        var snapshot = this.Snapshot;
        var channel = snapshot.FindChannel(channelId);
        var sender = new Protocol.User { UserId = -1, Name = "Simulated Sender", WorldName = Protocol.ProtocolInfo.DebugWorldName };
        this._chat.Message(new IncomingMessage(channelId, channel?.Name, sender, false, text, false, DateTimeOffset.Now), this.SlotOf(channelId));
    }

    private void OnPlayerChanged(PlayerInfo? player) {
        if (player?.ContentId == this._sessionPlayer?.ContentId && this.Session != null) {
            return;
        }

        this.Stop();
        if (player != null && this._config.AutoConnect) {
            this.StartFor(player);
        }
    }

    private void StartFor(PlayerInfo player) {
        Uri uri;
        try {
            uri = new Uri(this._config.ServerUrl);
        } catch (UriFormatException) {
            this._chat.Notice(NoticeLevel.Error, $"Invalid server URL: {this._config.ServerUrl}");
            return;
        }

        ClientSession session;
        try {
            session = new ClientSession(new ClientSessionOptions {
                ServerUri = uri,
                ClientVersion = typeof(SessionManager).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
                Log = (level, text) => {
                    if (level >= NoticeLevel.Warning) {
                        Services.Log.Warning(text);
                    } else {
                        Services.Log.Debug(text);
                    }
                },
            }, ProtectedSecretStore.For(player.ContentId, this._config.ServerUrl));
        } catch (Exception ex) {
            Services.Log.Error(ex, "Couldn't start a WonderlandChat session");
            this._chat.Notice(NoticeLevel.Error, $"Couldn't load your keys: {ex.Message}");
            return;
        }

        session.MessageReceived += message => this._chat.Message(message, this.SlotOf(message.ChannelId));
        session.Notice += this.OnNotice;
        session.SnapshotChanged += snapshot => Services.Framework.RunOnFrameworkThread(() => this.SyncSlots(snapshot));

        this._sessionPlayer = player;
        this.RefreshSlotCache();
        Volatile.Write(ref this._session, session);
        session.Start();
    }

    private void OnNotice(SessionNotice notice) {
        lock (this._noticesLock) {
            this._notices.AddLast(notice);
            while (this._notices.Count > NoticeHistory) {
                this._notices.RemoveFirst();
            }
        }

        this._chat.Notice(notice.Level, notice.Text);
    }

    private void SyncSlots(SessionSnapshot snapshot) {
        if (this._sessionPlayer is not { } player || snapshot.State != ConnectionState.Ready) {
            return;
        }

        if (this._config.ForCharacter(player.ContentId).SyncSlots(snapshot.Channels.Select(channel => channel.Id))) {
            this._config.Save();
            this.RefreshSlotCache();
        }
    }

    private void Stop() {
        var session = Interlocked.Exchange(ref this._session, null);
        this._sessionPlayer = null;
        this.RefreshSlotCache();
        if (session == null) {
            return;
        }

        // Disposing closes the socket; don't block the game on a slow network.
        if (!Task.Run(() => session.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(3))) {
            Services.Log.Warning("WonderlandChat session took too long to close");
        }
    }

    public void Dispose() {
        this._player.Changed -= this.OnPlayerChanged;
        this.Stop();
    }
}
