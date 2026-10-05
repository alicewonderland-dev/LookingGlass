namespace LookingGlass.Core.Client;

/// <summary>
/// What the user is told, in plain words, about a channel whose place belongs to an identity key they no longer have, and
/// the technical messages (from the membership rules, or a server) that would otherwise reach them about it.
/// </summary>
public static class PlainMessages {
    /// <summary>Shown on a channel whose place belongs to the old key (see <see cref="ChannelView.OldKeyMembership"/>).</summary>
    public const string OldKeyChannel = "";

    /// <summary>Why Leave can't work there.</summary>
    public const string CantLeaveOldKeyChannel = "";

    /// <summary>A message to show, with technical ones about old keys replaced by what they mean for the user.</summary>
    public static string Of(string message) => message;
}
