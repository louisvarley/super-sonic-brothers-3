namespace S2Port.Extraction.Smb3;

public sealed record Smb3Area(string Id, int Tileset, int LayoutBank, int LayoutAddress,
    int ObjectBank, int ObjectAddress);
public sealed record Smb3MapEntry(int Index, int Screen, int Row, int Column, int Tileset,
    int LayoutAddress, int ObjectAddress, string? AreaId);
public sealed record Smb3World(int Number, int[] Map, Smb3MapEntry[] Entries);
public sealed record Smb3Catalog(Smb3World[] Worlds, Smb3Area[] Areas)
{
    public static void RequireSupported(NesRom rom)
    {
        if (rom.Sha1 != Smb3Map.SupportedSha1)
            throw new InvalidDataException($"Unsupported SMB3 ROM SHA-1 {rom.Sha1}; expected {Smb3Map.SupportedSha1}.");
    }

    /// <summary>Read the map's actual pointer tables, then follow alternate-area headers to a fixed point.</summary>
    public static Smb3Catalog Read(NesRom rom)
    {
        RequireSupported(rom);
        var areas = new Dictionary<string, Smb3Area>(StringComparer.Ordinal);
        var pending = new Queue<Smb3Area>();
        int Word(int bank, int a) => rom.Prg(bank, a) | rom.Prg(bank, a + 1) << 8;
        string? Add(int tileset, int layout, int objects)
        {
            // Types 0 and 15 are map/bonus commands, not ordinary layout/object pointers.
            if (tileset is < 1 or > 14 || layout is < 0xA000 or >= 0xC000 ||
                objects is < 0xC000 or >= 0xE000) return null;
            int bank = rom.Prg(30, 0x83E9 + tileset);
            string id = $"ts{tileset:D2}-{bank:D2}-{layout:X4}-{objects:X4}";
            if (!areas.ContainsKey(id))
            {
                var area = new Smb3Area(id, tileset, bank, layout, 6, objects);
                areas.Add(id, area);
                pending.Enqueue(area);
            }
            return id;
        }

        var worlds = new List<Smb3World>();
        for (int world = 0; world < 8; world++)
        {
            int map = Word(12, 0xA598 + world * 2), nextMap = Word(12, 0xA598 + (world + 1) * 2);
            byte[] cells = rom.Prg(12, map, nextMap - map - 1);
            if (cells.Length == 0 || cells.Length % 144 != 0 || rom.Prg(12, nextMap - 1) != 0xFF)
                throw new InvalidDataException($"World {world + 1}: invalid map extent.");
            int init = Word(12, 0xB3CA + world * 2), rows = Word(12, 0xB3DC + world * 2);
            int cols = Word(12, 0xB3EE + world * 2), objects = Word(12, 0xB400 + world * 2);
            int layouts = Word(12, 0xB412 + world * 2);
            int count = cols - rows;
            if (count <= 0 || objects - cols != count || layouts - objects != count * 2)
                throw new InvalidDataException($"World {world + 1}: inconsistent map entry tables.");
            var entries = new List<Smb3MapEntry>();
            for (int i = 0; i < count; i++)
            {
                int rowType = rom.Prg(12, rows + i), col = rom.Prg(12, cols + i);
                int screen = col >> 4, row = (rowType >> 4) - 2, tileset = rowType & 15;
                // InitIndex is the original map's entry offset per horizontal screen.
                if (screen >= cells.Length / 144 || rom.Prg(12, init + screen) > i)
                    throw new InvalidDataException($"World {world + 1}: invalid screen index at entry {i}.");
                int layout = Word(12, layouts + i * 2), obj = Word(12, objects + i * 2);
                entries.Add(new(i, screen, row, col & 15, tileset, layout, obj, Add(tileset, layout, obj)));
            }
            worlds.Add(new(world + 1, cells.Select(b => (int)b).ToArray(), entries.ToArray()));
        }
        for (int world = 0; world < 8; world++)
        {
            Add(rom.Prg(26, 0xAB37 + world), Word(26, 0xAB17 + world * 2), Word(26, 0xAB27 + world * 2));
            Add(rom.Prg(26, 0xA92B + world), Word(26, 0xA90B + world * 2), Word(26, 0xA91B + world * 2));
        }
        while (pending.TryDequeue(out var area))
        {
            int alternateLayout = Word(area.LayoutBank, area.LayoutAddress);
            int alternateObjects = Word(area.LayoutBank, area.LayoutAddress + 2);
            int alternateTileset = rom.Prg(area.LayoutBank, area.LayoutAddress + 6) & 15;
            Add(alternateTileset, alternateLayout, alternateObjects);
        }
        return new(worlds.ToArray(), areas.Values.OrderBy(a => a.Id, StringComparer.Ordinal).ToArray());
    }
}

/// <summary>Execute the original tileset loader offline; this is not an NES gameplay emulator.</summary>
public sealed class Smb3DecodedArea
{
    public const string Seed = "F9E211D5F65DB10A6800";
    public byte[] TileMemory { get; }
    public byte[] Header { get; }
    public bool Vertical => (Header[6] & 0x10) != 0;
    public int Width => Header[4] & 15;
    public long Instructions { get; }
    public byte[] JunctionX { get; }
    public byte[] JunctionY { get; }

    public Smb3DecodedArea(NesRom rom, Smb3Area area)
    {
        Header = rom.Prg(area.LayoutBank, area.LayoutAddress, 9);
        var cpu = new Cpu6502(rom);
        cpu.Banks[1] = area.LayoutBank;
        cpu.Banks[2] = rom.Prg(30, 0x83D6 + area.Tileset);
        cpu.Ram[0x61] = (byte)area.LayoutAddress;
        cpu.Ram[0x62] = (byte)(area.LayoutAddress >> 8);
        cpu.Ram[0x70A] = (byte)area.Tileset;
        Convert.FromHexString(Seed).CopyTo(cpu.Ram, 0x781);
        int dispatch = 0x9A23 + area.Tileset * 2;
        int loader = rom.Prg(30, dispatch) | rom.Prg(30, dispatch + 1) << 8;
        cpu.Call(loader, maxSteps: 2_000_000);
        TileMemory = cpu.PrgRam[..Smb3Map.TileMemSize];
        JunctionX = cpu.PrgRam.AsSpan(0x1F64, 16).ToArray();
        JunctionY = cpu.PrgRam.AsSpan(0x1F54, 16).ToArray();
        Instructions = cpu.Steps;
    }

    public byte Tile(int row, int column) =>
        Vertical ? TileMemory[row * 16 + column]
            : TileMemory[(column >> 4) * Smb3Map.ScreenSize + row * 16 + (column & 15)];
}
