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

    /// <summary>
    /// Within one window, a stream of new addresses (an attacker with many IPv6 networks, say) would still be kept until
    /// the window passed. Past a cap, the counter sweeps at once, and if that isn't enough, forgets the addresses seen
    /// least recently: their limits start afresh, but memory stays bounded.
    /// </summary>
    [Fact]
    public void WindowCountersStayWithinTheirCapWithinOneWindow() {
        var clock = new ManualClock();
        var counter = new WindowCounter(3, TimeSpan.FromHours(1), clock) { MaxKeys = 100 };
        Assert.True(counter.TryAdd("203.0.113.1"));
        Assert.True(counter.TryAdd("203.0.113.1"));
        for (var i = 0; i < 1000; i++) {
            clock.Offset += TimeSpan.FromMilliseconds(10);
            Assert.True(counter.TryAdd($"2001:db8:{i:x}::/64"));
            // Kept busy: the address in use stays, and keeps what it counted.
            if (i % 50 == 0) {
                counter.TryAdd("203.0.113.1");
            }
        }

        Assert.InRange(counter.TrackedKeys, 1, 100);
        Assert.True(counter.IsFull("203.0.113.1"));
    }

    /// <summary>Per-user (and per inviter and invitee) buckets unused for an hour are dropped, so they don't pile up either.</summary>
    [Fact]
    public void RateLimitsForgetKeysUnusedForAnHour() {
        var clock = new ManualClock();
        var limits = new KeyedRateLimits<(long, long)>(perSecond: 1.0 / 60, burst: 20, clock);
        for (var i = 0; i < 300; i++) {
            Assert.True(limits.TryTake((1, i)));
        }

        Assert.Equal(300, limits.TrackedKeys);
        clock.Offset += TimeSpan.FromHours(2);
        Assert.True(limits.TryTake((2, 2)));
        Assert.Equal(1, limits.TrackedKeys);
    }

    /// <summary>
    /// A bucket that takes longer than an hour to fill (an operator's slow invite settings) is kept until it is full, so
    /// dropping it never gives back more than waiting would; and a refusal says how long until the next token.
    /// </summary>
    [Fact]
    public void SlowRateLimitsAreKeptUntilFullAndSayHowLongToWait() {
        var clock = new ManualClock();
        var limits = new UserRateLimits(perSecond: 1.0 / 3600, burst: 2, clock);
        Assert.True(limits.TryTake(1, out var none));
        Assert.Equal(TimeSpan.Zero, none);
        Assert.True(limits.TryTake(1));
        Assert.False(limits.TryTake(1, out var wait));
        Assert.InRange(wait.TotalMinutes, 59, 60.01);

        // An hour and a half on (sweeping others): half full, and still kept.
        clock.Offset += TimeSpan.FromMinutes(90);
        Assert.True(limits.TryTake(2));
        Assert.Equal(2, limits.TrackedKeys);
        Assert.True(limits.TryTake(1));
        Assert.False(limits.TryTake(1, out wait));
        Assert.InRange(wait.TotalMinutes, 29, 30.01);

        // Long after it is full, it goes.
        clock.Offset += TimeSpan.FromHours(3);
        Assert.True(limits.TryTake(3));
        Assert.Equal(1, limits.TrackedKeys);
    }

    /// <summary>
    /// The Lodestone client caches every character it looks up (an hour if found, ten minutes if not). Every registration
    /// attempt names one, so expired results are dropped, and the cache has a cap.
    /// </summary>
    [Fact]
    public async Task LodestoneSearchesAreForgottenAndCapped() {
        var clock = new ManualClock();
        var options = Microsoft.Extensions.Options.Options.Create(new LookingGlass.Server.ServerOptions { Lodestone = { BaseUrl = FakeLodestone.BaseUrl, MinDelaySeconds = 0 } });
        var lodestone = new LodestoneClient(new HttpClient(new FakeLodestone { Name = "Nobody Matches" }), options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LodestoneClient>.Instance, clock) { MaxCachedSearches = 50 };

        for (var i = 0; i < 40; i++) {
            Assert.Null(await lodestone.FindCharacterAsync($"Seeker {i}", "Gilgamesh", Ct));
        }

        Assert.Equal(40, lodestone.CachedSearches);

        // Long after: they have all expired, and the next search sweeps them out.
        clock.Offset += TimeSpan.FromHours(2);
        Assert.Null(await lodestone.FindCharacterAsync("Later Seeker", "Gilgamesh", Ct));
        Assert.Equal(1, lodestone.CachedSearches);

        // However many names are searched at once, no more than the cap are kept.
        for (var i = 0; i < 200; i++) {
            await lodestone.FindCharacterAsync($"Flood {i}", "Gilgamesh", Ct);
        }

        Assert.InRange(lodestone.CachedSearches, 1, 50);
    }

    private void SetLastUsed(byte[] token, DateTimeOffset when) {
        this._db.SetDeviceLastUsedForTests(token, when);
    }

    private static byte[] RandomToken() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
}
