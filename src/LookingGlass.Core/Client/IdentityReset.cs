using System.Collections.Immutable;
using LookingGlass.Protocol;

namespace LookingGlass.Core.Client;

/// <summary>Whether "Reset my identity" may go ahead now (see <see cref="IdentityResetPlan"/>).</summary>
public enum ResetReadiness {
    /// <summary>Connected, logged in, the channel list loaded, and this user is the admin of no channel: it may go ahead.</summary>
    Ready,

    /// <summary>
    /// This user is the admin of a channel (<see cref="IdentityResetPlan.AdminOf"/>). Blocked until they hand admin on,
    /// disband it, or leave it (as its only member) themselves, so losing a channel is always their own choice.
    /// </summary>
    AdminOfChannels,

    /// <summary>Logged in, but the channel list isn't loaded yet: wait.</summary>
    Loading,

    /// <summary>
    /// Connected, but the server has no working login for this key (it doesn't know it, or it was retired): nothing can be
    /// checked or left. Only with the user's explicit say-so (see <see cref="IdentityResetPlan.CanOverride"/>).
    /// </summary>
    NotLoggedIn,

    /// <summary>
    /// Not connected: nothing can be checked or left. Connect first; only if the server is gone for good, with the user's
    /// explicit say-so (see <see cref="IdentityResetPlan.CanOverride"/>).
    /// </summary>
    Offline,
}

/// <summary>What "Reset my identity" does with a channel, before the old key is retired.</summary>
public enum ResetChannelAction {
    /// <summary>Leave it, signed with the old key while it still exists.</summary>
    Leave,

    /// <summary>Kept: the place already belongs to an older key (an earlier reset), which can't sign a leave either.</summary>
    KeepOldKey,

    /// <summary>Kept: this client hasn't verified the channel's log (yet), so it can't sign anything for it.</summary>
    KeepUnverified,
}

public sealed record ResetChannelStep(ChannelView Channel, ResetChannelAction Action);

/// <summary>
/// What "Reset my identity" does first, while the old key still exists, and whether it may go ahead at all. Never while
/// this user is the admin of a channel: they hand admin to another member, disband it, or leave it (as its only member)
/// themselves first. Otherwise every channel they're a member of is left with a leave signed by the old key, and every
/// invite declined. Worked out from a snapshot, so the reset dialog can show it (again with every snapshot) and
/// <see cref="ClientSession.LeaveChannelsForResetAsync"/> can carry it out.
/// </summary>
/// <param name="AdminOf">The channels this user is the admin of, under their current key: each blocks the reset.</param>
/// <param name="Channels">What is done with every other channel listed.</param>
public sealed record IdentityResetPlan(
    ResetReadiness Readiness,
    ImmutableArray<ChannelView> AdminOf,
    ImmutableArray<ResetChannelStep> Channels,
    ImmutableArray<InviteView> Declines) {
    public static IdentityResetPlan Of(SessionSnapshot snapshot) => throw new NotImplementedException();

    public IEnumerable<ChannelView> ToLeave => this.Channels.Where(step => step.Action == ResetChannelAction.Leave).Select(step => step.Channel);

    /// <summary>The channels that stay in the list after the reset, as places that belong to the old key, to remove with "Remove from my list".</summary>
    public IEnumerable<ResetChannelStep> Kept => this.Channels.Where(step => step.Action != ResetChannelAction.Leave);

    /// <summary>The reset may go ahead, but only if the user explicitly accepts that nothing is checked or left first.</summary>
    public bool CanOverride => this.Readiness is ResetReadiness.NotLoggedIn or ResetReadiness.Offline;

    /// <summary>Why the reset can't go ahead now, and what to do, in plain words; empty when it can.</summary>
    public string Explanation => "";
}

/// <param name="What">The channel's (or invite's) name, as shown.</param>
public sealed record ResetFailure(string ChannelId, string What, string Error);

/// <summary>What <see cref="ClientSession.LeaveChannelsForResetAsync"/> did.</summary>
public sealed record IdentityResetCleanup(
    ImmutableArray<ChannelView> Left,
    ImmutableArray<InviteView> Declined,
    ImmutableArray<ResetChannelStep> Kept,
    ImmutableArray<ResetFailure> Failed);
