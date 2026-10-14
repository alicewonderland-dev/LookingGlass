using System.Globalization;
using System.Text;

namespace LookingGlass.Core.SelfTest;

/// <summary>What the report's header says about the setup: no names.</summary>
public sealed record SelfTestReportSetup(string PluginVersion, bool ChatTwo, bool AdvancedMode, PlaceKind Place, bool Partner = false);

/// <summary>
/// The self-test's report (docs/self-test-plan.md, "The report"): copyable text in pending-tests.md's "How to report"
/// shape, the checklist and steps, passed, failed or skipped, and why. No character, world or channel names, no message
/// text, no run code; times to the minute with the time zone, so the owner finds the log lines by them. The tester's own
/// notes are copied as typed, but anything the runner knows to be a name is taken out of the whole text anyway
/// (<see cref="Scrub"/>).
/// </summary>
public static class SelfTestReport {
    public const string Heading = "LookingGlass self-test report";

    /// <summary>What stands in for a name the scrub took out.</summary>
    public const string Hidden = "(a name)";

    /// <summary>The report's text.</summary>
    /// <param name="checks">Each check run, with its record (in the order run).</param>
    /// <param name="cleanUp">The clean-up's line (<see cref="SelfTestWords.CleanUp"/>), or null if it hasn't run.</param>
    /// <param name="forbidden">
    /// Names and texts that must never be in it, taken out of the whole text: the tester's (and a partner's) name and world,
    /// names typed in for a step, the run's code and marker.
    /// </param>
    /// <param name="inNotes">
    /// Names taken out of the tester's notes only (every channel's name): the rest of the report is fixed words, where a
    /// channel called "chat" mustn't turn "chat history" into nonsense.
    /// </param>
    public static string Build(SelfTestReportSetup setup, DateTimeOffset started, DateTimeOffset? ended, TimeZoneInfo zone,
        IEnumerable<(SelfTestCheck Check, CheckRecord Record)> checks, string? cleanUp, IEnumerable<string> forbidden, IEnumerable<string>? inNotes = null) {
        var everywhere = forbidden.ToList();
        var notes = everywhere.Concat(inNotes ?? []).ToList();
        var text = new StringBuilder();
        text.AppendLine(Heading);
        text.AppendLine($"Plugin {setup.PluginVersion}, ChatTwo {(setup.ChatTwo ? "on" : "off")}, {(setup.AdvancedMode ? "advanced" : "simple")} mode, " +
                        $"{SelfTestWords.Place(setup.Place)}, {(setup.Partner ? "with a partner" : "one player")}");
        text.AppendLine(ended is { } end
            ? $"{Time(started, zone, withDate: true)} to {Time(end, zone, withDate: TimeZoneInfo.ConvertTime(end, zone).Date != TimeZoneInfo.ConvertTime(started, zone).Date)} ({Offset(zone, started)})"
            : $"{Time(started, zone, withDate: true)} ({Offset(zone, started)})");
        text.AppendLine();
        foreach (var (check, record) in checks) {
            text.AppendLine(Line(check.Steps, record with { Notes = record.Notes.Select(note => Scrub(note, notes)).ToList() }, zone));
        }

        if (cleanUp != null) {
            text.AppendLine(cleanUp);
        }

        return Scrub(text.ToString().TrimEnd() + Environment.NewLine, everywhere);
    }

    /// <summary>One check's line: "local-chat 32-33: FAILED at 20:11: why (step 32)".</summary>
    public static string Line(string steps, CheckRecord record, TimeZoneInfo zone) {
        var line = new StringBuilder(steps).Append(": ");
        switch (record.Outcome) {
            case CheckOutcome.Passed:
                line.Append(SelfTestWords.Passed);
                var details = new List<string>();
                if (record.NotRun.Count > 0) {
                    details.Add($"not run: {string.Join("; ", record.NotRun)}");
                }

                if (record.Confirmed.Count > 0) {
                    details.Add($"you said yes: {string.Join(", ", record.Confirmed)}");
                }

                details.AddRange(record.Did);

                if (details.Count > 0) {
                    line.Append(" (").Append(string.Join("; ", details)).Append(')');
                }

                break;
            case CheckOutcome.Failed:
                line.Append("FAILED");
                if (record.At is { } at) {
                    line.Append(" at ").Append(Time(at, zone, withDate: false));
                }

                line.Append(": ").Append(record.Why ?? SelfTestWords.SomethingWentWrong);
                if (record.Step != null) {
                    line.Append(" (step ").Append(record.Step).Append(')');
                }

                if (record.NotReached.Count > 0) {
                    line.Append("; not asked: ").Append(string.Join(", ", record.NotReached));
                }

                break;
            case CheckOutcome.Skipped:
                line.Append(SelfTestWords.Skipped).Append(": ").Append(record.Why ?? SelfTestWords.YouSkipped);
                break;
            case CheckOutcome.NotRun:
                line.Append(SelfTestWords.NotRunWord).Append(": ").Append(record.Why ?? SelfTestWords.NothingRan);
                break;
            case CheckOutcome.Stopped:
                line.Append(record.Why ?? SelfTestWords.StoppedByYou);
                if (record.At is { } stoppedAt) {
                    line.Append(" at ").Append(Time(stoppedAt, zone, withDate: false));
                }

                break;
        }

        foreach (var note in record.Notes) {
            line.Append("; your note: \"").Append(note.Replace("\r", " ").Replace("\n", " ")).Append('"');
        }

        return line.ToString();
    }

    /// <summary>The runner's one line per check in dalamud.log: "[selftest] local-chat 32-33: failed (step 32)". No names, no text, no reasons.</summary>
    public static string LogLine(string steps, CheckRecord record) {
        var outcome = record.Outcome switch {
            CheckOutcome.Passed => SelfTestWords.Passed,
            CheckOutcome.Failed => SelfTestWords.Failed,
            CheckOutcome.Skipped => SelfTestWords.Skipped,
            CheckOutcome.NotRun => SelfTestWords.NotRunWord,
            _ => SelfTestWords.StoppedWord,
        };
        return record.Step != null && record.Outcome == CheckOutcome.Failed
            ? $"[selftest] {steps}: {outcome} (step {record.Step}{(record.FailedOn is { } asked ? $": {asked}" : "")})"
            : $"[selftest] {steps}: {outcome}";
    }

    /// <summary>
    /// Whether "Run the failed ones again" offers a check: one that failed or was stopped, and one not run unless every
    /// reason is something a second run doesn't change by itself (ChatTwo on or off, the tester's own history, the game's
    /// version, another data centre, a partner blocked before).
    /// </summary>
    public static bool WorthRunningAgain(CheckRecord record) => record.Outcome switch {
        CheckOutcome.Failed or CheckOutcome.Stopped => true,
        CheckOutcome.NotRun => (record.Why ?? "").Split("; ").Any(why => !Lasting.Contains(why)),
        _ => false,
    };

    /// <summary>Reasons a check can't run that a second run doesn't change by itself.</summary>
    private static readonly HashSet<string> Lasting = [
        SelfTestWords.NeedsChatTwo, SelfTestWords.NeedsChatTwoOff, SelfTestWords.NeedsPartner, SelfTestWords.HistoryWasYours, SelfTestWords.NeedsGateHooks,
        SelfTestWords.NeedsStranger, SelfTestWords.NeedsAnotherCharacter, SelfTestWords.NeedsOtherDataCentre, SelfTestWords.PartnerWasBlocked,
    ];

    /// <summary>
    /// Takes out of <paramref name="text"/> every one of <paramref name="forbidden"/> (ignoring case, as whole words where
    /// they start or end with a letter or digit), longest first.
    /// </summary>
    public static string Scrub(string text, IEnumerable<string> forbidden) {
        foreach (var word in forbidden.Where(word => !string.IsNullOrWhiteSpace(word) && word.Trim().Length >= 2).Select(word => word.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(word => word.Length)) {
            var pattern = (char.IsLetterOrDigit(word[0]) ? @"(?<![\p{L}\p{N}])" : "") + System.Text.RegularExpressions.Regex.Escape(word) +
                          (char.IsLetterOrDigit(word[^1]) ? @"(?![\p{L}\p{N}])" : "");
            text = System.Text.RegularExpressions.Regex.Replace(text, pattern, Hidden, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        return text;
    }

    /// <summary>A time in the computer's zone, to the minute.</summary>
    private static string Time(DateTimeOffset time, TimeZoneInfo zone, bool withDate) {
        var local = TimeZoneInfo.ConvertTime(time, zone);
        return local.ToString(withDate ? "yyyy-MM-dd HH:mm" : "HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>"UTC+02:00", the zone's offset at that time.</summary>
    private static string Offset(TimeZoneInfo zone, DateTimeOffset at) {
        var offset = zone.GetUtcOffset(at);
        return $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset.Duration():hh\\:mm}";
    }
}
