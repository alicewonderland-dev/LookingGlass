using LookingGlass.Core.SelfTest;

namespace LookingGlass.Tests;

/// <summary>
/// The self-test's report and log lines (docs/self-test-plan.md, "The report"): the fixed format, and given names, channel
/// names and marker text, none of them appears.
/// </summary>
public sealed class SelfTestReportTests {
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(2), "Test", "Test");
    private static readonly DateTimeOffset Start = new(2026, 10, 13, 18, 5, 0, TimeSpan.Zero);

    private static SelfTestCheck Check(string steps) => new() {
        Id = steps.Replace(' ', '-'), Title = steps, Steps = steps, Mode = CheckMode.Automatic,
        StepList = [new SelfTestStep { Title = "x", Kind = StepKind.Automatic, Run = _ => Task.FromResult(StepResult.Pass()) }],
    };

    private static readonly string[] Forbidden = ["Alice Liddell", "Twintania", "Bob Hatter", "Lich", "Sky Pirates", "LG self-test K7Q2", "K7Q2"];

    [Fact]
    public void TheReportHasTheShapeTheOwnerFilesBy() {
        var checks = new (SelfTestCheck, CheckRecord)[] {
            (Check("local-chat 29"), new CheckRecord { Check = "a", Outcome = CheckOutcome.Passed }),
            (Check("local-chat 30-31"), new CheckRecord { Check = "b", Outcome = CheckOutcome.Passed, NotRun = ["the part next to a stranger"] }),
            (Check("local-chat 32-33"), new CheckRecord {
                Check = "c", Outcome = CheckOutcome.Failed, Why = "\"/s\" with text went to the test channel, not to Say", Step = "32", At = Start.AddMinutes(6),
            }),
            (Check("local-chat 34-36"), new CheckRecord { Check = "d", Outcome = CheckOutcome.Passed, Confirmed = ["Tab", "info bar", "Send Tell"] }),
            (Check("windows-only 17"), new CheckRecord { Check = "e", Outcome = CheckOutcome.Skipped, Why = SelfTestWords.NeedsStrikingDummy }),
            (Check("channel-windows 37c-37f"), new CheckRecord { Check = "f", Outcome = CheckOutcome.Passed, Notes = ["flash is subtle"] }),
        };

        var report = SelfTestReport.Build(new SelfTestReportSetup("0.2.17", false, false, PlaceKind.InnRoom), Start, Start.AddMinutes(36), Zone, checks,
            SelfTestWords.CleanUp(1, 0, 7, 0, true, false), Forbidden);

        Assert.Equal(string.Join(Environment.NewLine,
            "LookingGlass self-test report",
            "Plugin 0.2.17, ChatTwo off, simple mode, an inn room, one player",
            "2026-10-13 20:05 to 20:41 (UTC+02:00)",
            "",
            "local-chat 29: passed",
            "local-chat 30-31: passed (not run: the part next to a stranger)",
            "local-chat 32-33: FAILED at 20:11: \"/s\" with text went to the test channel, not to Say (step 32)",
            "local-chat 34-36: passed (you said yes: Tab, info bar, Send Tell)",
            "windows-only 17: skipped: needs a striking dummy",
            "channel-windows 37c-37f: passed; your note: \"flash is subtle\"",
            "Clean-up: test channel left (deleted); 7 settings put back; your channel windows opened again.",
            ""), report);
    }

    [Fact]
    public void NoNameChannelOrMarkerEverReachesTheReport() {
        // Even if something with a name got into a reason or a note, it is taken out.
        var checks = new (SelfTestCheck, CheckRecord)[] {
            (Check("local-chat 29"), new CheckRecord {
                Check = "a", Outcome = CheckOutcome.Failed, Why = "Alice Liddell@Twintania saw \"LG self-test K7Q2 4, please ignore\" in Sky Pirates",
                Notes = ["bob hatter of lich said hi"],
            }),
        };

        var report = SelfTestReport.Build(new SelfTestReportSetup("0.2.17", true, true, PlaceKind.Apartment, Partner: true), Start, null, Zone, checks, null, Forbidden);

        foreach (var word in Forbidden) {
            Assert.DoesNotContain(word, report, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains(SelfTestReport.Hidden, report);
        Assert.Contains("with a partner", report);
        Assert.Contains("inside an apartment", report);
        // Whole words only: "Lich" is taken out, not the "lich" in other words.
        Assert.Equal("a lichen (a name)", SelfTestReport.Scrub("a lichen Lich", ["Lich"]));
    }

    [Fact]
    public void TheLogLinesHaveNoReasonsNamesOrText() {
        var failed = new CheckRecord { Check = "c", Outcome = CheckOutcome.Failed, Why = "Alice Liddell typed hello", Step = "32", Notes = ["Bob"] };

        Assert.Equal("[selftest] local-chat 32-33: failed (step 32)", SelfTestReport.LogLine("local-chat 32-33", failed));
        Assert.Equal("[selftest] local-chat 29: passed", SelfTestReport.LogLine("local-chat 29", new CheckRecord { Check = "a", Outcome = CheckOutcome.Passed, Notes = ["Alice"] }));
        Assert.Equal("[selftest] windows-only 17: skipped", SelfTestReport.LogLine("windows-only 17", new CheckRecord { Check = "e", Outcome = CheckOutcome.Skipped, Why = "x" }));
        Assert.Equal("[selftest] x 1: stopped", SelfTestReport.LogLine("x 1", new CheckRecord { Check = "e", Outcome = CheckOutcome.Stopped }));
    }

    [Fact]
    public void TimesAreInTheComputersZoneWithItsOffset() {
        var west = TimeZoneInfo.CreateCustomTimeZone("West", TimeSpan.FromHours(-7), "West", "West");
        var report = SelfTestReport.Build(new SelfTestReportSetup("1", false, false, PlaceKind.House), Start, Start.AddHours(8), west, [], null, []);

        Assert.Contains("2026-10-13 11:05 to 19:05 (UTC-07:00)", report);
        Assert.Contains("inside a house", report);
    }
}
