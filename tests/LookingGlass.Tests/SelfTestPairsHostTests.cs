using LookingGlass.Core.SelfTest;
using LookingGlass.Core.SelfTest.Suites;

namespace LookingGlass.Tests;

/// <summary>
/// Two players, the host's side (docs/self-test-plan.md, "Two players"), against a scripted partner: pairing (one lookup,
/// one invite, the hello within two minutes), requests and their time-outs, still-there and gone, a refusal, someone
/// meddling, the partner's block and channel, and the clean-up: the partner leaves first, the host last, disbanding only
/// if the partner is still in.
/// </summary>
public sealed class SelfTestPairsHostTests : IDisposable {
    private readonly FakeSelfTestHost _host = new();
    private readonly ScriptedPartner _partner;

    public SelfTestPairsHostTests() {
        this._partner = new ScriptedPartner(this._host);
    }

    public void Dispose() => this._host.Dispose();

    private static SelfTestStep Auto(string title, Func<StepContext, Task<StepResult>> run) => new() { Title = title, Kind = StepKind.Automatic, Run = run };

    private static SelfTestCheck Check(string id, params SelfTestStep[] steps) => new() {
        Id = id, Title = id, Steps = $"test {id}", Mode = CheckMode.Automatic, Needs = SelfTestNeeds.Partner | SelfTestNeeds.Connection, StepList = steps,
    };

    private SelfTestRun NewRun(params SelfTestCheck[] checks) {
        SelfTestSuite[] suites = [PairingSuite.Create(), new SelfTestSuite("pairs", "Pairs", checks)];
        var pacer = new SelfTestPacer(() => this._host.Now, this._host.Delay, role: SelfTestRole.Host);
        var data = SelfTestRun.NewData("K7Q2", 7, "wss://example.test/ws", this._host.Now, suites, role: SelfTestRole.Host, other: ScriptedPartner.Who);
        return new SelfTestRun(this._host, suites, pacer, this._host.File, data);
    }

    private static CheckRecord Result(SelfTestRun run, string id) => run.Data.Results.Single(result => result.Check == id);

    [Fact]
    public async Task ItPairsAsksAndTheHostLeavesLast() {
        PartnerAnswer? got = null;
        this._partner.Answer = (request, _) => request.Verb == PartnerVerb.ReportReceived
            ? new PartnerAnswer(request.Number, PartnerOutcome.Done, new Dictionary<string, string> { ["got"] = "2" })
            : null;
        var run = this.NewRun(Check("ask", Auto("asks", async context => {
            got = await context.Partner.AskAsync(context, PartnerVerb.ReportReceived);
            return StepResult.Pass();
        })));

        await run.RunAsync();

        Assert.Equal(CheckOutcome.Passed, Result(run, "pairing").Outcome);
        Assert.Equal(CheckOutcome.Passed, Result(run, "ask").Outcome);
        Assert.Equal(2, got?.Int("got"));
        Assert.Equal((ScriptedPartner.Who, "K7Q2", SelfTestRole.Host), this._host.Paired);
        // One lookup by the run (the invite's own is paced with it), one invite, to the partner only.
        Assert.Equal(1, this._host.UserLookups);
        var channel = Assert.Single(this._host.Invited).Channel;
        Assert.Equal("Bob Hatter", this._host.Invited[0].Name);
        // The clean-up: bye, the partner went, so the host left (deleting the channel), never disbanding it.
        Assert.Equal(PartnerVerb.Bye, this._partner.Requests[^1].Verb);
        Assert.Contains(channel, this._host.Left);
        Assert.Empty(this._host.Disbanded);
        Assert.False(this._host.File.Exists);
        Assert.Contains("Partner joined", run.View.PairLine);
        // Every protocol line was paced as one, and the partner's name went nowhere but the invite.
        Assert.Equal(this._host.Sent.Count, run.Pacer.CountThisRun(SelfTestAction.PartnerMessage) + 1);
        Assert.DoesNotContain(this._host.Logged, line => line.Contains("Bob", StringComparison.Ordinal) || line.Contains("Hatter", StringComparison.Ordinal));
        Assert.DoesNotContain(run.View.PairLine!, "Bob", StringComparison.Ordinal);
    }

    [Fact]
    public async Task APartnerWhoNeverJoinsSkipsEveryCheckThatNeedsThem() {
        this._partner.JoinsOnInvite = false;
        var ran = false;
        var run = this.NewRun(Check("ask", Auto("asks", _ => {
            ran = true;
            return Task.FromResult(StepResult.Pass());
        })));

        await run.RunAsync();

        Assert.False(ran);
        Assert.Equal(CheckOutcome.Skipped, Result(run, "pairing").Outcome);
        Assert.Equal(SelfTestWords.PartnerDidntJoin, Result(run, "ask").Why);
        // Two minutes, then it gave up; the invite still open, the channel was disbanded (nobody is left in it).
        Assert.True(this._host.Now - run.View.StartedAt >= SelfTestProtocol.PairingWithin);
        Assert.Single(this._host.Disbanded);
        Assert.DoesNotContain(this._partner.Requests, request => request.Verb == PartnerVerb.Bye);
    }

    [Fact]
    public async Task APartnerWhoIsntRegisteredIsNeverInvited() {
        this._host.Users.Clear();
        var run = this.NewRun(Check("ask", Auto("asks", _ => Task.FromResult(StepResult.Pass()))));

        await run.RunAsync();

        Assert.Equal(SelfTestWords.PartnerNotRegistered, Result(run, "ask").Why);
        Assert.Empty(this._host.Invited);
        Assert.Empty(this._host.BlockChanges);
    }

    [Fact]
    public async Task APartnerBlockedBeforeTheTestStaysBlockedAndIsntPaired() {
        this._host.Blocked.Add(ScriptedPartner.Id);
        var run = this.NewRun(Check("ask", Auto("asks", _ => Task.FromResult(StepResult.Pass()))));

        await run.RunAsync();

        Assert.Equal(SelfTestWords.PartnerWasBlocked, Result(run, "ask").Why);
        Assert.Empty(this._host.Invited);
        Assert.Empty(this._host.BlockChanges);
        Assert.Contains(ScriptedPartner.Id, this._host.Blocked);
    }

    [Fact]
    public async Task OneSilenceAsksStillThereAndSkipsOnlyThatRequest() {
        var first = true;
        this._partner.Ignore = request => {
            if (request.Verb == PartnerVerb.ReportWindows && first) {
                first = false;
                return true;
            }

            return false;
        };
        var run = this.NewRun(
            Check("silent", Auto("asks", async context => {
                await context.Partner.AskAsync(context, PartnerVerb.ReportWindows);
                return StepResult.Pass();
            })),
            Check("after", Auto("asks", async context => {
                await context.Partner.AskAsync(context, PartnerVerb.ReportWindows);
                return StepResult.Pass();
            })));

        await run.RunAsync();

        Assert.Equal(CheckOutcome.Skipped, Result(run, "silent").Outcome);
        Assert.Equal(SelfTestWords.PartnerSilent, Result(run, "silent").Why);
        Assert.Equal(CheckOutcome.Passed, Result(run, "after").Outcome);
        Assert.Equal([PartnerVerb.ReportWindows, PartnerVerb.StillThere, PartnerVerb.ReportWindows, PartnerVerb.Bye],
            this._partner.Requests.Select(request => request.Verb).ToArray());
    }

    [Fact]
    public async Task TwoSilencesTakeThePartnerAsGoneAndTheRestIsSkipped() {
        var asked = 0;
        var run = this.NewRun(
            Check("silent", Auto("asks", async context => {
                this._partner.Silent = true;
                await context.Partner.AskAsync(context, PartnerVerb.ReportLog);
                return StepResult.Pass();
            })),
            Check("after", Auto("asks", _ => {
                asked++;
                return Task.FromResult(StepResult.Pass());
            })));

        await run.RunAsync();

        Assert.Equal(SelfTestWords.PartnerGone, Result(run, "silent").Why);
        Assert.Equal(SelfTestWords.PartnerGone, Result(run, "after").Why);
        Assert.Equal(0, asked);
        // No bye to someone gone: the host disbands what they may still be in.
        Assert.DoesNotContain(this._partner.Requests, request => request.Verb == PartnerVerb.Bye);
        Assert.Single(this._host.Disbanded);
    }

    [Fact]
    public async Task APartnerOutOfTheChannelIsGoneAtOnce() {
        var took = TimeSpan.MaxValue;
        var run = this.NewRun(Check("left", Auto("asks", async context => {
            this._host.Part(context.Partner.ChannelId!, ScriptedPartner.Id);
            var before = this._host.Now;
            try {
                await context.Partner.AskAsync(context, PartnerVerb.ReportLog);
            } finally {
                took = this._host.Now - before;
            }

            return StepResult.Pass();
        })));

        await run.RunAsync();

        Assert.Equal(SelfTestWords.PartnerGone, Result(run, "left").Why);
        Assert.DoesNotContain(this._partner.Requests, request => request.Verb == PartnerVerb.ReportLog);
        Assert.True(took < TimeSpan.FromSeconds(1), took.ToString());
    }

    [Fact]
    public async Task ARefusalSkipsTheStep() {
        this._partner.Answer = (request, _) => new PartnerAnswer(request.Number, PartnerOutcome.Refused, new Dictionary<string, string>());
        var run = this.NewRun(Check("refused", Auto("asks", async context => {
            await context.Partner.AskAsync(context, PartnerVerb.Disconnect, "60");
            return StepResult.Pass();
        })));

        await run.RunAsync();

        Assert.Equal(SelfTestWords.PartnerRefused, Result(run, "refused").Why);
    }

    [Fact]
    public async Task ALineWithThePrefixFromSomeoneElseStopsTheRun() {
        var ranAfter = false;
        this._partner.Answer = (request, channel) => {
            this._host.Deliver(channel, 99, "Mallory Meddler@Lich", "LG self-test K7Q2 #1 done got=99");
            return null;
        };
        var run = this.NewRun(
            Check("meddled", Auto("asks", async context => {
                await context.Partner.AskAsync(context, PartnerVerb.ReportReceived);
                return StepResult.Pass();
            })),
            Check("after", Auto("asks", _ => {
                ranAfter = true;
                return Task.FromResult(StepResult.Pass());
            })));

        await run.RunAsync();

        Assert.Equal(CheckOutcome.Stopped, Result(run, "meddled").Outcome);
        Assert.Equal(SelfTestWords.Meddling, Result(run, "meddled").Why);
        Assert.False(ranAfter);
        // Cleaned up all the same.
        Assert.Equal(SelfTestState.Done, run.View.State);
        Assert.False(this._host.File.Exists);
    }

    [Fact]
    public async Task LinesWithoutThePrefixFromAnyoneAreIgnored() {
        this._partner.Answer = (request, channel) => {
            this._host.Deliver(channel, 99, "Mallory Meddler@Lich", "pulling at 9");
            this._host.Deliver(channel, 99, "Mallory Meddler@Lich", "LG self-test ABCD #1 done");
            return null;
        };
        var run = this.NewRun(Check("fine", Auto("asks", async context => {
            await context.Partner.AskAsync(context, PartnerVerb.ReportReceived);
            return StepResult.Pass();
        })));

        await run.RunAsync();

        Assert.Equal(CheckOutcome.Passed, Result(run, "fine").Outcome);
    }

    [Fact]
    public async Task APartnerStillInAfterByeIsDisbanded() {
        this._partner.LeavesOnBye = false;
        var run = this.NewRun(Check("ask", Auto("asks", async context => {
            await context.Partner.AskAsync(context, PartnerVerb.Hello);
            return StepResult.Pass();
        })));

        await run.RunAsync();

        Assert.Equal(PartnerVerb.Bye, this._partner.Requests[^1].Verb);
        Assert.Single(this._host.Disbanded);
        Assert.Empty(this._host.Left);
    }

    [Fact]
    public async Task TheBlockIsTheRunsOwnAndUndoneAtTheEnd() {
        var run = this.NewRun(Check("block", Auto("blocks", async context => {
            await context.BlockPartnerAsync(true);
            Assert.True(context.Data.BlockedOther);
            return StepResult.Pass();
        })));

        await run.RunAsync();

        Assert.Equal([(ScriptedPartner.Id, true), (ScriptedPartner.Id, false)], this._host.BlockChanges);
        Assert.False(this._host.File.Exists);
    }

    [Fact]
    public async Task ABlockThatCouldntBeUndoneKeepsTheRunFile() {
        var run = this.NewRun(Check("block", Auto("blocks", async context => {
            await context.BlockPartnerAsync(true);
            this._host.Ready = false;
            return StepResult.Pass();
        })));

        await run.RunAsync();

        Assert.True(this._host.File.Exists);
        Assert.True(this._host.File.Read(out _)!.BlockedOther);
    }

    [Fact]
    public async Task ThePartnersChannelIsJoinedOnlyFromThemAndOnlyLeft() {
        this._host.Invites.Add(new SelfTestInvite("stranger-channel", PartnerSide.OwnChannelName("K7Q2"), "Mallory Meddler@Lich", true));
        string? joined = null;
        var run = this.NewRun(Check("own", Auto("joins", async context => {
            await context.Partner.AskAsync(context, PartnerVerb.InviteMe);
            joined = await context.Partner.AcceptOwnChannelAsync(context);
            return StepResult.Pass();
        })));

        await run.RunAsync();

        Assert.Equal(ScriptedPartner.OwnChannel, joined);
        Assert.Equal([ScriptedPartner.OwnChannel], this._host.Accepted);
        Assert.Contains(ScriptedPartner.OwnChannel, this._host.Left);
        Assert.DoesNotContain(ScriptedPartner.OwnChannel, this._host.Disbanded);
        // Left before the bye, so the partner can delete its own channel by leaving it.
        Assert.True(this._host.Left.IndexOf(ScriptedPartner.OwnChannel) == 0);
    }

    [Fact]
    public async Task ARunAloneSkipsPartnerChecks() {
        var suite = new SelfTestSuite("pairs", "Pairs", [Check("ask", Auto("asks", _ => Task.FromResult(StepResult.Pass())))]);
        var run = new SelfTestRun(this._host, [suite], new SelfTestPacer(() => this._host.Now, this._host.Delay), this._host.File,
            SelfTestRun.NewData("K7Q2", 7, "wss://example.test/ws", this._host.Now, [suite]));

        await run.RunAsync();

        Assert.Equal(SelfTestWords.NeedsPartner, Result(run, "ask").Why);
        Assert.Null(this._host.Paired);
        Assert.Null(run.Data.Partner);
    }

    [Theory]
    [InlineData("K7Q2", true)]
    [InlineData("k7q2", false)]
    [InlineData("K7Q", false)]
    [InlineData("K0Q2", false)]
    [InlineData("K7Q2 ", false)]
    public void APartnersCodeIsFourOfTheCodesLetters(string code, bool ok) => Assert.Equal(ok, SelfTestRun.IsCode(code));
}
