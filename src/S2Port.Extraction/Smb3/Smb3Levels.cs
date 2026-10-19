namespace S2Port.Extraction.Smb3;

/// <summary>
/// One SMB3 level built as a Sonic zone by <see cref="Smb3ZoneBuilder"/>: where its layout and objects are, the tileset
/// the map loads it with, and that tileset's tile layout, attributes, background CHR bank (Level_BG_Pages1 by the
/// header's LEVEL4_BGBANK_INDEX; tiles $80-$FF are the animated PT2_Anim banks), palette set and song (Set 2B).
/// </summary>
public sealed record Smb3Level(
    string Name,
    int Zone,
    (int Bank, int Addr) Layout,
    (int Bank, int Addr) Objects,
    int Tileset,
    (int Bank, int Addr) TileLayout,
    (int Bank, int Addr) TileAttributes,
    int BgBank,
    int PaletteSet,
    int Song,
    int MusicId,
    int MusicAddress,
    string? Limitation = null);

public static class Smb3Levels
{
    /// <summary>World 1-1: Plains (prg015), BG bank $08, PalSet_Plains, the overworld theme (MUS2B_OVERWORLD).</summary>
    public static readonly Smb3Level W11 = new("1-1", 0x40, Smb3Map.Level11Layout, Smb3Map.Level11Objects, 1,
        (15, 0xA000), (15, 0xA400), Smb3Map.PlainsBgBank, 0xAC92, 0x10, 0x94, 0xF80000);

    /// <summary>World 1-2: Hills (prg016), BG bank $6C (index 19, "Hills / Underground alternate"),
    /// PalSet_HillsUnder ($AE12 in bank 27), the athletic theme (MUS2B_ATHLETIC).</summary>
    public static readonly Smb3Level W12 = new("1-2", 0x41, Smb3Map.Level12Layout, Smb3Map.Level12Objects,
        Smb3Map.Level12Tileset, (16, 0xA000), (16, 0xA400), 0x6C, 0xAE12, 0x90, 0x95, 0xF90000);

    public static readonly Smb3Level W13 = new("1-3", 0x42, (15, 0xAE09), (6, 0xC2EE), 1,
        (15, 0xA000), (15, 0xA400), Smb3Map.PlainsBgBank, 0xAC92, 0x10, 0x94, 0xF80000,
        "The main goal route is playable; the white-block/warp-whistle secret route is not implemented.");

    public static readonly Smb3Level[] All = [W11, W12, W13];

    public static Smb3Level? ByZone(int zone) => All.FirstOrDefault(l => l.Zone == zone);
}
