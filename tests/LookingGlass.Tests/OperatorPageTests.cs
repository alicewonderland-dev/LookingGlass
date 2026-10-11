using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LookingGlass.Core.Crypto;
using LookingGlass.Server;
using LookingGlass.Server.Admin;
using LookingGlass.Server.Data;
using LookingGlass.Server.Services;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// The operator web page in a running server, with fake Tailscale headers (as <c>tailscale serve</c> would set them): who is let
/// in (a listed login only; never a request through Funnel, which also raises an urgent alert; never one asked for under
/// another name), refusals logged at most once a minute, the page's routes only on its own host and none of the public host's
/// there; what it shows (counts, flags with full addresses, bans with who made them, history, operators, the audit trail, the
/// time zone), banning, lifting, pinning and restoring through confirmation pages, the test alert, the security headers, and
/// everything from outside HTML-encoded.
/// </summary>
public sealed class OperatorPageTests {
    private const string Role = "424242424242424242";

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Registers a character directly in the server's database, with keys of its own; returns them (as its fingerprint).</summary>
    private static string Register(Harness server, long userId, string name, string world = "Lich") {
        using var keys = IdentityKeys.Generate();
        server.Database.RegisterUser(userId, name, 73, world, keys.ToBundle(), false);
        return server.Database.GetUser(userId)!.Keys.Fingerprint;
    }

    private static OperatorActions Actions(Harness server) => server.Factory.Services.GetRequiredService<OperatorActions>();

    private static void AssertSecurityHeaders(Answer answer) {
        Assert.Equal(OperatorPage.ContentSecurityPolicy, answer.Header("Content-Security-Policy"));
        Assert.Equal("default-src 'none'; style-src 'self'; img-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'", answer.Header("Content-Security-Policy"));
        Assert.Equal("nosniff", answer.Header("X-Content-Type-Options"));
        Assert.Equal("same-origin", answer.Header("Referrer-Policy"));
        Assert.Equal("no-store", answer.Header("Cache-Control"));
    }

    // ================================================================ who gets in

    /// <summary>
    /// Without a listed Tailscale login, or asked for under another name than <c>Admin:Url</c>, every request gets 403 and the
    /// same few words (not which check failed); the log says which.
    /// </summary>
    [Theory]
    [InlineData(null, PageServer.Host, "has no Tailscale-User-Login")]
    [InlineData("", PageServer.Host, "has no Tailscale-User-Login")]
    [InlineData("mallory@github", PageServer.Host, "isn't listed in LookingGlass:Admin:TailscaleLogins")]
    [InlineData("alice@github.evil", PageServer.Host, "isn't listed")]
    [InlineData(PageServer.Login, null, "(no X-Forwarded-Host)")]
    [InlineData(PageServer.Login, "lg-test.tailnet-0000.ts.net", "not lg-test.tailnet-0000.ts.net:8444")]
    [InlineData(PageServer.Login, "evil.example:8444", "not lg-test.tailnet-0000.ts.net:8444")]
    [InlineData(PageServer.Login, "lg-test.tailnet-0000.ts.net:443", "not lg-test.tailnet-0000.ts.net:8444")]
    public async Task OnlyAListedLoginAskingForThePagesAddressIsLetIn(string? login, string? host, string logged) {
        var logs = new CapturingLoggerProvider();
        await using var page = new PageServer(logs: logs);
        foreach (var path in new[] { "/", "/style.css", "/lift?ban=1", "/pin?user=1", "/nothing" }) {
            var answer = await page.GetAsync(path, login, host);
            Assert.Equal(HttpStatusCode.Forbidden, answer.Status);
            Assert.Contains("Not allowed.", answer.Text);
            Assert.Contains(OperatorPage.NotAllowed, answer.Text);
            Assert.DoesNotContain("Ban someone", answer.Html);
            Assert.DoesNotContain("stylesheet", answer.Html);
            AssertSecurityHeaders(answer);
        }

        var refused = await page.PostAsync("/ban/confirm", [new("whom", "203.0.113.9"), new("token", "x")], login, host: host);
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Empty(page.Server.Database.GetActiveBans(Now));

        // Logged once (the rest within the minute only counted), saying which check failed.
        var warning = Assert.Single(logs.Entries, entry => entry.Message.StartsWith("Refused a request to the operator web page", StringComparison.Ordinal));
        Assert.Contains(logged, warning.Message);
        Assert.Contains($"from {PageServer.TailnetAddress}", warning.Message);
    }

    /// <summary>A listed login is let in, whatever its case; the page says who is signed in.</summary>
    [Fact]
    public async Task AListedLoginIsLetIn() {
        await using var page = new PageServer();
        foreach (var login in new[] { PageServer.Login, "Alice@GitHub" }) {
            var answer = await page.GetAsync("/", login);
            Assert.Equal(HttpStatusCode.OK, answer.Status);
            Assert.Equal("text/html; charset=utf-8", answer.Header("Content-Type"));
            Assert.Contains($"Signed in: {login}", answer.Text);
            Assert.Contains("Ban someone", answer.Text);
            AssertSecurityHeaders(answer);
        }

        var css = await page.GetAsync("/style.css");
        Assert.Equal(HttpStatusCode.OK, css.Status);
        Assert.Equal("text/css; charset=utf-8", css.Header("Content-Type"));
        Assert.Contains("prefers-color-scheme: dark", css.Html);
        Assert.Contains("max-width: 700px", css.Html);
        AssertSecurityHeaders(css);
    }

    /// <summary>
    /// A request marked as through Tailscale Funnel is refused, even with a listed login, and raises an urgent alert with the
    /// ping; more within ten minutes aren't posted again at once.
    /// </summary>
    [Fact]
    public async Task ARequestThroughFunnelIsRefusedAndRaisesAnUrgentAlert() {
        var discord = new FakeDiscord();
        var logs = new CapturingLoggerProvider();
        await using var page = new PageServer(discord: discord, logs: logs, settings: [FakeDiscord.On, ("LookingGlass:Alerts:PingRoleId", Role), ("LookingGlass:Alerts:Restarts", "false")]);
        var answer = await page.GetAsync("/", funnel: true);
        Assert.Equal(HttpStatusCode.Forbidden, answer.Status);
        Assert.Contains(OperatorPage.NotAllowed, answer.Text);
        AssertSecurityHeaders(answer);
        var posted = await discord.WaitForPost(message => message.Content.Contains("asked for through Tailscale Funnel"));
        Assert.StartsWith($"<@&{Role}> **Urgent: the web page was asked for through Tailscale Funnel**", posted.Content);
        Assert.Equal(Role, Assert.Single(posted.Roles));
        Assert.Contains(logs.Entries, entry => entry.Message.Contains("it came through Tailscale Funnel"));

        Assert.Equal(HttpStatusCode.Forbidden, (await page.GetAsync("/", funnel: true)).Status);
        await Task.Delay(500, Ct);
        Assert.Single(discord.Posts, message => message.Content.Contains("Tailscale Funnel"));
    }

    /// <summary>
    /// A request from this machine itself is refused, even with a listed login: <c>tailscale serve</c> names such a request by
    /// the machine's own Tailscale login (the owner's, on an untagged server), which any program on the machine could use. So is
    /// one without an <c>X-Forwarded-For</c> address, which <c>tailscale serve</c> always sets.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1", "it came from this machine itself")]
    [InlineData("127.0.0.2", "it came from this machine itself")]
    [InlineData("::1", "it came from this machine itself")]
    [InlineData("::ffff:127.0.0.1", "it came from this machine itself")]
    [InlineData("0.0.0.0", "it came from this machine itself")]
    [InlineData(null, "it has no X-Forwarded-For address")]
    [InlineData("", "it has no X-Forwarded-For address")]
    [InlineData("not-an-address", "it has no X-Forwarded-For address")]
    public async Task ARequestFromThisMachineIsRefused(string? forwardedFor, string logged) {
        var logs = new CapturingLoggerProvider();
        await using var page = new PageServer(logs: logs);
        var answer = await page.GetAsync("/", forwardedFor: forwardedFor);
        Assert.Equal(HttpStatusCode.Forbidden, answer.Status);
        Assert.Contains(OperatorPage.NotAllowed, answer.Text);
        AssertSecurityHeaders(answer);
        Assert.Contains(logs.Entries, entry => entry.Message.StartsWith("Refused a request to the operator web page", StringComparison.Ordinal) && entry.Message.Contains(logged));
    }

    /// <summary>Every address of every network interface here (as a Tailscale address of this machine would be) is refused too.</summary>
    [Fact]
    public async Task ARequestFromAnyOfThisMachinesAddressesIsRefused() {
        await using var page = new PageServer();
        var addresses = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses).Select(unicast => unicast.Address).ToList();
        Assert.NotEmpty(addresses);
        foreach (var address in addresses) {
            Assert.Equal(HttpStatusCode.Forbidden, (await page.GetAsync("/", forwardedFor: address.ToString())).Status);
        }

        Assert.Equal(HttpStatusCode.OK, (await page.GetAsync("/")).Status);
    }

    /// <summary>An address that comes after the start (Tailscale's, once it is up) is found: the addresses are read again when one isn't among them.</summary>
    [Fact]
    public void AnAddressThatComesLaterIsFound() {
        var current = new List<IPAddress> { IPAddress.Parse("192.0.2.10") };
        var local = new LocalAddresses(source: () => current.ToList());
        Assert.True(local.IsLocal(IPAddress.Parse("192.0.2.10")));
        Assert.True(local.IsLocal(IPAddress.Parse("::ffff:192.0.2.10")));
        Assert.False(local.IsLocal(IPAddress.Parse("100.64.0.7")));
        current.Add(IPAddress.Parse("100.64.0.7"));
        current.Add(IPAddress.Parse("fd7a:115c:a1e0::7"));
        Assert.True(local.IsLocal(IPAddress.Parse("100.64.0.7")));
        Assert.True(local.IsLocal(IPAddress.Parse("fd7a:115c:a1e0::7")));
        Assert.True(local.IsLocal(IPAddress.Loopback));
        Assert.False(local.IsLocal(IPAddress.Parse(PageServer.TailnetAddress)));
    }

    /// <summary>
    /// <c>Admin:OwnAddresses</c> adds addresses and prefixes counted as this machine's own (in Docker, the host's tailnet
    /// addresses, on no interface the container sees): their requests are refused whatever their login.
    /// </summary>
    [Fact]
    public async Task OwnAddressesAreRefusedLikeThisMachines() {
        var logs = new CapturingLoggerProvider();
        await using var page = new PageServer(logs: logs, settings: [("LookingGlass:Admin:OwnAddresses:0", "100.101.102.7/24"), ("LookingGlass:Admin:OwnAddresses:1", "fd7a:115c:a1e0::5")]);
        var refused = await page.GetAsync("/");
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Contains(logs.Entries, entry => entry.Message.Contains($"it came from this machine itself ({PageServer.TailnetAddress}), or from an address in LookingGlass:Admin:OwnAddresses"));
        Assert.Equal(HttpStatusCode.Forbidden, (await page.GetAsync("/", forwardedFor: "fd7a:115c:a1e0::5")).Status);
        Assert.Equal(HttpStatusCode.OK, (await page.GetAsync("/", forwardedFor: "100.101.103.4")).Status);
        Assert.Equal(HttpStatusCode.OK, (await page.GetAsync("/", forwardedFor: "fd7a:115c:a1e0::6")).Status);

        Assert.Equal("100.101.102.0/24", LocalAddresses.ParseNetwork(" 100.101.102.7/24 ")!.Value.ToString());
        Assert.Equal("fd7a:115c:a1e0::5/128", LocalAddresses.ParseNetwork("fd7a:115c:a1e0::5")!.Value.ToString());
        Assert.Equal("203.0.113.9/32", LocalAddresses.ParseNetwork("::ffff:203.0.113.9")!.Value.ToString());
        foreach (var wrong in new[] { "", "not-an-address", "10.0.0.0/33", "10.0.0.0/", "fe80::1%eth0", "fd7a::/129" }) {
            Assert.Null(LocalAddresses.ParseNetwork(wrong));
        }
    }

    /// <summary>
    /// The interfaces are read again only for a request from a listed login (the address check comes after the login checks), so a
    /// tailnet member who isn't listed can't make the server read them for every request.
    /// </summary>
    [Fact]
    public async Task OnlyAListedLoginsRequestsMakeTheInterfacesBeReadAgain() {
        var local = new LocalAddresses(source: () => [IPAddress.Parse("100.64.0.1")]);
        var logs = new CapturingLoggerProvider();
        await using var page = new PageServer(logs: logs, services: services => services.AddSingleton(local));
        var reads = local.Reads;
        for (var i = 0; i < 5; i++) {
            Assert.Equal(HttpStatusCode.Forbidden, (await page.GetAsync("/", "mallory@github")).Status);
        }

        Assert.Equal(reads, local.Reads);
        // An unlisted login from this machine's own address is refused for its login, before the address is looked at.
        Assert.Equal(HttpStatusCode.Forbidden, (await page.GetAsync("/", "mallory@github", forwardedFor: "100.64.0.1")).Status);
        Assert.Contains(logs.Entries, entry => entry.Message.Contains("isn't listed"));
        Assert.Equal(reads, local.Reads);

        Assert.Equal(HttpStatusCode.OK, (await page.GetAsync("/")).Status);
        Assert.Equal(reads + 1, local.Reads);
        Assert.Equal(HttpStatusCode.Forbidden, (await page.GetAsync("/", forwardedFor: "100.64.0.1")).Status);
        Assert.Equal(reads + 1, local.Reads);
    }

    /// <summary>
    /// If the interfaces can't be read, the server starts all the same, and every request counts as from this machine: refused,
    /// saying why, rather than let in on a guess.
    /// </summary>
    [Fact]
    public async Task IfTheInterfacesCantBeReadRequestsAreRefused() {
        var local = new LocalAddresses(source: () => throw new InvalidOperationException("No interfaces"));
        Assert.True(local.ReadFailed);
        Assert.True(local.IsLocal(IPAddress.Parse(PageServer.TailnetAddress)));
        var logs = new CapturingLoggerProvider();
        await using var page = new PageServer(logs: logs, services: services => services.AddSingleton(local));
        Assert.Equal(HttpStatusCode.Forbidden, (await page.GetAsync("/")).Status);
        Assert.Contains(logs.Entries, entry => entry.Message.Contains("this machine's own addresses couldn't be read"));
    }

    /// <summary>A request that fails unexpectedly is answered by the page itself, with the security headers and nothing about the error.</summary>
    [Fact]
    public async Task AnUnexpectedFailureIsAnsweredWithTheHeaders() {
        await using var page = new PageServer();
        page.Server.ExpectedErrors = message => message.StartsWith("The operator web page failed to answer", StringComparison.Ordinal);
        page.Server.Factory.Services.GetRequiredService<OperatorPage>().BeforeAnswerForTests = _ => throw new InvalidOperationException("Secret detail");
        var answer = await page.GetAsync("/");
        Assert.Equal(HttpStatusCode.InternalServerError, answer.Status);
        AssertSecurityHeaders(answer);
        Assert.DoesNotContain("Secret detail", answer.Html);
        Assert.Contains("Something went wrong on the server", answer.Text);

        // A form too long to read: 400, with the headers.
        page.Server.Factory.Services.GetRequiredService<OperatorPage>().BeforeAnswerForTests = null;
        var tooLong = await page.PostAsync("/ban", [new("whom", new string('x', 5000)), new("token", "x")]);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.Status);
        AssertSecurityHeaders(tooLong);
    }

    /// <summary>Refused requests are logged at most once a minute; the next line counts those in between.</summary>
    [Fact]
    public async Task RefusalsAreLoggedAtMostOnceAMinute() {
        var clock = new StoppedClock();
        var logs = new CapturingLoggerProvider();
        await using var page = new PageServer(clock, logs: logs);
        for (var i = 0; i < 3; i++) {
            Assert.Equal(HttpStatusCode.Forbidden, (await page.GetAsync("/", "mallory@github")).Status);
        }

        Assert.Single(logs.Entries, entry => entry.Message.StartsWith("Refused a request to the operator web page", StringComparison.Ordinal));
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(HttpStatusCode.Forbidden, (await page.GetAsync("/", login: null)).Status);
        var lines = logs.Entries.Where(entry => entry.Message.StartsWith("Refused a request to the operator web page", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Contains("2 more refused since the last of these", lines[1].Message);
        Assert.All(lines, line => Assert.Equal(LogLevel.Warning, line.Level));
    }

    // ================================================================ two hosts

    /// <summary>The public host has none of the page's routes, whatever headers come with the request; the page's host has nothing of the public one's.</summary>
    [Fact]
    public async Task ThePagesRoutesAreOnlyOnItsOwnHost() {
        await using var page = new PageServer();
        using var publicClient = page.Server.Factory.CreateClient();
        foreach (var path in new[] { "/", "/style.css", "/ban", "/ban/confirm", "/lift", "/lift?ban=1", "/lift/confirm", "/pin", "/pin?user=1", "/pin/confirm", "/test-alert" }) {
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post }) {
                using var request = PageServer.Request(method, path);
                if (method == HttpMethod.Post) {
                    request.Content = new FormUrlEncodedContent([new("whom", "203.0.113.9")]);
                    request.Headers.TryAddWithoutValidation("Origin", PageServer.Origin);
                }

                using var response = await publicClient.SendAsync(request, Ct);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }
        }

        // The public host's own: there, and not on the page's host.
        using (var health = await publicClient.GetAsync("/health", Ct)) {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }

        foreach (var path in new[] { "/ws", "/health" }) {
            var answer = await page.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, answer.Status);
            AssertSecurityHeaders(answer);
        }

        Assert.Empty(page.Server.Database.GetActiveBans(Now));
    }

    // ================================================================ what it shows

    /// <summary>
    /// The main page: the counts, flags with full addresses and their Lodestone links and <b>Ban...</b> (filling the form in,
    /// with 7 days), bans in force with who made each and with which tool, the history, and the audit trail.
    /// </summary>
    [Fact]
    public async Task TheMainPageShowsFlagsBansHistoryAndTheAuditTrail() {
        await using var page = new PageServer();
        var server = page.Server;
        Register(server, 31337, "Bob Hatter");
        Register(server, 4242, "Carol Queen", "Odin");
        var now = Now;
        server.Database.SaveFlag(new FlagRow("user 31337", 31337, null, now - 600, now - 60, "InviteBurstPerPair,LookupBurst", "refused in 30 of the last 60 minutes"));
        server.Database.SaveFlag(new FlagRow("address 203.0.113.9", null, "203.0.113.9", now - 900, now - 120, "ConnectionsPerIp,LookupBurst",
            "refused by 4 different limits within 10 minutes"));
        var actions = Actions(server);
        Assert.True(actions.Ban(new OperatorActor(OperatorTool.Plugin, "Alice Wonderland@Lich", "", 55555), "4242", 7, "Spamming invites").Done);
        Assert.True(actions.Ban(new OperatorActor(OperatorTool.CommandLine, "alice"), "198.51.100.0/24", 1, "Flooding").Done);
        Assert.True(actions.Ban(new OperatorActor(OperatorTool.CommandLine, "alice"), "777", 3, null).Done);
        Assert.True(actions.Lift(new OperatorActor(OperatorTool.CommandLine, "alice"), "777").Done);

        var answer = await page.GetAsync("/");
        var text = answer.Text;
        Assert.Contains("2 flagged", text);
        Assert.Contains("2 bans in force", text);
        Assert.Contains("0 blocked automatically", text);
        Assert.Contains("0 players online", text);
        Assert.Contains("Flagged in the last 24 hours (2)", text);
        Assert.Contains("Bob Hatter@Lich (31337)", text);
        Assert.Contains("https://na.finalfantasyxiv.com/lodestone/character/31337/", answer.Html);
        Assert.Contains("address 203.0.113.9", text);
        Assert.Contains("ConnectionsPerIp, LookupBurst", text);
        Assert.Contains("href=\"/?whom=31337&amp;days=7#ban\"", answer.Html);
        Assert.Contains("href=\"/?whom=203.0.113.9&amp;days=7#ban\"", answer.Html);
        Assert.Contains("Bans in force (2)", text);
        Assert.Contains("Carol Queen@Odin (4242)", text);
        Assert.Contains("in the plugin by Alice Wonderland@Lich", text);
        Assert.Contains("address 198.51.100.0/24", text);
        Assert.Contains("on the command line by alice", text);
        Assert.Contains("\u201cSpamming invites\u201d", text);
        Assert.Contains("(7 days)", text);
        Assert.Contains("Lifted or ended in the last 90 days (1)", text);
        Assert.Contains("user 777", text);
        Assert.Contains("What operators did (the last 50)", text);
        Assert.Contains("banned address 198.51.100.0/24, for 1 day. Reason: Flooding", text);
        Assert.Contains("lifted the ban on user 777", text);
        Assert.Contains("Times are UTC.", text);
        Assert.Contains("Discord alerts are off.", text);
        Assert.DoesNotContain("Operators (", text);
        Assert.DoesNotContain("<script", answer.Html);

        // A Ban... link fills the form in (and does nothing else).
        var prefilled = await page.GetAsync("/?whom=203.0.113.9&days=7");
        var fields = PageServer.Fields(prefilled.Html, "/ban");
        Assert.Equal(("203.0.113.9", "7"), (fields["whom"], fields["days"]));
        Assert.Equal(2, server.Database.GetActiveBans(Now).Count);
    }

    /// <summary>
    /// Names, worlds, reasons, limits and even a listed login are HTML-encoded, and anything hidden in them shown as escapes: no
    /// markup from outside reaches the page.
    /// </summary>
    [Fact]
    public async Task EverythingFromOutsideIsEncoded() {
        const string login = "<b>alice</b>@github";
        await using var page = new PageServer(settings: ("LookingGlass:Admin:TailscaleLogins:0", login));
        var server = page.Server;
        Register(server, 31337, "Bob <script>alert(1)</script>", "Li\u001B[31mch");
        var now = Now;
        server.Database.SaveFlag(new FlagRow("user 31337", 31337, null, now - 600, now - 60, "<img src=x onerror=alert(2)>", "why \"<i>\" & 'more'"));
        Assert.True(Actions(server).Ban(new OperatorActor(OperatorTool.CommandLine, "<u>root</u>"), "31337", 7, "Spam <a href=\"https://evil.example\">here</a> & 'there'").Done);

        var answer = await page.GetAsync("/", login);
        Assert.Equal(HttpStatusCode.OK, answer.Status);
        foreach (var markup in new[] { "<script>", "<img", "<i>", "<a href=\"https://evil.example\"", "<u>", "<b>alice", "\u001B" }) {
            Assert.DoesNotContain(markup, answer.Html, StringComparison.Ordinal);
        }

        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", answer.Html);
        Assert.Contains("&lt;img src=x onerror=alert(2)&gt;", answer.Html);
        Assert.Contains("&lt;b&gt;alice&lt;/b&gt;@github", answer.Html);
        Assert.Contains("Li\\u001B[31mch", answer.Text);
        Assert.Contains("Spam <a href=\"https://evil.example\">here</a> & 'there'", answer.Text);
        Assert.Contains("Bob <script>alert(1)</script>", answer.Text);

        // The confirmation pages too.
        var lift = await page.GetAsync(Regex.Match(answer.Html, "href=\"(/lift\\?ban=\\d+)\"").Groups[1].Value, login);
        Assert.DoesNotContain("<script>", lift.Html);
        Assert.Contains("Lift the ban on Bob <script>alert(1)</script>@Li\\u001B[31mch?", lift.Text);
    }

    // ================================================================ banning and lifting

    /// <summary>
    /// <b>Continue...</b> leads to a confirmation page (nothing banned yet); its button bans, as the web page, by the Tailscale
    /// login, from the device's tailnet address, and the page says so at the top. <b>Lift...</b> likewise.
    /// </summary>
    [Fact]
    public async Task BanningAndLiftingGoThroughConfirmationPages() {
        await using var page = new PageServer();
        var server = page.Server;
        Register(server, 31337, "Bob Hatter");
        var main = await page.GetAsync("/");
        var confirm = await page.SubmitAsync(main, "/ban", ("whom", "Bob Hatter@Lich"), ("days", "7"), ("reason", "Spamming invites"));
        Assert.Equal(HttpStatusCode.OK, confirm.Status);
        Assert.Contains("Ban Bob Hatter@Lich?", confirm.Text);
        Assert.Contains("Character Bob Hatter@Lich, Lodestone ID 31337", confirm.Text);
        Assert.Contains("For 7 days, until ", confirm.Text);
        Assert.Contains("Reason, which the player is shown Spamming invites", confirm.Text);
        Assert.Contains("Their connection closes now.", confirm.Text);
        Assert.Empty(server.Database.GetActiveBans(Now));
        Assert.Empty(server.Database.GetOperatorLog(0, 10));

        var done = await page.SubmitAsync(confirm, "/ban/confirm");
        Assert.Equal(HttpStatusCode.SeeOther, done.Status);
        var location = done.Response.Headers.Location!.ToString();
        Assert.StartsWith("/?note=", location);
        var ban = Assert.Single(server.Database.GetActiveBans(Now));
        Assert.Equal((31337L, "web page", PageServer.Login, "Spamming invites"), (ban.UserId!.Value, ban.MadeVia!, ban.MadeBy!, ban.Reason));
        Assert.InRange(ban.ExpiresAt!.Value - ban.CreatedAt, 7 * 86400, 7 * 86400);
        var entry = Assert.Single(server.Database.GetOperatorLog(0, 10));
        Assert.Equal(("web page", PageServer.Login, PageServer.TailnetAddress, "ban", true), (entry.Tool, entry.Who, entry.From, entry.Action, entry.Done));

        var after = await page.GetAsync(location);
        Assert.Contains("Banned Bob Hatter@Lich for 7 days.", after.Text);
        Assert.Contains("on the web page by alice@github", after.Text);
        // Only for whoever did it, and only that once in a while: another login, or another note, shows nothing.
        Assert.DoesNotContain("Banned Bob", (await page.GetAsync("/?note=00")).Text);

        var liftLink = Regex.Match(after.Html, "href=\"(/lift\\?ban=\\d+)\"").Groups[1].Value;
        var lift = await page.GetAsync(liftLink);
        Assert.Contains("Lift the ban on Bob Hatter@Lich?", lift.Text);
        Assert.Contains("on the web page by alice@github", lift.Text);
        Assert.Contains("Reason: Spamming invites", lift.Text);
        Assert.Single(server.Database.GetActiveBans(Now));

        var lifted = await page.SubmitAsync(lift, "/lift/confirm");
        Assert.Equal(HttpStatusCode.SeeOther, lifted.Status);
        Assert.Empty(server.Database.GetActiveBans(Now));
        var history = Assert.Single(server.Database.GetBanHistory(Now));
        Assert.Equal(("web page", PageServer.Login), (history.LiftedVia!, history.LiftedBy!));
        Assert.Contains("Lifted the ban on Bob Hatter@Lich.", (await page.GetAsync(lifted.Response.Headers.Location!.ToString())).Text);
    }

    /// <summary>An address's confirmation page says how many it shuts out; one not registered yet is banned by its Lodestone ID so it can't register.</summary>
    [Fact]
    public async Task ConfirmationPagesSayWhatABanCovers() {
        await using var page = new PageServer();
        var main = await page.GetAsync("/");
        var address = await page.SubmitAsync(main, "/ban", ("whom", "203.0.113.7/24"), ("days", ""), ("reason", "Flooding"));
        Assert.Contains("Ban address 203.0.113.0/24?", address.Text);
        Assert.Contains("Everyone connecting from 203.0.113.0/24 (256 addresses) is shut out, including people who share it.", address.Text);
        Assert.Contains("For Until you lift it", address.Text);
        Assert.Equal("203.0.113.0/24", PageServer.Fields(address.Html, "/ban/confirm")["whom"]);

        var unregistered = await page.SubmitAsync(await page.GetAsync("/"), "/ban", ("whom", "88888"), ("days", "30"));
        Assert.Contains("Ban user 88888?", unregistered.Text);
        Assert.Contains("not registered on this server: the ban stops it registering", unregistered.Text);
        Assert.Contains("None: the player is told only that this server's operator blocked them.", unregistered.Text);
        Assert.Empty(page.Server.Database.GetActiveBans(Now));
    }

    /// <summary>
    /// A ban the rules refuse comes back to the form with why, in the command line's words, filled in as it was: the server's own
    /// address without the tick box, an operator character, a target that names nobody, days out of range, the hour's 30.
    /// With the tick box, the server's own address can be banned.
    /// </summary>
    [Fact]
    public async Task ABanTheRulesRefuseComesBackToTheFormWithWhy() {
        await using var page = new PageServer(settings: ("LookingGlass:Operators:Characters:0", "55555"));
        var server = page.Server;
        Register(server, 55555, "Alice Rabbit", "Odin");

        async Task<Answer> Continue(string whom, string days = "7", bool force = false) =>
            await page.SubmitAsync(await page.GetAsync("/"), "/ban", ("whom", whom), ("days", days), ("reason", "Testing"), ("force", force ? "yes" : ""));

        var proxy = await Continue("127.0.0.1");
        Assert.Equal(HttpStatusCode.OK, proxy.Status);
        Assert.Contains("127.0.0.1", proxy.Text);
        Assert.Contains("Nothing was banned.", proxy.Text);
        Assert.Contains("tick \"This is the server's own or its proxy's address, and I mean it\"", proxy.Text);
        Assert.Equal(("127.0.0.1", "7", "Testing"), (PageServer.Fields(proxy.Html, "/ban")["whom"], PageServer.Fields(proxy.Html, "/ban")["days"], PageServer.Fields(proxy.Html, "/ban")["reason"]));

        Assert.Contains("is listed as an operator", (await Continue("Alice Rabbit@Odin")).Text);
        Assert.Contains("is listed as an operator", (await Continue("55555", force: true)).Text);
        Assert.Contains("No character Nobody@Lich is registered on this server.", (await Continue("Nobody@Lich")).Text);
        Assert.Contains("isn't an address or a prefix", (await Continue("not a target")).Text);
        Assert.Contains("An IPv4 ban covers a /16 at the widest", (await Continue("10.0.0.0/8")).Text);
        Assert.Contains("For: a whole number of days, 1 to 36,500", (await Continue("31337", days: "0")).Text);
        Assert.Contains("For: a whole number of days", (await Continue("31337", days: "seven")).Text);
        Assert.Contains("one line of plain text", (await page.SubmitAsync(await page.GetAsync("/"), "/ban", ("whom", "31337"), ("reason", new string('x', 301)))).Text);
        Assert.Empty(server.Database.GetActiveBans(Now));
        // Continuing changes nothing, so it isn't in the audit trail.
        Assert.Empty(server.Database.GetOperatorLog(0, 10));

        // With the tick box: the confirmation page warns, and its button bans.
        var forced = await Continue("127.0.0.1", force: true);
        Assert.Contains("Ban address 127.0.0.1?", forced.Text);
        Assert.Contains("this shuts out every player", forced.Text);
        Assert.Equal(HttpStatusCode.SeeOther, (await page.SubmitAsync(forced, "/ban/confirm")).Status);
        Assert.Equal("127.0.0.1", Assert.Single(server.Database.GetActiveBans(Now)).Address);

        // The page's 30 bans an hour, then why not.
        var actions = Actions(server);
        for (var i = 1; i < OperatorPolicy.WebPageBansPerHour; i++) {
            Assert.True(actions.Ban(new OperatorActor(OperatorTool.WebPage, PageServer.Login), (1000 + i).ToString(CultureInfo.InvariantCulture), 1, null).Done);
        }

        var full = await Continue("31337");
        Assert.Contains("30 bans have been made on the web page in the last hour, the most it allows; use the command line for more.", full.Text);
        // Checked again when confirmed: a confirmation page from before the 30th doesn't get round it.
        Assert.Equal(OperatorPolicy.WebPageBansPerHour, server.Database.GetActiveBans(Now).Count);
    }

    /// <summary>A confirmation page made before the hour's 30th ban is checked again when its button is pressed.</summary>
    [Fact]
    public async Task TheHoursLimitIsCheckedAgainWhenConfirmed() {
        await using var page = new PageServer();
        var server = page.Server;
        var confirm = await page.SubmitAsync(await page.GetAsync("/"), "/ban", ("whom", "31337"), ("days", "1"));
        Assert.Contains("Ban user 31337?", confirm.Text);
        var actions = Actions(server);
        for (var i = 0; i < OperatorPolicy.WebPageBansPerHour; i++) {
            Assert.True(actions.Ban(new OperatorActor(OperatorTool.WebPage, PageServer.Login), (1000 + i).ToString(CultureInfo.InvariantCulture), 1, null).Done);
        }

        var refused = await page.SubmitAsync(confirm, "/ban/confirm");
        Assert.Equal(HttpStatusCode.OK, refused.Status);
        Assert.Contains("30 bans have been made on the web page in the last hour", refused.Text);
        Assert.DoesNotContain(server.Database.GetActiveBans(Now), ban => ban.UserId == 31337);
        Assert.False(server.Database.GetOperatorLog(0, 1).Single().Done);
    }

    /// <summary>A lift confirmed after the ban shown was replaced by another lifts nothing: the token named the ban shown.</summary>
    [Fact]
    public async Task ALiftConfirmedAfterTheBanWasReplacedLiftsNothing() {
        await using var page = new PageServer();
        var server = page.Server;
        var actions = Actions(server);
        var cli = new OperatorActor(OperatorTool.CommandLine, "alice");
        Assert.True(actions.Ban(cli, "203.0.113.0/24", 1, "Flooding").Done);
        var main = await page.GetAsync("/");
        var lift = await page.GetAsync(Regex.Match(main.Html, "href=\"(/lift\\?ban=\\d+)\"").Groups[1].Value);
        Assert.True(actions.Ban(cli, "203.0.113.0/24", 30, "Flooding again").Done);

        var refused = await page.SubmitAsync(lift, "/lift/confirm");
        Assert.Equal(HttpStatusCode.OK, refused.Status);
        Assert.Contains("isn't in force any more", refused.Text);
        Assert.Equal("Flooding again", Assert.Single(server.Database.GetActiveBans(Now)).Reason);
        Assert.False(server.Database.GetOperatorLog(0, 1).Single().Done);
    }

    // ================================================================ operators

    /// <summary>
    /// <b>Restore operator status...</b> shows the pinned and current fingerprints; confirmed after the keys changed again, it is
    /// refused (the token names the keys shown), and nothing is pinned; from a fresh page, it pins the keys shown, which Discord
    /// hears of at once, with the ping.
    /// </summary>
    [Fact]
    public async Task RestoringPinsOnlyTheKeysThePageShowed() {
        var discord = new FakeDiscord();
        await using var page = new PageServer(discord: discord, settings: [
            ("LookingGlass:Operators:Characters:0", "55555"), FakeDiscord.On, ("LookingGlass:Alerts:PingRoleId", Role), ("LookingGlass:Alerts:Restarts", "false"),
        ]);
        var server = page.Server;
        var first = Register(server, 55555, "Alice Rabbit", "Odin");
        Assert.True(Actions(server).PinByFingerprint(new OperatorActor(OperatorTool.CommandLine, "alice"), "55555", first).Done);
        // Posted (as the command line's would be, at the alerts' next look), so the restore below is told as one.
        server.Factory.Services.GetRequiredService<ServerAlerts>().Nudge();
        await discord.WaitForPost(message => message.Content.Contains("**Operator keys pinned** on the command line"));
        var second = Register(server, 55555, "Alice Rabbit", "Odin");

        var main = await page.GetAsync("/");
        Assert.Contains("<details class=\"fold\" id=\"operators\" open>", main.Html);
        Assert.Contains("Operators (1) · 1 to restore", main.Text);
        Assert.Contains("NOT an operator now", main.Text);
        Assert.Contains(first, main.Text);
        Assert.Contains(second, main.Text);

        var restore = await page.GetAsync("/pin?user=55555");
        Assert.Contains("Restore operator status for Alice Rabbit@Odin?", restore.Text);
        Assert.Contains($"Pinned keys {first}", restore.Text);
        Assert.Contains($"Keys now (key {server.Database.GetUser(55555)!.KeyVersion}) {second}", restore.Text);
        Assert.Contains("check that the fingerprint under \"Your identity\" is exactly the \"Keys now\" one above", restore.Text);
        Assert.Contains("don't restore", restore.Text);

        // The keys change again before the click: refused, nothing pinned.
        var third = Register(server, 55555, "Alice Rabbit", "Odin");
        var refused = await page.SubmitAsync(restore, "/pin/confirm");
        Assert.Equal(HttpStatusCode.OK, refused.Status);
        Assert.Contains($"changed again since the page showed them (they are {third} now). Nothing was pinned.", refused.Text);
        Assert.Equal(IdentityKeys.FingerprintOfHash(server.Database.GetOperatorPin(55555)!.KeyHash), first);
        Assert.Equal(OperatorStatus.KeysChanged, Actions(server).StatusOf(55555));

        // A fresh page shows the keys now; confirming pins them.
        var fresh = await page.GetAsync("/pin?user=55555");
        Assert.Contains($"Keys now (key {server.Database.GetUser(55555)!.KeyVersion}) {third}", fresh.Text);
        var done = await page.SubmitAsync(fresh, "/pin/confirm");
        Assert.Equal(HttpStatusCode.SeeOther, done.Status);
        Assert.Contains($"Alice Rabbit@Odin is an operator again, with keys {third}.", (await page.GetAsync(done.Response.Headers.Location!.ToString())).Text);
        var pin = server.Database.GetOperatorPin(55555)!;
        Assert.Equal((third, "web page", PageServer.Login), (IdentityKeys.FingerprintOfHash(pin.KeyHash), pin.PinnedVia, pin.PinnedBy));
        Assert.Equal(OperatorStatus.Operator, Actions(server).StatusOf(55555));
        var posted = await discord.WaitForPost(message => message.Content.Contains("**Operator status restored** on the web page"));
        Assert.StartsWith($"<@&{Role}> ", posted.Content);
        Assert.Contains($"now pinned to keys {third}", posted.Content);
        Assert.Contains($"the web page's audit trail ({PageServer.Url})", posted.Content);
        Assert.DoesNotContain(PageServer.Login, posted.Content);

        var trail = server.Database.GetOperatorLog(0, 10).Where(row => row.Tool == "web page").ToList();
        Assert.Equal([true, false], trail.Select(row => row.Done));
    }

    /// <summary>
    /// <b>Pin...</b>, for a listed character never pinned, shows only the keys now; the page can't pin a character the settings
    /// don't list, nor one already an operator.
    /// </summary>
    [Fact]
    public async Task PinningAListedCharacterThatWasNeverPinned() {
        await using var page = new PageServer(settings: [("LookingGlass:Operators:Characters:0", "77777"), ("LookingGlass:Operators:Characters:1", "88888")]);
        var server = page.Server;
        var keys = Register(server, 77777, "Alice Hearts");
        Register(server, 4242, "Carol Queen");
        var main = await page.GetAsync("/");
        Assert.Contains("Operators (2) · 1 to pin", main.Text);
        Assert.Contains($"Not pinned yet. Keys now {keys}", main.Text);
        Assert.Contains("Not registered on this server, so it has no keys to pin yet.", main.Text);
        Assert.Contains("href=\"/pin?user=77777\"", main.Html);

        var pin = await page.GetAsync("/pin?user=77777");
        Assert.Contains("Pin operator keys for Alice Hearts@Lich?", pin.Text);
        Assert.DoesNotContain("Pinned keys", pin.Text);
        Assert.Contains($"Keys now (key {server.Database.GetUser(77777)!.KeyVersion}) {keys}", pin.Text);
        var done = await page.SubmitAsync(pin, "/pin/confirm");
        Assert.Equal(HttpStatusCode.SeeOther, done.Status);
        Assert.Contains($"Alice Hearts@Lich is an operator now, with keys {keys}.", (await page.GetAsync(done.Response.Headers.Location!.ToString())).Text);
        Assert.Contains("Operator. Keys", (await page.GetAsync("/")).Text);
        Assert.Contains("nothing to pin", (await page.GetAsync("/pin?user=77777")).Text);

        // Not listed: no page to pin it, and a forged form for it is refused.
        Assert.Contains("isn't listed in LookingGlass:Operators:Characters", (await page.GetAsync("/pin?user=4242")).Text);
        var forged = await page.PostAsync("/pin/confirm", [
            new("user", "4242"), new("keys", Convert.ToHexString(server.Database.GetUser(4242)!.Keys.Hash)), new("token", PageServer.Fields(pin.Html, "/pin/confirm")["token"]),
        ]);
        Assert.Equal(HttpStatusCode.Forbidden, forged.Status);
        Assert.Null(server.Database.GetOperatorPin(4242));
    }

    // ================================================================ the rest

    /// <summary><b>Send a test alert</b> (only with alerts on) posts one message to the Discord channel; alerts link the page.</summary>
    [Fact]
    public async Task TheTestAlertAndThePagesLinkInAlerts() {
        var discord = new FakeDiscord();
        await using var page = new PageServer(discord: discord, settings: [FakeDiscord.On, ("LookingGlass:Alerts:Restarts", "false")]);
        Register(page.Server, 31337, "Bob Hatter");
        // Alerts that wait for others to go with them wait a moment here, rather than a minute.
        page.Server.Factory.Services.GetRequiredService<ServerAlerts>().Poster!.BatchWindow = TimeSpan.FromMilliseconds(200);
        var main = await page.GetAsync("/");
        Assert.Contains("Send a test alert", main.Text);
        var sent = await page.SubmitAsync(main, "/test-alert");
        Assert.Equal(HttpStatusCode.SeeOther, sent.Status);
        Assert.Contains("Sent a test alert to the Discord channel.", (await page.GetAsync(sent.Response.Headers.Location!.ToString())).Text);
        var test = await discord.WaitForPost(message => message.Content.Contains("**Test alert**"));
        Assert.Empty(test.Roles);

        // A ban made on the page is posted at once, with the page's link to lift it.
        var confirm = await page.SubmitAsync(await page.GetAsync("/"), "/ban", ("whom", "31337"), ("days", "7"), ("reason", "Spamming invites"));
        await page.SubmitAsync(confirm, "/ban/confirm");
        var ban = await discord.WaitForPost(message => message.Content.Contains("**Ban made** on the web page"));
        Assert.Contains($"Lift: {PageServer.Url} or `LG --unban 31337`", ban.Content);
        Assert.DoesNotContain(PageServer.Login, ban.Content);
    }

    /// <summary>
    /// With the page on, alerts link it as well as giving the commands (a flag, an automatic block, a ban made, an operator's other
    /// keys, a pin, the daily summary, a cut message); without it, they give the commands alone, as before.
    /// </summary>
    [Fact]
    public void AlertsLinkThePageWhenItIsOn() {
        const string url = PageServer.Url;
        using var keys = IdentityKeys.Generate();
        var user = new UserRow(31337, "Bob Hatter", 73, "Lich", keys.SigningPublicKey, keys.AgreementPublicKey, [], 1, false);
        var character = new FlagRow("user 31337", 31337, null, 0, 0, "LookupBurst", "refused in 30 of the last 60 minutes");
        var address = new FlagRow("address 203.0.113.9", null, "203.0.113.9", 0, 0, "ConnectionsPerIp", "refused by 4 different limits within 10 minutes");
        var ban = new BanRow(1, 31337, null, "Spamming invites", 0, 7 * 86400, null, false) { MadeVia = BanTools.WebPage, MadeBy = PageServer.Login };
        var addressBan = new BanRow(2, null, "203.0.113.0/24", "", 0, 86400, null, false) { MadeVia = BanTools.CommandLine };
        var block = new BanRow(3, null, "198.51.100.7", "", 0, 900, null, true) { MadeVia = BanTools.Automatic };
        var pin = new OperatorPinRow(31337, new byte[32], 0, BanTools.WebPage, PageServer.Login);
        var summary = new DailySummary(86400, 1, 0, 0, [], 0, 0, 1, 0, null, null, 0, "0.2.0", TimeSpan.FromDays(3), 1 << 20, 0);

        Assert.Contains($"Look: {url} or `LG --bans`", AlertWords.Flagged(character, user, 24, url).Text);
        Assert.Contains($"Full address on the web page: {url} or `LG --bans`", AlertWords.Flagged(address, null, 24, url).Text);
        Assert.Contains($"Look: {url} (ban it for longer, or lift the block), or `LG --bans`", AlertWords.Blocked(block, new AbuseOptions(), url).Text);
        Assert.Contains($"Lift: {url} or `LG --unban 31337`", AlertWords.BanMade(ban, user, null, url).Text);
        Assert.Contains($"Lift: {url}, or `LG --bans` shows the full address", AlertWords.BanMade(addressBan, null, null, url).Text);
        Assert.Contains($"lift it ({url} or `LG --unban 31337`)", AlertWords.BanMade(ban with { MadeVia = BanTools.Plugin, MadeBy = "Alice Wonderland@Lich" }, user, null, url).Text);
        Assert.Contains($"restore it: {url} or `LG --operator-pin 31337", AlertWords.OperatorOtherKeys(user, pin, url).Text);
        Assert.Contains($"the web page's audit trail ({url}) or `LG --audit`", AlertWords.OperatorPinned(31337, user, pin, null, url).Text);
        Assert.EndsWith($"\nWeb page: {url}", AlertWords.Summary(summary, url).Text);
        var many = Enumerable.Range(0, 40).Select(i => new Alert(new string('x', 100), AlertKind.Batched)).ToList();
        Assert.Contains($"more: see the web page, {url}, or `LG --bans`", AlertWords.Message(many, null, false, out _, out _, url));
        // The page's address and the Tailscale login: the address is linked, the login never posted.
        Assert.DoesNotContain(PageServer.Login, AlertWords.BanMade(ban, user, null, url).Text);

        // Without the page: the commands alone.
        Assert.DoesNotContain("https://lg-test", AlertWords.Flagged(character, user, 24).Text + AlertWords.BanMade(ban, user, null).Text + AlertWords.Summary(summary).Text
                                                  + AlertWords.OperatorOtherKeys(user, pin).Text + AlertWords.OperatorPinned(31337, user, pin, null).Text);
        Assert.Contains("Lift: `LG --unban 31337`", AlertWords.BanMade(ban, user, null).Text);
    }

    /// <summary>Without alerts, there is no test button, and the footer says alerts are off.</summary>
    [Fact]
    public async Task NoTestAlertWithoutAlerts() {
        await using var page = new PageServer();
        var main = await page.GetAsync("/");
        Assert.DoesNotContain("/test-alert", main.Html);
        Assert.Contains("Discord alerts are off.", main.Text);
    }

    /// <summary>Times are shown in <c>Admin:TimeZone</c>, each with the zone's abbreviation (or offset), and the page says which zone.</summary>
    [Fact]
    public async Task TimesAreInTheOwnersTimeZone() {
        await using var page = new PageServer(settings: ("LookingGlass:Admin:TimeZone", "America/New_York"));
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        Assert.True(Actions(page.Server).Ban(new OperatorActor(OperatorTool.CommandLine, "alice"), "31337", 1, null).Done);
        var ban = Assert.Single(page.Server.Database.GetActiveBans(Now));
        var main = await page.GetAsync("/");
        Assert.Contains($"until {PageTime.Format(ban.ExpiresAt!.Value, zone)} (1 day)", main.Text);
        Assert.Contains($"Times are America/New_York ({PageTime.Abbreviation(zone, DateTimeOffset.UtcNow)} now).", main.Text);
    }
}
