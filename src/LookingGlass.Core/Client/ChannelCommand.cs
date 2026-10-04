namespace LookingGlass.Core.Client;

/// <summary>The arguments of /lgc &lt;nickname&gt; &lt;message&gt;; either part is empty if it's missing.</summary>
public readonly record struct NicknameArguments(string Nickname, string Message);

/// <summary>
/// What a channel command (/lgc1 to /lgc50, or /lgc &lt;nickname&gt;) comes to: a message to send
/// to a channel, or a notice to show instead. Kept free of game types so it can be tested.
/// </summary>
public abstract record ChannelCommand {
    private ChannelCommand() {
    }

    /// <summary>Send <paramref name="Text"/> to the channel.</summary>
    public sealed record Send(string ChannelId, string Text) : ChannelCommand;

    /// <summary>The command was typed without a message (or a nickname): show how to use it.</summary>
    public sealed record Usage(string Text) : ChannelCommand;

    /// <summary>No channel is on that slot, or has that nickname.</summary>
    public sealed record NotFound(string Text) : ChannelCommand;

    public static string NicknameUsage =>
        $"Usage: {CommandSlots.Prefix} <nickname> <message>, or {CommandSlots.Prefix}1 to {CommandSlots.Prefix}{CommandSlots.Count} <message>. " +
        "Set nicknames and numbers in the main window (/lg).";

    /// <summary>/lgc&lt;slot&gt; &lt;message&gt;.</summary>
    public static ChannelCommand ForSlot(IReadOnlyDictionary<string, int> slots, int slot, string arguments) {
        var text = arguments.Trim();
        if (text.Length == 0) {
            return new Usage($"Usage: {CommandSlots.Prefix}{slot} <message>");
        }

        return CommandSlots.ChannelIn(slots, slot) is { } channelId
            ? new Send(channelId, text)
            : new NotFound($"No channel is on {CommandSlots.Prefix}{slot}. Assign one in the main window (/lg).");
    }

    /// <summary>/lgc &lt;nickname&gt; &lt;message&gt;, whether or not that channel also has a slot.</summary>
    public static ChannelCommand ForNickname(IReadOnlyDictionary<string, string> nicknames, string arguments) {
        var (nickname, text) = ParseNickname(arguments);
        if (nickname.Length == 0 || text.Length == 0) {
            return new Usage(NicknameUsage);
        }

        return ChannelNicknames.ChannelWith(nicknames, nickname) is { } channelId
            ? new Send(channelId, text)
            : new NotFound($"No channel has the nickname '{nickname}'.");
    }

    /// <summary>Splits "sky hello there" into the nickname "sky" and the message "hello there".</summary>
    public static NicknameArguments ParseNickname(string arguments) {
        var trimmed = arguments.Trim();
        var end = 0;
        while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end])) {
            end++;
        }

        return new NicknameArguments(trimmed[..end], trimmed[end..].Trim());
    }
}
