namespace LookingGlass.Core.Crypto;

/// <summary>
/// What a client signs, with the identity keys it registers, so the server may move the account's places in its channels to
/// those keys (a key recovered entry in each channel's membership log), and how anyone checks it.
///
/// The Lodestone check is what proves the user is who they say, and only the server sees it: clients take that on the
/// server's word. This signature proves something smaller, which clients can check: whoever holds the new keys agreed to
/// be that user. Without it the server could bind any keys it fetched from elsewhere (another user's, from a channel they
/// share) to someone's place, and have messages and keys go to them under that user's name. It covers the user ID and
/// both public keys, nothing about a channel or a position in its log, so one signature, made when registering, goes into
/// every channel's entry; replaying it only ever binds those keys to that user again, which the server can do with keys of
/// its own anyway (see the design doc, "Recovering an identity"). No server address: an honest server only takes it with a
/// registration of those keys, which is bound to its address already (see <see cref="RegistrationProof"/>).
/// </summary>
public static class KeyRecoveryProof {
    public static byte[] Payload(long userId, ReadOnlySpan<byte> signingPublicKey, ReadOnlySpan<byte> agreementPublicKey) {
        return new SigningPayload(Domains.KeyRecovery)
            .Add(userId)
            .Add(signingPublicKey)
            .Add(agreementPublicKey)
            .ToArray();
    }

    public static byte[] Sign(IdentityKeys identity, long userId) {
        return identity.Sign(Payload(userId, identity.SigningPublicKey, identity.AgreementPublicKey));
    }

    public static bool Verify(ReadOnlySpan<byte> signingPublicKey, ReadOnlySpan<byte> agreementPublicKey, long userId, ReadOnlySpan<byte> signature) {
        return IdentityKeys.Verify(signingPublicKey, Payload(userId, signingPublicKey, agreementPublicKey), signature);
    }
}
