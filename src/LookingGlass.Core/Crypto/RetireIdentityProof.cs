using System.Security.Cryptography;
using System.Text;

namespace LookingGlass.Core.Crypto;

/// <summary>
/// What a client signs to retire its identity key on a server ("Reset my identity"), and how the server checks it.
///
/// Retiring revokes every login of the account and stops the key working there for good, so a login (device token)
/// alone, which a thief may have without the key, isn't enough: the request must be signed by the account's current
/// identity key. The signature covers the user ID and the SHA-256 of the device token the connection logged in with
/// (what the server stores), which binds it to this server's login: a signature made with another login, on this
/// server or another one the same key is used with, is refused. It needs no challenge: it only ever retires the key
/// that signed it, and once that key is retired it is never the account's working key again, so a replay changes nothing.
/// </summary>
public static class RetireIdentityProof {
    public static byte[] Payload(long userId, ReadOnlySpan<byte> deviceTokenHash) {
        return new SigningPayload(Domains.RetireIdentity)
            .Add(userId)
            .Add(deviceTokenHash)
            .ToArray();
    }

    /// <summary>The hash of a device token the signature covers: SHA-256 of its UTF-8 bytes, as the server stores it.</summary>
    public static byte[] TokenHash(string deviceToken) => SHA256.HashData(Encoding.UTF8.GetBytes(deviceToken));

    public static byte[] Sign(IdentityKeys identity, long userId, string deviceToken) {
        return identity.Sign(Payload(userId, TokenHash(deviceToken)));
    }

    public static bool Verify(ReadOnlySpan<byte> signingPublicKey, long userId, ReadOnlySpan<byte> deviceTokenHash, ReadOnlySpan<byte> signature) {
        return IdentityKeys.Verify(signingPublicKey, Payload(userId, deviceTokenHash), signature);
    }
}
