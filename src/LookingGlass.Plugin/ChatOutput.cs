using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using LookingGlass.Core.Client;
using LookingGlass.Core.Util;

namespace LookingGlass.Plugin;

/// <summary>
/// Prints LookingGlass lines into the game's chat log. Every call is marshalled to the framework thread. LookingGlass's
/// own lines are in one of three colours, chosen in <see cref="NoticeColours"/>: blue for information, light red for
/// warnings, dark red for critical warnings.
/// </summary>
public sealed class ChatOutput(Configuration config) {
    /// <summary>The channel tag's colour unless the channel has one of its own: LookingGlass blue.</summary>
    public const ushort TagColour = NoticeColours.Blue;

    /// <param name="slot">The channel's command number, if it has one.</param>
    /// <param name="nickname">The channel's nickname, if it has one: its tag, unless nickname tags are turned off.</param>
    /// <param name="colour">The channel's colour (a UIColor row or a custom colour), or null for the default: only the tag coloured.</param>
    /// <param name="sentAt">For a message caught up from while the player was away: when it was sent (see <see cref="CatchUpChat.TimeLabel"/>), after the tag.</param>
    /// <param name="nameColour">The sender's name colour (0xRRGGBB; see <see cref="NameColours"/>), or null: their name in the line's colour.</param>
    public void Message(IncomingMessage message, int? slot, string? nickname, ChannelColour? colour = null, string? sentAt = null, uint? nameColour = null) {
        RunOnFramework(() => {
            var tag = ChannelTag.For(slot, nickname, config.NicknameTags);
            // The tag in the channel's colour, and the whole line too (like the game's own linkshells) if that's on; the
            // sender's name in its own colour if it has one; a custom colour layered over its closest game colour (see
            // ColouredText, which also cleans the sender's name and world: everything from other users is sanitised).
            var parts = ColouredText.Message(tag, colour, sentAt, config.ColourWholeLine, GameText.Nearest, message.Sender.Name, message.Sender.WorldName, nameColour);
            var line = GameText.Append(new SeStringBuilder(), parts, builder => {
                if (message.Unsupported) {
                    builder.AddItalics("(a message type this version can't show)");
                } else if (message.Links.Count == 0) {
                    builder.AddText(TextSanitizer.Clean(message.Text));
                } else {
                    AddLinked(builder, message.Linked);
                }
            });

            Services.Chat.Print(new XivChatEntry { Type = config.ChatType, Message = line.Build() });
        });
    }

    /// <summary>
    /// A message's text with its links where they were in the sentence: the text sanitised as all remote text is, and
    /// each link rebuilt from its ids as the game's own interactive link if it checks out against the player's own game
    /// data (see <see cref="GameLinks.TryAppend"/>), else the sender's "[name]", sanitised, as plain text. No byte that
    /// came over the network reaches the chat log as anything but text.
    /// </summary>
    private static void AddLinked(SeStringBuilder builder, LinkedText message) {
        // Sanitised, and within one length limit across all its pieces, as a message without links is.
        foreach (var part in message.ShownParts()) {
            switch (part) {
                case MessagePart.Text text:
                    builder.AddText(text.Value);
                    break;
                case MessagePart.Link link when !GameLinks.TryAppend(builder, link.Target):
                    builder.AddText(link.Fallback);
                    break;
            }
        }
    }

    /// <summary>
    /// Shows a notice in chat only, in the colour of its level and kind (see <see cref="NoticeColours.ToneOf"/>). Notice
    /// text can contain names and channel names, so it is never logged.
    /// </summary>
    public void Notice(NoticeLevel level, string text, NoticeKind kind = NoticeKind.General) {
        if (level == NoticeLevel.Debug) {
            return;
        }

        this.Notice(NoticeColours.ToneOf(level, kind), text);
    }

    /// <summary>Shows a notice in chat only, in a tone's colour.</summary>
    /// <param name="tag">A channel's tag in the text, shown in the channel's colour (<paramref name="tagColour"/>).</param>
    public void Notice(NoticeTone tone, string text, string? tag = null, ChannelColour? tagColour = null) {
        // Notices embed remote text (names, channel names, server errors and announcements).
        text = TextSanitizer.Clean(text);
        tag = tag == null ? null : TextSanitizer.Clean(tag);
        RunOnFramework(() => {
            var parts = ColouredText.Notice(tone, text, tag, tagColour, GameText.Nearest);
            Services.Chat.Print(new XivChatEntry { Type = config.ChatType, Message = GameText.Build(parts) });
        });
    }

    /// <summary>
    /// A line about a channel (talking in it, or stopping): information, in LookingGlass blue, with the channel's tag in
    /// its colour (the tag colour for the default).
    /// </summary>
    public void ChannelNotice(string text, string tag, ChannelColour? colour) => this.Notice(NoticeTone.Info, text, tag, colour);

    /// <summary>
    /// The colour test (/lgdebug): for each colour, a line layered as a channel's would be, one in only its closest game
    /// colour, and one in only the exact colour (see <see cref="ColouredText.Samples"/>). Nothing about any channel changes.
    /// </summary>
    public void ColourSamples(IEnumerable<uint> colours) {
        var samples = colours.ToList();
        RunOnFramework(() => {
            foreach (var rgb in samples) {
                foreach (var line in ColouredText.Samples(rgb, GameText.Nearest)) {
                    Services.Chat.Print(new XivChatEntry { Type = config.ChatType, Message = GameText.Build(line) });
                }
            }
        });
    }

    private static void RunOnFramework(Action action) {
        _ = Services.Framework.RunOnFrameworkThread(() => {
            try {
                action();
            } catch (Exception ex) {
                Services.Log.Error(ex, "Couldn't print to chat");
            }
        });
    }
}
