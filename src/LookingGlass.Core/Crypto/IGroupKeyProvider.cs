using Google.Protobuf;
using LookingGlass.Core.Membership;
using LookingGlass.Protocol;

namespace LookingGlass.Core.Crypto;

/// <summary>
/// The group-key layer: each channel epoch's key, how it reaches exactly the members at a
/// membership log position, and what is encrypted under it (the channel name, messages, and
/// the name sealed to an invitee). Clients and the server only use channel keys through this,
/// so the v0.2 sealed epoch keys (<see cref="SealedEpochKeyProvider"/>) can later be replaced by
/// MLS (RFC 9420) together with <see cref="IMembershipProvider"/>, without touching chat, the
/// UI or the server's routing.
/// </summary>
public interface IGroupKeyProvider {
    byte[] NewEpochKey();

    /// <summary>Seals <paramref name="epochKey"/> to each of <paramref name="recipients"/>, for the members at <paramref name="position"/>.</summary>
    /// <param name="createdUnixMs">When it was made, stated and signed in every copy (see <see cref="SealedEpochKey.CreatedUnixMs"/>); null for none.</param>
    /// <exception cref="SealingFailedException">The key couldn't be sealed to one of them.</exception>
    EpochRekey SealToMembers(byte[] epochKey, string channelId, ulong epoch, LogPosition position, IEnumerable<ChannelMember> recipients, IdentityKeys author, long authorId,
        long? createdUnixMs = null);

    bool VerifyEpochKey(SealedEpochKey key, string channelId, ulong epoch, long authorId, ReadOnlySpan<byte> authorSigningKey);

    /// <summary>When the author says, signed, they made the key; null if the key doesn't say (an older client made it) or it isn't signed.</summary>
    long? KeyCreatedAt(SealedEpochKey key, string channelId, ulong epoch, long authorId, ReadOnlySpan<byte> authorSigningKey);

    EpochKeyCheck OpenEpochKey(SealedEpochKey key, string channelId, ulong epoch, long authorId, ReadOnlySpan<byte> authorSigningKey, IdentityKeys me, long myId, out byte[]? epochKey);

    EncryptedName EncryptName(string name, byte[] epochKey, string channelId, ulong epoch, LogPosition position, IdentityKeys author, long authorId, ulong revision = 0, NameSource? carriedFrom = null);

    bool VerifyName(EncryptedName name, string channelId, ReadOnlySpan<byte> authorSigningKey);

    string? DecryptName(EncryptedName? name, string channelId, byte[] epochKey, ReadOnlySpan<byte> authorSigningKey);

    SendMessage EncryptMessage(Content content, byte[] epochKey, string channelId, ulong epoch, IdentityKeys sender, long senderId, long timestampMs);

    bool VerifyMessage(ChatMessage message, ReadOnlySpan<byte> senderSigningKey);

    Content? DecryptMessage(ChatMessage message, byte[] epochKey, ReadOnlySpan<byte> senderSigningKey);

    (SealedBox SealedName, byte[] Signature) SealInvite(string channelName, string channelId, LogPosition invite, long inviteeId, ReadOnlySpan<byte> inviteeAgreementKey, IdentityKeys inviter, long inviterId);

    bool VerifyInvite(string channelId, LogPosition invite, long inviteeId, long inviterId, SealedBox sealedName, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> inviterSigningKey);

    string? OpenInvite(InviteInfo invite, ReadOnlySpan<byte> inviterSigningKey, IdentityKeys me, long myId);
}

/// <param name="KeyCommitment">The commitment every copy carries.</param>
/// <param name="Keys">One sealed, signed copy per recipient.</param>
public sealed record EpochRekey(ByteString KeyCommitment, IReadOnlyList<SealedEpochKey> Keys);

/// <summary>An epoch key couldn't be sealed to <see cref="Member"/>'s agreement key.</summary>
public sealed class SealingFailedException(ChannelMember member, Exception inner)
    : Exception($"The key couldn't be sealed to user {member.UserId} ({inner.Message}).", inner) {
    public ChannelMember Member { get; } = member;
}

/// <summary>The v0.2 group-key layer: per-epoch keys sealed to each member's X25519 key (see <see cref="ChannelCrypto"/>).</summary>
public sealed class SealedEpochKeyProvider : IGroupKeyProvider {
    public static readonly SealedEpochKeyProvider Instance = new();

    public byte[] NewEpochKey() => ChannelCrypto.NewEpochKey();

    public EpochRekey SealToMembers(byte[] epochKey, string channelId, ulong epoch, LogPosition position, IEnumerable<ChannelMember> recipients, IdentityKeys author, long authorId,
        long? createdUnixMs = null) {
        var commitment = ChannelCrypto.KeyCommitment(channelId, epoch, epochKey);
        // One statement for every copy: it signs the commitment, not the recipient.
        var created = createdUnixMs is { } at ? ByteString.CopyFrom(ChannelCrypto.SignKeyCreated(channelId, epoch, commitment, position, author, authorId, at)) : null;
        var keys = new List<SealedEpochKey>();
        foreach (var member in recipients) {
            SealedEpochKey sealedKey;
            try {
                sealedKey = ChannelCrypto.SealEpochKey(epochKey, channelId, epoch, position, author, authorId, member.UserId, member.Keys.AgreementPublicKey);
            } catch (Exception ex) {
                throw new SealingFailedException(member, ex);
            }

            if (created != null) {
                sealedKey.CreatedUnixMs = createdUnixMs!.Value;
                sealedKey.CreatedSignature = created;
            }

            keys.Add(sealedKey);
        }

        return new EpochRekey(ByteString.CopyFrom(commitment), keys);
    }

    public bool VerifyEpochKey(SealedEpochKey key, string channelId, ulong epoch, long authorId, ReadOnlySpan<byte> authorSigningKey) {
        return ChannelCrypto.VerifyEpochKey(key, channelId, epoch, authorId, authorSigningKey);
    }

    public long? KeyCreatedAt(SealedEpochKey key, string channelId, ulong epoch, long authorId, ReadOnlySpan<byte> authorSigningKey) {
        return ChannelCrypto.KeyCreatedAt(key, channelId, epoch, authorId, authorSigningKey);
    }

    public EpochKeyCheck OpenEpochKey(SealedEpochKey key, string channelId, ulong epoch, long authorId, ReadOnlySpan<byte> authorSigningKey, IdentityKeys me, long myId, out byte[]? epochKey) {
        return ChannelCrypto.TryOpenEpochKey(key, channelId, epoch, authorId, authorSigningKey, me, myId, out epochKey);
    }

    public EncryptedName EncryptName(string name, byte[] epochKey, string channelId, ulong epoch, LogPosition position, IdentityKeys author, long authorId, ulong revision = 0, NameSource? carriedFrom = null) {
        return ChannelCrypto.EncryptName(name, epochKey, channelId, epoch, position, author, authorId, revision, carriedFrom);
    }

    public bool VerifyName(EncryptedName name, string channelId, ReadOnlySpan<byte> authorSigningKey) => ChannelCrypto.VerifyName(name, channelId, authorSigningKey);

    public string? DecryptName(EncryptedName? name, string channelId, byte[] epochKey, ReadOnlySpan<byte> authorSigningKey) {
        return ChannelCrypto.DecryptName(name, channelId, epochKey, authorSigningKey);
    }

    public SendMessage EncryptMessage(Content content, byte[] epochKey, string channelId, ulong epoch, IdentityKeys sender, long senderId, long timestampMs) {
        return ChannelCrypto.EncryptMessage(content, epochKey, channelId, epoch, sender, senderId, timestampMs);
    }

    public bool VerifyMessage(ChatMessage message, ReadOnlySpan<byte> senderSigningKey) => ChannelCrypto.VerifyMessage(message, senderSigningKey);

    public Content? DecryptMessage(ChatMessage message, byte[] epochKey, ReadOnlySpan<byte> senderSigningKey) {
        return ChannelCrypto.DecryptMessage(message, epochKey, senderSigningKey);
    }

    public (SealedBox SealedName, byte[] Signature) SealInvite(string channelName, string channelId, LogPosition invite, long inviteeId, ReadOnlySpan<byte> inviteeAgreementKey, IdentityKeys inviter, long inviterId) {
        return ChannelCrypto.SealInvite(channelName, channelId, invite, inviteeId, inviteeAgreementKey, inviter, inviterId);
    }

    public bool VerifyInvite(string channelId, LogPosition invite, long inviteeId, long inviterId, SealedBox sealedName, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> inviterSigningKey) {
        return ChannelCrypto.VerifyInvite(channelId, invite, inviteeId, inviterId, sealedName, signature, inviterSigningKey);
    }

    public string? OpenInvite(InviteInfo invite, ReadOnlySpan<byte> inviterSigningKey, IdentityKeys me, long myId) {
        return ChannelCrypto.OpenInvite(invite, inviterSigningKey, me, myId);
    }
}
