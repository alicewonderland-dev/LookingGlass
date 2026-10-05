using System.Collections.Immutable;
using LookingGlass.Core.Client;
using LookingGlass.Protocol;

namespace LookingGlass.Tests;

/// <summary>
/// What "Reset my identity" does with each channel before the old key is retired (see <see cref="IdentityResetPlan"/>),
/// and what the user is told about a channel whose place belongs to an old key.
/// </summary>
public sealed class IdentityResetPlanTests {
    private static readonly User Me = new() { UserId = 1, Name = "Alice Liddell", WorldName = "Twintania" };
    private static readonly User Bob = new() { UserId = 2, Name = "Bob Hatter", WorldName = "Lich" };
    private static readonly User Carol = new() { UserId = 3, Name = "Carol Queen", WorldName = "Odin" };
    private static readonly LogPosition Head = new() { Seq = 4 };

    private static MemberView Member(User user, Rank rank) => new(user, rank, "11111", false);

    private static ChannelView Channel(string id, Rank myRank, bool oldKey = false, params MemberView[] others) {
        // Under an old key, the log still has this user as admin (say), but not under the current key: MyRank is Unspecified.
        var members = new List<MemberView> { Member(Me, oldKey ? Rank.Admin : myRank) };
        members.AddRange(others);
        return new ChannelView(id, $"Channel {id}", 1, 1, true, false, myRank, [.. members], Head, null, oldKey);
    }

    private static InviteView Invite(string id) => new(id, Bob, $"Invite {id}", true, DateTimeOffset.UnixEpoch, false, "22222");

    private static SessionSnapshot Snapshot(ConnectionState state, bool loaded, ChannelView[] channels, InviteView[]? invites = null) =>
        SessionSnapshot.Empty with {
            State = state,
            Me = Me,
            Channels = [.. channels],
            Invites = [.. invites ?? []],
            ChannelsLoaded = loaded,
        };

    [Fact]
    public void MembersModeratorsAndALoneAdminLeave() {
        var plan = IdentityResetPlan.Of(Snapshot(ConnectionState.Ready, true, [
            Channel("a", Rank.Member, others: Member(Bob, Rank.Admin)),
            Channel("b", Rank.Moderator, others: [Member(Bob, Rank.Admin), Member(Carol, Rank.Member)]),
            // Alone (an invitee doesn't count: the log lets the admin leave, and the channel goes with them).
            Channel("c", Rank.Admin, others: Member(Carol, Rank.Invited)),
        ]));

        Assert.True(plan.Connected);
        Assert.Equal(["a", "b", "c"], plan.ToLeave.Select(channel => channel.Id));
        Assert.Empty(plan.Kept);
    }

    [Fact]
    public void TheAdminOfAChannelOthersAreStillInCantLeaveIt() {
        var plan = IdentityResetPlan.Of(Snapshot(ConnectionState.Ready, true, [
            Channel("a", Rank.Admin, others: Member(Bob, Rank.Member)),
            Channel("b", Rank.Member, others: Member(Carol, Rank.Admin)),
        ]));

        Assert.Equal([new ResetChannelStep(plan.Channels[0].Channel, ResetChannelAction.KeepLastAdmin)], plan.Kept);
        Assert.Equal(["b"], plan.ToLeave.Select(channel => channel.Id));
    }

    [Fact]
    public void ChannelsTheCurrentKeyIsntAMemberOfAreKept() {
        var plan = IdentityResetPlan.Of(Snapshot(ConnectionState.Ready, true, [
            // An earlier reset's leftover: only the older key could leave it.
            Channel("old", Rank.Unspecified, oldKey: true, others: Member(Bob, Rank.Member)),
            // Its log isn't verified yet, so nothing can be signed for it.
            Channel("unverified", Rank.Unspecified) with { LogHead = null, Members = [] },
        ]));

        Assert.Empty(plan.ToLeave);
        Assert.Equal([ResetChannelAction.KeepOldKey, ResetChannelAction.KeepUnverified], plan.Channels.Select(step => step.Action));
    }

    [Fact]
    public void InvitesAreDeclined() {
        var plan = IdentityResetPlan.Of(Snapshot(ConnectionState.Ready, true, [], [Invite("x"), Invite("y")]));

        Assert.Equal(["x", "y"], plan.Declines.Select(invite => invite.ChannelId));
    }

    [Theory]
    [InlineData(ConnectionState.Ready, false)]
    [InlineData(ConnectionState.Reconnecting, true)]
    [InlineData(ConnectionState.Stopped, false)]
    [InlineData(ConnectionState.Unregistered, false)]
    public void NothingCanBeLeftFirstWithoutTheCompleteListOnALiveLogin(ConnectionState state, bool loaded) {
        var plan = IdentityResetPlan.Of(Snapshot(state, loaded, [Channel("a", Rank.Member, others: Member(Bob, Rank.Admin))]));

        Assert.False(plan.Connected);
        // Still worked out, to say which channels will stay in the list.
        Assert.Equal(["a"], plan.ToLeave.Select(channel => channel.Id));
    }

    [Fact]
    public void AnOldKeyChannelIsExplainedInPlainWordsAndPointsAtRemovingIt() {
        var channel = Channel("old", Rank.Unspecified, oldKey: true, others: Member(Bob, Rank.Member)) with {
            HasKey = false, MembershipWarning = PlainMessages.OldKeyChannel,
        };

        var attention = ChannelAttention.Of(channel);

        Assert.Equal(AttentionLevel.Warning, attention.Level);
        // Not "waiting for the key": no key is coming for an old key's place.
        Assert.Equal([PlainMessages.OldKeyChannel], attention.Reasons);
        Assert.Contains("Remove from my list", PlainMessages.OldKeyChannel);
        Assert.Contains("Remove from my list", PlainMessages.CantLeaveOldKeyChannel);
    }

    [Theory]
    [InlineData("It isn't signed with the key the log knows its author by.")]
    [InlineData("Leaving (encrypted channel 800fcbd0) failed: It isn't signed with the key the log knows its author by.")]
    [InlineData("Invalid membership log entry: It isn't signed with the key the log knows its author by. (InvalidRequest)")]
    public void TheLogSignatureErrorIsSaidInPlainWords(string message) {
        var plain = PlainMessages.Of(message);

        Assert.DoesNotContain("isn't signed with the key the log knows", plain);
        Assert.Contains("Remove from my list", plain);
        // What was being done stays.
        if (message.StartsWith("Leaving", StringComparison.Ordinal)) {
            Assert.StartsWith("Leaving (encrypted channel 800fcbd0) failed: ", plain);
        }
    }

    [Fact]
    public void OtherMessagesAreLeftAsTheyAre() {
        Assert.Equal("Too many requests; slow down.", PlainMessages.Of("Too many requests; slow down."));
    }
}
