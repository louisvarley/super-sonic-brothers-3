using System.Text.Json;
using S2Port.Extraction.Audio;
using S2Port.Extraction.Compression;
using S2Port.Extraction.Graphics;
using S2Port.Extraction.Levels;

namespace S2Port.Extraction;

/// <summary>
/// Extracts data from the ROM into a data folder. Used by tools/Extract and, on first launch,
/// by the game itself (see <see cref="EnsureExtracted"/>).
/// </summary>
public sealed partial class Extractor(RomImage rom, string outDir, Action<string>? log = null)
{
    /// <summary>Bump when output changes so existing data folders are re-extracted.</summary>
    public const int Version = 71;

    public const string ManifestFileName = "manifest.json";

    private readonly Manifest _manifest = new();

    /// <summary>
    /// Returns true if extraction ran. Skips when the manifest matches this ROM and extractor version.
    /// </summary>
    public static bool EnsureExtracted(string romPath, string outDir, Action<string>? log = null)
    {
        var existing = Manifest.TryLoad(Path.Combine(outDir, ManifestFileName));
        if (existing is { ExtractorVersion: Version } && existing.RomSha1 == RomMap.SupportedSha1)
            return false;

        if (!File.Exists(romPath))
            throw new FileNotFoundException("Extracted data is missing or out of date, and the ROM needed to rebuild it was not found.", romPath);

        var rom = RomImage.Load(romPath);
        Extract(rom, outDir, log);
        return true;
    }

    /// <summary>
    /// True when the data path is (or will be) a single archive file such as data.bin rather than a folder: an
    /// existing zip, or a path with an extension that is not a folder.
    /// </summary>
    public static bool IsArchivePath(string path) =>
        DataFile.IsArchive(path) || (!Directory.Exists(path) && Path.HasExtension(path));

    /// <summary>Extracts into a folder, or into a working folder that is then packed into the archive.</summary>
    public static void Extract(RomImage rom, string outDir, Action<string>? log = null, bool allowUnknownRom = false, bool previews = false)
    {
        if (!IsArchivePath(outDir)) { new Extractor(rom, outDir, log).Run(allowUnknownRom, previews); return; }
        string work = outDir + ".work";
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        new Extractor(rom, work, log).Run(allowUnknownRom, previews);
        DataFile.Pack(work, outDir);
        log?.Invoke($"Packed the extracted data into {outDir}");
    }

    public void Run(bool allowUnknownRom = false, bool previews = false)
    {
        if (rom.Sha1 != RomMap.SupportedSha1 && !allowUnknownRom)
            throw new InvalidDataException(
                $"ROM SHA-1 {rom.Sha1} does not match the supported build {RomMap.SupportedSha1}. " +
                "Table addresses are build-specific.");

        Directory.CreateDirectory(outDir);
        _manifest.ExtractorVersion = Version;
        _manifest.RomSha1 = rom.Sha1;
        _manifest.RomLength = rom.Length;
        _manifest.ExtractedAt = DateTimeOffset.UtcNow;

        var palettes = ExtractPalettes();
        ExtractCollisionSets();
        ExtractPlcs(palettes);
        ExtractZones(palettes);
        ExtractAudio68k();
        ExtractAudioZ80();
        ExtractSonicAnimations();
        ExtractSonicSprites(palettes);
        ExtractRingMappings();
        ExtractHud(palettes);
        foreach (var (name, maps, anims, frames) in RomMap.ObjectSprites) ExtractObjectSprites(name, maps, anims, frames);
        ExtractStreamedObjectArt(palettes);
        ExtractTitleCard(palettes);
        ExtractFrontend();
        foreach (var (file, address, length) in RomMap.ObjectDataTables)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(outDir, "objects", file))!);
            WriteCompactJson($"objects/{file}", new
            {
                format = "s2port.object-data/1",
                description = "A data table object code reads by ROM address (baseAddress = ROM address of data[0]).",
                baseAddress = address,
                data = rom.Slice(address, length).ToArray().Select(b => (int)b),
            });
            _manifest.Items.Add(new ManifestItem($"objects/{file}", "object-data", [SourceRef.At(address, "raw", size: length)]));
        }
        ExtractAnimalTables();
        ExtractOscillation();
        WriteCompactJson("objects/stars/stars.json", new
        {
            format = "s2port.stars/1",
            circle = Enumerable.Range(0, 64).Select(i => (int)(sbyte)rom.U8(RomMap.StarsCircle + i)).ToArray(),
            sequencesAddress = RomMap.StarsSequences,
            sequences = Enumerable.Range(0, RomMap.StarsSequencesEnd - RomMap.StarsSequences).Select(i => (int)rom.U8(RomMap.StarsSequences + i)).ToArray(),
            trailing = new[] { new[] { 0x21A13 - RomMap.StarsSequences, 0x000B }, new[] { 0x21A28 - RomMap.StarsSequences, 0x160D }, new[] { 0x21A41 - RomMap.StarsSequences, 0x2C0D } },
        });
        _manifest.Items.Add(new ManifestItem("objects/stars/stars.json", "object-data",
            [SourceRef.At(RomMap.StarsCircle, "raw", size: 64), SourceRef.At(RomMap.StarsSequences, "raw", size: RomMap.StarsSequencesEnd - RomMap.StarsSequences), SourceRef.At(0x2181A, "raw", size: 18)]));
        int[] Speeds(int a) => Enumerable.Range(0, RomMap.WallSpeedsSize / 2).Select(i => (int)(short)rom.U16(a + i * 2)).ToArray();
        WriteCompactJson("objects/wall/wall.json", new
        {
            format = "s2port.wall/1",
            fields = "x_vel, y_vel per fragment",
            fromLeft = Speeds(RomMap.WallSpeedsRight),
            fromRight = Speeds(RomMap.WallSpeedsLeft),
        });
        _manifest.Items.Add(new ManifestItem("objects/wall/wall.json", "object-data",
            [SourceRef.At(RomMap.WallSpeedsRight, "raw", size: RomMap.WallSpeedsSize), SourceRef.At(RomMap.WallSpeedsLeft, "raw", size: RomMap.WallSpeedsSize)]));
        WriteCompactJson("objects/ledge/ledge.json", new
        {
            format = "s2port.ledge/1",
            slope = Enumerable.Range(0, RomMap.LedgeSlopeSize).Select(i => (int)(sbyte)rom.U8(RomMap.LedgeSlope + i)).ToArray(),
            delays = Enumerable.Range(0, RomMap.LedgeDelaysSize).Select(i => (int)rom.U8(RomMap.LedgeDelays + i)).ToArray(),
        });
        _manifest.Items.Add(new ManifestItem("objects/ledge/ledge.json", "object-data",
            [SourceRef.At(RomMap.LedgeSlope, "raw", size: RomMap.LedgeSlopeSize), SourceRef.At(RomMap.LedgeDelays, "raw", size: RomMap.LedgeDelaysSize)]));
        WriteCompactJson("objects/bridge/bridge.json", new
        {
            format = "s2port.bridge/1",
            depressionFirstSubtype = 8,
            depression = Enumerable.Range(0, RomMap.BridgeDepressionSize).Select(i => (int)rom.U8(RomMap.BridgeDepression + i)).ToArray(),
            bend = Enumerable.Range(0, RomMap.BridgeBendSize).Select(i => (int)rom.U8(RomMap.BridgeBend + i)).ToArray(),
        });
        _manifest.Items.Add(new ManifestItem("objects/bridge/bridge.json", "object-data",
            [SourceRef.At(RomMap.BridgeDepression, "raw", size: RomMap.BridgeDepressionSize), SourceRef.At(RomMap.BridgeBend, "raw", size: RomMap.BridgeBendSize)]));

        // Manifest is written last: its presence marks a complete extraction.
        _manifest.Save(Path.Combine(outDir, ManifestFileName));

        if (previews) RenderPreviews();
        log?.Invoke($"Wrote {_manifest.Items.Count} items to {outDir}");
    }

    // ---------------------------------------------------------------- palettes

    public sealed record PaletteEntry(int Index, int Source, int StartColour, ushort[] Words);

    private List<PaletteEntry> ExtractPalettes()
    {
        var result = new List<PaletteEntry>();
        for (int i = 0; i < RomMap.PalPointerCount; i++)
        {
            int e = RomMap.PalPointers + i * 8;
            int src = (int)rom.U32(e);
            int dest = rom.U16(e + 4);
            int colours = (rom.U16(e + 6) + 1) * 2;
            int start = (dest - RomMap.PaletteRamBase) / 2;

            var words = new ushort[colours];
            for (int c = 0; c < colours; c++) words[c] = rom.U16(src + c * 2);
            var entry = new PaletteEntry(i, src, start, words);
            result.Add(entry);

            string name = $"palettes/pal_{i:X2}";
            WriteJson(name + ".json", new
            {
                index = i,
                startColour = start,
                startLine = start / 16,
                colourCount = colours,
                colours = words.Select(w => new { word = $"0x{w:X4}", rgb = new MdColor(w).ToHex() }),
            });
            WritePaletteSwatch(name + ".png", words);

            _manifest.Items.Add(new ManifestItem(name + ".json", "palette",
                [SourceRef.At(e, "raw", size: 8), SourceRef.At(src, "raw", size: colours * 2)],
                $"PalPointers[{i:X2}] -> CRAM colour {start}"));
            _manifest.Items.Add(new ManifestItem(name + ".png", "palette-swatch",
                [SourceRef.At(src, "raw", size: colours * 2)], "16 colours per row, preview only"));
        }
        log?.Invoke($"Palettes: {result.Count}");
        return result;
    }

    private void WritePaletteSwatch(string relPath, ushort[] words)
    {
        const int cell = 16;
        int cols = 16, rows = (words.Length + 15) / 16;
        var pixels = new byte[cols * cell * rows * cell];
        for (int c = 0; c < words.Length; c++)
        for (int y = 0; y < cell; y++)
        for (int x = 0; x < cell; x++)
            pixels[((c / 16) * cell + y) * cols * cell + (c % 16) * cell + x] = (byte)c;
        IndexedPng.Write(Path.Combine(outDir, relPath), cols * cell, rows * cell, pixels,
            words.Select(w => new MdColor(w).ToRgb()).ToList());
    }

    /// <summary>Builds the 64-colour CRAM state a zone sees: base palette then zone palette.</summary>
    public static ushort[] ComposeCram(IReadOnlyList<PaletteEntry> palettes, params int[] ids)
    {
        var cram = new ushort[64];
        foreach (int id in ids)
        {
            var p = palettes[id];
            for (int c = 0; c < p.Words.Length && p.StartColour + c < 64; c++) cram[p.StartColour + c] = p.Words[c];
        }
        return cram;
    }

    // ---------------------------------------------------------------- level tilesets

    private void ExtractZones(List<PaletteEntry> palettes)
    {
        for (int zone = 0; zone < RomMap.ZoneCount; zone++)
        {
            int e = RomMap.LevelArtPointers + zone * 12;
            int artAddr = (int)(rom.U32(e) & 0xFFFFFF);
            int blockAddr = (int)(rom.U32(e + 4) & 0xFFFFFF);
            int palId = rom.U8(e + 8);
            int chunkAddr = (int)(rom.U32(e + 8) & 0xFFFFFF);
            var sources = new List<SourceRef> { SourceRef.At(e, "raw", size: 12) };

            // Art, with LoadZoneTiles' per-zone overlays.
            var art = Kosinski.Decompress(rom, artAddr, out int artPacked).ToList();
            sources.Add(SourceRef.At(artAddr, "kosinski", artPacked, art.Count));
            foreach (var ov in RomMap.ArtOverlays.Where(o => o.Zone == zone))
            {
                if (ov.Source is int s)
                {
                    var extra = Kosinski.Decompress(rom, s, out int packed);
                    sources.Add(SourceRef.At(s, "kosinski", packed, extra.Length));
                    while (art.Count < ov.DestOffset + extra.Length) art.Add(0);
                    for (int i = 0; i < extra.Length; i++) art[ov.DestOffset + i] = extra[i];
                }
                art = art.Take(ov.FinalLength).ToList();
            }
            var artBytes = art.ToArray();
            int tileCount = artBytes.Length / Tiles.BytesPerTile;

            // 16x16 blocks as the engine sees them in RAM at $FFFF9000.
            var blocks = Kosinski.Decompress(rom, blockAddr, out int blkPacked).ToList();
            var blockSources = new List<SourceRef> { SourceRef.At(blockAddr, "kosinski", blkPacked, blocks.Count) };
            foreach (var (_, src, dest) in RomMap.BlockOverlays.Where(o => o.Zone == zone))
            {
                var extra = Kosinski.Decompress(rom, src, out int packed);
                blockSources.Add(SourceRef.At(src, "kosinski", packed, extra.Length));
                Patch(blocks, dest, extra);
            }
            // LoadAnimatedBlocks (0x44810): raw words copied into the top of the block buffer (1P path).
            int aniEntry = RomMap.AnimatedBlocks + rom.U16(RomMap.AnimatedBlocks + zone * 2);
            int aniDest = rom.U16(aniEntry);
            if (aniDest != 0)
            {
                int aniBytes = (rom.U16(aniEntry + 2) + 1) * 2;
                blockSources.Add(SourceRef.At(aniEntry, "raw", size: aniBytes + 4));
                Patch(blocks, aniDest, rom.Slice(aniEntry + 4, aniBytes).ToArray());
            }
            sources.AddRange(blockSources);
            var lineOf = PaletteLinesFromBlocks(blocks, tileCount, out int outOfRange, out int unreferenced);

            var cram = ComposeCram(palettes, RomMap.LevelBasePalette, palId);
            string name = $"zones/zone_{zone:X2}/tiles";
            var (w, h, pixels) = Tiles.ToSheet(artBytes, 16, t => lineOf[t]);
            IndexedPng.Write(Path.Combine(outDir, name + ".png"), w, h, pixels,
                cram.Select(c => new MdColor(c).ToRgb()).ToList(), transparentIndices: [0, 16, 32, 48]);

            RomMap.RetailZoneHints.TryGetValue(zone, out var hint);
            WriteJson($"zones/zone_{zone:X2}/tiles.json", new
            {
                zone,
                retailZoneHint = hint,
                tileCount,
                tilesPerRow = 16,
                pixelEncoding = "index = paletteLine * 16 + colour; raw 4bpp value = index & 15",
                palettes = new[] { RomMap.LevelBasePalette, palId },
                paletteOverridesByAct = RomMap.PaletteOverrides
                    .Where(kv => kv.Key >> 8 == zone)
                    .ToDictionary(kv => $"act{kv.Key & 0xFF}", kv => kv.Value),
                paletteLineSource = "most frequent line among 16x16 block mappings referencing the tile; unreferenced tiles use line 2",
                unreferencedTiles = unreferenced,
                blockRefsOutOfRange = outOfRange,
            });

            _manifest.Items.Add(new ManifestItem(name + ".png", "tileset", sources,
                $"Level art for zone {zone:X2}{(hint is null ? "" : $" (retail slot: {hint})")}, {tileCount} tiles"));

            // 16x16 blocks.
            var blockWords = ToWords(blocks);
            string zoneDir = $"zones/zone_{zone:X2}";
            WriteCompactJson($"{zoneDir}/blocks.json", LevelJson.BlocksToJson(blockWords));
            _manifest.Items.Add(new ManifestItem($"{zoneDir}/blocks.json", "blocks", blockSources,
                "Index = RAM block number; gaps between sources are zero, as in RAM"));

            // 128x128 chunks (zone default).
            var chunkWords = ToWords(Kosinski.Decompress(rom, chunkAddr, out int chkPacked));
            WriteCompactJson($"{zoneDir}/chunks.json", LevelJson.ChunksToJson(chunkWords));
            _manifest.Items.Add(new ManifestItem($"{zoneDir}/chunks.json", "chunks",
                [SourceRef.At(chunkAddr, "kosinski", chkPacked, chunkWords.Length * 2)]));

            bool hasCollision = ExtractCollisionIndex(zone, zoneDir);
            ExtractAnimatedTiles(zone, zoneDir, palettes, palId);
            ExtractPaletteCycle(zone, zoneDir);

            for (int act = 0; act < 2; act++)
                ExtractAct(zone, act, palettes, palId, artBytes, blockWords, chunkWords, hasCollision);
        }
        log?.Invoke($"Zones: {RomMap.ZoneCount} (tiles, blocks, chunks, 2 acts each)");
    }

    /// <summary>
    /// Pattern word: bit15 priority, bits 13-14 palette line, bit12 vflip, bit11 hflip, bits 0-10 tile.
    /// Returns each tile's most frequent line; tiles never referenced default to line 2.
    /// </summary>
    private static int[] PaletteLinesFromBlocks(List<byte> blocks, int tileCount, out int outOfRange, out int unreferenced)
    {
        var counts = new int[tileCount, 4];
        outOfRange = 0;
        for (int i = 0; i + 1 < blocks.Count; i += 2)
        {
            int word = (blocks[i] << 8) | blocks[i + 1];
            int tile = word & 0x7FF, line = (word >> 13) & 3;
            if (tile < tileCount) counts[tile, line]++;
            else outOfRange++;
        }
        var result = new int[tileCount];
        unreferenced = 0;
        for (int t = 0; t < tileCount; t++)
        {
            int best = 2, bestCount = 0;
            for (int l = 0; l < 4; l++)
                if (counts[t, l] > bestCount) { best = l; bestCount = counts[t, l]; }
            if (bestCount == 0) unreferenced++;
            result[t] = best;
        }
        return result;
    }

    private void ExtractAct(int zone, int act, List<PaletteEntry> palettes, int zonePalId,
        byte[] art, ushort[] blockWords, ushort[] zoneChunks, bool hasCollision)
    {
        int zoneAct = (zone << 8) | act;
        string actDir = $"zones/zone_{zone:X2}/act_{act + 1}";

        // Layout: word offset table indexed by zone*2+act (LevelDataLoad, 0x10696).
        int entry = RomMap.LevelLayoutOffsets + (zone * 2 + act) * 2;
        int layoutAddr = RomMap.LevelLayoutOffsets + rom.U16(entry);
        var layoutRam = Kosinski.Decompress(rom, layoutAddr, out int layoutPacked);
        var (fg, bg) = LevelData.SplitLayout(layoutRam);
        WriteCompactJson($"{actDir}/layout.json", LevelJson.LayoutToJson(fg, bg));
        _manifest.Items.Add(new ManifestItem($"{actDir}/layout.json", "layout",
            [SourceRef.At(entry, "raw", size: 2), SourceRef.At(layoutAddr, "kosinski", layoutPacked, layoutRam.Length)]));

        // Object and ring placement. Offsets are sign-extended (adda.w / lea (a1,d0.w)).
        int idx = (zone * 2 + act) * 2;
        int objAddr = RomMap.ObjectLayouts + (short)rom.U16(RomMap.ObjectLayouts + idx);
        var objects = new List<ObjectPlacement>();
        for (int a = objAddr; rom.U16(a) != 0xFFFF; a += 6)
            objects.Add(ObjectPlacement.FromRaw(rom.U16(a), rom.U16(a + 2), rom.U8(a + 4), rom.U8(a + 5)));
        // listAddress: the original keeps ROM pointers into this list in RAM ($F770/$F774); the game reproduces them
        // from this number. boundaryX: the x word of the record before the list, which the leftward scan compares.
        WriteCompactJson($"{actDir}/objects.json", LevelJson.ObjectsToJson(objects, objAddr, rom.U16(objAddr - 6)));
        _manifest.Items.Add(new ManifestItem($"{actDir}/objects.json", "objects",
            [SourceRef.At(RomMap.ObjectLayouts + idx, "raw", size: 2), SourceRef.At(objAddr, "raw", size: objects.Count * 6 + 2)]));

        int ringAddr = RomMap.RingLayouts + (short)rom.U16(RomMap.RingLayouts + idx);
        var rings = new List<RingGroup>();
        for (int a = ringAddr; (rom.U16(a) & 0x8000) == 0; a += 4)
            rings.Add(RingGroup.FromRaw(rom.U16(a), rom.U16(a + 2)));
        WriteCompactJson($"{actDir}/rings.json", LevelJson.RingsToJson(rings));
        _manifest.Items.Add(new ManifestItem($"{actDir}/rings.json", "rings",
            [SourceRef.At(RomMap.RingLayouts + idx, "raw", size: 2), SourceRef.At(ringAddr, "raw", size: rings.Count * 4 + 2)]));

        // Optional per-act chunk override.
        string chunksRef = "../chunks.json";
        var chunks = zoneChunks;
        if (RomMap.ChunkOverrides.TryGetValue(zoneAct, out int chunkOverride))
        {
            chunks = ToWords(Kosinski.Decompress(rom, chunkOverride, out int packed));
            chunksRef = "chunks.json";
            WriteCompactJson($"{actDir}/chunks.json", LevelJson.ChunksToJson(chunks));
            _manifest.Items.Add(new ManifestItem($"{actDir}/chunks.json", "chunks",
                [SourceRef.At(chunkOverride, "kosinski", packed, chunks.Length * 2)], "Per-act override (LevelDataLoad 0x10648)"));
        }

        int palId = RomMap.PaletteOverrides.TryGetValue(zoneAct, out int po) ? po : zonePalId;
        var level = new LevelData
        {
            Art = art, BlockWords = blockWords, ChunkWords = chunks,
            Foreground = fg, Background = bg,
            Cram = ComposeCram(palettes, RomMap.LevelBasePalette, palId),
        };
        int unresolved = level.UnresolvedBlockRefs();
        int unresolvedChunks = level.UnresolvedChunkRefs();

        WriteCompactJson($"{actDir}/level.json", new
        {
            format = "s2port.level/1",
            zone,
            act = act + 1,
            tiles = "../tiles.png",
            blocks = "../blocks.json",
            chunks = chunksRef,
            layout = "layout.json",
            collision = hasCollision ? "../collision.json" : null,
            animatedTiles = "../animated_tiles.json",
            paletteCycle = File.Exists(Path.Combine(outDir, $"zones/zone_{zone:X2}/palette_cycle.json")) ? "../palette_cycle.json" : null,
            plcs = new
            {
                fromTitleScreen = new[] { 0x00 },   // title screen init (LoadPLC2 at 0x48EC); stays in VRAM
                onLoad = LevelPlcs(zone, act),
                afterTitleCard = new[] { RomMap.TitleCardStandardPlc, (int)rom.U8(RomMap.AnimalPlcTable + zone) },
                note = "onLoad: queued by Level/LevelDataLoad in this order for 1-player Sonic (2P / Tails-alone add 06-09, 0x4CDE). " +
                       "afterTitleCard: queued by the title card as it leaves (0x171FC). Others are requested by objects at runtime.",
                lists = "../../../plc/plc_lists.json",
            },
            objects = "objects.json",
            rings = "rings.json",
            bounds = new
            {
                minX = rom.U16(RomMap.LevelSizes + (zone * 2 + act) * 8),
                maxX = rom.U16(RomMap.LevelSizes + (zone * 2 + act) * 8 + 2),
                minY = rom.U16(RomMap.LevelSizes + (zone * 2 + act) * 8 + 4),
                maxY = rom.U16(RomMap.LevelSizes + (zone * 2 + act) * 8 + 6),
                source = $"LevelSizeLoad table 0x{RomMap.LevelSizes:X6}",
            },
            start = new
            {
                x = rom.U16(RomMap.StartLocations + (zone * 2 + act) * 4),
                y = rom.U16(RomMap.StartLocations + (zone * 2 + act) * 4 + 2),
            },
            palettes = new[] { RomMap.LevelBasePalette, palId }.Select(p => $"../../../palettes/pal_{p:X2}.json"),
            unresolvedBlockRefs = unresolved,
            unresolvedChunkRefs = unresolvedChunks,
            notes = unresolved + unresolvedChunks == 0 ? null :
                "Some references point past the loaded block/chunk data (RAM $FFFF9000 / $FFFF0000); they render as empty. " +
                "Only seen in unused zone slots.",
        });
        _manifest.Items.Add(new ManifestItem($"{actDir}/level.json", "level", []));
    }

    // ---------------------------------------------------------------- audio: 68k SMPS driver

    /// <summary>
    /// What the ported driver (src/SonicBros3/Audio/Smps68kDriver.cs) reads: the driver's data block and all sound data at
    /// their ROM addresses (the driver keeps absolute pointers in RAM, so the port's RAM matches the original's), the
    /// constant tables inside the driver code, and the zones' music list.
    /// </summary>
    private void ExtractAudio68kPlayerData(string dir)
    {
        var segments = new List<object>();
        void Segment(string file, int start, int end, string what)
        {
            File.WriteAllBytes(Path.Combine(outDir, dir, file), rom.Slice(start, end - start).ToArray());
            segments.Add(new { file, baseAddress = start, length = end - start, what });
            _manifest.Items.Add(new ManifestItem($"{dir}/{file}", "smps-bank", [SourceRef.At(start, "raw", size: end - start)]));
        }
        Segment("bank_tables.bin", Smps68k.DataBlockStart, Smps68k.DataBlockEnd, "pointer block, PSG envelopes, tempos, priorities");
        Segment("bank_sounds.bin", Smps68k.SoundDataStart, Smps68k.SoundDataEnd, "all songs and SFX");
        int[] Words(int a, int n) => Enumerable.Range(0, n).Select(i => (int)rom.U16(a + i * 2)).ToArray();
        int[] Bytes(int a, int n) => rom.Slice(a, n).ToArray().Select(b => (int)b).ToArray();
        WriteCompactJson($"{dir}/player.json", new
        {
            format = "s2port.smps68k-player/1",
            segments,
            // The driver indexes these with (note + transpose) & $7F, so 128 entries are exported (past 96 / 70 the
            // original reads whatever follows the table; real songs stay inside).
            fmFrequencies = Words(Smps68k.FmFrequencyTable, 128),
            psgFrequencies = Words(Smps68k.PsgFrequencyTable, 128),
            dacPitches = Bytes(Smps68k.TimpaniPitchTable, 8),
            carrierMasks = Bytes(Smps68k.CarrierMaskTable, 8),
            voiceRegisters = Bytes(Smps68k.VoiceRegisterTable, 24),
            levelMusic = Bytes(RomMap.LevelMusicList, 30),
            levelMusic2P = Bytes(RomMap.LevelMusicList2P, 30),
        });
    }

    private void ExtractAudio68k()
    {
        const string dir = "audio/smps68k";
        int musicIndex = (int)rom.U32(Smps68k.MusicIndexPtr);
        int sfxIndex = (int)rom.U32(Smps68k.SfxIndexPtr);
        int specialIndex = (int)rom.U32(Smps68k.SpecialSfxIndexPtr);
        var files = new Dictionary<int, string>(); // ROM address -> exported file (dedupes shared IDs)
        var problems = new List<string>();

        string Export(int id, int addr, bool music)
        {
            if (files.TryGetValue(addr, out var existing)) return existing;
            var snd = music ? Smps68k.ParseMusic(rom, addr) : Smps68k.ParseSfx(rom, addr);
            problems.AddRange(snd.Problems.Select(p => $"{id:X2}: {p}"));
            string name = $"{(music ? "music" : "sfx")}/{(music ? "song" : "sfx")}_{id:X2}";
            File.WriteAllBytes(Path.Combine(outDir, dir, name + ".smps"), rom.Slice(addr, snd.Length).ToArray());
            var voices = Enumerable.Range(0, snd.VoiceCount)
                .Select(v => Smps68k.DecodeVoice(rom.Slice(addr + snd.VoiceOffset + v * Smps68k.VoiceSize, Smps68k.VoiceSize)));
            WriteCompactJson($"{dir}/{name}.json", new
            {
                format = "s2port.smps68k-sound/1",
                kind = music ? "music" : "sfx",
                firstId = id,
                data = Path.GetFileName(name) + ".smps",
                length = snd.Length,
                tempoDivider = snd.TempoDivider,
                tempo = snd.Tempo,
                voiceOffset = snd.VoiceOffset,
                channels = snd.Channels,
                voices,
            });
            _manifest.Items.Add(new ManifestItem($"{dir}/{name}.smps", music ? "music" : "sfx",
                [SourceRef.At(addr, "raw", size: snd.Length)], "SMPS 68k (Sonic 1 format), relative pointers"));
            return files[addr] = Path.GetFileName(name) + ".smps";
        }

        Directory.CreateDirectory(Path.Combine(outDir, dir, "music"));
        Directory.CreateDirectory(Path.Combine(outDir, dir, "sfx"));
        var musicMap = Enumerable.Range(0, Smps68k.MusicCount).ToDictionary(
            i => $"{Smps68k.FirstMusic + i:X2}", i => "music/" + Export(Smps68k.FirstMusic + i, (int)rom.U32(musicIndex + i * 4), true));
        var sfxMap = Enumerable.Range(0, Smps68k.SfxCount).ToDictionary(
            i => $"{Smps68k.FirstSfx + i:X2}", i => "sfx/" + Export(Smps68k.FirstSfx + i, (int)rom.U32(sfxIndex + i * 4), false));
        sfxMap[$"{Smps68k.SpecialSfx:X2}"] = "sfx/" + Export(Smps68k.SpecialSfx, (int)rom.U32(specialIndex), false);
        // $E0-$F7 index a second table at 0x137342, which is the SFX table from entry $D1 on; only $D1 (= $E0) is valid.
        sfxMap["D1"] = sfxMap["E0"] = "sfx/" + Export(0xD1, (int)rom.U32(sfxIndex + 0x31 * 4), false);

        // DAC samples from the Kosinski Z80 driver.
        var z80 = Kosinski.Decompress(rom, RomMap.Z80DacDriver, out int z80Packed);
        var deltas = z80.AsSpan(Smps68k.Z80DeltaTable, 16);
        var samples = new List<object>();
        for (int i = 0; i < Smps68k.Z80SampleCount; i++)
        {
            int e = Smps68k.Z80SampleTable + i * 8;
            int ptr = z80[e] | (z80[e + 1] << 8), len = z80[e + 2] | (z80[e + 3] << 8), pitch = z80[e + 4];
            var pcm = Smps68k.DecodeDpcm(z80.AsSpan(ptr, len), deltas);
            string wav = $"dac/dac_{0x81 + i:X2}.wav";
            Wav.WriteUnsigned8Mono(Path.Combine(outDir, dir, wav), pcm, (int)Math.Round(Smps68k.SampleRate(pitch)));
            samples.Add(new { id = 0x81 + i, wav, z80Address = ptr, dpcmBytes = len, pitch, sampleRate = Math.Round(Smps68k.SampleRate(pitch)) });
        }
        var timpaniPitches = Enumerable.Range(0, 4).ToDictionary(
            i => $"{0x88 + i:X2}", i => new { plays = 0x83, pitch = (int)rom.U8(Smps68k.TimpaniPitchTable + i),
                sampleRate = Math.Round(Smps68k.SampleRate(rom.U8(Smps68k.TimpaniPitchTable + i))) });
        _manifest.Items.Add(new ManifestItem($"{dir}/dac", "dac-samples",
            [SourceRef.At(RomMap.Z80DacDriver, "kosinski", z80Packed, z80.Length), SourceRef.At(Smps68k.TimpaniPitchTable, "raw", size: 4)]));

        int speedUp = (int)rom.U32(Smps68k.SpeedUpTablePtr);
        WriteCompactJson($"{dir}/driver.json", new
        {
            format = "s2port.smps68k-driver/1",
            description = "Sonic 1-style 68k SMPS driver used in zones >= $11 ($FFFFE5 = 1). Requests: music/SFX ID to $FFFFF00A/B.",
            music = musicMap,
            sfx = sfxMap,
            speedUpTempos = rom.Slice(speedUp, 8).ToArray().Select(b => (int)b),
            dac = new
            {
                samples,
                timpani = timpaniPitches,
                note = "Notes $88-$8B write their pitch to the Z80 (sample $83's pitch byte at $00EA) and play $83; " +
                       "the pitch persists for later $83 notes. Rates are computed from Z80 cycle counts (unverified).",
            },
            commands = new Dictionary<string, string>
            {
                // Dispatch at 0x130676 (IDs $F9-$FD); handlers read and labelled from the code.
                ["F9"] = "fade out music (0x130BE6)",
                ["FA"] = "play SEGA PCM, 68k busy loop (0x130694)",
                ["FB"] = "speed-up tempo (0x130D20)",
                ["FC"] = "normal tempo (0x130D4E)",
                ["FD"] = "stop all sound (0x130C9E)",
            },
            segaPcm = new { file = "sega.pcm", format = "8-bit unsigned mono", sampleRate = (int?)null, note = "rate not yet measured" },
            problems,
        });
        File.WriteAllBytes(Path.Combine(outDir, dir, "sega.pcm"), rom.Slice(0x137E80, 0x6978).ToArray());
        ExtractAudio68kPlayerData(dir);
        _manifest.Items.Add(new ManifestItem($"{dir}/sega.pcm", "pcm", [SourceRef.At(0x137E80, "raw", size: 0x6978)]));
        log?.Invoke($"68k SMPS: {files.Count} unique sounds, {samples.Count} DAC samples, {problems.Count} parse problems");
    }

    // ---------------------------------------------------------------- audio: Sonic 2 Z80 SMPS driver

    private void ExtractAudioZ80()
    {
        const string dir = "audio/smpsz80";
        Directory.CreateDirectory(Path.Combine(outDir, dir, "music"));
        Directory.CreateDirectory(Path.Combine(outDir, dir, "sfx"));
        Directory.CreateDirectory(Path.Combine(outDir, dir, "dac"));
        var driver = SmpsZ80.DecompressDriver(rom);
        var problems = new List<string>();

        // The driver's code and tables as the Z80 sees them (its RAM from 0), and the ROM banks it maps at 0x8000:
        // the player (Audio/SmpsZ80Driver) reads both at their Z80 addresses.
        File.WriteAllBytes(Path.Combine(outDir, dir, "driver.bin"), driver);
        _manifest.Items.Add(new ManifestItem($"{dir}/driver.bin", "z80-code",
            [SourceRef.At(SmpsZ80.DriverSource, "saxman", SmpsZ80.DriverCompressedLength, driver.Length)], "the Z80 driver, decompressed"));
        foreach (int bankBase in SmpsZ80.Banks)
        {
            File.WriteAllBytes(Path.Combine(outDir, dir, $"bank_{bankBase:X6}.bin"), rom.Slice(bankBase, 0x8000).ToArray());
            _manifest.Items.Add(new ManifestItem($"{dir}/bank_{bankBase:X6}.bin", "z80-bank", [SourceRef.At(bankBase, "raw", size: 0x8000)]));
        }
        var exported = new Dictionary<int, string>();

        string Write(string kind, int id, SmpsZ80.Image img, SmpsZ80.Sound snd, object extra)
        {
            if (exported.TryGetValue(img.RomAddress, out var existing)) return existing;
            problems.AddRange(snd.Problems.Select(p => $"{id:X2}: {p}"));
            string name = $"{kind}/{(kind == "music" ? "song" : "sfx")}_{id:X2}";
            File.WriteAllBytes(Path.Combine(outDir, dir, name + ".smps"), img.Data.AsSpan(0, snd.Length).ToArray());
            int voiceStart = snd.VoicePointer - img.Base;
            var voices = voiceStart >= 0
                ? Enumerable.Range(0, snd.VoiceCount).Select(v => Smps68k.DecodeVoice(img.Data.AsSpan(voiceStart + v * Smps68k.VoiceSize, Smps68k.VoiceSize))).ToList()
                : [];
            WriteCompactJson($"{dir}/{name}.json", new
            {
                format = "s2port.smpsz80-sound/1",
                kind,
                firstId = id,
                data = Path.GetFileName(name) + ".smps",
                baseAddress = img.Base,
                pointers = "absolute little-endian Z80 addresses; file offset = address - baseAddress",
                length = snd.Length,
                tempoDivider = snd.TempoDivider,
                tempo = snd.Tempo,
                voicePointer = snd.VoicePointer,
                channels = snd.Channels,
                voices,
                rom = extra,
            });
            _manifest.Items.Add(new ManifestItem($"{dir}/{name}.smps", kind,
                [SourceRef.At(img.RomAddress, img.CompressedSize is null ? "raw" : "saxman", img.CompressedSize, snd.Length)],
                "SMPS Z80 (Sonic 2 format)"));
            return exported[img.RomAddress] = $"{kind}/{Path.GetFileName(name)}.smps";
        }

        var music = new Dictionary<string, object>();
        for (int i = 0; i < SmpsZ80.MusicCount; i++)
        {
            var song = SmpsZ80.LoadSong(rom, driver, i);
            var snd = SmpsZ80.ParseMusic(song.Image);
            string file = Write("music", song.Id, song.Image, snd,
                new { flags = song.Flags, bank = song.Bank, uncompressed = song.Uncompressed, palSpeedUpDisabled = song.PalSpeedUpDisabled });
            music[$"{song.Id:X2}"] = new { file, speedUpTempo = (int)driver[SmpsZ80.SpeedUpTempos + i], palSpeedUpDisabled = song.PalSpeedUpDisabled };
        }

        // SFX share voices and code, so the used part of the SFX bank is exported once with its base address.
        int sfxTable = SmpsZ80.BankAddress(SmpsZ80.SfxBank, SmpsZ80.SfxTableWindow);
        var bank = new SmpsZ80.Image(rom.Slice(SmpsZ80.SfxBank, 0x8000).ToArray(), 0x8000, SmpsZ80.SfxBank, null);
        var sfxSounds = Enumerable.Range(0, SmpsZ80.SfxCount).Select(i =>
        {
            int ptr = rom.U8(sfxTable + i * 2) | (rom.U8(sfxTable + i * 2 + 1) << 8);
            return (Id: 0xA0 + i, Sound: SmpsZ80.ParseSfx(bank, ptr));
        }).ToList();
        int regionStart = sfxSounds.Min(s => s.Sound.Start), regionEnd = sfxSounds.Max(s => s.Sound.End);
        problems.AddRange(sfxSounds.SelectMany(s => s.Sound.Problems.Select(p => $"{s.Id:X2}: {p}")));
        File.WriteAllBytes(Path.Combine(outDir, dir, "sfx/sfx_bank.smps"), bank.Data.AsSpan(regionStart - 0x8000, regionEnd - regionStart).ToArray());
        _manifest.Items.Add(new ManifestItem($"{dir}/sfx/sfx_bank.smps", "sfx",
            [SourceRef.At(SmpsZ80.BankAddress(SmpsZ80.SfxBank, regionStart), "raw", size: regionEnd - regionStart)],
            "All Z80-driver SFX; shared voices and calls between SFX"));
        var sfx = sfxSounds.ToDictionary(s => $"{s.Id:X2}", s =>
        {
            var v = s.Sound;
            return (object)new
            {
                header = v.Header,
                priority = (int)driver[SmpsZ80.SfxPriorities + s.Id - 0xA0],
                tempoDivider = v.TempoDivider,
                voicePointer = v.VoiceCount > 0 ? v.VoicePointer : (int?)null,
                channels = v.Channels,
                voices = Enumerable.Range(0, v.VoiceCount).Select(k =>
                    Smps68k.DecodeVoice(bank.Data.AsSpan(v.VoicePointer - 0x8000 + k * Smps68k.VoiceSize, Smps68k.VoiceSize))),
            };
        });
        WriteCompactJson($"{dir}/sfx/sfx.json", new
        {
            format = "s2port.smpsz80-sfx/1",
            data = "sfx_bank.smps",
            baseAddress = regionStart,
            pointers = "absolute little-endian Z80 addresses; file offset = address - baseAddress",
            sfx,
        });

        // DAC: 7 DPCM samples in the DAC bank, and a playlist mapping notes to {sample, pitch}.
        var deltas = driver.AsSpan(SmpsZ80.DacDeltaTable, 16);
        var samples = new List<object>();
        for (int i = 0; i < SmpsZ80.DacSampleCount; i++)
        {
            int e = SmpsZ80.DacSampleTable + i * 4;
            int ptr = driver[e] | (driver[e + 1] << 8), len = driver[e + 2] | (driver[e + 3] << 8);
            int romAddr = SmpsZ80.BankAddress(SmpsZ80.DacBank, ptr);
            var pcm = Smps68k.DecodeDpcm(rom.Slice(romAddr, len), deltas);
            File.WriteAllBytes(Path.Combine(outDir, dir, $"dac/sample_{0x81 + i:X2}.dpcm"), rom.Slice(romAddr, len).ToArray());
            Wav.WriteUnsigned8Mono(Path.Combine(outDir, dir, $"dac/sample_{0x81 + i:X2}.pcm.wav"), pcm, 8000);
            samples.Add(new { id = 0x81 + i, dpcm = $"dac/sample_{0x81 + i:X2}.dpcm", decoded = $"dac/sample_{0x81 + i:X2}.pcm.wav", romAddress = romAddr, bytes = len });
            _manifest.Items.Add(new ManifestItem($"{dir}/dac/sample_{0x81 + i:X2}.dpcm", "dac-sample", [SourceRef.At(romAddr, "raw", size: len)]));
        }
        var notes = Enumerable.Range(0, SmpsZ80.DacPlaylistCount).ToDictionary(
            i => $"{0x81 + i:X2}",
            i =>
            {
                int sample = driver[SmpsZ80.DacPlaylist + i * 2], pitch = driver[SmpsZ80.DacPlaylist + i * 2 + 1];
                return new { sample, pitch, sampleRate = Math.Round(SmpsZ80.SampleRate(pitch)) };
            });

        File.WriteAllBytes(Path.Combine(outDir, dir, "sega.pcm"), rom.Slice(SmpsZ80.BankAddress(0x120000, 0x9E8C), 0x30BA).ToArray());
        _manifest.Items.Add(new ManifestItem($"{dir}/sega.pcm", "pcm", [SourceRef.At(SmpsZ80.BankAddress(0x120000, 0x9E8C), "raw", size: 0x30BA)]));

        WriteCompactJson($"{dir}/driver.json", new
        {
            format = "s2port.smpsz80-driver/1",
            description = "Sonic 2 Z80 SMPS driver: title screen and zones < $11 ($FFFFE5 = 0). Requests: $FFFFE0 (music), $FFFFE1/E2 (SFX).",
            music,
            sfx = "sfx/sfx.json",
            dac = new
            {
                samples,
                notes,
                note = "Each DAC note plays {sample} at {pitch}; rate = 3579545 / ((262 + 26*pitch) / 2), computed from Z80 " +
                       "cycle counts (unverified). The .pcm.wav previews are decoded at a nominal 8000 Hz.",
            },
            specialSfx = new Dictionary<string, string>
            {
                ["B5"] = "alternates with $CE on every other request (0x092F)",
                ["DA"] = "toggles; only every other request plays (0x0943)",
                ["E0"] = "repeated requests within 60 frames raise the pitch, up to 12 steps (0x0954)",
            },
            commands = new Dictionary<string, string>
            {
                ["F8"] = "stop SFX (0x0A3C)", ["F9"] = "fade out music (0x0ABF)", ["FA"] = "play SEGA PCM (0x06EF)",
                ["FB"] = "speed-up tempo (0x0BBE)", ["FC"] = "normal tempo (0x0BCB)", ["FD"] = "stop all (0x0B4E)",
            },
            segaPcm = new { file = "sega.pcm", format = "8-bit unsigned mono", sampleRate = (int?)null, note = "rate not yet measured" },
            problems,
        });
        log?.Invoke($"Z80 SMPS: {exported.Count} unique sounds, {samples.Count} DAC samples, {problems.Count} parse problems");
    }

    // ---------------------------------------------------------------- objects: Sonic

    /// <summary>
    /// Sonic's animation scripts (Sonic_Animate 0x1F138: tables at 0x1F41A and 0x1F5DC). Each table holds signed
    /// word offsets (adda.w) to scripts; a script runs until a command byte >= $F0 (plus its parameter).
    /// </summary>
    private void ExtractSonicAnimations()
    {
        object Table(int baseAddr, out List<SourceRef> sources)
        {
            int count = rom.U16(baseAddr) / 2;
            var scripts = new List<int[]>();
            for (int i = 0; i < count; i++)
            {
                int a = baseAddr + (short)rom.U16(baseAddr + i * 2);
                var bytes = new List<int> { rom.U8(a) };
                int p = a + 1;
                while (rom.U8(p) < 0xF0) bytes.Add(rom.U8(p++));
                int cmd = rom.U8(p);
                bytes.Add(cmd);
                if (cmd != 0xFF) bytes.Add(rom.U8(p + 1));
                scripts.Add(bytes.ToArray());
            }
            sources = [SourceRef.At(baseAddr, "raw", size: count * 2)];
            return new { tableAddress = $"0x{baseAddr:X6}", scripts };
        }

        // The scripts are also exported as one raw block (both tables and every script, 0x1F41A-0x1F65E):
        // Sonic_Animate can index past the end of a script when switching between walk and run, and then reads the
        // bytes that follow it in ROM. Indexing into this block reproduces that exactly.
        const int blockStart = 0x1F41A, blockEnd = 0x1F65E;
        WriteCompactJson("objects/sonic/animations.json", new
        {
            format = "s2port.animations/2",
            description = "data = raw bytes from blockAddress; tables hold signed word offsets (from the table start) to scripts. " +
                          "Script = [duration or $FF/$FE/$FD special, frames..., command]. Commands: $FF loop, $FE n back n, $FD n go to anim n.",
            blockAddress = $"0x{blockStart:X6}",
            normalTable = 0x1F41A - blockStart,
            superTable = 0x1F5DC - blockStart,
            data = rom.Slice(blockStart, blockEnd - blockStart).ToArray().Select(v => (int)v),
            normal = Table(0x1F41A, out var s1),
            super = Table(0x1F5DC, out var s2),
        });
        _manifest.Items.Add(new ManifestItem("objects/sonic/animations.json", "animations", [.. s1, .. s2]));
    }

    /// <summary>
    /// Sonic's sprite data: uncompressed art (0x53080), mappings (0x76860; Obj01_Init sets this pointer) and dynamic
    /// pattern load cues (0x781C8, LoadSonicDynPLC 0x1F65E). Tables use signed word offsets.
    /// </summary>
    private void ExtractSonicSprites(List<PaletteEntry> palettes)
    {
        ExtractPlayerSprites(palettes, "sonic", 0x53080, 0x76860, 0x781C8, 0x780);
        ExtractPlayerSprites(palettes, "tails", 0x687A0, 0x7A6F6, 0x7B300, 0x7A0);
        ExtractPlayerSprites(palettes, "dust", 0x78D10, 0x21DDE, 0x21EF4, 0x49C, animTable: 0x21DB8);   // Obj08
        ExtractTailsAnimations();
    }

    /// <summary>
    /// Tails_Animate (0x20C24) scripts, and the tails object's (Obj05 0x21076). Like Sonic's, kept as raw blocks:
    /// the code also addresses some scripts directly (walk, run, fastest run, roll, push, and the tails' directional
    /// set), whose offsets into the block are recorded here.
    /// </summary>
    private void ExtractTailsAnimations()
    {
        const int tailsStart = 0x20EAE, tailsEnd = 0x20FFA, objStart = 0x21114, objEnd = 0x2118E;
        WriteCompactJson("objects/tails/animations.json", new
        {
            format = "s2port.animations/2",
            blockAddress = $"0x{tailsStart:X6}",
            table = 0,
            walk = 0x20EF0 - tailsStart, run = 0x20EFA - tailsStart, fastest = 0x20FEC - tailsStart,
            roll = 0x20F04 - tailsStart, roll2 = 0x20F09 - tailsStart, push = 0x20F0E - tailsStart,
            data = rom.Slice(tailsStart, tailsEnd - tailsStart).ToArray().Select(v => (int)v),
        });
        WriteCompactJson("objects/tails-tails/animations.json", new
        {
            format = "s2port.animations/2",
            description = "Obj05: animMap (Tails' anim -> tails anim, 0x21114), then the script table at 0x21136; directional = the $FC scripts at 0x2115E.",
            blockAddress = $"0x{objStart:X6}",
            animMap = 0, table = 0x21136 - objStart, directional = 0x2115E - objStart,
            data = rom.Slice(objStart, objEnd - objStart).ToArray().Select(v => (int)v),
        });
        _manifest.Items.Add(new ManifestItem("objects/tails/animations.json", "animations", [SourceRef.At(tailsStart, "raw", size: tailsEnd - tailsStart)]));
        _manifest.Items.Add(new ManifestItem("objects/tails-tails/animations.json", "animations", [SourceRef.At(objStart, "raw", size: objEnd - objStart)]));
    }

    /// <summary>A player's uncompressed art, DPLCs (word: bits 12-15 count-1, 0-11 first tile) and mappings.</summary>
    private void ExtractPlayerSprites(List<PaletteEntry> palettes, string name, int artAddr, int mapAddr, int dplcAddr, int vramTile, int animTable = 0)
    {
        int Entry(int table, int i) => table + (short)rom.U16(table + i * 2);
        int frames = rom.U16(dplcAddr) / 2;

        var dplc = new List<int[][]>();
        int tiles = 0;
        for (int f = 0; f < frames; f++)
        {
            int a = Entry(dplcAddr, f);
            int n = rom.U16(a);
            var entries = Enumerable.Range(0, n).Select(k =>
            {
                int e = rom.U16(a + 2 + k * 2);
                return new[] { e & 0xFFF, (e >> 12) + 1 };   // [first art tile, tile count]
            }).ToArray();
            foreach (var e in entries) tiles = Math.Max(tiles, e[0] + e[1]);
            dplc.Add(entries);
        }
        var mappings = new List<int[][]>();
        int mapFrames = rom.U16(mapAddr) / 2;
        for (int f = 0; f < mapFrames; f++)
        {
            int a = Entry(mapAddr, f);
            int n = rom.U16(a);
            mappings.Add(Enumerable.Range(0, n).Select(k =>
            {
                int p = a + 2 + k * 8;
                return new int[] { (sbyte)rom.U8(p), rom.U8(p + 1), rom.U16(p + 2), (short)rom.U16(p + 6) };   // y, size, pattern, x
            }).ToArray());
        }

        var art = rom.Slice(artAddr, tiles * Tiles.BytesPerTile).ToArray();
        var (w, h, px) = Tiles.ToSheet(art, 16);
        var pal = ComposeCram(palettes, RomMap.LevelBasePalette).Select(c => new MdColor(c).ToRgb()).ToList();
        IndexedPng.Write(Path.Combine(outDir, $"objects/{name}/art.png"), w, h, px, pal, [0]);
        WriteCompactJson($"objects/{name}/sprites.json", new
        {
            format = "s2port.sprites/1",
            romPointer = mapAddr,
            art = "art.png",
            artTiles = tiles,
            vramTile,
            dplcFields = new[] { "firstArtTile", "tileCount" },
            dplc,
            pieceFields = new[] { "y", "size", "pattern", "x" },
            size = "bits 0-1 height-1, bits 2-3 width-1 (tiles); tiles are column-major",
            mappings,
            animations = animTable == 0 ? null : AnimationBlock(animTable),
        });
        _manifest.Items.Add(new ManifestItem($"objects/{name}/art.png", "sprite-art", [SourceRef.At(artAddr, "raw", size: art.Length)]));
        _manifest.Items.Add(new ManifestItem($"objects/{name}/sprites.json", "sprite-mappings",
            [SourceRef.At(mapAddr, "raw"), SourceRef.At(dplcAddr, "raw")]));
    }

    /// <summary>
    /// Ring sprite mappings (BuildRings 0x1AEF0 / table 0x1B0EA): one piece per frame, no piece count.
    /// Frames 0-3 spin, 4-7 are the collect sparkle. Ring art is at tile $6BC (pattern + $26BC).
    /// </summary>
    private void ExtractRingMappings()
    {
        const int table = 0x1B0EA;
        int frames = rom.U16(table) / 2;
        var mappings = Enumerable.Range(0, frames).Select(f =>
        {
            int a = table + (short)rom.U16(table + f * 2);
            return new[] { new int[] { (sbyte)rom.U8(a), rom.U8(a + 1), rom.U16(a + 2), (short)rom.U16(a + 6) } };
        }).ToArray();
        WriteCompactJson("objects/rings/sprites.json", new
        {
            format = "s2port.sprites/1",
            artTile = 0x26BC,
            pieceFields = new[] { "y", "size", "pattern", "x" },
            mappings,
        });
        _manifest.Items.Add(new ManifestItem("objects/rings/sprites.json", "sprite-mappings", [SourceRef.At(table, "raw")]));
    }

    /// <summary>
    /// The HUD (BuildHUD 0x44D58, HudUpdate 0x4531E, Hud_Base 0x4563C):
    /// * sprites.json: the 4 HUD frames from 0x4502E (normal; RINGS flashing; TIME flashing; both), drawn at art_tile $86CA
    ///   from screen position ($90,$108)
    /// * digits.png: the uncompressed 8x16 digit art at 0x4591A (two tiles per digit, column order) that HudUpdate
    ///   copies to VRAM for score, time and rings; 24 tiles (digits 0-9, then the extra tiles Hud_Base uses)
    /// * lives-digits.png: the 8x8 lives counter digits at 0x45C1A
    /// * hud.json: the Hud_Base and Hud_InitRings tile lists ($FF = blank tile)
    /// </summary>
    /// <summary>
    /// Mappings and animation scripts for one object (see <see cref="RomMap.ObjectSprites"/>). The frame count is
    /// taken from the first offset (frames follow the table). Animation scripts are kept as one raw block, like
    /// Sonic's, because AnimateSprite indexes it with offsets and the scripts' command bytes.
    /// romPointer/frameOffsets let the game resolve the ROM pointers object code stores in its slot (offset $04),
    /// including "static" pointers straight at a frame's first piece.
    /// </summary>
    /// <summary>An AnimateSprite script table and its scripts as one raw block (table at offset 0).</summary>
    private int[] AnimationBlock(int animTable)
    {
        int count = rom.U16(animTable) / 2;
        int end = animTable + count * 2;
        for (int i = 0; i < count; i++)
        {
            int p = animTable + (short)rom.U16(animTable + i * 2) + 1;
            while (true)
            {
                int b = rom.U8(p++);
                if (b < 0xF9) continue;
                if (b is 0xFE or 0xFD) p++;
                break;
            }
            end = Math.Max(end, p);
        }
        return Enumerable.Range(0, end - animTable).Select(i => (int)rom.U8(animTable + i)).ToArray();
    }

    /// <summary>
    /// Uncompressed object art that the game copies to VRAM itself rather than through a PLC: the giant ring
    /// (streamed $1C0 bytes per frame by 0x43EE0) and its flash (0x43F50), both to VRAM tile $3D2.
    /// </summary>
    private void ExtractStreamedObjectArt(List<PaletteEntry> palettes)
    {
        foreach (var (name, address, tiles) in RomMap.StreamedObjectArt)
        {
            string png = $"objects/{name}/art.png";
            Directory.CreateDirectory(Path.Combine(outDir, "objects", name));
            var data = rom.Slice(address, tiles * Tiles.BytesPerTile).ToArray();
            var (w, h, px) = Tiles.ToSheet(data, 16, _ => 1);
            IndexedPng.Write(Path.Combine(outDir, png), w, h, px,
                ComposeCram(palettes, RomMap.LevelBasePalette).Select(c => new MdColor(c).ToRgb()).ToList(), [0, 16, 32, 48]);
            _manifest.Items.Add(new ManifestItem(png, "object-art", [SourceRef.At(address, "raw", size: data.Length)],
                $"{tiles} tiles, uncompressed, copied to VRAM by object code"));
        }
    }

    /// <summary>The title card's art, letter font, per-zone letter lists and name frames (see RomMap.TitleCardBaseArt).</summary>
    private void ExtractTitleCard(List<PaletteEntry> palettes)
    {
        const string dir = "objects/title-card";
        Directory.CreateDirectory(Path.Combine(outDir, dir));
        var cram = ComposeCram(palettes, RomMap.LevelBasePalette).Select(c => new MdColor(c).ToRgb()).ToList();
        int Write(string file, int address)
        {
            var art = Nemesis.Decompress(rom, address, out int packed);
            var (w, h, px) = Tiles.ToSheet(art, 16, _ => 0);
            IndexedPng.Write(Path.Combine(outDir, dir, file), w, h, px, cram, [0, 16, 32, 48]);
            _manifest.Items.Add(new ManifestItem($"{dir}/{file}", "nemesis-art", [SourceRef.At(address, "nemesis", packed, art.Length)]));
            return art.Length / Tiles.BytesPerTile;
        }
        int baseTiles = Write("base.png", RomMap.TitleCardBaseArt);
        int fontTiles = Write("font.png", RomMap.TitleCardFont);
        var letters = new Dictionary<string, int[][]>();
        for (int zone = 0; zone <= 0x1C; zone++)
        {
            int a = RomMap.TitleCardLetterLists + rom.U16(RomMap.TitleCardLetterOffsets + zone * 2);
            var list = new List<int[]>();
            while (rom.U8(a) != 0xFF) { list.Add([rom.U8(a), rom.U8(a + 1)]); a += 2; }
            letters[$"{zone:X2}"] = [.. list];
        }
        WriteCompactJson($"{dir}/title-card.json", new
        {
            format = "s2port.title-card/1",
            description = "LoadTitleCard (0x18D7C): base.png to baseVramTile; then, for the zone, each [font tile, count] of " +
                          "letters copied in order from font.png to letterVramTile on. nameFrames: Obj34's zone-name frame per zone " +
                          "(index $1B is used for zone/act $1801).",
            baseVramTile = RomMap.TitleCardBaseTile, baseTiles, fontTiles, letterVramTile = RomMap.TitleCardLetterTile, letters,
            nameFrames = rom.Slice(RomMap.TitleCardNameFrames, 0x1C).ToArray().Select(b => (int)b),
        });
        _manifest.Items.Add(new ManifestItem($"{dir}/title-card.json", "title-card",
            [SourceRef.At(RomMap.TitleCardLetterOffsets, "raw", size: 0x3A), SourceRef.At(RomMap.TitleCardNameFrames, "raw", size: 0x1C)]));
    }

    private void ExtractObjectSprites(string name, int table, int animTable, int frameCount = 0)
    {
        int frames = frameCount != 0 ? frameCount : rom.U16(table) / 2;
        var offsets = Enumerable.Range(0, frames).Select(f => (int)(short)rom.U16(table + f * 2)).ToArray();
        var mappings = offsets.Select(off =>
        {
            int a = table + off;
            return Enumerable.Range(0, rom.U16(a)).Select(k =>
            {
                int p = a + 2 + k * 8;
                return new int[] { (sbyte)rom.U8(p), rom.U8(p + 1), rom.U16(p + 2), (short)rom.U16(p + 6) };
            }).ToArray();
        }).ToArray();
        object? animations = null;
        var sources = new List<SourceRef> { SourceRef.At(table, "raw") };
        if (animTable != 0)
        {
            int count = rom.U16(animTable) / 2;
            int end = animTable + count * 2;
            for (int i = 0; i < count; i++)
            {
                int p = animTable + (short)rom.U16(animTable + i * 2) + 1;
                while (true)
                {
                    int b = rom.U8(p++);
                    if (b < 0xF9) continue;
                    if (b is 0xFE or 0xFD) p++;
                    break;
                }
                end = Math.Max(end, p);
            }
            animations = Enumerable.Range(0, end - animTable).Select(i => (int)rom.U8(animTable + i)).ToArray();
            sources.Add(SourceRef.At(animTable, "raw", size: end - animTable));
        }
        WriteCompactJson($"objects/{name}/sprites.json", new
        {
            format = "s2port.sprites/1",
            romPointer = table,
            frameOffsets = offsets,
            pieceFields = new[] { "y", "size", "pattern", "x" },
            mappings,
            animations,
        });
        _manifest.Items.Add(new ManifestItem($"objects/{name}/sprites.json", "sprite-mappings", sources));
    }

    /// <summary>
    /// Oscillating numbers (OscillateNumInit 0x5D26 / OscillateNumDo 0x5DCC): 16 values at $FE60 that swing back and
    /// forth (platforms and other objects read them). Initial state: 33 words for $FE5E; parameters: {speed, limit}
    /// per value. The original zones and the hack zones ($FFE6) each have their own pair of tables.
    /// </summary>
    private void ExtractOscillation()
    {
        int[] Words(int a, int n) => Enumerable.Range(0, n).Select(i => (int)rom.U16(a + i * 2)).ToArray();
        WriteCompactJson("objects/oscillation.json", new
        {
            format = "s2port.oscillation/1",
            normal = new { initial = Words(0x5D48, 33), parameters = Words(0x5E30, 32) },
            hack = new { initial = Words(0x5D8A, 33), parameters = Words(0x5E70, 32) },
        });
        _manifest.Items.Add(new ManifestItem("objects/oscillation.json", "object-data",
            [SourceRef.At(0x5D48, "raw", size: 66), SourceRef.At(0x5E30, "raw", size: 64), SourceRef.At(0x5D8A, "raw", size: 66), SourceRef.At(0x5E70, "raw", size: 64)]));
    }

    /// <summary>Obj28's data tables: which two animal types each zone uses, and each type's speeds and mappings pointer.</summary>
    private void ExtractAnimalTables()
    {
        int zoneBytes = RomMap.AnimalZoneCount * 2;
        WriteCompactJson("objects/animal/animals.json", new
        {
            format = "s2port.animals/1",
            zoneTypes = Enumerable.Range(0, zoneBytes).Select(i => (int)rom.U8(RomMap.AnimalZoneTypes + i)).ToArray(),
            typeFields = new[] { "xVel", "yVel", "mappings" },
            types = Enumerable.Range(0, RomMap.AnimalTypeCount).Select(i =>
            {
                int a = RomMap.AnimalTypes + i * 8;
                return new[] { (int)(short)rom.U16(a), (int)(short)rom.U16(a + 2), (int)rom.U32(a + 4) };
            }).ToArray(),
        });
        _manifest.Items.Add(new ManifestItem("objects/animal/animals.json", "object-data",
            [SourceRef.At(RomMap.AnimalZoneTypes, "raw", size: zoneBytes), SourceRef.At(RomMap.AnimalTypes, "raw", size: RomMap.AnimalTypeCount * 8)]));
    }

    private void ExtractHud(List<PaletteEntry> palettes)
    {
        const int table = 0x4502E, digits = 0x4591A, livesDigits = 0x45C1A;
        const int digitTiles = (livesDigits - digits) / Tiles.BytesPerTile, livesTiles = 10;
        int frames = rom.U16(table) / 2;
        var mappings = Enumerable.Range(0, frames).Select(f =>
        {
            int a = table + (short)rom.U16(table + f * 2);
            return Enumerable.Range(0, rom.U16(a)).Select(k =>
            {
                int p = a + 2 + k * 8;
                return new int[] { (sbyte)rom.U8(p), rom.U8(p + 1), rom.U16(p + 2), (short)rom.U16(p + 6) };
            }).ToArray();
        }).ToArray();
        var pal = ComposeCram(palettes, RomMap.LevelBasePalette).Select(c => new MdColor(c).ToRgb()).ToList();
        foreach (var (name, addr, count) in new[] { ("digits.png", digits, digitTiles), ("lives-digits.png", livesDigits, livesTiles) })
        {
            var (w, h, px) = Tiles.ToSheet(rom.Slice(addr, count * Tiles.BytesPerTile).ToArray(), 8);
            IndexedPng.Write(Path.Combine(outDir, "objects/hud", name), w, h, px, pal, [0]);
            _manifest.Items.Add(new ManifestItem("objects/hud/" + name, "sprite-art", [SourceRef.At(addr, "raw", size: count * Tiles.BytesPerTile)]));
        }
        WriteCompactJson("objects/hud/sprites.json", new
        {
            format = "s2port.sprites/1",
            artTile = 0x86CA,
            pieceFields = new[] { "y", "size", "pattern", "x" },
            mappings,
        });
        WriteCompactJson("objects/hud/hud.json", new
        {
            format = "s2port.hud/1",
            digits = "digits.png",
            digitTiles,
            livesDigits = "lives-digits.png",
            livesDigitTiles = livesTiles,
            baseTiles = Enumerable.Range(0, 15).Select(i => (int)rom.U8(0x456A2 + i)).ToArray(),
            initRingsTiles = Enumerable.Range(0, 3).Select(i => (int)rom.U8(0x456AE + i)).ToArray(),
        });
        _manifest.Items.Add(new ManifestItem("objects/hud/sprites.json", "sprite-mappings", [SourceRef.At(table, "raw")]));
        _manifest.Items.Add(new ManifestItem("objects/hud/hud.json", "hud", [SourceRef.At(0x456A2, "raw", size: 0x10)]));
    }

    // ---------------------------------------------------------------- PLCs / Nemesis art

    private void ExtractPlcs(List<PaletteEntry> palettes)
    {
        var previewPalette = ComposeCram(palettes, RomMap.LevelBasePalette, 0x04).Select(c => new MdColor(c).ToRgb()).ToList();
        var written = new Dictionary<int, int>(); // source -> tile count
        var lists = new List<object>();
        for (int id = 0; id < RomMap.PlcCount; id++)
        {
            int listAddr = RomMap.PlcOffsets + (short)rom.U16(RomMap.PlcOffsets + id * 2);
            int count = (short)rom.U16(listAddr) + 1;
            var entries = new List<object>();
            for (int i = 0; i < count; i++)
            {
                int e = listAddr + 2 + i * 6;
                int src = (int)rom.U32(e);
                int vram = rom.U16(e + 4);
                if (!written.TryGetValue(src, out int tiles))
                {
                    var art = Nemesis.Decompress(rom, src, out int packed);
                    tiles = art.Length / Tiles.BytesPerTile;
                    written[src] = tiles;
                    string png = $"art/nemesis/nem_{src:X6}.png";
                    var (w, h, px) = Tiles.ToSheet(art, 16);
                    IndexedPng.Write(Path.Combine(outDir, png), w, h, px, previewPalette, transparentIndices: [0, 16, 32, 48]);
                    _manifest.Items.Add(new ManifestItem(png, "nemesis-art", [SourceRef.At(src, "nemesis", packed, art.Length)],
                        $"{tiles} tiles; preview colours = palette line 0 of pal_03"));
                }
                entries.Add(new { art = $"../art/nemesis/nem_{src:X6}.png", tiles, vramTile = vram / Tiles.BytesPerTile });
            }
            lists.Add(new { id, entries });
        }
        WriteCompactJson("plc/plc_lists.json", new
        {
            format = "s2port.plc/1",
            description = "Pattern load cues: art to decompress into VRAM. vramTile = destination tile index (VRAM address / 32). " +
                          "Art PNG pixels are raw 4bpp values (palette line chosen by whoever draws the tiles).",
            lists,
        });
        _manifest.Items.Add(new ManifestItem("plc/plc_lists.json", "plc-lists", [SourceRef.At(RomMap.PlcOffsets, "raw", size: RomMap.PlcCount * 2)]));
        log?.Invoke($"PLC lists: {RomMap.PlcCount}, Nemesis art blocks: {written.Count}");
    }

    private readonly HashSet<int> _writtenUncompressedArt = [];

    private void ExtractAnimatedTiles(int zone, string zoneDir, List<PaletteEntry> palettes, int palId)
    {
        int routine = RomMap.AnimatedTiles + (short)rom.U16(RomMap.AnimatedTiles + zone * 4);
        int list = RomMap.AnimatedTiles + (short)rom.U16(RomMap.AnimatedTiles + zone * 4 + 2);
        var scripts = new List<object>();
        var sources = new List<SourceRef> { SourceRef.At(RomMap.AnimatedTiles + zone * 4, "raw", size: 4) };

        if (routine != RomMap.AnimatedTilesNullRoutine)
        {
            int count = rom.U16(list) + 1;
            int a = list + 2;
            for (int i = 0; i < count; i++)
            {
                int duration = rom.U8(a);
                int art = (int)(rom.U32(a) & 0xFFFFFF);
                int vramTile = rom.U16(a + 4) / Tiles.BytesPerTile;
                int frames = rom.U8(a + 6), tilesPerFrame = rom.U8(a + 7);
                bool perFrame = (duration & 0x80) != 0;
                var frameList = Enumerable.Range(0, frames).Select(f => perFrame
                    ? new[] { (int)rom.U8(a + 8 + f * 2), rom.U8(a + 9 + f * 2) }
                    : new[] { (int)rom.U8(a + 8 + f), duration }).ToArray();
                int artTiles = frameList.Max(f => f[0]) + tilesPerFrame;
                string png = $"art/animated/unc_{art:X6}_{artTiles}.png";
                if (_writtenUncompressedArt.Add(art * 4096 + artTiles))
                {
                    var data = rom.Slice(art, artTiles * Tiles.BytesPerTile).ToArray();
                    var (w, h, px) = Tiles.ToSheet(data, 16, _ => 2);
                    IndexedPng.Write(Path.Combine(outDir, png), w, h, px,
                        ComposeCram(palettes, RomMap.LevelBasePalette, palId).Select(c => new MdColor(c).ToRgb()).ToList(), [0, 16, 32, 48]);
                    _manifest.Items.Add(new ManifestItem(png, "animated-art", [SourceRef.At(art, "raw", size: data.Length)],
                        $"{artTiles} tiles, uncompressed; preview uses line 2 of zone {zone:X2}'s palette"));
                }
                scripts.Add(new { art = $"../../{png}", artTiles, vramTile, tilesPerFrame, frames = frameList });
                sources.Add(SourceRef.At(a, "raw", size: 8));
                a += 8 + (perFrame ? frames * 2 : (frames + 1) & ~1);
            }
        }

        object? magma = null;
        if (routine == RomMap.MarbleAnimatedArtRoutine)
        {
            // Sonic 1 Marble Zone magma (0x4423A): 4 frames of 32x32 px linear 4bpp art (16 bytes per row) that the
            // routine copies column by column, shifted by the oscillating value at $FE68.
            const string bin = "art/animated/magma_1114F6.bin";
            Directory.CreateDirectory(Path.Combine(outDir, "art", "animated"));
            File.WriteAllBytes(Path.Combine(outDir, bin), rom.Slice(RomMap.MarbleMagmaArt, 0x800).ToArray());
            _manifest.Items.Add(new ManifestItem(bin, "magma-art", [SourceRef.At(RomMap.MarbleMagmaArt, "raw", size: 0x800)]));
            magma = new { art = $"../../{bin}", vramTile = 0x2D2, frames = 4 };
            sources.Add(SourceRef.At(RomMap.MarbleMagmaArt, "raw", size: 0x800));
        }

        WriteCompactJson($"{zoneDir}/animated_tiles.json", new
        {
            magma,
            format = "s2port.animated-tiles/1",
            description = "Each script copies tilesPerFrame tiles from art[tileOffset] to vramTile when its timer expires. " +
                          "frames: [tileOffset, duration]; a frame shows for duration+1 frames. Initial state: frame 0.",
            driver = routine == RomMap.AnimatedTilesNullRoutine ? "none"
                : routine == RomMap.AnimatedTilesGenericRoutine ? "scripts"
                : $"scripts + custom code at 0x{routine:X6} (reimplement in game logic)",
            scripts,
        });
        _manifest.Items.Add(new ManifestItem($"{zoneDir}/animated_tiles.json", "animated-tiles", sources));
    }

    /// <summary>
    /// Palette cycling data (dispatch table 0x1BC8). The routines identified so far, each 4 frames of 4 colours:
    /// 0x2040 (zones $11, $12: Sonic 1 Green Hill's water; table 0x258C to colours 40-43, timer reload 5) and 0x1C04
    /// (zone $00: Emerald Hill's water, Sonic 2's; table 0x22C8, two colours to 19-20 and two to 30-31, reload 7).
    /// </summary>
    private void ExtractPaletteCycle(int zone, string zoneDir)
    {
        var (table, routine, colours, reload) = zone switch
        {
            0x11 or 0x12 => (0x258C, "ghz-water (ROM 0x2040)", new[] { 40, 41, 42, 43 }, 5),
            0x00 => (0x22C8, "ehz-water (ROM 0x1C04)", new[] { 19, 20, 30, 31 }, 7),
            _ => (0, "", Array.Empty<int>(), 0),
        };
        if (table == 0) return;
        var frames = Enumerable.Range(0, 4).Select(f => Enumerable.Range(0, 4).Select(c => (int)rom.U16(table + f * 8 + c * 2)).ToArray()).ToArray();
        WriteCompactJson($"{zoneDir}/palette_cycle.json", new
        {
            format = "s2port.palette-cycle/2",
            routine,
            colours,
            timerReload = reload,
            timerAddress = 0xF634,
            frameAddress = 0xF632,
            frames,
        });
        _manifest.Items.Add(new ManifestItem($"{zoneDir}/palette_cycle.json", "palette-cycle", [SourceRef.At(table, "raw", size: 32)]));
    }

    private int[] LevelPlcs(int zone, int act)
    {
        int e = RomMap.LevelArtPointers + zone * 12;
        int plc1 = rom.U8(e), plc2 = rom.U8(e + 4);
        var ids = new List<int>();
        if (plc1 != 0) ids.Add(((zone << 8) | act) == 0x1B01 ? plc1 + 1 : plc1); // 0x4CC2-0x4CD0
        ids.Add(RomMap.LevelCommonPlc);                                          // 0x4CD4
        if (plc2 != 0) ids.Add(plc2);                                            // LevelDataLoad 0x10668
        return ids.ToArray();
    }

    // ---------------------------------------------------------------- collision

    private void ExtractCollisionSets()
    {
        foreach (var set in new[] { RomMap.NormalCollision, RomMap.AlternateCollision })
        {
            var heights = rom.Slice(set.Heights, 0x1000).ToArray();
            var widths = rom.Slice(set.Widths, 0x1000).ToArray();
            string path = $"collision/{set.Name}.json";
            WriteCompactJson(path, new
            {
                format = "s2port.collision-set/1",
                description = "Indexed by collision ID (from a zone's collision index). Heights/widths: 16 signed values per ID; " +
                              "positive = solid from the bottom/right edge, negative = from the top/left, 16 = full. " +
                              "Angle 0xFF = flat-surface fallback handled by the floor code.",
                angles = rom.Slice(set.AngleMap, 256).ToArray().Select(b => (int)b),
                heights = Enumerable.Range(0, 256).Select(i => Signed(heights.AsSpan(i * 16, 16))),
                widths = Enumerable.Range(0, 256).Select(i => Signed(widths.AsSpan(i * 16, 16))),
            });
            _manifest.Items.Add(new ManifestItem(path, "collision-set",
                [SourceRef.At(set.AngleMap, "raw", size: 256), SourceRef.At(set.Heights, "raw", size: 0x1000), SourceRef.At(set.Widths, "raw", size: 0x1000)],
                set == RomMap.AlternateCollision ? $"Used when $FFFFE6=1 (zones >= {RomMap.AlternateCollisionFirstZone:X2})" : null));
            WriteHeightMaskSheet($"collision/{set.Name}_heights.png", heights);
        }
        log?.Invoke("Collision sets: 2");

        static int[] Signed(ReadOnlySpan<byte> b)
        {
            var r = new int[b.Length];
            for (int i = 0; i < b.Length; i++) r[i] = (sbyte)b[i];
            return r;
        }
    }

    /// <summary>16x16 grid of 16x16 height masks (collision ID = row*16 + column), for eyeballing.</summary>
    private void WriteHeightMaskSheet(string relPath, ReadOnlySpan<byte> heights)
    {
        const int size = 16 * 17;
        var px = new byte[size * size];
        for (int id = 0; id < 256; id++)
        {
            int ox = (id % 16) * 17, oy = (id / 16) * 17;
            for (int x = 0; x < 16; x++)
            {
                int h = (sbyte)heights[id * 16 + x];
                for (int y = 0; y < 16; y++)
                {
                    bool solid = h > 0 ? y >= 16 - h : h < 0 && y < -h;
                    px[(oy + y) * size + ox + x] = (byte)(solid ? 1 : 2);
                }
            }
        }
        IndexedPng.Write(Path.Combine(outDir, relPath), size, size, px, [(0, 0, 0), (230, 230, 230), (40, 40, 60)]);
    }

    private bool ExtractCollisionIndex(int zone, string zoneDir)
    {
        int p = (int)rom.U32(RomMap.CollisionIndexPrimary + zone * 4);
        int s = (int)rom.U32(RomMap.CollisionIndexSecondary + zone * 4);
        if (p == RomMap.LevelLayoutOffsets)
            return false; // unused zone slots point at the layout table, not collision data

        var primary = PadTo(Kosinski.Decompress(rom, p, out int pPacked), RomMap.CollisionIndexLength);
        var secondary = PadTo(Kosinski.Decompress(rom, s, out int sPacked), RomMap.CollisionIndexLength);
        var set = zone >= RomMap.AlternateCollisionFirstZone ? RomMap.AlternateCollision : RomMap.NormalCollision;
        WriteCompactJson($"{zoneDir}/collision.json", new
        {
            format = "s2port.collision-index/1",
            set = $"../../collision/{set.Name}.json",
            description = "Collision ID per block number. primary = layer A (chunk field solidityPrimary), secondary = layer B (soliditySecondary). The active layer is switched at runtime via $FFFFF796 (0x1EC7E etc.).",
            primary = primary.Select(b => (int)b),
            secondary = secondary.Select(b => (int)b),
        });
        _manifest.Items.Add(new ManifestItem($"{zoneDir}/collision.json", "collision-index",
            [SourceRef.At(p, "kosinski", pPacked), SourceRef.At(s, "kosinski", sPacked)],
            "Padded with zeros to 0x300 entries (collision RAM is cleared at level start, 0x65FE)"));
        return true;

        static byte[] PadTo(byte[] data, int length)
        {
            var r = new byte[length];
            data.AsSpan(0, Math.Min(data.Length, length)).CopyTo(r);
            return r;
        }
    }

    /// <summary>Renders every act's planes by loading the extracted data back through LevelLoader.</summary>
    private void RenderPreviews()
    {
        Parallel.For(0, RomMap.ZoneCount, zone =>
        {
            for (int act = 1; act <= 2; act++)
            {
                var level = LevelLoader.Load(outDir, zone, act);
                foreach (var (planeName, plane) in new[] { ("fg", level.Foreground), ("bg", level.Background) })
                {
                    var (w, h) = LevelData.UsedExtent(plane);
                    if (w == 0) continue;
                    var (pw, ph, px) = PlaneRenderer.Render(level, plane, w, h);
                    IndexedPng.Write(Path.Combine(outDir, $"previews/zone_{zone:X2}_act{act}_{planeName}.png"), pw, ph, px,
                        level.Cram.Select(c => new MdColor(c).ToRgb()).ToList(), [0]);
                }
            }
        });
        log?.Invoke("Previews rendered");
    }

    private static void Patch(List<byte> target, int offset, byte[] data)
    {
        while (target.Count < offset + data.Length) target.Add(0);
        for (int i = 0; i < data.Length; i++) target[offset + i] = data[i];
    }

    private static ushort[] ToWords(IReadOnlyList<byte> bytes)
    {
        var words = new ushort[bytes.Count / 2];
        for (int i = 0; i < words.Length; i++) words[i] = (ushort)((bytes[i * 2] << 8) | bytes[i * 2 + 1]);
        return words;
    }

    // ---------------------------------------------------------------- helpers

    private void WriteCompactJson(string relPath, object value)
    {
        string path = Path.Combine(outDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, CompactJson.Serialize(value));
    }

    private void WriteJson(string relPath, object value)
    {
        string path = Path.Combine(outDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Manifest.JsonOptions));
    }
}
