using System.Text.Json;
using S2Port.Extraction.Graphics;

namespace S2Port.Extraction.Levels;

/// <summary>Loads an act from the extracted data folder (never from the ROM).</summary>
public static class LevelLoader
{
    public static string ActDir(string dataDir, int zone, int act) =>
        zone == Smb3.Smb3Campaign.Zone
            ? Path.Combine(Smb3.Smb3Campaign.AreaDirectory(dataDir, act), "act_1")
            : Path.Combine(dataDir, "zones", $"zone_{zone:X2}", $"act_{act}");

    public static LevelData Load(string dataDir, int zone, int act)
    {
        string dir = ActDir(dataDir, zone, act);
        using var levelDoc = JsonDocument.Parse(S2Port.Extraction.DataFile.ReadAllText(Path.Combine(dir, "level.json")));
        var level = levelDoc.RootElement;
        string Rel(string prop) => Path.GetFullPath(Path.Combine(dir, level.GetProperty(prop).GetString()!));

        // Tiles: PNG pixels carry line*16+colour; the low nibble is the raw tile data.
        string tilesPng = Rel("tiles");
        using var tilesMeta = JsonDocument.Parse(S2Port.Extraction.DataFile.ReadAllText(Path.ChangeExtension(tilesPng, ".json")));
        int tileCount = tilesMeta.RootElement.GetProperty("tileCount").GetInt32();
        (int width, _, byte[] pixels, _) = ReadPng(tilesPng);
        var art = Tiles.FromSheet(pixels, width, tileCount);

        var blocks = LevelJson.BlocksFromJson(ParseFile(Rel("blocks")));
        var chunks = LevelJson.ChunksFromJson(ParseFile(Rel("chunks")));
        var (fg, bg) = LevelJson.LayoutFromJson(ParseFile(Rel("layout")));

        var cram = new ushort[64];
        ushort[]? titleCardCram = null;
        foreach (var p in level.GetProperty("palettes").EnumerateArray())
        {
            if (titleCardCram is null && cram.Any(c => c != 0)) titleCardCram = (ushort[])cram.Clone();
            var pal = ParseFile(Path.GetFullPath(Path.Combine(dir, p.GetString()!)));
            int start = pal.GetProperty("startColour").GetInt32();
            int i = 0;
            foreach (var c in pal.GetProperty("colours").EnumerateArray())
            {
                if (start + i < 64) cram[start + i] = Convert.ToUInt16(c.GetProperty("word").GetString(), 16);
                i++;
            }
        }

        if (titleCardCram is null || titleCardCram.All(c => c == 0)) titleCardCram = (ushort[])cram.Clone();
        byte[]? colP = null, colS = null;
        CollisionSet? set = null;
        if (level.TryGetProperty("collision", out var colRef) && colRef.ValueKind == JsonValueKind.String)
        {
            string colPath = Path.GetFullPath(Path.Combine(dir, colRef.GetString()!));
            var col = ParseFile(colPath);
            colP = col.GetProperty("primary").EnumerateArray().Select(v => (byte)v.GetInt32()).ToArray();
            colS = col.GetProperty("secondary").EnumerateArray().Select(v => (byte)v.GetInt32()).ToArray();
            string setPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(colPath)!, col.GetProperty("set").GetString()!));
            set = LoadCollisionSet(setPath);
        }

        IReadOnlyList<ObjectPlacement> objects = [];
        int objectListAddress = 0, objectBoundaryX = 0;
        if (level.TryGetProperty("objects", out var o))
        {
            var objRoot = ParseFile(Path.Combine(dir, o.GetString()!));
            objects = LevelJson.ObjectsFromJson(objRoot);
            if (objRoot.TryGetProperty("listAddress", out var la)) objectListAddress = la.GetInt32();
            if (objRoot.TryGetProperty("boundaryX", out var bx)) objectBoundaryX = bx.GetInt32();
        }
        var rings = level.TryGetProperty("rings", out var r) ? LevelJson.RingsFromJson(ParseFile(Path.Combine(dir, r.GetString()!))) : [];

        byte[]? vram = null;
        string? plcListsPath = null;
        var afterTitleCard = new List<int>();
        if (level.TryGetProperty("plcs", out var plcs))
        {
            vram = new byte[0x10000];
            art.CopyTo(vram, 0);
            plcListsPath = Path.GetFullPath(Path.Combine(dir, plcs.GetProperty("lists").GetString()!));
            var ids = new List<int>();
            if (plcs.TryGetProperty("fromTitleScreen", out var fts)) ids.AddRange(fts.EnumerateArray().Select(i => i.GetInt32()));
            ids.AddRange(plcs.GetProperty("onLoad").EnumerateArray().Select(i => i.GetInt32()));
            if (plcs.TryGetProperty("afterTitleCard", out var atc))
            {
                afterTitleCard.AddRange(atc.EnumerateArray().Select(i => i.GetInt32()));
                ids.AddRange(afterTitleCard);
            }
            foreach (var id in ids) ApplyPlc(plcListsPath, id, vram);
        }

        var animated = new List<AnimatedTileScript>();
        byte[]? magmaArt = null;
        int magmaTile = 0;
        if (level.TryGetProperty("animatedTiles", out var aniRef))
        {
            string aniPath = Path.GetFullPath(Path.Combine(dir, aniRef.GetString()!));
            var aniRoot = ParseFile(aniPath);
            if (aniRoot.TryGetProperty("magma", out var mg) && mg.ValueKind == JsonValueKind.Object)
            {
                magmaArt = S2Port.Extraction.DataFile.ReadAllBytes(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(aniPath)!, mg.GetProperty("art").GetString()!)));
                magmaTile = mg.GetProperty("vramTile").GetInt32();
            }
            foreach (var sc in ParseFile(aniPath).GetProperty("scripts").EnumerateArray())
            {
                string png = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(aniPath)!, sc.GetProperty("art").GetString()!));
                animated.Add(new AnimatedTileScript(
                    LoadTileArt(png, sc.GetProperty("artTiles").GetInt32()),
                    sc.GetProperty("vramTile").GetInt32(),
                    sc.GetProperty("tilesPerFrame").GetInt32(),
                    sc.GetProperty("frames").EnumerateArray().Select(f => (f[0].GetInt32(), f[1].GetInt32())).ToList()));
            }
        }

        var bounds = level.TryGetProperty("bounds", out var b)
            ? (b.GetProperty("minX").GetInt32(), b.GetProperty("maxX").GetInt32(), b.GetProperty("minY").GetInt32(), b.GetProperty("maxY").GetInt32())
            : (0, 0, 0, 0);
        var startPos = level.TryGetProperty("start", out var st) ? (st.GetProperty("x").GetInt32(), st.GetProperty("y").GetInt32()) : (0, 0);

        PaletteCycle? cycle = null;
        if (level.TryGetProperty("paletteCycle", out var pc) && pc.ValueKind == JsonValueKind.String)
        {
            var c = ParseFile(Path.GetFullPath(Path.Combine(dir, pc.GetString()!)));
            cycle = new PaletteCycle(c.GetProperty("colours").EnumerateArray().Select(v => v.GetInt32()).ToList(), c.GetProperty("timerReload").GetInt32(),
                c.GetProperty("frames").EnumerateArray().Select(f => f.EnumerateArray().Select(v => (ushort)v.GetInt32()).ToArray()).ToList());
        }

        var result = new LevelData
        {
            PaletteCycle = cycle,
            Bounds = bounds,
            Start = startPos,
            Vram = vram, PlcListsPath = plcListsPath, TitleCardCram = titleCardCram, AfterTitleCardPlcs = afterTitleCard,
            MagmaArt = magmaArt, MagmaVramTile = magmaTile,
            AnimatedTiles = animated,
            Objects = objects, ObjectListAddress = objectListAddress, ObjectBoundaryX = objectBoundaryX, RingGroups = rings,
            Art = art, BlockWords = blocks, ChunkWords = chunks, Foreground = fg, Background = bg, Cram = cram,
            CollisionPrimary = colP, CollisionSecondary = colS, Collision = set,
            MusicFile = level.TryGetProperty("musicFile", out var musicFile)
                ? Path.GetFullPath(Path.Combine(dir, musicFile.GetString()!)) : null,
            Gameplay = level.TryGetProperty("gameplay", out var gameplay)
                ? JsonSerializer.Deserialize<Smb3.Smb3Gameplay>(DataFile.ReadAllText(Path.Combine(dir, gameplay.GetString()!)), Manifest.JsonOptions)
                    ?? throw new InvalidDataException("Empty SMB3 gameplay metadata.") : null,
        };
        result.ApplyAnimatedFrames();
        return result;
    }

    private static readonly Dictionary<string, byte[]> TileArtCache = new();

    /// <summary>Raw 4bpp tile bytes from an extracted tile-sheet PNG.</summary>
    /// <summary>Decompressed-art pattern load list: copy each entry's tiles to its VRAM tile.</summary>
    public static void ApplyPlc(string listsPath, int id, byte[] vram)
    {
        var lists = ParseFile(listsPath).GetProperty("lists");
        foreach (var entry in lists[id].GetProperty("entries").EnumerateArray())
        {
            string png = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(listsPath)!, entry.GetProperty("art").GetString()!));
            var tiles = LoadTileArt(png, entry.GetProperty("tiles").GetInt32());
            int dest = entry.GetProperty("vramTile").GetInt32() * 32;
            tiles.AsSpan(0, Math.Min(tiles.Length, vram.Length - dest)).CopyTo(vram.AsSpan(dest));
        }
    }

    /// <summary>
    /// A PLC list's entries: the art's ROM address (from its file name, nem_XXXXXX; a made-up nonzero pointer for art
    /// that is not from the ROM), its tile count and its VRAM tile.
    /// </summary>
    public static List<(uint Address, int Tiles, int VramTile)> PlcEntries(string listsPath, int id)
    {
        var result = new List<(uint, int, int)>();
        var lists = ParseFile(listsPath).GetProperty("lists");
        foreach (var entry in lists[id].GetProperty("entries").EnumerateArray())
        {
            string name = Path.GetFileNameWithoutExtension(entry.GetProperty("art").GetString()!);
            uint address = name.StartsWith("nem_") && uint.TryParse(name[4..], System.Globalization.NumberStyles.HexNumber, null, out uint a)
                ? a : 0xF00000;
            result.Add((address, entry.GetProperty("tiles").GetInt32(), entry.GetProperty("vramTile").GetInt32()));
        }
        return result;
    }

    public static byte[] LoadTileArt(string pngPath, int tileCount)
    {
        lock (TileArtCache)
        {
            if (TileArtCache.TryGetValue(pngPath, out var cached)) return cached;
            var (width, _, pixels, _) = ReadPng(pngPath);
            return TileArtCache[pngPath] = Tiles.FromSheet(pixels, width, tileCount);
        }
    }

    public static CollisionSet LoadCollisionSet(string path)
    {
        var root = ParseFile(path);
        static sbyte[] Flat(JsonElement rows) =>
            rows.EnumerateArray().SelectMany(r => r.EnumerateArray().Select(v => (sbyte)v.GetInt32())).ToArray();
        return new CollisionSet(
            Path.GetFileNameWithoutExtension(path),
            root.GetProperty("angles").EnumerateArray().Select(v => (byte)v.GetInt32()).ToArray(),
            Flat(root.GetProperty("heights")),
            Flat(root.GetProperty("widths")));
    }

    private static JsonElement ParseFile(string path) => JsonDocument.Parse(S2Port.Extraction.DataFile.ReadAllText(path)).RootElement.Clone();

    private static (int, int, byte[], (byte, byte, byte)[]) ReadPng(string path)
    {
        using var fs = S2Port.Extraction.DataFile.OpenRead(path);
        return IndexedPng.Read(fs);
    }
}
