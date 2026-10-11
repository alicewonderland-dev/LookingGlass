using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using LookingGlass.Server.Data;
using LookingGlass.Server.Realtime;
using LookingGlass.Server.Services;

namespace LookingGlass.Server.Admin;

/// <summary>
/// The operator web page (see "The web page" in docs/operator-tools-plan.md, and "The operator web page" in docs/server.md):
/// every request to the page's own host comes here, and nothing else does. It lets a request in only when
/// <c>tailscale serve</c> says it comes from a listed Tailscale login (<c>Tailscale-User-Login</c>), not through Funnel (no
/// <c>Tailscale-Funnel-Request</c>, which also raises an urgent alert), and asked for at <c>Admin:Url</c>
/// (<c>X-Forwarded-Host</c>); everything else gets 403 and the same few words. Nothing changes on a GET; every form carries
/// a single-use token (<see cref="PageTokens"/>), and a POST must come from the page's own origin. Every answer carries the
/// security headers, and everything shown is HTML-encoded, anything hidden in it shown as escapes (<see cref="VisibleText"/>).
/// Bans, lifts and pins go through <see cref="OperatorActions"/> as the web page, by the Tailscale login, from the device's
/// tailnet address.
/// </summary>
public sealed class OperatorPage {
    /// <summary>The Content Security Policy on every answer: the page's own stylesheet and forms, and nothing else (no script at all, no framing).</summary>
    public const string ContentSecurityPolicy = "default-src 'none'; style-src 'self'; img-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";

    /// <summary>The refusal, for anyone not let in: nothing about which check failed (the log says that).</summary>
    public const string NotAllowed = "This page is only for this server's operators, reached through Tailscale from an account they listed.";

    /// <summary>How many audit entries the page lists.</summary>
    public const int AuditEntries = 50;

    /// <summary>How long a line saying what was done is kept for the page it leads back to.</summary>
    private static readonly TimeSpan NoteLifetime = TimeSpan.FromMinutes(10);

    private const int MaxNotes = 500;

    private static readonly string Stylesheet = LoadStylesheet();

    private readonly Database _db;
    private readonly OperatorActions _actions;
    private readonly ServerAlerts _alerts;
    private readonly ConnectionRegistry _registry;
    private readonly AdminOptions _admin;
    private readonly ILogger _logger;
    private readonly LocalAddresses _local;
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;
    private readonly IReadOnlyList<string> _logins;
    private readonly string _host;
    private readonly string _origin;
    private readonly DateTimeOffset _started;
    private readonly ConcurrentDictionary<string, Note> _notes = new();
    private readonly Lock _refusals = new();
    private long _refusalLogged = long.MinValue;
    private int _refusalsSince;

    public OperatorPage(Database db, OperatorActions actions, ServerAlerts alerts, ConnectionRegistry registry, IOptions<ServerOptions> options, ILogger<OperatorPage> logger,
        TimeProvider? time = null, LocalAddresses? local = null) {
        this._db = db;
        this._actions = actions;
        this._alerts = alerts;
        this._registry = registry;
        this._admin = options.Value.Admin;
        this._logger = logger;
        this._time = time ?? TimeProvider.System;
        this._local = local ?? new LocalAddresses();
        this._zone = this._admin.Zone;
        this._logins = this._admin.Logins;
        var page = this._admin.PageUri ?? new Uri("https://unset.invalid:1/");
        this._host = page.Authority;
        this._origin = page.GetLeftPart(UriPartial.Authority);
        this._started = this._time.GetUtcNow();
        this.Tokens = new PageTokens(this._time);
    }

    /// <summary>The forms' tokens: made at this start, so a form from before a restart is out of date.</summary>
    internal PageTokens Tokens { get; }

    private long Now => this._time.GetUtcNow().ToUnixTimeSeconds();

    // ================================================================ every request

    /// <summary>Answers one request to the page's host: who it is first, then what it asks for.</summary>
    public async Task HandleAsync(HttpContext context) {
        var request = context.Request;
        var response = context.Response;
        SetSecurityHeaders(response.Headers);

        var login = this.Identify(request);
        if (login == null) {
            await Write(response, StatusCodes.Status403Forbidden, Document("Not allowed", $"<main class=\"confirm\"><h1>Not allowed.</h1><p>{E(NotAllowed)}</p></main>", styled: false));
            return;
        }

        var path = request.Path.Value ?? "/";
        var reading = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);
        var posting = HttpMethods.IsPost(request.Method);
        var actor = new OperatorActor(OperatorTool.WebPage, login, From(request));
        try {
            this.BeforeAnswerForTests?.Invoke(context);
            switch (path) {
                case "/" when reading:
                    await Write(response, StatusCodes.Status200OK, this.MainPage(login, this.Prefilled(request, login)));
                    return;
                case "/style.css" when reading:
                    response.StatusCode = StatusCodes.Status200OK;
                    response.ContentType = "text/css; charset=utf-8";
                    await response.WriteAsync(Stylesheet);
                    return;
                case "/lift" when reading:
                    await Write(response, StatusCodes.Status200OK, this.LiftPage(login, request.Query["ban"]));
                    return;
                case "/pin" when reading:
                    await Write(response, StatusCodes.Status200OK, this.PinPage(login, request.Query["user"]));
                    return;
                case "/ban" or "/ban/confirm" or "/lift/confirm" or "/pin/confirm" or "/test-alert" when posting:
                    await this.PostAsync(context, path, login, actor);
                    return;
                case "/" or "/style.css" or "/lift" or "/pin":
                    response.Headers.Allow = "GET, HEAD";
                    await Write(response, StatusCodes.Status405MethodNotAllowed, this.Message(login, "Not here", "That address is only for reading."));
                    return;
                case "/ban" or "/ban/confirm" or "/lift/confirm" or "/pin/confirm" or "/test-alert":
                    // Nothing changes on a GET: these only take a form sent from the page.
                    response.Headers.Allow = "POST";
                    await Write(response, StatusCodes.Status405MethodNotAllowed, this.Message(login, "Not here", "That address only takes a form sent from the page."));
                    return;
                default:
                    await Write(response, StatusCodes.Status404NotFound, this.Message(login, "Not found", "There is nothing at that address."));
                    return;
            }
        } catch (Microsoft.Data.Sqlite.SqliteException ex) when (!response.HasStarted) {
            this._logger.LogWarning(ex, "The operator web page couldn't read or write the database");
            await Write(response, StatusCodes.Status503ServiceUnavailable,
                this.Message(login, "Not now", "The server couldn't read or write its database just now, so nothing was done. Try again in a moment."));
        } catch (Exception ex) when (!response.HasStarted && ex is not OperationCanceledException) {
            // Answered here, not by Kestrel (which would drop the security headers): a plain page, nothing about the error.
            this._logger.LogError(ex, "The operator web page failed to answer {Method} {Path}", Visible(request.Method), Visible(request.Path.Value));
            response.Clear();
            SetSecurityHeaders(response.Headers);
            await Write(response, StatusCodes.Status500InternalServerError,
                Document("Not done", "<main class=\"confirm\"><h1>Not done</h1><p>Something went wrong on the server, so that may not have been done. " +
                                     "The server's log says what. <a href=\"/\">Back to the page</a></p></main>", styled: false));
        }
    }

    /// <summary>For tests: run before each request let in is answered (to make one fail, say).</summary>
    internal Action<HttpContext>? BeforeAnswerForTests { get; set; }

    /// <summary>
    /// The headers on every answer, refusals and errors included: no script, no framing, nothing kept, and the page's address
    /// told to nobody else (<c>same-origin</c>: a Lodestone link sends no referrer, while the page's own forms still carry their
    /// real <c>Origin</c>, which <c>no-referrer</c> would make browsers send as <c>null</c>).
    /// </summary>
    public static void SetSecurityHeaders(IHeaderDictionary headers) {
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "same-origin";
        headers.CacheControl = "no-store";
    }

    // ================================================================ who

    /// <summary>
    /// The Tailscale login, if the request may come in: through <c>tailscale serve</c> (never Funnel), from another device than
    /// this machine, from a listed login, asked for at the page's own address. Otherwise null, logged (at most once a minute) with
    /// which check failed; a request through Funnel raises an urgent alert too.
    /// </summary>
    /// <remarks>
    /// <c>tailscale serve</c> names a request by the Tailscale login of the device it came from, and a request from this machine
    /// itself comes from the machine's own tailnet address, so on a server signed in to Tailscale as the owner (not tagged) it
    /// would carry the owner's login: any program on the machine could reach the page as the owner, around the socket's
    /// permissions. So a request whose <c>X-Forwarded-For</c> (which <c>tailscale serve</c> sets to the device's address) is one
    /// of this machine's own addresses, or that has none, is refused.
    /// </remarks>
    private string? Identify(HttpRequest request) {
        string why;
        if (request.Headers.ContainsKey("Tailscale-Funnel-Request")) {
            this._alerts.FunnelRequest();
            why = "it came through Tailscale Funnel (Tailscale-Funnel-Request), from the public internet; Funnel must never lead to the page";
        } else if (ForwardedFor(request) is not { } source) {
            why = "it has no X-Forwarded-For address (tailscale serve always sets one): it didn't come through tailscale serve";
        } else if (this._local.IsLocal(source)) {
            why = $"it came from this machine itself ({source}): tailscale serve would name such a request by this machine's own Tailscale login, " +
                  "which any program here could use, so the page is only for other devices";
        } else if (request.Headers["Tailscale-User-Login"] is not { Count: 1 } logins || string.IsNullOrEmpty(logins[0])) {
            why = request.Headers["Tailscale-User-Login"].Count > 1
                ? "it has more than one Tailscale-User-Login"
                : "it has no Tailscale-User-Login (it didn't come through tailscale serve, or came from a tagged device or from this machine itself)";
        } else if (this._logins.FirstOrDefault(listed => string.Equals(listed, logins[0], StringComparison.OrdinalIgnoreCase)) == null) {
            why = $"its Tailscale login, {Visible(logins[0])}, isn't listed in LookingGlass:Admin:TailscaleLogins";
        } else if (request.Headers["X-Forwarded-Host"] is not { Count: 1 } host || !string.Equals(host[0], this._host, StringComparison.OrdinalIgnoreCase)) {
            why = $"it was asked for as {(request.Headers["X-Forwarded-Host"].Count == 0 ? "(no X-Forwarded-Host)" : Visible(request.Headers["X-Forwarded-Host"].ToString()))}, " +
                  $"not {this._host} (LookingGlass:Admin:Url)";
        } else {
            return logins[0]!;
        }

        this.LogRefusal(request, why);
        return null;
    }

    /// <summary>The first <c>X-Forwarded-For</c> address (the device's, as <c>tailscale serve</c> sets it), or null if there is none.</summary>
    private static System.Net.IPAddress? ForwardedFor(HttpRequest request) {
        var first = request.Headers["X-Forwarded-For"].ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first != null && System.Net.IPAddress.TryParse(first, out var address) ? address : null;
    }

    /// <summary>Where the request came from: the device's tailnet address, as <c>tailscale serve</c> passes it on.</summary>
    private static string From(HttpRequest request) {
        var forwarded = request.Headers["X-Forwarded-For"].ToString();
        var first = forwarded.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first != null ? VisibleText.Of(first, 60) : request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
    }

    /// <summary>Logs a refused request, at most once a minute (counting the rest), so nobody can fill the log with them.</summary>
    private void LogRefusal(HttpRequest request, string why) {
        var now = this._time.GetUtcNow().UtcTicks;
        int more;
        lock (this._refusals) {
            if (this._refusalLogged != long.MinValue && now - this._refusalLogged < TimeSpan.TicksPerMinute) {
                this._refusalsSince++;
                return;
            }

            more = this._refusalsSince;
            this._refusalsSince = 0;
            this._refusalLogged = now;
        }

        var from = From(request);
        this._logger.LogWarning("Refused a request to the operator web page ({Method} {Path}{From}): {Why}. Said at most once a minute{More}.",
            Visible(request.Method), Visible(request.Path.Value), from.Length > 0 ? $" from {from}" : "", why,
            more > 0 ? $"; {more} more refused since the last of these" : "");
    }

    /// <summary>
    /// Whether a form was sent from the page itself: a browser's <c>Sec-Fetch-Site</c>, if there is one, says same-origin; and its
    /// <c>Origin</c> is exactly the page's (<c>Admin:Url</c>'s scheme, name and port; <c>null</c> is refused), or, only without an
    /// Origin, <c>Sec-Fetch-Site</c> says same-origin. Neither header can be set by a page.
    /// </summary>
    private bool SameOrigin(HttpRequest request) {
        var site = request.Headers["Sec-Fetch-Site"];
        if (site.Count > 0 && !(site.Count == 1 && site[0] == "same-origin")) {
            return false;
        }

        var origin = request.Headers.Origin;
        if (origin.Count > 0) {
            return origin.Count == 1 && string.Equals(origin[0], this._origin, StringComparison.Ordinal);
        }

        return site.Count == 1;
    }

    // ================================================================ forms

    private async Task PostAsync(HttpContext context, string path, string login, OperatorActor actor) {
        var request = context.Request;
        var response = context.Response;
        if (!this.SameOrigin(request)) {
            this.LogRefusal(request, $"a form came from another site (Origin {Visible(request.Headers.Origin.ToString())}, Sec-Fetch-Site " +
                                     $"{Visible(request.Headers["Sec-Fetch-Site"].ToString())}), not from {this._origin}");
            await Write(response, StatusCodes.Status403Forbidden,
                this.Message(login, "Not done", "That form didn't come from this page, so nothing was done."));
            return;
        }

        IFormCollection form;
        try {
            if (!request.HasFormContentType || !(request.ContentType ?? "").StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidDataException("Not a form.");
            }

            context.Features.Set<IFormFeature>(new FormFeature(request, new FormOptions {
                ValueCountLimit = 16, KeyLengthLimit = 64, ValueLengthLimit = 4096, MultipartBodyLengthLimit = 16 * 1024, BufferBody = false,
            }));
            form = await request.ReadFormAsync(context.RequestAborted);
        } catch (Exception ex) when (ex is InvalidDataException or IOException or BadHttpRequestException or InvalidOperationException) {
            await Write(response, StatusCodes.Status400BadRequest, this.Message(login, "Not done", "That form couldn't be read, so nothing was done."));
            return;
        }

        string Field(string name) => form[name] is { Count: 1 } value ? value[0] ?? "" : "";
        var token = Field("token");
        switch (path) {
            case "/ban":
                await this.ContinueAsync(response, login, actor, token, Field("whom"), Field("days"), Field("reason"), Field("force") == "yes");
                return;
            case "/ban/confirm":
                await this.BanAsync(response, login, actor, token, Field("whom"), Field("days"), Field("reason"), Field("force") == "yes");
                return;
            case "/lift/confirm":
                await this.LiftAsync(response, login, actor, token, Field("ban"));
                return;
            case "/pin/confirm":
                await this.PinAsync(response, login, actor, token, Field("user"), Field("keys"));
                return;
            default:
                if (!await this.TokenGoodAsync(response, login, token, TestAlertAction)) {
                    return;
                }

                await this.Done(response, login, this._alerts.SendTest()
                    ? "Sent a test alert to the Discord channel."
                    : "Alerts are off (no Discord webhook is set), so nothing was sent.");
                return;
        }
    }

    private const string ContinueAction = "continue";
    private const string TestAlertAction = "test-alert";

    /// <summary>The exact ban a confirmation page shows, as its token is bound to it.</summary>
    private static string BanAction(string whom, int? days, string reason, bool force) =>
        string.Join('\n', "ban", whom, days?.ToString(CultureInfo.InvariantCulture) ?? "", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reason))), force ? "force" : "");

    private static string LiftAction(long banId) => "lift\n" + banId.ToString(CultureInfo.InvariantCulture);

    private static string PinAction(long userId, byte[] keyHash) => $"pin\n{userId.ToString(CultureInfo.InvariantCulture)}\n{Convert.ToHexString(keyHash)}";

    /// <summary>Whether a form's token is good for this login and action (using it up); if not, answers saying why, with the page again.</summary>
    private async Task<bool> TokenGoodAsync(HttpResponse response, string login, string token, string action) {
        var check = this.Tokens.Check(token, login, action);
        if (check == TokenCheck.Good) {
            return true;
        }

        await this.RefuseFormAsync(response, login, check);
        return false;
    }

    /// <summary>Answers a form whose token isn't good, saying why, with the page again.</summary>
    private async Task RefuseFormAsync(HttpResponse response, string login, TokenCheck check) {
        var why = check switch {
            TokenCheck.Restarted => "This page is out of date (the server restarted). Check and confirm again.",
            TokenCheck.Expired => $"This page is out of date: its forms work for {PageTokens.Lifetime.TotalMinutes.ToString(CultureInfo.InvariantCulture)} minutes. Check and confirm again.",
            TokenCheck.Used => "That form was sent already (each works once). Check and confirm again.",
            _ => "That form isn't one this page made for you, so nothing was done.",
        };
        if (check == TokenCheck.Wrong) {
            this.LogRefusal(response.HttpContext.Request, $"a form from {Visible(login)} has no token the page made for that login and that action");
        }

        await Write(response, StatusCodes.Status403Forbidden, this.MainPage(login, new PageState { Error = why }));
    }

    /// <summary>The ban form's <b>Continue...</b>: the confirmation page, or the form again with why not.</summary>
    private async Task ContinueAsync(HttpResponse response, string login, OperatorActor actor, string token, string whom, string daysText, string reason, bool force) {
        if (!await this.TokenGoodAsync(response, login, token, ContinueAction)) {
            return;
        }

        var state = new PageState { Whom = whom, Days = daysText, Reason = reason, Force = force };
        if (string.IsNullOrWhiteSpace(whom)) {
            await Write(response, StatusCodes.Status200OK, this.MainPage(login, state with { Error = "Whom to ban: a name@world, a Lodestone ID, or an address or prefix." }));
            return;
        }

        if (!TryDays(daysText, out var days)) {
            await Write(response, StatusCodes.Status200OK, this.MainPage(login, state with {
                Error = $"For: a whole number of days, 1 to {OperatorActions.MaxDays.ToString("N0", CultureInfo.InvariantCulture)}, or empty to ban until you lift it. Nothing was banned.",
            }));
            return;
        }

        var check = this._actions.CheckBan(actor, whom, days, reason, force);
        if (!check.Done) {
            await Write(response, StatusCodes.Status200OK, this.MainPage(login, state with { Error = BanRefusal(check) }));
            return;
        }

        await Write(response, StatusCodes.Status200OK, this.BanPage(login, check, days, reason.Trim(), force));
    }

    /// <summary>The confirmation page's <b>Ban</b>: the ban, then the page saying so (or the form again with why not).</summary>
    private async Task BanAsync(HttpResponse response, string login, OperatorActor actor, string token, string whom, string daysText, string reason, bool force) {
        if (!TryDays(daysText, out var days)) {
            // Not what the page put in its form.
            await this.RefuseFormAsync(response, login, TokenCheck.Wrong);
            return;
        }

        if (!await this.TokenGoodAsync(response, login, token, BanAction(whom, days, reason, force))) {
            return;
        }

        var result = this._actions.Ban(actor, whom, days, reason, force);
        if (!result.Done) {
            await Write(response, StatusCodes.Status200OK, this.MainPage(login, new PageState {
                Whom = whom, Days = daysText, Reason = reason, Force = force, Error = BanRefusal(result),
            }));
            return;
        }

        var span = days is { } count ? $" for {Days(count)}" : " until you lift it";
        await this.Done(response, login, $"Banned {this.ShortName(result.UserId, result.Address)}{span}{(result.Replaced > 0 ? ", replacing the ban it had" : "")}.");
    }

    /// <summary>The confirmation page's <b>Lift the ban</b>.</summary>
    private async Task LiftAsync(HttpResponse response, string login, OperatorActor actor, string token, string banText) {
        if (!long.TryParse(banText, NumberStyles.None, CultureInfo.InvariantCulture, out var banId)) {
            await this.RefuseFormAsync(response, login, TokenCheck.Wrong);
            return;
        }

        if (!await this.TokenGoodAsync(response, login, token, LiftAction(banId))) {
            return;
        }

        var now = this.Now;
        var ban = this._db.GetActiveBans(now).Concat(this._db.GetBanHistory(now)).FirstOrDefault(row => row.BanId == banId);
        if (ban == null) {
            await Write(response, StatusCodes.Status200OK, this.MainPage(login, new PageState { Error = "That ban isn't in force any more, so nothing was lifted." }));
            return;
        }

        var result = this._actions.Lift(actor, ban.UserId?.ToString(CultureInfo.InvariantCulture) ?? ban.Address!, banId);
        if (!result.Done) {
            await Write(response, StatusCodes.Status200OK, this.MainPage(login, new PageState { Error = result.Why }));
            return;
        }

        await this.Done(response, login, $"Lifted the ban on {this.ShortName(ban.UserId, ban.Address)}.");
    }

    /// <summary>The confirmation page's <b>Restore operator status</b> or <b>Pin operator keys</b>: only the keys it showed.</summary>
    private async Task PinAsync(HttpResponse response, string login, OperatorActor actor, string token, string userText, string keysText) {
        byte[]? keys = null;
        try {
            keys = keysText.Length == 64 ? Convert.FromHexString(keysText) : null;
        } catch (FormatException) {
            // Not hex: not a form the page made.
        }

        if (!long.TryParse(userText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var userId) || keys == null) {
            await this.RefuseFormAsync(response, login, TokenCheck.Wrong);
            return;
        }

        if (!await this.TokenGoodAsync(response, login, token, PinAction(userId, keys))) {
            return;
        }

        var result = this._actions.PinShownKeys(actor, userId, keys);
        // Posted to Discord at once (urgent), rather than at the alerts' next look.
        this._alerts.Nudge();
        var name = this.ShortName(userId, null);
        if (!result.Done) {
            var why = result.Refusal == OperatorRefusal.KeysDiffer
                ? $"The keys of {name} changed again since the page showed them (they are {result.Fingerprint} now). Nothing was pinned. Compare the new " +
                  "fingerprint with the plugin's Settings, then try again."
                : result.Why;
            await Write(response, StatusCodes.Status200OK, this.MainPage(login, new PageState { Error = why }));
            return;
        }

        await this.Done(response, login, result.Unchanged
            ? $"The keys of {name} ({result.Fingerprint}) were pinned already: nothing changed."
            : result.PreviousPin != null
                ? $"{name} is an operator again, with keys {result.Fingerprint}."
                : $"{name} is an operator now, with keys {result.Fingerprint}.");
    }

    /// <summary>After something was done: back to the page (a GET, so reloading it does nothing again), saying what was done.</summary>
    private Task Done(HttpResponse response, string login, string text) {
        var now = this._time.GetUtcNow();
        if (this._notes.Count >= MaxNotes) {
            foreach (var (id, note) in this._notes.OrderBy(entry => entry.Value.At).Take(this._notes.Count - MaxNotes + 1).ToList()) {
                this._notes.TryRemove(id, out _);
            }
        }

        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        this._notes[key] = new Note(login, text, now);
        response.StatusCode = StatusCodes.Status303SeeOther;
        response.Headers.Location = "/?note=" + key;
        return Task.CompletedTask;
    }

    /// <summary>What the main page shows filled in: the line saying what was just done, or a <b>Ban...</b> link's whom and days.</summary>
    private PageState Prefilled(HttpRequest request, string login) {
        var state = new PageState {
            Whom = Cut(request.Query["whom"].ToString(), 200),
            Days = Cut(request.Query["days"].ToString(), 10),
        };
        var key = request.Query["note"].ToString();
        if (key.Length > 0 && this._notes.TryGetValue(key, out var note) && string.Equals(note.Login, login, StringComparison.OrdinalIgnoreCase)
            && this._time.GetUtcNow() - note.At < NoteLifetime) {
            state = state with { Done = note.Text };
        }

        return state;
    }

    private static bool TryDays(string text, out int? days) {
        days = null;
        if (string.IsNullOrWhiteSpace(text)) {
            return true;
        }

        if (int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count is >= 1 and <= OperatorActions.MaxDays) {
            days = count;
            return true;
        }

        return false;
    }

    /// <summary>Why a ban wasn't made, in the page's words.</summary>
    private static string BanRefusal(OperatorResult result) => result.Refusal switch {
        OperatorRefusal.NoSuchCharacter => $"No character {result.Whom} is registered on this server. To ban one that isn't (so it can't register), use its " +
                                           "Lodestone ID: the number in its Lodestone address.",
        OperatorRefusal.NotATarget => $"{result.Problem} Whom to ban is a name@world, a Lodestone ID, or an address or prefix.",
        OperatorRefusal.ProxyAddress => $"{result.Problem} Nothing was banned. If you really mean it, tick \"This is the server's own or its proxy's address, " +
                                        "and I mean it\".",
        _ => result.Why,
    };

    // ================================================================ pages

    /// <summary>What the main page shows besides the lists.</summary>
    private sealed record PageState {
        public string Whom { get; init; } = "";
        public string Days { get; init; } = "";
        public string Reason { get; init; } = "";
        public bool Force { get; init; }
        public string? Done { get; init; }
        public string? Error { get; init; }
    }

    private sealed record Note(string Login, string Text, DateTimeOffset At);

    private string MainPage(string login, PageState state) {
        var now = this.Now;
        var view = this._actions.See(OperatorTool.WebPage);
        var listed = this._actions.Operators();
        var audit = this._actions.Audit(now - OperatorActions.AuditKeptDays * 86400L, AuditEntries);
        var users = this._db.GetUsers(view.InForce.Concat(view.History).Select(shown => shown.Ban.UserId).Concat(view.Flags.Select(shown => shown.Flag.UserId))
            .OfType<long>().Distinct()).ToDictionary(user => user.UserId);
        var html = new StringBuilder();
        html.Append(this.Header(login));
        html.Append("<main>");
        if (state.Done != null) {
            html.Append($"<p class=\"note done\" role=\"status\">{E(state.Done)}</p>");
        }

        if (state.Error != null) {
            html.Append($"<p class=\"note error\" role=\"alert\">{E(state.Error)}</p>");
        }

        var bans = view.InForce.Count(shown => !shown.Ban.Automatic);
        var blocked = view.InForce.Count(shown => shown.Ban.Automatic);
        html.Append("<p class=\"counts\">")
            .Append($"<span>{Count(view.Flags.Count, "flagged", "flagged")}</span>")
            .Append($"<span>{Count(bans, "ban in force", "bans in force")}</span>")
            .Append($"<span>{Count(blocked, "blocked automatically", "blocked automatically")}</span>")
            .Append($"<span>{Count(this._registry.OnlineCount, "player online", "players online")}</span>")
            .Append("</p>");

        // Ban someone.
        html.Append("<section><h2 id=\"ban\">Ban someone</h2>")
            .Append("<form class=\"ban\" method=\"post\" action=\"/ban\">")
            .Append($"<input type=\"hidden\" name=\"token\" value=\"{E(this.Tokens.Make(login, ContinueAction))}\">")
            .Append($"<label for=\"whom\">Whom</label><input type=\"text\" id=\"whom\" name=\"whom\" maxlength=\"200\" required autocomplete=\"off\" spellcheck=\"false\" placeholder=\"name@world, Lodestone ID, or address or prefix\" value=\"{E(state.Whom)}\">")
            .Append($"<label for=\"days\">For</label><span><input type=\"number\" id=\"days\" name=\"days\" min=\"1\" max=\"{OperatorActions.MaxDays}\" inputmode=\"numeric\" value=\"{E(state.Days)}\"> days</span>")
            .Append("<span class=\"hint\">Leave empty to ban until you lift it.</span>")
            .Append($"<label for=\"reason\">Reason</label><input type=\"text\" id=\"reason\" name=\"reason\" maxlength=\"{OperatorActions.MaxReasonLength}\" autocomplete=\"off\" value=\"{E(state.Reason)}\">")
            .Append($"<span class=\"hint\">The player is shown this. One line, at most {OperatorActions.MaxReasonLength} characters.</span>")
            .Append($"<label class=\"tick\"><input type=\"checkbox\" name=\"force\" value=\"yes\"{(state.Force ? " checked" : "")}>This is the server's own or its proxy's address, and I mean it</label>")
            .Append("<span class=\"go\"><button type=\"submit\">Continue...</button></span>")
            .Append("</form></section>");

        // Flagged.
        html.Append($"<section><h2>Flagged in the last {Count(view.FlagHours, "hour")} ({view.Flags.Count})</h2>");
        if (view.Flags.Count == 0) {
            html.Append($"<p class=\"empty\">Nothing flagged in the last {Count(view.FlagHours, "hour")}.</p>");
        } else {
            html.Append("<ul class=\"rows\">");
            foreach (var (flag, _, banned) in view.Flags) {
                var whom = flag.UserId?.ToString(CultureInfo.InvariantCulture) ?? flag.Address ?? "";
                html.Append("<li class=\"row\">")
                    .Append($"<div>{this.Who(flag.UserId, flag.Address, users)}{(banned ? " <span class=\"warn\">banned</span>" : "")}</div>")
                    .Append($"<div>{E(flag.Why)}</div>")
                    .Append($"<div class=\"sub\">{E(flag.Limits.Replace(",", ", ", StringComparison.Ordinal).Replace(",  ", ", ", StringComparison.Ordinal))}</div>")
                    .Append($"<div class=\"sub\">Last {E(PageTime.Format(flag.LastRefusedAt, this._zone))}</div>")
                    .Append($"<div class=\"act\"><a class=\"button\" href=\"{E($"/?whom={Uri.EscapeDataString(whom)}&days=7#ban")}\">Ban...</a></div>")
                    .Append("</li>");
            }

            html.Append("</ul>");
        }

        html.Append("</section>");

        // Bans in force.
        html.Append($"<section><h2>Bans in force ({view.InForce.Count})</h2>");
        if (view.InForce.Count == 0) {
            html.Append("<p class=\"empty\">No bans in force.</p>");
        } else {
            html.Append("<ul class=\"rows\">");
            foreach (var (ban, _) in view.InForce) {
                html.Append("<li class=\"row\">")
                    .Append($"<div>{this.Who(ban.UserId, ban.Address, users)}</div>")
                    .Append($"<div>{E(this.Until(ban))}</div>")
                    .Append($"<div class=\"sub\">{E(Made(ban))}</div>")
                    .Append($"<div>{(ban.Reason.Length > 0 ? $"&ldquo;{E(ban.Reason)}&rdquo;" : "<span class=\"sub\">No reason given.</span>")}</div>")
                    .Append($"<div class=\"act\"><a class=\"button quiet\" href=\"/lift?ban={ban.BanId.ToString(CultureInfo.InvariantCulture)}\">Lift...</a></div>")
                    .Append("</li>");
            }

            html.Append("</ul>");
        }

        html.Append("</section>");

        // Operators, folded unless one needs pinning or restoring.
        if (listed.Count > 0) {
            var attention = listed.Count(one => one.Status is OperatorStatus.KeysChanged or OperatorStatus.NotPinned);
            var restore = listed.Count(one => one.Status == OperatorStatus.KeysChanged);
            var summary = $"Operators ({listed.Count})" + (restore > 0 ? $" · {restore} to restore" : attention > 0 ? $" · {attention} to pin" : "");
            html.Append($"<details class=\"fold\" id=\"operators\"{(attention > 0 ? " open" : "")}><summary>{E(summary)}</summary><ul class=\"rows\">");
            foreach (var one in listed) {
                html.Append(this.OperatorRow(one));
            }

            html.Append("</ul></details>");
        }

        // Lifted or ended lately.
        html.Append($"<details class=\"fold\"><summary>Lifted or ended in the last {Count(view.HistoryDays, "day")} ({view.History.Count})</summary>");
        if (view.History.Count == 0) {
            html.Append($"<p class=\"empty\">No bans lifted or ended in the last {Count(view.HistoryDays, "day")}.</p>");
        } else {
            html.Append("<ul class=\"rows\">");
            foreach (var (ban, _) in view.History) {
                var ended = ban.LiftedAt is { } lifted
                    ? $"{(ban.ReplacedBy != null ? "replaced by a new ban" : "lifted")} {PageTime.Format(lifted, this._zone)}{(OperatorActions.LiftedWords(ban) is { } by ? $" {by}" : "")}"
                    : $"ended {PageTime.Format(ban.ExpiresAt!.Value, this._zone)}";
                html.Append("<li class=\"row\">")
                    .Append($"<div>{this.Who(ban.UserId, ban.Address, users)}</div>")
                    .Append($"<div>From {E(PageTime.Format(ban.CreatedAt, this._zone))}, {E(ended)}.</div>")
                    .Append($"<div class=\"sub\">{E(Made(ban))}</div>")
                    .Append($"<div class=\"sub\">{(ban.Reason.Length > 0 ? $"Reason: {E(ban.Reason)}" : "No reason given.")}</div>")
                    .Append("</li>");
            }

            html.Append("</ul>");
        }

        html.Append("</details>");

        // The audit trail.
        html.Append($"<details class=\"fold\"><summary>What operators did (the last {AuditEntries})</summary>");
        if (audit.Count == 0) {
            html.Append($"<p class=\"empty\">Nothing yet. Every ban, lift and operator pin, tried with any tool, is kept here for {OperatorActions.AuditKeptDays} days.</p>");
        } else {
            html.Append("<ul class=\"rows\">");
            foreach (var entry in audit) {
                var who = entry.Who + (entry.From.Length > 0 ? $" from {entry.From}" : "");
                html.Append("<li class=\"row\">")
                    .Append($"<div class=\"sub\">{E(PageTime.Format(entry.At, this._zone))}</div>")
                    .Append($"<div class=\"sub\">{E(OperatorActions.ToolWords(entry.Tool, null) ?? entry.Tool)}</div>")
                    .Append($"<div>{E(who)}</div>")
                    .Append($"<div{(entry.Done ? "" : " class=\"refused\"")}>{E(Hosting.BanCommand.AuditWords(entry))}</div>")
                    .Append("</li>");
            }

            html.Append("</ul>");
        }

        html.Append("</details>");
        html.Append("</main>");

        // The test alert, and the time zone.
        html.Append("<footer>");
        if (this._alerts.On) {
            html.Append("<form method=\"post\" action=\"/test-alert\">")
                .Append($"<input type=\"hidden\" name=\"token\" value=\"{E(this.Tokens.Make(login, TestAlertAction))}\">")
                .Append("<button type=\"submit\" class=\"quiet\">Send a test alert</button></form>");
        } else {
            html.Append("<span>Discord alerts are off.</span>");
        }

        html.Append($"<span>{E(PageTime.Legend(this._zone, this._time.GetUtcNow()))}</span>");
        html.Append("</footer>");
        return Document("LookingGlass operator", html.ToString());
    }

    private string OperatorRow(ListedOperator one) {
        var name = one.User != null ? $"<strong>{E($"{one.User.Name}@{one.User.WorldName}")}</strong> ({one.UserId.ToString(CultureInfo.InvariantCulture)})"
            : $"user {one.UserId.ToString(CultureInfo.InvariantCulture)}";
        var row = new StringBuilder("<li class=\"row\">").Append($"<div>{name}</div>");
        var id = one.UserId.ToString(CultureInfo.InvariantCulture);
        switch (one.Status) {
            case OperatorStatus.Operator:
                row.Append($"<div>Operator. Keys <span class=\"fp\">{E(one.CurrentFingerprint)}</span>, pinned {E(this.Pinned(one.Pin!))}.</div>");
                break;
            case OperatorStatus.KeysChanged:
                row.Append($"<div><span class=\"warn\">NOT an operator now</span>: its keys changed{E(this.KeysChangedWhen(one))}.<br>")
                    .Append($"Pinned: <span class=\"fp\">{E(one.PinnedFingerprint)}</span><br>")
                    .Append($"Now: <span class=\"fp\">{E(one.CurrentFingerprint)}</span></div>")
                    .Append($"<div class=\"act\"><a class=\"button\" href=\"/pin?user={id}\">Restore operator status...</a></div>");
                break;
            case OperatorStatus.NotPinned:
                row.Append($"<div>Not pinned yet. Keys now <span class=\"fp\">{E(one.CurrentFingerprint)}</span></div>")
                    .Append($"<div class=\"act\"><a class=\"button\" href=\"/pin?user={id}\">Pin...</a></div>");
                break;
            default:
                row.Append("<div class=\"sub\">Not registered on this server, so it has no keys to pin yet.</div>");
                break;
        }

        return row.Append("</li>").ToString();
    }

    /// <summary>The confirmation page for a ban the rules allow (as <see cref="OperatorActions.CheckBan"/> found it).</summary>
    private string BanPage(string login, OperatorResult check, int? days, string reason, bool force) {
        var whom = check.UserId?.ToString(CultureInfo.InvariantCulture) ?? check.Address!;
        var name = this.ShortName(check.UserId, check.Address);
        var user = check.UserId is { } userId ? this._db.GetUser(userId) : null;
        var now = this.Now;
        var existing = this._db.GetActiveBans(now).FirstOrDefault(ban => ban.UserId == check.UserId && ban.Address == check.Address);
        var html = new StringBuilder($"<main class=\"confirm\"><h1>Ban {E(name)}?</h1><dl>");
        if (check.UserId is { } id) {
            var lodestone = id > 0 ? $" ({LodestoneLink(id)})" : "";
            html.Append(user != null
                ? $"<dt>Character</dt><dd>{E($"{user.Name}@{user.WorldName}")}, Lodestone ID {id.ToString(CultureInfo.InvariantCulture)}{lodestone}</dd>"
                : $"<dt>Character</dt><dd>Lodestone ID {id.ToString(CultureInfo.InvariantCulture)}{lodestone}, not registered on this server: the ban stops it registering.</dd>");
        } else {
            html.Append($"<dt>Address</dt><dd>{E(check.Address)} ({E(AddressCount(check.Address!))})</dd>");
        }

        html.Append(days is { } count
            ? $"<dt>For</dt><dd>{E(Days(count))}, until {E(PageTime.Format(now + count * 86400L, this._zone))}</dd>"
            : "<dt>For</dt><dd>Until you lift it</dd>");
        html.Append(reason.Length > 0
            ? $"<dt>Reason, which the player is shown</dt><dd>{E(reason)}</dd>"
            : "<dt>Reason</dt><dd>None: the player is told only that this server's operator blocked them.</dd>");
        html.Append("</dl>");
        if (existing != null) {
            html.Append($"<p>It is banned already ({E(this.Until(existing))}, made {E(Made(existing))}): this replaces that ban.</p>");
        }

        if (force) {
            html.Append($"<p class=\"warn\">{E(check.Problem ?? "You ticked: this is the server's own or its proxy's address.")} You ticked that you mean it: " +
                        "when the proxy's forwarded address is missing, this shuts out every player.</p>");
        }

        html.Append(check.UserId != null
            ? "<p>They can't sign in, or register this character again with any keys, until the ban ends. Their connection closes now. Their places in " +
              "channels stay; channel admins aren't told.</p>"
            : $"<p>Everyone connecting from {E(check.Address)} ({E(AddressCount(check.Address!))}) is shut out, including people who share it. Their " +
              "connections close now.</p>");
        html.Append("<form method=\"post\" action=\"/ban/confirm\">")
            .Append($"<input type=\"hidden\" name=\"token\" value=\"{E(this.Tokens.Make(login, BanAction(whom, days, reason, force)))}\">")
            .Append($"<input type=\"hidden\" name=\"whom\" value=\"{E(whom)}\">")
            .Append($"<input type=\"hidden\" name=\"days\" value=\"{days?.ToString(CultureInfo.InvariantCulture) ?? ""}\">")
            .Append($"<input type=\"hidden\" name=\"reason\" value=\"{E(reason)}\">")
            .Append($"<input type=\"hidden\" name=\"force\" value=\"{(force ? "yes" : "")}\">")
            .Append($"<div class=\"buttons\"><a class=\"button quiet\" href=\"/\">Cancel</a><button type=\"submit\" class=\"danger\">Ban {E(name)}</button></div>")
            .Append("</form></main>");
        return Document($"Ban {name}?", this.Header(login) + html);
    }

    /// <summary>The confirmation page for lifting a ban in force (a GET, which changes nothing).</summary>
    private string LiftPage(string login, StringValues banText) {
        var now = this.Now;
        var ban = long.TryParse(banText.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var banId)
            ? this._db.GetActiveBans(now).FirstOrDefault(row => row.BanId == banId)
            : null;
        if (ban == null) {
            return this.MainPage(login, new PageState { Error = "That ban isn't in force any more (it ended, or was lifted or replaced)." });
        }

        var name = this.ShortName(ban.UserId, ban.Address);
        var made = ban.Automatic && ban.MadeVia is null or BanTools.Automatic
            ? $"Blocked automatically at {PageTime.Format(ban.CreatedAt, this._zone)}, {this.Until(ban)}."
            : $"Made {PageTime.Format(ban.CreatedAt, this._zone)} {Made(ban)}, {this.Until(ban)}.";
        var html = new StringBuilder($"<main class=\"confirm\"><h1>Lift the ban on {E(name)}?</h1>")
            .Append($"<p>{E(made)}</p>")
            .Append(ban.Reason.Length > 0 ? $"<p>Reason: {E(ban.Reason)}</p>" : "<p>No reason was given.</p>")
            .Append(ban.UserId != null
                ? "<p>They can sign in again at once; their plugin tries again by itself within 5 minutes (or at once with \"Try again now\").</p>"
                : $"<p>Connections from {E(ban.Address)} are let in again at once.</p>")
            .Append("<form method=\"post\" action=\"/lift/confirm\">")
            .Append($"<input type=\"hidden\" name=\"token\" value=\"{E(this.Tokens.Make(login, LiftAction(ban.BanId)))}\">")
            .Append($"<input type=\"hidden\" name=\"ban\" value=\"{ban.BanId.ToString(CultureInfo.InvariantCulture)}\">")
            .Append("<div class=\"buttons\"><a class=\"button quiet\" href=\"/\">Cancel</a><button type=\"submit\">Lift the ban</button></div>")
            .Append("</form></main>");
        return Document($"Lift the ban on {name}?", this.Header(login) + html);
    }

    /// <summary>The confirmation page for pinning a listed character's keys, or restoring it after they changed (a GET, which changes nothing).</summary>
    private string PinPage(string login, StringValues userText) {
        var one = long.TryParse(userText.ToString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var userId)
            ? this._actions.Operators().FirstOrDefault(listed => listed.UserId == userId)
            : null;
        if (one == null) {
            return this.MainPage(login, new PageState { Error = "That character isn't listed in LookingGlass:Operators:Characters, so it can't be pinned here." });
        }

        if (one.User == null) {
            return this.MainPage(login, new PageState { Error = $"user {one.UserId.ToString(CultureInfo.InvariantCulture)} isn't registered on this server, so it has no keys to pin yet." });
        }

        var name = $"{one.User.Name}@{one.User.WorldName}";
        if (one.Status == OperatorStatus.Operator) {
            return this.MainPage(login, new PageState { Done = $"{name} is an operator, with keys {one.CurrentFingerprint}: nothing to pin." });
        }

        var restore = one.Status == OperatorStatus.KeysChanged;
        var keys = one.User.Keys.Hash;
        var version = one.User.KeyVersion.ToString(CultureInfo.InvariantCulture);
        var html = new StringBuilder($"<main class=\"confirm\"><h1>{(restore ? "Restore operator status for" : "Pin operator keys for")} {E(name)}?</h1>");
        if (restore) {
            html.Append($"<p>Its keys changed{E(this.KeysChangedWhen(one))}: the character was registered again through the Lodestone (a new computer, " +
                        "\"Reset my identity\", or recovering it). Since then this server hasn't treated it as an operator.</p>");
        }

        html.Append("<dl>");
        if (restore) {
            html.Append($"<dt>Pinned keys</dt><dd class=\"fp\">{E(one.PinnedFingerprint)}</dd>");
        }

        html.Append($"<dt>Keys now (key {E(version)})</dt><dd class=\"fp\">{E(one.CurrentFingerprint)}</dd></dl>")
            .Append("<p>Before you confirm: on your own computer, open LookingGlass's Settings with Advanced mode on, and check that the fingerprint under " +
                    "\"Your identity\" is exactly the \"Keys now\" one above.</p>")
            .Append(restore
                ? "<p>If it isn't, or you didn't register this character again, don't restore: someone else did, which only needs control of its " +
                  "Lodestone page (its Square Enix account). Secure that account, register the character again from your own computer, then come back here.</p>"
                : "<p>If it isn't, don't pin: someone else registered this character, which only needs control of its Lodestone page (its Square Enix " +
                  "account). Secure that account, register the character again from your own computer, then come back here.</p>")
            .Append("<form method=\"post\" action=\"/pin/confirm\">")
            .Append($"<input type=\"hidden\" name=\"token\" value=\"{E(this.Tokens.Make(login, PinAction(one.UserId, keys)))}\">")
            .Append($"<input type=\"hidden\" name=\"user\" value=\"{one.UserId.ToString(CultureInfo.InvariantCulture)}\">")
            .Append($"<input type=\"hidden\" name=\"keys\" value=\"{Convert.ToHexString(keys)}\">")
            .Append($"<div class=\"buttons\"><a class=\"button quiet\" href=\"/\">Cancel</a><button type=\"submit\">{(restore ? "Restore operator status" : "Pin these keys")}</button></div>")
            .Append("</form></main>");
        return Document($"{(restore ? "Restore operator status for" : "Pin operator keys for")} {name}?", this.Header(login) + html);
    }

    /// <summary>A short page saying something (not found, not done), with the way back.</summary>
    private string Message(string login, string title, string text) =>
        Document(title, this.Header(login) + $"<main class=\"confirm\"><h1>{E(title)}</h1><p>{E(text)}</p><p><a class=\"button\" href=\"/\">Back to the page</a></p></main>");

    private string Header(string login) =>
        "<header><h1>LookingGlass operator</h1>" +
        $"<p class=\"meta\">Server {E(RequestHandler.ServerVersion)}, up {E(Uptime(this._time.GetUtcNow() - this._started))} · Signed in: {E(login)}</p>" +
        "<a class=\"button quiet\" href=\"/\">Refresh</a></header>";

    // ================================================================ words

    /// <summary>"Bob Hatter@Lich (31337) Lodestone", "user 31337 Lodestone", "address 203.0.113.9", as HTML.</summary>
    private string Who(long? userId, string? address, IReadOnlyDictionary<long, UserRow> users) {
        if (userId is { } id) {
            var name = users.TryGetValue(id, out var user)
                ? $"<strong>{E($"{user.Name}@{user.WorldName}")}</strong> ({id.ToString(CultureInfo.InvariantCulture)})"
                : $"<strong>user {id.ToString(CultureInfo.InvariantCulture)}</strong>";
            return id > 0 ? $"{name} · {LodestoneLink(id)}" : name;
        }

        return $"<strong>address {E(address)}</strong>";
    }

    /// <summary>"Bob Hatter@Lich", "user 31337" (not registered), "address 203.0.113.0/24": as the page's titles and lines name them.</summary>
    private string ShortName(long? userId, string? address) =>
        userId is { } id ? this._db.GetUser(id) is { } user ? $"{user.Name}@{user.WorldName}" : $"user {id.ToString(CultureInfo.InvariantCulture)}"
        : $"address {address}";

    private static string LodestoneLink(long userId) =>
        $"<a href=\"{AlertWords.Lodestone(userId)}\" rel=\"noreferrer noopener\" target=\"_blank\">Lodestone</a>";

    /// <summary>"until 2026-10-17 15:02 BST (7 days)", or "until lifted".</summary>
    private string Until(BanRow ban) =>
        ban.ExpiresAt is { } ends ? $"until {PageTime.Format(ends, this._zone)} ({Span(ends - ban.CreatedAt)})" : "until lifted";

    /// <summary>"on the web page by alice@github", "automatically", "by an older version of the server".</summary>
    private static string Made(BanRow ban) => OperatorActions.MadeWords(ban) ?? "by an older version of the server";

    private string Pinned(OperatorPinRow pin) =>
        $"{PageTime.Format(pin.PinnedAt, this._zone)} {OperatorActions.ToolWords(pin.PinnedVia, pin.PinnedBy)}";

    /// <summary>" on 2026-10-10 at 14:02 BST", or "" when it isn't known (keys registered before schema 12) or is before the pin.</summary>
    private string KeysChangedWhen(ListedOperator one) =>
        one.User?.KeysRegisteredAt is { } at && (one.Pin == null || at >= one.Pin.PinnedAt)
            ? $" on {PageTime.Date(at, this._zone)} at {PageTime.Format(at, this._zone)[11..]}"
            : one.Pin != null ? $" since they were pinned ({PageTime.Format(one.Pin.PinnedAt, this._zone)})" : "";

    /// <summary>"256 addresses", "one address", "a /64: one household's or device's whole network, usually".</summary>
    private static string AddressCount(string prefix) {
        var slash = prefix.IndexOf('/');
        var ipv6 = prefix.Contains(':');
        var length = slash < 0 ? (ipv6 ? 64 : 32) : int.Parse(prefix[(slash + 1)..], CultureInfo.InvariantCulture);
        if (!ipv6) {
            var count = 1L << (32 - length);
            return count == 1 ? "one address" : $"{count.ToString("N0", CultureInfo.InvariantCulture)} addresses";
        }

        return length == 64
            ? "a /64: usually one household's or device's whole network"
            : $"a /{length.ToString(CultureInfo.InvariantCulture)}: {(1L << (64 - length)).ToString("N0", CultureInfo.InvariantCulture)} networks of a /64 each";
    }

    private static string Days(int days) => days == 1 ? "1 day" : $"{days.ToString("N0", CultureInfo.InvariantCulture)} days";

    private static string Span(long seconds) =>
        seconds >= 86400 && seconds % 86400 == 0 ? Count(seconds / 86400, "day")
        : seconds >= 3600 && seconds % 3600 == 0 ? Count(seconds / 3600, "hour")
        : Count(Math.Max(1, (seconds + 30) / 60), "minute");

    private static string Uptime(TimeSpan uptime) =>
        uptime < TimeSpan.FromHours(1) ? Count(Math.Max(0, (long) uptime.TotalMinutes), "minute")
        : uptime < TimeSpan.FromDays(2) ? Count((long) uptime.TotalHours, "hour")
        : Count((long) uptime.TotalDays, "day");

    private static string Count(long count, string one, string? many = null) =>
        count == 1 ? $"1 {one}" : $"{count.ToString("N0", CultureInfo.InvariantCulture)} {many ?? one + "s"}";

    // ================================================================ HTML

    /// <summary>Text from outside as HTML: anything hidden in it shown as escapes first, then encoded (names, worlds, reasons, limits, logins).</summary>
    public static string E(string? text) => System.Text.Encodings.Web.HtmlEncoder.Default.Encode(VisibleText.Of(text));

    /// <summary>Text from outside as the log shows it: visible, and cut.</summary>
    private static string Visible(string? text) => "\"" + VisibleText.Of(text, 100) + "\"";

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..length];

    private static string Document(string title, string body, bool styled = true) =>
        "<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n" +
        "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<meta name=\"color-scheme\" content=\"light dark\">\n" +
        $"<title>{E(title)}</title>\n" +
        (styled ? "<link rel=\"stylesheet\" href=\"/style.css\">\n" : "") +
        $"</head>\n<body>\n{body}\n</body>\n</html>\n";

    private static async Task Write(HttpResponse response, int status, string html) {
        response.StatusCode = status;
        response.ContentType = "text/html; charset=utf-8";
        await response.WriteAsync(html);
    }

    private static string LoadStylesheet() {
        using var stream = typeof(OperatorPage).Assembly.GetManifestResourceStream("operator.css")
                           ?? throw new InvalidOperationException("The operator web page's stylesheet is missing from the build.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
