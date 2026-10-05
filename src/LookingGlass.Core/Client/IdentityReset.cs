using System.Collections.Immutable;

namespace LookingGlass.Core.Client;

/// <summary>What "Reset my identity" does with a channel before the old key is retired.</summary>
public enum ResetChannelAction {
    /// <summary>Leave it, signed with the old key while it still exists.</summary>
    Leave,

    /// <summary>
    /// Kept: this user is its admin and others are still in it, and the log doesn't let the admin leave then. It stays in
    /// the list without the user being a member, to remove afterwards with "Remove from my list".
    /// </summary>
    KeepLastAdmin,

    /// <summary>Kept: the place already belongs to an older key (an earlier reset), which can't sign a leave either.</summary>
    KeepOldKey,

    /// <summary>Kept: this client hasn't verified the channel's log (yet), so it can't sign anything for it.</summary>
    KeepUnverified,
}

public sealed record ResetChannelStep(ChannelView Channel, ResetChannelAction Action);

/// <summary>
/// What "Reset my identity" does first, while the old key still exists: which channels it leaves (with a leave entry
/// signed by that key) and which it can't, and which invites it declines. Worked out from a snapshot, so the reset dialog
/// can show it and <see cref="ClientSession.LeaveChannelsForResetAsync"/> can carry it out.
/// </summary>
/// <param name="Connected">Connected, logged in and with the complete channel list: only then can anything be left first.</param>
public sealed record IdentityResetPlan(bool Connected, ImmutableArray<ResetChannelStep> Channels, ImmutableArray<InviteView> Declines) {
    public static IdentityResetPlan Of(SessionSnapshot snapshot) => throw new NotImplementedException();

    public IEnumerable<ChannelView> ToLeave => this.Channels.Where(step => step.Action == ResetChannelAction.Leave).Select(step => step.Channel);

    /// <summary>The channels that stay in the list, as ones whose place belongs to the old key.</summary>
    public IEnumerable<ResetChannelStep> Kept => this.Channels.Where(step => step.Action != ResetChannelAction.Leave);
}

/// <param name="What">The channel's (or invite's) name, as shown.</param>
public sealed record ResetFailure(string ChannelId, string What, string Error);

/// <summary>What <see cref="ClientSession.LeaveChannelsForResetAsync"/> did.</summary>
public sealed record IdentityResetCleanup(
    ImmutableArray<ChannelView> Left,
    ImmutableArray<InviteView> Declined,
    ImmutableArray<ResetChannelStep> Kept,
    ImmutableArray<ResetFailure> Failed);
