using System.Text.Json;

namespace LookingGlass.Core.SelfTest;

/// <summary>Where a run is, for the window.</summary>
public enum SelfTestState {
    Starting,
    Running,

    /// <summary>Waiting before a step: not in a private place, someone near, not connected.</summary>
    Paused,

    /// <summary>A guided step waits on the tester.</summary>
    NeedsYou,

    /// <summary>Waiting for a relog or a plugin reload; it carries on afterwards.</summary>
    WaitingToCarryOn,

    CleaningUp,
    Done,
}

/// <summary>A check's line in the window.</summary>
public sealed record CheckView(SelfTestCheck Check, string Suite, CheckRecord? Result, bool Current);

/// <summary>A guided step waiting on the tester.</summary>
public sealed record PromptView(GuidedPrompt Prompt, int Step, int Steps, string? Watching);

/// <summary>Everything the window shows of a run: an immutable copy, swapped as the run goes.</summary>
public sealed record SelfTestView {
    public SelfTestState State { get; init; }

    public string Code { get; init; } = "";

    public IReadOnlyList<CheckView> Checks { get; init; } = [];

    public PromptView? Prompt { get; init; }

    /// <summary>Why it is paused (the words to show), while <see cref="SelfTestState.Paused"/>.</summary>
    public string? Pause { get; init; }

    /// <summary>The kind of place it is in now.</summary>
    public PlaceKind Place { get; init; }

    /// <summary>The clean-up's summary, once it has run.</summary>
    public string? CleanUp { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>Alone, the host of two players, or the partner.</summary>
    public SelfTestRole Role { get; init; }

    /// <summary>Two players: a line about the other tester (the partner's facts, or what the partner answered), never a name.</summary>
    public string? PairLine { get; init; }

    /// <summary>How many checks have a result.</summary>
    public int Finished => this.Checks.Count(check => check.Result != null);
}

/// <summary>Stops a step: the tester stopped the run, or it was paused for good.</summary>
internal sealed class SelfTestStopException(string why) : Exception(why) {
    public string Why { get; } = why;
}

/// <summary>
/// One self-test run (docs/self-test-plan.md): its checks, one after the other, each a list of steps; then the clean-up,
/// always. The rules it keeps, each tested:
/// <list type="bullet">
/// <item>The whole run only in a private place: the private-area check (<see cref="PrivateAreaWatch"/>) runs before every
/// step, and again right before every line typed; a "no" pauses before the step, never after. In a house or apartment, a
/// step that types also waits while anyone else is near.</item>
/// <item>Every server request is paced (<see cref="SelfTestPacer"/>); the first refusal for a limit stops the run, and
/// nothing refused is asked again.</item>
/// <item>Every setting is snapshotted in the run file before it is first changed; the clean-up puts it back, and runs
/// whatever happens: at the end, when stopped, after a refusal, or when a check throws.</item>
/// <item>A check that throws fails alone; the run goes on.</item>
/// <item>Results and the log lines hold fixed words only: no names, no message text.</item>
/// </list>
/// </summary>
public sealed partial class SelfTestRun {
    /// <summary>How long a run waits for a relog or reload before it gives up and cleans up.</summary>
    public static readonly TimeSpan MostWaitToCarryOn = TimeSpan.FromMinutes(30);

    /// <summary>How long a disconnect may last before the run stops.</summary>
    public static readonly TimeSpan MostDisconnected = TimeSpan.FromMinutes(5);

    /// <summary>After a refusal, how long the run waits before it cleans up.</summary>
    public static readonly TimeSpan AfterRefusal = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(500);

    private readonly ISelfTestHost _host;
    private readonly IReadOnlyList<SelfTestSuite> _suites;
    private readonly SelfTestRunFile _file;
    private readonly SelfTestRunData _data;
    private readonly bool _resumed;
    private readonly PrivateAreaWatch _place = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private SelfTestView _view;
    private string? _stopWhy;
    private TaskCompletionSource<(GuidedAnswer Answer, string? Note, string? Input)>? _answer;
    private TaskCompletionSource? _carryOn;
    private int _marker;
    private bool _abandoned;

    /// <param name="suites">The suites chosen (for a resumed run, the ones its file names).</param>
    /// <param name="resume">A run file to carry on from (after a reload), or null for a new run.</param>
    public SelfTestRun(ISelfTestHost host, IReadOnlyList<SelfTestSuite> suites, SelfTestPacer pacer, SelfTestRunFile file, SelfTestRunData data, bool resume = false) {
        this._host = host;
        this._suites = suites;
        this.Pacer = pacer;
        this._file = file;
        this._data = data;
        this._resumed = resume;
        host.RunFor(data.ContentId, data.ServerUrl);
        if (data.Role != SelfTestRole.Solo) {
            // The other tester isn't "someone near", and only their lines are read as the protocol's.
            host.Pair(data.Partner, data.Code, data.Role);
        }

        this.Link = data.Role == SelfTestRole.Host ? new PartnerLink(this) : null;
        // Every action spent goes in the run file at once: a reload keeps the run's pace and its most.
        pacer.OnSpend = spend => {
            lock (this._lock) {
                this._data.Spent.Add(spend);
            }

            this.Save();
        };
        this._view = new SelfTestView {
            State = SelfTestState.Starting,
            Code = data.Code,
            StartedAt = data.StartedAt,
            Checks = this.CheckViews(null),
            Role = data.Role,
        };
    }

    /// <summary>The host's side of two players (null alone, or for the partner).</summary>
    public PartnerLink? Link { get; }

    /// <summary>A new run's file contents: its code, the character and server, the suites chosen.</summary>
    /// <param name="onlyChecks">Only these checks of the suites (the failed ones, run again), or all.</param>
    /// <param name="role">Alone, or which of two players.</param>
    /// <param name="other">Two players: the other tester's name@world (held in the run file while the run lasts only).</param>
    public static SelfTestRunData NewData(string code, ulong contentId, string serverUrl, DateTimeOffset now, IEnumerable<SelfTestSuite> suites,
        IEnumerable<string>? onlyChecks = null, SelfTestRole role = SelfTestRole.Solo, string? other = null) => new() {
        Code = code,
        ContentId = contentId,
        ServerUrl = serverUrl,
        StartedAt = now,
        Phase = SelfTestPhase.Running,
        Suites = suites.Select(suite => suite.Id).ToList(),
        OnlyChecks = onlyChecks?.ToList() ?? [],
        Role = role,
        Partner = role == SelfTestRole.Solo ? null : other?.Trim(),
    };

    /// <summary>A code typed in by a partner: four of the letters and digits a code is made of.</summary>
    public static bool IsCode(string? code) => code is { Length: 4 } && code.All(c => CodeLetters.Contains(c, StringComparison.Ordinal));

    private const string CodeLetters = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>A new run's code: four capital letters and digits, without the ones easily mistaken (0, O, 1, I).</summary>
    public static string NewCode() {
        const string letters = CodeLetters;
        return new string(Enumerable.Range(0, 4).Select(_ => letters[System.Security.Cryptography.RandomNumberGenerator.GetInt32(letters.Length)]).ToArray());
    }

    public SelfTestPacer Pacer { get; }

    public ISelfTestHost Host => this._host;

    /// <summary>The run's file contents (the window reads the code and the results; the runner alone changes it).</summary>
    public SelfTestRunData Data => this._data;

    public string Code => this._data.Code;

    /// <summary>What the window shows now.</summary>
    public SelfTestView View {
        get {
            lock (this._lock) {
                return this._view;
            }
        }
    }

    /// <summary>
    /// The run's test channels so far, and the other tester's it joined (a copy, safe from any thread): the only channels
    /// the plugin's host sends to, leaves or changes for the run.
    /// </summary>
    public IReadOnlyList<string> TestChannelsNow() {
        lock (this._lock) {
            return this._data.TestChannels.Concat(this._data.Joined).Distinct().ToList();
        }
    }

    /// <summary>Joined the other tester's channel (a partner joins the host's; the host the partner's one): kept in the run file.</summary>
    internal void MarkJoined(string channelId) {
        lock (this._lock) {
            if (!this._data.Joined.Contains(channelId)) {
                this._data.Joined.Add(channelId);
            }
        }

        this.Save();
    }

    /// <summary>Two players: shows a line about the other tester (fixed words, never a name).</summary>
    internal void ShowPairLine(string line) => this.SetView(view => view with { PairLine = line });

    /// <summary>The run blocked the other tester (unblocked at the clean-up), or unblocked them again.</summary>
    internal void MarkBlocked(bool blocked) {
        lock (this._lock) {
            this._data.BlockedOther = blocked;
        }

        this.Save();
    }

    /// <summary>Two players: the pair has ended (bye, or gone): the other tester's name goes from the run file now.</summary>
    internal void ForgetOtherName() {
        lock (this._lock) {
            if (this._data.Partner == null) {
                return;
            }

            this._data.Partner = null;
        }

        this.Save();
    }

    /// <summary>Saves the run file (after a step changed the run's data).</summary>
    internal void SaveNow() => this.Save();

    /// <summary>A test channel the run made by itself, kept in the file before anything is sent to it.</summary>
    internal void AddTestChannel(string channelId) {
        lock (this._lock) {
            this._data.TestChannels.Add(channelId);
        }

        this.Save();
    }

    /// <summary>The marker every typed and sent line starts with: "LG self-test K7Q2".</summary>
    public string MarkerPrefix => $"LG self-test {this.Code}";

    /// <summary>
    /// Whether a channel's name looks like a self-test's (made by one, or renamed by its history check: "LG self-test K7Q2",
    /// "... b", "... r"): only for the leftovers list, never to remove anything by itself.
    /// </summary>
    public static bool IsTestChannelName(string? name) => name != null && TestName().IsMatch(name);

    [System.Text.RegularExpressions.GeneratedRegex(@"^LG self-test [A-Z0-9]{4}( .*)?$")]
    private static partial System.Text.RegularExpressions.Regex TestName();

    /// <summary>The test channel's name for a run: "LG self-test K7Q2", then "LG self-test K7Q2 b" and "c".</summary>
    public static string ChannelName(string code, int index) => index == 0 ? $"LG self-test {code}" : $"LG self-test {code} {(char) ('a' + index)}";

    /// <summary>The checks of the suites chosen, in order.</summary>
    private IEnumerable<(SelfTestSuite Suite, SelfTestCheck Check)> Checks() =>
        this._suites.SelectMany(suite => suite.Checks.Where(check => this._data.OnlyChecks.Count == 0 || this._data.OnlyChecks.Contains(check.Id))
            .Select(check => (suite, check)));

    // ================================================================ the tester's buttons

    /// <summary>Stop and clean up: the step in progress is abandoned (a line being typed is already sent or not), then the clean-up runs.</summary>
    public void Stop(string? why = null) {
        lock (this._lock) {
            this._stopWhy ??= why ?? SelfTestWords.StoppedByYou;
        }

        this._stop.Cancel();
    }

    /// <summary>
    /// The plugin is going away (turned off, or the game closing): the run stops without cleaning up, and its file stays,
    /// so the next start carries it on (if it waits for that) or puts everything back.
    /// </summary>
    public void Abandon() {
        lock (this._lock) {
            this._abandoned = true;
            this._stopWhy ??= SelfTestWords.StoppedByYou;
        }

        this._stop.Cancel();
    }

    private bool Abandoned {
        get {
            lock (this._lock) {
                return this._abandoned;
            }
        }
    }

    /// <summary>The tester answered a guided step.</summary>
    /// <param name="note">An optional note, as typed (the window asks for no names).</param>
    /// <param name="input">What was typed in the step's own box (<see cref="GuidedPrompt.Input"/>): for the step only.</param>
    public void Answer(GuidedAnswer answer, string? note = null, string? input = null) {
        TaskCompletionSource<(GuidedAnswer, string?, string?)>? waiting;
        lock (this._lock) {
            waiting = this._answer;
        }

        waiting?.TrySetResult((answer, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), string.IsNullOrWhiteSpace(input) ? null : input.Trim()));
    }

    /// <summary>The server refused something for a limit (seen by the plugin, for a line typed): the run stops sending.</summary>
    public void ReportRefusal() {
        this.Pacer.Refuse();
        this.Stop(SelfTestWords.ServerSlowDown);
    }

    /// <summary>A relog or reload the run waited for has happened (or the tester pressed Carry on): it goes on.</summary>
    public void CarriedOn() {
        TaskCompletionSource? waiting;
        lock (this._lock) {
            waiting = this._carryOn;
        }

        waiting?.TrySetResult();
    }

    // ================================================================ the run

    /// <summary>
    /// Runs the checks, then cleans up (unless abandoned, or waiting for a reload when the plugin went away). Never throws.
    /// </summary>
    public async Task RunAsync() {
        var token = this._stop.Token;
        try {
            this._data.Phase = SelfTestPhase.Running;
            this._data.WaitingSince = null;
            // Before anything else: the file is what puts everything back after a crash.
            this.Save();
            var started = !this._resumed;
            foreach (var (suite, check) in this.Checks()) {
                if (this._data.Results.Any(result => result.Check == check.Id)) {
                    continue;
                }

                var first = 0;
                if (!started) {
                    if (this._data.NextCheck != check.Id) {
                        continue;
                    }

                    started = true;
                    first = this._data.NextStep;
                }

                token.ThrowIfCancellationRequested();
                var record = await this.RunCheckAsync(suite, check, first, token).ConfigureAwait(false);
                if (this.Abandoned) {
                    // Nothing more is written: the file is for the next start.
                    break;
                }

                this.Record(record);
                if (record.Outcome == CheckOutcome.Stopped) {
                    break;
                }
            }
        } catch (OperationCanceledException) when (token.IsCancellationRequested) {
            // Stopped between checks: the clean-up below.
        } catch (SelfTestStopException) {
            // As above.
        } catch (Exception ex) {
            this._host.Log($"[selftest] the runner failed: {ex.GetType().Name}");
        }

        if (this.Abandoned) {
            this.SetView(view => view with { State = SelfTestState.Done, EndedAt = this._host.Now });
            return;
        }

        if (this.Pacer.Refused) {
            // Nothing more is sent for a while; leaving the test channel is a different request, and costs no limit.
            this.SetView(view => view with { State = SelfTestState.CleaningUp, Pause = SelfTestWords.ServerSlowDown });
            try {
                await this._host.Delay(AfterRefusal, CancellationToken.None).ConfigureAwait(false);
            } catch (Exception) {
                // Clean up anyway.
            }
        }

        await this.CleanUpAsync().ConfigureAwait(false);
    }

    private void Record(CheckRecord record) {
        lock (this._lock) {
            this._data.Results.RemoveAll(result => result.Check == record.Check);
            this._data.Results.Add(record);
            this._data.NextCheck = null;
            this._data.NextStep = 0;
            this._data.InProgress = [];
        }

        this.Save();
        this._host.Log(SelfTestReport.LogLine(this.CheckById(record.Check)?.Steps ?? record.Check, record));
        this.SetView(view => view with { Checks = this.CheckViews(null) });
    }

    private SelfTestCheck? CheckById(string id) => this.Checks().Select(entry => entry.Check).FirstOrDefault(check => check.Id == id);

    /// <summary>Runs one check, from <paramref name="first"/> (a check carried on after a reload): its result. Never throws but to stop.</summary>
    private async Task<CheckRecord> RunCheckAsync(SelfTestSuite suite, SelfTestCheck check, int first, CancellationToken token) {
        this.SetView(view => view with { State = SelfTestState.Running, Checks = this.CheckViews(check.Id), Prompt = null });
        var results = new List<(SelfTestStep Step, StepResult Result, DateTimeOffset At)>();
        var notes = new List<string>();
        var state = new Dictionary<string, object>();
        if (first > 0) {
            // Carried on after a reload: the steps before, as the run file kept them (the one it waited for passed).
            List<StepRecord> kept;
            lock (this._lock) {
                kept = this._data.InProgress.ToList();
            }

            results.AddRange(check.StepList.Take(first).Select((step, i) => i < kept.Count
                ? (step, new StepResult(kept[i].Outcome, kept[i].Why, kept[i].NotRun, kept[i].Confirmed), kept[i].At)
                : (step, StepResult.Pass(), this._host.Now)));
        }

        try {
            var environment = await this._host.ReadEnvironmentAsync().ConfigureAwait(false);
            var needs = check.Needs;
            if (needs.HasFlag(SelfTestNeeds.Partner)) {
                // Only the host of two players, paired, with the partner still answering.
                if ((this.Link == null ? SelfTestWords.NeedsPartner : this.Link.Lacks) is { } noPartner) {
                    return new CheckRecord { Check = check.Id, Outcome = CheckOutcome.Skipped, Why = noPartner, At = this._host.Now };
                }

                needs &= ~SelfTestNeeds.Partner;
            }

            if (needs.HasFlag(SelfTestNeeds.HistoryOffAndEmpty)) {
                bool already;
                lock (this._lock) {
                    already = this._data.HistoryWasOffAndEmpty;
                }

                if (already) {
                    // Found so earlier in this run (and turned on by it since): everything in it is the test's.
                    needs &= ~SelfTestNeeds.HistoryOffAndEmpty;
                } else if (environment.Lacks(SelfTestNeeds.HistoryOffAndEmpty) == null) {
                    lock (this._lock) {
                        this._data.HistoryWasOffAndEmpty = true;
                    }

                    this.Save();
                }
            }

            var lacks = environment.Lacks(needs) ?? check.CantRun?.Invoke(environment);
            if (check.Needs.HasFlag(SelfTestNeeds.TestChannel) && lacks == null && this._data.TestChannels.Count == 0
                && environment.ChannelCount > SelfTestEnvironment.MostOwnChannels) {
                lacks = SelfTestWords.NeedsChannelRoom;
            }

            if (lacks != null) {
                return new CheckRecord { Check = check.Id, Outcome = CheckOutcome.Skipped, Why = lacks, At = this._host.Now };
            }

            for (var i = first; i < check.StepList.Count; i++) {
                var step = check.StepList[i];
                await this.GateAsync(step.Types, token).ConfigureAwait(false);
                var context = new StepContext(this, check, i, token, notes, state);
                StepResult result;
                if (step.Kind == StepKind.CarryOnAfter) {
                    lock (this._lock) {
                        this._data.InProgress = results.Select(entry => new StepRecord(entry.Result.Outcome, entry.Result.Why, entry.Result.NotRun, entry.Result.Confirmed, entry.At))
                            .ToList();
                    }

                    result = await this.CarryOnAsync(check, i, step, context, token).ConfigureAwait(false);
                } else {
                    result = await this.RunStepAsync(step, context, token).ConfigureAwait(false);
                }

                results.Add((step, result, this._host.Now));
                // A step that failed, threw or timed out between its own disconnect and reconnect: connected again now, so
                // the gates' connection and same-character checks are back for what comes next.
                await this.ReconnectIfTheRunDisconnectedAsync().ConfigureAwait(false);
                if (result.Outcome == StepOutcome.Failed || result.EndsCheck) {
                    // The rest of a check builds on what failed (or what it needs is missing): it stops there.
                    break;
                }
            }
        } catch (Exception ex) when (ex is OperationCanceledException or SelfTestStopException || SelfTestPacer.IsRefusal(ex)) {
            if (SelfTestPacer.IsRefusal(ex)) {
                this.Pacer.Refuse();
                lock (this._lock) {
                    this._stopWhy ??= SelfTestWords.ServerSlowDown;
                }

                this._stop.Cancel();
            }

            string why;
            lock (this._lock) {
                why = this._stopWhy ?? (ex as SelfTestStopException)?.Why ?? SelfTestWords.StoppedByYou;
            }

            return Summarise(check, results, notes) with { Outcome = CheckOutcome.Stopped, Why = why, At = this._host.Now };
        } finally {
            this.SetView(view => view with { Prompt = null });
        }

        return Summarise(check, results, notes);
    }

    /// <summary>Runs a step with its time-out. A step that throws fails, with fixed words; stopping and refusals go up.</summary>
    private async Task<StepResult> RunStepAsync(SelfTestStep step, StepContext context, CancellationToken token) {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (step.Kind == StepKind.Automatic) {
            timeout.CancelAfter(step.Timeout);
            lock (this._lock) {
                this._stepTimeout = timeout;
                this._stepTimeoutLength = step.Timeout;
            }
        }

        context.Token = timeout.Token;
        try {
            return await step.Run(context).ConfigureAwait(false);
        } catch (Exception ex) when (ex is OperationCanceledException or SelfTestStopException || SelfTestPacer.IsRefusal(ex)) {
            if (token.IsCancellationRequested || ex is SelfTestStopException || SelfTestPacer.IsRefusal(ex)) {
                throw;
            }

            return StepResult.Fail(SelfTestWords.TimedOut);
        } catch (SelfTestSpentException) {
            return StepResult.Skip(SelfTestWords.RunSpent);
        } catch (PartnerSilentException silent) {
            return StepResult.Skip(silent.Gone ? SelfTestWords.PartnerGone : SelfTestWords.PartnerSilent);
        } catch (PartnerRefusedException) {
            return StepResult.Skip(SelfTestWords.PartnerRefused);
        } catch (PartnerMeddlingException) {
            // Someone else knows the run's lines: nothing more is sent, and the run cleans up.
            lock (this._lock) {
                this._stopWhy ??= SelfTestWords.Meddling;
            }

            this._stop.Cancel();
            throw new SelfTestStopException(SelfTestWords.Meddling);
        } catch (SelfTestNotTypedException notTyped) {
            return StepResult.Fail(notTyped.NotPrivate ? SelfTestWords.NotPrivate : SelfTestWords.NotSay);
        } catch (Exception ex) {
            // Never the message: it may hold a name, or what the server said.
            this._host.Log($"[selftest] a step failed: {ex.GetType().Name}");
            return StepResult.Fail(SelfTestWords.SomethingWentWrong);
        } finally {
            lock (this._lock) {
                if (this._stepTimeout == timeout) {
                    this._stepTimeout = null;
                }
            }
        }
    }

    /// <summary>
    /// A relog or reload: the run file says where to carry on (the next step), and the step waits until the host sees the
    /// relog, the tester presses Carry on or Skip, or the plugin goes away (then the next start carries on from the file).
    /// </summary>
    private async Task<StepResult> CarryOnAsync(SelfTestCheck check, int index, SelfTestStep step, StepContext context, CancellationToken token) {
        var carryOn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (this._lock) {
            this._carryOn = carryOn;
            this._data.Phase = SelfTestPhase.WaitingToCarryOn;
            this._data.WaitingSince = this._host.Now;
            this._data.NextCheck = check.Id;
            this._data.NextStep = index + 1;
        }

        this.Save();
        if (step.Types) {
            // The tester types on the way (/lgl while LookingGlass is off): Say underneath, as for any typed line.
            await this.EnsureSayAsync().ConfigureAwait(false);
        }

        var logins = this._host.Logins;
        var since = this._host.Now;
        var prompt = new GuidedPrompt(step.Title) { Short = null };
        var answer = this.NewAnswer();
        this.SetView(view => view with { State = SelfTestState.WaitingToCarryOn, Prompt = new PromptView(prompt, index + 1, check.StepList.Count, SelfTestWords.WaitingToCarryOn) });
        var result = new StepResult(StepOutcome.Passed);
        try {
            while (true) {
                token.ThrowIfCancellationRequested();
                await this.KeepPartnerAsync(token).ConfigureAwait(false);
                if (carryOn.Task.IsCompleted || this._host.Logins != logins) {
                    break;
                }

                if (answer.Task.IsCompleted) {
                    var (given, note, _) = answer.Task.Result;
                    context.AddNote(note);
                    if (given == GuidedAnswer.Skip) {
                        result = StepResult.Skip(SelfTestWords.YouSkipped);
                    }

                    break;
                }

                if (this._host.Now - since > MostWaitToCarryOn) {
                    throw new SelfTestStopException(SelfTestWords.GaveUpWaiting);
                }

                await Task.WhenAny(carryOn.Task, answer.Task, this._host.Delay(Poll, token)).ConfigureAwait(false);
            }
        } finally {
            lock (this._lock) {
                this._carryOn = null;
                this._answer = null;
                // Abandoned (the plugin going away while it waits), the file keeps saying where to carry on at the next start.
                // Otherwise it no longer waits: carried on, skipped, or stopped (and cleaned up).
                if (!this._abandoned) {
                    this._data.Phase = SelfTestPhase.Running;
                    this._data.WaitingSince = null;
                    this._data.NextCheck = null;
                    this._data.NextStep = 0;
                }
            }

            if (!this.Abandoned) {
                this.Save();
            }
        }

        return result;
    }

    /// <summary>A check's record from its steps' results.</summary>
    private static CheckRecord Summarise(SelfTestCheck check, List<(SelfTestStep Step, StepResult Result, DateTimeOffset At)> results, List<string> notes) {
        var failed = results.FirstOrDefault(entry => entry.Result.Outcome == StepOutcome.Failed);
        var notRun = results.Where(entry => entry.Result.NotRun != null).Select(entry => entry.Result.NotRun!)
            .Concat(results.Where(entry => entry.Result.Outcome == StepOutcome.Skipped).Select(entry => $"{Lower(entry.Step.Title)}: {entry.Result.Why}"))
            .Distinct().ToList();
        var confirmed = results.Where(entry => entry.Result.Confirmed != null).Select(entry => entry.Result.Confirmed!).Distinct().ToList();
        var did = results.Where(entry => entry.Result.Did != null).Select(entry => entry.Result.Did!).ToList();
        if (failed.Result != null) {
            return new CheckRecord {
                Check = check.Id, Outcome = CheckOutcome.Failed, Why = failed.Result.Why, Step = failed.Step.Checklist, At = failed.At,
                NotRun = notRun, Confirmed = confirmed, Notes = notes.ToList(), Did = did,
            };
        }

        // A pass needs one of its real steps to have passed (owner, 2026-10-13): setup steps and notes never make one. Otherwise
        // it is "not run", with why: the real steps' reasons first, then the setup's.
        if (!results.Any(entry => entry.Step.Real && entry.Result.Outcome == StepOutcome.Passed)) {
            var reasons = results.Where(entry => entry.Result.Outcome == StepOutcome.Skipped).OrderBy(entry => entry.Step.Real ? 0 : 1)
                .Select(entry => entry.Result.Why).OfType<string>().Distinct().ToList();
            return new CheckRecord {
                Check = check.Id, Outcome = CheckOutcome.NotRun, Why = reasons.Count > 0 ? string.Join("; ", reasons) : SelfTestWords.NothingRan,
                At = results.Count > 0 ? results[^1].At : null, NotRun = notRun, Confirmed = confirmed, Notes = notes.ToList(), Did = did,
            };
        }

        return new CheckRecord {
            Check = check.Id, Outcome = CheckOutcome.Passed, At = results.Count > 0 ? results[^1].At : null, NotRun = notRun, Confirmed = confirmed, Notes = notes.ToList(), Did = did,
        };
    }

    private static string Lower(string text) => text.Length > 0 && char.IsUpper(text[0]) && (text.Length == 1 || !char.IsUpper(text[1])) ? char.ToLowerInvariant(text[0]) + text[1..] : text;

    // ================================================================ the gate

    /// <summary>
    /// Waits until the run may go on: in a private place (settled), connected with the run's own character on its own
    /// server, and, before a step that types in a house or apartment, nobody else near. Shows why it waits meanwhile. A
    /// step paused in its middle gets its whole time-out again. Throws to stop (the tester stopped, five minutes without
    /// a connection, or another character for too long).
    /// </summary>
    /// <param name="whileDisconnected">A step that types while disconnected on purpose: the connection isn't waited for.</param>
    internal async Task GateAsync(bool typing, CancellationToken token, bool whileDisconnected = false) {
        DateTimeOffset? disconnectedSince = null;
        DateTimeOffset? otherSince = null;
        var paused = false;
        while (true) {
            token.ThrowIfCancellationRequested();
            await this.KeepPartnerAsync(token).ConfigureAwait(false);
            var now = this._host.Now;
            var why = await this.WhyWaitAsync(typing, whileDisconnected || this.DisconnectedByRun).ConfigureAwait(false);
            if (why == SelfTestWords.PausedDisconnected) {
                disconnectedSince ??= now;
                if (now - disconnectedSince > MostDisconnected) {
                    throw new SelfTestStopException(SelfTestWords.Disconnected);
                }
            } else if (why == SelfTestWords.PausedOtherCharacter) {
                otherSince ??= now;
                if (now - otherSince > MostWaitToCarryOn) {
                    throw new SelfTestStopException(SelfTestWords.OtherCharacter);
                }
            }

            if (why == null) {
                if (paused) {
                    this.SetView(view => view with { State = SelfTestState.Running, Pause = null, Place = this._place.Last.Kind });
                    this.RestartStepTimeout();
                } else {
                    this.SetView(view => view with { Place = this._place.Last.Kind });
                }

                return;
            }

            paused = true;
            var reason = why;
            this.SetView(view => view with { State = SelfTestState.Paused, Pause = reason, Place = this._place.Last.Kind, Prompt = null });
            await this._host.Delay(Poll, token).ConfigureAwait(false);
        }
    }

    /// <summary>Why the run must wait now (the words to show), or null if it may go on. Reads the place once.</summary>
    private async Task<string?> WhyWaitAsync(bool typing, bool whileDisconnected) {
        PlaceFacts facts;
        try {
            facts = await this._host.ReadPlaceAsync().ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            facts = PlaceFacts.Unreadable;
        }

        if (!this._place.Observe(facts, this._host.Now)) {
            return this._place.Last.Private || this._place.Last.Kind is PlaceKind.Loading ? SelfTestWords.WaitingPlace : SelfTestWords.PausedLeft;
        }

        if (!whileDisconnected) {
            if (!await this.ReadyAsync().ConfigureAwait(false)) {
                return SelfTestWords.PausedDisconnected;
            }

            if (!await this.IsForAsync().ConfigureAwait(false)) {
                return SelfTestWords.PausedOtherCharacter;
            }
        }

        if (typing && this._place.Last.NeedsNobodyNear && await this.OthersNearAsync().ConfigureAwait(false) > 0) {
            return SelfTestWords.WaitingSomeoneNear;
        }

        return null;
    }

    private async Task<bool> ReadyAsync() {
        try {
            return await this._host.IsReadyAsync().ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            return false;
        }
    }

    private int _keepingPartner;

    /// <summary>
    /// Two players, the host: sends "alive" when due (five minutes since its last line to the partner), while the run waits
    /// anywhere (a gate, a guided step, a relog), so the partner never gives up on a host whose tester is busy. Only while
    /// connected with the run's own character. A refusal stops the run, as for any request.
    /// </summary>
    private async Task KeepPartnerAsync(CancellationToken token) {
        if (this.Link is not { KeepAliveDue: true } link || this.Pacer.Refused || Interlocked.Exchange(ref this._keepingPartner, 1) == 1) {
            return;
        }

        try {
            if (await this.ReadyAsync().ConfigureAwait(false) && await this.IsForAsync().ConfigureAwait(false)) {
                await link.KeepAliveAsync(this.Pacer, token).ConfigureAwait(false);
            }
        } catch (Exception ex) when (SelfTestPacer.IsRefusal(ex)) {
            this.Pacer.Refuse();
            throw new SelfTestRefusedException();
        } catch (Exception ex) when (ex is not (OperationCanceledException or SelfTestRefusedException)) {
            this._host.Log($"[selftest] the partner couldn't be told the run is still on ({ex.GetType().Name})");
        } finally {
            Interlocked.Exchange(ref this._keepingPartner, 0);
        }
    }

    /// <summary>The run's own character, on its own server, is logged in (never act on another).</summary>
    private async Task<bool> IsForAsync() {
        try {
            return await this._host.IsForAsync(this._data.ContentId, this._data.ServerUrl).ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            return false;
        }
    }

    private async Task<int> OthersNearAsync() {
        try {
            return await this._host.OthersNearAsync(PrivateArea.NearbyLimit).ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            // Can't tell: someone may be near.
            return 1;
        }
    }

    // The automatic step running now, and its time-out: started again after a pause in its middle.
    private CancellationTokenSource? _stepTimeout;
    private TimeSpan _stepTimeoutLength;

    private void RestartStepTimeout() {
        lock (this._lock) {
            try {
                if (this._stepTimeout is { IsCancellationRequested: false } timeout) {
                    timeout.CancelAfter(this._stepTimeoutLength);
                }
            } catch (ObjectDisposedException) {
                // The step has ended.
            }
        }
    }

    /// <summary>
    /// If the run disconnected on purpose and hasn't connected again, connects (waiting up to a minute) and, once connected,
    /// clears the mark. Never throws.
    /// </summary>
    private async Task ReconnectIfTheRunDisconnectedAsync() {
        if (!this.DisconnectedByRun) {
            return;
        }

        try {
            await this._host.ConnectAsync().ConfigureAwait(false);
            for (var waited = TimeSpan.Zero; waited < TimeSpan.FromMinutes(1) && !await this.ReadyAsync().ConfigureAwait(false); waited += Poll) {
                await this._host.Delay(Poll, CancellationToken.None).ConfigureAwait(false);
            }
        } catch (Exception ex) {
            this._host.Log($"[selftest] couldn't connect again ({ex.GetType().Name})");
        }

        if (await this.ReadyAsync().ConfigureAwait(false)) {
            this.MarkDisconnected(false);
        }
    }

    /// <summary>The run disconnected on purpose and hasn't connected again: its gates don't wait for a connection.</summary>
    internal bool DisconnectedByRun {
        get {
            lock (this._lock) {
                return this._data.DisconnectedByRun;
            }
        }
    }

    /// <summary>The run disconnects on purpose, or has connected again (kept in the run file, for the clean-up).</summary>
    internal void MarkDisconnected(bool disconnected) {
        lock (this._lock) {
            this._data.DisconnectedByRun = disconnected;
        }

        this.Save();
    }

    /// <summary>
    /// Types a line: the private-area check right before (and nobody near, in a house), the game's chat channel set to Say
    /// first (unless talking in a channel, when it must already be Say underneath), then through the host, which checks again.
    /// </summary>
    internal async Task TypeAsync(string line, TestLink link, CancellationToken token, bool whileDisconnected = false) {
        for (var attempt = 0; ; attempt++) {
            await this.GateAsync(typing: true, token, whileDisconnected).ConfigureAwait(false);
            await this.EnsureSayAsync().ConfigureAwait(false);
            try {
                await this._host.TypeAsync(line, link).ConfigureAwait(false);
                return;
            } catch (SelfTestNotTypedException notTyped) when (notTyped.NotPrivate && attempt < 3) {
                // The place (or who is near) changed between the two checks: nothing was typed. Wait for it again.
                this._place.Observe(PlaceFacts.Unreadable, this._host.Now);
            }
        }
    }

    /// <summary>
    /// Makes Say the game's chat channel, remembering the one before in the run file for the clean-up (also after a reload).
    /// While talking in a channel the channel underneath isn't changed (that would end it): the host refuses to type unless
    /// it is Say.
    /// </summary>
    private async Task EnsureSayAsync() {
        var sticky = await this._host.StickyAsync().ConfigureAwait(false);
        if (sticky.ChannelId != null) {
            return;
        }

        var channel = await this._host.GameChannelAsync().ConfigureAwait(false);
        if (channel == SelfTestGameChannels.Say) {
            return;
        }

        if (channel is { } before) {
            bool first;
            lock (this._lock) {
                first = this._data.GameChannelBefore == null;
                this._data.GameChannelBefore ??= before;
            }

            if (first) {
                this.Save();
            }
        }

        await this._host.SetGameChannelAsync(SelfTestGameChannels.Say).ConfigureAwait(false);
    }

    // ================================================================ guided steps

    private TaskCompletionSource<(GuidedAnswer Answer, string? Note, string? Input)> NewAnswer() {
        var answer = new TaskCompletionSource<(GuidedAnswer, string?, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (this._lock) {
            this._answer = answer;
        }

        return answer;
    }

    /// <summary>
    /// Shows a guided step and waits for the tester's answer, or for the runner to see the expected change. A step where
    /// the tester types in game chat (<see cref="GuidedPrompt.TesterTypes"/>) shows only through the typing gate, with Say
    /// as the game's chat channel (and ChatTwo's input on Say, if asked); while that stops being so, the step is taken off
    /// the screen (paused, with why), and shown again once it is.
    /// </summary>
    internal async Task<(GuidedAnswer Answer, string? Note, string? Input)> AskAsync(SelfTestCheck check, int index, GuidedPrompt prompt, CancellationToken token) {
        var answer = this.NewAnswer();
        if (prompt.TesterTypes && !await this.TypingReadyAsync(check, index, prompt, answer.Task, token).ConfigureAwait(false)) {
            return answer.Task.Result;
        }

        string? watching = null;
        var shown = new PromptView(prompt, index + 1, check.StepList.Count, null);
        this.SetView(view => view with { State = SelfTestState.NeedsYou, Prompt = shown, Pause = null });
        try {
            while (true) {
                token.ThrowIfCancellationRequested();
                await this.KeepPartnerAsync(token).ConfigureAwait(false);
                if (answer.Task.IsCompleted) {
                    return answer.Task.Result;
                }

                if (prompt.TesterTypes && (await this.WhyWaitAsync(typing: true, this.DisconnectedByRun).ConfigureAwait(false) != null
                                           || !await this.SayStillAsync(prompt).ConfigureAwait(false) || !await HoldsAsync(prompt).ConfigureAwait(false))) {
                    // Off the screen until it may be typed again: no line asked for while not private, or someone is near.
                    if (!await this.TypingReadyAsync(check, index, prompt, answer.Task, token).ConfigureAwait(false)) {
                        return answer.Task.Result;
                    }

                    var again = shown with { Watching = watching };
                    this.SetView(view => view with { State = SelfTestState.NeedsYou, Prompt = again, Pause = null });
                    continue;
                }

                if (prompt.Watch != null) {
                    WatchUpdate update;
                    try {
                        update = await prompt.Watch().ConfigureAwait(false);
                    } catch (Exception ex) when (ex is not OperationCanceledException) {
                        update = new WatchUpdate("", false);
                    }

                    if (update.Done) {
                        return (GuidedAnswer.Seen, null, null);
                    }

                    if (update.Seeing != watching) {
                        watching = update.Seeing;
                        var seeing = watching;
                        this.SetView(view => view with { Prompt = view.Prompt is { } current ? current with { Watching = seeing } : null });
                    }
                }

                await Task.WhenAny(answer.Task, this._host.Delay(Poll, token)).ConfigureAwait(false);
            }
        } finally {
            lock (this._lock) {
                if (this._answer == answer) {
                    this._answer = null;
                }
            }

            this.SetView(view => view with { State = view.State == SelfTestState.NeedsYou ? SelfTestState.Running : view.State, Prompt = null });
        }
    }

    /// <summary>
    /// Before the tester is asked to type: the typing gate, Say as the game's chat channel, and (if asked) ChatTwo's input on
    /// Say, the tester asked to put it there and the step waiting until it is.
    /// </summary>
    /// <returns>False if the tester answered meanwhile (Skip, the only button of the "put ChatTwo on Say" request).</returns>
    private async Task<bool> TypingReadyAsync(SelfTestCheck check, int index, GuidedPrompt prompt, Task answered, CancellationToken token) {
        while (true) {
            if (answered.IsCompleted) {
                return false;
            }

            await this.GateAsync(typing: true, token).ConfigureAwait(false);
            await this.EnsureSayAsync().ConfigureAwait(false);
            if (!await this.GameOnSayAsync().ConfigureAwait(false)) {
                // Talking in a channel over another chat channel (the switch would end it), or unreadable: not asked to type.
                this.SetView(view => view with { State = SelfTestState.Paused, Pause = SelfTestWords.WaitingSay, Prompt = null });
                await this._host.Delay(Poll, token).ConfigureAwait(false);
                continue;
            }

            if (!await HoldsAsync(prompt).ConfigureAwait(false)) {
                // Something the line needs isn't so (a party of just the two testers): not asked to type meanwhile.
                this.SetView(view => view with { State = SelfTestState.Paused, Pause = prompt.HoldsWhy ?? SelfTestWords.WaitingSay, Prompt = null });
                await this._host.Delay(Poll, token).ConfigureAwait(false);
                continue;
            }

            if (!prompt.ChatTwoOnSay || await this.ChatTwoOnSayAsync().ConfigureAwait(false)) {
                return true;
            }

            var ask = new PromptView(new GuidedPrompt(SelfTestWords.ChatTwoToSay, PromptKind.SkipOnly), index + 1, check.StepList.Count, "ChatTwo's input isn't on Say");
            this.SetView(view => view with { State = SelfTestState.NeedsYou, Prompt = ask, Pause = null });
            await this._host.Delay(Poll, token).ConfigureAwait(false);
        }
    }

    /// <summary>What a line to type needs besides the gate holds (see <see cref="GuidedPrompt.Holds"/>); a failure to tell is no.</summary>
    private static async Task<bool> HoldsAsync(GuidedPrompt prompt) {
        try {
            return prompt.Holds == null || await prompt.Holds().ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            return false;
        }
    }

    /// <summary>ChatTwo's input is on Say, or can't be read (no ChatTwo, or one too old to say).</summary>
    private async Task<bool> ChatTwoOnSayAsync() {
        try {
            return await this._host.ChatTwoInputAsync().ConfigureAwait(false) is null or SelfTestGameChannels.ChatTwoSay;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            return false;
        }
    }

    /// <summary>The game's chat channel is Say (underneath, while talking in a channel); null (unreadable) is not.</summary>
    private async Task<bool> GameOnSayAsync() {
        try {
            return await this._host.GameChannelAsync().ConfigureAwait(false) == SelfTestGameChannels.Say;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            return false;
        }
    }

    /// <summary>While a line to type is asked for: the game's chat channel still Say, and ChatTwo's input too where asked.</summary>
    private async Task<bool> SayStillAsync(GuidedPrompt prompt) =>
        await this.GameOnSayAsync().ConfigureAwait(false) && (!prompt.ChatTwoOnSay || await this.ChatTwoOnSayAsync().ConfigureAwait(false));
    // ================================================================ settings and channels

    /// <summary>Changes a setting for the run: snapshotted in the run file (saved) before its first change.</summary>
    internal async Task ChangeSettingAsync(SelfTestSetting setting, JsonElement value) {
        bool snapshot;
        lock (this._lock) {
            snapshot = !this._data.Settings.ContainsKey(setting);
        }

        if (snapshot) {
            var before = await this._host.ReadSettingAsync(setting).ConfigureAwait(false);
            lock (this._lock) {
                this._data.Settings.TryAdd(setting, before.Clone());
            }

            this.Save();
        }

        await this._host.WriteSettingAsync(setting, value).ConfigureAwait(false);
    }

    /// <summary>
    /// The run's test channel number <paramref name="index"/> (0, 1 or 2), made the first time it is asked for: paced, named
    /// <see cref="ChannelName"/>, its ID in the run file at once, and put on the first free /lgc number from 50 down.
    /// </summary>
    internal async Task<string> TestChannelAsync(int index, CancellationToken token) {
        if (index is < 0 or > 2) {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        lock (this._lock) {
            if (index < this._data.TestChannels.Count) {
                return this._data.TestChannels[index];
            }
        }

        string id = "";
        while (true) {
            lock (this._lock) {
                if (index < this._data.TestChannels.Count) {
                    return this._data.TestChannels[index];
                }
            }

            await this.Pacer.WaitAsync(SelfTestAction.CreateChannel, token).ConfigureAwait(false);
            int count;
            lock (this._lock) {
                count = this._data.TestChannels.Count;
            }

            try {
                id = await this._host.CreateChannelAsync(ChannelName(this.Code, count)).ConfigureAwait(false);
            } catch (Exception ex) when (SelfTestPacer.IsRefusal(ex)) {
                this.Pacer.Refuse();
                throw;
            }

            lock (this._lock) {
                this._data.TestChannels.Add(id);
            }

            this.Save();
            await this.PutOnFreeSlotAsync(id).ConfigureAwait(false);
            if (count >= index) {
                return id;
            }
        }
    }

    /// <summary>The first free /lgc number from 50 down, so the tester's own numbers never move.</summary>
    private async Task PutOnFreeSlotAsync(string channelId) {
        for (var wait = 0; wait < 20 && await this._host.SlotOfAsync(channelId).ConfigureAwait(false) == null; wait++) {
            await this._host.Delay(TimeSpan.FromMilliseconds(250), this._stop.Token).ConfigureAwait(false);
        }

        var used = await this._host.SlotsInUseAsync().ConfigureAwait(false);
        var mine = await this._host.SlotOfAsync(channelId).ConfigureAwait(false);
        for (var slot = SelfTestEnvironment.MaxChannels; slot >= 1; slot--) {
            if (slot == mine) {
                return;
            }

            if (!used.Contains(slot)) {
                await this._host.AssignSlotAsync(channelId, slot).ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>The next marker for a test line: "LG self-test K7Q2 4, please ignore".</summary>
    internal string NextMarker() => $"{this.MarkerPrefix} {Interlocked.Increment(ref this._marker)}, please ignore";

    // ================================================================ clean-up

    /// <summary>
    /// The clean-up, always, each step on its own (one failing doesn't stop the rest), in this order: stop talking in any
    /// channel; the tester's channel windows back as they were (the test channels' go with them); connect again if the run
    /// had disconnected on purpose; then, only with the run's own character connected, leave each test channel (as its only
    /// member, that deletes it; disbanded if anyone else is in it) and remove its lines from the tester's own chat history;
    /// put the settings back; delete the whole history if it was off and empty before the run; the game's chat channel back
    /// (never a tell: left on Say); delete the run file, unless something is left to do (then the next connection does it).
    /// </summary>
    private async Task CleanUpAsync() {
        this.SetView(view => view with { State = SelfTestState.CleaningUp, Prompt = null });
        var left = 0;
        var notLeft = 0;
        var windowsBack = false;
        var historyCleared = false;
        var historyFailed = false;
        var restored = 0;
        var failed = 0;
        var leftOnSay = false;

        await this.TryAsync("stop talking", () => this._host.StopTalkingAsync()).ConfigureAwait(false);
        await this.TryAsync("windows", async () => {
            JsonElement? windows;
            lock (this._lock) {
                windows = this._data.Settings.TryGetValue(SelfTestSetting.ChannelWindows, out var value) ? value : null;
            }

            if (windows is { } layouts) {
                await this._host.WriteSettingAsync(SelfTestSetting.ChannelWindows, layouts).ConfigureAwait(false);
                lock (this._lock) {
                    this._data.Settings.Remove(SelfTestSetting.ChannelWindows);
                }

                restored++;
                windowsBack = true;
                this.Save();
            }
        }).ConfigureAwait(false);

        // Stopped while the run had disconnected on purpose: connect again first (the player didn't press Disconnect).
        await this.TryAsync("connect", this.ReconnectIfTheRunDisconnectedAsync).ConfigureAwait(false);

        // Only with the run's own character, connected: otherwise its channels and its file stay, for the next connection.
        var reachable = await this.ReadyAsync().ConfigureAwait(false) && await this.IsForAsync().ConfigureAwait(false);
        List<string> channels;
        List<string> joined;
        bool historyWasOffAndEmpty;
        lock (this._lock) {
            channels = this._data.TestChannels.ToList();
            joined = this._data.Joined.Where(id => !this._data.TestChannels.Contains(id)).ToList();
            historyWasOffAndEmpty = this._data.HistoryWasOffAndEmpty;
        }

        var channelsLater = channels.Count + joined.Count > 0 && !reachable;
        if (reachable) {
            // The other tester's channels this run joined: only ever left, never disbanded.
            foreach (var channelId in joined) {
                var ok = await this.TryAsync("leave", async () => {
                    if ((await this._host.ChannelsAsync().ConfigureAwait(false)).Any(channel => channel.Id == channelId)) {
                        await this._host.LeaveChannelAsync(channelId).ConfigureAwait(false);
                    }
                }).ConfigureAwait(false);
                if (ok) {
                    left++;
                } else {
                    notLeft++;
                }
            }

            // The host of two players: the partner leaves first (asked "bye"), so the host leaves last, and disbands a test
            // channel only if the partner is still in it after a minute.
            // After a refusal nothing more is sent: no bye then (the partner, if still in, goes with the disbanded channel).
            if (this.Link != null && !this.Pacer.Refused) {
                await this.TryAsync("bye", this.Link.ByeAsync).ConfigureAwait(false);
            }

            foreach (var channelId in channels) {
                var ok = await this.TryAsync("leave", async () => {
                    var listed = (await this._host.ChannelsAsync().ConfigureAwait(false)).FirstOrDefault(channel => channel.Id == channelId);
                    if (listed is { Members: > 1 }) {
                        // Someone else is in it (or invited): leaving would leave them a channel. It goes for everyone.
                        await this._host.DisbandChannelAsync(channelId).ConfigureAwait(false);
                    } else if (listed != null) {
                        await this._host.LeaveChannelAsync(channelId).ConfigureAwait(false);
                    }
                }).ConfigureAwait(false);
                if (ok) {
                    left++;
                } else {
                    notLeft++;
                }
            }

            // Unblocked if the run blocked the other tester (never someone the tester had blocked before).
            bool blocked;
            long other;
            lock (this._lock) {
                blocked = this._data.BlockedOther;
                other = this._data.OtherUserId;
            }

            if (blocked && other != 0 && await this.TryAsync("unblock", () => this._host.SetBlockedAsync(other, false)).ConfigureAwait(false)) {
                lock (this._lock) {
                    this._data.BlockedOther = false;
                }
            }

            // The tester's own history (on before the run): only the test channels' lines go, before any setting changes.
            var tested = channels.Concat(joined).ToList();
            if (!historyWasOffAndEmpty && tested.Count > 0) {
                historyCleared = await this.TryAsync("history", () => this._host.ForgetHistoryAsync(tested)).ConfigureAwait(false);
                historyFailed = !historyCleared;
            }

            if (notLeft == 0 && !historyFailed) {
                lock (this._lock) {
                    this._data.TestChannels.Clear();
                    this._data.Joined.Clear();
                }
            }
        }

        await this.TryAsync("settings", async () => {
            List<KeyValuePair<SelfTestSetting, JsonElement>> settings;
            lock (this._lock) {
                settings = this._data.Settings.ToList();
            }

            foreach (var (setting, value) in settings) {
                try {
                    await this._host.WriteSettingAsync(setting, value).ConfigureAwait(false);
                    lock (this._lock) {
                        this._data.Settings.Remove(setting);
                    }

                    restored++;
                } catch (Exception ex) {
                    failed++;
                    this._host.Log($"[selftest] a setting couldn't be put back: {setting} ({ex.GetType().Name})");
                }
            }
        }).ConfigureAwait(false);

        // Off and empty before the run: everything in the history is the test's (real channels' lines recorded during the
        // run too). Deleted once the setting is back off, so nothing more is recorded after it.
        bool historyBack;
        lock (this._lock) {
            historyBack = !this._data.Settings.ContainsKey(SelfTestSetting.KeepHistory);
        }

        if (historyWasOffAndEmpty && !historyBack) {
            // The setting couldn't be put back off: nothing is deleted while it may be recording; the next start decides.
            historyFailed = true;
        } else if (historyWasOffAndEmpty) {
            historyFailed = !await this.TryAsync("history", () => this._host.DeleteHistoryAsync()).ConfigureAwait(false);
            if (!historyFailed) {
                lock (this._lock) {
                    this._data.HistoryWasOffAndEmpty = false;
                }
            }
        }

        await this.TryAsync("chat channel", async () => {
            int? before;
            lock (this._lock) {
                before = this._data.GameChannelBefore;
            }

            if (before is not { } channel) {
                return;
            }

            // A tell needs the person's name, which the run never keeps: left on Say, and said so.
            if (SelfTestGameChannels.IsTell(channel)) {
                leftOnSay = true;
            } else if ((await this._host.StickyAsync().ConfigureAwait(false)).ChannelId == null) {
                await this._host.SetGameChannelAsync(channel).ConfigureAwait(false);
            }

            lock (this._lock) {
                this._data.GameChannelBefore = null;
            }
        }).ConfigureAwait(false);

        await this.TryAsync("spent", () => {
            this._file.WriteSpent(this.Pacer.Spent);
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        // The file stays if anything is left to do: the next connection (or start) finishes it.
        bool stillBlocked;
        lock (this._lock) {
            // The other tester's name goes with the run: a file kept for what is left to do doesn't need it.
            this._data.Partner = null;
            stillBlocked = this._data.BlockedOther;
        }

        if (!channelsLater && notLeft == 0 && failed == 0 && !historyFailed && !stillBlocked) {
            await this.TryAsync("run file", () => {
                this._file.Delete();
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        } else {
            lock (this._lock) {
                this._data.Phase = SelfTestPhase.Running;
            }

            this.Save();
        }

        var summary = SelfTestWords.CleanUp(left, notLeft, restored, failed, windowsBack, historyCleared, channelsLater, leftOnSay);
        this._host.Log($"[selftest] {summary}");
        this.SetView(view => view with { State = SelfTestState.Done, CleanUp = summary, EndedAt = this._host.Now, Prompt = null, Pause = view.Pause });
    }

    /// <summary>Whether a run file is left (an earlier clean-up didn't finish).</summary>
    public bool FileLeft => this._file.Exists;
    /// <summary>One clean-up step: never throws; at most a minute.</summary>
    private async Task<bool> TryAsync(string what, Func<Task> action) {
        try {
            var work = action();
            var done = await Task.WhenAny(work, this._host.Delay(TimeSpan.FromMinutes(1), CancellationToken.None)).ConfigureAwait(false);
            if (done != work) {
                this._host.Log($"[selftest] clean-up: {what} took too long");
                return false;
            }

            await work.ConfigureAwait(false);
            return true;
        } catch (Exception ex) {
            this._host.Log($"[selftest] clean-up: {what} failed ({ex.GetType().Name})");
            return false;
        }
    }

    // ================================================================ the file and the view

    private void Save() {
        try {
            lock (this._lock) {
                this._data.LastActive = this._host.Now;
                this._file.Write(this._data);
            }
        } catch (Exception ex) {
            this._host.Log($"[selftest] the run file couldn't be saved ({ex.GetType().Name})");
        }
    }

    private void SetView(Func<SelfTestView, SelfTestView> change) {
        lock (this._lock) {
            this._view = change(this._view);
        }
    }

    private IReadOnlyList<CheckView> CheckViews(string? current) {
        lock (this._lock) {
            return this.Checks().Select(entry => new CheckView(entry.Check, entry.Suite.Title,
                this._data.Results.FirstOrDefault(result => result.Check == entry.Check.Id), entry.Check.Id == current)).ToList();
        }
    }
}

/// <summary>
/// What a step's code works with: the host, and the runner's safe ways to type, ask, send, change a setting and make a
/// test channel. Everything that reaches game chat or the server goes through here, never straight to the host.
/// </summary>
public sealed class StepContext {
    private readonly SelfTestRun _run;
    private readonly SelfTestCheck _check;
    private readonly int _index;
    private readonly List<string> _notes;

    internal StepContext(SelfTestRun run, SelfTestCheck check, int index, CancellationToken token, List<string> notes, IDictionary<string, object> state) {
        this._run = run;
        this._check = check;
        this._index = index;
        this.Token = token;
        this._notes = notes;
        this.State = state;
    }

    public ISelfTestHost Host => this._run.Host;

    /// <summary>Cancelled when the run is stopped or the step times out.</summary>
    public CancellationToken Token { get; internal set; }

    /// <summary>"LG self-test K7Q2": every test line starts with it.</summary>
    public string MarkerPrefix => this._run.MarkerPrefix;

    /// <summary>The run's code.</summary>
    public string Code => this._run.Code;

    /// <summary>The next test line's text: "LG self-test K7Q2 4, please ignore".</summary>
    public string NextMarker() => this._run.NextMarker();

    /// <summary>Types a line through the game's chat box gate (see <see cref="SelfTestRun"/>: checked first, every time).</summary>
    /// <param name="spends">What the line asks the server for, if anything (a /lgc line sends a channel message): paced first.</param>
    /// <param name="whileDisconnected">Typed while disconnected on purpose (the step disconnected): the connection isn't waited for.</param>
    public async Task TypeAsync(string line, TestLink link = TestLink.None, SelfTestAction? spends = null, bool whileDisconnected = false) {
        if (spends is { } action) {
            await this._run.Pacer.WaitAsync(action, this.Token).ConfigureAwait(false);
        }

        await this._run.TypeAsync(line, link, this.Token, whileDisconnected).ConfigureAwait(false);
    }

    /// <summary>What the check's steps share (marks, a channel's ID), in memory, for this check only.</summary>
    public IDictionary<string, object> State { get; }

    /// <summary>Asks the server for something, paced; a refusal for a limit stops the run (it is never retried).</summary>
    public async Task<T> ServerAsync<T>(SelfTestAction action, Func<Task<T>> request) {
        await this._run.Pacer.WaitAsync(action, this.Token).ConfigureAwait(false);
        try {
            return await request().ConfigureAwait(false);
        } catch (Exception ex) when (SelfTestPacer.IsRefusal(ex)) {
            this._run.Pacer.Refuse();
            throw new SelfTestRefusedException();
        }
    }

    public Task ServerAsync(SelfTestAction action, Func<Task> request) => this.ServerAsync(action, async () => {
        await request().ConfigureAwait(false);
        return true;
    });

    /// <summary>Shows a guided step and waits for the answer (see <see cref="GuidedPrompt"/>).</summary>
    public async Task<GuidedAnswer> AskAsync(GuidedPrompt prompt) => (await this.AskForInputAsync(prompt).ConfigureAwait(false)).Answer;

    /// <summary>Shows a guided step with a box to type in (<see cref="GuidedPrompt.Input"/>): the answer, and what was typed.</summary>
    public async Task<(GuidedAnswer Answer, string? Input)> AskForInputAsync(GuidedPrompt prompt) {
        var (answer, note, input) = await this._run.AskAsync(this._check, this._index, prompt, this.Token).ConfigureAwait(false);
        this.AddNote(note);
        return (answer, input);
    }

    /// <summary>Asks, and turns the answer into the step's result: Done, Yes or seen pass; No or It didn't work fail; Skip skips.</summary>
    public async Task<StepResult> AskForResultAsync(GuidedPrompt prompt) => Judge(await this.AskAsync(prompt).ConfigureAwait(false), prompt.Short);

    /// <summary>A guided answer as a step's result.</summary>
    public static StepResult Judge(GuidedAnswer answer, string? confirmed) => answer switch {
        GuidedAnswer.Done or GuidedAnswer.Yes or GuidedAnswer.Seen => new StepResult(StepOutcome.Passed, Confirmed: confirmed),
        GuidedAnswer.Skip => StepResult.Skip(SelfTestWords.YouSkipped),
        _ => StepResult.Fail(SelfTestWords.YouSaidNo),
    };

    internal void AddNote(string? note) {
        if (note != null) {
            this._notes.Add(note);
        }
    }

    /// <summary>Changes a setting for the run (put back at the end, see <see cref="SelfTestSetting"/>).</summary>
    public Task ChangeSettingAsync<T>(SelfTestSetting setting, T value) =>
        this._run.ChangeSettingAsync(setting, SelfTestSettingsSnapshot.Value(value));

    /// <summary>The run's test channel number <paramref name="index"/>, made the first time it is asked for.</summary>
    public Task<string> TestChannelAsync(int index = 0) => this._run.TestChannelAsync(index, this.Token);

    /// <summary>The run's test channels made so far.</summary>
    public IReadOnlyList<string> TestChannels => this._run.Data.TestChannels.ToList();

    /// <summary>The run's settings snapshot holds this setting already (it was changed by this run).</summary>
    public bool Snapshotted(SelfTestSetting setting) => this._run.Data.Settings.ContainsKey(setting);

    /// <summary>Marks the chat history as off and empty before the run (so the clean-up deletes it whole).</summary>
    public void HistoryWasOffAndEmpty() => this._run.Data.HistoryWasOffAndEmpty = true;

    /// <summary>Whether the chat history was off and empty before the run.</summary>
    public bool HistoryOffAndEmptyBefore => this._run.Data.HistoryWasOffAndEmpty;

    /// <summary>Waits a while, then for the gate (it pauses here if the tester left the private place meanwhile).</summary>
    public async Task DelayAsync(TimeSpan delay) {
        await this.Host.Delay(delay, this.Token).ConfigureAwait(false);
        await this._run.GateAsync(typing: false, this.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until <paramref name="condition"/> holds, checking four times a second, at most <paramref name="timeout"/>. It
    /// pauses (the time paused not counted) while the tester is out of the private place, or disconnected when the run
    /// didn't disconnect on purpose.
    /// </summary>
    /// <returns>Whether it held.</returns>
    public async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null) {
        var until = this.Host.Now + (timeout ?? TimeSpan.FromSeconds(5));
        while (true) {
            if (await condition().ConfigureAwait(false)) {
                return true;
            }

            if (this.Host.Now >= until) {
                return false;
            }

            var before = this.Host.Now;
            await this._run.GateAsync(typing: false, this.Token).ConfigureAwait(false);
            until += this.Host.Now - before;
            await this.Host.Delay(TimeSpan.FromMilliseconds(250), this.Token).ConfigureAwait(false);
        }
    }

    /// <summary>The run disconnects on purpose (its gates stop waiting for a connection), or has connected again.</summary>
    internal void MarkDisconnected(bool disconnected) => this._run.MarkDisconnected(disconnected);

    /// <summary>Waits for the gate again: private, connected (and nobody near, before typing).</summary>
    public Task GateAsync(bool typing = false) => this._run.GateAsync(typing, this.Token);

    /// <summary>The environment as read at the start of the run (the plugin's version, ChatTwo, the mode).</summary>
    public Task<SelfTestEnvironment> EnvironmentAsync() => this.Host.ReadEnvironmentAsync();

    // ---------------------------------------------------------------- two players

    /// <summary>Alone, the host, or the partner.</summary>
    public SelfTestRole Role => this._run.Data.Role;

    /// <summary>The host's link to the partner (a check that needs one has <see cref="SelfTestNeeds.Partner"/>).</summary>
    public PartnerLink Partner => this._run.Link ?? throw new PartnerSilentException(gone: true);

    /// <summary>The run's file contents (for the partner's runner and the pairing check).</summary>
    internal SelfTestRunData Data => this._run.Data;

    /// <summary>Saves the run file after a change to <see cref="Data"/>.</summary>
    internal void Save() => this._run.SaveNow();

    /// <summary>Joined the other tester's channel: left (never disbanded) at the clean-up.</summary>
    internal void MarkJoined(string channelId) => this._run.MarkJoined(channelId);

    /// <summary>The pair has ended: the other tester's name goes from the run file now.</summary>
    internal void ForgetOtherName() => this._run.ForgetOtherName();

    /// <summary>Shows a line about the other tester in the window (fixed words, never a name).</summary>
    public void ShowPairLine(string line) => this._run.ShowPairLine(line);

    /// <summary>
    /// Blocks or unblocks the partner (only them, by their user ID): the run file says so before a block, so the clean-up
    /// (or the next start) unblocks them. Never when the tester had blocked them before the test.
    /// </summary>
    public async Task BlockPartnerAsync(bool blocked) {
        if (this._run.Data.OtherWasBlocked || this._run.Data.OtherUserId == 0) {
            throw new InvalidOperationException("Only a partner the tester hadn't blocked.");
        }

        if (blocked) {
            this._run.MarkBlocked(true);
            await this.Host.SetBlockedAsync(this._run.Data.OtherUserId, true).ConfigureAwait(false);
        } else {
            await this.Host.SetBlockedAsync(this._run.Data.OtherUserId, false).ConfigureAwait(false);
            this._run.MarkBlocked(false);
        }
    }

    /// <summary>The pacer (the partner's runner checks its most before it answers).</summary>
    internal SelfTestPacer Pacer => this._run.Pacer;

    /// <summary>Adds a channel this run made by itself (the partner's one channel of its own), to the file at once.</summary>
    internal void AddTestChannel(string channelId) => this._run.AddTestChannel(channelId);
}
