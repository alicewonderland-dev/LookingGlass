using System.Collections.Immutable;
using LookingGlass.Protocol;

namespace LookingGlass.Core.Client;

/// <summary>Whether "Reset my identity" may go ahead now (see <see cref="IdentityResetPlan"/>).</summary>
public enum ResetReadiness {
    /// <summary>
    /// Connected, logged in, the channel list loaded and every channel in it checked, and this user is the admin of no
    /// channel: it may go ahead.
    /// </summary>
    Ready,

    /// <summary>
    /// This user is the admin of a channel (<see cref="IdentityResetPlan.AdminOf"/>). Blocked until they hand admin on,
    /// disband it, or leave it (as its only member) themselves, so losing a channel is always their own choice.
    /// </summary>
    AdminOfChannels,

    /// <summary>
    /// Logged in, but some channels' membership logs aren't verified (yet), or are behind what the server said, so whether
    /// this user is their admin isn't known (<see cref="IdentityResetPlan.Unchecked"/>): wait, or refresh.
    /// </summary>
    Checking,

    /// <summary>Logged in, but the channel list isn't loaded yet: wait.</summary>
    Loading,

    /// <summary>Connecting (or reconnecting after a connection that worked), and no attempt has failed yet: wait.</summary>
    Connecting,

    /// <summary>
    /// Connected, but the server has no working login for this key (it doesn't know it, or it was retired): nothing can be
    /// checked or left. Only with the user's explicit say-so (see <see cref="IdentityResetPlan.CanOverride"/>).
    /// </summary>
    NotLoggedIn,

    /// <summary>
    /// Not connected (stopped, or connecting failed): nothing can be checked or left. Connect first; only if the server is
    /// gone for good, with the user's explicit say-so (see <see cref="IdentityResetPlan.CanOverride"/>).
    /// </summary>
    Offline,
}

/// <summary>What "Reset my identity" does with a channel, before the old key is retired.</summary>
public enum ResetChannelAction {
    /// <summary>Leave it, signed with the old key while it still exists.</summary>
    Leave,

    /// <summary>Kept: the place already belongs to an older key (an earlier reset), which can't sign a leave either.</summary>
    KeepOldKey,

    /// <summary>
    /// Not left: this client hasn't verified the channel's log (yet), so it can't sign anything for it. Never in a plan that
    /// may go ahead: such a channel blocks the reset (see <see cref="ResetReadiness.Checking"/>).
    /// </summary>
    KeepUnverified,
}

public sealed record ResetChannelStep(ChannelView Channel, ResetChannelAction Action);

/// <summary>
/// What "Reset my identity" does first, while the old key still exists, and whether it may go ahead at all. Never while
/// this user is the admin of a channel: they hand admin to another member, disband it, or leave it (as its only member)
/// themselves first. Nor while any channel isn't checked, as it may be one they're the admin of. Otherwise every channel
/// they're a member of is left with a leave signed by the old key, and every invite declined. Worked out from a snapshot,
/// so the reset dialog can show it (again with every snapshot) and <see cref="ClientSession.LeaveChannelsForResetAsync"/>
/// can carry it out.
/// </summary>
/// <param name="AdminOf">
/// The channels this user is the admin of, under their current key, as their verified log says or (until it catches up)
/// the server: each blocks the reset.
/// </param>
/// <param name="Channels">What is done with every other channel listed.</param>
/// <param name="Unchecked">
/// The channels whose verified log doesn't say whether this user is their admin: not verified (yet), or behind the server
/// saying they are. Each blocks the reset until it is checked.
/// </param>
/// <param name="LastKnownAdminOf">
/// Without a live login (<see cref="CanOverride"/>): the channels this user was the admin of when last connected, which
/// resetting anyway leaves without an admin for good.
/// </param>
public sealed record IdentityResetPlan(
    ResetReadiness Readiness,
    ImmutableArray<ChannelView> AdminOf,
    ImmutableArray<ResetChannelStep> Channels,
    ImmutableArray<InviteView> Declines,
    ImmutableArray<ChannelView> Unchecked,
    ImmutableArray<ChannelView> LastKnownAdminOf) {
    public static IdentityResetPlan Of(SessionSnapshot snapshot) {
        var readiness = snapshot.State switch {
            ConnectionState.Ready when snapshot.Me == null || !snapshot.ChannelsLoaded => ResetReadiness.Loading,
            ConnectionState.Ready => ResetReadiness.Ready,
            ConnectionState.Unregistered or ConnectionState.Registering or ConnectionState.LoginNotRecognized => ResetReadiness.NotLoggedIn,
            // Only once an attempt has failed: before that, the answer is to wait for it.
            ConnectionState.Connecting or ConnectionState.Reconnecting when !snapshot.ConnectionFailed => ResetReadiness.Connecting,
            _ => ResetReadiness.Offline,
        };

        // Only a live, complete list says who is the admin of what (and only it can be acted on): without one, nothing is listed.
        var known = readiness is ResetReadiness.Ready ? snapshot.Channels : [];
        var adminOf = known.Where(IsAdmin).ToImmutableArray();
        var steps = known.Where(channel => !IsAdmin(channel))
            .Select(channel => new ResetChannelStep(channel,
                channel.MyRank >= Rank.Member ? ResetChannelAction.Leave
                : channel.OldKeyMembership ? ResetChannelAction.KeepOldKey
                : ResetChannelAction.KeepUnverified))
            .ToImmutableArray();
        // An old key's place is known (the log is verified, and has another key there); any other the log doesn't place
        // this user in as a member, or that the server says is theirs to run beyond what the log says, isn't checked yet.
        var notChecked = known
            .Where(channel => !channel.OldKeyMembership && channel.MyRank != Rank.Admin && (channel.MyRank < Rank.Member || channel.AdminPerServer))
            .ToImmutableArray();

        if (readiness == ResetReadiness.Ready) {
            // What the user must do comes before what needs a moment. (An admin place only the server has is unchecked.)
            readiness = adminOf.Any(channel => channel.MyRank == Rank.Admin) ? ResetReadiness.AdminOfChannels
                : notChecked.Length > 0 ? ResetReadiness.Checking
                : ResetReadiness.Ready;
        }

        var overridable = readiness is ResetReadiness.NotLoggedIn or ResetReadiness.Offline;
        return new IdentityResetPlan(readiness, adminOf, steps,
            readiness is ResetReadiness.Ready or ResetReadiness.AdminOfChannels or ResetReadiness.Checking ? snapshot.Invites : [],
            notChecked,
            overridable ? snapshot.Channels.Where(IsAdmin).ToImmutableArray() : []);
    }

    /// <summary>
    /// The log has one admin per channel, and MyRank is under the current key: an old key's admin place isn't this key's to
    /// deal with. The server saying so counts while the verified log is behind it.
    /// </summary>
    private static bool IsAdmin(ChannelView channel) => channel.MyRank == Rank.Admin || (channel.AdminPerServer && !channel.OldKeyMembership);

    public IEnumerable<ChannelView> ToLeave => this.Channels.Where(step => step.Action == ResetChannelAction.Leave).Select(step => step.Channel);

    /// <summary>The channels that stay in the list after the reset, as places that belong to the old key, to remove with "Remove from my list".</summary>
    public IEnumerable<ResetChannelStep> Kept => this.Channels.Where(step => step.Action != ResetChannelAction.Leave);

    /// <summary>
    /// The reset may go ahead, but only if the user explicitly accepts that nothing is checked or left first: without a live
    /// login, once connecting has failed (or stopped), or with a login the server doesn't accept.
    /// </summary>
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
        ResetReadiness.Checking =>
            (this.Unchecked.Length == 1
                ? $"LookingGlass hasn't finished checking {Quote(this.Unchecked[0])}, "
                : $"LookingGlass hasn't finished checking {this.Unchecked.Length} of your channels ({string.Join(", ", this.Unchecked.Select(Quote))}), ") +
            "so it doesn't know yet whether you're the admin there. Wait a moment, or refresh. Resetting is unavailable until then, " +
            "so no channel is lost without you choosing it. If one stays unchecked, open it to see why.",
        ResetReadiness.Loading => "Your channels are still loading. Wait a moment: LookingGlass checks them before a reset.",
        ResetReadiness.Connecting => "Connecting to the server. Wait a moment: LookingGlass checks your channels before a reset.",
        ResetReadiness.NotLoggedIn =>
            "The server doesn't accept your login, so LookingGlass can't check which channels you're the admin of, or leave your channels " +
            "first. " + LosingAdmin +
            " Channels still held by your old key stay in your list, to remove afterwards with \"Remove from my list\".",
        ResetReadiness.Offline =>
            "You're not connected, so LookingGlass can't check which channels you're the admin of, or leave your channels first. " +
            "Connect, then reset. Only if this server is gone for good, reset anyway without that. " + LosingAdmin,
        _ => "",
    };

    /// <summary>
    /// With this plan worked out again once the channels were left: why the reset must stop there, before the old key is
    /// retired, or null if it may go on. Once the key is retired, nothing it still holds can be left any more, so a leave or
    /// decline that failed, or anything that changed meanwhile (a channel joined, an invite or admin role received, the
    /// connection lost), is the user's to try again.
    /// </summary>
    internal string? WhyStopAfterLeaving(IReadOnlyCollection<ResetFailure> failed) {
        const string NothingReset = "Nothing was reset, so your old key still works and you can try again.";
        if (failed.Count > 0) {
            return $"Couldn't leave (or decline) {string.Join(", ", failed.Select(failure => $"\"{failure.What}\""))}. {NothingReset} " +
                   "What was left stays left. If one still can't be left, open it to see why.";
        }

        if (this.Readiness != ResetReadiness.Ready) {
            return $"Something changed while your channels were being left. {NothingReset} {this.Explanation}";
        }

        if (this.ToLeave.Any() || !this.Declines.IsEmpty) {
            return $"While your channels were being left, you joined another channel or were invited to one. {NothingReset} " +
                   "Resetting again leaves that too.";
        }

        return null;
    }

    // What resetting without a live login costs, said wherever it is offered.
    private const string LosingAdmin =
        "Resetting anyway leaves every channel you're the admin of without an admin, for good: nobody can rename it, change ranks or disband it after that.";

    private static string Quote(ChannelView channel) => $"\"{channel.DisplayName}\"";
}

/// <param name="What">The channel's (or invite's) name, as shown.</param>
public sealed record ResetFailure(string ChannelId, string What, string Error);

/// <summary>What <see cref="ClientSession.LeaveChannelsForResetAsync"/> did.</summary>
/// <param name="StopReason">
/// Why the reset must stop here, before the old key is retired, in plain words: a leave or decline failed, or something
/// changed meanwhile. The old key still works then, so the user can try again. Null if it may go on.
/// </param>
public sealed record IdentityResetCleanup(
    ImmutableArray<ChannelView> Left,
    ImmutableArray<InviteView> Declined,
    ImmutableArray<ResetChannelStep> Kept,
    ImmutableArray<ResetFailure> Failed,
    string? StopReason = null) {
    /// <summary>The reset may go on to retire the old key: everything was left and declined, and nothing changed meanwhile.</summary>
    public bool MayRetire => this.StopReason == null;
}
