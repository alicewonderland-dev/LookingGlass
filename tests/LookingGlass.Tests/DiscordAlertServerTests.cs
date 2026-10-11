using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LookingGlass.Core.Client;
using LookingGlass.Protocol;
using LookingGlass.Server;
using LookingGlass.Server.Data;
using LookingGlass.Server.Hosting;
using LookingGlass.Server.Realtime;
using LookingGlass.Server.Services;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// Discord alerts in a running server, against a fake webhook: nothing posted (or even tried) without a webhook; a malformed
/// one stops the server without being quoted; the restart alert across real restarts (stopped cleanly, or not); a command
/// line ban (another process) posted by the server, once, across restarts; one made inside the server posted at once; what
/// is waiting posted when the server stops; an operator character signing in with other keys posted once; the most players
/// online at once; and the webhook never in the log.
/// </summary>
public sealed class DiscordAlertServerTests {
    private const string Role = "424242424242424242";

    // Bans are read from the database every second here, rather than every 30.
    private static readonly (string, string) CheckEverySecond = ("LookingGlass:Abuse:BanCheckSeconds", "1");

    private static string Debug(string name) => $"{name}@{ProtocolInfo.DebugWorldName}";

    private static int CommandLine(Harness server, BanCommand.BanAction action, string target, int? days = null, string? reason = null) =>
        BanCommand.Run(new BanCommand.Request(action, target, days, reason), Path.Combine(server.DataDirectory, "lookingglass.db"), new AbuseOptions(), new StringWriter(),
            new StringWriter(), debugAccounts: true);

    /// <summary>Alerts that wait for others to go with them wait a moment here, rather than a minute.</summary>
    private static void PostSoon(Harness server) => server.Factory.Services.GetRequiredService<ServerAlerts>().Poster!.BatchWindow = TimeSpan.FromMilliseconds(200);

    private static string NewDirectory() => Path.Combine(Path.GetTempPath(), "lgt-" + Guid.NewGuid().ToString("N"));

    /// <summary>Without a webhook, nothing is posted, nor tried, whatever happens; the startup line says alerts are off.</summary>
    [Fact]
    public async Task NothingIsPostedWithoutAWebhook() {
        var discord = new FakeDiscord();
        var logs = new CapturingLoggerProvider();
        await using (var server = new Harness(logs: logs, discord: discord, settings: CheckEverySecond)) {
            await server.RegisterAsync("Bob Unposted");
            Assert.Equal(0, CommandLine(server, BanCommand.BanAction.Ban, Debug("Bob Unposted"), 1));
            Assert.False(server.Factory.Services.GetRequiredService<ServerAlerts>().SendTest());
            await Task.Delay(2500, Ct);
        }

        Assert.Empty(discord.Attempts);
        Assert.Contains(logs.Entries, entry => entry.Message.Contains("; Discord alerts off; database "));
    }

    /// <summary>A webhook that isn't one stops the server, saying what is wrong, never what it is (a secret).</summary>
    [Theory]
    [InlineData("https://discordapp.com/api/webhooks/123/Secret-Token")]
    [InlineData("http://discord.com/api/webhooks/123/Secret-Token")]
    [InlineData("https://discord.com/api/webhooks/123/Secret-Token?wait=true")]
    public async Task AMalformedWebhookStopsTheServerWithoutQuotingIt(string url) {
        var logs = new CapturingLoggerProvider();
        await ExitCodeGate.WaitAsync(Ct);
        var exitCode = Environment.ExitCode;
        var directory = NewDirectory();
        try {
            await Assert.ThrowsAnyAsync<Exception>(async () => {
                await using var server = new Harness(directory, logs: logs, settings: ("LookingGlass:Alerts:DiscordWebhookUrl", url));
                await using var raw = await server.ConnectRawAsync();
            });
            var critical = Assert.Single(logs.AtLeast(LogLevel.Critical));
            Assert.Contains("LookingGlass:Alerts:DiscordWebhookUrl", critical);
            Assert.Contains("won't start", critical);
            Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains("Secret-Token"));
            Assert.Equal(1, Environment.ExitCode);
        } finally {
            Environment.ExitCode = exitCode;
            ExitCodeGate.Release();
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// The server posts when it starts: the first time plainly, then whether it stopped cleanly (a real stop notes it) or not
    /// (as if killed); not at all with <c>Alerts:Restarts</c> off. The startup line says only that alerts are on.
    /// </summary>
    [Fact]
    public async Task TheServerPostsWhenItStartsAndHowItLastStopped() {
        var discord = new FakeDiscord();
        var logs = new CapturingLoggerProvider();
        var directory = NewDirectory();
        try {
            await using (new Harness(directory, logs: logs, discord: discord, settings: FakeDiscord.On)) {
                Assert.Equal($"**Server started:** LookingGlass {RequestHandler.ServerVersion}, in Development.",
                    (await discord.WaitForPost(posted => posted.Content.StartsWith("**Server started:**"))).Content);
            }

            await using (new Harness(directory, logs: logs, discord: discord, settings: FakeDiscord.On)) {
                await discord.WaitForPost(posted => posted.Content.Contains("It stopped cleanly at <t:"));
            }

            var killed = new Harness(directory, logs: logs, discord: discord, settings: [FakeDiscord.On, ("LookingGlass:Alerts:Restarts", "false")]);
            await Task.Delay(300, Ct);
            await killed.DisposeAsync();
            // As if it had been killed: no clean stop noted since it started.
            Assert.Equal(1, killed.ExecuteSql("UPDATE server_state SET value = '0' WHERE key = 'server_stopped_cleanly_at';"));

            await using (new Harness(directory, logs: logs, discord: discord, settings: FakeDiscord.On)) {
                await discord.WaitForPost(posted => posted.Content.Contains("It didn't stop cleanly: it had been running since <t:"));
            }

            Assert.Equal(3, discord.Posts.Count(posted => posted.Content.StartsWith("**Server started:**")));
            Assert.Contains(logs.Entries, entry => entry.Message.Contains("; Discord alerts on; database "));
            Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains(FakeDiscord.Token));
        } finally {
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// A ban from the command line (another process, which can't post: the webhook is the service's secret) is posted by the
    /// running server when it picks it up, once: not again by the server started again. Its lift likewise. Logged at Trace,
    /// the server never logs the webhook.
    /// </summary>
    [Fact]
    public async Task ACommandLineBanIsPostedByTheServerOnceAcrossRestarts() {
        var discord = new FakeDiscord();
        var logs = new CapturingLoggerProvider();
        var directory = NewDirectory();
        (string, string)[] settings = [
            FakeDiscord.On, CheckEverySecond, ("Logging:LogLevel:Default", "Trace"), ("Logging:LogLevel:LookingGlass", "Trace"),
            ("Logging:LogLevel:System.Net.Http.HttpClient", "Trace"), ("Logging:LogLevel:Microsoft", "Trace"),
        ];
        try {
            await using (var server = new Harness(directory, logs: logs, discord: discord, settings: settings)) {
                PostSoon(server);
                await server.RegisterAsync("Bob Posted");
                await discord.WaitForPost(posted => posted.Content.StartsWith("**Server started:**"));
                Assert.Equal(0, CommandLine(server, BanCommand.BanAction.Ban, Debug("Bob Posted"), 7, "Testing alerts"));
                var made = await discord.WaitForPost(posted => posted.Content.Contains("**Ban made** on the command line: Bob Posted@Debug (user "));
                Assert.Contains(", for 7 days, until <t:", made.Content);
                Assert.Contains("Reason, which the player is shown: Testing alerts", made.Content);
                // A debug account has no Lodestone page.
                Assert.DoesNotContain("Lodestone", made.Content);
            }

            await using (var again = new Harness(directory, logs: logs, discord: discord, settings: settings)) {
                PostSoon(again);
                await discord.WaitForPost(posted => posted.Content.Contains("It stopped cleanly"));
                await Task.Delay(2500, Ct);
                Assert.Single(discord.Posts, posted => posted.Content.Contains("**Ban made**"));
                Assert.Equal(0, CommandLine(again, BanCommand.BanAction.Unban, Debug("Bob Posted")));
                await discord.WaitForPost(posted => posted.Content.Contains("**Ban lifted** on the command line: Bob Posted@Debug (user "));
                await Task.Delay(2500, Ct);
            }

            Assert.Single(discord.Posts, posted => posted.Content.Contains("**Ban lifted**"));
            Assert.All(discord.Posts, posted => Assert.Equal((0, 4), (posted.Parse, posted.Flags)));
            Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains(FakeDiscord.Token) || entry.Message.Contains("api/webhooks"));
        } finally {
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// Someone with many addresses (a /48, proxies) getting one blocked after another: every block is posted, but the role is
    /// pinged once (at most once every ten minutes), and the later ones wait for the minute's message.
    /// </summary>
    [Fact]
    public async Task AStormOfAutomaticBlocksPingsOnce() {
        var discord = new FakeDiscord();
        await using var server = new Harness(discord: discord, settings: [
            FakeDiscord.On, ("LookingGlass:Alerts:PingRoleId", Role), ("LookingGlass:Abuse:AutoBlockAfterRefusals", "100"),
        ]);
        PostSoon(server);
        await discord.WaitForPost(posted => posted.Content.StartsWith("**Server started:**"));
        var monitor = server.Factory.Services.GetRequiredService<AbuseMonitor>();
        for (var block = 0; block < 6; block++) {
            for (var i = 0; i < 100; i++) {
                monitor.Refused("ConnectionsPerMinutePerIp", null, $"2001:db8:{block}::1");
            }

            await Task.Delay(500, Ct);
        }

        await WaitFor(() => discord.Posts.Sum(posted => posted.Content.Split("blocked automatically:").Length - 1) == 6 ? new object() : null);
        Assert.Single(discord.Posts, posted => posted.Roles.Count > 0);
        Assert.StartsWith($"<@&{Role}> **Urgent: blocked automatically:** address 2001:db8::/40 (shortened)", discord.Posts.Single(posted => posted.Roles.Count > 0).Content);
    }

    /// <summary>The webhook's client follows no redirect: the message, and the webhook's path, go to Discord alone.</summary>
    [Fact]
    public async Task TheWebhooksClientFollowsNoRedirect() {
        await using var server = new Harness();
        HttpMessageHandler handler = server.Factory.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(ServerAlerts.HttpClientName);
        while (handler is DelegatingHandler delegating) {
            handler = delegating.InnerHandler!;
        }

        Assert.False(Assert.IsType<SocketsHttpHandler>(handler).AllowAutoRedirect);
    }

    /// <summary>A ban made inside the server (the web page's, when it comes) is posted at once, not at the next look (30 seconds here).</summary>
    [Fact]
    public async Task ABanMadeInsideTheServerIsPostedAtOnce() {
        var discord = new FakeDiscord();
        await using var server = new Harness(discord: discord, settings: FakeDiscord.On);
        PostSoon(server);
        await server.RegisterAsync("Carol Posted");
        await discord.WaitForPost(posted => posted.Content.StartsWith("**Server started:**"));
        var result = server.Factory.Services.GetRequiredService<OperatorActions>()
            .Ban(new OperatorActor(OperatorTool.WebPage, "someone@github", "100.101.102.103"), Debug("Carol Posted"), 1, null);
        Assert.True(result.Done);
        var posted = await discord.WaitForPost(message => message.Content.Contains("**Ban made** on the web page: Carol Posted@Debug"));
        Assert.DoesNotContain("someone@github", posted.Content);
    }

    /// <summary>What is waiting to be posted when the server stops is posted then, not lost (a flag waits up to a minute for others).</summary>
    [Fact]
    public async Task WhatIsWaitingIsPostedWhenTheServerStops() {
        var discord = new FakeDiscord();
        await using (var server = new Harness(discord: discord, settings: FakeDiscord.On)) {
            await discord.WaitForPost(posted => posted.Content.StartsWith("**Server started:**"));
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            server.Factory.Services.GetRequiredService<ServerAlerts>()
                .Flagged(new FlagRow("user 1234", 1234, null, now, now, "LookupBurst", "refused by limits in 30 of the last 60 minutes"));
            await Task.Delay(300, Ct);
            Assert.DoesNotContain(discord.Posts, posted => posted.Content.Contains("**Flagged:**"));
        }

        Assert.Single(discord.Posts, posted => posted.Content.StartsWith("**Flagged:** user 1234: refused by limits in 30 of the last 60 minutes, by LookupBurst."));
    }

    /// <summary>
    /// A character listed as an operator, pinned to keys it no longer has, is posted as urgent (with the ping) when it signs in,
    /// once: not again at its next login with the same keys.
    /// </summary>
    [Fact]
    public async Task AnOperatorCharacterSigningInWithOtherKeysIsPostedOnce() {
        var discord = new FakeDiscord();
        var lodestone = new FakeLodestone { Name = "Alice Operator", World = "Gilgamesh" };
        await using var server = new Harness(lodestone: lodestone, discord: discord, settings: [
            FakeDiscord.On, ("LookingGlass:Alerts:PingRoleId", Role), ("LookingGlass:Operators:Characters:0", "31337"), ("LookingGlass:PublicUrls:0", "ws://localhost/ws"),
        ]);
        var alice = server.StartClient("Alice Operator");
        await WaitFor(() => alice.Session.Snapshot.State == ConnectionState.Unregistered ? new object() : null);
        var challenge = await alice.Session.StartRegistrationAsync(new Character { Name = "Alice Operator", WorldName = "Gilgamesh" }, Ct);
        lodestone.Profile = challenge.Code;
        await alice.Session.CompleteRegistrationAsync(Ct);
        await WaitFor(() => alice.Session.Snapshot.State == ConnectionState.Ready ? new object() : null);

        // Pinned to keys it doesn't have (as after registering again on another computer); pinned before alerts were on, so not posted as a pin.
        server.ExecuteSql("INSERT INTO operator_pins (user_id, key_hash, pinned_at, pinned_via, pinned_by) VALUES (31337, $hash, 0, 'command line', 'alice');",
            ("$hash", RandomNumberGenerator.GetBytes(32)));
        await alice.Session.DisposeAsync();
        alice = await server.RestartAsync(alice);
        var posted = await discord.WaitForPost(message => message.Content.Contains("an operator character signed in with other keys"));
        Assert.StartsWith($"<@&{Role}> **Urgent: an operator character signed in with other keys:** Alice Operator@Gilgamesh (user 31337), keys " +
                          $"{server.Database.GetUser(31337)!.Keys.Fingerprint}, not the pinned ", posted.Content);
        Assert.Equal(Role, Assert.Single(posted.Roles));

        await alice.Session.DisposeAsync();
        await server.RestartAsync(alice);
        await Task.Delay(500, Ct);
        Assert.Single(discord.Posts, message => message.Content.Contains("other keys"));
        Assert.DoesNotContain(discord.Posts, message => message.Content.Contains("Operator keys pinned"));
    }

    /// <summary>The most players online at once is kept as they log in (for the daily summary), and starts again from those online when taken.</summary>
    [Fact]
    public async Task TheMostPlayersOnlineAtOnceIsKept() {
        await using var server = new Harness();
        var bob = await server.RegisterAsync("Bob Counted");
        await server.RegisterAsync("Carol Counted");
        var (peak, at) = server.Registry.TakePeak();
        Assert.Equal(2, peak);
        Assert.InRange(at, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1);
        await bob.Session.DisposeAsync();
        await WaitFor(() => server.Registry.OnlineCount == 1 ? new object() : null);
        Assert.Equal(2, server.Registry.TakePeak().Peak);
        Assert.Equal(1, server.Registry.TakePeak().Peak);
    }
}
