using System.Security.Cryptography;
using Google.Protobuf;
using NSec.Cryptography;
using WonderlandChat.Protocol;

namespace WonderlandChat.Core.Crypto;

/// <summary>
/// A character's long-term identity: an Ed25519 key for signing and an X25519
/// key that other members seal epoch keys to. Created once and kept across
/// sessions; it only changes when the character re-registers.
/// </summary>
public sealed class IdentityKeys : IDisposable {
    private static KeyCreationParameters Exportable => new() {
        ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
    };

    private readonly Key _signing;
    private readonly Key _agreement;

    public byte[] SigningPublicKey { get; }
    public byte[] AgreementPublicKey { get; }

    private IdentityKeys(Key signing, Key agreement) {
        this._signing = signing;
        this._agreement = agreement;
        this.SigningPublicKey = signing.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        this.AgreementPublicKey = agreement.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }

    public static IdentityKeys Generate() {
        return new IdentityKeys(
            Key.Create(SignatureAlgorithm.Ed25519, Exportable),
            Key.Create(KeyAgreementAlgorithm.X25519, Exportable));
    }

    public static IdentityKeys Import(byte[] signingPrivateKey, byte[] agreementPrivateKey) {
        return new IdentityKeys(
            Key.Import(SignatureAlgorithm.Ed25519, signingPrivateKey, KeyBlobFormat.RawPrivateKey, Exportable),
            Key.Import(KeyAgreementAlgorithm.X25519, agreementPrivateKey, KeyBlobFormat.RawPrivateKey, Exportable));
    }

    public (byte[] Signing, byte[] Agreement) ExportPrivateKeys() {
        return (this._signing.Export(KeyBlobFormat.RawPrivateKey), this._agreement.Export(KeyBlobFormat.RawPrivateKey));
    }

    public byte[] Sign(byte[] payload) => SignatureAlgorithm.Ed25519.Sign(this._signing, payload);

    internal Key AgreementKey => this._agreement;

    public IdentityBundle ToBundle() {
        return new IdentityBundle {
            SigningPublicKey = ByteString.CopyFrom(this.SigningPublicKey),
            AgreementPublicKey = ByteString.CopyFrom(this.AgreementPublicKey),
            BindingSignature = ByteString.CopyFrom(this.Sign(BindingPayload(this.AgreementPublicKey))),
        };
    }

    public string Fingerprint => FingerprintOf(this.SigningPublicKey, this.AgreementPublicKey);

    public void Dispose() {
        this._signing.Dispose();
        this._agreement.Dispose();
    }

    private static byte[] BindingPayload(ReadOnlySpan<byte> agreementPublicKey) {
        return new SigningPayload(Domains.IdentityBinding).Add(agreementPublicKey).ToArray();
    }

    /// <summary>Checks key sizes and that the signing key vouches for the agreement key.</summary>
    public static bool IsValidBundle(IdentityBundle? bundle) {
        if (bundle == null
            || bundle.SigningPublicKey.Length != 32
            || bundle.AgreementPublicKey.Length != 32
            || bundle.BindingSignature.Length != 64) {
            return false;
        }

        return Verify(bundle.SigningPublicKey.Span, BindingPayload(bundle.AgreementPublicKey.Span), bundle.BindingSignature.Span);
    }

    public static bool Verify(ReadOnlySpan<byte> signingPublicKey, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature) {
        if (signature.Length != 64) {
            return false;
        }

        if (!PublicKey.TryImport(SignatureAlgorithm.Ed25519, signingPublicKey, KeyBlobFormat.RawPublicKey, out var publicKey)) {
            return false;
        }

        return SignatureAlgorithm.Ed25519.Verify(publicKey!, payload, signature);
    }

    /// <summary>
    /// A 25-digit code (five groups of five) that two people can compare to
    /// confirm they see the same identity keys.
    /// </summary>
    public static string FingerprintOf(ReadOnlySpan<byte> signingPublicKey, ReadOnlySpan<byte> agreementPublicKey) {
        var hash = SHA256.HashData(new SigningPayload(Domains.Fingerprint)
            .Add(signingPublicKey)
            .Add(agreementPublicKey)
            .ToArray());

        var groups = new string[5];
        for (var i = 0; i < groups.Length; i++) {
            var value = BitConverter.ToUInt32(hash, i * 4) % 100_000;
            groups[i] = value.ToString("D5");
        }

        return string.Join(' ', groups);
    }
}
