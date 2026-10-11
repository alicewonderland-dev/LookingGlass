using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace LookingGlass.Server.Services;

/// <summary>
/// Posts alerts to a Discord webhook, on a task of its own, so a slow or failing Discord never delays the server: alerts go
/// into a bounded queue (<see cref="QueueLength"/>; past it they are counted as not delivered), and are posted from there.
/// Alerts within <see cref="BatchWindow"/> of the first waiting go as one message; an urgent one, or one to go at once, goes
/// straight away with whatever is waiting, but never sooner than <see cref="MinGap"/> after the last message (Discord allows
/// a webhook a few a second), so a burst becomes one message. An urgent alert anyone on the internet can set off (an automatic
/// block, a request through Funnel: <see cref="Alert.Public"/>) goes at once, and pings, only once every
/// <see cref="PublicPingEvery"/>; between, such alerts wait for the minute's message, without a ping, so nobody outside can make the
/// role ping again and again, or fill the channel. The other urgent ones (an operator's keys) always go at once and ping. Alerts
/// waiting for the minute are kept to <see cref="QueueLength"/> too. A message holds at most 2,000 characters (see
/// <see cref="AlertWords.Message"/>). Every message has <c>allowed_mentions</c> empty, or naming only the ping role when it
/// holds an urgent alert, and link previews off (the suppress-embeds flag). A 429's <c>Retry-After</c> is honoured; other
/// failures are tried a few times, then counted (<see cref="Undelivered"/>) and logged at most every
/// <see cref="FailureLogEvery"/>, never with the webhook's address.
/// </summary>
public sealed class AlertPoster {
    /// <summary>Alerts waiting at most; past it, new ones are counted as not delivered.</summary>
    public const int QueueLength = 1000;

    /// <summary>How often a failure to post is logged, at most.</summary>
    public static readonly TimeSpan FailureLogEvery = TimeSpan.FromMinutes(10);

    /// <summary>How often an alert the public can set off (<see cref="Alert.Public"/>) may go at once and ping, at most.</summary>
    public static readonly TimeSpan PublicPingEvery = TimeSpan.FromMinutes(10);

    /// <summary>The longest <c>Retry-After</c> waited for; a longer one counts as a failure.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(15);

    // Discord's suppress-embeds message flag: no link previews, so Discord fetches neither the Lodestone nor anything else.
    private const int SuppressEmbeds = 1 << 2;

    // Waits between tries after a failure that isn't a 429 (a 5xx, a timeout, no connection); then it is given up.
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)];

    // Times a 429 is honoured for one message before it is given up.
    private const int MaxRateLimitedTries = 5;

    private readonly string _url;
    private readonly string? _roleId;
    private readonly Func<HttpClient> _http;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Channel<Alert> _queue = Channel.CreateBounded<Alert>(new BoundedChannelOptions(QueueLength) { SingleReader = true });
    private int _undelivered;
    private int _undeliveredSinceTaken;
    private long _failureLogged = long.MinValue;
    private bool _failing;
    // When an alert the public can set off last went at once (and pinged); MinValue: never.
    private DateTimeOffset _publicPinged = DateTimeOffset.MinValue;

    /// <param name="url">The webhook (checked at startup: see <see cref="AlertOptions.Problem"/>). Never logged.</param>
    /// <param name="roleId">The role urgent alerts ping, or null.</param>
    /// <param name="http">Gives the client to post with (one for each message, as the factory hands them out).</param>
    public AlertPoster(string url, string? roleId, Func<HttpClient> http, ILogger logger, TimeProvider? time = null) {
        this._url = url;
        this._roleId = roleId;
        this._http = http;
        this._logger = logger;
        this._time = time ?? TimeProvider.System;
        this.RetryDelay = (wait, ct) => Task.Delay(wait, this._time, ct);
    }

    /// <summary>How long alerts wait for others to go with them.</summary>
    internal TimeSpan BatchWindow { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>The least time between two messages.</summary>
    internal TimeSpan MinGap { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How a wait before trying again is made (tests skip it, and note how long it was).</summary>
    internal Func<TimeSpan, CancellationToken, Task> RetryDelay { get; init; }

    /// <summary>Alerts that couldn't be posted since the server started (a failed message, or a full queue).</summary>
    public int Undelivered => Volatile.Read(ref this._undelivered);

    /// <summary>Messages posted so far.</summary>
    public int Posted => Volatile.Read(ref this._posted);

    private int _posted;

    /// <summary>The alerts not delivered since this was last asked (for the daily summary), and starts counting again.</summary>
    public int TakeUndelivered() => Interlocked.Exchange(ref this._undeliveredSinceTaken, 0);

    /// <summary>Queues an alert. Never blocks and never throws: past <see cref="QueueLength"/> waiting, it is counted as not delivered.</summary>
    public void Post(Alert alert) {
        if (!this._queue.Writer.TryWrite(alert)) {
            this.CountUndelivered(1);
            this.LogFailure($"more than {QueueLength} alerts were waiting to be posted");
        }
    }

    /// <summary>Posts what is queued until <paramref name="stopping"/>; then, for a few seconds more, what is still waiting.</summary>
    public async Task RunAsync(CancellationToken stopping) {
        var waiting = new List<(Alert Alert, DateTimeOffset At)>();
        var lastSent = DateTimeOffset.MinValue;
        try {
            while (!stopping.IsCancellationRequested) {
                if (waiting.Count == 0 && !await this._queue.Reader.WaitToReadAsync(stopping)) {
                    return;
                }

                var now = this._time.GetUtcNow();
                while (this._queue.Reader.TryRead(out var alert)) {
                    // What waits for the minute is kept to the queue's length too; what goes at once never waits long.
                    if (waiting.Count >= QueueLength && !this.GoesAtOnce(alert, now)) {
                        this.CountUndelivered(1);
                        this.LogFailure($"more than {QueueLength} alerts were waiting to be posted");
                        continue;
                    }

                    waiting.Add((alert, now));
                }

                if (waiting.Count == 0) {
                    continue;
                }

                var due = waiting.Any(entry => this.GoesAtOnce(entry.Alert, now))
                    ? (lastSent == DateTimeOffset.MinValue ? now : lastSent + this.MinGap)
                    : waiting.Min(entry => entry.At) + this.BatchWindow;
                if (due > now && !await this.WaitUntilDueOrMoreAsync(due - now, stopping)) {
                    // More came (perhaps urgent): looked at again.
                    continue;
                }

                // Cleared once posted (or given up): stopping in the middle of it, it is tried once more below.
                await this.SendAsync(waiting.Select(entry => entry.Alert).ToList(), stopping);
                waiting.Clear();
                lastSent = this._time.GetUtcNow();
            }
        } catch (OperationCanceledException) when (stopping.IsCancellationRequested) {
            // Stopping: what is still waiting goes now.
        } finally {
            while (this._queue.Reader.TryRead(out var alert)) {
                waiting.Add((alert, this._time.GetUtcNow()));
            }

            if (waiting.Count > 0) {
                using var last = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try {
                    await this.SendAsync(waiting.Select(entry => entry.Alert).ToList(), last.Token, finalTry: true);
                } catch (OperationCanceledException) {
                    this.CountUndelivered(waiting.Count);
                }
            }
        }
    }

    /// <summary>Posts everything queued now, as one message, at once, when <see cref="RunAsync"/> isn't running: for tests.</summary>
    internal async Task FlushAsync(CancellationToken ct) {
        var batch = new List<Alert>();
        while (this._queue.Reader.TryRead(out var alert)) {
            batch.Add(alert);
        }

        if (batch.Count > 0) {
            await this.SendAsync(batch, ct);
        }
    }

    /// <summary>
    /// Whether an alert goes at once rather than with the minute's message: one to go at once, or an urgent one, unless it is one the
    /// public can set off and one of those went at once less than <see cref="PublicPingEvery"/> ago.
    /// </summary>
    private bool GoesAtOnce(Alert alert, DateTimeOffset now) =>
        alert.Kind == AlertKind.AtOnce || (alert.Urgent && (!alert.Public || this.PublicPingFree(now)));

    private bool PublicPingFree(DateTimeOffset now) => this._publicPinged == DateTimeOffset.MinValue || now - this._publicPinged >= PublicPingEvery;

    /// <returns>True once <paramref name="wait"/> has passed; false if more alerts came first.</returns>
    private async Task<bool> WaitUntilDueOrMoreAsync(TimeSpan wait, CancellationToken stopping) {
        using var either = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var more = this._queue.Reader.WaitToReadAsync(either.Token).AsTask();
        var due = Task.Delay(wait, this._time, either.Token);
        var first = await Task.WhenAny(more, due);
        await either.CancelAsync();
        stopping.ThrowIfCancellationRequested();
        if (first == more && more.IsCompletedSuccessfully && more.Result) {
            return false;
        }

        return true;
    }

    /// <summary>Posts one message holding <paramref name="batch"/>, trying again as Discord asks, or a few times after a failure.</summary>
    private async Task SendAsync(IReadOnlyList<Alert> batch, CancellationToken ct, bool finalTry = false) {
        // An urgent alert pings, unless every urgent one here is one the public can set off and such a ping went out lately.
        var now = this._time.GetUtcNow();
        var ping = batch.Any(alert => alert.Urgent && (!alert.Public || this.PublicPingFree(now)));
        if (ping && batch.Any(alert => alert.Urgent && alert.Public)) {
            this._publicPinged = now;
        }

        var content = AlertWords.Message(batch, this._roleId, ping, out var pinged, out _);
        var body = Payload(content, pinged ? this._roleId : null);
        var failures = 0;
        var rateLimited = 0;
        while (true) {
            string why;
            try {
                var json = new ByteArrayContent(body);
                json.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                using var request = new HttpRequestMessage(HttpMethod.Post, this._url) { Content = json };
                using var response = await this._http().SendAsync(request, ct);
                if (response.IsSuccessStatusCode) {
                    Interlocked.Increment(ref this._posted);
                    if (this._failing) {
                        this._failing = false;
                        this._logger.LogInformation("Alerts reach Discord again ({Count} couldn't be posted since the server started)", this.Undelivered);
                    }

                    return;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests && !finalTry && ++rateLimited <= MaxRateLimitedTries) {
                    var wait = await RetryAfterAsync(response, ct);
                    if (wait <= MaxRetryAfter) {
                        await this.RetryDelay(wait, ct);
                        continue;
                    }

                    why = $"Discord asked to wait {wait.TotalMinutes:0} minutes before posting again (rate limited)";
                } else if ((int) response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout) {
                    why = $"Discord answered {(int) response.StatusCode} {response.ReasonPhrase}";
                    if (!finalTry && failures < Backoff.Length) {
                        await this.RetryDelay(Backoff[failures++], ct);
                        continue;
                    }
                } else {
                    // Another 4xx won't get better by trying again: most likely the webhook was deleted (401, 404), or the message refused.
                    why = $"Discord answered {(int) response.StatusCode} {response.ReasonPhrase}" +
                          (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized
                              ? "; the webhook may have been deleted: make a new one and put it in LookingGlass:Alerts:DiscordWebhookUrl (see \"Alerts to Discord\" in docs/server.md)"
                              : "");
                }
            } catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested) {
                // No connection, or no answer in time. The exception isn't logged as such: its message is scrubbed of the webhook first.
                why = ex is TaskCanceledException ? "Discord didn't answer in time" : $"Discord couldn't be reached ({this.Scrub(ex.Message)})";
                if (!finalTry && failures < Backoff.Length) {
                    await this.RetryDelay(Backoff[failures++], ct);
                    continue;
                }
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                // Anything else (none is expected) gives up this message, never the posting: only its kind is logged, as above.
                why = $"posting failed ({ex.GetType().Name})";
            }

            this.CountUndelivered(batch.Count);
            this.LogFailure(why);
            return;
        }
    }

    /// <summary>The message as Discord's webhook takes it: the text, whom it may mention (nobody, or the ping role), and no previews.</summary>
    internal static byte[] Payload(string content, string? pingRole) {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer)) {
            json.WriteStartObject();
            json.WriteString("content", content);
            json.WriteStartObject("allowed_mentions");
            json.WriteStartArray("parse");
            json.WriteEndArray();
            json.WriteStartArray("roles");
            if (pingRole != null) {
                json.WriteStringValue(pingRole);
            }

            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteNumber("flags", SuppressEmbeds);
            json.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>How long a 429 asks to wait: its Retry-After header (seconds), or its body's retry_after; 5 seconds if neither says.</summary>
    private static async Task<TimeSpan> RetryAfterAsync(HttpResponseMessage response, CancellationToken ct) {
        if (response.Headers.RetryAfter?.Delta is { } delta) {
            return delta;
        }

        if (response.Headers.TryGetValues("Retry-After", out var values)
            && double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0) {
            return TimeSpan.FromSeconds(seconds);
        }

        try {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (document.RootElement.TryGetProperty("retry_after", out var after) && after.TryGetDouble(out var bodySeconds) && bodySeconds >= 0) {
                return TimeSpan.FromSeconds(bodySeconds);
            }
        } catch (JsonException) {
            // Not JSON: the default below.
        }

        return TimeSpan.FromSeconds(5);
    }

    private void CountUndelivered(int count) {
        Interlocked.Add(ref this._undelivered, count);
        Interlocked.Add(ref this._undeliveredSinceTaken, count);
    }

    /// <summary>Logs a failure, at most every <see cref="FailureLogEvery"/>, without the webhook's address.</summary>
    private void LogFailure(string why) {
        this._failing = true;
        var now = this._time.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref this._failureLogged);
        if ((last != long.MinValue && now - last < FailureLogEvery.Ticks) || Interlocked.CompareExchange(ref this._failureLogged, now, last) != last) {
            return;
        }

        this._logger.LogWarning(
            "Couldn't post an alert to Discord: {Why}. {Count} alerts couldn't be posted since the server started (said once every {Minutes} minutes at most)",
            this.Scrub(why), this.Undelivered, (int) FailureLogEvery.TotalMinutes);
    }

    /// <summary>Text with the webhook's address and its token taken out, in case an exception's message holds them.</summary>
    private string Scrub(string text) {
        var scrubbed = new StringBuilder(text).Replace(this._url, "<the webhook>");
        var token = this._url[(this._url.LastIndexOf('/') + 1)..];
        if (token.Length > 0) {
            scrubbed.Replace(token, "<the webhook's token>");
        }

        return scrubbed.ToString();
    }
}
