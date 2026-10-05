using Google.Protobuf;
using LookingGlass.Core.Client;
using LookingGlass.Protocol;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// What the server lets a place under old keys do, and what it tells the account holding it. A stale place (the account
/// registered again with new keys, or reset its identity, without leaving) has no say in the channel: it can only read the
/// log (where the client sees the place is the old key's) and be removed from the account's list. A forgotten place
/// ("Remove from my list") gets nothing about the channel at all, though it stays a member for the log and the others.
/// </summary>
public sealed class StalePlaceTests : IAsyncLifetime {
    private Harness _server = null!;

    public ValueTask InitializeAsync() {
        this._server = new Harness();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() {
        await this._server.DisposeAsync();
        DeleteDirectory(this._server.DataDirectory);
    }

    /// <summary>
    /// The admin's place under the old key can't disband, rename, send or fetch keys, whatever its rank says: none of that
    /// is signed by the log's key for it, and a disband would end the channel for everyone. Said in words the plugin turns
    /// into plain ones. Reading the log, and removing the place from the list, still work.
    /// </summary>
    [Fact]
    public async Task AStalePlaceHasNoSayButCanReadTheLogAndBeRemoved() {
        var alice = await this._server.RegisterAsync("Alice Stale Admin");
        var bob = await this._server.RegisterAsync("Bob Under Stale");
        var channelId = await alice.Session.CreateChannelAsync("Stale Authority", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var again = await ForgetChannelTests.ResetWithoutLeavingAsync(this._server, alice);
        await WaitFor(() => again.Session.Snapshot.FindChannel(channelId) is { OldKeyMembership: true } c ? c : null);

        foreach (var request in new[] {
                     new ClientFrame { RenameChannel = new RenameChannel { ChannelId = channelId } },
                     new ClientFrame { SendMessage = new SendMessage { ChannelId = channelId, MessageId = ByteString.CopyFrom(new byte[16]) } },
                     new ClientFrame { FetchEpochKeys = new FetchEpochKeys { ChannelId = channelId } },
                     new ClientFrame { DisbandChannel = new DisbandChannel { ChannelId = channelId } },
                 }) {
            var refused = await Assert.ThrowsAsync<ServerErrorException>(() => again.Session.SendRawAsync(request, Ct));
            Assert.Equal(ErrorCode.Forbidden, refused.Code);
            Assert.Contains("Remove from my list", PlainMessages.Of(refused.ServerMessage));
        }

        // Nothing changed for bob.
        Assert.NotNull(this._server.Database.GetChannel(channelId));
        await bob.Session.SendTextAsync(channelId, "still ours", Ct);

        // The log can still be read: it is how the client sees whose place this is.
        var log = (await again.Session.SendRawAsync(new ClientFrame { FetchMembershipLog = new FetchMembershipLog { ChannelId = channelId } }, Ct)).MembershipLog;
        Assert.NotNull(log);
        Assert.NotEmpty(log.Entries);

        await again.Session.ForgetChannelAsync(channelId, Ct);
        Assert.Empty(this._server.Database.GetChannelsForUser(again.UserId));
    }

    /// <summary>
    /// The last member whose place isn't forgotten leaving is the last member leaving: the channel goes, rather than
    /// staying on the server with nobody but places their owners removed from their lists.
    /// </summary>
    [Fact]
    public async Task TheLastMemberLeavingBesidesForgottenPlacesDeletesTheChannel() {
        var alice = await this._server.RegisterAsync("Alice Forgets Admin");
        var bob = await this._server.RegisterAsync("Bob Last One");
        var channelId = await alice.Session.CreateChannelAsync("Zombie", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var again = await ForgetChannelTests.ResetWithoutLeavingAsync(this._server, alice);
        await WaitFor(() => again.Session.Snapshot.FindChannel(channelId) is { OldKeyMembership: true } c ? c : null);
        await again.Session.ForgetChannelAsync(channelId, Ct);

        await bob.Session.LeaveAsync(channelId, Ct);

        Assert.Null(this._server.Database.GetChannel(channelId));
        Assert.Empty(this._server.Database.GetChannelsForUser(bob.UserId));
    }

    /// <summary>
    /// Nothing about a channel reaches the account that removed it from its list: not messages, renames, log entries,
    /// rekey requests or new keys, not the presence of members it shares nothing else with (coming online, going offline,
    /// or joining), and not its own removal. Checked on the wire, so a client ignoring them can't hide one.
    /// </summary>
    [Fact]
    public async Task AForgottenMembershipIsToldNothingAboutTheChannel() {
        var bob = await this._server.RegisterAsync("Bob Busy Admin");
        var alice = await this._server.RegisterAsync("Alice Not Listening");
        var carol = await this._server.RegisterAsync("Carol Comes And Goes");
        var dave = await this._server.RegisterAsync("Dave Joins Later");
        var channelId = await bob.Session.CreateChannelAsync("Busy", Ct);
        await AddMemberAsync(bob, channelId, alice);
        await AddMemberAsync(bob, channelId, carol);
        var again = await ForgetChannelTests.ResetWithoutLeavingAsync(this._server, alice);
        await WaitFor(() => again.Session.Snapshot.FindChannel(channelId) is { OldKeyMembership: true } c ? c : null);
        await again.Session.ForgetChannelAsync(channelId, Ct);
        var aliceId = again.UserId;
        await using var raw = await this.ListenAsAsync(again);

        await bob.Session.SendTextAsync(channelId, "hello all", Ct);
        await bob.Session.RenameAsync(channelId, "Busier", Ct);
        // A log entry, dave's presence as he joins, a rekey request, and the new key.
        await AddMemberAsync(bob, channelId, dave);
        // Carol goes offline, and comes back.
        var carolId = carol.UserId;
        await carol.Session.DisposeAsync();
        await WaitFor(() => this._server.Registry.IsOnline(carolId) ? null : new object());
        await this._server.RestartAsync(carol);
        // And the place itself is removed.
        await bob.Session.KickAsync(channelId, aliceId, Ct);

        Assert.NotNull((await raw.SendAsync(new ClientFrame { Ping = new Ping() })).Pong);
        Assert.DoesNotContain(raw.Events, ev => ev.KindCase != Event.KindOneofCase.Announcement);
    }

    /// <summary>
    /// Neither is anything about a channel (or an invite) it removed when that goes: disbanding, a cancelled invite, an
    /// invite that goes with its inviter's removal, or a channel whose last member leaves.
    /// </summary>
    [Fact]
    public async Task AForgottenPlaceIsntToldWhenItsChannelOrInviteGoes() {
        var bob = await this._server.RegisterAsync("Bob Ends Things");
        var carol = await this._server.RegisterAsync("Carol Moderates");
        var alice = await this._server.RegisterAsync("Alice Forgot It All");
        // A channel alice is a member of, which carol disbands.
        var disbanded = await carol.Session.CreateChannelAsync("Disbanded", Ct);
        await AddMemberAsync(carol, disbanded, alice);
        // Invites: one bob cancels, one to a channel bob disbands, one by a moderator bob removes, and one to a channel bob leaves.
        var cancelled = await bob.Session.CreateChannelAsync("Cancelled", Ct);
        var gone = await bob.Session.CreateChannelAsync("Gone", Ct);
        var inviterRemoved = await bob.Session.CreateChannelAsync("Inviter Removed", Ct);
        await AddMemberAsync(bob, inviterRemoved, carol);
        await bob.Session.SetRankAsync(inviterRemoved, carol.UserId, Rank.Moderator, Ct);
        await WaitFor(() => carol.Session.Snapshot.FindChannel(inviterRemoved) is { MyRank: Rank.Moderator } c ? c : null);
        var abandoned = await bob.Session.CreateChannelAsync("Abandoned", Ct);
        foreach (var channelId in new[] { cancelled, gone, abandoned }) {
            await bob.Session.InviteAsync(channelId, alice.Name, ProtocolInfo.DebugWorldName, Ct);
        }

        await carol.Session.InviteAsync(inviterRemoved, alice.Name, ProtocolInfo.DebugWorldName, Ct);
        await WaitFor(() => alice.Session.Snapshot.Invites.Length == 4 ? new object() : null);

        var again = await ForgetChannelTests.ResetWithoutLeavingAsync(this._server, alice);
        await WaitFor(() => again.Session.Snapshot.Invites.Length == 4 && again.Session.Snapshot.FindChannel(disbanded) != null ? new object() : null);
        foreach (var channelId in new[] { disbanded, cancelled, gone, inviterRemoved, abandoned }) {
            await again.Session.ForgetChannelAsync(channelId, Ct);
        }

        var aliceId = again.UserId;
        await using var raw = await this.ListenAsAsync(again);

        await carol.Session.DisbandAsync(disbanded, Ct);
        await bob.Session.KickAsync(cancelled, aliceId, Ct);
        await bob.Session.DisbandAsync(gone, Ct);
        await bob.Session.KickAsync(inviterRemoved, carol.UserId, Ct);
        await bob.Session.LeaveAsync(abandoned, Ct);

        Assert.Null(this._server.Database.GetChannel(abandoned));
        Assert.Null(this._server.ServerMembership(inviterRemoved).FindInvitee(aliceId));
        Assert.NotNull((await raw.SendAsync(new ClientFrame { Ping = new Ping() })).Pong);
        Assert.DoesNotContain(raw.Events, ev => ev.KindCase != Event.KindOneofCase.Announcement);
    }

    /// <summary>
    /// A place under the old key that isn't forgotten still hears about the channel (it is listed, and says so), but is
    /// never the one asked to rekey: it can't sign one, and the channel would wait for it.
    /// </summary>
    [Fact]
    public async Task AStalePlaceIsNeverAskedToRekey() {
        var alice = await this._server.RegisterAsync("Alice Stale Online");
        var bob = await this._server.RegisterAsync("Bob Moderator Rekeys");
        var carol = await this._server.RegisterAsync("Carol Joins Stale");
        var channelId = await alice.Session.CreateChannelAsync("Who Rekeys", Ct);
        await AddMemberAsync(alice, channelId, bob);
        await alice.Session.SetRankAsync(channelId, bob.UserId, Rank.Moderator, Ct);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { MyRank: Rank.Moderator } c ? c : null);
        var again = await ForgetChannelTests.ResetWithoutLeavingAsync(this._server, alice);
        await WaitFor(() => again.Session.Snapshot.FindChannel(channelId) is { OldKeyMembership: true } c ? c : null);
        // Online, as the old admin's (highest-ranked) place, the one the server would otherwise ask.
        await using var raw = await this.ListenAsAsync(again);

        await bob.Session.InviteAsync(channelId, carol.Name, ProtocolInfo.DebugWorldName, Ct);
        await WaitFor(() => carol.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == channelId && i.ChannelName != null));
        await carol.Session.RespondToInviteAsync(channelId, true, Ct);

        Assert.NotNull((await raw.SendAsync(new ClientFrame { Ping = new Ping() })).Pong);
        var asked = raw.Events.Where(ev => ev.RekeyNeeded?.ChannelId == channelId).Select(ev => ev.RekeyNeeded.DesignatedUserId).ToList();
        Assert.NotEmpty(asked);
        Assert.All(asked, designated => Assert.Equal(bob.UserId, designated));
        await WaitFor(() => carol.Session.Snapshot.FindChannel(channelId) is { HasKey: true, RekeyPending: false } c ? c : null);
    }

    /// <summary>
    /// A forgotten place or invite doesn't count for anything: not to see the people in it, read its log, or answer the
    /// invite. Before it is forgotten, the old key's invite can still be read (its log is how the client sees it is the old key's).
    /// </summary>
    [Fact]
    public async Task AForgottenPlaceShowsNobodyAndCantBeReadOrAnswered() {
        var bob = await this._server.RegisterAsync("Bob Invisible");
        var alice = await this._server.RegisterAsync("Alice Sees Nobody");
        var member = await bob.Session.CreateChannelAsync("Was A Member", Ct);
        await AddMemberAsync(bob, member, alice);
        var invited = await bob.Session.CreateChannelAsync("Was Invited", Ct);
        await bob.Session.InviteAsync(invited, alice.Name, ProtocolInfo.DebugWorldName, Ct);
        await WaitFor(() => alice.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == invited && i.ChannelName != null));
        var again = await ForgetChannelTests.ResetWithoutLeavingAsync(this._server, alice);
        await WaitFor(() => again.Session.Snapshot.FindChannel(member) != null && again.Session.Snapshot.Invites.Length == 1 ? new object() : null);

        var fetchLog = new ClientFrame { FetchMembershipLog = new FetchMembershipLog { ChannelId = invited } };
        Assert.Contains(bob.UserId, await this.VisibleAsync(again, bob.UserId));
        Assert.NotNull((await again.Session.SendRawAsync(fetchLog, Ct)).MembershipLog);

        // Each of the two shows bob on its own.
        await again.Session.ForgetChannelAsync(member, Ct);
        Assert.Contains(bob.UserId, await this.VisibleAsync(again, bob.UserId));
        await again.Session.ForgetChannelAsync(invited, Ct);
        Assert.Empty(await this.VisibleAsync(again, bob.UserId));

        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<ServerErrorException>(() => again.Session.SendRawAsync(fetchLog, Ct))).Code);
        var answer = new ClientFrame { RespondToInvite = new RespondToInvite { ChannelId = invited } };
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<ServerErrorException>(() => again.Session.SendRawAsync(answer, Ct))).Code);
    }

    /// <summary>
    /// An invite row written again for a new invite (which the log only allows once the old one is gone) is a new place,
    /// not a forgotten one. Played by appending straight to the database, as a server skipping the log's checks would.
    /// </summary>
    [Fact]
    public async Task AnInviteWrittenAgainIsntForgotten() {
        var bob = await this._server.RegisterAsync("Bob Invites Twice");
        var alice = await this._server.RegisterAsync("Alice Invited Twice");
        var channelId = await bob.Session.CreateChannelAsync("Twice", Ct);
        await bob.Session.InviteAsync(channelId, alice.Name, ProtocolInfo.DebugWorldName, Ct);
        await WaitFor(() => alice.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == channelId && i.ChannelName != null));
        var again = await ForgetChannelTests.ResetWithoutLeavingAsync(this._server, alice);
        await WaitFor(() => again.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == channelId));
        await again.Session.ForgetChannelAsync(channelId, Ct);
        Assert.Empty(this._server.Database.GetInvitesForUser(again.UserId));

        var entry = this._server.ForgeEntry(channelId, bob, MembershipEntryKind.Invite, again.UserId, again.Keys());
        Assert.True(this._server.Database.AppendEntry(channelId, entry, new SealedBox(), new byte[64]));

        Assert.Single(this._server.Database.GetInvitesForUser(again.UserId));
    }

    /// <summary>A place is under the current keys only if both keys are the account's now: a new agreement key alone makes it stale too.</summary>
    [Fact]
    public async Task APlaceIsCurrentOnlyWithBothKeys() {
        var alice = await this._server.RegisterAsync("Alice One Key Changed");
        var channelId = await alice.Session.CreateChannelAsync("Half Changed", Ct);
        Assert.Equal((Rank.Admin, true), this._server.Database.GetPlace(channelId, alice.UserId));

        this._server.ExecuteSql("UPDATE users SET agreement_key = randomblob(32) WHERE user_id = $u;", ("$u", alice.UserId));

        Assert.Equal((Rank.Admin, false), this._server.Database.GetPlace(channelId, alice.UserId));
        Assert.False(Assert.Single(this._server.Database.GetMembers(channelId)).CurrentKeys);
    }

    [Fact]
    public async Task RemovingFromTheListIsRateLimited() {
        var alice = await this._server.RegisterAsync("Alice Forgets A Lot");

        ServerErrorException? limited = null;
        for (var i = 0; i < 400 && limited?.Code != ErrorCode.RateLimited; i++) {
            try {
                await alice.Session.SendRawAsync(new ClientFrame { ForgetChannel = new ForgetChannel { ChannelId = Guid.NewGuid().ToString("N") } }, Ct);
            } catch (ServerErrorException ex) {
                limited = ex;
            }
        }

        Assert.Equal(ErrorCode.RateLimited, limited?.Code);
    }

    /// <summary>The users the server shows <paramref name="client"/> of those asked for.</summary>
    private async Task<List<long>> VisibleAsync(TestClient client, params long[] userIds) {
        var response = await client.Session.SendRawAsync(new ClientFrame { GetIdentities = new GetIdentities { UserIds = { userIds } } }, Ct);
        return response.Identities.Identities_.Select(identity => identity.User.UserId).ToList();
    }

    /// <summary>
    /// The account of <paramref name="client"/>, logged in on a raw connection that keeps every event it is sent, in place of
    /// its session (which is closed: one connection per account is online).
    /// </summary>
    private async Task<RawConnection> ListenAsAsync(TestClient client) {
        var userId = client.UserId;
        var token = client.Store.Load().DeviceToken!;
        await client.Session.DisposeAsync();
        await WaitFor(() => this._server.Registry.IsOnline(userId) ? null : new object());
        var raw = await this._server.ConnectRawAsync();
        Assert.NotNull((await raw.SendAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = token } })).AuthenticateOk);
        return raw;
    }
}
