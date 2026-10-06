using System.Text;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using LookingGlass.Core.Client;

namespace LookingGlass.Plugin;

/// <summary>
/// The game's side of text commands (&lt;t&gt;, &lt;me&gt;; the rules are in Core, <see cref="TextCommands"/>): what one
/// stands for now, as the game itself would send it. Each is put through the game's own expander,
/// <c>PronounModule.ProcessString</c> (FFXIVClientStructs; the function the game uses on a chat line, and ChatTwo for its
/// tells and echo): once to encode it (the text command becomes the game's fixed macro: a player and their world, a map
/// position), and once to decode that into what the chat log shows, which is read as plain text. A player from
/// another world is "Name" with the game's cross-world mark and their world, as the game shows them. Game thread only.
/// Nothing here is ever logged: what a text command stands for is someone's name.
/// </summary>
internal static unsafe class GameTextCommands {
    /// <summary>The longest string the expander is asked to make, as the game's own default.</summary>
    private const int MaxLength = 1023;

    /// <summary>
    /// What <paramref name="command"/> ("&lt;t&gt;", as typed) stands for now, as plain text; null if the game doesn't
    /// replace it (nothing targeted, no such party member, a text command it doesn't know) or it can't be read.
    /// </summary>
    public static string? Resolve(string command) {
        var module = PronounModule.Instance();
        if (module == null) {
            return null;
        }

        var input = Utf8String.FromString(command);
        Utf8String* encoded = null;
        try {
            // The result is the module's own buffer, which the next call overwrites: copied straight away.
            var result = module->ProcessString(input, true, MaxLength);
            if (result == null) {
                return null;
            }

            encoded = Utf8String.FromSequence(result->AsSpan());
            var decoded = module->ProcessString(encoded, false, MaxLength);
            return decoded == null ? null : PlainText(decoded->AsSpan());
        } finally {
            input->Dtor(true);
            if (encoded != null) {
                encoded->Dtor(true);
            }
        }
    }

    /// <summary>
    /// The text of what the game decoded: its text as shown, without its link (a player's, a map position's) or link
    /// arrow, and with the game's cross-world mark where it draws one before a world's name.
    /// </summary>
    private static string PlainText(ReadOnlySpan<byte> raw) {
        var text = new StringBuilder();
        foreach (var payload in SeString.Parse(raw).Payloads) {
            switch (payload) {
                case IconPayload { Icon: BitmapFontIcon.CrossWorld }:
                    text.Append((char) SeIconChar.CrossWorld);
                    break;
                case ITextProvider provider:
                    text.Append(provider.Text.Replace(((char) SeIconChar.LinkMarker).ToString(), ""));
                    break;
            }
        }

        return text.ToString();
    }
}
