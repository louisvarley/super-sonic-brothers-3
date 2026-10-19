namespace SonicBros3.Smb3;

/// <summary>
/// The parts of the NES PPU the SMB3 screens need: nametable RAM with the cartridge's mirroring, palette RAM, and
/// SMB3's video update streams (the Video_Upd_Table format: vaddr hi/lo, a count byte with VU_VERT $80 and VU_REPEAT
/// $40, the data; a 0 high byte ends the stream). Rendering is done by <see cref="NesPresenter"/>.
/// </summary>
public sealed class NesPpu
{
    /// <summary>$2000-$2FFF; with horizontal mirroring $2400 = $2000 and $2C00 = $2800 (vertically stacked screens).</summary>
    public byte[] Nametables { get; } = new byte[0x1000];
    public byte[] Palette { get; } = new byte[32];
    public bool HorizontalMirroring { get; set; } = true;

    private int Map(int a)
    {
        a &= 0xFFF;
        return HorizontalMirroring ? a & ~0x400 : a & ~0x800;
    }

    public void Write(int address, int value)
    {
        address &= 0x3FFF;
        if (address >= 0x3F00)
        {
            int i = address & 0x1F;
            if ((i & 3) == 0) i &= 0x0F;   // $3F10/$14/$18/$1C mirror $3F00/$04/$08/$0C
            Palette[i] = (byte)(value & 0x3F);
        }
        else if (address >= 0x2000) Nametables[Map(address - 0x2000)] = (byte)value;
    }

    public int Read(int address) => (address & 0x3FFF) >= 0x3F00 ? Palette[address & 0x1F] : Nametables[Map(address - 0x2000)];

    /// <summary>Clears the nametables to <paramref name="tile"/> and the attributes to 0.</summary>
    public void Clear(int tile)
    {
        for (int nt = 0; nt < 4; nt++)
        for (int i = 0; i < 0x400; i++) Nametables[Map(nt * 0x400 + i)] = (byte)(i < 0x3C0 ? tile : 0);
    }

    /// <summary>Tile at (column, row) of nametable <paramref name="nt"/> (0-3: $2000, $2400, $2800, $2C00).</summary>
    public int Tile(int nt, int col, int row) => Nametables[Map(nt * 0x400 + row * 32 + col)];

    /// <summary>The background palette (0-3) of a cell, from the attribute table.</summary>
    public int CellPalette(int nt, int col, int row)
    {
        int a = Nametables[Map(nt * 0x400 + 0x3C0 + (row >> 2) * 8 + (col >> 2))];
        return (a >> (((row >> 1) & 1) * 4 + ((col >> 1) & 1) * 2)) & 3;
    }

    public void SetCellPalette(int nt, int col, int row, int pal)
    {
        int i = Map(nt * 0x400 + 0x3C0 + (row >> 2) * 8 + (col >> 2));
        int shift = ((row >> 1) & 1) * 4 + ((col >> 1) & 1) * 2;
        Nametables[i] = (byte)((Nametables[i] & ~(3 << shift)) | ((pal & 3) << shift));
    }

    /// <summary>Video_Misc_Updates: runs one stream from <paramref name="read"/> starting at <paramref name="p"/>.</summary>
    public void RunStream(Func<int, byte> read, int p)
    {
        while (true)
        {
            int hi = read(p);
            if (hi == 0) return;
            int address = (hi << 8) | read(p + 1);
            int c = read(p + 2);
            p += 3;
            bool vertical = (c & 0x80) != 0, repeat = (c & 0x40) != 0;
            int n = c & 0x3F;
            if (n == 0) n = 64;
            for (int i = 0; i < n; i++)
            {
                Write(address, read(repeat ? p : p + i));
                address += vertical ? 32 : 1;
            }
            p += repeat ? 1 : n;
        }
    }
}
