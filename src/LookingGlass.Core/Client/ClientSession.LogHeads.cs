using LookingGlass.Core.Membership;
using LookingGlass.Protocol;

namespace LookingGlass.Core.Client;

/// <summary>
/// Log heads in messages (see "Log heads in messages" in docs/design.md). Each channel message carries, inside its encrypted,
/// signed content (<see cref="Content.LogHead"/>), the newest entry of the channel's membership log its sender's client had
/// verified: its sequence number and hash. A receiver compares it with the log it verified:
///
/// <list type="bullet">
/// <item><b>The same hash there</b>, or a position too far back to remember (a fork there shows at every later position
/// too): nothing to do. A lookup per message (<see cref="VerifiedHashAt"/>).</item>
/// <item><b>Another hash at a position this client verified.</b> The server is asked for its entry there. If it shows
/// another entry than the one verified here, it has shown two versions of the log, and the fork check every log sync uses
/// (<see cref="CheckForkAsync"/>) looks into it, with the same warning and the same mark on the channel. If it shows the one
/// verified here, only the member's word says otherwise: the user is told, naming them (the server may be showing them
/// another version, or their client is wrong or lying), and the server isn't blamed.</item>
/// <item><b>A position this client hasn't reached.</b> The log is fetched and verified as always, then compared. If the
/// server's log ends before it, that too is the member's word against the server's.</item>
/// </list>
///
/// Never holds a message up: it is shown first, and anything that asks the server runs in the background, at most once per
/// sender and channel per <see cref="ClientSessionOptions.ForkCheckInterval"/>, and once per claim. Each member is named at
/// most once per channel and session.
/// </summary>
public sealed partial class ClientSession {
    // Hashes of verified entries remembered per channel for comparing heads, on top of the membership's own recent ones
    // (which start again at every join or leave).
    private const int MaxHeadHashesPerChannel = 1024;

    // ---- state guarded by _lock
    // Channel ID → hashes of the entries this client verified most recently, by sequence number (see RememberHeadHashes).
    private readonly Dictionary<string, HeadHashes> _headHashes = new();
    // Heads claimed in messages that were looked into ("channel/seq/hash"), whatever came of it: each is looked into once.
    private readonly HashSet<string> _headClaimsLookedInto = new();
    // When the server was last asked about a head in a sender's message in a channel.
    private readonly Dictionary<(string ChannelId, long SenderId), DateTimeOffset> _headLooksAt = new();
    // Senders the user was told see another membership, per channel.
    private readonly HashSet<(string ChannelId, long SenderId)> _toldOtherMembership = new();
    // ----

    private enum HeadLook {
        /// <summary>Nothing to do: the same, too far back to compare, looked into already, or too soon to ask the server again.</summary>
        Nothing,

        /// <summary>Another hash at a position this client verified.</summary>
        Differs,

        /// <summary>A position this client hasn't reached.</summary>
        Ahead,
    }

    /// <summary>The hashes of a run of consecutive verified entries, at most <see cref="MaxHeadHashesPerChannel"/> of the newest.</summary>
    private sealed class HeadHashes {
        private readonly List<byte[]> _hashes = new();
        private ulong _from;

        /// <summary>Adds the next entry's hash; one that doesn't follow on starts the run again.</summary>
        public void Add(ulong seq, byte[] hash) {
            if (this._hashes.Count == 0 || seq != this._from + (ulong) this._hashes.Count) {
                this._hashes.Clear();
                this._from = seq;
            }

            this._hashes.Add(hash);
            if (this._hashes.Count > MaxHeadHashesPerChannel) {
                var dropped = this._hashes.Count - MaxHeadHashesPerChannel;
                this._hashes.RemoveRange(0, dropped);
                this._from += (ulong) dropped;
            }
        }

        public byte[]? At(ulong seq) => seq >= this._from && seq - this._from < (ulong) this._hashes.Count ? this._hashes[(int) (seq - this._from)] : null;

        public void Clear() => this._hashes.Clear();
    }

    /// <summary>
    /// Remembers the hashes of entries just verified, for comparing heads at their positions after a join or leave (from which
    /// the membership keeps only newer hashes). Only entries that follow on from what was verified before; any other change
    /// starts again. Call inside the lock, from <see cref="SetMembership"/>.
    /// </summary>
    private void RememberHeadHashes(string channelId, IChannelMembership before, IReadOnlyList<MembershipEntry> applied) {
        if (!this._headHashes.TryGetValue(channelId, out var kept)) {
            kept = new HeadHashes();
            this._headHashes[channelId] = kept;
        }

        if (applied.Count == 0 || applied[0].Seq != (before.Head == null ? 0 : before.Head.Seq + 1)) {
            kept.Clear();
        }

        foreach (var entry in applied) {
            kept.Add(entry.Seq, MembershipEntries.Hash(entry));
        }
    }

    /// <summary>The hash of the entry this client verified at <paramref name="seq"/>, if it is remembered. Call inside the lock.</summary>
    private byte[]? VerifiedHashAt(string channelId, ulong seq) {
        var membership = this.MembershipOf(channelId);
        if (membership.Head == null || seq > membership.Head.Seq) {
            return null;
        }

        return membership.HashAt(seq) ?? this._headHashes.GetValueOrDefault(channelId)?.At(seq);
    }

    /// <summary>
    /// Compares the log head a member's message carries with this client's verified log (see the class summary). Called once
    /// the message has been accepted and shown (live or caught up), in the inbox; returns at once.
    /// </summary>
    /// <param name="claimed">The message's head; null from a plugin from before log heads, which is taken as before.</param>
    private void CompareSenderHead(string channelId, long senderId, LogPosition? claimed) {
        if (claimed == null || claimed.Hash.Length != MembershipEntries.HashSize) {
            return;
        }

        HeadLook look;
        lock (this._lock) {
            look = this.HeadLookFor(channelId, senderId, claimed, rateLimited: true);
        }

        if (look == HeadLook.Differs) {
            this.RunBackground(PlainMessages.CheckingMembership, ct => this.LookIntoHeadAsync(channelId, senderId, claimed, ct));
        } else if (look == HeadLook.Ahead) {
            this.RunBackground(PlainMessages.CheckingMembership, ct => this.CatchUpWithHeadAsync(channelId, senderId, claimed, ct));
        }
    }

    /// <summary>What a head in a message needs. Call inside the lock.</summary>
    /// <param name="rateLimited">Count it against the sender's allowance of questions to the server (and give up if spent).</param>
    private HeadLook HeadLookFor(string channelId, long senderId, LogPosition claimed, bool rateLimited) {
        if (senderId == this._me?.UserId || this.MembershipOf(channelId).Head is not { } head) {
            return HeadLook.Nothing;
        }

        var look = HeadLook.Ahead;
        if (claimed.Seq <= head.Seq) {
            // The usual case, for every message: one lookup.
            var mine = this.VerifiedHashAt(channelId, claimed.Seq);
            if (mine == null || mine.AsSpan().SequenceEqual(claimed.Hash.Span)) {
                return HeadLook.Nothing;
            }

            look = HeadLook.Differs;
        }

        if (this._headClaimsLookedInto.Contains(ClaimKey(channelId, claimed))) {
            return HeadLook.Nothing;
        }

        if (rateLimited) {
            // Whatever a member puts in their messages, they get the server asked at most this often.
            var now = this._options.TimeProvider.GetUtcNow();
            if (this._headLooksAt.TryGetValue((channelId, senderId), out var last) && now - last < this._options.ForkCheckInterval) {
                return HeadLook.Nothing;
            }

            this._headLooksAt[(channelId, senderId)] = now;
        }

        return look;
    }

    private static string ClaimKey(string channelId, LogPosition claimed) => $"{channelId}/{claimed.Seq}/{Convert.ToHexString(claimed.Hash.Span)}";

    /// <summary>
    /// A member's head names a position this client verified, with another hash. Asks the server for its entry there: another
    /// entry than the one verified here means it has shown two versions of the log, which the fork check looks into; the one
    /// verified here leaves only the member's word against it.
    /// </summary>
    private async Task LookIntoHeadAsync(string channelId, long senderId, LogPosition claimed, CancellationToken ct) {
        try {
            var response = await this.RequestAsync(new ClientFrame {
                FetchMembershipLog = new FetchMembershipLog { ChannelId = channelId, FromSeq = claimed.Seq },
            }, ct);
            var log = response.MembershipLog ?? throw Unexpected(response);
            var theirs = log.Entries.FirstOrDefault(entry => entry.Seq == claimed.Seq);
            byte[]? mine;
            lock (this._lock) {
                this._headClaimsLookedInto.Add(ClaimKey(channelId, claimed));
                mine = this.VerifiedHashAt(channelId, claimed.Seq);
            }

            if (mine == null) {
                return;
            }

            if (theirs == null) {
                // The server won't show the entry verified here: the fork check fetches its whole log and says what that means.
                this.Log(NoticeLevel.Info, $"A message in {channelId} names another membership log entry #{claimed.Seq}, and the server shows none there");
                await this.CheckForkAsync(channelId, null, ct, null);
            } else if (!MembershipEntries.Hash(theirs).AsSpan().SequenceEqual(mine)) {
                // Another entry than the one verified here: the server has shown two versions of the log.
                this.Log(NoticeLevel.Info, $"A message in {channelId} names another membership log entry #{claimed.Seq}, and the server shows another one there too");
                await this.CheckForkAsync(channelId, theirs, ct, null);
            } else {
                this.TellOtherMembership(channelId, senderId, claimed.Seq, ahead: false);
            }
        } catch (Exception ex) when (ex is ServerErrorException or SessionDisconnectedException or TimeoutException) {
            // The sender's next message brings it again.
            this.Log(NoticeLevel.Warning, $"Couldn't look into a membership log head in a message in {channelId}: {ex.Message}");
        }
    }

    /// <summary>
    /// A member's head names a position this client hasn't reached: fetches and verifies the log as always, then compares.
    /// If the server's log ends before that position, only the member's word says there is more.
    /// </summary>
    private async Task CatchUpWithHeadAsync(string channelId, long senderId, LogPosition claimed, CancellationToken ct) {
        try {
            await this.SyncLogAsync(channelId, ct, fetch: LogFetch.Always);
        } catch (Exception ex) when (ex is ServerErrorException or SessionDisconnectedException or TimeoutException) {
            this.Log(NoticeLevel.Warning, $"Couldn't fetch the membership log of {channelId} for a head in a message: {ex.Message}");
            return;
        }

        HeadLook look;
        bool shortOfIt;
        lock (this._lock) {
            look = this.HeadLookFor(channelId, senderId, claimed, rateLimited: false);
            // Still short of it, and so is the server's log, as it says (not merely more to fetch than one sync takes).
            var serverHead = this._channels.GetValueOrDefault(channelId)?.LogHead;
            shortOfIt = look == HeadLook.Ahead && (serverHead == null || serverHead.Seq < claimed.Seq);
            if (shortOfIt) {
                this._headClaimsLookedInto.Add(ClaimKey(channelId, claimed));
            }
        }

        if (look == HeadLook.Differs) {
            await this.LookIntoHeadAsync(channelId, senderId, claimed, ct);
        } else if (shortOfIt) {
            this.TellOtherMembership(channelId, senderId, claimed.Seq, ahead: true);
        }
    }

    /// <summary>
    /// Tells the user (once per member and channel) that a member's message says they verified another membership than the
    /// server shows this client. It names the member and blames neither: see <see cref="PlainMessages.MemberSeesOtherMembership"/>.
    /// </summary>
    private void TellOtherMembership(string channelId, long senderId, ulong seq, bool ahead) {
        Wording text;
        lock (this._lock) {
            if (!this._toldOtherMembership.Add((channelId, senderId))) {
                return;
            }

            var sender = Shown(this.UserOf(senderId));
            text = PlainMessages.MemberSeesOtherMembership($"{sender.Name}@{sender.WorldName}",
                this._channels.GetValueOrDefault(channelId)?.DisplayName ?? ChannelView.PlaceholderName(channelId), seq, ahead);
        }

        // Who is in the notice; the diagnostic log says only where.
        this.Log(NoticeLevel.Warning, ahead
            ? $"A member's message in {channelId} names membership log entry #{seq}, beyond the server's log"
            : $"A member's message in {channelId} names another membership log entry #{seq} than the one verified, which the server shows");
        this.RaiseNotice(NoticeLevel.Warning, text, channelId);
    }
}
