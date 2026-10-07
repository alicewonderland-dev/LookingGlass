using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using NSec.Cryptography;
using LookingGlass.Core.Membership;
using LookingGlass.Protocol;

namespace LookingGlass.Core.Crypto;

/// <summary>
/// Everything encrypted or signed inside a channel: epoch keys, channel names,
/// invites and messages. Payload layouts here are part of the protocol; the
/// server uses the same functions to verify signatures. Clients and the server
/// reach these through <see cref="IGroupKeyProvider"/>; tests call them directly
/// to forge what a dishonest member could make.
/// </summary>
public static class ChannelCrypto {
    public const int EpochKeySize = 32;
    public const int KeyCommitmentSize = 32;
    private static readonly AeadAlgorithm Aead = AeadAlgorithm.XChaCha20Poly1305;

    public static byte[] NewEpochKey() => RandomNumberGenerator.GetBytes(EpochKeySize);

    // ------------------------------------------------------------ epoch keys

    private static byte[] EpochKeyContext(string channelId, ulong epoch, long recipientId, long authorId) {
        return new SigningPayload(Domains.EpochKey)
            .Add(channelId).Add(epoch).Add(recipientId).Add(authorId)
            .ToArray();
    }

    private static byte[] EpochKeySignaturePayload(string channelId, ulong epoch, long authorId, SealedEpochKey key) {
        return new SigningPayload(Domains.EpochKey)
            .Add(EpochKeyContext(channelId, epoch, key.RecipientId, authorId))
            .Add(key.Box == null ? ReadOnlySpan<byte>.Empty : key.Box.EphemeralPublicKey.Span)
            .Add(key.Box == null ? ReadOnlySpan<byte>.Empty : key.Box.Ciphertext.Span)
            .Add(key.KeyCommitment.Span)
            .Add(key.LogPosition)
            .ToArray();
    }

    /// <summary>
    /// Commits to an epoch key without revealing it. Every copy of a key carries
    /// this, signed, so a recipient can tell whether the author gave everyone
    /// the same key.
    /// </summary>
    public static byte[] KeyCommitment(string channelId, ulong epoch, ReadOnlySpan<byte> epochKey) {
        return SHA256.HashData(new SigningPayload(Domains.EpochKeyCommitment).Add(channelId).Add(epoch).Add(epochKey).ToArray());
    }

    /// <param name="position">The membership log position the key is made for: it goes to exactly the members there.</param>
    public static SealedEpochKey SealEpochKey(byte[] epochKey, string channelId, ulong epoch, LogPosition position, IdentityKeys author, long authorId, long recipientId, ReadOnlySpan<byte> recipientAgreementKey) {
        var sealedKey = new SealedEpochKey {
            RecipientId = recipientId,
            LogPosition = position.Clone(),
            Box = SealedBoxes.Seal(epochKey, recipientAgreementKey, EpochKeyContext(channelId, epoch, recipientId, authorId)),
            KeyCommitment = ByteString.CopyFrom(KeyCommitment(channelId, epoch, epochKey)),
        };
        SignEpochKey(sealedKey, channelId, epoch, author, authorId);
        return sealedKey;
    }

    /// <summary>(Re)signs a sealed key as it stands. Tests use it to forge keys a dishonest member could make.</summary>
    internal static void SignEpochKey(SealedEpochKey key, string channelId, ulong epoch, IdentityKeys author, long authorId) {
        key.Signature = ByteString.CopyFrom(author.Sign(EpochKeySignaturePayload(channelId, epoch, authorId, key)));
    }

    public static bool VerifyEpochKey(SealedEpochKey key, string channelId, ulong epoch, long authorId, ReadOnlySpan<byte> authorSigningKey) {
        return IdentityKeys.Verify(authorSigningKey, EpochKeySignaturePayload(channelId, epoch, authorId, key), key.Signature.Span);
    }

    private static byte[] KeyCreatedPayload(string channelId, ulong epoch, long authorId, ReadOnlySpan<byte> commitment, LogPosition? position, long createdMs) {
        return new SigningPayload(Domains.EpochKeyCreated).Add(channelId).Add(epoch).Add(authorId).Add(commitment).Add(position).Add(createdMs).ToArray();
    }

    /// <summary>
    /// The author's signed statement of when they made an epoch key (see <see cref="SealedEpochKey.CreatedUnixMs"/>): over
    /// the channel, epoch, author, the key's commitment and log position, and the time. The same for every copy of the key.
    /// </summary>
    public static byte[] SignKeyCreated(string channelId, ulong epoch, ReadOnlySpan<byte> commitment, LogPosition position, IdentityKeys author, long authorId, long createdMs) {
        return author.Sign(KeyCreatedPayload(channelId, epoch, authorId, commitment, position, createdMs));
    }

    /// <summary>When the author says they made a key, if the key says so and they signed it; otherwise null (keys made by older clients don't say).</summary>
    public static long? KeyCreatedAt(SealedEpochKey key, string channelId, ulong epoch, long authorId, ReadOnlySpan<byte> authorSigningKey) {
        if (key.CreatedSignature.IsEmpty) {
            return null;
        }

        return IdentityKeys.Verify(authorSigningKey, KeyCreatedPayload(channelId, epoch, authorId, key.KeyCommitment.Span, key.LogPosition, key.CreatedUnixMs), key.CreatedSignature.Span)
            ? key.CreatedUnixMs
            : null;
    }

    /// <summary>
    /// Verifies and opens a sealed epoch key. <see cref="EpochKeyCheck.Unreadable"/>
    /// and <see cref="EpochKeyCheck.CommitmentMismatch"/> mean the author really
    /// signed a key that is no use to this recipient.
    /// </summary>
    public static EpochKeyCheck TryOpenEpochKey(SealedEpochKey key, string channelId, ulong epoch, long authorId, ReadOnlySpan<byte> authorSigningKey, IdentityKeys me, long myId, out byte[]? epochKey) {
        epochKey = null;
        if (key.RecipientId != myId || !VerifyEpochKey(key, channelId, epoch, authorId, authorSigningKey)) {
            return EpochKeyCheck.BadSignature;
        }

        var opened = SealedBoxes.Open(key.Box, me, EpochKeyContext(channelId, epoch, myId, authorId));
        if (opened is not { Length: EpochKeySize }) {
            return EpochKeyCheck.Unreadable;
        }

        if (!CryptographicOperations.FixedTimeEquals(KeyCommitment(channelId, epoch, opened), key.KeyCommitment.Span)) {
            return EpochKeyCheck.CommitmentMismatch;
        }

        epochKey = opened;
        return EpochKeyCheck.Valid;
    }

    /// <returns>The epoch key, or null if the signature, decryption or commitment check fails.</returns>
    public static byte[]? OpenEpochKey(SealedEpochKey key, string channelId, ulong epoch, long authorId, ReadOnlySpan<byte> authorSigningKey, IdentityKeys me, long myId) {
        return TryOpenEpochKey(key, channelId, epoch, authorId, authorSigningKey, me, myId, out var epochKey) == EpochKeyCheck.Valid ? epochKey : null;
    }

    // ------------------------------------------------------------ channel names

    private static byte[] NameAssociatedData(string channelId, ulong epoch, ulong revision, long authorId, LogPosition? position, NameSource? carriedFrom) {
        var payload = new SigningPayload(Domains.ChannelName).Add(channelId).Add(epoch).Add(revision).Add(authorId).Add(position);
        // Fields are length-prefixed, so a name with a source can't pass for one without.
        if (carriedFrom != null) {
            payload.Add(carriedFrom.Epoch).Add(carriedFrom.Revision);
        }

        return payload.ToArray();
    }

    private static byte[] NameSignaturePayload(string channelId, EncryptedName name) {
        return new SigningPayload(Domains.ChannelName)
            .Add(NameAssociatedData(channelId, name.Epoch, name.Revision, name.AuthorId, name.LogPosition, name.CarriedFrom))
            .Add(name.Ciphertext.Span)
            .ToArray();
    }

    /// <param name="revision">Incremented by every rename within the epoch; 0 for a new channel or a rekey.</param>
    /// <param name="position">The membership log position the name is made at. Signed, so a name can't outlive a change of members.</param>
    /// <param name="carriedFrom">For a rekey: the version of the name being carried into the new epoch.</param>
    public static EncryptedName EncryptName(string name, byte[] epochKey, string channelId, ulong epoch, LogPosition position, IdentityKeys author, long authorId, ulong revision = 0, NameSource? carriedFrom = null) {
        var encrypted = new EncryptedName {
            Epoch = epoch,
            Revision = revision,
            AuthorId = authorId,
            CarriedFrom = carriedFrom,
            LogPosition = position.Clone(),
            Ciphertext = ByteString.CopyFrom(EncryptWithNonce(epochKey, NameAssociatedData(channelId, epoch, revision, authorId, position, carriedFrom), Encoding.UTF8.GetBytes(name))),
        };
        encrypted.Signature = ByteString.CopyFrom(author.Sign(NameSignaturePayload(channelId, encrypted)));
        return encrypted;
    }

    public static bool VerifyName(EncryptedName name, string channelId, ReadOnlySpan<byte> authorSigningKey) {
        return IdentityKeys.Verify(authorSigningKey, NameSignaturePayload(channelId, name), name.Signature.Span);
    }

    public static string? DecryptName(EncryptedName? name, string channelId, byte[] epochKey, ReadOnlySpan<byte> authorSigningKey) {
        if (name == null || !VerifyName(name, channelId, authorSigningKey)) {
            return null;
        }

        var plaintext = DecryptWithNonce(epochKey, NameAssociatedData(channelId, name.Epoch, name.Revision, name.AuthorId, name.LogPosition, name.CarriedFrom), name.Ciphertext.Span);
        return plaintext == null ? null : Encoding.UTF8.GetString(plaintext);
    }

    // ------------------------------------------------------------ invites

    /// <param name="invite">The invite's entry in the membership log, so a sealed name can't be moved to another invite.</param>
    private static byte[] InviteContext(string channelId, long inviteeId, long inviterId, LogPosition? invite) {
        return new SigningPayload(Domains.Invite).Add(channelId).Add(inviteeId).Add(inviterId).Add(invite).ToArray();
    }

    private static byte[] InviteSignaturePayload(string channelId, long inviteeId, long inviterId, LogPosition? invite, SealedBox sealedName) {
        return new SigningPayload(Domains.Invite)
            .Add(InviteContext(channelId, inviteeId, inviterId, invite))
            .Add(sealedName.EphemeralPublicKey.Span)
            .Add(sealedName.Ciphertext.Span)
            .ToArray();
    }

    public static (SealedBox SealedName, byte[] Signature) SealInvite(string channelName, string channelId, LogPosition invite, long inviteeId, ReadOnlySpan<byte> inviteeAgreementKey, IdentityKeys inviter, long inviterId) {
        var box = SealedBoxes.Seal(Encoding.UTF8.GetBytes(channelName), inviteeAgreementKey, InviteContext(channelId, inviteeId, inviterId, invite));
        return (box, inviter.Sign(InviteSignaturePayload(channelId, inviteeId, inviterId, invite, box)));
    }

    /// <summary>Checks the inviter's signature over an invite. The server uses this before storing one.</summary>
    public static bool VerifyInvite(string channelId, LogPosition invite, long inviteeId, long inviterId, SealedBox sealedName, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> inviterSigningKey) {
        return IdentityKeys.Verify(inviterSigningKey, InviteSignaturePayload(channelId, inviteeId, inviterId, invite, sealedName), signature);
    }

    public static string? OpenInvite(InviteInfo invite, ReadOnlySpan<byte> inviterSigningKey, IdentityKeys me, long myId) {
        if (invite.SealedName == null || invite.Inviter == null || invite.Entry == null) {
            return null;
        }

        var inviterId = invite.Inviter.UserId;
        var position = MembershipEntries.PositionOf(invite.Entry);
        if (!VerifyInvite(invite.ChannelId, position, myId, inviterId, invite.SealedName, invite.Signature.Span, inviterSigningKey)) {
            return null;
        }

        var plaintext = SealedBoxes.Open(invite.SealedName, me, InviteContext(invite.ChannelId, myId, inviterId, position));
        return plaintext == null ? null : Encoding.UTF8.GetString(plaintext);
    }

    // ------------------------------------------------------------ messages

    private static byte[] MessageAssociatedData(string channelId, ulong epoch, long senderId, ReadOnlySpan<byte> messageId, long timestampMs) {
        return new SigningPayload(Domains.Message)
            .Add(channelId).Add(epoch).Add(senderId).Add(messageId).Add(timestampMs)
            .ToArray();
    }

    private static byte[] MessageSignaturePayload(string channelId, ulong epoch, long senderId, ReadOnlySpan<byte> messageId, long timestampMs, ReadOnlySpan<byte> ciphertext) {
        return new SigningPayload(Domains.Message)
            .Add(MessageAssociatedData(channelId, epoch, senderId, messageId, timestampMs))
            .Add(ciphertext)
            .ToArray();
    }

    public static SendMessage EncryptMessage(Content content, byte[] epochKey, string channelId, ulong epoch, IdentityKeys sender, long senderId, long timestampMs) {
        var messageId = RandomNumberGenerator.GetBytes(16);
        var ad = MessageAssociatedData(channelId, epoch, senderId, messageId, timestampMs);
        var ciphertext = EncryptWithNonce(epochKey, ad, content.ToByteArray());
        var signature = sender.Sign(MessageSignaturePayload(channelId, epoch, senderId, messageId, timestampMs, ciphertext));

        return new SendMessage {
            ChannelId = channelId,
            Epoch = epoch,
            MessageId = ByteString.CopyFrom(messageId),
            TimestampUnixMs = timestampMs,
            Ciphertext = ByteString.CopyFrom(ciphertext),
            Signature = ByteString.CopyFrom(signature),
        };
    }

    public static bool VerifyMessage(ChatMessage message, ReadOnlySpan<byte> senderSigningKey) {
        var payload = MessageSignaturePayload(message.ChannelId, message.Epoch, message.SenderId, message.MessageId.Span, message.TimestampUnixMs, message.Ciphertext.Span);
        return IdentityKeys.Verify(senderSigningKey, payload, message.Signature.Span);
    }

    /// <returns>The content, or null if the signature, decryption or parsing fails.</returns>
    public static Content? DecryptMessage(ChatMessage message, byte[] epochKey, ReadOnlySpan<byte> senderSigningKey) {
        if (!VerifyMessage(message, senderSigningKey)) {
            return null;
        }

        var ad = MessageAssociatedData(message.ChannelId, message.Epoch, message.SenderId, message.MessageId.Span, message.TimestampUnixMs);
        var plaintext = DecryptWithNonce(epochKey, ad, message.Ciphertext.Span);
        if (plaintext == null) {
            return null;
        }

        try {
            return Content.Parser.ParseFrom(plaintext);
        } catch (InvalidProtocolBufferException) {
            return null;
        }
    }

    // ------------------------------------------------------------ AEAD helpers

    private static byte[] EncryptWithNonce(byte[] rawKey, byte[] associatedData, ReadOnlySpan<byte> plaintext) {
        using var key = Key.Import(Aead, rawKey, KeyBlobFormat.RawSymmetricKey);
        var nonce = RandomNumberGenerator.GetBytes(Aead.NonceSize);
        var ciphertext = Aead.Encrypt(key, nonce, associatedData, plaintext);
        var result = new byte[nonce.Length + ciphertext.Length];
        nonce.CopyTo(result, 0);
        ciphertext.CopyTo(result, nonce.Length);
        return result;
    }

    private static byte[]? DecryptWithNonce(byte[] rawKey, byte[] associatedData, ReadOnlySpan<byte> data) {
        if (rawKey.Length != EpochKeySize || data.Length < Aead.NonceSize + Aead.TagSize) {
            return null;
        }

        using var key = Key.Import(Aead, rawKey, KeyBlobFormat.RawSymmetricKey);
        return Aead.Decrypt(key, data[..Aead.NonceSize], associatedData, data[Aead.NonceSize..]);
    }
}

public enum EpochKeyCheck {
    Valid,

    /// <summary>Not addressed to this recipient, or not signed by the claimed author. Could be forged by the server.</summary>
    BadSignature,

    /// <summary>Signed by the author, but the box doesn't open to a key for this recipient.</summary>
    Unreadable,

    /// <summary>Signed by the author, but the key inside doesn't match the commitment everyone else got.</summary>
    CommitmentMismatch,
}
