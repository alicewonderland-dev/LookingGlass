namespace LookingGlass.Core.Client;

/// <summary>
/// The tag in front of a channel's lines in game chat. A channel with a nickname is tagged with
/// it, as in [sky], so it reads the way you talk there (/lgc sky); otherwise with its command
/// number, as in [LGC3]. A channel with neither (more channels than numbers, or a message that
/// arrives before the channel list is in) is tagged [LGC].
/// </summary>
public static class ChannelTag {
    public const string Fallback = "[LGC]";

    /// <param name="slot">The channel's command number (1 to <see cref="CommandSlots.Count"/>), if it has one.</param>
    /// <param name="nickname">The channel's nickname, if it has one.</param>
    /// <param name="useNickname">False to always tag with the number, as with the setting "Show nicknames in chat tags" off.</param>
    public static string For(int? slot, string? nickname, bool useNickname) {
        // Nicknames are checked when set, but the settings file can be edited by hand: only a valid one
        // (a few ASCII letters, digits, - and _) is shown, so it can't carry formatting or look like a number.
        if (useNickname && nickname is { } nick && ChannelNicknames.Validate(nick.Trim()) == null) {
            return $"[{nick.Trim()}]";
        }

        return slot is >= 1 and <= CommandSlots.Count ? $"[LGC{slot}]" : Fallback;
    }
}
