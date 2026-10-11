using LookingGlass.Core.Crypto;
using LookingGlass.Server;
using LookingGlass.Server.Data;
using LookingGlass.Server.Hosting;
using LookingGlass.Server.Services;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// Text from outside in the operator tools: what an operator typed is kept in the audit trail with its control and format
/// characters shown as escapes, and cut to a sensible length; and whatever the commands print (names and reasons from the
/// database too) can't carry a terminal's escape sequences or forge a line.
/// </summary>
public sealed class OperatorTextTests : IDisposable {
    private const string ClearScreen = "\u001b[2J\u001b[31mevil@World";
    private const string Retitle = "1.2.3.4\u001b]0;pwned\u0007";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lgt-optext-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private readonly Database _db;

    public OperatorTextTests() {
        Directory.CreateDirectory(this._directory);
        this._path = Path.Combine(this._directory, "lookingglass.db");
        this._db = new Database(this._path);
    }

    public void Dispose() => DeleteDirectory(this._directory);

    private OperatorActions Actions() => new(this._db, new OperatorSettings(new AbuseOptions(), [], [31337]));

    /// <summary>No character a terminal or a reader would act on or not see: controls (but the line breaks written), format characters, separators.</summary>
    private static void AssertPlain(string text) {
        foreach (var line in text.Split(["\r\n", "\n"], StringSplitOptions.None)) {
            Assert.True(TextSanitizerIsPlain(line), $"Not plain: {VisibleText.Of(line)}");
        }
    }

    private static bool TextSanitizerIsPlain(string line) => line.All(c => !char.IsControl(c) && char.GetUnicodeCategory(c) is not
        (System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator));

    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData("\u001b[2J", "\\u001B[2J")]
    [InlineData("a\nb\rc\td", "a\\u000Ab\\u000Dc\\u0009d")]
    [InlineData("right\u202Eleft", "right\\u202Eleft")]
    [InlineData("zero\u200Bwidth", "zero\\u200Bwidth")]
    [InlineData("line\u2028break", "line\\u2028break")]
    [InlineData("Alice Wonderland@Lich ✓ 😀", "Alice Wonderland@Lich ✓ 😀")]
    [InlineData("tag \U000E0041", "tag \\U000E0041")]
    public void HiddenCharactersAreShownAsEscapes(string text, string shown) {
        Assert.Equal(shown, VisibleText.Of(text));
    }

    [Fact]
    public void BrokenSurrogatesAreShownAsEscapesAndLongTextIsCut() {
        // Not in the theory above: its data can't carry a lone surrogate.
        Assert.Equal("broken \\uD800 pair", VisibleText.Of("broken \uD800 pair"));
        Assert.Equal("end \\uDC00", VisibleText.Of("end \uDC00"));
        Assert.Equal(new string('x', 10) + "…", VisibleText.Of(new string('x', 50), 10));
        Assert.Equal("xx", VisibleText.Of("xx", 10));
    }

    /// <summary>
    /// Whom, as typed, is kept visible and cut in the audit trail, whichever action and whatever the outcome; so are a typed
    /// fingerprint, a refused reason, and who and from where.
    /// </summary>
    [Fact]
    public void TheAuditTrailKeepsWhatWasTypedVisible() {
        using var keys = IdentityKeys.Generate();
        this._db.RegisterUser(31337, "Alice Wonderland", 73, "Lich", keys.ToBundle(), false);
        var actions = this.Actions();
        var actor = new OperatorActor(OperatorTool.WebPage, "alice\u001b[31m@github", "100.64.0.1\u0007");
        Assert.Equal(OperatorRefusal.NoSuchCharacter, actions.Ban(actor, ClearScreen, 1, null).Refusal);
        Assert.Equal(OperatorRefusal.NotATarget, actions.Ban(actor, Retitle, 1, null).Refusal);
        Assert.Equal(OperatorRefusal.NotValid, actions.Ban(actor, "31337", 1, "two\u001b[2Jlines").Refusal);
        Assert.Equal(OperatorRefusal.NotATarget, actions.Lift(actor, Retitle).Refusal);
        Assert.Equal(OperatorRefusal.NoSuchCharacter, actions.AllowKeyLogin(new OperatorActor(OperatorTool.CommandLine, "alice"), ClearScreen).Refusal);
        Assert.Equal(OperatorRefusal.KeysDiffer, actions.PinByFingerprint(new OperatorActor(OperatorTool.CommandLine, "alice"), "31337", "\u001b]0;x\u0007 12345").Refusal);
        Assert.Equal(OperatorRefusal.NotATarget, actions.Ban(actor, new string('9', 5) + new string('x', 10_000), 1, null).Refusal);

        var trail = this._db.GetOperatorLog(0, 100);
        Assert.Equal(7, trail.Count);
        foreach (var entry in trail) {
            AssertPlain(entry.Target + entry.Why + entry.Detail + entry.Who + entry.From);
            Assert.True(entry.Target.Length <= 201, $"{entry.Target.Length} characters kept");
        }

        Assert.Contains("\\u001B[2J\\u001B[31mevil@World", trail.Select(entry => entry.Target));
        Assert.Contains(trail, entry => entry.Target.Contains("1.2.3.4\\u001B]0;pwned\\u0007", StringComparison.Ordinal) && entry.Why.Contains("\\u001B", StringComparison.Ordinal));
        Assert.Contains(trail, entry => entry.Detail.Contains("two\\u001B[2Jlines", StringComparison.Ordinal));
        Assert.Contains(trail, entry => entry.Detail.Contains("\\u001B]0;x\\u0007 12345", StringComparison.Ordinal));
        Assert.All(trail.Where(entry => entry.Tool == "web page"), entry => Assert.Equal(("alice\\u001B[31m@github", "100.64.0.1\\u0007"), (entry.Who, entry.From)));
    }

    /// <summary>
    /// Whatever the commands print is plain, whatever the database holds (a name, a reason, a flag, an audit entry written
    /// by something else): <c>--ban</c>'s refusals, <c>--bans</c>, <c>--audit</c> and <c>--operators</c>.
    /// </summary>
    [Fact]
    public void TheCommandsPrintNothingATerminalWouldActOn() {
        using var keys = IdentityKeys.Generate();
        this._db.RegisterUser(31337, "Evil\u001b[2J\u202EName", 73, "Lich\u0007", keys.ToBundle(), false);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        this._db.AddBan(31337, null, "Reason\u001b]0;pwned\u0007\nInjected line", null, false, now, out _, "web\u001b[31m", "who\u2028else");
        this._db.AddBan(4242, null, "Lifted\u001b[2J", null, false, now - 60, out _);
        this._db.LiftBans(4242, null, now - 30, "plugin", "Some\u001b[1mone");
        this._db.SaveFlag(new FlagRow("user 31337", 31337, null, now, now, "Limit\u001b[5m", "why\nforged"));
        this._db.AddOperatorLog(new OperatorLogRow(0, now, "web page\u001b[2J", "who\u0007", "from\u202E", null, "ban", "target\nforged", "detail\u001b", true, ""));

        foreach (var request in new[] {
                     new BanCommand.Request(BanCommand.BanAction.Ban, ClearScreen, null, null),
                     new BanCommand.Request(BanCommand.BanAction.Ban, Retitle, null, null),
                     new BanCommand.Request(BanCommand.BanAction.Unban, Retitle, null, null),
                     new BanCommand.Request(BanCommand.BanAction.List, null, null, null),
                     new BanCommand.Request(BanCommand.BanAction.Audit, null, null, null),
                     new BanCommand.Request(BanCommand.BanAction.Operators, null, null, null),
                     new BanCommand.Request(BanCommand.BanAction.OperatorPin, "31337", null, null, Fingerprint: "00000 00000 00000 00000 00000"),
                 }) {
            var output = new StringWriter();
            var errors = new StringWriter();
            BanCommand.Run(request, this._path, new AbuseOptions(), output, errors, operators: [31337], who: "alice");
            AssertPlain(output.ToString());
            AssertPlain(errors.ToString());
            Assert.False(string.IsNullOrWhiteSpace(output + errors.ToString()));
            if (request.Action == BanCommand.BanAction.List) {
                Assert.Contains("Evil\\u001B[2J\\u202EName@Lich\\u0007", output.ToString());
                Assert.Contains("Reason\\u001B]0;pwned\\u0007\\u000AInjected line", output.ToString());
                // The line written by the command stays one line.
                Assert.DoesNotContain(output.ToString().Split(Environment.NewLine), line => line.StartsWith("Injected", StringComparison.Ordinal));
            }
        }
    }
}
