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
        // Server: 60 at once, then 1 a second.
        [SelfTestAction.Lookup] = new(TimeSpan.FromSeconds(2), 1, TimeSpan.FromSeconds(2), 2),
        // Server: to one person 20 at once, then 1 a minute.
        [SelfTestAction.Invite] = new(TimeSpan.FromSeconds(30), 1, TimeSpan.FromSeconds(30), 8),
        // Server: 60 new connections a minute per address, 4 not logged in.
        [SelfTestAction.Reconnect] = new(TimeSpan.FromSeconds(60), 1, TimeSpan.FromSeconds(60), 5),
        // Server: 20 at once, then 1 every 6 seconds.
        [SelfTestAction.DeviceList] = new(TimeSpan.FromSeconds(10), 1, TimeSpan.FromSeconds(10), 2),
    };

    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private readonly Lock _lock = new();
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly List<SelfTestSpend> _spent;
    private readonly Dictionary<SelfTestAction, (DateTimeOffset Last, int Run, int Count)> _state = new();
    // Turns taken but not yet spent, per action: two callers waiting at once queue behind each other.
    private readonly Dictionary<SelfTestAction, DateTimeOffset> _booked = new();
    private bool _refused;

    /// <param name="now">The clock (a fake one in tests).</param>
    /// <param name="delay">How to wait (a fake one in tests).</param>
    /// <param name="spentBefore">What earlier runs spent, from the plugin's file: only the last hour counts.</param>
    public SelfTestPacer(Func<DateTimeOffset> now, Func<TimeSpan, CancellationToken, Task> delay, IEnumerable<SelfTestSpend>? spentBefore = null) {
        this._now = now;
        this._delay = delay;
        this._spent = (spentBefore ?? []).Where(spend => now() - spend.At < Hour).ToList();
    }

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
            return this._state.TryGetValue(action, out var state) ? state.Count : 0;
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
    /// past any rule's most; zero if it can start now.
    /// </summary>
    public TimeSpan WaitBeforeStart(IReadOnlyDictionary<SelfTestAction, int> planned) {
        lock (this._lock) {
            var now = this._now();
            var wait = TimeSpan.Zero;
            foreach (var (action, count) in planned) {
                var most = Rules[action].RunMost;
                var spent = this._spent.Where(spend => spend.Action == action && now - spend.At < Hour).OrderBy(spend => spend.At).ToList();
                var over = spent.Count + Math.Min(count, most) - most;
                if (over > 0) {
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
        var rule = Rules[action];
        DateTimeOffset turn;
        lock (this._lock) {
            this.ThrowIfStopped(action, rule);
            var now = this._now();
            var (last, run, count) = this._state.GetValueOrDefault(action, (DateTimeOffset.MinValue, 0, 0));
            turn = now;
            if (count > 0) {
                var gap = run >= rule.InARow ? rule.AfterARow : rule.Every;
                if (last + gap > turn) {
                    turn = last + gap;
                }
            }

            if (this._booked.TryGetValue(action, out var booked) && booked + rule.Every > turn) {
                turn = booked + rule.Every;
            }

            this._booked[action] = turn;
        }

        var wait = turn - this._now();
        if (wait > TimeSpan.Zero) {
            await this._delay(wait, ct).ConfigureAwait(false);
        }

        lock (this._lock) {
            this.ThrowIfStopped(action, rule);
            var now = this._now();
            var (last, run, count) = this._state.GetValueOrDefault(action, (DateTimeOffset.MinValue, 0, 0));
            // A run of sends is ones closer together than the pause after a run.
            run = count > 0 && now - last < rule.AfterARow && run < rule.InARow ? run + 1 : 1;
            this._state[action] = (now, run, count + 1);
            this._spent.Add(new SelfTestSpend(action, now));
        }
    }

    private void ThrowIfStopped(SelfTestAction action, PaceRule rule) {
        if (this._refused) {
            throw new SelfTestRefusedException();
        }

        if (this._state.TryGetValue(action, out var state) && state.Count >= rule.RunMost) {
            throw new SelfTestSpentException(action);
        }
    }
}

/// <summary>One thing the self-test asked the server for, and when: kept an hour, in the plugin's config folder.</summary>
public sealed record SelfTestSpend(SelfTestAction Action, DateTimeOffset At);
