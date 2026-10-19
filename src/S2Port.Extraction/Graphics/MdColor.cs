namespace S2Port.Extraction.Graphics;

/// <summary>
/// Mega Drive CRAM colour word: 0000 BBB0 GGG0 RRR0.
/// Conversion to 8-bit uses a linear scale (c * 255 / 7). Real hardware output is
/// slightly non-linear; the raw words are always kept alongside so a renderer can choose.
/// </summary>
public readonly record struct MdColor(ushort Word)
{
    public int R3 => (Word >> 1) & 7;
    public int G3 => (Word >> 5) & 7;
    public int B3 => (Word >> 9) & 7;

    public static byte To8Bit(int c3) => (byte)((c3 * 255 + 3) / 7);

    public (byte R, byte G, byte B) ToRgb() => (To8Bit(R3), To8Bit(G3), To8Bit(B3));

    public string ToHex()
    {
        var (r, g, b) = ToRgb();
        return $"#{r:X2}{g:X2}{b:X2}";
    }
}
