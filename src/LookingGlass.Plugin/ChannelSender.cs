using LookingGlass.Core.Client;

namespace LookingGlass.Plugin;

/// <summary>
/// The one send path for text typed in game or in a channel window: /lgcN &lt;message&gt;, /lgc &lt;nickname&gt; &lt;message&gt;,
/// plain text while talking in a channel, and a channel window's input box. Call on the framework thread; the send itself
/// runs in the background, and a failure is told (a "Not sent" line is information, in LookingGlass blue: nothing went
/// anywhere it shouldn't): in game chat, or where the caller says (a channel window shows it in itself).
/// </summary>
public sealed class ChannelSender(SessionManager sessions, ChatOutput chat) {
    /// <summary>
    /// Sends what was typed, links and all (see <see cref="LinkText"/>): each link as its "[name]" in the text, and as a
    /// link over it. A placeholder still in it is resolved now, from what the game holds for it. A link the game says
    /// nothing about (not even its name) is left out, and once the rest has been sent, one line says so: a message is
    /// either sent or not, never both "sent" and an error. The game's text commands (&lt;t&gt;, &lt;me&gt;) are replaced
    /// as the game would replace them (see <see cref="TextCommands"/>, <see cref="GameTextCommands"/>), as plain text.
    /// </summary>
    /// <param name="stickyTag">
    /// The channel's tag if this was typed while talking in it: then every failure says the message didn't go to game
    /// chat either, so the player knows to send it again.
    /// </param>
    /// <param name="sent">Run once the message has been sent (on a background thread), and only then.</param>
    /// <param name="tell">
    /// Where to say what went wrong (and that a link was left out), from any thread; null for game chat. A channel window
    /// keeps it in the channel's history, so it shows where the message was typed.
    /// </param>
    /// <param name="notSent">Run (on any thread) once it is certain the message wasn't sent: a channel window puts it back to send again.</param>
    /// <returns>
    /// The message being sent, whether a link was left out, and how many text commands were replaced (a count, for the
    /// diagnostic log); null if nothing is sent.
    /// </returns>
    internal (LinkedText Message, bool LeftOut, int TextCommands)? Send(string channelId, TypedLine typed, string? stickyTag = null, Action? sent = null,
        Action<NoticeTone, string>? tell = null, Action? notSent = null) {
        tell ??= (tone, text) => chat.Notice(tone, text);
        var session = sessions.Session;
        if (session == null || session.Snapshot.State != ConnectionState.Ready) {
            tell(NoticeTone.Info, stickyTag == null
                ? "Not connected to LookingGlass. Open /lookingglass to check."
                : StickyMessages.NotSent(stickyTag, StickyMessages.NotConnectedReason));
            notSent?.Invoke();
            return null;
        }

        // Links first (the chat box's <item> and the like), then the game's text commands (<t>, <me>), as the game would send
        // them: never what a received message holds.
        var (resolved, replaced) = TextCommands.Resolve(LinkText.ResolvePlaceholders(typed, GameLinks.Placeholder), GameTextCommands.Resolve);
        var (message, leftOut) = LinkText.Compose(resolved);
        if (string.IsNullOrWhiteSpace(message.Text)) {
            tell(NoticeTone.Info, stickyTag == null
                ? $"Not sent: {StickyMessages.LinkUnreadableReason}"
                : StickyMessages.NotSent(stickyTag, StickyMessages.LinkUnreadableReason));
            notSent?.Invoke();
            return null;
        }

        // Talking in a channel means you've caught up with it.
        sessions.Unread.MarkRead(channelId);
        _ = Task.Run(async () => {
            try {
                await session.SendAsync(channelId, message);
            } catch (Exception ex) {
                var advanced = sessions.AdvancedMode;
                var reason = PlainMessages.MessageOf(ex, advanced);
                tell(NoticeTone.Info, PlainMessages.Of(stickyTag == null ? $"Not sent: {reason}" : StickyMessages.NotSent(stickyTag, reason), advanced));
                notSent?.Invoke();
                return;
            }

            try {
                if (leftOut) {
                    tell(NoticeTone.Info, StickyMessages.LinkNotSent);
                }

                sent?.Invoke();
            } catch (Exception ex) {
                Services.Log.Error(ex, "Error after a message was sent");
            }
        });

        return (message, leftOut, replaced);
    }
}
