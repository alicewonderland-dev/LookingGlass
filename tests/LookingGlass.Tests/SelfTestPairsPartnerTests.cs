using LookingGlass.Core.Client;
using LookingGlass.Core.SelfTest;
using LookingGlass.Core.SelfTest.Suites;

namespace LookingGlass.Tests;

/// <summary>
/// A host as a partner test needs it: invites, then sends one request after each answer (as raw lines, so anything can be
/// tried), and records the partner's answers. Everything happens inside the partner's own flow.
/// </summary>
internal sealed class ScriptedHost {
    public const long Id = 7;
    public const string Who = "Hal Host@Lich";
    public const string Channel = "host-channel";
    public const string Code = "K7Q2";

    private readonly FakeSelfTestHost _host;
    private int _number;

    public ScriptedHost(FakeSelfTestHost host, params string[] requests) {
        this._host = host;
        this.Requests = new Queue<string>(requests);
        host.Channels.Add(new SelfTestChannel("mine", "My own channel", 3));
        host.OnSent = this.Sent;
    }

    public Queue<string> Requests { get; }

    public List<PartnerAnswer> Answers { get; } = [];

    /// <summary>Told each answer (after it is recorded), before the next request goes.</summary>
    public Action<PartnerAnswer>? AfterAnswer { get; set; }

    public void Invite(string channel = Channel, string? name = null, string from = Who, bool verified = true) {
        this._host.Join(channel, Id, Who);
        this._host.Invites.Add(new SelfTestInvite(channel, name ?? SelfTestRun.ChannelName(Code, 0), from, verified));
    }

    public void Say(string text, string? channel = Channel, IReadOnlyList<ChatLink>? links = null, bool caughtUp = false) =>
        this._host.Deliver(channel, Id, Who, text, links: links, caughtUp: caughtUp);

    /// <summary>The next request, numbered after the last.</summary>
    public void Next() {
        if (this.Requests.TryDequeue(out var request)) {
            this.Say($"LG self-test {Code} #{++this._number} {request}");
        }
    }

    private void Sent(string channel, string text) {
        if (SelfTestProtocol.Parse(Code, text) is not ProtocolLine.Answer { Value: var answer }) {
            return;
        }

        this.Answers.Add(answer);
        this.AfterAnswer?.Invoke(answer);
        this.Next();
    }
}

/// <summary>
/// Two players, the partner's side (docs/self-test-plan.md, "Two players"), against a scripted host: only the host's invite
/// to the host's channel is accepted; every request on the list answered within its cap, nothing else; its player asked
/// behind the gate, never typing for them; someone meddling, silence, and "bye" end it; the clean-up only leaves.
/// </summary>
public sealed class SelfTestPairsPartnerTests : IDisposable {
    private readonly FakeSelfTestHost _host = new();
    private SelfTestRun? _run;

    public void Dispose() => this._host.Dispose();

    private SelfTestRun NewRun() {
        SelfTestSuite[] suites = [PartnerSuite.Create()];
        var pacer = new SelfTestPacer(() => this._host.Now, this._host.Delay, role: SelfTestRole.Partner);
        var data = SelfTestRun.NewData(ScriptedHost.Code, 9, "wss://example.test/ws", this._host.Now, suites, role: SelfTestRole.Partner, other: ScriptedHost.Who);
        this._run = new SelfTestRun(this._host, suites, pacer, this._host.File, data);
        return this._run;
    }

    private static CheckRecord Result(SelfTestRun run) => run.Data.Results.Single(result => result.Check == "partner");

    private static PartnerAnswer Of(ScriptedHost host, int number) => host.Answers.Single(answer => answer.Number == number);

    [Fact]
    public async Task ItJoinsSaysHelloAndAnswersEachRequest() {
        var host = new ScriptedHost(this._host, "send 2 link=item", "local 1", "report check-code", "report received", "leave", "send 1", "ask say-hello",
            "report saw 3", "bye");
        this._host.CheckCodes[ScriptedHost.Channel] = "#41 12345 67890 13579 24680 11223";
        host.AfterAnswer = answer => {
            if (answer.Number == 2) {
                // The host's own test line, with an item link, before "report received"; and seen in the partner's game chat.
                host.Say("LG self-test K7Q2 3, please ignore", links: [new ChatLink.Item(4551)]);
                this._host.GameChat(GameChatKind.Say, "LG self-test K7Q2 3, please ignore", fromTester: false);
            } else if (answer.Number == 5) {
                // Left: invited back.
                host.Invite();
            }
        };
        host.Invite();
        this._host.OnTick = () => {
            if (this._run?.View.Prompt != null) {
                this._run.Answer(GuidedAnswer.Done);
            }
        };
        var run = this.NewRun();

        await run.RunAsync();

        Assert.Equal((ScriptedHost.Who, "K7Q2", SelfTestRole.Partner), this._host.Paired);
        var hello = host.Answers[0];
        Assert.Equal(PartnerOutcome.Hello, hello.Outcome);
        Assert.True(hello.Is("private", "yes"));
        Assert.True(hello.Is("place", "innroom"));
        Assert.Equal(2, Of(host, 1).Int("sent"));
        Assert.Equal([("LG self-test K7Q2 p1, please ignore", "item"), ("LG self-test K7Q2 p2, please ignore", "item")],
            this._host.SentWithLink.Select(sent => (sent.Text, sent.Link)).ToArray());
        Assert.Equal(["LG self-test K7Q2 p3, please ignore"], this._host.LocalSent);
        Assert.Equal("41:12345.67890.13579.24680.11223", Of(host, 3).Fields["check"]);
        Assert.Equal(1, Of(host, 4).Int("got"));
        Assert.Equal("item", Of(host, 4).Fields["links"]);
        Assert.Equal(2, this._host.Accepted.Count);
        Assert.Equal(1, Of(host, 6).Int("sent"));
        Assert.Equal(PartnerOutcome.Yes, Of(host, 7).Outcome);
        Assert.Equal("p5", Of(host, 7).Fields["line"]);
        Assert.Equal("say", Of(host, 8).Fields["in"]);
        Assert.Equal(PartnerOutcome.Done, Of(host, 9).Outcome);
        // Never typed in game chat by itself: its player was asked.
        Assert.Empty(this._host.Typed);
        // The clean-up only left the host's channel (as asked once, and at the end); nothing disbanded.
        Assert.Equal([ScriptedHost.Channel, ScriptedHost.Channel], this._host.Left);
        Assert.Empty(this._host.Disbanded);
        Assert.False(this._host.File.Exists);
        var result = Result(run);
        Assert.Equal(CheckOutcome.Passed, result.Outcome);
        Assert.Contains(result.Did, did => did.StartsWith("answered 9 requests: 3 test lines sent, 1 to local chat", StringComparison.Ordinal));
        Assert.Contains("say-hello", result.Did.Single());
        Assert.DoesNotContain(this._host.Logged, line => line.Contains("Hal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnlyTheHostsInviteToTheHostsChannelIsAccepted() {
        var host = new ScriptedHost(this._host);
        host.Invite("a", from: "Mallory Meddler@Lich");
        host.Invite("b", name: "Raid night");
        host.Invite("c", verified: false);
        host.Invite("d", name: "LG self-test ABCD");
        var run = this.NewRun();

        await run.RunAsync();

        Assert.Empty(this._host.Accepted);
        Assert.Empty(this._host.Sent);
        Assert.Equal(CheckOutcome.Skipped, Result(run).Outcome);
        Assert.Equal(SelfTestWords.NoInvite, Result(run).Why);
    }

    [Fact]
    public async Task ItSendsAtMostEightyTestLines() {
        var host = new ScriptedHost(this._host, [.. Enumerable.Repeat("send 5", 17), "bye"]);
        host.Invite();
        var run = this.NewRun();

        await run.RunAsync();

        Assert.Equal(80, this._host.Sent.Count(sent => SelfTestProtocol.Parse("K7Q2", sent.Text) is ProtocolLine.TestLine));
        Assert.Equal(PartnerOutcome.Refused, Of(host, 17).Outcome);
        Assert.Equal(5, Of(host, 16).Int("sent"));
    }

    [Fact]
    public async Task LeavesAndDisconnectsHaveTheirCaps() {
        var host = new ScriptedHost(this._host, [.. Enumerable.Repeat("leave", 6), .. Enumerable.Repeat("disconnect 5", 4), "bye"]);
        host.AfterAnswer = answer => {
            if (answer.Number <= 6) {
                host.Invite();
            }
        };
        host.Invite();
        var run = this.NewRun();

        await run.RunAsync();

        Assert.Equal(PartnerOutcome.Done, Of(host, 5).Outcome);
        Assert.Equal(PartnerOutcome.Refused, Of(host, 6).Outcome);
        Assert.Equal(PartnerOutcome.Done, Of(host, 9).Outcome);
        Assert.Equal(PartnerOutcome.Refused, Of(host, 10).Outcome);
        Assert.Equal(3, this._host.Ops.Count(op => op == "connect"));
        Assert.True(this._host.Ready);
    }

    [Fact]
    public async Task ALineWithThePrefixFromSomeoneElseStopsIt() {
        var host = new ScriptedHost(this._host);
        host.AfterAnswer = _ => this._host.Deliver(ScriptedHost.Channel, 99, "Mallory Meddler@Lich", "LG self-test K7Q2 #1 send 5");
        host.Invite();
        var run = this.NewRun();

        await run.RunAsync();

        Assert.Equal(CheckOutcome.Stopped, Result(run).Outcome);
        Assert.Equal(SelfTestWords.Meddling, Result(run).Why);
        Assert.DoesNotContain(this._host.Sent, sent => SelfTestProtocol.Parse("K7Q2", sent.Text) is ProtocolLine.TestLine);
        Assert.Contains(ScriptedHost.Channel, this._host.Left);
    }

    [Fact]
    public async Task FifteenMinutesWithoutARequestEndIt() {
        var host = new ScriptedHost(this._host);
        host.Invite();
        var run = this.NewRun();

        await run.RunAsync();

        var result = Result(run);
        Assert.Equal(CheckOutcome.Passed, result.Outcome);
        Assert.Contains(result.NotRun, notRun => notRun.Contains(SelfTestWords.HostSilent, StringComparison.Ordinal));
        Assert.True(this._host.Now - run.View.StartedAt >= SelfTestProtocol.PartnerGivesUpAfter);
        Assert.Contains(ScriptedHost.Channel, this._host.Left);
    }

    [Fact]
    public async Task TheHostsChannelGoingAwayEndsIt() {
        var host = new ScriptedHost(this._host, "report log");
        host.AfterAnswer = answer => {
            if (answer.Number == 1) {
                this._host.Channels.RemoveAll(channel => channel.Id == ScriptedHost.Channel);
            }
        };
        host.Invite();
        var run = this.NewRun();

        await run.RunAsync();

        Assert.Contains(Result(run).NotRun, notRun => notRun.Contains(SelfTestWords.HostLeft, StringComparison.Ordinal));
        Assert.True(this._host.Now - run.View.StartedAt < TimeSpan.FromMinutes(14));
    }

    [Fact]
    public async Task RepeatsOldNumbersAndRequestsOutsideItsChannelsAreIgnored() {
        var host = new ScriptedHost(this._host);
        host.AfterAnswer = answer => {
            if (answer.Number != 0) {
                return;
            }

            host.Say("LG self-test K7Q2 #1 send 1");
            host.Say("LG self-test K7Q2 #1 send 1");
            host.Say("LG self-test K7Q2 #2 send 1", channel: null);
            host.Say("LG self-test K7Q2 #3 send 1", channel: "mine");
            host.Say("LG self-test K7Q2 #4 bye");
        };
        host.Invite();
        var run = this.NewRun();

        await run.RunAsync();

        Assert.Single(this._host.Sent, sent => SelfTestProtocol.Parse("K7Q2", sent.Text) is ProtocolLine.TestLine);
        Assert.Equal([0, 1, 4], host.Answers.Select(answer => answer.Number).ToArray());
    }

    [Fact]
    public async Task LocalChatWaitsForItsOwnGate() {
        var host = new ScriptedHost(this._host, "local 1", "bye");
        this._host.Place = FakeSelfTestHost.House;
        this._host.Others = 1;
        var waitedUntil = DateTimeOffset.MaxValue;
        host.Invite();
        var start = this._host.Now;
        this._host.OnTick = () => {
            if (this._host.Others == 1 && this._host.Now - start > TimeSpan.FromMinutes(3)) {
                this._host.Others = 0;
                waitedUntil = this._host.Now;
            }
        };
        var run = this.NewRun();

        await run.RunAsync();

        Assert.Single(this._host.LocalSent);
        Assert.True(waitedUntil < DateTimeOffset.MaxValue);
        Assert.Equal(1, Of(host, 1).Int("sent"));
    }

    [Fact]
    public async Task ARequestNamesWhichOfTheHostsChannels() {
        var host = new ScriptedHost(this._host, "report check-code in=b", "send 1 in=b", "leave in=b", "report check-code in=b", "send 1 in=c", "bye");
        this._host.CheckCodes["b-channel"] = "#7 11111 22222 33333 44444 55555";
        host.Invite();
        host.Invite("b-channel", SelfTestRun.ChannelName("K7Q2", 1));
        var run = this.NewRun();

        await run.RunAsync();

        Assert.Equal([ScriptedHost.Channel, "b-channel"], this._host.Accepted);
        Assert.Equal("b-channel", this._host.Sent.First(sent => SelfTestProtocol.Parse("K7Q2", sent.Text) is ProtocolLine.TestLine).Channel);
        // Left b only (answering first, in the channel the request came in); then neither b's check code nor a line to c: it isn't in them.
        Assert.Equal("b-channel", this._host.Left[0]);
        Assert.Equal("7:11111.22222.33333.44444.55555", Of(host, 1).Fields["check"]);
        Assert.Equal(PartnerOutcome.Done, Of(host, 3).Outcome);
        Assert.Equal(PartnerOutcome.Refused, Of(host, 4).Outcome);
        Assert.Equal(PartnerOutcome.Refused, Of(host, 5).Outcome);
    }

    [Fact]
    public async Task ItsLocalChatAcceptanceIsPutBack() {
        var host = new ScriptedHost(this._host, "withdraw-local", "withdraw-local", "restore-local", "bye");
        host.Invite();
        var before = this._host.Settings[SelfTestSetting.LocalPrivacyAccepted];
        var run = this.NewRun();

        await run.RunAsync();

        Assert.Equal(PartnerOutcome.Done, Of(host, 1).Outcome);
        Assert.Equal(PartnerOutcome.Refused, Of(host, 2).Outcome);
        Assert.Equal(PartnerOutcome.Done, Of(host, 3).Outcome);
        Assert.Equal(before.GetRawText(), this._host.Settings[SelfTestSetting.LocalPrivacyAccepted].GetRawText());
    }

    [Fact]
    public async Task ItsOneChannelInvitesOnlyTheHostAndGoesAtTheEnd() {
        var host = new ScriptedHost(this._host, "invite-me", "invite-me", "bye");
        host.Invite();
        var run = this.NewRun();

        await run.RunAsync();

        Assert.Equal(PartnerOutcome.Done, Of(host, 1).Outcome);
        Assert.Equal(PartnerOutcome.Refused, Of(host, 2).Outcome);
        Assert.Equal([PartnerSide.OwnChannelName("K7Q2")], this._host.CreatedNames);
        var invited = Assert.Single(this._host.Invited);
        Assert.Equal("Hal Host", invited.Name);
        // Its own channel is the run's to remove: left (nobody else in it) at the clean-up, with the host's.
        Assert.Contains(invited.Channel, this._host.Left);
    }
}
