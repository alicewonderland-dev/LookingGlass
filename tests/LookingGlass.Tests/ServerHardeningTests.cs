using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using Google.Protobuf;
using LookingGlass.Core.Client;
using LookingGlass.Core.Crypto;
using LookingGlass.Core.Membership;
using LookingGlass.Core.Util;
using LookingGlass.Protocol;
using LookingGlass.Server;
using LookingGlass.Server.Data;
using LookingGlass.Server.Hosting;
using LookingGlass.Server.Realtime;
using LookingGlass.Server.Services;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>Server-side guards: debug-account gating, frame limits, database race guards, identity visibility.</summary>
public sealed class ServerHardeningTests {
    [Fact]
    public async Task DebugAccountsStopWorkingWhenDisabled() {
        var directory = Path.Combine(Path.GetTempPath(), "lgt-" + Guid.NewGuid().ToString("N"));
        var store = new InMemorySecretStore();
        try {
            await using (var enabled = new Harness(directory)) {
                await enabled.RegisterAsync("Debug Gate", store);
            }

            await using var disabled = new Harness(directory, allowDebugAccounts: false);

            // The existing debug account's token is refused (and kept, in case the server allows them again)...
            var token = store.Load().DeviceToken;
            var returning = disabled.StartClient("Debug Gate", store);
            await WaitFor(() => returning.Session.Snapshot.State == ConnectionState.LoginNotRecognized ? new object() : null);
            Assert.False(returning.Session.Snapshot.DebugAccountsEnabled);
            Assert.Equal(token, store.Load().DeviceToken);

            // ...and new debug registrations are refused.
            var error = await Assert.ThrowsAsync<ServerErrorException>(() => returning.Session.StartRegistrationAsync(
                new Character { Name = "Debug Gate", WorldName = ProtocolInfo.DebugWorldName }, Ct));
            Assert.Equal(ErrorCode.RegistrationFailed, error.Code);
        } finally {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task OversizedFrameClosesTheConnection() {
        await using var server = new Harness();
        try {
            using var socket = await server.Factory.Server.CreateWebSocketClient()
                .ConnectAsync(new Uri(server.Factory.Server.BaseAddress, ProtocolInfo.WebSocketPath), Ct);
            var huge = new byte[(int) ProtocolInfo.DefaultLimits().MaxFrameBytes + 1];
            await socket.SendAsync(huge, WebSocketMessageType.Binary, true, Ct);

            var buffer = new byte[1024];
            var result = await socket.ReceiveAsync(buffer, Ct).WaitAsync(Harness.Timeout, Ct);
            Assert.Equal(WebSocketMessageType.Close, result.MessageType);
            Assert.Equal(WebSocketCloseStatus.MessageTooBig, result.CloseStatus);
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
    }

    [Fact]
    public void RenameIsRejectedForStaleEpochOrPendingRekey() {
        var (db, directory) = NewDatabase();
        try {
            var (channelId, admin, keys) = CreateChannel(db);
            var head = db.GetChannel(channelId)!.LogHead;
            var name = ChannelCrypto.EncryptName("Renamed", ChannelCrypto.NewEpochKey(), channelId, 0, head, keys, admin, revision: 1);

            Assert.True(db.RenameChannel(channelId, name));
            Assert.Equal(1UL, db.GetChannel(channelId)!.Name!.Revision);
            Assert.False(db.RenameChannel(channelId, ChannelCrypto.EncryptName("Stale", ChannelCrypto.NewEpochKey(), channelId, 5, head, keys, admin, revision: 2)));

            // A revision that isn't newer than the stored one (a replay, or a stale client) is refused.
            Assert.False(db.RenameChannel(channelId, name));
            Assert.False(db.RenameChannel(channelId, ChannelCrypto.EncryptName("Older", ChannelCrypto.NewEpochKey(), channelId, 0, head, keys, admin)));
            Assert.True(db.RenameChannel(channelId, ChannelCrypto.EncryptName("Newer", ChannelCrypto.NewEpochKey(), channelId, 0, head, keys, admin, revision: 2)));

            // A name made for an older log position is refused once the log moves on...
            var (other, otherKeys) = RegisterUser(db, "Other User");
            Assert.True(db.AppendEntry(channelId, Next(db, channelId, MembershipEntryKind.Invite, other, keys, admin, MemberKeys.Of(otherKeys)), SomeBox(), new byte[64]));
            Assert.False(db.RenameChannel(channelId, ChannelCrypto.EncryptName("Old Position", ChannelCrypto.NewEpochKey(), channelId, 0, head, keys, admin, revision: 3)));

            // ...and any name while a membership change awaits its rekey.
            Assert.True(db.AppendEntry(channelId, Next(db, channelId, MembershipEntryKind.Accept, other, otherKeys, other)));
            Assert.True(db.GetChannel(channelId)!.RekeyPending);
            var newHead = db.GetChannel(channelId)!.LogHead;
            Assert.False(db.RenameChannel(channelId, ChannelCrypto.EncryptName("Pending", ChannelCrypto.NewEpochKey(), channelId, 0, newHead, keys, admin, revision: 4)));
        } finally {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void EmptyOlderDatabaseIsMigrated() {
        var (db, directory) = NewDatabase();
        try {
            var path = Path.Combine(directory, "test.db");
            var (user, _) = RegisterUser(db, "Early User");

            // As if made by schema 2, before name sources and the membership log, and holding no channels.
            QueryLong(path, """
                DROP TABLE retired_keys;
                DROP INDEX users_by_signing_key;
                DROP TABLE membership_log;
                ALTER TABLE channels DROP COLUMN name_source_epoch;
                ALTER TABLE channels DROP COLUMN name_source_revision;
                ALTER TABLE channels DROP COLUMN log_seq;
                ALTER TABLE channels DROP COLUMN log_hash;
                ALTER TABLE channels DROP COLUMN name_log_seq;
                ALTER TABLE channels DROP COLUMN name_log_hash;
                ALTER TABLE members DROP COLUMN signing_key;
                ALTER TABLE members DROP COLUMN agreement_key;
                ALTER TABLE invites DROP COLUMN signing_key;
                ALTER TABLE invites DROP COLUMN agreement_key;
                ALTER TABLE invites DROP COLUMN invite_seq;
                ALTER TABLE invites DROP COLUMN invite_hash;
                ALTER TABLE invites DROP COLUMN inviter_signing_key;
                ALTER TABLE invites DROP COLUMN inviter_agreement_key;
                ALTER TABLE epoch_keys DROP COLUMN log_seq;
                ALTER TABLE epoch_keys DROP COLUMN log_hash;
                DELETE FROM schema_version WHERE version >= 3;
                SELECT 0;
                """);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            var migrated = new Database(path);
            Assert.Equal(6L, QueryLong(path, "SELECT MAX(version) FROM schema_version;"));
            Assert.NotNull(migrated.GetUser(user));
            var (channelId, _, _) = CreateChannel(migrated);
            Assert.Null(migrated.GetChannel(channelId)!.Name!.CarriedFrom);
            Assert.Single(migrated.GetLogEntries(channelId, 0, 10));
        } finally {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// A 0.2 database from before retired keys were kept (schema 4) is upgraded in place, channels and all: from then on
    /// a key the account replaces is retired. Keys replaced before the upgrade aren't known.
    /// </summary>
    [Fact]
    public void Schema4DatabaseGainsRetiredKeys() {
        var (db, directory) = NewDatabase();
        try {
            var path = Path.Combine(directory, "test.db");
            var (channelId, admin, keys) = CreateChannel(db);
            QueryLong(path, "DROP TABLE retired_keys; DROP INDEX users_by_signing_key; DELETE FROM schema_version WHERE version >= 5; SELECT 0;");
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            var migrated = new Database(path);
            Assert.Equal(6L, QueryLong(path, "SELECT MAX(version) FROM schema_version;"));
            Assert.NotNull(migrated.GetChannel(channelId));
            Assert.False(migrated.IsKeyRetired(admin, keys.SigningPublicKey));

            using var newKeys = IdentityKeys.Generate();
            migrated.RegisterUser(admin, "Channel Admin", 0, ProtocolInfo.DebugWorldName, newKeys.ToBundle(), true);
            Assert.True(migrated.IsKeyRetired(admin, keys.SigningPublicKey));
            Assert.Throws<KeyRetiredException>(() => migrated.RegisterUser(admin, "Channel Admin", 0, ProtocolInfo.DebugWorldName, keys.ToBundle(), true));
        } finally {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// A database made while retired keys were kept per key alone (schema 5, never released) keeps its retirements, each
    /// now for the account it was recorded for: one that recorded another account's key (as the exploit of registering
    /// someone's public key did) no longer shuts that account out.
    /// </summary>
    [Fact]
    public void Schema5DatabaseKeepsItsRetiredKeysPerAccount() {
        var (db, directory) = NewDatabase();
        try {
            var path = Path.Combine(directory, "test.db");
            var (alice, aliceKeys) = RegisterUser(db, "Schema Five Alice");
            var (mallory, _) = RegisterUser(db, "Schema Five Mallory");
            using var replaced = IdentityKeys.Generate();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False")) {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    DROP TABLE retired_keys;
                    DROP INDEX users_by_signing_key;
                    CREATE TABLE retired_keys (
                        signing_key BLOB    PRIMARY KEY,
                        user_id     INTEGER NOT NULL,
                        retired_at  INTEGER NOT NULL
                    );
                    INSERT INTO retired_keys (signing_key, user_id, retired_at) VALUES ($replaced, $alice, 1), ($aliceKey, $mallory, 2);
                    DELETE FROM schema_version WHERE version >= 6;
                    """;
                command.Parameters.AddWithValue("$replaced", replaced.SigningPublicKey);
                command.Parameters.AddWithValue("$alice", alice);
                command.Parameters.AddWithValue("$aliceKey", aliceKeys.SigningPublicKey);
                command.Parameters.AddWithValue("$mallory", mallory);
                command.ExecuteNonQuery();
            }

            var migrated = new Database(path);
            Assert.Equal(6L, QueryLong(path, "SELECT MAX(version) FROM schema_version;"));
            Assert.Equal(2L, QueryLong(path, "SELECT COUNT(*) FROM retired_keys;"));
            Assert.True(migrated.IsKeyRetired(alice, replaced.SigningPublicKey));
            Assert.True(migrated.IsKeyRetired(mallory, aliceKeys.SigningPublicKey));
            Assert.False(migrated.IsKeyRetired(alice, aliceKeys.SigningPublicKey));
            var user = migrated.GetUser(alice)!;
            Assert.True(migrated.AddDeviceForKey(alice, user.SigningKey, user.KeyVersion, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));

            // One key may now be retired for several accounts.
            Assert.True(migrated.RetireIdentity(alice, user.SigningKey, user.KeyVersion));
            Assert.Equal(2L, QueryLong(path, "SELECT COUNT(*) FROM retired_keys WHERE signing_key = x'" + Convert.ToHexString(aliceKeys.SigningPublicKey) + "';"));
        } finally {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// Before registrations were signed, anyone could register another user's public key for their own character, so a
    /// database from then (schema 4 or 5) may hold one key for two accounts, or (schema 5) a key retired for an account
    /// other than the one now registered with it. The upgrade leaves them as they are but warns the operator, naming the
    /// accounts and a short fingerprint of the key, never the key itself. A database without them is upgraded quietly.
    /// </summary>
    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void UpgradingWarnsAboutKeysTwoAccountsRegistered(int schema) {
        var (db, directory) = NewDatabase();
        try {
            var path = Path.Combine(directory, "test.db");
            var (alice, aliceKeys) = RegisterUser(db, "Shared Key Alice");
            var (mallory, _) = RegisterUser(db, "Shared Key Mallory");
            var (bob, bobKeys) = RegisterUser(db, "Shared Key Bob");
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False")) {
                connection.Open();
                using var command = connection.CreateCommand();
                // Mallory registered Alice's public key as theirs, as an unsigned registration could. In schema 5, someone
                // also registered Bob's and replaced it, which retired it (for everyone, then) under their own account.
                command.CommandText = """
                    UPDATE users SET signing_key = $aliceKey WHERE user_id = $mallory;
                    DROP TABLE retired_keys;
                    DROP INDEX users_by_signing_key;
                    """ + (schema == 5
                        ? """
                          CREATE TABLE retired_keys (
                              signing_key BLOB    PRIMARY KEY,
                              user_id     INTEGER NOT NULL,
                              retired_at  INTEGER NOT NULL
                          );
                          INSERT INTO retired_keys (signing_key, user_id, retired_at) VALUES ($bobKey, $mallory, 1);
                          DELETE FROM schema_version WHERE version >= 6;
                          """
                        : "DELETE FROM schema_version WHERE version >= 5;");
                command.Parameters.AddWithValue("$aliceKey", aliceKeys.SigningPublicKey);
                command.Parameters.AddWithValue("$bobKey", bobKeys.SigningPublicKey);
                command.Parameters.AddWithValue("$mallory", mallory);
                command.ExecuteNonQuery();
            }

            using var logs = new CapturingLoggerProvider();
            _ = new Database(path, logs.CreateLogger("Database"));
            Assert.Equal(6L, QueryLong(path, "SELECT MAX(version) FROM schema_version;"));
            var warnings = logs.AtLeast(Microsoft.Extensions.Logging.LogLevel.Warning);
            Assert.Equal(schema == 5 ? 2 : 1, warnings.Count);

            var shared = Assert.Single(warnings, w => w.Contains(Database.ShortKeyId(aliceKeys.SigningPublicKey)));
            Assert.Contains(alice.ToString(), shared);
            Assert.Contains(mallory.ToString(), shared);
            Assert.DoesNotContain(bob.ToString(), shared);
            if (schema == 5) {
                var retired = Assert.Single(warnings, w => w.Contains(Database.ShortKeyId(bobKeys.SigningPublicKey)));
                Assert.Contains(bob.ToString(), retired);
                Assert.Contains(mallory.ToString(), retired);
            }

            // Never the keys themselves.
            foreach (var key in new[] { aliceKeys.SigningPublicKey, bobKeys.SigningPublicKey }) {
                Assert.DoesNotContain(warnings, w => w.Contains(Convert.ToHexString(key), StringComparison.OrdinalIgnoreCase) || w.Contains(Convert.ToBase64String(key)));
            }

            // Left as they were: the key is still both accounts', and registering it again is refused while the other has it.
            Assert.Equal(2L, QueryLong(path, "SELECT COUNT(*) FROM users WHERE signing_key = x'" + Convert.ToHexString(aliceKeys.SigningPublicKey) + "';"));

            // Upgrading a database that has none of this says nothing.
            var (clean, cleanDirectory) = NewDatabase();
            try {
                var cleanPath = Path.Combine(cleanDirectory, "test.db");
                RegisterUser(clean, "Clean Alice");
                QueryLong(cleanPath, "DROP TABLE retired_keys; DROP INDEX users_by_signing_key; DELETE FROM schema_version WHERE version >= 5; SELECT 0;");
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                using var quiet = new CapturingLoggerProvider();
                _ = new Database(cleanPath, quiet.CreateLogger("Database"));
                Assert.Empty(quiet.AtLeast(Microsoft.Extensions.Logging.LogLevel.Warning));
            } finally {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                DeleteDirectory(cleanDirectory);
            }
        } finally {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void PreReleaseDatabaseWithChannelsIsRefusedAndLeftAlone() {
        var (db, directory) = NewDatabase();
        try {
            var path = Path.Combine(directory, "test.db");
            var (channelId, _, _) = CreateChannel(db);
            Assert.Equal(6L, QueryLong(path, "SELECT MAX(version) FROM schema_version;"));

            // As if the file were left over from the unreleased schema 1.
            QueryLong(path, "DELETE FROM schema_version WHERE version >= 2; SELECT 0;");
            var error = Assert.Throws<UnsupportedDatabaseException>(() => new Database(path));
            Assert.Contains(Path.GetFullPath(path), error.Message);
            Assert.Contains("delete or move", error.Message);

            // Nothing was deleted or migrated.
            Assert.Equal(1L, QueryLong(path, "SELECT MAX(version) FROM schema_version;"));
            Assert.Equal(1L, QueryLong(path, "SELECT COUNT(*) FROM channels WHERE channel_id = '" + channelId + "';"));
        } finally {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void VersionZeroPointOneDatabaseWithChannelsIsRefusedAndLeftAlone() {
        var (db, directory) = NewDatabase();
        try {
            var path = Path.Combine(directory, "test.db");
            var (channelId, _, _) = CreateChannel(db);

            // As if made by 0.1 (schema 3): its channels have no membership log anyone could sign now.
            QueryLong(path, "DELETE FROM schema_version WHERE version >= 4; SELECT 0;");
            var error = Assert.Throws<UnsupportedDatabaseException>(() => new Database(path));
            Assert.Contains(Path.GetFullPath(path), error.Message);
            Assert.Contains("schema 3", error.Message);
            Assert.Contains("delete or move", error.Message);

            // Nothing was deleted or migrated.
            Assert.Equal(3L, QueryLong(path, "SELECT MAX(version) FROM schema_version;"));
            Assert.Equal(1L, QueryLong(path, "SELECT COUNT(*) FROM channels WHERE channel_id = '" + channelId + "';"));
        } finally {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            DeleteDirectory(directory);
        }
    }

    private static long QueryLong(string path, string sql) {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    [Fact]
    public void AdminTransferFailsIfTargetLeftMeanwhile() {
        var (db, directory) = NewDatabase();
        try {
            var (channelId, admin, keys) = CreateChannel(db);
            var (member, memberKeys) = RegisterUser(db, "Leaving Member");
            AddMember(db, channelId, admin, keys, member, memberKeys);

            // The transfer is made at the log's head, but the member leaves first: it's no longer next.
            var transfer = Next(db, channelId, MembershipEntryKind.TransferAdmin, member, keys, admin);
            Assert.True(db.AppendEntry(channelId, Next(db, channelId, MembershipEntryKind.Leave, member, memberKeys, member)));
            Assert.False(db.AppendEntry(channelId, transfer));

            Assert.Equal(Rank.Admin, db.GetRank(channelId, admin));
            Assert.Null(db.GetRank(channelId, member));
        } finally {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void IdentitiesAreOnlyVisibleToChannelMates() {
        var (db, directory) = NewDatabase();
        try {
            var (channelId, admin, keys) = CreateChannel(db);
            var (invitee, inviteeKeys) = RegisterUser(db, "Invited Person");
            var (stranger, _) = RegisterUser(db, "Total Stranger");
            Assert.True(db.AppendEntry(channelId, Next(db, channelId, MembershipEntryKind.Invite, invitee, keys, admin, MemberKeys.Of(inviteeKeys)), SomeBox(), new byte[64]));

            var adminSees = db.GetVisibleUserIds(admin);
            Assert.Contains(invitee, adminSees);
            Assert.DoesNotContain(stranger, adminSees);

            var inviteeSees = db.GetVisibleUserIds(invitee);
            Assert.Contains(admin, inviteeSees);
            Assert.DoesNotContain(stranger, inviteeSees);
        } finally {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void NameTakeoverKeepsTheOldAccountsMemberships() {
        var (db, directory) = NewDatabase();
        try {
            var (channelId, admin, _) = CreateChannel(db);
            using var keys = IdentityKeys.Generate();

            // A different character (new Lodestone ID) now holds the admin's old name.
            var adminRow = db.GetUser(admin)!;
            db.RegisterUser(admin + 1000, adminRow.Name, 0, adminRow.WorldName, keys.ToBundle(), true);

            Assert.Equal(Rank.Admin, db.GetRank(channelId, admin));
            Assert.Equal(admin + 1000, db.FindUser(adminRow.Name, adminRow.WorldName)!.UserId);

            // The old account no longer shows as the same Name@World as the new one.
            Assert.Equal(adminRow.Name + " (former)", db.GetUser(admin)!.Name);
            Assert.Equal(adminRow.Name, db.GetUser(admin + 1000)!.Name);
        } finally {
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// Replaces "SetRankOnlyChangesCurrentNonAdminMembers": who may change whose rank is now decided by
    /// the log's rules (see MembershipRulesTests), and the database only applies entries checked
    /// against them. What must hold here is that its tables, which the server checks the next entry
    /// against, always agree with replaying its log.
    /// </summary>
    [Fact]
    public void ServerTablesFollowTheLog() {
        var (db, directory) = NewDatabase();
        try {
            var (channelId, admin, keys) = CreateChannel(db);
            var (member, memberKeys) = RegisterUser(db, "Ranked Member");
            var (other, otherKeys) = RegisterUser(db, "Other Member");
            var (invitee, inviteeKeys) = RegisterUser(db, "Still Invited");
            AddMember(db, channelId, admin, keys, member, memberKeys);
            AddMember(db, channelId, admin, keys, other, otherKeys);
            Assert.True(db.AppendEntry(channelId, Next(db, channelId, MembershipEntryKind.Invite, invitee, keys, admin, MemberKeys.Of(inviteeKeys)), SomeBox(), new byte[64]));
            Assert.True(db.AppendEntry(channelId, Next(db, channelId, MembershipEntryKind.SetRank, member, keys, admin, rank: Rank.Moderator)));
            Assert.True(db.AppendEntry(channelId, Next(db, channelId, MembershipEntryKind.Remove, other, memberKeys, member)));
            Assert.True(db.AppendEntry(channelId, Next(db, channelId, MembershipEntryKind.TransferAdmin, member, keys, admin)));

            Assert.Equal(Rank.Moderator, db.GetRank(channelId, admin));
            Assert.Equal(Rank.Admin, db.GetRank(channelId, member));
            Assert.Null(db.GetRank(channelId, other));
            Assert.Equal(Rank.Invited, db.GetRank(channelId, invitee));

            // The old admin invited them; demoted to member, that invite goes too.
            Assert.True(db.AppendEntry(channelId, Next(db, channelId, MembershipEntryKind.SetRank, admin, memberKeys, member, rank: Rank.Member)));
            Assert.Null(db.GetRank(channelId, invitee));

            var replayed = Membership.Empty(channelId);
            foreach (var entry in db.GetLogEntries(channelId, 0, 100)) {
                replayed = replayed.Apply(entry);
            }

            var tables = Membership.Restore(db.GetMembershipCheckpoint(channelId)!);
            Assert.True(MembershipEntries.SamePosition(replayed.Head, tables.Head));
            Assert.Equal(replayed.Members.OrderBy(m => m.UserId), tables.Members.OrderBy(m => m.UserId));
            Assert.Equal(replayed.Invitees.OrderBy(i => i.UserId), tables.Invitees.OrderBy(i => i.UserId));
        } finally {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void TrustedProxiesAcceptAddressesAndNetworks() {
        var options = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions();
        ClientAddresses.AddTrustedProxies(options, ["172.17.0.0/16", "192.0.2.5", "fd00:1::/64"]);

        Assert.Contains(options.KnownIPNetworks, network => network.Contains(IPAddress.Parse("172.17.0.1")));
        Assert.Contains(options.KnownIPNetworks, network => network.Contains(IPAddress.Parse("fd00:1::5")));
        Assert.Contains(IPAddress.Parse("192.0.2.5"), options.KnownProxies);
        Assert.Throws<InvalidOperationException>(() => ClientAddresses.AddTrustedProxies(options, ["not-an-address"]));
        Assert.Throws<InvalidOperationException>(() => ClientAddresses.AddTrustedProxies(options, ["10.0.0.0/99"]));
    }

    [Theory]
    [InlineData("203.0.113.5", "203.0.113.5")]
    [InlineData("::ffff:203.0.113.5", "203.0.113.5")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:3::1", "2001:db8:1:3::/64")]
    public void PerIpLimitsGroupIpv6BySlash64(string address, string key) {
        Assert.Equal(key, ClientAddresses.LimitKey(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task LodestoneQueueRejectsWhenFull() {
        var handler = new BlockingHandler();
        var options = Microsoft.Extensions.Options.Options.Create(new ServerOptions { Lodestone = { BaseUrl = "https://lodestone.test", MinDelaySeconds = 0 } });
        var lodestone = new LodestoneClient(new HttpClient(handler), options, Microsoft.Extensions.Logging.Abstractions.NullLogger<LodestoneClient>.Instance);

        // One request holds the queue; the rest wait (each name is new, so nothing is cached).
        var queued = Enumerable.Range(0, LodestoneClient.MaxWaitingRequests + 1)
            .Select(i => lodestone.FindCharacterAsync($"Queued {i}", "Gilgamesh", Ct))
            .ToList();
        await Assert.ThrowsAsync<LodestoneBusyException>(() => lodestone.FindCharacterAsync("One Too Many", "Gilgamesh", Ct));

        handler.Release.SetResult();
        await Task.WhenAll(queued).WaitAsync(Harness.Timeout, Ct);
        Assert.Null(await lodestone.FindCharacterAsync("After The Rush", "Gilgamesh", Ct));
    }

    [Fact]
    public void ClosedConnectionIsNotKeptAliveByItsLoginTimer() {
        var connection = RunConnectionToCompletion();
        for (var i = 0; i < 10 && connection.IsAlive; i++) {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(connection.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RunConnectionToCompletion() {
        var connection = new ClientConnection(new ClosedWebSocket(), "203.0.113.1", 1024, 4, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        connection.RunAsync((_, _, _) => Task.FromResult(new Response())).GetAwaiter().GetResult();
        return new WeakReference(connection);
    }

    /// <summary>Holds every request until released, then answers with an empty page.</summary>
    private sealed class BlockingHandler : HttpMessageHandler {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            await this.Release.Task.WaitAsync(ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("<html></html>") };
        }
    }

    private static readonly IMembershipProvider Membership = SignedLogMembershipProvider.Instance;

    private static (Database Db, string Directory) NewDatabase() {
        var directory = Path.Combine(Path.GetTempPath(), "lgt-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return (new Database(Path.Combine(directory, "test.db")), directory);
    }

    /// <returns>The user's ID and identity keys (left for the test process to clean up).</returns>
    private static (long Id, IdentityKeys Keys) RegisterUser(Database db, string name) {
        var keys = IdentityKeys.Generate();
        var id = Random.Shared.NextInt64(1, long.MaxValue / 2);
        db.RegisterUser(id, name, 0, ProtocolInfo.DebugWorldName, keys.ToBundle(), true);
        return (id, keys);
    }

    private static (string ChannelId, long Admin, IdentityKeys Keys) CreateChannel(Database db) {
        var (admin, keys) = RegisterUser(db, "Channel Admin " + Guid.NewGuid().ToString("N")[..8]);
        var channelId = Guid.NewGuid().ToString("N");
        var key = ChannelCrypto.NewEpochKey();
        var genesis = Membership.CreateGenesis(channelId, keys, admin, 1);
        var position = MembershipEntries.PositionOf(genesis);
        db.CreateChannel(channelId, genesis,
            ChannelCrypto.SealEpochKey(key, channelId, 0, position, keys, admin, admin, keys.AgreementPublicKey),
            ChannelCrypto.EncryptName("Original", key, channelId, 0, position, keys, admin));
        return (channelId, admin, keys);
    }

    /// <summary>The next entry of the channel's log in the database, made and signed by <paramref name="actorId"/>.</summary>
    private static MembershipEntry Next(Database db, string channelId, MembershipEntryKind kind, long subjectId, IdentityKeys actor, long actorId,
        MemberKeys? inviteeKeys = null, Rank rank = Rank.Unspecified) {
        return Membership.Restore(db.GetMembershipCheckpoint(channelId)!).Create(kind, subjectId, actor, actorId, 1, inviteeKeys, rank);
    }

    private static void AddMember(Database db, string channelId, long inviter, IdentityKeys inviterKeys, long userId, IdentityKeys userKeys) {
        Assert.True(db.AppendEntry(channelId, Next(db, channelId, MembershipEntryKind.Invite, userId, inviterKeys, inviter, MemberKeys.Of(userKeys)), SomeBox(), new byte[64]));
        Assert.True(db.AppendEntry(channelId, Next(db, channelId, MembershipEntryKind.Accept, userId, userKeys, userId)));
    }

    private static SealedBox SomeBox() => new() { EphemeralPublicKey = ByteString.CopyFrom(new byte[32]), Ciphertext = ByteString.CopyFrom(new byte[48]) };
}

public sealed class SecretFileTests {
    [Fact]
    public void ConcurrentStoresForOneFileNeverCollide() {
        var directory = Path.Combine(Path.GetTempPath(), "lgt-files-" + Guid.NewGuid().ToString("N"));
        try {
            var path = Path.Combine(directory, "secrets.json");
            // Two stores for the same file, as when a closing session and a new one overlap.
            var stores = new[] { new FileSecretStore(path), new FileSecretStore(path) };
            Parallel.For(0, 400, i => stores[i % 2].Save(new ClientSecrets { UserId = i }));

            Assert.NotNull(stores[0].Load().UserId);
            Assert.Equal(["secrets.json", "secrets.json.bak"], Directory.GetFiles(directory).Select(Path.GetFileName).Order());
        } finally {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void SavingKeepsThePreviousVersionAsABackup() {
        var directory = Path.Combine(Path.GetTempPath(), "lgt-files-" + Guid.NewGuid().ToString("N"));
        try {
            var path = Path.Combine(directory, "secrets.json");
            var store = new FileSecretStore(path);
            store.Save(new ClientSecrets { UserId = 1 });
            Assert.False(File.Exists(AtomicFile.BackupPath(path)));

            store.Save(new ClientSecrets { UserId = 2 });
            Assert.Equal(2, store.Load().UserId);
            Assert.Equal(1, ClientSecrets.Deserialize(File.ReadAllBytes(AtomicFile.BackupPath(path))).UserId);
        } finally {
            DeleteDirectory(directory);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("garbled")]
    public void DamagedSecretsFileFallsBackToTheBackup(string damage) {
        var directory = Path.Combine(Path.GetTempPath(), "lgt-files-" + Guid.NewGuid().ToString("N"));
        try {
            var path = Path.Combine(directory, "secrets.json");
            var warnings = new List<string>();
            var store = new FileSecretStore(path, warnings.Add);
            store.Save(new ClientSecrets { UserId = 1 });
            store.Save(new ClientSecrets { UserId = 2 });

            // As a power cut mid-save might leave it.
            switch (damage) {
                case "missing":
                    File.Delete(path);
                    break;
                case "empty":
                    File.WriteAllBytes(path, []);
                    break;
                default:
                    File.WriteAllBytes(path, "{\"UserId\": 2, \"Pinned"u8.ToArray());
                    break;
            }

            Assert.Equal(1, store.Load().UserId);
            Assert.Contains("secrets.json.bak", Assert.Single(warnings));
        } finally {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void DamagedSecretsFileWithoutABackupIsAnError() {
        var directory = Path.Combine(Path.GetTempPath(), "lgt-files-" + Guid.NewGuid().ToString("N"));
        try {
            var path = Path.Combine(directory, "secrets.json");
            var store = new FileSecretStore(path);
            store.Save(new ClientSecrets { UserId = 1 });
            File.WriteAllBytes(path, "not json"u8.ToArray());

            // Starting afresh would silently replace the identity and lose every key.
            Assert.ThrowsAny<System.Text.Json.JsonException>(() => store.Load());
            Assert.Null(new FileSecretStore(Path.Combine(directory, "none.json")).Load().UserId);
        } finally {
            DeleteDirectory(directory);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("garbled")]
    public void UnreadableBackupIsNamedInTheError(string damage) {
        var directory = Path.Combine(Path.GetTempPath(), "lgt-files-" + Guid.NewGuid().ToString("N"));
        try {
            var path = Path.Combine(directory, "secrets.json");
            var warnings = new List<string>();
            var store = new FileSecretStore(path, warnings.Add);
            store.Save(new ClientSecrets { UserId = 1 });
            store.Save(new ClientSecrets { UserId = 2 });

            if (damage == "missing") {
                File.Delete(path);
            } else {
                File.WriteAllBytes(path, "not json"u8.ToArray());
            }

            File.WriteAllBytes(AtomicFile.BackupPath(path), "not json either"u8.ToArray());

            // Someone who deleted the file to start afresh needs to know the backup is in the way.
            var error = Assert.ThrowsAny<Exception>(() => store.Load());
            Assert.Contains(AtomicFile.BackupPath(path), error.Message);
            if (damage == "garbled") {
                Assert.Contains(path + " ", error.Message);
            }

            Assert.Empty(warnings);
        } finally {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void FileLockIsReentrant() {
        var directory = Path.Combine(Path.GetTempPath(), "lgt-files-" + Guid.NewGuid().ToString("N"));
        try {
            var path = Path.Combine(directory, "secrets.bin");
            lock (AtomicFile.LockFor(path)) {
                AtomicFile.Write(path, [1, 2, 3]);
            }

            Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
        } finally {
            DeleteDirectory(directory);
        }
    }
}

public sealed class TextSanitizerTests {
    [Theory]
    [InlineData("hello", "hello")]
    [InlineData("a\u0002\u0010\u0003b", "ab")]                 // game macro bytes
    [InlineData("line one\nline two", "line one line two")]
    [InlineData("evil‮txt.exe", "eviltxt.exe")]          // bidi override
    [InlineData("zero​width", "zerowidth")]
    [InlineData(" icon", " icon")]                // game icons (private use) are kept
    [InlineData("emoji 😀 ok", "emoji 😀 ok")]
    public void RemovesControlAndFormatCharacters(string input, string expected) {
        Assert.Equal(expected, TextSanitizer.Clean(input));
    }

    [Fact]
    public void CapsLength() {
        Assert.Equal(TextSanitizer.MaxNameLength + 1, TextSanitizer.Name(new string('x', 500)).Length);
    }
}
