using LookingGlass.Core.Client;

namespace LookingGlass.Plugin;

/// <summary>
/// The one send path for text typed in game: /lgcN &lt;message&gt;, /lgc &lt;nickname&gt; &lt;message&gt;, and plain text while
/// talking in a channel. Call on the framework thread; the send itself runs in the background, and a failure is printed
/// (a "Not sent" line is information, in LookingGlass blue: nothing went anywhere it shouldn't).
/// </summary>
public sealed class ChannelSender(SessionManager sessions, ChatOutput chat) {
    /// <param name="stickyTag">
    /// The channel's tag if this was typed while talking in it: then every failure says the message didn't go to game
    /// chat either, so the player knows to send it again.
    /// </param>
    /// <param name="sent">Run once the message has been sent (on a background thread), and only then.</param>
    public void Send(string channelId, string text, string? stickyTag = null, Action? sent = null) {
        var session = sessions.Session;
        if (session == null || session.Snapshot.State != ConnectionState.Ready) {
            chat.Notice(NoticeTone.Info, stickyTag == null
                ? "Not connected to LookingGlass. Open /lookingglass to check."
                : StickyMessages.NotSent(stickyTag, StickyMessages.NotConnectedReason));
            return;
        }

        // Talking in a channel means you've caught up with it.
        sessions.Unread.MarkRead(channelId);
        _ = Task.Run(async () => {
            try {
                await session.SendTextAsync(channelId, text);
            } catch (Exception ex) {
                var advanced = sessions.AdvancedMode;
                var reason = PlainMessages.MessageOf(ex, advanced);
                chat.Notice(NoticeTone.Info, PlainMessages.Of(stickyTag == null ? $"Not sent: {reason}" : StickyMessages.NotSent(stickyTag, reason), advanced));
                return;
            }

            try {
                sent?.Invoke();
            } catch (Exception ex) {
                Services.Log.Error(ex, "Error after a message was sent");
            }
        });
    }
}
