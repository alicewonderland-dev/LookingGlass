using Google.Protobuf;
using WonderlandChat.Core.Crypto;
using WonderlandChat.Protocol;

namespace WonderlandChat.Core.Membership;

/// <summary>
/// The identity keys a membership is bound to: the Ed25519 key a member signs with
/// and the X25519 key epoch keys are sealed to. Compared by value.
/// </summary>
public sealed class MemberKeys : IEquatable<MemberKeys> {
    public const int KeySize = 32;

    private readonly byte[] _signing;
    private readonly byte[] _agreement;
    private string? _fingerprint;

    public MemberKeys(ReadOnlySpan<byte> signingPublicKey, ReadOnlySpan<byte> agreementPublicKey) {
        this._signing = signingPublicKey.ToArray();
        this._agreement = agreementPublicKey.ToArray();
        this.Hash = IdentityKeys.KeyHash(this._signing, this._agreement);
    }

    public ReadOnlySpan<byte> SigningPublicKey => this._signing;
    public ReadOnlySpan<byte> AgreementPublicKey => this._agreement;

    /// <summary>What log entries name their signer's keys by (see <see cref="IdentityKeys.KeyHash"/>).</summary>
    public byte[] Hash { get; }

    public string Fingerprint => this._fingerprint ??= IdentityKeys.FingerprintOf(this._signing, this._agreement);

    /// <summary>Both keys have the right size. Says nothing about whether they're any use.</summary>
    public bool IsWellFormed => this._signing.Length == KeySize && this._agreement.Length == KeySize;

    public static MemberKeys Of(IdentityKeys identity) => new(identity.SigningPublicKey, identity.AgreementPublicKey);

    public static MemberKeys Of(IdentityBundle bundle) => new(bundle.SigningPublicKey.Span, bundle.AgreementPublicKey.Span);

    public static MemberKeys? FromProto(MemberKey? key) => key == null ? null : new MemberKeys(key.SigningPublicKey.Span, key.AgreementPublicKey.Span);

    public MemberKey ToProto(long userId) => new() {
        UserId = userId,
        SigningPublicKey = ByteString.CopyFrom(this._signing),
        AgreementPublicKey = ByteString.CopyFrom(this._agreement),
    };

    public byte[] SigningKeyArray() => (byte[]) this._signing.Clone();

    public byte[] AgreementKeyArray() => (byte[]) this._agreement.Clone();

    public bool Equals(MemberKeys? other) {
        return other != null && this._signing.AsSpan().SequenceEqual(other._signing) && this._agreement.AsSpan().SequenceEqual(other._agreement);
    }

    public override bool Equals(object? obj) => this.Equals(obj as MemberKeys);

    public override int GetHashCode() => BitConverter.ToInt32(this.Hash, 0);

    public static bool operator ==(MemberKeys? left, MemberKeys? right) => left?.Equals(right) ?? right is null;

    public static bool operator !=(MemberKeys? left, MemberKeys? right) => !(left == right);
}
