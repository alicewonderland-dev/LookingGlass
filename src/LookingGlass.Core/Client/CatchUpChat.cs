using System.Globalization;

namespace LookingGlass.Core.Client;

/// <summary>What of a channel's caught-up messages goes to game chat (see <see cref="CatchUpChat.Plan"/>).</summary>
/// <param name="Header">The one line said first, with the channel's tag (LookingGlass blue).</param>
/// <param name="Shown">The messages printed, oldest first: the last <see cref="CatchUpChat.GameChatCap"/>.</param>
/// <param name="NotShown">How many earlier ones aren't printed (they are in the channel's window).</param>
public sealed record CatchUpChatPlan(string Header, IReadOnlyList<IncomingMessage> Shown, int NotShown);

/// <summary>
/// How messages caught up from while the player was away (message catch-up) show in game chat, unobtrusively: one line
/// saying how many, then at most the last <see cref="GameChatCap"/>, each with the time it was sent, so a long absence
/// doesn't flood the chat log. Every one of them is in the channel's window too (up to its history's size), after a line
/// saying the same. Channels turned off game chat show them only there.
/// </summary>
public static class CatchUpChat {
    /// <summary>The most caught-up messages of one channel printed to game chat at once.</summary>
    public const int GameChatCap = 50;

    /// <param name="tag">The channel's tag (see <see cref="ChannelTag"/>).</param>
    /// <param name="historyCapacity">How many lines the channel's window keeps (see <see cref="ChannelHistory.Capacity"/>).</param>
    public static CatchUpChatPlan Plan(string tag, IReadOnlyList<IncomingMessage> messages, int historyCapacity = ChannelHistory.DefaultCapacity) {
        var shown = messages.Count <= GameChatCap ? messages : messages.Skip(messages.Count - GameChatCap).ToList();
        var notShown = messages.Count - shown.Count;
        string header;
        if (notShown == 0) {
            header = messages.Count == 1 ? $"{tag} 1 message was sent while you were away:" : $"{tag} {messages.Count} messages were sent while you were away:";
        } else {
            header = $"{tag} {messages.Count} messages were sent while you were away. The last {GameChatCap} are below; to read the {notShown} before them, " +
                     "open the channel's window (right-click the channel in the main window)" +
                     (messages.Count > historyCapacity ? $", which keeps the last {historyCapacity}." : ".");
        }

        return new CatchUpChatPlan(header, shown, notShown);
    }

    /// <summary>The line said in a channel's window before its caught-up messages.</summary>
    public static string WindowSeparator(int count) =>
        count == 1 ? "1 message was sent while you were away:" : $"{count} messages were sent while you were away:";

    /// <summary>
    /// When a caught-up message was sent, as shown beside it: "14:05" (by this computer's clock) if it was today, else with
    /// the day, "Mon 14:05" (it can be up to a week old).
    /// </summary>
    /// <param name="zone">The clock's time zone; the computer's own by default.</param>
    public static string TimeLabel(DateTimeOffset sent, DateTimeOffset now, TimeZoneInfo? zone = null) {
        zone ??= TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(sent, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone);
        return local.Date == today.Date
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("ddd HH:mm", CultureInfo.InvariantCulture);
    }
}
