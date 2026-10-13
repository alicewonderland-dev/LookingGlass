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
            await pacer.WaitAsync(action);
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
            await pacer.WaitAsync(SelfTestAction.ChannelMessage);
            times.Add((this._now - start).TotalSeconds);
        }

        Assert.Equal([0, 2, 4, 10, 12, 14, 20], times);
    }

    [Fact]
    public async Task ARunStopsAtItsMost() {
        var pacer = this.NewPacer();
        for (var i = 0; i < SelfTestPacer.Rules[SelfTestAction.CreateChannel].RunMost; i++) {
            await pacer.WaitAsync(SelfTestAction.CreateChannel);
        }

        var spent = await Assert.ThrowsAsync<SelfTestSpentException>(() => pacer.WaitAsync(SelfTestAction.CreateChannel));
        Assert.Equal(SelfTestAction.CreateChannel, spent.Action);
        Assert.Equal(3, pacer.CountThisRun(SelfTestAction.CreateChannel));
    }

    [Fact]
    public async Task AfterTheFirstRefusalNothingMoreIsAskedFor() {
        var pacer = this.NewPacer();
        await pacer.WaitAsync(SelfTestAction.ChannelMessage);
        pacer.Refuse();

        foreach (var action in Enum.GetValues<SelfTestAction>()) {
            await Assert.ThrowsAsync<SelfTestRefusedException>(() => pacer.WaitAsync(action));
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
            await first.WaitAsync(SelfTestAction.CreateChannel);
        }

        var spent = first.Spent;
        this._now += TimeSpan.FromMinutes(40);
        var second = this.NewPacer(spent);

        var wait = second.WaitBeforeStart(new Dictionary<SelfTestAction, int> { [SelfTestAction.CreateChannel] = 1 });
        // Once the first create is an hour old (in about 20 minutes).
        Assert.Equal(spent[0].At + TimeSpan.FromHours(1) - this._now, wait);
        Assert.InRange(wait, TimeSpan.FromMinutes(19), TimeSpan.FromMinutes(20));
        Assert.Equal(TimeSpan.Zero, second.WaitBeforeStart(new Dictionary<SelfTestAction, int> { [SelfTestAction.ChannelMessage] = 100 }));

        this._now += TimeSpan.FromMinutes(21);
        var third = this.NewPacer(spent);
        Assert.Equal(TimeSpan.Zero, third.WaitBeforeStart(new Dictionary<SelfTestAction, int> { [SelfTestAction.CreateChannel] = 1 }));
        // Older than an hour, it isn't kept.
        Assert.True(third.Spent.Count < spent.Count);
    }
}
