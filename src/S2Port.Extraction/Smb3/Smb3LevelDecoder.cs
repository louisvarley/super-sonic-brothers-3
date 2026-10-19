namespace S2Port.Extraction.Smb3;

/// <summary>
/// SMB3's level loader for the Plains and Hills tilesets (LevelLoad prg030 $97B7; LoadLevel_Generator_TS1 /
/// LeveLoad_FixedSizeGen_TS1 prg015 and LoadLevel_Generator_TS3 / LeveLoad_FixedSizeGen_TS3 prg016; the generators,
/// shared, in prg014), ported routine by routine. It turns a layout (9-byte header, then 3/4-byte commands ending in
/// $FF) into tile memory: 16x16 tile numbers, screens of 27 rows x 16 columns ($1B0 bytes) side by side.
/// Only the generators Worlds 1-1 (Plains) and 1-2 (Hills) use are ported; others throw. Tile memory addressing
/// keeps the 6502's pointer arithmetic (Map_Tile_Addr + Y with carries), so out-of-table reads and the original's
/// quirks come out the same. Validated against MAME's $6000-$794F in 1-1 (tools/Validate smb3-level) and against the
/// original loader run on a 6502 core for 1-2 (tools/Validate smb3-level-6502).
///
/// The tileset is the map's (Map_ByRowType's low nibble, which LevelLoad uses), not the header's: 1-2's header says
/// 1, but the map loads it as tileset 3 (Hills).
/// </summary>
public sealed class Smb3LevelDecoder
{
    private readonly NesRom _rom;
    private readonly byte[] _tileMem = new byte[Smb3Map.TileMemSize];

    // The loader's zero-page state.
    private int _ptr;                 // Level_LayPtr (CPU address in the layout's bank)
    private readonly int _bank;
    private byte _tv15, _tv16, _shape; // Temp_Var15/16, LL_ShapeDef
    private byte _mapL, _mapH;        // Map_Tile_AddrL/H
    private byte _off;                // TileAddr_Off
    private byte _tv5, _tv6;          // Temp_Var5/6 as set by LoadLevel_Set_TileMemAddr

    /// <summary>Random_Pool ($0781-$078A); the clouds generator draws from it.</summary>
    public byte[] RandomPool { get; } = new byte[10];

    public int Width { get; private set; }            // screens - 1
    public int YStartIndex { get; private set; }
    public int XStartIndex { get; private set; }
    public int Tileset { get; private set; }
    public int BgPalette { get; private set; }
    public int ObjPalette { get; private set; }
    public int Music { get; private set; }
    public List<(int Index, byte Y, byte X)> JunctionStarts { get; } = [];
    /// <summary>Tile memory cells written by the random "power up clouds" generator (row, column within level).</summary>
    public HashSet<int> RandomCells { get; } = [];

    /// <param name="tileset">Level_Tileset as the map sets it (1 Plains, 3 Hills); null = the header's.</param>
    public Smb3LevelDecoder(NesRom rom, (int Bank, int Addr) layout, int? tileset = null)
    {
        _rom = rom;
        _bank = layout.Bank;
        _ptr = layout.Addr;
        _tileset = tileset;
    }

    private readonly int? _tileset;

    public ReadOnlySpan<byte> TileMem => _tileMem;

    /// <summary>The tile at a level row (0-26) and column (0 .. screens*16-1).</summary>
    public byte Tile(int row, int column) => _tileMem[(column >> 4) * Smb3Map.ScreenSize + row * 16 + (column & 15)];

    private byte Layout(int i) => _rom.Prg(_bank, _ptr + i);
    private byte Gen(int addr) => _rom.Prg(Smb3Map.GenBank, addr);

    /// <summary>LevelLoad_TS1 / LevelLoad_TS3 then LevelLoad: clear to sky and run the layout (new level, no junction).</summary>
    public void Load()
    {
        if (_bank is not (15 or 16)) throw new UnportedSmb3Exception("layouts outside the Plains and Hills banks");

        // Header (bytes 0-3: alternate layout/objects pointers).
        byte h4 = Layout(4), h5 = Layout(5), h6 = Layout(6), h7 = Layout(7), h8 = Layout(8);
        YStartIndex = h4 >> 5;
        Width = h4 & 0x0F;
        BgPalette = h5 & 7;
        ObjPalette = ((h5 & 0x18) >> 3) | 8;
        XStartIndex = (h5 & 0x60) >> 5;
        if ((h6 & 0x10) != 0) throw new UnportedSmb3Exception("vertical levels");
        Tileset = _tileset ?? h6 & 0x0F;
        if (Tileset is not (1 or 3)) throw new UnportedSmb3Exception($"tileset {Tileset}");
        // LevelLoad_TS1 fills tile memory with TILE1_SKY; LevelLoad_TS3's two Tile_Mem_ClearA/B loops cover all of it
        // with TILE3_SKY.
        Array.Fill(_tileMem, Tileset == 1 ? (byte)0x80 : (byte)0x02);
        Music = h8 & 0x0F;
        _ptr += 9;

        while (true)
        {
            byte b0 = Layout(0);
            if (b0 == 0xFF) break;
            _tv15 = b0;
            _tv16 = Layout(1);
            _shape = Layout(2);
            _ptr += 3;
            if ((_tv15 & 0xE0) == 0xE0)
            {
                JunctionStarts.Add((_tv15 & 0x0F, _tv16, _shape));   // LoadLevel_StoreJctStart
                continue;
            }
            SetTileMemAddr();
            if (Tileset == 3)
            {
                if ((_shape & 0xF0) != 0) HillsGenerator();
                else HillsFixedSizeGenerator();
            }
            else if ((_shape & 0xF0) != 0) Generator();
            else FixedSizeGenerator();
        }
    }

    /// <summary>LoadLevel_Set_TileMemAddr (prg030).</summary>
    private void SetTileMemAddr()
    {
        _off = (byte)((_tv15 << 4) | (_tv16 & 0x0F));
        int x = (_tv16 & 0xF0) >> 3;
        if (x == 0x1E) x -= 2;
        _mapL = _rom.Prg(Smb3Map.TileMemAddr.Bank, Smb3Map.TileMemAddr.Addr + x);
        _mapH = _rom.Prg(Smb3Map.TileMemAddr.Bank, Smb3Map.TileMemAddr.Addr + x + 1);
        _tv5 = (byte)(_mapH + 1);
        if ((_tv15 & 0x10) != 0) _mapH++;
        _tv6 = _mapH;
    }

    // [Map_Tile_AddrL],Y
    private int Addr(byte y) => ((_mapH << 8) | _mapL) + y - Smb3Map.TileMemBase;
    private void Store(byte y, byte v)
    {
        int a = Addr(y);
        if ((uint)a < Smb3Map.TileMemSize) _tileMem[a] = v;
    }
    private byte Load(byte y)
    {
        int a = Addr(y);
        return (uint)a < Smb3Map.TileMemSize ? _tileMem[a] : (byte)0;
    }

    /// <summary>LoadLevel_NextColumn: Y+1, or column 0 of the same row on the next screen (+$1B0).</summary>
    private byte NextColumn(byte y)
    {
        y++;
        if ((y & 0x0F) != 0) return y;
        int l = _mapL + 0xB0;
        _mapL = (byte)l;
        _mapH = (byte)(_mapH + 1 + (l >> 8));
        return (byte)(_off & 0xF0);
    }

    /// <summary>LoadLevel_TileMemNextRow: Y += 16 with the carry into Map_Tile_AddrH.</summary>
    private byte NextRow(byte y)
    {
        int s = y + 16;
        _mapH = (byte)(_mapH + (s >> 8));
        return (byte)s;
    }

    /// <summary>The common "restore the backup, TileAddr_Off += 16 with carry, update the backup" step.</summary>
    private void NextRowFrom(ref byte backupL, ref byte backupH)
    {
        _mapL = backupL;
        _mapH = backupH;
        int s = _off + 16;
        _off = (byte)s;
        _mapH = (byte)(_mapH + (s >> 8));
        backupH = _mapH;
    }

    /// <summary>LoadLevel_Generator_TS1.</summary>
    private void Generator()
    {
        int x = (_tv15 & 0xE0) >> 5;
        int index = (_shape >> 4) + x * 15 - 1;
        switch (index)
        {
            case >= 0 and <= 3: GenerateBigBlock(index); break;
            case >= 4 and <= 7: FloatingBigBlock(); break;
            case 8: LittleBushRun(); break;
            case 9: Pitfall(0); break;
            case 10: LittleCloudRun(); break;
            case 11: GroundRun(); break;
            case 13: CloudRun(); break;
            case >= 15 and <= 22: BlockRun(); break;
            case >= 23 and <= 25: VGroundPipeRun(); break;
            default: throw new UnportedSmb3Exception($"Plains generator {index}");
        }
    }

    /// <summary>LeveLoad_FixedSizeGen_TS1.</summary>
    private void FixedSizeGenerator()
    {
        int index = ((_tv15 & 0xE0) >> 1) + _shape;
        switch (index)
        {
            case 0: Bush(0, 0); break;
            case 1: Bush(Smb3BushOffsets.Small, 1); break;
            case 2: Bush(Smb3BushOffsets.Big, 2); break;
            case 3: RandomPowerUpClouds(); break;
            case >= 16 and <= 40: PowerBlock(index - 16); break;
            case 41: EndGoal(); break;
            default: throw new UnportedSmb3Exception($"Plains fixed-size generator {index}");
        }
    }

    /// <summary>Offsets of LL_Bush2/LL_Bush3 from LL_Bush1 (the bush designs are 5x4, 4x3 and 6x8 tiles + terminators).</summary>
    private static class Smb3BushOffsets { public const int Small = 21, Big = 34; }

    /// <summary>LoadLevel_LittleBushRun.</summary>
    private void LittleBushRun()
    {
        int n = _shape & 0x0F;
        byte y = _off;
        for (int i = n; i >= 0; i--)
        {
            Store(y, LittleBushTile);
            y = NextColumn(y);
        }
    }

    /// <summary>The operand of LDA #TILE1_LITTLE_BUSH in LoadLevel_LittleBushRun (prg014 $D28C).</summary>
    private byte LittleBushTile => Gen(0xD28D);

    /// <summary>LoadLevel_Pitfall (x = 0) / PitfallW (x = 2).</summary>
    private void Pitfall(int x)
    {
        byte bl = _mapL, bh = _mapH;
        int width = _shape & 0x0F;
        byte y = _off;
        do
        {
            int w = width;
            Store(y, Gen(Smb3Map.LL_RightEdge + x));
            y = NextColumn(y);
            while (--w != 0)
            {
                Store(y, Gen(Smb3Map.LL_PitTile + x));
                y = NextColumn(y);
            }
            Store(y, Gen(Smb3Map.LL_LeftEdge + x));
            NextRowFrom(ref bl, ref bh);
            y = _off;
            x++;
        } while (y < 0xB0);
    }

    /// <summary>LoadLevel_GroundRun: a 4-byte command; byte 4 is the width, the shape's low nibble the extra rows.</summary>
    private void GroundRun()
    {
        byte widthByte = Layout(0);
        _ptr++;
        byte bl = _mapL, bh = _mapH;
        int sel = ((_shape - 0xC0) & 0xFF) >> 4;
        int x = Gen(Smb3Map.LL_RunGround_Offset + sel);
        int rows = _shape & 0x0F;
        byte y = _off;
        int w = widthByte;
        Store(y, Gen(Smb3Map.LL_RunGroundTop + x));
        y = NextColumn(y);
        w = (w - 1) & 0xFF;
        if (w != 0)
        {
            do
            {
                Store(y, Gen(Smb3Map.LL_RunGroundTop + x + 1));
                y = NextColumn(y);
                w = (w - 1) & 0xFF;
            } while (w != 0);
        }
        Store(y, Gen(Smb3Map.LL_RunGroundTop + x + 2));
        while (true)
        {
            NextRowFrom(ref bl, ref bh);
            y = _off;
            rows = (rows - 1) & 0xFF;
            if ((sbyte)rows < 0) return;
            w = widthByte;
            Store(y, Gen(Smb3Map.LL_RunGroundMid + x));
            y = NextColumn(y);
            w = (w - 1) & 0xFF;
            if (w != 0)
            {
                do
                {
                    Store(y, Gen(Smb3Map.LL_RunGroundMid + x + 1));
                    y = NextColumn(y);
                    w = (w - 1) & 0xFF;
                } while (w != 0);
            }
            Store(y, Gen(Smb3Map.LL_RunGroundMid + x + 2));
        }
    }

    /// <summary>LoadLevel_CloudRun: two rows of the big cloud, the middle repeated.</summary>
    private void CloudRun()
    {
        byte bl = _mapL, bh = _mapH;
        int width = _shape & 0x0F;
        byte y = _off;
        for (int x = 0; x < 2; x++)
        {
            int w = width;
            byte tile = Gen(Smb3Map.LL_Cloud + x);
            while (true)
            {
                Store(y, tile);
                y = NextColumn(y);
                w = (w - 1) & 0xFF;
                if (w == 0) break;
                tile = Gen(Smb3Map.LL_Cloud + 2 + x);
            }
            Store(y, Gen(Smb3Map.LL_Cloud + 4 + x));
            NextRowFrom(ref bl, ref bh);
            y = _off;
        }
    }

    /// <summary>LoadLevel_LittleCloudRun.</summary>
    private void LittleCloudRun()
    {
        int n = _shape & 0x0F;
        byte y = _off;
        for (int i = n; i >= 0; i--)
        {
            Store(y, Gen(Smb3Map.LL_LittleCloud));
            y = NextColumn(y);
        }
    }

    /// <summary>LoadLevel_BlockRun: 1-16 of one block type (coins check Level_BlockGrabHitMem: empty on a first load).</summary>
    private void BlockRun()
    {
        int x = (((_shape - 16) & 0xFF) >> 4) & 0x0F;
        int n = _shape & 0x0F;
        byte y = _off;
        for (int i = n; i >= 0; i--)
        {
            Store(y, Gen(Smb3Map.LoadLevel_Blocks + x));
            y = NextColumn(y);
        }
    }

    /// <summary>LoadLevel_PowerBlock (index 0-24; the 1-up checks see an empty hit memory on a first load).</summary>
    private void PowerBlock(int x)
    {
        byte tile = Gen(Smb3Map.LL_PowerBlocks + x);
        Store(_off, tile);
        if (tile == 0x70)   // TILEA_BRICKPSWITCH: the row above becomes TILEA_PSWITCH_BLANK
        {
            int s = _off - 16;
            byte y = (byte)s;
            if (s < 0) _mapH--;
            Store(y, 0xC1);
        }
    }

    /// <summary>LoadLevel_VGroundPipeRun: a pipe top, then the shaft down for the shape's low nibble more rows.</summary>
    private void VGroundPipeRun()
    {
        byte bl = _mapL, bh = _mapH;
        int x = (((_shape - 0x90) & 0xF0) >> 3);
        int rows = _shape & 0x0F;
        byte y = _off;
        Store(y, Gen(Smb3Map.LL_VertPipe + x));
        y = NextColumn(y);
        Store(y, Gen(Smb3Map.LL_VertPipe + x + 1));
        while (true)
        {
            NextRowFrom(ref bl, ref bh);
            rows = (rows - 1) & 0xFF;
            if ((sbyte)rows < 0) return;
            y = _off;
            Store(y, 0xBA);   // TILE1_PIPEVL
            y = NextColumn(y);
            Store(y, 0xBB);   // TILE1_PIPEVR
        }
    }

    /// <summary>LoadLevel_(Mid/Small/Big)SizeBush: a prefab bush, merging with bushes already there.</summary>
    private void Bush(int start, int widthIndex)
    {
        byte bl = _mapL, bh = _mapH;
        int x = start;
        do
        {
            int w = Gen(Smb3Map.LL_Bush_Width + widthIndex);
            byte y = _off;
            do
            {
                byte tile = Gen(Smb3Map.LL_Bush1 + x);
                if (tile != 0x80)
                {
                    byte place = tile;
                    if (tile is not (0x92 or 0x93))
                    {
                        byte here = Load(y);
                        if (here >= 0x90 && here < 0x9F)
                        {
                            if (here == tile) goto skip;
                            place = tile < 0x94 ? (byte)(tile + 2) : (byte)0x98;
                        }
                    }
                    Store(y, place);
                }
            skip:
                y = NextColumn(y);
                x++;
                w--;
            } while (w >= 0);
            _mapL = bl;
            _mapH = bh;
            int s = _off + 16;
            _off = (byte)s;
            _mapH = (byte)(_mapH + (s >> 8));
            bh = _mapH;
        } while (Gen(Smb3Map.LL_Bush1 + x) != 0xFF);
    }

    /// <summary>LoadLevel_GenerateBigBlock (index 0-3 = colour): a big block down to the ground, then its shadow.</summary>
    private void GenerateBigBlock(int colour)
    {
        byte tv3 = _mapL, tv4 = _mapH;
        int tv1 = _shape & 0x0F;
        int edge = 0;
        byte y;
        while (true)
        {
            int tv13 = (edge + Gen(Smb3Map.LL_BigBlock_Top_ColorOff + colour)) & 0xFF;
            y = _off;
            Store(y, Gen(Smb3Map.LL_BigBlock_Top + tv13));
            while (true)
            {
                y = NextRow(y);
                byte t = Load(y);
                if (t is 0x53 or 0x55) break;   // TILE1_GROUNDTM / TILE1_GROUNDTL
                Store(y, Gen(Smb3Map.LL_BigBlock_Mid + tv13));
            }
            int s = y - 16;
            y = (byte)s;
            if (s < 0) _mapH--;
            Store(y, Gen(Smb3Map.LL_BigBlock_Bot + tv13));
            _mapL = tv3;
            _mapH = tv4;
            y = (byte)(_off + 1);
            if ((y & 0x0F) == 0)
            {
                y = (byte)(_off & 0xF0);
                int l = _mapL + 0xB0;
                _mapL = (byte)l;
                tv3 = _mapL;
                _mapH = (byte)(_mapH + 1 + (l >> 8));
                tv4 = _mapH;
                _tv5 = _mapH;
                if ((_tv15 & 0x10) == 0) _tv5++;
            }
            _off = y;
            tv1 = (tv1 - 1) & 0xFF;
            // CMP #$01 / BGS: a signed "A >= 1" after the compare (N xor V clear); 0 and $80-$FF fall through.
            if (Bgs(tv1, 1)) { edge = 1; continue; }
            if (tv1 != 0) break;
            edge = 2;
        }

        // The shadow: the corner beside the top, then down the right side to the ground.
        byte here = Load(y);
        byte corner = here == 0x80 ? (byte)0xC0
            : here >= 0x90 && here < 0x9F ? (byte)0x9D
            : (byte)((here & 0xC0) | 0x0B);
        Store(y, corner);
        while (true)
        {
            y = NextRow(y);
            byte t = Load(y);
            if (t is 0x53 or 0x57) return;   // TILE1_GROUNDTM / TILE1_GROUNDTR
            int count = Smb3Map.LL_BigBlock_EndShadowChks - Smb3Map.LL_BigBlock_ShadowChks;
            byte shadow = 0xC4;   // TILE1_BLOCK_SHADOW
            for (int i = count - 1; i >= 0; i--)
                if (Gen(Smb3Map.LL_BigBlock_ShadowChks + i) == t) { shadow = Gen(Smb3Map.LL_BigBlock_ApprShadow + i); break; }
            Store(y, shadow);
        }
    }

    /// <summary>CMP #m then BGS (the disassembly's macro for BPL): bit 7 of A - m clear.</summary>
    private static bool Bgs(int a, int m) => ((a - m) & 0x80) == 0;

    /// <summary>LoadLevel_FloatingBigBlock: a 3-row floating block with its shadow; the colour comes from the shape byte.</summary>
    private void FloatingBigBlock()
    {
        byte bl = _mapL, bh = _mapH;
        int sub = (_shape - 0x50) & 0xFF;
        int colour = (sub & 0xF0) >> 4;
        int width = sub & 0x0F;
        for (int row = 0; row < 3; row++)
        {
            int x = (row + Gen(Smb3Map.LL_BigBlock_Top_ColorOff + colour)) & 0xFF;
            int w = width;
            byte y = _off;
            Store(y, Gen(Smb3Map.LL_BigBlockSky_Left + x));
            y = NextColumn(y);
            w = (w - 1) & 0xFF;
            do
            {
                Store(y, Gen(Smb3Map.LL_BigBlockSky_Middle + x));
                y = NextColumn(y);
                w = (w - 1) & 0xFF;
            } while (w != 1);
            Store(y, Gen(Smb3Map.LL_BigBlockSky_Right + x));
            y = NextColumn(y);
            Store(y, Gen(Smb3Map.LL_BigBlockSky_Shadow + x));
            NextRowFrom(ref bl, ref bh);
        }
    }

    /// <summary>Randomize (prg030): rotate the pool right through a feedback bit.</summary>
    private void Randomize()
    {
        bool carry = ((RandomPool[0] & 2) ^ (RandomPool[1] & 2)) != 0;
        for (int i = 0; i < 9; i++)
        {
            bool next = (RandomPool[i] & 1) != 0;
            RandomPool[i] = (byte)((RandomPool[i] >> 1) | (carry ? 0x80 : 0));
            carry = next;
        }
    }

    /// <summary>LoadLevel_RandomPUpClouds: 60 random clouds in the top rows (depends on the random pool).</summary>
    private void RandomPowerUpClouds()
    {
        for (int counter = 59; counter >= 0; counter--)
        {
            Randomize();
            int row = RandomPool[1] & 0x0F;
            if (row >= 10) row -= 9;
            _tv15 = (byte)row;
            int x = RandomPool[2];
            if ((x & 0xF0) == 0xF0) x = (x + 0x20) & 0xFF;
            _tv16 = (byte)x;
            int kind = x & 3;
            if (kind == 3) kind -= 2;
            SetTileMemAddr();
            int a = Addr(_off);
            if ((uint)a < Smb3Map.TileMemSize) RandomCells.Add(a);
            Store(_off, Gen(Smb3Map.LL_PUPClouds + kind));
        }
    }

    /// <summary>LoadLevel_EndGoal: the goal edge column, then a screen of black filler with the goal square.</summary>
    private void EndGoal()
    {
        byte bl = _mapL, bh = _mapH;
        int part = 0, x = 0;
        while (true)
        {
            byte y = _off;
            for (int n = 25; n >= 0; n--)
            {
                Store(y, Gen(Smb3Map.LL_GoalTiles + x));
                y = NextRow(y);
            }
            _mapL = bl;
            _mapH = bh;
            y = (byte)(_off + 1);
            if ((y & 0x0F) == 0)
            {
                if (part == 1) EndGoalDecoSquare(bl, bh);
                int l = _mapL + 0xB0;
                _mapL = (byte)l;
                bl = _mapL;
                _mapH = (byte)(_mapH + 1 + (l >> 8));
                bh = _mapH;
                part++;
                y = (byte)(_off & 0xF0);
            }
            _off = y;
            x = 1;
            if (part == 2) return;
        }
    }

    // ------------------------------------------------------------------ Hills (tileset 3)

    // prg014 tables (shared by the Hills and Underground styles; column order Above Ground, Hills, Underwater).
    private const int LL_Corners = 0xD43C, LL_SlopeMidGround = 0xD454, LL_45SlopesT2B = 0xD457, LL_45SlopesB2T = 0xD45A,
        LL_225SlopesT2B_Upper = 0xD600, LL_225SlopesT2B_Lower = 0xD603, LL_225SlopesB2T_Upper = 0xD66F,
        LL_225SlopesB2T_Lower = 0xD672, LL_VertGroundL = 0xD7F7, LL_DecoGroundTop = 0xD840, LL_DecoGroundMid = 0xD846,
        LL_FillBackground = 0xD9CA, LL_Tunnel = 0xDAED, LL_HorzPipe = 0xC76F;
    private const byte TILE3_BGBUSH_L = 0x81, TILE3_BGBUSH_M = 0x82, TILE3_BGBUSH_R = 0x83, TILE1_PIPEHT = 0xB8,
        TILE1_PIPEHB = 0xB9;

    /// <summary>LoadLevel_Generator_TS3 (prg016).</summary>
    private void HillsGenerator()
    {
        int x = (_tv15 & 0xE0) >> 5;
        int index = (_shape >> 4) + x * 15 - 1;
        switch (index)
        {
            case 0 or 4 or 8: Slope45T2B(); break;
            case 1 or 5 or 9: Slope45B2T(); break;
            case 12 or 13 or 14: VertGround(right: false); break;
            case >= 15 and <= 22: BlockRun(); break;
            case >= 23 and <= 25: VGroundPipeRun(); break;
            case 28 or 29: HRightWallPipeRun(); break;
            case 30 or 31: HLeftWallPipeRun(); break;
            case 45 or 49 or 53: Slope225T2B(); break;
            case 46 or 50 or 54: Slope225B2T(); break;
            case 57 or 58 or 59: VertGround(right: true); break;
            case >= 63 and <= 68: DecoGround(); break;
            case 72: BgBush(); break;
            case 75 or 76: Tunnel(); break;
            case 77: CloudRun(); break;
            default: throw new UnportedSmb3Exception($"Hills generator {index}");
        }
    }

    /// <summary>LeveLoad_FixedSizeGen_TS3 (prg016).</summary>
    private void HillsFixedSizeGenerator()
    {
        int index = ((_tv15 & 0xE0) >> 1) + _shape;
        switch (index)
        {
            case >= 0 and <= 11: Store(_off, Gen(LL_Corners + _shape)); break;   // LoadLevel_Corner
            case >= 12 and <= 14: break;                                          // LoadLevel_Nothing
            case >= 16 and <= 40: PowerBlock(index - 16); break;
            case 41: EndGoal(); break;
            case 42: Bush(0, 0); break;
            case 43: Bush(Smb3BushOffsets.Small, 1); break;
            case 44: Bush(Smb3BushOffsets.Big, 2); break;
            case 45 or 46 or 47: FillBackground(); break;
            default: throw new UnportedSmb3Exception($"Hills fixed-size generator {index}");
        }
    }

    /// <summary>SUB #$10 / AND #$C0 / CLC / ROL x3: the column (Above Ground, Hills, Underwater) of a slope table.</summary>
    private int SlopeColumn => ((_shape - 0x10) & 0xC0) >> 6;

    /// <summary>DEC then BPL on a byte counter: true while it has not gone below 0.</summary>
    private static bool DecBpl(ref int counter)
    {
        counter = (counter - 1) & 0xFF;
        return (counter & 0x80) == 0;
    }

    /// <summary>LoadLevel_Slope45T2B: each row one more midground tile left of the slope tile.</summary>
    private void Slope45T2B()
    {
        byte bl = _mapL, bh = _mapH;
        int tv3 = 0, tv4 = _shape & 0x0F, x = SlopeColumn;
        do
        {
            byte y = _off;
            for (int tv5 = tv3; tv5 != 0; tv5--)
            {
                Store(y, Gen(LL_SlopeMidGround + x));
                y = NextColumn(y);
            }
            Store(y, Gen(LL_45SlopesT2B + x));
            NextRowFrom(ref bl, ref bh);
            tv3++;
        } while (DecBpl(ref tv4));
    }

    /// <summary>LoadLevel_Slope45B2T: each row the slope tile then one more midground tile; rows step left (+15),
    /// back a screen when they cross its left edge.</summary>
    private void Slope45B2T()
    {
        byte bl = _mapL, bh = _mapH;
        int tv3 = 0, tv4 = _shape & 0x0F, x = SlopeColumn;
        do
        {
            byte y = _off;
            int tv5 = tv3;
            Store(y, Gen(LL_45SlopesB2T + x));
            while (true)
            {
                y = NextColumn(y);
                if (!DecBpl(ref tv5)) break;
                Store(y, Gen(LL_SlopeMidGround + x));
            }
            tv3++;
            _mapL = bl;
            _mapH = bh;
            y = StepLeftRow(15, ref bl, ref bh);
            if ((y & 0x0F) == 0x0F) y = BackAScreen(y, ref bl, ref bh);
            _off = y;
        } while (DecBpl(ref tv4));
    }

    /// <summary>LoadLevel_Slope225T2B: two slope tiles a row, two more midground tiles each row.</summary>
    private void Slope225T2B()
    {
        byte bl = _mapL, bh = _mapH;
        int tv3 = 0, tv4 = _shape & 0x0F, x = SlopeColumn;
        do
        {
            byte y = _off;
            for (int tv5 = tv3; tv5 != 0; tv5--)
            {
                Store(y, Gen(LL_SlopeMidGround + x));
                y = NextColumn(y);
                Store(y, Gen(LL_SlopeMidGround + x));
                y = NextColumn(y);
            }
            Store(y, Gen(LL_225SlopesT2B_Upper + x));
            y = NextColumn(y);
            Store(y, Gen(LL_225SlopesT2B_Lower + x));
            NextRowFrom(ref bl, ref bh);
            tv3++;
        } while (DecBpl(ref tv4));
    }

    /// <summary>LoadLevel_Slope225B2T: two slope tiles then two more midground tiles a row; rows step left by two
    /// (+14), back a screen when they cross its left edge.</summary>
    private void Slope225B2T()
    {
        byte bl = _mapL, bh = _mapH;
        int tv3 = 0, tv4 = _shape & 0x0F, x = SlopeColumn;
        do
        {
            byte y = _off;
            int tv5 = tv3;
            Store(y, Gen(LL_225SlopesB2T_Upper + x));
            y = NextColumn(y);
            Store(y, Gen(LL_225SlopesB2T_Lower + x));
            while (true)
            {
                y = NextColumn(y);
                if (!DecBpl(ref tv5)) break;
                Store(y, Gen(LL_SlopeMidGround + x));
                y = NextColumn(y);
                Store(y, Gen(LL_SlopeMidGround + x));
            }
            _mapL = bl;
            _mapH = bh;
            y = StepLeftRow(14, ref bl, ref bh);
            if ((y & 0x0F) >= 14) y = BackAScreen(y, ref bl, ref bh);
            _off = y;
            tv3++;
        } while (DecBpl(ref tv4));
    }

    /// <summary>TileAddr_Off + <paramref name="add"/> into Y, the carry into Map_Tile_AddrH (and its backup).</summary>
    private byte StepLeftRow(int add, ref byte bl, ref byte bh)
    {
        int s = _off + add;
        _mapH = (byte)(_mapH + (s >> 8));
        bh = _mapH;
        return (byte)s;
    }

    /// <summary>The bottom-to-top slopes' screen step: Map_Tile_Addr - $1B0 (SUB #$B0 / SBC #$01), then Y + 16.</summary>
    private byte BackAScreen(byte y, ref byte bl, ref byte bh)
    {
        int l = _mapL - 0xB0;
        _mapL = (byte)l;
        bl = _mapL;
        _mapH = (byte)(_mapH - 1 - (l < 0 ? 1 : 0));
        bh = _mapH;
        int s = y + 16;
        _mapH = (byte)(_mapH + (s >> 8));
        bh = _mapH;
        return (byte)s;
    }

    /// <summary>LoadLevel_VertGroundL / VertGroundR: a column of 1-16 edge tiles (R uses LL_VertGroundL + 3).</summary>
    private void VertGround(bool right)
    {
        int tv1 = _shape & 0x0F;
        int x = (((_shape - 0xD0) & 0xFF) >> 4) + (right ? 3 : 0);
        byte y = _off;
        do
        {
            Store(y, Gen(LL_VertGroundL + x));
            y = NextRow(y);
        } while (DecBpl(ref tv1));
    }

    /// <summary>LoadLevel_DecoGround (4-byte): a rectangle, its top row from LL_DecoGroundTop (horizontal ground or
    /// midground), the rest midground.</summary>
    private void DecoGround()
    {
        int tv3 = Layout(0);
        _ptr++;
        byte bl = _mapL, bh = _mapH;
        int tv5 = _shape & 0x0F;
        int x = ((_shape - 0x40) & 0xFF) >> 4;
        byte y = _off;
        int tv4 = tv3;
        do
        {
            Store(y, Gen(LL_DecoGroundTop + x));
            y = NextColumn(y);
            tv4 = (tv4 - 1) & 0xFF;
        } while (tv4 != 0xFF);
        while (true)
        {
            NextRowFrom(ref bl, ref bh);
            y = _off;
            tv4 = tv3;
            if (!DecBpl(ref tv5)) return;
            do
            {
                Store(y, Gen(LL_DecoGroundMid + x));
                y = NextColumn(y);
                tv4 = (tv4 - 1) & 0xFF;
            } while (tv4 != 0xFF);
        }
    }

    /// <summary>LoadLevel_FillBackground: sky / underground speckle on every screen from the row down
    /// (Tile_Mem_ClearA to the end of Y's range, then Tile_Mem_ClearB to $EF).</summary>
    private void FillBackground()
    {
        int x = (_shape - 0x0D) & 0xFF;
        byte v = Gen(LL_FillBackground + x);
        int y = _off;
        if ((_tv15 & 0x10) != 0 || y >= 0xC0) y = (y - 0xC0) & 0xFF;
        else
        {
            do { ClearA(y, v); y = (y + 1) & 0xFF; } while (y != 0);
        }
        do { ClearB(y, v); y++; } while (y != 0xF0);
    }

    /// <summary>Tile_Mem_ClearA / ClearB (prg030): the same offset on all 15 screens (ClearB from row 12).</summary>
    private void ClearA(int y, byte v)
    {
        for (int n = 0; n < 15; n++) _tileMem[n * Smb3Map.ScreenSize + y] = v;
    }

    private void ClearB(int y, byte v)
    {
        for (int n = 0; n < 15; n++)
        {
            int a = 0xC0 + n * Smb3Map.ScreenSize + y;
            if (a < Smb3Map.TileMemSize) _tileMem[a] = v;
        }
    }

    /// <summary>LoadLevel_BGBush: left edge, middle tiles, right edge (BNE: a width of 0 runs 255 middles).</summary>
    private void BgBush()
    {
        int tv3 = _shape & 0x0F;
        byte y = _off;
        Store(y, TILE3_BGBUSH_L);
        while (true)
        {
            y = NextColumn(y);
            tv3 = (tv3 - 1) & 0xFF;
            if (tv3 == 0) break;
            Store(y, TILE3_BGBUSH_M);
        }
        Store(y, TILE3_BGBUSH_R);
    }

    /// <summary>LoadLevel_Tunnel: a run of tunnel tiles (the second kind, the diamond block, does nothing).</summary>
    private void Tunnel()
    {
        int x = ((_shape - 0x10) & 0xF0) >> 4;
        if (x != 0) return;
        int tv4 = _shape & 0x0F;
        byte y = _off;
        do
        {
            Store(y, Gen(LL_Tunnel + x));
            y = NextColumn(y);
        } while (DecBpl(ref tv4));
    }

    /// <summary>LoadLevel_HRightWallPipeRun: a horizontal pipe with its left end (it runs into a wall on the right).</summary>
    private void HRightWallPipeRun()
    {
        int x = ((_shape - 0xE0) & 0xFF) >> 4;
        byte bl = _mapL, bh = _mapH;
        int tv3 = _shape & 0x0F;
        byte y = _off;
        Store(y, Gen(LL_HorzPipe + x));
        while (true)
        {
            y = NextColumn(y);
            if (!DecBpl(ref tv3)) break;
            Store(y, TILE1_PIPEHT);
        }
        NextRowFrom(ref bl, ref bh);
        y = _off;
        tv3 = _shape & 0x0F;
        Store(y, Gen(LL_HorzPipe + 3 + x));
        while (true)
        {
            y = NextColumn(y);
            if (!DecBpl(ref tv3)) break;
            Store(y, TILE1_PIPEHB);
        }
    }

    /// <summary>LoadLevel_HLeftWallPipeRun: a horizontal pipe with its right end (BNE: a length of 0 runs 255).</summary>
    private void HLeftWallPipeRun()
    {
        byte bl = _mapL, bh = _mapH;
        int x = ((_shape - 0x10) & 0xFF) >> 4;
        int tv3 = _shape & 0x0F;
        byte y = _off;
        do
        {
            Store(y, TILE1_PIPEHT);
            y = NextColumn(y);
            tv3 = (tv3 - 1) & 0xFF;
        } while (tv3 != 0);
        Store(y, Gen(LL_HorzPipe + x));
        NextRowFrom(ref bl, ref bh);
        y = _off;
        tv3 = _shape & 0x0F;
        do
        {
            Store(y, TILE1_PIPEHB);
            y = NextColumn(y);
            tv3 = (tv3 - 1) & 0xFF;
        } while (tv3 != 0);
        Store(y, Gen(LL_HorzPipe + 3 + x));
    }

    /// <summary>LoadLevel_EndGoalDecoSquare: 6 rows of the tileset's goal decoration, 20 rows down.</summary>
    private void EndGoalDecoSquare(byte bl, byte bh)
    {
        _mapH++;
        int l = _mapL + 64;
        _mapL = (byte)l;
        _mapH = (byte)(_mapH + (l >> 8));
        int count = 7;   // LL_Goal_TSetListEnd - LL_Goal_TSetList
        int i = count - 1;
        while (i != 0 && Gen(Smb3Map.LL_Goal_TSetList + i) != Tileset) i--;
        int table = Gen(Smb3Map.LL_Goal_TSetAddr + i * 2) | (Gen(Smb3Map.LL_Goal_TSetAddr + i * 2 + 1) << 8);
        byte y = 0;
        for (int row = 0; row < 6; row++)
            for (int c = 0; c < 16; c++, y++)
                Store(y, Gen(table + y));
        _mapL = bl;
        _mapH = bh;
    }
}

public sealed class UnportedSmb3Exception(string what) : Exception($"SMB3 level loader: {what} not ported");
