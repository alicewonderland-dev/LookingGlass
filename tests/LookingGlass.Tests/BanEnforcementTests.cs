using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LookingGlass.Core.Client;
using LookingGlass.Protocol;
using LookingGlass.Server;
using LookingGlass.Server.Hosting;
using LookingGlass.Server.Realtime;
using LookingGlass.Server.Services;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// What a ban does, against an in-process server: a banned account can't sign in (saved login or identity key) or register
/// again, a banned address can't connect, a ban made from the command line while the server runs drops the player within
/// <see cref="AbuseOptions.BanCheckSeconds"/>, and the plugin says plainly that the operator blocked them and why, and doesn't
/// keep reconnecting. Their places in channels stay. Also that refusals by limits reach <see cref="AbuseMonitor"/>.
/// </summary>
public sealed class BanEnforcementTests {
    // Bans are read from the database every second here, rather than every 30.
    private static readonly (string, string) CheckEverySecond = ("LookingGlass:Abuse:BanCheckSeconds", "1");

    private static int Ban(Harness server, string target, string? reason = null, int? days = null) =>
        BanCommand.Run(new BanCommand.Request(BanCommand.BanAction.Ban, target, days, reason), DatabasePath(server), new AbuseOptions(), new StringWriter(), new StringWriter());

    private static int Unban(Harness server, string target) {
        var code = BanCommand.Run(new BanCommand.Request(BanCommand.BanAction.Unban, target, null, null), DatabasePath(server), new AbuseOptions(), new StringWriter(), new StringWriter());
        // Rather than waiting for the next check.
        server.Factory.Services.GetRequiredService<BanList>().Refresh();
        return code;
    }

    private static string DatabasePath(Harness server) => Path.Combine(server.DataDirectory, "lookingglass.db");

    /// <summary>Asserts the server closed the connection: nothing more can be asked on it.</summary>
    private static async Task AssertClosedAsync(RawConnection raw) {
        var failed = await Record.ExceptionAsync(() => raw.SendAsync(new ClientFrame { Ping = new Ping() }));
        Assert.True(failed is InvalidOperationException or IOException or System.Net.WebSockets.WebSocketException, $"The connection is still open ({failed}).");
    }

    private static Task WaitForState(TestClient client, ConnectionState state) =>
        WaitFor(() => client.Session.Snapshot.State == state ? new object() : null);

    /// <summary>
    /// Banned from the command line while chatting: dropped within seconds, told plainly (once) that the server's operator
    /// blocked them, with the reason and until when, and left alone rather than reconnecting every few seconds. The others
    /// see them go offline; their place in the channel stays. Lifted, and asked to try again, they're back.
    /// </summary>
    [Fact]
    public async Task ABannedPlayerIsDroppedToldWhyAndLeftAlone() {
        await using var server = new Harness(settings: CheckEverySecond);
        var attempts = 0;
        var alice = await server.RegisterAsync("Alice Banned", options: server.Options(beforeConnect: _ => {
            Interlocked.Increment(ref attempts);
            return Task.CompletedTask;
        }));
        var bob = await server.RegisterAsync("Bob Stays");
        var channelId = await bob.Session.CreateChannelAsync("Tea party", Ct);
        await AddMemberAsync(bob, channelId, alice);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId)?.Members.SingleOrDefault(m => m.User.UserId == alice.UserId && m.Online));

        Assert.Equal(0, Ban(server, "Alice Banned@Debug", "Spamming invites", days: 2));
        await WaitForState(alice, ConnectionState.Blocked);

        var notice = Assert.Single(alice.Notices, notice => notice.Kind == NoticeKind.Blocked);
        Assert.Equal(NoticeLevel.Warning, notice.Level);
        Assert.Contains("Spamming invites", notice.Text);
        Assert.Contains("Spamming invites", notice.TextFor(advanced: false));
        Assert.Contains("operator", notice.Text);
        Assert.Contains("Spamming invites", alice.Session.Snapshot.StatusFor(advanced: false));

        // Not hammering: no new connection for a while (an ordinary drop reconnects within 100 ms here).
        var seen = Volatile.Read(ref attempts);
        await Task.Delay(TimeSpan.FromSeconds(1.5), Ct);
        Assert.Equal(seen, Volatile.Read(ref attempts));
        Assert.Equal(ConnectionState.Blocked, alice.Session.Snapshot.State);
        Assert.Single(alice.Notices, notice => notice.Kind == NoticeKind.Blocked);

        // Offline to the others, and still a member.
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId)?.Members.SingleOrDefault(m => m.User.UserId == alice.UserId && !m.Online));
        Assert.Contains(server.Database.GetMembers(channelId), member => member.User.UserId == alice.UserId);

        Assert.Equal(0, Unban(server, "Alice Banned@Debug"));
        alice.Session.Reconnect();
        await WaitForState(alice, ConnectionState.Ready);
        await alice.Session.SendTextAsync(channelId, "Back again", Ct);
        await WaitFor(() => bob.Messages.FirstOrDefault(message => message.Text == "Back again"));
    }

    /// <summary>
    /// The saved login is refused with the block (and why), in a sentence that says it all for a plugin from before bans,
    /// and the server then closes the connection.
    /// </summary>
    [Fact]
    public async Task ABannedAccountsSavedLoginIsRefusedAndTheConnectionClosed() {
        await using var server = new Harness(settings: CheckEverySecond);
        var alice = await server.RegisterAsync("Alice Refused");
        var token = alice.Store.Load().DeviceToken!;
        await alice.Session.DisposeAsync();
        Assert.Equal(0, Ban(server, alice.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture), "Spamming invites"));
        server.Factory.Services.GetRequiredService<BanList>().Refresh();

        var raw = await server.ConnectRawAsync();
        var refused = (await raw.SendAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = token } })).Error;
        Assert.Equal(ErrorCode.Blocked, refused?.Code);
        Assert.Equal(("Spamming invites", 0L, false), (refused!.Block.Reason, refused.Block.UntilUnix, refused.Block.Address));
        Assert.Contains("blocked", refused.Message);
        Assert.Contains("Spamming invites", refused.Message);
        await AssertClosedAsync(raw);
    }

    /// <summary>A banned account whose saved login the server lost can't sign in with its identity key either: no new login is made.</summary>
    [Fact]
    public async Task ABannedAccountCantSignInWithItsKeyEither() {
        await using var server = new Harness(settings: CheckEverySecond);
        var alice = await server.RegisterAsync("Alice Keyed");
        var userId = alice.UserId;
        await alice.Session.DisposeAsync();
        server.ExecuteSql("DELETE FROM devices;");
        Assert.Equal(0, Ban(server, "Alice Keyed@Debug"));
        server.Factory.Services.GetRequiredService<BanList>().Refresh();

        var again = server.StartClient("Alice Keyed", alice.Store);
        await WaitForState(again, ConnectionState.Blocked);
        Assert.Equal(0, server.Database.CountDevices(userId));
        Assert.Single(again.Notices, notice => notice.Kind == NoticeKind.Blocked);
    }

    /// <summary>
    /// A banned character can't register again, with new keys or any: the ban is on the character (its Lodestone ID), so even
    /// one banned before it ever registered can't. Nothing is registered or moved.
    /// </summary>
    [Fact]
    public async Task ABannedCharacterCantRegister() {
        await using var server = new Harness(settings: CheckEverySecond);
        var alice = await server.RegisterAsync("Alice Again");
        var keys = server.Database.GetUser(alice.UserId)!.SigningKey;
        Assert.Equal(0, Ban(server, "Alice Again@Debug"));
        Assert.Equal(0, Ban(server, RequestHandler.DebugUserId("Dana Never").ToString(System.Globalization.CultureInfo.InvariantCulture)));
        server.Factory.Services.GetRequiredService<BanList>().Refresh();

        foreach (var name in new[] { "Alice Again", "Dana Never" }) {
            var client = server.StartClient(name);
            await WaitForState(client, ConnectionState.Unregistered);
            await client.Session.StartRegistrationAsync(new Character { Name = name, WorldName = ProtocolInfo.DebugWorldName }, Ct);
            var refused = await Assert.ThrowsAsync<ServerErrorException>(() => client.Session.CompleteRegistrationAsync(Ct));
            Assert.Equal(ErrorCode.Blocked, refused.Code);
            await WaitForState(client, ConnectionState.Blocked);
        }

        Assert.Equal(keys, server.Database.GetUser(alice.UserId)!.SigningKey);
        Assert.Null(server.Database.GetUser(RequestHandler.DebugUserId("Dana Never")));
    }

    /// <summary>
    /// A banned address (or prefix) can't connect: its first request is answered with the block, saying it is the address,
    /// and the connection closed. An IPv6 ban covers the /64s in its prefix. Connections it had open are dropped.
    /// </summary>
    [Fact]
    public async Task ABannedAddressCantConnect() {
        await using var server = new Harness(settings: CheckEverySecond);
        var open = await server.ConnectRawAsync(remoteAddress: "203.0.113.77");
        Assert.Equal(0, Ban(server, "203.0.113.0/24", "Flooding"));
        Assert.Equal(0, Ban(server, "2001:db8:5:6::/64"));
        server.Factory.Services.GetRequiredService<BanList>().Refresh();

        foreach (var address in new[] { "203.0.113.5", "2001:db8:5:6::99" }) {
            var raw = await server.ConnectRawAsync(remoteAddress: address, hello: false);
            var hello = new Hello();
            hello.ProtocolVersions.Add(ProtocolInfo.CurrentVersion);
            var refused = (await raw.SendAsync(new ClientFrame { Hello = hello })).Error;
            Assert.Equal(ErrorCode.Blocked, refused?.Code);
            Assert.True(refused!.Block.Address);
            await AssertClosedAsync(raw);
        }

        // Neighbours outside the prefixes connect as usual.
        await server.ConnectRawAsync(remoteAddress: "203.0.114.5");
        await server.ConnectRawAsync(remoteAddress: "2001:db8:5:7::1");

        // The connection that was open when the ban was made is closed within a check or two.
        var deadline = DateTimeOffset.UtcNow + Harness.Timeout;
        while (true) {
            try {
                await open.SendAsync(new ClientFrame { Ping = new Ping() });
            } catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Net.WebSockets.WebSocketException) {
                break;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, "The banned address's open connection wasn't closed.");
            await Task.Delay(200, Ct);
        }
    }

    /// <summary>A plugin connecting from a banned address is told so, as for an account, and doesn't keep reconnecting.</summary>
    [Fact]
    public async Task APluginOnABannedAddressIsToldSo() {
        await using var server = new Harness(settings: CheckEverySecond);
        Assert.Equal(0, Ban(server, "2001:db8:9:9::/64", "Flooding"));
        var client = server.StartClient("Carol Behind", options: server.Options(remoteAddress: "2001:db8:9:9::1"));
        await WaitForState(client, ConnectionState.Blocked);
        var notice = Assert.Single(client.Notices, notice => notice.Kind == NoticeKind.Blocked);
        Assert.Contains("address", notice.Text);
        Assert.Contains("Flooding", notice.TextFor(advanced: false));
    }

    /// <summary>
    /// Refusals by limits are counted towards flagging: here a lookup limit spent at once, and new connections from one address
    /// past their limit, each flagged at the first refusal (the threshold lowered to one minute).
    /// </summary>
    [Fact]
    public async Task RefusalsByLimitsAreCountedAndFlagged() {
        var logs = new CapturingLoggerProvider();
        await using var server = new Harness(logs: logs, settings: [
            ("LookingGlass:Abuse:FlagAfterMinutesRefused", "1"),
            ("LookingGlass:Limits:LookupBurst", "1"),
            ("LookingGlass:Limits:LookupIntervalSeconds", "86400"),
            ("LookingGlass:Limits:ConnectionsPerMinutePerIp", "2"),
        ]);
        var alice = await server.RegisterAsync("Alice Looking");
        var lookup = new ClientFrame { LookupUser = new LookupUser { Name = alice.Name, WorldName = ProtocolInfo.DebugWorldName } };
        await alice.Session.SendRawAsync(lookup, Ct);
        var refused = await Assert.ThrowsAsync<ServerErrorException>(() => alice.Session.SendRawAsync(lookup, Ct));
        Assert.Equal(ErrorCode.RateLimited, refused.Code);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var flag = await WaitFor(() => server.Database.GetFlag($"user {alice.UserId}"));
        Assert.Equal("LookupBurst", flag.Limits);
        var warning = Assert.Single(logs.AtLeast(LogLevel.Warning), line => line.Contains($"user {alice.UserId}"));
        Assert.Contains("LookupBurst", warning);
        Assert.DoesNotContain("Alice", warning);

        for (var i = 0; i < 2; i++) {
            await server.ConnectRawAsync(remoteAddress: "198.51.100.40", hello: false);
        }

        await Assert.ThrowsAnyAsync<Exception>(() => server.ConnectRawAsync(remoteAddress: "198.51.100.40", hello: false));
        Assert.Equal("ConnectionsPerMinutePerIp", (await WaitFor(() => server.Database.GetFlag("address 198.51.100.40"))).Limits);
        Assert.True(server.Database.GetFlags(now - 60).Count >= 2);
    }
}
