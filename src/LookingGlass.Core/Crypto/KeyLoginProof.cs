namespace LookingGlass.Core.Crypto;

/// <summary>
/// What a client signs to log in with its identity key (key login), and how the server checks it.
///
/// The signature covers the server's challenge (fresh, single use, bound to one connection), the user ID,
/// and the server's address as the client connected to it. The address is what stops a relay: a
/// malicious server the user connects to could fetch another server's challenge for the user and
/// have the user sign it, but the signature then names the malicious server's address, which the
/// other server refuses (see the server's <c>ServerOrigin</c>).
/// </summary>
public static class KeyLoginProof {
    /// <summary>The size of a key login challenge, in bytes.</summary>
    public const int ChallengeSize = 32;

    public static byte[] Payload(ReadOnlySpan<byte> challenge, long userId, string serverUrl) {
        return new SigningPayload(Domains.KeyLogin)
            .Add(challenge)
            .Add(userId)
            .Add(serverUrl)
            .ToArray();
    }

    public static byte[] Sign(IdentityKeys identity, ReadOnlySpan<byte> challenge, long userId, string serverUrl) {
        return identity.Sign(Payload(challenge, userId, serverUrl));
    }

    public static bool Verify(ReadOnlySpan<byte> signingPublicKey, ReadOnlySpan<byte> challenge, long userId, string serverUrl, ReadOnlySpan<byte> signature) {
        return challenge.Length == ChallengeSize && IdentityKeys.Verify(signingPublicKey, Payload(challenge, userId, serverUrl), signature);
    }
}
