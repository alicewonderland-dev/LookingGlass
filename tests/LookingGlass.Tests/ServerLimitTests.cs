using Google.Protobuf;
using LookingGlass.Core.Client;
using LookingGlass.Core.Crypto;
using LookingGlass.Core.Membership;
using LookingGlass.Protocol;
using LookingGlass.Server.Realtime;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>Invite abuse, per-user throttles and bounded responses, against an in-process server.</summary>
public sealed class ServerLimitTests : IAsyncLifetime {
    private Harness _server = null!;

    public ValueTask InitializeAsync() {
        this._server = new Harness();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() {
        await this._server.DisposeAsync();
    }

    [Fact]
    public async Task IdentityLookupsAreSplitToFitTheServersLimit() {
        await using var server = new Harness(settings: ("LookingGlass:Limits:MaxIdentitiesPerRequest", "2"));
        try {
            var alice = await server.RegisterAsync("Alice Crowd");
            Assert.Equal(2u, alice.Session.Snapshot.Limits!.MaxIdentitiesPerRequest);
            var channelId = await alice.Session.CreateChannelAsync("Crowd", Ct);
            var members = new List<TestClient>();
            foreach (var name in new[] { "Bob Crowd", "Carol Crowd", "Dave Crowd" }) {
                var member = await server.RegisterAsync(name);
                await AddMemberAsync(alice, channelId, member);
                members.Add(member);
            }

            // A new session has nothing cached, so it needs all four identities when it connects.
            await alice.Session.DisposeAsync();
            var restarted = await server.RestartAsync(alice);
            Assert.True(restarted.Session.Snapshot.ChannelsLoaded);
            var channel = restarted.Session.Snapshot.FindChannel(channelId)!;
            Assert.Equal("Crowd", channel.Name);
            Assert.Equal(4, channel.Members.Length);
            Assert.All(channel.Members, member => Assert.NotNull(member.Fingerprint));

            await members[2].Session.SendTextAsync(channelId, "can you all hear me", Ct);
            await WaitFor(() => restarted.Messages.FirstOrDefault(m => m.Text == "can you all hear me"));
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
    }

    [Fact]
    public async Task FailedIdentityLookupOnConnectKeepsTheConnection() {
        // A server that refuses every identity lookup (as one with a limit the client exceeds would).
        await using var server = new Harness(settings: ("LookingGlass:Limits:MaxIdentitiesPerRequest", "0"));
        try {
            var store = new InMemorySecretStore();
            var alice = await server.RegisterAsync("Alice Refused", store);
            var channelId = await alice.Session.CreateChannelAsync("Still Listed", Ct);
            await alice.Session.DisposeAsync();

            var restarted = server.StartClient(alice.Name, store);
            await WaitFor(() => restarted.Session.Snapshot.ChannelsLoaded ? new object() : null);
            await WaitFor(() => restarted.Notices.FirstOrDefault(n => n.Level == NoticeLevel.Warning && n.Text.Contains("identity keys")));
            Assert.NotNull(restarted.Session.Snapshot.FindChannel(channelId));

            // Connected once and stayed connected, rather than reconnecting forever.
            await Task.Delay(300, Ct);
            Assert.Equal(ConnectionState.Ready, restarted.Session.Snapshot.State);
            Assert.Single(restarted.Session.GetTrace(), entry => entry.Outgoing && entry.Summary.EndsWith(" Hello"));
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
    }

    /// <summary>A plugin speaking an older protocol version is turned away at Hello, and the user is told why, once.</summary>
    [Fact]
    public async Task APluginSpeakingAnOlderProtocolIsToldToUpdate() {
        var old = this._server.StartClient("Old Plugin", options: this._server.Options(protocolVersion: 1));
        var notice = await WaitFor(() => old.Notices.FirstOrDefault(n => n.Text.Contains("different protocol versions")));
        Assert.Equal(NoticeLevel.Warning, notice.Level);
        Assert.Contains("Please update the plugin", notice.Text);

        // It keeps trying (the server may be updated), but says so only once, and never gets further.
        await WaitFor(() => old.Session.GetTrace().Count(entry => entry.Outgoing && entry.Summary.EndsWith(" Hello")) >= 2 ? new object() : null);
        Assert.Single(old.Notices, n => n.Text.Contains("different protocol versions"));
        Assert.NotEqual(ConnectionState.Unregistered, old.Session.Snapshot.State);
        Assert.NotEqual(ConnectionState.Ready, old.Session.Snapshot.State);
    }

    [Fact]
    public async Task InvitesMustBeSmallAndCorrectlySigned() {
        var alice = await this._server.RegisterAsync("Alice Invite Checks");
        var bob = await this._server.RegisterAsync("Bob Invite Checks");
        var channelId = await alice.Session.CreateChannelAsync("Checks", Ct);
        using var aliceKeys = alice.LoadIdentity();
        var bobAgreement = bob.LoadIdentity().AgreementPublicKey;

        (SealedBox, byte[]) Huge(LogPosition at) => ChannelCrypto.SealInvite(new string('x', 200), channelId, at, bob.UserId, bobAgreement, aliceKeys, alice.UserId);
        var error = await Assert.ThrowsAsync<ServerErrorException>(() => alice.Session.SendRawAsync(this.Invite(channelId, alice, bob.UserId, bob.Keys(), Huge), Ct));
        Assert.Equal(ErrorCode.TooLarge, error.Code);

        using var otherKeys = IdentityKeys.Generate();
        (SealedBox, byte[]) Forged(LogPosition at) => ChannelCrypto.SealInvite("Checks", channelId, at, bob.UserId, bobAgreement, otherKeys, alice.UserId);
        error = await Assert.ThrowsAsync<ServerErrorException>(() => alice.Session.SendRawAsync(this.Invite(channelId, alice, bob.UserId, bob.Keys(), Forged), Ct));
        Assert.Equal(ErrorCode.InvalidRequest, error.Code);

        Assert.Empty(this._server.Database.GetInvitesForUser(bob.UserId));
    }

    [Fact]
    public async Task PendingInvitesPerInviteeAreCapped() {
        var alice = await this._server.RegisterAsync("Alice Invite Cap");
        var bob = await this._server.RegisterAsync("Bob Invite Cap");
        Assert.Equal(50, this._server.Handler.MaxPendingInvitesPerUser);
        // 25 from each of two others (as many as one inviter may have waiting).
        foreach (var name in new[] { "Carol Invite Cap", "Dave Invite Cap" }) {
            var other = await this._server.RegisterAsync(name);
            foreach (var seeded in this.SeedChannels(other, this._server.Handler.MaxPendingInvitesPerUser / 2)) {
                this.SeedInvite(other, seeded, bob);
            }
        }

        var channelId = await alice.Session.CreateChannelAsync("One Too Many", Ct);
        var error = await Assert.ThrowsAsync<ServerErrorException>(() => alice.Session.InviteAsync(channelId, bob.Name, ProtocolInfo.DebugWorldName, Ct));
        Assert.Equal(ErrorCode.LimitReached, error.Code);
        Assert.Equal("Bob Invite Cap@Debug already has 50 invites waiting, the most they can have; once they accept or decline some, they can be invited again.",
            error.ServerMessage);
        PlainLanguage.AssertPlain(error.ServerMessage);
    }

    [Fact]
    public async Task ChannelListReturnsABoundedNumberOfInvites() {
        var alice = await this._server.RegisterAsync("Alice Many Invites");
        var bob = await this._server.RegisterAsync("Bob Many Invites");
        // Seeded straight into the database, past every cap, as an old or hostile server state.
        foreach (var seeded in this.SeedChannels(alice, 60)) {
            this.SeedInvite(alice, seeded, bob);
        }

        await bob.Session.RefreshAsync(Ct);
        Assert.Equal(this._server.Handler.MaxPendingInvitesPerUser, bob.Session.Snapshot.Invites.Length);
    }

    /// <summary>
    /// Many inviters together can't flood one person: 30 invites at once, then one every 10 seconds; whoever is refused is told
    /// how long to wait.
    /// </summary>
    [Fact]
    public async Task InvitesToOnePersonAreRateLimited() {
        var clock = await this.StopTheClockAsync();
        var bob = await this._server.RegisterAsync("Bob Invite Rate");
        var invitee = new Invitee(bob.UserId, bob.Keys());
        var sent = 0;
        TestClient? last = null;
        ServerErrorException? refused = null;
        foreach (var name in new[] { "Alice Invite Rate", "Carol Invite Rate", "Dave Invite Rate", "Erin Invite Rate" }) {
            last = await this._server.RegisterAsync(name);
            var (accepted, limited) = await this.InviteUntilLimitedAsync(last, this.SeedChannels(last, 10).Select(channelId => (channelId, invitee)).ToList());
            sent += accepted;
            if (limited != null) {
                refused = limited;
                break;
            }
        }

        Assert.Equal(30, sent);
        Assert.Equal("Bob Invite Rate@Debug has been sent a lot of invites recently; try again in about 10 seconds.", refused?.ServerMessage);
        PlainLanguage.AssertPlain(refused!.ServerMessage);

        // Ten seconds on, one more.
        clock.Advance(TimeSpan.FromSeconds(10));
        var (more, _) = await this.InviteUntilLimitedAsync(last!, this.SeedChannels(last!, 3).Select(channelId => (channelId, invitee)).ToList());
        Assert.Equal(1, more);
    }

    /// <summary>
    /// Someone bringing their channels over invites the same friend to each: 20 in a row go through, the 21st is told (in the
    /// plugin, in plain words) to wait about a minute, and goes through a minute later.
    /// </summary>
    [Fact]
    public async Task OneFriendCanBeInvitedToTwentyChannelsInARow() {
        var clock = await this.StopTheClockAsync();
        var alice = await this._server.RegisterAsync("Alice Moving In");
        var bob = await this._server.RegisterAsync("Bob Moving In");
        var invitee = new Invitee(bob.UserId, bob.Keys());
        var channels = this.SeedChannels(alice, 21);
        var (sent, refused) = await this.InviteUntilLimitedAsync(alice, channels.Take(20).Select(channelId => (channelId, invitee)).ToList());
        Assert.Equal(20, sent);
        Assert.Null(refused);

        // The 21st, as the plugin sends it.
        await alice.Session.RefreshAsync(Ct);
        var last = channels[20];
        var error = await Assert.ThrowsAsync<ServerErrorException>(() => alice.Session.InviteAsync(last, bob.Name, ProtocolInfo.DebugWorldName, Ct));
        Assert.Equal(ErrorCode.RateLimited, error.Code);
        Assert.Equal("You've sent a lot of invites to Bob Moving In@Debug recently; try again in about a minute.", error.ServerMessage);
        var said = ContextInvites.NotInvited(new InviteTarget(bob.Name, ProtocolInfo.DebugWorldName), new InviteOffer(last, "[mv]", null, "Moving", null), error, advanced: false);
        Assert.Equal("Couldn't invite Bob Moving In@Debug to [mv]: You've sent a lot of invites to Bob Moving In@Debug recently; try again in about a minute.", said);
        PlainLanguage.AssertPlain(said);

        clock.Advance(TimeSpan.FromSeconds(30));
        error = await Assert.ThrowsAsync<ServerErrorException>(() => alice.Session.InviteAsync(last, bob.Name, ProtocolInfo.DebugWorldName, Ct));
        Assert.EndsWith("recently; try again in about 30 seconds.", error.ServerMessage);

        clock.Advance(TimeSpan.FromSeconds(30));
        await alice.Session.InviteAsync(last, bob.Name, ProtocolInfo.DebugWorldName, Ct);
        Assert.Equal(21, this._server.Database.CountInvitesForUser(bob.UserId));
    }

    /// <summary>
    /// One inviter can't use up someone's invites, whether or not that person blocked them (the server doesn't know: a
    /// blocked inviter's invites are declined unseen by the invitee's client). An invite, cancel, invite loop is stopped
    /// after 20 invites to the same person (then one a minute), and leaves the rest of what others can send them (10 of 30).
    /// </summary>
    [Fact]
    public async Task OnePersonCantUseUpAnothersInvites() {
        await this.StopTheClockAsync();
        var mallory = await this._server.RegisterAsync("Mallory Invite Hog");
        var bob = await this._server.RegisterAsync("Bob Invite Hog");
        var alice = await this._server.RegisterAsync("Alice Invite Hog");
        var channelId = await mallory.Session.CreateChannelAsync("Hogging", Ct);
        using var malloryKeys = mallory.LoadIdentity();
        var bobAgreement = bob.LoadIdentity().AgreementPublicKey;

        var sent = 0;
        ServerErrorException? limited = null;
        for (var i = 0; i < 30 && limited == null; i++) {
            try {
                await mallory.Session.SendRawAsync(this.Invite(channelId, mallory, bob.UserId, bob.Keys(),
                    at => ChannelCrypto.SealInvite("Hogging", channelId, at, bob.UserId, bobAgreement, malloryKeys, mallory.UserId)), Ct);
                sent++;
                await mallory.Session.KickAsync(channelId, bob.UserId, Ct);
            } catch (ServerErrorException ex) {
                limited = ex;
            }
        }

        Assert.Equal(20, sent);
        Assert.Equal(ErrorCode.RateLimited, limited?.Code);
        Assert.Equal("You've sent a lot of invites to Bob Invite Hog@Debug recently; try again in about a minute.", limited!.ServerMessage);

        // Her further tries are refused before they spend anything of his: the pair's limit is checked first.
        for (var i = 0; i < 10; i++) {
            var refused = await Assert.ThrowsAsync<ServerErrorException>(() => mallory.Session.SendRawAsync(this.Invite(channelId, mallory, bob.UserId, bob.Keys(),
                at => ChannelCrypto.SealInvite("Hogging", channelId, at, bob.UserId, bobAgreement, malloryKeys, mallory.UserId)), Ct));
            Assert.StartsWith("You've sent a lot of invites to Bob Invite Hog@Debug recently", refused.ServerMessage);
        }

        // Alice and Carol can still invite him, with the rest of his budget (30): 10 between them.
        var carol = await this._server.RegisterAsync("Carol Invite Hog");
        var invitee = new Invitee(bob.UserId, bob.Keys());
        var (fromAlice, aliceRefused) = await this.InviteUntilLimitedAsync(alice, this.SeedChannels(alice, 5).Select(id => (id, invitee)).ToList());
        Assert.Equal(5, fromAlice);
        Assert.Null(aliceRefused);
        var (fromCarol, carolRefused) = await this.InviteUntilLimitedAsync(carol, this.SeedChannels(carol, 6).Select(id => (id, invitee)).ToList());
        Assert.Equal(5, fromCarol);
        Assert.Equal("Bob Invite Hog@Debug has been sent a lot of invites recently; try again in about 10 seconds.", carolRefused?.ServerMessage);
    }

    /// <summary>
    /// An invite the server refuses for its log entry (made before someone else's change landed: the client fetches the log
    /// and tries again) spends nothing: not the inviter's, the pair's or the invitee's allowance. Otherwise one invite,
    /// retried, could use up all of the pair's, and enough retries all of the inviter's and invitee's.
    /// </summary>
    [Fact]
    public async Task AnInviteRefusedForItsLogEntrySpendsNothing() {
        await this.StopTheClockAsync();
        var mallory = await this._server.RegisterAsync("Mallory Invite Race");
        var bob = await this._server.RegisterAsync("Bob Invite Race");
        var carol = await this._server.RegisterAsync("Carol Invite Race");
        var channelId = await mallory.Session.CreateChannelAsync("Race", Ct);
        var before = this._server.ServerMembership(channelId).Head!;
        await mallory.Session.InviteAsync(channelId, carol.Name, ProtocolInfo.DebugWorldName, Ct);
        using var malloryKeys = mallory.LoadIdentity();
        var bobAgreement = bob.LoadIdentity().AgreementPublicKey;

        // Forty tries with an entry made before Carol's invite, each refused as not the next one: more than the pair's (20),
        // Bob's (30) or what Mallory has left (59) would allow, if they were counted.
        for (var i = 0; i < 40; i++) {
            var stale = this._server.ForgeEntry(channelId, mallory, MembershipEntryKind.Invite, bob.UserId, bob.Keys(), after: before);
            var (sealedName, signature) = ChannelCrypto.SealInvite("Race", channelId, MembershipEntries.PositionOf(stale), bob.UserId, bobAgreement, malloryKeys, mallory.UserId);
            var refused = await Assert.ThrowsAsync<ServerErrorException>(() => mallory.Session.SendRawAsync(new ClientFrame {
                InviteMember = new InviteMember { ChannelId = channelId, Entry = stale, SealedName = sealedName, Signature = ByteString.CopyFrom(signature) },
            }, Ct));
            Assert.Equal(ErrorCode.Conflict, refused.Code);
        }

        // The pair's 20 invites are all still there, and the 21st is refused by the pair's limit, not Bob's or Mallory's.
        var invitee = new Invitee(bob.UserId, bob.Keys());
        var (sent, limited) = await this.InviteUntilLimitedAsync(mallory, this.SeedChannels(mallory, 21).Select(id => (id, invitee)).ToList());
        Assert.Equal(20, sent);
        Assert.StartsWith("You've sent a lot of invites to Bob Invite Race@Debug recently", limited?.ServerMessage);
    }

    /// <summary>
    /// One inviter (with many channels) can't fill someone's pending invites: at most 25 from any one inviter wait at once,
    /// of the 50 they can have.
    /// </summary>
    [Fact]
    public async Task PendingInvitesFromOnePersonAreCapped() {
        var mallory = await this._server.RegisterAsync("Mallory Invite Pile");
        var bob = await this._server.RegisterAsync("Bob Invite Pile");
        var alice = await this._server.RegisterAsync("Alice Invite Pile");
        Assert.Equal(25, this._server.Handler.MaxPendingInvitesFromOneInviter);
        foreach (var seeded in this.SeedChannels(mallory, this._server.Handler.MaxPendingInvitesFromOneInviter)) {
            this.SeedInvite(mallory, seeded, bob);
        }

        var more = await mallory.Session.CreateChannelAsync("One More Pile", Ct);
        var error = await Assert.ThrowsAsync<ServerErrorException>(() => mallory.Session.InviteAsync(more, bob.Name, ProtocolInfo.DebugWorldName, Ct));
        Assert.Equal(ErrorCode.LimitReached, error.Code);
        Assert.Equal("Bob Invite Pile@Debug already has 25 invites from you waiting; once they accept or decline some, you can invite them again.", error.ServerMessage);
        PlainLanguage.AssertPlain(error.ServerMessage);
        Assert.Equal(25, this._server.Database.CountInvitesForUser(bob.UserId));

        var aliceChannel = await alice.Session.CreateChannelAsync("Still Room", Ct);
        await alice.Session.InviteAsync(aliceChannel, bob.Name, ProtocolInfo.DebugWorldName, Ct);
        Assert.Equal(26, this._server.Database.CountInvitesForUser(bob.UserId));

        // Others fill the rest, to 50, and then nobody can invite him until he answers some.
        var carol = await this._server.RegisterAsync("Carol Invite Pile");
        foreach (var seeded in this.SeedChannels(carol, 24)) {
            this.SeedInvite(carol, seeded, bob);
        }

        var dave = await this._server.RegisterAsync("Dave Invite Pile");
        var daveChannel = await dave.Session.CreateChannelAsync("No Room", Ct);
        error = await Assert.ThrowsAsync<ServerErrorException>(() => dave.Session.InviteAsync(daveChannel, bob.Name, ProtocolInfo.DebugWorldName, Ct));
        Assert.Equal(ErrorCode.LimitReached, error.Code);
        Assert.StartsWith("Bob Invite Pile@Debug already has 50 invites waiting", error.ServerMessage);
        Assert.Equal(50, this._server.Database.CountInvitesForUser(bob.UserId));
    }

    /// <summary>One person can't spam many: 60 invites at once, then one every 5 seconds.</summary>
    [Fact]
    public async Task InvitesFromOnePersonAreRateLimited() {
        var clock = await this.StopTheClockAsync();
        var alice = await this._server.RegisterAsync("Alice Invite Spam");
        // Two channels, as one takes at most 50 pending invites.
        var channels = this.SeedChannels(alice, 2);
        var strangers = Enumerable.Range(0, 62).Select(i => this.SeedUser($"Stranger {i}")).ToList();
        var invites = strangers.Select((stranger, i) => (channels[i % 2], stranger)).ToList();

        var (sent, limited) = await this.InviteUntilLimitedAsync(alice, invites);
        Assert.Equal(60, sent);
        Assert.Equal("You've sent a lot of invites recently; try again in a few seconds.", limited?.ServerMessage);
        PlainLanguage.AssertPlain(limited!.ServerMessage);

        clock.Advance(TimeSpan.FromSeconds(5));
        var (more, _) = await this.InviteUntilLimitedAsync(alice, invites.Skip(60).ToList());
        Assert.Equal(1, more);
    }

    [Fact]
    public async Task InviteCancelLoopsAreRateLimited() {
        var alice = await this._server.RegisterAsync("Alice Invite Loop");
        var bob = await this._server.RegisterAsync("Bob Invite Loop");
        var channelId = await alice.Session.CreateChannelAsync("Loop", Ct);
        using var aliceKeys = alice.LoadIdentity();
        var bobAgreement = bob.LoadIdentity().AgreementPublicKey;

        ServerErrorException? limited = null;
        for (var i = 0; i < 30 && limited == null; i++) {
            try {
                await alice.Session.SendRawAsync(this.Invite(channelId, alice, bob.UserId, bob.Keys(), at => ChannelCrypto.SealInvite("Loop", channelId, at, bob.UserId, bobAgreement, aliceKeys, alice.UserId)), Ct);
            } catch (ServerErrorException ex) {
                limited = ex;
                break;
            }

            await alice.Session.KickAsync(channelId, bob.UserId, Ct);
        }

        Assert.Equal(ErrorCode.RateLimited, limited?.Code);
        // Cancelling invites never needed a rekey.
        Assert.Equal(0UL, this._server.Database.GetChannel(channelId)!.Epoch);
    }

    /// <summary>
    /// An invite refused by a limit is logged, so refusals can be traced: one Information line naming the limit (as its setting)
    /// and the user IDs, never names, and at most one a minute per inviter. The limits are the operator's settings.
    /// </summary>
    [Fact]
    public async Task InviteRefusalsAreLoggedByUserIdAtMostOnceAMinutePerInviter() {
        var logs = new CapturingLoggerProvider();
        var clock = await this.StopTheClockAsync(logs, ("LookingGlass:Limits:InviteBurstPerPair", "2"));
        var mallory = await this._server.RegisterAsync("Mallory Refusal Log");
        var bob = await this._server.RegisterAsync("Bob Refusal Log");
        var invitee = new Invitee(bob.UserId, bob.Keys());
        var channels = this.SeedChannels(mallory, 9);

        // This server allows two at once between a pair: then six refusals, logged once.
        var (sent, _) = await this.InviteUntilLimitedAsync(mallory, channels.Take(3).Select(id => (id, invitee)).ToList());
        Assert.Equal(2, sent);
        for (var i = 3; i < 8; i++) {
            var (none, refused) = await this.InviteUntilLimitedAsync(mallory, [(channels[i], invitee)]);
            Assert.Equal(0, none);
            Assert.Equal(ErrorCode.RateLimited, refused?.Code);
        }

        var line = Assert.Single(RefusalLines(logs));
        Assert.Equal($"Invite from user {mallory.UserId} to user {bob.UserId} refused by InviteBurstPerPair", line);

        // A minute on, the pair has one more, and the next refusal is logged again.
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(1, (await this.InviteUntilLimitedAsync(mallory, channels.Skip(7).Select(id => (id, invitee)).ToList())).Sent);
        Assert.Equal(2, RefusalLines(logs).Count);

        static List<string> RefusalLines(CapturingLoggerProvider logs) => logs.Entries
            .Where(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Information && entry.Message.StartsWith("Invite from user ", StringComparison.Ordinal))
            .Select(entry => entry.Message).ToList();
    }

    /// <summary>The invite settings are checked at startup, keeping one inviter from using up what others can send someone.</summary>
    [Fact]
    public void InviteSettingsOutOfRangeAreRefused() {
        Assert.Null(new LookingGlass.Server.LimitOptions().InviteProblem());
        Assert.Contains("InviteBurstPerInviter", new LookingGlass.Server.LimitOptions { InviteBurstPerInviter = 0 }.InviteProblem());
        Assert.Contains("InviteIntervalSecondsPerInvitee", new LookingGlass.Server.LimitOptions { InviteIntervalSecondsPerInvitee = 0 }.InviteProblem());
        Assert.Contains("InviteIntervalSecondsPerPair", new LookingGlass.Server.LimitOptions { InviteIntervalSecondsPerPair = 100_000 }.InviteProblem());
        // The pair's allowance must stay smaller and slower than the invitee's.
        Assert.Contains("InviteBurstPerPair", new LookingGlass.Server.LimitOptions { InviteBurstPerPair = 30 }.InviteProblem());
        Assert.Contains("InviteIntervalSecondsPerPair", new LookingGlass.Server.LimitOptions { InviteIntervalSecondsPerPair = 10 }.InviteProblem());
        Assert.Null(new LookingGlass.Server.LimitOptions { InviteBurstPerPair = 29, InviteIntervalSecondsPerPair = 11 }.InviteProblem());
        // One inviter can't fill all of someone's pending invites.
        Assert.Contains("MaxPendingInvitesFromOneInviter", new LookingGlass.Server.LimitOptions { MaxPendingInvitesFromOneInviter = 50 }.InviteProblem());
        Assert.Contains("MaxPendingInvitesPerUser", new LookingGlass.Server.LimitOptions { MaxPendingInvitesPerUser = 201 }.InviteProblem());
        Assert.Null(new LookingGlass.Server.LimitOptions { MaxPendingInvitesPerUser = 200, MaxPendingInvitesFromOneInviter = 199 }.InviteProblem());
    }

    /// <summary>A server with an invite setting out of range doesn't start, and says why.</summary>
    [Theory]
    [InlineData("InviteBurstPerPair", "0")]
    [InlineData("InviteBurstPerPair", "40")]
    [InlineData("MaxPendingInvitesFromOneInviter", "60")]
    public async Task AServerWithInviteSettingsOutOfRangeDoesntStart(string setting, string value) {
        var logs = new CapturingLoggerProvider();
        await ExitCodeGate.WaitAsync(Ct);
        var exitCode = Environment.ExitCode;
        var directory = Path.Combine(Path.GetTempPath(), "lgt-" + Guid.NewGuid().ToString("N"));
        try {
            Exception? failed = null;
            try {
                await using var server = new Harness(directory, logs: logs, settings: ($"LookingGlass:Limits:{setting}", value));
                await using var raw = await server.ConnectRawAsync();
            } catch (Exception ex) {
                failed = ex;
            }

            Assert.NotNull(failed);
            var critical = Assert.Single(logs.AtLeast(Microsoft.Extensions.Logging.LogLevel.Critical));
            Assert.Contains($"LookingGlass:Limits:{setting}", critical);
            Assert.Contains("won't start", critical);
            Assert.Equal(1, Environment.ExitCode);
        } finally {
            Environment.ExitCode = exitCode;
            ExitCodeGate.Release();
            DeleteDirectory(directory);
        }
    }

    /// <summary>How long to wait, as the refusals say it.</summary>
    [Theory]
    [InlineData(0.2, "a few seconds")]
    [InlineData(5, "a few seconds")]
    [InlineData(5.1, "about 10 seconds")]
    [InlineData(10, "about 10 seconds")]
    [InlineData(30, "about 30 seconds")]
    [InlineData(49, "about 50 seconds")]
    [InlineData(58, "about a minute")]
    [InlineData(60, "about a minute")]
    [InlineData(150, "about 3 minutes")]
    [InlineData(3600, "about an hour")]
    [InlineData(4 * 3600, "about 4 hours")]
    public void WaitsAreSaidRoughly(double seconds, string said) {
        Assert.Equal(said, RequestHandler.AboutHowLong(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public async Task CreateRenameAndDisbandAreRateLimited() {
        var alice = await this._server.RegisterAsync("Alice Busy Admin");
        var created = new List<string>();
        var error = await Assert.ThrowsAsync<ServerErrorException>(async () => {
            for (var i = 0; i < 12; i++) {
                created.Add(await alice.Session.CreateChannelAsync($"Channel {i}", Ct));
            }
        });
        Assert.Equal(ErrorCode.RateLimited, error.Code);
        Assert.Equal(10, created.Count);

        error = await Assert.ThrowsAsync<ServerErrorException>(async () => {
            for (var i = 0; i < 12; i++) {
                await alice.Session.RenameAsync(created[0], $"Renamed {i}", Ct);
            }
        });
        Assert.Equal(ErrorCode.RateLimited, error.Code);

        var disbanded = 0;
        error = await Assert.ThrowsAsync<ServerErrorException>(async () => {
            foreach (var channelId in created) {
                await alice.Session.DisbandAsync(channelId, Ct);
                disbanded++;
            }
        });
        Assert.Equal(ErrorCode.RateLimited, error.Code);
        Assert.Equal(5, disbanded);
    }

    [Fact]
    public async Task ReadsShareAPerUserBudget() {
        var alice = await this._server.RegisterAsync("Alice Reader");
        var channelId = await alice.Session.CreateChannelAsync("Reading", Ct);

        ServerErrorException? limited = null;
        for (var i = 0; i < 400 && limited == null; i++) {
            ClientFrame frame = (i % 3) switch {
                0 => new ClientFrame { ListChannels = new ListChannels() },
                1 => new ClientFrame { GetIdentities = new GetIdentities { UserIds = { alice.UserId } } },
                _ => new ClientFrame { FetchEpochKeys = new FetchEpochKeys { ChannelId = channelId } },
            };

            try {
                await alice.Session.SendRawAsync(frame, Ct);
            } catch (ServerErrorException ex) {
                limited = ex;
            }
        }

        Assert.Equal(ErrorCode.RateLimited, limited?.Code);
    }

    [Fact]
    public async Task SettingTheSameRankChangesAndAnnouncesNothing() {
        var alice = await this._server.RegisterAsync("Alice Ranks");
        var bob = await this._server.RegisterAsync("Bob Ranks");
        var channelId = await alice.Session.CreateChannelAsync("Ranks", Ct);
        await AddMemberAsync(alice, channelId, bob);

        await alice.Session.SetRankAsync(channelId, bob.UserId, Rank.Member, Ct);
        await alice.Session.SetRankAsync(channelId, bob.UserId, Rank.Moderator, Ct);

        await WaitFor(() => bob.Notices.FirstOrDefault(n => n.Text.Contains("is now moderator")));
        Assert.DoesNotContain(bob.Notices, n => n.Text.Contains("is now member"));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>An invite request whose log entry is correctly made and signed by <paramref name="inviter"/> for the server's log head.</summary>
    /// <param name="seal">Seals the channel name for the invite entry at the given position.</param>
    private ClientFrame Invite(string channelId, TestClient inviter, long inviteeId, MemberKeys inviteeKeys, Func<LogPosition, (SealedBox SealedName, byte[] Signature)> seal) {
        var entry = this._server.NextEntry(channelId, inviter, MembershipEntryKind.Invite, inviteeId, inviteeKeys);
        var (sealedName, signature) = seal(MembershipEntries.PositionOf(entry));
        return new ClientFrame {
            InviteMember = new InviteMember { ChannelId = channelId, Entry = entry, SealedName = sealedName, Signature = ByteString.CopyFrom(signature) },
        };
    }

    private sealed record Invitee(long UserId, MemberKeys Keys);

    /// <returns>How many invites succeeded before the server said RATE_LIMITED, and what it said then (null if it never did).</returns>
    private async Task<(int Sent, ServerErrorException? Refused)> InviteUntilLimitedAsync(TestClient inviter, List<(string ChannelId, Invitee Invitee)> invites) {
        using var keys = inviter.LoadIdentity();
        var sent = 0;
        foreach (var (channelId, invitee) in invites) {
            try {
                await inviter.Session.SendRawAsync(this.Invite(channelId, inviter, invitee.UserId, invitee.Keys,
                    at => ChannelCrypto.SealInvite("Seeded", channelId, at, invitee.UserId, invitee.Keys.AgreementPublicKey, keys, inviter.UserId)), Ct);
            } catch (ServerErrorException ex) when (ex.Code == ErrorCode.RateLimited) {
                return (sent, ex);
            }

            sent++;
        }

        return (sent, null);
    }

    /// <summary>
    /// Replaces the test's server with one whose clock stands still until the test moves it, so what the invite limits
    /// allow doesn't depend on how fast the test runs.
    /// </summary>
    private async Task<StoppedClock> StopTheClockAsync(CapturingLoggerProvider? logs = null, params (string Key, string Value)[] settings) {
        var clock = new StoppedClock();
        await this._server.DisposeAsync();
        this._server = new Harness(serverTime: clock, logs: logs, settings: settings);
        return clock;
    }

    /// <summary>Channels with <paramref name="admin"/> as admin, created in the database directly (no create limit).</summary>
    private List<string> SeedChannels(TestClient admin, int count) {
        using var keys = admin.LoadIdentity();
        var channels = new List<string>();
        for (var i = 0; i < count; i++) {
            var channelId = Guid.NewGuid().ToString("N");
            var key = ChannelCrypto.NewEpochKey();
            var genesis = SignedLogMembershipProvider.Instance.CreateGenesis(channelId, keys, admin.UserId, 1);
            var position = MembershipEntries.PositionOf(genesis);
            this._server.Database.CreateChannel(channelId, genesis,
                ChannelCrypto.SealEpochKey(key, channelId, 0, position, keys, admin.UserId, admin.UserId, keys.AgreementPublicKey),
                ChannelCrypto.EncryptName($"Seeded {i}", key, channelId, 0, position, keys, admin.UserId));
            channels.Add(channelId);
        }

        return channels;
    }

    /// <summary>An invite appended straight to the database's log (no caps), validly signed by the inviter.</summary>
    private void SeedInvite(TestClient inviter, string channelId, TestClient invitee) {
        using var keys = inviter.LoadIdentity();
        var entry = this._server.NextEntry(channelId, inviter, MembershipEntryKind.Invite, invitee.UserId, invitee.Keys());
        var (sealedName, signature) = ChannelCrypto.SealInvite("Seeded", channelId, MembershipEntries.PositionOf(entry), invitee.UserId, invitee.Keys().AgreementPublicKey, keys, inviter.UserId);
        Assert.True(this._server.Database.AppendEntry(channelId, entry, sealedName, signature));
    }

    /// <summary>A registered user with no client, straight in the database.</summary>
    private Invitee SeedUser(string name) {
        using var keys = IdentityKeys.Generate();
        var (user, _) = this._server.Database.RegisterUser(RequestHandler.DebugUserId(name), name, 0, ProtocolInfo.DebugWorldName, keys.ToBundle(), true);
        return new Invitee(user.UserId, MemberKeys.Of(keys));
    }
}
