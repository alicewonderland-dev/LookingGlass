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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void IdentityBundleWithLowOrderAgreementKeyFails(byte first) {
        // All zeros, and u = 1: points of small order, so no agreement with them yields a secret.
        using var identity = IdentityKeys.Generate();
        var lowOrder = new byte[32];
        lowOrder[0] = first;
        Assert.False(IdentityKeys.IsValidBundle(BundleWithAgreementKey(identity, lowOrder)));
        Assert.Throws<InvalidOperationException>(() => SealedBoxes.Seal([1, 2, 3], lowOrder, [9]));

        // The same construction with a real key is fine.
        Assert.True(IdentityKeys.IsValidBundle(BundleWithAgreementKey(identity, identity.AgreementPublicKey)));
    }

    /// <summary>A bundle whose binding signature is valid for any agreement key, as a misbehaving client could send.</summary>
    internal static IdentityBundle BundleWithAgreementKey(IdentityKeys identity, byte[] agreementPublicKey) {
        return new IdentityBundle {
            SigningPublicKey = ByteString.CopyFrom(identity.SigningPublicKey),
            AgreementPublicKey = ByteString.CopyFrom(agreementPublicKey),
            BindingSignature = ByteString.CopyFrom(identity.Sign(new SigningPayload(Domains.IdentityBinding).Add(agreementPublicKey).ToArray())),
        };
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
    public void EpochKeyCommitmentIsSignedAndChecked() {
        using var author = IdentityKeys.Generate();
        using var member = IdentityKeys.Generate();
        var key = ChannelCrypto.NewEpochKey();
        var otherKey = ChannelCrypto.NewEpochKey();

        var honest = ChannelCrypto.SealEpochKey(key, ChannelId, 3, author, 1, 2, member.AgreementPublicKey);
        Assert.Equal(EpochKeyCheck.Valid, ChannelCrypto.TryOpenEpochKey(honest, ChannelId, 3, 1, author.SigningPublicKey, member, 2, out var opened));
        Assert.Equal(key, opened);

        // The author seals a different key under the commitment everyone else got: signed, but caught.
        var split = ChannelCrypto.SealEpochKey(otherKey, ChannelId, 3, author, 1, 2, member.AgreementPublicKey);
        split.KeyCommitment = honest.KeyCommitment;
        ChannelCrypto.SignEpochKey(split, ChannelId, 3, author, 1);
        Assert.Equal(EpochKeyCheck.CommitmentMismatch, ChannelCrypto.TryOpenEpochKey(split, ChannelId, 3, 1, author.SigningPublicKey, member, 2, out _));
        Assert.Null(ChannelCrypto.OpenEpochKey(split, ChannelId, 3, 1, author.SigningPublicKey, member, 2));

        // The author signs a box that opens to nothing.
        var junk = honest.Clone();
        junk.Box = new SealedBox { EphemeralPublicKey = honest.Box.EphemeralPublicKey, Ciphertext = ByteString.CopyFrom(new byte[48]) };
        ChannelCrypto.SignEpochKey(junk, ChannelId, 3, author, 1);
        Assert.Equal(EpochKeyCheck.Unreadable, ChannelCrypto.TryOpenEpochKey(junk, ChannelId, 3, 1, author.SigningPublicKey, member, 2, out _));

        // Anyone else changing the commitment breaks the signature.
        var tampered = honest.Clone();
        tampered.KeyCommitment = ByteString.CopyFrom(ChannelCrypto.KeyCommitment(ChannelId, 3, otherKey));
        Assert.Equal(EpochKeyCheck.BadSignature, ChannelCrypto.TryOpenEpochKey(tampered, ChannelId, 3, 1, author.SigningPublicKey, member, 2, out _));
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
