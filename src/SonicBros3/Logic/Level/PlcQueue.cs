using S2Port.Extraction.Levels;

namespace SonicBros3.Logic;

/// <summary>
/// The pattern load cue (PLC) queue: art requested during play is Nemesis-decompressed to VRAM a few tiles per frame,
/// and objects wait for it (e.g. the results wait until <see cref="Vars.PlcBuffer"/> is empty).
///
/// LoadPLC (0x17F2) appends a list's entries to the buffer (6 bytes each: the art's address, its VRAM address).
/// RunPLC_RAM (0x185E, in the level loop) starts the first entry: the tile count from the art's header goes to
/// $F6F8. ProcessDPLC (0x18D0, in VBlank) decompresses 3 tiles a frame; when the entry runs out, the queue moves
/// down one entry.
///
/// The port copies the art to VRAM at once (nothing can draw it before it is "ready" anyway) but keeps the queue and
/// its counters in RAM, frame for frame as the original does, so everything that waits on them waits as long. (The
/// decompressor's own registers, $F6E0-$F6F7, and the advancing art pointer are not modelled.)
/// </summary>
public sealed class PlcQueue(Ram ram, LevelData level)
{
    private const int EntrySize = 6, Entries = 16;
    /// <summary>$F6F8: tiles left in the entry being decompressed; $F6FA: tiles left in this frame's batch.</summary>
    private const int TilesLeft = 0xF6F8, BatchLeft = 0xF6FA;
    private const int TilesPerFrame = 3;

    /// <summary>Tile counts by art address (the Nemesis header's count, which RunPLC_RAM reads).</summary>
    private readonly Dictionary<uint, int> _tiles = [];

    /// <summary>LoadPLC (0x17F2): queue list <paramref name="id"/> (and copy its art).</summary>
    public void Load(int id)
    {
        level.LoadPlc(id);
        if (level.PlcListsPath is null) return;
        int slot = Vars.PlcBuffer;
        while (ram.U32(slot) != 0) slot += EntrySize;
        foreach (var (address, tiles, vramTile) in LevelLoader.PlcEntries(level.PlcListsPath, id))
        {
            if (slot >= Vars.PlcBuffer + Entries * EntrySize) break;
            _tiles[address] = tiles;
            ram.W32(slot, address);
            ram.W16(slot + 4, vramTile * 32);
            slot += EntrySize;
        }
    }

    /// <summary>RunPLC_RAM (0x185E): start the first entry when none is being decompressed.</summary>
    public void Run()
    {
        uint address = ram.U32(Vars.PlcBuffer);
        if (address == 0 || ram.U16(TilesLeft) != 0) return;
        ram.W16(TilesLeft, _tiles.GetValueOrDefault(address, 1) & 0x7FFF);
    }

    /// <summary>ProcessDPLC (0x18D0, VBlank): 3 tiles; at the entry's end, the queue moves down.</summary>
    public void Process()
    {
        if (ram.U16(TilesLeft) == 0) return;
        ram.W16(BatchLeft, TilesPerFrame);
        ram.W16(Vars.PlcBuffer + 4, ram.U16(Vars.PlcBuffer + 4) + TilesPerFrame * 32);
        while (true)
        {
            ram.W16(TilesLeft, ram.U16(TilesLeft) - 1);
            if (ram.U16(TilesLeft) == 0)
            {
                // 0x194E: 22 longs move down 6 bytes.
                for (int i = 0; i < 0x16 * 4; i++) ram.W8(Vars.PlcBuffer + i, ram.U8(Vars.PlcBuffer + EntrySize + i));
                return;
            }
            ram.W16(BatchLeft, ram.U16(BatchLeft) - 1);
            if (ram.U16(BatchLeft) == 0) return;
        }
    }
}
