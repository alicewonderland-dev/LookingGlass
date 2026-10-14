using LookingGlass.Core.SelfTest;
using LookingGlass.Core.SelfTest.Suites;

namespace LookingGlass.Tests;

/// <summary>
/// The two-player protocol (docs/self-test-plan.md, "The protocol"): parsing and writing every request and answer; lines
/// without the run's exact prefix, malformed, too long, or past the caps are refused; a partner's test lines are told apart.
/// </summary>
public sealed class SelfTestProtocolTests {
    private const string Code = "K7Q2";

    [Fact]
    public void EveryRequestRoundTrips() {
        var requests = new[] {
            PartnerRequest.Of(1, PartnerVerb.Hello),
            PartnerRequest.Of(2, PartnerVerb.StillThere),
            PartnerRequest.Of(3, PartnerVerb.Send, "2", "link=item"),
            PartnerRequest.Of(4, PartnerVerb.Send, "1", "in=b"),
            PartnerRequest.Of(5, PartnerVerb.SendWhileAway, "2"),
            PartnerRequest.Of(6, PartnerVerb.Local, "3"),
            PartnerRequest.Of(7, PartnerVerb.ReportReceived),
            PartnerRequest.Of(8, PartnerVerb.ReportCheckCode),
            PartnerRequest.Of(9, PartnerVerb.ReportSaw, "12"),
            PartnerRequest.Of(10, PartnerVerb.ReportWindows),
            PartnerRequest.Of(11, PartnerVerb.ReportLog),
            PartnerRequest.Of(12, PartnerVerb.Leave),
            PartnerRequest.Of(19, PartnerVerb.Leave, "in=b"),
            PartnerRequest.Of(20, PartnerVerb.ReportCheckCode, "in=c"),
            PartnerRequest.Of(13, PartnerVerb.WithdrawLocal),
            PartnerRequest.Of(14, PartnerVerb.RestoreLocal),
            PartnerRequest.Of(15, PartnerVerb.Disconnect, "60"),
            PartnerRequest.Of(16, PartnerVerb.Ask, "say-hello"),
            PartnerRequest.Of(17, PartnerVerb.InviteMe),
            PartnerRequest.Of(18, PartnerVerb.Bye),
            PartnerRequest.Of(21, PartnerVerb.KeepAlive),
        };

        foreach (var request in requests) {
            var line = SelfTestProtocol.Write(Code, request);
            Assert.StartsWith("LG self-test K7Q2 #", line);
            Assert.True(line.Length <= SelfTestProtocol.MaxLength, line);
            var parsed = Assert.IsType<ProtocolLine.Request>(SelfTestProtocol.Parse(Code, line));
            Assert.Equal(request, parsed.Value);
        }

        Assert.Equal("LG self-test K7Q2 #3 send 2 link=item", SelfTestProtocol.Write(Code, PartnerRequest.Of(3, PartnerVerb.Send, "2", "link=item")));
    }

    [Fact]
    public void AnswersRoundTripWithFixedFields() {
        var answer = new PartnerAnswer(12, PartnerOutcome.Done, new Dictionary<string, string> { ["got"] = "0", ["sent"] = "2" });
        var line = SelfTestProtocol.Write(Code, answer);

        Assert.Equal("LG self-test K7Q2 #12 done got=0 sent=2", line);
        var parsed = Assert.IsType<ProtocolLine.Answer>(SelfTestProtocol.Parse(Code, line)).Value;
        Assert.Equal(12, parsed.Number);
        Assert.Equal(PartnerOutcome.Done, parsed.Outcome);
        Assert.Equal("2", parsed.Fields["sent"]);
        Assert.Equal(2, parsed.Int("sent"));
        Assert.Null(parsed.Int("missing"));

        foreach (var outcome in Enum.GetValues<PartnerOutcome>()) {
            // "away" and "back" are a tester's own lines: numbered 0 only.
            var number = outcome is PartnerOutcome.Away or PartnerOutcome.Back ? 0 : 5;
            var written = SelfTestProtocol.Write(Code, new PartnerAnswer(number, outcome, new Dictionary<string, string>()));
            Assert.Equal(outcome, Assert.IsType<ProtocolLine.Answer>(SelfTestProtocol.Parse(Code, written)).Value.Outcome);
        }

        // A check code's five groups of digits, as one field.
        var code = new PartnerAnswer(13, PartnerOutcome.Done, new Dictionary<string, string> { ["check"] = "41:12345.67890.13579.24680.11223" });
        Assert.Equal("41:12345.67890.13579.24680.11223", Assert.IsType<ProtocolLine.Answer>(SelfTestProtocol.Parse(Code, SelfTestProtocol.Write(Code, code))).Value.Fields["check"]);
    }

    [Fact]
    public void ATestersOwnLinesAreHelloAwayAndBack() {
        var hello = SelfTestProtocol.Write(Code, new PartnerAnswer(0, PartnerOutcome.Hello, new Dictionary<string, string> { ["v"] = "0.2.17", ["private"] = "yes" }));
        Assert.Equal(PartnerOutcome.Hello, Assert.IsType<ProtocolLine.Answer>(SelfTestProtocol.Parse(Code, hello)).Value.Outcome);

        // Out of the private place (or about to relog), and back inside (owner, 2026-10-13): exactly these, nothing more.
        var away = SelfTestProtocol.Write(Code, new PartnerAnswer(0, PartnerOutcome.Away, new Dictionary<string, string>()));
        var back = SelfTestProtocol.Write(Code, new PartnerAnswer(0, PartnerOutcome.Back, new Dictionary<string, string>()));
        Assert.Equal("LG self-test K7Q2 #0 away", away);
        Assert.Equal("LG self-test K7Q2 #0 back", back);
        Assert.Equal(PartnerOutcome.Away, Assert.IsType<ProtocolLine.Answer>(SelfTestProtocol.Parse(Code, away)).Value.Outcome);
        var parsed = Assert.IsType<ProtocolLine.Answer>(SelfTestProtocol.Parse(Code, back)).Value;
        Assert.Equal((0, PartnerOutcome.Back, 0), (parsed.Number, parsed.Outcome, parsed.Fields.Count));
    }

    [Theory]
    [InlineData("LG self-test K7Q2 #1 away")]
    [InlineData("LG self-test K7Q2 #12 back")]
    [InlineData("LG self-test K7Q2 #0 away for=5")]
    [InlineData("LG self-test K7Q2 #0 back place=house")]
    [InlineData("LG self-test K7Q2 #0 away now")]
    [InlineData("LG self-test K7Q2 #0 back ")]
    [InlineData("LG self-test K7Q2 #00 away")]
    [InlineData("LG self-test K7Q2 #00 back x")]
    [InlineData("LG self-test K7Q2 #0 Away")]
    [InlineData("LG self-test K7Q2 #0 away, please ignore")]
    [InlineData("LG self-test K7Q2 # away")]
    public void AwayAndBackAreReadStrictly(string line) {
        var parsed = SelfTestProtocol.Parse(Code, line);
        Assert.True(parsed is ProtocolLine.Malformed, $"{line} parsed as {parsed}");
    }

    [Fact]
    public void AwayAndBackAreCapped() {
        Assert.Equal(40, SelfTestProtocol.MostPresenceLines);
        // The host plans them, with its hello and "alive" lines, in pairing's budget, within its 150 protocol lines.
        var planned = PairingSuite.Create().Planned[SelfTestAction.PartnerMessage];
        Assert.Equal(3 + SelfTestProtocol.MostKeepAlives + SelfTestProtocol.MostPresenceLines, planned);
        Assert.True(planned <= new SelfTestPacer(() => DateTimeOffset.UnixEpoch, (_, _) => Task.CompletedTask, role: SelfTestRole.Host).MostOf(SelfTestAction.PartnerMessage));
    }

    [Fact]
    public void TestLinesAreToldApartFromControlLines() {
        Assert.Equal("LG self-test K7Q2 p4, please ignore", SelfTestProtocol.PartnerMarker(Code, 4));
        Assert.IsType<ProtocolLine.TestLine>(SelfTestProtocol.Parse(Code, "LG self-test K7Q2 p4, please ignore"));
        Assert.IsType<ProtocolLine.TestLine>(SelfTestProtocol.Parse(Code, "LG self-test K7Q2 4, please ignore"));
        // An item link's name after it is still a test line.
        Assert.IsType<ProtocolLine.TestLine>(SelfTestProtocol.Parse(Code, "LG self-test K7Q2 p5 [Potion], please ignore"));
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("LG self-test ABCD #1 hello")]
    [InlineData("lg self-test K7Q2 #1 hello")]
    [InlineData("LG self-test K7Q2 #1 fly away")]
    [InlineData("LG self-test K7Q2 #x hello")]
    [InlineData("LG self-test K7Q2 #-1 hello")]
    [InlineData("LG self-test K7Q2 #99999999999 hello")]
    [InlineData("LG self-test K7Q2 #1 send")]
    [InlineData("LG self-test K7Q2 #1 send 6")]
    [InlineData("LG self-test K7Q2 #1 send 0")]
    [InlineData("LG self-test K7Q2 #1 send 2 link=gil")]
    [InlineData("LG self-test K7Q2 #1 send 2 in=z")]
    [InlineData("LG self-test K7Q2 #1 local 4")]
    [InlineData("LG self-test K7Q2 #1 disconnect 61")]
    [InlineData("LG self-test K7Q2 #1 ask open-the-bank")]
    [InlineData("LG self-test K7Q2 #1 hello extra")]
    [InlineData("LG self-test K7Q2 #1 done sent=<script>")]
    [InlineData("LG self-test K7Q2 #1 done sent")]
    [InlineData("LG self-test K7Q2 #1 report")]
    [InlineData("LG self-test K7Q2 #1 report saw abc")]
    [InlineData("LG self-test K7Q2 #1 leave in=d")]
    [InlineData("LG self-test K7Q2 #1 alive now")]
    [InlineData("LG self-test K7Q2 #1 leave in=b in=c")]
    [InlineData("LG self-test K7Q2 #1 leave link=item")]
    public void AnythingElseIsRefused(string line) {
        var parsed = SelfTestProtocol.Parse(Code, line);
        Assert.True(parsed is ProtocolLine.NotOurs or ProtocolLine.Malformed, $"{line} parsed as {parsed}");
    }

    [Fact]
    public void TooLongIsMalformedAndOthersRunsAreNotOurs() {
        Assert.IsType<ProtocolLine.Malformed>(SelfTestProtocol.Parse(Code, "LG self-test K7Q2 #1 done " + string.Join(" ", Enumerable.Range(0, 60).Select(i => $"k{i}=1"))));
        Assert.IsType<ProtocolLine.NotOurs>(SelfTestProtocol.Parse(Code, "LG self-test ABCD #1 hello"));
        Assert.IsType<ProtocolLine.NotOurs>(SelfTestProtocol.Parse(Code, "pulling at 9"));
        // Ours, but nothing the protocol knows: malformed (the partner ignores it; from someone else it ends the run).
        Assert.IsType<ProtocolLine.Malformed>(SelfTestProtocol.Parse(Code, "LG self-test K7Q2 #1 fly away"));
        Assert.True(SelfTestProtocol.IsOurs(Code, "LG self-test K7Q2 anything"));
        Assert.False(SelfTestProtocol.IsOurs(Code, "LG self-test K7Q22 anything"));
    }

    [Fact]
    public void ThePartnersCapsAreFixed() {
        Assert.Equal(5, SelfTestProtocol.MostPerSend);
        Assert.Equal(3, SelfTestProtocol.MostPerLocal);
        Assert.Equal(60, SelfTestProtocol.MostDisconnectSeconds);
        Assert.Equal(5, SelfTestProtocol.MostLeaves);
        Assert.Equal(3, SelfTestProtocol.MostDisconnects);
        Assert.Equal(TimeSpan.FromSeconds(60), SelfTestProtocol.AnswerWithin);
        Assert.Equal(TimeSpan.FromMinutes(10), SelfTestProtocol.GuidedAnswerWithin);
        Assert.Equal(TimeSpan.FromMinutes(45), SelfTestProtocol.PartnerGivesUpOffline);
        Assert.Equal(TimeSpan.FromMinutes(60), SelfTestProtocol.PartnerGivesUpSilent);
        Assert.Equal(TimeSpan.FromMinutes(5), SelfTestProtocol.KeepAliveEvery);
        Assert.Equal(30, SelfTestProtocol.MostKeepAlives);
    }
}
