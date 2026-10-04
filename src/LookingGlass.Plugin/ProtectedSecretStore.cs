using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;
using LookingGlass.Core.Client;

namespace LookingGlass.Plugin;

/// <summary>
/// Keeps a character's keys encrypted at rest. Uses Windows DPAPI (current
/// user) when it works. Under Wine/Proton, where DPAPI may be unavailable, it
/// falls back to a random key file in the plugin's config folder: that guards
/// against accidentally sharing the secrets file, not against someone who can
/// read your files.
/// </summary>
public sealed class ProtectedSecretStore : ISecretStore {
    private static readonly byte[] DpapiMagic = "LGD1"u8.ToArray();
    private static readonly byte[] KeyFileMagic = "LGK1"u8.ToArray();
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LookingGlass secrets v1");
    private static readonly AeadAlgorithm Aead = AeadAlgorithm.XChaCha20Poly1305;

    private readonly string _path;
    private readonly string _keyFilePath;
    private readonly Action<string>? _tellUser;
    private readonly HashSet<string> _told = [];

    /// <param name="tellUser">Shows a warning to the user, such as a backup being loaded. Called on any thread.</param>
    public ProtectedSecretStore(string path, string keyFilePath, Action<string>? tellUser = null) {
        this._path = path;
        this._keyFilePath = keyFilePath;
        this._tellUser = tellUser;
    }

    /// <summary>How the secrets are protected, for the settings UI.</summary>
    public static string Protection { get; private set; } = "not saved yet";

    // Locks are per file and shared by every instance: a closing session and a new one for
    // the same character use different store objects but the same secrets file.
    public ClientSecrets Load() {
        // A damaged or missing file (say, after a power cut) falls back to the copy kept by the last save.
        // Deleting the file is also how people reset their identity, so they're told the backup was used.
        return AtomicFile.Read(this._path, this.Decode, message =>
                   this.Warn($"{message} To reset your LookingGlass identity instead, disconnect, delete both files, then connect again."))
               ?? new ClientSecrets();
    }

    private ClientSecrets Decode(byte[] data) {
        var magic = data.AsSpan(0, Math.Min(4, data.Length));
        var body = data.AsSpan(Math.Min(4, data.Length)).ToArray();

        if (magic.SequenceEqual(DpapiMagic)) {
            Protection = "Windows DPAPI";
            return ClientSecrets.Deserialize(ProtectedData.Unprotect(body, Entropy, DataProtectionScope.CurrentUser));
        }

        if (magic.SequenceEqual(KeyFileMagic)) {
            Protection = "local key file";
            if (body.Length < Aead.NonceSize) {
                throw new CryptographicException("The secrets file is truncated.");
            }

            var nonce = body.AsSpan(0, Aead.NonceSize);
            // Never a new key here: one made now couldn't decrypt anything.
            using var key = this.LoadFileKey(create: false);
            var plaintext = Aead.Decrypt(key, nonce, KeyFileMagic, body.AsSpan(Aead.NonceSize))
                            ?? throw new CryptographicException("The secrets file couldn't be decrypted with the local key file.");
            return ClientSecrets.Deserialize(plaintext);
        }

        throw new CryptographicException("Unrecognised secrets file format.");
    }

    public void Save(ClientSecrets secrets) {
        lock (AtomicFile.LockFor(this._path)) {
            var plaintext = secrets.Serialize();
            byte[] output;

            try {
                output = [.. DpapiMagic, .. ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser)];
                Protection = "Windows DPAPI";
            } catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException or EntryPointNotFoundException or DllNotFoundException) {
                using var key = this.LoadFileKey(create: true);
                var nonce = RandomNumberGenerator.GetBytes(Aead.NonceSize);
                output = [.. KeyFileMagic, .. nonce, .. Aead.Encrypt(key, nonce, KeyFileMagic, plaintext)];
                Protection = "local key file";
            }

            AtomicFile.Write(this._path, output);
        }
    }

    private Key LoadFileKey(bool create) {
        // Shared by every character: two stores creating it at once would each encrypt with a different key.
        lock (AtomicFile.LockFor(this._keyFilePath)) {
            var raw = AtomicFile.Read(this._keyFilePath,
                data => data.Length == Aead.KeySize ? data : throw new CryptographicException("The local key file is damaged."),
                this.Warn);
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

    private void Warn(string message) {
        Services.Log.Warning(message);
        bool first;
        lock (this._told) {
            // The key file is read on every save, so the same warning could otherwise repeat.
            first = this._told.Add(message);
        }

        if (first) {
            this._tellUser?.Invoke(message);
        }
    }

    /// <summary>One secrets file per character and server.</summary>
    /// <param name="tellUser">Shows a warning to the user. Called on any thread.</param>
    public static ProtectedSecretStore For(ulong contentId, string serverUrl, Action<string>? tellUser = null) {
        var directory = Services.PluginInterface.ConfigDirectory.FullName;
        var serverHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serverUrl.Trim().ToLowerInvariant())))[..12];
        return new ProtectedSecretStore(
            Path.Combine(directory, $"secrets-{contentId:X16}-{serverHash}.bin"),
            Path.Combine(directory, "local.key"),
            tellUser);
    }
}
