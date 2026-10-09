using LookingGlass.Protocol;

namespace LookingGlass.Core.Client;

/// <summary>
/// Keys have a maximum age (see "Keys have a maximum age" in docs/design.md). A channel whose members don't change would
/// otherwise keep one key for ever, so one key that leaked (a crash dump, a debug log, a memory read) would read every new
/// message. Once a channel's newest key is older than <see cref="EpochMaxAge"/>, a member online who could make the next
/// key makes it, as any automatic rekey: silently, sealed to every member, the old key still opening what was sent under it.
/// The key's age comes from the time its maker signed into it, never from the server.
/// </summary>
public sealed partial class ClientSession {
    /// <summary>How old a channel's newest key may get before a member online makes the next. Not a server setting.</summary>
    public static readonly TimeSpan EpochMaxAge = TimeSpan.FromDays(7);

    /// <summary>
    /// The least time between two tries at replacing one channel's old key, by this client: a server refusing every new key
    /// (so the old one stays in use) costs one try an hour, not a stream of them.
    /// </summary>
    internal static readonly TimeSpan OldKeyRetryInterval = TimeSpan.FromHours(1);

    // ---- state guarded by _lock
    // When this client last tried to replace each channel's old key (by the session's clock).
    private readonly Dictionary<string, DateTimeOffset> _oldKeyTriedAt = new();
    // ----

    private int _keyAgeChecks;
    private int _oldKeyTries;

    /// <summary>How many times keys' ages have been looked at, this session.</summary>
    internal int KeyAgeChecksForTests => Volatile.Read(ref this._keyAgeChecks);

    /// <summary>How many tries at replacing an old key were started, this session.</summary>
    internal int OldKeyTriesForTests => Volatile.Read(ref this._oldKeyTries);

    /// <summary>
    /// While <paramref name="connection"/> lasts, now and every <see cref="ClientSessionOptions.KeyAgeCheckInterval"/>: starts
    /// replacing the key of every channel whose newest key is too old and which this client may rekey (see
    /// <see cref="TakeChannelsWithOldKeys"/>), each after a random wait of up to <see cref="ClientSessionOptions.KeyAgeJitter"/>.
    /// Members online together each pick their own wait, so one usually goes first and the others then hold a new key and
    /// stop; two that try at once are settled as any rekeys at once (the server takes the first for the next epoch, and the
    /// other is refused and gives up).
    /// </summary>
    private async Task ReplaceOldKeysAsync(Connection connection, CancellationToken ct) {
        while (!ct.IsCancellationRequested && !connection.Closed.IsCompleted) {
            foreach (var channelId in this.TakeChannelsWithOldKeys()) {
                var wait = this._options.KeyAgeJitter > TimeSpan.Zero
                    ? TimeSpan.FromTicks((long) (Random.Shared.NextDouble() * this._options.KeyAgeJitter.Ticks))
                    : TimeSpan.Zero;
                Interlocked.Increment(ref this._oldKeyTries);
                this.Log(NoticeLevel.Debug, $"The key of {channelId} is too old: replacing it in {wait.TotalSeconds:0} s");
                this.RunBackground(PlainMessages.Rekeying, rekeyCt => this.ReplaceOldKeyAsync(channelId, wait, rekeyCt));
            }

            Interlocked.Increment(ref this._keyAgeChecks);
            await Task.WhenAny(Task.Delay(this._options.KeyAgeCheckInterval, ct), connection.Closed);
        }
    }

    /// <summary>
    /// Waits <paramref name="wait"/>, then makes the channel's next key if its newest is still too old (nobody else made one
    /// meanwhile). Silent, like every automatic rekey: a try that fails (refused, a newer key arrived first, the connection
    /// went) is only noted in the diagnostic log, at Debug, without names; the next is an hour on.
    /// </summary>
    private async Task ReplaceOldKeyAsync(string channelId, TimeSpan wait, CancellationToken ct) {
        try {
            if (wait > TimeSpan.Zero) {
                await Task.Delay(wait, ct);
            }

            await this.RekeyAsync(channelId, RekeyReason.KeyTooOld, ct);
        } catch (Exception ex) when (!ct.IsCancellationRequested) {
            // Not the exception's words: they can name a member.
            var why = ex is ServerErrorException refused ? $"the server answered {refused.Code}" : ex.GetType().Name;
            this.Log(NoticeLevel.Debug, $"Couldn't replace the too old key of {channelId}: {why}");
        }
    }

    /// <summary>
    /// The channels whose key this client should replace now, each marked as tried: those it may replace (see
    /// <see cref="MayReplaceOldKey"/>) and hasn't tried to in the last <see cref="OldKeyRetryInterval"/>. None if this client
    /// doesn't rekey by itself (<see cref="ClientSessionOptions.AutoRekeyWhenDesignated"/> off).
    /// </summary>
    private List<string> TakeChannelsWithOldKeys() {
        if (!this._options.AutoRekeyWhenDesignated || !this._options.ReplaceOldKeys) {
            return [];
        }

        List<string> due;
        bool stamped;
        lock (this._lock) {
            var now = this._options.TimeProvider.GetUtcNow();
            var version = this._secretsVersion;
            due = this._channels.Values
                .Where(channel => this.MayReplaceOldKey(channel, now)
                                  // A clock put back counts as a new hour.
                                  && (!this._oldKeyTriedAt.TryGetValue(channel.Id, out var tried) || now - tried >= OldKeyRetryInterval || now < tried))
                .Select(channel => channel.Id)
                .ToList();
            foreach (var channelId in due) {
                this._oldKeyTriedAt[channelId] = now;
            }

            stamped = this._secretsVersion != version;
        }

        if (stamped) {
            this.SaveSecrets();
        }

        return due;
    }

    /// <summary>
    /// This client makes the channel's next key because its newest is older than <see cref="EpochMaxAge"/>: by the rule for
    /// automatic rekeys (as the server picks whom to ask), a member under the keys this client has, holding the channel's
    /// current key and name, with no rekey for a membership change waiting (that one goes as it always does). Not in a
    /// channel it is alone in: nobody else could read what a leaked key opens, and anyone joining brings a new key anyway.
    /// Call inside the lock.
    /// </summary>
    private bool MayReplaceOldKey(ChannelState channel, DateTimeOffset now) {
        return this.IsMember(channel.Id) && !this.IsAloneIn(channel.Id) && channel.Name != null && this.HasCurrentKey(channel.Id) && !this.NeedsRekey(channel)
               && this.KeyEpochOf(channel.Id) is { } held && this.KeyMadeAt(channel.Id, held) is { } made && now - made >= EpochMaxAge;
    }

    /// <summary>
    /// When the key of <paramref name="epoch"/> was made, as this client can tell: the time its maker signed into it, but
    /// never after this client got it (a time ahead can't keep a key in use longer); when this client got it, if the key
    /// doesn't say (an older plugin made it, or the server changed the time, which the signature then doesn't match). A key
    /// kept by an earlier version, which saved neither, counts from now (saved): updating doesn't make every key old at once.
    /// Null if no position is kept for the key. Call inside the lock.
    /// </summary>
    private DateTimeOffset? KeyMadeAt(string channelId, ulong epoch) {
        if (this._secrets.EpochKeyPositions.GetValueOrDefault(channelId)?.GetValueOrDefault(epoch) is not { } held) {
            return null;
        }

        if (held is { CreatedMs: <= 0, HeldSinceMs: <= 0 }) {
            held.HeldSinceMs = this.NowMs();
            this._secretsVersion++;
        }

        var made = held.CreatedMs <= 0 ? held.HeldSinceMs
            : held.HeldSinceMs <= 0 ? held.CreatedMs
            : Math.Min(held.CreatedMs, held.HeldSinceMs);
        return DateTimeOffset.FromUnixTimeMilliseconds(made);
    }

    /// <summary>Why a channel is rekeyed: what <see cref="RekeyAsync(string, RekeyReason, CancellationToken)"/> checks first.</summary>
    private enum RekeyReason {
        /// <summary>A membership change (or an unusable key) needs one: done only if it still does.</summary>
        Pending,

        /// <summary>Asked for by hand (the debug tool): done whatever.</summary>
        Forced,

        /// <summary>The newest key is too old (see <see cref="EpochMaxAge"/>): done only if it still is, or if one is pending.</summary>
        KeyTooOld,
    }
}
