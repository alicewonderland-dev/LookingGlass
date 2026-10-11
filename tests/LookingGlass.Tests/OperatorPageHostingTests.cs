using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using LookingGlass.Core.Client;
using LookingGlass.Server;
using LookingGlass.Server.Admin;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// Where the operator web page listens, and the settings it needs: a real loopback port (with the startup warning) and a real
/// Unix socket (only its owner may open it, on Linux; a stale socket file is replaced), the server refusing to start with the
/// page anywhere else, on a port Funnel serves, without logins, or with a time zone it doesn't know; and times shown in the
/// owner's time zone with its abbreviation, across a daylight-saving change.
/// </summary>
public sealed class OperatorPageHostingTests {
    private static string NewDirectory() => Path.Combine(Path.GetTempPath(), "lgt-" + Guid.NewGuid().ToString("N"));

    /// <summary>A loopback port nothing listens on (never the public server's 5180).</summary>
    private static int FreePort() {
        while (true) {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint) listener.LocalEndpoint).Port;
            listener.Stop();
            if (port != 5180) {
                return port;
            }
        }
    }

    private static HttpRequestMessage Request(string url, bool identity) {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (identity) {
            request.Headers.TryAddWithoutValidation("Tailscale-User-Login", PageServer.Login);
            request.Headers.TryAddWithoutValidation("X-Forwarded-Host", PageServer.Host);
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", PageServer.TailnetAddress);
        }

        return request;
    }

    // ================================================================ real listeners

    /// <summary>
    /// On a loopback port, the page answers there (403 without the headers <c>tailscale serve</c> sets, the page with them), the
    /// server warns that any program on the machine could connect, and the startup line says where the page is.
    /// </summary>
    [Fact]
    public async Task ThePageListensOnALoopbackPortWithAWarning() {
        var port = FreePort();
        var logs = new CapturingLoggerProvider();
        await using var server = new Harness(logs: logs, settings: [
            ("LookingGlass:Admin:Listen", $"http://127.0.0.1:{port}"), ("LookingGlass:Admin:Url", PageServer.Url), ("LookingGlass:Admin:TailscaleLogins:0", PageServer.Login),
        ]);
        using var client = new HttpClient();
        using (var refused = await client.SendAsync(Request($"http://127.0.0.1:{port}/", identity: false), Ct)) {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains("Not allowed.", await refused.Content.ReadAsStringAsync(Ct));
            Assert.False(refused.Headers.Contains("Server"));
        }

        using (var page = await client.SendAsync(Request($"http://127.0.0.1:{port}/", identity: true), Ct)) {
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains("Signed in: alice@github", await page.Content.ReadAsStringAsync(Ct));
        }

        using (var ws = await client.SendAsync(Request($"http://127.0.0.1:{port}/ws", identity: true), Ct)) {
            Assert.Equal(HttpStatusCode.NotFound, ws.StatusCode);
        }

        // The page's host has no lifetime of its own: one "Application started" at most (the server's), no second set of signal handlers.
        Assert.True(logs.Entries.Count(entry => entry.Message.StartsWith("Application started", StringComparison.Ordinal)) <= 1);
        var warning = Assert.Single(logs.Entries, entry => entry.Message.StartsWith("The operator web page listens on a loopback port", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("any program or user on this machine can connect to it", warning.Message);
        Assert.Contains(logs.Entries, entry => entry.Message.Contains($"; operator web page starting, for {PageServer.Url} (loopback port; 1 login let in; times in UTC); Discord alerts off;"));
    }

    /// <summary>
    /// On a Unix socket (Windows has them too), the page answers there; a stale socket file left there is replaced; on Linux,
    /// only the service's user may open it (mode 0600).
    /// </summary>
    [Fact]
    public async Task ThePageListensOnAUnixSocket() {
        var folder = NewDirectory();
        Directory.CreateDirectory(folder);
        var socket = Path.Combine(folder, "admin.sock");
        // Left by a server that didn't stop cleanly: a socket nothing listens on any more.
        using (var stale = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)) {
            stale.Bind(new UnixDomainSocketEndPoint(socket));
        }

        if (OperatingSystem.IsLinux()) {
            // (Windows removes it as the socket closes.)
            Assert.True(File.Exists(socket));
        }
        var logs = new CapturingLoggerProvider();
        try {
            await using (var server = new Harness(logs: logs, settings: [
                             ("LookingGlass:Admin:Listen", $"unix:{socket}"), ("LookingGlass:Admin:Url", PageServer.Url), ("LookingGlass:Admin:TailscaleLogins:0", PageServer.Login),
                         ])) {
                using var handler = new SocketsHttpHandler {
                    ConnectCallback = async (_, ct) => {
                        var unix = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                        await unix.ConnectAsync(new UnixDomainSocketEndPoint(socket), ct);
                        return new NetworkStream(unix, ownsSocket: true);
                    },
                };
                using var client = new HttpClient(handler);
                using (var page = await client.SendAsync(Request("http://localhost/", identity: true), Ct)) {
                    Assert.Equal(HttpStatusCode.OK, page.StatusCode);
                    Assert.Contains("Ban someone", await page.Content.ReadAsStringAsync(Ct));
                }

                using (var refused = await client.SendAsync(Request("http://localhost/", identity: false), Ct)) {
                    Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
                }

                Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains("loopback port"));
                Assert.Contains(logs.Entries, entry => entry.Message.Contains("(Unix socket; 1 login let in; times in UTC)"));
                if (!OperatingSystem.IsWindows()) {
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(socket));
                }
            }
        } finally {
            DeleteDirectory(folder);
        }
    }

    /// <summary>
    /// If the page can't start where its settings say (something else listening on its socket, a file that isn't a socket in its
    /// place, its port taken), the chat server runs on without it: an error in the log, and an urgent alert with the ping.
    /// </summary>
    [Theory]
    [InlineData("listening")]
    [InlineData("file")]
    [InlineData("port")]
    public async Task IfThePageCantStartTheChatServerRunsOn(string what) {
        var folder = NewDirectory();
        Directory.CreateDirectory(folder);
        var socket = Path.Combine(folder, "admin.sock");
        using var other = new Socket(what == "port" ? AddressFamily.InterNetwork : AddressFamily.Unix, SocketType.Stream, what == "port" ? ProtocolType.Tcp : ProtocolType.Unspecified);
        var port = 0;
        switch (what) {
            case "listening":
                other.Bind(new UnixDomainSocketEndPoint(socket));
                other.Listen();
                break;
            case "file":
                await File.WriteAllTextAsync(socket, "not a socket", Ct);
                break;
            default:
                other.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                other.Listen();
                port = ((IPEndPoint) other.LocalEndPoint!).Port;
                break;
        }

        var discord = new FakeDiscord();
        var logs = new CapturingLoggerProvider();
        try {
            await using (var server = new Harness(logs: logs, discord: discord, settings: [
                             ("LookingGlass:Admin:Listen", what == "port" ? $"http://127.0.0.1:{port}" : $"unix:{socket}"), ("LookingGlass:Admin:Url", PageServer.Url),
                             ("LookingGlass:Admin:TailscaleLogins:0", PageServer.Login), FakeDiscord.On, ("LookingGlass:Alerts:PingRoleId", "424242424242424242"),
                             ("LookingGlass:Alerts:Restarts", "false"),
                         ])) {
                // (Kestrel's own host says so too, when its port is taken.)
                server.ExpectedErrors = message => message.StartsWith("The operator web page didn't start", StringComparison.Ordinal)
                                                   || message.StartsWith("Hosting failed to start", StringComparison.Ordinal);
                var error = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Error && entry.Message.StartsWith("The operator web page", StringComparison.Ordinal));
                Assert.Contains("the chat server runs on without it", error.Message);
                Assert.Contains(what switch { "listening" => "Something is listening on", "file" => "isn't a socket", _ => "" }, error.Message);
                var alert = await discord.WaitForPost(message => message.Content.Contains("**Urgent: the operator web page didn't start:**"));
                Assert.StartsWith("<@&424242424242424242> ", alert.Content);

                // Chat goes on.
                var client = await server.RegisterAsync("Bob Chatting");
                Assert.Equal(ConnectionState.Ready, client.Session.Snapshot.State);
            }

            if (what == "file") {
                Assert.Equal("not a socket", await File.ReadAllTextAsync(socket, Ct));
            }
        } finally {
            other.Dispose();
            DeleteDirectory(folder);
        }
    }

    /// <summary>
    /// On Linux, the page doesn't start with its socket's folder open to others' writing, nor in place of a link (which is never
    /// followed, nor deleted).
    /// </summary>
    [Theory]
    [InlineData("group-writable")]
    [InlineData("link")]
    public async Task OnLinuxTheSocketsFolderMustBePrivate(string what) {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Unix permissions and links: Linux only (checked by hand on the server: step 45 of the bans checklist).");
        var folder = NewDirectory();
        Directory.CreateDirectory(folder);
        var socket = Path.Combine(folder, "admin.sock");
        var target = Path.Combine(folder, "elsewhere.txt");
        if (!OperatingSystem.IsLinux()) {
            return;
        }

        if (what == "link") {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await File.WriteAllTextAsync(target, "keep me", Ct);
            File.CreateSymbolicLink(socket, target);
        } else {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);
        }

        var logs = new CapturingLoggerProvider();
        try {
            await using (var server = new Harness(logs: logs, settings: [
                             ("LookingGlass:Admin:Listen", $"unix:{socket}"), ("LookingGlass:Admin:Url", PageServer.Url), ("LookingGlass:Admin:TailscaleLogins:0", PageServer.Login),
                         ])) {
                server.ExpectedErrors = message => message.StartsWith("The operator web page didn't start", StringComparison.Ordinal);
                var error = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Error);
                Assert.Contains(what == "link" ? "isn't a socket" : "Others can write in", error.Message);
            }

            if (what == "link") {
                Assert.Equal("keep me", await File.ReadAllTextAsync(target, Ct));
                Assert.NotNull(new FileInfo(socket).LinkTarget);
            }
        } finally {
            DeleteDirectory(folder);
        }
    }

    // ================================================================ settings

    /// <summary>
    /// The server won't start with the page listening anywhere but a Unix socket or loopback, on the public server's port, at an
    /// address Funnel can serve (443, 8443, 10000) or that isn't HTTPS, without logins, with a time zone it doesn't know, or with
    /// half of its settings.
    /// </summary>
    [Theory]
    [InlineData("LookingGlass:Admin:Listen", "http://0.0.0.0:5181", "LookingGlass:Admin:Listen")]
    [InlineData("LookingGlass:Admin:Listen", "http://192.168.1.5:5181", "LookingGlass:Admin:Listen")]
    [InlineData("LookingGlass:Admin:Listen", "http://100.101.102.103:5181", "LookingGlass:Admin:Listen")]
    [InlineData("LookingGlass:Admin:Listen", "http://[::]:5181", "LookingGlass:Admin:Listen")]
    [InlineData("LookingGlass:Admin:Listen", "http://localhost:5181", "LookingGlass:Admin:Listen")]
    [InlineData("LookingGlass:Admin:Listen", "https://127.0.0.1:5181", "LookingGlass:Admin:Listen")]
    [InlineData("LookingGlass:Admin:Listen", "http://127.0.0.1", "give the port")]
    [InlineData("LookingGlass:Admin:Listen", "http://127.0.0.1:5180", "is the public server's")]
    [InlineData("LookingGlass:Admin:Listen", "unix:admin.sock", "a full path to a file ending in .sock")]
    [InlineData("LookingGlass:Admin:Listen", "unix:/nowhere-at-all/admin.socket", "a full path to a file ending in .sock")]
    [InlineData("LookingGlass:Admin:Listen", "tcp://127.0.0.1:5181", "LookingGlass:Admin:Listen")]
    [InlineData("LookingGlass:Admin:Url", "https://lg-test.tailnet-0000.ts.net/", "port 443, which Tailscale Funnel can serve")]
    [InlineData("LookingGlass:Admin:Url", "https://lg-test.tailnet-0000.ts.net:443/", "port 443")]
    [InlineData("LookingGlass:Admin:Url", "https://lg-test.tailnet-0000.ts.net:8443/", "port 8443")]
    [InlineData("LookingGlass:Admin:Url", "https://lg-test.tailnet-0000.ts.net:10000/", "port 10000")]
    [InlineData("LookingGlass:Admin:Url", "http://lg-test.tailnet-0000.ts.net:8444/", "LookingGlass:Admin:Url")]
    [InlineData("LookingGlass:Admin:Url", "https://lg-test:8444/", "LookingGlass:Admin:Url")]
    [InlineData("LookingGlass:Admin:Url", "https://lg-test.tailnet-0000.ts.net:8444/admin", "LookingGlass:Admin:Url")]
    [InlineData("LookingGlass:Admin:Url", "", "LookingGlass:Admin:Url isn't")]
    [InlineData("LookingGlass:Admin:TailscaleLogins:0", "", "TailscaleLogins lists nobody")]
    [InlineData("LookingGlass:Admin:TailscaleLogins:0", "Alice Liddell", "LookingGlass:Admin:TailscaleLogins lists")]
    [InlineData("LookingGlass:Admin:TimeZone", "Mars/Olympus_Mons", "LookingGlass:Admin:TimeZone")]
    [InlineData("LookingGlass:Admin:OwnAddresses:0", "the host", "LookingGlass:Admin:OwnAddresses lists")]
    [InlineData("LookingGlass:Admin:OwnAddresses:0", "100.64.0.0/33", "LookingGlass:Admin:OwnAddresses lists")]
    public async Task TheServerWontStartWithThePageSetUpWrongly(string key, string value, string said) {
        var logs = new CapturingLoggerProvider();
        await ExitCodeGate.WaitAsync(Ct);
        var exitCode = Environment.ExitCode;
        var directory = NewDirectory();
        try {
            await Assert.ThrowsAnyAsync<Exception>(async () => {
                await using var server = new Harness(directory, logs: logs, settings: [
                    ("LookingGlass:Admin:Listen", "http://127.0.0.1:5998"), ("LookingGlass:Admin:Url", PageServer.Url),
                    ("LookingGlass:Admin:TailscaleLogins:0", PageServer.Login), (key, value),
                ]);
                await using var raw = await server.ConnectRawAsync();
            });
            var critical = Assert.Single(logs.AtLeast(LogLevel.Critical));
            Assert.Contains(said, critical);
            Assert.Contains("won't start", critical);
            Assert.Equal(1, Environment.ExitCode);
        } finally {
            Environment.ExitCode = exitCode;
            ExitCodeGate.Release();
            DeleteDirectory(directory);
        }
    }

    /// <summary>Without the page, an unknown time zone still stops the server, and so does an address without a listener; nothing else is needed.</summary>
    [Theory]
    [InlineData("LookingGlass:Admin:TimeZone", "Nowhere/Land", "LookingGlass:Admin:TimeZone")]
    [InlineData("LookingGlass:Admin:Url", PageServer.Url, "LookingGlass:Admin:Listen isn't")]
    public async Task WithoutThePageOnlyItsTimeZoneAndUrlAreChecked(string key, string value, string said) {
        var problem = new AdminOptions { TimeZone = key.EndsWith("TimeZone", StringComparison.Ordinal) ? value : "UTC", Url = key.EndsWith("Url", StringComparison.Ordinal) ? value : "" }.Problem();
        Assert.NotNull(problem);
        Assert.Contains(said, problem);
        Assert.Contains("won't start", problem);
        Assert.Null(new AdminOptions().Problem());
        Assert.Null(new AdminOptions { TimeZone = "Europe/London" }.Problem());
        Assert.Null(new AdminOptions { TimeZone = "utc" }.Problem());
    }

    /// <summary>What a right set of settings looks like, read back.</summary>
    [Fact]
    public void GoodSettingsReadBack() {
        var folder = NewDirectory();
        Directory.CreateDirectory(folder);
        try {
            var socket = new AdminOptions {
                Listen = $"unix:{Path.Combine(folder, "admin.sock")}", Url = "https://LG.tailnet-0000.ts.net:8444", TailscaleLogins = ["alice@github", "", " ALICE@github ", "bob@example.com"],
            };
            Assert.Null(socket.Problem("http://127.0.0.1:5180"));
            Assert.Equal(Path.Combine(folder, "admin.sock"), socket.SocketPath);
            Assert.Null(socket.LoopbackEndPoint);
            Assert.Equal("https://lg.tailnet-0000.ts.net:8444/", socket.PageLink);
            Assert.Equal(["alice@github", "bob@example.com"], socket.Logins);

            var loopback = new AdminOptions { Listen = "http://127.0.0.1:5181", Url = PageServer.Url, TailscaleLogins = [PageServer.Login], TimeZone = "Europe/London" };
            Assert.Null(loopback.Problem("http://127.0.0.1:5180"));
            Assert.Equal(new IPEndPoint(IPAddress.Loopback, 5181), loopback.LoopbackEndPoint);
            Assert.Equal("Europe/London", loopback.Zone.Id);
            Assert.Contains("is the public server's", loopback.Problem("http://127.0.0.1:5181"));
            Assert.Null(new AdminOptions { Listen = "http://[::1]:5181", Url = PageServer.Url, TailscaleLogins = [PageServer.Login] }.Problem());
            Assert.Null(new AdminOptions().PageLink);
        } finally {
            DeleteDirectory(folder);
        }
    }

    // ================================================================ times

    /// <summary>
    /// Times in a zone with daylight saving carry the abbreviation in force at each (a zone whose system names are abbreviations,
    /// as on the Linux server); a zone whose names aren't (Windows' long names) shows the offset instead. Either way, a time just
    /// before and just after the change reads differently, and each is right.
    /// </summary>
    [Fact]
    public void TimesCarryTheZonesAbbreviationAcrossADaylightSavingChange() {
        // As the Linux server sees Europe/London: abbreviations for names; summer time from the last Sunday in March, 01:00 UTC.
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 1, 0, 0), 3, 5, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 10, 5, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1), start, end);
        var london = TimeZoneInfo.CreateCustomTimeZone("Test/London", TimeSpan.Zero, "London", "GMT", "BST", [rule]);

        // 2026's change: 29 March at 01:00 UTC.
        var before = new DateTimeOffset(2026, 3, 29, 0, 59, 0, TimeSpan.Zero);
        var after = new DateTimeOffset(2026, 3, 29, 1, 1, 0, TimeSpan.Zero);
        Assert.Equal("2026-03-29 00:59 GMT", PageTime.Format(before, london));
        Assert.Equal("2026-03-29 02:01 BST", PageTime.Format(after, london));
        // And back, 25 October 2026 at 01:00 UTC.
        Assert.Equal("2026-10-25 01:59 BST", PageTime.Format(new DateTimeOffset(2026, 10, 25, 0, 59, 0, TimeSpan.Zero), london));
        Assert.Equal("2026-10-25 01:01 GMT", PageTime.Format(new DateTimeOffset(2026, 10, 25, 1, 1, 0, TimeSpan.Zero), london));

        // As Windows names it: long names, so the offset stands in.
        var windows = TimeZoneInfo.CreateCustomTimeZone("Test/Windows", TimeSpan.Zero, "London", "GMT Standard Time", "GMT Daylight Time", [rule]);
        Assert.Equal("2026-03-29 00:59 UTC+0", PageTime.Format(before, windows));
        Assert.Equal("2026-03-29 02:01 UTC+1", PageTime.Format(after, windows));

        // The system's own zone, whatever it names it: the local time and the abbreviation (or offset) change together.
        var system = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        Assert.StartsWith("2026-03-29 00:59 ", PageTime.Format(before, system));
        Assert.Contains(PageTime.Abbreviation(system, before), new[] { "GMT", "UTC+0" });
        Assert.StartsWith("2026-03-29 02:01 ", PageTime.Format(after, system));
        Assert.Contains(PageTime.Abbreviation(system, after), new[] { "BST", "UTC+1" });

        Assert.Equal("2026-10-10 14:02 UTC", PageTime.Format(new DateTimeOffset(2026, 10, 10, 14, 2, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
        Assert.Equal("Times are UTC.", PageTime.Legend(TimeZoneInfo.Utc, after));
        Assert.Equal("Times are Test/London (BST now).", PageTime.Legend(london, after));
        Assert.Equal("UTC-3:30", PageTime.Offset(TimeSpan.FromHours(-3.5)));
        Assert.Equal("UTC+5:45", PageTime.Offset(new TimeSpan(5, 45, 0)));
        // A zone named only by its offset ("-03") shows it as an offset from UTC.
        var numeric = TimeZoneInfo.CreateCustomTimeZone("Test/Numeric", TimeSpan.FromHours(-3), "-03", "-03");
        Assert.Equal("2026-10-10 11:02 UTC-3", PageTime.Format(new DateTimeOffset(2026, 10, 10, 14, 2, 0, TimeSpan.Zero), numeric));
    }
}
