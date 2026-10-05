using LookingGlass.Core.Client;
using LookingGlass.Protocol;

namespace LookingGlass.Tests;

/// <summary>
/// Whether "Reset my identity" may go ahead, and what it does with each channel before the old key is retired (see
/// <see cref="IdentityResetPlan"/>); and what the user is told about a channel whose place belongs to an old key.
/// </summary>
public sealed class IdentityResetPlanTests {
    private static readonly User Me = new() { UserId = 1, Name = "Alice Liddell", WorldName = "Twintania" };
    private static readonly User Bob = new() { UserId = 2, Name = "Bob Hatter", WorldName = "Lich" };
    private static readonly User Carol = new() { UserId = 3, Name = "Carol Queen", WorldName = "Odin" };
    private static readonly LogPosition Head = new() { Seq = 4 };

    private static MemberView Member(User user, Rank rank) => new(user, rank, "11111", false);

    private static ChannelView Channel(string id, Rank myRank, bool oldKey = false, params MemberView[] others) {
        // Under an old key the log still has this user (as admin, say), but not under the current key: MyRank is Unspecified.
        var members = new List<MemberView> { Member(Me, oldKey ? Rank.Admin : myRank) };
        members.AddRange(others);
        return new ChannelView(id, $"Channel {id}", 1, 1, true, false, myRank, [.. members], Head, null, oldKey);
    }

    private static InviteView Invite(string id) => new(id, Bob, $"Invite {id}", true, DateTimeOffset.UnixEpoch, false, "22222");

    private static SessionSnapshot Snapshot(ChannelView[] channels, InviteView[]? invites = null, ConnectionState state = ConnectionState.Ready, bool loaded = true) =>
        SessionSnapshot.Empty with {
            State = state,
            Me = state == ConnectionState.Ready ? Me : null,
            Channels = [.. channels],
            Invites = [.. invites ?? []],
            ChannelsLoaded = loaded,
        };

    [Fact]
    public void AMemberOrModeratorEverywhereMayResetAndLeavesEverythingFirst() {
        var plan = IdentityResetPlan.Of(Snapshot([
            Channel("a", Rank.Member, others: Member(Bob, Rank.Admin)),
            Channel("b", Rank.Moderator, others: [Member(Bob, Rank.Admin), Member(Carol, Rank.Member)]),
        ], [Invite("x"), Invite("y")]));

        Assert.Equal(ResetReadiness.Ready, plan.Readiness);
        Assert.Empty(plan.AdminOf);
        Assert.Equal(["a", "b"], plan.ToLeave.Select(channel => channel.Id));
        Assert.Equal(["x", "y"], plan.Declines.Select(invite => invite.ChannelId));
        Assert.Empty(plan.Kept);
        Assert.False(plan.CanOverride);
        Assert.Equal("", plan.Explanation);
    }

    /// <summary>
    /// The admin of any channel (others in it or not) can't reset until they hand admin on, disband it, or leave it (as its
    /// only member) themselves: losing a channel is always a choice they make. Each such channel is named, with what to do.
    /// </summary>
    [Fact]
    public void BeingTheAdminOfAnyChannelBlocksTheReset() {
        var plan = IdentityResetPlan.Of(Snapshot([
            Channel("shared", Rank.Admin, others: Member(Bob, Rank.Member)),
            Channel("alone", Rank.Admin),
            Channel("theirs", Rank.Member, others: Member(Carol, Rank.Admin)),
        ]));

        Assert.Equal(ResetReadiness.AdminOfChannels, plan.Readiness);
        Assert.Equal(["shared", "alone"], plan.AdminOf.Select(channel => channel.Id));
        Assert.False(plan.CanOverride);
        Assert.Contains("Channel shared", plan.Explanation);
        Assert.Contains("Channel alone", plan.Explanation);
        Assert.Contains("Make admin", plan.Explanation);
        Assert.Contains("disband", plan.Explanation);
        Assert.Contains("only member", plan.Explanation);
        // The others are still worked out, for once the admin channels are dealt with.
        Assert.Equal(["theirs"], plan.ToLeave.Select(channel => channel.Id));
    }

    [Fact]
    public void ChannelsTheCurrentKeyIsntAMemberOfAreKeptAndDontBlock() {
        var plan = IdentityResetPlan.Of(Snapshot([
            // An earlier reset's leftover, where the old key is the admin: only that key could do anything there.
            Channel("old", Rank.Unspecified, oldKey: true, others: Member(Bob, Rank.Member)),
            // Its log isn't verified yet, so nothing can be signed for it.
            Channel("unverified", Rank.Unspecified) with { LogHead = null, Members = [] },
        ]));

        Assert.Equal(ResetReadiness.Ready, plan.Readiness);
        Assert.Empty(plan.ToLeave);
        Assert.Equal([ResetChannelAction.KeepOldKey, ResetChannelAction.KeepUnverified], plan.Channels.Select(step => step.Action));
    }

    [Fact]
    public void ItWaitsForTheChannelList() {
        var plan = IdentityResetPlan.Of(Snapshot([Channel("a", Rank.Member, others: Member(Bob, Rank.Admin))], loaded: false));

        Assert.Equal(ResetReadiness.Loading, plan.Readiness);
        Assert.False(plan.CanOverride);
        Assert.NotEqual("", plan.Explanation);
    }

    /// <summary>
    /// Not connected, admin status can't be checked and nothing can be left: blocked, with "connect first". Only the user's
    /// explicit say-so (the server is gone for good, or doesn't know the login any more) lets it go ahead without that.
    /// </summary>
    [Theory]
    [InlineData(ConnectionState.Stopped, ResetReadiness.Offline)]
    [InlineData(ConnectionState.Connecting, ResetReadiness.Offline)]
    [InlineData(ConnectionState.Reconnecting, ResetReadiness.Offline)]
    [InlineData(ConnectionState.Unregistered, ResetReadiness.NotLoggedIn)]
    [InlineData(ConnectionState.LoginNotRecognized, ResetReadiness.NotLoggedIn)]
    [InlineData(ConnectionState.Registering, ResetReadiness.NotLoggedIn)]
    public void WithoutALiveLoginNothingCanBeCheckedOrLeft(ConnectionState state, ResetReadiness readiness) {
        // Even a channel the (stale) list says this user is the admin of: it can't be checked.
        var plan = IdentityResetPlan.Of(Snapshot([Channel("a", Rank.Admin, others: Member(Bob, Rank.Member))], state: state, loaded: false));

        Assert.Equal(readiness, plan.Readiness);
        Assert.True(plan.CanOverride);
        Assert.Contains(readiness == ResetReadiness.Offline ? "connect" : "login", plan.Explanation, StringComparison.OrdinalIgnoreCase);
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
    [InlineData("Your place in this channel belongs to the identity key you had before you registered again. A moderator must remove you and invite you again. (Forbidden)")]
    public void TheLogSignatureErrorIsSaidInPlainWords(string message) {
        var plain = PlainMessages.Of(message);

        Assert.DoesNotContain("isn't signed with the key the log knows", plain);
        Assert.DoesNotContain("Invalid membership log entry", plain);
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
