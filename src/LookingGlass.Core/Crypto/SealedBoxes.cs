using Google.Protobuf;
using NSec.Cryptography;
using LookingGlass.Protocol;

namespace LookingGlass.Core.Crypto;

/// <summary>
/// Anonymous public-key encryption to an X25519 key: a fresh ephemeral key
/// agrees with the recipient's key, HKDF-SHA256 derives a one-time
/// XChaCha20-Poly1305 key, and <c>context</c> is bound as associated data.
/// The sender is authenticated separately, by signing the result.
/// </summary>
public static class SealedBoxes {
    private static readonly AeadAlgorithm Aead = AeadAlgorithm.XChaCha20Poly1305;

    public static SealedBox Seal(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> recipientAgreementPublicKey, ReadOnlySpan<byte> context) {
        var recipient = PublicKey.Import(KeyAgreementAlgorithm.X25519, recipientAgreementPublicKey, KeyBlobFormat.RawPublicKey);
        using var ephemeral = Key.Create(KeyAgreementAlgorithm.X25519);
        var ephemeralPublic = ephemeral.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        using var shared = KeyAgreementAlgorithm.X25519.Agree(ephemeral, recipient)
            ?? throw new InvalidOperationException("Key agreement failed");
        using var key = DeriveKey(shared, ephemeralPublic, recipientAgreementPublicKey, context);

        // The key is used exactly once, so an all-zero nonce is safe.
        var nonce = new byte[Aead.NonceSize];
        var ciphertext = Aead.Encrypt(key, nonce, context, plaintext);

        return new SealedBox {
            EphemeralPublicKey = ByteString.CopyFrom(ephemeralPublic),
            Ciphertext = ByteString.CopyFrom(ciphertext),
        };
    }

    /// <returns>The plaintext, or null if the box is malformed or was not sealed to this key with this context.</returns>
    public static byte[]? Open(SealedBox? box, IdentityKeys recipient, ReadOnlySpan<byte> context) {
        if (box == null || box.EphemeralPublicKey.Length != 32 || box.Ciphertext.Length < Aead.TagSize) {
            return null;
        }

        if (!PublicKey.TryImport(KeyAgreementAlgorithm.X25519, box.EphemeralPublicKey.Span, KeyBlobFormat.RawPublicKey, out var ephemeral)) {
            return null;
        }

        using var shared = KeyAgreementAlgorithm.X25519.Agree(recipient.AgreementKey, ephemeral!);
        if (shared == null) {
            return null;
        }

        using var key = DeriveKey(shared, box.EphemeralPublicKey.Span, recipient.AgreementPublicKey, context);
        var nonce = new byte[Aead.NonceSize];
        return Aead.Decrypt(key, nonce, context, box.Ciphertext.Span);
    }

    private static Key DeriveKey(SharedSecret shared, ReadOnlySpan<byte> ephemeralPublic, ReadOnlySpan<byte> recipientPublic, ReadOnlySpan<byte> context) {
        var salt = new byte[ephemeralPublic.Length + recipientPublic.Length];
        ephemeralPublic.CopyTo(salt);
        recipientPublic.CopyTo(salt.AsSpan(ephemeralPublic.Length));
        var info = new SigningPayload(Domains.Seal).Add(context).ToArray();
        return KeyDerivationAlgorithm.HkdfSha256.DeriveKey(shared, salt, info, Aead);
    }
}
