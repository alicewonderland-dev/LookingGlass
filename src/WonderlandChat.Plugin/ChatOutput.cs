using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using WonderlandChat.Core.Client;
using WonderlandChat.Core.Util;

namespace WonderlandChat.Plugin;

/// <summary>Prints WonderlandChat lines into the game's chat log. Every call is marshalled to the framework thread.</summary>
public sealed class ChatOutput(Configuration config) {
    // UIColor rows used for the channel tag and for warnings.
    private const ushort TagColour = 37;
    private const ushort WarningColour = 17;
    private const ushort ErrorColour = 534;

    public void Message(IncomingMessage message, int? slot) {
        RunOnFramework(() => {
            var tag = slot is { } s ? $"WCL{s}" : "WCL?";
            // Everything from other users is sanitised: raw control bytes would become live game formatting.
            var builder = new SeStringBuilder()
                .AddUiForeground($"[{tag}]", TagColour)
                .AddText($"<{TextSanitizer.Name(message.Sender.Name)}@{TextSanitizer.Name(message.Sender.WorldName)}> ");

            if (message.Unsupported) {
                builder.AddItalics("(a message type this version can't show)");
            } else {
                builder.AddText(TextSanitizer.Clean(message.Text));
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
            var builder = new SeStringBuilder().AddUiForeground("[WonderlandChat] ", TagColour);
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
