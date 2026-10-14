using System.Globalization;
using LookingGlass.Core.Client;
using LookingGlass.Core.SelfTest;
using LookingGlass.Core.SelfTest.Suites;

namespace LookingGlass.Tests;

/// <summary>
/// The two-player suites (docs/self-test-plan.md, "Group 2"), run whole by a host against a scripted partner that plays
/// along, with a tester who answers every question: every check ends with a result and the run cleans up, every line typed
/// is a test line or a command, never /p by the runner, nothing typed outside a private place, the partner only ever
/// invited, and no name in the report.
/// </summary>
public sealed class SelfTestPairsSuitesTests : IDisposable {
    private readonly FakeSelfTestHost _host = new();
    private readonly ScriptedPartner _partner;
    private int _marker;

    public SelfTestPairsSuitesTests() {
        this._partner = new ScriptedPartner(this._host) { Answer = this.PlayAlong };
    }

    public void Dispose() => this._host.Dispose();

    /// <summary>Every two-player task the plan covers, by its checklist steps, as the report names them.</summary>
    private static readonly string[] Covered = [
        "local-chat 3d", "local-chat 4-7", "local-chat 7a", "local-chat 8-11", "local-chat 14 (first part)", "local-chat 15-16", "local-chat 17-18", "local-chat 19-23",
        "local-chat 25 (players)", "local-chat 47", "local-chat 32 (/p brb)", "windows-only 12 (the invite line)",
        "chat-links 16-17", "chat-links 19",
        "channel-windows 17b, 17d; windows-only 3, 6", "channel-windows 33c (second part)", "channel-windows 33f (second part)", "channel-windows 36b (group 1)",
        "windows-only 3-4, 6, 10", "windows-only 15b, 25", "windows-only 18-19", "windows-only 22",
        "name-colours 1-6 (group 1)", "name-colours 7-15", "name-colours 16-19", "name-colours 20", "name-colours 22", "custom-colours 9 (caught-up part)",
        "context-invites 1, 3, 5", "context-invites 2, 4", "context-invites 6 (group 1)", "context-invites 12-14, 16, 18",
        "chat-links 27", "chat-links 28", "chat-links 29",
        "chat-log 2", "chat-log 4, 6-7 (B's parts)", "chat-log 10-11", "chat-log 19", "chat-log 29",
        "sticky-channel 30, 30b, 32b",
    ];

    [Fact]
    public void EveryCoveredTaskHasACheckThatNeedsThePartner() {
        var suites = SelfTestSuites.Pairs();
        var checks = suites.SelectMany(suite => suite.Checks).ToList();

        Assert.Equal(Covered.Order(), checks.Select(check => check.Steps).Order());
        var every = SelfTestSuites.Every().SelectMany(suite => suite.Checks).Select(check => check.Id).ToList();
        Assert.Equal(every.Count, every.Distinct().Count());
        Assert.All(checks, check => Assert.True(check.Needs.HasFlag(SelfTestNeeds.Partner), check.Id));
        Assert.All(SelfTestSuites.Every(), suite => Assert.All(suite.Planned, planned => Assert.True(planned.Value <= SelfTestPacer.Rules[planned.Key].RunMost)));
        Assert.Equal(SelfTestSuites.Every().Count, SelfTestSuites.Every().Select(suite => suite.Id).Distinct().Count());
        // Pairing comes first in a host's run, once.
        Assert.Equal([PairingSuite.Id, LocalTogetherSuite.Id], SelfTestSuites.ForHost([PairingSuite.Create(), LocalTogetherSuite.Create()]).Select(suite => suite.Id));
    }

    /// <summary>The partner's plugin, playing along: lines where asked (delivered to the host), plausible reports, yes to every ask.</summary>
    private PartnerAnswer? PlayAlong(PartnerRequest request, string channel) {
        Dictionary<string, string> fields = [];
        switch (request.Verb) {
            case PartnerVerb.Send:
            case PartnerVerb.SendWhileAway:
            case PartnerVerb.Local: {
                var target = request.Verb == PartnerVerb.Local ? null
                    : request.Option("in") is { } letter ? this._partner.ChannelOf(letter) ?? channel : channel;
                for (var i = 0; i < request.Count; i++) {
                    var text = SelfTestProtocol.PartnerMarker("K7Q2", ++this._marker);
                    var links = request.Option("link") == "item" ? new ChatLink[] { new ChatLink.Item(4551) } : [];
                    this._host.Deliver(target, ScriptedPartner.Id, ScriptedPartner.Who, text, caughtUp: request.Verb == PartnerVerb.SendWhileAway, links: links);
                    this._host.Print(new TapLine(target == null ? TapKind.LocalMessage : TapKind.Message, null, text, target == null ? LocalChat.Tag : "[LGC50]", target));
                }

                fields["sent"] = request.Count.ToString(CultureInfo.InvariantCulture);
                break;
            }

            case PartnerVerb.ReportSaw:
                fields["in"] = "lookingglass";
                fields["printed"] = "yes";
                fields["tag"] = "Local";
                break;
            case PartnerVerb.ReportReceived:
                fields["got"] = "2";
                fields["links"] = "item.pf";
                fields["you"] = "1";
                fields["warn"] = "0";
                break;
            case PartnerVerb.ReportCheckCode:
                fields["check"] = "41:12345.67890.13579.24680.11223";
                break;
            case PartnerVerb.ReportLog:
                fields["errors"] = fields["warnings"] = fields["forbidden"] = fields["jargon"] = "0";
                break;
            case PartnerVerb.Ask:
                if (request.Args[0] is "say-hello" or "say-hello-chattwo") {
                    var line = $"p{++this._marker}";
                    fields["line"] = line;
                    this._host.GameChat(GameChatKind.Say, $"LG self-test K7Q2 {line}, please ignore", fromTester: false);
                }

                return new PartnerAnswer(request.Number, PartnerOutcome.Yes, fields);
            case PartnerVerb.Hello:
            case PartnerVerb.StillThere:
                return ScriptedPartner.Default(request, this._partner.Facts);
        }

        return new PartnerAnswer(request.Number, PartnerOutcome.Done, fields);
    }

    [Fact]
    public async Task EveryTwoPlayerSuiteRunsToTheEndTypingOnlyTestLinesInAPrivatePlace() {
        var suites = SelfTestSuites.ForHost(SelfTestSuites.Pairs());
        this._host.Place = FakeSelfTestHost.House;
        this._host.HistoryEmpty = true;
        this._host.Party = (2, true);
        this._host.CheckCodes["channel1"] = "#41 12345 67890 13579 24680 11223";
        var pacer = new SelfTestPacer(() => this._host.Now, this._host.Delay, role: SelfTestRole.Host);
        var data = SelfTestRun.NewData("K7Q2", 9, "wss://example.test/ws", this._host.Now, suites, role: SelfTestRole.Host, other: ScriptedPartner.Who);
        var run = new SelfTestRun(this._host, suites, pacer, this._host.File, data);
        var shown = new HashSet<string>();
        this._host.BeforeType = () => this._host.RecordIfOutside("line");
        this._host.OnTick = () => {
            var view = run.View;
            if (view.Prompt is { } prompt) {
                shown.Add(prompt.Prompt.Text);
            }

            switch (view.State) {
                case SelfTestState.WaitingToCarryOn:
                    this._host.Logins++;
                    break;
                case SelfTestState.NeedsYou when view.Prompt is { } waiting:
                    run.Answer(waiting.Prompt.Kind == PromptKind.YesNo ? GuidedAnswer.Yes : waiting.Prompt.Kind == PromptKind.SkipOnly ? GuidedAnswer.Skip : GuidedAnswer.Done);
                    break;
            }
        };

        await run.RunAsync();

        Assert.Equal(SelfTestState.Done, run.View.State);
        Assert.True(suites.Sum(suite => suite.Checks.Count) == run.Data.Results.Count,
            string.Join("; ", run.Data.Results.Select(result => $"{result.Check}: {result.Outcome} {result.Why}")));
        // Paired, and the partner never gone: most checks ran rather than being skipped for want of the partner.
        Assert.Equal(CheckOutcome.Passed, run.Data.Results.Single(result => result.Check == "pairing").Outcome);
        Assert.False(run.Data.Results.Any(result => result.Why is SelfTestWords.PartnerGone or SelfTestWords.PartnerDidntJoin or SelfTestWords.Meddling),
            string.Join("; ", run.Data.Results.Select(result => $"{result.Check}: {result.Outcome} {result.Why}")));
        Assert.Empty(this._host.TypedOutside);
        Assert.All(this._host.Typed, typed => Assert.True(typed.Line.Length == 0 || typed.Line.StartsWith('/') || typed.Line.StartsWith(run.MarkerPrefix, StringComparison.Ordinal),
            $"Typed something that isn't a test line or a command: {typed.Line}"));
        // The runner never types to Party (the tester does, as asked, only with the two of them in it), nor anywhere else no test should reach.
        Assert.DoesNotContain(this._host.Typed, typed => typed.Line.StartsWith("/p ", StringComparison.Ordinal) || typed.Line.StartsWith("/sh ", StringComparison.Ordinal)
            || typed.Line.StartsWith("/fc ", StringComparison.Ordinal) || typed.Line.StartsWith("/t ", StringComparison.Ordinal));
        // Invited: the partner only.
        Assert.All(this._host.Invited, invited => Assert.Equal("Bob Hatter", invited.Name));
        // The clean-up: the partner told bye, every channel left (none disbanded: the partner had gone), every setting back, the block undone.
        Assert.Equal(PartnerVerb.Bye, this._partner.Requests[^1].Verb);
        Assert.Empty(this._host.Disbanded);
        Assert.False(this._host.File.Exists);
        Assert.Empty(this._host.Blocked);
        Assert.All(Enum.GetValues<SelfTestSetting>(), setting => Assert.Equal(this._host.Before[setting], this._host.Settings[setting].GetRawText()));
        Assert.DoesNotContain(this._host.Channels, channel => SelfTestRun.IsTestChannelName(channel.Name));

        var report = SelfTestReport.Build(new SelfTestReportSetup("0.2.17", false, false, PlaceKind.House, Partner: true), run.View.StartedAt, run.View.EndedAt, this._host.TimeZone,
            run.View.Checks.Where(check => check.Result != null).Select(check => (check.Check, check.Result!)), run.View.CleanUp,
            [ScriptedPartner.Who, "Bob Hatter", "Lich", run.MarkerPrefix, "K7Q2"]);
        Assert.DoesNotContain("Bob", report);
        Assert.DoesNotContain("K7Q2", report);
        Assert.Contains("with a partner", report);
        Assert.DoesNotContain(this._host.Logged, line => line.Contains("Bob", StringComparison.Ordinal));

        // Plain words: every question, every reason and part not run, every step's title.
        var words = shown.Concat(run.Data.Results.SelectMany(result => result.NotRun.Append(result.Why ?? "")))
            .Concat(suites.SelectMany(suite => suite.Checks).SelectMany(check => check.StepList).Where(step => step.Kind != StepKind.CarryOnAfter).Select(step => step.Title))
            .Concat(suites.Select(suite => suite.Title)).Where(text => text.Length > 0);
        Assert.All(words, text => Assert.True(PlainLanguage.Jargon(text).Count == 0, $"Not plain: {text}"));
        Assert.All(shown, text => Assert.DoesNotContain("{", text));
    }
}
