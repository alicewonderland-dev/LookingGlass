namespace LookingGlass.Core.Client;

/// <summary>
/// What the user is told, in plain words, about a channel whose place belongs to an identity key they no longer have, and
/// the technical messages (from the membership rules, or a server) that would otherwise reach them about it.
/// </summary>
public static class PlainMessages {
    /// <summary>Shown on a channel whose place belongs to the old key (see <see cref="ChannelView.OldKeyMembership"/>).</summary>
    public const string OldKeyChannel =
        "You're in this channel with the identity key you had before you reset your identity (or registered again with new keys). " +
        "Your current key isn't a member, so you can't read, send or leave here. Use \"Remove from my list\" in the channel's menu to " +
        "take it off your list. To come back, a moderator must remove your old key and invite you again.";

    /// <summary>Why Leave can't work there.</summary>
    public const string CantLeaveOldKeyChannel =
        "You can't leave this channel: your place in it belongs to the identity key you had before you reset your identity (or registered " +
        "again), and only that key could sign leaving. Use \"Remove from my list\" in the channel's menu instead.";

    /// <summary>Why nothing else that changes the members can work there either.</summary>
    public const string OldKeyCantChangeMembers =
        "Your place in this channel belongs to the identity key you had before you reset your identity (or registered again), so your " +
        "current key can't change anything here. Use \"Remove from my list\" in the channel's menu to take it off your list.";

    // What the membership rules say when an entry isn't signed by the key the log knows its author by, and what a server says
    // when someone acts with keys the log doesn't know them by.
    private static readonly string[] OldKeyReasons = [
        "It isn't signed with the key the log knows its author by.",
        "Your place in this channel belongs to the identity key you had before you registered again. A moderator must remove you and invite you again.",
    ];

    /// <summary>A message to show, with technical ones about old keys replaced by what they mean for the user.</summary>
    public static string Of(string message) {
        foreach (var reason in OldKeyReasons) {
            var at = message.IndexOf(reason, StringComparison.Ordinal);
            if (at >= 0) {
                // What was being done ("Leaving ... failed: ") stays; whatever the server or the rules added after it goes.
                var before = message[..at];
                if (before.EndsWith("Invalid membership log entry: ", StringComparison.Ordinal)) {
                    before = before[..^"Invalid membership log entry: ".Length];
                }

                return before + OldKeyCantChangeMembers;
            }
        }

        return message;
    }
}
