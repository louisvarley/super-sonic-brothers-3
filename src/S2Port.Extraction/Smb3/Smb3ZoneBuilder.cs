using System.Text.Json;
using S2Port.Extraction.Graphics;
using S2Port.Extraction.Levels;

namespace S2Port.Extraction.Smb3;

/// <summary>
/// The Easter egg: Super Mario Bros. 3's World 1-1 as a Sonic zone (zone <see cref="Zone"/>), built from the player's own
/// smb3.nes. Registered levels are decoded by the original ROM loader (<see cref="Smb3DecodedArea"/>) and converted to
/// native zones:
/// <list type="bullet">
/// <item>Colours: the four NES background palettes (4 colours each) share palette line 2 (colour = palette * 4 + value;
///   value 0 is transparent and shows the backdrop); the sprite palettes share line 3. Lines 0-1 are Green Hill's
///   (Sonic, rings, HUD).</item>
/// <item>Graphics: every (CHR tile, NES palette) pair used becomes one Mega Drive tile; each SMB3 16x16 tile becomes one
///   16x16 block. Level art stays below VRAM tile $340.</item>
/// <item>Collision: SMB3's per-quadrant thresholds (Tile_Attributes_TS1): a tile at or above the floor threshold is
///   top-solid, at or above the wall threshold solid all round; both use the flat full-square collision shape $FF.</item>
/// <item>Interactive tiles (? blocks, bricks, coins) are not drawn into the level: coins become rings and blocks become
///   objects ($E0), so they can be bumped without editing shared chunks. Enemies become objects $E1-$E5; the end card
///   becomes Sonic's signpost.</item>
/// </list>
/// </summary>
public sealed class Smb3ZoneBuilder(NesRom rom, string outDir, Action<string>? log = null, Smb3Level? level = null,
    Smb3Area? campaignArea = null)
{
    /// <summary>World 1-1's zone (the Easter egg); other levels: <see cref="Smb3Levels"/>.</summary>
    public const int Zone = 0x40;
    /// <summary>Bump when the output changes; zones/zone_XX/smb3.json records it with the image's SHA-1.</summary>
    public const int Version = 12;

    private readonly Smb3Level _level = level ?? Smb3Levels.W11;

    private const int MaxLevelTiles = 0x340;
    /// <summary>TILE1_SKY / TILE3_SKY: the tile memory's clear value.</summary>
    private int SkyTile => _level.Tileset switch { 3 or 14 => 0x02, 6 => 0x8A, 10 => 0x06, 13 => 0xCC,
        2 or 8 => 0x00, _ => 0x80 };
    private int BgBank2 => campaignArea is null ? 0x60 : rom.Prg(30, 0x9779 + (Header[7] & 31));
    private byte[] Header => rom.Prg(_level.Layout.Bank, _level.Layout.Addr, 9);

    /// <summary>
    /// The Hills slope tiles (TILE3_SLOPE*) as collision shapes of the set the game uses for zones $11 and up, the
    /// hack's (collision/alternate.json): $99 45 degrees up (id 80), $9C/$9D the lower and upper halves of 22.5
    /// degrees up (28, 29). The set has no falling slopes of these: $9B 45 degrees down is 80 mirrored, $9E/$9F (the
    /// upper and lower halves of 22.5 degrees down) are 29 / 28 mirrored: the block is placed x-flipped in its chunk
    /// (the floor code mirrors a flipped block's heights and angle) with its tiles pre-mirrored so it still looks as
    /// SMB3 draws it.
    /// </summary>
    private Dictionary<int, (int Shape, bool Flip)> Slopes => _level.Tileset == 3
        ? new() { [0x99] = (80, false), [0x9B] = (80, true), [0x9C] = (28, false), [0x9D] = (29, false), [0x9E] = (29, true), [0x9F] = (28, true) }
        : [];
    /// <summary>Tile_Attributes_TS1 (prg015 $A400): floor thresholds then wall thresholds per quadrant.</summary>

    /// <summary>Object ids of the Easter egg's own objects (no Sonic 2 / hack object uses them).</summary>
    public const int ObjBlock = 0xE0, ObjGoomba = 0xE1, ObjParagoomba = 0xE2, ObjKoopa = 0xE3, ObjParatroopa = 0xE4,
        ObjPiranha = 0xE5, ObjGoombaPipe = 0xE8;

    public static bool IsCurrent(string outDir, NesRom rom, Smb3Level? level = null)
    {
        string path = Path.Combine(outDir, "zones", $"zone_{(level ?? Smb3Levels.W11).Zone:X2}", "smb3.json");
        if (!DataFile.Exists(path)) return false;
        using var doc = JsonDocument.Parse(DataFile.ReadAllText(path));
        return doc.RootElement.GetProperty("version").GetInt32() == Version &&
               doc.RootElement.GetProperty("sha1").GetString() == rom.Sha1;
    }

    /// <summary>Builds the zone if smb3.nes is the supported image and the output is missing or out of date.</summary>
    public static bool EnsureBuilt(string nesPath, string outDir, Action<string>? log = null, params Smb3Level[] levels)
    {
        if (!File.Exists(nesPath)) return false;
        var rom = NesRom.Load(nesPath);
        if (rom.Sha1 != Smb3Map.SupportedSha1) { log?.Invoke($"{nesPath}: not the supported SMB3 image (SHA-1 {rom.Sha1})"); return false; }
        if (levels.Length == 0) levels = [Smb3Levels.W11];
        var stale = levels.Where(l => !IsCurrent(outDir, rom, l)).ToList();
        if (stale.Count == 0) return true;
        if (!Extractor.IsArchivePath(outDir)) { foreach (var l in stale) new Smb3ZoneBuilder(rom, outDir, log, l).Build(); return true; }
        // data.bin: add the zones to the archive.
        string work = DataFile.Unpack(outDir);
        foreach (var l in stale) new Smb3ZoneBuilder(rom, work, log, l).Build();
        DataFile.Pack(work, outDir);
        return true;
    }

    private int ZoneId => _level.Zone;
    private string ZoneFolder => campaignArea is null ? $"zone_{ZoneId:X2}" : $"area_{campaignArea.Id}";
    private string ZoneDir => Path.Combine(outDir, "zones", ZoneFolder);

    public void Build()
    {
        var decoder = new Smb3DecodedArea(rom, new(_level.Name, _level.Tileset, _level.Layout.Bank,
            _level.Layout.Addr, _level.Objects.Bank, _level.Objects.Addr));
        int columns = decoder.Vertical ? 16 : (decoder.Width + 1) * 16;
        int rows = decoder.Vertical ? (decoder.Width + 1) * 15 : Smb3Map.ScreenRows;
        if (rows > LevelData.LayoutHeight * 8)
            throw new InvalidDataException($"Vertical area has {rows} rows; the current engine layout supports {LevelData.LayoutHeight * 8}.");
        Directory.CreateDirectory(Path.Combine(ZoneDir, "act_1"));

        var attrs = rom.Prg(_level.TileAttributes.Bank, _level.TileAttributes.Addr, 8);
        var layoutTable = rom.Prg(_level.TileLayout.Bank, _level.TileLayout.Addr, 1024);
        var interactive = InteractiveTiles();

        // Cells: interactive tiles leave the level (sky) and become rings / objects.
        var cells = new int[rows, columns];
        var objects = new List<ObjectPlacement>();
        var rings = new List<RingGroup>();
        for (int r = 0; r < rows; r++)
        for (int c = 0; c < columns; c++)
        {
            int t = decoder.Tile(r, c);
            if (t == 0x40) { rings.Add(new RingGroup(c * 16 + 8, r * 16 + 8, 1, false)); t = SkyTile; }
            else if (interactive.Contains(t)) { objects.Add(new ObjectPlacement(c * 16 + 8, r * 16 + 8, ObjBlock, t, false, false, false)); t = SkyTile; }
            cells[r, c] = t;
        }

        // Art: one MD tile per (CHR tile, palette) used; MD tile 0 is blank.
        var tileData = new List<byte[]> { new byte[32] };
        var tileIndex = new Dictionary<(int Chr, int Pal), int>();
        int MdTile(int chr, int pal)
        {
            if (tileIndex.TryGetValue((chr, pal), out int i)) return i;
            int bank = chr < 0x80 ? _level.BgBank : BgBank2;
            var nes = rom.ChrTile(bank + ((chr & 0x7F) >> 6), chr & 0x3F);
            tileData.Add(NesToMd(nes, pal));
            return tileIndex[(chr, pal)] = tileData.Count - 1;
        }

        var slopes = Slopes;
        // Blocks: block = metatile id (block 0 = sky, empty).
        var blockWords = new ushort[256 * 4];
        var usedMeta = new HashSet<int>();
        foreach (int t in cells) usedMeta.Add(t);
        // Pipes are drawn in front of sprites (high priority) so piranha plants come out of them.
        var pipes = new HashSet<int> { 0xBA, 0xBB, 0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9 };
        if (_level.Tileset is 3 or 14) { pipes.Add(0xBD); pipes.Add(0xBE); }
        if (_level.Tileset == 8)
        {
            foreach (int t in Enumerable.Range(0x4C, 8).Concat(Enumerable.Range(0x91, 2))
                .Concat(Enumerable.Range(0x99, 7)).Concat(Enumerable.Range(0xA1, 12))) pipes.Add(t);
        }
        for (int i = 0; i < 6; i++) pipes.Add(rom.Prg(Smb3Map.GenBank, Smb3Map.LL_VertPipe + i));
        foreach (int t in usedMeta.Order())
        {
            if (t == SkyTile) continue;
            int pal = t >> 6;
            // Tile_Layout: UL, LL, UR, LR arrays; blocks are TL, TR, BL, BR.
            int[] chr = [layoutTable[t], layoutTable[512 + t], layoutTable[256 + t], layoutTable[768 + t]];
            bool mirrored = slopes.TryGetValue(t, out var slope) && slope.Flip;
            if (mirrored) chr = [chr[1], chr[0], chr[3], chr[2]];   // the chunk flips it back
            for (int q = 0; q < 4; q++)
                blockWords[t * 4 + q] = (ushort)(MdTile(chr[q], pal) | (2 << 13) | (pipes.Contains(t) ? 0x8000 : 0) | (mirrored ? 0x800 : 0));
        }
        // Block 0 must be the empty block: swap metatile 0 (if used) with the sky's slot.
        int BlockOf(int t) => t == SkyTile ? 0 : t == 0 ? SkyTile : t;
        var blocks = new ushort[256 * 4];
        for (int t = 0; t < 256; t++)
            for (int q = 0; q < 4; q++) blocks[BlockOf(t) * 4 + q] = blockWords[t * 4 + q];
        if (tileData.Count > MaxLevelTiles) throw new InvalidDataException($"SMB3 {_level.Name} needs {tileData.Count} tiles (limit {MaxLevelTiles})");

        // Solidity and collision per block.
        int Solidity(int t)
        {
            if (Smb3Gameplay.WaterTile(_level.Tileset, t)) return 0;
            if (t == SkyTile) return 0;
            if (slopes.ContainsKey(t)) return 3;
            int q = t >> 6;
            return t >= attrs[4 + q] ? 3 : t >= attrs[q] ? 1 : 0;
        }
        var collision = new int[256];
        foreach (int t in usedMeta)
            if (Solidity(t) != 0) collision[BlockOf(t)] = slopes.TryGetValue(t, out var shape) ? shape.Shape : 0xFF;

        // Chunks (8x8 blocks), deduplicated; chunk 0 is empty. 27 rows of cells -> 4 chunk rows.
        int chunkCols = (columns + 7) / 8, chunkRows = (rows + 7) / 8;
        var chunks = new List<ushort[]> { new ushort[64] };
        var chunkIds = new Dictionary<string, int> { [Key(new ushort[64])] = 0 };
        var fg = new byte[LevelData.LayoutWidth * LevelData.LayoutHeight];
        for (int cy = 0; cy < chunkRows; cy++)
        for (int cx = 0; cx < chunkCols; cx++)
        {
            var words = new ushort[64];
            for (int by = 0; by < 8; by++)
            for (int bx = 0; bx < 8; bx++)
            {
                int r = cy * 8 + by, c = cx * 8 + bx;
                if (r >= rows || c >= columns) continue;
                int t = cells[r, c];
                int s = Solidity(t);
                bool flip = slopes.TryGetValue(t, out var sl) && sl.Flip;
                words[by * 8 + bx] = (ushort)(BlockOf(t) | (flip ? 0x400 : 0) | (s << 12) | (s << 14));
            }
            string key = Key(words);
            if (!chunkIds.TryGetValue(key, out int id)) { id = chunks.Count; chunks.Add(words); chunkIds[key] = id; }
            fg[cy * LevelData.LayoutWidth + cx] = (byte)id;
        }
        if (chunks.Count > 256) throw new InvalidDataException("too many chunks");

        // Enemies and the goal.
        foreach (var source in Smb3ObjectConverter.Read(rom, _level.Objects.Bank, _level.Objects.Addr))
        {
            ObjectPlacement? o = Smb3ObjectConverter.Convert(source);
            if (o is null) log?.Invoke($"SMB3 {_level.Name}: object ${source.Id:X2} at {source.Column},{source.Row} not converted");
            else objects.Add(o);
        }
        objects.Sort((a, b) => a.X.CompareTo(b.X));

        // Start: SMB3's start column, standing on the first floor below the start row.
        int startX = campaignArea is null ? 32 : new[] { 0x18, 0x70, 0xD8, 0x80 }[(decoder.Header[5] >> 5) & 3] + 8;
        int startRow = 0;
        for (int r = 0; r < rows; r++) if (Solidity(cells[r, Math.Min(columns - 1, startX / 16)]) != 0) { startRow = r; break; }
        int startY = campaignArea is null ? startRow * 16 - 20
            : new[] { 0x170, 0x40, 0x00, 0x140, 0x70, 0xB0, 0xF0, 0x180 }[decoder.Header[4] >> 5];
        if (campaignArea is not null && decoder.Vertical) startY = Math.Min(startY, rows * 16 - 40);

        WriteMusic();
        var objectArt = new Smb3ObjectArt(rom, outDir);
        if (campaignArea is null) objectArt.Build();
        WritePlcLists(objectArt.TileCount);
        WriteFiles(decoder, tileData, blocks, collision, chunks, fg, objects, rings, columns, rows, (startX, startY));
        if (campaignArea is not null && BgBank2 == 0x60) WriteAnimation(tileData, tileIndex);
        log?.Invoke($"SMB3 {_level.Name} -> zone ${ZoneId:X2}: {tileData.Count} tiles, {usedMeta.Count} blocks, {chunks.Count} chunks, " +
                    $"{objects.Count} objects, {rings.Count} rings");

        static string Key(ushort[] w) => Convert.ToBase64String(w.SelectMany(BitConverter.GetBytes).ToArray());
    }

    /// <summary>The ? blocks, bricks and other bumpable tiles (LoadLevel_Blocks and LL_PowerBlocks), minus coins and the
    /// plain solid wood block.</summary>
    private HashSet<int> InteractiveTiles()
    {
        var set = new HashSet<int>();
        for (int i = 0; i < 9; i++) set.Add(rom.Prg(Smb3Map.GenBank, Smb3Map.LoadLevel_Blocks + i));
        for (int i = 0; i < 25; i++) set.Add(rom.Prg(Smb3Map.GenBank, Smb3Map.LL_PowerBlocks + i));
        set.Remove(0x40);   // TILEA_COIN: rings
        set.Remove(0x79);   // TILEA_WOODBLOCK: an ordinary solid block
        return set;
    }

    /// <summary>NES 2bpp planar tile -> MD 4bpp, colour = palette * 4 + value (value 0 stays 0, transparent).</summary>
    public static byte[] NesToMd(ReadOnlySpan<byte> nes, int pal)
    {
        var md = new byte[32];
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
        {
            int v = ((nes[y] >> (7 - x)) & 1) | (((nes[y + 8] >> (7 - x)) & 1) << 1);
            int c = v == 0 ? 0 : pal * 4 + v;
            md[y * 4 + x / 2] |= (byte)((x & 1) == 0 ? c << 4 : c);
        }
        return md;
    }

    /// <summary>The 1-1 palettes (PalSel 0 for Plains; background then sprites, 16 NES colour indices each).</summary>
    public ushort[] Palette()
    {
        var bg = rom.Prg(Smb3Palettes.Bank, _level.PaletteSet + (campaignArea is null ? 0 : Header[5] & 7) * 16, 16);
        var spr = rom.Prg(Smb3Palettes.Bank, _level.PaletteSet + (campaignArea is null ? 8 : ((Header[5] >> 3) & 3) | 8) * 16, 16);
        return bg.Concat(spr).Select(i => Smb3Palettes.ToMd(i)).ToArray();
    }

    private void WriteFiles(Smb3DecodedArea decoder, List<byte[]> tiles, ushort[] blocks, int[] collision,
        List<ushort[]> chunks, byte[] fg, List<ObjectPlacement> objects, List<RingGroup> rings, int columns,
        int rows, (int X, int Y) start)
    {
        var palette = Palette();
        var art = tiles.SelectMany(t => t).ToArray();
        var (w, h, pixels) = Tiles.ToSheet(art, 16, _ => 2);
        var preview = new ushort[64];
        palette.CopyTo(preview, 32);
        IndexedPng.Write(Path.Combine(ZoneDir, "tiles.png"), w, h, pixels,
            preview.Select(c => new MdColor(c).ToRgb()).ToList(), transparentIndices: [0, 16, 32, 48]);
        Write("tiles.json", new
        {
            zone = ZoneId,
            tileCount = tiles.Count,
            tilesPerRow = 16,
            pixelEncoding = "index = paletteLine * 16 + colour; raw 4bpp value = index & 15",
            source = $"smb3.nes CHR banks ${_level.BgBank:X2}-${_level.BgBank + 1:X2} and $60-$61 (tileset {_level.Tileset}), NES palette p -> line 2 colours p*4+1..3",
        });
        Write("blocks.json", LevelJson.BlocksToJson(blocks));
        Write("chunks.json", LevelJson.ChunksToJson(chunks.SelectMany(c => c).ToArray()));
        Write("collision.json", new
        {
            format = "s2port.collision-index/1",
            set = "../../collision/alternate.json",
            description = "SMB3 solid tiles use the flat full square ($FF), Hills slopes the set's ramps; see Smb3ZoneBuilder.",
            primary = collision,
            secondary = collision,
        });
        Write("palette.json", new
        {
            startColour = 32,
            startLine = 2,
            colourCount = 32,
            colours = palette.Select(c => new { word = $"0x{c:X4}", rgb = new MdColor(c).ToHex() }),
            source = $"smb3.nes palette set ${_level.PaletteSet:X4} (bank 27): background palette 0 and sprite palette 8, via the NES 2C02 palette",
        });
        Write("act_1/layout.json", LevelJson.LayoutToJson(fg, new byte[fg.Length]));
        Write("act_1/objects.json", LevelJson.ObjectsToJson(objects));
        Write("act_1/rings.json", LevelJson.RingsToJson(rings));
        int width = columns * 16;
        Write("act_1/level.json", new
        {
            format = "s2port.level/1",
            zone = ZoneId,
            act = 1,
            easterEgg = $"Super Mario Bros. 3 World {_level.Name}",
            tiles = "../tiles.png",
            blocks = "../blocks.json",
            chunks = "../chunks.json",
            layout = "layout.json",
            collision = "../collision.json",
            plcs = new
            {
                fromTitleScreen = new[] { 0x00 },
                onLoad = new[] { 0x01, PlcObjects },
                afterTitleCard = new[] { 0x02, 0x45 },   // Green Hill's animals, for destroyed enemies
                lists = $"../../../plc/{PlcListsFile}",
            },
            objects = "objects.json",
            rings = "rings.json",
            // Sonic may run to Camera_Max_X + $168 (Sonic_LevelBound): keep that on the map's last ground.
            bounds = new { minX = 0, maxX = Math.Max(0, width - 0x168 - 16), minY = 0, maxY = Math.Max(0, rows * 16 - 224) },
            start = new { x = start.X, y = start.Y },
            palettes = new[] { "../../../palettes/pal_03.json", "../../../palettes/pal_28.json", "../palette.json" },
            music = 0x81,
            campaignArea = campaignArea?.Id,
            musicFile = "../music.json",
            gameplay = "gameplay.json",
        });
        bool mapPipe = campaignArea is not null && Smb3Catalog.Read(rom).Worlds.Any(w =>
            w.Entries.Any(e => e.AreaId == campaignArea.Id && e.Row is >= 0 and < 9 &&
                w.Map[e.Screen * 144 + e.Row * 16 + e.Column] == 0xBC));
        Write("act_1/gameplay.json", Smb3Gameplay.Build(decoder, _level.Tileset, mapPipe));
        Write("smb3.json", new { version = Version, sha1 = rom.Sha1, level = _level.Name, screens = decoder.Width + 1 });
    }

    /// <summary>The music id the Easter egg's song takes: one of the driver's unused ids (it points at song $81).</summary>
    public const int MusicId = 0x94;
    /// <summary>Where the song is mapped for the driver (outside the ROM).</summary>
    public const int MusicAddress = 0xF80000;

    /// <summary>
    /// The level's song (Set 2B: 1-1 the overworld theme $10, 1-2 the athletic theme $90) for the 68k SMPS driver
    /// (Smb3SongConverter), in zones/zone_XX/music.bin with music.json for Smps68kData's extra songs. Each level has
    /// its own music id and address (<see cref="Smb3Level"/>).
    /// </summary>
    private void WriteMusic()
    {
        string player = Path.Combine(outDir, "audio", "smps68k", "player.json");
        if (!File.Exists(player)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(player));
        ushort[] Words(string name) => doc.RootElement.GetProperty(name).EnumerateArray().Select(v => (ushort)v.GetInt32()).ToArray();
        if (campaignArea is not null)
        {
            string converted = Path.Combine(outDir, "conversion", "music", $"set2B_{_level.Song:X2}");
            Directory.CreateDirectory(ZoneDir);
            File.Copy(converted + ".bin", Path.Combine(ZoneDir, "music.bin"), overwrite: true);
            var metadata = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(converted + ".json"))!;
            metadata["songs"]![0]!["file"] = "music.bin";
            Write("music.json", metadata);
            return;
        }
        var converter = new Smb3SongConverter(Words("psgFrequencies"), Words("fmFrequencies"));
        var song = converter.Convert(rom, _level.Song, _level.MusicAddress, out int intro, out int loop);
        Directory.CreateDirectory(ZoneDir);
        File.WriteAllBytes(Path.Combine(ZoneDir, "music.bin"), song);
        Write("music.json", new
        {
            songs = new[] { new { id = _level.MusicId, address = _level.MusicAddress, file = "music.bin" } },
            source = $"smb3.nes Set 2B song ${_level.Song:X2}, arranged for the 68k SMPS driver by Smb3SongConverter",
            introFrames = intro,
            loopFrames = loop,
        });
        log?.Invoke($"SMB3 song ${_level.Song:X2} -> music ${_level.MusicId:X2}: {song.Length} bytes, intro {intro} frames, loop {loop} frames");
    }

    /// <summary>The zone's art lists: the game's lists (objects request them by number at runtime) plus one more
    /// that loads the Easter egg's object art.</summary>
    public const string PlcListsFile = "plc_lists_zone40.json";
    public const int PlcObjects = 94;

    private void WritePlcLists(int objectTiles)
    {
        if (campaignArea is not null) return;
        string src = Path.Combine(outDir, "plc", "plc_lists.json");
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(src))!;
        var lists = root["lists"]!.AsArray();
        if (lists.Count != PlcObjects) throw new InvalidDataException($"expected {PlcObjects} art lists, found {lists.Count}");
        lists.Add(System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new
        {
            id = PlcObjects,
            entries = new[] { new { art = $"../zones/zone_{Zone:X2}/objects.png", tiles = objectTiles, vramTile = Smb3ObjectArt.VramTile } },
        })));
        root["description"] = "plc_lists.json plus list 94: the SMB3 Easter egg's object art (Smb3ZoneBuilder)";
        File.WriteAllText(Path.Combine(outDir, "plc", PlcListsFile), root.ToJsonString(Manifest.JsonOptions));
    }

    private void Write(string rel, object value)
    {
        string path = Path.Combine(ZoneDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Manifest.JsonOptions));
    }

    private void WriteAnimation(List<byte[]> tiles, Dictionary<(int Chr, int Pal), int> tileIndex)
    {
        var palette = new ushort[64].Select(c => new MdColor(c).ToRgb()).ToList();
        var frames = new List<byte>();
        foreach (int bank in Smb3Map.PlainsAnimatedBanks)
        {
            var art = tiles.SelectMany(t => t).ToArray();
            foreach (var (source, tile) in tileIndex)
                if (source.Chr >= 0x80)
                    NesToMd(rom.ChrTile(bank + ((source.Chr & 127) >> 6), source.Chr & 63), source.Pal)
                        .CopyTo(art, tile * 32);
            frames.AddRange(art);
        }
        var (w, h, pixels) = Tiles.ToSheet(frames.ToArray(), 16);
        IndexedPng.Write(Path.Combine(ZoneDir, "animation.png"), w, h, pixels, palette);
        Write("animation.json", new { scripts = new[] { new { art = "animation.png",
            artTiles = tiles.Count * 4, vramTile = 0, tilesPerFrame = tiles.Count,
            frames = Enumerable.Range(0, 4).Select(f => new[] { f * tiles.Count, 7 }).ToArray() } } });
        var path = Path.Combine(ZoneDir, "act_1", "level.json");
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        metadata["animatedTiles"] = "../animation.json";
        File.WriteAllText(path, metadata.ToJsonString(Manifest.JsonOptions));
    }
}
