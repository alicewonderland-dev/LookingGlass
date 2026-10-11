using System.Net;
using System.Text.RegularExpressions;
using LookingGlass.Server.Admin;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// The operator web page's forms against another site (see "Forms and CSRF" in docs/operator-tools-plan.md): nothing changes on
/// a GET; every form needs a token the page made, for this login, for this exact action, within 30 minutes, once, and from
/// before no restart; a form must come from the page's own origin; and the security headers come with every answer.
/// </summary>
public sealed class OperatorPageCsrfTests {
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>The confirmation page for banning user 31337 for 7 days.</summary>
    private static async Task<Answer> ConfirmationAsync(PageServer page, string login = PageServer.Login) {
        var main = await page.GetAsync("/", login);
        var fields = PageServer.Fields(main.Html, "/ban");
        fields["whom"] = "31337";
        fields["days"] = "7";
        fields["reason"] = "Spamming invites";
        var confirm = await page.PostAsync("/ban", fields, login);
        Assert.Contains("Ban user 31337?", confirm.Text);
        return confirm;
    }

    private static void AssertNothingBanned(PageServer page) => Assert.Empty(page.Server.Database.GetActiveBans(Now));

    /// <summary>A form without a token, or with one that isn't a token at all, does nothing.</summary>
    [Fact]
    public async Task AFormWithoutAGoodTokenDoesNothing() {
        await using var page = new PageServer();
        foreach (var token in new[] { null, "", "not-a-token", new string('A', 87), "AQ" }) {
            var form = new Dictionary<string, string> { ["whom"] = "31337", ["days"] = "7", ["reason"] = "" };
            if (token != null) {
                form["token"] = token;
            }

            foreach (var path in new[] { "/ban", "/ban/confirm", "/test-alert" }) {
                var answer = await page.PostAsync(path, form);
                Assert.Equal(HttpStatusCode.Forbidden, answer.Status);
                Assert.Contains("That form isn't one this page made for you, so nothing was done.", answer.Text);
            }
        }

        AssertNothingBanned(page);
        Assert.Empty(page.Server.Database.GetOperatorLog(0, 10));
    }

    /// <summary>Another login's token does nothing (and doesn't use it up for its own login).</summary>
    [Fact]
    public async Task AnotherLoginsTokenDoesNothing() {
        await using var page = new PageServer(settings: ("LookingGlass:Admin:TailscaleLogins:1", "bob@github"));
        var confirm = await ConfirmationAsync(page);
        var fields = PageServer.Fields(confirm.Html, "/ban/confirm");
        var refused = await page.PostAsync("/ban/confirm", fields, login: "bob@github");
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Contains("isn't one this page made for you", refused.Text);
        AssertNothingBanned(page);

        Assert.Equal(HttpStatusCode.SeeOther, (await page.PostAsync("/ban/confirm", fields)).Status);
        Assert.Single(page.Server.Database.GetActiveBans(Now));
    }

    /// <summary>
    /// A confirmation token works for the action its page showed only: not for another whom, other days, another reason, the
    /// tick box, nor another form (a lift, a pin, the test alert).
    /// </summary>
    [Fact]
    public async Task AConfirmationTokenWorksForItsOwnActionOnly() {
        await using var page = new PageServer();
        var confirm = await ConfirmationAsync(page);
        var fields = PageServer.Fields(confirm.Html, "/ban/confirm");
        foreach (var (name, value) in new[] { ("whom", "4242"), ("whom", "203.0.113.9"), ("days", "30"), ("days", ""), ("reason", "Something else"), ("force", "yes") }) {
            var changed = new Dictionary<string, string>(fields) { [name] = value };
            var refused = await page.PostAsync("/ban/confirm", changed);
            Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
            AssertNothingBanned(page);
        }

        foreach (var (path, form) in new (string, Dictionary<string, string>)[] {
                     ("/lift/confirm", new() { ["ban"] = "1", ["token"] = fields["token"] }),
                     ("/pin/confirm", new() { ["user"] = "31337", ["keys"] = new string('0', 64), ["token"] = fields["token"] }),
                     ("/test-alert", new() { ["token"] = fields["token"] }),
                     ("/ban", new() { ["whom"] = "31337", ["token"] = fields["token"] }),
                 }) {
            Assert.Equal(HttpStatusCode.Forbidden, (await page.PostAsync(path, form)).Status);
        }

        // None of those used it up: the action it was made for still works.
        Assert.Equal(HttpStatusCode.SeeOther, (await page.PostAsync("/ban/confirm", fields)).Status);
        Assert.Equal(31337, Assert.Single(page.Server.Database.GetActiveBans(Now)).UserId);
    }

    /// <summary>A token works once: the second press of the same confirmation does nothing more.</summary>
    [Fact]
    public async Task ATokenWorksOnce() {
        await using var page = new PageServer();
        var confirm = await ConfirmationAsync(page);
        Assert.Equal(HttpStatusCode.SeeOther, (await page.SubmitAsync(confirm, "/ban/confirm")).Status);
        var again = await page.SubmitAsync(confirm, "/ban/confirm");
        Assert.Equal(HttpStatusCode.Forbidden, again.Status);
        Assert.Contains("That form was sent already (each works once). Check and confirm again.", again.Text);
        Assert.Single(page.Server.Database.GetActiveBans(Now));
        Assert.Single(page.Server.Database.GetOperatorLog(0, 10));

        // The main page's form too.
        var main = await page.GetAsync("/");
        Assert.Contains("Ban user 31337?", (await page.SubmitAsync(main, "/ban", ("whom", "31337"))).Text);
        Assert.Contains("sent already", (await page.SubmitAsync(main, "/ban", ("whom", "31337"))).Text);
    }

    /// <summary>A token older than 30 minutes is out of date.</summary>
    [Fact]
    public async Task ATokenExpiresAfterThirtyMinutes() {
        var clock = new StoppedClock();
        await using var page = new PageServer(clock);
        var confirm = await ConfirmationAsync(page);
        clock.Advance(TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(2));
        var late = await page.SubmitAsync(confirm, "/ban/confirm");
        Assert.Equal(HttpStatusCode.Forbidden, late.Status);
        Assert.Contains("This page is out of date: its forms work for 30 minutes. Check and confirm again.", late.Text);
        AssertNothingBanned(page);

        // Within the 30 minutes, it works.
        var fresh = await ConfirmationAsync(page);
        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(HttpStatusCode.SeeOther, (await page.SubmitAsync(fresh, "/ban/confirm")).Status);
    }

    /// <summary>After a restart, a page from before is out of date, and says so.</summary>
    [Fact]
    public async Task APageFromBeforeARestartIsOutOfDate() {
        var directory = Path.Combine(Path.GetTempPath(), "lgt-" + Guid.NewGuid().ToString("N"));
        try {
            Answer confirm;
            await using (var before = new PageServer(directory: directory)) {
                confirm = await ConfirmationAsync(before);
            }

            await using var after = new PageServer(directory: directory);
            var stale = await after.SubmitAsync(confirm, "/ban/confirm");
            Assert.Equal(HttpStatusCode.Forbidden, stale.Status);
            Assert.Contains("This page is out of date (the server restarted). Check and confirm again.", stale.Text);
            // With the page again, to check and confirm from.
            Assert.Contains("Ban someone", stale.Text);
            AssertNothingBanned(after);
        } finally {
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// A form must come from the page's own origin (<c>Admin:Url</c>); without an Origin (or with <c>null</c>, which browsers send
    /// with the page's no-referrer policy), the browser's <c>Sec-Fetch-Site</c> must say same-origin. Anything else does nothing,
    /// even with a good token.
    /// </summary>
    [Theory]
    [InlineData("https://evil.example", null, false)]
    [InlineData("https://lg-test.tailnet-0000.ts.net:8444.evil.example", null, false)]
    [InlineData("http://lg-test.tailnet-0000.ts.net:8444", null, false)]
    [InlineData("https://lg-test.tailnet-0000.ts.net", null, false)]
    [InlineData("https://evil.example", "same-origin", false)]
    [InlineData(null, null, false)]
    [InlineData(null, "cross-site", false)]
    [InlineData(null, "same-site", false)]
    [InlineData(null, "none", false)]
    [InlineData("null", null, false)]
    [InlineData("null", "cross-site", false)]
    [InlineData(PageServer.Origin, null, true)]
    [InlineData("HTTPS://LG-TEST.tailnet-0000.ts.net:8444", null, true)]
    [InlineData(null, "same-origin", true)]
    [InlineData("null", "same-origin", true)]
    public async Task AFormMustComeFromThePagesOwnOrigin(string? origin, string? fetchSite, bool allowed) {
        var logs = new CapturingLoggerProvider();
        await using var page = new PageServer(logs: logs);
        var confirm = await ConfirmationAsync(page);
        var answer = await page.PostAsync("/ban/confirm", PageServer.Fields(confirm.Html, "/ban/confirm"), origin: origin, fetchSite: fetchSite);
        if (allowed) {
            Assert.Equal(HttpStatusCode.SeeOther, answer.Status);
            Assert.Single(page.Server.Database.GetActiveBans(Now));
        } else {
            Assert.Equal(HttpStatusCode.Forbidden, answer.Status);
            Assert.Contains("That form didn't come from this page, so nothing was done.", answer.Text);
            AssertNothingBanned(page);
            Assert.Contains(logs.Entries, entry => entry.Message.Contains("a form came from another site"));
        }
    }

    /// <summary>Nothing changes on a GET: the form addresses answer 405, and a confirmation page (a GET) changes nothing.</summary>
    [Fact]
    public async Task NothingChangesOnAGet() {
        var discord = new FakeDiscord();
        await using var page = new PageServer(discord: discord, settings: [FakeDiscord.On, ("LookingGlass:Alerts:Restarts", "false")]);
        var confirm = await ConfirmationAsync(page);
        var fields = PageServer.Fields(confirm.Html, "/ban/confirm");
        var query = string.Join('&', fields.Select(field => $"{field.Key}={Uri.EscapeDataString(field.Value)}"));
        foreach (var path in new[] { $"/ban/confirm?{query}", $"/ban?{query}", "/test-alert", "/lift/confirm?ban=1", "/pin/confirm?user=1" }) {
            var answer = await page.GetAsync(path);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, answer.Status);
            Assert.Equal("POST", answer.Header("Allow"));
        }

        AssertNothingBanned(page);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await page.PostAsync("/", fields)).Status);

        // The lift confirmation page is a GET: the ban stays until its button is pressed.
        Assert.Equal(HttpStatusCode.SeeOther, (await page.PostAsync("/ban/confirm", fields)).Status);
        var main = await page.GetAsync("/");
        var lift = await page.GetAsync(Regex.Match(main.Html, "href=\"(/lift\\?ban=\\d+)\"").Groups[1].Value);
        Assert.Contains("Lift the ban on user 31337?", lift.Text);
        Assert.Single(page.Server.Database.GetActiveBans(Now));
        await Task.Delay(300, Ct);
        Assert.DoesNotContain(discord.Posts, message => message.Content.Contains("Test alert"));
    }

    /// <summary>Every answer, whatever it is, carries the security headers: a refusal, a page, the stylesheet, a redirect, an error.</summary>
    [Fact]
    public async Task EveryAnswerCarriesTheSecurityHeaders() {
        await using var page = new PageServer();
        var confirm = await ConfirmationAsync(page);
        var answers = new List<Answer> {
            await page.GetAsync("/", login: null),
            await page.GetAsync("/", funnel: true),
            await page.GetAsync("/"),
            await page.GetAsync("/style.css"),
            await page.GetAsync("/nothing/here"),
            await page.GetAsync("/ban/confirm"),
            await page.GetAsync("/lift?ban=999"),
            await page.GetAsync("/pin?user=999"),
            await page.PostAsync("/ban/confirm", PageServer.Fields(confirm.Html, "/ban/confirm"), origin: "https://evil.example"),
            await page.PostAsync("/ban/confirm", new Dictionary<string, string> { ["token"] = "x" }),
            await page.SubmitAsync(confirm, "/ban/confirm"),
        };
        var request = PageServer.Request(HttpMethod.Post, "/ban");
        request.Headers.TryAddWithoutValidation("Origin", PageServer.Origin);
        request.Content = new StringContent("{\"whom\":\"31337\"}", System.Text.Encoding.UTF8, "application/json");
        answers.Add(await page.SendAsync(request));

        Assert.Contains(answers, answer => answer.Status == HttpStatusCode.SeeOther);
        Assert.Contains(answers, answer => answer.Status == HttpStatusCode.BadRequest);
        foreach (var answer in answers) {
            Assert.Equal(OperatorPage.ContentSecurityPolicy, answer.Header("Content-Security-Policy"));
            Assert.Equal("nosniff", answer.Header("X-Content-Type-Options"));
            Assert.Equal("no-referrer", answer.Header("Referrer-Policy"));
            Assert.Equal("no-store", answer.Header("Cache-Control"));
            Assert.Null(answer.Header("Set-Cookie"));
            Assert.Null(answer.Header("Server"));
        }
    }

    /// <summary>The tokens themselves: bound to the login (ignoring case) and the action, from this start only, once, 30 minutes.</summary>
    [Fact]
    public void TokensAreBoundToTheLoginTheActionAndTheStart() {
        var clock = new StoppedClock();
        var tokens = new PageTokens(clock);
        var token = tokens.Make("alice@github", "ban\n31337");
        Assert.Equal(TokenCheck.Wrong, tokens.Check(token, "bob@github", "ban\n31337"));
        Assert.Equal(TokenCheck.Wrong, tokens.Check(token, "alice@github", "ban\n3133"));
        Assert.Equal(TokenCheck.Wrong, tokens.Check(token[..^2] + (token[^2] == 'A' ? "B" : "A") + token[^1], "alice@github", "ban\n31337"));
        Assert.Equal(TokenCheck.Restarted, new PageTokens(clock).Check(token, "alice@github", "ban\n31337"));
        Assert.Equal(TokenCheck.Good, tokens.Check(token, "ALICE@GitHub", "ban\n31337"));
        Assert.Equal(TokenCheck.Used, tokens.Check(token, "alice@github", "ban\n31337"));

        // Length-prefixed: a login and an action can't be shifted into each other.
        var shifted = tokens.Make("alice@github\nban", "31337");
        Assert.Equal(TokenCheck.Wrong, tokens.Check(shifted, "alice@github", "ban\n31337"));

        var old = tokens.Make("alice@github", "x");
        clock.Advance(PageTokens.Lifetime + TimeSpan.FromSeconds(1));
        Assert.Equal(TokenCheck.Expired, tokens.Check(old, "alice@github", "x"));

        // Used ones are forgotten once they would have expired anyway.
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(TokenCheck.Good, tokens.Check(tokens.Make("alice@github", "y"), "alice@github", "y"));
        Assert.Equal(1, tokens.Remembered);
    }
}
