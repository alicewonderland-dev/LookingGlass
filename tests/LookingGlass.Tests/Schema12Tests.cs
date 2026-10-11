using LookingGlass.Core.Crypto;
using LookingGlass.Protocol;
using LookingGlass.Server;
using LookingGlass.Server.Data;
using LookingGlass.Server.Hosting;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// Schema 12 (the operator tools): the upgrade from 11, on a copy of a database a running server filled (channels, messages,
/// devices, bans of every kind, flags), marks the bans from before as made on the command line or automatically and as posted;
/// an older server's writes to a schema 12 database still read sensibly; and when each account's keys were registered is
/// kept, by whichever server registers them.
/// </summary>
public sealed class Schema12Tests : IDisposable {
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lgt-schema12-" + Guid.NewGuid().ToString("N"));

    public Schema12Tests() => Directory.CreateDirectory(this._directory);

    public void Dispose() {
        Database.ReleasePooledConnections(Path.Combine(this._directory, "lookingglass.db"));
        DeleteDirectory(this._directory);
    }

    private static readonly string[] Tables = ["users", "devices", "channels", "members", "invites", "epoch_keys", "membership_log", "messages", "retired_keys", "bans",
        "abuse_flags"];

    /// <summary>
    /// A schema 11 database, as a running server leaves it, upgraded on a copy: everything kept, the bans from before marked as
    /// made on the command line (or automatically) and as posted (lifts already made too), nobody named as having made them,
    /// every account's key registration time unknown, the new tables empty; the original untouched; opening it again changes
    /// nothing more; and a server runs on it.
    /// </summary>
    [Fact]
    public async Task ASchema11DatabaseIsUpgradedInPlaceKeepingEverything() {
        var original = Path.Combine(this._directory, "lookingglass.db");
        long carolId;
        await using (var server = new Harness(this._directory)) {
            var alice = await server.RegisterAsync("Alice Kept");
            var bob = await server.RegisterAsync("Bob Kept");
            var carol = await server.RegisterAsync("Carol Banned");
            carolId = carol.UserId;
            var channelId = await bob.Session.CreateChannelAsync("Tea party", Ct);
            await AddMemberAsync(bob, channelId, alice);
            await alice.Session.SendTextAsync(channelId, "Kept through the upgrade", Ct);
            await WaitFor(() => bob.Messages.FirstOrDefault(message => message.Text == "Kept through the upgrade"));

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var path = Path.Combine(server.DataDirectory, "lookingglass.db");
            Assert.Equal(0, BanCommand.Run(new BanCommand.Request(BanCommand.BanAction.Ban, "Carol Banned@Debug", 7, "Spamming invites"), path, new AbuseOptions(),
                new StringWriter(), new StringWriter()));
            server.Database.AddBan(null, "203.0.113.0/24", "Flooding", null, false, now - 600, out _);
            server.Database.LiftBans(null, "203.0.113.0/24", now - 300);
            server.Database.AddBan(null, "198.51.100.7", "", now + 900, true, now, out _);
            server.Database.AddBan(4242, null, "Ended", now - 60, false, now - 86400, out _);
            server.Database.SaveFlag(new FlagRow("user 4242", 4242, null, now - 600, now - 60, "LookupBurst", "refused by limits in 30 of the last 60 minutes"));
        }

        // What a schema 11 server left: none of 12's columns, tables, triggers or version row.
        Database.ReleasePooledConnections(original);
        ExecuteSql(original, """
            DROP TRIGGER users_keys_registered_on_insert; DROP TRIGGER users_keys_registered_on_change;
            DROP TABLE operator_log; DROP TABLE server_state; DROP TABLE operator_pins;
            ALTER TABLE bans DROP COLUMN made_via; ALTER TABLE bans DROP COLUMN made_by; ALTER TABLE bans DROP COLUMN lifted_via;
            ALTER TABLE bans DROP COLUMN lifted_by; ALTER TABLE bans DROP COLUMN made_announced; ALTER TABLE bans DROP COLUMN lifted_announced;
            ALTER TABLE users DROP COLUMN keys_registered_at;
            DELETE FROM schema_version WHERE version = 12;
            """);
        var before = Tables.ToDictionary(table => table, table => Query(original, $"SELECT COUNT(*) FROM {table};"));
        Assert.True(before["messages"] >= 1 && before["membership_log"] >= 2 && before["bans"] == 4 && before["users"] == 3);

        var copyDirectory = Path.Combine(this._directory, "copy");
        Directory.CreateDirectory(copyDirectory);
        var copy = Path.Combine(copyDirectory, "lookingglass.db");
        Database.BackupFile(original, copy);

        var upgraded = new Database(copy);
        Assert.Equal(12L, Query(copy, "SELECT MAX(version) FROM schema_version;"));
        Assert.Equal(before, Tables.ToDictionary(table => table, table => Query(copy, $"SELECT COUNT(*) FROM {table};")));
        Assert.Equal(11L, Query(original, "SELECT MAX(version) FROM schema_version;"));
        Assert.Equal(0L, Query(original, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'operator_log';"));

        var bans = upgraded.GetActiveBans(0).Concat(upgraded.GetBanHistory(long.MaxValue)).DistinctBy(ban => ban.BanId).OrderBy(ban => ban.BanId).ToList();
        Assert.Equal(4, bans.Count);
        Assert.All(bans, ban => Assert.True(ban.MadeAnnounced));
        Assert.All(bans, ban => Assert.Null(ban.MadeBy));
        Assert.Equal(["command line", "command line", "automatic", "command line"], bans.Select(ban => ban.MadeVia));
        var lifted = bans.Single(ban => ban.Address == "203.0.113.0/24");
        Assert.Equal(("command line", (string?) null, true), (lifted.LiftedVia, lifted.LiftedBy, lifted.LiftedAnnounced));
        var inForce = bans.Single(ban => ban.UserId == carolId);
        Assert.Equal(((string?) null, false), (inForce.LiftedVia, inForce.LiftedAnnounced));
        var users = Users(upgraded, copy);
        Assert.Equal(3, users.Count);
        Assert.All(users, user => Assert.Null(user.KeysRegisteredAt));
        Assert.Empty(upgraded.GetOperatorPins());
        Assert.Empty(upgraded.GetOperatorLog(0, 10));
        Assert.Null(upgraded.GetServerState("anything"));
        Assert.Equal("Flooding", lifted.Reason);
        Assert.NotNull(upgraded.GetFlag("user 4242"));

        // --bans on it: as made on the command line (by nobody known), or automatically.
        var output = new StringWriter();
        Assert.Equal(0, BanCommand.Run(new BanCommand.Request(BanCommand.BanAction.List, null, null, null), copy, new AbuseOptions(), output, new StringWriter()));
        Assert.Contains($"user {carolId} (Carol Banned@Debug): since ", output.ToString());
        Assert.Contains("(7 days), made on the command line. Reason: Spamming invites", output.ToString());
        Assert.Contains("(made automatically)", output.ToString());
        Assert.Contains("lifted ", output.ToString());
        Assert.Contains(" on the command line, made on the command line. Reason: Flooding", output.ToString());

        // Opening it again changes nothing more.
        Database.ReleasePooledConnections(copy);
        var again = new Database(copy);
        Assert.Equal(1L, Query(copy, "SELECT COUNT(*) FROM schema_version WHERE version = 12;"));
        Assert.All(again.GetActiveBans(0).Concat(again.GetBanHistory(long.MaxValue)), ban => Assert.True(ban.MadeAnnounced));
        Database.ReleasePooledConnections(copy);

        // A server runs on it, and a character registered now has its keys' time.
        await using (var server = new Harness(copyDirectory)) {
            var dana = await server.RegisterAsync("Dana New");
            Assert.NotNull(server.Database.GetUser(dana.UserId)!.KeysRegisteredAt);
            Assert.Null(server.Database.GetUser(carolId)!.KeysRegisteredAt);
        }

        Database.ReleasePooledConnections(copy);
    }

    /// <summary>
    /// An older server (schema 11) opens a schema 12 database (it only checks the version is at least its own) and ignores what
    /// it doesn't know. What it writes there, in its own words (its statements, as they were), still reads sensibly here: its
    /// bans have no tool recorded and aren't posted yet; its lifts likewise; keys it registers get their time from the
    /// database's triggers.
    /// </summary>
    [Fact]
    public void AnOlderServersWritesToASchema12DatabaseReadSensibly() {
        var path = Path.Combine(this._directory, "lookingglass.db");
        var db = new Database(path);
        using var keys = IdentityKeys.Generate();
        db.RegisterUser(77, "Kept User", 0, ProtocolInfo.DebugWorldName, keys.ToBundle(), true);
        Assert.True(Query(path, "SELECT MAX(version) FROM schema_version;") >= 11);

        // Schema 11's AddBan and LiftBans.
        ExecuteSql(path, """
            INSERT INTO bans (user_id, address, reason, created_at, expires_at, lifted_at, automatic) VALUES (77, NULL, 'Old server', 1000, NULL, NULL, 0);
            INSERT INTO bans (user_id, address, reason, created_at, expires_at, lifted_at, automatic) VALUES (NULL, '203.0.113.5', '', 1000, NULL, NULL, 0);
            UPDATE bans SET lifted_at = 2000 WHERE address = '203.0.113.5' AND lifted_at IS NULL AND (expires_at IS NULL OR expires_at > 2000);
            """);
        var ban = Assert.Single(db.GetActiveBans(3000));
        Assert.Equal(((string?) null, (string?) null, false), (ban.MadeVia, ban.MadeBy, ban.MadeAnnounced));
        var lifted = Assert.Single(db.GetBanHistory(3000));
        Assert.Equal(((string?) null, false), (lifted.LiftedVia, lifted.LiftedAnnounced));
        var output = new StringWriter();
        Assert.Equal(0, BanCommand.Run(new BanCommand.Request(BanCommand.BanAction.List, null, null, null), path, new AbuseOptions(), output, new StringWriter(),
            DateTimeOffset.FromUnixTimeSeconds(3000)));
        Assert.Contains("user 77 (Kept User@Debug): since 1970-01-01 00:16 UTC, until lifted. Reason: Old server", output.ToString());

        // Schema 11's RegisterUser, with new keys: the trigger records when.
        using var newKeys = IdentityKeys.Generate();
        ExecuteSql(path, $"""
            INSERT INTO users (user_id, name, name_key, world_id, world_name, world_key, signing_key, agreement_key,
                               binding_signature, key_version, is_debug, created_at, updated_at)
            VALUES (77, 'Kept User', 'kept user', 0, 'Debug', 'debug', x'{Convert.ToHexString(newKeys.SigningPublicKey)}',
                    x'{Convert.ToHexString(newKeys.AgreementPublicKey)}', x'00', 2, 1, 5000, 5000)
            ON CONFLICT (user_id) DO UPDATE SET
                name = excluded.name, name_key = excluded.name_key,
                world_id = excluded.world_id, world_name = excluded.world_name, world_key = excluded.world_key,
                signing_key = excluded.signing_key, agreement_key = excluded.agreement_key,
                binding_signature = excluded.binding_signature, key_version = excluded.key_version,
                is_debug = excluded.is_debug, updated_at = excluded.updated_at;
            INSERT INTO users (user_id, name, name_key, world_id, world_name, world_key, signing_key, agreement_key,
                               binding_signature, key_version, is_debug, created_at, updated_at)
            VALUES (78, 'Older User', 'older user', 0, 'Debug', 'debug', x'01', x'02', x'00', 1, 1, 6000, 6000);
            """);
        Assert.Equal(5000, db.GetUser(77)!.KeysRegisteredAt);
        Assert.Equal(6000, db.GetUser(78)!.KeysRegisteredAt);
    }

    /// <summary>
    /// When an account's current keys were registered: when it was made, kept while it registers again with the same keys, and
    /// moved when its keys change.
    /// </summary>
    [Fact]
    public void WhenTheKeysWereRegisteredIsKept() {
        var path = Path.Combine(this._directory, "lookingglass.db");
        var db = new Database(path);
        using var keys = IdentityKeys.Generate();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var made = db.RegisterUser(77, "Kept User", 73, "Lich", keys.ToBundle(), false).User;
        Assert.InRange(made.KeysRegisteredAt!.Value, now - 5, now + 5);

        ExecuteSql(path, "UPDATE users SET keys_registered_at = 1000 WHERE user_id = 77;");
        Assert.Equal(1000, db.RegisterUser(77, "Kept User", 73, "Lich", keys.ToBundle(), false).User.KeysRegisteredAt);
        // Renamed, same keys: still the same.
        Assert.Equal(1000, db.RegisterUser(77, "Renamed User", 73, "Lich", keys.ToBundle(), false).User.KeysRegisteredAt);

        using var newKeys = IdentityKeys.Generate();
        Assert.InRange(db.RegisterUser(77, "Renamed User", 73, "Lich", newKeys.ToBundle(), false).User.KeysRegisteredAt!.Value, now - 5, now + 5);
        Database.ReleasePooledConnections(path);
    }

    /// <summary>The server's own bookkeeping (for the alerts: the daily summary, a clean stop) is kept by key.</summary>
    [Fact]
    public void TheServersStateIsKeptByKey() {
        var path = Path.Combine(this._directory, "lookingglass.db");
        var db = new Database(path);
        Assert.Null(db.GetServerState("daily_summary_at"));
        db.SetServerState("daily_summary_at", "1000");
        db.SetServerState("daily_summary_at", "2000");
        db.SetServerState("stopped_cleanly", "true");
        Assert.Equal(("2000", "true"), (db.GetServerState("daily_summary_at"), db.GetServerState("stopped_cleanly")));
        Database.ReleasePooledConnections(path);
    }

    private static List<UserRow> Users(Database db, string path) {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT user_id FROM users;";
        using var reader = command.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read()) {
            ids.Add(reader.GetInt64(0));
        }

        return db.GetUsers(ids);
    }

    private static void ExecuteSql(string path, string sql) {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Query(string path, string sql) {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
