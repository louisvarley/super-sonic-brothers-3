namespace S2Port.Extraction.Levels;

/// <summary>
/// One act's level data in the same shape the 68k code holds it in RAM.
/// <list type="bullet">
/// <item>Blocks: 16x16, 4 pattern words each (TL, TR, BL, BR).
///   Pattern word: bit 15 priority, 13-14 palette line, 12 V-flip, 11 H-flip, 0-10 tile.</item>
/// <item>Chunks: 128x128, 8x8 block words each, row-major.
///   Block word: 14-15 secondary solidity, 12-13 primary solidity, 11 Y-flip, 10 X-flip, 0-9 block.</item>
/// <item>Layout: 128x16 chunk IDs per plane.</item>
/// </list>
/// </summary>
public sealed class LevelData
{
    public const int LayoutWidth = 128;
    public const int LayoutHeight = 16;
    public const int ChunkCount = 256;
    public const int BlocksPerChunkSide = 8;

    public required byte[] Art { get; init; }
    public required ushort[] BlockWords { get; init; }
    public required ushort[] ChunkWords { get; init; }
    public required byte[] Foreground { get; init; }
    public required byte[] Background { get; init; }
    public required ushort[] Cram { get; init; }
    public string? MusicFile { get; init; }
    public Smb3.Smb3Gameplay? Gameplay { get; init; }

    /// <summary>Collision ID per block for layer A / layer B (null for unused zone slots).</summary>
    public byte[]? CollisionPrimary { get; init; }
    public byte[]? CollisionSecondary { get; init; }
    public CollisionSet? Collision { get; init; }

    public IReadOnlyList<AnimatedTileScript> AnimatedTiles { get; init; } = [];

    /// <summary>The zone's palette cycle, or null.</summary>
    public PaletteCycle? PaletteCycle { get; init; }

    /// <summary>Writes the given frame of each animated-tile script into <see cref="Vram"/> (frame 0 if not given).</summary>
    public void ApplyAnimatedFrames(IReadOnlyList<int>? frameIndexes = null)
    {
        if (Vram is null) return;
        for (int i = 0; i < AnimatedTiles.Count; i++)
        {
            var s = AnimatedTiles[i];
            int frame = frameIndexes is null ? 0 : frameIndexes[i];
            int offset = s.Frames[frame].TileOffset;
            s.Art.AsSpan(offset * 32, s.TilesPerFrame * 32).CopyTo(Vram.AsSpan(s.VramTile * 32));
        }
    }

    /// <summary>Camera limits {minX, maxX, minY, maxY} from LevelSizeLoad.</summary>
    public (int MinX, int MaxX, int MinY, int MaxY) Bounds { get; init; }
    private (int X, int Y) _start;
    public (int X, int Y) Start { get => _start; init => _start = value; }

    /// <summary>Copy with a different start position (debug / testing).</summary>
    /// <summary>The same level with another start position (debug starts); every other field is shared.</summary>
    public LevelData WithStart((int X, int Y) start)
    {
        var copy = (LevelData)MemberwiseClone();
        copy._start = start;
        return copy;
    }

    public IReadOnlyList<ObjectPlacement> Objects { get; init; } = [];
    /// <summary>ROM address of the placement list (the original keeps pointers into it in RAM) and the x word before it.</summary>
    public int ObjectListAddress { get; init; }
    public int ObjectBoundaryX { get; init; }
    public IReadOnlyList<RingGroup> RingGroups { get; init; } = [];

    /// <summary>
    /// Individual rings as the ring manager holds them at $FFFFE806: groups expanded with 24 px spacing, then
    /// sorted with the ROM's exchange sort (0x1B0B6). It is not stable, so equal-x rings keep the ROM's order only
    /// if this exact algorithm is used. Slot 0 is the zero sentinel record at $FFFFE800; the sort covers slots 0-255.
    /// </summary>
    public IReadOnlyList<(int X, int Y)> ExpandRings()
    {
        var expanded = RingGroups.SelectMany(g => Enumerable.Range(0, g.Count)
            .Select(i => g.Vertical ? (g.X, g.Y + i * RingGroup.Spacing) : (g.X + i * RingGroup.Spacing, g.Y))).ToList();

        // slots: [sentinel(0,0), rings..., terminator(x=$FFFF), zeros...]
        var slots = new (int X, int Y)[Math.Max(256, expanded.Count + 2)];
        for (int i = 0; i < expanded.Count; i++) slots[i + 1] = expanded[i];
        slots[expanded.Count + 1] = (0xFFFF, 0xFFFF);

        for (int i = 0; i <= 254; i++)
        for (int j = i + 1; j <= 255; j++)
        {
            if (slots[j].X == 0) continue;
            if (slots[i].X > slots[j].X) (slots[i], slots[j]) = (slots[j], slots[i]);
        }
        return slots.Skip(1).Take(expanded.Count).ToList();
    }

    /// <summary>
    /// 64 KiB VRAM image: level art at tile 0 plus the level's load-time PLC art (null if not composed).
    /// Animated tiles are not included; game logic updates them at runtime.
    /// </summary>
    public byte[]? Vram { get; init; }
    public string? PlcListsPath { get; init; }
    /// <summary>The palette while the title card shows: the level's first palette alone (lines 2-3 still black).</summary>
    public ushort[] TitleCardCram { get; init; } = new ushort[64];
    /// <summary>PLCs the title card queues as it leaves (0x171FC).</summary>
    public IReadOnlyList<int> AfterTitleCardPlcs { get; init; } = [];
    /// <summary>Marble Zone's magma art (4 frames of 32x32 linear 4bpp) and where it goes, or null.</summary>
    public byte[]? MagmaArt { get; init; }
    public int MagmaVramTile { get; init; }

    /// <summary>LoadPLC at run time (objects and level events request art): the tiles go straight into VRAM.</summary>
    public void LoadPlc(int id)
    {
        if (Vram is null || PlcListsPath is null) return;
        LevelLoader.ApplyPlc(PlcListsPath, id, Vram);
    }

    /// <summary>Tile data the renderer reads: composed VRAM if available, otherwise level art only.</summary>
    public byte[] TileSource => Vram ?? Art;

    public int TileCount => TileSource.Length / 32;
    public int BlockCount => BlockWords.Length / 4;
    public int LoadedChunkCount => ChunkWords.Length / 64;

    /// <summary>Splits the RAM layout (FG and BG rows interleaved every 0x80 bytes) into two planes.</summary>
    public static (byte[] Fg, byte[] Bg) SplitLayout(ReadOnlySpan<byte> ram)
    {
        if (ram.Length != 0x1000) throw new ArgumentException("Layout must be 0x1000 bytes.");
        var fg = new byte[LayoutWidth * LayoutHeight];
        var bg = new byte[LayoutWidth * LayoutHeight];
        for (int row = 0; row < LayoutHeight; row++)
        {
            ram.Slice(row * 0x100, 0x80).CopyTo(fg.AsSpan(row * LayoutWidth));
            ram.Slice(row * 0x100 + 0x80, 0x80).CopyTo(bg.AsSpan(row * LayoutWidth));
        }
        return (fg, bg);
    }

    /// <summary>Extent of non-zero chunks in a plane, in chunks.</summary>
    public static (int Width, int Height) UsedExtent(byte[] plane)
    {
        int w = 0, h = 0;
        for (int y = 0; y < LayoutHeight; y++)
        for (int x = 0; x < LayoutWidth; x++)
            if (plane[y * LayoutWidth + x] != 0) { w = Math.Max(w, x + 1); h = Math.Max(h, y + 1); }
        return (w, h);
    }

    /// <summary>Count of distinct chunk IDs the layout uses that are past the loaded chunk data.</summary>
    public int UnresolvedChunkRefs() =>
        Foreground.Concat(Background).Select(b => (int)b).Distinct().Count(c => c >= LoadedChunkCount);

    /// <summary>Count of block references (in chunks the layout actually uses) that point past the loaded blocks.</summary>
    public int UnresolvedBlockRefs()
    {
        var used = new HashSet<int>(Foreground.Concat(Background).Select(b => (int)b));
        int n = 0;
        foreach (int c in used.Where(c => c < LoadedChunkCount))
            for (int i = 0; i < 64; i++)
                if ((ChunkWords[c * 64 + i] & 0x3FF) >= BlockCount) n++;
        return n;
    }
}

/// <summary>Angle, height and width arrays shared by all zones using the set (256 collision IDs).</summary>
public sealed record CollisionSet(string Name, byte[] Angles, sbyte[] Heights, sbyte[] Widths);

/// <param name="Remember">Respawn tracking: once destroyed, the object stays gone when it scrolls back into view.</param>
public sealed record ObjectPlacement(int X, int Y, int Id, int Subtype, bool FlipX, bool FlipY, bool Remember)
{
    public static ObjectPlacement FromRaw(ushort x, ushort y, byte id, byte subtype) =>
        new(x, y & 0xFFF, id, subtype, (y & 0x2000) != 0, (y & 0x4000) != 0, (y & 0x8000) != 0);
}

/// <summary>A line of rings: <paramref name="Count"/> rings from (X, Y), 24 px apart.</summary>
public sealed record RingGroup(int X, int Y, int Count, bool Vertical)
{
    public const int Spacing = 0x18;

    public static RingGroup FromRaw(ushort x, ushort y) =>
        new(x, y & 0xFFF, ((y >> 12) & 7) + 1, (y & 0x8000) != 0);
}

public sealed record AnimatedTileScript(byte[] Art, int VramTile, int TilesPerFrame, IReadOnlyList<(int TileOffset, int Duration)> Frames);

/// <summary>
/// A zone's palette cycle: every TimerReload+1 frames the next of 4 frames' colours is written to the palette, at
/// <see cref="Colours"/> (palette indexes, one per colour in a frame).
/// </summary>
public sealed record PaletteCycle(IReadOnlyList<int> Colours, int TimerReload, IReadOnlyList<ushort[]> Frames);
