using LookingGlass.Core.Client;
using LookingGlass.Protocol;

namespace LookingGlass.Tests;

/// <summary>What the channel sidebar flags: the warnings the channel details show, gathered per channel.</summary>
public sealed class ChannelAttentionTests {
    private static readonly User Me = new() { UserId = 1, Name = "Alice Liddell", WorldName = "Twintania" };
    private static readonly User Bob = new() { UserId = 2, Name = "Bob Hatter", WorldName = "Lich" };
    private static readonly User Carol = new() { UserId = 3, Name = "Carol Queen", WorldName = "Odin" };

    private static ChannelView Channel(bool hasKey = true, bool rekeyPending = false, string? warning = null, params MemberView[] others) {
        var members = new List<MemberView> { new(Me, Rank.Admin, "11111 11111 11111 11111 11111", false, true) };
        members.AddRange(others);
        return new ChannelView("aaa", "Tea party", 3, 3, hasKey, rekeyPending, Rank.Admin, [.. members], null, warning);
    }

    [Fact]
    public void AHealthyChannelNeedsNothing() {
        // Members whose fingerprints nobody compared yet are normal (trusted on first use): not flagged.
        var channel = Channel(others: new MemberView(Bob, Rank.Member, "22222 22222 22222 22222 22222", false));

        var attention = ChannelAttention.Of(channel);

        Assert.Equal(AttentionLevel.None, attention.Level);
        Assert.Empty(attention.Reasons);
    }

    [Fact]
    public void MembershipWarningsAreWarnings() {
        var attention = ChannelAttention.Of(Channel(warning: "The server showed this channel's members differently to different members."));

        Assert.Equal(AttentionLevel.Warning, attention.Level);
        Assert.Equal(["The server showed this channel's members differently to different members."], attention.Reasons);
    }

    [Fact]
    public void ChangedKeysAndMembersWhoRegisteredAgainAreWarnings() {
        var channel = Channel(others: [
            new MemberView(Bob, Rank.Member, "22222 22222 22222 22222 22222", KeyChanged: true),
            new MemberView(Carol, Rank.Moderator, "33333 33333 33333 33333 33333", KeyChanged: true, KeyReplaced: true, NewFingerprint: "44444 44444 44444 44444 44444"),
        ]);

        var attention = ChannelAttention.Of(channel);

        Assert.Equal(AttentionLevel.Warning, attention.Level);
        Assert.Equal([
            "Key changed: Bob Hatter@Lich. Compare fingerprints.",
            "Registered again: Carol Queen@Odin. Remove them and invite them again to let their new key in.",
        ], attention.Reasons);
    }

    [Fact]
    public void SomeoneWhoRegisteredAgainStaysFlaggedAfterTheirNewKeyIsVerified() {
        var channel = Channel(others: new MemberView(Carol, Rank.Member, "33333 33333 33333 33333 33333", KeyChanged: false, FingerprintCompared: true, KeyReplaced: true,
            NewFingerprint: "44444 44444 44444 44444 44444"));

        Assert.Equal(["Registered again: Carol Queen@Odin. Remove them and invite them again to let their new key in."], ChannelAttention.Of(channel).Reasons);
    }

    [Fact]
    public void NamesAreListedTogether() {
        var channel = Channel(others: [
            new MemberView(Bob, Rank.Member, "22222 22222 22222 22222 22222", KeyChanged: true),
            new MemberView(Carol, Rank.Member, "33333 33333 33333 33333 33333", KeyChanged: true),
        ]);

        Assert.Equal(["Key changed: Bob Hatter@Lich, Carol Queen@Odin. Compare fingerprints."], ChannelAttention.Of(channel).Reasons);
    }

    [Fact]
    public void AMissingOrPendingKeyIsPending() {
        var waiting = ChannelAttention.Of(Channel(hasKey: false));
        Assert.Equal(AttentionLevel.Pending, waiting.Level);
        Assert.Equal(["Waiting for the channel key."], waiting.Reasons);

        var rekey = ChannelAttention.Of(Channel(rekeyPending: true));
        Assert.Equal(AttentionLevel.Pending, rekey.Level);
        Assert.Equal(["A new channel key is pending."], rekey.Reasons);

        // Rekey pending is the more specific of the two.
        Assert.Equal(["A new channel key is pending."], ChannelAttention.Of(Channel(hasKey: false, rekeyPending: true)).Reasons);
    }

    [Fact]
    public void WarningsOutrankPendingKeysAndAllReasonsAreKept() {
        var channel = Channel(hasKey: false, warning: "Something is wrong.", others: new MemberView(Bob, Rank.Member, "22222 22222 22222 22222 22222", KeyChanged: true));

        var attention = ChannelAttention.Of(channel);

        Assert.Equal(AttentionLevel.Warning, attention.Level);
        Assert.Equal(["Something is wrong.", "Key changed: Bob Hatter@Lich. Compare fingerprints.", "Waiting for the channel key."], attention.Reasons);
    }
}
