using LookingGlass.Core.Membership;
using LookingGlass.Protocol;

namespace LookingGlass.Core.Client;

/// <summary>How a member's log head disagrees with the log this client verified, as far as it can tell.</summary>
public enum HeadDisagreement {
    /// <summary>Another hash at a position this client verified; the server shows the entry verified here, or wouldn't answer.</summary>
    Differs,

    /// <summary>Another hash at a position this client verified, and the server shows no entry there.</summary>
    NotShown,

    /// <summary>A position beyond the log the server shows this client.</summary>
    Ahead,
}

/// <summary>
/// Log heads in messages (see "Log heads in messages" in docs/design.md). Each channel message carries, inside its encrypted,
/// signed content (<see cref="Content.LogHead"/>), the newest entry of the channel's membership log its sender's client had
/// verified: its sequence number and hash. A receiver compares it with the log it verified:
///
/// <list type="bullet">
/// <item><b>The same hash there</b>, or a position too far back to remember (a fork there shows at every later position
/// too): nothing to do. A lookup per message (<see cref="VerifiedHashAt"/>).</item>
/// <item><b>Another hash at a position this client verified.</b> That the two disagree is certain, whatever the server says:
/// the sender signed it. The server is asked for its entry there, which can only make it worse for the server. If it shows
/// another entry than the one verified here, the fork check every log sync uses (<see cref="CheckForkAsync"/>) looks into it;
/// if it shows none, the same check fetches its whole log. When that check blames the server (the critical fork warning, or
/// the server hiding the membership), that warning stands. Otherwise (the server shows the entry verified here, shows junk,
/// fails, or the check has to wait), the user is told, naming the sender, and the sender is marked in the member list.</item>
/// <item><b>A position this client hasn't reached.</b> The log is fetched and verified as always, then compared. If the
/// server's log ends before it, or several tries in a row don't reach it, the same: the sender is named.</item>
/// </list>
///
/// Never holds a message up: it is shown first, and anything that asks the server runs in the background, at most once per
/// sender and channel per <see cref="ClientSessionOptions.ForkCheckInterval"/>, and once per claim. Each member is told about
/// once per channel until their messages agree again; the mark on them stays until then, or until they leave.
/// </summary>
public sealed partial class ClientSession {
    // Hashes of verified entries remembered per channel for comparing heads, on top of the membership's own recent ones
    // (which start again at every join or leave).
    private const int MaxHeadHashesPerChannel = 1024;

    // Tries in a row to fetch the log up to a member's head before they are named.
    private const int MaxHeadCatchUpFailures = 3;

    // ---- state guarded by _lock
    // Channel ID → hashes of the entries this client verified most recently, by sequence number (see OnMembershipSet).
    private readonly Dictionary<string, HeadHashes> _headHashes = new();
    // Heads claimed in messages that were looked into ("channel/seq/hash"), whatever came of it: each is looked into once.
    private readonly HashSet<string> _headClaimsLookedInto = new();
    // When the server was last asked about a head in a sender's message in a channel.
    private readonly Dictionary<(string ChannelId, long SenderId), DateTimeOffset> _headLooksAt = new();
    // Tries in a row that didn't reach a sender's head (see MaxHeadCatchUpFailures).
    private readonly Dictionary<(string ChannelId, long SenderId), int> _headCatchUpFailures = new();
    // Senders the user was told see another membership, per channel, until their messages agree again.
    private readonly HashSet<(string ChannelId, long SenderId)> _toldOtherMembership = new();
    // ----

    private enum HeadLook {
        /// <summary>Nothing to do: too far back to compare, looked into already, or too soon to ask the server again.</summary>
        Nothing,

        /// <summary>The same hash at that position.</summary>
        Same,

        /// <summary>Another hash at a position this client verified.</summary>
        Differs,

        /// <summary>A position this client hasn't reached.</summary>
        Ahead,
    }

    /// <summary>The hashes of a run of consecutive verified entries, at most <see cref="MaxHeadHashesPerChannel"/> of the newest.</summary>
    private sealed class HeadHashes {
        private readonly List<byte[]> _hashes = new();
        private ulong _from;

        public bool IsEmpty => this._hashes.Count == 0;

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
    /// A newly verified membership (from <see cref="SetMembership"/>): remembers the hashes of the entries just verified, for
    /// comparing heads at their positions after a join or leave (from which the membership keeps only newer hashes), and takes
    /// the mark off members who are gone. Call inside the lock.
    /// </summary>
    private void OnMembershipSet(string channelId, IChannelMembership before, IChannelMembership membership, IReadOnlyList<MembershipEntry> applied) {
        if (!this._headHashes.TryGetValue(channelId, out var kept)) {
            kept = new HeadHashes();
            this._headHashes[channelId] = kept;
        }

        var followsOn = applied.Count > 0 && applied[0].Seq == (before.Head == null ? 0 : before.Head.Seq + 1);
        if (!followsOn) {
            kept.Clear();
        } else if (kept.IsEmpty && before.Head != null) {
            // The first change since this client started: carry on from the hashes the restored membership kept.
            var from = before.Head.Seq;
            while (from > 0 && before.Head.Seq - from + 1 < MaxHeadHashesPerChannel && before.HashAt(from - 1) != null) {
                from--;
            }

            for (var seq = from; seq <= before.Head.Seq; seq++) {
                kept.Add(seq, before.HashAt(seq)!);
            }
        }

        foreach (var entry in applied) {
            kept.Add(entry.Seq, MembershipEntries.Hash(entry));
        }

        if (this._channels.TryGetValue(channelId, out var channel)) {
            foreach (var gone in channel.SeeOtherMembership.Where(userId => membership.FindMember(userId) == null).ToList()) {
                channel.SeeOtherMembership.Remove(gone);
                this._toldOtherMembership.Remove((channelId, gone));
            }
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
        var agreesAgain = false;
        lock (this._lock) {
            look = this.HeadLookFor(channelId, senderId, claimed, rateLimited: true);
            if (look == HeadLook.Same && this._channels.TryGetValue(channelId, out var channel) && channel.SeeOtherMembership.Remove(senderId)) {
                // Their messages agree with this client's log again: the mark goes, and a new disagreement is told again.
                this._toldOtherMembership.Remove((channelId, senderId));
                agreesAgain = true;
            }
        }

        if (agreesAgain) {
            this.Publish();
        } else if (look == HeadLook.Differs) {
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
            if (mine == null) {
                return HeadLook.Nothing;
            }

            if (mine.AsSpan().SequenceEqual(claimed.Hash.Span)) {
                return HeadLook.Same;
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
    /// A member's head names a position this client verified, with another hash. The disagreement is certain (they signed it);
    /// the server's entry there can only make it the server's fault. Another entry than the one verified here goes to the fork
    /// check, none to the check of its whole log; unless one of them blames the server, the sender is named.
    /// </summary>
    private async Task LookIntoHeadAsync(string channelId, long senderId, LogPosition claimed, CancellationToken ct) {
        lock (this._lock) {
            this._headClaimsLookedInto.Add(ClaimKey(channelId, claimed));
        }

        var how = HeadDisagreement.Differs;
        try {
            var response = await this.RequestAsync(new ClientFrame {
                FetchMembershipLog = new FetchMembershipLog { ChannelId = channelId, FromSeq = claimed.Seq },
            }, ct);
            var log = response.MembershipLog ?? throw Unexpected(response);
            var theirs = log.Entries.FirstOrDefault(entry => entry.Seq == claimed.Seq);
            var mine = this.Read(() => this.VerifiedHashAt(channelId, claimed.Seq));
            if (theirs == null) {
                // The server won't show the entry verified here: the fork check fetches its whole log and says what that means.
                how = HeadDisagreement.NotShown;
                this.Log(NoticeLevel.Info, $"A message in {channelId} names another membership log entry #{claimed.Seq}, and the server shows none there");
                await this.CheckForkAsync(channelId, null, ct, null);
            } else if (mine != null && !MembershipEntries.Hash(theirs).AsSpan().SequenceEqual(mine)) {
                // Another entry than the one verified here: the server may have shown two versions of the log.
                this.Log(NoticeLevel.Info, $"A message in {channelId} names another membership log entry #{claimed.Seq}, and the server shows another one there too");
                await this.CheckForkAsync(channelId, theirs, ct, null);
            }
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            // Whatever went wrong, the sender's word still disagrees with this client's log.
            this.Log(NoticeLevel.Warning, $"Couldn't look into a membership log head in a message in {channelId}: {ex.Message}");
        }

        this.TellOtherMembership(channelId, senderId, claimed.Seq, how);
    }

    /// <summary>
    /// A member's head names a position this client hasn't reached: fetches and verifies the log as always, then compares.
    /// If the server's log ends before that position, or <see cref="MaxHeadCatchUpFailures"/> tries in a row don't reach it,
    /// the sender is named.
    /// </summary>
    private async Task CatchUpWithHeadAsync(string channelId, long senderId, LogPosition claimed, CancellationToken ct) {
        var synced = true;
        try {
            await this.SyncLogAsync(channelId, ct, fetch: LogFetch.Always);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            this.Log(NoticeLevel.Warning, $"Couldn't fetch the membership log of {channelId} for a head in a message: {ex.Message}");
            synced = false;
        }

        HeadLook look;
        var tell = false;
        lock (this._lock) {
            look = synced ? this.HeadLookFor(channelId, senderId, claimed, rateLimited: false) : HeadLook.Ahead;
            var serverHead = this._channels.GetValueOrDefault(channelId)?.LogHead;
            if (look != HeadLook.Ahead) {
                this._headCatchUpFailures.Remove((channelId, senderId));
            } else if (synced && (serverHead == null || serverHead.Seq < claimed.Seq)) {
                // The server says its log ends before it: the sender's word against the server's.
                tell = true;
            } else {
                // Not there yet (the fetch failed, or there was more than one sync takes): a few tries, then the sender is named.
                var failures = this._headCatchUpFailures.GetValueOrDefault((channelId, senderId)) + 1;
                this._headCatchUpFailures[(channelId, senderId)] = failures;
                tell = failures >= MaxHeadCatchUpFailures;
            }

            if (tell) {
                this._headCatchUpFailures.Remove((channelId, senderId));
                this._headClaimsLookedInto.Add(ClaimKey(channelId, claimed));
            }
        }

        if (look == HeadLook.Differs) {
            await this.LookIntoHeadAsync(channelId, senderId, claimed, ct);
        } else if (tell) {
            this.TellOtherMembership(channelId, senderId, claimed.Seq, HeadDisagreement.Ahead);
        }
    }

    /// <summary>
    /// Unless the server is blamed for the channel's membership already (a fork, or a membership it hides, which says more),
    /// marks the member as seeing another membership and tells the user (once, until their messages agree again). It names the
    /// member and blames neither: see <see cref="PlainMessages.MemberSeesOtherMembership"/>.
    /// </summary>
    private void TellOtherMembership(string channelId, long senderId, ulong seq, HeadDisagreement how) {
        Wording? text = null;
        lock (this._lock) {
            if (!this._channels.TryGetValue(channelId, out var channel)
                || channel.MembershipWarning?.Kind is NoticeKind.MembershipForked or NoticeKind.MembershipHidden or NoticeKind.MembersShownDifferently
                || this.MembershipOf(channelId).FindMember(senderId) == null) {
                return;
            }

            channel.SeeOtherMembership.Add(senderId);
            if (this._toldOtherMembership.Add((channelId, senderId))) {
                var sender = Shown(this.UserOf(senderId));
                text = PlainMessages.MemberSeesOtherMembership($"{sender.Name}@{sender.WorldName}", channel.DisplayName, seq, how,
                    MembershipCheckCode.Of(this.MembershipOf(channelId).Head));
            }
        }

        this.Publish();
        if (text == null) {
            return;
        }

        // Who is in the notice; the diagnostic log says only where.
        this.Log(NoticeLevel.Warning, $"A member's message in {channelId} names membership log entry #{seq}, which the server doesn't bear out ({how})");
        this.RaiseNotice(NoticeLevel.Warning, text, channelId);
    }
}
