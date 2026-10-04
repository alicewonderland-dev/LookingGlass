using Google.Protobuf;
using Microsoft.Data.Sqlite;
using WonderlandChat.Core.Membership;
using WonderlandChat.Protocol;

namespace WonderlandChat.Server.Data;

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

public sealed record MemberRow(UserRow User, Rank Rank);

/// <param name="Entry">The invite's entry in the channel's membership log.</param>
public sealed record InviteRow(string ChannelId, UserRow Invitee, UserRow Inviter, SealedBox SealedName, byte[] Signature, long CreatedUnix, MembershipEntry? Entry);

public enum RekeyResult {
    Applied,
    EpochStale,
    MembershipChanged,
}

/// <summary>The database file can't be used by this version of the server. It is left as it was.</summary>
public sealed class UnsupportedDatabaseException(string message) : Exception(message);

/// <summary>
/// SQLite storage. Every multi-statement change runs in one transaction.
/// Methods are synchronous (SQLite is in-process) and short.
/// </summary>
public sealed class Database {
    private const int SchemaVersion = 4;
    private const int KeptEpochs = 4;

    private readonly string _path;
    private readonly string _connectionString;

    /// <exception cref="UnsupportedDatabaseException">The file is from a version whose channels this one can't use.</exception>
    public Database(string path) {
        this._path = path;
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

        tx.Commit();
    }

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

    /// <summary>Creates or replaces a user's registration. Revokes all their devices.</summary>
    /// <returns>The stored user, and whether their identity keys changed.</returns>
    public (UserRow User, bool KeysChanged) RegisterUser(long userId, string name, uint worldId, string worldName, IdentityBundle identity, bool isDebug) {
        using var connection = this.Open();
        using var tx = connection.BeginTransaction();
        var now = Now();
        var existing = QueryUsers(connection, tx, "SELECT * FROM users WHERE user_id = $id;", ("$id", userId)).FirstOrDefault();

        var signing = identity.SigningPublicKey.ToByteArray();
        var agreement = identity.AgreementPublicKey.ToByteArray();
        var keysChanged = existing != null
                          && (!existing.SigningKey.AsSpan().SequenceEqual(signing) || !existing.AgreementKey.AsSpan().SequenceEqual(agreement));
        var keyVersion = existing == null ? 1u : keysChanged ? existing.KeyVersion + 1 : existing.KeyVersion;

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
            // The new keys can't open anything sealed to the old ones. Their memberships stay bound to the
            // old keys (the log says so), and the new ones only join a channel when invited again.
            Execute(connection, tx, "DELETE FROM epoch_keys WHERE recipient_id = $id;", ("$id", userId));
        }

        tx.Commit();
        var stored = QueryUsers(connection, null, "SELECT * FROM users WHERE user_id = $id;", ("$id", userId)).Single();
        return (stored, keysChanged);
    }

    public void AddDevice(long userId, byte[] tokenHash) {
        using var connection = this.Open();
        var now = Now();
        Execute(connection, null, "INSERT INTO devices (token_hash, user_id, created_at, last_used_at) VALUES ($hash, $id, $now, $now);",
            ("$hash", tokenHash), ("$id", userId), ("$now", now));
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

    /// <param name="limit">At most this many, oldest memberships first.</param>
    public List<ChannelRow> GetChannelsForUser(long userId, int limit = int.MaxValue) {
        using var connection = this.Open();
        using var command = Command(connection, null,
            "SELECT c.* FROM channels c JOIN members m ON m.channel_id = c.channel_id WHERE m.user_id = $id ORDER BY m.joined_at, c.channel_id LIMIT $limit;",
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
        return Convert.ToInt32(Scalar(connection, null, "SELECT COUNT(*) FROM members WHERE user_id = $id;", ("$id", userId)));
    }

    /// <returns>The user's rank: a member rank, <see cref="Rank.Invited"/> for a pending invite, or null.</returns>
    public Rank? GetRank(string channelId, long userId) {
        using var connection = this.Open();
        return GetRank(connection, null, channelId, userId);
    }

    public List<MemberRow> GetMembers(string channelId) {
        using var connection = this.Open();
        return GetMembers(connection, null, channelId);
    }

    public List<MemberRow> GetInvitees(string channelId) {
        using var connection = this.Open();
        return this.QueryInvites(connection, "WHERE i.channel_id = $id", ("$id", channelId))
            .Select(invite => new MemberRow(invite.Invitee, Rank.Invited))
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

    public void DeleteChannel(string channelId) {
        using var connection = this.Open();
        Execute(connection, null, "DELETE FROM channels WHERE channel_id = $id;", ("$id", channelId));
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
        var channel = GetChannel(connection, null, channelId);
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

        using (var command = Command(connection, null, "SELECT user_id, rank, signing_key, agreement_key FROM members WHERE channel_id = $id;", ("$id", channelId))) {
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

        using (var command = Command(connection, null, """
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
                        inviter_signing_key = excluded.inviter_signing_key, inviter_agreement_key = excluded.inviter_agreement_key;
                    """,
                    ("$channel", channelId), ("$user", subject.UserId), ("$inviter", entry.ActorId),
                    ("$ephemeral", sealedName?.EphemeralPublicKey.ToByteArray() ?? []), ("$ciphertext", sealedName?.Ciphertext.ToByteArray() ?? []),
                    ("$signature", inviteSignature ?? []), ("$now", Now()),
                    ("$signing", subject.SigningPublicKey.ToByteArray()), ("$agreement", subject.AgreementPublicKey.ToByteArray()),
                    ("$seq", (long) entry.Seq), ("$hash", hash), ("$inviterSigning", inviter.Item1), ("$inviterAgreement", inviter.Item2));
                break;
            }
            case MembershipEntryKind.Accept:
                Execute(connection, tx, "DELETE FROM invites WHERE channel_id = $channel AND user_id = $user;", who);
                Execute(connection, tx, """
                    INSERT OR REPLACE INTO members (channel_id, user_id, rank, joined_at, signing_key, agreement_key)
                    VALUES ($channel, $user, $rank, $now, $signing, $agreement);
                    """,
                    ("$channel", channelId), ("$user", subject.UserId), ("$rank", (long) Rank.Member), ("$now", Now()),
                    ("$signing", subject.SigningPublicKey.ToByteArray()), ("$agreement", subject.AgreementPublicKey.ToByteArray()));
                Execute(connection, tx, "UPDATE channels SET rekey_pending = 1 WHERE channel_id = $channel;", ("$channel", channelId));
                break;
            case MembershipEntryKind.Decline:
            case MembershipEntryKind.CancelInvite:
                Execute(connection, tx, "DELETE FROM invites WHERE channel_id = $channel AND user_id = $user;", who);
                break;
            case MembershipEntryKind.Remove:
            case MembershipEntryKind.Leave:
                Execute(connection, tx, "DELETE FROM members WHERE channel_id = $channel AND user_id = $user;", who);
                Execute(connection, tx, "DELETE FROM epoch_keys WHERE channel_id = $channel AND recipient_id = $user;", who);
                Execute(connection, tx, "UPDATE channels SET rekey_pending = 1 WHERE channel_id = $channel;", ("$channel", channelId));
                break;
            case MembershipEntryKind.SetRank:
                Execute(connection, tx, "UPDATE members SET rank = $rank WHERE channel_id = $channel AND user_id = $user;",
                    ("$rank", (long) entry.Rank), ("$channel", channelId), ("$user", subject.UserId));
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
        tx.Commit();
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
        return this.QueryInvites(connection, "WHERE i.channel_id = $channel AND i.user_id = $user", ("$channel", channelId), ("$user", userId)).FirstOrDefault();
    }

    /// <param name="limit">At most this many, newest first.</param>
    public List<InviteRow> GetInvitesForUser(long userId, int limit = int.MaxValue) {
        using var connection = this.Open();
        return this.QueryInvites(connection, "WHERE i.user_id = $user ORDER BY i.created_at DESC, i.channel_id LIMIT $limit", ("$user", userId), ("$limit", limit));
    }

    public int CountInvitesForUser(long userId) {
        using var connection = this.Open();
        return Convert.ToInt32(Scalar(connection, null, "SELECT COUNT(*) FROM invites WHERE user_id = $user;", ("$user", userId)));
    }

    /// <summary>Users whose identities a user may fetch: themselves, people in their channels, and their inviters.</summary>
    public HashSet<long> GetVisibleUserIds(long userId) {
        using var connection = this.Open();
        using var command = Command(connection, null, """
            SELECT $me
            UNION SELECT m.user_id FROM members m WHERE m.channel_id IN (SELECT channel_id FROM members WHERE user_id = $me)
            UNION SELECT i.user_id FROM invites i WHERE i.channel_id IN (SELECT channel_id FROM members WHERE user_id = $me)
            UNION SELECT i.inviter_id FROM invites i WHERE i.user_id = $me
            UNION SELECT m.user_id FROM members m WHERE m.channel_id IN (SELECT channel_id FROM invites WHERE user_id = $me);
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

    private static Rank? GetRank(SqliteConnection connection, SqliteTransaction? tx, string channelId, long userId) {
        var rank = Scalar(connection, tx, "SELECT rank FROM members WHERE channel_id = $channel AND user_id = $user;", ("$channel", channelId), ("$user", userId));
        if (rank != null) {
            return (Rank) Convert.ToInt32(rank);
        }

        var invited = Scalar(connection, tx, "SELECT 1 FROM invites WHERE channel_id = $channel AND user_id = $user;", ("$channel", channelId), ("$user", userId));
        return invited != null ? Rank.Invited : null;
    }

    private static List<MemberRow> GetMembers(SqliteConnection connection, SqliteTransaction? tx, string channelId) {
        using var command = Command(connection, tx,
            "SELECT u.*, m.rank AS member_rank FROM members m JOIN users u ON u.user_id = m.user_id WHERE m.channel_id = $id;", ("$id", channelId));
        using var reader = command.ExecuteReader();
        var members = new List<MemberRow>();
        while (reader.Read()) {
            members.Add(new MemberRow(ReadUser(reader), (Rank) reader.GetInt32(reader.GetOrdinal("member_rank"))));
        }

        return members;
    }

    private List<InviteRow> QueryInvites(SqliteConnection connection, string where, params (string, object)[] parameters) {
        using var command = Command(connection, null, $"""
            SELECT i.channel_id, i.user_id, i.inviter_id, i.sealed_ephemeral, i.sealed_ciphertext, i.signature, i.created_at, l.entry
            FROM invites i LEFT JOIN membership_log l ON l.channel_id = i.channel_id AND l.seq = i.invite_seq AND l.hash = i.invite_hash
            {where};
            """, parameters);
        using var reader = command.ExecuteReader();
        var raw = new List<(string Channel, long Invitee, long Inviter, byte[] Ephemeral, byte[] Ciphertext, byte[] Signature, long Created, MembershipEntry? Entry)>();
        while (reader.Read()) {
            var entry = reader.IsDBNull(7) ? null : MembershipEntry.Parser.ParseFrom((byte[]) reader[7]);
            raw.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), (byte[]) reader[3], (byte[]) reader[4], (byte[]) reader[5], reader.GetInt64(6), entry));
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
                r.Signature, r.Created, r.Entry))
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
