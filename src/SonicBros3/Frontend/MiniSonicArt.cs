namespace SonicBros3.Frontend;

/// <summary>
/// The world map's mini Sonic: two 16x16 walking frames drawn for this game in the size and style of SMB3's map
/// Mario (black outline, front view), in Sonic's palette (line 0). As 2x2-cell sprites: 4 tiles per frame,
/// column-major (top left, bottom left, top right, bottom right).
/// </summary>
public static class MiniSonicArt
{
    // Line 0 colours: 1 black, 2/3 blues, 6 white, 10/11 skin, 12/13 reds.
    private static readonly Dictionary<char, int> Colour = new()
    {
        ['.'] = 0, ['k'] = 1, ['b'] = 2, ['B'] = 3, ['w'] = 6, ['p'] = 10, ['P'] = 11, ['r'] = 12, ['R'] = 13,
    };

    private static readonly string[][] Frames =
    [
        [
            "......kkk.......",
            "....kkBBBkk.....",
            "..kkBBBBBBBk....",
            ".kBBBBBBBBBBk...",
            "kBBBBkwwkwwBk...",
            ".kkBBwwkwwkwBk..",
            "..kBBwwkwwkwk...",
            ".kBBBpwwwwpk....",
            "kBBBBkppkppk....",
            ".kkBBkPppppk....",
            "..kkpkkPPPk.....",
            "...kpkBBkkpk....",
            "....kkBBBBkk....",
            "...krrkkkrrk....",
            "..krrrwk.krrrk..",
            "..kkkkk...kkkk..",
        ],
        [
            "......kkk.......",
            "....kkBBBkk.....",
            "..kkBBBBBBBk....",
            ".kBBBBBBBBBBk...",
            "kBBBBkwwkwwBk...",
            ".kkBBwwkwwkwBk..",
            "..kBBwwkwwkwk...",
            ".kBBBpwwwwpk....",
            "kBBBBkppkppk....",
            ".kkBBkPppppk....",
            "..kkkpPPPPkpk...",
            "...kpkBBBkkk....",
            "....kkBBBBk.....",
            "....krrkkrrk....",
            "...krrrwkrrrk...",
            "...kkkkk.kkkk...",
        ],
    ];

    /// <summary>All frames' tiles (32 bytes each), frame f at tile f * 4.</summary>
    public static byte[] Tiles()
    {
        var data = new byte[Frames.Length * 4 * 32];
        for (int f = 0; f < Frames.Length; f++)
        for (int y = 0; y < 16; y++)
        for (int x = 0; x < 16; x++)
        {
            int c = Colour[Frames[f][y][x]];
            int tile = f * 4 + (x >> 3) * 2 + (y >> 3);
            int i = tile * 32 + (y & 7) * 4 + (x & 7) / 2;
            data[i] |= (byte)((x & 1) == 0 ? c << 4 : c);
        }
        return data;
    }
}
