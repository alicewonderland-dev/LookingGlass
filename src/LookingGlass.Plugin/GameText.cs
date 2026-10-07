using Dalamud.Game.Text.SeStringHandling;
using LookingGlass.Core.Client;

namespace LookingGlass.Plugin;

/// <summary>
/// Turns coloured text (<see cref="TextPart"/>s, decided and tested in <see cref="ColouredText"/>) into the game's text, as
/// Dalamud's <see cref="SeString"/>. A UIColor row is Dalamud's own UIForeground payload. An exact colour is the game's
/// Color macro (0x13): Dalamud's SeString has no payload of its own for it, so it is written with Lumina's
/// <see cref="Lumina.Text.SeStringBuilder.PushColorRgba(byte, byte, byte, byte)"/> and
/// <see cref="Lumina.Text.SeStringBuilder.PopColor"/> and read back with <see cref="SeString.Parse(byte[])"/>, which keeps
/// it, byte for byte, as a <see cref="Dalamud.Game.Text.SeStringHandling.Payloads.RawPayload"/>. Checked against both
/// libraries: the push for 0x123456 is <c>02 13 06 FE FF 12 34 56 03</c>, the pop <c>02 13 02 EC 03</c>.
/// </summary>
internal static class GameText {
    /// <summary>Appends the parts; <paramref name="body"/> fills in <see cref="TextPart.Body"/>, if there is one.</summary>
    public static SeStringBuilder Append(SeStringBuilder builder, IEnumerable<TextPart> parts, Action<SeStringBuilder>? body = null) {
        foreach (var part in parts) {
            switch (part) {
                case TextPart.Text text:
                    builder.AddText(text.Value);
                    break;
                case TextPart.ForegroundOn on:
                    builder.AddUiForeground(on.Row);
                    break;
                case TextPart.ForegroundOff:
                    builder.AddUiForegroundOff();
                    break;
                case TextPart.ColourOn on:
                    builder.Append(ColourPush(on.Rgb));
                    break;
                case TextPart.ColourOff:
                    builder.Append(ColourPop());
                    break;
                case TextPart.Body:
                    body?.Invoke(builder);
                    break;
            }
        }

        return builder;
    }

    /// <summary>The parts as one SeString.</summary>
    public static SeString Build(IEnumerable<TextPart> parts) => Append(new SeStringBuilder(), parts).Build();

    /// <summary>The closest UIColor row to a custom colour, for <see cref="ColouredText"/>'s layered fallback.</summary>
    public static ushort? Nearest(uint rgb) => Ui.ChannelPalette.Nearest(rgb);

    private static SeString ColourPush(uint rgb) =>
        SeString.Parse(new Lumina.Text.SeStringBuilder().PushColorRgba((byte) (rgb >> 16), (byte) (rgb >> 8), (byte) rgb, 0xFF).ToArray());

    private static SeString ColourPop() => SeString.Parse(new Lumina.Text.SeStringBuilder().PopColor().ToArray());
}
