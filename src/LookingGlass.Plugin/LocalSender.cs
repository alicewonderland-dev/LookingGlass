using LookingGlass.Core.Client;

namespace LookingGlass.Plugin;

/// <summary>
/// /lgl &lt;message&gt;: local chat (see <see cref="LocalChat"/>). Sends to the friends near the player who use LookingGlass,
/// as the game shows them now, closest first: the object table and the friends list are read here, on the framework thread,
/// in the same frame the line was typed; the session looks them up and sends in the background. What is said is
/// information (LookingGlass blue), in game chat, where the line was typed: nobody to send to (and, if the friends list may
/// not be loaded, to open it once), none of them uses LookingGlass, or "Not sent" and why.
/// </summary>
/// <param name="privacyAccepted">The player accepted what local chat tells the server (see <see cref="LocalChat.FirstStep"/>).</param>
/// <param name="askPrivacy">Opens the privacy notice, to accept or not.</param>
public sealed class LocalSender(SessionManager sessions, ChatOutput chat, Func<bool> privacyAccepted, Action askPrivacy) {
    /// <summary>Sends what was typed after /lgl, links and all, as a channel message is (see <see cref="ChannelSender"/>). Framework thread.</summary>
    internal void Send(TypedLine typed) {
        if (string.IsNullOrWhiteSpace(LinkText.StripMarkers(typed.Text))) {
            this.Tell(LocalChatWords.Usage);
            return;
        }

        // Before anything is read or looked up: the first time, the player is asked, and nothing is sent.
        if (LocalChat.FirstStep(privacyAccepted()) == LocalChatStep.AskFirst) {
            this.Tell(LocalChatWords.PrivacyAsked);
            askPrivacy();
            return;
        }

        var session = sessions.Session;
        if (session == null || session.Snapshot.State != ConnectionState.Ready) {
            chat.Notice(NoticeTone.Info, "Not connected to LookingGlass. Open /lookingglass to check.");
            return;
        }

        if (!session.LocalChatAvailable) {
            this.Tell(LocalChatWords.NotOnThisServer);
            return;
        }

        // As for a channel: links first, then the game's text commands (<t>, <me>), as the game would send them.
        var (resolved, _) = TextCommands.Resolve(LinkText.ResolvePlaceholders(typed, GameLinks.Placeholder), GameTextCommands.Resolve);
        var (message, leftOut) = LinkText.Compose(resolved);
        if (string.IsNullOrWhiteSpace(message.Text)) {
            chat.Notice(NoticeTone.Info, $"Not sent: {StickyMessages.LinkUnreadableReason}");
            return;
        }

        LocalSurroundings around;
        try {
            around = LocalChatGame.Read();
        } catch (Exception ex) {
            Services.Log.Error(ex, "Couldn't read who is near for local chat");
            chat.Notice(NoticeTone.Info, "Not sent: LookingGlass couldn't see who is near you.");
            return;
        }

        if (LocalChat.NobodyToSendTo(around) is { } nobody) {
            this.Tell(nobody);
            return;
        }

        var friends = LocalChat.Recipients(around, LocalChat.MaxRecipients).Select(friend => (friend.Name, friend.WorldName)).ToList();
        // Counts only: never who, nor what.
        Services.Log.Debug($"Local chat: sending to {friends.Count} friends near ({around.Players.Count} players near, friends list {(around.FriendsListLoaded ? "loaded" : "not loaded")})");
        _ = Task.Run(async () => {
            LocalSendResult result;
            try {
                result = await session.SendLocalAsync(friends, message);
            } catch (Exception ex) {
                var advanced = sessions.AdvancedMode;
                chat.Notice(NoticeTone.Info, PlainMessages.Of($"Not sent: {PlainMessages.MessageOf(ex, advanced)}", advanced));
                return;
            }

            Services.Log.Debug($"Local chat: sent to {result.Sent} (not using LookingGlass {result.NotUsingIt}, not checked {result.CouldntCheck})");
            if (LocalChatWords.Sent(result) is { } said) {
                this.Tell(said);
            } else if (leftOut) {
                chat.Notice(NoticeTone.Info, StickyMessages.LinkNotSent);
            }
        });
    }

    private void Tell(Wording wording) => chat.Notice(NoticeTone.Info, wording.For(sessions.AdvancedMode));
}
