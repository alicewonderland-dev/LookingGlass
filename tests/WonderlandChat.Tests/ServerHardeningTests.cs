using System.Net.WebSockets;
using Google.Protobuf;
using WonderlandChat.Core.Client;
using WonderlandChat.Core.Crypto;
using WonderlandChat.Core.Util;
using WonderlandChat.Protocol;
using WonderlandChat.Server.Data;
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
        } finally {
            DeleteDirectory(directory);
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
