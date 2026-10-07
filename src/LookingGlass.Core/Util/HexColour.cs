using System.Globalization;

namespace LookingGlass.Core.Util;

/// <summary>Colour codes as typed and shown: "#RRGGBB" (the "#" may be left out when typing; upper or lower case).</summary>
public static class HexColour {
    /// <summary>Reads a colour code into 0xRRGGBB. Spaces around it are ignored; anything but six hex digits, with or without "#", is refused.</summary>
    public static bool TryParse(string? text, out uint rgb) {
        rgb = 0;
        if (text == null) {
            return false;
        }

        var span = text.AsSpan().Trim();
        if (span.StartsWith("#")) {
            span = span[1..];
        }

        if (span.Length != 6) {
            return false;
        }

        foreach (var c in span) {
            if (!char.IsAsciiHexDigit(c)) {
                return false;
            }
        }

        return uint.TryParse(span, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rgb);
    }

    /// <summary>0xRRGGBB as "#RRGGBB", in capitals. Anything above the low 24 bits is ignored.</summary>
    public static string Format(uint rgb) => "#" + (rgb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);
}
