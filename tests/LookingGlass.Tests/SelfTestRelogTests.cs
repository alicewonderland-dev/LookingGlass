using LookingGlass.Core.SelfTest;
using LookingGlass.Core.SelfTest.Suites;

namespace LookingGlass.Tests;

/// <summary>
/// Relogs in a house or apartment (owner, 2026-10-13): the game puts the character back outside, in the ward. The tester is
/// told so before; the relog step ends only once they are back inside, with no time limit while they walk; and with two
/// players, while either is outside, the host sends no requests, its time-outs wait, and each side says who it waits for.
/// </summary>
public sealed class SelfTestRelogTests : IDisposable {
    private static readonly TimeSpan Walk = TimeSpan.FromMinutes(3);
    private readonly FakeSelfTestHost _host = new();

    public void Dispose() => this._host.Dispose();

    private static SelfTestStep Auto(string title, Func<StepContext, Task<StepResult>> run) => new() { Title = title, Kind = StepKind.Automatic, Run = run };

    private static SelfTestCheck Check(string id, SelfTestNeeds needs, params SelfTestStep[] steps) => new() {
        Id = id, Title = id, Steps = $"test {id}", Mode = CheckMode.Automatic, Needs = needs, StepList = steps,
    };

    private static PartnerAnswer Presence(PartnerOutcome outcome) => new(0, outcome, new Dictionary<string, string>());

    // ================================================================ one player

    [Theory]
    [InlineData("house")]
    [InlineData("inn")]
    public async Task AfterARelogOutsideTheStepWaitsUntilTheTesterIsBackInside(string where) {
        var inside = where == "house" ? FakeSelfTestHost.House : FakeSelfTestHost.InnRoom;
        this._host.Place = inside;
        SelfTestRun? run = null;
        DateTimeOffset? loggedIn = null;
        DateTimeOffset? afterAt = null;
        PlaceKind? afterPlace = null;
        string? asked = null;
        var walkingShown = false;
        this._host.OnTick = () => {
            if (run?.View.State == SelfTestState.WaitingToCarryOn && loggedIn == null) {
                asked = run.View.Prompt?.Prompt.Text;
                this._host.Logins++;
                this._host.Place = FakeSelfTestHost.Ward;
                loggedIn = this._host.Now;
            } else if (loggedIn is { } at) {
                walkingShown |= run?.View.Pause == SelfTestWords.WalkBackInside;
                if (this._host.Now - at >= Walk) {
                    this._host.Place = inside;
                }
            }
        };
        var suite = new SelfTestSuite("suite", "Suite", [Check("a", SelfTestNeeds.None, SelfTestSteps.CarryOn(SelfTestSteps.Relog, "1", relog: true), Auto("after", _ => {
            afterAt = this._host.Now;
            afterPlace = PrivateArea.Judge(this._host.Place).Kind;
            return Task.FromResult(StepResult.Pass());
        }))]);
        var pacer = new SelfTestPacer(() => this._host.Now, this._host.Delay);
        run = new SelfTestRun(this._host, [suite], pacer, this._host.File, SelfTestRun.NewData("K7Q2", 42, "wss://example.test/ws", this._host.Now, [suite]));

        await run.RunAsync();

        Assert.Equal(CheckOutcome.Passed, run.Data.Results.Single().Outcome);
        // Told before: in a house, that they'll be outside; in an inn room, what to do if they are.
        Assert.Contains(SelfTestWords.ComeBackInside(PrivateArea.Judge(inside).Kind), asked, StringComparison.Ordinal);
        Assert.StartsWith(SelfTestSteps.Relog, asked, StringComparison.Ordinal);
        // The step ended once they were back inside, three minutes later, and said meanwhile what it waits for.
        Assert.True(walkingShown);
        Assert.Equal(PrivateArea.Judge(inside).Kind, afterPlace);
        Assert.True(afterAt - loggedIn >= Walk);
        Assert.Empty(this._host.TypedOutside);
    }

    [Fact]
    public void AReloadSaysNothingAboutComingBackOutside() {
        Assert.False(SelfTestSteps.CarryOn(SelfTestSteps.Reload, "20").Relogs);
        Assert.True(SelfTestSteps.CarryOn(SelfTestSteps.Relog, "6", relog: true).Relogs);
        // Every relog step of the suites says so; a reload doesn't move the character.
        var steps = SelfTestSuites.Every().SelectMany(suite => suite.Checks).SelectMany(check => check.StepList).Where(step => step.Kind == StepKind.CarryOnAfter).ToList();
        Assert.NotEmpty(steps);
        Assert.All(steps, step => Assert.Equal(step.Title.Contains("title screen", StringComparison.OrdinalIgnoreCase), step.Relogs));
        // The questions answered after coming in with another character, and back, are relogs too.
        var across = SelfTestSuites.Every().SelectMany(suite => suite.Checks).Where(check => check.Id is "name-colours-21-other" or "custom-colours-15-other").ToList();
        Assert.Equal(2, across.Count);
        Assert.All(across, check => Assert.Single(check.StepList, step => step.Relogs));
    }

    [Fact]
    public async Task AnAnswerFromAnotherCharactersRelogWaitsUntilTheTesterIsBackInside() {
        this._host.Place = FakeSelfTestHost.House;
        SelfTestRun? run = null;
        DateTimeOffset? answeredAt = null;
        string? asked = null;
        var walkingShown = false;
        this._host.OnTick = () => {
            if (run?.View.Prompt?.Prompt is not { } prompt) {
                if (answeredAt is { } at) {
                    walkingShown |= run?.View.Pause == SelfTestWords.WalkBackInside;
                    if (this._host.Now - at >= Walk) {
                        this._host.Place = FakeSelfTestHost.House;
                    }
                }

                return;
            }

            if (prompt.Text.StartsWith("Come in with that other character", StringComparison.Ordinal)) {
                // Back with this character, but in the ward: answers from there, then walks in for three minutes.
                asked = prompt.Text;
                this._host.Logins++;
                this._host.Place = FakeSelfTestHost.Ward;
                answeredAt = this._host.Now;
            }

            run.Answer(GuidedAnswer.Yes);
        };
        var suite = ColoursSuite.Create();
        var pacer = new SelfTestPacer(() => this._host.Now, this._host.Delay);
        run = new SelfTestRun(this._host, [suite], pacer, this._host.File,
            SelfTestRun.NewData("K7Q2", 42, "wss://example.test/ws", this._host.Now, [suite], onlyChecks: ["custom-colours-15-other"]));

        await run.RunAsync();

        var result = run.Data.Results.Single();
        Assert.True(result.Outcome == CheckOutcome.Passed, $"{result.Outcome}: {result.Why}");
        Assert.Contains(SelfTestWords.ComeBackInside(PlaceKind.House), asked, StringComparison.Ordinal);
        Assert.True(walkingShown);
        Assert.True(result.At - answeredAt >= Walk);
    }

    // ================================================================ two players: the host's side

    private SelfTestRun HostRun(params SelfTestCheck[] checks) {
        SelfTestSuite[] suites = [PairingSuite.Create(), new SelfTestSuite("pairs", "Pairs", checks)];
        var pacer = new SelfTestPacer(() => this._host.Now, this._host.Delay, role: SelfTestRole.Host);
        var data = SelfTestRun.NewData("K7Q2", 7, "wss://example.test/ws", this._host.Now, suites, role: SelfTestRole.Host, other: ScriptedPartner.Who);
        return new SelfTestRun(this._host, suites, pacer, this._host.File, data);
    }

    private const SelfTestNeeds Pair = SelfTestNeeds.Partner | SelfTestNeeds.Connection;

    private readonly List<(string Text, DateTimeOffset At)> _sent = [];

    /// <summary>Every line the host sends, with when.</summary>
    private void RecordSent() {
        var sent = this._host.OnSent;
        this._host.OnSent = (channel, text) => {
            this._sent.Add((text, this._host.Now));
            sent?.Invoke(channel, text);
        };
    }

    /// <summary>The host's requests (not "alive", not its own away and back), with when.</summary>
    private List<(PartnerRequest Request, DateTimeOffset At)> Requests() => this._sent
        .Select(sent => (Parsed: SelfTestProtocol.Parse("K7Q2", sent.Text), sent.At))
        .Where(sent => sent.Parsed is ProtocolLine.Request { Value.Verb: not PartnerVerb.KeepAlive })
        .Select(sent => (((ProtocolLine.Request) sent.Parsed).Value, sent.At)).ToList();

    private List<(PartnerOutcome Outcome, DateTimeOffset At)> OwnPresence() => this._sent
        .Select(sent => (Parsed: SelfTestProtocol.Parse("K7Q2", sent.Text), sent.At))
        .Where(sent => sent.Parsed is ProtocolLine.Answer { Value.Number: 0 })
        .Select(sent => (((ProtocolLine.Answer) sent.Parsed).Value.Outcome, sent.At)).ToList();

    private static SelfTestStep AskWindows() => Auto("asks", async context => {
        await context.Partner.AskAsync(context, PartnerVerb.ReportWindows);
        return StepResult.Pass();
    });

    [Fact]
    public async Task APartnerWhoRelogsAndTakesThreeMinutesToWalkBackInIsWaitedFor() {
        var partner = new ScriptedPartner(this._host);
        this.RecordSent();
        int? relog = null;
        DateTimeOffset? asked = null;
        DateTimeOffset? backAt = null;
        var waitingShown = false;
        SelfTestRun? run = null;
        partner.Ignore = request => {
            if (request.Verb != PartnerVerb.Ask || request.Args[0] != "relog") {
                return false;
            }

            // Its plugin says it is going (before the relog), and answers only once its player is back inside.
            relog = request.Number;
            asked = this._host.Now;
            partner.Say(this._host.Created[0], Presence(PartnerOutcome.Away));
            return true;
        };
        this._host.OnTick = () => {
            if (asked is not { } at || backAt != null) {
                return;
            }

            waitingShown |= run?.View.Pause == SelfTestWords.WaitingPartnerInside;
            var since = this._host.Now - at;
            // Eight minutes at the title screen (offline), then outside in the ward for three: past the ten-minute time-out.
            this._host.SetOnline(this._host.Created[0], ScriptedPartner.Id, since >= TimeSpan.FromMinutes(8));
            if (since >= TimeSpan.FromMinutes(8) + Walk) {
                backAt = this._host.Now;
                partner.Say(this._host.Created[0], Presence(PartnerOutcome.Back));
                partner.Say(this._host.Created[0], new PartnerAnswer(relog!.Value, PartnerOutcome.Yes, new Dictionary<string, string>()));
            }
        };
        run = this.HostRun(Check("relog", Pair,
            SelfTestSteps.SetupGuided("Your partner relogs", "4", context => context.Partner.AskPlayerAsync(context, PartnerAsk.Relog, "partner relogged")),
            AskWindows()));

        await run.RunAsync();

        Assert.Equal(CheckOutcome.Passed, run.Data.Results.Single(result => result.Check == "relog").Outcome);
        Assert.DoesNotContain(partner.Requests, request => request.Verb == PartnerVerb.StillThere);
        Assert.True(waitingShown);
        // Nothing asked while they were away: the next request only once they were back inside.
        Assert.All(this.Requests().Where(sent => sent.Request.Number > relog), sent => Assert.True(sent.At >= backAt));
        Assert.Contains(this.Requests(), sent => sent.Request.Verb == PartnerVerb.ReportWindows);
        // The keep-alive went on meanwhile, so the partner's side never gave up on the host.
        Assert.Contains(this._sent, sent => SelfTestProtocol.Parse("K7Q2", sent.Text) is ProtocolLine.Request { Value.Verb: PartnerVerb.KeepAlive } && sent.At > asked && sent.At < backAt);
    }

    [Fact]
    public async Task APartnerOutsideBeforeARequestIsAskedNothingUntilBack() {
        var partner = new ScriptedPartner(this._host);
        this.RecordSent();
        DateTimeOffset? outAt = null;
        DateTimeOffset? backAt = null;
        SelfTestRun? run = null;
        var waitingShown = false;
        partner.Ignore = request => {
            if (request.Verb != PartnerVerb.ReportWindows || outAt != null) {
                return false;
            }

            // It answers the first request, then its player walks out of the house.
            partner.Say(this._host.Created[0], ScriptedPartner.Default(request, partner.Facts));
            partner.Say(this._host.Created[0], Presence(PartnerOutcome.Away));
            outAt = this._host.Now;
            return true;
        };
        this._host.OnTick = () => {
            if (outAt is not { } at || backAt != null) {
                return;
            }

            waitingShown |= run?.View.Pause == SelfTestWords.WaitingPartnerInside;
            if (this._host.Now - at >= Walk) {
                backAt = this._host.Now;
                partner.Say(this._host.Created[0], Presence(PartnerOutcome.Back));
            }
        };
        run = this.HostRun(Check("before", Pair, AskWindows(), AskWindows()));

        await run.RunAsync();

        Assert.Equal(CheckOutcome.Passed, run.Data.Results.Single(result => result.Check == "before").Outcome);
        Assert.True(waitingShown);
        var windows = this.Requests().Where(sent => sent.Request.Verb == PartnerVerb.ReportWindows).ToList();
        Assert.Equal(2, windows.Count);
        Assert.True(windows[1].At >= backAt);
        Assert.False(run.Data.PartnerAway);
    }

    [Fact]
    public async Task AHostWhoWalksOutBeforeARequestSendsItOnlyOnceBackInside() {
        var partner = new ScriptedPartner(this._host);
        this.RecordSent();
        DateTimeOffset? outAt = null;
        DateTimeOffset? insideAt = null;
        this._host.OnTick = () => {
            if (outAt == null && partner.Requests.Any(request => request.Verb == PartnerVerb.ReportWindows)) {
                // Walks out of the inn room right after the first request, while the next one waits its turn.
                this._host.Place = FakeSelfTestHost.Ward;
                outAt = this._host.Now;
            } else if (outAt is { } at && insideAt == null && this._host.Now - at >= Walk) {
                this._host.Place = FakeSelfTestHost.InnRoom;
                insideAt = this._host.Now;
            }
        };
        var run = this.HostRun(Check("out", Pair, AskWindows(), AskWindows()));

        await run.RunAsync();

        Assert.Equal(CheckOutcome.Passed, run.Data.Results.Single(result => result.Check == "out").Outcome);
        var windows = this.Requests().Where(sent => sent.Request.Verb == PartnerVerb.ReportWindows).ToList();
        Assert.Equal(2, windows.Count);
        Assert.True(windows[1].At >= insideAt);
        // The partner was told: "away" once it was steadily outside, "back" once inside and settled, before the request.
        var presence = this.OwnPresence();
        Assert.Equal([PartnerOutcome.Away, PartnerOutcome.Back], presence.Select(found => found.Outcome).ToArray());
        Assert.True(presence[0].At > outAt);
        Assert.True(presence[1].At >= insideAt && presence[1].At <= windows[1].At);
        Assert.Empty(this._host.TypedOutside);
    }

    [Fact]
    public async Task AwayAndBackAreSaidAtMostTwentyTimesARunEndingBack() {
        var partner = new ScriptedPartner(this._host);
        this.RecordSent();
        var toggles = 0;
        var last = this._host.Now;
        this._host.OnTick = () => {
            // In and out of the inn room every ten seconds, fifteen times.
            if (toggles < 30 && this._host.Now - last >= TimeSpan.FromSeconds(10)) {
                last = this._host.Now;
                toggles++;
                this._host.Place = toggles % 2 == 1 ? FakeSelfTestHost.Ward : FakeSelfTestHost.InnRoom;
            }
        };
        var run = this.HostRun(Check("busy", Pair, AskWindows(), Auto("waits", async context => {
            await context.WaitUntilAsync(() => Task.FromResult(toggles >= 30), TimeSpan.FromHours(1));
            return StepResult.Pass();
        }), AskWindows()));

        await run.RunAsync();

        Assert.Equal(CheckOutcome.Passed, run.Data.Results.Single(result => result.Check == "busy").Outcome);
        var presence = this.OwnPresence().Select(found => found.Outcome).ToList();
        Assert.Equal(SelfTestProtocol.MostPresenceLines, presence.Count);
        Assert.All(presence.Select((outcome, i) => (outcome, i)), found => Assert.Equal(found.i % 2 == 0 ? PartnerOutcome.Away : PartnerOutcome.Back, found.outcome));
        Assert.False(run.Data.ToldAway);
    }

    [Fact]
    public async Task AMomentsUnreadablePlaceSaysNothingToThePartner() {
        var partner = new ScriptedPartner(this._host);
        this.RecordSent();
        DateTimeOffset? flickerAt = null;
        var done = false;
        this._host.OnTick = () => {
            if (flickerAt == null && partner.Requests.Any(request => request.Verb == PartnerVerb.ReportWindows)) {
                // The housing facts lag a zone change for a second: not "outside".
                this._host.Place = new PlaceFacts(177, 2, null, HousingKind.None, null, null, null, null);
                flickerAt = this._host.Now;
            } else if (flickerAt is { } at && !done && this._host.Now - at >= TimeSpan.FromSeconds(1)) {
                this._host.Place = FakeSelfTestHost.InnRoom;
                done = true;
            }
        };
        var run = this.HostRun(Check("flicker", Pair, AskWindows(), AskWindows()));

        await run.RunAsync();

        Assert.Equal(CheckOutcome.Passed, run.Data.Results.Single(result => result.Check == "flicker").Outcome);
        Assert.True(done);
        Assert.Empty(this.OwnPresence());
    }

    [Fact]
    public async Task ThePartnersAwayPastItsMostOrOutsideTheTestChannelIsIgnored() {
        var partner = new ScriptedPartner(this._host);
        this.RecordSent();
        DateTimeOffset? firstAt = null;
        partner.Ignore = request => {
            if (request.Verb != PartnerVerb.ReportWindows || firstAt != null) {
                return false;
            }

            firstAt = this._host.Now;
            var channel = this._host.Created[0];
            for (var i = 0; i < SelfTestProtocol.MostPresenceLines / 2; i++) {
                partner.Say(channel, Presence(PartnerOutcome.Away));
                partner.Say(channel, Presence(PartnerOutcome.Back));
            }

            partner.Say(channel, ScriptedPartner.Default(request, partner.Facts));
            // One more "away", past its most; and one in local chat, not the test channel; and a malformed one.
            partner.Say(channel, Presence(PartnerOutcome.Away));
            this._host.Deliver(null, ScriptedPartner.Id, ScriptedPartner.Who, "LG self-test K7Q2 #0 away");
            this._host.Deliver(channel, ScriptedPartner.Id, ScriptedPartner.Who, "LG self-test K7Q2 #3 away");
            return true;
        };
        var run = this.HostRun(Check("most", Pair, AskWindows(), AskWindows()));
        // Were the last "away" taken, the host would wait for ever on a partner still online: stopped instead, so it fails.
        var start = this._host.Now;
        this._host.OnTick = () => {
            if (this._host.Now - start > TimeSpan.FromHours(2)) {
                run.Stop();
            }
        };

        await run.RunAsync();

        Assert.Equal(CheckOutcome.Passed, run.Data.Results.Single(result => result.Check == "most").Outcome);
        var windows = this.Requests().Where(sent => sent.Request.Verb == PartnerVerb.ReportWindows).ToList();
        Assert.Equal(2, windows.Count);
        Assert.True(windows[1].At - firstAt < TimeSpan.FromSeconds(30));
        Assert.Equal(SelfTestProtocol.MostPresenceLines, run.Data.PartnerPresenceLines);
        Assert.False(run.Data.PartnerAway);
    }

    [Fact]
    public async Task APartnerWhoStepsOutsideMidRequestIsWaitedFor() {
        var partner = new ScriptedPartner(this._host);
        this.RecordSent();
        int? pending = null;
        DateTimeOffset? outAt = null;
        partner.Ignore = request => {
            if (request.Verb != PartnerVerb.ReportWindows || pending != null) {
                return false;
            }

            pending = request.Number;
            outAt = this._host.Now;
            partner.Say(this._host.Created[0], Presence(PartnerOutcome.Away));
            return true;
        };
        var done = false;
        this._host.OnTick = () => {
            if (outAt is { } at && !done && this._host.Now - at >= Walk) {
                done = true;
                partner.Say(this._host.Created[0], Presence(PartnerOutcome.Back));
                partner.Say(this._host.Created[0], ScriptedPartner.Default(PartnerRequest.Of(pending!.Value, PartnerVerb.ReportWindows), partner.Facts));
            }
        };
        var run = this.HostRun(Check("out", Pair, AskWindows(), AskWindows()));

        await run.RunAsync();

        Assert.Equal(CheckOutcome.Passed, run.Data.Results.Single(result => result.Check == "out").Outcome);
        Assert.DoesNotContain(partner.Requests, request => request.Verb == PartnerVerb.StillThere);
        Assert.Equal(2, partner.Requests.Count(request => request.Verb == PartnerVerb.ReportWindows));
    }

    [Fact]
    public async Task APartnerAwayAndOfflineFor45MinutesHasLeftTheTest() {
        var partner = new ScriptedPartner(this._host);
        DateTimeOffset? outAt = null;
        partner.Ignore = request => {
            if (request.Verb != PartnerVerb.ReportWindows) {
                return false;
            }

            outAt = this._host.Now;
            partner.Say(this._host.Created[0], Presence(PartnerOutcome.Away));
            this._host.SetOnline(this._host.Created[0], ScriptedPartner.Id, false);
            return true;
        };
        var run = this.HostRun(Check("gone", Pair, AskWindows()));

        await run.RunAsync();

        var result = run.Data.Results.Single(found => found.Check == "gone");
        Assert.Equal(SelfTestWords.PartnerGone, result.Why);
        // When the check ended (the clean-up after it takes its own minutes on the test's clock).
        Assert.InRange(result.At!.Value - outAt!.Value, SelfTestProtocol.PartnerGivesUpOffline, SelfTestProtocol.PartnerGivesUpOffline + TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task AHostWhoRelogsAndTakesThreeMinutesToWalkBackInCarriesOn() {
        this._host.Place = FakeSelfTestHost.House;
        var partner = new ScriptedPartner(this._host);
        this.RecordSent();
        DateTimeOffset? loggedIn = null;
        DateTimeOffset? insideAt = null;
        SelfTestRun? run = null;
        this._host.OnTick = () => {
            if (run?.View.State == SelfTestState.WaitingToCarryOn && loggedIn == null) {
                this._host.Logins++;
                this._host.Place = FakeSelfTestHost.Ward;
                loggedIn = this._host.Now;
            } else if (loggedIn is { } at && insideAt == null && this._host.Now - at >= Walk) {
                this._host.Place = FakeSelfTestHost.House;
                insideAt = this._host.Now;
            }
        };
        run = this.HostRun(Check("relog", Pair, SelfTestSteps.CarryOn(SelfTestSteps.Relog, "6", relog: true), AskWindows()));

        await run.RunAsync();

        Assert.Equal(CheckOutcome.Passed, run.Data.Results.Single(result => result.Check == "relog").Outcome);
        Assert.DoesNotContain(partner.Requests, request => request.Verb == PartnerVerb.StillThere);
        // Its plugin told the partner it was going, before the relog, and that it was back once inside again.
        var presence = this.OwnPresence();
        Assert.Equal([PartnerOutcome.Away, PartnerOutcome.Back], presence.Select(found => found.Outcome).ToArray());
        Assert.True(presence[0].At <= loggedIn);
        Assert.True(presence[1].At >= insideAt);
        // The next request only once inside.
        Assert.All(this.Requests().Where(sent => sent.Request.Verb == PartnerVerb.ReportWindows), sent => Assert.True(sent.At >= insideAt));
        Assert.Empty(this._host.TypedOutside);
    }

    // ================================================================ two players: the partner's side

    private SelfTestRun PartnerRun() {
        SelfTestSuite[] suites = [PartnerSuite.Create()];
        var pacer = new SelfTestPacer(() => this._host.Now, this._host.Delay, role: SelfTestRole.Partner);
        var data = SelfTestRun.NewData(ScriptedHost.Code, 9, "wss://example.test/ws", this._host.Now, suites, role: SelfTestRole.Partner, other: ScriptedHost.Who);
        return new SelfTestRun(this._host, suites, pacer, this._host.File, data);
    }

    [Fact]
    public async Task APartnerRelogOutsideIsAnsweredOnlyOnceBackInside() {
        this._host.Place = FakeSelfTestHost.House;
        var host = new ScriptedHost(this._host, "ask relog", "report windows", "bye");
        var at = new List<(PartnerAnswer Answer, DateTimeOffset At)>();
        host.AfterAnswer = answer => at.Add((answer, this._host.Now));
        host.Invite();
        SelfTestRun? run = null;
        DateTimeOffset? loggedIn = null;
        string? asked = null;
        var walkingShown = false;
        this._host.OnTick = () => {
            if (loggedIn == null && run?.View.Prompt?.Prompt.Text is { } text && text.StartsWith(SelfTestSteps.Relog, StringComparison.Ordinal)) {
                asked = text;
                this._host.Logins++;
                this._host.Place = FakeSelfTestHost.Ward;
                loggedIn = this._host.Now;
            } else if (loggedIn is { } since) {
                walkingShown |= run?.View.Pause == SelfTestWords.WalkBackInside;
                if (this._host.Now - since >= Walk) {
                    this._host.Place = FakeSelfTestHost.House;
                }
            }
        };
        run = this.PartnerRun();

        await run.RunAsync();

        Assert.Equal(CheckOutcome.Passed, run.Data.Results.Single().Outcome);
        Assert.Contains(SelfTestWords.ComeBackInside(PlaceKind.House), asked, StringComparison.Ordinal);
        Assert.True(walkingShown);
        Assert.Equal([PartnerOutcome.Hello, PartnerOutcome.Away, PartnerOutcome.Back, PartnerOutcome.Yes, PartnerOutcome.Done, PartnerOutcome.Done],
            at.Select(found => found.Answer.Outcome).ToArray());
        // "away" before the relog; "back", then its "yes", only once inside again.
        Assert.True(at[1].At <= loggedIn);
        Assert.True(at[2].At - loggedIn >= Walk);
        Assert.True(at[3].At >= at[2].At);
    }

    [Fact]
    public async Task APartnerOutsideAnswersNothingUntilBackInside() {
        var host = new ScriptedHost(this._host, "report windows", "report windows", "bye");
        var at = new List<(PartnerAnswer Answer, DateTimeOffset At)>();
        DateTimeOffset? outAt = null;
        host.AfterAnswer = answer => {
            at.Add((answer, this._host.Now));
            if (answer.Number == 1) {
                // Its player walks out of the inn room right after the first answer; the host's next request is already on its way.
                this._host.Place = FakeSelfTestHost.Ward;
                outAt = this._host.Now;
            }
        };
        host.Invite();
        this._host.OnTick = () => {
            if (outAt is { } since && this._host.Now - since >= Walk) {
                this._host.Place = FakeSelfTestHost.InnRoom;
            }
        };
        var run = this.PartnerRun();

        await run.RunAsync();

        Assert.Equal(CheckOutcome.Passed, run.Data.Results.Single().Outcome);
        Assert.Equal([PartnerOutcome.Hello, PartnerOutcome.Done, PartnerOutcome.Away, PartnerOutcome.Back, PartnerOutcome.Done, PartnerOutcome.Done],
            at.Select(found => found.Answer.Outcome).ToArray());
        Assert.True(at[3].At - outAt >= Walk);
        Assert.True(at[4].At >= at[3].At);
    }

    [Fact]
    public async Task APartnerWhoWalksOutDuringAStepAnswersOnlyOnceBackInside() {
        var host = new ScriptedHost(this._host, "ask click-link", "bye");
        var at = new List<(PartnerAnswer Answer, DateTimeOffset At)>();
        host.AfterAnswer = answer => at.Add((answer, this._host.Now));
        host.Invite();
        SelfTestRun? run = null;
        DateTimeOffset? outAt = null;
        DateTimeOffset? insideAt = null;
        this._host.OnTick = () => {
            if (outAt == null && run?.View.Prompt != null) {
                // Its player walks out of the inn room, and answers from the ward.
                this._host.Place = FakeSelfTestHost.Ward;
                outAt = this._host.Now;
                run.Answer(GuidedAnswer.Yes);
            } else if (outAt is { } since && insideAt == null && this._host.Now - since >= Walk) {
                this._host.Place = FakeSelfTestHost.InnRoom;
                insideAt = this._host.Now;
            }
        };
        run = this.PartnerRun();

        await run.RunAsync();

        Assert.Equal(CheckOutcome.Passed, run.Data.Results.Single().Outcome);
        Assert.Equal([PartnerOutcome.Hello, PartnerOutcome.Away, PartnerOutcome.Back, PartnerOutcome.Yes, PartnerOutcome.Done],
            at.Select(found => found.Answer.Outcome).ToArray());
        // Its "yes" only once back inside, after its "back".
        Assert.True(at[2].At >= insideAt);
        Assert.True(at[3].At >= at[2].At);
        Assert.Empty(this._host.TypedOutside);
    }

    [Fact]
    public async Task AHostsAwayOutsideItsTestChannelIsNotShown() {
        var host = new ScriptedHost(this._host);
        host.Invite();
        SelfTestRun? run = null;
        DateTimeOffset? saidAt = null;
        string? after = null;
        this._host.OnTick = () => {
            if (this._host.Accepted.Count == 0 || run == null) {
                return;
            }

            if (saidAt == null) {
                saidAt = this._host.Now;
                // In local chat, not the host's test channel: not the protocol's place for it.
                host.Say("LG self-test K7Q2 #0 away", channel: null);
            } else if (after == null && this._host.Now - saidAt >= Walk) {
                after = run.View.PairLine;
                host.Requests.Enqueue("bye");
                host.Next();
            }
        };
        run = this.PartnerRun();

        await run.RunAsync();

        Assert.NotNull(after);
        Assert.NotEqual(SelfTestWords.WaitingPartnerInside, after);
        Assert.Equal(CheckOutcome.Passed, run.Data.Results.Single().Outcome);
    }

    [Fact]
    public async Task AHostOutsideIsShownAsWhatThePartnerWaitsFor() {
        var host = new ScriptedHost(this._host);
        host.Invite();
        SelfTestRun? run = null;
        DateTimeOffset? awayAt = null;
        string? whileAway = null;
        var back = false;
        this._host.OnTick = () => {
            if (this._host.Accepted.Count == 0 || run == null) {
                return;
            }

            if (awayAt == null) {
                awayAt = this._host.Now;
                host.Say("LG self-test K7Q2 #0 away");
            } else if (!back && this._host.Now - awayAt >= Walk) {
                whileAway = run.View.PairLine;
                back = true;
                host.Say("LG self-test K7Q2 #0 back");
                host.Requests.Enqueue("report windows");
                host.Requests.Enqueue("bye");
                host.Next();
            }
        };
        run = this.PartnerRun();

        await run.RunAsync();

        Assert.Equal(SelfTestWords.WaitingPartnerInside, whileAway);
        Assert.Equal(CheckOutcome.Passed, run.Data.Results.Single().Outcome);
        Assert.Equal(PartnerOutcome.Done, host.Answers[^1].Outcome);
    }
}
