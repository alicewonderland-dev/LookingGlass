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
/// its sender had when its key was made (someone who has left since, too: <see cref="ClientSecrets.FormerMembers"/>), and,
/// instead of the live rules on age (which an older message fails), it must be newer than everything already accepted from
/// its sender in the channel (persisted, with that message's ID), and not dated in the future. So each is accepted at most
/// once, even after a restart, and none can be passed off as new: it is marked as caught up, with the time it was sent.
/// Anything else is dropped; a channel's drops are told in one notice.</item>
/// <item><b>Keys.</b> A key of an epoch this client missed (the channel was rekeyed while it was away) is fetched for reading
/// only, kept in memory (<see cref="_pastKeys"/>), and never sent with.</item>
/// </list>
/// </summary>
public sealed partial class ClientSession {
    // The most live messages held back while catching up; past that they are taken as they come.
    private const int MaxHeldLiveMessages = 2000;

    // Pages one channel's catch-up fetches at most (a page holds at least 24 of the longest messages).
    private const int MaxCatchUpPagesPerChannel = 250;

    // Past epoch keys kept per channel, for reading caught-up messages.
    private const int MaxPastKeysPerChannel = 64;

    // ---- state guarded by _lock
    // The server agreed to message catch-up on the current connection.
    private bool _catchUpAgreed;
    // The catch-up of the current login, while live messages are held back for it.
    private CatchUpState? _catchUp;
    // Channel ID → epoch → keys of epochs this client missed, fetched to read caught-up messages only (never to send with).
    private readonly Dictionary<string, Dictionary<ulong, HeldKey>> _pastKeys = new();
    // ----

    /// <summary>
    /// Raised on a background thread, once per channel each time this client comes back, with the messages sent there while it
    /// was disconnected (oldest first), each checked and accepted once. They don't go through <see cref="MessageReceived"/>.
    /// </summary>
    public event Action<CaughtUpMessages>? MessagesCaughtUp;

    /// <summary>A server event, or work that must run in order with them (see <see cref="RunInInboxAsync"/>).</summary>
    private sealed record InboxItem(Event? Event, Func<CancellationToken, Task>? Work);

    /// <summary>An epoch key, and the log position it was made for (if known).</summary>
    private sealed record HeldKey(byte[] Key, LogPosition? Position);

    /// <summary>The live messages held back for one login's catch-up, and the channels done. Guarded by <see cref="_lock"/>.</summary>
    private sealed class CatchUpState {
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

            this._catchUp = new CatchUpState();
            return this._catchUp;
        }
    }

    /// <summary>Holds a live message back while its channel's missed messages are still to come. Called in the inbox.</summary>
    /// <returns>True if it was held back (it is taken when its channel's catch-up ends).</returns>
    private bool HeldBack(ChatMessage message) {
        lock (this._lock) {
            if (this._catchUp is not { Over: false } state || state.Released.Contains(message.ChannelId) || state.Held.Count >= MaxHeldLiveMessages) {
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
            var channels = this.Read(() => this._channels.Keys.Where(this.IsMember).Order(StringComparer.Ordinal).ToList());
            foreach (var channelId in channels) {
                if (connection.Closed.IsCompleted || connection != this._connection) {
                    break;
                }

                try {
                    await this.CatchUpChannelAsync(connection, state, channelId, ct);
                } catch (Exception ex) when (ex is not OperationCanceledException) {
                    // That channel's position stays where it got to: the next login carries on from there.
                    this.Log(NoticeLevel.Warning, $"Couldn't catch up on missed messages in {channelId}: {ex.Message}");
                }
            }
        } finally {
            // Whatever happened, the live messages held back are taken now: they're as genuine as ever.
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

    /// <summary>One channel: its missed messages, page by page, then its live messages held back meanwhile.</summary>
    private async Task CatchUpChannelAsync(Connection connection, CatchUpState state, string channelId, CancellationToken ct) {
        var batch = new CatchUpBatch(channelId, connection);
        try {
            var after = this.Read(() => this._secrets.LastMessageIds.GetValueOrDefault(channelId));
            // Without a position (the first login with this version, a new computer, a channel just joined): the last while.
            var within = after == 0 ? (uint) Math.Clamp(this._options.CatchUpWithoutPosition.TotalSeconds, 0, uint.MaxValue) : 0u;
            for (var pages = 0; pages < MaxCatchUpPagesPerChannel; pages++) {
                var page = await this.FetchStoredMessagesAsync(connection, channelId, after, within, ct);
                // Only what the server says comes after the position, in order: anything else is the server's mistake (or
                // worse), and is left out rather than trusted. (Nothing is accepted for its number, only by its own checks.)
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
                    // Nothing more this client may read: what it skips up to the newest is its own, or from before it joined.
                    if (!page.More && page.LatestId > after) {
                        lock (this._lock) {
                            this.NoteServerId(channelId, page.LatestId);
                        }
                    }

                    break;
                }
            }
        } finally {
            await this.RunInInboxAsync(inboxCt => this.FinishChannelAsync(state, batch, inboxCt)).WaitAsync(ct);
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
        var (member, skip) = this.Read(() => (this.IsMember(channelId), this.IsBlocked(message.SenderId) || this._seenMessages.Contains(messageId)));
        if (!member) {
            // No longer in it (removed meanwhile): nothing of it is shown any more.
            return;
        }

        if (skip) {
            // From someone blocked (hidden, as live), or already had in this session.
            this.Noted(channelId, message);
            return;
        }

        var key = this.Read(() => this.KeyFor(channelId, message.Epoch));
        if (key == null && batch.KeysLookedFor.Add(message.Epoch)) {
            await this.FetchPastKeysAsync(batch.Connection, channelId, message.Epoch, ct);
            key = this.Read(() => this.KeyFor(channelId, message.Epoch));
        }

        // Signed by the keys the sender had when the key it is under was made (someone who left since, too, if it was made
        // before they left), not whatever the server says their keys are now.
        var signer = key == null ? null : this.Read(() => this.SignerFor(channelId, message.SenderId, key.Position));
        var content = signer == null ? null : this._groupKeys.DecryptMessage(message, key!.Key, signer.SigningPublicKey);
        var now = this._options.TimeProvider.GetUtcNow();
        if (content == null || message.TimestampUnixMs > (now + MaxMessageClockSkew).ToUnixTimeMilliseconds()) {
            // Under a key this client never held (from before it joined, say), from someone who couldn't send it then, not
            // really theirs, or dated in the future (which would hold back their later messages).
            batch.Dropped++;
            this.Noted(channelId, message);
            return;
        }

        string? channelName;
        User sender;
        bool isOwn;
        lock (this._lock) {
            this.NoteServerId(channelId, message.ServerId);
            // Each is accepted once: never one this client already has, or older than what it has from the sender (which
            // the server, sending in its order, never sends but again).
            if (this.AlreadyHad(channelId, message.SenderId, message.TimestampUnixMs, messageId) || !this.MarkSeen(messageId)) {
                return;
            }

            this.SetNewestMessage(channelId, message.SenderId, message.TimestampUnixMs, messageId);
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
    /// messages held back meanwhile are taken. Called in the inbox.
    /// </summary>
    private async Task FinishChannelAsync(CatchUpState state, CatchUpBatch batch, CancellationToken ct) {
        var channelId = batch.ChannelId;
        var (name, display) = this.Read(() => (this._channels.GetValueOrDefault(channelId)?.Name, this._channels.GetValueOrDefault(channelId)?.DisplayName));
        if (batch.Accepted.Count > 0) {
            this.InvokeSafely(this.MessagesCaughtUp,
                new CaughtUpMessages(channelId, name, batch.Accepted.Select(message => message with { Sender = Shown(message.Sender) }).ToList()));
        }

        if (batch.Dropped > 0) {
            this.RaiseNotice(NoticeLevel.Warning, PlainMessages.MessagesNotCaughtUp(display ?? ChannelView.PlaceholderName(channelId), batch.Dropped), channelId);
        }

        List<ChatMessage> held;
        lock (this._lock) {
            state.Released.Add(channelId);
            held = state.Held.Where(message => message.ChannelId == channelId).ToList();
            state.Held.RemoveAll(message => message.ChannelId == channelId);
        }

        await this.TakeHeldAsync(held, ct);
        // The position and message times now, so a crash doesn't show these again as missed.
        this.SaveSecrets();
    }

    /// <summary>The login's catch-up is over: every live message still held back is taken, in the order it came. Called in the inbox.</summary>
    private async Task ReleaseAllAsync(CatchUpState state, CancellationToken ct) {
        List<ChatMessage> held;
        lock (this._lock) {
            state.Over = true;
            held = [.. state.Held];
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
            return new HeldKey(key, this.KeyPositionOf(channelId, epoch));
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

                past[entry.Epoch] = new HeldKey(key, position);
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
        this._replayStateDirty = true;
    }

    /// <summary>Records the newest message accepted from a sender in a channel (saved with the next save). Call inside the lock.</summary>
    private void SetNewestMessage(string channelId, long senderId, long timestampMs, string messageId) {
        if (!this._secrets.NewestMessageTimes.TryGetValue(channelId, out var times)) {
            times = new Dictionary<long, long>();
            this._secrets.NewestMessageTimes[channelId] = times;
        }

        if (!this._secrets.NewestMessageIds.TryGetValue(channelId, out var ids)) {
            ids = new Dictionary<long, string>();
            this._secrets.NewestMessageIds[channelId] = ids;
        }

        times[senderId] = timestampMs;
        ids[senderId] = messageId;
        this._replayStateDirty = true;
    }

    /// <summary>
    /// Whether a caught-up message is one this client already has from its sender, or older: dated before the newest
    /// accepted from them in the channel, or at the same time and that message (or, from before message IDs were kept with
    /// the times, any at that time). Call inside the lock.
    /// </summary>
    private bool AlreadyHad(string channelId, long senderId, long timestampMs, string messageId) {
        if (!this._secrets.NewestMessageTimes.TryGetValue(channelId, out var times) || !times.TryGetValue(senderId, out var newest)) {
            return false;
        }

        if (timestampMs != newest) {
            return timestampMs < newest;
        }

        return !this._secrets.NewestMessageIds.TryGetValue(channelId, out var ids) || !ids.TryGetValue(senderId, out var newestId) || newestId == messageId;
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
            LeftAtMs = entry.TimestampUnixMs,
        };
        foreach (var userId in former.Where(pair => pair.Value.LeftAtMs < cutoff).Select(pair => pair.Key).ToList()) {
            former.Remove(userId);
        }

        foreach (var userId in former.OrderBy(pair => pair.Value.LeftAtSeq).Take(Math.Max(0, former.Count - FormerMember.KeptPerChannel)).Select(pair => pair.Key).ToList()) {
            former.Remove(userId);
        }

        this._secretsVersion++;
    }
}
