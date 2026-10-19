using System.Text.Json;
using S2Port.Extraction;
using S2Port.Extraction.Smb3;

namespace SonicBros3.Smb3;

/// <summary>
/// The parts of the player's own smb3.nes the title screen and the world map use, copied into the game's data file
/// (sb3/): whole 8 KiB PRG banks, read at the disassembly's CPU addresses (captainsouthbird's labels; every address
/// used here was checked against this image), and the 1 KiB CHR banks the screens select. Nothing is converted at
/// build time: the screens run SMB3's own data (video update streams, map layout, tile layouts, palettes) as it is.
/// </summary>
public sealed class Smb3Assets
{
    /// <summary>Bump when the copied set changes; sb3/assets.json records it with the image's SHA-1.</summary>
    public const int Version = 2;

    /// <summary>PRG banks: 10 (world intro box), 12 (map tiles, World 1 layout), 22 (the bonus room and N-Spade:
    /// tile layout, text, card tables), 24/25 (title), 27 (palettes), 30 (status bar, video update table).</summary>
    public static readonly int[] PrgBanks = [10, 12, 22, 24, 25, 27, 30];

    /// <summary>CHR 1 KiB banks: title BG $78-$7B and sprites $20/$21/$04/$7F; map BG $14-$17 and its animation
    /// frames $70-$75; status bar $5C-$5F; map sprites $20-$23; the bonus room BG $24/$25 (with $5E/$5F), N-Spade BG
    /// $5C-$5F and sprites $28-$2B / $5A (card flips, the cursor).</summary>
    public static readonly int[] ChrBanks =
        [0x04, 0x14, 0x15, 0x16, 0x17, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x28, 0x29, 0x2A, 0x2B, 0x5A, 0x5C, 0x5D, 0x5E,
         0x5F, 0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x78, 0x79, 0x7A, 0x7B, 0x7F];

    /// <summary>The bonus room's first screen of tile memory (27 rows x 16), as SMB3's own loader builds it.</summary>
    public byte[] BonusRoom { get; }

    private readonly Dictionary<int, byte[]> _prg = [];
    private readonly Dictionary<int, byte[]> _chr = [];

    public Smb3Assets(string dataDir)
    {
        foreach (int b in PrgBanks) _prg[b] = DataFile.ReadAllBytes(Path.Combine(dataDir, "sb3", $"prg_{b:D2}.bin"));
        foreach (int b in ChrBanks) _chr[b] = DataFile.ReadAllBytes(Path.Combine(dataDir, "sb3", $"chr_{b:X2}.bin"));
        BonusRoom = DataFile.ReadAllBytes(Path.Combine(dataDir, "sb3", "bonus_room.bin"));
    }

    public static bool Available(string dataDir) => DataFile.Exists(Path.Combine(dataDir, "sb3", "assets.json"));

    /// <summary>A byte of PRG bank <paramref name="bank"/> at CPU address <paramref name="address"/>.</summary>
    public byte Prg(int bank, int address) => _prg[bank][address & 0x1FFF];

    public int PrgWord(int bank, int address) => Prg(bank, address) | (Prg(bank, address + 1) << 8);

    /// <summary>The 16 bytes of tile <paramref name="tile"/> (0-63) of 1 KiB CHR bank <paramref name="bank"/>.</summary>
    public ReadOnlySpan<byte> Chr(int bank, int tile) => _chr[bank].AsSpan(tile * 16, 16);

    // ------------------------------------------------------------------ build

    public static bool IsCurrent(string dataDir, NesRom rom)
    {
        string path = Path.Combine(dataDir, "sb3", "assets.json");
        if (!DataFile.Exists(path)) return false;
        using var doc = JsonDocument.Parse(DataFile.ReadAllText(path));
        return doc.RootElement.GetProperty("version").GetInt32() == Version &&
               doc.RootElement.GetProperty("sha1").GetString() == rom.Sha1;
    }

    /// <summary>Copies the banks into <paramref name="dataDir"/> (a folder, or the data archive) if missing or old.</summary>
    public static void EnsureBuilt(NesRom rom, string dataDir, Action<string>? log = null)
    {
        if (IsCurrent(dataDir, rom)) return;
        if (!Extractor.IsArchivePath(dataDir)) { Build(rom, dataDir); }
        else
        {
            string work = DataFile.Unpack(dataDir);
            Build(rom, work);
            DataFile.Pack(work, dataDir);
        }
        log?.Invoke($"SMB3 title and map data -> {dataDir}");
    }

    private static void Build(NesRom rom, string outDir)
    {
        string dir = Path.Combine(outDir, "sb3");
        Directory.CreateDirectory(dir);
        foreach (int b in PrgBanks) File.WriteAllBytes(Path.Combine(dir, $"prg_{b:D2}.bin"), rom.Prg(b, 0, 0x2000));
        foreach (int b in ChrBanks)
        {
            var bank = new byte[0x400];
            for (int t = 0; t < 64; t++) rom.ChrTile(b, t).CopyTo(bank.AsSpan(t * 16));
            File.WriteAllBytes(Path.Combine(dir, $"chr_{b:X2}.bin"), bank);
        }
        File.WriteAllBytes(Path.Combine(dir, "bonus_room.bin"), BuildBonusRoom(rom));
        File.WriteAllText(Path.Combine(dir, "assets.json"), JsonSerializer.Serialize(new
        {
            version = Version,
            sha1 = rom.Sha1,
            source = "smb3.nes: PRG banks and CHR banks used by the title screen, the World 1 map and the card game " +
                     "(Smb3Assets); bonus_room.bin: BonusLayout_Typical's first screen of tile memory from SMB3's loader",
            prgBanks = PrgBanks,
            chrBanks = ChrBanks,
        }, Manifest.JsonOptions));
    }

    // smb3.fns (the disassembly assembled with nesasm; bank 22 matches this image apart from a few operands).
    private const int LevelLoad_TS15_TS16_TS17 = 0xC400, BonusLayout_Typical = 0xC429;
    private const int Level_LayPtr_AddrL = 0x61, Level_JctCtl = 0x3DE, Level_Tileset = 0x70A;

    /// <summary>
    /// The bonus room (Bonus_LayoutData entry 0, "Mario and Table", the only one SMB3 uses), built by running
    /// LevelLoad_TS15_TS16_TS17 on <see cref="Cpu6502"/> with the pages MO_NSpade's level entry sets (A000 = 26,
    /// C000 = 22, Level_Tileset = 15). It is one static screen: running the original is exact and simpler than
    /// porting its five one-off generators (floor, border, table, background, the player's tiles).
    /// </summary>
    private static byte[] BuildBonusRoom(NesRom rom)
    {
        var cpu = new Cpu6502(rom);
        cpu.Banks[1] = 26;
        cpu.Banks[2] = 22;
        cpu.Ram[Level_LayPtr_AddrL] = BonusLayout_Typical & 0xFF;
        cpu.Ram[Level_LayPtr_AddrL + 1] = BonusLayout_Typical >> 8;
        cpu.Ram[Level_JctCtl] = 0;
        cpu.Ram[Level_Tileset] = 15;
        cpu.Call(LevelLoad_TS15_TS16_TS17);
        return cpu.PrgRam[..Smb3Map.ScreenSize];
    }
}
