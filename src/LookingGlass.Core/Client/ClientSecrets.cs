using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using LookingGlass.Core.Membership;

namespace LookingGlass.Core.Client;

/// <summary>
/// Everything a client must keep private and persist between sessions. One
/// instance belongs to one character on one server.
/// </summary>
public sealed class ClientSecrets {
    public int Version { get; set; } = 1;
    public byte[]? SigningPrivateKey { get; set; }
    public byte[]? AgreementPrivateKey { get; set; }
    public string? DeviceToken { get; set; }
    public long? UserId { get; set; }

    /// <summary>Identity keys seen for other users (trust on first use).</summary>
    public Dictionary<long, PinnedIdentity> PinnedIdentities { get; set; } = new();

    /// <summary>Channel ID → epoch → raw epoch key.</summary>
    public Dictionary<string, Dictionary<ulong, byte[]>> EpochKeys { get; set; } = new();

    /// <summary>
    /// Channel ID → the (epoch, revision) of the newest channel name accepted.
    /// Kept so a server can't roll a name back, even across restarts.
    /// </summary>
    public Dictionary<string, NameVersion> ChannelNameVersions { get; set; } = new();

    /// <summary>
    /// Channel ID → sender → timestamp (Unix ms) of the newest message accepted
    /// from them. Messages much older than this are replays; unlike the in-memory
    /// seen-set, this survives restarts. Saved along with other changes, not per message.
    /// </summary>
    public Dictionary<string, Dictionary<long, long>> NewestMessageTimes { get; set; } = new();

    /// <summary>Users whose invites are declined unseen and whose messages are hidden.</summary>
    public HashSet<long> BlockedUsers { get; set; } = new();

    /// <summary>
    /// Channel ID → the membership verified from its log, at the newest position verified. Not
    /// secret, but kept so the next session carries on from there and notices a server that
    /// shows an older log. Kept after leaving a channel, as name versions are.
    /// </summary>
    public Dictionary<string, MembershipCheckpoint> Memberships { get; set; } = new();

    /// <summary>Channel ID → epoch → the log position its key was made for (pruned with <see cref="EpochKeys"/>).</summary>
    public Dictionary<string, Dictionary<ulong, KeyPosition>> EpochKeyPositions { get; set; } = new();

    public ClientSecrets Clone() {
        return JsonSerializer.Deserialize<ClientSecrets>(JsonSerializer.SerializeToUtf8Bytes(this))!;
    }

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this);

    public static ClientSecrets Deserialize(byte[] data) {
        return JsonSerializer.Deserialize<ClientSecrets>(data) ?? new ClientSecrets();
    }
}

public sealed class PinnedIdentity {
    public byte[] SigningPublicKey { get; set; } = [];
    public byte[] AgreementPublicKey { get; set; } = [];
    public uint KeyVersion { get; set; }
    public string Name { get; set; } = "";
    public string WorldName { get; set; } = "";

    /// <summary>The keys changed since they were first seen, and the user hasn't confirmed the new ones yet.</summary>
    public bool KeyChangeUnacknowledged { get; set; }

    /// <summary>The user confirmed these keys (compared fingerprints). Cleared when the keys change.</summary>
    public bool Compared { get; set; }
}

/// <summary>A membership log position, as saved with an epoch key.</summary>
public sealed class KeyPosition {
    public ulong Seq { get; set; }
    public byte[] Hash { get; set; } = [];
}

/// <summary>Orders channel names: a later epoch wins, then a higher revision within the epoch.</summary>
public sealed record NameVersion(ulong Epoch, ulong Revision) : IComparable<NameVersion> {
    public int CompareTo(NameVersion? other) {
        if (other == null) {
            return 1;
        }

        var byEpoch = this.Epoch.CompareTo(other.Epoch);
        return byEpoch != 0 ? byEpoch : this.Revision.CompareTo(other.Revision);
    }
}

/// <summary>Where a client keeps its <see cref="ClientSecrets"/>.</summary>
public interface ISecretStore {
    /// <returns>The stored secrets, or a new empty instance if none exist.</returns>
    ClientSecrets Load();

    void Save(ClientSecrets secrets);
}

public sealed class InMemorySecretStore : ISecretStore {
    private byte[]? _data;

    public ClientSecrets Load() {
        var data = Volatile.Read(ref this._data);
        return data == null ? new ClientSecrets() : ClientSecrets.Deserialize(data);
    }

    public void Save(ClientSecrets secrets) {
        Volatile.Write(ref this._data, secrets.Serialize());
    }
}

/// <summary>
/// Plain JSON file. Only for the echo bot and tests: the plugin uses an
/// encrypted store instead.
/// </summary>
/// <param name="warn">Told when the file couldn't be used and its backup was loaded instead.</param>
public sealed class FileSecretStore(string path, Action<string>? warn = null) : ISecretStore {
    public ClientSecrets Load() {
        return AtomicFile.Read(path, ClientSecrets.Deserialize, warn) ?? new ClientSecrets();
    }

    public void Save(ClientSecrets secrets) {
        AtomicFile.Write(path, secrets.Serialize());
    }
}

public static class AtomicFile {
    private static readonly ConcurrentDictionary<string, Lock> Locks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One lock per file, shared by everything in the process, so two stores
    /// (say, an old session still saving and a new one starting) never write
    /// the same file at once. Reentrant, so callers may hold it around <see cref="Write"/>.
    /// </summary>
    public static Lock LockFor(string path) => Locks.GetOrAdd(Path.GetFullPath(path), _ => new Lock());

    /// <summary>Where <see cref="Write"/> keeps the previous version of a file.</summary>
    public static string BackupPath(string path) => path + ".bak";

    /// <summary>
    /// Writes to a temporary file and flushes it to disk, then replaces the target, keeping the
    /// previous version as <see cref="BackupPath"/>. A crash or power cut leaves the old file or
    /// the new one, or at worst only the backup, which <see cref="Read{T}"/> falls back to.
    /// </summary>
    public static void Write(string path, byte[] data) {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory != null) {
            Directory.CreateDirectory(directory);
        }

        lock (LockFor(path)) {
            // Unique, so a writer in another process (or a leftover from a crash) can't collide with it.
            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            try {
                WriteToDisk(temp, data);
                if (File.Exists(path)) {
                    ReplaceKeepingBackup(temp, path, BackupPath(path));
                } else {
                    File.Move(temp, path);
                }
            } finally {
                if (File.Exists(temp)) {
                    File.Delete(temp);
                }
            }
        }
    }

    /// <summary>
    /// Reads a file written by <see cref="Write"/>. If it is missing, empty or can't be parsed,
    /// its backup is used instead and <paramref name="warn"/> is told; if the backup can't be parsed
    /// either, the error names both files. Errors reading the disk aren't covered: loading an older
    /// backup then would lose whatever was saved since.
    /// </summary>
    /// <param name="parse">Turns the bytes into the result; throws if they're unusable.</param>
    /// <returns>The parsed file, or null if neither it nor a backup exists.</returns>
    public static T? Read<T>(string path, Func<byte[], T> parse, Action<string>? warn = null) where T : class {
        lock (LockFor(path)) {
            Exception? failure = null;
            if (File.Exists(path)) {
                try {
                    return Parse(File.ReadAllBytes(path), parse);
                } catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException) {
                    failure = ex;
                }
            }

            var backup = BackupPath(path);
            if (!File.Exists(backup)) {
                if (failure != null) {
                    ExceptionDispatchInfo.Throw(failure);
                }

                return null;
            }

            T result;
            try {
                result = Parse(File.ReadAllBytes(backup), parse);
            } catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException) {
                // Name both files: someone who deleted the file to start afresh needs to know the backup is in the way.
                var fullPath = Path.GetFullPath(path);
                throw new InvalidDataException(failure == null
                    ? $"{fullPath} is missing, and its backup {Path.GetFullPath(backup)} couldn't be read ({ex.Message})."
                    : $"{fullPath} couldn't be read ({failure.Message}), and neither could its backup {Path.GetFullPath(backup)} ({ex.Message}).",
                    failure ?? ex);
            }

            warn?.Invoke(failure == null
                ? $"{Path.GetFileName(path)} is missing; loaded the backup {Path.GetFileName(backup)} instead."
                : $"{Path.GetFileName(path)} couldn't be read ({failure.Message}); loaded the backup {Path.GetFileName(backup)} instead.");
            return result;
        }
    }

    private static T Parse<T>(byte[] data, Func<byte[], T> parse) {
        if (data.Length == 0) {
            throw new InvalidDataException("The file is empty.");
        }

        return parse(data);
    }

    private static void WriteToDisk(string path, ReadOnlySpan<byte> data) {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(data);
        // Without this, a power cut soon after the rename can leave an empty or garbled file.
        stream.Flush(flushToDisk: true);
    }

    private static void ReplaceKeepingBackup(string temp, string path, string backup) {
        try {
            File.Replace(temp, path, backup, ignoreMetadataErrors: true);
            return;
        } catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException) {
            // Not supported everywhere (some Wine versions and file systems). Do it in steps,
            // each of which leaves a complete file under the path or the backup path.
        }

        if (File.Exists(path)) {
            var backupTemp = $"{backup}.{Guid.NewGuid():N}.tmp";
            try {
                WriteToDisk(backupTemp, File.ReadAllBytes(path));
                File.Move(backupTemp, backup, overwrite: true);
            } finally {
                if (File.Exists(backupTemp)) {
                    File.Delete(backupTemp);
                }
            }
        }

        File.Move(temp, path, overwrite: true);
    }
}
