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
    public void AnOldKeysPlaceIsKeptAndDoesntBlock() {
        var plan = IdentityResetPlan.Of(Snapshot([
            // An earlier reset's leftover, where the old key is the admin (as the server says too): only that key could do anything there.
            Channel("old", Rank.Unspecified, oldKey: true, others: Member(Bob, Rank.Member)) with { AdminPerServer = true },
            Channel("a", Rank.Member, others: Member(Bob, Rank.Admin)),
        ]));

        Assert.Equal(ResetReadiness.Ready, plan.Readiness);
        Assert.Empty(plan.AdminOf);
        Assert.Equal(["a"], plan.ToLeave.Select(channel => channel.Id));
        Assert.Equal([ResetChannelAction.KeepOldKey], plan.Kept.Select(step => step.Action));
    }

    /// <summary>
    /// A channel whose log this client hasn't verified (yet) may be one this user is the admin of: until it is, the reset
    /// waits, saying which, rather than go ahead and lose it.
    /// </summary>
    [Fact]
    public void AChannelNotCheckedYetBlocksTheReset() {
        var plan = IdentityResetPlan.Of(Snapshot([
            Channel("unverified", Rank.Unspecified) with { LogHead = null, Members = [] },
            Channel("a", Rank.Member, others: Member(Bob, Rank.Admin)),
        ]));

        Assert.Equal(ResetReadiness.Checking, plan.Readiness);
        Assert.Equal(["unverified"], plan.Unchecked.Select(channel => channel.Id));
        Assert.Empty(plan.AdminOf);
        Assert.False(plan.CanOverride);
        Assert.Contains("Channel unverified", plan.Explanation);
        Assert.Contains("refresh", plan.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The server says this user is the admin at a point of the log this client hasn't verified up to (made admin since,
    /// say): the reset waits for the log, and counts the channel as one they're the admin of meanwhile.
    /// </summary>
    [Theory]
    [InlineData(Rank.Unspecified)]
    [InlineData(Rank.Member)]
    [InlineData(Rank.Moderator)]
    public void TheServerSayingThisUserIsTheAdminBlocksTheReset(Rank verified) {
        var plan = IdentityResetPlan.Of(Snapshot([
            Channel("made admin", verified, others: Member(Bob, Rank.Admin)) with { AdminPerServer = true },
        ]));

        Assert.Equal(ResetReadiness.Checking, plan.Readiness);
        Assert.Equal(["made admin"], plan.AdminOf.Select(channel => channel.Id));
        Assert.Equal(["made admin"], plan.Unchecked.Select(channel => channel.Id));
        Assert.Empty(plan.ToLeave);
    }

    /// <summary>What to do about a channel this user is the admin of comes first: it needs them, where checking needs a moment.</summary>
    [Fact]
    public void AnAdminChannelIsExplainedBeforeOneNotCheckedYet() {
        var plan = IdentityResetPlan.Of(Snapshot([
            Channel("mine", Rank.Admin, others: Member(Bob, Rank.Member)),
            Channel("unverified", Rank.Unspecified) with { LogHead = null, Members = [] },
        ]));

        Assert.Equal(ResetReadiness.AdminOfChannels, plan.Readiness);
        Assert.Equal(["mine"], plan.AdminOf.Select(channel => channel.Id));
        Assert.Equal(["unverified"], plan.Unchecked.Select(channel => channel.Id));
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
    /// explicit say-so (the server is gone for good, or doesn't know the login any more) lets it go ahead without that, and
    /// only once connecting has failed (or stopped): while it is still trying, the answer is to wait. The say-so is to losing
    /// the admin of their channels for good, which is said, with the channels they were the admin of when last connected.
    /// </summary>
    [Theory]
    [InlineData(ConnectionState.Stopped, false, ResetReadiness.Offline)]
    [InlineData(ConnectionState.Connecting, true, ResetReadiness.Offline)]
    [InlineData(ConnectionState.Reconnecting, true, ResetReadiness.Offline)]
    [InlineData(ConnectionState.Unregistered, false, ResetReadiness.NotLoggedIn)]
    [InlineData(ConnectionState.LoginNotRecognized, false, ResetReadiness.NotLoggedIn)]
    [InlineData(ConnectionState.Registering, false, ResetReadiness.NotLoggedIn)]
    public void WithoutALiveLoginNothingCanBeCheckedOrLeft(ConnectionState state, bool failed, ResetReadiness readiness) {
        // Even a channel the (stale) list says this user is the admin of: it can't be checked.
        var plan = IdentityResetPlan.Of(Snapshot([
            Channel("a", Rank.Admin, others: Member(Bob, Rank.Member)),
            Channel("b", Rank.Member, others: Member(Bob, Rank.Admin)) with { AdminPerServer = true },
            Channel("c", Rank.Member, others: Member(Bob, Rank.Admin)),
        ], state: state, loaded: false) with { ConnectionFailed = failed });

        Assert.Equal(readiness, plan.Readiness);
        Assert.True(plan.CanOverride);
        Assert.Contains(readiness == ResetReadiness.Offline ? "connect" : "login", plan.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("admin", plan.Explanation);
        Assert.Contains("for good", plan.Explanation);
        Assert.Equal(["a", "b"], plan.LastKnownAdminOf.Select(channel => channel.Id));
        Assert.Empty(plan.AdminOf);
        Assert.Empty(plan.ToLeave);
    }

    /// <summary>Connecting (or reconnecting after a connection that worked) with no attempt failed yet: wait, nothing to override.</summary>
    [Theory]
    [InlineData(ConnectionState.Connecting)]
    [InlineData(ConnectionState.Reconnecting)]
    public void WhileStillConnectingTheResetWaits(ConnectionState state) {
        var plan = IdentityResetPlan.Of(Snapshot([Channel("a", Rank.Admin, others: Member(Bob, Rank.Member))], state: state, loaded: false));

        Assert.Equal(ResetReadiness.Connecting, plan.Readiness);
        Assert.False(plan.CanOverride);
        Assert.NotEqual("", plan.Explanation);
    }

    /// <summary>
    /// Once the channels are left, the reset goes on to retire the old key only if nothing is left to do with it: no leave
    /// failed, and the plan worked out again still may go ahead, with nothing new to leave or decline. Otherwise it stops,
    /// saying why in plain words, while the old key can still sign.
    /// </summary>
    [Fact]
    public void AfterLeavingTheResetGoesOnOnlyIfNothingIsLeftToDo() {
        var done = IdentityResetPlan.Of(Snapshot([Channel("old", Rank.Unspecified, oldKey: true, others: Member(Bob, Rank.Member))]));
        Assert.Null(done.WhyStopAfterLeaving([]));

        var failed = done.WhyStopAfterLeaving([new ResetFailure("a", "Channel a", "It went wrong.")]);
        Assert.Contains("\"Channel a\"", failed);
        Assert.Contains("try again", failed);

        // Made the admin of a channel meanwhile: what to do about it is said.
        var admin = IdentityResetPlan.Of(Snapshot([Channel("a", Rank.Admin, others: Member(Bob, Rank.Member))]));
        Assert.Contains("Make admin", admin.WhyStopAfterLeaving([]));
        // Disconnected meanwhile.
        Assert.Contains("connect", IdentityResetPlan.Of(Snapshot([], state: ConnectionState.Stopped)).WhyStopAfterLeaving([]));
        // Joined a channel, or invited to one, meanwhile.
        Assert.Contains("try again", IdentityResetPlan.Of(Snapshot([Channel("a", Rank.Member, others: Member(Bob, Rank.Admin))])).WhyStopAfterLeaving([]));
        Assert.Contains("invited", IdentityResetPlan.Of(Snapshot([], [Invite("x")])).WhyStopAfterLeaving([]));
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
