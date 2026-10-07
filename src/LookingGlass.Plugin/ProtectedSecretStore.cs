using LookingGlass.Core.Client;

namespace LookingGlass.Plugin;

/// <summary>
/// Keeps a character's keys encrypted at rest (see <see cref="LocalProtection"/>). Uses Windows DPAPI (current
/// user) when it works. Under Wine/Proton, where DPAPI may be unavailable, it
/// falls back to a random key file in the plugin's config folder: that guards
/// against accidentally sharing the secrets file, not against someone who can
/// read your files.
/// </summary>
public sealed class ProtectedSecretStore : IFileSecretStore {
    private readonly string _path;
    private readonly LocalProtection _protection;
    private readonly Action<string>? _tellUser;
    private readonly HashSet<string> _told = [];

    /// <param name="tellUser">Shows a warning to the user, such as a backup being loaded. Called on any thread.</param>
    public ProtectedSecretStore(string path, string keyFilePath, Action<string>? tellUser = null) {
        this._path = path;
        // As it always was: DPAPI's entropy names the secrets, and the key file binds nothing after its magic.
        this._protection = new LocalProtection(keyFilePath, "secrets file", "LookingGlass secrets v1", [], this.Warn, shown: true);
        this._tellUser = tellUser;
    }

    /// <summary>How the secrets are protected, for the settings UI.</summary>
    public static string Protection => LocalProtection.Protection;

    // Locks are per file and shared by every instance: a closing session and a new one for
    // the same character use different store objects but the same secrets file.
    public ClientSecrets Load() {
        // A damaged or missing file (say, after a power cut) falls back to the copy kept by the last save.
        // Someone who deleted the file to start afresh is told the backup was used, and how to reset instead.
        return AtomicFile.Read(this._path, this.Decode, message =>
                   this.Warn($"{message} To replace your LookingGlass identity instead, use \"Reset my identity\" in Settings."))
               ?? new ClientSecrets();
    }

    public ClientSecrets? LoadFileOnly() => AtomicFile.ReadFileOnly(this._path, this.Decode);

    private ClientSecrets Decode(byte[] data) => ClientSecrets.Deserialize(this._protection.Unprotect(data));

    public void Save(ClientSecrets secrets) {
        lock (AtomicFile.LockFor(this._path)) {
            AtomicFile.Write(this._path, this._protection.Protect(secrets.Serialize()));
        }
    }

    /// <summary>
    /// The protection the chat log's key is kept with: the secrets file's (DPAPI, or the same local key file), for its own
    /// purpose. Warnings about the key file go to the log only.
    /// </summary>
    public static LocalProtection ChatLogProtection() =>
        new(KeyFilePath, "chat log key file", "LookingGlass chat log v1", "chat log"u8.ToArray(), message => Services.Log.Warning(message));

    private static string KeyFilePath => Path.Combine(ConfigDirectory, "local.key");

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

    private static string ConfigDirectory => Services.PluginInterface.ConfigDirectory.FullName;

    /// <summary>
    /// One secrets file per character and server address, which holds the address and is refused for any other (see
    /// <see cref="ServerSecretFiles"/>). An old-style file for the address is moved to the new name first, and kept.
    /// </summary>
    /// <param name="tellUser">Shows a warning to the user. Called on any thread.</param>
    /// <exception cref="SecretsServerMismatchException">The file belongs to another address.</exception>
    public static ServerBoundSecretStore For(ulong contentId, string serverUrl, Action<string>? tellUser = null) {
        return ServerSecretFiles.Open(ConfigDirectory, contentId, serverUrl, path => At(path, tellUser), message => Services.Log.Information(message));
    }

    /// <summary>
    /// Moves every character's old-style secrets file for the configured address to its new name, when the plugin
    /// starts, so they name their address before the user could switch to one whose short hash collides with them.
    /// </summary>
    public static void MigrateOldFiles(string serverUrl) {
        try {
            ServerSecretFiles.MigrateAll(ConfigDirectory, serverUrl, path => At(path, null), message => Services.Log.Information(message));
        } catch (Exception ex) {
            Services.Log.Warning(ex, "Couldn't move old LookingGlass secrets files");
        }
    }

    /// <summary>
    /// The characters with an identity for <paramref name="oldUrl"/> and nothing for <paramref name="newUrl"/>: those whose
    /// identity a move to the new address could carry over. Reads (and decrypts) their files: not on the framework thread.
    /// A file that can't be read, or belongs to another address, counts as something there, never as free.
    /// </summary>
    public static IReadOnlyList<ulong> CharactersToMove(string oldUrl, string newUrl) {
        MigrateOldFiles(oldUrl);
        return ServerSecretFiles.Characters(ConfigDirectory, oldUrl)
            .Where(id => Holds(id, oldUrl, identity: true) && !Holds(id, newUrl, identity: false))
            .ToList();
    }

    /// <summary>
    /// "Reset my identity" for a character at an address, in every file that holds the old identity (see
    /// <see cref="ServerSecretFiles.ResetIdentity"/>). Only while no session uses the files. Warnings about the files
    /// read (a backup loaded, say) go to the log only: they'd be about files the user didn't pick.
    /// </summary>
    public static IdentityReset ResetIdentity(ulong contentId, string serverUrl) {
        return ServerSecretFiles.ResetIdentity(ConfigDirectory, contentId, serverUrl, path => At(path, null), message => Services.Log.Information(message));
    }

    /// <summary>
    /// The backup of a character's identity for an address that the user may restore (see <see cref="ServerSecretFiles.FindBackup"/>),
    /// or null. Reads (and decrypts) files: not on the framework thread. A file that can't be read counts as no backup.
    /// </summary>
    public static SecretsBackup? FindBackup(ulong contentId, string serverUrl) {
        try {
            return ServerSecretFiles.FindBackup(ConfigDirectory, contentId, serverUrl, path => At(path, null));
        } catch (Exception ex) {
            Services.Log.Warning(ex, "Couldn't look for a backup of a LookingGlass identity");
            return null;
        }
    }

    /// <summary>Restores the backup <see cref="FindBackup"/> offers. Only while no session uses the address's file.</summary>
    /// <exception cref="InvalidOperationException">The address has an identity of its own, or there is no backup.</exception>
    public static void RestoreBackup(ulong contentId, string serverUrl) {
        ServerSecretFiles.RestoreBackup(ConfigDirectory, contentId, serverUrl, path => At(path, null));
    }

    /// <param name="identity">Identity keys (true), or anything at all: keys or a login (false).</param>
    private static bool Holds(ulong contentId, string serverUrl, bool identity) {
        try {
            var secrets = For(contentId, serverUrl).Load();
            return identity
                ? secrets.SigningPrivateKey != null && secrets.AgreementPrivateKey != null
                : secrets.SigningPrivateKey != null || secrets.DeviceToken != null;
        } catch (Exception ex) {
            Services.Log.Warning(ex, "Couldn't read a LookingGlass secrets file");
            return !identity;
        }
    }

    private static ProtectedSecretStore At(string path, Action<string>? tellUser) {
        return new ProtectedSecretStore(path, KeyFilePath, tellUser);
    }
}
