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
    private readonly ChatLogKeeper _chatLogs;
    private ClientSession? _session;
    private PlayerInfo? _sessionPlayer;
    private volatile ImmutableDictionary<string, int> _slots = ImmutableDictionary<string, int>.Empty;
    private volatile ImmutableDictionary<string, string> _nicknames = ImmutableDictionary<string, string>.Empty;
    private volatile ImmutableDictionary<string, ChannelColour> _colours = ImmutableDictionary<string, ChannelColour>.Empty;
    private volatile ImmutableHashSet<string> _gameChatOff = ImmutableHashSet<string>.Empty;
    private Task? _closing;
    // Framework thread only. Bumped by every start and stop, so a start that was
    // waiting for the previous session to close is dropped if anything changed meanwhile.
    private int _generation;

    public SessionManager(Configuration config, PlayerTracker player, ChatOutput chat) {
        this._config = config;
        this._player = player;
        this._chat = chat;
        // Diagnostics only: the chat log never says what was said, or who said it.
        this._chatLogs = new ChatLogKeeper(Services.PluginInterface.ConfigDirectory.FullName, ProtectedSecretStore.ChatLogProtection(),
            message => Services.Log.Warning(message));
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

    /// <summary>Whether a channel's colour is used for its whole chat line, not only its tag (see <see cref="Configuration.ColourWholeLine"/>).</summary>
    public bool ColourWholeLine => this._config.ColourWholeLine;

    /// <summary>A channel's tag in chat, as in "[sky]" or "[LGC3]" (see <see cref="ChannelTag"/>). Safe from any thread.</summary>
    public string TagOf(string channelId) => ChannelTag.For(this.SlotOf(channelId), this.NicknameOf(channelId), this._config.NicknameTags);

    /// <summary>Unread messages per channel, for the current session. Safe from any thread.</summary>
    public UnreadCounter Unread { get; } = new();

    /// <summary>
    /// What channel windows show: each channel's messages and notices since this session started, in memory only. Cleared
    /// whenever a session stops or starts (logging out, another character or server); reconnects keep it. Safe from any thread.
    /// </summary>
    public ChannelHistory History { get; } = new();

    /// <summary>
    /// The current session's chat log on this computer, while the player keeps one (<see cref="Configuration.KeepChatLog"/>):
    /// channel windows read older lines from it. Null when off or logged out. Safe from any thread.
    /// </summary>
    public ChatLog? ChatLog => this._chatLogs.Current;

    /// <summary>The character the current session is for, or null. Framework (and draw) thread.</summary>
    public PlayerInfo? SessionPlayer => this._sessionPlayer;

    /// <summary>The server address set now (the current session's, if there is one).</summary>
    public string ServerUrl => this._config.ServerUrl;

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
    /// unless they still agree. The old address's secrets files are left as they are, so switching back works; each carried
    /// character's chat log moves with it (once its log has closed), so it goes on at the new address. Call on the
    /// framework thread; the returned task finishes the copying, and fails (having copied what it could) if any copy was refused.
    /// </summary>
    public Task ChangeServer(string newUrl, ServerMoveCheck? keepIdentity = null, IReadOnlyList<ulong>? characters = null) {
        var oldUrl = this._config.ServerUrl;
        this.Stop();
        var closing = this._closing ?? Task.CompletedTask;
        // The chat log closes with the session (Stop): one carried over moves only once nothing writes to it.
        var logsClosed = this._chatLogs.Settled;
        var copying = Task.Run(async () => {
            await closing;
            await logsClosed;
            if (keepIdentity == null) {
                return;
            }

            var check = keepIdentity.IsFresh() ? keepIdentity : await ServerMove.CheckAsync(keepIdentity.CurrentUrl, keepIdentity.NewUrl);
            var refused = new List<string>();
            foreach (var contentId in characters ?? []) {
                try {
                    ServerMove.CopyIdentity(check, ProtectedSecretStore.For(contentId, oldUrl), ProtectedSecretStore.For(contentId, newUrl));
                    Services.Log.Information("Carried a character's LookingGlass identity over to the server's new address");
                    MoveChatLog(contentId, oldUrl, newUrl);
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

    /// <summary>
    /// A character's chat log goes with its identity to the server's new address (see <see cref="ChatLogFiles.MoveToAddress"/>):
    /// a log already there is left as it is. A failure is only written to the diagnostic log: the identity still moved.
    /// </summary>
    private static void MoveChatLog(ulong contentId, string oldUrl, string newUrl) {
        try {
            if (ChatLogFiles.MoveToAddress(Services.PluginInterface.ConfigDirectory.FullName, contentId, oldUrl, newUrl)) {
                Services.Log.Information("Moved a character's chat log to the server's new address");
            }
        } catch (Exception ex) {
            Services.Log.Warning($"Couldn't move a character's chat log to the server's new address: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// "Keep a chat log on this computer" on or off. On, the current session's lines from now on are kept (this character's
    /// log for this server); off, nothing more is added, and the log open is closed (its files stay until deleted). Call on
    /// the framework (or draw) thread.
    /// </summary>
    public void SetKeepChatLog(bool keep) {
        if (this._config.KeepChatLog == keep) {
            return;
        }

        this._config.KeepChatLog = keep;
        this._config.Save();
        if (keep && this.Session != null && this._sessionPlayer is { } player) {
            this.History.SetRecorder(this._chatLogs.Open(player.ContentId, this._config.ServerUrl, this._config.ChatLogMaxBytes()));
        } else if (!keep) {
            this.History.SetRecorder(null);
            this._chatLogs.Close();
        }
    }

    /// <summary>The chat log's size limit, in megabytes (kept in range). The oldest lines go at once if it is over. Framework thread.</summary>
    public void SetChatLogMegabytes(int megabytes) {
        megabytes = ChatLogLimits.ClampMegabytes(megabytes);
        if (this._config.ChatLogMegabytes == megabytes) {
            return;
        }

        this._config.ChatLogMegabytes = megabytes;
        this._config.Save();
        this._chatLogs.SetLimit(this._config.ChatLogMaxBytes());
    }

    /// <summary>
    /// "Delete my chat log": every character's chat log on this computer, for every server. If it is on, the current
    /// session's goes on afterwards, empty. The returned task fails if some of it couldn't be deleted.
    /// </summary>
    public Task DeleteChatLogs() {
        var deleting = this._chatLogs.DeleteAllAsync();
        Services.Log.Information("Deleting the chat logs on this computer");
        return deleting;
    }

    /// <summary>How much room every chat log on this computer takes. Reads the disk in the background.</summary>
    public Task<long> ChatLogSize() => this._chatLogs.SizeAsync();

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

    /// <summary>The colour (a UIColor row or a custom colour) of a channel for the current character, or null for the default. Safe from any thread.</summary>
    public ChannelColour? ColourOf(string channelId) => ChannelColours.Of(this._colours, channelId);

    /// <summary>Every channel's colour for the current character, by channel ID. Safe from any thread.</summary>
    public IReadOnlyDictionary<string, ChannelColour> Colours => this._colours;

    /// <summary>
    /// Whether a channel's messages also go to the game's chat log ("Also show in game chat"; on unless turned off). Off,
    /// they show only in its channel windows, and its notices too, but warnings. Safe from any thread.
    /// </summary>
    public bool ShowsInGameChat(string channelId) => GameChatChannels.Shows(this._gameChatOff, channelId);

    /// <summary>The channels turned off game chat, for the current character. Safe from any thread.</summary>
    public IReadOnlySet<string> GameChatOff => this._gameChatOff;

    /// <summary>Turns "Also show in game chat" on or off for a channel. Call on the framework (or draw) thread.</summary>
    public void SetShowInGameChat(string channelId, bool show) {
        if (this._sessionPlayer is { } player && GameChatChannels.Set(this._config.ForCharacter(player.ContentId).GameChatOff, channelId, show)) {
            this._config.Save();
            this.RefreshCommandCache();
        }
    }

    /// <summary>Sets a channel's colour (a UIColor row or a custom colour), or with null its default. Call on the framework thread.</summary>
    public void SetColour(string channelId, ChannelColour? colour) {
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
            this._colours = ChannelColours.Merge(settings.ChannelColours, settings.CustomChannelColours);
            this._gameChatOff = settings.GameChatOff.ToImmutableHashSet();
        } else {
            this._slots = ImmutableDictionary<string, int>.Empty;
            this._nicknames = ImmutableDictionary<string, string>.Empty;
            this._colours = ImmutableDictionary<string, ChannelColour>.Empty;
            this._gameChatOff = ImmutableHashSet<string>.Empty;
        }
    }

    /// <summary>Prints a fake incoming message locally, to test chat output without the server.</summary>
    public void SimulateIncoming(string channelId, string text) {
        var snapshot = this.Snapshot;
        var channel = snapshot.FindChannel(channelId);
        var sender = new Protocol.User { UserId = -1, Name = "Simulated Sender", WorldName = Protocol.ProtocolInfo.DebugWorldName };
        this.Deliver(new IncomingMessage(channelId, channel?.Name, sender, false, text, false, DateTimeOffset.Now));
    }

    /// <summary>
    /// Keeps a message in its channel's history (for its windows), counts it as unread, and prints it in game chat unless
    /// the channel is turned off there. From any thread.
    /// </summary>
    /// <param name="generation">The history's generation the session was started with (see <see cref="ChannelHistory.Generation"/>).</param>
    private void Deliver(IncomingMessage message, int? generation = null) {
        // Where it goes is read before checking it is still this session's: Stop starts the history's next generation
        // before it empties this list, so a message caught in a logout is dropped, never printed as if no channel were off.
        var off = this._gameChatOff;
        if (generation != null && generation != this.History.Generation) {
            return;
        }

        this.History.Add(message, generation);
        this.Unread.Add(message);
        if (GameChatChannels.Shows(off, message.ChannelId)) {
            this._chat.Message(message, this.SlotOf(message.ChannelId), this.NicknameOf(message.ChannelId), this.ColourOf(message.ChannelId));
        }
    }

    /// <summary>
    /// A channel's messages sent while the player was away (message catch-up): all of them in its history (for its windows),
    /// after a line saying how many, and counted as unread; in game chat, unless the channel is turned off there, a line
    /// saying how many and the last <see cref="CatchUpChat.GameChatCap"/> of them, each with the time it was sent. From any thread.
    /// </summary>
    private void DeliverCaughtUp(CaughtUpMessages caughtUp, int generation) {
        // As in Deliver: where it goes is read before checking it is still this session's.
        var off = this._gameChatOff;
        if (generation != this.History.Generation) {
            return;
        }

        this.History.AddCaughtUp(caughtUp, generation);
        foreach (var message in caughtUp.Messages) {
            this.Unread.Add(message);
        }

        if (!GameChatChannels.Shows(off, caughtUp.ChannelId)) {
            return;
        }

        var (slot, nickname, colour) = (this.SlotOf(caughtUp.ChannelId), this.NicknameOf(caughtUp.ChannelId), this.ColourOf(caughtUp.ChannelId));
        var tag = ChannelTag.For(slot, nickname, this._config.NicknameTags);
        var plan = CatchUpChat.Plan(tag, caughtUp.Messages, this.History.Capacity);
        this._chat.ChannelNotice(plan.Header, tag, colour);
        var now = DateTimeOffset.Now;
        foreach (var message in plan.Shown) {
            this._chat.Message(message, slot, nickname, colour, CatchUpChat.TimeLabel(message.Timestamp, now));
        }
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

        // A new session's history starts empty, and what an older one still delivers is dropped. Its lines go to its chat
        // log too, if the player keeps one: this character's for this server.
        var log = this._config.KeepChatLog ? this._chatLogs.Open(player.ContentId, this._config.ServerUrl, this._config.ChatLogMaxBytes()) : null;
        var history = this.History.Clear(log);

        // Events from a session that has since been replaced are ignored.
        session.MessageReceived += message => {
            if (this.Session == session) {
                this.Deliver(message, history);
            }
        };
        session.MessagesCaughtUp += caughtUp => {
            if (this.Session == session) {
                this.DeliverCaughtUp(caughtUp, history);
            }
        };
        session.Notice += notice => {
            if (this.Session == session) {
                this.OnNotice(notice, history);
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

    private void OnNotice(SessionNotice notice, int history) {
        lock (this._noticesLock) {
            this._notices.AddLast(notice);
            while (this._notices.Count > NoticeHistory) {
                this._notices.RemoveFirst();
            }
        }

        // One about a channel shows in its windows too; in game chat unless the channel is turned off there (a warning always).
        // The list is read before the session is checked, as in Deliver.
        var off = this._gameChatOff;
        if (history != this.History.Generation) {
            return;
        }

        this.History.AddNotice(notice, history);
        if (!GameChatChannels.NoticeToGameChat(off, notice)) {
            return;
        }

        // In the words of the mode set now: switching modes changes the words of what comes next, never whether it is shown.
        this._chat.Notice(notice.Level, notice.TextFor(this.AdvancedMode), notice.Kind);
    }

    /// <summary>Prints something to chat in the words of the mode set now.</summary>
    private void Tell(NoticeLevel level, Wording wording) => this._chat.Notice(level, wording.For(this.AdvancedMode), wording.Kind);

    private void SyncCommands(SessionSnapshot snapshot) {
        this.Unread.Retain(snapshot);
        this.History.Retain(snapshot);
        this.History.NoteNames(snapshot);

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
        // The history's next generation first, then the channel settings: see Deliver. Its chat log closes in the
        // background, once it has written what it was given.
        this.History.Clear();
        this._chatLogs.Close();
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

        // Unloading: the final save must finish before the plugin goes away, so this one may wait. So does the chat log's
        // last write (it only ever loses its last few lines if it doesn't make it).
        var closing = Task.WhenAll(this._closing ?? Task.CompletedTask, this._chatLogs.Settled);
        if (!closing.IsCompleted && !closing.Wait(TimeSpan.FromSeconds(5))) {
            Services.Log.Warning("Previous LookingGlass session is still closing");
        }
    }
}
