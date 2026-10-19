namespace S2Port.Extraction.Graphics;

/// <summary>Helpers for Mega Drive 4bpp 8x8 tiles (32 bytes each, 2 pixels per byte, high nibble first).</summary>
public static class Tiles
{
    public const int BytesPerTile = 32;

    /// <summary>
    /// Lays tiles out on a sheet <paramref name="tilesPerRow"/> wide. Output pixel = line * 16 + nibble,
    /// so the raw 4bpp index is always <c>pixel &amp; 15</c> and the palette line is <c>pixel &gt;&gt; 4</c>.
    /// </summary>
    public static (int Width, int Height, byte[] Pixels) ToSheet(ReadOnlySpan<byte> tileData, int tilesPerRow,
        Func<int, int>? paletteLineForTile = null)
    {
        int count = tileData.Length / BytesPerTile;
        int rows = Math.Max(1, (count + tilesPerRow - 1) / tilesPerRow);
        int width = tilesPerRow * 8, height = rows * 8;
        var pixels = new byte[width * height];

        for (int t = 0; t < count; t++)
        {
            int line = paletteLineForTile?.Invoke(t) ?? 0;
            int ox = (t % tilesPerRow) * 8, oy = (t / tilesPerRow) * 8;
            var tile = tileData.Slice(t * BytesPerTile, BytesPerTile);
            for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                byte b = tile[y * 4 + x / 2];
                int nib = (x & 1) == 0 ? b >> 4 : b & 0xF;
                pixels[(oy + y) * width + ox + x] = (byte)(line * 16 + nib);
            }
        }
        return (width, height, pixels);
    }

    /// <summary>Inverse of <see cref="ToSheet"/>: rebuilds raw tile bytes from sheet pixels (palette line discarded).</summary>
    public static byte[] FromSheet(ReadOnlySpan<byte> pixels, int width, int tileCount)
    {
        int tilesPerRow = width / 8;
        var data = new byte[tileCount * BytesPerTile];
        for (int t = 0; t < tileCount; t++)
        {
            int ox = (t % tilesPerRow) * 8, oy = (t / tilesPerRow) * 8;
            for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x += 2)
            {
                int hi = pixels[(oy + y) * width + ox + x] & 0xF;
                int lo = pixels[(oy + y) * width + ox + x + 1] & 0xF;
                data[t * BytesPerTile + y * 4 + x / 2] = (byte)((hi << 4) | lo);
            }
        }
        return data;
    }
}
