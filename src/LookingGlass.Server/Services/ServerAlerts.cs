using System.Globalization;
using System.Security.Cryptography;
using System.Threading.Channels;
using LookingGlass.Server.Data;
using LookingGlass.Server.Realtime;

namespace LookingGlass.Server.Services;

/// <summary>
/// What the server tells its operator's Discord channel (see "Alerts to a Discord channel" in docs/design.md, and the mockups
/// in docs/operator-tools-plan.md): someone flagged, an address blocked automatically, a ban made or lifted with any tool, an
/// operator character's keys pinned or restored, an operator character signing in with other keys, the proxy warning, a
/// request to the web page through Funnel, the server starting, and a daily summary. It posts through an
/// <see cref="AlertPoster"/>, off every request's path; with no webhook set it does nothing at all, but still keeps the
/// server's own bookkeeping (when it started, and whether it stopped cleanly), so alerts turned on later tell it right.
/// <para>
/// <b>Posted once, by the server.</b> Bans and lifts are posted by this server when it sees them in the database (at once for
/// those made inside it, through <see cref="Nudge"/>; within <see cref="AbuseOptions.BanCheckSeconds"/> for the command line's,
/// another process, which never posts), marking each as posted first (<see cref="Database.ClaimBanMade"/>), so none is posted
/// twice, across restarts too. What it finds from before alerts were on (the "alerts on since" time it keeps, set when it starts
/// with alerts on and finds none) it marks as posted without posting. Pins and restores, which the command line makes too, are
/// found the same way: the keys last posted for each operator character are kept, and new ones are posted.
/// </para>
/// </summary>
public sealed class ServerAlerts : BackgroundService {
    /// <summary>The name of the HTTP client alerts are posted with (its logging is removed: it would log the webhook).</summary>
    public const string HttpClientName = "discord-alerts";

    /// <summary>The least time between two alerts about requests through Funnel (later ones are counted into the next).</summary>
    public static readonly TimeSpan FunnelAlertEvery = TimeSpan.FromMinutes(10);

    /// <summary>The server's own bookkeeping, as kept in <c>server_state</c>.</summary>
    internal static class State {
        /// <summary>When this server first started with alerts on (since they were last off): bans and lifts from before aren't posted.</summary>
        public const string AlertsOnSince = "alerts_on_since";

        /// <summary>The end of the 24 hours the last daily summary covered.</summary>
        public const string SummaryPostedTo = "daily_summary_posted_to";

        /// <summary>When the server last started.</summary>
        public const string StartedAt = "server_started_at";

        /// <summary>When the server last stopped cleanly.</summary>
        public const string StoppedAt = "server_stopped_cleanly_at";

        /// <summary>Before an operator character's user ID: the key hash last posted as pinned for it.</summary>
        public const string PinPosted = "alerts_pin_posted:";
    }

    private readonly Database _db;
    private readonly AlertOptions _settings;
    private readonly AbuseOptions _abuse;
    private readonly HashSet<long> _operators;
    private readonly ConnectionRegistry? _registry;
    private readonly string _environment;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly DateTimeOffset _started;

    // The operator web page's address, linked in alerts; null without the page.
    private readonly string? _page;
    private readonly Channel<bool> _nudges = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Lock _funnel = new();
    private DateTimeOffset _funnelPosted = DateTimeOffset.MinValue;
    private int _funnelMore;
    private long _alertsOnSince = long.MaxValue;
    private long? _summaryPostedTo;
    private long? _previousStart;
    private long? _previousCleanStop;
    private readonly Lazy<bool> _stopNoted;

    /// <param name="http">Gives the client to post with (<see cref="HttpClientName"/>, from the factory).</param>
    /// <param name="registry">Who is online, for the daily summary's most online at once.</param>
    /// <param name="environment">The hosting environment's name, as the restart alert says it.</param>
    public ServerAlerts(Database db, ServerOptions options, Func<HttpClient> http, ConnectionRegistry? registry, string environment, ILogger<ServerAlerts> logger,
        TimeProvider? time = null) {
        this._db = db;
        this._settings = options.Alerts;
        this._abuse = options.Abuse;
        this._operators = [.. options.Operators.UserIds];
        this._registry = registry;
        this._environment = environment;
        this._logger = logger;
        this._time = time ?? TimeProvider.System;
        this._started = this._time.GetUtcNow();
        this._page = options.Admin.PageLink;
        this.Poster = this._settings.On ? new AlertPoster(this._settings.WebhookUrl, this._settings.RoleId, http, logger, time) { PageUrl = this._page } : null;
        this._stopNoted = new Lazy<bool>(() => {
            this.MarkStoppedCleanly();
            return true;
        });
    }

    /// <summary>Whether alerts are on (a webhook is set).</summary>
    public bool On => this.Poster != null;

    /// <summary>What posts them; null with alerts off.</summary>
    internal AlertPoster? Poster { get; }

    private long Now => this._time.GetUtcNow().ToUnixTimeSeconds();

    // ================================================================ running

    public override async Task StartAsync(CancellationToken cancellationToken) {
        this.StartUp();
        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken) {
        // Noted once. A host may be stopped twice at once (a test's server is: by the test host, and by the program's own run
        // when that sees the stop): the second waits for the first's note rather than writing it again, perhaps after whoever
        // stopped the server has gone on to use its database.
        _ = this._stopNoted.Value;
        await base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Notes this start (and remembers how the last run ended, for the restart alert); with alerts on, the "alerts on since" time
    /// if there is none, and where the daily summaries start; with them off, forgets "alerts on since", so turning them on later
    /// doesn't post what was done meanwhile.
    /// </summary>
    internal void StartUp() {
        var now = this.Now;
        try {
            this._previousStart = Number(this._db.GetServerState(State.StartedAt));
            this._previousCleanStop = Number(this._db.GetServerState(State.StoppedAt));
            this._db.SetServerState(State.StartedAt, Text(now));
            if (!this.On) {
                this._db.DeleteServerState(State.AlertsOnSince);
                return;
            }

            if (Number(this._db.GetServerState(State.AlertsOnSince)) is { } since) {
                this._alertsOnSince = since;
            } else {
                this._alertsOnSince = now;
                this._db.SetServerState(State.AlertsOnSince, Text(now));
            }

            this._summaryPostedTo = Number(this._db.GetServerState(State.SummaryPostedTo));
            if (this._summaryPostedTo == null && this._settings.DailySummaryHourUtc >= 0) {
                // The first summary comes when the hour next comes round (perhaps minutes from now); like every one, it covers the
                // 24 hours before it, from the database.
                this._summaryPostedTo = LatestDue(this._time.GetUtcNow(), this._settings.DailySummaryHourUtc).ToUnixTimeSeconds();
                this._db.SetServerState(State.SummaryPostedTo, Text(this._summaryPostedTo.Value));
            }
        } catch (Exception ex) {
            this._logger.LogWarning(ex, "Couldn't read or write the server's own bookkeeping for alerts and restarts");
        }
    }

    /// <summary>Notes that the server stopped cleanly (the next start's restart alert says so).</summary>
    internal void MarkStoppedCleanly() {
        try {
            if (File.Exists(this._db.FilePath)) {
                this._db.SetServerState(State.StoppedAt, Text(this.Now));
            }
        } catch (Exception ex) {
            this._logger.LogWarning(ex, "Couldn't note that the server stopped cleanly");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (this.Poster == null) {
            return;
        }

        // Off the host's start: nothing here holds it up.
        await Task.Yield();
        var posting = this.Poster.RunAsync(stoppingToken);
        if (this._settings.Restarts) {
            this.Poster.Post(this.StartedAlert());
        }

        var every = TimeSpan.FromSeconds(Math.Clamp(this._abuse.BanCheckSeconds, 1, 60));
        try {
            while (!stoppingToken.IsCancellationRequested) {
                this.Tick();
                await this.WaitForNudgeAsync(every, stoppingToken);
            }
        } catch (OperationCanceledException) {
            // Stopping.
        }

        try {
            await posting;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            this._logger.LogWarning(ex, "Posting alerts to Discord stopped");
        }
    }

    /// <summary>The restart alert: this version, and how the last run ended (as <see cref="StartUp"/> found it).</summary>
    internal Alert StartedAlert() => AlertWords.Started(RequestHandler.ServerVersion, this._environment, this._previousStart, this._previousCleanStop);

    /// <summary>Looks for bans, lifts and pins to post, and posts the daily summary when it is due.</summary>
    internal void Tick() {
        if (this.Poster == null) {
            return;
        }

        foreach (var (what, step) in new (string, Action)[] { ("bans", this.ScanBans), ("operator pins", this.ScanPins), ("the daily summary", this.PostSummaryIfDue) }) {
            try {
                step();
            } catch (Exception ex) {
                // Tried again at the next look.
                this._logger.LogWarning(ex, "Looking for {What} to post to Discord failed", what);
            }
        }
    }

    /// <summary>Looks for bans and pins to post now, rather than at the next look: for those made inside the server.</summary>
    public void Nudge() => this._nudges.Writer.TryWrite(true);

    private async Task WaitForNudgeAsync(TimeSpan every, CancellationToken stopping) {
        using var either = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var nudged = this._nudges.Reader.WaitToReadAsync(either.Token).AsTask();
        var due = Task.Delay(every, this._time, either.Token);
        await Task.WhenAny(nudged, due);
        await either.CancelAsync();
        stopping.ThrowIfCancellationRequested();
        this._nudges.Reader.TryRead(out _);
    }

    // ================================================================ what happens (each a no-op with alerts off; none throws)

    /// <summary>Someone was flagged (newly: a flag still in force from before a restart isn't told again).</summary>
    public void Flagged(FlagRow flag) {
        if (this.Poster == null) {
            return;
        }

        try {
            var user = flag.UserId is { } id ? this._db.GetUser(id) : null;
            this.Poster.Post(AlertWords.Flagged(flag, user, Math.Clamp(this._abuse.FlagExpiresAfterHours, 1, 8760), this._page));
        } catch (Exception ex) {
            this._logger.LogWarning(ex, "Couldn't make the alert for {Subject}'s flag", flag.Subject);
        }
    }

    /// <summary>Refusals from the server's own or its proxy's address would have flagged it: the forwarded client address is missing.</summary>
    public void ProxyWarning(string address) => this.Poster?.Post(AlertWords.ProxyWarning(address));

    /// <summary>
    /// The web page was asked for through Tailscale Funnel (the page calls this): posted at most every <see cref="FunnelAlertEvery"/>,
    /// with how many more came meanwhile, so the public can't make it ping again and again.
    /// </summary>
    public void FunnelRequest() {
        if (this.Poster == null) {
            return;
        }

        int more;
        lock (this._funnel) {
            var now = this._time.GetUtcNow();
            if (now - this._funnelPosted < FunnelAlertEvery) {
                this._funnelMore++;
                return;
            }

            more = this._funnelMore;
            this._funnelMore = 0;
            this._funnelPosted = now;
        }

        this.Poster.Post(AlertWords.FunnelRequest(more));
    }

    /// <summary>The least time between two alerts about one operator character's unsigned bans or lifts (later ones are counted into the next).</summary>
    public static readonly TimeSpan BadSignatureAlertEvery = TimeSpan.FromMinutes(10);

    // Per operator character: when its last bad-signature alert was posted, and how many came since. Only listed characters
    // whose keys are pinned get here, so it stays as small as the list.
    private readonly Dictionary<long, (DateTimeOffset Posted, int More)> _badSignatures = new();

    /// <summary>
    /// A ban or lift from an operator character's own login wasn't signed by its identity key (the plugin's requests call this):
    /// urgent, as a copy of its login may be in use, but posted at most every <see cref="BadSignatureAlertEvery"/> per character,
    /// with how many more came meanwhile, so whoever holds the copy can't make it ping again and again.
    /// </summary>
    public void OperatorBadSignature(UserRow user) {
        if (this.Poster == null) {
            return;
        }

        int more;
        lock (this._badSignatures) {
            var now = this._time.GetUtcNow();
            if (this._badSignatures.TryGetValue(user.UserId, out var last) && now - last.Posted < BadSignatureAlertEvery) {
                this._badSignatures[user.UserId] = last with { More = last.More + 1 };
                return;
            }

            more = last.More;
            this._badSignatures[user.UserId] = (now, 0);
        }

        this.Poster.Post(AlertWords.OperatorBadSignature(user, more, this._page));
    }

    /// <summary>The operator web page couldn't start where its settings say (the chat server runs on without it): urgent.</summary>
    public void PageFailed(string why) => this.Poster?.Post(AlertWords.PageFailed(why));

    /// <summary>Posts a test alert, at once (the web page's <b>Send a test alert</b>).</summary>
    /// <returns>False if alerts are off.</returns>
    public bool SendTest() {
        if (this.Poster == null) {
            return false;
        }

        this.Poster.Post(AlertWords.Test(RequestHandler.ServerVersion));
        return true;
    }

    /// <summary>
    /// A character signed in (a login answered): if it is listed as an operator, pinned, and its keys aren't the pinned ones, it is
    /// posted as urgent, once for each new set of keys (see <see cref="Database.ClaimOperatorMismatch"/>). A listed character
    /// never pinned isn't posted: it was never an operator.
    /// </summary>
    public void OperatorSignedIn(UserRow user) {
        if (this.Poster == null || !this._operators.Contains(user.UserId)) {
            return;
        }

        try {
            if (this._db.GetOperatorPin(user.UserId) is not { } pin) {
                return;
            }

            var keys = user.Keys.Hash;
            if (CryptographicOperations.FixedTimeEquals(pin.KeyHash, keys) || !this._db.ClaimOperatorMismatch(user.UserId, keys)) {
                return;
            }

            this.Poster.Post(AlertWords.OperatorOtherKeys(user, pin, this._page));
        } catch (Exception ex) {
            this._logger.LogWarning(ex, "Couldn't check operator character {UserId}'s keys against its pin for an alert", user.UserId);
        }
    }

    // ================================================================ looking in the database

    /// <summary>
    /// Posts each ban made and lifted that hasn't been (marking it first, so it is posted once), in the order they happened: a
    /// ban that replaced another says so, and the other's lift isn't posted on its own; an automatic block is urgent. Those from
    /// before "alerts on since" are marked without being posted, and so are automatic blocks that have ended by the time they are seen.
    /// Bans that ran out aren't posted (the daily summary counts them).
    /// </summary>
    internal void ScanBans() {
        var bans = this._db.GetUnannouncedBans();
        if (bans.Count == 0 || this.Poster == null || !this.KnowsAlertsOnSince()) {
            return;
        }

        var users = this._db.GetUsers(bans.Select(ban => ban.UserId).OfType<long>().Distinct()).ToDictionary(user => user.UserId);
        var events = bans.Where(ban => !ban.MadeAnnounced).Select(ban => (At: ban.CreatedAt, Lift: false, Ban: ban))
            .Concat(bans.Where(ban => ban.LiftedAt != null && !ban.LiftedAnnounced).Select(ban => (At: ban.LiftedAt!.Value, Lift: true, Ban: ban)))
            .OrderBy(entry => entry.At).ThenBy(entry => entry.Lift).ThenBy(entry => entry.Ban.BanId).ToList();
        var quiet = 0;
        var ended = 0;
        var now = this.Now;
        foreach (var (at, lift, ban) in events) {
            var user = ban.UserId is { } id ? users.GetValueOrDefault(id) : null;
            if (!lift) {
                if (!this._db.ClaimBanMade(ban.BanId)) {
                    continue;
                }

                if (at < this._alertsOnSince) {
                    quiet++;
                    continue;
                }

                if (ban.Automatic && ban.ExpiresAt <= now) {
                    // A block that has ended already (one an older server made, before going back to this one): nothing to do
                    // about it, so no urgent alert; the daily summary counts it.
                    ended++;
                    continue;
                }

                var replaced = bans.FirstOrDefault(old => old.ReplacedBy == ban.BanId);
                if (replaced != null) {
                    this._db.ClaimBanLifted(replaced.BanId);
                }

                this.Poster.Post(ban.Automatic ? AlertWords.Blocked(ban, this._abuse, this._page) : AlertWords.BanMade(ban, user, replaced, this._page));
            } else if (this._db.ClaimBanLifted(ban.BanId) && ban.ReplacedBy == null) {
                if (at < this._alertsOnSince) {
                    quiet++;
                    continue;
                }

                this.Poster.Post(AlertWords.BanLifted(ban, user));
            }
        }

        if (quiet > 0) {
            this._logger.LogInformation("Marked {Count} bans and lifts made before Discord alerts were on as posted, without posting them", quiet);
        }

        if (ended > 0) {
            this._logger.LogInformation("Marked {Count} automatic blocks that had already ended as posted, without posting them", ended);
        }
    }

    /// <summary>
    /// Whether "alerts on since" is known: if reading it at startup failed (the database, say), it is read (or set) now; until
    /// it is, nothing is marked as posted, so nothing is lost.
    /// </summary>
    private bool KnowsAlertsOnSince() {
        if (this._alertsOnSince != long.MaxValue) {
            return true;
        }

        if (Number(this._db.GetServerState(State.AlertsOnSince)) is not { } since) {
            since = this.Now;
            this._db.SetServerState(State.AlertsOnSince, Text(since));
        }

        this._alertsOnSince = since;
        return true;
    }

    /// <summary>
    /// Posts each operator character's keys pinned, or pinned again, since last posted (by any tool: the command line is another
    /// process). Pins from before "alerts on since" that were never posted are noted without being posted.
    /// </summary>
    internal void ScanPins() {
        var pins = this._db.GetOperatorPins();
        var posted = this._db.GetServerStates(State.PinPosted);
        if ((pins.Count == 0 && posted.Count == 0) || this.Poster == null || !this.KnowsAlertsOnSince()) {
            return;
        }

        var users = this._db.GetUsers(pins.Select(pin => pin.UserId)).ToDictionary(user => user.UserId);
        foreach (var pin in pins) {
            var key = State.PinPosted + Text(pin.UserId);
            var keys = Convert.ToHexString(pin.KeyHash);
            posted.TryGetValue(key, out var before);
            if (before == keys) {
                continue;
            }

            this._db.SetServerState(key, keys);
            if (before == null && pin.PinnedAt < this._alertsOnSince) {
                continue;
            }

            this.Poster.Post(AlertWords.OperatorPinned(pin.UserId, users.GetValueOrDefault(pin.UserId), pin, before == null ? null : Convert.FromHexString(before), this._page));
        }

        // A pin deleted (its character unlisted) is forgotten here too, so pinning it again is posted.
        foreach (var key in posted.Keys.Where(key => pins.All(pin => State.PinPosted + Text(pin.UserId) != key))) {
            this._db.DeleteServerState(key);
        }
    }

    /// <summary>
    /// Posts the daily summary once <see cref="AlertOptions.DailySummaryHourUtc"/> has come since the last one: the end of the 24
    /// hours each covered is kept, so a restart neither skips one (one missed while the server was down is posted when it is back,
    /// for the latest day) nor repeats one.
    /// </summary>
    internal void PostSummaryIfDue() {
        var hour = this._settings.DailySummaryHourUtc;
        if (hour < 0 || this.Poster == null) {
            return;
        }

        var now = this._time.GetUtcNow();
        var due = LatestDue(now, hour).ToUnixTimeSeconds();
        if (this._summaryPostedTo is { } posted && posted >= due) {
            return;
        }

        // Noted first: whatever goes wrong next, it isn't tried again every minute.
        this._db.SetServerState(State.SummaryPostedTo, Text(due));
        var first = this._summaryPostedTo == null;
        this._summaryPostedTo = due;
        if (!first) {
            this.Poster.Post(AlertWords.Summary(this.Summarise(due, now), this._page));
        }
    }

    /// <summary>When the daily summary was last due at <paramref name="now"/>: today at <paramref name="hourUtc"/>:00 UTC, or yesterday if that is still to come.</summary>
    public static DateTimeOffset LatestDue(DateTimeOffset now, int hourUtc) {
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddHours(hourUtc);
        return now >= today ? today : today.AddDays(-1);
    }

    /// <summary>What the daily summary for the 24 hours to <paramref name="end"/> says.</summary>
    internal DailySummary Summarise(long end, DateTimeOffset now) {
        var start = end - 86400;
        bool Within(long? at) => at is { } time && time > start && time <= end;
        var flags = this._db.GetFlags(start).Where(flag => Within(flag.FirstFlaggedAt)).ToList();
        var bans = this._db.GetBansChangedSince(start);
        var made = bans.Where(ban => !ban.Automatic && Within(ban.CreatedAt)).GroupBy(ban => ban.MadeVia)
            .OrderBy(group => group.Key switch { BanTools.CommandLine => 0, BanTools.WebPage => 1, BanTools.Plugin => 2, null => 4, _ => 3 })
            .Select(group => (group.Key, group.Count())).ToList();
        var (peak, peakAt) = this._registry?.TakePeak() ?? (0, 0);
        var databaseBytes = new[] { this._db.FilePath, this._db.FilePath + "-wal" }.Where(File.Exists).Sum(path => new FileInfo(path).Length);
        return new DailySummary(end,
            FlaggedCharacters: flags.Count(flag => flag.UserId != null),
            FlaggedAddresses: flags.Count(flag => flag.UserId == null),
            Blocked: bans.Count(ban => ban.Automatic && Within(ban.CreatedAt)),
            Made: made,
            Lifted: bans.Count(ban => Within(ban.LiftedAt) && ban.ReplacedBy == null),
            Ended: bans.Count(ban => ban.LiftedAt == null && Within(ban.ExpiresAt)),
            InForce: this._db.GetActiveBans(now.ToUnixTimeSeconds()).Count,
            Peak: peak,
            PeakAt: peak > 0 && peakAt > 0 ? peakAt : null,
            CountedSince: this._started.ToUnixTimeSeconds() > start ? this._started.ToUnixTimeSeconds() : null,
            Registered: this._db.CountUsersRegistered(start, end),
            Version: RequestHandler.ServerVersion,
            Uptime: now - this._started,
            DatabaseBytes: databaseBytes,
            Undelivered: this.Poster?.TakeUndelivered() ?? 0);
    }

    private static long? Number(string? text) =>
        long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
}
