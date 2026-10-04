using Google.Protobuf;
using WonderlandChat.Core.Crypto;
using WonderlandChat.Protocol;

namespace WonderlandChat.Tests;

public class CryptoTests {
    private const string ChannelId = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void IdentityBundleVerifies() {
        using var identity = IdentityKeys.Generate();
        Assert.True(IdentityKeys.IsValidBundle(identity.ToBundle()));
    }

    [Fact]
    public void IdentityBundleWithSwappedAgreementKeyFails() {
        using var a = IdentityKeys.Generate();
        using var b = IdentityKeys.Generate();
        var bundle = a.ToBundle();
        bundle.AgreementPublicKey = ByteString.CopyFrom(b.AgreementPublicKey);
        Assert.False(IdentityKeys.IsValidBundle(bundle));
    }

    [Fact]
    public void IdentityRoundTripsThroughExport() {
        using var original = IdentityKeys.Generate();
        var (signing, agreement) = original.ExportPrivateKeys();
        using var restored = IdentityKeys.Import(signing, agreement);
        Assert.Equal(original.SigningPublicKey, restored.SigningPublicKey);
        Assert.Equal(original.Fingerprint, restored.Fingerprint);
    }

    [Fact]
    public void FingerprintIsFiveGroupsOfFiveDigits() {
        using var identity = IdentityKeys.Generate();
        Assert.Matches(@"^\d{5}( \d{5}){4}$", identity.Fingerprint);
    }

    [Fact]
    public void SealedBoxOpensOnlyForRecipientAndContext() {
        using var recipient = IdentityKeys.Generate();
        using var other = IdentityKeys.Generate();
        var box = SealedBoxes.Seal([1, 2, 3], recipient.AgreementPublicKey, [9]);

        Assert.Equal([1, 2, 3], SealedBoxes.Open(box, recipient, [9]));
        Assert.Null(SealedBoxes.Open(box, other, [9]));
        Assert.Null(SealedBoxes.Open(box, recipient, [8]));
    }

    [Fact]
    public void EpochKeyOpensForRecipientAndRejectsWrongAuthor() {
        using var author = IdentityKeys.Generate();
        using var impostor = IdentityKeys.Generate();
        using var member = IdentityKeys.Generate();
        var key = ChannelCrypto.NewEpochKey();

        var sealedKey = ChannelCrypto.SealEpochKey(key, ChannelId, 3, author, 1, 2, member.AgreementPublicKey);
        Assert.Equal(key, ChannelCrypto.OpenEpochKey(sealedKey, ChannelId, 3, 1, author.SigningPublicKey, member, 2));

        // A server substituting its own author key is caught by the signature.
        Assert.Null(ChannelCrypto.OpenEpochKey(sealedKey, ChannelId, 3, 1, impostor.SigningPublicKey, member, 2));
        // The key can't be replayed into another epoch or channel.
        Assert.Null(ChannelCrypto.OpenEpochKey(sealedKey, ChannelId, 4, 1, author.SigningPublicKey, member, 2));
        Assert.Null(ChannelCrypto.OpenEpochKey(sealedKey, "fedcba9876543210fedcba9876543210", 3, 1, author.SigningPublicKey, member, 2));
    }

    [Fact]
    public void MessageDecryptsAndTamperingIsDetected() {
        using var sender = IdentityKeys.Generate();
        using var impostor = IdentityKeys.Generate();
        var key = ChannelCrypto.NewEpochKey();
        var content = new Content { Text = new TextContent { Text = "hello" } };

        var sent = ChannelCrypto.EncryptMessage(content, key, ChannelId, 0, sender, 7, 1000);
        var received = ToChatMessage(sent, 7);
        Assert.Equal("hello", ChannelCrypto.DecryptMessage(received, key, sender.SigningPublicKey)?.Text.Text);

        // Re-attributed to another sender: rejected.
        Assert.Null(ChannelCrypto.DecryptMessage(ToChatMessage(sent, 8), key, sender.SigningPublicKey));
        // Forged by someone else's key: rejected.
        Assert.Null(ChannelCrypto.DecryptMessage(received, key, impostor.SigningPublicKey));
        // Ciphertext altered: rejected.
        var tampered = received.Clone();
        var bytes = tampered.Ciphertext.ToByteArray();
        bytes[^1] ^= 1;
        tampered.Ciphertext = ByteString.CopyFrom(bytes);
        Assert.Null(ChannelCrypto.DecryptMessage(tampered, key, sender.SigningPublicKey));
        // Wrong epoch key: rejected.
        Assert.Null(ChannelCrypto.DecryptMessage(received, ChannelCrypto.NewEpochKey(), sender.SigningPublicKey));
    }

    [Fact]
    public void ChannelNameRoundTrips() {
        using var author = IdentityKeys.Generate();
        var key = ChannelCrypto.NewEpochKey();
        var name = ChannelCrypto.EncryptName("Tea Party", key, ChannelId, 2, author, 5);
        Assert.True(ChannelCrypto.VerifyName(name, ChannelId, author.SigningPublicKey));
        Assert.Equal("Tea Party", ChannelCrypto.DecryptName(name, ChannelId, key, author.SigningPublicKey));
    }

    [Fact]
    public void InviteOpensForInviteeOnly() {
        using var inviter = IdentityKeys.Generate();
        using var invitee = IdentityKeys.Generate();
        using var other = IdentityKeys.Generate();
        var (box, signature) = ChannelCrypto.SealInvite("Tea Party", ChannelId, 20, invitee.AgreementPublicKey, inviter, 10);
        var invite = new InviteInfo {
            ChannelId = ChannelId,
            Inviter = new User { UserId = 10 },
            SealedName = box,
            Signature = ByteString.CopyFrom(signature),
        };

        Assert.Equal("Tea Party", ChannelCrypto.OpenInvite(invite, inviter.SigningPublicKey, invitee, 20));
        Assert.Null(ChannelCrypto.OpenInvite(invite, inviter.SigningPublicKey, other, 21));
        Assert.Null(ChannelCrypto.OpenInvite(invite, other.SigningPublicKey, invitee, 20));
    }

    private static ChatMessage ToChatMessage(SendMessage sent, long senderId) => new() {
        ChannelId = sent.ChannelId,
        Epoch = sent.Epoch,
        SenderId = senderId,
        MessageId = sent.MessageId,
        TimestampUnixMs = sent.TimestampUnixMs,
        Ciphertext = sent.Ciphertext,
        Signature = sent.Signature,
    };
}
