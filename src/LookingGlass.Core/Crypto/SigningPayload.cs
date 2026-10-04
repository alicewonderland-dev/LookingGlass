using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace LookingGlass.Core.Crypto;

/// <summary>
/// Builds unambiguous byte strings for signatures and associated data.
/// Every field is length-prefixed, so two different field lists can never
/// produce the same bytes.
/// </summary>
public sealed class SigningPayload {
    private readonly ArrayBufferWriter<byte> _buffer = new(256);

    public SigningPayload(string domain) {
        this.Add(domain);
    }

    public SigningPayload Add(string value) {
        return this.Add(Encoding.UTF8.GetBytes(value));
    }

    public SigningPayload Add(ReadOnlySpan<byte> value) {
        BinaryPrimitives.WriteInt32BigEndian(this._buffer.GetSpan(4), value.Length);
        this._buffer.Advance(4);
        value.CopyTo(this._buffer.GetSpan(value.Length));
        this._buffer.Advance(value.Length);
        return this;
    }

    public SigningPayload Add(long value) {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        return this.Add(bytes);
    }

    public SigningPayload Add(ulong value) {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return this.Add(bytes);
    }

    /// <summary>A membership log position, or a marker that there is none.</summary>
    public SigningPayload Add(Protocol.LogPosition? position) {
        if (position == null) {
            return this.Add(0L);
        }

        return this.Add(1L).Add(position.Seq).Add(position.Hash.Span);
    }

    public byte[] ToArray() => this._buffer.WrittenSpan.ToArray();
}

/// <summary>Domain-separation strings. Each signature or key derivation uses its own.</summary>
public static class Domains {
    public const string IdentityBinding = "lookingglass/identity-binding/v1";
    public const string Fingerprint = "lookingglass/fingerprint/v1";
    public const string Seal = "lookingglass/seal/v1";
    public const string EpochKey = "lookingglass/epoch-key/v1";
    public const string EpochKeyCommitment = "lookingglass/epoch-key-commitment/v1";
    public const string ChannelName = "lookingglass/channel-name/v1";
    public const string Invite = "lookingglass/invite/v1";
    public const string Message = "lookingglass/message/v1";
    public const string MembershipEntry = "lookingglass/membership-entry/v1";
    public const string MembershipEntryHash = "lookingglass/membership-entry-hash/v1";
}
