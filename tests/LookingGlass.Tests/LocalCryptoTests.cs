using Google.Protobuf;
using LookingGlass.Core.Crypto;
using LookingGlass.Protocol;

namespace LookingGlass.Tests;

/// <summary>
/// Local chat's encryption (see <see cref="LocalCrypto"/>): one ciphertext under a key made for the message, that key sealed
/// to each recipient's identity key, and each copy signed by the sender. Only its recipient opens a copy, only the sender's
/// key verifies it, and nothing in it can be moved to another recipient, message or time.
/// </summary>
public sealed class LocalCryptoTests {
    private const long SenderId = 101;
    private const long BobId = 202;
    private const long CarolId = 303;

    private static Content Text(string text) => new() { Text = new TextContent { Text = text } };

    private static (SendLocalMessage Sent, IdentityKeys Sender, IdentityKeys Bob, IdentityKeys Carol) SealToBobAndCarol(string text = "hello") {
        var sender = IdentityKeys.Generate();
        var bob = IdentityKeys.Generate();
        var carol = IdentityKeys.Generate();
        var sent = LocalCrypto.Seal(Text(text), sender, SenderId, [(BobId, bob.AgreementPublicKey.ToArray()), (CarolId, carol.AgreementPublicKey.ToArray())], 1_700_000_000_000);
        return (sent, sender, bob, carol);
    }

    /// <summary>The copy for <paramref name="recipientId"/>, as the server passes it on.</summary>
    private static LocalMessage Delivered(SendLocalMessage sent, long recipientId) {
        var copy = sent.Copies.Single(c => c.RecipientId == recipientId);
        return new LocalMessage {
            Sender = new UserIdentity { User = new User { UserId = SenderId, Name = "Alice Liddell", WorldName = "Lich" } },
            MessageId = sent.MessageId,
            TimestampUnixMs = sent.TimestampUnixMs,
            Ciphertext = sent.Ciphertext,
            KeyCommitment = sent.KeyCommitment,
            SealedKey = copy.SealedKey,
            Signature = copy.Signature,
        };
    }

    [Fact]
    public void EachRecipientOpensTheirOwnCopy() {
        var (sent, sender, bob, carol) = SealToBobAndCarol();

        Assert.Equal(16, sent.MessageId.Length);
        Assert.Equal(2, sent.Copies.Count);
        Assert.Equal("hello", LocalCrypto.Open(Delivered(sent, BobId), bob, BobId, sender.SigningPublicKey)?.Text.Text);
        Assert.Equal("hello", LocalCrypto.Open(Delivered(sent, CarolId), carol, CarolId, sender.SigningPublicKey)?.Text.Text);
    }

    [Fact]
    public void ACopyOpensForNobodyElse() {
        var (sent, sender, _, carol) = SealToBobAndCarol();

        // Carol given Bob's copy, as herself or claiming to be Bob.
        Assert.Null(LocalCrypto.Open(Delivered(sent, BobId), carol, CarolId, sender.SigningPublicKey));
        Assert.Null(LocalCrypto.Open(Delivered(sent, BobId), carol, BobId, sender.SigningPublicKey));
    }

    [Fact]
    public void OnlyTheSendersKeyVerifiesIt() {
        var (sent, _, bob, _) = SealToBobAndCarol();
        using var someoneElse = IdentityKeys.Generate();

        Assert.Null(LocalCrypto.Open(Delivered(sent, BobId), bob, BobId, someoneElse.SigningPublicKey));
    }

    [Fact]
    public void ItCantBePassedOffAsFromSomeoneElseOrAnotherTimeOrMessage() {
        var (sent, sender, bob, _) = SealToBobAndCarol();

        var otherSender = Delivered(sent, BobId);
        otherSender.Sender.User.UserId = 999;
        Assert.Null(LocalCrypto.Open(otherSender, bob, BobId, sender.SigningPublicKey));

        var later = Delivered(sent, BobId);
        later.TimestampUnixMs += 60_000;
        Assert.Null(LocalCrypto.Open(later, bob, BobId, sender.SigningPublicKey));

        var otherId = Delivered(sent, BobId);
        otherId.MessageId = ByteString.CopyFrom(new byte[16]);
        Assert.Null(LocalCrypto.Open(otherId, bob, BobId, sender.SigningPublicKey));
    }

    [Fact]
    public void ACopysKeyCantBeMovedToAnotherMessage() {
        var (first, sender, bob, _) = SealToBobAndCarol("first");
        var second = LocalCrypto.Seal(Text("second"), sender, SenderId, [(BobId, bob.AgreementPublicKey.ToArray())], first.TimestampUnixMs);

        // The second message's text with the first's key and signature (or the other way round).
        var mixed = Delivered(first, BobId);
        mixed.Ciphertext = second.Ciphertext;
        Assert.Null(LocalCrypto.Open(mixed, bob, BobId, sender.SigningPublicKey));
    }

    [Fact]
    public void TamperingIsCaught() {
        var (sent, sender, bob, _) = SealToBobAndCarol();

        var text = Delivered(sent, BobId);
        var bytes = text.Ciphertext.ToByteArray();
        bytes[^1] ^= 1;
        text.Ciphertext = ByteString.CopyFrom(bytes);
        Assert.Null(LocalCrypto.Open(text, bob, BobId, sender.SigningPublicKey));

        var box = Delivered(sent, BobId);
        box.SealedKey = new SealedBox { EphemeralPublicKey = box.SealedKey.EphemeralPublicKey, Ciphertext = ByteString.CopyFrom(new byte[48]) };
        Assert.Null(LocalCrypto.Open(box, bob, BobId, sender.SigningPublicKey));

        var missing = Delivered(sent, BobId);
        missing.SealedKey = null;
        Assert.Null(LocalCrypto.Open(missing, bob, BobId, sender.SigningPublicKey));
        missing.Sender = null;
        Assert.Null(LocalCrypto.Open(missing, bob, BobId, sender.SigningPublicKey));
    }

    [Fact]
    public void TheServerChecksEachCopysSignatureWithoutOpeningIt() {
        var (sent, sender, _, _) = SealToBobAndCarol();
        using var someoneElse = IdentityKeys.Generate();

        foreach (var copy in sent.Copies) {
            Assert.True(LocalCrypto.VerifyCopy(sent, copy, SenderId, sender.SigningPublicKey));
            Assert.False(LocalCrypto.VerifyCopy(sent, copy, SenderId, someoneElse.SigningPublicKey));
            Assert.False(LocalCrypto.VerifyCopy(sent, copy, SenderId + 1, sender.SigningPublicKey));
        }

        // Bob's copy readdressed to Carol.
        var readdressed = sent.Copies[0].Clone();
        readdressed.RecipientId = CarolId;
        Assert.False(LocalCrypto.VerifyCopy(sent, readdressed, SenderId, sender.SigningPublicKey));
    }

    [Fact]
    public void TheMessagesKeyIsCommittedToAndChecked() {
        var (sent, sender, bob, _) = SealToBobAndCarol();
        Assert.Equal(32, sent.KeyCommitment.Length);

        // Another commitment than the one signed: the signature fails.
        var swapped = Delivered(sent, BobId);
        swapped.KeyCommitment = ByteString.CopyFrom(new byte[32]);
        Assert.Null(LocalCrypto.Open(swapped, bob, BobId, sender.SigningPublicKey));

        // A sender who signs a commitment that isn't to the key they sealed (to give recipients different keys, or a
        // ciphertext that opens two ways): the copy verifies, but doesn't open.
        var lying = LocalCrypto.Seal(Text("two ways"), sender, SenderId, [(BobId, bob.AgreementPublicKey)], 1_700_000_000_000,
            forgedCommitment: new byte[32]);
        Assert.True(LocalCrypto.VerifyCopy(lying, lying.Copies[0], SenderId, sender.SigningPublicKey));
        Assert.Null(LocalCrypto.Open(Delivered(lying, BobId), bob, BobId, sender.SigningPublicKey));
    }

    [Fact]
    public void ALocalSealedKeyIsNoChannelKeyAndALocalSignatureNoChannelMessages() {
        var (sent, sender, bob, _) = SealToBobAndCarol();
        var copy = sent.Copies.Single(c => c.RecipientId == BobId);

        // Bob's copy of the message's key offered as a channel's epoch key from the sender.
        var asEpochKey = new SealedEpochKey { RecipientId = BobId, Box = copy.SealedKey, Signature = copy.Signature, KeyCommitment = sent.KeyCommitment };
        Assert.NotEqual(EpochKeyCheck.Valid, ChannelCrypto.TryOpenEpochKey(asEpochKey, "", 0, SenderId, sender.SigningPublicKey, bob, BobId, out _));

        // The copy's signature offered as a channel message's.
        var asChannelMessage = new ChatMessage {
            SenderId = SenderId, MessageId = sent.MessageId, TimestampUnixMs = sent.TimestampUnixMs, Ciphertext = sent.Ciphertext, Signature = copy.Signature,
        };
        Assert.False(ChannelCrypto.VerifyMessage(asChannelMessage, sender.SigningPublicKey));

        // And a channel message's signature offered as a local copy's.
        var channelMessage = ChannelCrypto.EncryptMessage(Text("in a channel"), ChannelCrypto.NewEpochKey(), "", 0, sender, SenderId, sent.TimestampUnixMs);
        var asLocalCopy = Delivered(sent, BobId);
        asLocalCopy.Signature = channelMessage.Signature;
        Assert.Null(LocalCrypto.Open(asLocalCopy, bob, BobId, sender.SigningPublicKey));
    }

    [Fact]
    public void TheMostRecipientsAServerAllowsFitInOneFrame() {
        using var sender = IdentityKeys.Generate();
        var recipients = Enumerable.Range(1, 200).Select(i => {
            using var keys = IdentityKeys.Generate();
            return ((long) i * 1_000_000_007, keys.AgreementPublicKey.ToArray());
        }).ToList();
        // As long as a message may be.
        var sent = LocalCrypto.Seal(Text(new string('x', 4000)), sender, SenderId, recipients, 1_700_000_000_000);

        var frame = new ClientFrame { RequestId = uint.MaxValue, SendLocalMessage = sent };
        Assert.True(frame.CalculateSize() < ProtocolInfo.DefaultLimits().MaxFrameBytes, $"{frame.CalculateSize()} bytes");
        Assert.Equal(200, LookingGlass.Server.LimitOptions.MaxMaxLocalRecipients);
    }

    [Fact]
    public void FiftyCopiesFitInOneFrame() {
        using var sender = IdentityKeys.Generate();
        var recipients = Enumerable.Range(1, 50).Select(i => {
            using var keys = IdentityKeys.Generate();
            return ((long) i, keys.AgreementPublicKey.ToArray());
        }).ToList();
        // The longest a channel message may be.
        var sent = LocalCrypto.Seal(Text(new string('é', 1000)), sender, SenderId, recipients, 1_700_000_000_000);

        var frame = new ClientFrame { RequestId = 1, SendLocalMessage = sent };
        Assert.True(frame.CalculateSize() < ProtocolInfo.DefaultLimits().MaxFrameBytes / 4, $"{frame.CalculateSize()} bytes");
    }
}
