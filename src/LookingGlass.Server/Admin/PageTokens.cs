using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace LookingGlass.Server.Admin;

/// <summary>What <see cref="PageTokens.Check"/> found.</summary>
public enum TokenCheck {
    /// <summary>Made by this server, for this login and this action, less than 30 minutes ago, and not used before.</summary>
    Good,

    /// <summary>None, or not one this server made for this login and this action (another site's form, another login's, another action's).</summary>
    Wrong,

    /// <summary>Made before the server last started: its key is gone.</summary>
    Restarted,

    /// <summary>Older than <see cref="PageTokens.Lifetime"/>.</summary>
    Expired,

    /// <summary>Used once already.</summary>
    Used,
}

/// <summary>
/// The tokens every form on the operator web page carries (see "Forms and CSRF" in docs/operator-tools-plan.md): an HMAC, under
/// a key made at each start and never stored, of the Tailscale login, the time it was made, a random nonce and the action it is
/// for (a confirmation page's exact ban, lift or pin; or a form's purpose). Each works once, for <see cref="Lifetime"/>. Another
/// site can't read the page, so it can't get one; one made before a restart says so.
/// </summary>
public sealed class PageTokens(TimeProvider? time = null) {
    /// <summary>How long a token works.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private const byte Version = 1;
    private const int StartSize = 8;
    private const int NonceSize = 16;
    private const int MacSize = 32;
    private const int Size = 1 + StartSize + 8 + NonceSize + MacSize;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] _start = RandomNumberGenerator.GetBytes(StartSize);

    // The nonces used, with when their token was made, until it would have expired anyway.
    private readonly ConcurrentDictionary<string, long> _used = new();

    /// <summary>A new token for <paramref name="login"/> to do <paramref name="action"/> with.</summary>
    public string Make(string login, string action) {
        var token = new byte[Size];
        token[0] = Version;
        this._start.CopyTo(token, 1);
        BinaryPrimitives.WriteInt64BigEndian(token.AsSpan(1 + StartSize), this._time.GetUtcNow().ToUnixTimeSeconds());
        RandomNumberGenerator.Fill(token.AsSpan(1 + StartSize + 8, NonceSize));
        this.Mac(token.AsSpan(0, Size - MacSize), login, action).CopyTo(token, Size - MacSize);
        return Base64Url(token);
    }

    /// <summary>
    /// Whether <paramref name="token"/> is good for <paramref name="login"/> to do <paramref name="action"/> now; a good one is
    /// used up by this (only one of two sent at once is good).
    /// </summary>
    public TokenCheck Check(string? token, string login, string action) {
        if (string.IsNullOrEmpty(token) || token.Length > 200 || FromBase64Url(token) is not { Length: Size } bytes || bytes[0] != Version) {
            return TokenCheck.Wrong;
        }

        if (!CryptographicOperations.FixedTimeEquals(bytes.AsSpan(1, StartSize), this._start)) {
            return TokenCheck.Restarted;
        }

        if (!CryptographicOperations.FixedTimeEquals(this.Mac(bytes.AsSpan(0, Size - MacSize), login, action), bytes.AsSpan(Size - MacSize))) {
            return TokenCheck.Wrong;
        }

        var made = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(1 + StartSize));
        var now = this._time.GetUtcNow().ToUnixTimeSeconds();
        if (made > now + 60) {
            return TokenCheck.Wrong;
        }

        if (now - made > (long) Lifetime.TotalSeconds) {
            return TokenCheck.Expired;
        }

        this.Forget(now);
        return this._used.TryAdd(Convert.ToHexString(bytes, 1 + StartSize + 8, NonceSize), made) ? TokenCheck.Good : TokenCheck.Used;
    }

    /// <summary>How many used tokens are remembered (they are forgotten once they would have expired).</summary>
    internal int Remembered => this._used.Count;

    private void Forget(long now) {
        var expired = now - (long) Lifetime.TotalSeconds - 60;
        foreach (var (nonce, made) in this._used) {
            if (made < expired) {
                this._used.TryRemove(nonce, out _);
            }
        }
    }

    private byte[] Mac(ReadOnlySpan<byte> head, string login, string action) {
        // Each part with its length first, so no two (login, action) pairs give the same bytes.
        var loginBytes = Encoding.UTF8.GetBytes(login.ToLowerInvariant());
        var actionBytes = Encoding.UTF8.GetBytes(action);
        var data = new byte[head.Length + 8 + loginBytes.Length + actionBytes.Length];
        head.CopyTo(data);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(head.Length), loginBytes.Length);
        loginBytes.CopyTo(data, head.Length + 4);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(head.Length + 4 + loginBytes.Length), actionBytes.Length);
        actionBytes.CopyTo(data, head.Length + 8 + loginBytes.Length);
        return HMACSHA256.HashData(this._key, data);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? FromBase64Url(string text) {
        if (text.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) {
            return null;
        }

        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        try {
            return Convert.FromBase64String(padded);
        } catch (FormatException) {
            return null;
        }
    }
}
