using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;
using WonderlandChat.Core.Client;

namespace WonderlandChat.Plugin;

/// <summary>
/// Keeps a character's keys encrypted at rest. Uses Windows DPAPI (current
/// user) when it works. Under Wine/Proton, where DPAPI may be unavailable, it
/// falls back to a random key file in the plugin's config folder: that guards
/// against accidentally sharing the secrets file, not against someone who can
/// read your files.
/// </summary>
public sealed class ProtectedSecretStore : ISecretStore {
    private static readonly byte[] DpapiMagic = "WCD1"u8.ToArray();
    private static readonly byte[] KeyFileMagic = "WCK1"u8.ToArray();
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WonderlandChat secrets v1");
    private static readonly AeadAlgorithm Aead = AeadAlgorithm.XChaCha20Poly1305;

    private readonly string _path;
    private readonly string _keyFilePath;
    private readonly Lock _lock = new();

    public ProtectedSecretStore(string path, string keyFilePath) {
        this._path = path;
        this._keyFilePath = keyFilePath;
    }

    /// <summary>How the secrets are protected, for the settings UI.</summary>
    public static string Protection { get; private set; } = "not saved yet";

    public ClientSecrets Load() {
        lock (this._lock) {
            if (!File.Exists(this._path)) {
                return new ClientSecrets();
            }

            var data = File.ReadAllBytes(this._path);
            var magic = data.AsSpan(0, Math.Min(4, data.Length));
            var body = data.AsSpan(Math.Min(4, data.Length)).ToArray();

            if (magic.SequenceEqual(DpapiMagic)) {
                Protection = "Windows DPAPI";
                return ClientSecrets.Deserialize(ProtectedData.Unprotect(body, Entropy, DataProtectionScope.CurrentUser));
            }

            if (magic.SequenceEqual(KeyFileMagic)) {
                Protection = "local key file";
                var nonce = body.AsSpan(0, Aead.NonceSize);
                using var key = this.LoadOrCreateFileKey();
                var plaintext = Aead.Decrypt(key, nonce, KeyFileMagic, body.AsSpan(Aead.NonceSize))
                                ?? throw new CryptographicException("The secrets file couldn't be decrypted with the local key file.");
                return ClientSecrets.Deserialize(plaintext);
            }

            throw new CryptographicException("Unrecognised secrets file format.");
        }
    }

    public void Save(ClientSecrets secrets) {
        lock (this._lock) {
            var plaintext = secrets.Serialize();
            byte[] output;

            try {
                output = [.. DpapiMagic, .. ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser)];
                Protection = "Windows DPAPI";
            } catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException or EntryPointNotFoundException or DllNotFoundException) {
                using var key = this.LoadOrCreateFileKey();
                var nonce = RandomNumberGenerator.GetBytes(Aead.NonceSize);
                output = [.. KeyFileMagic, .. nonce, .. Aead.Encrypt(key, nonce, KeyFileMagic, plaintext)];
                Protection = "local key file";
            }

            AtomicFile.Write(this._path, output);
        }
    }

    private Key LoadOrCreateFileKey() {
        if (!File.Exists(this._keyFilePath)) {
            AtomicFile.Write(this._keyFilePath, RandomNumberGenerator.GetBytes(Aead.KeySize));
        }

        return Key.Import(Aead, File.ReadAllBytes(this._keyFilePath), KeyBlobFormat.RawSymmetricKey);
    }

    /// <summary>One secrets file per character and server.</summary>
    public static ProtectedSecretStore For(ulong contentId, string serverUrl) {
        var directory = Services.PluginInterface.ConfigDirectory.FullName;
        var serverHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serverUrl.Trim().ToLowerInvariant())))[..12];
        return new ProtectedSecretStore(
            Path.Combine(directory, $"secrets-{contentId:X16}-{serverHash}.bin"),
            Path.Combine(directory, "local.key"));
    }
}
