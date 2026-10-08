using Microsoft.Extensions.Logging;
using LookingGlass.Server;
using LookingGlass.Server.Data;
using LookingGlass.Server.Services;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// Noticing abuse (see <see cref="AbuseMonitor"/>): refusals by limits are counted per account and per address (an IPv6
/// one per /64 and per /56), and someone refused in most minutes of an hour, or by several different limits within a few
/// minutes, is flagged: one warning in the log and an entry the operator lists with <c>--bans</c>.
/// </summary>
public sealed class AbuseFlagTests : IDisposable {
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lgt-abuse-" + Guid.NewGuid().ToString("N"));
    private readonly Database _db;
    private readonly StoppedClock _clock = new();
    private readonly CapturingLoggerProvider _logs = new();

    public AbuseFlagTests() {
        Directory.CreateDirectory(this._directory);
        this._db = new Database(Path.Combine(this._directory, "test.db"));
    }

    public void Dispose() => DeleteDirectory(this._directory);

    private AbuseMonitor NewMonitor(Action<AbuseOptions>? configure = null, string[]? trustedProxies = null) {
        var settings = new ServerOptions { TrustedProxies = trustedProxies ?? [] };
        configure?.Invoke(settings.Abuse);
        var options = Microsoft.Extensions.Options.Options.Create(settings);
        var logger = LoggerFactory.Create(logging => logging.AddProvider(this._logs)).CreateLogger<AbuseMonitor>();
        return new AbuseMonitor(this._db, new BanList(this._db, options, this._clock), options, logger, this._clock);
    }

    private long Now => this._clock.GetUtcNow().ToUnixTimeSeconds();

    private IReadOnlyList<string> Warnings => this._logs.AtLeast(LogLevel.Warning);

    private List<string> Flagged() => this._db.GetFlags(this.Now - 86400).Select(flag => flag.Subject).Order().ToList();

    /// <summary>
    /// Refused in 30 different minutes of an hour (by default): flagged, account and address, each with one warning that names
    /// the limits and the user ID, never a name. Refused in 29: not.
    /// </summary>
    [Fact]
    public void RefusedInMostMinutesOfAnHourIsFlaggedOnce() {
        var monitor = this.NewMonitor();
        for (var minute = 0; minute < 29; minute++) {
            for (var i = 0; i < 5; i++) {
                monitor.Refused("SendMessage", 42, "203.0.113.9");
            }

            this._clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Empty(this.Flagged());
        Assert.Empty(this.Warnings);

        monitor.Refused("LookupBurst", 42, "203.0.113.9");
        Assert.Equal(["address 203.0.113.9", "user 42"], this.Flagged());
        Assert.Equal(2, this.Warnings.Count);
        var user = Assert.Single(this.Warnings, warning => warning.Contains("user 42"));
        Assert.Contains("30 of the last 60 minutes", user);
        Assert.Contains("SendMessage", user);
        Assert.Contains("LookupBurst", user);
        Assert.Equal("LookupBurst, SendMessage", this._db.GetFlag("user 42")!.Limits);

        // Refused on: still flagged, and no more warnings.
        for (var minute = 0; minute < 30; minute++) {
            monitor.Refused("SendMessage", 42, "203.0.113.9");
            this._clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(2, this.Warnings.Count);
        Assert.True(this._db.GetFlag("user 42")!.LastRefusedAt >= this.Now - 600);
    }

    /// <summary>
    /// A shared address or a plugin with a bug, refused now and then (here, every third minute, for hours) is never flagged:
    /// the hour slides along, and only what is in it counts.
    /// </summary>
    [Fact]
    public void RefusalsNowAndThenAreNeverFlagged() {
        var monitor = this.NewMonitor();
        for (var minute = 0; minute < 5 * 60; minute++) {
            if (minute % 3 == 0) {
                monitor.Refused("KeyLoginsPerHourPerIp", null, "198.51.100.7");
                monitor.Refused("StartKeyLogin", 7, "198.51.100.7");
            }

            this._clock.Advance(TimeSpan.FromMinutes(1));
        }

        // Twenty-five minutes, an hour's gap, twenty-five more: never 30 in one hour.
        for (var round = 0; round < 2; round++) {
            for (var minute = 0; minute < 25; minute++) {
                monitor.Refused("SendMessage", 8, null);
                this._clock.Advance(TimeSpan.FromMinutes(1));
            }

            this._clock.Advance(TimeSpan.FromMinutes(61));
        }

        Assert.Empty(this.Flagged());
        Assert.Empty(this.Warnings);
    }

    /// <summary>Refused by 4 different limits within 10 minutes (by default): flagged. By 3, or by 4 spread over 20 minutes: not.</summary>
    [Fact]
    public void RefusedBySeveralLimitsWithinMinutesIsFlagged() {
        var monitor = this.NewMonitor();
        foreach (var limit in new[] { "LookupBurst", "InviteBurstPerInviter", "SendMessage", "CreateChannel" }) {
            monitor.Refused(limit, 1, null);
            this._clock.Advance(TimeSpan.FromMinutes(6));
        }

        foreach (var limit in new[] { "LookupBurst", "InviteBurstPerInviter", "SendMessage" }) {
            monitor.Refused(limit, 2, null);
        }

        Assert.Empty(this.Flagged());
        monitor.Refused("RenameChannel", 2, null);
        Assert.Equal(["user 2"], this.Flagged());
        var warning = Assert.Single(this.Warnings);
        Assert.Contains("4 different limits within 10 minutes", warning);
        Assert.Contains("RenameChannel", warning);
    }

    /// <summary>
    /// An IPv6 client is counted per /64 (what one usually has) and per /56 (what one customer usually has): one that
    /// moves between /64s is flagged by its /56.
    /// </summary>
    [Fact]
    public void Ipv6IsCountedPerSlash64AndPerSlash56() {
        var monitor = this.NewMonitor();
        for (var minute = 0; minute < 30; minute++) {
            monitor.Refused("ConnectionsPerMinutePerIp", null, minute % 2 == 0 ? "2001:db8:1:2::/64" : "2001:db8:1:3::/64");
            this._clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(["address 2001:db8:1::/56"], this.Flagged());
        Assert.Contains("2001:db8:1::/56", Assert.Single(this.Warnings));
    }

    /// <summary>
    /// A flag lasts 24 hours (by default) after its last refusal, then expires by itself: it isn't listed, a sweep deletes it,
    /// and being flagged again warns again. A server started again meanwhile doesn't warn twice for a flag still in force.
    /// </summary>
    [Fact]
    public void FlagsExpireByThemselvesAndAreNotRepeatedMeanwhile() {
        var monitor = this.NewMonitor(abuse => abuse.FlagAfterMinutesRefused = 1);
        monitor.Refused("LookupBurst", 42, null);
        Assert.Equal(["user 42"], this.Flagged());
        Assert.Single(this.Warnings);

        // Restarted: the flag is in the database, so it isn't announced again.
        this._clock.Advance(TimeSpan.FromHours(2));
        monitor = this.NewMonitor(abuse => abuse.FlagAfterMinutesRefused = 1);
        monitor.Refused("LookupBurst", 42, null);
        Assert.Single(this.Warnings);
        Assert.Equal(this.Now, this._db.GetFlag("user 42")!.LastRefusedAt);

        this._clock.Advance(TimeSpan.FromHours(23));
        Assert.Equal(["user 42"], this.Flagged());
        this._clock.Advance(TimeSpan.FromHours(2));
        Assert.Empty(this.Flagged());

        monitor.Refused("LookupBurst", 42, null);
        Assert.Equal(2, this.Warnings.Count);
    }

    /// <summary>Memory stays bounded: at most <see cref="AbuseOptions.MaxTrackedKeys"/> accounts and addresses are counted at once.</summary>
    [Fact]
    public void TrackedKeysAreCapped() {
        var monitor = this.NewMonitor(abuse => abuse.MaxTrackedKeys = 1000);
        for (var i = 0; i < 5000; i++) {
            monitor.Refused("ConnectionsPerIp", null, $"10.{i / 65536 % 256}.{i / 256 % 256}.{i % 256}");
        }

        Assert.InRange(monitor.TrackedKeys, 1, 1000);
    }

    /// <summary>No automatic block unless the operator turns it on: flagging only tells.</summary>
    [Fact]
    public void NothingIsBlockedAutomaticallyByDefault() {
        var monitor = this.NewMonitor();
        for (var i = 0; i < 20_000; i++) {
            monitor.Refused("ConnectionsPerMinutePerIp", null, "203.0.113.66");
        }

        Assert.Empty(this._db.GetActiveBans(this.Now));
    }

    /// <summary>
    /// With <see cref="AbuseOptions.AutoBlockMinutes"/> set, an address far past the threshold (that many refusals within the
    /// window) is blocked for that long, by itself: only an address, never an account, and never over a ban the operator made.
    /// </summary>
    [Fact]
    public void AnAddressFarPastTheThresholdCanBeBlockedForAFewMinutes() {
        var monitor = this.NewMonitor(abuse => {
            abuse.AutoBlockMinutes = 15;
            abuse.AutoBlockAfterRefusals = 100;
        });
        for (var i = 0; i < 99; i++) {
            monitor.Refused("SendMessage", 42, "203.0.113.66");
        }

        Assert.Empty(this._db.GetActiveBans(this.Now));
        monitor.Refused("SendMessage", 42, "203.0.113.66");
        var ban = Assert.Single(this._db.GetActiveBans(this.Now));
        Assert.Equal(("203.0.113.66", true, this.Now + 15 * 60), (ban.Address, ban.Automatic, ban.ExpiresAt));
        Assert.Contains(this.Warnings, warning => warning.Contains("203.0.113.66") && warning.Contains("15 minutes"));

        // An operator's ban on an address isn't cut short by one made automatically.
        this._db.AddBan(null, "198.51.100.0/24", "Flooding", null, false, this.Now, out _);
        for (var i = 0; i < 200; i++) {
            monitor.Refused("SendMessage", null, "198.51.100.5");
        }

        Assert.Null(this._db.GetActiveBans(this.Now).Single(row => row.Address == "198.51.100.0/24").ExpiresAt);
        Assert.DoesNotContain(this._db.GetActiveBans(this.Now), row => row.Address == "198.51.100.5");
    }

    /// <summary>
    /// Refusals from the server's own or its proxy's address (here loopback, and a trusted proxy) are every player's when the
    /// forwarded client address is missing: that address is never flagged or blocked, and the warning says what is wrong
    /// rather than suggesting a ban. The account is still counted and flagged.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::/64")]
    [InlineData("10.0.0.5")]
    public void TheProxysAddressIsNeverFlaggedOrBlocked(string address) {
        var monitor = this.NewMonitor(abuse => {
            abuse.FlagAfterMinutesRefused = 1;
            abuse.AutoBlockMinutes = 15;
            abuse.AutoBlockAfterRefusals = 100;
        }, trustedProxies: ["10.0.0.5"]);
        for (var i = 0; i < 500; i++) {
            monitor.Refused("SendMessage", 42, address);
        }

        Assert.Equal(["user 42"], this.Flagged());
        Assert.Empty(this._db.GetActiveBans(this.Now));
        var proxy = Assert.Single(this.Warnings, warning => !warning.Contains("user 42"));
        Assert.Contains("this is the proxy's address: the forwarded client address is missing", proxy);
        Assert.DoesNotContain("--ban", proxy);
    }

    [Fact]
    public void SettingsOutOfRangeAreRefused() {
        Assert.Null(new AbuseOptions().Problem());
        Assert.Contains("WindowMinutes", new AbuseOptions { WindowMinutes = 5 }.Problem());
        Assert.Contains("FlagAfterMinutesRefused", new AbuseOptions { FlagAfterMinutesRefused = 61 }.Problem());
        Assert.Contains("FlagAfterMinutesRefused", new AbuseOptions { FlagAfterMinutesRefused = 0 }.Problem());
        Assert.Contains("FlagAfterLimits", new AbuseOptions { FlagAfterLimits = 1 }.Problem());
        Assert.Contains("FlagLimitsWithinMinutes", new AbuseOptions { FlagLimitsWithinMinutes = 61 }.Problem());
        Assert.Contains("FlagExpiresAfterHours", new AbuseOptions { FlagExpiresAfterHours = 0 }.Problem());
        Assert.Contains("MaxTrackedKeys", new AbuseOptions { MaxTrackedKeys = 10 }.Problem());
        Assert.Contains("AutoBlockMinutes", new AbuseOptions { AutoBlockMinutes = -1 }.Problem());
        Assert.Contains("AutoBlockAfterRefusals", new AbuseOptions { AutoBlockAfterRefusals = 10 }.Problem());
        Assert.Contains("BanCheckSeconds", new AbuseOptions { BanCheckSeconds = 61 }.Problem());
        Assert.Contains("BanHistoryDays", new AbuseOptions { BanHistoryDays = 0 }.Problem());
        Assert.Null(new AbuseOptions { WindowMinutes = 1440, FlagAfterMinutesRefused = 1440, FlagLimitsWithinMinutes = 1440 }.Problem());
    }

    /// <summary>A server with an abuse setting out of range doesn't start, and says why.</summary>
    [Fact]
    public async Task AServerWithAbuseSettingsOutOfRangeDoesntStart() {
        var logs = new CapturingLoggerProvider();
        await ExitCodeGate.WaitAsync(Ct);
        var exitCode = Environment.ExitCode;
        var directory = Path.Combine(Path.GetTempPath(), "lgt-" + Guid.NewGuid().ToString("N"));
        try {
            await Assert.ThrowsAnyAsync<Exception>(async () => {
                await using var server = new Harness(directory, logs: logs, settings: ("LookingGlass:Abuse:FlagAfterMinutesRefused", "0"));
                await using var raw = await server.ConnectRawAsync();
            });
            var critical = Assert.Single(logs.AtLeast(LogLevel.Critical));
            Assert.Contains("LookingGlass:Abuse:FlagAfterMinutesRefused", critical);
            Assert.Contains("won't start", critical);
            Assert.Equal(1, Environment.ExitCode);
        } finally {
            Environment.ExitCode = exitCode;
            ExitCodeGate.Release();
            DeleteDirectory(directory);
        }
    }
}
