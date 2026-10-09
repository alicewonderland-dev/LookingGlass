namespace LookingGlass.Core.Crypto;

/// <summary>
/// What a client signs for "Sign out everywhere else" (SignOutOtherDevices), and how the server checks it.
///
/// Signing out everywhere else revokes every other login of the account, replaces this computer's, and stops the identity
/// key signing in until the character registers again through the Lodestone. A login alone, which a copy of the secrets
/// file holds, mustn't be enough, or whoever has the copy could shut the owner out: the request is signed by the account's
/// current identity key, as for <see cref="RetireIdentityProof"/>. The signature covers the user ID, the SHA-256 of the
/// login the connection logged in with and of the new one it is replaced with (so the new one can't be swapped on the way),
/// the server's address as the client connected to it (so a signature made for one server is no use on another), and a
/// fresh nonce the server gave this connection (so a signature can't be replayed).
/// </summary>
public static class SignOutProof {
    /// <summary>The size of the server's nonce.</summary>
    public const int NonceSize = 32;

    public static byte[] Payload(long userId, ReadOnlySpan<byte> deviceTokenHash, ReadOnlySpan<byte> newDeviceTokenHash, string serverUrl, ReadOnlySpan<byte> nonce) {
        return new SigningPayload(Domains.SignOutOthers)
            .Add(userId)
            .Add(deviceTokenHash)
            .Add(newDeviceTokenHash)
            .Add(serverUrl)
            .Add(nonce)
            .ToArray();
    }

    public static byte[] Sign(IdentityKeys identity, long userId, string deviceToken, string newDeviceToken, string serverUrl, ReadOnlySpan<byte> nonce) {
        return identity.Sign(Payload(userId, RetireIdentityProof.TokenHash(deviceToken), RetireIdentityProof.TokenHash(newDeviceToken), serverUrl, nonce));
    }

    public static bool Verify(ReadOnlySpan<byte> signingPublicKey, long userId, ReadOnlySpan<byte> deviceTokenHash, ReadOnlySpan<byte> newDeviceTokenHash,
        string serverUrl, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> signature) {
        return IdentityKeys.Verify(signingPublicKey, Payload(userId, deviceTokenHash, newDeviceTokenHash, serverUrl, nonce), signature);
    }
}
