using Microsoft.Extensions.DependencyInjection;
using LookingGlass.Core.Client;
using LookingGlass.Core.Crypto;
using LookingGlass.Server;
using LookingGlass.Server.Data;
using LookingGlass.Server.Hosting;
using LookingGlass.Server.Services;
using static LookingGlass.Server.Services.OperatorAction;
using static LookingGlass.Server.Services.OperatorRefusal;
using static LookingGlass.Server.Services.OperatorTool;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// The operator actions layer every tool goes through (schema 12): the policy table of what each tool may do (the command
/// line, the web page, the plugin), the same rules through the service for every kind of target, the audit trail (refused
/// attempts too) and its sweep, who made each ban as <c>--bans</c> shows it, <c>--audit</c>, and a ban made inside the running
/// server applying at once.
/// </summary>
public sealed class OperatorActionsTests : IDisposable {
    private const long Bob = 31337;
    private const long Alice = 55555;
    private const long Rabbit = 66666;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lgt-operator-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private readonly Database _db;
    private readonly StoppedClock _clock = new();

    public OperatorActionsTests() {
        Directory.CreateDirectory(this._directory);
        this._path = Path.Combine(this._directory, "lookingglass.db");
        this._db = new Database(this._path);
        foreach (var (id, name, world) in new[] { (Bob, "Bob Hatter", "Lich"), (Alice, "Alice Wonderland", "Lich"), (Rabbit, "Alice Rabbit", "Odin") }) {
            using var keys = IdentityKeys.Generate();
            this._db.RegisterUser(id, name, 73, world, keys.ToBundle(), false);
        }
    }

    public void Dispose() => DeleteDirectory(this._directory);

    /// <summary>Registers a character (the plugin bans only registered ones).</summary>
    private void Register(long id, string name, string world = "Lich") {
        using var keys = IdentityKeys.Generate();
        this._db.RegisterUser(id, name, 73, world, keys.ToBundle(), false);
    }

    private long Now => this._clock.GetUtcNow().ToUnixTimeSeconds();

    private OperatorActions Actions(Action? applied = null) =>
        new(this._db, new OperatorSettings(new AbuseOptions(), ["10.0.0.5"], [Alice, Rabbit]), this._clock, applied);

    private static OperatorActor Actor(OperatorTool tool, long operatorCharacter = Alice) => tool switch {
        CommandLine => new OperatorActor(CommandLine, "alice"),
        WebPage => new OperatorActor(WebPage, "alice@github", "100.101.102.103"),
        _ => new OperatorActor(Plugin, operatorCharacter == Alice ? "Alice Wonderland@Lich" : "Alice Rabbit@Odin", UserId: operatorCharacter),
    };

    // ================================================================ the policy table

    /// <summary>
    /// What each tool may do, as one table: every action, against a character, an address, the proxy's address with and without
    /// force, a character listed as an operator with and without force, and the hour's limit.
    /// </summary>
    [Theory]
    // Seeing the flags and bans: every tool.
    [InlineData(CommandLine, See, false, false, false, false, 0, None)]
    [InlineData(WebPage, See, false, false, false, false, 0, None)]
    [InlineData(Plugin, See, false, false, false, false, 0, None)]
    // Banning a character: every tool.
    [InlineData(CommandLine, Ban, false, false, false, false, 0, None)]
    [InlineData(WebPage, Ban, false, false, false, false, 0, None)]
    [InlineData(Plugin, Ban, false, false, false, false, 0, None)]
    // An address or prefix: not the plugin.
    [InlineData(CommandLine, Ban, true, false, false, false, 0, None)]
    [InlineData(WebPage, Ban, true, false, false, false, 0, None)]
    [InlineData(Plugin, Ban, true, false, false, false, 0, AddressesNotHere)]
    // The server's own or its proxy's address: only with force, and never from the plugin.
    [InlineData(CommandLine, Ban, true, true, false, false, 0, ProxyAddress)]
    [InlineData(WebPage, Ban, true, true, false, false, 0, ProxyAddress)]
    [InlineData(Plugin, Ban, true, true, false, false, 0, AddressesNotHere)]
    [InlineData(CommandLine, Ban, true, true, false, true, 0, None)]
    [InlineData(WebPage, Ban, true, true, false, true, 0, None)]
    [InlineData(Plugin, Ban, true, true, false, true, 0, AddressesNotHere)]
    // Force on a character that needs none: harmless where force is allowed; the plugin has none.
    [InlineData(CommandLine, Ban, false, false, false, true, 0, None)]
    [InlineData(WebPage, Ban, false, false, false, true, 0, None)]
    [InlineData(Plugin, Ban, false, false, false, true, 0, ForceNotHere)]
    // A character listed as an operator: only the command line, only with --force.
    [InlineData(CommandLine, Ban, false, false, true, false, 0, OperatorCharacter)]
    [InlineData(WebPage, Ban, false, false, true, false, 0, OperatorCharacter)]
    [InlineData(Plugin, Ban, false, false, true, false, 0, OperatorCharacter)]
    [InlineData(CommandLine, Ban, false, false, true, true, 0, None)]
    [InlineData(WebPage, Ban, false, false, true, true, 0, OperatorCharacter)]
    [InlineData(Plugin, Ban, false, false, true, true, 0, ForceNotHere)]
    // Bans an hour: no limit on the command line, 30 on the web page, 10 per operator character in the plugin.
    [InlineData(CommandLine, Ban, false, false, false, false, 10_000, None)]
    [InlineData(WebPage, Ban, false, false, false, false, 29, None)]
    [InlineData(WebPage, Ban, false, false, false, false, 30, HourLimit)]
    [InlineData(Plugin, Ban, false, false, false, false, 9, None)]
    [InlineData(Plugin, Ban, false, false, false, false, 10, HourLimit)]
    // Lifting: characters with every tool, addresses (automatic blocks included) not from the plugin; the hour's limit is for bans.
    [InlineData(CommandLine, Lift, false, false, false, false, 0, None)]
    [InlineData(WebPage, Lift, false, false, false, false, 0, None)]
    [InlineData(Plugin, Lift, false, false, false, false, 10, None)]
    [InlineData(CommandLine, Lift, true, false, false, false, 0, None)]
    [InlineData(WebPage, Lift, true, false, false, false, 0, None)]
    [InlineData(Plugin, Lift, true, false, false, false, 0, AddressesNotHere)]
    // Pinning or restoring an operator's keys: the command line and the web page.
    [InlineData(CommandLine, Pin, false, false, true, false, 0, None)]
    [InlineData(WebPage, Pin, false, false, true, false, 0, None)]
    [InlineData(Plugin, Pin, false, false, true, false, 0, NotThisTool)]
    // Letting a key sign in again: the command line only (not on the web page at first).
    [InlineData(CommandLine, AllowKeyLogin, false, false, false, false, 0, None)]
    [InlineData(WebPage, AllowKeyLogin, false, false, false, false, 0, NotThisTool)]
    [InlineData(Plugin, AllowKeyLogin, false, false, false, false, 0, NotThisTool)]
    public void ThePolicyTableSaysWhatEachToolMayDo(OperatorTool tool, OperatorAction action, bool address, bool proxy, bool operatorCharacter, bool force,
        int bansThisHour, OperatorRefusal expected) {
        Assert.Equal(expected, OperatorPolicy.Check(tool, action, new PolicyTarget(address, proxy, operatorCharacter), force, bansThisHour));
    }

    /// <summary>
    /// The plugin neither bans a character not registered here (by its Lodestone ID; the web page and the command line do) nor
    /// lifts a ban on a character listed as an operator (whose bans the other tools make and lift); it lifts any other
    /// character's ban, registered or not.
    /// </summary>
    [Theory]
    [InlineData(CommandLine, Ban, false, true, None)]
    [InlineData(WebPage, Ban, false, true, None)]
    [InlineData(Plugin, Ban, false, true, NotRegistered)]
    [InlineData(Plugin, Lift, false, true, None)]
    [InlineData(CommandLine, Lift, true, false, None)]
    [InlineData(WebPage, Lift, true, false, None)]
    [InlineData(Plugin, Lift, true, false, OperatorCharacter)]
    public void ThePluginLeavesUnregisteredAndOperatorCharactersToTheOtherTools(OperatorTool tool, OperatorAction action, bool operatorCharacter, bool unregistered,
        OperatorRefusal expected) =>
        Assert.Equal(expected, OperatorPolicy.Check(tool, action, new PolicyTarget(false, IsOperatorCharacter: operatorCharacter, IsUnregistered: unregistered)));

    /// <summary>The rest of each tool's row: what it sees, its hour's limit, and who it records as having done it.</summary>
    [Fact]
    public void EachToolsRowSaysWhatItSeesAndWhoItRecords() {
        Assert.Equal((true, (int?) null, false, "command line"),
            (OperatorPolicy.For(CommandLine).FullAddresses, OperatorPolicy.For(CommandLine).BansPerHour, OperatorPolicy.For(CommandLine).BansPerHourPerOperator,
                OperatorPolicy.Code(CommandLine)));
        Assert.Equal((true, (int?) 30, false, "web page"),
            (OperatorPolicy.For(WebPage).FullAddresses, OperatorPolicy.For(WebPage).BansPerHour, OperatorPolicy.For(WebPage).BansPerHourPerOperator,
                OperatorPolicy.Code(WebPage)));
        Assert.Equal((false, (int?) 10, true, "plugin"),
            (OperatorPolicy.For(Plugin).FullAddresses, OperatorPolicy.For(Plugin).BansPerHour, OperatorPolicy.For(Plugin).BansPerHourPerOperator,
                OperatorPolicy.Code(Plugin)));
        Assert.Contains("SUDO_USER", OperatorPolicy.For(CommandLine).Who);
        Assert.Contains("Tailscale", OperatorPolicy.For(WebPage).Who);
        Assert.Contains("operator character", OperatorPolicy.For(Plugin).Who);
    }

    // ================================================================ the same rules through the service

    /// <summary>
    /// The policy, through the service, for every kind of target each tool might be given: a registered name, an unregistered
    /// ID, a name nobody registered, an address, a prefix, one too wide, the proxy's address with and without force, and a
    /// character listed as an operator with and without force. Every attempt is in the audit trail, done or refused.
    /// </summary>
    [Theory]
    [InlineData(CommandLine, "Bob Hatter@Lich", false, None)]
    [InlineData(WebPage, "Bob Hatter@Lich", false, None)]
    [InlineData(Plugin, "Bob Hatter@Lich", false, None)]
    [InlineData(CommandLine, "4242", false, None)]
    [InlineData(WebPage, "4242", false, None)]
    // A character not registered here: not from the plugin (owner's default, 2026-10-11, after the review).
    [InlineData(Plugin, "4242", false, NotRegistered)]
    [InlineData(CommandLine, "Nobody Here@Lich", false, NoSuchCharacter)]
    [InlineData(WebPage, "Nobody Here@Lich", false, NoSuchCharacter)]
    [InlineData(Plugin, "Nobody Here@Lich", false, NoSuchCharacter)]
    [InlineData(CommandLine, "203.0.113.5", false, None)]
    [InlineData(WebPage, "203.0.113.5", false, None)]
    [InlineData(Plugin, "203.0.113.5", false, AddressesNotHere)]
    [InlineData(CommandLine, "203.0.113.0/24", false, None)]
    [InlineData(WebPage, "203.0.113.0/24", false, None)]
    [InlineData(Plugin, "203.0.113.0/24", false, AddressesNotHere)]
    [InlineData(CommandLine, "10.0.0.0/8", false, NotATarget)]
    [InlineData(WebPage, "10.0.0.0/8", false, NotATarget)]
    [InlineData(Plugin, "10.0.0.0/8", false, NotATarget)]
    [InlineData(CommandLine, "127.0.0.1", false, ProxyAddress)]
    [InlineData(WebPage, "127.0.0.1", false, ProxyAddress)]
    [InlineData(Plugin, "127.0.0.1", false, AddressesNotHere)]
    [InlineData(CommandLine, "10.0.0.5", true, None)]
    [InlineData(WebPage, "10.0.0.5", true, None)]
    [InlineData(Plugin, "10.0.0.5", true, AddressesNotHere)]
    [InlineData(CommandLine, "66666", false, OperatorCharacter)]
    [InlineData(WebPage, "Alice Rabbit@Odin", false, OperatorCharacter)]
    [InlineData(Plugin, "66666", false, OperatorCharacter)]
    [InlineData(CommandLine, "Alice Rabbit@Odin", true, None)]
    [InlineData(WebPage, "66666", true, OperatorCharacter)]
    [InlineData(Plugin, "Alice Rabbit@Odin", true, ForceNotHere)]
    public void TheServiceAppliesTheTableToEveryKindOfTarget(OperatorTool tool, string whom, bool force, OperatorRefusal expected) {
        var result = this.Actions().Ban(Actor(tool), whom, 7, "Spamming invites", force);
        Assert.Equal(expected, result.Refusal);
        Assert.Equal(expected == None, result.Done);
        Assert.Equal(expected == None, this._db.GetActiveBans(this.Now).Count == 1);
        if (expected != None) {
            Assert.False(string.IsNullOrWhiteSpace(result.Why));
        }

        var entry = Assert.Single(this._db.GetOperatorLog(0, 10));
        Assert.Equal((OperatorPolicy.Code(tool), Actor(tool).Who, "ban", expected == None), (entry.Tool, entry.Who, entry.Action, entry.Done));
        Assert.Equal(result.Why, entry.Why);
        Assert.Equal(tool == Plugin ? Alice : null, entry.ActorUserId);
        Assert.Equal(tool == WebPage ? "100.101.102.103" : "", entry.From);
        if (expected == None) {
            var ban = Assert.Single(this._db.GetActiveBans(this.Now));
            Assert.Equal((OperatorPolicy.Code(tool), Actor(tool).Who, false), (ban.MadeVia, ban.MadeBy, ban.MadeAnnounced));
            Assert.Contains("for 7 days", entry.Detail);
            Assert.Contains("Spamming invites", entry.Detail);
        }
    }

    /// <summary>Lifting, by every tool: characters with any, addresses (automatic blocks too) not from the plugin; nothing in force is refused.</summary>
    [Theory]
    [InlineData(CommandLine, "Bob Hatter@Lich", None)]
    [InlineData(WebPage, "31337", None)]
    [InlineData(Plugin, "31337", None)]
    [InlineData(CommandLine, "203.0.113.0/24", None)]
    [InlineData(WebPage, "203.0.113.0/24", None)]
    [InlineData(Plugin, "203.0.113.0/24", AddressesNotHere)]
    [InlineData(Plugin, "198.51.100.7", AddressesNotHere)]
    [InlineData(CommandLine, "4242", NothingToLift)]
    [InlineData(WebPage, "4242", NothingToLift)]
    [InlineData(Plugin, "4242", NothingToLift)]
    public void LiftingFollowsTheTableToo(OperatorTool tool, string whom, OperatorRefusal expected) {
        this._db.AddBan(Bob, null, "", null, false, this.Now, out _, BanTools.CommandLine, "carol");
        this._db.AddBan(null, "203.0.113.0/24", "", null, false, this.Now, out _, BanTools.WebPage, "carol@github");
        this._db.AddBan(null, "198.51.100.7", "", this.Now + 900, true, this.Now, out _);

        var result = this.Actions().Lift(Actor(tool), whom);
        Assert.Equal(expected, result.Refusal);
        Assert.Equal(expected == None ? 2 : 3, this._db.GetActiveBans(this.Now).Count);
        var entry = Assert.Single(this._db.GetOperatorLog(0, 10));
        Assert.Equal(("lift", expected == None), (entry.Action, entry.Done));
        if (expected == None) {
            var lifted = Assert.Single(this._db.GetBanHistory(this.Now));
            Assert.Equal((OperatorPolicy.Code(tool), Actor(tool).Who, false), (lifted.LiftedVia, lifted.LiftedBy, lifted.LiftedAnnounced));
        }
    }

    /// <summary>
    /// The hour's limits: 30 bans on the web page whoever is signed in, 10 per operator character in the plugin (another
    /// operator character has its own 10), none on the command line. Refused attempts don't count, and the hour slides.
    /// </summary>
    [Fact]
    public void EachToolsBansAnHourAreLimited() {
        var actions = this.Actions();
        for (var i = 0; i < 40; i++) {
            Assert.True(actions.Ban(Actor(CommandLine), (1000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), null, null).Done);
        }

        for (var i = 0; i < 30; i++) {
            Assert.True(actions.Ban(new OperatorActor(WebPage, i % 2 == 0 ? "alice@github" : "alice@example.com"), (2000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), 1, null).Done);
            if (i == 10) {
                // A refused attempt counts for nothing.
                Assert.Equal(ProxyAddress, actions.Ban(Actor(WebPage), "127.0.0.1", 1, null).Refusal);
            }
        }

        var refused = actions.Ban(Actor(WebPage), "2999", 1, null);
        Assert.Equal(HourLimit, refused.Refusal);
        Assert.Contains("30", refused.Why);
        // Lifting isn't limited.
        Assert.True(actions.Lift(Actor(WebPage), "2000").Done);

        // The plugin bans registered characters only.
        foreach (var id in new long[] { 3000, 3001, 3002, 3003, 3004, 3005, 3006, 3007, 3008, 3009, 3500, 3999 }) {
            this.Register(id, $"Spare Character{id}");
        }

        for (var i = 0; i < 10; i++) {
            Assert.True(actions.Ban(Actor(Plugin), (3000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), 1, null).Done);
        }

        Assert.Equal(HourLimit, actions.Ban(Actor(Plugin), "3999", 1, null).Refusal);
        Assert.Contains("web page", actions.Ban(Actor(Plugin), "3999", 1, null).Why);
        Assert.True(actions.Ban(Actor(Plugin, Rabbit), "3500", 1, null).Done);
        Assert.True(actions.Ban(Actor(CommandLine), "3501", 1, null).Done);

        // An hour on, each may ban again.
        this._clock.Advance(TimeSpan.FromMinutes(61));
        Assert.True(actions.Ban(Actor(WebPage), "2999", 1, null).Done);
        Assert.True(actions.Ban(Actor(Plugin), "3999", 1, null).Done);
    }

    /// <summary>A user ID is a positive number (a leading + or zeros are fine); zero or a negative one names nobody, for any action.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("-0")]
    [InlineData("-31337")]
    public void ZeroAndNegativeUserIdsNameNobody(string whom) {
        var actions = this.Actions();
        var actor = Actor(CommandLine);
        foreach (var result in new[] { actions.Ban(actor, whom, 1, null), actions.Lift(actor, whom), actions.AllowKeyLogin(actor, whom),
                     actions.PinByFingerprint(actor, whom, "00000 00000 00000 00000 00000") }) {
            Assert.Equal(NotATarget, result.Refusal);
            Assert.Contains("positive", result.Why);
        }

        Assert.Empty(this._db.GetActiveBans(this.Now));
        Assert.Equal(1, BanCommand.Run(new BanCommand.Request(BanCommand.BanAction.Ban, whom, null, null), this._path, new AbuseOptions(), new StringWriter(),
            new StringWriter(), who: "alice"));
        Assert.Equal("user 31337 (Bob Hatter@Lich)", actions.Ban(actor, "+0031337", 1, null).Whom);
    }

    /// <summary>
    /// Debug accounts (test servers) have negative user IDs: one registered can be named by it, and where debug accounts may
    /// register, any (to ban one before it registers). Zero never names anyone.
    /// </summary>
    [Fact]
    public void DebugAccountsNegativeIdsCanBeNamed() {
        using var keys = IdentityKeys.Generate();
        this._db.RegisterUser(-77, "Dana Debug", 0, "Debug", keys.ToBundle(), true);
        Assert.True(this.Actions().Ban(Actor(CommandLine), "-77", 1, null).Done);
        Assert.Equal(NotATarget, this.Actions().Ban(Actor(CommandLine), "-78", 1, null).Refusal);
        var debugServer = new OperatorActions(this._db, new OperatorSettings(new AbuseOptions(), [], [], DebugAccounts: true), this._clock);
        Assert.True(debugServer.Ban(Actor(CommandLine), "-78", 1, null).Done);
        Assert.Equal(NotATarget, debugServer.Ban(Actor(CommandLine), "0", 1, null).Refusal);
    }

    /// <summary>The days and the reason are checked by the service too (the web page and the plugin send them as typed).</summary>
    [Theory]
    [InlineData(0, "Spam")]
    [InlineData(36_501, "Spam")]
    [InlineData(1, "two\nlines")]
    public void DaysAndReasonsOutOfBoundsAreRefused(int days, string reason) {
        Assert.Equal(NotValid, this.Actions().Ban(Actor(WebPage), "31337", days, reason).Refusal);
        Assert.Equal(NotValid, this.Actions().Ban(Actor(WebPage), "31337", 1, new string('x', OperatorActions.MaxReasonLength + 1)).Refusal);
        Assert.Empty(this._db.GetActiveBans(this.Now));
    }

    // ================================================================ the audit trail

    /// <summary>
    /// Every attempt to change something goes into the audit trail, done or refused, with when, which tool, who, from where,
    /// what and the outcome; it is kept 365 days, then swept.
    /// </summary>
    [Fact]
    public void EveryAttemptIsAuditedAndTheTrailIsKeptAYear() {
        var actions = this.Actions();
        actions.Ban(Actor(WebPage), "203.0.113.0/24", 1, "Flooding");
        this._clock.Advance(TimeSpan.FromMinutes(1));
        actions.Ban(Actor(WebPage), "127.0.0.1", null, null);
        this._clock.Advance(TimeSpan.FromMinutes(1));
        actions.Lift(Actor(CommandLine), "4242");
        this._clock.Advance(TimeSpan.FromMinutes(1));
        actions.Lift(Actor(Plugin), "Bob Hatter@Lich");
        this._clock.Advance(TimeSpan.FromMinutes(1));
        actions.PinByFingerprint(Actor(Plugin), "55555", "00000 00000 00000 00000 00000");
        this._clock.Advance(TimeSpan.FromMinutes(1));
        actions.AllowKeyLogin(Actor(CommandLine), "Bob Hatter@Lich");

        var trail = actions.Audit(0, 100);
        Assert.Equal(["allow key login", "pin", "lift", "lift", "ban", "ban"], trail.Select(entry => entry.Action));
        Assert.Equal([false, false, false, false, false, true], trail.Select(entry => entry.Done));
        Assert.Equal([this.Now, this.Now - 60], trail.Take(2).Select(entry => entry.At));
        Assert.Equal("address 203.0.113.0/24", trail[^1].Target);
        Assert.Equal("alice@github", trail[^1].Who);
        Assert.Equal("100.101.102.103", trail[^1].From);
        Assert.Contains("this machine's own", trail[^2].Why);
        Assert.Contains("There is no ban on user 4242", trail[^3].Why);
        Assert.Contains("There is no ban on user 31337 (Bob Hatter@Lich)", trail[^4].Why);
        Assert.Contains("in the plugin", trail[1].Why);
        Assert.Contains("isn't turned off", trail[0].Why);

        // Kept 365 days (as the server sweeps it: see TheServerSweepsTheAuditTrail).
        int Sweep() => this._db.SweepOperatorLog(this.Now - OperatorActions.AuditKeptDays * 86400L);
        this._clock.Advance(TimeSpan.FromDays(364));
        Assert.Equal(0, Sweep());
        actions.Lift(Actor(CommandLine), "203.0.113.0/24");
        this._clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(5.5));
        Assert.Equal(6, Sweep());
        Assert.Equal("lift", Assert.Single(actions.Audit(0, 100)).Action);
    }

    /// <summary>A change and its audit entry are one transaction: if the entry can't be written, nothing was banned, lifted or pinned.</summary>
    [Fact]
    public void AChangeIsNeverMadeWithoutItsAuditEntry() {
        var actions = this.Actions();
        Assert.True(actions.Ban(Actor(CommandLine), "4242", null, null).Done);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={this._path};Pooling=False")) {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER no_audit BEFORE INSERT ON operator_log BEGIN SELECT RAISE(ABORT, 'the disk is full'); END;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => actions.Ban(Actor(WebPage), "31337", 1, null));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => actions.Lift(Actor(WebPage), "4242"));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => actions.PinByFingerprint(Actor(CommandLine), "55555", this._db.GetUser(Alice)!.Keys.Fingerprint));
        Assert.Equal([4242L], this._db.GetActiveBans(this.Now).Select(ban => ban.UserId!.Value));
        Assert.Empty(this._db.GetOperatorPins());
    }

    /// <summary>The running server sweeps the audit trail when it starts (and every ten minutes), with the old bans and flags.</summary>
    [Fact]
    public async Task TheServerSweepsTheAuditTrail() {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        this._db.AddOperatorLog(new OperatorLogRow(0, now - 366 * 86400L, "command line", "alice", "", null, "ban", "user 1", "", true, ""));
        this._db.AddOperatorLog(new OperatorLogRow(0, now - 364 * 86400L, "command line", "alice", "", null, "ban", "user 2", "", true, ""));
        Database.ReleasePooledConnections(this._path);
        try {
            await using var server = new Harness(this._directory);
            Assert.Equal(["user 2"], server.Database.GetOperatorLog(0, 10).Select(entry => entry.Target));
        } finally {
            Database.ReleasePooledConnections(this._path);
        }
    }

    // ================================================================ applied at once

    /// <summary>
    /// A ban made inside the running server (as the web page and the plugin will) applies at once: the connection closes long
    /// before the next check of the bans (every 30 seconds here, the default). Lifting likewise lets them back at once.
    /// </summary>
    [Fact]
    public async Task ABanMadeInsideTheServerAppliesAtOnce() {
        await using var server = new Harness();
        var carol = await server.RegisterAsync("Carol Queen");
        var actions = server.Factory.Services.GetRequiredService<OperatorActions>();

        var started = DateTimeOffset.UtcNow;
        Assert.True(actions.Ban(new OperatorActor(WebPage, "alice@github", "100.101.102.103"), "Carol Queen@Debug", 7, "Spamming invites").Done);
        await WaitFor(() => carol.Session.Snapshot.State == ConnectionState.Blocked ? new object() : null);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(10));
        var ban = Assert.Single(server.Database.GetActiveBans(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        Assert.Equal(("web page", "alice@github"), (ban.MadeVia, ban.MadeBy));

        Assert.True(actions.Lift(new OperatorActor(Plugin, "Alice Wonderland@Debug", UserId: 1), carol.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture)).Done);
        Assert.Null(server.Factory.Services.GetRequiredService<BanList>().ForUser(carol.UserId));
    }

    /// <summary>
    /// The hook that applies a ban at once is called for what was done only; the command line (another process) has none, so
    /// the server picks its bans up at the next check, as before, and it says so.
    /// </summary>
    [Fact]
    public void OnlyWhatWasDoneIsAppliedAndTheCommandLineWaitsForTheCheck() {
        var applied = 0;
        var actions = this.Actions(() => applied++);
        actions.Ban(Actor(WebPage), "31337", null, null);
        actions.Ban(Actor(WebPage), "127.0.0.1", null, null);
        actions.Lift(Actor(WebPage), "31337");
        actions.Lift(Actor(WebPage), "31337");
        Assert.Equal(2, applied);

        // A hook that fails doesn't undo the ban: the regular check applies it.
        Assert.True(this.Actions(() => throw new InvalidOperationException("closed")).Ban(Actor(WebPage), "4242", 1, null).Done);
        Assert.Contains(this._db.GetActiveBans(this.Now), ban => ban.UserId == 4242);

        var output = new StringWriter();
        Assert.Equal(0, BanCommand.Run(new BanCommand.Request(BanCommand.BanAction.Ban, "31337", 1, null), this._path, new AbuseOptions(), output, new StringWriter(),
            who: "alice"));
        Assert.Contains("A running server applies it within 30 seconds", output.ToString());
    }

    // ================================================================ the command line's words

    /// <summary>
    /// <c>--bans</c> says who made each ban, and with which tool, and who lifted it; the rest of its words stay as they were.
    /// Bans an older server made (no tool recorded) say nothing about it.
    /// </summary>
    [Fact]
    public void TheBansListSaysWhoMadeEachBan() {
        var start = this.Now;
        var actions = this.Actions();
        actions.Ban(Actor(CommandLine), "Bob Hatter@Lich", null, "Spamming invites");
        actions.Ban(Actor(WebPage), "203.0.113.0/24", 1, "Flooding");
        this.Register(4242, "Carol Queen", "Odin");
        actions.Ban(Actor(Plugin), "4242", 7, null);
        this._db.AddBan(null, "198.51.100.7", "", start + 900, true, start, out _);
        this._db.AddBan(777, null, "", null, false, start, out _);
        actions.Ban(Actor(WebPage), "888", 1, null);
        actions.Lift(Actor(CommandLine), "888");

        var output = new StringWriter();
        Assert.Equal(0, BanCommand.Run(new BanCommand.Request(BanCommand.BanAction.List, null, null, null), this._path, new AbuseOptions(), output, new StringWriter(),
            this._clock.GetUtcNow()));
        var text = output.ToString();
        Assert.Contains("Bans in force (5):", text);
        Assert.Contains("user 31337 (Bob Hatter@Lich): since ", text);
        Assert.Contains("until lifted, made on the command line by alice. Reason: Spamming invites", text);
        Assert.Contains("(1 day), made on the web page by alice@github. Reason: Flooding", text);
        Assert.Contains("(7 days), made in the plugin by Alice Wonderland@Lich", text);
        Assert.Contains("(made automatically)", text);
        var older = text.Split(Environment.NewLine).Single(line => line.Contains("user 777"));
        Assert.DoesNotContain("made", older);
        var lifted = text.Split(Environment.NewLine).Single(line => line.Contains("user 888"));
        Assert.Contains("lifted ", lifted);
        Assert.Contains(" on the command line by alice, made on the web page by alice@github", lifted);
    }

    /// <summary>
    /// A ban replaced by banning the same subject again is recorded as replaced, and by which ban (lifted by that ban's tool
    /// and maker), so it can be told once, with the new ban, rather than as a lift of its own; a ban lifted is not.
    /// </summary>
    [Fact]
    public void AReplacedBanIsRecordedAsReplacedNotLifted() {
        var actions = this.Actions();
        actions.Ban(Actor(CommandLine), "Bob Hatter@Lich", null, "First");
        var second = actions.Ban(Actor(WebPage), "31337", 7, "Second");
        Assert.Equal(1, second.Replaced);
        var first = Assert.Single(this._db.GetBanHistory(this.Now));
        Assert.Equal((second.Ban!.BanId, "web page", "alice@github", false), (first.ReplacedBy, first.LiftedVia, first.LiftedBy, first.LiftedAnnounced));
        Assert.Null(Assert.Single(this._db.GetActiveBans(this.Now)).ReplacedBy);

        actions.Lift(Actor(CommandLine), "31337");
        Assert.All(this._db.GetBanHistory(this.Now).Where(ban => ban.BanId == second.Ban.BanId), ban => Assert.Null(ban.ReplacedBy));

        var output = new StringWriter();
        BanCommand.Run(new BanCommand.Request(BanCommand.BanAction.List, null, null, null), this._path, new AbuseOptions(), output, new StringWriter(), this._clock.GetUtcNow());
        Assert.Contains(" on the web page by alice@github, made on the command line by alice. Reason: First", output.ToString());
        Assert.Contains("replaced by a new ban ", output.ToString());
        Assert.Contains(", lifted ", output.ToString());
    }

    /// <summary>
    /// An automatic block is never made over a ban in force on the address (or a prefix holding it), nor replaces one: the
    /// check and the block are one transaction. Over nothing, it is made, and it covers its address from then on.
    /// </summary>
    [Fact]
    public void AnAutomaticBlockNeverReplacesABan() {
        var now = this.Now;
        this._db.AddBan(null, "198.51.100.0/24", "Operator's", null, false, now, out _, BanTools.WebPage, "alice@github");
        this._db.AddBan(null, "203.0.113.9", "Operator's exact", null, false, now, out _, BanTools.CommandLine, "alice");
        Assert.Null(this._db.AddAutomaticBlock("198.51.100.7", "198.51.100.7", now + 900, now));
        Assert.Null(this._db.AddAutomaticBlock("203.0.113.9", "203.0.113.9", now + 900, now));
        Assert.Equal(["Operator's", "Operator's exact"], this._db.GetActiveBans(now).Select(ban => ban.Reason).Order());
        Assert.Empty(this._db.GetBanHistory(now));

        var block = this._db.AddAutomaticBlock("192.0.2.5", "192.0.2.5", now + 900, now);
        Assert.Equal((true, "automatic"), (block!.Automatic, block.MadeVia));
        Assert.Null(this._db.AddAutomaticBlock("192.0.2.5", "192.0.2.5", now + 900, now + 1));
        Assert.Equal(3, this._db.GetActiveBans(now + 1).Count);
    }

    /// <summary><c>--audit</c> lists the trail, newest first, with each tool, who, from where, and what was done or refused and why.</summary>
    [Fact]
    public void TheAuditCommandListsTheTrail() {
        var output = new StringWriter();
        Assert.Equal(0, BanCommand.Run(new BanCommand.Request(BanCommand.BanAction.Audit, null, null, null), this._path, new AbuseOptions(), output, new StringWriter()));
        Assert.Contains("Nothing in the audit trail", output.ToString());

        var actions = this.Actions();
        actions.Ban(Actor(WebPage), "203.0.113.0/24", 1, "Flooding");
        this._clock.Advance(TimeSpan.FromMinutes(1));
        actions.Ban(Actor(Plugin), "Bob Hatter@Lich", 7, null);
        this._clock.Advance(TimeSpan.FromMinutes(1));
        actions.Ban(Actor(WebPage), "127.0.0.1", null, null);
        this._clock.Advance(TimeSpan.FromDays(3));
        actions.Lift(Actor(CommandLine), "Bob Hatter@Lich");

        output = new StringWriter();
        Assert.Equal(0, BanCommand.Run(new BanCommand.Request(BanCommand.BanAction.Audit, null, null, null), this._path, new AbuseOptions(), output, new StringWriter(),
            this._clock.GetUtcNow()));
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("What operators did, newest first (the last 4)", lines[0]);
        Assert.Contains("command line  alice: lifted the ban on user 31337 (Bob Hatter@Lich)", lines[1]);
        Assert.Contains("web page      alice@github from 100.101.102.103: refused to ban address 127.0.0.1: 127.0.0.1 is or holds", lines[2]);
        Assert.Contains("plugin        Alice Wonderland@Lich: banned user 31337 (Bob Hatter@Lich), for 7 days", lines[3]);
        Assert.Contains("banned address 203.0.113.0/24, for 1 day. Reason: Flooding", lines[4]);
        Assert.Contains(" UTC ", lines[1]);

        // --days 1: only the last day's.
        output = new StringWriter();
        Assert.Equal(0, BanCommand.Run(new BanCommand.Request(BanCommand.BanAction.Audit, null, 1, null), this._path, new AbuseOptions(), output, new StringWriter(),
            this._clock.GetUtcNow()));
        Assert.Contains("What operators did in the last 1 day, newest first (1):", output.ToString());
    }

    [Fact]
    public void TheAuditCommandIsReadFromTheCommandLine() {
        Assert.Equal(new BanCommand.Request(BanCommand.BanAction.Audit, null, null, null), BanCommand.Parse(["--audit"]));
        Assert.Equal(new BanCommand.Request(BanCommand.BanAction.Audit, null, 30, null), BanCommand.Parse(["--audit", "--days", "30"]));
        foreach (var wrong in new[] { new[] { "--audit", "--days", "0" }, ["--audit", "--days", "366"], ["--audit", "--reason", "x"], ["--audit", "--force"],
                     ["--audit", "--bans"], ["--audit", "--fingerprint", "1"] }) {
            Assert.False(string.IsNullOrWhiteSpace(Assert.Throws<ArgumentException>(() => BanCommand.Parse(wrong)).Message));
        }
    }

    /// <summary>
    /// The command line refuses to ban a character listed as an operator unless <c>--force</c> says it is meant (the <c>LG</c>
    /// helper passes the service's <c>Operators</c> settings on), and records who ran it from <c>SUDO_USER</c>.
    /// </summary>
    [Fact]
    public void TheCommandLineBansAnOperatorCharacterOnlyWithForce() {
        var errors = new StringWriter();
        Assert.Equal(1, BanCommand.Run(new BanCommand.Request(BanCommand.BanAction.Ban, "Alice Wonderland@Lich", null, null), this._path, new AbuseOptions(),
            new StringWriter(), errors, operators: [Alice], who: "alice"));
        Assert.Contains("listed as an operator", errors.ToString());
        Assert.Contains("--force", errors.ToString());
        Assert.Empty(this._db.GetActiveBans(this.Now));

        Assert.Equal(0, BanCommand.Run(new BanCommand.Request(BanCommand.BanAction.Ban, "Alice Wonderland@Lich", null, null, Force: true), this._path,
            new AbuseOptions(), new StringWriter(), new StringWriter(), operators: [Alice], who: "alice"));
        Assert.Equal("alice", Assert.Single(this._db.GetActiveBans(DateTimeOffset.UtcNow.ToUnixTimeSeconds())).MadeBy);

        Assert.Equal("bob", OperatorActions.CommandLineUser(name => name switch { "SUDO_USER" => "bob", "USER" => "lookingglass", _ => null }));
        Assert.Equal("lookingglass", OperatorActions.CommandLineUser(name => name == "USER" ? "lookingglass" : null));
        Assert.Equal("unknown", OperatorActions.CommandLineUser(_ => null));
        Assert.Equal("a\\u000Ab", OperatorActions.CommandLineUser(_ => "a\nb"));
    }

    // ================================================================ shortened addresses

    /// <summary>
    /// Addresses shortened for the plugin (and, later, the alerts channel): an IPv4 address or a /24 or narrower as its first
    /// three parts, an IPv6 one or a /40 or narrower as its /40, a wider prefix as it is.
    /// </summary>
    [Theory]
    [InlineData("203.0.113.9", "203.0.113.x")]
    [InlineData("203.0.113.0/24", "203.0.113.x")]
    [InlineData("203.0.113.16/28", "203.0.113.x")]
    [InlineData("203.0.0.0/16", "203.0.0.0/16")]
    [InlineData("2001:db8:abcd:1::/64", "2001:db8:ab00::/40")]
    [InlineData("2001:db8:abcd:1::77", "2001:db8:ab00::/40")]
    [InlineData("2001:db8:ab00::/40", "2001:db8:ab00::/40")]
    [InlineData("2001:db8::/32", "2001:db8::/32")]
    [InlineData("unknown", "unknown")]
    public void AddressesAreShortenedWhereTheyMustNotBeHandedOut(string address, string shortened) {
        Assert.Equal(shortened, ClientAddresses.Shorten(address));
    }

    /// <summary>The plugin sees addresses shortened (in bans and flags); the command line and the web page see them in full.</summary>
    [Fact]
    public void ThePluginSeesAddressesShortened() {
        this._db.AddBan(null, "203.0.113.0/24", "", null, false, this.Now, out _);
        this._db.SaveFlag(new FlagRow("address 2001:db8:abcd:1::/64", null, "2001:db8:abcd:1::/64", this.Now, this.Now, "LookupBurst", "why"));
        this._db.SaveFlag(new FlagRow("user 31337", Bob, null, this.Now, this.Now, "LookupBurst", "why"));
        var actions = this.Actions();

        var plugin = actions.See(Plugin);
        Assert.Equal("address 203.0.113.x", Assert.Single(plugin.InForce).Whom);
        Assert.Equal(["address 2001:db8:ab00::/40", "user 31337 (Bob Hatter@Lich)"], plugin.Flags.Select(flag => flag.Whom).Order());
        foreach (var tool in new[] { CommandLine, WebPage }) {
            var full = actions.See(tool);
            Assert.Equal("address 203.0.113.0/24", Assert.Single(full.InForce).Whom);
            Assert.Contains(full.Flags, flag => flag.Whom == "address 2001:db8:abcd:1::/64");
        }
    }
}
