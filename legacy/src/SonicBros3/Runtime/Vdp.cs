using SonicBros3.Logic;
using S2Port.Extraction.Levels;

namespace SonicBros3.Runtime;

/// <summary>
/// Software stand-in for the Mega Drive VDP's plane output: a 320x224 buffer of CRAM indices (0-63).
/// Planes are drawn in hardware priority order: backdrop, BG low, FG low, BG high, FG high.
/// Sprites, per-line scroll and shadow/highlight are not implemented yet.
/// </summary>
public sealed class Vdp
{
    public const int Width = 320, Height = 224;

    /// <summary>CRAM index per pixel.</summary>
    public byte[] Frame { get; } = new byte[Width * Height];

    /// <summary>Pixels drawn by a high-priority plane tile (low-priority sprites go behind these).</summary>
    private readonly bool[] _highPlane = new bool[Width * Height];

    /// <summary>64 CRAM words (0000 BBB0 GGG0 RRR0).</summary>
    public ushort[] Cram { get; } = new ushort[64];
    /// <summary>First line drawn with <see cref="CramBelow"/> (a mid-screen palette change by H-interrupt), or -1.</summary>
    public int SplitLine { get; set; } = -1;
    public ushort[] CramBelow { get; } = new ushort[64];

    /// <summary>The frame as RGBA (0xAABBGGRR), honouring the palette split.</summary>
    public void ToRgba(uint[] dest)
    {
        Span<uint> above = stackalloc uint[64], below = stackalloc uint[64];
        for (int i = 0; i < 64; i++)
        {
            above[i] = Rgba(Cram[i]);
            below[i] = Rgba(CramBelow[i]);
        }
        for (int y = 0; y < Height; y++)
        {
            var pal = SplitLine >= 0 && y >= SplitLine ? below : above;
            int row = y * Width;
            for (int x = 0; x < Width; x++) dest[row + x] = pal[Frame[row + x]];
        }

        static uint Rgba(ushort c)
        {
            var (r, g, b) = new S2Port.Extraction.Graphics.MdColor(c).ToRgb();
            return 0xFF000000u | ((uint)b << 16) | ((uint)g << 8) | r;
        }
    }

    /// <summary>Backdrop colour index (VDP register 7). Sonic 2 sets $8720: line 2, colour 0.</summary>
    public int BackdropIndex { get; set; } = 0x20;

    public void RenderLevel(LevelData level, int fgX, int fgY, int bgX, int bgY, bool drawBackground = true)
    {
        Array.Fill(Frame, (byte)BackdropIndex);
        if (drawBackground) DrawPlane(level, level.Background, bgX, bgY, highPriority: false);
        DrawPlane(level, level.Foreground, fgX, fgY, highPriority: false);
        if (drawBackground) DrawPlane(level, level.Background, bgX, bgY, highPriority: true);
        DrawPlane(level, level.Foreground, fgX, fgY, highPriority: true);
    }

    /// <summary>
    /// Renders using the game's own scroll RAM, as the hardware does: a per-line horizontal scroll buffer
    /// ($E000: per line FG word, BG word, both negated camera X) and full-screen vertical scroll ($F616 FG, $F618 BG).
    /// </summary>
    /// <param name="planeAOverride">64x32 nametable cells drawn instead of the level where set (0xFFFF = level): the title card.</param>
    public void RenderLevel(LevelData level, Logic.Ram ram, bool drawBackground = true, BackgroundPlane? planeB = null,
        ushort[]? planeAOverride = null)
    {
        _override = planeAOverride;
        Array.Fill(Frame, (byte)BackdropIndex);
        Array.Fill(_highPlane, false);
        int vFg = (short)ram.U16(Vars.VscrollFactorFg), vBg = (short)ram.U16(Vars.VscrollFactorBg);
        int Fg(int line) => -(short)ram.U16(Vars.HorizScrollBuf + line * 4);
        int Bg(int line) => -(short)ram.U16(Vars.HorizScrollBuf + 0x2 + line * 4);
        if (drawBackground) DrawBg(false);
        DrawPlaneLines(level, level.Foreground, Fg, vFg, false);
        if (drawBackground) DrawBg(true);
        DrawPlaneLines(level, level.Foreground, Fg, vFg, true);
        DrawSprites(level.TileSource, ram, masking: false);

        void DrawBg(bool high)
        {
            if (planeB is null) DrawPlaneLines(level, level.Background, Bg, vBg, high);
            else DrawNametableLines(level, planeB.Cells, Bg, vBg, high);
        }
    }

    /// <summary>
    /// Sprites from the sprite table in RAM ($F800, as DMA'd to the VDP's SAT): followed by link from entry 0,
    /// at most 80 sprites, 20 sprites / 320 pixels per line (H40). Earlier sprites are in front of later ones;
    /// a low-priority sprite pixel is hidden by an opaque high-priority plane pixel. Tiles are column-major.
    /// </summary>
    private void DrawSprites(byte[] tiles, Logic.Ram ram, bool masking)
    {
        var drawn = new bool[Width * Height];
        var masked = new bool[Height];
        var lineSprites = new int[Height];
        var linePixels = new int[Height];
        int index = 0;
        for (int n = 0; n < 80; n++)
        {
            int e = 0xF800 + index * 8;
            int sy = (ram.U16(e) & 0x1FF) - 128;
            int size = ram.U8(e + 2);
            int link = ram.U8(e + 3) & 0x7F;
            int pattern = ram.U16(e + 4);
            int sx = (ram.U16(e + 6) & 0x1FF) - 128;
            int wCells = ((size >> 2) & 3) + 1, hCells = (size & 3) + 1;
            bool fx = (pattern & 0x800) != 0, fy = (pattern & 0x1000) != 0, high = (pattern & 0x8000) != 0;
            int line0 = (pattern >> 13) & 3;
            bool maskSprite = masking && (ram.U16(e + 6) & 0x1FF) == 0;
            for (int y = 0; y < hCells * 8; y++)
            {
                int py = sy + y;
                if ((uint)py >= Height) continue;
                if (maskSprite && lineSprites[py] > 0) masked[py] = true;
                if (masked[py] || lineSprites[py] >= 20 || linePixels[py] >= 320) continue;
                int cy = fy ? hCells * 8 - 1 - y : y;
                for (int x = 0; x < wCells * 8; x++)
                {
                    int px = sx + x;
                    if (linePixels[py] + x >= 320) break;
                    if ((uint)px >= Width) continue;
                    int cx = fx ? wCells * 8 - 1 - x : x;
                    int tile = (pattern & 0x7FF) + (cx >> 3) * hCells + (cy >> 3);
                    if ((tile + 1) * 32 > tiles.Length) continue;
                    byte b = tiles[tile * 32 + (cy & 7) * 4 + (cx & 7) / 2];
                    int nib = (cx & 1) == 0 ? b >> 4 : b & 0xF;
                    int i = py * Width + px;
                    if (nib == 0 || drawn[i]) continue;
                    drawn[i] = true;
                    if (!high && _highPlane[i]) continue;
                    Frame[i] = (byte)(line0 * 16 + nib);
                }
            }
            for (int y = 0; y < hCells * 8; y++)
            {
                int py = sy + y;
                if ((uint)py >= Height) continue;
                lineSprites[py]++;
                linePixels[py] += wCells * 8;
            }
            if (link == 0) break;
            index = link;
        }
    }

    /// <summary>A 64x32-cell nametable plane, wrapping at 512x256 px like the hardware.</summary>
    private void DrawNametableLines(LevelData level, ushort[] cells, Func<int, int> scrollX, int scrollY, bool highPriority)
    {
        var tiles = level.TileSource;
        for (int sy = 0; sy < Height; sy++)
        {
            int py = Mod(sy + scrollY, 256);
            int sx0 = scrollX(sy);
            for (int x = 0; x < Width; x++)
            {
                int px = Mod(sx0 + x, 512);
                ushort pattern = cells[(py >> 3) * 64 + (px >> 3)];
                if (((pattern & 0x8000) != 0) != highPriority) continue;
                int tile = pattern & 0x7FF;
                if ((tile + 1) * 32 > tiles.Length) continue;
                int row = (pattern & 0x1000) != 0 ? 7 - (py & 7) : py & 7;
                int col = (pattern & 0x800) != 0 ? 7 - (px & 7) : px & 7;
                byte b = tiles[tile * 32 + row * 4 + col / 2];
                int nib = (col & 1) == 0 ? b >> 4 : b & 0xF;
                if (nib != 0)
                {
                    Frame[sy * Width + x] = (byte)((((pattern >> 13) & 3) * 16) + nib);
                    if (highPriority) _highPlane[sy * Width + x] = true;
                }
            }
        }
    }

    private ushort[]? _override;

    /// <summary>
    /// A screen that is not a level (title, menus): planes A and B straight from VRAM nametables (64x32 cells), the
    /// per-line horizontal scroll buffer at RAM $E000 and full-screen vertical scroll ($F616 / $F618), then sprites
    /// from RAM $F800 with sprite masking (a sprite at x = 0 hides the later sprites on its lines, once an earlier
    /// sprite is on the line).
    /// </summary>
    public void RenderScreen(byte[] vram, Logic.Ram ram, int planeA = 0xC000, int planeB = 0xE000)
    {
        _override = null;
        Array.Fill(Frame, (byte)BackdropIndex);
        Array.Fill(_highPlane, false);
        int vFg = (short)ram.U16(Vars.VscrollFactorFg), vBg = (short)ram.U16(Vars.VscrollFactorBg);
        int Fg(int line) => -(short)ram.U16(Vars.HorizScrollBuf + line * 4);
        int Bg(int line) => -(short)ram.U16(Vars.HorizScrollBuf + 0x2 + line * 4);
        Func<int, int?> lineA = LineScrollA ?? (_ => vFg), lineB = LineScrollB ?? (_ => vBg);
        var a = Nametable(vram, planeA);
        var b = Nametable(vram, planeB);
        DrawCells(vram, b, Bg, lineB, false);
        DrawCells(vram, a, Fg, lineA, false);
        DrawCells(vram, b, Bg, lineB, true);
        DrawCells(vram, a, Fg, lineA, true);
        DrawSprites(vram, ram, masking: true);
    }

    /// <summary>
    /// Per-line vertical scroll for <see cref="RenderScreen"/> (plane y shown on a screen line, null = the plane is
    /// not drawn there), in place of the full-screen VSRAM values: the SMB3 screens use it for the NES's mid-frame
    /// scroll split. Not something the Mega Drive VDP does (it scrolls by 2-cell columns at best); null = hardware.
    /// </summary>
    public Func<int, int?>? LineScrollA { get; set; }
    public Func<int, int?>? LineScrollB { get; set; }

    private static ushort[] Nametable(byte[] vram, int address)
    {
        var cells = new ushort[64 * 32];
        for (int i = 0; i < cells.Length; i++) cells[i] = (ushort)((vram[(address + i * 2) & 0xFFFF] << 8) | vram[(address + i * 2 + 1) & 0xFFFF]);
        return cells;
    }

    private void DrawCells(byte[] tiles, ushort[] cells, Func<int, int> scrollX, Func<int, int?> scrollY, bool highPriority)
    {
        for (int sy = 0; sy < Height; sy++)
        {
            if (scrollY(sy) is not { } vy) continue;
            int py = Mod(sy + vy, 256);
            int sx0 = scrollX(sy);
            for (int x = 0; x < Width; x++)
            {
                int px = Mod(sx0 + x, 512);
                ushort pattern = cells[(py >> 3) * 64 + (px >> 3)];
                if (((pattern & 0x8000) != 0) != highPriority) continue;
                int tile = pattern & 0x7FF;
                int row = (pattern & 0x1000) != 0 ? 7 - (py & 7) : py & 7;
                int col = (pattern & 0x800) != 0 ? 7 - (px & 7) : px & 7;
                byte bt = tiles[tile * 32 + row * 4 + col / 2];
                int nib = (col & 1) == 0 ? bt >> 4 : bt & 0xF;
                if (nib == 0) continue;
                Frame[sy * Width + x] = (byte)((((pattern >> 13) & 3) * 16) + nib);
                if (highPriority) _highPlane[sy * Width + x] = true;
            }
        }
    }

    /// <summary>Black screen (the palette is black while the next level loads).</summary>
    public void RenderBlack()
    {
        Array.Fill(Frame, (byte)0);
        Array.Clear(Cram);
    }

    private void DrawPlaneLines(LevelData level, byte[] plane, Func<int, int> scrollX, int scrollY, bool highPriority)
    {
        var over = plane == level.Foreground ? _override : null;
        const int planeW = LevelData.LayoutWidth * 128, planeH = LevelData.LayoutHeight * 128;
        var tiles = level.TileSource;
        for (int sy = 0; sy < Height; sy++)
        {
            int py = Mod(sy + scrollY, planeH);
            int sx0 = scrollX(sy);
            int x = 0;
            while (x < Width)
            {
                int px = Mod(sx0 + x, planeW);
                int run = Math.Min(8 - (px & 7), Width - x);
                ushort pattern = PatternAt(level, plane, px, py);
                if (over is not null && over[((py & 0xFF) >> 3) * 64 + ((px & 0x1FF) >> 3)] is var o && o != 0xFFFF) pattern = o;
                int tile = pattern & 0x7FF;
                if (((pattern & 0x8000) != 0) == highPriority && (tile + 1) * 32 <= tiles.Length)
                {
                    int line = (pattern >> 13) & 3;
                    bool fx = (pattern & 0x800) != 0, fy = (pattern & 0x1000) != 0;
                    int row = fy ? 7 - (py & 7) : py & 7;
                    for (int i = 0; i < run; i++)
                    {
                        int col = (px + i) & 7;
                        if (fx) col = 7 - col;
                        byte b = tiles[tile * 32 + row * 4 + col / 2];
                        int nib = (col & 1) == 0 ? b >> 4 : b & 0xF;
                        if (nib != 0)
                        {
                            Frame[sy * Width + x + i] = (byte)(line * 16 + nib);
                            if (highPriority) _highPlane[sy * Width + x + i] = true;
                        }
                    }
                }
                x += run;
            }
        }
    }

    /// <summary>
    /// Draws tiles of one priority from a layout plane, with the camera at (scrollX, scrollY) in plane pixels.
    /// The plane wraps at 128x16 chunks, matching the layout's size.
    /// </summary>
    private void DrawPlane(LevelData level, byte[] plane, int scrollX, int scrollY, bool highPriority)
    {
        const int planeW = LevelData.LayoutWidth * 128, planeH = LevelData.LayoutHeight * 128;
        var tiles = level.TileSource;
        int firstTileX = FloorDiv(scrollX, 8), firstTileY = FloorDiv(scrollY, 8);
        int offX = Mod(scrollX, 8), offY = Mod(scrollY, 8);

        for (int ty = 0; ty <= Height / 8; ty++)
        for (int tx = 0; tx <= Width / 8; tx++)
        {
            int px = Mod((firstTileX + tx) * 8, planeW), py = Mod((firstTileY + ty) * 8, planeH);
            ushort pattern = PatternAt(level, plane, px, py);
            if (((pattern & 0x8000) != 0) != highPriority) continue;
            int tile = pattern & 0x7FF;
            if ((tile + 1) * 32 > tiles.Length) continue;
            int line = (pattern >> 13) & 3;
            bool fx = (pattern & 0x800) != 0, fy = (pattern & 0x1000) != 0;
            int sx0 = tx * 8 - offX, sy0 = ty * 8 - offY;
            for (int y = 0; y < 8; y++)
            {
                int sy = sy0 + y;
                if ((uint)sy >= Height) continue;
                int row = fy ? 7 - y : y;
                for (int x = 0; x < 8; x++)
                {
                    int sx = sx0 + x;
                    if ((uint)sx >= Width) continue;
                    int col = fx ? 7 - x : x;
                    byte b = tiles[tile * 32 + row * 4 + col / 2];
                    int nib = (col & 1) == 0 ? b >> 4 : b & 0xF;
                    if (nib != 0) Frame[sy * Width + sx] = (byte)(line * 16 + nib);
                }
            }
        }
    }

    /// <summary>Resolves the 8x8 tile pattern word at a plane pixel: layout -> chunk -> block -> tile, applying block flips.</summary>
    public static ushort PatternAt(LevelData level, byte[] plane, int px, int py)
    {
        int chunk = plane[(py >> 7) * LevelData.LayoutWidth + (px >> 7)];
        if (chunk >= level.LoadedChunkCount) return 0;
        ushort cw = level.ChunkWords[chunk * 64 + ((py >> 4) & 7) * 8 + ((px >> 4) & 7)];
        int block = cw & 0x3FF;
        if (block >= level.BlockCount) return 0;
        bool bfx = (cw & 0x400) != 0, bfy = (cw & 0x800) != 0;
        int qx = (px >> 3) & 1, qy = (py >> 3) & 1;
        if (bfx) qx ^= 1;
        if (bfy) qy ^= 1;
        ushort pw = level.BlockWords[block * 4 + qy * 2 + qx];
        // A block flip mirrors each tile too: toggle the tile's own flip bits.
        if (bfx) pw ^= 0x800;
        if (bfy) pw ^= 0x1000;
        return pw;
    }

    private static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);
    private static int Mod(int a, int m) => ((a % m) + m) % m;
}
