using LookingGlass.Core.Util;

namespace LookingGlass.Core.Client;

/// <summary>
/// A channel's colour: a row of the game's UIColor sheet (one of the picker's swatches, as every colour was before custom
/// colours), or a custom colour, any RGB value (0xRRGGBB). See <see cref="ChannelColours"/> for how they are kept, and
/// <see cref="ColouredText"/> for how a custom colour reaches the game's text.
/// </summary>
public readonly record struct ChannelColour {
    private ChannelColour(ushort row, uint rgb, bool custom) {
        this.Row = row;
        this.Rgb = rgb;
        this.IsCustom = custom;
    }

    /// <summary>The UIColor row; 0 for a custom colour.</summary>
    public ushort Row { get; }

    /// <summary>The custom colour as 0xRRGGBB; 0 for a row.</summary>
    public uint Rgb { get; }

    /// <summary>A custom RGB colour rather than a UIColor row.</summary>
    public bool IsCustom { get; }

    public static ChannelColour OfRow(ushort row) => new(row, 0, false);

    /// <summary>A custom colour; anything above the low 24 bits (as from a hand-edited settings file) is dropped.</summary>
    public static ChannelColour Custom(uint rgb) => new(0, rgb & 0xFFFFFF, true);

    public override string ToString() => this.IsCustom ? HexColour.Format(this.Rgb) : $"UIColor {this.Row}";
}
