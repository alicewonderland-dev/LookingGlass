using LookingGlass.Core.Crypto;
using LookingGlass.Protocol;
using LookingGlass.Server.Data;
using LookingGlass.Server.Services;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>What the server keeps per user and per address can't grow without bound.</summary>
public sealed class ServerGrowthTests : IDisposable {
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lgt-growth-" + Guid.NewGuid().ToString("N"));
    private readonly Database _db;

    public ServerGrowthTests() {
        Directory.CreateDirectory(this._directory);
        this._db = new Database(Path.Combine(this._directory, "test.db"));
    }

    public void Dispose() => DeleteDirectory(this._directory);

    /// <summary>
    /// Every key login adds a device, so a client whose logins keep getting lost (or someone holding the key) would add
    /// rows forever. Each user keeps the most recently used <see cref="Database.MaxDevicesPerUser"/>; adding one prunes
    /// the least recently used, in the same transaction.
    /// </summary>
    [Fact]
    public void DevicesPerUserAreCappedKeepingTheMostRecentlyUsed() {
        using var keys = IdentityKeys.Generate();
        var (user, _) = this._db.RegisterUser(-77, "Alice Devices", 0, ProtocolInfo.DebugWorldName, keys.ToBundle(), true);
        var tokens = Enumerable.Range(0, Database.MaxDevicesPerUser).Select(_ => RandomToken()).ToList();
        foreach (var token in tokens) {
            this._db.AddDevice(user.UserId, token);
        }

        // Make them used in order, the first one longest ago; then the first is used again, now.
        for (var i = 0; i < tokens.Count; i++) {
            this.SetLastUsed(tokens[i], DateTimeOffset.UtcNow.AddDays(-30).AddMinutes(i));
        }

        Assert.Equal(user.UserId, this._db.FindDevice(tokens[0]));

        // Five more, some through key login: the five least recently used go, and nothing else.
        var added = Enumerable.Range(0, 5).Select(_ => RandomToken()).ToList();
        for (var i = 0; i < added.Count; i++) {
            if (i % 2 == 0) {
                this._db.AddDevice(user.UserId, added[i]);
            } else {
                Assert.True(this._db.AddDeviceForKey(user.UserId, user.SigningKey, user.KeyVersion, added[i]));
            }
        }

        Assert.Equal(Database.MaxDevicesPerUser, this._db.CountDevices(user.UserId));
        Assert.Equal(user.UserId, this._db.FindDevice(tokens[0]));
        foreach (var gone in tokens.Skip(1).Take(5)) {
            Assert.Null(this._db.FindDevice(gone));
        }

        foreach (var kept in tokens.Skip(6).Concat(added)) {
            Assert.Equal(user.UserId, this._db.FindDevice(kept));
        }

        // Another user's devices aren't touched.
        using var otherKeys = IdentityKeys.Generate();
        var (other, _) = this._db.RegisterUser(-78, "Bob Devices", 0, ProtocolInfo.DebugWorldName, otherKeys.ToBundle(), true);
        var bobs = RandomToken();
        this._db.AddDevice(other.UserId, bobs);
        Assert.Equal(other.UserId, this._db.FindDevice(bobs));
        Assert.Equal(Database.MaxDevicesPerUser, this._db.CountDevices(user.UserId));
    }

    /// <summary>
    /// Per-address counters (registrations, key login challenges and failures) used to keep an entry for every address
    /// ever seen. Entries whose events have all expired are dropped.
    /// </summary>
    [Fact]
    public void WindowCountersForgetAddressesWhoseEventsExpired() {
        var clock = new ManualClock();
        var counter = new WindowCounter(3, TimeSpan.FromMinutes(10), clock);
        for (var i = 0; i < 500; i++) {
            Assert.True(counter.TryAdd($"198.51.100.{i}"));
        }

        Assert.Equal(500, counter.TrackedKeys);

        // Long after: the next use sweeps out everything expired, and only the address in use is tracked.
        clock.Offset += TimeSpan.FromHours(2);
        Assert.True(counter.TryAdd("203.0.113.1"));
        Assert.Equal(1, counter.TrackedKeys);

        // A key still within its window is kept, and still counted.
        Assert.True(counter.TryAdd("203.0.113.1"));
        Assert.True(counter.TryAdd("203.0.113.1"));
        clock.Offset += TimeSpan.FromMinutes(20);
        Assert.False(counter.IsFull("203.0.113.1"));
        Assert.True(counter.TryAdd("203.0.113.2"));
        Assert.Equal(1, counter.TrackedKeys);
    }

    [Fact]
    public void ASweptAddressStartsAfresh() {
        var clock = new ManualClock();
        var counter = new WindowCounter(2, TimeSpan.FromMinutes(10), clock);
        Assert.True(counter.TryAdd("203.0.113.9"));
        Assert.True(counter.TryAdd("203.0.113.9"));
        Assert.False(counter.TryAdd("203.0.113.9"));
        Assert.True(counter.IsFull("203.0.113.9"));

        // Still full a minute later; free again once the window has passed.
        clock.Offset += TimeSpan.FromMinutes(1);
        Assert.False(counter.TryAdd("203.0.113.9"));
        clock.Offset += TimeSpan.FromHours(1);
        Assert.True(counter.TryAdd("203.0.113.9"));
    }

    private void SetLastUsed(byte[] token, DateTimeOffset when) {
        this._db.SetDeviceLastUsedForTests(token, when);
    }

    private static byte[] RandomToken() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
}
