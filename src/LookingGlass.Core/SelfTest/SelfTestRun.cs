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
public sealed class SelfTestRun {
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
    private int? _gameChannelBefore;
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
        this._view = new SelfTestView {
            State = SelfTestState.Starting,
            Code = data.Code,
            StartedAt = data.StartedAt,
            Checks = this.CheckViews(null),
        };
    }

    /// <summary>A new run's file contents: its code, the character and server, the suites chosen.</summary>
    /// <param name="onlyChecks">Only these checks of the suites (the failed ones, run again), or all.</param>
    public static SelfTestRunData NewData(string code, ulong contentId, string serverUrl, DateTimeOffset now, IEnumerable<SelfTestSuite> suites,
        IEnumerable<string>? onlyChecks = null) => new() {
        Code = code,
        ContentId = contentId,
        ServerUrl = serverUrl,
        StartedAt = now,
        Phase = SelfTestPhase.Running,
        Suites = suites.Select(suite => suite.Id).ToList(),
        OnlyChecks = onlyChecks?.ToList() ?? [],
    };

    /// <summary>A new run's code: four capital letters and digits, without the ones easily mistaken (0, O, 1, I).</summary>
    public static string NewCode() {
        const string letters = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
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

    /// <summary>The run's test channels so far (a copy, safe from any thread).</summary>
    public IReadOnlyList<string> TestChannelsNow() {
        lock (this._lock) {
            return this._data.TestChannels.ToList();
        }
    }

    /// <summary>The marker every typed and sent line starts with: "LG self-test K7Q2".</summary>
    public string MarkerPrefix => $"LG self-test {this.Code}";

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
            // Carried on after a reload: the steps before were done before it, and the one it waited for is.
            results.AddRange(check.StepList.Take(first).Select(step => (step, StepResult.Pass(), this._host.Now)));
        }

        try {
            var environment = await this._host.ReadEnvironmentAsync().ConfigureAwait(false);
            var lacks = environment.Lacks(check.Needs) ?? check.CantRun?.Invoke(environment);
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
                    result = await this.CarryOnAsync(check, i, step, context, token).ConfigureAwait(false);
                } else {
                    result = await this.RunStepAsync(step, context, token).ConfigureAwait(false);
                }

                results.Add((step, result, this._host.Now));
                if (result.Outcome == StepOutcome.Failed) {
                    // The rest of a check builds on what failed: it stops there.
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
        } catch (SelfTestNotTypedException notTyped) {
            return StepResult.Fail(notTyped.NotPrivate ? SelfTestWords.NotPrivate : SelfTestWords.NotSay);
        } catch (Exception ex) {
            // Never the message: it may hold a name, or what the server said.
            this._host.Log($"[selftest] a step failed: {ex.GetType().Name}");
            return StepResult.Fail(SelfTestWords.SomethingWentWrong);
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
        var logins = this._host.Logins;
        var since = this._host.Now;
        var prompt = new GuidedPrompt(step.Title) { Short = null };
        var answer = this.NewAnswer();
        this.SetView(view => view with { State = SelfTestState.WaitingToCarryOn, Prompt = new PromptView(prompt, index + 1, check.StepList.Count, SelfTestWords.WaitingToCarryOn) });
        var result = new StepResult(StepOutcome.Passed);
        try {
            while (true) {
                token.ThrowIfCancellationRequested();
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
        if (failed.Result != null) {
            return new CheckRecord {
                Check = check.Id, Outcome = CheckOutcome.Failed, Why = failed.Result.Why, Step = failed.Step.Checklist, At = failed.At,
                NotRun = notRun, Confirmed = confirmed, Notes = notes.ToList(),
            };
        }

        if (results.Count > 0 && results.All(entry => entry.Result.Outcome == StepOutcome.Skipped)) {
            return new CheckRecord { Check = check.Id, Outcome = CheckOutcome.Skipped, Why = results[0].Result.Why, At = results[^1].At, Notes = notes.ToList() };
        }

        return new CheckRecord {
            Check = check.Id, Outcome = CheckOutcome.Passed, At = results.Count > 0 ? results[^1].At : null, NotRun = notRun, Confirmed = confirmed, Notes = notes.ToList(),
        };
    }

    private static string Lower(string text) => text.Length > 0 && char.IsUpper(text[0]) && (text.Length == 1 || !char.IsUpper(text[1])) ? char.ToLowerInvariant(text[0]) + text[1..] : text;

    // ================================================================ the gate

    /// <summary>
    /// Waits until the run may go on: in a private place (settled), connected, and, before a step that types in a house
    /// or apartment, nobody else near. Shows why it waits meanwhile. Throws to stop (the tester stopped, or five minutes
    /// without a connection).
    /// </summary>
    /// <param name="whileDisconnected">A step that types while disconnected on purpose: the connection isn't waited for.</param>
    internal async Task GateAsync(bool typing, CancellationToken token, bool whileDisconnected = false) {
        DateTimeOffset? disconnectedSince = null;
        var paused = false;
        while (true) {
            token.ThrowIfCancellationRequested();
            var now = this._host.Now;
            PlaceFacts facts;
            try {
                facts = await this._host.ReadPlaceAsync().ConfigureAwait(false);
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                facts = PlaceFacts.Unreadable;
            }

            var isPrivate = this._place.Observe(facts, now);
            string? why = null;
            if (!isPrivate) {
                why = this._place.Last.Private || this._place.Last.Kind is PlaceKind.Loading ? SelfTestWords.WaitingPlace : SelfTestWords.PausedLeft;
            } else if (!whileDisconnected && !await this.ReadyAsync().ConfigureAwait(false)) {
                disconnectedSince ??= now;
                if (now - disconnectedSince > MostDisconnected) {
                    throw new SelfTestStopException(SelfTestWords.Disconnected);
                }

                why = SelfTestWords.PausedDisconnected;
            } else if (typing && this._place.Last.NeedsNobodyNear && await this.OthersNearAsync().ConfigureAwait(false) > 0) {
                why = SelfTestWords.WaitingSomeoneNear;
            }

            if (why == null) {
                if (paused) {
                    this.SetView(view => view with { State = SelfTestState.Running, Pause = null, Place = this._place.Last.Kind });
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

    private async Task<bool> ReadyAsync() {
        try {
            return await this._host.IsReadyAsync().ConfigureAwait(false);
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
                // The place changed between the two checks: nothing was typed. Wait for it again.
                this._place.Observe(PlaceFacts.Unreadable, this._host.Now);
            }
        }
    }

    /// <summary>
    /// Makes Say the game's chat channel, remembering the one before for the clean-up. While talking in a channel the
    /// channel underneath isn't changed (that would end it): the host refuses to type unless it is Say.
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
            lock (this._lock) {
                this._gameChannelBefore ??= before;
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

    /// <summary>Shows a guided step and waits for the tester's answer, or for the runner to see the expected change.</summary>
    internal async Task<(GuidedAnswer Answer, string? Note, string? Input)> AskAsync(SelfTestCheck check, int index, GuidedPrompt prompt, CancellationToken token) {
        var answer = this.NewAnswer();
        string? watching = null;
        this.SetView(view => view with { State = SelfTestState.NeedsYou, Prompt = new PromptView(prompt, index + 1, check.StepList.Count, null) });
        try {
            while (true) {
                token.ThrowIfCancellationRequested();
                if (answer.Task.IsCompleted) {
                    return answer.Task.Result;
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
                        this.SetView(view => view with { Prompt = view.Prompt is { } shown ? shown with { Watching = seeing } : null });
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
    /// channel; the tester's channel windows back as they were (the test channels' go with them); leave each test channel
    /// (as its only member, that deletes it); remove the test channels' lines from the chat history (before the history
    /// setting is put back, so the log is still open); put the settings back, and the game's chat channel; delete the run file.
    /// </summary>
    private async Task CleanUpAsync() {
        this.SetView(view => view with { State = SelfTestState.CleaningUp, Prompt = null });
        var left = 0;
        var notLeft = 0;
        var windowsBack = false;
        var historyCleared = false;
        var restored = 0;
        var failed = 0;

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

        List<string> channels;
        lock (this._lock) {
            channels = this._data.TestChannels.ToList();
        }

        foreach (var channelId in channels) {
            var ok = await this.TryAsync("leave", async () => {
                var listed = await this._host.ChannelsAsync().ConfigureAwait(false);
                if (listed.Any(channel => channel.Id == channelId)) {
                    await this._host.LeaveChannelAsync(channelId).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
            if (ok) {
                left++;
            } else {
                notLeft++;
            }
        }

        bool historyWasOffAndEmpty;
        lock (this._lock) {
            historyWasOffAndEmpty = this._data.HistoryWasOffAndEmpty;
        }

        if (channels.Count > 0) {
            historyCleared = await this.TryAsync("history", () => historyWasOffAndEmpty ? this._host.DeleteHistoryAsync() : this._host.ForgetHistoryAsync(channels))
                .ConfigureAwait(false);
        }

        if (notLeft == 0) {
            lock (this._lock) {
                this._data.TestChannels.Clear();
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

        await this.TryAsync("chat channel", async () => {
            int? before;
            lock (this._lock) {
                before = this._gameChannelBefore;
            }

            if (before is { } channel && (await this._host.StickyAsync().ConfigureAwait(false)).ChannelId == null) {
                await this._host.SetGameChannelAsync(channel).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);

        await this.TryAsync("spent", () => {
            this._file.WriteSpent(this.Pacer.Spent);
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        // The file stays if a channel couldn't be left: the next start tries again.
        if (notLeft == 0 && failed == 0) {
            await this.TryAsync("run file", () => {
                this._file.Delete();
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        } else {
            this.Save();
        }

        var summary = SelfTestWords.CleanUp(left, notLeft, restored, failed, windowsBack, historyCleared && !historyWasOffAndEmpty);
        this._host.Log($"[selftest] {summary}");
        this.SetView(view => view with { State = SelfTestState.Done, CleanUp = summary, EndedAt = this._host.Now, Prompt = null, Pause = view.Pause });
    }

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

    /// <summary>Waits a while (paced against the fake clock in tests).</summary>
    public Task DelayAsync(TimeSpan delay) => this.Host.Delay(delay, this.Token);

    /// <summary>Waits until <paramref name="condition"/> holds, checking twice a second, at most <paramref name="timeout"/>.</summary>
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

            await this.Host.Delay(TimeSpan.FromMilliseconds(250), this.Token).ConfigureAwait(false);
        }
    }

    /// <summary>Waits for the gate again: private, connected (and nobody near, before typing).</summary>
    public Task GateAsync(bool typing = false) => this._run.GateAsync(typing, this.Token);

    /// <summary>The environment as read at the start of the run (the plugin's version, ChatTwo, the mode).</summary>
    public Task<SelfTestEnvironment> EnvironmentAsync() => this.Host.ReadEnvironmentAsync();
}
