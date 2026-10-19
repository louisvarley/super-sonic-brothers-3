namespace S2Port.Extraction.Smb3;

/// <summary>
/// Where things are in Super Mario Bros. 3 (USA). Labels follow captainsouthbird's disassembly (github.com/
/// captainsouthbird/smb3, which targets PRG1); this image differs from PRG1 only in bank 30/27/31 code layout and a few
/// operands, so the bank 14/15 tables used here are at the disassembly's addresses. (Bank, CPU address) pairs.
/// </summary>
public static class Smb3Map
{
    /// <summary>SHA-1 of the supported image ("Super Mario Bros. 3 (USA).nes", iNES header included).</summary>
    public const string SupportedSha1 = "a03e7e526e79df222e048ae22214bca2bc49c449";

    public const int TileMemBase = 0x6000, TileMemSize = 6480, ScreenSize = 0x1B0, ScreenRows = 27;

    /// <summary>World 1-1's layout (PRG/levels/Plains/1-1.asm, "Original address was $BB82") and objects (W101O).</summary>
    public static readonly (int Bank, int Addr) Level11Layout = (15, 0xBB82);
    public static readonly (int Bank, int Addr) Level11Objects = (6, 0xC527);

    /// <summary>World 1-2's layout (PRG/levels/Hills/1-2.asm, "Original address was $AF2A", in the Hills bank) and
    /// objects (W102O). The map loads it as tileset 3 (W1_ByRowType's $23), whatever its header says.</summary>
    public static readonly (int Bank, int Addr) Level12Layout = (16, 0xAF2A);
    public static readonly (int Bank, int Addr) Level12Objects = (6, 0xC6AA);
    public const int Level12Tileset = 3;

    /// <summary>Tile_Mem_Addr: the start of each screen in tile memory (prg030 $8000).</summary>
    public static readonly (int Bank, int Addr) TileMemAddr = (30, 0x8000);

    /// <summary>Tile_Layout_TS1: the four 8x8 tiles of each Plains 16x16 tile (UL, LL, UR, LR arrays of 256).</summary>
    public static readonly (int Bank, int Addr) TileLayoutTs1 = (15, 0xA000);

    // Generator tables (prg014, bank 14 at $C000).
    public const int GenBank = 14;
    public const int LL_VertPipe = 0xC642, LL_PowerBlocks = 0xCAC4, LoadLevel_Blocks = 0xCB16, LL_Bush1 = 0xCB51,
        LL_Bush_Width = 0xCBA4, LL_BigBlock_Top = 0xD037, LL_BigBlock_Mid = 0xD043, LL_BigBlock_Bot = 0xD04F,
        LL_BigBlock_ShadowChks = 0xD05B, LL_BigBlock_EndShadowChks = 0xD074, LL_BigBlock_ApprShadow = 0xD074,
        LL_BigBlock_Top_ColorOff = 0xD091, LL_BigBlockSky_Left = 0xD178, LL_BigBlockSky_Middle = 0xD184,
        LL_BigBlockSky_Right = 0xD190, LL_BigBlockSky_Shadow = 0xD19C, LL_PUPClouds = 0xD223, LL_RightEdge = 0xD29B,
        LL_PitTile = 0xD29F, LL_LeftEdge = 0xD2A3, LL_RunGroundTop = 0xD2F8, LL_RunGroundMid = 0xD2FE,
        LL_RunGround_Offset = 0xD304, LL_Cloud = 0xD399, LL_LittleCloud = 0xCFD4, LL_GoalTiles = 0xC860,
        LL_Goal_TSetAddr = 0xCA67, LL_Goal_TSetList = 0xCA75;

    /// <summary>Plains background graphics: 1 KiB CHR banks for tiles $00-$7F, and the animated $80-$FF set.</summary>
    public const int PlainsBgBank = 0x08;
    public static readonly int[] PlainsAnimatedBanks = [0x60, 0x62, 0x64, 0x66];
}
