using System.Security.Cryptography;

namespace LookingGlass.Core.Crypto;

/// <summary>
/// Logins (device tokens): "lgt_" and 32 random bytes in base64url. The server makes them for registrations and key logins;
/// a client makes the one "Sign out everywhere else" replaces its own with, saving it before it sends it (see SignOutProof).
/// </summary>
public static class DeviceTokens {
    private const string Prefix = "lgt_";

    // 32 bytes in base64url, without padding.
    private const int EncodedLength = 43;

    /// <summary>A new login, from 32 random bytes.</summary>
    public static string New() =>
        Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Whether a login is one <see cref="New"/> could have made.</summary>
    public static bool IsWellFormed(string? token) =>
        token is { Length: 4 + EncodedLength } && token.StartsWith(Prefix, StringComparison.Ordinal)
        && token.AsSpan(Prefix.Length).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_") < 0;
}
