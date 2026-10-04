using System.Security.Cryptography;
using System.Text;
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
    /// The store for a character's secrets for a server address. If only an old-style file exists for it, that is moved
    /// to the new name first (and kept): see the class summary.
    /// </summary>
    /// <param name="storeAt">The store for a file path (the plugin's encrypts; tests use plain files).</param>
    /// <param name="log">Told when an old file is moved.</param>
    /// <exception cref="SecretsServerMismatchException">The old file belongs to another address.</exception>
    public static ServerBoundSecretStore Open(string directory, ulong contentId, string serverUrl, Func<string, ISecretStore> storeAt, Action<string>? log = null) {
        var path = Path.Combine(directory, FileName(contentId, serverUrl));
        var store = new ServerBoundSecretStore(storeAt(path), serverUrl, path);
        if (Exists(path)) {
            return store;
        }

        var legacyPath = Path.Combine(directory, LegacyFileName(contentId, serverUrl));
        if (!Exists(legacyPath)) {
            return store;
        }

        // Refused if it names another address: it was moved for that one, and this address's short hash collides with it.
        var legacy = new ServerBoundSecretStore(storeAt(legacyPath), serverUrl, legacyPath);
        var secrets = legacy.Load();
        store.Save(secrets.Clone());
        // The original bytes stay in the old file's .bak; this copy names its address from now on.
        legacy.Save(secrets);
        log?.Invoke($"Moved your LookingGlass keys for {serverUrl.Trim()} to {Path.GetFileName(path)}; the old file {Path.GetFileName(legacyPath)} is kept as a backup.");
        return store;
    }

    /// <summary>
    /// Moves every character's old-style file for <paramref name="serverUrl"/> to its new name (see <see cref="Open"/>).
    /// Run for the configured address when the plugin starts, so its old files name their address before the user
    /// could switch to an address whose short hash collides with them. A file that can't be moved is left as it is.
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
                Open(directory, contentId, serverUrl, storeAt, log);
                moved++;
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
}

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

/// <summary>A secrets file belongs to another server address than the one it was opened for.</summary>
public sealed class SecretsServerMismatchException(string path, string belongsTo, string openedFor) : IOException(
    $"{Path.GetFileName(path)} holds LookingGlass keys for {belongsTo}, not {openedFor}, so it isn't used: one server's keys and login are " +
    "never used with another. If you copied or renamed secrets files by hand, put them back; otherwise this address may have been " +
    "chosen to collide with another server's file name.") {
    public string BelongsTo { get; } = belongsTo;
}
