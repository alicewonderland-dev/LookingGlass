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

    /// <summary>
    /// Advanced mode (see <see cref="Configuration.AdvancedMode"/>): what is shown and printed uses the technical words, and
    /// fingerprints and keys show. Otherwise simple mode's words (see <see cref="Wording"/>). Read whenever something is shown,
    /// so switching takes effect at once. Safe from any thread.
    /// </summary>
    public bool AdvancedMode => this._config.AdvancedMode;

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
    /// "Reset my identity" for the logged-in character on the configured server, for a key that was lost or may have been
    /// stolen. Nothing is left or declined first: the channels stay the character's.
    /// <list type="number">
    /// <item>If the session is logged in, it asks the server to retire the old key (see <see cref="ClientSession.RetireIdentityAsync"/>),
    /// which ends every login made with it there at once. If it can't (not connected, or the server refuses or is too
    /// old), the reset goes on, and the user is told the old key and login keep working on the server until they register again.</item>
    /// <item>Then it stops the session and, once that has closed (so nothing else writes the files meanwhile), gives the
    /// address new keys and removes the old identity from every other file that holds it (see <see cref="ServerSecretFiles.ResetIdentity"/>).</item>
    /// <item>Then it connects again, which leads to registering the new keys through the Lodestone. That brings the
    /// character's channels, ranks and invites along to the new keys (and retires the old key, if the server wasn't told
    /// before), and their members are told.</item>
    /// </list>
    /// Call on the framework thread; the returned task finishes the work.
    /// </summary>
    public Task ResetIdentity() {
        if (this._player.Current is not { } player) {
            return Task.FromException(new InvalidOperationException("Log in to the character whose identity you want to reset."));
        }

        var serverUrl = this._config.ServerUrl;
        // This character's session for this address, if any: only it holds a login to sign the request with.
        var session = this._sessionPlayer?.ContentId == player.ContentId ? this.Session : null;
        return Task.Run(async () => {
            var notRetired = await Retire(session);
            IdentityReset reset;
            try {
                reset = await await Services.Framework.RunOnFrameworkThread<Task<IdentityReset>>(() =>
                    this.ReplaceSecrets(player, () => ProtectedSecretStore.ResetIdentity(player.ContentId, serverUrl)));
            } catch (Exception ex) when (notRetired == null) {
                // The old key is already gone on the server, so the files still holding it are of no use there now.
                this.Tell(NoticeLevel.Warning, PlainMessages.IdentityResetFailed(serverUrl, ex.Message));
                throw;
            }

            Services.Log.Information($"Reset the LookingGlass identity of a character ({reset.Scrubbed.Count} other files held the old one)");

            if (notRetired == null) {
                this.Tell(NoticeLevel.Info, PlainMessages.IdentityReset(serverUrl));
            } else {
                this.Tell(NoticeLevel.Warning, PlainMessages.IdentityResetNotRetired(serverUrl, notRetired));
            }

            foreach (var problem in reset.Problems) {
                this._chat.Notice(NoticeLevel.Warning, $"{problem} It may still hold your old identity.");
            }
        });
    }

    /// <summary>Asks the server to retire the session's identity key, if it is logged in.</summary>
    /// <returns>Null if it did; otherwise why not, for the user.</returns>
    private static async Task<string?> Retire(ClientSession? session) {
        if (session?.Snapshot.State != ConnectionState.Ready) {
            return "you weren't connected and logged in";
        }

        try {
            await session.RetireIdentityAsync();
            return null;
        } catch (ServerErrorException ex) when (ex.Code == Protocol.ErrorCode.InvalidRequest) {
            Services.Log.Warning($"The server refused to retire an identity key: {ex.ServerMessage}");
            return $"it doesn't know how yet, or refused: \"{ex.ServerMessage}\"";
        } catch (Exception ex) {
            Services.Log.Warning(ex, "Couldn't ask the server to retire an identity key");
            return ex is ServerErrorException refused ? $"it said: \"{refused.ServerMessage}\"" : ex.Message;
        }
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
            return true;
        });
    }

    /// <summary>
    /// Stops the session, runs <paramref name="work"/> on a character's secrets files once it has closed (so nothing
    /// else writes them meanwhile), then connects again for the character logged in now (normally <paramref name="player"/>).
    /// Call on the framework thread; the returned task finishes the work.
    /// </summary>
    private Task<T> ReplaceSecrets<T>(PlayerInfo player, Func<T> work) {
        this.Stop();
        var closing = this._closing ?? Task.CompletedTask;
        var replacing = Task.Run(async () => {
            await closing;
            return work();
        });

        // Any session started meanwhile (this one below, a relog, Connect) waits for it, as for a closing session.
        this._closing = replacing.ContinueWith(_ => { }, TaskScheduler.Default);
        if (this._player.Current is { } current) {
            this.StartFor(current);
        }

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
            this.Tell(NoticeLevel.Error, PlainMessages.CouldntLoadKeys(ex.Message));
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

        // In the words of the mode set now: switching modes changes the words of what comes next, never whether it is shown.
        this._chat.Notice(notice.Level, notice.TextFor(this.AdvancedMode), notice.Kind);
    }

    /// <summary>Prints something to chat in the words of the mode set now.</summary>
    private void Tell(NoticeLevel level, Wording wording) => this._chat.Notice(level, wording.For(this.AdvancedMode), wording.Kind);

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
