using LookingGlass.Core.Client;
using LookingGlass.Core.SelfTest;
using LookingGlass.Protocol;

namespace LookingGlass.Tests;

/// <summary>
/// The self-test's pacing (docs/self-test-plan.md, "Pacing"): against a fake clock, no run of requests goes past its pace,
/// nor would the server's own limits ever refuse one; the first refusal stops everything; the hour's budget.
/// </summary>
public sealed class SelfTestPacerTests {
    private DateTimeOffset _now = new(2026, 10, 13, 18, 0, 0, TimeSpan.Zero);

    private SelfTestPacer NewPacer(IEnumerable<SelfTestSpend>? before = null) => new(() => this._now, (delay, _) => {
        this._now += delay;
        return Task.CompletedTask;
    }, before);

    /// <summary>The server's limits (its defaults): a bucket of <paramref name="burst"/>, refilled one every <paramref name="every"/>.</summary>
    private sealed class ServerBucket(int burst, TimeSpan every) {
        private double _tokens = burst;
        private DateTimeOffset? _last;

        public bool Take(DateTimeOffset now) {
            if (this._last is { } last) {
                this._tokens = Math.Min(burst, this._tokens + (now - last).TotalSeconds / every.TotalSeconds);
            }

            this._last = now;
            if (this._tokens < 1) {
                return false;
            }

            this._tokens--;
            return true;
        }
    }

    public static TheoryData<SelfTestAction, int, double> ServerLimits() => new() {
        { SelfTestAction.ChannelMessage, 5, 1 },
        { SelfTestAction.LocalMessage, 5, 1 },
        { SelfTestAction.CreateChannel, 10, 60 },
        { SelfTestAction.Disband, 5, 60 },
        { SelfTestAction.Rename, 10, 10 },
        { SelfTestAction.Lookup, 60, 1 },
        { SelfTestAction.Invite, 20, 60 },
        { SelfTestAction.DeviceList, 20, 6 },
    };

    [Theory]
    [MemberData(nameof(ServerLimits))]
    public async Task ARunNeverGoesPastItsPaceNorTheServersLimit(SelfTestAction action, int burst, double everySeconds) {
        var pacer = this.NewPacer();
        var rule = SelfTestPacer.Rules[action];
        var server = new ServerBucket(burst, TimeSpan.FromSeconds(everySeconds));
        var times = new List<DateTimeOffset>();
        for (var i = 0; i < rule.RunMost; i++) {
            await pacer.WaitAsync(action, TestContext.Current.CancellationToken);
            times.Add(this._now);
            Assert.True(server.Take(this._now), $"The server would refuse {action} number {i + 1}.");
        }

        for (var i = 1; i < times.Count; i++) {
            Assert.True(times[i] - times[i - 1] >= rule.Every, $"{action} {i} came {(times[i] - times[i - 1]).TotalSeconds} s after the one before.");
        }

        // Never more than "in a row" without the longer pause.
        for (var i = rule.InARow; i < times.Count; i++) {
            Assert.True(times[i] - times[i - rule.InARow] >= rule.AfterARow + rule.Every * (rule.InARow - 1) || rule.InARow == 1,
                $"{action}: more than {rule.InARow} in a row.");
        }
    }

    [Fact]
    public async Task ChannelMessagesComeEveryTwoSecondsAtMostThreeInARow() {
        var pacer = this.NewPacer();
        var start = this._now;
        var times = new List<double>();
        for (var i = 0; i < 7; i++) {
            await pacer.WaitAsync(SelfTestAction.ChannelMessage, TestContext.Current.CancellationToken);
            times.Add((this._now - start).TotalSeconds);
        }

        Assert.Equal([0, 2, 4, 10, 12, 14, 20], times);
    }

    [Fact]
    public async Task ProtocolLinesShareTheTestLinesPaceAndTheServerNeverRefuses() {
        var pacer = new SelfTestPacer(() => this._now, (delay, _) => {
            this._now += delay;
            return Task.CompletedTask;
        }, role: SelfTestRole.Host);
        var server = new ServerBucket(5, TimeSpan.FromSeconds(1));
        var start = this._now;
        var times = new List<double>();
        for (var i = 0; i < 200; i++) {
            await pacer.WaitAsync(i % 3 == 0 ? SelfTestAction.ChannelMessage : SelfTestAction.PartnerMessage, TestContext.Current.CancellationToken);
            Assert.True(server.Take(this._now), $"The server would refuse message {i + 1}.");
            if (i < 7) {
                times.Add((this._now - start).TotalSeconds);
            }
        }

        // One pace for both: every two seconds, three in a row.
        Assert.Equal([0, 2, 4, 10, 12, 14, 20], times);
    }

    [Fact]
    public async Task EachRoleHasItsOwnMost() {
        SelfTestPacer Pacer(SelfTestRole role) => new(() => this._now, (delay, _) => {
            this._now += delay;
            return Task.CompletedTask;
        }, role: role);

        // Alone, no protocol lines at all.
        await Assert.ThrowsAsync<SelfTestSpentException>(() => Pacer(SelfTestRole.Solo).WaitAsync(SelfTestAction.PartnerMessage, TestContext.Current.CancellationToken));
        Assert.Equal(100, Pacer(SelfTestRole.Host).MostOf(SelfTestAction.ChannelMessage));
        Assert.Equal(150, Pacer(SelfTestRole.Host).MostOf(SelfTestAction.PartnerMessage));
        var partner = Pacer(SelfTestRole.Partner);
        Assert.Equal(80, partner.MostOf(SelfTestAction.ChannelMessage));
        Assert.Equal(3, partner.MostOf(SelfTestAction.Reconnect));
        Assert.Equal(1, partner.MostOf(SelfTestAction.Invite));
        Assert.Equal(0, partner.MostOf(SelfTestAction.Rename));
        for (var i = 0; i < 80; i++) {
            await partner.WaitAsync(SelfTestAction.ChannelMessage, TestContext.Current.CancellationToken);
        }

        await Assert.ThrowsAsync<SelfTestSpentException>(() => partner.WaitAsync(SelfTestAction.ChannelMessage, TestContext.Current.CancellationToken));
        // Its answers still go.
        await partner.WaitAsync(SelfTestAction.PartnerMessage, TestContext.Current.CancellationToken);

        // Messages have no hourly budget (owner, 2026-10-13): another partner run may start at once.
        var next = new SelfTestPacer(() => this._now, (delay, _) => Task.CompletedTask, partner.Spent, role: SelfTestRole.Partner);
        Assert.Equal(TimeSpan.Zero, next.WaitBeforeStart(new Dictionary<SelfTestAction, int> { [SelfTestAction.ChannelMessage] = 80 }).Wait);
    }

    [Fact]
    public async Task ARunStopsAtItsMost() {
        var pacer = this.NewPacer();
        for (var i = 0; i < SelfTestPacer.Rules[SelfTestAction.CreateChannel].RunMost; i++) {
            await pacer.WaitAsync(SelfTestAction.CreateChannel, TestContext.Current.CancellationToken);
        }

        var spent = await Assert.ThrowsAsync<SelfTestSpentException>(() => pacer.WaitAsync(SelfTestAction.CreateChannel, TestContext.Current.CancellationToken));
        Assert.Equal(SelfTestAction.CreateChannel, spent.Action);
        Assert.Equal(3, pacer.CountThisRun(SelfTestAction.CreateChannel));
    }

    [Fact]
    public async Task AfterTheFirstRefusalNothingMoreIsAskedFor() {
        var pacer = this.NewPacer();
        await pacer.WaitAsync(SelfTestAction.ChannelMessage, TestContext.Current.CancellationToken);
        pacer.Refuse();

        foreach (var action in Enum.GetValues<SelfTestAction>()) {
            await Assert.ThrowsAsync<SelfTestRefusedException>(() => pacer.WaitAsync(action, TestContext.Current.CancellationToken));
        }

        Assert.True(pacer.Refused);
    }

    [Fact]
    public void RefusalsAreTheLimitsErrorsOnly() {
        Assert.True(SelfTestPacer.IsRefusal(new ServerErrorException(ErrorCode.RateLimited, "x")));
        Assert.True(SelfTestPacer.IsRefusal(new ServerErrorException(ErrorCode.LimitReached, "x")));
        Assert.True(SelfTestPacer.IsRefusal(new ServerErrorException(ErrorCode.Blocked, "x")));
        Assert.True(SelfTestPacer.IsRefusal(new SelfTestRefusedException()));
        Assert.False(SelfTestPacer.IsRefusal(new ServerErrorException(ErrorCode.NotFound, "x")));
        Assert.False(SelfTestPacer.IsRefusal(new IOException("x")));
    }

    [Fact]
    public async Task ARunThatWouldTakeTheHourPastItsMostWaits() {
        var first = this.NewPacer();
        for (var i = 0; i < 3; i++) {
            await first.WaitAsync(SelfTestAction.CreateChannel, TestContext.Current.CancellationToken);
        }

        var spent = first.Spent;
        this._now += TimeSpan.FromMinutes(40);
        var second = this.NewPacer(spent);

        // Two runs' worth fit in the hour (eight test channels); a third's three would take it past, until the first create is an
        // hour old (in about 20 minutes).
        Assert.Equal(TimeSpan.Zero, second.WaitBeforeStart(new Dictionary<SelfTestAction, int> { [SelfTestAction.CreateChannel] = 3 }).Wait);
        var busier = this.NewPacer([.. spent, .. spent.Select(spend => spend with { At = spend.At + TimeSpan.FromSeconds(1) })]);
        var (wait, waitingFor) = busier.WaitBeforeStart(new Dictionary<SelfTestAction, int> { [SelfTestAction.CreateChannel] = 3 });
        Assert.Equal(SelfTestAction.CreateChannel, waitingFor);
        Assert.InRange(wait, TimeSpan.FromMinutes(19), TimeSpan.FromMinutes(21));
        Assert.Equal(TimeSpan.Zero, second.WaitBeforeStart(new Dictionary<SelfTestAction, int> { [SelfTestAction.ChannelMessage] = 100 }).Wait);

        this._now += TimeSpan.FromMinutes(21);
        var third = this.NewPacer(spent);
        Assert.Equal(TimeSpan.Zero, third.WaitBeforeStart(new Dictionary<SelfTestAction, int> { [SelfTestAction.CreateChannel] = 3 }).Wait);
        // Older than an hour, it isn't kept.
        Assert.True(third.Spent.Count < spent.Count);
    }

    [Fact]
    public void BackToBackRunsOfMessagesNeverWait() {
        // A full host run's messages, lookups, reconnects, renames and device lists, a minute ago: none of their server
        // limits refills slower than the self-test's pace, so none has an hourly budget (owner, 2026-10-13).
        SelfTestAction[] fast = [SelfTestAction.ChannelMessage, SelfTestAction.PartnerMessage, SelfTestAction.LocalMessage, SelfTestAction.Lookup,
            SelfTestAction.Reconnect, SelfTestAction.Rename, SelfTestAction.DeviceList];
        var spent = fast.SelectMany(action => Enumerable.Repeat(new SelfTestSpend(action, this._now - TimeSpan.FromMinutes(1)), 150)).ToList();
        var planned = fast.ToDictionary(action => action, action => SelfTestPacer.Rules[action].RunMost);
        foreach (var role in Enum.GetValues<SelfTestRole>()) {
            var pacer = new SelfTestPacer(() => this._now, (_, _) => Task.CompletedTask, spent, role: role);
            Assert.Equal((TimeSpan.Zero, null), pacer.WaitBeforeStart(planned));
        }

        Assert.All(fast, action => Assert.False(SelfTestPacer.HourMost.ContainsKey(action), action.ToString()));
    }

    [Fact]
    public void OnlyTheSlowRefillingActionsHaveAnHourlyBudgetUnderTheServersBurst() {
        // The server's defaults (RequestHandler, LimitOptions): creates 10 at once then 1 a minute; disbands 5 then 1 a
        // minute; invites to one person 20 then 1 a minute.
        Assert.Equal([SelfTestAction.CreateChannel, SelfTestAction.Disband, SelfTestAction.Invite], SelfTestPacer.HourMost.Keys.Order());
        Assert.True(SelfTestPacer.HourMost[SelfTestAction.CreateChannel] < 10);
        Assert.True(SelfTestPacer.HourMost[SelfTestAction.Disband] < 5);
        Assert.True(SelfTestPacer.HourMost[SelfTestAction.Invite] < 20);
        // Each still lets at least one run's most through.
        Assert.All(SelfTestPacer.HourMost, entry => Assert.True(entry.Value >= SelfTestPacer.Rules[entry.Key].RunMost));
    }

    [Fact]
    public void TheWaitSaysWhatIsWaiting() {
        var words = SelfTestWords.WaitBeforeStart(TimeSpan.FromMinutes(19.5), SelfTestAction.CreateChannel);
        Assert.StartsWith("Wait about 20 minutes", words, StringComparison.Ordinal);
        Assert.Contains("test channels", words, StringComparison.Ordinal);
        Assert.Contains("invites", SelfTestWords.WaitBeforeStart(TimeSpan.FromMinutes(3), SelfTestAction.Invite), StringComparison.Ordinal);
        PlainLanguage.AssertPlain(words);
    }
}
