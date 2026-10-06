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
    /// <param name="colour">The channel's colour (a UIColor row), or null for the default: only the tag coloured.</param>
    public void Message(IncomingMessage message, int? slot, string? nickname, ushort? colour = null) {
        RunOnFramework(() => {
            var tag = ChannelTag.For(slot, nickname, config.NicknameTags);
            // Everything from other users is sanitised: raw control bytes would become live game formatting.
            var sender = $"<{TextSanitizer.Name(message.Sender.Name)}@{TextSanitizer.Name(message.Sender.WorldName)}> ";
            var builder = new SeStringBuilder().AddUiForeground(tag, colour ?? TagColour);

            // The whole line in the channel's colour, like the game's own linkshells.
            var wholeLine = colour != null && config.ColourWholeLine;
            if (wholeLine) {
                builder.AddUiForeground(colour!.Value);
            }

            builder.AddText(sender);
            if (message.Unsupported) {
                builder.AddItalics("(a message type this version can't show)");
            } else if (message.Links.Count == 0) {
                builder.AddText(TextSanitizer.Clean(message.Text));
            } else {
                AddLinked(builder, message.Linked);
            }

            if (wholeLine) {
                builder.AddUiForegroundOff();
            }

            Services.Chat.Print(new XivChatEntry { Type = config.ChatType, Message = builder.Build() });
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
    public void Notice(NoticeTone tone, string text, string? tag = null, ushort? tagColour = null) {
        // Notices embed remote text (names, channel names, server errors and announcements).
        text = TextSanitizer.Clean(text);
        tag = tag == null ? null : TextSanitizer.Clean(tag);
        var colour = NoticeColours.Of(tone);
        RunOnFramework(() => {
            var builder = new SeStringBuilder().AddUiForeground("[LookingGlass] ", NoticeColours.Blue);
            var at = string.IsNullOrEmpty(tag) ? -1 : text.IndexOf(tag, StringComparison.Ordinal);
            if (at < 0) {
                builder.AddUiForeground(text, colour);
            } else {
                if (at > 0) {
                    builder.AddUiForeground(text[..at], colour);
                }

                builder.AddUiForeground(tag!, tagColour ?? TagColour);
                if (at + tag!.Length < text.Length) {
                    builder.AddUiForeground(text[(at + tag.Length)..], colour);
                }
            }

            Services.Chat.Print(new XivChatEntry { Type = config.ChatType, Message = builder.Build() });
        });
    }

    /// <summary>
    /// A line about a channel (talking in it, or stopping): information, in LookingGlass blue, with the channel's tag in
    /// its colour (the tag colour for the default).
    /// </summary>
    public void ChannelNotice(string text, string tag, ushort? colour) => this.Notice(NoticeTone.Info, text, tag, colour);

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
