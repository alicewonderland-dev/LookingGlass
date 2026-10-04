using System.Numerics;

namespace LookingGlass.Core.Util;

/// <summary>
/// The game's UIColor sheet packs each colour as 0xRRGGBBAA (red in the high byte, alpha in the
/// low one). Checked against the sheet itself: row 37 (the default [LGC] tag) is 0x0099FFFF, an
/// azure, and every fully opaque row ends in FF. ImGui wants 0xAABBGGRR, or a Vector4.
/// </summary>
public static class UiColorPacking {
    public static Vector4 ToVector4(uint rgba) {
        return new Vector4(
            ((rgba >> 24) & 0xFF) / 255f,
            ((rgba >> 16) & 0xFF) / 255f,
            ((rgba >> 8) & 0xFF) / 255f,
            (rgba & 0xFF) / 255f);
    }

    /// <summary>The same colour as ImGui packs it (ImU32): 0xAABBGGRR, the bytes reversed.</summary>
    public static uint ToImGui(uint rgba) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(rgba);
}
