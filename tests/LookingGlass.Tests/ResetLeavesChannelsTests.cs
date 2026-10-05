using LookingGlass.Core.Client;
using LookingGlass.Core.Membership;
using LookingGlass.Protocol;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// "Reset my identity" leaves the user's channels first, while the old key still exists to sign the leaves, so they don't
/// stay behind as places nobody can clear. Where the log doesn't allow a leave (the admin of a channel others are still
/// in), the channel stays, and is removed from the list afterwards.
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
        // Alone in her own: leaving it is allowed, and the channel goes.
        var own = await alice.Session.CreateChannelAsync("Alice's Own", Ct);
        // And a pending invite.
        var invited = await bob.Session.CreateChannelAsync("Bob Invites", Ct);
        await bob.Session.InviteAsync(invited, alice.Name, ProtocolInfo.DebugWorldName, Ct);
        await WaitFor(() => alice.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == invited && i.ChannelName != null));
        await WaitFor(() => alice.Session.Snapshot.FindChannel(carols) is { MyRank: Rank.Moderator } c ? c : null);
        var oldKeys = alice.Keys();
        var userId = alice.UserId;

        var (cleanup, reset) = await this.ResetAsync(alice);

        Assert.Equal(new[] { bobs, carols, own }.Order(), cleanup.Left.Select(channel => channel.Id).Order());
        Assert.Equal([invited], cleanup.Declined.Select(invite => invite.ChannelId));
        Assert.Empty(cleanup.Kept);
        Assert.Empty(cleanup.Failed);

        // Each log ends with her leave, signed by the old key, and the others see her gone.
        foreach (var (owner, channelId) in new[] { (bob, bobs), (carol, carols) }) {
            await WaitFor(() => owner.Session.Snapshot.FindChannel(channelId) is { } c && c.Members.All(m => m.User.UserId != userId) ? c : null);
            var leave = this._server.Database.GetLogEntries(channelId, 0, 100).Last();
            Assert.Equal(MembershipEntryKind.Leave, leave.Kind);
            Assert.Equal(userId, leave.ActorId);
            Assert.Equal(oldKeys.Hash, leave.ActorKeyHash.ToByteArray());
        }

        Assert.Null(this._server.Database.GetChannel(own));
        Assert.Equal(MembershipEntryKind.Decline, this._server.Database.GetLogEntries(invited, 0, 100).Last().Kind);

        // Nothing is left in the new identity's list.
        Assert.Empty(reset.Session.Snapshot.Channels);
        Assert.Empty(reset.Session.Snapshot.Invites);
        Assert.Empty(this._server.Database.GetChannelsForUser(userId));
    }

    /// <summary>
    /// The admin of a channel others are still in can't leave it (the log's rule), so it is kept, said so, and stays in
    /// the list under the old key afterwards, to remove with "Remove from my list". The others keep the channel.
    /// </summary>
    [Fact]
    public async Task TheLastAdminsChannelIsKeptAndCanBeRemovedAfterwards() {
        var alice = await this._server.RegisterAsync("Alice Last Admin");
        var bob = await this._server.RegisterAsync("Bob The Echo");
        var channelId = await alice.Session.CreateChannelAsync("Kept", Ct);
        await AddMemberAsync(alice, channelId, bob);

        var (cleanup, reset) = await this.ResetAsync(alice);

        Assert.Empty(cleanup.Left);
        Assert.Empty(cleanup.Failed);
        var kept = Assert.Single(cleanup.Kept);
        Assert.Equal((channelId, ResetChannelAction.KeepLastAdmin), (kept.Channel.Id, kept.Action));

        var stale = await WaitFor(() => reset.Session.Snapshot.FindChannel(channelId) is { OldKeyMembership: true } c ? c : null);
        Assert.Equal(Rank.Unspecified, stale.MyRank);
        await reset.Session.ForgetChannelAsync(channelId, Ct);
        Assert.Empty(reset.Session.Snapshot.Channels);

        await bob.Session.SendTextAsync(channelId, "carrying on", Ct);
        Assert.NotNull(this._server.Database.GetChannel(channelId));
    }

    /// <summary>A leave that fails (here: the server refuses it) is reported, and the rest of the reset goes on.</summary>
    [Fact]
    public async Task AFailedLeaveIsReportedAndTheRestGoesOn() {
        var alice = await this._server.RegisterAsync("Alice Refused Leave");
        var bob = await this._server.RegisterAsync("Bob Refuses");
        var refused = await bob.Session.CreateChannelAsync("Refused", Ct);
        await AddMemberAsync(bob, refused, alice);
        var fine = await bob.Session.CreateChannelAsync("Fine", Ct);
        await AddMemberAsync(bob, fine, alice);
        // A server that doesn't know her as a member any more (as its tables say), though the log does.
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
        await alice.Session.CreateChannelAsync("Stays", Ct);
        await alice.Session.DisposeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => alice.Session.LeaveChannelsForResetAsync(Ct));
        Assert.False(IdentityResetPlan.Of(alice.Session.Snapshot).Connected);
    }
}
