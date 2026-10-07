using LookingGlass.Protocol;
using LookingGlass.Server.Realtime;

namespace LookingGlass.Tests;

/// <summary>Walks every rank × action pair, as the design doc requires.</summary>
public class PolicyTests {
    public static TheoryData<Rank?, ChannelAction, bool> Matrix() {
        var data = new TheoryData<Rank?, ChannelAction, bool>();
        var expected = new Dictionary<ChannelAction, Rank?> {
            [ChannelAction.Send] = Rank.Member,
            [ChannelAction.Rekey] = Rank.Member,
            [ChannelAction.FetchKeys] = Rank.Member,
            [ChannelAction.Leave] = Rank.Member,
            [ChannelAction.Invite] = Rank.Moderator,
            [ChannelAction.Kick] = Rank.Moderator,
            [ChannelAction.SetRank] = Rank.Admin,
            [ChannelAction.Rename] = Rank.Admin,
            [ChannelAction.Disband] = Rank.Admin,
            // Invitees read the log to check an invite before answering it.
            [ChannelAction.FetchLog] = Rank.Invited,
            // Stored messages (catch-up) are for members: an invitee holds no key for them.
            [ChannelAction.FetchMessages] = Rank.Member,
        };

        Rank?[] ranks = [null, Rank.Unspecified, Rank.Invited, Rank.Member, Rank.Moderator, Rank.Admin];
        foreach (var action in Enum.GetValues<ChannelAction>()) {
            foreach (var rank in ranks) {
                data.Add(rank, action, rank != null && rank >= expected[action]);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void RankActionMatrix(Rank? rank, ChannelAction action, bool allowed) {
        Assert.Equal(allowed, Policy.Can(rank, action));
    }

    [Theory]
    [InlineData(Rank.Moderator, Rank.Member, true)]
    [InlineData(Rank.Moderator, Rank.Invited, true)]
    [InlineData(Rank.Moderator, Rank.Moderator, false)]
    [InlineData(Rank.Moderator, Rank.Admin, false)]
    [InlineData(Rank.Admin, Rank.Moderator, true)]
    [InlineData(Rank.Member, Rank.Invited, false)]
    public void KickRequiresHigherRank(Rank actor, Rank target, bool allowed) {
        Assert.Equal(allowed, Policy.CanKick(actor, target));
    }
}
