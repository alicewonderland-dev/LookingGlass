namespace LookingGlass.Core.Client;

/// <summary>
/// The two colour fields of the message LookingGlass sends ChatTwo on <c>ExtraChat.OverrideChannelColour</c>, chosen as
/// ExtraChat chose them (its <c>Ipc.BroadcastOverride</c>): <see cref="UiColour"/> the UIColor row, <see cref="Rgba"/> that
/// colour packed 0xRRGGBBAA (red in the high byte, alpha in the low one, as the sheet packs it).
/// <para>
/// What ChatTwo does with them (its public source, 1.40.9, commit a63403c, unchanged in 1.41.0): it keeps only
/// <see cref="Rgba"/> (<c>Ipc/ExtraChat.cs</c>, <c>OnOverrideChannel</c>) and paints it on the text typed in its input
/// while that text isn't a command (<c>Ui/Handler/InputHandler.cs</c>, <c>DrawInputArea</c>, byte-reversed for ImGui);
/// it never reads <see cref="UiColour"/>. The label naming the channel ("LookingGlass [sky]") is drawn with no colour of
/// its own (<c>Ui/ChatLog/ChatLog.Window.cs</c>, <c>ReadChannelName</c>), in ChatTwo's plain text colour, whatever is
/// sent; so it has been since ChatTwo first took this message (2022). An Rgba of 0 would make the typed text invisible
/// (alpha 0), so a colour that can't be read is sent as opaque white, and every colour sent is opaque.
/// </para>
/// </summary>
public readonly record struct ChatTwoOverrideColour(ushort UiColour, uint Rgba) {
    /// <summary>Opaque white, 0xFFFFFFFF: what is sent when a colour can't be read (and with no override at all).</summary>
    public const uint White = 0xFFFFFFFF;

    /// <summary>Nothing to colour: no row, opaque white.</summary>
    public static readonly ChatTwoOverrideColour None = new(0, White);

    /// <summary>
    /// The fields for a channel's colour (null: the default tag colour, LookingGlass blue, as chat shows it). A row is
    /// sent as itself and its colour from the sheet; a custom colour exactly in <see cref="Rgba"/>, with the closest row
    /// in <see cref="UiColour"/> (for a reader that takes only rows, as the game's chat does; 0 if it isn't known).
    /// </summary>
    /// <param name="colour">The channel's colour (local chat's included), or null for the default.</param>
    /// <param name="rowRgba">A UIColor row's colour (its Dark column, 0xRRGGBBAA), or null (or 0) if the sheet doesn't say.</param>
    /// <param name="nearest">The row closest to a custom colour (0xRRGGBB), or null if unknown.</param>
    public static ChatTwoOverrideColour Of(ChannelColour? colour, Func<ushort, uint?> rowRgba, Func<uint, ushort?> nearest) {
        var shown = colour ?? ColouredText.DefaultTag;
        if (shown.IsCustom) {
            return new ChatTwoOverrideColour(nearest(shown.Rgb) ?? 0, Opaque(shown.Rgb));
        }

        return new ChatTwoOverrideColour(shown.Row, rowRgba(shown.Row) is { } rgba and not 0 ? rgba | 0xFF : White);
    }

    /// <summary>A colour (0xRRGGBB) packed 0xRRGGBBAA, fully opaque.</summary>
    public static uint Opaque(uint rgb) => ((rgb & 0xFFFFFF) << 8) | 0xFF;
}
