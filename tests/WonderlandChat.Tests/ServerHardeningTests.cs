using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using Google.Protobuf;
using WonderlandChat.Core.Client;
using WonderlandChat.Core.Crypto;
using WonderlandChat.Core.Util;
using WonderlandChat.Protocol;
using WonderlandChat.Server;
using WonderlandChat.Server.Data;
using WonderlandChat.Server.Hosting;
using WonderlandChat.Server.Realtime;
using WonderlandChat.Server.Services;
using static WonderlandChat.Tests.Harness;

namespace WonderlandChat.Tests;

/// <summary>Server-side guards: debug-account gating, frame limits, database race guards, identity visibility.</summary>
public sealed class ServerHardeningTests {
    [Fact]
    public async Task DebugAccountsStopWorkingWhenDisabled() {
        var directory = Path.Combine(Path.GetTempPath(), "wct-" + Guid.NewGuid().ToString("N"));
        var store = new InMemorySecretStore();
        try {
            await using (var enabled = new Harness(directory)) {
                await enabled.RegisterAsync("Debug Gate", store);
            }

            await using var disabled = new Harness(directory, allowDebugAccounts: false);

            // The existing debug account's token is refused...
            var returning = disabled.StartClient("Debug Gate", store);
            await WaitFor(() => returning.Session.Snapshot.State == ConnectionState.Unregistered ? new object() : null);
            Assert.False(returning.Session.Snapshot.DebugAccountsEnabled);

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
            var name = ChannelCrypto.EncryptName("Renamed", ChannelCrypto.NewEpochKey(), channelId, 0, keys, admin, revision: 1);

            Assert.True(db.RenameChannel(channelId, name));
            Assert.Equal(1UL, db.GetChannel(channelId)!.Name!.Revision);
            Assert.False(db.RenameChannel(channelId, ChannelCrypto.EncryptName("Stale", ChannelCrypto.NewEpochKey(), channelId, 5, keys, admin, revision: 2)));

            // A revision that isn't newer than the stored one (a replay, or a stale client) is refused.
            Assert.False(db.RenameChannel(channelId, name));
            Assert.False(db.RenameChannel(channelId, ChannelCrypto.EncryptName("Older", ChannelCrypto.NewEpochKey(), channelId, 0, keys, admin)));
            Assert.True(db.RenameChannel(channelId, ChannelCrypto.EncryptName("Newer", ChannelCrypto.NewEpochKey(), channelId, 0, keys, admin, revision: 2)));

            var other = RegisterUser(db, "Other User");
            db.AddInvite(channelId, other, admin, new SealedBox { EphemeralPublicKey = ByteString.CopyFrom(new byte[32]), Ciphertext = ByteString.CopyFrom(new byte[48]) }, new byte[64]);
            Assert.True(db.AcceptInvite(channelId, other));
            Assert.True(db.GetChannel(channelId)!.RekeyPending);
            Assert.False(db.RenameChannel(channelId, name));
        } finally {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void SchemaTwoDatabaseGainsNameSources() {
        var (db, directory) = NewDatabase();
        try {
            var path = Path.Combine(directory, "test.db");
            var (channelId, _, _) = CreateChannel(db);

            // As if made before rekeys said which name they carry over.
            QueryLong(path, """
                ALTER TABLE channels DROP COLUMN name_source_epoch;
                ALTER TABLE channels DROP COLUMN name_source_revision;
                DELETE FROM schema_version WHERE version = 3;
                SELECT 0;
                """);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            var migrated = new Database(path);
            Assert.Equal(3L, QueryLong(path, "SELECT MAX(version) FROM schema_version;"));
            Assert.Null(migrated.GetChannel(channelId)!.Name!.CarriedFrom);
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
            Assert.Equal(3L, QueryLong(path, "SELECT MAX(version) FROM schema_version;"));

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
            var (channelId, admin, _) = CreateChannel(db);
            var member = RegisterUser(db, "Leaving Member");
            db.AddInvite(channelId, member, admin, new SealedBox { EphemeralPublicKey = ByteString.CopyFrom(new byte[32]), Ciphertext = ByteString.CopyFrom(new byte[48]) }, new byte[64]);
            db.AcceptInvite(channelId, member);
            db.RemoveMember(channelId, member);

            Assert.False(db.TransferAdmin(channelId, admin, member));
            Assert.Equal(Rank.Admin, db.GetRank(channelId, admin));
        } finally {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void IdentitiesAreOnlyVisibleToChannelMates() {
        var (db, directory) = NewDatabase();
        try {
            var (channelId, admin, _) = CreateChannel(db);
            var invitee = RegisterUser(db, "Invited Person");
            var stranger = RegisterUser(db, "Total Stranger");
            db.AddInvite(channelId, invitee, admin, new SealedBox { EphemeralPublicKey = ByteString.CopyFrom(new byte[32]), Ciphertext = ByteString.CopyFrom(new byte[48]) }, new byte[64]);

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

    [Fact]
    public void SetRankOnlyChangesCurrentNonAdminMembers() {
        var (db, directory) = NewDatabase();
        try {
            var (channelId, admin, _) = CreateChannel(db);
            var member = RegisterUser(db, "Ranked Member");
            db.AddInvite(channelId, member, admin, new SealedBox { EphemeralPublicKey = ByteString.CopyFrom(new byte[32]), Ciphertext = ByteString.CopyFrom(new byte[48]) }, new byte[64]);
            Assert.False(db.SetRank(channelId, member, Rank.Moderator));
            db.AcceptInvite(channelId, member);

            Assert.True(db.SetRank(channelId, member, Rank.Moderator));
            Assert.False(db.SetRank(channelId, member, Rank.Moderator));
            Assert.False(db.SetRank(channelId, admin, Rank.Member));
            Assert.Equal(Rank.Admin, db.GetRank(channelId, admin));

            db.RemoveMember(channelId, member);
            Assert.False(db.SetRank(channelId, member, Rank.Member));
            Assert.Null(db.GetRank(channelId, member));
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

    private static (Database Db, string Directory) NewDatabase() {
        var directory = Path.Combine(Path.GetTempPath(), "wct-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return (new Database(Path.Combine(directory, "test.db")), directory);
    }

    private static long RegisterUser(Database db, string name) {
        using var keys = IdentityKeys.Generate();
        var id = Random.Shared.NextInt64(1, long.MaxValue / 2);
        db.RegisterUser(id, name, 0, ProtocolInfo.DebugWorldName, keys.ToBundle(), true);
        return id;
    }

    private static (string ChannelId, long Admin, IdentityKeys Keys) CreateChannel(Database db) {
        var keys = IdentityKeys.Generate();
        var admin = Random.Shared.NextInt64(1, long.MaxValue / 2);
        db.RegisterUser(admin, "Channel Admin " + admin, 0, ProtocolInfo.DebugWorldName, keys.ToBundle(), true);
        var channelId = Guid.NewGuid().ToString("N");
        var key = ChannelCrypto.NewEpochKey();
        db.CreateChannel(channelId, admin,
            ChannelCrypto.SealEpochKey(key, channelId, 0, keys, admin, admin, keys.AgreementPublicKey),
            ChannelCrypto.EncryptName("Original", key, channelId, 0, keys, admin));
        return (channelId, admin, keys);
    }
}

public sealed class SecretFileTests {
    [Fact]
    public void ConcurrentStoresForOneFileNeverCollide() {
        var directory = Path.Combine(Path.GetTempPath(), "wct-files-" + Guid.NewGuid().ToString("N"));
        try {
            var path = Path.Combine(directory, "secrets.json");
            // Two stores for the same file, as when a closing session and a new one overlap.
            var stores = new[] { new FileSecretStore(path), new FileSecretStore(path) };
            Parallel.For(0, 400, i => stores[i % 2].Save(new ClientSecrets { UserId = i }));

            Assert.NotNull(stores[0].Load().UserId);
            Assert.Equal(["secrets.json"], Directory.GetFiles(directory).Select(Path.GetFileName));
        } finally {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void FileLockIsReentrant() {
        var directory = Path.Combine(Path.GetTempPath(), "wct-files-" + Guid.NewGuid().ToString("N"));
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
