using LookingGlass.Protocol;

namespace LookingGlass.Core.SelfTest;

/// <summary>What the self-test asks the server for, each paced on its own (see <see cref="SelfTestPacer.Rules"/>).</summary>
public enum SelfTestAction {
    ChannelMessage,
    LocalMessage,
    CreateChannel,
    Disband,
    Rename,
    Lookup,
    Invite,
    Reconnect,
    DeviceList,

    /// <summary>A two-player protocol line (a request or an answer): a channel message, paced with the test lines.</summary>
    PartnerMessage,
}

/// <summary>
/// How fast the self-test may ask for one thing, and how much a run (and an hour) may spend on it: at most one every
/// <see cref="Every"/>, after <see cref="InARow"/> of them a pause of <see cref="AfterARow"/>, and at most
/// <see cref="RunMost"/> in a run, and in any hour.
/// </summary>
public sealed record PaceRule(TimeSpan Every, int InARow, TimeSpan AfterARow, int RunMost);

/// <summary>Thrown once the server has refused something for a limit: the run stops sending, and never retries.</summary>
public sealed class SelfTestRefusedException() : Exception("The server asked the self-test to slow down.");

/// <summary>Thrown when a run has used all it may of something (<see cref="PaceRule.RunMost"/>).</summary>
public sealed class SelfTestSpentException(SelfTestAction action) : Exception($"The self-test has used all it may of {action}.") {
    public SelfTestAction Action { get; } = action;
}

/// <summary>
/// Keeps the self-test far below every server limit (docs/self-test-plan.md, "Pacing"), and stops it at the first refusal.
/// <list type="bullet">
/// <item>Each action waits its turn (<see cref="WaitAsync"/>): never faster than its <see cref="PaceRule"/>.</item>
/// <item>The first refusal for a limit (<see cref="IsRefusal"/>) stops everything: every later <see cref="WaitAsync"/>
/// throws <see cref="SelfTestRefusedException"/>, so nothing refused is ever asked for again.</item>
/// <item>What was spent is kept for an hour (<see cref="Spent"/>, saved by the plugin), so a run that would take the hour
/// past a rule's <see cref="PaceRule.RunMost"/> doesn't start (<see cref="WaitBeforeStart"/>).</item>
/// </list>
/// Thread-safe.
/// </summary>
public sealed class SelfTestPacer {
    /// <summary>The pace for each action: the plan's table, against the server's defaults.</summary>
    public static readonly IReadOnlyDictionary<SelfTestAction, PaceRule> Rules = new Dictionary<SelfTestAction, PaceRule> {
        // Server: 5 at once, then 1 a second.
        [SelfTestAction.ChannelMessage] = new(TimeSpan.FromSeconds(2), 3, TimeSpan.FromSeconds(6), 100),
        // Server: 5 at once, then 1 a second; to one person 30, then 1 every 2 seconds.
        [SelfTestAction.LocalMessage] = new(TimeSpan.FromSeconds(4), 1, TimeSpan.FromSeconds(4), 20),
        // Server: 10 at once, then 1 a minute; 50 channels.
        [SelfTestAction.CreateChannel] = new(TimeSpan.FromSeconds(10), 1, TimeSpan.FromSeconds(10), 3),
        // Server: 5 at once, then 1 a minute. Avoided: leaving as the only member deletes the channel.
        [SelfTestAction.Disband] = new(TimeSpan.FromSeconds(20), 1, TimeSpan.FromSeconds(20), 3),
        // Server: 10 at once, then 1 every 10 seconds.
        [SelfTestAction.Rename] = new(TimeSpan.FromSeconds(20), 1, TimeSpan.FromSeconds(20), 2),
        // Server: 60 at once, then 1 a second. The invite check looks its person up first, then each invite may again.
        [SelfTestAction.Lookup] = new(TimeSpan.FromSeconds(2), 1, TimeSpan.FromSeconds(2), 3),
        // Server: to one person 20 at once, then 1 a minute.
        [SelfTestAction.Invite] = new(TimeSpan.FromSeconds(30), 1, TimeSpan.FromSeconds(30), 8),
        // Server: 60 new connections a minute per address, 4 not logged in.
        [SelfTestAction.Reconnect] = new(TimeSpan.FromSeconds(60), 1, TimeSpan.FromSeconds(60), 5),
        // Server: 20 at once, then 1 every 6 seconds.
        [SelfTestAction.DeviceList] = new(TimeSpan.FromSeconds(10), 1, TimeSpan.FromSeconds(10), 2),
        // Shares the channel messages' pace (see LaneOf); its own most, for the host and the partner.
        [SelfTestAction.PartnerMessage] = new(TimeSpan.FromSeconds(2), 3, TimeSpan.FromSeconds(6), 150),
    };

    /// <summary>
    /// The most a run may spend of each action for its role, where it differs from <see cref="PaceRule.RunMost"/>: a host's
    /// protocol lines; a partner's 80 test lines, a few reconnects, one channel of its own and one invite (windows-only 10).
    /// </summary>
    private static readonly Dictionary<SelfTestRole, Dictionary<SelfTestAction, int>> RoleMost = new() {
        [SelfTestRole.Solo] = new() { [SelfTestAction.PartnerMessage] = 0 },
        [SelfTestRole.Host] = new() { [SelfTestAction.PartnerMessage] = 150 },
        [SelfTestRole.Partner] = new() {
            [SelfTestAction.ChannelMessage] = 80, [SelfTestAction.PartnerMessage] = 150, [SelfTestAction.LocalMessage] = 20, [SelfTestAction.Reconnect] = 3,
            [SelfTestAction.CreateChannel] = 1, [SelfTestAction.Invite] = 1, [SelfTestAction.Lookup] = 1, [SelfTestAction.Rename] = 0, [SelfTestAction.Disband] = 1,
            [SelfTestAction.DeviceList] = 0,
        },
    };

    /// <summary>Test lines and protocol lines are both channel messages: they share one pace (and so the server's limit).</summary>
    private static SelfTestAction LaneOf(SelfTestAction action) => action == SelfTestAction.PartnerMessage ? SelfTestAction.ChannelMessage : action;

    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private readonly Lock _lock = new();
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly List<SelfTestSpend> _spent;
    // Each lane's last send and how many in a row; each action's count this run.
    private readonly Dictionary<SelfTestAction, (DateTimeOffset Last, int Run)> _lanes = new();
    private readonly Dictionary<SelfTestAction, int> _counts = new();
    // Turns taken but not yet spent, per lane: two callers waiting at once queue behind each other.
    private readonly Dictionary<SelfTestAction, DateTimeOffset> _booked = new();
    private bool _refused;

    /// <param name="now">The clock (a fake one in tests).</param>
    /// <param name="delay">How to wait (a fake one in tests).</param>
    /// <param name="spentBefore">What earlier runs spent, from the plugin's file: only the last hour counts.</param>
    /// <param name="thisRun">What this run spent before a relog or reload (its run file): its pace and its most go on from there.</param>
    /// <param name="role">Alone, a host, or a partner: each may spend its own most.</param>
    public SelfTestPacer(Func<DateTimeOffset> now, Func<TimeSpan, CancellationToken, Task> delay, IEnumerable<SelfTestSpend>? spentBefore = null,
        IEnumerable<SelfTestSpend>? thisRun = null, SelfTestRole role = SelfTestRole.Solo) {
        this._now = now;
        this._delay = delay;
        this.Role = role;
        var mine = (thisRun ?? []).ToList();
        this._spent = (spentBefore ?? []).Concat(mine).Distinct().Where(spend => now() - spend.At < Hour).ToList();
        foreach (var group in mine.GroupBy(spend => spend.Action)) {
            this._counts[group.Key] = group.Count();
        }

        foreach (var lane in mine.GroupBy(spend => LaneOf(spend.Action))) {
            var rule = Rules[lane.Key];
            var times = lane.Select(spend => spend.At).Order().ToList();
            var run = 1;
            for (var i = 1; i < times.Count; i++) {
                run = times[i] - times[i - 1] < rule.AfterARow && run < rule.InARow ? run + 1 : 1;
            }

            this._lanes[lane.Key] = (times[^1], run);
        }
    }

    public SelfTestRole Role { get; }

    /// <summary>The most this run may spend of an action, for its role.</summary>
    public int MostOf(SelfTestAction action) => RoleMost[this.Role].TryGetValue(action, out var most) ? most : Rules[action].RunMost;

    /// <summary>Told of every action spent, as it is (the runner keeps it in the run file).</summary>
    public Action<SelfTestSpend>? OnSpend { get; set; }

    /// <summary>The server refused something for a limit: nothing more is sent.</summary>
    public bool Refused {
        get {
            lock (this._lock) {
                return this._refused;
            }
        }
    }

    /// <summary>What was spent in the last hour, this run's included, to save for the next run.</summary>
    public IReadOnlyList<SelfTestSpend> Spent {
        get {
            lock (this._lock) {
                var now = this._now();
                return this._spent.Where(spend => now - spend.At < Hour).ToList();
            }
        }
    }

    /// <summary>How many of an action this run has spent.</summary>
    public int CountThisRun(SelfTestAction action) {
        lock (this._lock) {
            return this._counts.GetValueOrDefault(action);
        }
    }

    /// <summary>
    /// Whether a server error is a refusal for a limit: rate limited, a limit reached, or blocked. Any of them stops the run.
    /// </summary>
    public static bool IsRefusal(Exception ex) =>
        ex is SelfTestRefusedException || (ex is Client.ServerErrorException server && server.Code is ErrorCode.RateLimited or ErrorCode.LimitReached or ErrorCode.Blocked);

    /// <summary>Stops all sending: the server refused something. Never undone for this run.</summary>
    public void Refuse() {
        lock (this._lock) {
            this._refused = true;
        }
    }

    /// <summary>
    /// How long to wait before a run that may spend up to <paramref name="planned"/> can start without taking the last hour
    /// past any action's most (for this role); zero if it can start now.
    /// </summary>
    public TimeSpan WaitBeforeStart(IReadOnlyDictionary<SelfTestAction, int> planned) {
        lock (this._lock) {
            var now = this._now();
            var wait = TimeSpan.Zero;
            foreach (var (action, count) in planned) {
                var most = this.MostOf(action);
                var spent = this._spent.Where(spend => spend.Action == action && now - spend.At < Hour).OrderBy(spend => spend.At).ToList();
                var over = spent.Count + Math.Min(count, most) - most;
                if (over > 0 && over <= spent.Count) {
                    // Once the oldest "over" of them are an hour old, there is room.
                    var free = spent[over - 1].At + Hour - now;
                    if (free > wait) {
                        wait = free;
                    }
                }
            }

            return wait;
        }
    }

    /// <summary>
    /// Waits for an action's turn, then counts it as spent. Call right before asking the server.
    /// </summary>
    /// <exception cref="SelfTestRefusedException">The server refused something earlier in the run.</exception>
    /// <exception cref="SelfTestSpentException">The run has used all it may of this action.</exception>
    public async Task WaitAsync(SelfTestAction action, CancellationToken ct = default) {
        var lane = LaneOf(action);
        var rule = Rules[lane];
        DateTimeOffset turn;
        lock (this._lock) {
            this.ThrowIfStopped(action);
            var now = this._now();
            turn = now;
            if (this._lanes.TryGetValue(lane, out var state)) {
                var gap = state.Run >= rule.InARow ? rule.AfterARow : rule.Every;
                if (state.Last + gap > turn) {
                    turn = state.Last + gap;
                }
            }

            if (this._booked.TryGetValue(lane, out var booked) && booked + rule.Every > turn) {
                turn = booked + rule.Every;
            }

            this._booked[lane] = turn;
        }

        var wait = turn - this._now();
        if (wait > TimeSpan.Zero) {
            await this._delay(wait, ct).ConfigureAwait(false);
        }

        SelfTestSpend spend;
        lock (this._lock) {
            this.ThrowIfStopped(action);
            var now = this._now();
            var (last, run) = this._lanes.GetValueOrDefault(lane, (DateTimeOffset.MinValue, 0));
            // A run of sends is ones closer together than the pause after a run.
            run = last != DateTimeOffset.MinValue && now - last < rule.AfterARow && run < rule.InARow ? run + 1 : 1;
            this._lanes[lane] = (now, run);
            this._counts[action] = this._counts.GetValueOrDefault(action) + 1;
            spend = new SelfTestSpend(action, now);
            this._spent.Add(spend);
        }

        this.OnSpend?.Invoke(spend);
    }

    private void ThrowIfStopped(SelfTestAction action) {
        if (this._refused) {
            throw new SelfTestRefusedException();
        }

        if (this._counts.GetValueOrDefault(action) >= this.MostOf(action)) {
            throw new SelfTestSpentException(action);
        }
    }
}

/// <summary>Who a run is: alone, the host of two players, or the partner.</summary>
public enum SelfTestRole {
    Solo,
    Host,
    Partner,
}
/// <summary>One thing the self-test asked the server for, and when: kept an hour, in the plugin's config folder.</summary>
public sealed record SelfTestSpend(SelfTestAction Action, DateTimeOffset At);
