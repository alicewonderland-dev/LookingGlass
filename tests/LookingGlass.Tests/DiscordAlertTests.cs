using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using LookingGlass.Core.Crypto;
using LookingGlass.Server;
using LookingGlass.Server.Data;
using LookingGlass.Server.Realtime;
using LookingGlass.Server.Services;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// Discord alerts (step 2 of docs/operator-tools-plan.md), against a fake webhook: the settings (a webhook of the right shape,
/// never quoted), what makes names and reasons safe to post, addresses shortened, batching within a minute and urgent alerts
/// at once, <c>Retry-After</c>, failures counted and logged once every ten minutes, the 2,000-character cut,
/// <c>allowed_mentions</c> naming only the ping role and only on urgent alerts, each ban and lift posted once by the server
/// whichever process made it (and what was done before alerts were on marked, not posted), replacements posted once, pins
/// and restores, an operator character signing in with other keys once per set of keys, the restart alert, and the daily
/// summary's hour, quiet day and counts.
/// </summary>
public sealed class DiscordAlertTests : IDisposable {
    private const long Bob = 31337;
    private const long Carol = 4242;
    private const long Alice = 55555;
    private const long Rabbit = 66666;
    private const string Role = "424242424242424242";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lgt-alerts-" + Guid.NewGuid().ToString("N"));
    private readonly Database _db;
    private readonly StoppedClock _clock = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly FakeDiscord _discord = new();

    public DiscordAlertTests() {
        Directory.CreateDirectory(this._directory);
        this._db = new Database(Path.Combine(this._directory, "lookingglass.db"));
        foreach (var (id, name, world) in new[] { (Bob, "Bob Hatter", "Lich"), (Carol, "Carol Queen", "Odin"), (Alice, "Alice Wonderland", "Lich"), (Rabbit, "Alice Rabbit", "Odin") }) {
            this.Register(id, name, world);
        }
    }

    public void Dispose() => DeleteDirectory(this._directory);

    private UserRow Register(long id, string name, string world) {
        using var keys = IdentityKeys.Generate();
        return this._db.RegisterUser(id, name, 73, world, keys.ToBundle(), false).User;
    }

    private ILogger<T> Logger<T>() => LoggerFactory.Create(logging => logging.AddProvider(this._logs)).CreateLogger<T>();

    private long Now => this._clock.GetUtcNow().ToUnixTimeSeconds();

    private void SetClock(DateTimeOffset at) => this._clock.Advance(at - this._clock.GetUtcNow());

    private static ServerOptions Settings(bool on = true, string? role = Role, int hour = 9, bool restarts = true) => new() {
        Operators = { Characters = [Alice.ToString(), Rabbit.ToString()] },
        Alerts = { DiscordWebhookUrl = on ? FakeDiscord.Url : "", PingRoleId = role ?? "", DailySummaryHourUtc = hour, Restarts = restarts },
    };

    /// <summary>The server's alerts, started (as at a server's start), posting to the fake webhook; nothing runs by itself.</summary>
    private ServerAlerts Alerts(ServerOptions? settings = null) {
        var alerts = new ServerAlerts(this._db, settings ?? Settings(), () => new HttpClient(this._discord, disposeHandler: false), null, "Production",
            this.Logger<ServerAlerts>(), this._clock);
        alerts.StartUp();
        return alerts;
    }

    /// <summary>One look in the database, then everything queued posted at once: what was posted (one message, or none).</summary>
    private async Task<string?> PostedAsync(ServerAlerts alerts) {
        var before = this._discord.Posts.Count;
        alerts.Tick();
        await alerts.Poster!.FlushAsync(Ct);
        var posted = this._discord.Posts.Skip(before).ToList();
        Assert.True(posted.Count <= 1);
        return posted.SingleOrDefault()?.Content;
    }

    private OperatorActions Actions() => new(this._db, new OperatorSettings(new AbuseOptions(), [], [Alice, Rabbit]), this._clock);

    private static readonly OperatorActor CommandLine = new(OperatorTool.CommandLine, "unixuser");
    private static readonly OperatorActor WebPage = new(OperatorTool.WebPage, "someone@github", "100.101.102.103");
    private static readonly OperatorActor Plugin = new(OperatorTool.Plugin, "Alice Wonderland@Lich", UserId: Alice);

    private AlertPoster Poster(string? role = Role, TimeProvider? time = null, TimeSpan? batch = null, List<TimeSpan>? waits = null) =>
        new(FakeDiscord.Url, role, () => new HttpClient(this._discord, disposeHandler: false), this.Logger<AlertPoster>(), time ?? this._clock) {
            BatchWindow = batch ?? TimeSpan.FromMinutes(1),
            MinGap = TimeSpan.Zero,
            RetryDelay = (wait, _) => {
                waits?.Add(wait);
                return Task.CompletedTask;
            },
        };

    private static Alert Batched(string text) => new(text, AlertKind.Batched);

    // ================================================================ settings

    /// <summary>
    /// The webhook must be exactly what Discord's Copy Webhook URL gives (https, discord.com, api/webhooks/number/token, nothing
    /// more), and the server says what is wrong without ever quoting it; the ping role is a number; the summary's hour 0 to 23, or -1.
    /// </summary>
    [Theory]
    [InlineData("http://discord.com/api/webhooks/1/Secret-Token", "doesn't start with https://")]
    [InlineData("https://discordapp.com/api/webhooks/1/Secret-Token", "isn't discord.com")]
    [InlineData("https://discord.com.evil.example/api/webhooks/1/Secret-Token", "isn't discord.com")]
    [InlineData("https://discord.com/api/webhooks/1/Secret-Token?wait=true", "api/webhooks/<number>/<token>")]
    [InlineData("https://discord.com/api/webhooks/1/Secret-Token/slack", "api/webhooks/<number>/<token>")]
    [InlineData("https://discord.com/api/webhooks/Secret-Token", "api/webhooks/<number>/<token>")]
    [InlineData("https://discord.com:8443/api/webhooks/1/Secret-Token", "isn't discord.com")]
    public void AWebhookOfTheWrongShapeIsRefusedWithoutQuotingIt(string url, string hint) {
        var problem = new AlertOptions { DiscordWebhookUrl = url }.Problem();
        Assert.NotNull(problem);
        Assert.Contains("LookingGlass:Alerts:DiscordWebhookUrl", problem);
        Assert.Contains("won't start", problem);
        Assert.Contains(hint, problem);
        Assert.DoesNotContain("Secret-Token", problem);
    }

    [Fact]
    public void TheOtherAlertSettingsAreChecked() {
        Assert.Null(new AlertOptions().Problem());
        Assert.False(new AlertOptions().On);
        Assert.Null(new AlertOptions { DiscordWebhookUrl = FakeDiscord.Url }.Problem());
        Assert.Null(new AlertOptions { DiscordWebhookUrl = " " + FakeDiscord.Url + " " }.Problem());
        Assert.True(new AlertOptions { DiscordWebhookUrl = FakeDiscord.Url }.On);
        Assert.Equal((9, true, ""), (new AlertOptions().DailySummaryHourUtc, new AlertOptions().Restarts, new AlertOptions().PingRoleId));
        Assert.Null(new AlertOptions { PingRoleId = Role }.Problem());
        Assert.Contains("PingRoleId", new AlertOptions { PingRoleId = "@ops" }.Problem());
        Assert.Contains("PingRoleId", new AlertOptions { PingRoleId = "0" }.Problem());
        Assert.Contains("PingRoleId", new AlertOptions { PingRoleId = "-5" }.Problem());
        foreach (var hour in new[] { -1, 0, 23 }) {
            Assert.Null(new AlertOptions { DailySummaryHourUtc = hour }.Problem());
        }

        foreach (var hour in new[] { -2, 24 }) {
            Assert.Contains("DailySummaryHourUtc", new AlertOptions { DailySummaryHourUtc = hour }.Problem());
        }
    }

    // ================================================================ safe to post

    /// <summary>
    /// A name or reason can't format the message, link anywhere, make a time stamp or mention anyone: Discord's markdown
    /// characters are escaped, <c>:</c> too (no <c>https://</c> link), every <c>@</c> is followed by a zero-width space, and
    /// control and format characters (a new line, a bidirectional override) are shown as escapes.
    /// </summary>
    [Theory]
    [InlineData("@everyone and @here", "@\u200Beveryone and @\u200Bhere")]
    [InlineData("<@123456789> <@&42> <#7>", "\\<@\u200B123456789\\> \\<@\u200B&42\\> \\<\\#7\\>")]
    [InlineData("**bold** __under__ ~~gone~~ ||spoiler||", "\\*\\*bold\\*\\* \\_\\_under\\_\\_ \\~\\~gone\\~\\~ \\|\\|spoiler\\|\\|")]
    [InlineData("`code` [free gil](https://evil.example)", "\\`code\\` \\[free gil\\](https\\://evil.example)")]
    [InlineData("<t:1760000000:R>", "\\<t\\:1760000000\\:R\\>")]
    [InlineData("back\\slash", "back\\\\slash")]
    [InlineData("two\nlines", "two\\\\u000Alines")]
    [InlineData("\u001B[31mred", "\\\\u001B\\[31mred")]
    [InlineData("\u202Eesrever", "\\\\u202Eesrever")]
    [InlineData("Jean-Luc O'Hara", "Jean-Luc O'Hara")]
    public void NamesAndReasonsAreEscaped(string text, string escaped) {
        Assert.Equal(escaped, AlertWords.Escape(text));
    }

    /// <summary>A whole alert with a hostile name and reason: nothing in it pings, links or formats.</summary>
    [Fact]
    public async Task AHostileNameAndReasonPostNothingTheyShouldnt() {
        this.Register(Carol, "@everyone **Free**", "<@&1>");
        var alerts = this.Alerts();
        this._clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(this.Actions().Ban(CommandLine, Carol.ToString(), 7, "@here https://evil.example ||spoiler|| `run this`").Done);
        var posted = await this.PostedAsync(alerts);
        Assert.NotNull(posted);
        foreach (var forbidden in new[] { "@everyone", "@here", "<@&1>", "**Free", "https://evil", "||spoiler", "`run this`" }) {
            // Ordinal, as Discord reads it (a culture's comparison skips the zero-width space).
            Assert.DoesNotContain(forbidden, posted, StringComparison.Ordinal);
        }

        var message = Assert.Single(this._discord.Posts);
        Assert.Equal((0, 0, 4), (message.Parse, message.Roles.Count, message.Flags));
        Assert.Equal(("POST", FakeDiscord.Url, "application/json"), (message.Method, message.Url, message.ContentType));
    }

    /// <summary>
    /// Addresses are shortened: IPv4 to its /24 (203.0.113.x), IPv6 to its /40, a wider prefix as it is; and the commands an
    /// alert gives never hold the full address.
    /// </summary>
    [Theory]
    [InlineData("203.0.113.9", "address 203.0.113.x (shortened)")]
    [InlineData("203.0.113.0/24", "address 203.0.113.x (shortened)")]
    [InlineData("203.0.113.128/25", "address 203.0.113.x (shortened)")]
    [InlineData("203.0.0.0/16", "address 203.0.0.0/16")]
    [InlineData("2001:db8:ab12:3456::/64", "address 2001:db8:ab00::/40 (shortened)")]
    [InlineData("2001:db8:ab12::/48", "address 2001:db8:ab00::/40 (shortened)")]
    [InlineData("2001:db8::/32", "address 2001:db8::/32")]
    public void AddressesAreShortened(string address, string shown) {
        Assert.Equal(shown, AlertWords.Address(address));
    }

    [Fact]
    public async Task AnAddressBanNeverPostsTheFullAddress() {
        var alerts = this.Alerts();
        this._clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(this.Actions().Ban(CommandLine, "203.0.113.9", 1, "Flooding").Done);
        var made = await this.PostedAsync(alerts);
        Assert.StartsWith("**Ban made** on the command line: address 203.0.113.x (shortened), for 1 day, until <t:", made);
        Assert.Contains("`LG --bans` shows the full address", made);
        Assert.True(this.Actions().Lift(CommandLine, "203.0.113.9").Done);
        var lifted = await this.PostedAsync(alerts);
        Assert.StartsWith("**Ban lifted** on the command line: address 203.0.113.x (shortened). It had been made on the command line, <t:", lifted);
        Assert.DoesNotContain("203.0.113.9", made + lifted);
    }

    // ================================================================ posting

    /// <summary>Alerts within the batching window go as one message, in the order they came.</summary>
    [Fact]
    public async Task AlertsWithinAMinuteGoAsOneMessage() {
        var poster = this.Poster(time: TimeProvider.System, batch: TimeSpan.FromMilliseconds(400));
        using var stop = new CancellationTokenSource();
        var running = poster.RunAsync(stop.Token);
        poster.Post(Batched("first"));
        poster.Post(Batched("second"));
        poster.Post(Batched("third"));
        var posted = await this._discord.WaitForPost(_ => true);
        Assert.Equal("first\n\nsecond\n\nthird", posted.Content);
        await Task.Delay(600, Ct);
        Assert.Single(this._discord.Attempts);
        await stop.CancelAsync();
        await running;
    }

    /// <summary>
    /// An urgent alert goes at once, not waiting out the minute, with what was waiting; it comes first, after the ping role's
    /// mention, and <c>allowed_mentions</c> names that role and nothing else.
    /// </summary>
    [Fact]
    public async Task AnUrgentAlertGoesAtOnceWithWhatWasWaiting() {
        var poster = this.Poster(time: TimeProvider.System, batch: TimeSpan.FromHours(1));
        using var stop = new CancellationTokenSource();
        var running = poster.RunAsync(stop.Token);
        poster.Post(Batched("a flag"));
        await Task.Delay(200, Ct);
        Assert.Empty(this._discord.Attempts);
        poster.Post(new Alert("**Urgent: blocked**", AlertKind.Urgent));
        var posted = await this._discord.WaitForPost(_ => true);
        Assert.Equal($"<@&{Role}> **Urgent: blocked**\n\na flag", posted.Content);
        Assert.Equal((0, Role), (posted.Parse, Assert.Single(posted.Roles)));
        await stop.CancelAsync();
        await running;
    }

    /// <summary>Only an urgent alert pings, and only the role set: other messages, or any without a role set, allow no mention at all.</summary>
    [Fact]
    public async Task OnlyUrgentAlertsMentionTheRole() {
        var withRole = this.Poster();
        withRole.Post(Batched("a ban"));
        withRole.Post(new Alert("the restart", AlertKind.AtOnce));
        await withRole.FlushAsync(Ct);
        var noRole = this.Poster(role: null);
        noRole.Post(new Alert("**Urgent: blocked**", AlertKind.Urgent));
        await noRole.FlushAsync(Ct);
        Assert.Collection(this._discord.Posts,
            posted => Assert.Equal(("a ban\n\nthe restart", 0, 0, 4), (posted.Content, posted.Parse, posted.Roles.Count, posted.Flags)),
            posted => Assert.Equal(("**Urgent: blocked**", 0, 0, 4), (posted.Content, posted.Parse, posted.Roles.Count, posted.Flags)));
    }

    /// <summary>A 429 is waited out as long as its Retry-After (or its body's retry_after) says, then the same message is posted.</summary>
    [Fact]
    public async Task RetryAfterIsHonoured() {
        var waits = new List<TimeSpan>();
        this._discord.Answer = number => number switch {
            1 => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { { "Retry-After", "3" } } },
            2 => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("""{"message": "You are being rate limited.", "retry_after": 1.5, "global": false}""") },
            _ => null,
        };
        var poster = this.Poster(waits: waits);
        poster.Post(Batched("a ban"));
        await poster.FlushAsync(Ct);
        Assert.Equal(new[] { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1.5) }, waits);
        Assert.Equal(3, this._discord.Attempts.Count);
        Assert.All(this._discord.Attempts, attempt => Assert.Equal("a ban", attempt.Content));
        Assert.Single(this._discord.Posts);
        Assert.Equal(0, poster.Undelivered);
    }

    /// <summary>
    /// A message Discord keeps failing is tried a few times, then counted as not delivered; failures are logged once every ten
    /// minutes at most, never with the webhook; a webhook that is gone (404) isn't tried again; and alerts reaching Discord
    /// again is said once.
    /// </summary>
    [Fact]
    public async Task FailuresAreCountedAndLoggedOnceEveryTenMinutes() {
        var waits = new List<TimeSpan>();
        this._discord.Answer = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);
        var poster = this.Poster(waits: waits);
        for (var i = 0; i < 3; i++) {
            poster.Post(Batched($"alert {i}"));
            await poster.FlushAsync(Ct);
        }

        Assert.Equal(9, this._discord.Attempts.Count);
        Assert.Equal(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30) }, waits.Take(2));
        Assert.Equal(3, poster.Undelivered);
        var warning = Assert.Single(this._logs.AtLeast(LogLevel.Warning));
        Assert.Contains("Couldn't post an alert to Discord: Discord answered 502", warning);

        this._clock.Advance(TimeSpan.FromMinutes(10));
        this._discord.Answer = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        poster.Post(Batched("alert 3"));
        await poster.FlushAsync(Ct);
        Assert.Equal(10, this._discord.Attempts.Count);
        Assert.Equal(2, this._logs.AtLeast(LogLevel.Warning).Count);
        Assert.Contains("the webhook may have been deleted", this._logs.AtLeast(LogLevel.Warning)[1]);
        Assert.Equal((4, 4, 0), (poster.Undelivered, poster.TakeUndelivered(), poster.TakeUndelivered()));

        this._discord.Answer = null;
        poster.Post(Batched("alert 4"));
        await poster.FlushAsync(Ct);
        Assert.Contains(this._logs.Entries, entry => entry.Message.Contains("Alerts reach Discord again"));
        Assert.DoesNotContain(this._logs.Entries, entry => entry.Message.Contains(FakeDiscord.Token) || entry.Message.Contains("webhooks/"));
    }

    /// <summary>No connection at all: counted, and the exception's message is logged without the webhook.</summary>
    [Fact]
    public async Task AnUnreachableDiscordIsLoggedWithoutTheWebhook() {
        var poster = new AlertPoster(FakeDiscord.Url, null, () => new HttpClient(new Unreachable()), this.Logger<AlertPoster>(), this._clock) {
            RetryDelay = (_, _) => Task.CompletedTask,
        };
        poster.Post(Batched("a ban"));
        await poster.FlushAsync(Ct);
        Assert.Equal(1, poster.Undelivered);
        var warning = Assert.Single(this._logs.AtLeast(LogLevel.Warning));
        Assert.Contains("Discord couldn't be reached", warning);
        Assert.DoesNotContain(FakeDiscord.Token, warning);
    }

    private sealed class Unreachable : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException($"No such host is known. ({request.RequestUri})");
    }

    /// <summary>
    /// A message past 2,000 characters holds as many alerts as fit and then says how many more there were; urgent ones come
    /// first, so the cut never takes them.
    /// </summary>
    [Fact]
    public async Task ALongMessageIsCutAt2000Characters() {
        var poster = this.Poster();
        for (var i = 0; i < 60; i++) {
            poster.Post(Batched($"**Flagged:** alert number {i:00} " + new string('x', 70)));
        }

        poster.Post(new Alert("**Urgent: blocked automatically:** the last one", AlertKind.Urgent));
        await poster.FlushAsync(Ct);
        var content = Assert.Single(this._discord.Posts).Content;
        Assert.InRange(content.Length, 1500, AlertWords.MaxMessageLength);
        Assert.StartsWith($"<@&{Role}> **Urgent: blocked automatically:** the last one\n\n**Flagged:** alert number 00", content);
        var included = content.Split("**Flagged:**").Length - 1;
        Assert.EndsWith($"\n\nand {60 - included} more: see `LG --bans`, `LG --audit` and `journalctl -u lookingglass`.", content);
        Assert.InRange(included, 10, 25);
    }

    /// <summary>The queue is bounded: past it, alerts are counted as not delivered, and nothing waits.</summary>
    [Fact]
    public void AFullQueueCountsAsNotDelivered() {
        var poster = this.Poster();
        for (var i = 0; i < AlertPoster.QueueLength + 3; i++) {
            poster.Post(Batched("x"));
        }

        Assert.Equal(3, poster.Undelivered);
        Assert.Contains("more than 1000 alerts were waiting", Assert.Single(this._logs.AtLeast(LogLevel.Warning)));
    }

    /// <summary>
    /// What waits for the minute is kept to the queue's length too, while the poster runs: the rest are counted as not delivered,
    /// and an urgent alert still goes at once.
    /// </summary>
    [Fact]
    public async Task AlertsWaitingForTheMinuteAreKeptToTheQueuesLength() {
        var poster = this.Poster(time: TimeProvider.System, batch: TimeSpan.FromHours(1));
        using var stop = new CancellationTokenSource();
        var running = poster.RunAsync(stop.Token);
        for (var lot = 0; lot < 20; lot++) {
            for (var i = 0; i < 500; i++) {
                poster.Post(Batched($"flag {lot}.{i}"));
            }

            await Task.Delay(30, Ct);
        }

        await WaitFor(() => poster.Undelivered >= 9000 ? new object() : null);
        Assert.Equal(9000, poster.Undelivered);
        Assert.Empty(this._discord.Attempts);
        poster.Post(new Alert("**Urgent: an operator character signed in with other keys**", AlertKind.Urgent));
        var posted = await this._discord.WaitForPost(_ => true);
        Assert.StartsWith($"<@&{Role}> **Urgent: an operator character signed in with other keys**\n\nflag 0.0\n\n", posted.Content);
        Assert.Matches(@"\n\nand [0-9,]+ more: see", posted.Content);
        await stop.CancelAsync();
        await running;
    }

    /// <summary>
    /// The public can set off automatic blocks (and requests through Funnel) as often as it likes: across ten minutes of a block
    /// every 2 seconds, the role is pinged once; then once again. An operator character's keys ping whenever they come.
    /// </summary>
    [Fact]
    public async Task AlertsThePublicCanSetOffPingOnceEveryTenMinutes() {
        var poster = this.Poster();
        for (var second = 0; second < 600; second += 2) {
            poster.Post(new Alert($"**Urgent: blocked automatically:** {second}", AlertKind.Urgent, Public: true));
            await poster.FlushAsync(Ct);
            if (second == 300) {
                poster.Post(new Alert("**Urgent: an operator character signed in with other keys**", AlertKind.Urgent));
                await poster.FlushAsync(Ct);
            }

            this._clock.Advance(TimeSpan.FromSeconds(2));
        }

        var pinged = this._discord.Posts.Where(posted => posted.Roles.Count > 0).Select(posted => posted.Content).ToList();
        Assert.Equal(2, pinged.Count);
        Assert.StartsWith($"<@&{Role}> **Urgent: blocked automatically:** 0", pinged[0]);
        Assert.StartsWith($"<@&{Role}> **Urgent: an operator character signed in with other keys**", pinged[1]);
        Assert.All(this._discord.Posts.Where(posted => posted.Roles.Count == 0), posted => Assert.DoesNotContain("<@&", posted.Content));
        Assert.Equal(301, this._discord.Posts.Count);

        poster.Post(new Alert("**Urgent: blocked automatically:** later", AlertKind.Urgent, Public: true));
        await poster.FlushAsync(Ct);
        Assert.Equal(Role, Assert.Single(this._discord.Posts[^1].Roles));
    }

    /// <summary>
    /// Running, with the real 2 seconds between messages: a burst of automatic blocks sends the first at once with the ping, and the
    /// rest with the minute's message (here a short one), without it; an operator character's keys still go at once, with the ping.
    /// </summary>
    [Fact]
    public async Task ABurstOfAutomaticBlocksPingsOnceAndWaitsForTheMinute() {
        var poster = new AlertPoster(FakeDiscord.Url, Role, () => new HttpClient(this._discord, disposeHandler: false), this.Logger<AlertPoster>()) {
            BatchWindow = TimeSpan.FromSeconds(1),
        };
        using var stop = new CancellationTokenSource();
        var running = poster.RunAsync(stop.Token);
        for (var block = 0; block < 25; block++) {
            poster.Post(new Alert($"**Urgent: blocked automatically:** number {block}", AlertKind.Urgent, Public: true));
            await Task.Delay(100, Ct);
        }

        var first = await this._discord.WaitForPost(_ => true);
        Assert.Equal($"<@&{Role}> **Urgent: blocked automatically:** number 0", first.Content);
        await WaitFor(() => this._discord.Posts.Any(posted => posted.Content.Contains("number 24")) ? new object() : null);
        Assert.InRange(this._discord.Posts.Count, 2, 6);
        Assert.Single(this._discord.Posts, posted => posted.Roles.Count > 0);
        Assert.Equal(25, this._discord.Posts.Sum(posted => posted.Content.Split("blocked automatically").Length - 1));

        poster.Post(new Alert("**Urgent: an operator character signed in with other keys**", AlertKind.Urgent));
        var keys = await this._discord.WaitForPost(posted => posted.Content.Contains("other keys"));
        Assert.Equal(Role, Assert.Single(keys.Roles));
        await stop.CancelAsync();
        await running;
    }

    /// <summary>One alert longer than a message is cut, never between the halves of a surrogate pair (an emoji).</summary>
    [Fact]
    public void AnAlertCutInTheMiddleKeepsEmojiWhole() {
        for (var shift = 0; shift < 4; shift++) {
            var text = new string('a', 1995 + shift) + "\U0001F600\U0001F600\U0001F600" + new string('b', 100);
            var content = AlertWords.Message([new Alert(text, AlertKind.AtOnce)], null, true, out _, out _);
            Assert.InRange(content.Length, 1, AlertWords.MaxMessageLength);
            Assert.EndsWith("…", content);
            Assert.False(char.IsHighSurrogate(content[^2]), $"A lone surrogate at {content.Length - 2}");
            Assert.NotEmpty(AlertPoster.Payload(content, null));
        }
    }

    // ================================================================ bans, posted once by the server

    /// <summary>With alerts off nothing is posted (and nothing breaks); "alerts on since" is forgotten.</summary>
    [Fact]
    public async Task WithAlertsOffNothingIsPosted() {
        var alerts = this.Alerts(Settings(on: false));
        Assert.False(alerts.On);
        Assert.Null(alerts.Poster);
        Assert.True(this.Actions().Ban(CommandLine, Bob.ToString(), 7, null).Done);
        alerts.Tick();
        alerts.Flagged(new FlagRow("user 31337", Bob, null, this.Now, this.Now, "LookupBurst", "refused"));
        alerts.ProxyWarning("127.0.0.1");
        alerts.FunnelRequest();
        alerts.OperatorSignedIn(this._db.GetUser(Alice)!);
        Assert.False(alerts.SendTest());
        Assert.Null(this._db.GetServerState("alerts_on_since"));
        await Task.Delay(100, Ct);
        Assert.Empty(this._discord.Attempts);
    }

    /// <summary>
    /// A ban the command line made (another process, which never posts) is posted by the server when it looks, once: not again
    /// at the next look, nor by the server started again. Its lift likewise. The words say which tool, never who on it.
    /// </summary>
    [Fact]
    public async Task ABanIsPostedOnceAcrossLooksAndRestarts() {
        var alerts = this.Alerts();
        this._clock.Advance(TimeSpan.FromMinutes(1));
        var ban = this.Actions().Ban(CommandLine, "Bob Hatter@Lich", 7, "Spamming invites").Ban!;
        Assert.Equal(
            $"**Ban made** on the command line: Bob Hatter@Lich (user 31337), for 7 days, until <t:{ban.ExpiresAt}:f>. Reason, which the player is shown: Spamming invites\n" +
            "Lodestone: https://na.finalfantasyxiv.com/lodestone/character/31337/\n" +
            "Lift: `LG --unban 31337`",
            await this.PostedAsync(alerts));
        Assert.Null(await this.PostedAsync(alerts));
        Assert.Null(await this.PostedAsync(this.Alerts()));

        this._clock.Advance(TimeSpan.FromHours(1));
        Assert.True(this.Actions().Lift(CommandLine, Bob.ToString()).Done);
        Assert.Equal($"**Ban lifted** on the command line: Bob Hatter@Lich (user 31337). It had been made on the command line, <t:{ban.CreatedAt}:d>, for 7 days.",
            await this.PostedAsync(alerts));
        Assert.Null(await this.PostedAsync(this.Alerts()));
        Assert.DoesNotContain(this._discord.Posts, posted => posted.Content.Contains("unixuser"));
    }

    /// <summary>
    /// A ban made and lifted between two looks is posted as both, in order; one made inside the server (the web page) says so,
    /// without the Tailscale login; one from the plugin names the operator character, and says what to do if it wasn't you.
    /// </summary>
    [Fact]
    public async Task EachToolIsNamedAndOnlyThePluginsOperatorCharacter() {
        var alerts = this.Alerts();
        this._clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(this.Actions().Ban(WebPage, Carol.ToString(), null, null).Done);
        Assert.True(this.Actions().Lift(WebPage, Carol.ToString()).Done);
        var posted = await this.PostedAsync(alerts);
        Assert.StartsWith("**Ban made** on the web page: Carol Queen@Odin (user 4242), until lifted. No reason given.\n", posted);
        Assert.Contains("\n\n**Ban lifted** on the web page: Carol Queen@Odin (user 4242). It had been made on the web page, <t:", posted);
        Assert.DoesNotContain("someone@github", posted);

        Assert.True(this.Actions().Ban(Plugin, "Bob Hatter@Lich", null, null).Done);
        var plugin = await this.PostedAsync(alerts);
        Assert.StartsWith("**Ban made** in the plugin by Alice Wonderland@Lich: Bob Hatter@Lich (user 31337), until lifted. No reason given.\n", plugin);
        Assert.EndsWith("\nIf you didn't make this, lift it (`LG --unban 31337`) and check that operator character's computers.", plugin);
        Assert.Empty(Assert.Single(this._discord.Posts, message => message.Content == plugin).Roles);
    }

    /// <summary>
    /// What was done before alerts were on (off, or an older server) is marked as posted without being posted; what is done
    /// after is posted. Turning alerts off and on again starts again from then.
    /// </summary>
    [Fact]
    public async Task WhatWasDoneBeforeAlertsWereOnIsMarkedNotPosted() {
        this.Alerts(Settings(on: false));
        var before = this.Actions().Ban(CommandLine, Bob.ToString(), 7, null).Ban!;
        this._clock.Advance(TimeSpan.FromMinutes(1));
        var alerts = this.Alerts();
        Assert.Null(await this.PostedAsync(alerts));
        Assert.True(this._db.GetBanHistory(this.Now).Concat(this._db.GetActiveBans(this.Now)).Single(ban => ban.BanId == before.BanId).MadeAnnounced);
        Assert.Contains(this._logs.Entries, entry => entry.Message.Contains("Marked 1 bans and lifts made before Discord alerts were on as posted"));

        this._clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(this.Actions().Ban(CommandLine, Carol.ToString(), 1, null).Done);
        Assert.StartsWith("**Ban made** on the command line: Carol Queen@Odin", await this.PostedAsync(alerts));

        // A restart with alerts still on keeps "alerts on since": a ban made while it was stopped is posted.
        this._clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(this.Actions().Lift(CommandLine, Carol.ToString()).Done);
        Assert.StartsWith("**Ban lifted** on the command line: Carol Queen@Odin", await this.PostedAsync(this.Alerts()));

        // Off, a lift made, then on again: not posted.
        this.Alerts(Settings(on: false));
        Assert.True(this.Actions().Lift(CommandLine, Bob.ToString()).Done);
        this._clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(await this.PostedAsync(this.Alerts()));
    }

    /// <summary>
    /// An automatic block found already ended (an older server made it, before going back to this one) isn't posted, and doesn't
    /// ping: there is nothing to do about it. It is marked, and counted by the daily summary.
    /// </summary>
    [Fact]
    public async Task AnAutomaticBlockThatHasEndedIsntPosted() {
        var alerts = this.Alerts();
        this._clock.Advance(TimeSpan.FromMinutes(1));
        var block = this._db.AddAutomaticBlock("198.51.100.7", "198.51.100.7", this.Now + 15 * 60, this.Now)!;
        this._clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Null(await this.PostedAsync(alerts));
        Assert.True(this._db.GetBanHistory(this.Now).Single(ban => ban.BanId == block.BanId).MadeAnnounced);
        Assert.Contains(this._logs.Entries, entry => entry.Message.Contains("Marked 1 automatic blocks that had already ended as posted"));
    }
    /// <summary>
    /// Banning someone banned already replaces the ban: posted once, as the new ban "replacing the ban made ...", and the old
    /// ban's lift isn't posted on its own (it is marked as posted with the new one).
    /// </summary>
    [Fact]
    public async Task AReplacementIsPostedOnceWithTheNewBan() {
        var alerts = this.Alerts();
        this._clock.Advance(TimeSpan.FromMinutes(1));
        var first = this.Actions().Ban(CommandLine, Bob.ToString(), 7, "Spamming").Ban!;
        Assert.NotNull(await this.PostedAsync(alerts));
        this._clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, this.Actions().Ban(WebPage, Bob.ToString(), 30, "Spamming again").Replaced);
        var replacing = await this.PostedAsync(alerts);
        Assert.StartsWith($"**Ban made** on the web page: Bob Hatter@Lich (user 31337), for 30 days, until <t:", replacing);
        Assert.Contains($", replacing the ban made <t:{first.CreatedAt}:d> on the command line (for 7 days). Reason, which the player is shown: Spamming again\n", replacing);
        Assert.Null(await this.PostedAsync(alerts));
        Assert.DoesNotContain(this._discord.Posts, posted => posted.Content.Contains("Ban lifted"));
        Assert.True(this._db.GetBanHistory(this.Now).Single(ban => ban.BanId == first.BanId).LiftedAnnounced);
    }

    // ================================================================ flags, blocks, the proxy, Funnel, the test

    /// <summary>
    /// A flag is posted as the monitor makes it (not again for one still in force); an automatic block is posted from the bans
    /// table, urgent, with the ping; refusals from the proxy's address post the urgent proxy warning instead, with that address
    /// in full.
    /// </summary>
    [Fact]
    public async Task FlagsBlocksAndTheProxyWarningArePosted() {
        var settings = Settings();
        settings.Abuse.FlagAfterMinutesRefused = 1;
        settings.Abuse.AutoBlockAfterRefusals = 100;
        var alerts = this.Alerts(settings);
        var options = Microsoft.Extensions.Options.Options.Create(settings);
        var monitor = new AbuseMonitor(this._db, new BanList(this._db, options, this._clock), options, this.Logger<AbuseMonitor>(), this._clock, alerts);

        monitor.Refused("LookupBurst", Bob, null);
        Assert.Equal(
            "**Flagged:** Bob Hatter@Lich (user 31337): refused by limits in 1 of the last 60 minutes, by LookupBurst.\n" +
            "Lodestone: https://na.finalfantasyxiv.com/lodestone/character/31337/\n" +
            "Look: `LG --bans`\n" +
            "Ban: `LG --ban 31337 --days 7 --reason \"...\"`\n" +
            "The flag ends by itself 24 hours after the last refusal. Nothing was banned.",
            await this.PostedAsync(alerts));
        monitor.Refused("LookupBurst", Bob, null);
        Assert.Null(await this.PostedAsync(alerts));

        for (var i = 0; i < 100; i++) {
            monitor.Refused("ConnectionsPerMinutePerIp", null, "203.0.113.66");
        }

        var block = await this.PostedAsync(alerts);
        Assert.StartsWith($"<@&{Role}> **Urgent: blocked automatically:** address 203.0.113.x (shortened), refused 100 times within 60 minutes. " +
                          $"Blocked for 15 minutes, until <t:{this.Now + 15 * 60}:t>.\nLook: `LG --bans`", block);
        Assert.Contains("\n\n**Flagged:** address 203.0.113.x (shortened): refused by limits in 1 of the last 60 minutes (ConnectionsPerMinutePerIp).\n" +
                        "It may be shared (a household, a mobile network). Full address: `LG --bans`", block);
        Assert.DoesNotContain("203.0.113.66", block);
        Assert.Equal(Role, Assert.Single(this._discord.Posts[^1].Roles));

        monitor.Refused("SendMessage", null, "127.0.0.1");
        Assert.StartsWith($"<@&{Role}> **Urgent: every player seems to come from 127.0.0.1.** The proxy isn't passing the client's address on", await this.PostedAsync(alerts));
    }

    /// <summary>Requests through Funnel (from step 3) are posted at most every ten minutes, counting the rest; a test alert goes at once.</summary>
    [Fact]
    public async Task FunnelRequestsAreNotPostedOverAndOverAndATestGoesAtOnce() {
        var alerts = this.Alerts();
        alerts.FunnelRequest();
        Assert.StartsWith($"<@&{Role}> **Urgent: the web page was asked for through Tailscale Funnel**", await this.PostedAsync(alerts));
        alerts.FunnelRequest();
        alerts.FunnelRequest();
        Assert.Null(await this.PostedAsync(alerts));
        this._clock.Advance(ServerAlerts.FunnelAlertEvery);
        alerts.FunnelRequest();
        Assert.EndsWith("(2 more requests like it since the last of these alerts.)", await this.PostedAsync(alerts));

        Assert.True(alerts.SendTest());
        Assert.Equal($"**Test alert** from LookingGlass {RequestHandler.ServerVersion}: alerts reach this channel.", await this.PostedAsync(alerts));
        Assert.Empty(this._discord.Posts[^1].Roles);
    }

    // ================================================================ operators

    /// <summary>
    /// An operator character's keys pinned (the command line, another process), and pinned again after they changed, are each
    /// posted once, urgent, with the ping and both fingerprints; one pinned before alerts were on isn't.
    /// </summary>
    [Fact]
    public async Task PinsAndRestoresArePostedOnceWithThePing() {
        var rabbit = this._db.GetUser(Rabbit)!;
        Assert.True(this.Actions().PinByFingerprint(CommandLine, Rabbit.ToString(), rabbit.Keys.Fingerprint).Done);
        this._clock.Advance(TimeSpan.FromMinutes(1));
        var alerts = this.Alerts();
        Assert.Null(await this.PostedAsync(alerts));

        this._clock.Advance(TimeSpan.FromMinutes(1));
        var alice = this._db.GetUser(Alice)!;
        Assert.True(this.Actions().PinByFingerprint(CommandLine, Alice.ToString(), alice.Keys.Fingerprint).Done);
        Assert.Equal(
            $"<@&{Role}> **Operator keys pinned** on the command line at <t:{this.Now}:f>: Alice Wonderland@Lich (user 55555), pinned to keys {alice.Keys.Fingerprint}.\n" +
            "If you didn't do this, unlist the character in the server's settings and restart, then check `LG --audit`.",
            await this.PostedAsync(alerts));
        Assert.Equal(Role, Assert.Single(this._discord.Posts[^1].Roles));
        Assert.Null(await this.PostedAsync(this.Alerts()));

        var again = this.Register(Alice, "Alice Wonderland", "Lich");
        Assert.True(this.Actions().PinByFingerprint(CommandLine, Alice.ToString(), again.Keys.Fingerprint).Done);
        Assert.StartsWith($"<@&{Role}> **Operator status restored** on the command line at <t:{this.Now}:f>: Alice Wonderland@Lich (user 55555), now pinned to keys " +
                          $"{again.Keys.Fingerprint} (was {alice.Keys.Fingerprint}).", await this.PostedAsync(alerts));
        Assert.Null(await this.PostedAsync(alerts));
    }

    /// <summary>
    /// A listed, pinned character signing in with keys that aren't its pin is posted, urgent, once for each new set of keys (not
    /// at every login); its own keys, a character not listed, or one listed but never pinned, aren't posted.
    /// </summary>
    [Fact]
    public async Task AnOperatorSigningInWithOtherKeysIsPostedOncePerSetOfKeys() {
        var alerts = this.Alerts();
        var pinned = this._db.GetUser(Alice)!;
        Assert.True(this.Actions().PinByFingerprint(CommandLine, Alice.ToString(), pinned.Keys.Fingerprint).Done);
        Assert.NotNull(await this.PostedAsync(alerts));

        alerts.OperatorSignedIn(pinned);
        alerts.OperatorSignedIn(this._db.GetUser(Bob)!);
        alerts.OperatorSignedIn(this._db.GetUser(Rabbit)!);
        Assert.Null(await this.PostedAsync(alerts));

        var other = this.Register(Alice, "Alice Wonderland", "Lich");
        alerts.OperatorSignedIn(other);
        var posted = await this.PostedAsync(alerts);
        Assert.StartsWith($"<@&{Role}> **Urgent: an operator character signed in with other keys:** Alice Wonderland@Lich (user 55555), keys " +
                          $"{other.Keys.Fingerprint}, not the pinned {pinned.Keys.Fingerprint}. It registered again through the Lodestone at <t:{other.KeysRegisteredAt}:t>, " +
                          "and this server no longer treats it as an operator.\n", posted);
        Assert.Contains("`LG --operator-pin 55555 --fingerprint \"<the fingerprint you checked>\"`", posted);
        Assert.DoesNotContain("--force", posted);
        Assert.Equal(Role, Assert.Single(this._discord.Posts[^1].Roles));

        alerts.OperatorSignedIn(other);
        Assert.Null(await this.PostedAsync(alerts));
        var restarted = this.Alerts();
        restarted.OperatorSignedIn(other);
        Assert.Null(await this.PostedAsync(restarted));

        var third = this.Register(Alice, "Alice Wonderland", "Lich");
        alerts.OperatorSignedIn(third);
        Assert.Contains($"keys {third.Keys.Fingerprint}, not the pinned {pinned.Keys.Fingerprint}", await this.PostedAsync(alerts));

        // Pinned again to those keys, the next other keys are posted again.
        Assert.True(this.Actions().PinByFingerprint(CommandLine, Alice.ToString(), third.Keys.Fingerprint).Done);
        Assert.NotNull(await this.PostedAsync(alerts));
        alerts.OperatorSignedIn(third);
        Assert.Null(await this.PostedAsync(alerts));
        var fourth = this.Register(Alice, "Alice Wonderland", "Lich");
        alerts.OperatorSignedIn(fourth);
        Assert.Contains($"not the pinned {third.Keys.Fingerprint}", await this.PostedAsync(alerts));
    }

    // ================================================================ the server

    /// <summary>The restart alert says whether the server last stopped cleanly, and when, or since when it had run if not; nothing the first time.</summary>
    [Fact]
    public void TheRestartAlertSaysHowTheServerLastStopped() {
        var first = this.Alerts();
        Assert.Equal($"**Server started:** LookingGlass {RequestHandler.ServerVersion}, in Production.", first.StartedAlert().Text);
        var started = this.Now;
        this._clock.Advance(TimeSpan.FromHours(5));
        first.MarkStoppedCleanly();
        var stopped = this.Now;

        this._clock.Advance(TimeSpan.FromMinutes(1));
        var second = this.Alerts();
        Assert.Equal((AlertKind.AtOnce, $"**Server started:** LookingGlass {RequestHandler.ServerVersion}, in Production. It stopped cleanly at <t:{stopped}:t>."),
            (second.StartedAlert().Kind, second.StartedAlert().Text));
        var secondStarted = this.Now;

        // Killed: no clean stop noted.
        this._clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal($"**Server started:** LookingGlass {RequestHandler.ServerVersion}, in Production. It didn't stop cleanly: it had been running since " +
                     $"<t:{secondStarted}:f> (a crash, the machine losing power, or the process being killed). See `journalctl -u lookingglass`.",
            this.Alerts().StartedAlert().Text);
        Assert.NotEqual(started, secondStarted);
    }

    /// <summary>When the summary was last due, in UTC hours: the same whatever any place's clocks do (here across Europe's change on 2026-10-25).</summary>
    [Theory]
    [InlineData("2026-10-25T08:59:59Z", 9, "2026-10-24T09:00:00Z")]
    [InlineData("2026-10-25T09:00:00Z", 9, "2026-10-25T09:00:00Z")]
    [InlineData("2026-10-25T23:30:00Z", 0, "2026-10-25T00:00:00Z")]
    [InlineData("2026-10-26T00:00:00Z", 23, "2026-10-25T23:00:00Z")]
    public void TheSummaryIsDueAtItsHourUtc(string now, int hour, string due) {
        Assert.Equal(DateTimeOffset.Parse(due), ServerAlerts.LatestDue(DateTimeOffset.Parse(now), hour));
    }

    /// <summary>
    /// The daily summary is posted once its hour (UTC) comes, once: not again within the day, nor by a server started again;
    /// one missed while the server was down is posted when it is back (only the latest day's); a quiet day says so, and
    /// that alerts work. With the hour -1, never.
    /// </summary>
    [Fact]
    public async Task TheDailySummaryIsPostedAtItsHourOnceAcrossRestarts() {
        this.SetClock(DateTimeOffset.Parse("2030-10-10T08:30:00Z"));
        var alerts = this.Alerts();
        Assert.Null(await this.PostedAsync(alerts));
        this.SetClock(DateTimeOffset.Parse("2030-10-10T08:59:59Z"));
        Assert.Null(await this.PostedAsync(alerts));

        this.SetClock(DateTimeOffset.Parse("2030-10-10T09:00:00Z"));
        var due = DateTimeOffset.Parse("2030-10-10T09:00:00Z").ToUnixTimeSeconds();
        Assert.Equal(
            $"**Daily summary**, the 24 hours to <t:{due}:f>: a quiet day. Nothing flagged, blocked, banned or lifted. In force now: 0 bans. " +
            $"Nobody online, counted since the restart at <t:{DateTimeOffset.Parse("2030-10-10T08:30:00Z").ToUnixTimeSeconds()}:t>; no characters registered " +
            "for the first time. Alerts are working.",
            await this.PostedAsync(alerts));
        this.SetClock(DateTimeOffset.Parse("2030-10-10T09:30:00Z"));
        Assert.Null(await this.PostedAsync(alerts));
        this.SetClock(DateTimeOffset.Parse("2030-10-10T09:45:00Z"));
        Assert.Null(await this.PostedAsync(this.Alerts()));

        // Down from before the next one until three days later: one, for the latest day.
        this.SetClock(DateTimeOffset.Parse("2030-10-13T11:00:00Z"));
        alerts = this.Alerts();
        Assert.Contains($"the 24 hours to <t:{DateTimeOffset.Parse("2030-10-13T09:00:00Z").ToUnixTimeSeconds()}:f>", await this.PostedAsync(alerts));
        Assert.Null(await this.PostedAsync(alerts));

        this.SetClock(DateTimeOffset.Parse("2030-10-14T10:00:00Z"));
        Assert.Null(await this.PostedAsync(this.Alerts(Settings(hour: -1))));
    }

    /// <summary>The summary counts the day: flags by kind, automatic blocks, bans by tool, lifts (not replacements), ends, bans in force, first registrations.</summary>
    [Fact]
    public async Task TheDailySummaryCountsTheDay() {
        this.SetClock(DateTimeOffset.Parse("2026-10-09T08:30:00Z"));
        Assert.True(this.Actions().Ban(CommandLine, "203.0.113.0/24", 1, null).Done);
        this.SetClock(DateTimeOffset.Parse("2026-10-10T08:00:00Z"));
        var alerts = this.Alerts();
        this.SetClock(DateTimeOffset.Parse("2026-10-10T08:10:00Z"));
        this._db.SaveFlag(new FlagRow("user 31337", Bob, null, this.Now, this.Now, "LookupBurst", "refused by limits in 30 of the last 60 minutes"));
        this._db.SaveFlag(new FlagRow("address 198.51.100.7", null, "198.51.100.7", this.Now, this.Now, "ConnectionsPerIp", "refused by 4 different limits within 10 minutes"));
        Assert.NotNull(this._db.AddAutomaticBlock("198.51.100.7", "198.51.100.7", this.Now + 15 * 60, this.Now));
        Assert.True(this.Actions().Ban(CommandLine, Bob.ToString(), 7, null).Done);
        Assert.True(this.Actions().Ban(WebPage, Carol.ToString(), null, null).Done);
        Assert.True(this.Actions().Ban(Plugin, "777", null, null).Done);
        Assert.Equal(1, this.Actions().Ban(CommandLine, Bob.ToString(), 30, null).Replaced);
        Assert.True(this.Actions().Lift(CommandLine, Carol.ToString()).Done);
        using (var connection = new SqliteConnection($"Data Source={this._db.FilePath};Pooling=False")) {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE users SET created_at = CASE user_id WHEN $bob THEN $within ELSE $before END;";
            command.Parameters.AddWithValue("$bob", Bob);
            command.Parameters.AddWithValue("$within", this.Now);
            command.Parameters.AddWithValue("$before", this.Now - 3 * 86400);
            command.ExecuteNonQuery();
        }

        this.SetClock(DateTimeOffset.Parse("2026-10-10T08:50:00Z"));
        Assert.NotNull(await this.PostedAsync(alerts));
        this.SetClock(DateTimeOffset.Parse("2026-10-10T09:00:00Z"));
        var summary = await this.PostedAsync(alerts);
        Assert.NotNull(summary);
        var lines = summary.Split('\n');
        Assert.Equal($"**Daily summary**, the 24 hours to <t:{this.Now}:f>", lines[0]);
        Assert.Equal("Flagged: 2 (1 character, 1 address). Blocked automatically: 1.", lines[1]);
        // Bob's 30 days replaced his 7 (a ban made, not a lift); the /24 and the automatic block ran out; Bob and user 777 are banned now.
        Assert.Equal("Bans made: 4 (2 on the command line, 1 on the web page, 1 in the plugin). Lifted: 1. Ended: 2. In force now: 2.", lines[2]);
        Assert.Equal($"Players: nobody online, counted since the restart at <t:{DateTimeOffset.Parse("2026-10-10T08:00:00Z").ToUnixTimeSeconds()}:t>; " +
                     "1 character registered for the first time.", lines[3]);
        Assert.StartsWith($"Server: {RequestHandler.ServerVersion}, up 1 hour; database ", lines[4]);
        Assert.EndsWith(". Alerts not delivered: none.", lines[4]);
    }
}
