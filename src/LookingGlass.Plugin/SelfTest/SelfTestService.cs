using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using LookingGlass.Core.Client;
using LookingGlass.Core.SelfTest;
using LookingGlass.Core.SelfTest.Suites;

namespace LookingGlass.Plugin.SelfTest;

/// <summary>What the plugin found at start in a run file an earlier run left (see <see cref="SelfTestService.AtStart"/>).</summary>
internal sealed record SelfTestStart(SelfTestRunData? Resume, SelfTestRunData? Recover, bool Damaged);

/// <summary>
/// The self-test in the plugin (docs/self-test-plan.md): starts runs, carries one on after a reload, puts back what an
/// interrupted run left, and finds channels left over from a test no run file knows about. Its window is
/// <see cref="Ui.SelfTestWindow"/>. Nothing here runs, taps or changes anything until the tester presses Start, but for
/// putting back what an interrupted run changed.
/// </summary>
internal sealed partial class SelfTestService : IDisposable {
    private readonly SelfTestParts _parts;
    private readonly SelfTestHost _host;
    private readonly SelfTestRunFile _file;
    private readonly Lock _lock = new();
    private SelfTestRun? _run;
    private SelfTestReportSetup? _setup;
    private List<string> _forbidden = [];
    private SelfTestRunData? _resume;
    private SelfTestRunData? _recover;
    private bool _recovering;
    private bool _tellInterrupted;
    private Task _lastRun = Task.CompletedTask;

    public SelfTestService(SelfTestParts parts, SelfTestStart start) {
        this._parts = parts;
        this._host = new SelfTestHost(parts);
        this._file = new SelfTestRunFile(Services.PluginInterface.ConfigDirectory.FullName);
        this._resume = start.Resume;
        this._recover = start.Recover;
        this._tellInterrupted = start.Recover != null || start.Damaged;
        Services.Framework.Update += this.OnUpdate;
    }

    /// <summary>Asks the window to show (a run carried on by itself, after a reload).</summary>
    public event Action? WantsWindow;

    /// <summary>
    /// At the plugin's start, before any session: reads the run file an earlier run left, if any. A run waiting for a
    /// reload (not for too long) is carried on once logged in; anything else was interrupted, and its settings are put back
    /// at once (and saved), its test channels left once connected (see <see cref="OnUpdate"/>).
    /// </summary>
    public static SelfTestStart AtStart(Configuration config, string configFolder) {
        var file = new SelfTestRunFile(configFolder);
        SelfTestRunData? data;
        bool damaged;
        try {
            data = file.Read(out damaged);
        } catch (Exception ex) {
            Services.Log.Warning($"[selftest] the run file couldn't be read ({ex.GetType().Name})");
            return new SelfTestStart(null, null, false);
        }

        if (data == null) {
            return new SelfTestStart(null, null, false);
        }

        if (!damaged && data.Phase == SelfTestPhase.WaitingToCarryOn && data.WaitingSince is { } since && DateTimeOffset.Now - since < SelfTestRun.MostWaitToCarryOn) {
            Services.Log.Information("[selftest] a run waits to carry on");
            return new SelfTestStart(data, null, false);
        }

        var (restored, failed) = SelfTestSettingsSnapshot.Restore(data, new PluginSettings(config, data.ContentId, data.ServerUrl));
        Services.Log.Information($"[selftest] an interrupted run's settings were put back ({restored}, {failed} couldn't be)");
        data.Phase = SelfTestPhase.Running;
        data.WaitingSince = null;
        data.NextCheck = null;
        try {
            if (data.TestChannels.Count == 0 && data.Settings.Count == 0) {
                file.Delete();
            } else {
                // Kept (or written again, after a damaged one was set aside) until its channels are left.
                file.Write(data);
            }
        } catch (Exception ex) {
            Services.Log.Warning($"[selftest] the run file couldn't be updated ({ex.GetType().Name})");
        }

        return new SelfTestStart(null, data, damaged);
    }

    // ================================================================ what the window reads

    /// <summary>The run going, or the last one (for its report), or null.</summary>
    public SelfTestRun? Run {
        get {
            lock (this._lock) {
                return this._run;
            }
        }
    }

    /// <summary>A run is going (not done).</summary>
    public bool Running => this.Run is { View.State: not SelfTestState.Done };

    /// <summary>Every suite, in the start screen's order.</summary>
    public IReadOnlyList<SelfTestSuite> Suites { get; } = SelfTestSuites.All();

    /// <summary>Where the player is now, judged as the run would (not settled). Framework thread.</summary>
    public static PlaceVerdict PlaceNow(out string? name) {
        var facts = PrivateAreaReader.Read();
        name = facts.Name;
        return PrivateArea.Judge(facts);
    }

    /// <summary>The environment now (framework thread).</summary>
    public Task<SelfTestEnvironment> EnvironmentAsync() => this._host.ReadEnvironmentAsync();

    /// <summary>Why a run can't start now, or null. Framework thread.</summary>
    public string? CantStart(IReadOnlyCollection<SelfTestSuite> suites) {
        if (this.Running) {
            return "A run is going.";
        }

        if (this._resume != null) {
            return "A run is waiting to carry on.";
        }

        if (this._recover != null || this._recovering || this.FileLeft()) {
            return "The last test's clean-up isn't finished: it finishes once you're connected.";
        }

        if (this._parts.Sessions.SessionPlayer == null || this._parts.Sessions.Snapshot is not { State: ConnectionState.Ready, ChannelsLoaded: true }) {
            return "Connect to LookingGlass first.";
        }

        if (suites.Count == 0) {
            return "Tick at least one.";
        }

        var wait = new SelfTestPacer(() => DateTimeOffset.Now, Task.Delay, this.Spent()).WaitBeforeStart(Planned(suites));
        return wait > TimeSpan.Zero ? SelfTestWords.WaitBeforeStart(wait) : null;
    }

    // The window asks every frame: the files are read again at most every two seconds.
    private (DateTimeOffset At, bool FileLeft, IReadOnlyList<SelfTestSpend> Spent)? _files;

    private (bool FileLeft, IReadOnlyList<SelfTestSpend> Spent) Files() {
        if (this._files is not { } files || DateTimeOffset.Now - files.At > TimeSpan.FromSeconds(2)) {
            files = (DateTimeOffset.Now, this._file.Exists, this._file.ReadSpent());
            this._files = files;
        }

        return (files.FileLeft, files.Spent);
    }

    private bool FileLeft() => this.Files().FileLeft;

    private IReadOnlyList<SelfTestSpend> Spent() => this.Files().Spent;

    /// <summary>What the suites may ask the server for at most, together.</summary>
    public static IReadOnlyDictionary<SelfTestAction, int> Planned(IEnumerable<SelfTestSuite> suites) {
        var planned = new Dictionary<SelfTestAction, int>();
        foreach (var (action, count) in suites.SelectMany(suite => suite.Planned)) {
            planned[action] = Math.Min(planned.GetValueOrDefault(action) + count, SelfTestPacer.Rules[action].RunMost);
        }

        return planned;
    }

    // ================================================================ starting, carrying on, stopping

    /// <summary>Starts a run of the suites (only <paramref name="onlyChecks"/> of them, to run the failed ones again). Framework thread.</summary>
    public void Start(IReadOnlyList<SelfTestSuite> suites, IReadOnlyCollection<string>? onlyChecks = null) {
        if (this.CantStart(suites) is { } why) {
            throw new InvalidOperationException(why);
        }

        var player = this._parts.Sessions.SessionPlayer!;
        var data = SelfTestRun.NewData(SelfTestRun.NewCode(), player.ContentId, this._parts.Sessions.ServerUrl, DateTimeOffset.Now, suites, onlyChecks);
        this.Begin(data, suites, resume: false);
    }

    private void Begin(SelfTestRunData data, IReadOnlyList<SelfTestSuite> suites, bool resume) {
        this._files = null;
        var pacer = new SelfTestPacer(() => DateTimeOffset.Now, Task.Delay, this._file.ReadSpent());
        var run = new SelfTestRun(this._host, suites, pacer, this._file, data, resume);
        var snapshot = this._parts.Sessions.Snapshot;
        var player = this._parts.Sessions.SessionPlayer;
        // What must never be in the log, nor in the report: the tester's name and world, every channel's name, the run's marker.
        var forbidden = new List<string> { run.MarkerPrefix, data.Code };
        if (snapshot.Me is { } me) {
            forbidden.AddRange([me.Name, me.WorldName]);
        }

        if (player != null) {
            forbidden.AddRange([player.Name, player.HomeWorldName]);
        }

        // The log tap looks for the marker and the names; the report is scrubbed of every channel's name too (a channel's
        // name can be any word, which the plugin's own log lines may hold by chance).
        var logForbidden = forbidden.ToList();
        forbidden.AddRange(snapshot.Channels.Select(channel => channel.Name).OfType<string>());
        var environment = this._host.ReadEnvironmentAsync().GetAwaiter().GetResult();
        lock (this._lock) {
            this._run = run;
            this._forbidden = forbidden;
            this._setup = new SelfTestReportSetup(environment.PluginVersion, environment.ChatTwoLoaded, environment.AdvancedMode, PlaceKind.Unknown);
        }

        this._host.StartTaps(run.MarkerPrefix, logForbidden);
        this._host.TestChannels = run.TestChannelsNow;
        this._parts.Sender.Failed = ex => this.OnSendFailed(run, ex);
        this._parts.Local.Failed = ex => this.OnSendFailed(run, ex);
        Services.Log.Information(resume ? "[selftest] a run carries on" : "[selftest] a run starts");
        this._lastRun = Task.Run(run.RunAsync).ContinueWith(_ => Services.Framework.RunOnFrameworkThread(() => this.Finished(run)), TaskScheduler.Default);
    }

    private void OnSendFailed(SelfTestRun run, Exception ex) {
        if (SelfTestPacer.IsRefusal(ex)) {
            run.ReportRefusal();
        }
    }

    /// <summary>A run ended (cleaned up, or abandoned): the taps stop and drop what they held.</summary>
    private void Finished(SelfTestRun run) {
        if (this.Run != run) {
            return;
        }

        this._files = null;
        this._host.StopTaps();
        this._parts.Sender.Failed = null;
        this._parts.Local.Failed = null;
        lock (this._lock) {
            this._setup = this._setup is { } setup ? setup with { Place = run.View.Place } : null;
        }

        Services.Log.Information("[selftest] the run ended");
    }

    /// <summary>Stop and clean up.</summary>
    public void Stop() => this.Run?.Stop();

    /// <summary>The run's report, as copied: no names, no message text.</summary>
    public string? Report() {
        SelfTestRun? run;
        SelfTestReportSetup? setup;
        List<string> forbidden;
        lock (this._lock) {
            run = this._run;
            setup = this._setup;
            forbidden = this._forbidden.ToList();
        }

        if (run == null || setup == null) {
            return null;
        }

        var view = run.View;
        var checks = view.Checks.Where(check => check.Result != null).Select(check => (check.Check, check.Result!));
        return SelfTestReport.Build(setup with { Place = view.Place }, view.StartedAt, view.EndedAt, this._host.TimeZone, checks, view.CleanUp,
            forbidden.Concat(this._host.ForbiddenExtra));
    }

    // ================================================================ leftovers

    [GeneratedRegex(@"^LG self-test [A-Z0-9]{4}( [bc])?$")]
    private static partial Regex TestChannelName();

    /// <summary>
    /// Channels named like a self-test's that no run file knows about (the file was lost): listed, never removed by
    /// themselves, since a player could name a real channel that.
    /// </summary>
    public IReadOnlyList<ChannelView> Leftovers() {
        var known = new HashSet<string>(this.Run?.TestChannelsNow() ?? []);
        if (this._recover is { } recover) {
            known.UnionWith(recover.TestChannels);
        }

        return this._parts.Sessions.Snapshot.Channels.Where(channel => channel.Name is { } name && TestChannelName().IsMatch(name) && !known.Contains(channel.Id)).ToList();
    }

    /// <summary>Leaves a leftover channel the tester chose to remove.</summary>
    public void RemoveLeftover(string channelId) {
        if (this._parts.Sessions.Session is not { } session || this.Leftovers().All(channel => channel.Id != channelId)) {
            return;
        }

        _ = Task.Run(async () => {
            try {
                await session.LeaveAsync(channelId);
                Services.Log.Information("[selftest] a leftover test channel was left");
            } catch (Exception ex) {
                Services.Log.Warning($"[selftest] a leftover test channel couldn't be left ({ex.GetType().Name})");
            }
        });
    }

    // ================================================================ every frame

    /// <summary>Carries on a run after a reload, and finishes what an interrupted run left, once the right session is ready.</summary>
    private void OnUpdate(IFramework framework) {
        try {
            var sessions = this._parts.Sessions;
            var ready = sessions.Snapshot is { State: ConnectionState.Ready, ChannelsLoaded: true } && sessions.SessionPlayer is { } player;
            if (this._resume is { } resume) {
                if (resume.WaitingSince is { } since && DateTimeOffset.Now - since >= SelfTestRun.MostWaitToCarryOn) {
                    // It waited too long: put everything back instead.
                    this._resume = null;
                    resume.Phase = SelfTestPhase.Running;
                    this._recover = resume;
                    this._tellInterrupted = true;
                } else if (ready && this.ForThis(resume) && !this.Running) {
                    this._resume = null;
                    var suites = this.Suites.Where(suite => resume.Suites.Contains(suite.Id)).ToList();
                    this.Begin(resume, suites, resume: true);
                    this.WantsWindow?.Invoke();
                }
            }

            if (this._recover is { } recover && !this._recovering && ready && this.ForThis(recover)) {
                this._recovering = true;
                _ = this.RecoverAsync(recover);
            }
        } catch (Exception ex) {
            Services.Log.Error(ex, "[selftest] couldn't carry on or finish a run");
        }
    }

    private bool ForThis(SelfTestRunData data) =>
        this._parts.Sessions.SessionPlayer?.ContentId == data.ContentId && this._parts.Sessions.ServerUrl == data.ServerUrl;

    /// <summary>
    /// Finishes an interrupted run's clean-up: its settings back (again, now through the session), its test channels left
    /// (as their only member, that deletes them), their lines out of the chat history, the file deleted, and one blue line.
    /// </summary>
    private async Task RecoverAsync(SelfTestRunData data) {
        var left = true;
        var channels = data.TestChannels.ToList();
        try {
            await Services.Framework.RunOnFrameworkThread(() => {
                if (this._parts.Sessions.SessionPlayer is { } player) {
                    SelfTestSettingsSnapshot.Restore(data, new PluginSettings(this._parts.Config, player.ContentId, this._parts.Sessions.ServerUrl, this._parts.Sessions));
                }
            });

            var session = this._parts.Sessions.Session ?? throw new InvalidOperationException("No session.");
            foreach (var channelId in data.TestChannels.ToList()) {
                try {
                    if (session.Snapshot.FindChannel(channelId) != null) {
                        await session.LeaveAsync(channelId);
                    }

                    data.TestChannels.Remove(channelId);
                } catch (Exception ex) {
                    left = false;
                    Services.Log.Warning($"[selftest] an interrupted run's test channel couldn't be left ({ex.GetType().Name})");
                }
            }

            if (this._parts.Sessions.ChatLog is { } log) {
                foreach (var channelId in channels) {
                    await log.ForgetChannelAsync(channelId);
                }
            }

            if (left && data.Settings.Count == 0) {
                this._file.Delete();
            } else {
                this._file.Write(data);
            }
        } catch (Exception ex) {
            left = false;
            Services.Log.Warning($"[selftest] an interrupted run couldn't be finished ({ex.GetType().Name})");
        }

        await Services.Framework.RunOnFrameworkThread(() => {
            if (this._tellInterrupted) {
                this._parts.Chat.Notice(NoticeTone.Info, left ? SelfTestWords.InterruptedRun : SelfTestWords.InterruptedRunChannelLeft);
                this._tellInterrupted = false;
            }

            // Not tried again every frame: the window's Clean up again, or the next start, does.
            this._recover = null;
            this._recovering = false;
        });
    }

    /// <summary>A run file is left (an earlier clean-up couldn't finish).</summary>
    public bool CleanUpLeft => !this.Running && this._resume == null && (this._recover != null || this._recovering || this.FileLeft());

    /// <summary>Tries again to finish what an earlier run's file says (the window's Clean up again). Framework thread.</summary>
    public void RetryCleanUp() {
        if (this.Running || this._recovering || this._recover != null) {
            return;
        }

        try {
            if (this._file.Read(out _) is { } data) {
                data.Phase = SelfTestPhase.Running;
                this._recover = data;
                this._tellInterrupted = true;
            }
        } catch (Exception ex) {
            Services.Log.Warning($"[selftest] the run file couldn't be read ({ex.GetType().Name})");
        }
    }

    public void Dispose() {
        Services.Framework.Update -= this.OnUpdate;
        // Going away: the run stops without cleaning up, and its file stays for the next start.
        this.Run?.Abandon();
        this._parts.Sender.Failed = null;
        this._parts.Local.Failed = null;
        this._host.Dispose();
    }
}
