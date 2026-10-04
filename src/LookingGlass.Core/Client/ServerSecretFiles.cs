using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LookingGlass.Core.Client;

/// <summary>
/// Where a character's secrets for one server address are kept: one file per character and address, named after a
/// hash of the address, and holding the address itself, which is checked whenever the file is used.
///
/// Earlier versions named files after 48 bits of the hash and didn't store the address, so a different address whose
/// short hash collides (which can be found offline) would have been given another server's keys and login. Names now
/// use 128 bits, and an old file is moved to its new name the first time its address is used, stamped with that
/// address. The old file is kept as a backup, and stamped too, so it can't be taken over by a colliding address later.
///
/// Only an old file that was never moved (it names no address) is moved by itself. A stamped one is a backup of how
/// things were when it was moved: moving it again whenever the new file went missing would silently bring back an old
/// login, old channel keys, or a key replaced since. Instead it is offered (<see cref="FindBackup"/>) and restored only
/// when the user asks (<see cref="RestoreBackup"/>). Files are never deleted.
/// </summary>
public static partial class ServerSecretFiles {
    private const int HashHexDigits = 32;
    private const int LegacyHashHexDigits = 12;

    /// <summary>The form of a server address secrets are filed under: trimmed and lowercase, as since 0.1.</summary>
    public static string NormaliseUrl(string serverUrl) => serverUrl.Trim().ToLowerInvariant();

    /// <summary>The secrets file for a character and server address: 128 bits of the address's SHA-256.</summary>
    public static string FileName(ulong contentId, string serverUrl) => $"secrets-{contentId:X16}-{Hash(serverUrl, HashHexDigits)}.bin";

    /// <summary>The name earlier versions used: 48 bits of the hash, which a colliding address can be found for.</summary>
    public static string LegacyFileName(ulong contentId, string serverUrl) => $"secrets-{contentId:X16}-{Hash(serverUrl, LegacyHashHexDigits)}.bin";

    /// <summary>
    /// The store for a character's secrets for a server address. If only an old-style file that was never moved exists
    /// for it, that is moved to the new name first (and kept); one moved before is left as a backup: see the class summary.
    /// </summary>
    /// <param name="storeAt">The store for a file path (the plugin's encrypts; tests use plain files).</param>
    /// <param name="log">Told when an old file is moved.</param>
    /// <exception cref="SecretsServerMismatchException">The old file belongs to another address.</exception>
    public static ServerBoundSecretStore Open(string directory, ulong contentId, string serverUrl, Func<string, ISecretStore> storeAt, Action<string>? log = null) {
        var path = Path.Combine(directory, FileName(contentId, serverUrl));
        var store = new ServerBoundSecretStore(storeAt(path), serverUrl, path);
        if (!Exists(path)) {
            TryMigrate(directory, contentId, serverUrl, storeAt, store, log);
        }

        return store;
    }

    /// <summary>Moves the old-style file for an address to <paramref name="store"/>, if there is one that was never moved.</summary>
    /// <returns>Whether it moved one.</returns>
    /// <exception cref="SecretsServerMismatchException">The old file belongs to another address.</exception>
    private static bool TryMigrate(string directory, ulong contentId, string serverUrl, Func<string, ISecretStore> storeAt, ServerBoundSecretStore store,
        Action<string>? log) {
        var legacyPath = Path.Combine(directory, LegacyFileName(contentId, serverUrl));
        // The file itself, not just its backup: when only the backup is left, that may be the original bytes a move kept
        // (see below), which name no address but were moved all the same. Offered as a backup instead.
        if (!File.Exists(legacyPath)) {
            return false;
        }

        // Refused if it names another address: it was moved for that one, and this address's short hash collides with it.
        var legacy = new ServerBoundSecretStore(storeAt(legacyPath), serverUrl, legacyPath);
        var secrets = legacy.Load();
        if (IsStamped(secrets)) {
            // Moved before, for this address: a backup now, never restored without asking.
            return false;
        }

        store.Save(secrets.Clone());
        // The original bytes stay in the old file's .bak; this copy names its address from now on, which marks it moved.
        legacy.Save(secrets);
        log?.Invoke($"Moved your LookingGlass keys for {serverUrl.Trim()} to {Path.GetFileName(store.FilePath)}; the old file {Path.GetFileName(legacyPath)} is kept as a backup.");
        return true;
    }

    /// <summary>
    /// The old-style file kept for a character and address, if it holds an identity and the address has none of its own
    /// (no identity keys, no login; say, the new file was lost): what the user may choose to restore. Reads and decrypts
    /// files. An old file that was never moved is moved first (as <see cref="Open"/> does), and then isn't a backup.
    /// </summary>
    /// <exception cref="SecretsServerMismatchException">The old file belongs to another address.</exception>
    public static SecretsBackup? FindBackup(string directory, ulong contentId, string serverUrl, Func<string, ISecretStore> storeAt) {
        var legacyPath = Path.Combine(directory, LegacyFileName(contentId, serverUrl));
        var file = File.Exists(legacyPath) ? legacyPath : File.Exists(AtomicFile.BackupPath(legacyPath)) ? AtomicFile.BackupPath(legacyPath) : null;
        if (file == null || HoldsAnything(Open(directory, contentId, serverUrl, storeAt).Load())) {
            return null;
        }

        var backup = new ServerBoundSecretStore(storeAt(legacyPath), serverUrl, legacyPath).Load();
        return HoldsIdentity(backup) ? new SecretsBackup(file, new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero)) : null;
    }

    /// <summary>
    /// Restores the backup <see cref="FindBackup"/> offers to the address's own file, stamped with the address. The old
    /// file is kept. Only when the user asked: it may be older than they think (an old login, old channel keys, or a key
    /// they have replaced since, which the server then refuses).
    /// </summary>
    /// <exception cref="InvalidOperationException">The address has an identity or login of its own, or there is no backup with an identity. Nothing was restored.</exception>
    /// <exception cref="SecretsServerMismatchException">The old file belongs to another address.</exception>
    public static void RestoreBackup(string directory, ulong contentId, string serverUrl, Func<string, ISecretStore> storeAt) {
        var store = Open(directory, contentId, serverUrl, storeAt);
        if (HoldsAnything(store.Load())) {
            throw new InvalidOperationException($"Nothing was restored: there already is an identity for {serverUrl.Trim()}.");
        }

        var legacyPath = Path.Combine(directory, LegacyFileName(contentId, serverUrl));
        var backup = Exists(legacyPath) ? new ServerBoundSecretStore(storeAt(legacyPath), serverUrl, legacyPath).Load() : null;
        if (backup == null || !HoldsIdentity(backup)) {
            throw new InvalidOperationException($"Nothing was restored: there is no backup of an identity for {serverUrl.Trim()}.");
        }

        store.Save(backup);
    }

    /// <summary>
    /// "Reset my identity" for a character at an address (see <see cref="ClientSecrets.ResetIdentity"/>), everywhere the
    /// old identity is kept. First every other file of the character whose signing key is the one being reset (another
    /// address it was carried to by a move, the old-style file, any of their .bak files) has the identity dropped
    /// (<see cref="ClientSecrets.ForgetIdentity"/>): kept as files, with what they hold about others and the channels,
    /// so nothing the user needs is lost, but never loadable as the old identity again. Then the address's own file gets
    /// new keys. Each file changed is written twice, so its .bak (which a write fills with the previous version) holds
    /// the new contents too. Only while no session uses these files.
    ///
    /// Without this, switching to another address, or a backup, would bring the old key and login back, and "Register
    /// again" (which keeps the key) would hand the account back to it. The server refuses to register a replaced key
    /// anyway; this keeps the client from trying, and from using a login the reset should have ended.
    /// </summary>
    /// <param name="log">Told about each file changed, and each that couldn't be read or written.</param>
    /// <returns>The other files the old identity was removed from, and the problems met (files that couldn't be checked).</returns>
    /// <exception cref="SecretsServerMismatchException">The address's own file belongs to another address. Nothing was changed.</exception>
    public static IdentityReset ResetIdentity(string directory, ulong contentId, string serverUrl, Func<string, ISecretStore> storeAt, Action<string>? log = null) {
        var store = Open(directory, contentId, serverUrl, storeAt, log);
        var secrets = store.Load();
        var scrubbed = new List<string>();
        var problems = new List<string>();
        // Copies first: if this stops half way, the address's own file still has the key, and a reset tried again finds them.
        if (secrets.SigningPrivateKey is { } oldKey) {
            foreach (var path in CharacterFiles(directory, contentId).Where(path => !SamePath(path, store.FilePath))) {
                try {
                    if (ForgetIdentityIn(path, oldKey, storeAt)) {
                        scrubbed.Add(path);
                        log?.Invoke($"Removed the reset identity from {Path.GetFileName(path)}.");
                    }
                } catch (Exception ex) {
                    // Reported, never skipped silently; the other files and the reset itself go on.
                    problems.Add($"Couldn't check {Path.GetFileName(path)} for the old identity: {ex.Message}");
                    log?.Invoke(problems[^1]);
                }
            }
        }

        secrets.ResetIdentity();
        store.Save(secrets);
        // Again, so the .bak doesn't keep the old identity either.
        store.Save(secrets);
        return new IdentityReset(scrubbed, problems);
    }

    /// <summary>
    /// Drops the identity whose signing key is <paramref name="oldKey"/> from a secrets file and its .bak: the file's
    /// contents (with the identity dropped, if it was there) are written twice, file then backup.
    /// </summary>
    /// <returns>Whether the file or its backup held it.</returns>
    private static bool ForgetIdentityIn(string path, byte[] oldKey, Func<string, ISecretStore> storeAt) {
        // Not bound to an address: other addresses' files name their own, and are written back naming it.
        var store = storeAt(path);
        lock (AtomicFile.LockFor(path)) {
            // The file (or, if it is missing or damaged, its backup, as a load would use)...
            var current = store.Load();
            // ...and the backup on its own (one written after it would be a .bak.bak, which nothing reads).
            var backupPath = AtomicFile.BackupPath(path);
            ClientSecrets? backup = null;
            if (File.Exists(backupPath)) {
                try {
                    backup = storeAt(backupPath).Load();
                } catch (Exception ex) when (ex is InvalidDataException or JsonException or CryptographicException) {
                    // Unreadable, so never loaded either; overwritten below if the file is rewritten.
                }
            }

            var inFile = Holds(current, oldKey);
            if (!inFile && !Holds(backup, oldKey)) {
                return false;
            }

            if (inFile) {
                current.ForgetIdentity();
            }

            store.Save(current);
            store.Save(current);
            return true;
        }
    }

    private static bool Holds(ClientSecrets? secrets, byte[] signingKey) {
        return secrets?.SigningPrivateKey is { } key && CryptographicOperations.FixedTimeEquals(key, signingKey);
    }

    /// <summary>Every secrets file of a character, new-style or old, by the path a store opens (a .bak counts as its file's).</summary>
    private static IEnumerable<string> CharacterFiles(string directory, ulong contentId) {
        if (!Directory.Exists(directory)) {
            return [];
        }

        var prefix = $"secrets-{contentId:X16}-";
        return Directory.EnumerateFiles(directory, prefix + "*")
            .Where(file => AnyName().IsMatch(Path.GetFileName(file)) && Path.GetFileName(file).StartsWith(prefix, StringComparison.Ordinal))
            .Select(file => file.EndsWith(".bak", StringComparison.Ordinal) ? file[..^".bak".Length] : file)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Saved through a <see cref="ServerBoundSecretStore"/>, which names the address: true of every file moved, or made, since.</summary>
    private static bool IsStamped(ClientSecrets secrets) => secrets.ServerUrl != null || secrets.ServerOrigin != null;

    private static bool HoldsIdentity(ClientSecrets secrets) => secrets.SigningPrivateKey != null && secrets.AgreementPrivateKey != null;

    /// <summary>Identity keys or a login: something a backup must never replace.</summary>
    private static bool HoldsAnything(ClientSecrets secrets) => secrets.SigningPrivateKey != null || secrets.DeviceToken != null;

    /// <summary>
    /// Moves every character's old-style file for <paramref name="serverUrl"/> that was never moved to its new name (see
    /// <see cref="Open"/>). Run for the configured address when the plugin starts, so its old files name their address
    /// before the user could switch to an address whose short hash collides with them. A file that can't be moved is
    /// left as it is.
    /// </summary>
    /// <returns>How many were moved.</returns>
    public static int MigrateAll(string directory, string serverUrl, Func<string, ISecretStore> storeAt, Action<string>? log = null) {
        if (!Directory.Exists(directory)) {
            return 0;
        }

        var suffix = $"-{Hash(serverUrl, LegacyHashHexDigits)}.bin";
        var moved = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "secrets-*" + suffix)) {
            var match = LegacyName().Match(Path.GetFileName(file));
            if (!match.Success || !ulong.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var contentId)
                || Exists(Path.Combine(directory, FileName(contentId, serverUrl)))) {
                continue;
            }

            try {
                var path = Path.Combine(directory, FileName(contentId, serverUrl));
                if (TryMigrate(directory, contentId, serverUrl, storeAt, new ServerBoundSecretStore(storeAt(path), serverUrl, path), log)) {
                    moved++;
                }
            } catch (Exception ex) {
                log?.Invoke($"Couldn't move {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        return moved;
    }

    /// <summary>The content IDs of the characters with a secrets file (new style) for a server address.</summary>
    public static IReadOnlyList<ulong> Characters(string directory, string serverUrl) {
        if (!Directory.Exists(directory)) {
            return [];
        }

        var suffix = $"-{Hash(serverUrl, HashHexDigits)}.bin";
        return Directory.EnumerateFiles(directory, "secrets-*" + suffix)
            .Select(file => NewName().Match(Path.GetFileName(file)))
            .Where(match => match.Success)
            .Select(match => ulong.Parse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber))
            .Distinct()
            .ToList();
    }

    /// <summary>A file, or the backup <see cref="AtomicFile.Read{T}"/> would fall back to.</summary>
    private static bool Exists(string path) => File.Exists(path) || File.Exists(AtomicFile.BackupPath(path));

    private static string Hash(string serverUrl, int hexDigits) {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormaliseUrl(serverUrl))))[..hexDigits];
    }

    [GeneratedRegex("^secrets-([0-9A-F]{16})-[0-9A-F]{12}\\.bin$")]
    private static partial Regex LegacyName();

    [GeneratedRegex("^secrets-([0-9A-F]{16})-[0-9A-F]{32}\\.bin$")]
    private static partial Regex NewName();

    /// <summary>A secrets file, new-style or old, or its .bak.</summary>
    [GeneratedRegex("^secrets-[0-9A-F]{16}-(?:[0-9A-F]{12}|[0-9A-F]{32})\\.bin(?:\\.bak)?$")]
    private static partial Regex AnyName();
}

/// <summary>What <see cref="ServerSecretFiles.ResetIdentity"/> did besides giving the address new keys.</summary>
/// <param name="Scrubbed">The other files (or their backups) the old identity was removed from.</param>
/// <param name="Problems">Files that couldn't be checked or changed, for the user: they may still hold the old identity.</param>
public sealed record IdentityReset(IReadOnlyList<string> Scrubbed, IReadOnlyList<string> Problems);

/// <summary>
/// A secret store bound to one server address: it refuses secrets that name another address (rather than use one
/// server's keys and login with another), and stamps what it saves with its address. Secrets that name no address
/// (files from before it was recorded, or nothing saved yet) are accepted, and stamped when next saved.
/// </summary>
public sealed class ServerBoundSecretStore : ISecretStore {
    private readonly ISecretStore _inner;
    private readonly string _url;
    private readonly string? _origin;
    private readonly string _path;
    private volatile bool _checked;

    /// <param name="path">The file, for messages.</param>
    public ServerBoundSecretStore(ISecretStore inner, string serverUrl, string path) {
        this._inner = inner;
        this._url = ServerSecretFiles.NormaliseUrl(serverUrl);
        this._origin = ServerOrigin.FromUrl(serverUrl.Trim())?.ToString();
        this._path = path;
    }

    /// <summary>The normalised address this store is for.</summary>
    public string ServerUrl => this._url;

    /// <summary>The file it keeps the secrets in.</summary>
    public string FilePath => this._path;

    /// <exception cref="SecretsServerMismatchException">The secrets belong to another address.</exception>
    public ClientSecrets Load() {
        var secrets = this._inner.Load();
        this.Check(secrets);
        this._checked = true;
        return secrets;
    }

    /// <exception cref="SecretsServerMismatchException">The file this would replace belongs to another address.</exception>
    public void Save(ClientSecrets secrets) {
        // Never overwrite another address's file, even if nothing was loaded through this store first.
        if (!this._checked) {
            this.Load();
        }

        // Secrets are only ever saved to the store they were loaded from, but check rather than assume.
        this.Check(secrets);
        secrets.ServerUrl = this._url;
        secrets.ServerOrigin = this._origin;
        this._inner.Save(secrets);
    }

    private void Check(ClientSecrets secrets) {
        if (secrets.ServerUrl != null && secrets.ServerUrl != this._url) {
            throw new SecretsServerMismatchException(this._path, secrets.ServerUrl, this._url);
        }

        if (secrets.ServerUrl == null && secrets.ServerOrigin != null && secrets.ServerOrigin != this._origin) {
            throw new SecretsServerMismatchException(this._path, secrets.ServerOrigin, this._url);
        }
    }
}

/// <summary>An old-style secrets file kept as a backup (see <see cref="ServerSecretFiles.FindBackup"/>).</summary>
/// <param name="Path">The file (or, if only that is left, its own backup).</param>
/// <param name="SavedAt">When it was last written.</param>
public sealed record SecretsBackup(string Path, DateTimeOffset SavedAt);

/// <summary>A secrets file belongs to another server address than the one it was opened for.</summary>
public sealed class SecretsServerMismatchException(string path, string belongsTo, string openedFor) : IOException(
    $"{Path.GetFileName(path)} holds LookingGlass keys for {belongsTo}, not {openedFor}, so it isn't used: one server's keys and login are " +
    "never used with another. If you copied or renamed secrets files by hand, put them back; otherwise this address may have been " +
    "chosen to collide with another server's file name.") {
    public string BelongsTo { get; } = belongsTo;
}
