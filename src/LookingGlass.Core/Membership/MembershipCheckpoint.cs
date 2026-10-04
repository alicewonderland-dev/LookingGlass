using LookingGlass.Protocol;

namespace LookingGlass.Core.Membership;

/// <summary>
/// A channel's verified membership at one log position, in a form that serialises to JSON.
/// Clients persist it, so after a restart they carry on from where they had verified up to
/// (and notice a server that shows them an older log); the server rebuilds one from its tables.
/// </summary>
public sealed class MembershipCheckpoint {
    public string ChannelId { get; set; } = "";
    public ulong Seq { get; set; }
    public byte[] Hash { get; set; } = [];
    public ulong MembersChangedAt { get; set; }

    /// <summary>See <see cref="IChannelMembership.MembersLeftAt"/>. Null in checkpoints saved before it was kept.</summary>
    public ulong? MembersLeftAt { get; set; }

    /// <summary>Hashes of the entries from <see cref="RecentFrom"/> to <see cref="Seq"/>, oldest first.</summary>
    public List<byte[]> RecentHashes { get; set; } = [];

    public ulong RecentFrom { get; set; }

    public List<CheckpointMember> Members { get; set; } = [];
    public List<CheckpointInvitee> Invitees { get; set; } = [];
}

public sealed class CheckpointMember {
    public long UserId { get; set; }
    public byte[] SigningPublicKey { get; set; } = [];
    public byte[] AgreementPublicKey { get; set; } = [];
    public Rank Rank { get; set; }
}

public sealed class CheckpointInvitee {
    public long UserId { get; set; }
    public byte[] SigningPublicKey { get; set; } = [];
    public byte[] AgreementPublicKey { get; set; } = [];
    public ulong InviteSeq { get; set; }
    public byte[] InviteHash { get; set; } = [];
    public long InviterId { get; set; }
    public byte[] InviterSigningPublicKey { get; set; } = [];
    public byte[] InviterAgreementPublicKey { get; set; } = [];
}
