using WonderlandChat.Protocol;

namespace WonderlandChat.Server.Realtime;

public enum ChannelAction {
    Send,
    Rekey,
    FetchKeys,
    Leave,
    Invite,
    Kick,
    SetRank,
    Rename,
    Disband,

    /// <summary>Read the channel's membership log. Invitees may, to check an invite before answering it.</summary>
    FetchLog,
}

/// <summary>
/// The single authorization table. Every channel request is checked here
/// before anything else happens. Ranks order: Invited &lt; Member &lt; Moderator &lt; Admin.
/// </summary>
public static class Policy {
    public static bool Can(Rank? actor, ChannelAction action) {
        if (actor is not { } rank) {
            return false;
        }

        return action switch {
            ChannelAction.Send or ChannelAction.Rekey or ChannelAction.FetchKeys or ChannelAction.Leave => rank >= Rank.Member,
            ChannelAction.Invite or ChannelAction.Kick => rank >= Rank.Moderator,
            ChannelAction.FetchLog => rank >= Rank.Invited,
            ChannelAction.SetRank or ChannelAction.Rename or ChannelAction.Disband => rank == Rank.Admin,
            _ => false,
        };
    }

    /// <summary>Moderators and admins can remove members (or cancel invites) strictly below their own rank.</summary>
    public static bool CanKick(Rank? actor, Rank target) {
        return Can(actor, ChannelAction.Kick) && target < actor;
    }

    /// <summary>Ranks an admin may assign. Assigning Admin transfers the role.</summary>
    public static bool IsAssignableRank(Rank rank) => rank is Rank.Member or Rank.Moderator or Rank.Admin;
}
