using LookingGlass.Core.Crypto;
using LookingGlass.Protocol;
using LookingGlass.Server;
using LookingGlass.Server.Data;
using LookingGlass.Server.Hosting;
using LookingGlass.Server.Services;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// Key-pinned operators (schema 12's <c>operator_pins</c>): the configuration lists who may ever be an operator, and a listed
/// character is one only while its account's current keys hash to the keys pinned for it, which only the operator pins
/// (here with <c>--operator-pin</c>, the fingerprint typed), never automatically. A key change drops it; pins for characters
/// no longer listed are deleted at startup; <c>--operators</c> lists them all.
/// </summary>
public sealed class OperatorPinTests : IDisposable {
    private const long Alice = 31337;
    private const long Rabbit = 55555;
    private const long Hearts = 77777;
    private const long Unregistered = 88888;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lgt-pins-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private readonly Database _db;
    private readonly IdentityKeys _aliceKeys = IdentityKeys.Generate();

    public OperatorPinTests() {
        Directory.CreateDirectory(this._directory);
        this._path = Path.Combine(this._directory, "lookingglass.db");
        this._db = new Database(this._path);
        this._db.RegisterUser(Alice, "Alice Wonderland", 73, "Lich", this._aliceKeys.ToBundle(), false);
        foreach (var (id, name) in new[] { (Rabbit, "Alice Rabbit"), (Hearts, "Alice Hearts"), (4242L, "Carol Queen") }) {
            using var keys = IdentityKeys.Generate();
            this._db.RegisterUser(id, name, 73, "Lich", keys.ToBundle(), false);
        }
    }

    public void Dispose() {
        this._aliceKeys.Dispose();
        DeleteDirectory(this._directory);
    }

    private static readonly long[] Listed = [Alice, Rabbit, Hearts, Unregistered];

    private OperatorActions Actions(params long[] listed) => new(this._db, new OperatorSettings(new AbuseOptions(), [], listed.Length == 0 ? Listed : listed));

    private static readonly OperatorActor CommandLine = new(OperatorTool.CommandLine, "alice");

    private string FingerprintOf(long userId) => this._db.GetUser(userId)!.Keys.Fingerprint;

    private (int Code, string Output, string Errors) Run(BanCommand.Request request, IReadOnlyList<long>? operators = null) {
        var output = new StringWriter();
        var errors = new StringWriter();
        var code = BanCommand.Run(request, this._path, new AbuseOptions(), output, errors, operators: operators ?? Listed, who: "alice");
        return (code, output.ToString(), errors.ToString());
    }

    private static BanCommand.Request PinRequest(string whom, string fingerprint) => new(BanCommand.BanAction.OperatorPin, whom, null, null, Fingerprint: fingerprint);

    /// <summary>A listed character isn't an operator until its keys are pinned: not at its first login, not ever by itself.</summary>
    [Fact]
    public void AListedCharacterIsNoOperatorUntilPinned() {
        var actions = this.Actions();
        Assert.Equal(OperatorStatus.NotPinned, actions.StatusOf(Alice));
        Assert.Equal(OperatorStatus.NotRegistered, actions.StatusOf(Unregistered));
        Assert.Equal(OperatorStatus.NotListed, actions.StatusOf(4242));
        Assert.Empty(this._db.GetOperatorPins());
    }

    /// <summary>
    /// <c>--operator-pin</c> pins only when the fingerprint typed is the character's current keys' (spaces don't matter), and
    /// only a listed, registered character. Refusals pin nothing and are in the audit trail.
    /// </summary>
    [Fact]
    public void TheCommandLinePinsOnlyTheCurrentKeysOfAListedCharacter() {
        var fingerprint = this.FingerprintOf(Alice);
        var wrong = this.FingerprintOf(Rabbit);

        var (code, _, errors) = this.Run(PinRequest("Alice Wonderland@Lich", wrong));
        Assert.Equal(1, code);
        Assert.Contains("isn't that of the keys user 31337 (Alice Wonderland@Lich) has now", errors);
        Assert.Contains(fingerprint, errors);
        Assert.Contains("Nothing was pinned", errors);

        (code, _, errors) = this.Run(PinRequest("4242", this.FingerprintOf(4242)));
        Assert.Equal(1, code);
        Assert.Contains("isn't listed in LookingGlass:Operators:Characters", errors);

        (code, _, errors) = this.Run(PinRequest(Unregistered.ToString(System.Globalization.CultureInfo.InvariantCulture), fingerprint));
        Assert.Equal(1, code);
        Assert.Contains("isn't a character registered on this server", errors);

        (code, _, errors) = this.Run(PinRequest("Nobody@Lich", fingerprint));
        Assert.Equal(1, code);
        Assert.Contains("No character Nobody@Lich", errors);
        Assert.Empty(this._db.GetOperatorPins());

        (code, var output, _) = this.Run(PinRequest("alice wonderland@lich", fingerprint.Replace(" ", "", StringComparison.Ordinal)));
        Assert.Equal(0, code);
        Assert.Contains($"Pinned the keys of user 31337 (Alice Wonderland@Lich) as an operator's: {fingerprint}.", output);
        var pin = Assert.Single(this._db.GetOperatorPins());
        Assert.Equal((Alice, "command line", "alice"), (pin.UserId, pin.PinnedVia, pin.PinnedBy));
        Assert.Equal(IdentityKeys.KeyHash(this._aliceKeys.SigningPublicKey, this._aliceKeys.AgreementPublicKey), pin.KeyHash);
        Assert.Equal(OperatorStatus.Operator, this.Actions().StatusOf(Alice));

        // Again: nothing changes.
        (code, output, _) = this.Run(PinRequest("31337", fingerprint));
        Assert.Equal(0, code);
        Assert.Contains("were pinned already: nothing changed", output);

        var trail = this._db.GetOperatorLog(0, 100);
        Assert.Equal([true, true, false, false, false, false], trail.Select(entry => entry.Done));
        Assert.All(trail, entry => Assert.Equal(("pin", "command line", "alice"), (entry.Action, entry.Tool, entry.Who)));
    }

    /// <summary>
    /// Registering again with new keys (a new computer, "Reset my identity", recovery) drops operator status at once, and
    /// nothing pins the new keys by itself; pinning again with the new fingerprint restores it, saying what it was.
    /// </summary>
    [Fact]
    public void ANewRegistrationDropsItAndOnlyPinningAgainRestoresIt() {
        var actions = this.Actions();
        Assert.True(actions.PinByFingerprint(CommandLine, "31337", this.FingerprintOf(Alice)).Done);
        var old = this.FingerprintOf(Alice);

        using var newKeys = IdentityKeys.Generate();
        this._db.RegisterUser(Alice, "Alice Wonderland", 73, "Lich", newKeys.ToBundle(), false);
        Assert.Equal(OperatorStatus.KeysChanged, actions.StatusOf(Alice));
        // Registering again (the same new keys) pins nothing either.
        this._db.RegisterUser(Alice, "Alice Wonderland", 73, "Lich", newKeys.ToBundle(), false);
        Assert.Equal(OperatorStatus.KeysChanged, actions.StatusOf(Alice));
        Assert.Equal(old, IdentityKeys.FingerprintOfHash(Assert.Single(this._db.GetOperatorPins()).KeyHash));

        var (code, output, _) = this.Run(new BanCommand.Request(BanCommand.BanAction.Operators, null, null, null));
        Assert.Equal(0, code);
        Assert.Contains("user 31337 (Alice Wonderland@Lich): NOT an operator now: its keys changed at ", output);
        Assert.Contains($"Pinned: {old}", output);
        Assert.Contains($"Now:    {newKeys.Fingerprint}", output);

        // The old fingerprint is refused now; the new one restores it.
        Assert.Equal(OperatorRefusal.KeysDiffer, actions.PinByFingerprint(CommandLine, "31337", old).Refusal);
        (code, output, _) = this.Run(PinRequest("31337", newKeys.Fingerprint));
        Assert.Equal(0, code);
        Assert.Contains($"Restored operator status for user 31337 (Alice Wonderland@Lich): its keys {newKeys.Fingerprint} are pinned now (they were {old}).", output);
        Assert.Equal(OperatorStatus.Operator, actions.StatusOf(Alice));
    }

    /// <summary>
    /// The pin is the full key hash, never the 25 digits: keys whose hash differs only past what the fingerprint shows are
    /// not the pinned keys.
    /// </summary>
    [Fact]
    public void ThePinComparesTheFullHashNotTheDigits() {
        var actions = this.Actions();
        var hash = IdentityKeys.KeyHash(this._aliceKeys.SigningPublicKey, this._aliceKeys.AgreementPublicKey);
        var near = (byte[]) hash.Clone();
        near[^1] ^= 1;
        Assert.Equal(IdentityKeys.FingerprintOfHash(hash), IdentityKeys.FingerprintOfHash(near));

        // A pin of the near hash (as a database edited by hand might hold) makes no operator.
        Assert.Equal(PinResult.KeysDiffer, this._db.PinOperator(Alice, near, "command line", "alice", 1, out _));
        ExecuteSql(this._path, $"INSERT INTO operator_pins (user_id, key_hash, pinned_at, pinned_via, pinned_by) VALUES ({Alice}, x'{Convert.ToHexString(near)}', 1, 'command line', 'alice');");
        Assert.Equal(OperatorStatus.KeysChanged, actions.StatusOf(Alice));

        // And keys shown with the near hash aren't pinned (the web page's way: the hash the confirmation page showed).
        Assert.Equal(OperatorRefusal.KeysDiffer, actions.PinShownKeys(new OperatorActor(OperatorTool.WebPage, "alice@github"), Alice, near).Refusal);
        Assert.True(actions.PinShownKeys(new OperatorActor(OperatorTool.WebPage, "alice@github"), Alice, hash).Done);
        Assert.Equal(OperatorStatus.Operator, actions.StatusOf(Alice));
        Assert.Equal(("web page", "alice@github"), (this._db.GetOperatorPin(Alice)!.PinnedVia, this._db.GetOperatorPin(Alice)!.PinnedBy));
    }

    /// <summary>
    /// The web page's pin names the keys its confirmation page showed: keys that changed again before the click aren't pinned
    /// unseen. The plugin can't pin at all, and nobody can pin a character the settings don't list.
    /// </summary>
    [Fact]
    public void KeysThatChangedAgainAreNotPinnedUnseenAndOnlyListedCharactersArePinned() {
        var actions = this.Actions();
        var shown = this._db.GetUser(Rabbit)!.Keys.Hash;
        using var newer = IdentityKeys.Generate();
        this._db.RegisterUser(Rabbit, "Alice Rabbit", 73, "Lich", newer.ToBundle(), false);
        var web = new OperatorActor(OperatorTool.WebPage, "alice@github");
        var refused = actions.PinShownKeys(web, Rabbit, shown);
        Assert.Equal(OperatorRefusal.KeysDiffer, refused.Refusal);
        Assert.Equal(newer.Fingerprint, refused.Fingerprint);
        Assert.Null(this._db.GetOperatorPin(Rabbit));

        Assert.Equal(OperatorRefusal.NotListed, actions.PinShownKeys(web, 4242, this._db.GetUser(4242)!.Keys.Hash).Refusal);
        Assert.Equal(OperatorRefusal.NotThisTool,
            actions.PinByFingerprint(new OperatorActor(OperatorTool.Plugin, "Alice Wonderland@Lich", UserId: Alice), "31337", this.FingerprintOf(Alice)).Refusal);
        Assert.Empty(this._db.GetOperatorPins());
    }

    /// <summary><c>--operators</c> lists every listed character: an operator, its keys changed, not pinned yet, or not registered.</summary>
    [Fact]
    public void TheOperatorsCommandListsEachListedCharacter() {
        var (code, output, _) = this.Run(new BanCommand.Request(BanCommand.BanAction.Operators, null, null, null), operators: []);
        Assert.Equal(0, code);
        Assert.Contains("No characters are listed as operators", output);

        var actions = this.Actions();
        Assert.True(actions.PinByFingerprint(CommandLine, "31337", this.FingerprintOf(Alice)).Done);
        (code, output, _) = this.Run(new BanCommand.Request(BanCommand.BanAction.Operators, null, null, null));
        Assert.Equal(0, code);
        Assert.Contains("Characters listed as operators (4):", output);
        Assert.Contains($"user 31337 (Alice Wonderland@Lich): an operator. Keys {this.FingerprintOf(Alice)}, pinned ", output);
        Assert.Contains(" UTC on the command line by alice.", output);
        Assert.Contains("user 55555 (Alice Rabbit@Lich): not an operator yet: its keys aren't pinned.", output);
        Assert.Contains($"Now:    {this.FingerprintOf(Rabbit)}", output);
        Assert.Contains("--operator-pin 55555 --fingerprint \"<the fingerprint>\"", output);
        Assert.Contains("user 88888: not registered on this server, so it has no keys to pin yet.", output);
    }

    /// <summary>The commands are read from the command line; <c>--operator-pin</c> needs whom and a fingerprint of 25 digits.</summary>
    [Fact]
    public void TheOperatorCommandsAreReadFromTheCommandLine() {
        Assert.Equal(new BanCommand.Request(BanCommand.BanAction.Operators, null, null, null), BanCommand.Parse(["--operators"]));
        Assert.Equal(PinRequest("Alice Wonderland@Lich", "48213 90551 17342 66018 30927"),
            BanCommand.Parse(["--operator-pin", "Alice Wonderland@Lich", "--fingerprint", "48213 90551 17342 66018 30927"]));
        foreach (var wrong in new[] {
                     new[] { "--operator-pin", "31337" }, ["--operator-pin", "--fingerprint", "48213 90551 17342 66018 30927"],
                     ["--operator-pin", "31337", "--fingerprint", "48213 90551"], ["--operator-pin", "31337", "--fingerprint", "48213 90551 17342 66018 3092x"],
                     ["--operator-pin", "31337", "--fingerprint"], ["--operators", "--days", "3"], ["--operators", "--fingerprint", "1"],
                     ["--ban", "31337", "--fingerprint", "48213 90551 17342 66018 30927"], ["--operators", "--operator-pin", "1"],
                 }) {
            Assert.False(string.IsNullOrWhiteSpace(Assert.Throws<ArgumentException>(() => BanCommand.Parse(wrong)).Message));
        }
    }

    /// <summary>
    /// At startup, pins for characters the settings no longer list are deleted (and logged): an unlisted character with a pin
    /// gets nothing. How each listed character stands is logged with its name.
    /// </summary>
    [Fact]
    public async Task PinsForCharactersNoLongerListedAreDeletedAtStartup() {
        var actions = this.Actions();
        Assert.True(actions.PinByFingerprint(CommandLine, "31337", this.FingerprintOf(Alice)).Done);
        Assert.True(actions.PinByFingerprint(CommandLine, "55555", this.FingerprintOf(Rabbit)).Done);
        Assert.Equal(OperatorStatus.NotListed, this.Actions(Alice).StatusOf(Rabbit));
        Database.ReleasePooledConnections(this._path);

        var logs = new CapturingLoggerProvider();
        try {
            await using var server = new Harness(this._directory, logs: logs, settings: [("LookingGlass:Operators:Characters:0", "31337"),
                ("LookingGlass:Operators:Characters:1", "77777")]);
            Assert.Equal([Alice], server.Database.GetOperatorPins().Select(pin => pin.UserId));
            Assert.Single(logs.AtLeast(Microsoft.Extensions.Logging.LogLevel.Warning), line => line.Contains("Deleted the operator pin of user 55555"));
            var listed = Assert.Single(logs.Entries, entry => entry.Message.StartsWith("Operators: ", StringComparison.Ordinal)).Message;
            Assert.Contains("user 31337 (Alice Wonderland@Lich), keys pinned", listed);
            Assert.Contains("user 77777 (Alice Hearts@Lich), keys not pinned yet", listed);
        } finally {
            Database.ReleasePooledConnections(this._path);
        }
    }

    /// <summary>The settings list user IDs only: anything else stops the server starting, and the commands running.</summary>
    [Fact]
    public async Task OnlyUserIdsCanBeListed() {
        Assert.Null(new OperatorOptions { Characters = ["31337", " 55555 ", ""] }.Problem());
        Assert.Equal([31337L, 55555L], new OperatorOptions { Characters = ["31337", " 55555 ", "", "31337"] }.UserIds);
        Assert.Contains("\"Alice Wonderland@Lich\"", new OperatorOptions { Characters = ["Alice Wonderland@Lich"] }.Problem());
        Assert.NotNull(new OperatorOptions { Characters = ["-5"] }.Problem());
        Assert.NotNull(new OperatorOptions { Characters = ["0"] }.Problem());

        await ExitCodeGate.WaitAsync(Ct);
        var exitCode = Environment.ExitCode;
        var directory = Path.Combine(Path.GetTempPath(), "lgt-" + Guid.NewGuid().ToString("N"));
        try {
            var logs = new CapturingLoggerProvider();
            var failed = await Record.ExceptionAsync(async () => {
                await using var server = new Harness(directory, logs: logs, settings: ("LookingGlass:Operators:Characters:0", "Alice Wonderland@Lich"));
                await using var raw = await server.ConnectRawAsync();
            });
            Assert.NotNull(failed);
            Assert.Contains("LookingGlass:Operators:Characters", Assert.Single(logs.AtLeast(Microsoft.Extensions.Logging.LogLevel.Critical)));

            Environment.ExitCode = 0;
            RunServer("--operators", $"--LookingGlass:DataDirectory={this._directory}", "--LookingGlass:Operators:Characters:0=Alice");
            Assert.Equal(1, Environment.ExitCode);
            Environment.ExitCode = 0;
            RunServer("--operators", $"--LookingGlass:DataDirectory={this._directory}", "--LookingGlass:Operators:Characters:0=31337");
            Assert.Equal(0, Environment.ExitCode);
            RunServer("--operator-pin", "31337", $"--LookingGlass:DataDirectory={this._directory}");
            Assert.Equal(2, Environment.ExitCode);
            Environment.ExitCode = 0;
            // The fingerprint, a value with spaces, doesn't upset the settings read from the command line.
            RunServer("--operator-pin", "31337", "--fingerprint", this.FingerprintOf(Alice), $"--LookingGlass:DataDirectory={this._directory}",
                "--LookingGlass:Operators:Characters:0=31337");
            Assert.Equal(0, Environment.ExitCode);
            Assert.Equal(OperatorStatus.Operator, this.Actions().StatusOf(Alice));
            // --audit, like --bans, has no value: the setting after it is still read as a setting.
            RunServer("--audit", $"--LookingGlass:DataDirectory={this._directory}");
            Assert.Equal(0, Environment.ExitCode);
        } finally {
            Environment.ExitCode = exitCode;
            ExitCodeGate.Release();
            DeleteDirectory(directory);
        }
    }

    private static void RunServer(params string[] args) {
        var result = typeof(Program).Assembly.EntryPoint!.Invoke(null, [args]);
        if (result is Task task) {
            task.GetAwaiter().GetResult();
        }
    }

    private static void ExecuteSql(string path, string sql) {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
