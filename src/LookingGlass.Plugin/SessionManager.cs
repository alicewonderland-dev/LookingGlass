using System.Collections.Immutable;
using LookingGlass.Core.Client;

namespace LookingGlass.Plugin;

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
    private volatile ImmutableDictionary<string, string> _nicknames = ImmutableDictionary<string, string>.Empty;
    private volatile ImmutableDictionary<string, ushort> _colours = ImmutableDictionary<string, ushort>.Empty;
    private Task? _closing;
    // Framework thread only. Bumped by every start and stop, so a start that was
    // waiting for the previous session to close is dropped if anything changed meanwhile.
    private int _generation;

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

    /// <summary>Unread messages per channel, for the current session. Safe from any thread.</summary>
    public UnreadCounter Unread { get; } = new();

    public IReadOnlyList<SessionNotice> RecentNotices {
        get {
            lock (this._noticesLock) {
                return this._notices.ToList();
            }
        }
    }

    /// <summary>
    /// Changes the server address and reconnects. With <paramref name="keepIdentity"/> (a check the current server
    /// vouched for, which the user confirmed), first copies each of <paramref name="characters"/>' identity to the new
    /// address (see <see cref="ServerMove"/>), once the session has closed; any session started meanwhile waits for that.
    /// If closing took long enough for the check to go stale, the servers are asked again first, and nothing is copied
    /// unless they still agree. The old address's files are left as they are, so switching back works. Call on the
    /// framework thread; the returned task finishes the copying, and fails (having copied what it could) if any copy was refused.
    /// </summary>
    public Task ChangeServer(string newUrl, ServerMoveCheck? keepIdentity = null, IReadOnlyList<ulong>? characters = null) {
        var oldUrl = this._config.ServerUrl;
        this.Stop();
        var closing = this._closing ?? Task.CompletedTask;
        var copying = Task.Run(async () => {
            await closing;
            if (keepIdentity == null) {
                return;
            }

            var check = keepIdentity.IsFresh() ? keepIdentity : await ServerMove.CheckAsync(keepIdentity.CurrentUrl, keepIdentity.NewUrl);
            var refused = new List<string>();
            foreach (var contentId in characters ?? []) {
                try {
                    ServerMove.CopyIdentity(check, ProtectedSecretStore.For(contentId, oldUrl), ProtectedSecretStore.For(contentId, newUrl));
                    Services.Log.Information("Carried a character's LookingGlass identity over to the server's new address");
                } catch (Exception ex) {
                    refused.Add(ex.Message);
                }
            }

            if (refused.Count > 0) {
                throw new InvalidOperationException(string.Join(" ", refused.Distinct()));
            }
        });

        this._closing = copying.ContinueWith(_ => { }, TaskScheduler.Default);
        this._config.ServerUrl = newUrl;
        this._config.Save();
        if (this._player.Current is { } player && this._config.AutoConnect) {
            this.StartFor(player);
        }

        return copying;
    }

    public void Connect() {
        if (this.Session == null && this._player.Current is { } player) {
            this.StartFor(player);
        }
    }

    public void Disconnect() => this.Stop();

    /// <summary>
    /// "Reset my identity" for the logged-in character on the configured server (see <see cref="ClientSecrets.ResetIdentity"/>):
    /// stops the session, replaces the keys once it has closed (so nothing else writes the secrets file meanwhile), then
    /// connects again, which leads to registering. Call on the framework thread; the returned task finishes the work.
    /// </summary>
    public Task ResetIdentity() {
        if (this._player.Current is not { } player) {
            return Task.FromException(new InvalidOperationException("Log in to the character whose identity you want to reset."));
        }

        var serverUrl = this._config.ServerUrl;
        return this.ReplaceSecrets(player, () => {
            var store = ProtectedSecretStore.For(player.ContentId, serverUrl, warning => this._chat.Notice(NoticeLevel.Warning, warning));
            var secrets = store.Load();
            secrets.ResetIdentity();
            store.Save(secrets);
            Services.Log.Information("Reset the LookingGlass identity of a character");
        });
    }

    /// <summary>
    /// Restores the backup of the logged-in character's identity for the configured server (see
    /// <see cref="ProtectedSecretStore.FindBackup"/>), once the session has closed, then connects again. Only when the
    /// user asked. Call on the framework thread; the returned task finishes the work.
    /// </summary>
    public Task RestoreBackup() {
        if (this._player.Current is not { } player) {
            return Task.FromException(new InvalidOperationException("Log in to the character whose identity you want to restore."));
        }

        var serverUrl = this._config.ServerUrl;
        return this.ReplaceSecrets(player, () => {
            ProtectedSecretStore.RestoreBackup(player.ContentId, serverUrl);
            Services.Log.Information("Restored the LookingGlass identity of a character from a backup");
        });
    }

    /// <summary>
    /// Stops the session, runs <paramref name="work"/> on a character's secrets files once it has closed (so nothing
    /// else writes them meanwhile), then connects again. Call on the framework thread; the returned task finishes the work.
    /// </summary>
    private Task ReplaceSecrets(PlayerInfo player, Action work) {
        this.Stop();
        var closing = this._closing ?? Task.CompletedTask;
        var replacing = Task.Run(async () => {
            await closing;
            work();
        });

        // Any session started meanwhile (this one below, a relog, Connect) waits for it, as for a closing session.
        this._closing = replacing.ContinueWith(_ => { }, TaskScheduler.Default);
        this.StartFor(player);
        return replacing;
    }

    /// <summary>The command slot of a channel for the current character. Safe from any thread.</summary>
    public int? SlotOf(string channelId) {
        return this._slots.TryGetValue(channelId, out var slot) ? slot : null;
    }

    /// <summary>The current character's command slots (channel ID → slot). Safe from any thread.</summary>
    public IReadOnlyDictionary<string, int> Slots => this._slots;

    /// <summary>The current character's channel nicknames (channel ID → nickname). Safe from any thread.</summary>
    public IReadOnlyDictionary<string, string> Nicknames => this._nicknames;

    /// <summary>The nickname of a channel for the current character. Safe from any thread.</summary>
    public string? NicknameOf(string channelId) {
        return this._nicknames.TryGetValue(channelId, out var nickname) ? nickname : null;
    }

    /// <summary>The colour (a UIColor row) of a channel for the current character, or null for the default. Safe from any thread.</summary>
    public ushort? ColourOf(string channelId) => ChannelColours.Of(this._colours, channelId);

    /// <summary>Sets a channel's colour (a UIColor row), or with null its default. Call on the framework thread.</summary>
    public void SetColour(string channelId, ushort? colour) {
        if (this._sessionPlayer is { } player) {
            this._config.ForCharacter(player.ContentId).SetColour(channelId, colour);
            this._config.Save();
            this.RefreshCommandCache();
        }
    }

    /// <summary>Call on the framework thread.</summary>
    public void AssignSlot(string channelId, int slot) {
        if (this._sessionPlayer is { } player) {
            this._config.ForCharacter(player.ContentId).AssignSlot(channelId, slot);
            this._config.Save();
            this.RefreshCommandCache();
        }
    }

    /// <summary>Sets or (with an empty one) clears a channel's nickname. Call on the framework thread.</summary>
    /// <returns>Why it was refused, or null.</returns>
    public string? SetNickname(string channelId, string nickname) {
        if (this._sessionPlayer is not { } player) {
            return "Not connected.";
        }

        var error = this._config.ForCharacter(player.ContentId).SetNickname(channelId, nickname);
        if (error == null) {
            this._config.Save();
            this.RefreshCommandCache();
        }

        return error;
    }

    /// <summary>
    /// Configuration is only touched on the framework thread; other threads read
    /// these immutable copies of the current character's slots, nicknames and colours.
    /// </summary>
    private void RefreshCommandCache() {
        if (this._sessionPlayer is { } player) {
            var settings = this._config.ForCharacter(player.ContentId);
            this._slots = settings.ChannelSlots.ToImmutableDictionary();
            this._nicknames = settings.Nicknames.ToImmutableDictionary();
            this._colours = settings.ChannelColours.ToImmutableDictionary();
        } else {
            this._slots = ImmutableDictionary<string, int>.Empty;
            this._nicknames = ImmutableDictionary<string, string>.Empty;
            this._colours = ImmutableDictionary<string, ushort>.Empty;
        }
    }

    /// <summary>Prints a fake incoming message locally, to test chat output without the server.</summary>
    public void SimulateIncoming(string channelId, string text) {
        var snapshot = this.Snapshot;
        var channel = snapshot.FindChannel(channelId);
        var sender = new Protocol.User { UserId = -1, Name = "Simulated Sender", WorldName = Protocol.ProtocolInfo.DebugWorldName };
        this.Deliver(new IncomingMessage(channelId, channel?.Name, sender, false, text, false, DateTimeOffset.Now));
    }

    /// <summary>Prints a message and counts it as unread. From any thread.</summary>
    private void Deliver(IncomingMessage message) {
        this.Unread.Add(message);
        this._chat.Message(message, this.SlotOf(message.ChannelId), this.NicknameOf(message.ChannelId), this.ColourOf(message.ChannelId));
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

    /// <summary>Call on the framework thread.</summary>
    private void StartFor(PlayerInfo player) {
        var generation = ++this._generation;
        if (this._closing is { IsCompleted: false } closing) {
            // A quick relog: the old session is still making its final save to the secrets file.
            // Start once it's done, back on the framework thread, without blocking the game meanwhile.
            _ = closing.ContinueWith(_ => Services.Framework.RunOnFrameworkThread(() => {
                if (this._generation == generation) {
                    this.StartNow(player);
                }
            }), TaskScheduler.Default);
            return;
        }

        this.StartNow(player);
    }

    private void StartNow(PlayerInfo player) {
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
                // Diagnostics only: the session keeps user content (names, messages) out of these.
                Log = (level, text) => {
                    if (level >= NoticeLevel.Warning) {
                        Services.Log.Warning(text);
                    } else {
                        Services.Log.Debug(text);
                    }
                },
            }, ProtectedSecretStore.For(player.ContentId, this._config.ServerUrl, warning => this._chat.Notice(NoticeLevel.Warning, warning)));
        } catch (Exception ex) {
            Services.Log.Error(ex, "Couldn't start a LookingGlass session");
            this._chat.Notice(NoticeLevel.Error, $"Couldn't load your keys: {ex.Message}");
            return;
        }

        // Events from a session that has since been replaced are ignored.
        session.MessageReceived += message => {
            if (this.Session == session) {
                this.Deliver(message);
            }
        };
        session.Notice += notice => {
            if (this.Session == session) {
                this.OnNotice(notice);
            }
        };
        // Snapshots can arrive out of order; always sync against the latest one of the current session.
        session.SnapshotChanged += _ => Services.Framework.RunOnFrameworkThread(() => {
            if (this.Session == session) {
                this.SyncCommands(session.Snapshot);
            }
        });

        this._sessionPlayer = player;
        this.RefreshCommandCache();
        // A new session (relog, another character or server) counts from zero; reconnects keep counting.
        this.Unread.Reset();
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

    private void SyncCommands(SessionSnapshot snapshot) {
        this.Unread.Retain(snapshot);

        // Only against the complete channel list; a partial one would free (and then hand out) slots in use, and drop nicknames.
        if (this._sessionPlayer is not { } player || snapshot is not { State: ConnectionState.Ready, ChannelsLoaded: true }) {
            return;
        }

        if (this._config.ForCharacter(player.ContentId).Sync(snapshot)) {
            this._config.Save();
            this.RefreshCommandCache();
        }
    }

    /// <summary>
    /// Stops the current session, and any start still waiting. Closing happens in
    /// the background so logging out never stalls the game; the next session
    /// starts only once it's done (see <see cref="StartFor"/>), so the two never
    /// write the same secrets file at once. Call on the framework thread.
    /// </summary>
    private void Stop() {
        this._generation++;
        var session = Interlocked.Exchange(ref this._session, null);
        this._sessionPlayer = null;
        this.RefreshCommandCache();
        this.Unread.Reset();
        if (session == null) {
            return;
        }

        var previous = this._closing ?? Task.CompletedTask;
        this._closing = Task.Run(async () => {
            try {
                await previous;
                await session.DisposeAsync();
            } catch (Exception ex) {
                Services.Log.Warning(ex, "Error closing LookingGlass session");
            }
        });
    }

    public void Dispose() {
        this._player.Changed -= this.OnPlayerChanged;
        this.Stop();

        // Unloading: the final save must finish before the plugin goes away, so this one may wait.
        if (this._closing is { IsCompleted: false } closing && !closing.Wait(TimeSpan.FromSeconds(5))) {
            Services.Log.Warning("Previous LookingGlass session is still closing");
        }
    }
}
