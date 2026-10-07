using System.Security.Cryptography;
using System.Text;
using LookingGlass.Core.Client;
using NSec.Cryptography;

namespace LookingGlass.Plugin;

/// <summary>
/// How the plugin keeps a secret on this computer: Windows DPAPI (current user) when it works; under Wine/Proton, where
/// DPAPI may be unavailable, a random key file in the plugin's config folder (<c>local.key</c>), which guards against
/// accidentally sharing a file, not against someone who can read your files. The secrets file and the chat log's key both
/// use it, each with its own purpose (DPAPI's entropy, and the associated data under the key file), so one's protected
/// bytes can't stand in for the other's.
/// </summary>
public sealed class LocalProtection : IAtRestProtection {
    private static readonly byte[] DpapiMagic = "LGD1"u8.ToArray();
    private static readonly byte[] KeyFileMagic = "LGK1"u8.ToArray();
    private static readonly AeadAlgorithm Aead = AeadAlgorithm.XChaCha20Poly1305;

    private readonly string _keyFilePath;
    private readonly string _what;
    private readonly byte[] _entropy;
    private readonly byte[] _associatedData;
    private readonly Action<string> _warn;

    /// <param name="keyFilePath">The local key file, shared by every character and purpose.</param>
    /// <param name="what">What is protected, for errors ("secrets file").</param>
    /// <param name="entropy">DPAPI's optional entropy: the purpose.</param>
    /// <param name="associatedData">Bound under the key file, after its magic: the purpose (the secrets file's is empty, as it always was).</param>
    /// <param name="warn">Told when the key file had to be read from its backup.</param>
    public LocalProtection(string keyFilePath, string what, string entropy, byte[] associatedData, Action<string> warn) {
        this._keyFilePath = keyFilePath;
        this._what = what;
        this._entropy = Encoding.UTF8.GetBytes(entropy);
        this._associatedData = [.. KeyFileMagic, .. associatedData];
        this._warn = warn;
    }

    /// <summary>How the plugin's secrets are protected, for the settings UI: what was last used.</summary>
    public static string Protection { get; private set; } = "not saved yet";

    public byte[] Protect(byte[] plaintext) {
        try {
            var output = (byte[]) [.. DpapiMagic, .. ProtectedData.Protect(plaintext, this._entropy, DataProtectionScope.CurrentUser)];
            Protection = "Windows DPAPI";
            return output;
        } catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException or EntryPointNotFoundException or DllNotFoundException) {
            using var key = this.LoadFileKey(create: true);
            var nonce = RandomNumberGenerator.GetBytes(Aead.NonceSize);
            var output = (byte[]) [.. KeyFileMagic, .. nonce, .. Aead.Encrypt(key, nonce, this._associatedData, plaintext)];
            Protection = "local key file";
            return output;
        }
    }

    public byte[] Unprotect(byte[] data) {
        var magic = data.AsSpan(0, Math.Min(4, data.Length));
        var body = data.AsSpan(Math.Min(4, data.Length)).ToArray();

        if (magic.SequenceEqual(DpapiMagic)) {
            Protection = "Windows DPAPI";
            return ProtectedData.Unprotect(body, this._entropy, DataProtectionScope.CurrentUser);
        }

        if (magic.SequenceEqual(KeyFileMagic)) {
            Protection = "local key file";
            if (body.Length < Aead.NonceSize) {
                throw new CryptographicException($"The {this._what} is truncated.");
            }

            var nonce = body.AsSpan(0, Aead.NonceSize);
            // Never a new key here: one made now couldn't decrypt anything.
            using var key = this.LoadFileKey(create: false);
            return Aead.Decrypt(key, nonce, this._associatedData, body.AsSpan(Aead.NonceSize))
                   ?? throw new CryptographicException($"The {this._what} couldn't be decrypted with the local key file.");
        }

        throw new CryptographicException($"Unrecognised {this._what} format.");
    }

    private Key LoadFileKey(bool create) {
        // Shared by every character and purpose: two creating it at once would each encrypt with a different key.
        lock (AtomicFile.LockFor(this._keyFilePath)) {
            var raw = AtomicFile.Read(this._keyFilePath,
                data => data.Length == Aead.KeySize ? data : throw new CryptographicException("The local key file is damaged."),
                this._warn);
            if (raw == null) {
                if (!create) {
                    throw new CryptographicException("The local key file is missing.");
                }

                raw = RandomNumberGenerator.GetBytes(Aead.KeySize);
                AtomicFile.Write(this._keyFilePath, raw);
            }

            return Key.Import(Aead, raw, KeyBlobFormat.RawSymmetricKey);
        }
    }
}
