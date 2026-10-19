namespace S2Port.Extraction.Levels;

/// <summary>
/// Software render of a layout plane to 64-colour indexed pixels (index = line*16 + colour, 0 = transparent).
/// Priority is ignored. Tiles and blocks outside the loaded data render as transparent.
/// </summary>
public static class PlaneRenderer
{
    public static (int Width, int Height, byte[] Pixels) Render(LevelData level, byte[] plane, int widthChunks, int heightChunks)
    {
        int width = widthChunks * 128, height = heightChunks * 128;
        var px = new byte[width * height];
        for (int cy = 0; cy < heightChunks; cy++)
        for (int cx = 0; cx < widthChunks; cx++)
        {
            int chunk = plane[cy * LevelData.LayoutWidth + cx];
            if (chunk >= level.LoadedChunkCount) continue;
            for (int by = 0; by < 8; by++)
            for (int bx = 0; bx < 8; bx++)
            {
                ushort cw = level.ChunkWords[chunk * 64 + by * 8 + bx];
                int block = cw & 0x3FF;
                if (block >= level.BlockCount) continue;
                bool bfx = (cw & 0x400) != 0, bfy = (cw & 0x800) != 0;
                for (int q = 0; q < 4; q++)
                {
                    // Block flips mirror both the quadrant position and each tile's flip.
                    int qx = q & 1, qy = q >> 1;
                    if (bfx) qx ^= 1;
                    if (bfy) qy ^= 1;
                    ushort pw = level.BlockWords[block * 4 + q];
                    DrawTile(level, px, width,
                        cx * 128 + bx * 16 + qx * 8, cy * 128 + by * 16 + qy * 8,
                        pw, bfx, bfy);
                }
            }
        }
        return (width, height, px);
    }

    private static void DrawTile(LevelData level, byte[] px, int width, int ox, int oy, ushort pw, bool bfx, bool bfy)
    {
        int tile = pw & 0x7FF;
        if (tile >= level.TileCount) return;
        int line = (pw >> 13) & 3;
        bool fx = ((pw & 0x800) != 0) ^ bfx;
        bool fy = ((pw & 0x1000) != 0) ^ bfy;
        int baseAddr = tile * 32;
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
        {
            int sx = fx ? 7 - x : x, sy = fy ? 7 - y : y;
            byte b = level.TileSource[baseAddr + sy * 4 + sx / 2];
            int nib = (sx & 1) == 0 ? b >> 4 : b & 0xF;
            if (nib != 0) px[(oy + y) * width + ox + x] = (byte)(line * 16 + nib);
        }
    }
}
