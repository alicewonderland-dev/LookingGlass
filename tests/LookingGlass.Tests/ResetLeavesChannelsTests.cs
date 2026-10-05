using LookingGlass.Core.Client;
using LookingGlass.Core.Membership;
using LookingGlass.Protocol;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// "Reset my identity" leaves the user's channels first, while the old key still exists to sign the leaves, so they don't
/// stay behind as places nobody can clear. It is refused while the user is the admin of a channel: they hand admin on,
/// disband it, or leave it (as its only member) themselves first, so losing a channel is always their own choice.
/// </summary>
public sealed class ResetLeavesChannelsTests : IAsyncLifetime {
    private Harness _server = null!;

    public ValueTask InitializeAsync() {
        this._server = new Harness();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() {
        await this._server.DisposeAsync();
        DeleteDirectory(this._server.DataDirectory);
    }

    /// <summary>The reset as the plugin does it: leave first, then retire the key, then new keys, registered again.</summary>
    private async Task<(IdentityResetCleanup Cleanup, TestClient Reset)> ResetAsync(TestClient client) {
        var cleanup = await client.Session.LeaveChannelsForResetAsync(Ct);
        await client.Session.RetireIdentityAsync(Ct);
        await client.Session.DisposeAsync();
        var secrets = client.Store.Load();
        secrets.ResetIdentity();
        client.Store.Save(secrets);

        var reset = this._server.StartClient(client.Name, client.Store);
        await WaitFor(() => reset.Session.Snapshot.State == ConnectionState.Unregistered ? new object() : null);
        await reset.Session.StartRegistrationAsync(new Character { Name = client.Name, WorldName = ProtocolInfo.DebugWorldName }, Ct);
        await reset.Session.CompleteRegistrationAsync(Ct);
        await WaitFor(() => reset.Session.Snapshot is { State: ConnectionState.Ready, ChannelsLoaded: true } ? new object() : null);
        return (cleanup, reset);
    }

    private static async Task AssertLeftWithOldKeyAsync(Harness server, TestClient other, string channelId, long userId, MemberKeys oldKeys) {
        await WaitFor(() => other.Session.Snapshot.FindChannel(channelId) is { } c && c.Members.All(m => m.User.UserId != userId) ? c : null);
        var leave = server.Database.GetLogEntries(channelId, 0, 100).Last();
        Assert.Equal(MembershipEntryKind.Leave, leave.Kind);
        Assert.Equal(userId, leave.ActorId);
        Assert.Equal(oldKeys.Hash, leave.ActorKeyHash.ToByteArray());
        Assert.Null(other.Session.MembershipForTests(channelId).FindMember(userId));
    }

    [Fact]
    public async Task AResetWhileConnectedLeavesEveryChannelCleanly() {
        var alice = await this._server.RegisterAsync("Alice Leaves All");
        var bob = await this._server.RegisterAsync("Bob Hosts");
        var carol = await this._server.RegisterAsync("Carol Hosts Too");
        var bobs = await bob.Session.CreateChannelAsync("Bob's", Ct);
        await AddMemberAsync(bob, bobs, alice);
        var carols = await carol.Session.CreateChannelAsync("Carol's", Ct);
        await AddMemberAsync(carol, carols, alice);
        await carol.Session.SetRankAsync(carols, alice.UserId, Rank.Moderator, Ct);
        // And a pending invite.
        var invited = await bob.Session.CreateChannelAsync("Bob Invites", Ct);
        await bob.Session.InviteAsync(invited, alice.Name, ProtocolInfo.DebugWorldName, Ct);
        await WaitFor(() => alice.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == invited && i.ChannelName != null));
        await WaitFor(() => alice.Session.Snapshot.FindChannel(carols) is { MyRank: Rank.Moderator } c ? c : null);
        var oldKeys = alice.Keys();
        var userId = alice.UserId;
        Assert.Equal(ResetReadiness.Ready, IdentityResetPlan.Of(alice.Session.Snapshot).Readiness);

        var (cleanup, reset) = await this.ResetAsync(alice);

        Assert.Equal(new[] { bobs, carols }.Order(), cleanup.Left.Select(channel => channel.Id).Order());
        Assert.Equal([invited], cleanup.Declined.Select(invite => invite.ChannelId));
        Assert.Empty(cleanup.Kept);
        Assert.Empty(cleanup.Failed);

        // Each log ends with her leave, signed by the old key, and the others see her gone.
        await AssertLeftWithOldKeyAsync(this._server, bob, bobs, userId, oldKeys);
        await AssertLeftWithOldKeyAsync(this._server, carol, carols, userId, oldKeys);
        Assert.Equal(MembershipEntryKind.Decline, this._server.Database.GetLogEntries(invited, 0, 100).Last().Kind);

        // Nothing is left in the new identity's list.
        Assert.Empty(reset.Session.Snapshot.Channels);
        Assert.Empty(reset.Session.Snapshot.Invites);
        Assert.Empty(this._server.Database.GetChannelsForUser(userId));
        Assert.Empty(this._server.Database.GetInvitesForUser(userId));
    }

    /// <summary>
    /// The admin of a channel can't reset until they deal with it themselves: here by handing admin to another member
    /// (which makes them a moderator, as the log's rules say), by disbanding one, and by leaving one they're the only
    /// member of. Then the reset goes ahead, and leaves the channel they handed on.
    /// </summary>
    [Fact]
    public async Task TheAdminMustHandOverDisbandOrLeaveTheirChannelsFirst() {
        var alice = await this._server.RegisterAsync("Alice Admin Of Three");
        var bob = await this._server.RegisterAsync("Bob Gets Admin");
        var carol = await this._server.RegisterAsync("Carol Disbanded");
        var handed = await alice.Session.CreateChannelAsync("Handed On", Ct);
        await AddMemberAsync(alice, handed, bob);
        var disbanded = await alice.Session.CreateChannelAsync("Disbanded", Ct);
        await AddMemberAsync(alice, disbanded, carol);
        var alone = await alice.Session.CreateChannelAsync("Alone", Ct);
        var oldKeys = alice.Keys();

        var plan = IdentityResetPlan.Of(alice.Session.Snapshot);
        Assert.Equal(ResetReadiness.AdminOfChannels, plan.Readiness);
        Assert.Equal(new[] { handed, disbanded, alone }.Order(), plan.AdminOf.Select(channel => channel.Id).Order());
        // Refused, and nothing was left or retired.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => alice.Session.LeaveChannelsForResetAsync(Ct));
        Assert.Equal(plan.Explanation, refused.Message);
        Assert.Equal(3, this._server.Database.CountChannelsForUser(alice.UserId));

        // Hand over: bob becomes admin, alice a moderator.
        await alice.Session.SetRankAsync(handed, bob.UserId, Rank.Admin, Ct);
        Assert.Equal(Rank.Moderator, alice.Session.Snapshot.FindChannel(handed)!.MyRank);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(handed) is { MyRank: Rank.Admin } c ? c : null);
        // Disband.
        await alice.Session.DisbandAsync(disbanded, Ct);
        await WaitFor(() => carol.Session.Snapshot.FindChannel(disbanded) == null ? new object() : null);
        // Leave, as the only member: the channel goes.
        await alice.Session.LeaveAsync(alone, Ct);
        Assert.Null(this._server.Database.GetChannel(alone));

        Assert.Equal(ResetReadiness.Ready, IdentityResetPlan.Of(alice.Session.Snapshot).Readiness);
        var userId = alice.UserId;
        var (cleanup, reset) = await this.ResetAsync(alice);

        Assert.Equal([handed], cleanup.Left.Select(channel => channel.Id));
        Assert.Empty(cleanup.Failed);
        await AssertLeftWithOldKeyAsync(this._server, bob, handed, userId, oldKeys);
        Assert.Empty(reset.Session.Snapshot.Channels);
        await bob.Session.SendTextAsync(handed, "mine now", Ct);
    }

    /// <summary>A leave that fails (here: the server refuses it) is reported, in plain words, and the rest goes on.</summary>
    [Fact]
    public async Task AFailedLeaveIsReportedAndTheRestGoesOn() {
        var alice = await this._server.RegisterAsync("Alice Refused Leave");
        var bob = await this._server.RegisterAsync("Bob Refuses");
        var refused = await bob.Session.CreateChannelAsync("Refused", Ct);
        await AddMemberAsync(bob, refused, alice);
        var fine = await bob.Session.CreateChannelAsync("Fine", Ct);
        await AddMemberAsync(bob, fine, alice);
        // A server whose tables say she's only invited there, though the log has her as a member: it refuses her leave.
        this._server.ExecuteSql("UPDATE members SET rank = 1 WHERE channel_id = $c AND user_id = $u;", ("$c", refused), ("$u", alice.UserId));

        var cleanup = await alice.Session.LeaveChannelsForResetAsync(Ct);

        Assert.Equal([fine], cleanup.Left.Select(channel => channel.Id));
        var failure = Assert.Single(cleanup.Failed);
        Assert.Equal(refused, failure.ChannelId);
        Assert.Equal("Refused", failure.What);
        Assert.NotEmpty(failure.Error);
    }

    [Fact]
    public async Task NothingIsLeftWhileDisconnected() {
        var alice = await this._server.RegisterAsync("Alice Offline Reset");
        var bob = await this._server.RegisterAsync("Bob Offline Host");
        var channelId = await bob.Session.CreateChannelAsync("Stays", Ct);
        await AddMemberAsync(bob, channelId, alice);
        var userId = alice.UserId;
        await alice.Session.DisposeAsync();

        var plan = IdentityResetPlan.Of(alice.Session.Snapshot);
        Assert.Equal(ResetReadiness.Offline, plan.Readiness);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => alice.Session.LeaveChannelsForResetAsync(Ct));
        Assert.Equal(plan.Explanation, refused.Message);
        Assert.NotNull(bob.Session.MembershipForTests(channelId).FindMember(userId));
    }
}
