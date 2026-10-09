using LookingGlass.Core.Crypto;
using LookingGlass.Protocol;
using LookingGlass.Server;
using LookingGlass.Server.Data;
using LookingGlass.Server.Hosting;
using LookingGlass.Server.Services;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// Bans as the operator makes them: kept in the database (schema 10), the forms an address or prefix can take, the
/// <c>--ban</c>, <c>--unban</c> and <c>--bans</c> commands, and the running server's cached list of them. What a ban does to
/// a player is in <see cref="BanEnforcementTests"/>.
/// </summary>
public sealed class BanTests : IDisposable {
    private static readonly DateTimeOffset Start = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lgt-ban-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private readonly Database _db;

    public BanTests() {
        Directory.CreateDirectory(this._directory);
        this._path = Path.Combine(this._directory, "lookingglass.db");
        this._db = new Database(this._path);
    }

    public void Dispose() => DeleteDirectory(this._directory);

    private long Now => Start.ToUnixTimeSeconds();

    // ================================================================ the database

    /// <summary>A ban is kept until it is lifted or ends; then it is history, kept for a while, then deleted.</summary>
    [Fact]
    public void BansAreKeptUntilLiftedOrEndedThenKeptAsHistoryForAWhile() {
        var forever = this._db.AddBan(31337, null, "Spamming invites", null, false, this.Now, out var replaced);
        Assert.Equal(0, replaced);
        var week = this._db.AddBan(null, "203.0.113.0/24", "", this.Now + 7 * 86400, false, this.Now, out _);

        Assert.Equal([forever.BanId, week.BanId], this._db.GetActiveBans(this.Now).Select(ban => ban.BanId).Order());
        Assert.Equal("Spamming invites", this._db.GetActiveBans(this.Now).Single(ban => ban.UserId == 31337).Reason);

        // A week on, the address's has ended; the character's lasts until lifted.
        var later = this.Now + 7 * 86400;
        Assert.Equal([forever.BanId], this._db.GetActiveBans(later).Select(ban => ban.BanId));
        Assert.Equal([week.BanId], this._db.GetBanHistory(later).Select(ban => ban.BanId));

        Assert.Equal(1, this._db.LiftBans(31337, null, later));
        Assert.Empty(this._db.GetActiveBans(later));
        Assert.Equal(0, this._db.LiftBans(31337, null, later));
        var lifted = this._db.GetBanHistory(later).Single(ban => ban.BanId == forever.BanId);
        Assert.Equal(later, lifted.LiftedAt);

        // History is kept 90 days after a ban ends or is lifted, then deleted.
        Assert.Equal(0, this._db.SweepBans(later + 89 * 86400, historyDays: 90));
        Assert.Equal(2, this._db.SweepBans(later + 91 * 86400, historyDays: 90));
        Assert.Empty(this._db.GetBanHistory(later + 91 * 86400));
    }

    /// <summary>Banning someone banned already replaces their ban (the old one becomes history); a ban in force is never swept.</summary>
    [Fact]
    public void BanningAgainReplacesTheBanAndBansInForceAreNeverSwept() {
        this._db.AddBan(31337, null, "First", this.Now + 86400, false, this.Now, out _);
        var second = this._db.AddBan(31337, null, "Second", null, false, this.Now + 60, out var replaced);
        Assert.Equal(1, replaced);
        Assert.Equal("Second", Assert.Single(this._db.GetActiveBans(this.Now + 60)).Reason);
        Assert.Single(this._db.GetBanHistory(this.Now + 60));

        Assert.Equal(1, this._db.SweepBans(this.Now + 400 * 86400, historyDays: 90));
        Assert.Equal(second.BanId, Assert.Single(this._db.GetActiveBans(this.Now + 400 * 86400)).BanId);
    }

    /// <summary>Flags are kept by subject, updated in place, listed while they last, and swept once they have expired.</summary>
    [Fact]
    public void FlagsAreKeptBySubjectAndSweptOnceExpired() {
        this._db.SaveFlag(new FlagRow("user 42", 42, null, this.Now, this.Now, "LookupBurst", "refused by limits in 30 of the last 60 minutes"));
        this._db.SaveFlag(new FlagRow("address 203.0.113.9", null, "203.0.113.9", this.Now, this.Now, "ConnectionsPerIp", "why"));
        this._db.SaveFlag(new FlagRow("user 42", 42, null, this.Now, this.Now + 600, "LookupBurst, SendMessage", "refused by 4 different limits within 10 minutes"));

        var flag = this._db.GetFlag("user 42")!;
        Assert.Equal((this.Now, this.Now + 600, "LookupBurst, SendMessage"), (flag.FirstFlaggedAt, flag.LastRefusedAt, flag.Limits));
        Assert.Equal(2, this._db.GetFlags(refusedSince: this.Now).Count);
        Assert.Equal(["user 42"], this._db.GetFlags(refusedSince: this.Now + 1).Select(row => row.Subject));

        Assert.Equal(1, this._db.SweepFlags(refusedBefore: this.Now + 1));
        Assert.Null(this._db.GetFlag("address 203.0.113.9"));
        Assert.NotNull(this._db.GetFlag("user 42"));
    }

    /// <summary>
    /// A database from before bans (schema 9) gains them in place, everything else as it was, and opening it again changes
    /// nothing more.
    /// </summary>
    [Fact]
    public void Schema9DatabaseGainsBansAndFlags() {
        using var keys = IdentityKeys.Generate();
        this._db.RegisterUser(77, "Kept User", 0, ProtocolInfo.DebugWorldName, keys.ToBundle(), true);
        Execute(this._path, "DROP TABLE bans; DROP TABLE abuse_flags; DELETE FROM schema_version WHERE version >= 10;");
        Database.ReleasePooledConnections(this._path);

        var migrated = new Database(this._path);
        Assert.Equal((long) Database.SchemaVersion, Query(this._path, "SELECT MAX(version) FROM schema_version;"));
        Assert.NotNull(migrated.GetUser(77));
        Assert.Empty(migrated.GetActiveBans(this.Now));
        migrated.AddBan(77, null, "", null, false, this.Now, out _);
        Database.ReleasePooledConnections(this._path);

        var again = new Database(this._path);
        Assert.Equal(1L, Query(this._path, "SELECT COUNT(*) FROM schema_version WHERE version = 10;"));
        Assert.Single(again.GetActiveBans(this.Now));
    }

    // ================================================================ addresses and prefixes

    /// <summary>
    /// What an operator can ban: an IPv4 address or network (a /16 or narrower), or an IPv6 /64 or wider prefix (one address
    /// stands for its /64, which one client usually has); written the one way flags and the list show them.
    /// </summary>
    [Theory]
    [InlineData("203.0.113.5", "203.0.113.5")]
    [InlineData(" 203.0.113.5 ", "203.0.113.5")]
    [InlineData("203.0.113.5/32", "203.0.113.5")]
    [InlineData("203.0.113.77/24", "203.0.113.0/24")]
    [InlineData("10.1.0.0/16", "10.1.0.0/16")]
    [InlineData("::ffff:203.0.113.5", "203.0.113.5")]
    [InlineData("2001:db8:1:2::99", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::/64", "2001:db8:1:2::/64")]
    [InlineData("2001:DB8:1:2ff::/56", "2001:db8:1:200::/56")]
    [InlineData("2001:db8:1::/48", "2001:db8:1::/48")]
    public void AddressesAndPrefixesAreWrittenOneWay(string given, string expected) {
        Assert.Equal(expected, ClientAddresses.BanPrefix(given, out var problem));
        Assert.Null(problem);
    }

    [Theory]
    [InlineData("10.0.0.0/8")]
    [InlineData("0.0.0.0/0")]
    [InlineData("2001:db8::/16")]
    [InlineData("2001:db8:1:2::/80")]
    [InlineData("2001:db8:1:2::1/128")]
    [InlineData("not an address")]
    [InlineData("203.0.113.5/33")]
    public void TooWideOrNarrowOrNoAddressIsRefused(string given) {
        Assert.Null(ClientAddresses.BanPrefix(given, out var problem));
        Assert.False(string.IsNullOrEmpty(problem));
    }

    /// <summary>
    /// The server's own addresses, and its proxy's, are where every player's connections come from when the forwarded client
    /// address is missing: loopback, the unspecified address and the configured trusted proxies (or a prefix holding one).
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.0/16", true)]
    [InlineData("::1", true)]
    [InlineData("::/64", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("10.0.0.0/16", true)]
    [InlineData("172.17.3.4", true)]
    [InlineData("2001:db8:aa:1::/64", true)]
    [InlineData("10.1.0.5", false)]
    [InlineData("203.0.113.5", false)]
    [InlineData("2001:db8:1:2::/64", false)]
    public void TheServersOwnAndItsProxysAddressesAreKnown(string prefix, bool proxy) {
        string[] trusted = ["10.0.0.5", "172.17.0.0/16", "2001:db8:aa:1::7"];
        Assert.Equal(proxy, ClientAddresses.ProxyProblem(prefix, trusted) != null);
    }

    /// <summary>
    /// <c>--ban</c> refuses the server's own or its proxy's address (banning it would ban every player), saying so, unless
    /// <c>--force</c> says it is meant.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("10.0.0.0/24")]
    public void TheProxysAddressIsOnlyBannedWithForce(string target) {
        var (code, _, errors) = this.Run(new BanCommand.Request(BanCommand.BanAction.Ban, target, null, null), trustedProxies: ["10.0.0.5"]);
        Assert.Equal(1, code);
        Assert.Contains("proxy", errors);
        Assert.Contains("every player", errors);
        Assert.Contains("--force", errors);
        Assert.Empty(this._db.GetActiveBans(this.Now));

        (code, _, _) = this.Run(new BanCommand.Request(BanCommand.BanAction.Ban, target, null, null, Force: true), trustedProxies: ["10.0.0.5"]);
        Assert.Equal(0, code);
        Assert.Single(this._db.GetActiveBans(this.Now));
    }

    /// <summary>A prefix covers the addresses limits count (an IPv4 address, or an IPv6 /64) inside it.</summary>
    [Theory]
    [InlineData("203.0.113.5", "203.0.113.5", true)]
    [InlineData("203.0.113.5", "203.0.113.6", false)]
    [InlineData("203.0.113.0/24", "203.0.113.200", true)]
    [InlineData("203.0.113.0/24", "203.0.114.1", false)]
    [InlineData("2001:db8:1:2::/64", "2001:db8:1:2::/64", true)]
    [InlineData("2001:db8:1:200::/56", "2001:db8:1:2ff::/64", true)]
    [InlineData("2001:db8:1:200::/56", "2001:db8:1:300::/64", false)]
    [InlineData("2001:db8:1:2::/64", "unknown", false)]
    [InlineData("203.0.113.0/24", "2001:db8:1:2::/64", false)]
    public void APrefixCoversTheAddressesInIt(string prefix, string limitKey, bool covered) {
        Assert.Equal(covered, ClientAddresses.Covers(prefix, limitKey));
    }

    // ================================================================ the running server's list

    /// <summary>
    /// The server reads bans from the database at most every <see cref="AbuseOptions.BanCheckSeconds"/>, so one made from
    /// the command line (another process) applies within that, and a ban that has ended stops applying at once.
    /// </summary>
    [Fact]
    public void TheServersListPicksUpNewBansWithinTheCheckInterval() {
        var clock = new StoppedClock();
        var bans = new BanList(this._db, Microsoft.Extensions.Options.Options.Create(new ServerOptions { Abuse = { BanCheckSeconds = 30 } }), clock);
        Assert.Null(bans.ForUser(31337));

        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        this._db.AddBan(31337, null, "Spamming", now + 3600, false, now, out _);
        this._db.AddBan(null, "2001:db8:1:200::/56", "", null, false, now, out _);
        Assert.Null(bans.ForUser(31337));
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal("Spamming", bans.ForUser(31337)?.Reason);
        Assert.NotNull(bans.ForAddress("2001:db8:1:2ff::/64"));
        Assert.Null(bans.ForAddress("2001:db8:1:300::/64"));
        Assert.Null(bans.ForUser(1));

        // Ended an hour on, whether or not the list was read again.
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Null(bans.ForUser(31337));

        // Refreshing reads them now.
        this._db.LiftBans(null, "2001:db8:1:200::/56", now + 3700);
        bans.Refresh();
        Assert.Null(bans.ForAddress("2001:db8:1:2ff::/64"));
    }

    // ================================================================ the commands

    [Fact]
    public void TheCommandsAreReadFromTheCommandLine() {
        Assert.Null(BanCommand.Parse(["--urls", "http://127.0.0.1:5180"]));
        Assert.Equal(new BanCommand.Request(BanCommand.BanAction.Ban, "Bob Hatter@Lich", null, null), BanCommand.Parse(["--ban", "Bob Hatter@Lich"]));
        Assert.Equal(new BanCommand.Request(BanCommand.BanAction.Ban, "203.0.113.5", 7, "Spamming invites"),
            BanCommand.Parse(["--ban", "203.0.113.5", "--days", "7", "--reason", "Spamming invites"]));
        Assert.Equal(new BanCommand.Request(BanCommand.BanAction.Unban, "31337", null, null), BanCommand.Parse(["--unban", "31337"]));
        Assert.Equal(new BanCommand.Request(BanCommand.BanAction.List, null, null, null), BanCommand.Parse(["--bans", "--LookingGlass:DataDirectory=/x"]));
        Assert.Equal(new BanCommand.Request(BanCommand.BanAction.Ban, "127.0.0.1", null, null, Force: true), BanCommand.Parse(["--ban", "127.0.0.1", "--force"]));
    }

    [Theory]
    [InlineData("--ban")]
    [InlineData("--ban", "--days", "3")]
    [InlineData("--unban")]
    [InlineData("--ban", "31337", "--days")]
    [InlineData("--ban", "31337", "--days", "0")]
    [InlineData("--ban", "31337", "--days", "1.5")]
    [InlineData("--ban", "31337", "--days", "40000")]
    [InlineData("--ban", "31337", "--reason")]
    [InlineData("--ban", "31337", "--reason", "two\nlines")]
    [InlineData("--unban", "31337", "--days", "3")]
    [InlineData("--bans", "--reason", "why")]
    [InlineData("--ban", "31337", "--bans")]
    [InlineData("--ban", "1", "--unban", "1")]
    [InlineData("--unban", "127.0.0.1", "--force")]
    [InlineData("--bans", "--force")]
    public void WrongCommandsSayWhatsWrong(params string[] args) {
        var error = Assert.Throws<ArgumentException>(() => BanCommand.Parse(args));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void AReasonCanBeLongButNotTooLong() {
        Assert.NotNull(BanCommand.Parse(["--ban", "1", "--reason", new string('x', BanCommand.MaxReasonLength)]));
        Assert.Throws<ArgumentException>(() => BanCommand.Parse(["--ban", "1", "--reason", new string('x', BanCommand.MaxReasonLength + 1)]));
    }

    /// <summary>
    /// <c>--ban name@world</c> bans the registered character by its user ID (its Lodestone ID), <c>--ban &lt;user ID&gt;</c>
    /// any character, registered or not, and <c>--ban &lt;address&gt;</c> an address or prefix; <c>--unban</c> lifts it and
    /// <c>--bans</c> lists bans, flags and recent history.
    /// </summary>
    [Fact]
    public void TheOperatorBansUnbansAndListsFromTheCommandLine() {
        using var keys = IdentityKeys.Generate();
        this._db.RegisterUser(31337, "Bob Hatter", 73, "Lich", keys.ToBundle(), false);

        var (code, output, _) = this.Run(new BanCommand.Request(BanCommand.BanAction.Ban, "bob hatter@lich", null, "Spamming invites"));
        Assert.Equal(0, code);
        Assert.Contains("user 31337 (Bob Hatter@Lich)", output);
        Assert.Contains("until lifted", output);
        Assert.Equal((31337L, "Spamming invites", (long?) null), this._db.GetActiveBans(this.Now).Select(ban => (ban.UserId!.Value, ban.Reason, ban.ExpiresAt)).Single());

        (code, output, _) = this.Run(new BanCommand.Request(BanCommand.BanAction.Ban, "2001:db8:1:2::99", 7, null));
        Assert.Equal(0, code);
        Assert.Contains("address 2001:db8:1:2::/64", output);
        Assert.Contains("2026-10-14 12:00 UTC", output);

        // A character nobody registered yet can be banned by ID (it then can't register); not by name, which the server doesn't know.
        Assert.Equal(0, this.Run(new BanCommand.Request(BanCommand.BanAction.Ban, "4242", null, null)).Code);
        (code, _, var errors) = this.Run(new BanCommand.Request(BanCommand.BanAction.Ban, "Nobody Here@Lich", null, null));
        Assert.Equal(1, code);
        Assert.Contains("Nobody Here@Lich", errors);
        (code, _, errors) = this.Run(new BanCommand.Request(BanCommand.BanAction.Ban, "10.0.0.0/8", null, null));
        Assert.Equal(1, code);
        Assert.Contains("/16", errors);

        this._db.SaveFlag(new FlagRow("user 31337", 31337, null, this.Now - 600, this.Now - 60, "InviteBurstPerPair, LookupBurst",
            "refused by limits in 31 of the last 60 minutes"));
        this._db.SaveFlag(new FlagRow("user 5", 5, null, this.Now - 3 * 86400, this.Now - 2 * 86400, "LookupBurst", "old"));
        (code, output, _) = this.Run(new BanCommand.Request(BanCommand.BanAction.List, null, null, null));
        Assert.Equal(0, code);
        Assert.Contains("Bans in force (3)", output);
        Assert.Contains("Spamming invites", output);
        Assert.Contains("refused by limits in 31 of the last 60 minutes", output);
        Assert.Contains("InviteBurstPerPair, LookupBurst", output);
        // Flags that expired (24 hours without refusals, by default) aren't listed.
        Assert.DoesNotContain("user 5", output);

        (code, output, _) = this.Run(new BanCommand.Request(BanCommand.BanAction.Unban, "Bob Hatter@Lich", null, null));
        Assert.Equal(0, code);
        Assert.Contains("Lifted", output);
        Assert.DoesNotContain(this._db.GetActiveBans(this.Now), ban => ban.UserId == 31337);
        (code, _, errors) = this.Run(new BanCommand.Request(BanCommand.BanAction.Unban, "31337", null, null));
        Assert.Equal(1, code);
        Assert.Contains("no ban", errors);

        Assert.Equal(0, this.Run(new BanCommand.Request(BanCommand.BanAction.Unban, "2001:db8:1:2::/64", null, null)).Code);
        (_, output, _) = this.Run(new BanCommand.Request(BanCommand.BanAction.List, null, null, null));
        Assert.Contains("Bans in force (1)", output);
        Assert.Contains("Lifted or ended in the last 90 days (2)", output);
    }

    /// <summary>The commands never make a database: one that isn't there is an error.</summary>
    [Fact]
    public void TheCommandsNeedADatabase() {
        var missing = Path.Combine(this._directory, "missing", "lookingglass.db");
        var errors = new StringWriter();
        Assert.Equal(1, BanCommand.Run(new BanCommand.Request(BanCommand.BanAction.List, null, null, null), missing, new AbuseOptions(), new StringWriter(), errors));
        Assert.Contains("no database", errors.ToString());
        Assert.False(File.Exists(missing));
    }

    /// <summary>The commands work alongside a running server, on its database, as the backup does.</summary>
    [Fact]
    public async Task TheCommandsRunAlongsideARunningServer() {
        await using var server = new Harness();
        var alice = await server.RegisterAsync("Alice Listed");
        var path = Path.Combine(server.DataDirectory, "lookingglass.db");
        var output = new StringWriter();
        Assert.Equal(0, BanCommand.Run(new BanCommand.Request(BanCommand.BanAction.Ban, "Alice Listed@Debug", 1, null), path, new AbuseOptions(), output, new StringWriter()));
        Assert.Contains($"user {alice.UserId} (Alice Listed@Debug)", output.ToString());
        Assert.Equal(alice.UserId, Assert.Single(server.Database.GetActiveBans(DateTimeOffset.UtcNow.ToUnixTimeSeconds())).UserId);
    }

    /// <summary><c>LookingGlass.Server --bans</c> runs the command and starts no server.</summary>
    [Fact]
    public async Task TheServerRunsTheCommandsWithoutStartingItself() {
        await ExitCodeGate.WaitAsync(Ct);
        var exitCode = Environment.ExitCode;
        try {
            this._db.AddBan(31337, null, "", null, false, this.Now, out _);
            Environment.ExitCode = 0;
            RunServer("--bans", $"--LookingGlass:DataDirectory={this._directory}");
            Assert.Equal(0, Environment.ExitCode);
            RunServer("--ban", $"--LookingGlass:DataDirectory={this._directory}");
            Assert.Equal(2, Environment.ExitCode);
            Environment.ExitCode = 0;
            RunServer("--unban", "31337", $"--LookingGlass:DataDirectory={this._directory}");
            Assert.Equal(0, Environment.ExitCode);
            Assert.Empty(this._db.GetActiveBans(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

            // --force, like --bans, has no value: the setting after it is still read as a setting.
            RunServer("--ban", "127.0.0.1", "--force", $"--LookingGlass:DataDirectory={this._directory}");
            Assert.Equal(0, Environment.ExitCode);
            Assert.Equal("127.0.0.1", Assert.Single(this._db.GetActiveBans(DateTimeOffset.UtcNow.ToUnixTimeSeconds())).Address);
        } finally {
            Environment.ExitCode = exitCode;
            ExitCodeGate.Release();
        }
    }

    private (int Code, string Output, string Errors) Run(BanCommand.Request request, string[]? trustedProxies = null) {
        var output = new StringWriter();
        var errors = new StringWriter();
        var code = BanCommand.Run(request, this._path, new AbuseOptions(), output, errors, Start, trustedProxies);
        return (code, output.ToString(), errors.ToString());
    }

    private static void RunServer(params string[] args) {
        var result = typeof(Program).Assembly.EntryPoint!.Invoke(null, [args]);
        if (result is Task task) {
            task.GetAwaiter().GetResult();
        }
    }

    private static void Execute(string path, string sql) => Query(path, sql + " SELECT 0;");

    private static long Query(string path, string sql) {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
