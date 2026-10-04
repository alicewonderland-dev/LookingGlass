using Google.Protobuf;
using Microsoft.Data.Sqlite;
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

public sealed record ChannelRow(string ChannelId, ulong Epoch, bool RekeyPending, EncryptedName? Name);

public sealed record MemberRow(UserRow User, Rank Rank);

public sealed record InviteRow(string ChannelId, UserRow Invitee, UserRow Inviter, SealedBox SealedName, byte[] Signature, long CreatedUnix);

public enum RekeyResult {
    Applied,
    EpochStale,
    MembershipChanged,
}

/// <summary>
/// SQLite storage. Every multi-statement change runs in one transaction.
/// Methods are synchronous (SQLite is in-process) and short.
/// </summary>
public sealed class Database {
    private const int SchemaVersion = 2;
    private const int KeptEpochs = 4;

    private readonly string _connectionString;

    public Database(string path) {
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

        using var tx = connection.BeginTransaction();
        if (current < 1) {
            CreateVersion1(connection, tx);
        }

        if (current < 2) {
            // Signed name revisions (so an older name can't be replayed within an epoch).
            Execute(connection, tx, """
                ALTER TABLE channels ADD COLUMN name_revision INTEGER NOT NULL DEFAULT 0;
                INSERT INTO schema_version (version) VALUES (2);
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
        // name without deleting that account, which would silently cascade away its memberships.
        Execute(connection, tx, """
            UPDATE users SET name_key = name_key || '#stale-' || user_id, world_key = world_key || '#stale-' || user_id
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
            // Their old keys can't open anything sealed to the new identity: every channel needs a rekey.
            Execute(connection, tx, "UPDATE channels SET rekey_pending = 1 WHERE channel_id IN (SELECT channel_id FROM members WHERE user_id = $id);", ("$id", userId));
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

    public List<ChannelRow> GetChannelsForUser(long userId) {
        using var connection = this.Open();
        using var command = Command(connection, null,
            "SELECT c.* FROM channels c JOIN members m ON m.channel_id = c.channel_id WHERE m.user_id = $id;", ("$id", userId));
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

    public void CreateChannel(string channelId, long creatorId, SealedEpochKey creatorKey, EncryptedName name) {
        using var connection = this.Open();
        using var tx = connection.BeginTransaction();
        var now = Now();
        Execute(connection, tx, """
            INSERT INTO channels (channel_id, epoch, rekey_pending, name_epoch, name_revision, name_author, name_ciphertext, name_signature, created_at)
            VALUES ($id, 0, 0, $nameEpoch, $nameRevision, $nameAuthor, $nameCiphertext, $nameSignature, $now);
            """,
            ("$id", channelId), ("$nameEpoch", (long) name.Epoch), ("$nameRevision", (long) name.Revision), ("$nameAuthor", name.AuthorId),
            ("$nameCiphertext", name.Ciphertext.ToByteArray()), ("$nameSignature", name.Signature.ToByteArray()), ("$now", now));
        Execute(connection, tx, "INSERT INTO members (channel_id, user_id, rank, joined_at) VALUES ($id, $user, $rank, $now);",
            ("$id", channelId), ("$user", creatorId), ("$rank", (long) Rank.Admin), ("$now", now));
        InsertEpochKey(connection, tx, channelId, 0, creatorId, creatorKey);
        tx.Commit();
    }

    public void DeleteChannel(string channelId) {
        using var connection = this.Open();
        Execute(connection, null, "DELETE FROM channels WHERE channel_id = $id;", ("$id", channelId));
    }

    /// <summary>
    /// Renames only if the channel is still at the name's epoch, not awaiting a
    /// rekey, and the name's revision is newer than the stored one.
    /// </summary>
    /// <returns>False if the channel changed meanwhile, or the revision isn't newer.</returns>
    public bool RenameChannel(string channelId, EncryptedName name) {
        using var connection = this.Open();
        return Execute(connection, null, """
            UPDATE channels SET name_epoch = $epoch, name_revision = $revision, name_author = $author,
                name_ciphertext = $ciphertext, name_signature = $signature
            WHERE channel_id = $id AND epoch = $epoch AND rekey_pending = 0
                AND (name_epoch < $epoch OR name_revision < $revision);
            """,
            ("$id", channelId), ("$epoch", (long) name.Epoch), ("$revision", (long) name.Revision), ("$author", name.AuthorId),
            ("$ciphertext", name.Ciphertext.ToByteArray()), ("$signature", name.Signature.ToByteArray())) == 1;
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

    public void AddInvite(string channelId, long inviteeId, long inviterId, SealedBox sealedName, byte[] signature) {
        using var connection = this.Open();
        Execute(connection, null, """
            INSERT INTO invites (channel_id, user_id, inviter_id, sealed_ephemeral, sealed_ciphertext, signature, created_at)
            VALUES ($channel, $user, $inviter, $ephemeral, $ciphertext, $signature, $now)
            ON CONFLICT (channel_id, user_id) DO UPDATE SET
                inviter_id = excluded.inviter_id, sealed_ephemeral = excluded.sealed_ephemeral,
                sealed_ciphertext = excluded.sealed_ciphertext, signature = excluded.signature, created_at = excluded.created_at;
            """,
            ("$channel", channelId), ("$user", inviteeId), ("$inviter", inviterId),
            ("$ephemeral", sealedName.EphemeralPublicKey.ToByteArray()), ("$ciphertext", sealedName.Ciphertext.ToByteArray()),
            ("$signature", signature), ("$now", Now()));
    }

    public InviteRow? GetInvite(string channelId, long userId) {
        using var connection = this.Open();
        return this.QueryInvites(connection, "WHERE i.channel_id = $channel AND i.user_id = $user", ("$channel", channelId), ("$user", userId)).FirstOrDefault();
    }

    public List<InviteRow> GetInvitesForUser(long userId) {
        using var connection = this.Open();
        return this.QueryInvites(connection, "WHERE i.user_id = $user", ("$user", userId));
    }

    public bool DeleteInvite(string channelId, long userId) {
        using var connection = this.Open();
        return Execute(connection, null, "DELETE FROM invites WHERE channel_id = $channel AND user_id = $user;", ("$channel", channelId), ("$user", userId)) > 0;
    }

    /// <summary>Turns an invite into membership and marks the channel for rekeying.</summary>
    public bool AcceptInvite(string channelId, long userId) {
        using var connection = this.Open();
        using var tx = connection.BeginTransaction();
        if (Execute(connection, tx, "DELETE FROM invites WHERE channel_id = $channel AND user_id = $user;", ("$channel", channelId), ("$user", userId)) == 0) {
            return false;
        }

        Execute(connection, tx, "INSERT INTO members (channel_id, user_id, rank, joined_at) VALUES ($channel, $user, $rank, $now);",
            ("$channel", channelId), ("$user", userId), ("$rank", (long) Rank.Member), ("$now", Now()));
        Execute(connection, tx, "UPDATE channels SET rekey_pending = 1 WHERE channel_id = $channel;", ("$channel", channelId));
        tx.Commit();
        return true;
    }

    /// <summary>Removes a member, their stored keys, and marks the channel for rekeying.</summary>
    public bool RemoveMember(string channelId, long userId) {
        using var connection = this.Open();
        using var tx = connection.BeginTransaction();
        if (Execute(connection, tx, "DELETE FROM members WHERE channel_id = $channel AND user_id = $user;", ("$channel", channelId), ("$user", userId)) == 0) {
            return false;
        }

        Execute(connection, tx, "DELETE FROM epoch_keys WHERE channel_id = $channel AND recipient_id = $user;", ("$channel", channelId), ("$user", userId));
        Execute(connection, tx, "UPDATE channels SET rekey_pending = 1 WHERE channel_id = $channel;", ("$channel", channelId));
        tx.Commit();
        return true;
    }

    public void SetRank(string channelId, long userId, Rank rank) {
        using var connection = this.Open();
        Execute(connection, null, "UPDATE members SET rank = $rank WHERE channel_id = $channel AND user_id = $user;",
            ("$rank", (long) rank), ("$channel", channelId), ("$user", userId));
    }

    /// <summary>Makes <paramref name="newAdminId"/> the admin and demotes the old admin to moderator, atomically.</summary>
    /// <returns>False (and nothing changes) if either is no longer in the expected role.</returns>
    public bool TransferAdmin(string channelId, long oldAdminId, long newAdminId) {
        using var connection = this.Open();
        using var tx = connection.BeginTransaction();
        var demoted = Execute(connection, tx, "UPDATE members SET rank = $new WHERE channel_id = $channel AND user_id = $user AND rank = $old;",
            ("$new", (long) Rank.Moderator), ("$old", (long) Rank.Admin), ("$channel", channelId), ("$user", oldAdminId));
        var promoted = Execute(connection, tx, "UPDATE members SET rank = $new WHERE channel_id = $channel AND user_id = $user AND rank IN ($member, $moderator);",
            ("$new", (long) Rank.Admin), ("$member", (long) Rank.Member), ("$moderator", (long) Rank.Moderator),
            ("$channel", channelId), ("$user", newAdminId));

        if (demoted != 1 || promoted != 1) {
            tx.Rollback();
            return false;
        }

        tx.Commit();
        return true;
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
    /// Applies a rekey if the channel is still at <c>newEpoch - 1</c> and the
    /// keys cover exactly the current members. All in one transaction.
    /// </summary>
    public RekeyResult ApplyRekey(string channelId, ulong newEpoch, long authorId, IReadOnlyList<SealedEpochKey> keys, EncryptedName name) {
        using var connection = this.Open();
        using var tx = connection.BeginTransaction();

        var channel = GetChannel(connection, tx, channelId);
        if (channel == null || channel.Epoch + 1 != newEpoch) {
            return RekeyResult.EpochStale;
        }

        var memberIds = GetMembers(connection, tx, channelId).Select(member => member.User.UserId).ToHashSet();
        var recipientIds = keys.Select(key => key.RecipientId).ToList();
        if (recipientIds.Count != memberIds.Count || !memberIds.SetEquals(recipientIds)) {
            return RekeyResult.MembershipChanged;
        }

        Execute(connection, tx, """
            UPDATE channels SET epoch = $epoch, rekey_pending = 0, name_epoch = $nameEpoch, name_revision = $nameRevision,
                name_author = $nameAuthor, name_ciphertext = $nameCiphertext, name_signature = $nameSignature
            WHERE channel_id = $id AND epoch = $oldEpoch;
            """,
            ("$epoch", (long) newEpoch), ("$oldEpoch", (long) channel.Epoch), ("$id", channelId),
            ("$nameEpoch", (long) name.Epoch), ("$nameRevision", (long) name.Revision), ("$nameAuthor", name.AuthorId),
            ("$nameCiphertext", name.Ciphertext.ToByteArray()), ("$nameSignature", name.Signature.ToByteArray()));

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
            SELECT epoch, author_id, ephemeral, ciphertext, signature FROM epoch_keys
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
        using var command = Command(connection, null, $"SELECT i.channel_id, i.user_id, i.inviter_id, i.sealed_ephemeral, i.sealed_ciphertext, i.signature, i.created_at FROM invites i {where};", parameters);
        using var reader = command.ExecuteReader();
        var raw = new List<(string Channel, long Invitee, long Inviter, byte[] Ephemeral, byte[] Ciphertext, byte[] Signature, long Created)>();
        while (reader.Read()) {
            raw.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), (byte[]) reader[3], (byte[]) reader[4], (byte[]) reader[5], reader.GetInt64(6)));
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
                r.Signature, r.Created))
            .ToList();
    }

    private static void InsertEpochKey(SqliteConnection connection, SqliteTransaction tx, string channelId, ulong epoch, long authorId, SealedEpochKey key) {
        Execute(connection, tx, """
            INSERT OR REPLACE INTO epoch_keys (channel_id, epoch, recipient_id, author_id, ephemeral, ciphertext, signature)
            VALUES ($channel, $epoch, $recipient, $author, $ephemeral, $ciphertext, $signature);
            """,
            ("$channel", channelId), ("$epoch", (long) epoch), ("$recipient", key.RecipientId), ("$author", authorId),
            ("$ephemeral", key.Box.EphemeralPublicKey.ToByteArray()), ("$ciphertext", key.Box.Ciphertext.ToByteArray()),
            ("$signature", key.Signature.ToByteArray()));
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
