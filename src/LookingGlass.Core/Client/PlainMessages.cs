namespace LookingGlass.Core.Client;

/// <summary>
/// What the user is told, in plain words, about identity keys changing: someone re-verifying their character with a new key,
/// their own channels coming back after they did, and a channel whose place belongs to an identity key they no longer have
/// (from before registering again moved channels to the new key), with the technical messages (from the membership rules,
/// or a server) that would otherwise reach them about it.
/// </summary>
public static class PlainMessages {
    /// <summary>After someone's name, in their channels: a key recovered entry moved their place to a new key.</summary>
    public const string ReVerified = "re-verified their character and has a new key.";

    /// <summary>
    /// The name a channel gets when the user, back with new keys, is its only member: nobody can tell them its name (it was
    /// encrypted under keys their old identity held). The admin can rename it.
    /// </summary>
    public const string RestoredChannelName = "Restored channel";

    /// <summary>Told after registering, when the account's places moved to the new key.</summary>
    public static string PlacesRestored(uint count) =>
        $"Welcome back: your channels and invites here ({count}) were restored with your new key, ranks and all, because you re-verified your " +
        "character through the Lodestone. Their members are told. Each channel works again as soon as a member who is online shares its new " +
        "key with you.";

    /// <summary>Shown on a channel whose place belongs to the old key (see <see cref="ChannelView.OldKeyMembership"/>).</summary>
    public const string OldKeyChannel =
        "Your place in this channel belongs to an identity key you no longer have, from before registering again brought your channels along " +
        "to the new key. Your current key isn't a member, so you can't read, send or leave here. Use \"Remove from my list\" in the channel's " +
        "menu to take it off your list. To come back, a moderator can remove your old key and invite you again; or reset your identity " +
        "(Settings) and register again, which brings every channel you're still listed in along to your new key, this one too.";

    /// <summary>Why Leave can't work there.</summary>
    public const string CantLeaveOldKeyChannel =
        "You can't leave this channel: your place in it belongs to an identity key you no longer have, and only that key could sign leaving. " +
        "Use \"Remove from my list\" in the channel's menu instead.";

    /// <summary>Why nothing else that changes the members can work there either.</summary>
    public const string OldKeyCantChangeMembers =
        "Your place in this channel belongs to an identity key you no longer have, so your current key can't change anything here. " +
        "Use \"Remove from my list\" in the channel's menu to take it off your list.";

    // What the membership rules say when an entry isn't signed by the key the log knows its author by, and what a server says
    // when someone acts with keys the log doesn't know them by.
    private static readonly string[] OldKeyReasons = [
        "It isn't signed with the key the log knows its author by.",
        "Your place in this channel belongs to an identity key your account no longer has. A moderator must remove you and invite you again.",
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
