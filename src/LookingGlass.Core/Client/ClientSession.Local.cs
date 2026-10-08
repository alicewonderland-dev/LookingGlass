using LookingGlass.Core.Crypto;
using LookingGlass.Core.Membership;
using LookingGlass.Protocol;

namespace LookingGlass.Core.Client;

/// <summary>
/// Local chat (friends only; see <see cref="LocalChat"/> and "Local chat (friends only)" in docs/design.md). The session
/// looks the friends the plugin names up, seals a message to them and sends it; and it opens what arrives, checks it was
/// signed by the key it holds for the sender, and passes it on. Where anyone is, and who is a friend, is the plugin's to
/// check: the session never knows.
/// </summary>
public sealed partial class ClientSession {
    /// <summary>
    /// Where local chat's replay state is kept with the channels' (<see cref="ClientSecrets.NewestMessageTimes"/>, by sender):
    /// a key no channel ID can be (those are 32 hex digits), so it is saved, and survives a restart, with theirs.
    /// </summary>
    internal const string LocalReplayKey = "local";

    // ---- state guarded by _lock
    // The server agreed to local chat on the current connection (capability "local.v1").
    private bool _localAgreed;
    // Local messages dropped this session, by reason, for the diagnostic log (counts only).
    private readonly Dictionary<string, int> _localDropped = new();
    // ----

    /// <summary>
    /// Raised on a background thread for every local message that opened and was signed by the key held for its sender (or,
    /// with none held, the keys it came with), not from anyone blocked, and for the player's own once the server took it.
    /// The plugin shows one from someone else only if they are near and a friend (<see cref="LocalChat.Judge"/>), and
    /// <see cref="ConfirmLocalSender"/> says so.
    /// </summary>
    public event Action<IncomingLocalMessage>? LocalMessageReceived;

    /// <summary>
    /// Raised on a background thread for a local message that couldn't be checked against what is held for its sender (other
    /// keys, or their keys held under another name), so it isn't shown: who the server says sent it, and why, never what it
    /// says. The plugin hints at it once a session, only if the sender is near and a friend (see <see cref="LocalHints"/>).
    /// </summary>
    public event Action<LocalUnchecked>? LocalMessageUnchecked;

    /// <summary>Logged in, on a server that agreed to local chat on this connection.</summary>
    public bool LocalChatAvailable => this.Read(() => this._localAgreed && this._state == ConnectionState.Ready);

    /// <summary>
    /// Sends a local message to <paramref name="friends"/>, the friends near the player as the plugin found them (closest
    /// first): each is looked up by name (a lookup is reused for a while, and so is the answer that nobody is registered as
    /// them), and the message is sealed to the keys held for them, at most as many as the server allows (the first ones). Not
    /// to the player themselves, nor to anyone blocked. Nothing is sent if none of them uses LookingGlass here.
    /// </summary>
    /// <exception cref="InvalidOperationException">Not logged in, or the server doesn't offer local chat (<see cref="LocalChatWords.NotOnThisServer"/>).</exception>
    public async Task<LocalSendResult> SendLocalAsync(IReadOnlyList<(string Name, string WorldName)> friends, LinkedText linked, CancellationToken ct = default) {
        var text = linked.Text;
        if (string.IsNullOrWhiteSpace(text)) {
            throw new ArgumentException("Message is empty.", nameof(linked));
        }

        var (identity, me) = this.RequireIdentityAndUser();
        var (agreed, max, maxBytes) = this.Read(() => (this._localAgreed, (int) Math.Min(this._limits?.MaxLocalRecipients ?? 0u, (uint) LocalChat.MaxRecipients),
            this._limits?.MaxMessageBytes ?? 4096));
        if (!agreed || max <= 0) {
            throw PlainMessages.Failure(LocalChatWords.NotOnThisServer);
        }

        // What a recipient would accept, and nothing else, as for a channel's message.
        var content = MessageContent.Encode(linked);
        var links = MessageContent.ValidLinks(text, content.Text.Links);
        if (links.Count != linked.Links.Count) {
            throw new ArgumentException("A link in the message isn't one LookingGlass can send.", nameof(linked));
        }

        // Checked before anyone is looked up: the ciphertext is the content, a 24-byte nonce and a 16-byte tag.
        if (content.CalculateSize() + 40 > maxBytes) {
            throw new InvalidOperationException("That message is too long.");
        }

        var recipients = new Dictionary<long, byte[]>();
        int notUsingIt = 0, couldntCheck = 0;
        foreach (var (name, worldName) in friends) {
            if (recipients.Count >= max) {
                break;
            }

            var lookup = $"{name.Trim()}@{worldName.Trim()}";
            if (this._lookups.IsMissing(lookup)) {
                notUsingIt++;
                continue;
            }

            var identityOf = this._lookups.Recall(lookup, userId => this.Read(() => this._identities.GetValueOrDefault(userId)));
            if (identityOf == null) {
                try {
                    identityOf = await this.LookUpAsync(name.Trim(), worldName.Trim(), lookup, ct);
                } catch (InvalidOperationException ex) when (ex.InnerException is ServerErrorException { Code: ErrorCode.NotFound }) {
                    this._lookups.RememberMissing(lookup);
                    notUsingIt++;
                    continue;
                } catch (Exception ex) when (ex is ServerErrorException or InvalidOperationException or TimeoutException) {
                    // Rate limited, or an identity that isn't valid: this one is left out this time.
                    couldntCheck++;
                    continue;
                }
            }

            var userId = identityOf.User.UserId;
            if (userId == me.UserId || this.Read(() => this.IsBlocked(userId))) {
                continue;
            }

            recipients.TryAdd(userId, identityOf.Identity.AgreementPublicKey.ToByteArray());
        }

        if (recipients.Count == 0) {
            return new LocalSendResult(0, notUsingIt, couldntCheck);
        }

        var timestamp = this.NowMs();
        var message = LocalCrypto.Seal(content, identity, me.UserId, recipients.Select(pair => (pair.Key, pair.Value)), timestamp);
        await this.RequestAsync(new ClientFrame { SendLocalMessage = message }, ct);
        lock (this._lock) {
            this.MarkSeen(LocalSeenId(message.MessageId));
        }

        this.Log(NoticeLevel.Debug, $"Sent a local message to {recipients.Count} (not using LookingGlass {notUsingIt}, not checked {couldntCheck})");
        this.InvokeSafely(this.LocalMessageReceived, new IncomingLocalMessage(Shown(me), true, text, false, DateTimeOffset.FromUnixTimeMilliseconds(timestamp)) { Links = links });
        return new LocalSendResult(recipients.Count, notUsingIt, couldntCheck);
    }

    // Local messages share the seen-set with channel messages, apart from them.
    private static string LocalSeenId(Google.Protobuf.ByteString messageId) => "local:" + Convert.ToHexString(messageId.Span);

    /// <summary>
    /// A local message from the server: passed on to the plugin only if the server agreed to local chat with this connection,
    /// it isn't from the player themselves or anyone blocked, it opens for this player, it is signed by the key held for the
    /// sender, and it is recent and new (not seen this session, and not older than, or one of, the newest shown from that
    /// sender, which are saved with the channels' message times, so a replay after a restart is refused too). The sender's
    /// identity that comes with it is used only if no key is held for them yet, and nothing is pinned or recorded here:
    /// <see cref="ConfirmLocalSender"/> does that once the plugin shows the message (the sender is near and a friend).
    /// <para>
    /// A message under other keys than those held, or from a held sender the server now names otherwise (a rename, a world
    /// transfer), isn't shown, and changes nothing: the keys held for someone change only through lookups and channels, with
    /// their warnings, never from a local message. It is passed on as <see cref="LocalMessageUnchecked"/> (without its
    /// content), for the plugin to hint at if the sender is near and a friend. Anything else dropped is only counted, in the
    /// diagnostic log.
    /// </para>
    /// </summary>
    private void ProcessLocalMessage(LocalMessage message) {
        var sender = message.Sender?.User;
        var (agreed, me, identity) = this.Read(() => (this._localAgreed, this._me, this._identity));
        if (!agreed || me == null || identity == null || sender == null || message.Sender?.Identity == null) {
            this.DropLocal("not expected");
            return;
        }

        if (sender.UserId == me.UserId || this.Read(() => this.IsBlocked(sender.UserId))) {
            this.DropLocal(sender.UserId == me.UserId ? "own" : "blocked");
            return;
        }

        if (!IdentityKeys.IsValidBundle(message.Sender.Identity)) {
            this.DropLocal("invalid identity");
            return;
        }

        var offered = MemberKeys.Of(message.Sender.Identity);
        var now = this._options.TimeProvider.GetUtcNow();
        var pinned = this.Read(() => this._secrets.PinnedIdentities.GetValueOrDefault(sender.UserId) is { } held
            ? new HeldSender(new MemberKeys(held.SigningPublicKey, held.AgreementPublicKey), held.Name, held.WorldName)
            : null);
        if (pinned != null && pinned.Keys != offered) {
            this.DropLocal("keys other than those held");
            // The next /lgl to them looks them up afresh rather than reusing what was looked up (which may be what changed).
            this._lookups.Forget($"{sender.Name}@{sender.WorldName}");
            this.InvokeSafely(this.LocalMessageUnchecked, new LocalUnchecked(Shown(sender), LocalUncheckedReason.KeysChanged));
            return;
        }

        var content = LocalCrypto.Open(message, identity, me.UserId, offered.SigningPublicKey);
        if (content == null) {
            this.DropLocal("failed checks");
            return;
        }

        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(message.TimestampUnixMs);
        if ((now - timestamp).Duration() > MaxMessageClockSkew) {
            this.DropLocal("dated too far from now");
            return;
        }

        var messageId = Convert.ToHexString(message.MessageId.Span);
        var replayed = this.Read(() => this.LocalReplayed(sender.UserId, message.TimestampUnixMs, messageId)
            ?? (this.MarkSeen(LocalSeenId(message.MessageId)) ? null : "had already"));
        if (replayed != null) {
            this.DropLocal(replayed);
            return;
        }

        // Held under another name or world than the server gives now: not shown under either (the held one may not be who is
        // near; the new one is only the server's word), but hinted at, so the player updates it.
        if (pinned is { Name.Length: > 0 } && (!string.Equals(pinned.Name, sender.Name, StringComparison.OrdinalIgnoreCase)
                                                || !string.Equals(pinned.WorldName, sender.WorldName, StringComparison.OrdinalIgnoreCase))) {
            this.DropLocal("held under another name");
            this.InvokeSafely(this.LocalMessageUnchecked, new LocalUnchecked(Shown(sender), LocalUncheckedReason.Renamed));
            return;
        }

        // Links only as the checks in MessageContent leave them, as for a channel's message.
        var incoming = MessageContent.Decode(content) is { } text
            ? new IncomingLocalMessage(Shown(sender), false, text.Text, false, timestamp) { Links = text.Links }
            : new IncomingLocalMessage(Shown(sender), false, null, true, timestamp);
        incoming = incoming with { MessageId = messageId, FirstSeen = pinned == null ? message.Sender.Clone() : null };
        this.InvokeSafely(this.LocalMessageReceived, incoming);
    }

    /// <summary>
    /// Why a local message from <paramref name="senderId"/> is a replay of one shown (more than a little older than the newest
    /// shown from them, or one of those), or null if it isn't. Call inside the lock.
    /// </summary>
    private string? LocalReplayed(long senderId, long timestampMs, string messageId) {
        if (this._secrets.NewestMessageTimes.GetValueOrDefault(LocalReplayKey) is { } times && times.TryGetValue(senderId, out var newest)
            && timestampMs < newest - (long) MessageReorderAllowance.TotalMilliseconds) {
            return "older than one already had";
        }

        // The newest shown from them, sent again after a restart (when the seen-set is empty): had already.
        return this.IsNewestHad(LocalReplayKey, senderId, timestampMs, messageId) ? "had already" : null;
    }

    /// <summary>The keys held for a local message's sender, and the name and world held with them.</summary>
    private sealed record HeldSender(MemberKeys Keys, string Name, string WorldName);

    /// <summary>
    /// The plugin is about to show a local message from someone else (they are near and a friend; see
    /// <see cref="LocalChat.Judge"/>): call first, and show it only if this says <see cref="LocalConfirmation.Show"/>. Then it
    /// is recorded against replays (only messages shown are, so strangers' can't grow what is saved), and if no key was held
    /// for the sender when it arrived, the keys it came with are pinned (trust on first use, as for a lookup); not if another
    /// account is held under the name it gives, or other keys were held for the sender meanwhile.
    /// </summary>
    public LocalConfirmation ConfirmLocalSender(IncomingLocalMessage message) {
        if (message.IsOwn || message.MessageId is not { } messageId) {
            return LocalConfirmation.Show;
        }

        var senderId = message.Sender.UserId;
        var timestampMs = message.Timestamp.ToUnixTimeMilliseconds();
        Wording? warning = null;
        var pinnedNow = false;
        lock (this._lock) {
            if (this.LocalReplayed(senderId, timestampMs, messageId) != null) {
                return LocalConfirmation.Replayed;
            }

            if (message.FirstSeen is { User: { } user, Identity: { } bundle } first) {
                var keys = MemberKeys.Of(bundle);
                if (this._secrets.PinnedIdentities.TryGetValue(user.UserId, out var held)) {
                    if (new MemberKeys(held.SigningPublicKey, held.AgreementPublicKey) != keys) {
                        return LocalConfirmation.OtherKeysHeld;
                    }
                } else {
                    // Someone else is held under this name: never taken from a local message (Pin would only warn, and take it).
                    if (this._secrets.PinnedIdentities.Any(pair => pair.Key != user.UserId
                                                                   && string.Equals(pair.Value.Name, user.Name, StringComparison.OrdinalIgnoreCase)
                                                                   && string.Equals(pair.Value.WorldName, user.WorldName, StringComparison.OrdinalIgnoreCase))) {
                        return LocalConfirmation.NameHeldByAnother;
                    }

                    warning = this.Pin(user.UserId, keys, user, first.KeyVersion);
                    this._identities[user.UserId] = first;
                    this._users[user.UserId] = user;
                    pinnedNow = true;
                }
            }

            // Saved soon, with the channels' message times.
            this.SetNewestMessage(LocalReplayKey, senderId, timestampMs, messageId);
        }

        if (pinnedNow) {
            this.SaveSecrets();
        }

        if (warning != null) {
            this.RaiseNotice(NoticeLevel.Warning, warning);
        }

        return LocalConfirmation.Show;
    }

    /// <summary>Counts a local message dropped, and logs the count (never who, nor what).</summary>
    private void DropLocal(string reason) {
        var count = this.Read(() => this._localDropped[reason] = this._localDropped.GetValueOrDefault(reason) + 1);
        this.Log(NoticeLevel.Debug, $"Dropped a local message ({reason}); {count} so far this session");
    }
}
