namespace S2Port.Extraction.Smb3;

/// <summary>
/// SMB3's palettes (Palette_By_Tileset in prg027; the Plains set found at bank 27 $AC92 in this image: 16 bytes per
/// background set, PalSel_Tile_Colors = 0 for 1-1; sprite set PalSel_Obj_Colors = 8 at +$80) and the NES colours.
/// </summary>
public static class Smb3Palettes
{
    public const int Bank = 27, PlainsBg = 0xAC92, Sprites = 0xAC92 + 8 * 16;

    /// <summary>
    /// The NTSC 2C02's 64 colours as RGB (a common measured palette; hardware, not ROM data). Index = NES colour.
    /// </summary>
    private static readonly int[] Rgb =
    [
        0x545454, 0x001E74, 0x081090, 0x300088, 0x440064, 0x5C0030, 0x540400, 0x3C1800,
        0x202A00, 0x083A00, 0x004000, 0x003C00, 0x00323C, 0x000000, 0x000000, 0x000000,
        0x989698, 0x084CC4, 0x3032EC, 0x5C1EE4, 0x8814B0, 0xA01464, 0x982220, 0x783C00,
        0x545A00, 0x287200, 0x087C00, 0x007628, 0x006678, 0x000000, 0x000000, 0x000000,
        0xECEEEC, 0x4C9AEC, 0x787CEC, 0xB062EC, 0xE454EC, 0xEC58B4, 0xEC6A64, 0xD48820,
        0xA0AA00, 0x74C400, 0x4CD020, 0x38CC6C, 0x38B4CC, 0x3C3C3C, 0x000000, 0x000000,
        0xECEEEC, 0xA8CCEC, 0xBCBCEC, 0xD4B2EC, 0xECAEEC, 0xECAED4, 0xECB4B0, 0xE4C490,
        0xCCD278, 0xB4DE78, 0xA8E290, 0x98E2B4, 0xA0D6E4, 0xA0A2A0, 0x000000, 0x000000,
    ];

    public static (byte R, byte G, byte B) ToRgb(int nes)
    {
        int c = Rgb[nes & 0x3F];
        return ((byte)(c >> 16), (byte)(c >> 8), (byte)c);
    }

    /// <summary>The nearest Mega Drive colour (3 bits per channel, rounded).</summary>
    public static ushort ToMd(int nes)
    {
        var (r, g, b) = ToRgb(nes);
        static int Q(byte v) => (v * 7 + 127) / 255;
        return (ushort)((Q(r) << 1) | (Q(g) << 5) | (Q(b) << 9));
    }
}
