namespace LookingGlass.Core.Client;

/// <summary>
/// The rules for local channel nicknames, used by /lgc &lt;nickname&gt; &lt;message&gt;, as a map
/// from channel ID to nickname. Nicknames belong to one character and are never sent to the
/// server. Like command slots (<see cref="CommandSlots"/>), a nickname stays with its channel
/// until that channel is gone, across restarts.
/// </summary>
public static class ChannelNicknames {
    public const int MaxLength = 16;

    /// <summary>The nickname no channel may have: its tag would be [Local], local chat's (see <see cref="LocalChat.Tag"/>).</summary>
    public const string Reserved = "Local";

    /// <summary>
    /// Checks the form of a nickname: 1 to 16 ASCII letters, digits, - or _, not only digits, and not "Local" (in any case,
    /// with or without spaces around it). A tag is only made from a nickname that passes (see <see cref="ChannelTag.For"/>),
    /// so one saved as "Local" before it was reserved shows as the channel's number.
    /// </summary>
    /// <returns>Why it isn't valid, or null if it is.</returns>
    public static string? Validate(string nickname) {
        if (nickname.Length == 0) {
            return "A nickname needs at least one character.";
        }

        if (string.Equals(nickname.Trim(), Reserved, StringComparison.OrdinalIgnoreCase)) {
            return $"{Reserved} is used by local chat; choose another nickname.";
        }

        if (nickname.Length > MaxLength) {
            return $"A nickname can be at most {MaxLength} characters.";
        }

        // ASCII only: no look-alike or full-width letters, which would be hard to tell apart when typing.
        if (!nickname.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) {
            return "A nickname can only use letters, digits, - and _.";
        }

        // "/lgc 3 hi" must not be mistaken for "/lgc3 hi" (or the other way round).
        if (nickname.All(char.IsAsciiDigit)) {
            return $"A nickname can't be only digits: {CommandSlots.Prefix} {nickname} would look like {CommandSlots.Prefix}{nickname}.";
        }

        return null;
    }

    /// <summary>Checks that a channel could take a nickname: valid, and not another channel's (ignoring case).</summary>
    /// <returns>Why it can't, or null if it can.</returns>
    public static string? Check(IReadOnlyDictionary<string, string> nicknames, string channelId, string nickname) {
        if (Validate(nickname) is { } error) {
            return error;
        }

        foreach (var (otherId, other) in nicknames) {
            if (otherId != channelId && string.Equals(other, nickname, StringComparison.OrdinalIgnoreCase)) {
                return $"Another channel already has the nickname '{other}'.";
            }
        }

        return null;
    }

    /// <summary>
    /// Gives a channel a nickname (surrounding spaces ignored), replacing any it had.
    /// An empty or blank nickname clears it. Nothing changes if it's refused.
    /// </summary>
    /// <returns>Why it was refused, or null if it was set or cleared.</returns>
    public static string? Set(Dictionary<string, string> nicknames, string channelId, string? nickname) {
        nickname = nickname?.Trim() ?? "";
        if (nickname.Length == 0) {
            nicknames.Remove(channelId);
            return null;
        }

        if (Check(nicknames, channelId, nickname) is { } error) {
            return error;
        }

        nicknames[channelId] = nickname;
        return null;
    }

    /// <summary>The channel with a nickname, ignoring case, or null if none has it.</summary>
    public static string? ChannelWith(IReadOnlyDictionary<string, string> nicknames, string nickname) {
        foreach (var (channelId, assigned) in nicknames) {
            if (string.Equals(assigned, nickname, StringComparison.OrdinalIgnoreCase)) {
                return channelId;
            }
        }

        return null;
    }

    /// <summary>
    /// Drops the nicknames of channels you're no longer in. Does nothing until the snapshot
    /// holds the complete channel list: before that, a missing channel may simply not have
    /// been listed yet.
    /// </summary>
    /// <returns>True if anything changed.</returns>
    public static bool Sync(Dictionary<string, string> nicknames, SessionSnapshot snapshot) => ChannelMaps.DropGone(nicknames, snapshot);
}
