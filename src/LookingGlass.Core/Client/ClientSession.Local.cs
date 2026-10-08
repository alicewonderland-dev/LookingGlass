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
    /// sender, and it is recent and new (by the newest times had from each sender, saved with the channels', so a replay
    /// after a restart is refused too). The sender's identity that comes with it is used only if no key is held for them yet,
    /// and nothing is pinned here: <see cref="ConfirmLocalSender"/> pins it once the plugin shows the message (the sender is
    /// near and a friend). A message under other keys than those held is dropped, and changes nothing: the keys held for
    /// someone change only through lookups and channels, with their warnings, never from a local message. A sender whose
    /// keys are held is named as held, not as the server says. Anything dropped is only counted, in the diagnostic log.
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
        var replayed = this.Read(() => {
            if (this._secrets.NewestMessageTimes.GetValueOrDefault(LocalReplayKey) is { } times && times.TryGetValue(sender.UserId, out var newest)
                && message.TimestampUnixMs < newest - (long) MessageReorderAllowance.TotalMilliseconds) {
                return "older than one already had";
            }

            // The newest had from them, sent again after a restart (when the seen-set is empty): had already.
            if (this.IsNewestHad(LocalReplayKey, sender.UserId, message.TimestampUnixMs, messageId) || !this.MarkSeen(LocalSeenId(message.MessageId))) {
                return "had already";
            }

            // Saved soon, with the channels' message times.
            this.SetNewestMessage(LocalReplayKey, sender.UserId, message.TimestampUnixMs, messageId);
            return null;
        });

        if (replayed != null) {
            this.DropLocal(replayed);
            return;
        }

        // Named as held, if their keys are: a server can't pass one friend's message off as another's (or as someone near).
        var user = pinned is { Name.Length: > 0 }
            ? new User { UserId = sender.UserId, Name = pinned.Name, WorldName = pinned.WorldName }
            : sender;
        // Links only as the checks in MessageContent leave them, as for a channel's message.
        var incoming = MessageContent.Decode(content) is { } text
            ? new IncomingLocalMessage(Shown(user), false, text.Text, false, timestamp) { Links = text.Links }
            : new IncomingLocalMessage(Shown(user), false, null, true, timestamp);
        this.InvokeSafely(this.LocalMessageReceived, pinned == null ? incoming with { FirstSeen = message.Sender.Clone() } : incoming);
    }

    /// <summary>The keys held for a local message's sender, and the name and world held with them.</summary>
    private sealed record HeldSender(MemberKeys Keys, string Name, string WorldName);

    /// <summary>
    /// The plugin is showing a local message from someone else (they are near and a friend; see <see cref="LocalChat.Judge"/>):
    /// if no key was held for them when it arrived, the keys it came with are pinned now (trust on first use, as for a
    /// lookup). Call before showing it, and show it only if this says so.
    /// </summary>
    /// <returns>
    /// Whether it may be shown: false only if other keys than the ones it was checked against are held for the sender by
    /// now (another first message, under other keys, was shown meanwhile).
    /// </returns>
    public bool ConfirmLocalSender(IncomingLocalMessage message) {
        if (message.IsOwn || message.FirstSeen is not { User: { } user, Identity: { } bundle } first) {
            return true;
        }

        var keys = MemberKeys.Of(bundle);
        Wording? warning;
        lock (this._lock) {
            if (this._secrets.PinnedIdentities.TryGetValue(user.UserId, out var held)) {
                return new MemberKeys(held.SigningPublicKey, held.AgreementPublicKey) == keys;
            }

            warning = this.Pin(user.UserId, keys, user, first.KeyVersion);
            this._identities[user.UserId] = first;
            this._users[user.UserId] = user;
        }

        this.SaveSecrets();
        if (warning != null) {
            this.RaiseNotice(NoticeLevel.Warning, warning);
        }

        return true;
    }

    /// <summary>Counts a local message dropped, and logs the count (never who, nor what).</summary>
    private void DropLocal(string reason) {
        var count = this.Read(() => this._localDropped[reason] = this._localDropped.GetValueOrDefault(reason) + 1);
        this.Log(NoticeLevel.Debug, $"Dropped a local message ({reason}); {count} so far this session");
    }
}
