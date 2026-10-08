using System.Security.Cryptography;
using Google.Protobuf;
using LookingGlass.Protocol;

namespace LookingGlass.Core.Crypto;

/// <summary>
/// Local chat's encryption (see "Local chat (friends only)" in docs/design.md). A message is encrypted once, with
/// XChaCha20-Poly1305 under a random key made for it alone, and that key is sealed to each recipient's identity key, as
/// epoch keys are sealed to members; the sender signs each copy with their identity key. So a crowd of friends costs a
/// small sealed key each rather than a copy of the whole message, and even 200 copies of the longest message fit in one
/// frame. Payload layouts here are part of the protocol; the server uses <see cref="VerifyCopies"/> to refuse garbage
/// before passing anything on.
/// </summary>
/// <remarks>
/// What each part binds: the ciphertext's associated data is the sender, message ID and time; a sealed key's context adds
/// the recipient, so a copy opens only for whom it was sealed to, as from whom and when it says; the signature covers that
/// context, a hash of the ciphertext, a commitment to the message's key (the same in every copy, as an epoch key's) and the
/// sealed key, so nothing can be moved between messages, recipients or senders, each recipient checks its own copy without
/// seeing who else got one, and a sender can't give recipients different keys (or a ciphertext that opens two ways under
/// two keys): a key that doesn't match the commitment is refused.
/// </remarks>
public static class LocalCrypto {
    public const int MessageKeySize = 32;
    public const int MessageIdSize = 16;
    public const int KeyCommitmentSize = 32;

    private static byte[] AssociatedData(long senderId, ReadOnlySpan<byte> messageId, long timestampMs) =>
        new SigningPayload(Domains.LocalMessage).Add(senderId).Add(messageId).Add(timestampMs).ToArray();

    private static byte[] CopyContext(long senderId, long recipientId, ReadOnlySpan<byte> messageId, long timestampMs) =>
        new SigningPayload(Domains.LocalMessageKey).Add(senderId).Add(recipientId).Add(messageId).Add(timestampMs).ToArray();

    /// <summary>Commits to a message's key without revealing it, bound to the sender and message.</summary>
    public static byte[] KeyCommitment(long senderId, ReadOnlySpan<byte> messageId, ReadOnlySpan<byte> key) =>
        SHA256.HashData(new SigningPayload(Domains.LocalMessageKeyCommitment).Add(senderId).Add(messageId).Add(key).ToArray());

    /// <param name="ciphertextHash">SHA-256 of the ciphertext, worked out once for every copy of a message.</param>
    private static byte[] CopySignaturePayload(long senderId, long recipientId, ReadOnlySpan<byte> messageId, long timestampMs, ReadOnlySpan<byte> ciphertextHash,
        ReadOnlySpan<byte> keyCommitment, SealedBox? sealedKey) =>
        new SigningPayload(Domains.LocalMessage)
            .Add(CopyContext(senderId, recipientId, messageId, timestampMs))
            .Add(ciphertextHash)
            .Add(keyCommitment)
            .Add(sealedKey == null ? ReadOnlySpan<byte>.Empty : sealedKey.EphemeralPublicKey.Span)
            .Add(sealedKey == null ? ReadOnlySpan<byte>.Empty : sealedKey.Ciphertext.Span)
            .ToArray();

    /// <summary>Encrypts <paramref name="content"/> once, and seals its key to each recipient's agreement key, signing each copy.</summary>
    /// <param name="recipients">Each recipient's user ID and identity agreement key (X25519, 32 bytes).</param>
    /// <param name="forgedCommitment">Tests only: a commitment to sign other than the key's, as a dishonest sender could.</param>
    public static SendLocalMessage Seal(Content content, IdentityKeys sender, long senderId, IEnumerable<(long RecipientId, byte[] AgreementKey)> recipients, long timestampMs,
        byte[]? forgedCommitment = null) {
        var messageId = RandomNumberGenerator.GetBytes(MessageIdSize);
        var key = RandomNumberGenerator.GetBytes(MessageKeySize);
        try {
            var ciphertext = ChannelCrypto.EncryptWithNonce(key, AssociatedData(senderId, messageId, timestampMs), content.ToByteArray());
            var ciphertextHash = SHA256.HashData(ciphertext);
            var commitment = forgedCommitment ?? KeyCommitment(senderId, messageId, key);
            var message = new SendLocalMessage {
                MessageId = ByteString.CopyFrom(messageId),
                TimestampUnixMs = timestampMs,
                Ciphertext = ByteString.CopyFrom(ciphertext),
                KeyCommitment = ByteString.CopyFrom(commitment),
            };

            foreach (var (recipientId, agreementKey) in recipients) {
                var sealedKey = SealedBoxes.Seal(key, agreementKey, CopyContext(senderId, recipientId, messageId, timestampMs));
                message.Copies.Add(new LocalMessageCopy {
                    RecipientId = recipientId,
                    SealedKey = sealedKey,
                    Signature = ByteString.CopyFrom(sender.Sign(CopySignaturePayload(senderId, recipientId, messageId, timestampMs, ciphertextHash, commitment, sealedKey))),
                });
            }

            return message;
        } finally {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static bool VerifyCopy(long senderId, ReadOnlySpan<byte> messageId, long timestampMs, ReadOnlySpan<byte> ciphertextHash, ReadOnlySpan<byte> keyCommitment,
        LocalMessageCopy copy, ReadOnlySpan<byte> senderSigningKey) =>
        copy.SealedKey != null
        && keyCommitment.Length == KeyCommitmentSize
        && IdentityKeys.Verify(senderSigningKey, CopySignaturePayload(senderId, copy.RecipientId, messageId, timestampMs, ciphertextHash, keyCommitment, copy.SealedKey),
            copy.Signature.Span);

    /// <summary>Whether one copy of a message is signed by the sender's key, for its recipient, this message, time and key commitment.</summary>
    public static bool VerifyCopy(SendLocalMessage message, LocalMessageCopy copy, long senderId, ReadOnlySpan<byte> senderSigningKey) =>
        VerifyCopy(senderId, message.MessageId.Span, message.TimestampUnixMs, SHA256.HashData(message.Ciphertext.Span), message.KeyCommitment.Span, copy, senderSigningKey);

    /// <summary>Whether every copy of a message is signed by the sender's key (the ciphertext hashed once). The server checks this before passing any on.</summary>
    public static bool VerifyCopies(SendLocalMessage message, long senderId, ReadOnlySpan<byte> senderSigningKey) {
        var ciphertextHash = SHA256.HashData(message.Ciphertext.Span);
        foreach (var copy in message.Copies) {
            if (!VerifyCopy(senderId, message.MessageId.Span, message.TimestampUnixMs, ciphertextHash, message.KeyCommitment.Span, copy, senderSigningKey)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Opens a local message delivered to this recipient: only if it is signed by <paramref name="senderSigningKey"/> for
    /// <paramref name="myId"/>, its key opens with this recipient's identity key and matches the signed commitment, and it
    /// decrypts the message.
    /// </summary>
    /// <returns>The content, or null if any check fails.</returns>
    public static Content? Open(LocalMessage message, IdentityKeys me, long myId, ReadOnlySpan<byte> senderSigningKey) {
        if (message.Sender?.User is not { } sender || message.SealedKey == null || message.MessageId.Length != MessageIdSize) {
            return null;
        }

        var senderId = sender.UserId;
        var copy = new LocalMessageCopy { RecipientId = myId, SealedKey = message.SealedKey, Signature = message.Signature };
        if (!VerifyCopy(senderId, message.MessageId.Span, message.TimestampUnixMs, SHA256.HashData(message.Ciphertext.Span), message.KeyCommitment.Span, copy, senderSigningKey)) {
            return null;
        }

        var key = SealedBoxes.Open(message.SealedKey, me, CopyContext(senderId, myId, message.MessageId.Span, message.TimestampUnixMs));
        if (key is not { Length: MessageKeySize }) {
            return null;
        }

        try {
            if (!CryptographicOperations.FixedTimeEquals(KeyCommitment(senderId, message.MessageId.Span, key), message.KeyCommitment.Span)) {
                return null;
            }

            var plaintext = ChannelCrypto.DecryptWithNonce(key, AssociatedData(senderId, message.MessageId.Span, message.TimestampUnixMs), message.Ciphertext.Span);
            return plaintext == null ? null : Content.Parser.ParseFrom(plaintext);
        } catch (InvalidProtocolBufferException) {
            return null;
        } finally {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
