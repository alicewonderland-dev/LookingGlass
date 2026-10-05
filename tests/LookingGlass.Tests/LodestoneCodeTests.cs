using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LookingGlass.Core.Client;
using LookingGlass.Core.Crypto;

namespace LookingGlass.Tests;

/// <summary>
/// The code a user puts in their Lodestone profile is derived from the server's address, the identity key being
/// registered, the server's registration nonce, the client's nonce and the character, so a client can check that the code it is shown was
/// made for its own server and key, and refuse one passed on from another server.
/// </summary>
public sealed class LodestoneCodeTests {
    private static readonly ServerOrigin Origin = ServerOrigin.FromUrl("wss://chat.example.com/ws")!;
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte) i).ToArray();
    private static readonly byte[] Nonce = Enumerable.Range(0xA0, 32).Select(i => (byte) i).ToArray();
    private static readonly byte[] ClientNonce = Enumerable.Range(0x40, 32).Select(i => (byte) i).ToArray();
    private const long CharacterId = 31337;

    // Crockford's base32: no I, L, O or U.
    private static readonly Regex Format = new("^LGC-[0-9A-HJKMNP-TV-Z]{4}(-[0-9A-HJKMNP-TV-Z]{4}){4}$");

    [Fact]
    public void TheCodeIsLgcAndTwentyCrockfordCharactersInGroupsOfFour() {
        var code = LodestoneCode.Derive(Origin, Key, Nonce, ClientNonce, CharacterId);

        Assert.Matches(Format, code);
        Assert.Equal(28, code.Length);
        Assert.Equal(LodestoneCode.Length, code.Length);
        // 20 characters of 5 bits each.
        Assert.Equal(100, LodestoneCode.Bits);

        // Many random inputs: always the format, and the symbols are spread over the whole alphabet.
        var seen = new HashSet<char>();
        for (var i = 0; i < 200; i++) {
            var random = LodestoneCode.Derive(Origin, RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32), i);
            Assert.Matches(Format, random);
            seen.UnionWith(random[4..].Replace("-", ""));
        }

        Assert.Equal(32, seen.Count);
    }

    [Fact]
    public void TheCodeIsDeterministic() {
        Assert.Equal(LodestoneCode.Derive(Origin, Key, Nonce, ClientNonce, CharacterId), LodestoneCode.Derive(Origin, Key.ToArray(), Nonce.ToArray(), ClientNonce, CharacterId));
        // Only the origin counts: another path, or the default port written out, is the same server.
        Assert.Equal(LodestoneCode.Derive(Origin, Key, Nonce, ClientNonce, CharacterId),
            LodestoneCode.Derive(ServerOrigin.FromUrl("wss://CHAT.example.com:443/other")!, Key, Nonce, ClientNonce, CharacterId));
    }

    [Fact]
    public void EveryInputChangesTheCode() {
        var code = LodestoneCode.Derive(Origin, Key, Nonce, ClientNonce, CharacterId);
        var otherKey = Key.ToArray();
        otherKey[31] ^= 1;
        var otherNonce = Nonce.ToArray();
        otherNonce[0] ^= 0x80;
        var otherClientNonce = ClientNonce.ToArray();
        otherClientNonce[17] ^= 4;

        var variants = new[] {
            LodestoneCode.Derive(ServerOrigin.FromUrl("wss://chat.example.org/ws")!, Key, Nonce, ClientNonce, CharacterId),
            LodestoneCode.Derive(ServerOrigin.FromUrl("ws://chat.example.com:443/ws")!, Key, Nonce, ClientNonce, CharacterId),
            LodestoneCode.Derive(ServerOrigin.FromUrl("wss://chat.example.com:8443/ws")!, Key, Nonce, ClientNonce, CharacterId),
            LodestoneCode.Derive(Origin, otherKey, Nonce, ClientNonce, CharacterId),
            LodestoneCode.Derive(Origin, Key, otherNonce, ClientNonce, CharacterId),
            LodestoneCode.Derive(Origin, Key, Nonce, otherClientNonce, CharacterId),
            // The two nonces are told apart.
            LodestoneCode.Derive(Origin, Key, ClientNonce, Nonce, CharacterId),
            LodestoneCode.Derive(Origin, Key, Nonce, ClientNonce, CharacterId + 1),
            LodestoneCode.Derive(Origin, Key, Nonce, ClientNonce, -CharacterId),
        };

        Assert.All(variants, variant => Assert.NotEqual(code, variant));
        Assert.Equal(variants.Length, variants.Distinct().Count());
    }

    /// <summary>
    /// The derivation, written out independently: SHA-256 of the length-prefixed fields (domain, origin as
    /// <see cref="ServerOrigin.ToString"/> gives it, signing key, server nonce, client nonce, character ID as 8 bytes
    /// big-endian), its first 100 bits in Crockford's base32, most significant bit first. Plus one value worked out with
    /// coreutils (see <see cref="KnownAnswer"/>), so a change to the format can't go unnoticed.
    /// </summary>
    [Fact]
    public void TheDerivationIsAsDocumented() {
        Assert.Equal("lookingglass/lodestone-code/v1", Domains.LodestoneCode);

        var payload = new MemoryStream();
        void Field(byte[] value) {
            var length = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
            payload.Write(length);
            payload.Write(value);
        }

        Field(Encoding.UTF8.GetBytes("lookingglass/lodestone-code/v1"));
        Field(Encoding.UTF8.GetBytes("wss://chat.example.com:443"));
        Field(Key);
        Field(Nonce);
        Field(ClientNonce);
        var id = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(id, CharacterId);
        Field(id);
        var hash = SHA256.HashData(payload.ToArray());

        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var symbols = new StringBuilder();
        for (var i = 0; i < 20; i++) {
            var value = 0;
            for (var bit = i * 5; bit < i * 5 + 5; bit++) {
                value = (value << 1) | ((hash[bit / 8] >> (7 - bit % 8)) & 1);
            }

            symbols.Append(alphabet[value]);
        }

        var body = symbols.ToString();
        var expected = $"LGC-{body[..4]}-{body[4..8]}-{body[8..12]}-{body[12..16]}-{body[16..]}";
        Assert.Equal(expected, LodestoneCode.Derive(Origin, Key, Nonce, ClientNonce, CharacterId));
        Assert.Equal(KnownAnswer, expected);
    }

    // See TheDerivationIsAsDocumented. Worked out with coreutils (printf, xxd, sha256sum) and bash, without .NET:
    //   f() { printf '%08x%s' $((${#1}/2)) "$1"; }; a() { printf %s "$1" | xxd -p | tr -d '\n'; }
    //   r() { for ((i=$1;i<$1+32;i++)); do printf %02x $i; done; }
    //   { f $(a lookingglass/lodestone-code/v1); f $(a wss://chat.example.com:443); f $(r 1); f $(r 160); f $(r 64)
    //     f $(printf %016x 31337); } | xxd -r -p | sha256sum
    // gives d25cb267e5f988c117421ced9c..., whose first 100 bits, five at a time, are these symbols (0-9, A-Z without
    // I, L, O and U). Without the client nonce (f $(r 64)), the same gives the code from before it: LGC-GAZW-4Q6Q-KDW8-40FY-8MKM.
    private const string KnownAnswer = "LGC-T9EB-4SZ5-Z64C-25T2-3KPS";

    [Fact]
    public void BadInputsAreRefused() {
        Assert.Throws<ArgumentException>(() => LodestoneCode.Derive(Origin, Key[..31], Nonce, ClientNonce, CharacterId));
        Assert.Throws<ArgumentException>(() => LodestoneCode.Derive(Origin, Key, Nonce[..16], ClientNonce, CharacterId));
        Assert.Throws<ArgumentException>(() => LodestoneCode.Derive(Origin, Key, [], ClientNonce, CharacterId));
        Assert.Throws<ArgumentException>(() => LodestoneCode.Derive(Origin, Key, Nonce, ClientNonce[..31], CharacterId));
        Assert.Throws<ArgumentException>(() => LodestoneCode.Derive(Origin, Key, Nonce, [], CharacterId));
        Assert.Throws<ArgumentNullException>(() => LodestoneCode.Derive(null!, Key, Nonce, ClientNonce, CharacterId));
    }

    /// <summary>
    /// What the server looks for in a profile: exactly the code, anywhere in the text, in any case, with a letter O
    /// read as zero and I or L as one (a code never contains those letters, so this only forgives retyping it).
    /// </summary>
    [Fact]
    public void AProfileContainsTheCodeAsTypedOrCopied() {
        var code = LodestoneCode.Derive(Origin, Key, Nonce, ClientNonce, CharacterId);
        var body = code[4..];

        Assert.True(LodestoneCode.AppearsIn(code, code));
        Assert.True(LodestoneCode.AppearsIn($"Hi! I play on weekends.\n{code}\nSee you around.", code));
        Assert.True(LodestoneCode.AppearsIn($"code:{code}.", code));
        Assert.True(LodestoneCode.AppearsIn(code.ToLowerInvariant(), code));
        Assert.True(LodestoneCode.AppearsIn("lgc-" + body.Replace('0', 'O').Replace('1', 'l'), code));
        Assert.True(LodestoneCode.AppearsIn("LGC-" + body.Replace('0', 'o').Replace('1', 'I'), code));
        // Behind an older code, or behind text that looks like the start of one.
        Assert.True(LodestoneCode.AppearsIn($"{LodestoneCode.Derive(Origin, Key, Nonce, ClientNonce, 1)} {code}", code));
        Assert.True(LodestoneCode.AppearsIn($"LGC-{code}", code));

        Assert.False(LodestoneCode.AppearsIn("", code));
        Assert.False(LodestoneCode.AppearsIn(code[..^1], code));
        Assert.False(LodestoneCode.AppearsIn(body, code));
        Assert.False(LodestoneCode.AppearsIn("LGC-" + body.Replace("-", ""), code));
        Assert.False(LodestoneCode.AppearsIn("LGC " + body, code));
        Assert.False(LodestoneCode.AppearsIn(LodestoneCode.Derive(Origin, Key, Nonce, ClientNonce, CharacterId + 1), code));
        // A changed character is another code, U included (it isn't in the alphabet, and isn't read as anything).
        foreach (var replacement in new[] { 'U', 'V', '2' }) {
            var changed = code[..^1] + (code[^1] == replacement ? 'W' : replacement);
            Assert.False(LodestoneCode.AppearsIn(changed, code));
        }
    }

    /// <summary>
    /// Text a server sends, shown to the user: every code in it is removed, however it is written (as
    /// <see cref="LodestoneCode.AppearsIn"/> would read it, and with characters a display or a copy drops in between),
    /// except the one this client checked was made for it and its key. A server can't have the user paste another
    /// server's code by writing it into an error, an announcement or a name.
    /// </summary>
    [Fact]
    public void RedactRemovesEveryCodeButTheOneToKeep() {
        var code = LodestoneCode.Derive(Origin, Key, Nonce, ClientNonce, CharacterId);
        var other = LodestoneCode.Derive(Origin, Key, Nonce, ClientNonce, CharacterId + 1);
        var body = other[4..];

        Assert.Equal("", LodestoneCode.Redact(""));
        Assert.Null(LodestoneCode.Redact(null));
        foreach (var unchanged in new[] { "Hello!", "[LGC3] hi", "LGC-", $"LGC {body}", $"LGC-{body[..^1]}", "LGC-" + body.Replace("-", ""),
                     $"{other[..10]}\n{other[10..]}", $"LGC-{(char) (body[0] + 0xFEE0)}{body[1..]}" }) {
            Assert.Equal(unchanged, LodestoneCode.Redact(unchanged));
        }

        Assert.Equal(LodestoneCode.Removed, LodestoneCode.Redact(other));
        Assert.Equal($"{LodestoneCode.Removed} isn't in your Lodestone profile yet.", LodestoneCode.Redact($"{other} isn't in your Lodestone profile yet."));
        Assert.Equal($"a {LodestoneCode.Removed}, b {LodestoneCode.Removed}.", LodestoneCode.Redact($"a {other}, b {code.ToLowerInvariant()}."));
        foreach (var disguised in new[] {
                     other.ToLowerInvariant(),
                     "lGc-" + body.Replace('0', 'o').Replace('1', 'I'),
                     "LGC-" + body.Replace('0', 'O').Replace('1', 'l'),
                     // Invisible: zero-width space, soft hyphen, a bidi override, a game macro byte.
                     $"L\u200BGC-{body[..2]}\u00AD{body[2..12]}\u202E{body[12..]}",
                     $"LGC-\u0002{body}",
                 }) {
            Assert.Equal($"[{LodestoneCode.Removed}]", LodestoneCode.Redact($"[{disguised}]"));
        }

        // Behind text that looks like the start of one, and a code followed by more symbols.
        Assert.Equal($"LGC-{LodestoneCode.Removed}", LodestoneCode.Redact($"LGC-{other}"));
        Assert.Equal($"{LodestoneCode.Removed}XY", LodestoneCode.Redact($"{other}XY"));

        // The one to keep stays, however it is written; others don't.
        Assert.Equal($"Put {code} in your profile.", LodestoneCode.Redact($"Put {code} in your profile.", code));
        Assert.Equal(code.ToLowerInvariant(), LodestoneCode.Redact(code.ToLowerInvariant(), code));
        Assert.Equal($"{code}, not {LodestoneCode.Removed}", LodestoneCode.Redact($"{code}, not {other}", code));
        Assert.Equal(LodestoneCode.Removed, LodestoneCode.Redact(other, ""));
    }
}
