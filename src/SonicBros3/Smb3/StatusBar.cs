using SonicBros3.Frontend;

namespace SonicBros3.Smb3;

/// <summary>
/// SMB3's status bar: the StatusBar stream (prg030; the $2B00 copy for the map and levels, the $2300 copy for the
/// horizontally mirrored bonus games) and the values StatusBar_Fill_* write into it: the world number, the player
/// badge ([S] redrawn over [M]: CHR $74/$75 of bank $5D), lives, score (SMB3's last digit is a fixed 0) and the coin
/// counter, which counts Sonic's rings. The cards are drawn by the screens as sprites (Sonic monitor icons).
/// </summary>
public static class StatusBar
{
    /// <summary>The two copies of the stream in bank 30.</summary>
    public const int Stream2B00 = 0x811B, Stream2300 = 0x819A;

    public static void Draw(NesPpu ppu, NesPresenter nes, MapProgress progress, int stream)
    {
        var a = nes.Assets;
        ppu.RunStream(x => a.Prg(30, x), stream);
        int b = stream == Stream2300 ? 0x2300 : 0x2B00;
        ppu.Write(b + 0x26, 0xF0 + progress.World);
        ppu.Write(b + 0x42, 0x74);
        ppu.Write(b + 0x43, 0x75);                                  // [S]
        Values(ppu, progress, b);
        nes.TileOverrides[(0x5D, 0x34)] = Glyph(SBadge, 0);
        nes.TileOverrides[(0x5D, 0x35)] = Glyph(SBadge, 8);
    }

    /// <summary>Lives, score and rings (StatusBar_Fill_Lives / Score / Coins), and no timer.</summary>
    public static void Values(NesPpu ppu, MapProgress progress, int b)
    {
        int lives = Math.Min(progress.Lives, 99);
        ppu.Write(b + 0x45, lives >= 10 ? 0xF0 + lives / 10 : 0xFE);
        ppu.Write(b + 0x46, 0xF0 + lives % 10);
        string score = Math.Min(progress.Score / 10, 999999).ToString("D6");
        for (int i = 0; i < 6; i++) ppu.Write(b + 0x48 + i, 0xF0 + score[i] - '0');
        int rings = Math.Min(progress.Rings, 99);
        ppu.Write(b + 0x32, rings >= 10 ? 0xF0 + rings / 10 : 0xFE);
        ppu.Write(b + 0x33, 0xF0 + rings % 10);
        for (int i = 0; i < 3; i++) ppu.Write(b + 0x51 + i, 0xFE);
    }

    /// <summary>NES y of the card slots' top (status bar row 1) for a stream at <paramref name="ntRow"/>.</summary>
    public static int CardSlotY(int ntRow) => (ntRow + 1) * 8;

    /// <summary>NES x of card slot <paramref name="i"/> (0-2).</summary>
    public static int CardSlotX(int i) => (22 + i * 3) * 8;

    // 16x8 pixel art (values 0-3): '.' 0, 'w' 1, 'b' 2, 'r' 3.
    private static readonly string[] SBadge =
    [
        "rrrrrrrrrrrrrrrr",
        "rwwwwwwwwwwwwwwr",
        "wwrrwwbbbbbwrrww",
        "wrrrwbbwwwwwrrrw",
        "wrrrwwbbbbwwrrrw",
        "wrrrwwwwwbbwrrrw",
        "wwrrwbbbbbwwrrww",
        "rwwwwwwwwwwwwwwr",
    ];

    /// <summary>An 8x8 tile of a pixel-art string block (values ".wbr" = 0-3), from column <paramref name="x0"/> and
    /// row <paramref name="y0"/>.</summary>
    public static byte[] Glyph(string[] rows, int x0, int y0 = 0)
    {
        var px = new byte[64];
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
            px[y * 8 + x] = (byte)".wbr".IndexOf(rows[y0 + y][x0 + x]);
        return px;
    }
}
