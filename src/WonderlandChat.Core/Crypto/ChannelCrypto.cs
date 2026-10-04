using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using NSec.Cryptography;
using WonderlandChat.Protocol;

namespace WonderlandChat.Core.Crypto;

/// <summary>
/// Everything encrypted or signed inside a channel: epoch keys, channel names,
/// invites and messages. Payload layouts here are part of the protocol; the
/// server uses the same functions to verify signatures.
/// </summary>
public static class ChannelCrypto {
    public const int EpochKeySize = 32;
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
            .ToArray();
    }

    public static SealedEpochKey SealEpochKey(byte[] epochKey, string channelId, ulong epoch, IdentityKeys author, long authorId, long recipientId, ReadOnlySpan<byte> recipientAgreementKey) {
        var sealedKey = new SealedEpochKey {
            RecipientId = recipientId,
            Box = SealedBoxes.Seal(epochKey, recipientAgreementKey, EpochKeyContext(channelId, epoch, recipientId, authorId)),
        };
        sealedKey.Signature = ByteString.CopyFrom(author.Sign(EpochKeySignaturePayload(channelId, epoch, authorId, sealedKey)));
        return sealedKey;
    }

    public static bool VerifyEpochKey(SealedEpochKey key, string channelId, ulong epoch, long authorId, ReadOnlySpan<byte> authorSigningKey) {
        return IdentityKeys.Verify(authorSigningKey, EpochKeySignaturePayload(channelId, epoch, authorId, key), key.Signature.Span);
    }

    /// <returns>The epoch key, or null if the signature or decryption fails.</returns>
    public static byte[]? OpenEpochKey(SealedEpochKey key, string channelId, ulong epoch, long authorId, ReadOnlySpan<byte> authorSigningKey, IdentityKeys me, long myId) {
        if (key.RecipientId != myId || !VerifyEpochKey(key, channelId, epoch, authorId, authorSigningKey)) {
            return null;
        }

        var opened = SealedBoxes.Open(key.Box, me, EpochKeyContext(channelId, epoch, myId, authorId));
        return opened is { Length: EpochKeySize } ? opened : null;
    }

    // ------------------------------------------------------------ channel names

    private static byte[] NameAssociatedData(string channelId, ulong epoch, ulong revision, long authorId) {
        return new SigningPayload(Domains.ChannelName).Add(channelId).Add(epoch).Add(revision).Add(authorId).ToArray();
    }

    private static byte[] NameSignaturePayload(string channelId, EncryptedName name) {
        return new SigningPayload(Domains.ChannelName)
            .Add(NameAssociatedData(channelId, name.Epoch, name.Revision, name.AuthorId))
            .Add(name.Ciphertext.Span)
            .ToArray();
    }

    /// <param name="revision">Incremented by every rename within the epoch; 0 for a new channel or a rekey.</param>
    public static EncryptedName EncryptName(string name, byte[] epochKey, string channelId, ulong epoch, IdentityKeys author, long authorId, ulong revision = 0) {
        var encrypted = new EncryptedName {
            Epoch = epoch,
            Revision = revision,
            AuthorId = authorId,
            Ciphertext = ByteString.CopyFrom(EncryptWithNonce(epochKey, NameAssociatedData(channelId, epoch, revision, authorId), Encoding.UTF8.GetBytes(name))),
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

        var plaintext = DecryptWithNonce(epochKey, NameAssociatedData(channelId, name.Epoch, name.Revision, name.AuthorId), name.Ciphertext.Span);
        return plaintext == null ? null : Encoding.UTF8.GetString(plaintext);
    }

    // ------------------------------------------------------------ invites

    private static byte[] InviteContext(string channelId, long inviteeId, long inviterId) {
        return new SigningPayload(Domains.Invite).Add(channelId).Add(inviteeId).Add(inviterId).ToArray();
    }

    private static byte[] InviteSignaturePayload(string channelId, long inviteeId, long inviterId, SealedBox sealedName) {
        return new SigningPayload(Domains.Invite)
            .Add(InviteContext(channelId, inviteeId, inviterId))
            .Add(sealedName.EphemeralPublicKey.Span)
            .Add(sealedName.Ciphertext.Span)
            .ToArray();
    }

    public static (SealedBox SealedName, byte[] Signature) SealInvite(string channelName, string channelId, long inviteeId, ReadOnlySpan<byte> inviteeAgreementKey, IdentityKeys inviter, long inviterId) {
        var box = SealedBoxes.Seal(Encoding.UTF8.GetBytes(channelName), inviteeAgreementKey, InviteContext(channelId, inviteeId, inviterId));
        return (box, inviter.Sign(InviteSignaturePayload(channelId, inviteeId, inviterId, box)));
    }

    public static string? OpenInvite(InviteInfo invite, ReadOnlySpan<byte> inviterSigningKey, IdentityKeys me, long myId) {
        if (invite.SealedName == null || invite.Inviter == null) {
            return null;
        }

        var inviterId = invite.Inviter.UserId;
        if (!IdentityKeys.Verify(inviterSigningKey, InviteSignaturePayload(invite.ChannelId, myId, inviterId, invite.SealedName), invite.Signature.Span)) {
            return null;
        }

        var plaintext = SealedBoxes.Open(invite.SealedName, me, InviteContext(invite.ChannelId, myId, inviterId));
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
