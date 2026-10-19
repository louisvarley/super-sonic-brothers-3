using S2Port.Extraction.Levels;
using SonicBros3.Logic;

namespace SonicBros3.Runtime;

/// <summary>
/// Plane B nametable (VRAM $E000, 64x32 cells), maintained the way the original draws it:
/// DrawInitialBG (ROM 0x1050E) at level start, then LoadTilesAsYouMove (0xFB16) each frame, which only redraws the
/// rows/columns that each background layer's scroll flags ask for. Background rows that no layer redraws keep their
/// initial contents and wrap every 512 px horizontally, as on hardware.
/// A block drawn at world (x, y) lands at cell ((x &amp; $1F0) >> 3, (y &amp; $F0) >> 3) (CalcBlockVRAMPos, 0x10494).
/// </summary>
public sealed class BackgroundPlane(LevelData level, Ram ram)
{
    public const int Columns = 64, Rows = 32;
    public ushort[] Cells { get; } = new ushort[Columns * Rows];

    // Background layer positions (x at +0, y at +4) and their redraw flag bytes.
    private const int Layer1 = Vars.CameraBgYPos - 4, Layer2 = Vars.CameraBg2XPos, Layer3 = Vars.CameraBg3XPos;

    private int X(int layer) => (short)ram.U16(layer);
    private int Y(int layer) => (short)ram.U16(layer + 4);

    /// <summary>DrawInitialBG (0x1050E): 16 rows of 32 blocks from 16 px above layer 1's position.</summary>
    public void DrawInitial()
    {
        for (int row = 0; row < 16; row++) DrawRow(X(Layer1), Y(Layer1) - 16 + row * 16, 32);
    }

    /// <summary>The background part of LoadTilesAsYouMove (0xFB16), for the current zone.</summary>
    public void Update()
    {
        int zone = ram.U8(Vars.CurrentZoneAndAct);
        UpdateLayer1();
        if (zone == 0x1B) throw new UnportedException(0xFDBC, "zone 1B background layer 2 drawing");
        BandColumns(Layer2, Vars.ScrollFlagsBg2, 0x70);
        if (zone is 0x13 or 0x14) { MarbleLayers(); return; }
        if (zone is 0x0D) throw new UnportedException(0xFE4C, $"zone {zone:X2} background layer 3 drawing");
        BandColumns(Layer3, Vars.ScrollFlagsBg3, 0x40);
    }

    /// <summary>
    /// Which background layer each 16-px row belongs to in Marble Zone (0xFFDC, indexed by (layer-1 y - $200) / 16):
    /// the redraw-flag bit of that layer's left edge (2 layer 1, 4 layer 2, 6 layer 3; 0 = sky rows, never redrawn).
    /// </summary>
    private static readonly byte[] MarbleRows = BuildMarbleRows();

    private static byte[] BuildMarbleRows()
    {
        var t = new byte[0x80];
        int i = 0;
        for (; i < 6; i++) t[i] = 0;
        for (; i < 8; i++) t[i] = 6;
        for (; i < 17; i++) t[i] = 4;
        for (; i < 97; i++) t[i] = 2;
        return t;   // the rest reads past the table (rows below any Marble Zone background)
    }

    /// <summary>The camera position of the layer for a row's bit (0xFF56: layers 1, 1, 2, 3).</summary>
    private int LayerFor(int bit) => bit switch { 4 => Layer2, 6 => Layer3, _ => Layer1 };

    /// <summary>
    /// 0x1003E (zones $13/$14): every layer's redraw flags are in layer 3's byte. A vertical scroll redraws the top or
    /// bottom row with the x of that row's layer; a horizontal one redraws, row by row, the left or right edge block of
    /// the rows whose layer moved.
    /// </summary>
    private void MarbleLayers()
    {
        int f = ram.U8(Vars.ScrollFlagsBg3);
        if (f == 0) return;
        int bgY = Y(Layer1);
        int d4 = -16;
        bool row = false;
        if ((f & 1) != 0) { f &= ~1; row = true; }
        else if ((f & 2) != 0) { f &= ~2; d4 = 0xE0; row = true; }
        if (row)
        {
            int bit = MarbleRows[(((bgY - 0x200 + d4) & 0x7F0) >> 4) + 1 & 0x7F];
            int layer = LayerFor(bit);
            DrawRow(X(layer) - 16, Y(layer) + d4, 22);
        }
        ram.W8(Vars.ScrollFlagsBg3, f);
        if (f == 0) return;
        int d5 = -16;
        int right = f & 0xA8;
        if (right != 0) { f = right >> 1; d5 = 0x140; }
        int start = ((bgY - 0x200) & 0x7F0) >> 4;
        for (int r = 0; r < 16; r++)
        {
            int bit = MarbleRows[(start + r) & 0x7F];
            if (((f >> bit) & 1) != 0)
            {
                int layer = LayerFor(bit);
                DrawBlock(X(layer) + d5, Y(layer) - 16 + r * 16);
            }
        }
        ram.W8(Vars.ScrollFlagsBg3, 0);
    }

    /// <summary>0xFC7C: layer 1 rows/columns at the screen edges (bits 0-3), full-width rows (bits 4-7).</summary>
    private void UpdateLayer1()
    {
        int f = ram.U8(Vars.ScrollFlagsBg);
        if (f == 0) return;
        int x = X(Layer1), y = Y(Layer1);
        if ((f & 0x01) != 0) DrawRow(x - 16, y - 16, 22);
        if ((f & 0x02) != 0) DrawRow(x - 16, y + 0xE0, 22);
        if ((f & 0x04) != 0) DrawColumn(x - 16, y - 16, 16);
        if ((f & 0x08) != 0) DrawColumn(x + 0x140, y - 16, 16);
        if ((f & 0x10) != 0) DrawRow(0, y - 16, 32);
        if ((f & 0x20) != 0) DrawRow(0, y + 0xE0, 32);
        if ((f & 0x40) != 0) DrawRow(x - 16, y - 16, 32);
        if ((f & 0x80) != 0) DrawRow(x - 16, y + 0xE0, 32);
        ram.W8(Vars.ScrollFlagsBg, 0);
    }

    /// <summary>0xFD4C / 0xFE46: a layer redraws 3-block columns of its band (bit 0 left edge, bit 1 right edge).</summary>
    private void BandColumns(int layer, int flags, int bandY)
    {
        int f = ram.U8(flags);
        if (f == 0) return;
        int x = X(layer), y = Y(layer) + bandY;
        if ((f & 0x01) != 0) DrawColumn(x - 16, y, 3);
        if ((f & 0x02) != 0) DrawColumn(x + 0x140, y, 3);
        ram.W8(flags, f & ~0x03);
    }

    private void DrawRow(int x, int y, int blocks)
    {
        for (int i = 0; i < blocks; i++) DrawBlock(x + i * 16, y);
    }

    private void DrawColumn(int x, int y, int blocks)
    {
        for (int i = 0; i < blocks; i++) DrawBlock(x, y + i * 16);
    }

    /// <summary>DrawBlock (0x10382): the 16x16 block at world (x, y) from the background layout, with chunk flips.</summary>
    private void DrawBlock(int x, int y)
    {
        int wx = ((x & 0xFFFF) % (LevelData.LayoutWidth * 128)) & ~15;
        int wy = (((y * 2) & 0xF00) >> 1) | (y & 0x70);
        int col = (x & 0x1F0) >> 3, row = (y & 0xF0) >> 3;
        for (int dy = 0; dy < 2; dy++)
        for (int dx = 0; dx < 2; dx++)
            Cells[(row + dy) * Columns + col + dx] = Vdp.PatternAt(level, level.Background, wx + dx * 8, wy + dy * 8);
    }
}
