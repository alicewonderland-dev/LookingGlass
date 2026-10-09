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

    /// <summary>
    /// The server address these secrets belong to, as <see cref="ServerSecretFiles.NormaliseUrl"/> gives it. A store
    /// bound to an address (<see cref="ServerBoundSecretStore"/>) refuses secrets that name another, so identities
    /// for different servers never mix. Null in files from before it was recorded, and in new, unsaved secrets.
    /// </summary>
    public string? ServerUrl { get; set; }

    /// <summary>The origin of <see cref="ServerUrl"/> (see <see cref="Client.ServerOrigin"/>), for reference.</summary>
    public string? ServerOrigin { get; set; }

    public byte[]? SigningPrivateKey { get; set; }
    public byte[]? AgreementPrivateKey { get; set; }
    public string? DeviceToken { get; set; }

    /// <summary>
    /// The login "Sign out everywhere else" is replacing <see cref="DeviceToken"/> with, saved before the request is sent, so
    /// an answer lost on the way doesn't lose the only login the server still knows for this computer. The next login tries
    /// it first: if it works it becomes <see cref="DeviceToken"/>, and if the old one works instead it is dropped.
    /// </summary>
    public string? PendingDeviceToken { get; set; }

    /// <summary>The server's ID (hex) of this computer's device, as its list of devices last said.</summary>
    public string? ThisDeviceId { get; set; }

    /// <summary>
    /// When the server says this computer's login was last used by this computer (AuthenticateOk.used_unix, Unix seconds): if
    /// its next login says the login was used at another time since, a copy of it was used elsewhere. Null until this login
    /// was used once (a new login starts afresh).
    /// </summary>
    public long? LastLoginUnix { get; set; }

    /// <summary>
    /// When this computer last used "Sign out everywhere else", as the server dated it (Devices.signed_out_at_unix), with this
    /// login. If it is later signed out by its own device's ID at that same time, it lost its own login since; at another
    /// time, a copy of its login did it.
    /// </summary>
    public long? SignedOutOthersAt { get; set; }

    /// <summary>
    /// The nonces (hex) of this computer's logins whose answers haven't come (yet), at most a few: if the server says the login
    /// was last used with one of them, that was this computer, not a copy of its login elsewhere. Cleared once a login is answered.
    /// </summary>
    public List<string>? UnansweredLoginNonces { get; set; }

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
    /// seen-set, this survives restarts. Saved along with other changes, not per message. Local chat's senders are
    /// kept here too, under <see cref="ClientSession.LocalReplayKey"/> ("local", which no channel ID can be).
    /// </summary>
    public Dictionary<string, Dictionary<long, long>> NewestMessageTimes { get; set; } = new();

    /// <summary>
    /// Channel ID → sender → the message IDs (hex) of the messages accepted at the time <see cref="NewestMessageTimes"/> has
    /// (a few: two in one millisecond are possible), so those, sent again with the same time, count as had too. Saved with them.
    /// </summary>
    public Dictionary<string, Dictionary<long, List<string>>> NewestMessageIds { get; set; } = new();

    /// <summary>
    /// Channel ID → what was already had when that channel's catch-up last failed, while it is still to be done: live messages
    /// accepted since don't move <see cref="LastMessageIds"/> on, and the missed messages are judged against this rather
    /// than against the newer messages had since (see <see cref="CatchUpGap"/>). Gone once a catch-up of the channel completes.
    /// </summary>
    public Dictionary<string, CatchUpGap> CatchUpGaps { get; set; } = new();

    /// <summary>
    /// Channel ID → the recent membership changes of its log (joins, leaves, removals, places moved to new keys), with their
    /// times: a caught-up message under a key made before one of them must be dated before it (give or take a little).
    /// Not secret. Kept for <see cref="FormerMember.KeptFor"/>.
    /// </summary>
    public Dictionary<string, MembershipChanges> MembershipChanges { get; set; } = new();

    /// <summary>
    /// Channel ID → the newest number the server stored a message of the channel under (ChatMessage.server_id) that this
    /// client has had, live or caught up: message catch-up asks for what came after it. The server's word, used only to
    /// ask; it decides nothing about which messages are accepted. Saved with the message times.
    /// </summary>
    public Dictionary<string, ulong> LastMessageIds { get; set; } = new();

    /// <summary>
    /// Channel ID → user → the keys a member had when the log says they left or were removed, and where: a message of
    /// theirs caught up after they left is checked against these, and only if made under a key from before they left.
    /// Not secret. Kept for <see cref="FormerMember.KeptFor"/>, at most <see cref="FormerMember.KeptPerChannel"/> per channel.
    /// </summary>
    public Dictionary<string, Dictionary<long, FormerMember>> FormerMembers { get; set; } = new();

    /// <summary>Users whose invites are declined unseen and whose messages are hidden.</summary>
    public HashSet<long> BlockedUsers { get; set; } = new();

    /// <summary>
    /// The IDs (hex) of the account's devices (logins) this computer has seen, those listed last first (at most
    /// <see cref="ClientSession.MaxKnownDevices"/>): a device listed that isn't one of them, and isn't this computer's, signed in
    /// since, and the player is told (see "Other computers signing in" in docs/design.md). Null until the list was first had
    /// here (a new computer, or one from before this): that first list is taken as it is, without telling anything. Not secret.
    /// </summary>
    public List<string>? KnownDevices { get; set; }

    /// <summary>
    /// Channel ID → the membership verified from its log, at the newest position verified. Not
    /// secret, but kept so the next session carries on from there and notices a server that
    /// shows an older log. Kept after leaving a channel, as name versions are.
    /// </summary>
    public Dictionary<string, MembershipCheckpoint> Memberships { get; set; } = new();

    /// <summary>Channel ID → epoch → the log position its key was made for (pruned with <see cref="EpochKeys"/>).</summary>
    public Dictionary<string, Dictionary<ulong, KeyPosition>> EpochKeyPositions { get; set; } = new();

    /// <summary>
    /// "Reset my identity", for a lost or stolen key: new identity keys, and nothing kept that belongs to the old
    /// identity on this server. Call only while no session uses these secrets; the next session starts unregistered.
    /// <list type="bullet">
    /// <item>Replaced or dropped: the identity keys; the login (device token and user ID), which is the old registration's
    /// and would otherwise log the new keys in as the old identity, and the devices of the account seen; every channel key and where it was made (sealed to the
    /// old keys, which the new ones can't open); and the pin of your own old keys.</item>
    /// <item>Kept, as they are about others or about the channels, not about the old keys: the keys pinned for other users
    /// (so a server can't swap them unnoticed now), blocked users, and per channel the newest verified log position, name
    /// version and message times (so a server can't roll a channel back, or replay old messages, once the new identity's
    /// places are moved to it). None of them lets anyone act as the old identity.</item>
    /// </list>
    /// Registering the new keys (through the Lodestone) revokes the old identity's logins on the server and stops its keys
    /// signing in; its places in channels (ranks and invites too) move to the new keys, and their members are told.
    /// Copies of the old identity in other files are removed with <see cref="ServerSecretFiles.ResetIdentity"/>.
    /// </summary>
    public void ResetIdentity() {
        this.ForgetIdentity();
        using var keys = Crypto.IdentityKeys.Generate();
        var (signing, agreement) = keys.ExportPrivateKeys();
        this.SigningPrivateKey = signing;
        this.AgreementPrivateKey = agreement;
    }

    /// <summary>
    /// Drops the identity and everything that belongs to it, as <see cref="ResetIdentity"/> does, without making new keys:
    /// for copies of an identity being reset (another address's file, a backup). What is about others and the channels is
    /// kept, as there. A session started on these secrets has no identity, so it registers (or a move can carry one over).
    /// </summary>
    public void ForgetIdentity() {
        if (this.UserId is { } me) {
            this.PinnedIdentities.Remove(me);
        }

        this.SigningPrivateKey = null;
        this.AgreementPrivateKey = null;
        this.DeviceToken = null;
        this.UserId = null;
        // The new identity registers afresh: its first list of devices is taken as it is, and its logins are new.
        this.KnownDevices = null;
        this.PendingDeviceToken = null;
        this.ThisDeviceId = null;
        this.LastLoginUnix = null;
        this.SignedOutOthersAt = null;
        this.UnansweredLoginNonces = null;
        this.EpochKeys.Clear();
        this.EpochKeyPositions.Clear();
    }

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

    /// <summary>
    /// The keys changed because the user re-verified their character with new ones, as a channel's membership log says (a
    /// key recovered entry): expected, so not a "key changed" warning, but shown until the user compares the new ones.
    /// </summary>
    public bool KeyRecovered { get; set; }

    /// <summary>The user confirmed these keys (compared fingerprints). Cleared when the keys change.</summary>
    public bool Compared { get; set; }
}

/// <summary>A member who left a channel (or was removed), as the channel's log said (see <see cref="ClientSecrets.FormerMembers"/>).</summary>
public sealed class FormerMember {
    /// <summary>How long one is remembered: longer than a server keeps messages by default (7 days).</summary>
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(8);

    /// <summary>The most remembered per channel (the most recent go last).</summary>
    public const int KeptPerChannel = 50;

    public byte[] SigningPublicKey { get; set; } = [];
    public byte[] AgreementPublicKey { get; set; } = [];

    /// <summary>The log entry by which they left: keys made before it were sealed to them.</summary>
    public ulong LeftAtSeq { get; set; }

    /// <summary>When that entry was made (Unix ms, as signed in it).</summary>
    public long LeftAtMs { get; set; }
}

/// <summary>
/// A channel whose catch-up failed (see <see cref="ClientSecrets.CatchUpGaps"/>): the newest message times (and IDs) had from
/// each sender when it did, and the IDs of the messages accepted since (at most <see cref="MaxAcceptedSince"/>), which
/// the next catch-up treats as had.
/// </summary>
public sealed class CatchUpGap {
    public const int MaxAcceptedSince = 5000;

    public Dictionary<long, long> Times { get; set; } = new();
    public Dictionary<long, List<string>> Ids { get; set; } = new();
    public List<string> AcceptedSince { get; set; } = new();
}

/// <summary>A channel's recent membership changes (see <see cref="ClientSecrets.MembershipChanges"/>).</summary>
public sealed class MembershipChanges {
    /// <summary>The log entries that changed who is a member (or under which keys), oldest first.</summary>
    public List<MembershipChange> Changes { get; set; } = new();

    /// <summary>From this log entry on, every change is in <see cref="Changes"/>: those before weren't recorded, or were dropped as old.</summary>
    public ulong CompleteFrom { get; set; }
}

/// <summary>One membership change of a channel's log (see <see cref="ClientSecrets.MembershipChanges"/>).</summary>
public sealed class MembershipChange {
    /// <summary>The entry's position in the log.</summary>
    public ulong Seq { get; set; }

    /// <summary>Its time (Unix ms), as the entry says, but never later than when this client verified it.</summary>
    public long AtMs { get; set; }

    /// <summary>Whose place it changed.</summary>
    public long SubjectId { get; set; }

    /// <summary>A leave, a removal or a member's place moving to new keys: the keys the subject had stopped being a member's then.</summary>
    public bool Exit { get; set; }

    /// <summary>The kind of entry it is (a <see cref="Protocol.MembershipEntryKind"/>).</summary>
    public int Kind { get; set; }

    /// <summary>
    /// A time by which it had happened that its subject and the server couldn't choose: its own, if someone else signed it (a
    /// removal), or that of a later entry someone other than its subject signed. Null if there is none (yet).
    /// </summary>
    public long? TrustedAtMs { get; set; }

    /// <summary>When this client verified it as it happened, connected (not on coming back): it had happened by then.</summary>
    public long? SeenLiveAtMs { get; set; }
}

/// <summary>A membership log position, as saved with an epoch key.</summary>
public sealed class KeyPosition {
    public ulong Seq { get; set; }
    public byte[] Hash { get; set; } = [];

    /// <summary>When its author says, signed, they made the key (Unix ms; see SealedEpochKey.created_unix_ms), or 0 if the key doesn't say.</summary>
    public long CreatedMs { get; set; }

    /// <summary>Who made the key (its author), with <see cref="CreatedMs"/>.</summary>
    public long CreatedBy { get; set; }
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

/// <summary>A <see cref="ISecretStore"/> kept in a file written by <see cref="AtomicFile"/>, whose loads fall back to its backup.</summary>
public interface IFileSecretStore : ISecretStore {
    /// <summary>
    /// The file's own contents, never its backup's (see <see cref="AtomicFile.ReadFileOnly{T}"/>): for deciding things the
    /// backup, which may be older, mustn't decide.
    /// </summary>
    /// <returns>Null if the file doesn't exist.</returns>
    /// <exception cref="Exception">It can't be read or used (empty, damaged, can't be decrypted).</exception>
    ClientSecrets? LoadFileOnly();
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
public sealed class FileSecretStore(string path, Action<string>? warn = null) : IFileSecretStore {
    public ClientSecrets Load() {
        return AtomicFile.Read(path, ClientSecrets.Deserialize, warn) ?? new ClientSecrets();
    }

    public ClientSecrets? LoadFileOnly() => AtomicFile.ReadFileOnly(path, ClientSecrets.Deserialize);

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

    /// <summary>
    /// Reads a file written by <see cref="Write"/>, but never its backup: for deciding something from what the file
    /// itself holds, which an older backup mustn't decide for it.
    /// </summary>
    /// <param name="parse">Turns the bytes into the result; throws if they're unusable.</param>
    /// <returns>The parsed file, or null if it doesn't exist.</returns>
    /// <exception cref="InvalidDataException">The file is empty.</exception>
    public static T? ReadFileOnly<T>(string path, Func<byte[], T> parse) where T : class {
        lock (LockFor(path)) {
            return File.Exists(path) ? Parse(File.ReadAllBytes(path), parse) : null;
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
