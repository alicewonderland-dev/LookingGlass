using Google.Protobuf;
using LookingGlass.Core.Client;
using LookingGlass.Core.Membership;
using LookingGlass.Protocol;
using LookingGlass.Server.Data;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// "Remove from my list" (ForgetChannel): a channel whose place belongs to identity keys the account no longer has (it
/// registered new keys on a server from before key recovery) can't be left (only the old keys could sign that), so the
/// server stops listing it instead. The log isn't touched: the others still see the old keys as a member, and rekey and chat as before.
/// </summary>
public sealed class ForgetChannelTests : IAsyncLifetime {
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
    /// A reset on a server from before key recovery: the key is retired and replaced, and registering the new one leaves the
    /// account's places with the old key, as such a server did. Places like that are still about, from then.
    /// </summary>
    internal static async Task<TestClient> RegisterOnAnOldServerAsync(Harness server, TestClient client) {
        await client.Session.RetireIdentityAsync(Ct);
        await client.Session.DisposeAsync();
        var secrets = client.Store.Load();
        secrets.ResetIdentity();
        client.Store.Save(secrets);

        var reset = server.StartClient(client.Name, client.Store);
        await WaitFor(() => reset.Session.Snapshot.State == ConnectionState.Unregistered ? new object() : null);
        server.Handler.KeepPlacesOnNewKeysForTests = true;
        try {
            await reset.Session.StartRegistrationAsync(new Character { Name = client.Name, WorldName = ProtocolInfo.DebugWorldName }, Ct);
            await reset.Session.CompleteRegistrationAsync(Ct);
        } finally {
            server.Handler.KeepPlacesOnNewKeysForTests = false;
        }

        await WaitFor(() => reset.Session.Snapshot is { State: ConnectionState.Ready, ChannelsLoaded: true } ? new object() : null);
        return reset;
    }

    [Fact]
    public async Task AStaleMembershipCanBeRemovedFromTheListWithoutChangingTheChannelForOthers() {
        var bob = await this._server.RegisterAsync("Bob Keeps Chatting");
        var alice = await this._server.RegisterAsync("Alice Stuck");
        var carol = await this._server.RegisterAsync("Carol Stays");
        var channelId = await bob.Session.CreateChannelAsync("Stuck Channel", Ct);
        await AddMemberAsync(bob, channelId, alice);
        await AddMemberAsync(bob, channelId, carol);
        var oldKeys = alice.Keys();

        var again = await RegisterOnAnOldServerAsync(this._server, alice);

        // Listed, but the place belongs to the old key: said in plain words, and Leave can't work.
        var stale = await WaitFor(() => again.Session.Snapshot.FindChannel(channelId) is { OldKeyMembership: true } c ? c : null);
        Assert.Equal(Rank.Unspecified, stale.MyRank);
        Assert.Equal(PlainMessages.OldKeyChannel, stale.MembershipWarning);
        var leave = await Assert.ThrowsAsync<InvalidOperationException>(() => again.Session.LeaveAsync(channelId, Ct));
        Assert.Equal(PlainMessages.CantLeaveOldKeyChannel, leave.Message);

        var headBefore = this._server.Database.GetChannel(channelId)!.LogHead;
        await again.Session.ForgetChannelAsync(channelId, Ct);

        // Gone from the list, here and on the server, and it stays gone.
        Assert.Null(again.Session.Snapshot.FindChannel(channelId));
        Assert.Empty(this._server.Database.GetChannelsForUser(again.UserId));
        Assert.Equal(0, this._server.Database.CountChannelsForUser(again.UserId));
        await again.Session.RefreshAsync(Ct);
        Assert.Null(again.Session.Snapshot.FindChannel(channelId));

        // The log is as it was: the old key is still a member, for the server and for the others.
        Assert.True(MembershipEntries.SamePosition(headBefore, this._server.Database.GetChannel(channelId)!.LogHead));
        Assert.Equal(oldKeys, this._server.ServerMembership(channelId).FindMember(again.UserId)!.Keys);
        Assert.Equal(oldKeys, bob.Session.MembershipForTests(channelId).FindMember(again.UserId)!.Keys);

        // The others chat, and rekey (sealing to every member in the log, the old key included), as before.
        await bob.Session.SendTextAsync(channelId, "still here", Ct);
        await WaitFor(() => carol.Messages.FirstOrDefault(m => m.Text == "still here"));
        var epoch = carol.Session.Snapshot.FindChannel(channelId)!.Epoch;
        // By carol: bob rekeys four times in this test already, and rekeys are rate-limited per member.
        await carol.Session.RekeyAsync(channelId, Ct, force: true);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { HasKey: true } c && c.Epoch == epoch + 1 ? c : null);
        await bob.Session.SendTextAsync(channelId, "new key works", Ct);
        await WaitFor(() => carol.Messages.FirstOrDefault(m => m.Text == "new key works"));
        // Nothing of the channel reaches the account that removed it.
        Assert.DoesNotContain(again.Messages, m => m.ChannelId == channelId);

        // A moderator can still remove the old key (the server's tables still have it), then invite the new one.
        await bob.Session.KickAsync(channelId, again.UserId, Ct);
        await AddMemberAsync(bob, channelId, again);
        var back = again.Session.Snapshot.FindChannel(channelId)!;
        Assert.Equal(Rank.Member, back.MyRank);
        Assert.False(back.OldKeyMembership);
        await bob.Session.SendTextAsync(channelId, "welcome back", Ct);
        await WaitFor(() => again.Messages.FirstOrDefault(m => m.Text == "welcome back"));
    }

    /// <summary>
    /// The owner's case before key recovery: the only admin resets, so nobody can remove the old key or invite the new one. The channel can
    /// still be removed from the list, and the plain member left behind can still chat and rekey with the admin's old key
    /// counted as a member.
    /// </summary>
    [Fact]
    public async Task TheOnlyAdminWhoResetCanRemoveTheChannelAndTheOthersCarryOn() {
        var alice = await this._server.RegisterAsync("Alice Only Admin");
        var bob = await this._server.RegisterAsync("Bob Plain Member");
        var channelId = await alice.Session.CreateChannelAsync("No Admin Left", Ct);
        await AddMemberAsync(alice, channelId, bob);

        var again = await RegisterOnAnOldServerAsync(this._server, alice);
        await WaitFor(() => again.Session.Snapshot.FindChannel(channelId) is { OldKeyMembership: true } c ? c : null);
        await again.Session.ForgetChannelAsync(channelId, Ct);
        Assert.Null(again.Session.Snapshot.FindChannel(channelId));

        // The old admin's (stale, forgotten) place doesn't stop bob's rekey.
        var epoch = bob.Session.Snapshot.FindChannel(channelId)!.Epoch;
        await bob.Session.RekeyAsync(channelId, Ct, force: true);
        Assert.Equal(epoch + 1, bob.Session.Snapshot.FindChannel(channelId)!.Epoch);
        Assert.Equal(epoch + 1, this._server.Database.GetChannel(channelId)!.Epoch);
    }

    /// <summary>An invite made for the old key can't be accepted or declined with the new one; declining removes it from the list.</summary>
    [Fact]
    public async Task AnInviteForTheOldKeyIsRemovedByDecliningIt() {
        var bob = await this._server.RegisterAsync("Bob Invited Old");
        var alice = await this._server.RegisterAsync("Alice Old Invite");
        var channelId = await bob.Session.CreateChannelAsync("Old Invite", Ct);
        await bob.Session.InviteAsync(channelId, alice.Name, ProtocolInfo.DebugWorldName, Ct);
        await WaitFor(() => alice.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == channelId && i.ChannelName != null));

        var again = await RegisterOnAnOldServerAsync(this._server, alice);
        await WaitFor(() => again.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == channelId));

        await again.Session.RespondToInviteAsync(channelId, accept: false, Ct);

        Assert.Empty(again.Session.Snapshot.Invites);
        Assert.Empty(this._server.Database.GetInvitesForUser(again.UserId));
        Assert.Equal(0, this._server.Database.CountInvitesForUser(again.UserId));
        // Still open in the log (only the old key could decline it, and a moderator can cancel it).
        Assert.NotNull(this._server.ServerMembership(channelId).FindInvitee(again.UserId));
        await bob.Session.KickAsync(channelId, again.UserId, Ct);
        Assert.Null(this._server.ServerMembership(channelId).FindInvitee(again.UserId));
    }

    [Fact]
    public async Task ForgettingIsRefusedForACurrentMembershipAChannelNeverJoinedAndBeforeLoggingIn() {
        var alice = await this._server.RegisterAsync("Alice Current");
        var bob = await this._server.RegisterAsync("Bob Never In");
        var channelId = await alice.Session.CreateChannelAsync("Current", Ct);
        var otherId = await bob.Session.CreateChannelAsync("Bob's Own", Ct);

        // Under the current key: leave it instead. Nothing changes.
        var current = await Assert.ThrowsAsync<ServerErrorException>(() => alice.Session.SendRawAsync(Forget(channelId), Ct));
        Assert.Equal(ErrorCode.Forbidden, current.Code);
        Assert.Contains("leave it", current.ServerMessage);
        Assert.Single(this._server.Database.GetChannelsForUser(alice.UserId));
        // The client refuses it too, before asking.
        await Assert.ThrowsAsync<InvalidOperationException>(() => alice.Session.ForgetChannelAsync(channelId, Ct));

        // Not in it at all, or no such channel.
        Assert.Equal(ErrorCode.NotFound, await ErrorOf(alice, Forget(otherId)));
        Assert.Equal(ErrorCode.NotFound, await ErrorOf(alice, Forget(Guid.NewGuid().ToString("N"))));
        Assert.Equal(ErrorCode.InvalidRequest, await ErrorOf(alice, Forget("not a channel")));

        // An invite under the current key: decline it instead.
        await bob.Session.InviteAsync(otherId, alice.Name, ProtocolInfo.DebugWorldName, Ct);
        Assert.Equal(ErrorCode.Forbidden, await ErrorOf(alice, Forget(otherId)));
        Assert.Single(this._server.Database.GetInvitesForUser(alice.UserId));

        // Not before logging in.
        await using var raw = await this._server.ConnectRawAsync();
        Assert.Equal(ErrorCode.NotAuthenticated, (await raw.SendAsync(Forget(channelId))).Error?.Code);
        Assert.Single(this._server.Database.GetChannelsForUser(alice.UserId));
    }

    /// <summary>
    /// A forgotten place can do nothing else: the account isn't in the channel as far as every other request goes (no
    /// log, keys, messages, renames or disbanding, even as the old admin), and asking to forget it again finds nothing.
    /// </summary>
    [Fact]
    public async Task AForgottenPlaceCanDoNothingElse() {
        var alice = await this._server.RegisterAsync("Alice Forgot Admin");
        var bob = await this._server.RegisterAsync("Bob Still Member");
        var channelId = await alice.Session.CreateChannelAsync("Forgotten", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var again = await RegisterOnAnOldServerAsync(this._server, alice);
        await WaitFor(() => again.Session.Snapshot.FindChannel(channelId) is { OldKeyMembership: true } c ? c : null);
        await again.Session.ForgetChannelAsync(channelId, Ct);

        foreach (var request in new[] {
                     new ClientFrame { FetchMembershipLog = new FetchMembershipLog { ChannelId = channelId } },
                     new ClientFrame { FetchEpochKeys = new FetchEpochKeys { ChannelId = channelId } },
                     new ClientFrame { DisbandChannel = new DisbandChannel { ChannelId = channelId } },
                     new ClientFrame { RenameChannel = new RenameChannel { ChannelId = channelId } },
                     new ClientFrame { SendMessage = new SendMessage { ChannelId = channelId, MessageId = ByteString.CopyFrom(new byte[16]) } },
                     Forget(channelId),
                 }) {
            Assert.Equal(ErrorCode.NotFound, await ErrorOf(again, request));
        }

        Assert.NotNull(this._server.Database.GetChannel(channelId));
        Assert.Equal([again.UserId], this._server.Database.GetMembers(channelId).Where(m => m.Forgotten).Select(m => m.User.UserId));
    }

    /// <summary>
    /// A channel the server already stopped listing to this account (removed from another device, say) still comes off
    /// this list, without an error.
    /// </summary>
    [Fact]
    public async Task RemovingAChannelTheServerAlreadyForgotStillRemovesItHere() {
        var bob = await this._server.RegisterAsync("Bob Forgotten Elsewhere");
        var alice = await this._server.RegisterAsync("Alice Two Devices");
        var channelId = await bob.Session.CreateChannelAsync("Forgotten Elsewhere", Ct);
        await AddMemberAsync(bob, channelId, alice);
        var again = await RegisterOnAnOldServerAsync(this._server, alice);
        await WaitFor(() => again.Session.Snapshot.FindChannel(channelId) is { OldKeyMembership: true } c ? c : null);
        Assert.Equal(ForgetResult.Forgotten, this._server.Database.ForgetStaleMembership(channelId, again.UserId));

        await again.Session.ForgetChannelAsync(channelId, Ct);

        Assert.Null(again.Session.Snapshot.FindChannel(channelId));
    }

    /// <summary>
    /// Changing the members through an old key's place (here, as the old admin) is refused before anything is sent, in
    /// plain words rather than the log's "isn't signed with the key the log knows its author by".
    /// </summary>
    [Fact]
    public async Task MemberChangesThroughAnOldKeysPlaceAreRefusedInPlainWords() {
        var alice = await this._server.RegisterAsync("Alice Old Admin Acts");
        var bob = await this._server.RegisterAsync("Bob Not Removed");
        var channelId = await alice.Session.CreateChannelAsync("Old Admin", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var again = await RegisterOnAnOldServerAsync(this._server, alice);
        await WaitFor(() => again.Session.Snapshot.FindChannel(channelId) is { OldKeyMembership: true } c ? c : null);
        var head = this._server.Database.GetChannel(channelId)!.LogHead;

        var kick = await Assert.ThrowsAsync<InvalidOperationException>(() => again.Session.KickAsync(channelId, bob.UserId, Ct));
        Assert.Equal(PlainMessages.OldKeyCantChangeMembers, kick.Message);
        var handOver = await Assert.ThrowsAsync<InvalidOperationException>(() => again.Session.SetRankAsync(channelId, bob.UserId, Rank.Admin, Ct));
        Assert.Equal(PlainMessages.OldKeyCantChangeMembers, handOver.Message);

        Assert.True(MembershipEntries.SamePosition(head, this._server.Database.GetChannel(channelId)!.LogHead));
    }

    private static async Task<ErrorCode?> ErrorOf(TestClient client, ClientFrame request) {
        try {
            await client.Session.SendRawAsync(request, Ct);
            return null;
        } catch (ServerErrorException ex) {
            return ex.Code;
        }
    }

    private static ClientFrame Forget(string channelId) => new() { ForgetChannel = new ForgetChannel { ChannelId = channelId } };
}
