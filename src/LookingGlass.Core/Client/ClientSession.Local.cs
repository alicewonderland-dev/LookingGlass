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
    // How often one sender's keys may be taken again from a local message that came with others than those held: each time
    // the user is warned, so a server switching someone's keys back and forth can't flood them with warnings.
    private static readonly TimeSpan LocalKeyChangeInterval = TimeSpan.FromMinutes(1);

    // ---- state guarded by _lock
    // The server agreed to local chat on the current connection (capability "local.v1").
    private bool _localAgreed;
    // The newest local message time accepted from each sender this session: one more than MessageReorderAllowance older is a replay.
    private readonly Dictionary<long, long> _localNewest = new();
    // When each sender's keys were last taken from a local message (see LocalKeyChangeInterval).
    private readonly Dictionary<long, DateTimeOffset> _localKeysTakenAt = new();
    // Local messages dropped this session, by reason, for the diagnostic log (counts only).
    private readonly Dictionary<string, int> _localDropped = new();
    // ----

    /// <summary>
    /// Raised on a background thread for every local message that opened and was signed by the key held for its sender (not
    /// from anyone blocked), and for the player's own once the server took it. The plugin shows one from someone else only
    /// if they are near and a friend (<see cref="LocalChat.Judge"/>).
    /// </summary>
    public event Action<IncomingLocalMessage>? LocalMessageReceived;

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
    /// A local message from the server: shown (passed on to the plugin) only if the server agreed to local chat with this
    /// connection, it isn't from the player themselves or anyone blocked, it opens for this player, it is signed by the key
    /// held for the sender (the one the server sends with it only if none is held yet: trust on first use, as for a lookup,
    /// pinned once the message checks out), and it is recent and new. Keys other than those held are taken, with the usual
    /// warning that the sender's keys changed, but the message that brought them is dropped. Anything dropped is only
    /// counted, in the diagnostic log.
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
        var held = this.Read(() => this._secrets.PinnedIdentities.GetValueOrDefault(sender.UserId) is { } pinned
            ? new MemberKeys(pinned.SigningPublicKey, pinned.AgreementPublicKey)
            : null);
        if (held != null && held != offered) {
            // Other keys than those held: taken as any identity from the server is, with the warning that their keys changed
            // (at most once a minute per sender), but this message isn't shown.
            if (this.Read(() => this.TakeLocalKeysNow(sender.UserId, now))) {
                this.AcceptIdentities([message.Sender]);
            }

            this.DropLocal("keys other than those held");
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

        var replayed = this.Read(() => {
            if (this._localNewest.TryGetValue(sender.UserId, out var newest) && message.TimestampUnixMs < newest - (long) MessageReorderAllowance.TotalMilliseconds) {
                return "older than one already had";
            }

            if (!this.MarkSeen(LocalSeenId(message.MessageId))) {
                return "had already";
            }

            this._localNewest[sender.UserId] = Math.Max(newest, message.TimestampUnixMs);
            return null;
        });

        if (replayed != null) {
            this.DropLocal(replayed);
            return;
        }

        // Only now, once it checked out, are the sender's keys pinned (trust on first use, as for a lookup) and their name
        // and world kept up to date (warned about if they moved to another account): a message that fails pins nobody.
        this.AcceptIdentities([message.Sender]);
        var user = this.Read(() => this.UserOf(sender.UserId));
        // Links only as the checks in MessageContent leave them, as for a channel's message.
        this.InvokeSafely(this.LocalMessageReceived, MessageContent.Decode(content) is { } text
            ? new IncomingLocalMessage(Shown(user), false, text.Text, false, timestamp) { Links = text.Links }
            : new IncomingLocalMessage(Shown(user), false, null, true, timestamp));
    }

    /// <summary>Whether a sender's other keys may be taken from a local message now (see <see cref="LocalKeyChangeInterval"/>). Call inside the lock.</summary>
    private bool TakeLocalKeysNow(long senderId, DateTimeOffset now) {
        if (this._localKeysTakenAt.TryGetValue(senderId, out var last) && now - last < LocalKeyChangeInterval) {
            return false;
        }

        foreach (var expired in this._localKeysTakenAt.Where(entry => now - entry.Value >= LocalKeyChangeInterval).Select(entry => entry.Key).ToList()) {
            this._localKeysTakenAt.Remove(expired);
        }

        this._localKeysTakenAt[senderId] = now;
        return true;
    }

    /// <summary>Counts a local message dropped, and logs the count (never who, nor what).</summary>
    private void DropLocal(string reason) {
        var count = this.Read(() => this._localDropped[reason] = this._localDropped.GetValueOrDefault(reason) + 1);
        this.Log(NoticeLevel.Debug, $"Dropped a local message ({reason}); {count} so far this session");
    }
}
