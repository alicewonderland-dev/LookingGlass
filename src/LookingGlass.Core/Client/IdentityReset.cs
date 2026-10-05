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
    public static IdentityResetPlan Of(SessionSnapshot snapshot) {
        var readiness = snapshot.State switch {
            ConnectionState.Ready when snapshot.Me == null || !snapshot.ChannelsLoaded => ResetReadiness.Loading,
            ConnectionState.Ready => ResetReadiness.Ready,
            ConnectionState.Unregistered or ConnectionState.Registering or ConnectionState.LoginNotRecognized => ResetReadiness.NotLoggedIn,
            _ => ResetReadiness.Offline,
        };

        // Only a live, complete list says who is the admin of what (and only it can be acted on): without one, nothing is listed.
        var known = readiness is ResetReadiness.Ready ? snapshot.Channels : [];
        // The log has one admin per channel, and MyRank is under the current key: an old key's admin place isn't this key's to deal with.
        var adminOf = known.Where(channel => channel.MyRank == Rank.Admin).ToImmutableArray();
        var steps = known.Where(channel => channel.MyRank != Rank.Admin)
            .Select(channel => new ResetChannelStep(channel,
                channel.MyRank >= Rank.Member ? ResetChannelAction.Leave
                : channel.OldKeyMembership ? ResetChannelAction.KeepOldKey
                : ResetChannelAction.KeepUnverified))
            .ToImmutableArray();

        if (readiness == ResetReadiness.Ready && adminOf.Length > 0) {
            readiness = ResetReadiness.AdminOfChannels;
        }

        return new IdentityResetPlan(readiness, adminOf, steps, readiness is ResetReadiness.Ready or ResetReadiness.AdminOfChannels ? snapshot.Invites : []);
    }

    public IEnumerable<ChannelView> ToLeave => this.Channels.Where(step => step.Action == ResetChannelAction.Leave).Select(step => step.Channel);

    /// <summary>The channels that stay in the list after the reset, as places that belong to the old key, to remove with "Remove from my list".</summary>
    public IEnumerable<ResetChannelStep> Kept => this.Channels.Where(step => step.Action != ResetChannelAction.Leave);

    /// <summary>The reset may go ahead, but only if the user explicitly accepts that nothing is checked or left first.</summary>
    public bool CanOverride => this.Readiness is ResetReadiness.NotLoggedIn or ResetReadiness.Offline;

    /// <summary>Why the reset can't go ahead now, and what to do, in plain words; empty when it can.</summary>
    public string Explanation => this.Readiness switch {
        ResetReadiness.AdminOfChannels =>
            (this.AdminOf.Length == 1
                ? $"You're the admin of {Quote(this.AdminOf[0])}. "
                : $"You're the admin of {this.AdminOf.Length} channels: {string.Join(", ", this.AdminOf.Select(Quote))}. ") +
            "Your channels are left as part of the reset, but the admin can't simply leave: first, for each of these, open it and either " +
            "make another member admin (their \"...\" menu, \"Make admin (hand over)\"), or disband it (the channel's \"...\" menu), or, " +
            "if you're its only member, leave it. Resetting is unavailable until then, so no channel is lost without you choosing it.",
        ResetReadiness.Loading => "Your channels are still loading. Wait a moment: LookingGlass checks them before a reset.",
        ResetReadiness.NotLoggedIn =>
            "The server doesn't accept your login, so LookingGlass can't check which channels you're the admin of, or leave your channels " +
            "first. Channels still held by your old key stay in your list, to remove afterwards with \"Remove from my list\".",
        ResetReadiness.Offline =>
            "You're not connected, so LookingGlass can't check which channels you're the admin of, or leave your channels first. " +
            "Connect, then reset. Only if this server is gone for good, reset anyway without that.",
        _ => "",
    };

    private static string Quote(ChannelView channel) => $"\"{channel.DisplayName}\"";
}

/// <param name="What">The channel's (or invite's) name, as shown.</param>
public sealed record ResetFailure(string ChannelId, string What, string Error);

/// <summary>What <see cref="ClientSession.LeaveChannelsForResetAsync"/> did.</summary>
public sealed record IdentityResetCleanup(
    ImmutableArray<ChannelView> Left,
    ImmutableArray<InviteView> Declined,
    ImmutableArray<ResetChannelStep> Kept,
    ImmutableArray<ResetFailure> Failed);
