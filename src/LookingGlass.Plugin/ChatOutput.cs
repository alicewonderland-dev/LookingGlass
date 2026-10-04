using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using LookingGlass.Core.Client;
using LookingGlass.Core.Util;

namespace LookingGlass.Plugin;

/// <summary>Prints LookingGlass lines into the game's chat log. Every call is marshalled to the framework thread.</summary>
public sealed class ChatOutput(Configuration config) {
    // UIColor rows used for the channel tag (unless the channel has a colour of its own) and for warnings.
    public const ushort TagColour = 37;
    private const ushort WarningColour = 17;
    private const ushort ErrorColour = 534;

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
            } else {
                builder.AddText(TextSanitizer.Clean(message.Text));
            }

            if (wholeLine) {
                builder.AddUiForegroundOff();
            }

            Services.Chat.Print(new XivChatEntry { Type = config.ChatType, Message = builder.Build() });
        });
    }

    /// <summary>Shows a notice in chat only. Notice text can contain names and channel names, so it is never logged.</summary>
    public void Notice(NoticeLevel level, string text) {
        if (level == NoticeLevel.Debug) {
            return;
        }

        // Notices embed remote text (names, channel names, server errors and announcements).
        text = TextSanitizer.Clean(text);
        RunOnFramework(() => {
            var builder = new SeStringBuilder().AddUiForeground("[LookingGlass] ", TagColour);
            switch (level) {
                case NoticeLevel.Warning:
                    builder.AddUiForeground(text, WarningColour);
                    break;
                case NoticeLevel.Error:
                    builder.AddUiForeground(text, ErrorColour);
                    break;
                default:
                    builder.AddText(text);
                    break;
            }

            Services.Chat.Print(new XivChatEntry { Type = config.ChatType, Message = builder.Build() });
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
