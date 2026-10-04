namespace LookingGlass.Core.Crypto;

/// <summary>
/// What a client signs to complete a registration, and how the server checks it: proof that the client holds the
/// private key of the identity it registers.
///
/// A user's public identity bundle, binding signature included, is fetched by everyone they share a channel with, so
/// without this anyone could register another user's key for their own character, and then (by registering again with
/// new keys) have the server retire it. The signature covers the server's registration nonce (fresh, single
/// registration, bound to one connection), the user ID being registered and the server's address as the client
/// connected to it, which stops a malicious server relaying another server's challenge to its users, as for
/// <see cref="KeyLoginProof"/>.
/// </summary>
public static class RegistrationProof {
    /// <summary>The size of a registration nonce, in bytes.</summary>
    public const int NonceSize = 32;

    public static byte[] Payload(ReadOnlySpan<byte> nonce, long userId, string serverUrl) {
        return new SigningPayload(Domains.Registration)
            .Add(nonce)
            .Add(userId)
            .Add(serverUrl)
            .ToArray();
    }

    public static byte[] Sign(IdentityKeys identity, ReadOnlySpan<byte> nonce, long userId, string serverUrl) {
        return identity.Sign(Payload(nonce, userId, serverUrl));
    }

    public static bool Verify(ReadOnlySpan<byte> signingPublicKey, ReadOnlySpan<byte> nonce, long userId, string serverUrl, ReadOnlySpan<byte> signature) {
        return nonce.Length == NonceSize && IdentityKeys.Verify(signingPublicKey, Payload(nonce, userId, serverUrl), signature);
    }
}
