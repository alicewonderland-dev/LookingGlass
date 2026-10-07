using Google.Protobuf;
using LookingGlass.Core.Crypto;
using LookingGlass.Core.Membership;
using LookingGlass.Protocol;

namespace LookingGlass.Core.Client;

/// <summary>
/// Message catch-up (capability "history.v1"): the messages sent while this client was disconnected, fetched from the server
/// when it logs in again, and checked and shown as live ones are. See "Message catch-up" in docs/design.md.
///
/// <list type="bullet">
/// <item><b>Order.</b> From before the login (the server relays to a connection as soon as it is logged in), live messages
/// are held back; each channel's missed messages are fetched after the last one this client had
/// (<see cref="ClientSecrets.LastMessageIds"/>), in the server's order, page by page; then that channel's held messages are
/// taken. All of it runs in the inbox, one thing at a time, with the events, so nothing races: a message delivered both
/// live and caught up is taken once (by its signed message ID), and a channel's messages are taken in order.</item>
/// <item><b>Checks.</b> Each is checked as a live message (sender, signature, the key of its epoch), but against the keys
/// its sender had when its key was made (someone who has left since, too: <see cref="ClientSecrets.FormerMembers"/>), and
/// dated no later than that key stopped being the channel's (the first membership change after it, give or take the live
/// grace: <see cref="UsableUntil"/>), so keys someone held then can't speak after they left or their place moved. Instead
/// of the live rules on age (which an older message fails), it must be newer than everything already accepted from its
/// sender in the channel (persisted, with those messages' IDs), and not dated in the future. So each is accepted at most
/// once, even after a restart, and none can be passed off as new: it is marked as caught up, with the time it was sent.
/// Anything else is dropped; a channel's drops are told in one notice.</item>
/// <item><b>Failures.</b> A channel whose catch-up fails is tried again a few times while its live messages wait. If it
/// still fails, they are taken, and the channel is marked as having a gap (<see cref="ClientSecrets.CatchUpGaps"/>): its
/// position doesn't move on, and its missed messages are judged against what was had before the gap, so the next login
/// fetches them. If the connection closed instead, its held live messages are let go: the server has them, and the next
/// login fetches them in order.</item>
/// <item><b>Keys.</b> A key of an epoch this client missed (the channel was rekeyed while it was away) is fetched for reading
/// only, kept in memory (<see cref="_pastKeys"/>), and never sent with.</item>
/// </list>
/// </summary>
public sealed partial class ClientSession {
    // Pages one channel's catch-up fetches at most (a page holds at least 24 of the longest messages).
    private const int MaxCatchUpPagesPerChannel = 250;

    // Tries of one channel's catch-up within a login, waiting longer each time.
    private const int CatchUpAttempts = 3;

    // Past epoch keys kept per channel, for reading caught-up messages.
    private const int MaxPastKeysPerChannel = 64;

    // Message IDs kept per sender at their newest time.
    private const int MaxNewestIds = 16;

    // Membership changes kept per channel at most.
    private const int MaxMembershipChanges = 500;

    // ---- state guarded by _lock
    // The server agreed to message catch-up on the current connection.
    private bool _catchUpAgreed;
    // The catch-up of the current login, while live messages are held back for it.
    private CatchUpState? _catchUp;
    // Channel ID → epoch → keys of epochs this client missed, fetched to read caught-up messages only (never to send with).
    private readonly Dictionary<string, Dictionary<ulong, HeldKey>> _pastKeys = new();
    // A save of the message times and positions is due soon (see ScheduleReplaySave).
    private bool _replaySaveScheduled;
    // When this client last began logging in (Unix ms): a key made after it says only when this client came back.
    private long _loginStartedMs = long.MaxValue;
    // Logging in, until the channel list and logs are in: what is verified meanwhile wasn't seen as it happened.
    private bool _loginSyncing;
    // ----

    /// <summary>
    /// Raised on a background thread, once per channel each time this client comes back, with the messages sent there while it
    /// was disconnected (oldest first), each checked and accepted once. They don't go through <see cref="MessageReceived"/>.
    /// </summary>
    public event Action<CaughtUpMessages>? MessagesCaughtUp;

    /// <summary>A server event, or work that must run in order with them (see <see cref="RunInInboxAsync"/>).</summary>
    private sealed record InboxItem(Event? Event, Func<CancellationToken, Task>? Work);

    /// <summary>an epoch key, the log position it was made for (if known), and when its author (<c>CreatedBy</c>) says they made it (0: not said).</summary>
    private sealed record HeldKey(byte[] Key, LogPosition? Position, long CreatedMs, long CreatedBy);

    /// <summary>The live messages held back for one login's catch-up, and the channels done. Guarded by <see cref="_lock"/>.</summary>
    private sealed class CatchUpState(Connection connection) {
        public Connection Connection { get; } = connection;

        /// <summary>The channels to catch up on (those this client was a member of when it started).</summary>
        public HashSet<string> Pending { get; } = new();

        /// <summary>Channels whose catch-up is over: their live messages are taken as they come.</summary>
        public HashSet<string> Released { get; } = new();

        /// <summary>Live messages held back, in the order they arrived.</summary>
        public List<ChatMessage> Held { get; } = new();

        /// <summary>The catch-up is over: nothing is held back any more.</summary>
        public bool Over { get; set; }
    }

    /// <summary>One channel's catch-up: what was accepted, how many were dropped, and the epochs whose keys were looked for.</summary>
    private sealed class CatchUpBatch(string channelId, Connection connection) {
        public string ChannelId { get; } = channelId;
        public Connection Connection { get; } = connection;
        public List<IncomingMessage> Accepted { get; } = new();
        public int Dropped { get; set; }

        /// <summary>Senders whose messages were left out only because their keys stopped (how) with no time for it they couldn't choose.</summary>
        public Dictionary<long, MembershipEntryKind> Unconfirmed { get; } = new();
        public int UnconfirmedCount { get; set; }
        public HashSet<ulong> KeysLookedFor { get; } = new();
    }

    /// <summary>
    /// Starts holding back live messages for a login on <paramref name="connection"/>, if the server agreed to message
    /// catch-up. A catch-up of an earlier login still under way takes what it held itself when it ends.
    /// </summary>
    /// <returns>The catch-up to start once logged in, or null if there is none.</returns>
    private CatchUpState? HoldLiveMessages(Connection connection) {
        lock (this._lock) {
            if (!this._catchUpAgreed || connection != this._connection) {
                return null;
            }

            this._catchUp = new CatchUpState(connection);
            return this._catchUp;
        }
    }

    /// <summary>Holds a live message back while its channel's missed messages are still to come. Called in the inbox.</summary>
    /// <returns>True if it was held back (it is taken when its channel's catch-up ends).</returns>
    private bool HeldBack(ChatMessage message) {
        lock (this._lock) {
            if (this._catchUp is not { Over: false } state || state.Released.Contains(message.ChannelId)) {
                return false;
            }

            if (state.Held.Count >= this._options.MaxHeldLiveMessages) {
                // Too many to hold: taken as they come, and its missed messages are judged against what was had before,
                // so none of them is taken for one older than what came live meanwhile.
                this.MarkGap(message.ChannelId);
                return false;
            }

            state.Held.Add(message);
            return true;
        }
    }

    /// <summary>A login that didn't happen (or a refresh that failed): what it held back is taken, in the inbox.</summary>
    private void ReleaseLater(CatchUpState? state) {
        if (state != null) {
            _ = this.RunInInboxAsync(ct => this.ReleaseAllAsync(state, ct));
        }
    }

    /// <summary>Catches up on every channel in the background, after a login (see the class summary).</summary>
    private void StartCatchUp(Connection connection, CatchUpState? state) {
        if (state != null) {
            this.RunBackground(Wording.Same("Catching up on missed messages"), ct => this.CatchUpAsync(connection, state, ct));
        }
    }

    private async Task CatchUpAsync(Connection connection, CatchUpState state, CancellationToken ct) {
        try {
            var channels = this.Read(() => {
                var members = this._channels.Keys.Where(this.IsMember).Order(StringComparer.Ordinal).ToList();
                state.Pending.UnionWith(members);
                return members;
            });
            foreach (var channelId in channels) {
                if (Closed(connection)) {
                    break;
                }

                await this.CatchUpChannelAsync(connection, state, channelId, ct);
            }
        } finally {
            // Whatever happened, the live messages held back are dealt with now (see ReleaseAllAsync).
            try {
                await this.RunInInboxAsync(inboxCt => this.ReleaseAllAsync(state, inboxCt)).WaitAsync(ct);
            } catch (OperationCanceledException) {
                // Stopping.
            }

            Interlocked.Increment(ref this._catchUpsDone);
        }
    }

    private int _catchUpsDone;

    /// <summary>How many logins' catch-ups are over (whatever came of them), for tests to wait on.</summary>
    internal int CatchUpsDone => Volatile.Read(ref this._catchUpsDone);

    private bool Closed(Connection connection) => connection.Closed.IsCompleted || connection != this._connection;

    /// <summary>
    /// One channel: its missed messages, page by page, tried again a few times if that fails (its live messages wait), then
    /// its live messages held back meanwhile.
    /// </summary>
    private async Task CatchUpChannelAsync(Connection connection, CatchUpState state, string channelId, CancellationToken ct) {
        var batch = new CatchUpBatch(channelId, connection);
        var complete = false;
        try {
            for (var attempt = 1; ; attempt++) {
                try {
                    await this.FetchMissedAsync(batch, ct);
                    complete = true;
                    break;
                } catch (Exception ex) when (ex is not OperationCanceledException) {
                    // From where it got to: what was taken stays taken.
                    this.Log(NoticeLevel.Warning, $"Couldn't catch up on missed messages in {channelId} (try {attempt}): {ex.Message}");
                    if (attempt >= CatchUpAttempts || Closed(connection)) {
                        break;
                    }

                    await Task.Delay(this._options.CatchUpRetryDelay * (1 << (attempt - 1)), ct);
                }
            }
        } finally {
            await this.RunInInboxAsync(inboxCt => this.FinishChannelAsync(state, batch, complete, inboxCt)).WaitAsync(ct);
        }
    }

    /// <summary>The channel's missed messages, from its position, page by page, each taken in the inbox.</summary>
    private async Task FetchMissedAsync(CatchUpBatch batch, CancellationToken ct) {
        var channelId = batch.ChannelId;
        var after = this.Read(() => this._secrets.LastMessageIds.GetValueOrDefault(channelId));
        // Without a position (the first login with this version, a new computer, a channel just joined): the last while.
        var within = after == 0 ? (uint) Math.Clamp(this._options.CatchUpWithoutPosition.TotalSeconds, 0, uint.MaxValue) : 0u;
        for (var pages = 0; pages < MaxCatchUpPagesPerChannel; pages++) {
            var page = await this.FetchStoredMessagesAsync(batch.Connection, channelId, after, within, ct);
            // Only what the server says comes after the position, in order: anything else is the server's mistake (or worse),
            // and is left out rather than trusted. (Nothing is accepted for its number, only by its own checks.)
            var messages = new List<ChatMessage>();
            foreach (var message in page.Messages) {
                if (message.ChannelId == channelId && message.ServerId > (messages.Count == 0 ? after : messages[^1].ServerId)) {
                    messages.Add(message);
                }
            }

            await this.RunInInboxAsync(async inboxCt => {
                foreach (var message in messages) {
                    await this.TakeCaughtUpAsync(message, batch, inboxCt);
                }
            }).WaitAsync(ct);

            if (messages.Count > 0) {
                after = messages[^1].ServerId;
            }

            if (!page.More || messages.Count == 0) {
                if (!page.More) {
                    lock (this._lock) {
                        if (page.LatestId > after) {
                            // Nothing more this client may read: what it skips up to the newest is its own, or from before it joined.
                            this.NoteServerId(channelId, page.LatestId);
                        } else if (page.LatestId < after) {
                            // The server's numbers went back (its database was restored from a backup, say): carry on from its
                            // newest, or what it numbers next would be taken for what was had already.
                            this._secrets.LastMessageIds[channelId] = page.LatestId;
                            this.ReplayStateChanged();
                        }
                    }
                }

                return;
            }
        }
    }

    /// <summary>A page of a channel's stored messages, waiting and asking again (a few times) if the server says to slow down.</summary>
    private async Task<StoredMessages> FetchStoredMessagesAsync(Connection connection, string channelId, ulong after, uint within, CancellationToken ct) {
        var frame = new ClientFrame { FetchMessages = new FetchMessages { ChannelId = channelId, AfterId = after, WithinSeconds = within } };
        for (var attempt = 0; ; attempt++) {
            try {
                var response = await this.RequestAsync(connection, frame, ct);
                return response.StoredMessages ?? throw Unexpected(response);
            } catch (ServerErrorException ex) when (ex.Code == ErrorCode.RateLimited && attempt < 5) {
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct);
            }
        }
    }

    /// <summary>
    /// Checks one caught-up message and, if it passes, adds it to the channel's batch (see the class summary). Called in the
    /// inbox. Throws only if a key couldn't be fetched (the connection dropped, say): the position stays before it then.
    /// </summary>
    private async Task TakeCaughtUpAsync(ChatMessage message, CatchUpBatch batch, CancellationToken ct) {
        var channelId = batch.ChannelId;
        var messageId = Convert.ToHexString(message.MessageId.Span);
        var (member, skip) = this.Read(() => (this.IsMember(channelId),
            this.IsBlocked(message.SenderId) || this._seenMessages.Contains(messageId)
            || this.AlreadyHad(channelId, message.SenderId, message.TimestampUnixMs, messageId)));
        if (!member) {
            // No longer in it (removed meanwhile): nothing of it is shown any more.
            return;
        }

        if (skip) {
            // From someone blocked (hidden, as live), or had already (this session, or by its time and ID): before any other
            // check, so what was received live before doesn't count as dropped.
            this.Noted(channelId, message);
            return;
        }

        var key = this.Read(() => this.KeyFor(channelId, message.Epoch));
        if (key == null && !batch.KeysLookedFor.Contains(message.Epoch)) {
            await this.FetchPastKeysAsync(batch.Connection, channelId, message.Epoch, ct);
            // Only once fetched: a fetch that failed is made again when the catch-up is tried again.
            batch.KeysLookedFor.Add(message.Epoch);
            key = this.Read(() => this.KeyFor(channelId, message.Epoch));
        }

        // The next epoch's key says, signed by whoever made it, when this one stopped being the channel's.
        var next = message.Epoch + 1;
        if (key != null && !batch.KeysLookedFor.Contains(next)
            && this.Read(() => this.KeyEpochOf(channelId) is { } newest && newest >= next && this.KeyFor(channelId, next) == null)) {
            await this.FetchPastKeysAsync(batch.Connection, channelId, next, ct);
            batch.KeysLookedFor.Add(next);
        }

        // Signed by the keys the sender had when the key it is under was made (someone who left since, too, if it was made
        // before they left), not whatever the server says their keys are now; and dated no later than that key stopped being
        // the channel's, so keys someone held then can't speak after they left, or after their place moved to new keys.
        var (signer, (until, unconfirmed)) = key == null
            ? (null, (long.MinValue, (MembershipEntryKind?) null))
            : this.Read(() => (this.SignerFor(channelId, message.SenderId, key.Position), this.UsableUntil(channelId, message.Epoch, key.Position, message.SenderId)));
        var content = signer == null ? null : this._groupKeys.DecryptMessage(message, key!.Key, signer.SigningPublicKey);
        var now = this._options.TimeProvider.GetUtcNow();
        if (content == null || message.TimestampUnixMs > until
            || message.TimestampUnixMs > (now + MaxMessageClockSkew).ToUnixTimeMilliseconds()) {
            // Under a key this client never held (from before it joined, say), from someone who couldn't send it then, not
            // really theirs, dated after its key was replaced, or dated further in the future than a live message may be.
            batch.Dropped++;
            this.Noted(channelId, message);
            return;
        }

        if (unconfirmed is { } how) {
            // Genuine as far as can be told, but the sender's keys stopped (they left, were removed, set up again) with nothing to
            // tell when that neither they nor the server chose: left out, and said plainly, not as a failed check.
            batch.Unconfirmed[message.SenderId] = how;
            batch.UnconfirmedCount++;
            this.Noted(channelId, message);
            return;
        }

        string? channelName;
        User sender;
        bool isOwn;
        lock (this._lock) {
            this.NoteServerId(channelId, message.ServerId);
            // Each is accepted once: never one this client already has, or older than what it had from the sender (before
            // the channel's gap, if its last catch-up failed), which the server, sending in its order, never sends but again.
            if (this.AlreadyHad(channelId, message.SenderId, message.TimestampUnixMs, messageId) || !this.MarkSeen(messageId)) {
                return;
            }

            this.Accepted(channelId, message.SenderId, message.TimestampUnixMs, messageId);
            channelName = this._channels.GetValueOrDefault(channelId)?.Name;
            sender = this.UserOf(message.SenderId);
            isOwn = message.SenderId == this._me?.UserId;
        }

        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(message.TimestampUnixMs);
        batch.Accepted.Add(MessageContent.Decode(content) is { } text
            ? new IncomingMessage(channelId, channelName, sender, isOwn, text.Text, false, timestamp) { Links = text.Links, CaughtUp = true }
            : new IncomingMessage(channelId, channelName, sender, isOwn, null, true, timestamp) { CaughtUp = true });
    }

    /// <summary>A caught-up message was had, whatever came of it: the next catch-up carries on after it.</summary>
    private void Noted(string channelId, ChatMessage message) {
        lock (this._lock) {
            this.NoteServerId(channelId, message.ServerId);
        }
    }

    /// <summary>
    /// A channel's catch-up is over: its messages go to <see cref="MessagesCaughtUp"/>, its drops are told once, and its live
    /// messages held back meanwhile are dealt with (see the class summary). Called in the inbox.
    /// </summary>
    private async Task FinishChannelAsync(CatchUpState state, CatchUpBatch batch, bool complete, CancellationToken ct) {
        var channelId = batch.ChannelId;
        var (name, display) = this.Read(() => (this._channels.GetValueOrDefault(channelId)?.Name, this._channels.GetValueOrDefault(channelId)?.DisplayName));
        if (batch.Accepted.Count > 0) {
            this.InvokeSafely(this.MessagesCaughtUp,
                new CaughtUpMessages(channelId, name, batch.Accepted.Select(message => message with { Sender = Shown(message.Sender) }).ToList()));
        }

        if (batch.Dropped > 0) {
            this.RaiseNotice(NoticeLevel.Warning, PlainMessages.MessagesNotCaughtUp(display ?? ChannelView.PlaceholderName(channelId), batch.Dropped), channelId);
        }

        if (batch.Unconfirmed.Count > 0) {
            // Not a warning: nothing failed a check. Who, but nothing of what they said, in the diagnostic log.
            this.Log(NoticeLevel.Info, $"{batch.UnconfirmedCount} caught-up messages in {channelId}, from {batch.Unconfirmed.Count} senders whose keys have stopped since, couldn't be dated independently and were left out");
            var senders = this.Read(() => batch.Unconfirmed.Select(pair => (Who: Shown(this.UserOf(pair.Key)) is var user ? $"{user.Name}@{user.WorldName}" : "", How: pair.Value)).ToList());
            this.RaiseNotice(NoticeLevel.Info, PlainMessages.MessagesNotConfirmed(display ?? ChannelView.PlaceholderName(channelId), senders), channelId);
        }

        List<ChatMessage> held;
        lock (this._lock) {
            state.Released.Add(channelId);
            held = state.Held.Where(message => message.ChannelId == channelId).ToList();
            state.Held.RemoveAll(message => message.ChannelId == channelId);
            if (complete) {
                // Everything up to its position is in: from here on, live messages move it on again.
                if (this._secrets.CatchUpGaps.Remove(channelId)) {
                    this.ReplayStateChanged();
                }
            } else if (Closed(batch.Connection)) {
                // The server has them, and the next login fetches them, in order, after what was missed.
                held.Clear();
            } else {
                // Taken now, but the position stays before what was missed, which the next login fetches.
                this.MarkGap(channelId);
            }
        }

        await this.TakeHeldAsync(held, ct);
        // The position and message times now, so a crash doesn't show these again as missed.
        this.SaveSecrets();
    }

    /// <summary>
    /// The login's catch-up is over: the live messages still held back are taken, in the order they came, but for those of
    /// channels it didn't get to because the connection closed (the next login fetches them). Called in the inbox.
    /// </summary>
    private async Task ReleaseAllAsync(CatchUpState state, CancellationToken ct) {
        List<ChatMessage> held;
        lock (this._lock) {
            state.Over = true;
            var closed = Closed(state.Connection);
            var notDone = state.Pending.Except(state.Released).ToHashSet();
            if (!closed) {
                foreach (var channelId in notDone) {
                    this.MarkGap(channelId);
                }
            }

            held = state.Held.Where(message => !closed || !notDone.Contains(message.ChannelId)).ToList();
            state.Held.Clear();
            if (this._catchUp == state) {
                this._catchUp = null;
            }
        }

        await this.TakeHeldAsync(held, ct);
        this.SaveSecrets();
    }

    private async Task TakeHeldAsync(List<ChatMessage> held, CancellationToken ct) {
        foreach (var message in held) {
            try {
                await this.ProcessChatMessageAsync(message, ct);
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                this.Log(NoticeLevel.Warning, $"Couldn't process a held-back message: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> in the inbox, in order with the server's events, and waits for it: what it throws is
    /// thrown here. If the session stops first, it never runs.
    /// </summary>
    private Task RunInInboxAsync(Func<CancellationToken, Task> work) {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = this._inbox.Writer.TryWrite(new InboxItem(null, async ct => {
            try {
                await work(ct);
                done.TrySetResult();
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                done.TrySetCanceled(ct);
            } catch (Exception ex) {
                done.TrySetException(ex);
            }
        }));
        if (!queued) {
            done.TrySetCanceled();
        }

        return done.Task;
    }

    /// <summary>The key of an epoch, held or fetched for reading a caught-up message. Call inside the lock.</summary>
    private HeldKey? KeyFor(string channelId, ulong epoch) {
        if (this.GetEpochKey(channelId, epoch) is { } key) {
            var held = this._secrets.EpochKeyPositions.GetValueOrDefault(channelId)?.GetValueOrDefault(epoch);
            return new HeldKey(key, this.KeyPositionOf(channelId, epoch), held?.CreatedMs ?? 0, held?.CreatedBy ?? 0);
        }

        return this._pastKeys.TryGetValue(channelId, out var past) ? past.GetValueOrDefault(epoch) : null;
    }

    /// <summary>
    /// The keys <paramref name="userId"/> signed with at <paramref name="position"/> (where a key was made): a member's, as
    /// the log had them there, or, for someone who has left (or was removed) since, the keys they had then, if the key was
    /// made before they left. Call inside the lock.
    /// </summary>
    /// <returns>Null if they couldn't have held a key made there.</returns>
    private MemberKeys? SignerFor(string channelId, long userId, LogPosition? position) {
        if (SignerKeys(this.MembershipOf(channelId), userId, position) is { } member) {
            return member;
        }

        return position != null && this._secrets.FormerMembers.TryGetValue(channelId, out var former) && former.TryGetValue(userId, out var left)
               && position.Seq < left.LeftAtSeq
            ? new MemberKeys(left.SigningPublicKey, left.AgreementPublicKey)
            : null;
    }

    /// <summary>
    /// The latest a message from <paramref name="senderId"/> under epoch <paramref name="epoch"/> (its key made at
    /// <paramref name="position"/>) may be dated (Unix ms), give or take the grace live messages under an older key get.
    /// An honest server takes messages under a key only until the next membership change, and the next key is made after it:
    /// <list type="bullet">
    /// <item>no later than when the next epoch's key was made, if it says (signed by whoever made it);</item>
    /// <item>no later than the first membership change after <paramref name="position"/>, as this client dates it (see
    /// <see cref="RecordMembershipChanges"/>), when it can tell;</item>
    /// <item>and if the sender's own keys stopped after <paramref name="position"/> (they left, were removed, or their place
    /// moved to new keys), no later than a time by then that neither they nor the server could choose: the next key's, if
    /// someone else made it before this client came back; that of an entry someone else signed; or when this client saw the
    /// change happen. Without one, it can't be told how long those keys spoke, and nothing of theirs passes.</item>
    /// </list>
    /// Call inside the lock.
    /// </summary>
    /// <returns>
    /// The latest time (<see cref="long.MinValue"/> if it can't be told: nothing passes then); and, when the sender's keys
    /// stopped (the kind of entry that stopped them) with no such time, which means nothing of theirs passes either way.
    /// </returns>
    private (long Until, MembershipEntryKind? Unconfirmed) UsableUntil(string channelId, ulong epoch, LogPosition? position, long senderId) {
        var changeGrace = (long) OldEpochGrace.TotalMilliseconds;
        // A key's maker may be a member whose clock is a few minutes out: as much as a live message may be.
        var keyGrace = (long) MaxMessageClockSkew.TotalMilliseconds;
        var next = this.KeyFor(channelId, epoch + 1) is { CreatedMs: > 0 } made ? made : null;
        var byChange = this.ChangeBound(channelId, position, changeGrace);
        var until = next != null ? Math.Min(next.CreatedMs + keyGrace, byChange ?? long.MaxValue) : byChange ?? long.MinValue;
        if (position == null || this._secrets.MembershipChanges.GetValueOrDefault(channelId)?.Changes
                .Where(change => change.Exit && change.SubjectId == senderId && change.Seq > position.Seq).MinBy(change => change.Seq) is not { } exit) {
            return (until, null);
        }

        var evidence = new[] {
            exit.TrustedAtMs + changeGrace,
            exit.SeenLiveAtMs + changeGrace,
            next != null && next.CreatedBy != senderId && next.CreatedMs <= this._loginStartedMs ? next.CreatedMs + keyGrace : null,
        }.Where(at => at != null).Select(at => at!.Value).DefaultIfEmpty(long.MinValue).Min();
        // Without a time by which their keys had stopped that neither they nor the server chose, nothing of those keys passes.
        return evidence == long.MinValue ? (until, (MembershipEntryKind) exit.Kind) : (Math.Min(until, evidence), null);
    }

    /// <summary>
    /// The first membership change after <paramref name="position"/>, plus <paramref name="grace"/>: <see cref="long.MaxValue"/>
    /// if there was none (the membership is today's), null if it can't be told (the changes since weren't all recorded).
    /// Call inside the lock.
    /// </summary>
    private long? ChangeBound(string channelId, LogPosition? position, long grace) {
        if (position == null || position.Seq >= this.MembershipOf(channelId).MembersChangedAt) {
            return long.MaxValue;
        }

        if (!this._secrets.MembershipChanges.TryGetValue(channelId, out var changes) || position.Seq + 1 < changes.CompleteFrom) {
            return null;
        }

        return changes.Changes.Where(change => change.Seq > position.Seq).MinBy(change => change.Seq) is { } first ? first.AtMs + grace : null;
    }

    /// <summary>
    /// Fetches the keys this client was given for epochs it missed (the channel was rekeyed while it was away), from
    /// <paramref name="fromEpoch"/>, to read caught-up messages: each sealed to it, signed by a member (or someone who left
    /// since) for a log position it verified, older than the newest key it holds. Kept in memory, never sent with.
    /// </summary>
    private async Task FetchPastKeysAsync(Connection connection, string channelId, ulong fromEpoch, CancellationToken ct) {
        var response = await this.RequestAsync(connection, new ClientFrame { FetchEpochKeys = new FetchEpochKeys { ChannelId = channelId, FromEpoch = fromEpoch } }, ct);
        var keys = response.EpochKeys ?? throw Unexpected(response);
        lock (this._lock) {
            if (this._identity == null || this._me == null || !this.IsMember(channelId) || this.KeyEpochOf(channelId) is not { } newest) {
                return;
            }

            var membership = this.MembershipOf(channelId);
            foreach (var entry in keys.Keys) {
                // Newer keys are taken (or refused) as any epoch key is, with every check that needs; never this way.
                if (entry.Key == null || entry.Epoch >= newest || this.KeyFor(channelId, entry.Epoch) != null) {
                    continue;
                }

                // Only for a position in the log this client verified.
                var position = entry.Key.LogPosition;
                if (position != null && (membership.Head is not { } head || position.Seq > head.Seq
                                         || (membership.HashAt(position.Seq) is { } hash && !hash.AsSpan().SequenceEqual(position.Hash.Span)))) {
                    continue;
                }

                if (this.SignerFor(channelId, entry.AuthorId, position) is not { } author) {
                    continue;
                }

                this._groupKeys.OpenEpochKey(entry.Key, channelId, entry.Epoch, entry.AuthorId, author.SigningPublicKey, this._identity, this._me.UserId, out var key);
                if (key == null) {
                    this.Log(NoticeLevel.Warning, $"An older epoch {entry.Epoch} key for {channelId} failed its checks; messages under it can't be read");
                    continue;
                }

                if (!this._pastKeys.TryGetValue(channelId, out var past)) {
                    past = new Dictionary<ulong, HeldKey>();
                    this._pastKeys[channelId] = past;
                }

                past[entry.Epoch] = new HeldKey(key, position,
                    this._groupKeys.KeyCreatedAt(entry.Key, channelId, entry.Epoch, entry.AuthorId, author.SigningPublicKey) ?? 0, entry.AuthorId);
                foreach (var oldest in past.Keys.Order().Take(Math.Max(0, past.Count - MaxPastKeysPerChannel)).ToList()) {
                    past.Remove(oldest);
                }
            }
        }
    }

    /// <summary>Remembers the newest server number of a channel's message this client has had. Call inside the lock.</summary>
    private void NoteServerId(string channelId, ulong serverId) {
        if (serverId == 0 || (this._secrets.LastMessageIds.TryGetValue(channelId, out var last) && last >= serverId)) {
            return;
        }

        this._secrets.LastMessageIds[channelId] = serverId;
        this.ReplayStateChanged();
    }

    /// <summary>
    /// The message times or positions changed: saved with the next save, which comes within <see cref="ClientSessionOptions.ReplayStateSaveDelay"/>
    /// (so a crash shows little again as missed), but never once per message. Call inside the lock.
    /// </summary>
    private void ReplayStateChanged() {
        this._replayStateDirty = true;
        if (this._replaySaveScheduled || Volatile.Read(ref this._disposed) == 1) {
            return;
        }

        this._replaySaveScheduled = true;
        var delay = this._options.ReplayStateSaveDelay;
        this.RunBackground("Saving message positions", async ct => {
            try {
                await Task.Delay(delay, ct);
            } finally {
                lock (this._lock) {
                    this._replaySaveScheduled = false;
                }
            }

            this.SaveSecrets();
        });
    }

    /// <summary>
    /// A message from <paramref name="senderId"/> was accepted: the newest times and IDs had from them move on, and while the
    /// channel has a gap, it is remembered as had for the next catch-up. Call inside the lock.
    /// </summary>
    private void Accepted(string channelId, long senderId, long timestampMs, string messageId) {
        this.SetNewestMessage(channelId, senderId, timestampMs, messageId);
        if (this._secrets.CatchUpGaps.TryGetValue(channelId, out var gap) && !gap.AcceptedSince.Contains(messageId)) {
            gap.AcceptedSince.Add(messageId);
            if (gap.AcceptedSince.Count > CatchUpGap.MaxAcceptedSince) {
                gap.AcceptedSince.RemoveAt(0);
            }

            this.ReplayStateChanged();
        }
    }

    /// <summary>Records a message accepted from a sender in a channel, if it is the newest from them (saved soon). Call inside the lock.</summary>
    private void SetNewestMessage(string channelId, long senderId, long timestampMs, string messageId) {
        if (!this._secrets.NewestMessageTimes.TryGetValue(channelId, out var times)) {
            times = new Dictionary<long, long>();
            this._secrets.NewestMessageTimes[channelId] = times;
        }

        if (!this._secrets.NewestMessageIds.TryGetValue(channelId, out var ids)) {
            ids = new Dictionary<long, List<string>>();
            this._secrets.NewestMessageIds[channelId] = ids;
        }

        if (!times.TryGetValue(senderId, out var newest) || timestampMs > newest) {
            times[senderId] = timestampMs;
            ids[senderId] = [messageId];
        } else if (timestampMs == newest) {
            if (!ids.TryGetValue(senderId, out var atNewest)) {
                atNewest = [];
                ids[senderId] = atNewest;
            }

            if (atNewest.Contains(messageId)) {
                return;
            }

            atNewest.Add(messageId);
            if (atNewest.Count > MaxNewestIds) {
                atNewest.RemoveAt(0);
            }
        } else {
            return;
        }

        this.ReplayStateChanged();
    }

    /// <summary>Whether a live message is one of the newest had from its sender (same time and ID). Call inside the lock.</summary>
    private bool IsNewestHad(string channelId, long senderId, long timestampMs, string messageId) {
        return this._secrets.NewestMessageTimes.TryGetValue(channelId, out var times) && times.TryGetValue(senderId, out var newest) && newest == timestampMs
               && this._secrets.NewestMessageIds.TryGetValue(channelId, out var ids) && ids.TryGetValue(senderId, out var atNewest) && atNewest.Contains(messageId);
    }

    /// <summary>
    /// Whether a caught-up message is one this client already has from its sender, or older: dated before the newest
    /// accepted from them in the channel, or at the same time and one of those messages (or, from before message IDs were
    /// kept with the times, any at that time). For a channel with a gap (its last catch-up failed), against what was had
    /// when it did, and the messages accepted since. Call inside the lock.
    /// </summary>
    private bool AlreadyHad(string channelId, long senderId, long timestampMs, string messageId) {
        Dictionary<long, long>? times;
        Dictionary<long, List<string>>? ids;
        if (this._secrets.CatchUpGaps.TryGetValue(channelId, out var gap)) {
            if (gap.AcceptedSince.Contains(messageId)) {
                return true;
            }

            (times, ids) = (gap.Times, gap.Ids);
        } else {
            times = this._secrets.NewestMessageTimes.GetValueOrDefault(channelId);
            ids = this._secrets.NewestMessageIds.GetValueOrDefault(channelId);
        }

        if (times == null || !times.TryGetValue(senderId, out var newest)) {
            return false;
        }

        if (timestampMs != newest) {
            return timestampMs < newest;
        }

        return ids == null || !ids.TryGetValue(senderId, out var atNewest) || atNewest.Contains(messageId);
    }

    /// <summary>
    /// The channel's catch-up is still to be done after live messages are taken (it failed, or too many live messages came
    /// to hold): its position stays where it is, and what was had until now is what its missed messages are judged against
    /// (see <see cref="ClientSecrets.CatchUpGaps"/>). An earlier gap stays as it was. Call inside the lock.
    /// </summary>
    private void MarkGap(string channelId) {
        if (this._secrets.CatchUpGaps.ContainsKey(channelId)) {
            return;
        }

        this._secrets.CatchUpGaps[channelId] = new CatchUpGap {
            Times = new Dictionary<long, long>(this._secrets.NewestMessageTimes.GetValueOrDefault(channelId) ?? []),
            Ids = (this._secrets.NewestMessageIds.GetValueOrDefault(channelId) ?? []).ToDictionary(pair => pair.Key, pair => pair.Value.ToList()),
        };
        this.ReplayStateChanged();
    }

    /// <summary>
    /// Remembers who left a channel (or was removed) by a log entry, with the keys they had then, for checking their messages
    /// caught up later (see <see cref="SignerFor"/>). Only recent ones, a few per channel. Call inside the lock.
    /// </summary>
    private void RememberFormerMember(string channelId, MembershipEntry entry) {
        var cutoff = this.NowMs() - (long) FormerMember.KeptFor.TotalMilliseconds;
        if (entry.TimestampUnixMs < cutoff || entry.Subject == null) {
            return;
        }

        if (!this._secrets.FormerMembers.TryGetValue(channelId, out var former)) {
            former = new Dictionary<long, FormerMember>();
            this._secrets.FormerMembers[channelId] = former;
        }

        former[entry.Subject.UserId] = new FormerMember {
            SigningPublicKey = entry.Subject.SigningPublicKey.ToByteArray(),
            AgreementPublicKey = entry.Subject.AgreementPublicKey.ToByteArray(),
            LeftAtSeq = entry.Seq,
            LeftAtMs = Math.Min(entry.TimestampUnixMs, this.NowMs()),
        };
        foreach (var userId in former.Where(pair => pair.Value.LeftAtMs < cutoff).Select(pair => pair.Key).ToList()) {
            former.Remove(userId);
        }

        foreach (var userId in former.OrderBy(pair => pair.Value.LeftAtSeq).Take(Math.Max(0, former.Count - FormerMember.KeptPerChannel)).Select(pair => pair.Key).ToList()) {
            former.Remove(userId);
        }

        this._secretsVersion++;
    }

    /// <summary>
    /// Records the membership changes among entries just verified (joins, leaves, removals, members' places moving to new
    /// keys), with their times, for <see cref="UsableUntil"/> where the next key doesn't say when it was made (one made by an
    /// older client). An entry's time is never taken as later than when this client verified it. Some times can't be
    /// trusted at all: a key recovered entry's is the server's (nothing signs it), and a leave's, an accept's or a decline's
    /// is signed by its subject (who may be the one it would help). Any later entry signed by someone else dates every
    /// change before it no later than its own time, since entries are made in order. A log replayed from its start is
    /// recorded whole; one carried on from a position verified before this was kept, from its last membership change on.
    /// Call inside the lock.
    /// </summary>
    /// <param name="live">Verified as it happened, connected (not on coming back): each had happened by now.</param>
    private void RecordMembershipChanges(string channelId, IChannelMembership before, IChannelMembership after, IReadOnlyList<MembershipEntry> applied, bool live) {
        if (applied.Count == 0) {
            return;
        }

        if (!this._secrets.MembershipChanges.TryGetValue(channelId, out var changes)) {
            // Entries since the last membership change verified before (an invite, a rank) changed nothing to record.
            changes = new MembershipChanges { CompleteFrom = before.Head == null ? 0 : before.MembersChangedAt + 1 };
            this._secrets.MembershipChanges[channelId] = changes;
        }

        var now = this.NowMs();
        foreach (var entry in applied) {
            var at = Math.Min(entry.TimestampUnixMs, now);
            // Signed by someone other than its subject: its time is theirs, not the server's or the subject's.
            var trusted = entry.Kind != MembershipEntryKind.KeyRecovered && entry.Subject != null && entry.ActorId != entry.Subject.UserId;
            var memberMoved = entry.Kind == MembershipEntryKind.KeyRecovered && entry.Subject is { } subject
                              && (before.FindMember(subject.UserId) != null || after.FindMember(subject.UserId) != null);
            var change = entry.Kind is MembershipEntryKind.Accept or MembershipEntryKind.Leave or MembershipEntryKind.Remove || memberMoved;
            if (change && entry.Subject != null && changes.Changes.All(known => known.Seq != entry.Seq)) {
                changes.Changes.Add(new MembershipChange {
                    Seq = entry.Seq,
                    AtMs = at,
                    SubjectId = entry.Subject.UserId,
                    Kind = (int) entry.Kind,
                    Exit = entry.Kind is MembershipEntryKind.Leave or MembershipEntryKind.Remove || memberMoved,
                    TrustedAtMs = trusted ? at : null,
                    SeenLiveAtMs = live ? now : null,
                });
            }

            if (entry.Kind != MembershipEntryKind.KeyRecovered) {
                // Entries are made in order: whatever came before this one had happened by its time, as far as its signer can say:
                // never a change's own subject, about their own change.
                foreach (var earlier in changes.Changes.Where(known => known.Seq < entry.Seq && known.SubjectId != entry.ActorId)) {
                    earlier.AtMs = Math.Min(earlier.AtMs, at);
                    earlier.TrustedAtMs = Math.Min(earlier.TrustedAtMs ?? long.MaxValue, at);
                }
            }
        }

        changes.Changes.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        var cutoff = now - (long) FormerMember.KeptFor.TotalMilliseconds;
        while (changes.Changes.Count > 0 && (changes.Changes[0].AtMs < cutoff || changes.Changes.Count > MaxMembershipChanges)) {
            // What came before what is kept is no longer known: a key from then is too old to read with.
            changes.CompleteFrom = Math.Max(changes.CompleteFrom, changes.Changes[0].Seq + 1);
            changes.Changes.RemoveAt(0);
        }

        this._secretsVersion++;
    }

    /// <summary>
    /// Says (once per channel and session) when a membership change verified as it happened is dated further ahead of this
    /// computer's clock than a live message may be: whoever dated it (its signer, or the server for a re-verification) is
    /// misdating it, perhaps to widen when old keys may still speak. It is dated by when it was seen instead. Call inside the lock.
    /// </summary>
    private void FlagChangesDatedAhead(string channelId, IReadOnlyList<MembershipEntry> applied) {
        var limit = this.NowMs() + (long) MaxMessageClockSkew.TotalMilliseconds;
        if (applied.Any(entry => entry.TimestampUnixMs > limit) && this._flaggedDatedAhead.Add(channelId)) {
            var name = this._channels.GetValueOrDefault(channelId)?.DisplayName ?? ChannelView.PlaceholderName(channelId);
            this._pendingNotices.Add(SessionNotice.Of(NoticeLevel.Warning, PlainMessages.MembershipChangeDatedAhead(name), channelId));
        }
    }

    // Channels whose membership changes dated ahead were said this session (see FlagChangesDatedAhead).
    private readonly HashSet<string> _flaggedDatedAhead = new();
}
