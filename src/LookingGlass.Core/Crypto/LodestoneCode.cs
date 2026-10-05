using System.Security.Cryptography;
using LookingGlass.Core.Client;

namespace LookingGlass.Core.Crypto;

/// <summary>
/// The code a user puts in their Lodestone profile to show a character is theirs when registering it. It isn't random:
/// it is derived from the server's address (its origin, as the client connected to it), the identity signing key being
/// registered, the server's registration nonce and the character's Lodestone ID, so the client recomputes it from its
/// own address and key before showing it, and refuses any other.
///
/// Without that, a malicious server M could start a registration on an honest server B for the user's character with
/// M's own key, show the user B's code as if it were M's, and once the user put it in their profile, complete the
/// registration on B and take the character's account there. B's code is now derived from B's address and M's key, and
/// the code the user's client accepts from M from M's address and the user's key, so M can only pass B's code on by
/// finding inputs of its own (its nonce, the ID it names) whose code is the same: a second preimage of a
/// <see cref="Bits"/>-bit truncated SHA-256, which M must find for a code B issued (from B's fresh nonce) and before that
/// registration on B expires.
///
/// <see cref="Bits"/>: 100. M grinds offline, on hardware of its choosing, and can hold many of B's codes at once (one
/// per connection, a few an hour per IP address, but IPv6 addresses are plentiful), so the work is about 2^100 divided by
/// the number of codes it holds, within a challenge's lifetime (15 minutes by default). Even 2^20 codes held at once
/// leave 2^80 hashes in 15 minutes (about 10^21 a second); precomputing codes for the user's key, which M may know in
/// advance, doesn't help more than holding more codes does (a table of 2^50 codes and 2^20 of B's gives a 2^-30 chance).
/// 80 bits would leave that at 2^60 hashes in 15 minutes: out of reach of anyone but a very large operation, but not by
/// a wide margin; the 20 more bits cost the user five more characters, which they copy with a button anyway.
///
/// Format: "LGC-" and 20 characters of Crockford's base32 (0-9 and A-Z without I, L, O and U, so no character can be
/// misread as another), in groups of four: "LGC-XXXX-XXXX-XXXX-XXXX-XXXX". Only letters, digits and hyphens, which the
/// Lodestone profile keeps as they are.
/// </summary>
public static class LodestoneCode {
    public const string Prefix = "LGC-";

    /// <summary>The bits of SHA-256 a code keeps. See the class documentation for why this many.</summary>
    public const int Bits = 100;

    private const int BitsPerSymbol = 5;
    private const int Symbols = Bits / BitsPerSymbol;
    private const int GroupSize = 4;

    /// <summary>The length of a code: the prefix, the symbols, and a hyphen between groups.</summary>
    public const int Length = 4 + Symbols + Symbols / GroupSize - 1;

    /// <summary>Crockford's base32.</summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>The code for registering the character <paramref name="lodestoneId"/> with this key, on this server, with this nonce.</summary>
    /// <param name="origin">The server's address, as the client connected to it (see <see cref="ServerOrigin"/>): only the origin counts.</param>
    /// <param name="signingPublicKey">The Ed25519 identity key being registered, 32 bytes.</param>
    /// <param name="nonce">The server's registration nonce, <see cref="RegistrationProof.NonceSize"/> bytes.</param>
    /// <exception cref="ArgumentException">The key or nonce has the wrong size.</exception>
    public static string Derive(ServerOrigin origin, ReadOnlySpan<byte> signingPublicKey, ReadOnlySpan<byte> nonce, long lodestoneId) {
        ArgumentNullException.ThrowIfNull(origin);
        if (signingPublicKey.Length != 32) {
            throw new ArgumentException("An identity signing key is 32 bytes.", nameof(signingPublicKey));
        }

        if (nonce.Length != RegistrationProof.NonceSize) {
            throw new ArgumentException($"A registration nonce is {RegistrationProof.NonceSize} bytes.", nameof(nonce));
        }

        var hash = SHA256.HashData(new SigningPayload(Domains.LodestoneCode)
            .Add(origin.ToString())
            .Add(signingPublicKey)
            .Add(nonce)
            .Add(lodestoneId)
            .ToArray());

        Span<char> code = stackalloc char[Length];
        Prefix.CopyTo(code);
        var at = Prefix.Length;
        for (var i = 0; i < Symbols; i++) {
            if (i > 0 && i % GroupSize == 0) {
                code[at++] = '-';
            }

            // Bits i*5 to i*5+4 of the hash, most significant first.
            var value = 0;
            for (var bit = i * BitsPerSymbol; bit < (i + 1) * BitsPerSymbol; bit++) {
                value = (value << 1) | ((hash[bit / 8] >> (7 - bit % 8)) & 1);
            }

            code[at++] = Alphabet[value];
        }

        return new string(code);
    }

    /// <summary>
    /// Whether a Lodestone profile's text contains <paramref name="code"/>: "LGC-" (in any case), then the code's
    /// characters, in any case, reading a letter O as zero and I or L as one, as Crockford's base32 does (a code never
    /// contains those letters, so this only forgives retyping it). Nothing else is forgiven: the hyphens must be there,
    /// and every other character must be the code's.
    /// </summary>
    /// <remarks>
    /// Only from where the text has "LGC-" itself: an L never stands for a one there, and a code's characters never
    /// include an L, so a code the user pasted can't be read as the start of another one.
    /// </remarks>
    public static bool AppearsIn(string text, string code) {
        if (code.Length != Length || !code.StartsWith(Prefix, StringComparison.Ordinal)) {
            return false;
        }

        for (var start = text.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
             start >= 0 && start + Length <= text.Length;
             start = text.IndexOf(Prefix, start + 1, StringComparison.OrdinalIgnoreCase)) {
            var matches = true;
            for (var i = Prefix.Length; i < Length && matches; i++) {
                matches = Canonical(text[start + i]) == code[i];
            }

            if (matches) {
                return true;
            }
        }

        return false;
    }

    /// <summary>What <see cref="Redact"/> puts in place of a code.</summary>
    public const string Removed = "[code removed]";

    /// <summary>
    /// <paramref name="text"/> with every code in it replaced by <see cref="Removed"/>, except <paramref name="keep"/>. For
    /// whatever a server (or anyone but this client) wrote that is shown to the user or logged: errors, announcements,
    /// names. A malicious server M that can't get another server's code past the client's check (see the class
    /// documentation) could otherwise just write it into its words, as "LGC-… isn't in your Lodestone profile yet",
    /// and hope the user pastes it. The only code a user should ever see is the one this client derived itself and
    /// checked, which is <paramref name="keep"/> (none while no registration is under way).
    /// </summary>
    /// <remarks>
    /// A code here is what <see cref="AppearsIn"/> could find in a profile: a literal "LGC-" (in any case), then the
    /// symbols and hyphens, reading a symbol as AppearsIn does (any case, O as 0, I or L as 1), and also
    /// with invisible characters in between (control and format characters, such as zero-width spaces, bidi overrides
    /// or the game's macro bytes), which a display or a copy may drop. Line breaks and tabs aren't skipped: a code
    /// broken by one doesn't paste as one. This only keeps codes out of what is shown; nothing can stop a server
    /// describing one in words.
    /// </remarks>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(text))]
    public static string? Redact(string? text, string? keep = null) {
        if (string.IsNullOrEmpty(text)) {
            return text;
        }

        System.Text.StringBuilder? redacted = null;
        var copied = 0;
        for (var start = text.IndexOfAny(['L', 'l']);
             start >= 0;
             start = start < text.Length ? text.IndexOfAny(['L', 'l'], start) : -1) {
            if (ReadAt(text, start) is not var (code, end)) {
                start++;
                continue;
            }

            if (!string.IsNullOrEmpty(keep) && code == keep) {
                start = end;
                continue;
            }

            redacted ??= new System.Text.StringBuilder(text.Length);
            redacted.Append(text, copied, start - copied).Append(Removed);
            copied = start = end;
        }

        return redacted == null ? text : redacted.Append(text, copied, text.Length - copied).ToString();
    }

    /// <summary>The code that starts at <paramref name="start"/>, as <see cref="Redact"/> reads one (in its canonical form), and where it ends.</summary>
    private static (string Code, int End)? ReadAt(string text, int start) {
        Span<char> code = stackalloc char[Length];
        var at = start;
        for (var i = 0; i < Length; i++) {
            // Invisible characters only between a code's characters, never before it starts.
            while (i > 0 && at < text.Length && Invisible(text[at])) {
                at++;
            }

            if (at >= text.Length) {
                return null;
            }

            var c = text[at++];
            if (i < Prefix.Length) {
                code[i] = c is >= 'a' and <= 'z' ? (char) (c - 'a' + 'A') : c;
                if (code[i] != Prefix[i]) {
                    return null;
                }
            } else if ((i - Prefix.Length) % (GroupSize + 1) == GroupSize) {
                if (c != '-') {
                    return null;
                }

                code[i] = c;
            } else {
                code[i] = Canonical(c);
                if (!Alphabet.Contains(code[i])) {
                    return null;
                }
            }
        }

        return (new string(code), at);
    }

    /// <summary>Characters that don't show, which <see cref="Redact"/> reads through: control (but line breaks and tabs) and format characters.</summary>
    private static bool Invisible(char c) {
        return c is not ('\n' or '\r' or '\t')
               && char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format;
    }

    private static char Canonical(char c) {
        return char.ToUpperInvariant(c) switch {
            'O' => '0',
            'I' or 'L' => '1',
            var upper => upper,
        };
    }
}
