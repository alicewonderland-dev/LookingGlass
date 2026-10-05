using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using LookingGlass.Core.Membership;
using LookingGlass.Protocol;

namespace LookingGlass.Server.Data;

public sealed record UserRow(
    long UserId,
    string Name,
    uint WorldId,
    string WorldName,
    byte[] SigningKey,
    byte[] AgreementKey,
    byte[] BindingSignature,
    uint KeyVersion,
    bool IsDebug) {
    public User ToProto() => new() { UserId = this.UserId, Name = this.Name, WorldId = this.WorldId, WorldName = this.WorldName };

    /// <summary>The identity keys the user is registered with now.</summary>
    public MemberKeys Keys => new(this.SigningKey, this.AgreementKey);

    public UserIdentity ToIdentity() => new() {
        User = this.ToProto(),
        KeyVersion = this.KeyVersion,
        Identity = new IdentityBundle {
            SigningPublicKey = ByteString.CopyFrom(this.SigningKey),
            AgreementPublicKey = ByteString.CopyFrom(this.AgreementKey),
            BindingSignature = ByteString.CopyFrom(this.BindingSignature),
        },
    };
}

/// <param name="LogHead">The newest entry in the channel's membership log.</param>
public sealed record ChannelRow(string ChannelId, ulong Epoch, bool RekeyPending, EncryptedName? Name, LogPosition LogHead);

/// <param name="Forgotten">
/// The user removed the channel from their list ("Remove from my list", see <see cref="Database.ForgetStaleMembership"/>):
/// still a member as far as the log (and so rekeys) go, but the channel isn't theirs to see or use any more.
/// </param>
/// <param name="CurrentKeys">The row's keys (those the log admitted them with) are the user's current keys.</param>
/// <param name="AwaitingKey">
/// The place moved to the user's new keys (a key recovered entry), and no rekey has given them the channel's key since: they
/// can't carry the channel's name into a new one, so they aren't asked to, unless nobody else holds the key (then one of
/// them names it anew; see "When nobody holds the key" in docs/design.md).
/// </param>
public sealed record MemberRow(UserRow User, Rank Rank, bool Forgotten = false, bool CurrentKeys = true, bool AwaitingKey = false);

/// <param name="Entry">The invite's entry in the channel's membership log.</param>
/// <param name="Forgotten">The invitee removed it from their list (see <see cref="Database.ForgetStaleMembership"/>).</param>
public sealed record InviteRow(string ChannelId, UserRow Invitee, UserRow Inviter, SealedBox SealedName, byte[] Signature, long CreatedUnix, MembershipEntry? Entry,
    bool Forgotten = false);

public enum RekeyResult {
    Applied,
    EpochStale,
    MembershipChanged,
}

/// <summary>What <see cref="Database.ForgetStaleMembership"/> did.</summary>
public enum ForgetResult {
    /// <summary>The user's places in the channel (all under keys they no longer have) are forgotten.</summary>
    Forgotten,

    /// <summary>Nothing: the user is in the channel under their current keys.</summary>
    Current,

    /// <summary>Nothing: the user isn't listed in the channel (or there is no such channel).</summary>
    NotListed,
}

/// <summary>
/// What a registration needs to move the account's places to the keys it registers (see <see cref="Database.RegisterUser"/>):
/// the keys' <see cref="Core.Crypto.KeyRecoveryProof"/> signature, the rules each key recovered entry is checked with, and its time.
/// </summary>
public sealed record KeyRecovery(byte[] Proof, IMembershipProvider Membership, long TimestampMs);

/// <summary>A place (a membership, or an invite) a registration moved to the new keys, and the key recovered entry that says so.</summary>
public sealed record RecoveredPlace(string ChannelId, MembershipEntry Entry, bool Member);

/// <summary>What <see cref="Database.RegisterUser"/> did.</summary>
/// <param name="KeysChanged">The user's identity keys changed.</param>
public sealed record Registration(UserRow User, bool KeysChanged) {
    /// <summary>The places moved to the keys registered, oldest first.</summary>
    public IReadOnlyList<RecoveredPlace> Recovered { get; init; } = [];
}

/// <summary>A registration names a signing key the account replaced or retired, which it never registers again.</summary>
public sealed class KeyRetiredException() : Exception("That identity key was replaced or retired, so it can't be registered again.");

/// <summary>A registration names a signing key another account is registered with: a key belongs to one account at most.</summary>
public sealed class KeyInUseException() : Exception("That identity key is registered to another account.");

/// <summary>The database file can't be used by this version of the server. It is left as it was.</summary>
public sealed class UnsupportedDatabaseException(string message) : Exception(message);

/// <summary>
/// SQLite storage. Every multi-statement change runs in one transaction.
/// Methods are synchronous (SQLite is in-process) and short.
/// </summary>
public sealed class Database {
    private const int SchemaVersion = 8;
    private const int KeptEpochs = 4;

    private readonly string _path;
    private readonly string _connectionString;
    private readonly ILogger? _logger;

    /// <param name="logger">Where upgrading the file reports what the operator should look at.</param>
    /// <exception cref="UnsupportedDatabaseException">The file is from a version whose channels this one can't use.</exception>
    public Database(string path, ILogger? logger = null) {
        this._path = path;
        this._logger = logger;
        this._connectionString = new SqliteConnectionStringBuilder {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();

        this.Migrate();
    }

    private SqliteConnection Open() {
        var connection = new SqliteConnection(this._connectionString);
        connection.Open();
        Execute(connection, null, "PRAGMA foreign_keys = ON;");
        return connection;
    }

    private void Migrate() {
        using var connection = this.Open();
        Execute(connection, null, "PRAGMA journal_mode = WAL;");
        Execute(connection, null, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);");

        var current = Convert.ToInt32(Scalar(connection, null, "SELECT COALESCE(MAX(version), 0) FROM schema_version;"));
        if (current >= SchemaVersion) {
            return;
        }

        // Version 2 signs key commitments and name revisions, so version 1's epoch keys and
        // names no longer verify and its channels would stall at the next membership change.
        // Version 1 was never released: rather than migrate, ask for a fresh database.
        if (current == 1 && Convert.ToInt64(Scalar(connection, null, "SELECT COUNT(*) FROM channels;")) > 0) {
            throw new UnsupportedDatabaseException(
                $"The database {Path.GetFullPath(this._path)} was made by a pre-release version (schema 1) whose channels this version can't use. " +
                "It has not been changed. Stop the server and delete or move that file (with its -wal and -shm files, if any), then start again " +
                "to create a new database. Everyone will need to register again.");
        }

        // Version 4 derives membership from a signed log that schema 2 and 3 channels don't have, and
        // nobody can sign one for them now: their members would all be ghosts to the new clients.
        // 0.1 was a pre-release, so as above, ask for a fresh database rather than migrate.
        if (current is 2 or 3 && Convert.ToInt64(Scalar(connection, null, "SELECT COUNT(*) FROM channels;")) > 0) {
            throw new UnsupportedDatabaseException(
                $"The database {Path.GetFullPath(this._path)} was made by version 0.1 (schema {current}), whose channels have no signed membership log, " +
                "so this version can't use them. It has not been changed. Stop the server and delete or move that file (with its -wal and -shm files, " +
                "if any), then start again to create a new database. Everyone will need to register again and create their channels anew.");
        }

        using var tx = connection.BeginTransaction();
        List<(byte[] Key, string Accounts, long? RetiredFor)> traces = [];
        if (current < 1) {
            CreateVersion1(connection, tx);
        }

        if (current < 2) {
            // Signed name revisions (so an older name can't be replayed within an epoch), and
            // epoch key commitments (so a member can't hand out different keys undetected).
            Execute(connection, tx, """
                ALTER TABLE channels ADD COLUMN name_revision INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE epoch_keys ADD COLUMN key_commitment BLOB NOT NULL DEFAULT x'';
                INSERT INTO schema_version (version) VALUES (2);
                """);
        }

        if (current < 3) {
            // The name version a rekey carried over (NULL for new channels and renames).
            Execute(connection, tx, """
                ALTER TABLE channels ADD COLUMN name_source_epoch INTEGER;
                ALTER TABLE channels ADD COLUMN name_source_revision INTEGER;
                INSERT INTO schema_version (version) VALUES (3);
                """);
        }

        if (current < 4) {
            // The signed membership log, and the state it leads to: the keys each member and invitee
            // was admitted with, each invite's entry, and the log position keys and names were made at.
            Execute(connection, tx, """
                CREATE TABLE membership_log (
                    channel_id TEXT    NOT NULL REFERENCES channels (channel_id) ON DELETE CASCADE,
                    seq        INTEGER NOT NULL,
                    hash       BLOB    NOT NULL,
                    entry      BLOB    NOT NULL,
                    PRIMARY KEY (channel_id, seq)
                );
                ALTER TABLE channels ADD COLUMN log_seq INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE channels ADD COLUMN log_hash BLOB NOT NULL DEFAULT x'';
                ALTER TABLE channels ADD COLUMN name_log_seq INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE channels ADD COLUMN name_log_hash BLOB NOT NULL DEFAULT x'';
                ALTER TABLE members ADD COLUMN signing_key BLOB NOT NULL DEFAULT x'';
                ALTER TABLE members ADD COLUMN agreement_key BLOB NOT NULL DEFAULT x'';
                ALTER TABLE invites ADD COLUMN signing_key BLOB NOT NULL DEFAULT x'';
                ALTER TABLE invites ADD COLUMN agreement_key BLOB NOT NULL DEFAULT x'';
                ALTER TABLE invites ADD COLUMN invite_seq INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE invites ADD COLUMN invite_hash BLOB NOT NULL DEFAULT x'';
                ALTER TABLE invites ADD COLUMN inviter_signing_key BLOB NOT NULL DEFAULT x'';
                ALTER TABLE invites ADD COLUMN inviter_agreement_key BLOB NOT NULL DEFAULT x'';
                ALTER TABLE epoch_keys ADD COLUMN log_seq INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE epoch_keys ADD COLUMN log_hash BLOB NOT NULL DEFAULT x'';
                INSERT INTO schema_version (version) VALUES (4);
                """);
        }

        if (current < 5) {
            // Identity signing keys that may never sign in or be registered again: replaced by registering again with
            // new keys, or retired by "Reset my identity". Keys replaced before this table existed aren't known.
            // (Version 6 makes them per account.)
            Execute(connection, tx, """
                CREATE TABLE retired_keys (
                    signing_key BLOB    PRIMARY KEY,
                    user_id     INTEGER NOT NULL,
                    retired_at  INTEGER NOT NULL
                );
                INSERT INTO schema_version (version) VALUES (5);
                """);
        }

        if (current < 6) {
            // A key is retired for the account that replaced or retired it, never for others: version 5 retired it for
            // everyone, so registering another user's public key and then replacing it shut that user out. Each row is
            // kept, for the account it was recorded for. And an index to find the account a signing key is registered to
            // (a key belongs to one at most; not a unique index, which a database where one was registered twice, before
            // registrations were signed, couldn't take).
            Execute(connection, tx, """
                CREATE TABLE retired_keys_per_user (
                    user_id     INTEGER NOT NULL,
                    signing_key BLOB    NOT NULL,
                    retired_at  INTEGER NOT NULL,
                    PRIMARY KEY (user_id, signing_key)
                );
                INSERT INTO retired_keys_per_user (user_id, signing_key, retired_at) SELECT user_id, signing_key, retired_at FROM retired_keys;
                DROP TABLE retired_keys;
                ALTER TABLE retired_keys_per_user RENAME TO retired_keys;
                CREATE INDEX IF NOT EXISTS users_by_signing_key ON users (signing_key);
                INSERT INTO schema_version (version) VALUES (6);
                """);
            traces = FindSharedKeys(connection, tx);
        }

        if (current < 7) {
            // "Remove from my list": a place in a channel (or an invite) under keys the user no longer has, which they
            // removed from their list. The row stays, as the log has it (rekeys go by it); the channel just isn't theirs
            // to see or use any more. (Checked, as tests of older schemas only remove the version rows.)
            if (!HasColumn(connection, tx, "members", "forgotten")) {
                Execute(connection, tx, "ALTER TABLE members ADD COLUMN forgotten INTEGER NOT NULL DEFAULT 0;");
            }

            if (!HasColumn(connection, tx, "invites", "forgotten")) {
                Execute(connection, tx, "ALTER TABLE invites ADD COLUMN forgotten INTEGER NOT NULL DEFAULT 0;");
            }

            Execute(connection, tx, "INSERT INTO schema_version (version) VALUES (7);");
        }

        if (current < 8) {
            // Key recovery: a place moved to the user's new keys holds no key for the channel until a rekey gives it one,
            // so it isn't asked to make one (see MemberRow.AwaitingKey). An invite's carries over when it is accepted.
            // Nothing else changes: places still under old keys are moved when their user next registers. (Checked, as
            // tests of older schemas only remove the version rows.)
            if (!HasColumn(connection, tx, "members", "awaiting_key")) {
                Execute(connection, tx, "ALTER TABLE members ADD COLUMN awaiting_key INTEGER NOT NULL DEFAULT 0;");
            }

            if (!HasColumn(connection, tx, "invites", "awaiting_key")) {
                Execute(connection, tx, "ALTER TABLE invites ADD COLUMN awaiting_key INTEGER NOT NULL DEFAULT 0;");
            }

            Execute(connection, tx, "INSERT INTO schema_version (version) VALUES (8);");
        }

        tx.Commit();
        this.ReportSharedKeys(traces);
    }

    /// <summary>
    /// What registrations from before they were signed may have left: a signing key registered to several accounts (anyone
    /// could register another user's public key as theirs), and a key retired for one account that another is registered
    /// with (schema 5 retired a key for everyone, under the account that replaced it, and kept one row per key, so a later
    /// retirement of the same key by another account was dropped).
    /// </summary>
    private static List<(byte[] Key, string Accounts, long? RetiredFor)> FindSharedKeys(SqliteConnection connection, SqliteTransaction tx) {
        var found = new List<(byte[], string, long?)>();
        using (var command = Command(connection, tx, """
                   SELECT signing_key, GROUP_CONCAT(user_id, ', ') FROM (SELECT signing_key, user_id FROM users ORDER BY user_id)
                   GROUP BY signing_key HAVING COUNT(*) > 1;
                   """)) {
            using var reader = command.ExecuteReader();
            while (reader.Read()) {
                found.Add(((byte[]) reader.GetValue(0), reader.GetString(1), null));
            }
        }

        using (var command = Command(connection, tx, """
                   SELECT retired_keys.signing_key, GROUP_CONCAT(users.user_id, ', '), retired_keys.user_id
                   FROM retired_keys JOIN users ON users.signing_key = retired_keys.signing_key AND users.user_id <> retired_keys.user_id
                   GROUP BY retired_keys.signing_key, retired_keys.user_id;
                   """)) {
            using var reader = command.ExecuteReader();
            while (reader.Read()) {
                found.Add(((byte[]) reader.GetValue(0), reader.GetString(1), reader.GetInt64(2)));
            }
        }

        return found;
    }

    /// <summary>
    /// Tells the operator about <see cref="FindSharedKeys"/>'s findings, by account and a short fingerprint of the key.
    /// They are left as they are: only the key's owner can use the key, and which account that is the server can't tell.
    /// </summary>
    private void ReportSharedKeys(List<(byte[] Key, string Accounts, long? RetiredFor)> traces) {
        foreach (var (key, accounts, retiredFor) in traces) {
            if (retiredFor == null) {
                this._logger?.LogWarning(
                    "Upgrading the database: identity key {Key} is registered to more than one account ({Accounts}). Before registrations were signed, " +
                    "anyone could register another user's public key as theirs, so all but one of these may not be its owner's. They were left as they " +
                    "are: none of them can register again with this key while another has it (the owner is told to reset their identity).",
                    ShortKeyId(key), accounts);
            } else {
                this._logger?.LogWarning(
                    "Upgrading the database: identity key {Key}, retired for account {RetiredFor}, is the current key of account {Accounts}. One of them " +
                    "registered the other's public key before registrations were signed. The retirement now counts only for the account it was recorded for.",
                    ShortKeyId(key), retiredFor, accounts);
            }
        }
    }

    private static bool HasColumn(SqliteConnection connection, SqliteTransaction tx, string table, string column) {
        return Scalar(connection, tx, "SELECT 1 FROM pragma_table_info($table) WHERE name = $column;", ("$table", table), ("$column", column)) != null;
    }

    /// <summary>A short, stable name for a signing key in the server's log: the start of its SHA-256 hash, not the key.</summary>
    internal static string ShortKeyId(byte[] signingKey) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(signingKey))[..16];

    private static void CreateVersion1(SqliteConnection connection, SqliteTransaction tx) {
        Execute(connection, tx, """
            CREATE TABLE users (
                user_id           INTEGER PRIMARY KEY,
                name              TEXT    NOT NULL,
                name_key          TEXT    NOT NULL,
                world_id          INTEGER NOT NULL,
                world_name        TEXT    NOT NULL,
                world_key         TEXT    NOT NULL,
                signing_key       BLOB    NOT NULL,
                agreement_key     BLOB    NOT NULL,
                binding_signature BLOB    NOT NULL,
                key_version       INTEGER NOT NULL,
                is_debug          INTEGER NOT NULL,
                created_at        INTEGER NOT NULL,
                updated_at        INTEGER NOT NULL
            );
            CREATE UNIQUE INDEX users_by_name ON users (name_key, world_key);

            CREATE TABLE devices (
                token_hash   BLOB    PRIMARY KEY,
                user_id      INTEGER NOT NULL REFERENCES users (user_id) ON DELETE CASCADE,
                created_at   INTEGER NOT NULL,
                last_used_at INTEGER NOT NULL
            );
            CREATE INDEX devices_by_user ON devices (user_id);

            CREATE TABLE channels (
                channel_id      TEXT    PRIMARY KEY,
                epoch           INTEGER NOT NULL,
                rekey_pending   INTEGER NOT NULL,
                name_epoch      INTEGER NOT NULL,
                name_author     INTEGER NOT NULL,
                name_ciphertext BLOB    NOT NULL,
                name_signature  BLOB    NOT NULL,
                created_at      INTEGER NOT NULL
            );

            CREATE TABLE members (
                channel_id TEXT    NOT NULL REFERENCES channels (channel_id) ON DELETE CASCADE,
                user_id    INTEGER NOT NULL REFERENCES users (user_id) ON DELETE CASCADE,
                rank       INTEGER NOT NULL,
                joined_at  INTEGER NOT NULL,
                PRIMARY KEY (channel_id, user_id)
            );
            CREATE INDEX members_by_user ON members (user_id);

            CREATE TABLE invites (
                channel_id        TEXT    NOT NULL REFERENCES channels (channel_id) ON DELETE CASCADE,
                user_id           INTEGER NOT NULL REFERENCES users (user_id) ON DELETE CASCADE,
                inviter_id        INTEGER NOT NULL REFERENCES users (user_id) ON DELETE CASCADE,
                sealed_ephemeral  BLOB    NOT NULL,
                sealed_ciphertext BLOB    NOT NULL,
                signature         BLOB    NOT NULL,
                created_at        INTEGER NOT NULL,
                PRIMARY KEY (channel_id, user_id)
            );
            CREATE INDEX invites_by_user ON invites (user_id);

            CREATE TABLE epoch_keys (
                channel_id   TEXT    NOT NULL REFERENCES channels (channel_id) ON DELETE CASCADE,
                epoch        INTEGER NOT NULL,
                recipient_id INTEGER NOT NULL,
                author_id    INTEGER NOT NULL,
                ephemeral    BLOB    NOT NULL,
                ciphertext   BLOB    NOT NULL,
                signature    BLOB    NOT NULL,
                PRIMARY KEY (channel_id, epoch, recipient_id)
            );
            CREATE INDEX epoch_keys_by_recipient ON epoch_keys (channel_id, recipient_id);

            INSERT INTO schema_version (version) VALUES (1);
            """);
    }

    // ================================================================ users and devices

    public UserRow? GetUser(long userId) {
        using var connection = this.Open();
        return QueryUsers(connection, null, "SELECT * FROM users WHERE user_id = $id;", ("$id", userId)).FirstOrDefault();
    }

    public UserRow? FindUser(string name, string worldName) {
        using var connection = this.Open();
        return QueryUsers(connection, null,
            "SELECT * FROM users WHERE name_key = $name AND world_key = $world;",
            ("$name", Key(name)), ("$world", Key(worldName))).FirstOrDefault();
    }

    public List<UserRow> GetUsers(IEnumerable<long> userIds) {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) {
            return [];
        }

        using var connection = this.Open();
        var parameters = ids.Select((id, i) => ($"$p{i}", (object) id)).ToArray();
        var sql = $"SELECT * FROM users WHERE user_id IN ({string.Join(", ", parameters.Select(p => p.Item1))});";
        return QueryUsers(connection, null, sql, parameters);
    }

    /// <summary>
    /// Creates or replaces a user's registration. Revokes all their devices. A signing key it replaces is retired for
    /// this user (see <see cref="IsKeyRetired"/>), and a key retired for them is never registered for them again. A key
    /// another user is registered with isn't registered for this one.
    ///
    /// With <paramref name="recovery"/> (the new keys' consent), every place of the user's in a channel (a membership,
    /// rank kept, or an invite) under other keys moves to the keys registered, in the same transaction: a key recovered
    /// entry for each, checked with the log's rules first, appended to the channel's log. Places the user removed from
    /// their list stay as they are. Without it (or for a place whose entry the rules refuse, which is logged), places stay
    /// under the keys they have, as before recovery existed.
    /// </summary>
    /// <returns>The stored user, whether their identity keys changed, and the places moved.</returns>
    /// <exception cref="KeyRetiredException">The signing key is retired for this user. Nothing was changed.</exception>
    /// <exception cref="KeyInUseException">Another user is registered with the signing key. Nothing was changed.</exception>
    public Registration RegisterUser(long userId, string name, uint worldId, string worldName, IdentityBundle identity, bool isDebug, KeyRecovery? recovery = null) {
        using var connection = this.Open();
        using var tx = connection.BeginTransaction();
        var now = Now();
        var existing = QueryUsers(connection, tx, "SELECT * FROM users WHERE user_id = $id;", ("$id", userId)).FirstOrDefault();

        var signing = identity.SigningPublicKey.ToByteArray();
        var agreement = identity.AgreementPublicKey.ToByteArray();
        // In the transaction that registers it, so a key retired, or registered by another user, after the registration
        // started still can't be registered.
        if (IsKeyRetired(connection, tx, userId, signing)) {
            throw new KeyRetiredException();
        }

        if (IsKeyInUseByAnother(connection, tx, userId, signing)) {
            throw new KeyInUseException();
        }

        var keysChanged = existing != null
                          && (!existing.SigningKey.AsSpan().SequenceEqual(signing) || !existing.AgreementKey.AsSpan().SequenceEqual(agreement));
        var keyVersion = existing == null ? 1u : keysChanged ? existing.KeyVersion + 1 : existing.KeyVersion;
        if (existing != null && !existing.SigningKey.AsSpan().SequenceEqual(signing)) {
            // Replaced: a copy of the old identity left anywhere must not take the account back by registering it again.
            Execute(connection, tx, "INSERT OR IGNORE INTO retired_keys (user_id, signing_key, retired_at) VALUES ($id, $key, $now);",
                ("$key", existing.SigningKey), ("$id", userId), ("$now", now));
        }

        // Another character may previously have held this name on this world (renames). Free the
        // name without deleting that account, which would silently cascade away its memberships,
        // and mark its display name so two members never show as the same Name@World.
        Execute(connection, tx, """
            UPDATE users SET name = name || ' (former)',
                name_key = name_key || '#stale-' || user_id, world_key = world_key || '#stale-' || user_id
            WHERE name_key = $name AND world_key = $world AND user_id <> $id;
            """,
            ("$name", Key(name)), ("$world", Key(worldName)), ("$id", userId));

        Execute(connection, tx, """
            INSERT INTO users (user_id, name, name_key, world_id, world_name, world_key, signing_key, agreement_key,
                               binding_signature, key_version, is_debug, created_at, updated_at)
            VALUES ($id, $name, $nameKey, $worldId, $world, $worldKey, $signing, $agreement, $binding, $version, $debug, $now, $now)
            ON CONFLICT (user_id) DO UPDATE SET
                name = excluded.name, name_key = excluded.name_key,
                world_id = excluded.world_id, world_name = excluded.world_name, world_key = excluded.world_key,
                signing_key = excluded.signing_key, agreement_key = excluded.agreement_key,
                binding_signature = excluded.binding_signature, key_version = excluded.key_version,
                is_debug = excluded.is_debug, updated_at = excluded.updated_at;
            """,
            ("$id", userId), ("$name", name), ("$nameKey", Key(name)), ("$worldId", (long) worldId),
            ("$world", worldName), ("$worldKey", Key(worldName)), ("$signing", signing), ("$agreement", agreement),
            ("$binding", identity.BindingSignature.ToByteArray()), ("$version", (long) keyVersion),
            ("$debug", isDebug ? 1 : 0), ("$now", now));

        Execute(connection, tx, "DELETE FROM devices WHERE user_id = $id;", ("$id", userId));

        if (keysChanged) {
            // The new keys can't open anything sealed to the old ones; a rekey gives them the channels' keys.
            Execute(connection, tx, "DELETE FROM epoch_keys WHERE recipient_id = $id;", ("$id", userId));
        }

        var recovered = recovery == null ? [] : this.RecoverPlaces(connection, tx, userId, new MemberKeys(signing, agreement), recovery);
        tx.Commit();
        var stored = QueryUsers(connection, null, "SELECT * FROM users WHERE user_id = $id;", ("$id", userId)).Single();
        return new Registration(stored, keysChanged) { Recovered = recovered };
    }

    /// <summary>
    /// Whether registering <paramref name="keys"/> for the user would move any place of theirs (see <see cref="RegisterUser"/>):
    /// one in a channel or an invite, not removed from their list, under other keys.
    /// </summary>
    public bool HasPlacesToRecover(long userId, MemberKeys keys) {
        using var connection = this.Open();
        return PlacesToRecover(connection, null, userId, keys).Count > 0;
    }

    private static List<string> PlacesToRecover(SqliteConnection connection, SqliteTransaction? tx, long userId, MemberKeys keys) {
        return Query(connection, tx, """
            SELECT channel_id FROM members WHERE user_id = $id AND forgotten = 0 AND NOT (signing_key = $signing AND agreement_key = $agreement)
            UNION SELECT channel_id FROM invites WHERE user_id = $id AND forgotten = 0 AND NOT (signing_key = $signing AND agreement_key = $agreement)
            ORDER BY channel_id;
            """,
            reader => reader.GetString(0), ("$id", userId), ("$signing", keys.SigningKeyArray()), ("$agreement", keys.AgreementKeyArray()));
    }

    /// <summary>Moves the user's places to <paramref name="keys"/>, in the registration's transaction (see <see cref="RegisterUser"/>).</summary>
    private List<RecoveredPlace> RecoverPlaces(SqliteConnection connection, SqliteTransaction tx, long userId, MemberKeys keys, KeyRecovery recovery) {
        var recovered = new List<RecoveredPlace>();
        foreach (var channelId in PlacesToRecover(connection, tx, userId, keys)) {
            var state = recovery.Membership.Restore(ReadCheckpoint(connection, tx, channelId)!);
            MembershipEntry entry;
            try {
                entry = state.CreateKeyRecovered(userId, keys, recovery.Proof, recovery.TimestampMs);
            } catch (MembershipException ex) {
                // Say, the keys are someone else's place there already. Left under the keys it has.
                this._logger?.LogWarning("Couldn't move the place of {User} in channel {Channel} to the keys they registered: {Reason}", userId, channelId, ex.Message);
                continue;
            }

            ApplyEntry(connection, tx, channelId, entry, null, null);
            recovered.Add(new RecoveredPlace(channelId, entry, state.FindMember(userId) != null));
        }

        return recovered;
    }

    /// <summary>
    /// Devices one user keeps. Every key login adds one, so a client whose logins keep getting lost, or anyone holding
    /// the key, would otherwise add rows forever. Adding one beyond this drops the least recently used.
    /// </summary>
    public const int MaxDevicesPerUser = 20;

    /// <summary>
    /// Adds a device whatever the user's keys, for tests of how devices are kept. The server only adds them with
    /// <see cref="AddDeviceForKey"/>, so none outlives a registration or a retirement that lands meanwhile.
    /// </summary>
    internal void AddDevice(long userId, byte[] tokenHash) {
        using var connection = this.Open();
        using var tx = connection.BeginTransaction();
        var now = Now();
        Execute(connection, tx, "INSERT INTO devices (token_hash, user_id, created_at, last_used_at) VALUES ($hash, $id, $now, $now);",
            ("$hash", tokenHash), ("$id", userId), ("$now", now));
        PruneDevices(connection, tx, userId);
        tx.Commit();
    }

    /// <summary>
    /// Adds a device for a key login (or a registration), but only while the user is still registered with the key that
    /// signed it (or was registered), and that key isn't retired for them. In one statement, so a registration with new
    /// keys or a retirement (each of which revokes every device) can't land between the check and the insert and leave
    /// the old key with a working login.
    /// </summary>
    /// <returns>False if the user's keys changed or were retired (or they're gone) since <paramref name="signingKey"/> was checked.</returns>
    public bool AddDeviceForKey(long userId, byte[] signingKey, uint keyVersion, byte[] tokenHash) {
        using var connection = this.Open();
        using var tx = connection.BeginTransaction();
        var now = Now();
        var added = Execute(connection, tx, """
            INSERT INTO devices (token_hash, user_id, created_at, last_used_at)
            SELECT $hash, user_id, $now, $now FROM users
            WHERE user_id = $id AND signing_key = $key AND key_version = $version
              AND NOT EXISTS (SELECT 1 FROM retired_keys WHERE retired_keys.user_id = users.user_id AND retired_keys.signing_key = users.signing_key);
            """,
            ("$hash", tokenHash), ("$id", userId), ("$key", signingKey), ("$version", (long) keyVersion), ("$now", now)) == 1;
        if (added) {
            PruneDevices(connection, tx, userId);
        }

        tx.Commit();
        return added;
    }

    /// <summary>
    /// "Reset my identity": retires the user's current signing key, while it still is that (and of that version), and
    /// revokes every device of theirs, in one transaction. The key then can't sign in (see <see cref="AddDeviceForKey"/>)
    /// or be registered again (see <see cref="RegisterUser"/>), so the account has no working login until new keys are
    /// registered. Its row, and the keys others see, stay as they are until then.
    /// </summary>
    /// <returns>False if the user's keys changed (or they're gone) since <paramref name="signingKey"/> was checked; nothing changed then.</returns>
    public bool RetireIdentity(long userId, byte[] signingKey, uint keyVersion) {
        using var connection = this.Open();
        using var tx = connection.BeginTransaction();
        var current = Convert.ToInt64(Scalar(connection, tx, "SELECT COUNT(*) FROM users WHERE user_id = $id AND signing_key = $key AND key_version = $version;",
            ("$id", userId), ("$key", signingKey), ("$version", (long) keyVersion))) == 1;
        if (!current) {
            return false;
        }

        Execute(connection, tx, "INSERT OR IGNORE INTO retired_keys (user_id, signing_key, retired_at) VALUES ($id, $key, $now);",
            ("$key", signingKey), ("$id", userId), ("$now", Now()));
        Execute(connection, tx, "DELETE FROM devices WHERE user_id = $id;", ("$id", userId));
        tx.Commit();
        return true;
    }

    /// <summary>
    /// Whether a user replaced or retired a signing key: it may never sign in to their account or be registered for it
    /// again. Only for that user: whatever one account does with a key never shuts another out.
    /// </summary>
    public bool IsKeyRetired(long userId, byte[] signingKey) {
        using var connection = this.Open();
        return IsKeyRetired(connection, null, userId, signingKey);
    }

    private static bool IsKeyRetired(SqliteConnection connection, SqliteTransaction? tx, long userId, byte[] signingKey) {
        return Scalar(connection, tx, "SELECT 1 FROM retired_keys WHERE user_id = $id AND signing_key = $key;", ("$id", userId), ("$key", signingKey)) != null;
    }

    /// <summary>Whether a user other than <paramref name="userId"/> is registered with a signing key (a key belongs to one at most).</summary>
    public bool IsKeyInUseByAnother(long userId, byte[] signingKey) {
        using var connection = this.Open();
        return IsKeyInUseByAnother(connection, null, userId, signingKey);
    }

    private static bool IsKeyInUseByAnother(SqliteConnection connection, SqliteTransaction? tx, long userId, byte[] signingKey) {
        return Scalar(connection, tx, "SELECT 1 FROM users WHERE signing_key = $key AND user_id <> $id;", ("$id", userId), ("$key", signingKey)) != null;
    }

    /// <summary>
    /// Keeps a user's <see cref="MaxDevicesPerUser"/> most recently used devices (the newest first among equals, so
    /// the one just added always stays), in the transaction that added one.
    /// </summary>
    private static void PruneDevices(SqliteConnection connection, SqliteTransaction tx, long userId) {
        Execute(connection, tx, """
            DELETE FROM devices WHERE user_id = $id AND rowid NOT IN (
                SELECT rowid FROM devices WHERE user_id = $id
                ORDER BY last_used_at DESC, created_at DESC, rowid DESC
                LIMIT $keep);
            """,
            ("$id", userId), ("$keep", MaxDevicesPerUser));
    }

    public int CountDevices(long userId) {
        using var connection = this.Open();
        return Convert.ToInt32(Scalar(connection, null, "SELECT COUNT(*) FROM devices WHERE user_id = $id;", ("$id", userId)));
    }

    /// <summary>Backdates a device's last use, for tests of which devices are kept.</summary>
    internal void SetDeviceLastUsedForTests(byte[] tokenHash, DateTimeOffset when) {
        using var connection = this.Open();
        Execute(connection, null, "UPDATE devices SET last_used_at = $when WHERE token_hash = $hash;",
            ("$when", when.ToUnixTimeSeconds()), ("$hash", tokenHash));
    }

    public long? FindDevice(byte[] tokenHash) {
        using var connection = this.Open();
        var result = Scalar(connection, null, "SELECT user_id FROM devices WHERE token_hash = $hash;", ("$hash", tokenHash));
        if (result == null) {
            return null;
        }

        Execute(connection, null, "UPDATE devices SET last_used_at = $now WHERE token_hash = $hash;", ("$now", Now()), ("$hash", tokenHash));
        return Convert.ToInt64(result);
    }

    // ================================================================ channels

    public ChannelRow? GetChannel(string channelId) {
        using var connection = this.Open();
        return GetChannel(connection, null, channelId);
    }

    /// <summary>The channels the user is a member of, but for those they removed from their list.</summary>
    /// <param name="limit">At most this many, oldest memberships first.</param>
    public List<ChannelRow> GetChannelsForUser(long userId, int limit = int.MaxValue) {
        using var connection = this.Open();
        using var command = Command(connection, null,
            "SELECT c.* FROM channels c JOIN members m ON m.channel_id = c.channel_id WHERE m.user_id = $id AND m.forgotten = 0 ORDER BY m.joined_at, c.channel_id LIMIT $limit;",
            ("$id", userId), ("$limit", limit));
        using var reader = command.ExecuteReader();
        var channels = new List<ChannelRow>();
        while (reader.Read()) {
            channels.Add(ReadChannel(reader));
        }

        return channels;
    }

    public int CountChannelsForUser(long userId) {
        using var connection = this.Open();
        return Convert.ToInt32(Scalar(connection, null, "SELECT COUNT(*) FROM members WHERE user_id = $id AND forgotten = 0;", ("$id", userId)));
    }

    /// <returns>
    /// The user's rank: a member rank, <see cref="Rank.Invited"/> for a pending invite, or null (also for a place they removed
    /// from their list: see <see cref="ForgetStaleMembership"/>).
    /// </returns>
    public Rank? GetRank(string channelId, long userId) => this.GetPlace(channelId, userId)?.Rank;

    /// <returns>
    /// The user's place in the channel: its rank, as <see cref="GetRank"/> gives it, and whether it is under the keys the user
    /// is registered with now (<see cref="MemberRow.CurrentKeys"/>; for an invite, the keys it was made for), and under
    /// <paramref name="keys"/> too if given. Null as for <see cref="GetRank"/>.
    /// </returns>
    /// <param name="keys">
    /// The keys whoever asks signed in with: a session of keys the user no longer has (one a registration with new keys
    /// should have disconnected) has no say through a place that moved to the new ones.
    /// </param>
    public (Rank Rank, bool CurrentKeys)? GetPlace(string channelId, long userId, MemberKeys? keys = null) {
        using var connection = this.Open();
        (string, object)[] who = [
            ("$channel", channelId), ("$user", userId),
            ("$signing", keys?.SigningKeyArray() ?? (object) DBNull.Value), ("$agreement", keys?.AgreementKeyArray() ?? (object) DBNull.Value),
        ];
        var member = Query(connection, null,
            $"SELECT m.rank, {SameKeys("m")} AND {SessionKeys("m")} FROM members m JOIN users u ON u.user_id = m.user_id " +
            "WHERE m.channel_id = $channel AND m.user_id = $user AND m.forgotten = 0;",
            reader => ((Rank) reader.GetInt32(0), reader.GetInt64(1) != 0), who);
        if (member is [var place]) {
            return place;
        }

        var invite = Query(connection, null,
            $"SELECT {SameKeys("i")} AND {SessionKeys("i")} FROM invites i JOIN users u ON u.user_id = i.user_id " +
            "WHERE i.channel_id = $channel AND i.user_id = $user AND i.forgotten = 0;",
            reader => reader.GetInt64(0) != 0, who);
        return invite is [var current] ? (Rank.Invited, current) : null;
    }

    /// <summary>The row's keys are the asking session's (<c>$signing</c>, <c>$agreement</c>), or no session's were given (null).</summary>
    private static string SessionKeys(string row) =>
        $"($signing IS NULL OR ({row}.signing_key = $signing AND {row}.agreement_key = $agreement))";

    /// <summary>
    /// Every member row of the channel, as the log has it: places the user removed from their list (<see cref="MemberRow.Forgotten"/>)
    /// included, as they are still members for the log (and so for rekeys). Who to tell about the channel leaves those out.
    /// </summary>
    public List<MemberRow> GetMembers(string channelId) {
        using var connection = this.Open();
        return GetMembers(connection, null, channelId);
    }

    /// <summary>Every invitee of the channel, as the log has them: invites removed from the invitee's list (<see cref="MemberRow.Forgotten"/>) included.</summary>
    public List<MemberRow> GetInvitees(string channelId) {
        using var connection = this.Open();
        return this.QueryInvites(connection, "WHERE i.channel_id = $id", ("$id", channelId))
            .Select(invite => new MemberRow(invite.Invitee, Rank.Invited, invite.Forgotten))
            .ToList();
    }

    /// <summary>Creates a channel from its genesis entry, which makes the creator admin.</summary>
    public void CreateChannel(string channelId, MembershipEntry genesis, SealedEpochKey creatorKey, EncryptedName name) {
        using var connection = this.Open();
        using var tx = connection.BeginTransaction();
        var now = Now();
        var hash = MembershipEntries.Hash(genesis);
        Execute(connection, tx, """
            INSERT INTO channels (channel_id, epoch, rekey_pending, name_epoch, name_revision, name_author, name_ciphertext, name_signature,
                                  name_log_seq, name_log_hash, log_seq, log_hash, created_at)
            VALUES ($id, 0, 0, $nameEpoch, $nameRevision, $nameAuthor, $nameCiphertext, $nameSignature, $nameLogSeq, $nameLogHash, 0, $hash, $now);
            """,
            ("$id", channelId), ("$nameEpoch", (long) name.Epoch), ("$nameRevision", (long) name.Revision), ("$nameAuthor", name.AuthorId),
            ("$nameCiphertext", name.Ciphertext.ToByteArray()), ("$nameSignature", name.Signature.ToByteArray()),
            ("$nameLogSeq", (long) (name.LogPosition?.Seq ?? 0)), ("$nameLogHash", name.LogPosition?.Hash.ToByteArray() ?? []),
            ("$hash", hash), ("$now", now));
        Execute(connection, tx, "INSERT INTO membership_log (channel_id, seq, hash, entry) VALUES ($id, 0, $hash, $entry);",
            ("$id", channelId), ("$hash", hash), ("$entry", genesis.ToByteArray()));
        Execute(connection, tx, """
            INSERT INTO members (channel_id, user_id, rank, joined_at, signing_key, agreement_key)
            VALUES ($id, $user, $rank, $now, $signing, $agreement);
            """,
            ("$id", channelId), ("$user", genesis.Subject.UserId), ("$rank", (long) Rank.Admin), ("$now", now),
            ("$signing", genesis.Subject.SigningPublicKey.ToByteArray()), ("$agreement", genesis.Subject.AgreementPublicKey.ToByteArray()));
        InsertEpochKey(connection, tx, channelId, 0, genesis.Subject.UserId, creatorKey);
        tx.Commit();
    }

    /// <summary>
    /// "Remove from my list": marks the user's member and invite rows for a channel forgotten, only if every one of them
    /// (not forgotten already) is under keys other than the user's current ones, all in one transaction (so a registration
    /// or an invite landing meanwhile is seen). The rows and the log stay: they are the log's state, which entries are
    /// checked against and rekeys are made for (see <see cref="ApplyRekey"/>). A forgotten row only stops counting for the
    /// user: the channel isn't listed to them, counted towards their limits, or theirs to act in (see <see cref="GetRank"/>),
    /// and they aren't sent its events. It goes when the log removes the place (a removal, a cancelled invite).
    /// </summary>
    public ForgetResult ForgetStaleMembership(string channelId, long userId) {
        using var connection = this.Open();
        using var tx = connection.BeginTransaction();
        var user = QueryUsers(connection, tx, "SELECT * FROM users WHERE user_id = $id;", ("$id", userId)).FirstOrDefault();
        if (user == null) {
            return ForgetResult.NotListed;
        }

        (string, object)[] who = [("$channel", channelId), ("$user", userId)];
        var places = Query(connection, tx, "SELECT signing_key, agreement_key FROM members WHERE channel_id = $channel AND user_id = $user AND forgotten = 0;",
                reader => new MemberKeys((byte[]) reader[0], (byte[]) reader[1]), who)
            .Concat(Query(connection, tx, "SELECT signing_key, agreement_key FROM invites WHERE channel_id = $channel AND user_id = $user AND forgotten = 0;",
                reader => new MemberKeys((byte[]) reader[0], (byte[]) reader[1]), who))
            .ToList();
        if (places.Count == 0) {
            return ForgetResult.NotListed;
        }

        if (places.Any(keys => keys == user.Keys)) {
            return ForgetResult.Current;
        }

        Execute(connection, tx, "UPDATE members SET forgotten = 1 WHERE channel_id = $channel AND user_id = $user;", who);
        Execute(connection, tx, "UPDATE invites SET forgotten = 1 WHERE channel_id = $channel AND user_id = $user;", who);
        tx.Commit();
        return ForgetResult.Forgotten;
    }

    public void DeleteChannel(string channelId) {
        using var connection = this.Open();
        Execute(connection, null, "DELETE FROM channels WHERE channel_id = $id;", ("$id", channelId));
    }

    /// <summary>
    /// Deletes a channel its last member is leaving, only if its log is still at <paramref name="head"/> (where
    /// their leave was checked) and they are still its only member, all in one transaction. Otherwise someone
    /// joined (or something else changed) meanwhile, and the channel stays. Places removed from their owners' lists (see
    /// <see cref="ForgetStaleMembership"/>) don't count: they go with the channel.
    /// </summary>
    /// <returns>Who was invited to it (but for invites removed from the invitee's list), to tell; or null (and nothing changes) if it changed meanwhile.</returns>
    public List<long>? DeleteAbandonedChannel(string channelId, LogPosition head, long lastMemberId) {
        using var connection = this.Open();
        using var tx = connection.BeginTransaction();
        var channel = GetChannel(connection, tx, channelId);
        if (channel == null || !MembershipEntries.SamePosition(channel.LogHead, head)) {
            return null;
        }

        // Places their owners removed from their lists don't keep a channel: nobody would ever see it again.
        var members = Query(connection, tx, "SELECT user_id FROM members WHERE channel_id = $id AND forgotten = 0;", reader => reader.GetInt64(0), ("$id", channelId));
        if (members is not [var only] || only != lastMemberId) {
            return null;
        }

        var invitees = Query(connection, tx, "SELECT user_id FROM invites WHERE channel_id = $id AND forgotten = 0;", reader => reader.GetInt64(0), ("$id", channelId));
        Execute(connection, tx, "DELETE FROM channels WHERE channel_id = $id;", ("$id", channelId));
        tx.Commit();
        return invitees;
    }

    /// <summary>
    /// Renames only if the channel is still at the name's epoch and log position, not
    /// awaiting a rekey, and the name's revision is newer than the stored one.
    /// </summary>
    /// <returns>False if the channel changed meanwhile, or the revision isn't newer.</returns>
    public bool RenameChannel(string channelId, EncryptedName name) {
        using var connection = this.Open();
        return Execute(connection, null, """
            UPDATE channels SET name_epoch = $epoch, name_revision = $revision, name_author = $author,
                name_ciphertext = $ciphertext, name_signature = $signature,
                name_source_epoch = NULL, name_source_revision = NULL,
                name_log_seq = $logSeq, name_log_hash = $logHash
            WHERE channel_id = $id AND epoch = $epoch AND rekey_pending = 0
                AND log_seq = $logSeq AND log_hash = $logHash
                AND (name_epoch < $epoch OR name_revision < $revision);
            """,
            ("$id", channelId), ("$epoch", (long) name.Epoch), ("$revision", (long) name.Revision), ("$author", name.AuthorId),
            ("$ciphertext", name.Ciphertext.ToByteArray()), ("$signature", name.Signature.ToByteArray()),
            ("$logSeq", (long) (name.LogPosition?.Seq ?? 0)), ("$logHash", name.LogPosition?.Hash.ToByteArray() ?? [])) == 1;
    }

    // ================================================================ the membership log

    /// <summary>
    /// The channel's membership as its tables stand (members and invitees with the keys they were
    /// admitted with, at the log's head), for checking the next entry. The tables only ever change
    /// with an entry (see <see cref="AppendEntry"/>), so they agree with replaying the log.
    /// </summary>
    public MembershipCheckpoint? GetMembershipCheckpoint(string channelId) {
        using var connection = this.Open();
        return ReadCheckpoint(connection, null, channelId);
    }

    private static MembershipCheckpoint? ReadCheckpoint(SqliteConnection connection, SqliteTransaction? tx, string channelId) {
        var channel = GetChannel(connection, tx, channelId);
        if (channel == null) {
            return null;
        }

        var checkpoint = new MembershipCheckpoint {
            ChannelId = channelId,
            Seq = channel.LogHead.Seq,
            Hash = channel.LogHead.Hash.ToByteArray(),
            MembersChangedAt = channel.LogHead.Seq,
            RecentHashes = [channel.LogHead.Hash.ToByteArray()],
            RecentFrom = channel.LogHead.Seq,
        };

        using (var command = Command(connection, tx, "SELECT user_id, rank, signing_key, agreement_key FROM members WHERE channel_id = $id;", ("$id", channelId))) {
            using var reader = command.ExecuteReader();
            while (reader.Read()) {
                checkpoint.Members.Add(new CheckpointMember {
                    UserId = reader.GetInt64(0),
                    Rank = (Rank) reader.GetInt32(1),
                    SigningPublicKey = (byte[]) reader[2],
                    AgreementPublicKey = (byte[]) reader[3],
                });
            }
        }

        using (var command = Command(connection, tx, """
                   SELECT user_id, signing_key, agreement_key, invite_seq, invite_hash, inviter_id, inviter_signing_key, inviter_agreement_key
                   FROM invites WHERE channel_id = $id;
                   """, ("$id", channelId))) {
            using var reader = command.ExecuteReader();
            while (reader.Read()) {
                checkpoint.Invitees.Add(new CheckpointInvitee {
                    UserId = reader.GetInt64(0),
                    SigningPublicKey = (byte[]) reader[1],
                    AgreementPublicKey = (byte[]) reader[2],
                    InviteSeq = (ulong) reader.GetInt64(3),
                    InviteHash = (byte[]) reader[4],
                    InviterId = reader.GetInt64(5),
                    InviterSigningPublicKey = (byte[]) reader[6],
                    InviterAgreementPublicKey = (byte[]) reader[7],
                });
            }
        }

        return checkpoint;
    }

    /// <param name="limit">At most this many, oldest first.</param>
    public List<MembershipEntry> GetLogEntries(string channelId, ulong fromSeq, int limit) {
        using var connection = this.Open();
        using var command = Command(connection, null, "SELECT entry FROM membership_log WHERE channel_id = $id AND seq >= $from ORDER BY seq LIMIT $limit;",
            ("$id", channelId), ("$from", (long) Math.Min(fromSeq, long.MaxValue)), ("$limit", limit));
        using var reader = command.ExecuteReader();
        var entries = new List<MembershipEntry>();
        while (reader.Read()) {
            entries.Add(MembershipEntry.Parser.ParseFrom((byte[]) reader[0]));
        }

        return entries;
    }

    /// <summary>
    /// Appends an entry the caller has checked against <see cref="GetMembershipCheckpoint"/>, and
    /// changes the members and invites as it says, in one transaction. For an invite, also stores
    /// the channel name sealed to the invitee.
    /// </summary>
    /// <returns>False (and nothing changes) if the log has moved on since: the entry isn't next any more.</returns>
    public bool AppendEntry(string channelId, MembershipEntry entry, SealedBox? sealedName = null, byte[]? inviteSignature = null) {
        using var connection = this.Open();
        using var tx = connection.BeginTransaction();
        if (!ApplyEntry(connection, tx, channelId, entry, sealedName, inviteSignature)) {
            return false;
        }

        tx.Commit();
        return true;
    }

    /// <summary><see cref="AppendEntry"/>, in a transaction the caller commits.</summary>
    private static bool ApplyEntry(SqliteConnection connection, SqliteTransaction tx, string channelId, MembershipEntry entry, SealedBox? sealedName, byte[]? inviteSignature) {
        var channel = GetChannel(connection, tx, channelId);
        if (channel == null || entry.Seq != channel.LogHead.Seq + 1 || !entry.PreviousHash.Span.SequenceEqual(channel.LogHead.Hash.Span)) {
            return false;
        }

        var hash = MembershipEntries.Hash(entry);
        Execute(connection, tx, "INSERT INTO membership_log (channel_id, seq, hash, entry) VALUES ($id, $seq, $hash, $entry);",
            ("$id", channelId), ("$seq", (long) entry.Seq), ("$hash", hash), ("$entry", entry.ToByteArray()));

        var subject = entry.Subject;
        (string, object)[] who = [("$channel", channelId), ("$user", subject.UserId)];
        switch (entry.Kind) {
            case MembershipEntryKind.Invite: {
                // Checked entries always have one; tests playing a server that skips the checks may not.
                var inviter = Query(connection, tx, "SELECT signing_key, agreement_key FROM members WHERE channel_id = $channel AND user_id = $user;",
                    reader => ((byte[]) reader[0], (byte[]) reader[1]), ("$channel", channelId), ("$user", entry.ActorId)).FirstOrDefault(([], []));
                Execute(connection, tx, """
                    INSERT INTO invites (channel_id, user_id, inviter_id, sealed_ephemeral, sealed_ciphertext, signature, created_at,
                                         signing_key, agreement_key, invite_seq, invite_hash, inviter_signing_key, inviter_agreement_key)
                    VALUES ($channel, $user, $inviter, $ephemeral, $ciphertext, $signature, $now,
                            $signing, $agreement, $seq, $hash, $inviterSigning, $inviterAgreement)
                    ON CONFLICT (channel_id, user_id) DO UPDATE SET
                        inviter_id = excluded.inviter_id, sealed_ephemeral = excluded.sealed_ephemeral,
                        sealed_ciphertext = excluded.sealed_ciphertext, signature = excluded.signature, created_at = excluded.created_at,
                        signing_key = excluded.signing_key, agreement_key = excluded.agreement_key,
                        invite_seq = excluded.invite_seq, invite_hash = excluded.invite_hash,
                        inviter_signing_key = excluded.inviter_signing_key, inviter_agreement_key = excluded.inviter_agreement_key,
                        forgotten = 0, awaiting_key = 0;
                    """,
                    ("$channel", channelId), ("$user", subject.UserId), ("$inviter", entry.ActorId),
                    ("$ephemeral", sealedName?.EphemeralPublicKey.ToByteArray() ?? []), ("$ciphertext", sealedName?.Ciphertext.ToByteArray() ?? []),
                    ("$signature", inviteSignature ?? []), ("$now", Now()),
                    ("$signing", subject.SigningPublicKey.ToByteArray()), ("$agreement", subject.AgreementPublicKey.ToByteArray()),
                    ("$seq", (long) entry.Seq), ("$hash", hash), ("$inviterSigning", inviter.Item1), ("$inviterAgreement", inviter.Item2));
                break;
            }
            case MembershipEntryKind.Accept:
                // An invite moved to new keys knows no name for the channel (it was sealed to the old ones), so its joiner
                // can't rekey either (see MemberRow.AwaitingKey).
                Execute(connection, tx, """
                    INSERT OR REPLACE INTO members (channel_id, user_id, rank, joined_at, signing_key, agreement_key, awaiting_key)
                    VALUES ($channel, $user, $rank, $now, $signing, $agreement,
                            COALESCE((SELECT awaiting_key FROM invites WHERE channel_id = $channel AND user_id = $user), 0));
                    """,
                    ("$channel", channelId), ("$user", subject.UserId), ("$rank", (long) Rank.Member), ("$now", Now()),
                    ("$signing", subject.SigningPublicKey.ToByteArray()), ("$agreement", subject.AgreementPublicKey.ToByteArray()));
                Execute(connection, tx, "DELETE FROM invites WHERE channel_id = $channel AND user_id = $user;", who);
                Execute(connection, tx, "UPDATE channels SET rekey_pending = 1 WHERE channel_id = $channel;", ("$channel", channelId));
                break;
            case MembershipEntryKind.KeyRecovered: {
                (string, object)[] keys = [
                    ("$channel", channelId), ("$user", subject.UserId),
                    ("$signing", entry.NewKeys.SigningPublicKey.ToByteArray()), ("$agreement", entry.NewKeys.AgreementPublicKey.ToByteArray()),
                ];
                // Rank, joined time, invite and whether it is forgotten stay: only the keys change.
                if (Execute(connection, tx, """
                        UPDATE members SET signing_key = $signing, agreement_key = $agreement, awaiting_key = 1
                        WHERE channel_id = $channel AND user_id = $user;
                        """, keys) > 0) {
                    // Nothing sealed to the old keys is any use to the new ones, and the old ones mustn't read what comes next.
                    Execute(connection, tx, "DELETE FROM epoch_keys WHERE channel_id = $channel AND recipient_id = $user;", who);
                    Execute(connection, tx, "UPDATE channels SET rekey_pending = 1 WHERE channel_id = $channel;", ("$channel", channelId));
                } else {
                    Execute(connection, tx, """
                        UPDATE invites SET signing_key = $signing, agreement_key = $agreement, awaiting_key = 1
                        WHERE channel_id = $channel AND user_id = $user;
                        """, keys);
                }

                break;
            }
            case MembershipEntryKind.Decline:
            case MembershipEntryKind.CancelInvite:
                Execute(connection, tx, "DELETE FROM invites WHERE channel_id = $channel AND user_id = $user;", who);
                break;
            case MembershipEntryKind.Remove:
            case MembershipEntryKind.Leave:
                Execute(connection, tx, "DELETE FROM members WHERE channel_id = $channel AND user_id = $user;", who);
                Execute(connection, tx, "DELETE FROM epoch_keys WHERE channel_id = $channel AND recipient_id = $user;", who);
                // Their invites die with their membership, as the log's rules say.
                Execute(connection, tx, "DELETE FROM invites WHERE channel_id = $channel AND inviter_id = $user;", who);
                Execute(connection, tx, "UPDATE channels SET rekey_pending = 1 WHERE channel_id = $channel;", ("$channel", channelId));
                break;
            case MembershipEntryKind.SetRank:
                Execute(connection, tx, "UPDATE members SET rank = $rank WHERE channel_id = $channel AND user_id = $user;",
                    ("$rank", (long) entry.Rank), ("$channel", channelId), ("$user", subject.UserId));
                if (entry.Rank < Rank.Moderator) {
                    Execute(connection, tx, "DELETE FROM invites WHERE channel_id = $channel AND inviter_id = $user;", who);
                }

                break;
            case MembershipEntryKind.TransferAdmin:
                Execute(connection, tx, "UPDATE members SET rank = $rank WHERE channel_id = $channel AND user_id = $user;",
                    ("$rank", (long) Rank.Admin), ("$channel", channelId), ("$user", subject.UserId));
                Execute(connection, tx, "UPDATE members SET rank = $rank WHERE channel_id = $channel AND user_id = $user;",
                    ("$rank", (long) Rank.Moderator), ("$channel", channelId), ("$user", entry.ActorId));
                break;
            default:
                throw new ArgumentException($"A {entry.Kind} entry can't be appended.", nameof(entry));
        }

        Execute(connection, tx, "UPDATE channels SET log_seq = $seq, log_hash = $hash WHERE channel_id = $channel;",
            ("$seq", (long) entry.Seq), ("$hash", hash), ("$channel", channelId));
        return true;
    }

    // ================================================================ invites and membership

    public int CountPendingInvites(string channelId) {
        using var connection = this.Open();
        return Convert.ToInt32(Scalar(connection, null, "SELECT COUNT(*) FROM invites WHERE channel_id = $id;", ("$id", channelId)));
    }

    public int CountMembers(string channelId) {
        using var connection = this.Open();
        return Convert.ToInt32(Scalar(connection, null, "SELECT COUNT(*) FROM members WHERE channel_id = $id;", ("$id", channelId)));
    }

    public InviteRow? GetInvite(string channelId, long userId) {
        using var connection = this.Open();
        return this.QueryInvites(connection, "WHERE i.channel_id = $channel AND i.user_id = $user AND i.forgotten = 0", ("$channel", channelId), ("$user", userId)).FirstOrDefault();
    }

    /// <param name="limit">At most this many, newest first.</param>
    public List<InviteRow> GetInvitesForUser(long userId, int limit = int.MaxValue) {
        using var connection = this.Open();
        return this.QueryInvites(connection, "WHERE i.user_id = $user AND i.forgotten = 0 ORDER BY i.created_at DESC, i.channel_id LIMIT $limit", ("$user", userId), ("$limit", limit));
    }

    public int CountInvitesForUser(long userId) {
        using var connection = this.Open();
        return Convert.ToInt32(Scalar(connection, null, "SELECT COUNT(*) FROM invites WHERE user_id = $user AND forgotten = 0;", ("$user", userId)));
    }

    /// <summary>Users whose identities a user may fetch: themselves, people in their channels, and their inviters.</summary>
    public HashSet<long> GetVisibleUserIds(long userId) {
        using var connection = this.Open();
        using var command = Command(connection, null, """
            SELECT $me
            UNION SELECT m.user_id FROM members m WHERE m.channel_id IN (SELECT channel_id FROM members WHERE user_id = $me AND forgotten = 0)
            UNION SELECT i.user_id FROM invites i WHERE i.channel_id IN (SELECT channel_id FROM members WHERE user_id = $me AND forgotten = 0)
            UNION SELECT i.inviter_id FROM invites i WHERE i.user_id = $me AND i.forgotten = 0
            UNION SELECT m.user_id FROM members m WHERE m.channel_id IN (SELECT channel_id FROM invites WHERE user_id = $me AND forgotten = 0);
            """, ("$me", userId));
        using var reader = command.ExecuteReader();
        var ids = new HashSet<long>();
        while (reader.Read()) {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    /// <summary>
    /// Users who are a member (not just invited) of at least one channel the user is also a member of,
    /// not counting the user: who is told when the user comes online or goes offline. Not through a place its owner removed
    /// from their list (see <see cref="ForgetStaleMembership"/>): they're told nothing about that channel. The user's own
    /// place, removed or not, still counts, as the others still have them as a member.
    /// </summary>
    public HashSet<long> GetCoMemberIds(long userId) {
        using var connection = this.Open();
        using var command = Command(connection, null, """
            SELECT DISTINCT m.user_id FROM members m
            WHERE m.channel_id IN (SELECT channel_id FROM members WHERE user_id = $me) AND m.user_id != $me AND m.forgotten = 0;
            """, ("$me", userId));
        using var reader = command.ExecuteReader();
        var ids = new HashSet<long>();
        while (reader.Read()) {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    // ================================================================ epochs

    /// <summary>
    /// Applies a rekey if the channel is still at <c>newEpoch - 1</c> and at the log
    /// <paramref name="position"/> it was made for, and the keys cover exactly the members
    /// there. All in one transaction.
    /// </summary>
    public RekeyResult ApplyRekey(string channelId, ulong newEpoch, long authorId, IReadOnlyList<SealedEpochKey> keys, EncryptedName name, LogPosition position) {
        using var connection = this.Open();
        using var tx = connection.BeginTransaction();

        var channel = GetChannel(connection, tx, channelId);
        if (channel == null || channel.Epoch + 1 != newEpoch) {
            return RekeyResult.EpochStale;
        }

        if (!MembershipEntries.SamePosition(channel.LogHead, position)) {
            return RekeyResult.MembershipChanged;
        }

        var memberIds = GetMembers(connection, tx, channelId).Select(member => member.User.UserId).ToHashSet();
        var recipientIds = keys.Select(key => key.RecipientId).ToList();
        if (recipientIds.Count != memberIds.Count || !memberIds.SetEquals(recipientIds)) {
            return RekeyResult.MembershipChanged;
        }

        Execute(connection, tx, """
            UPDATE channels SET epoch = $epoch, rekey_pending = 0, name_epoch = $nameEpoch, name_revision = $nameRevision,
                name_author = $nameAuthor, name_ciphertext = $nameCiphertext, name_signature = $nameSignature,
                name_source_epoch = $sourceEpoch, name_source_revision = $sourceRevision,
                name_log_seq = $logSeq, name_log_hash = $logHash
            WHERE channel_id = $id AND epoch = $oldEpoch;
            """,
            ("$epoch", (long) newEpoch), ("$oldEpoch", (long) channel.Epoch), ("$id", channelId),
            ("$nameEpoch", (long) name.Epoch), ("$nameRevision", (long) name.Revision), ("$nameAuthor", name.AuthorId),
            ("$nameCiphertext", name.Ciphertext.ToByteArray()), ("$nameSignature", name.Signature.ToByteArray()),
            ("$sourceEpoch", name.CarriedFrom == null ? DBNull.Value : (object) (long) name.CarriedFrom.Epoch),
            ("$sourceRevision", name.CarriedFrom == null ? DBNull.Value : (object) (long) name.CarriedFrom.Revision),
            ("$logSeq", (long) position.Seq), ("$logHash", position.Hash.ToByteArray()));

        foreach (var key in keys) {
            InsertEpochKey(connection, tx, channelId, newEpoch, authorId, key);
        }

        // Every member holds the channel's key now, places moved to new keys included.
        Execute(connection, tx, "UPDATE members SET awaiting_key = 0 WHERE channel_id = $id AND awaiting_key <> 0;", ("$id", channelId));

        Execute(connection, tx, "DELETE FROM epoch_keys WHERE channel_id = $id AND epoch + $kept <= $epoch;",
            ("$id", channelId), ("$kept", (long) KeptEpochs), ("$epoch", (long) newEpoch));
        tx.Commit();
        return RekeyResult.Applied;
    }

    public List<EpochKeyForMe> GetEpochKeys(string channelId, long recipientId, ulong fromEpoch) {
        using var connection = this.Open();
        using var command = Command(connection, null, """
            SELECT epoch, author_id, ephemeral, ciphertext, signature, key_commitment, log_seq, log_hash FROM epoch_keys
            WHERE channel_id = $channel AND recipient_id = $user AND epoch >= $from ORDER BY epoch;
            """, ("$channel", channelId), ("$user", recipientId), ("$from", (long) fromEpoch));
        using var reader = command.ExecuteReader();
        var keys = new List<EpochKeyForMe>();
        while (reader.Read()) {
            keys.Add(new EpochKeyForMe {
                Epoch = (ulong) reader.GetInt64(0),
                AuthorId = reader.GetInt64(1),
                Key = new SealedEpochKey {
                    RecipientId = recipientId,
                    Box = new SealedBox {
                        EphemeralPublicKey = ByteString.CopyFrom((byte[]) reader[2]),
                        Ciphertext = ByteString.CopyFrom((byte[]) reader[3]),
                    },
                    Signature = ByteString.CopyFrom((byte[]) reader[4]),
                    KeyCommitment = ByteString.CopyFrom((byte[]) reader[5]),
                    LogPosition = ReadPosition(reader, 6, 7),
                },
            });
        }

        return keys;
    }

    // ================================================================ helpers

    private static ChannelRow? GetChannel(SqliteConnection connection, SqliteTransaction? tx, string channelId) {
        using var command = Command(connection, tx, "SELECT * FROM channels WHERE channel_id = $id;", ("$id", channelId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadChannel(reader) : null;
    }

    private static List<MemberRow> GetMembers(SqliteConnection connection, SqliteTransaction? tx, string channelId) {
        using var command = Command(connection, tx,
            $"""
            SELECT u.*, m.rank AS member_rank, m.forgotten AS member_forgotten, {SameKeys("m")} AS member_current, m.awaiting_key AS member_awaiting
            FROM members m JOIN users u ON u.user_id = m.user_id WHERE m.channel_id = $id;
            """, ("$id", channelId));
        using var reader = command.ExecuteReader();
        var members = new List<MemberRow>();
        while (reader.Read()) {
            members.Add(new MemberRow(ReadUser(reader), (Rank) reader.GetInt32(reader.GetOrdinal("member_rank")),
                reader.GetInt64(reader.GetOrdinal("member_forgotten")) != 0, reader.GetInt64(reader.GetOrdinal("member_current")) != 0,
                reader.GetInt64(reader.GetOrdinal("member_awaiting")) != 0));
        }

        return members;
    }

    /// <summary>
    /// SQL: whether a member (or invite) row's keys, those the log admitted the user with, are the keys the user is registered
    /// with now. <paramref name="row"/> is the row's alias; the user's is <c>u</c>.
    /// </summary>
    private static string SameKeys(string row) => $"({row}.signing_key = u.signing_key AND {row}.agreement_key = u.agreement_key)";

    private List<InviteRow> QueryInvites(SqliteConnection connection, string where, params (string, object)[] parameters) {
        using var command = Command(connection, null, $"""
            SELECT i.channel_id, i.user_id, i.inviter_id, i.sealed_ephemeral, i.sealed_ciphertext, i.signature, i.created_at, l.entry, i.forgotten
            FROM invites i LEFT JOIN membership_log l ON l.channel_id = i.channel_id AND l.seq = i.invite_seq AND l.hash = i.invite_hash
            {where};
            """, parameters);
        using var reader = command.ExecuteReader();
        var raw = new List<(string Channel, long Invitee, long Inviter, byte[] Ephemeral, byte[] Ciphertext, byte[] Signature, long Created, MembershipEntry? Entry, bool Forgotten)>();
        while (reader.Read()) {
            var entry = reader.IsDBNull(7) ? null : MembershipEntry.Parser.ParseFrom((byte[]) reader[7]);
            raw.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), (byte[]) reader[3], (byte[]) reader[4], (byte[]) reader[5], reader.GetInt64(6), entry,
                reader.GetInt64(8) != 0));
        }

        var users = raw.Count == 0
            ? new Dictionary<long, UserRow>()
            : QueryUsers(connection, null,
                    $"SELECT * FROM users WHERE user_id IN ({string.Join(", ", raw.SelectMany(r => new[] { r.Invitee, r.Inviter }).Distinct())});")
                .ToDictionary(user => user.UserId);

        return raw
            .Where(r => users.ContainsKey(r.Invitee) && users.ContainsKey(r.Inviter))
            .Select(r => new InviteRow(r.Channel, users[r.Invitee], users[r.Inviter],
                new SealedBox { EphemeralPublicKey = ByteString.CopyFrom(r.Ephemeral), Ciphertext = ByteString.CopyFrom(r.Ciphertext) },
                r.Signature, r.Created, r.Entry, r.Forgotten))
            .ToList();
    }

    private static void InsertEpochKey(SqliteConnection connection, SqliteTransaction tx, string channelId, ulong epoch, long authorId, SealedEpochKey key) {
        Execute(connection, tx, """
            INSERT OR REPLACE INTO epoch_keys (channel_id, epoch, recipient_id, author_id, ephemeral, ciphertext, signature, key_commitment, log_seq, log_hash)
            VALUES ($channel, $epoch, $recipient, $author, $ephemeral, $ciphertext, $signature, $commitment, $logSeq, $logHash);
            """,
            ("$channel", channelId), ("$epoch", (long) epoch), ("$recipient", key.RecipientId), ("$author", authorId),
            ("$ephemeral", key.Box.EphemeralPublicKey.ToByteArray()), ("$ciphertext", key.Box.Ciphertext.ToByteArray()),
            ("$signature", key.Signature.ToByteArray()), ("$commitment", key.KeyCommitment.ToByteArray()),
            ("$logSeq", (long) (key.LogPosition?.Seq ?? 0)), ("$logHash", key.LogPosition?.Hash.ToByteArray() ?? []));
    }

    /// <summary>A stored log position; an empty hash means none was stored.</summary>
    private static LogPosition? ReadPosition(SqliteDataReader reader, int seqColumn, int hashColumn) {
        var hash = (byte[]) reader[hashColumn];
        return hash.Length == 0 ? null : new LogPosition { Seq = (ulong) reader.GetInt64(seqColumn), Hash = ByteString.CopyFrom(hash) };
    }

    private static List<T> Query<T>(SqliteConnection connection, SqliteTransaction? tx, string sql, Func<SqliteDataReader, T> read, params (string, object)[] parameters) {
        using var command = Command(connection, tx, sql, parameters);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) {
            rows.Add(read(reader));
        }

        return rows;
    }

    private static List<UserRow> QueryUsers(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string, object)[] parameters) {
        using var command = Command(connection, tx, sql, parameters);
        using var reader = command.ExecuteReader();
        var users = new List<UserRow>();
        while (reader.Read()) {
            users.Add(ReadUser(reader));
        }

        return users;
    }

    private static UserRow ReadUser(SqliteDataReader reader) {
        return new UserRow(
            reader.GetInt64(reader.GetOrdinal("user_id")),
            reader.GetString(reader.GetOrdinal("name")),
            (uint) reader.GetInt64(reader.GetOrdinal("world_id")),
            reader.GetString(reader.GetOrdinal("world_name")),
            (byte[]) reader["signing_key"],
            (byte[]) reader["agreement_key"],
            (byte[]) reader["binding_signature"],
            (uint) reader.GetInt64(reader.GetOrdinal("key_version")),
            reader.GetInt64(reader.GetOrdinal("is_debug")) != 0);
    }

    private static ChannelRow ReadChannel(SqliteDataReader reader) {
        return new ChannelRow(
            reader.GetString(reader.GetOrdinal("channel_id")),
            (ulong) reader.GetInt64(reader.GetOrdinal("epoch")),
            reader.GetInt64(reader.GetOrdinal("rekey_pending")) != 0,
            new EncryptedName {
                Epoch = (ulong) reader.GetInt64(reader.GetOrdinal("name_epoch")),
                Revision = (ulong) reader.GetInt64(reader.GetOrdinal("name_revision")),
                AuthorId = reader.GetInt64(reader.GetOrdinal("name_author")),
                Ciphertext = ByteString.CopyFrom((byte[]) reader["name_ciphertext"]),
                Signature = ByteString.CopyFrom((byte[]) reader["name_signature"]),
                CarriedFrom = reader.IsDBNull(reader.GetOrdinal("name_source_epoch")) ? null : new NameSource {
                    Epoch = (ulong) reader.GetInt64(reader.GetOrdinal("name_source_epoch")),
                    Revision = (ulong) reader.GetInt64(reader.GetOrdinal("name_source_revision")),
                },
                LogPosition = ReadPosition(reader, reader.GetOrdinal("name_log_seq"), reader.GetOrdinal("name_log_hash")),
            },
            new LogPosition {
                Seq = (ulong) reader.GetInt64(reader.GetOrdinal("log_seq")),
                Hash = ByteString.CopyFrom((byte[]) reader["log_hash"]),
            });
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string, object)[] parameters) {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = tx;
        foreach (var (name, value) in parameters) {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string, object)[] parameters) {
        using var command = Command(connection, tx, sql, parameters);
        return command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string, object)[] parameters) {
        using var command = Command(connection, tx, sql, parameters);
        var result = command.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    private static string Key(string value) => value.Trim().ToLowerInvariant();

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
