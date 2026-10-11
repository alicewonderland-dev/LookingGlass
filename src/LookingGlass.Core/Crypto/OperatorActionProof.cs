using System.Security.Cryptography;
using System.Text;

namespace LookingGlass.Core.Crypto;

/// <summary>
/// What an operator's plugin signs for a ban or a lift (OperatorBan, OperatorUnban), and how the server checks it (see "The
/// plugin's operator panel" in docs/operator-tools-plan.md).
///
/// Reading the flags and bans needs the operator character's login; changing them needs its current identity key as well, as
/// "Sign out everywhere else" does (<see cref="SignOutProof"/>), so a copy of the login alone can't ban. The signature covers
/// the action, the operator's user ID, the character it is about, the days, the SHA-256 of the reason (so none of them can be
/// changed on the way), the SHA-256 of the login the connection logged in with (so it is no use on another login), the
/// server's address as the client connected to it (so it can't be relayed to another server: user IDs are the same on every
/// server) and a fresh nonce the server gave this connection (single use, a few minutes, so it can't be replayed).
/// </summary>
public static class OperatorActionProof {
    /// <summary>The size of the server's nonce.</summary>
    public const int NonceSize = 32;

    /// <summary>The actions an operator's plugin signs.</summary>
    public static class Actions {
        public const string Ban = "ban";
        public const string Lift = "lift";
    }

    /// <summary>The SHA-256 of a reason's UTF-8 bytes, as the signature covers it.</summary>
    public static byte[] ReasonHash(string reason) => SHA256.HashData(Encoding.UTF8.GetBytes(reason));

    public static byte[] Payload(string action, long operatorUserId, long targetUserId, long days, ReadOnlySpan<byte> reasonHash,
        ReadOnlySpan<byte> deviceTokenHash, string serverUrl, ReadOnlySpan<byte> nonce) {
        return new SigningPayload(Domains.OperatorAction)
            .Add(action)
            .Add(operatorUserId)
            .Add(targetUserId)
            .Add(days)
            .Add(reasonHash)
            .Add(deviceTokenHash)
            .Add(serverUrl)
            .Add(nonce)
            .ToArray();
    }

    /// <param name="days">1 or more; 0 for a ban until lifted, and for a lift.</param>
    /// <param name="reason">The reason as sent; empty for none, and for a lift.</param>
    public static byte[] Sign(IdentityKeys identity, string action, long operatorUserId, long targetUserId, long days, string reason, string deviceToken,
        string serverUrl, ReadOnlySpan<byte> nonce) {
        return identity.Sign(Payload(action, operatorUserId, targetUserId, days, ReasonHash(reason), RetireIdentityProof.TokenHash(deviceToken), serverUrl, nonce));
    }

    public static bool Verify(ReadOnlySpan<byte> signingPublicKey, string action, long operatorUserId, long targetUserId, long days, string reason,
        ReadOnlySpan<byte> deviceTokenHash, string serverUrl, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> signature) {
        return IdentityKeys.Verify(signingPublicKey, Payload(action, operatorUserId, targetUserId, days, ReasonHash(reason), deviceTokenHash, serverUrl, nonce),
            signature);
    }
}
