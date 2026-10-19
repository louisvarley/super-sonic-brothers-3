using System.Text.Json;

namespace S2Port.Extraction.Levels;

/// <summary>
/// Readable JSON forms of blocks, chunks and layouts, and the matching readers.
/// Bit fields are expanded into named, ordered arrays; see "fields" in each file.
/// </summary>
public static class LevelJson
{
    public const string BlocksFormat = "s2port.blocks/1";
    public const string ChunksFormat = "s2port.chunks/1";
    public const string LayoutFormat = "s2port.layout/1";

    // ------------------------------------------------------------- blocks

    public static object BlocksToJson(ushort[] words) => new
    {
        format = BlocksFormat,
        fields = new[] { "tile", "paletteLine", "flipX", "flipY", "priority" },
        order = "topLeft, topRight, bottomLeft, bottomRight",
        count = words.Length / 4,
        blocks = Enumerable.Range(0, words.Length / 4)
            .Select(b => Enumerable.Range(0, 4).Select(i => PatternFields(words[b * 4 + i])).ToArray()),
    };

    public static int[] PatternFields(ushort w) =>
        [w & 0x7FF, (w >> 13) & 3, (w >> 11) & 1, (w >> 12) & 1, w >> 15];

    public static ushort PatternWord(JsonElement f) =>
        (ushort)(f[0].GetInt32() | (f[1].GetInt32() << 13) | (f[2].GetInt32() << 11) |
                 (f[3].GetInt32() << 12) | (f[4].GetInt32() << 15));

    public static ushort[] BlocksFromJson(JsonElement root)
    {
        Expect(root, BlocksFormat);
        var list = new List<ushort>();
        foreach (var block in root.GetProperty("blocks").EnumerateArray())
        foreach (var p in block.EnumerateArray())
            list.Add(PatternWord(p));
        return list.ToArray();
    }

    // ------------------------------------------------------------- chunks

    public static object ChunksToJson(ushort[] words) => new
    {
        format = ChunksFormat,
        fields = new[] { "block", "flipX", "flipY", "solidityPrimary", "soliditySecondary" },
        solidity = "bit 0 = top solid, bit 1 = left/right/bottom solid",
        size = LevelData.BlocksPerChunkSide,
        count = words.Length / 64,
        chunks = Enumerable.Range(0, words.Length / 64).Select(c =>
            Enumerable.Range(0, 8).Select(row =>
                Enumerable.Range(0, 8).Select(col => ChunkFields(words[c * 64 + row * 8 + col])).ToArray())),
    };

    public static int[] ChunkFields(ushort w) =>
        [w & 0x3FF, (w >> 10) & 1, (w >> 11) & 1, (w >> 12) & 3, (w >> 14) & 3];

    public static ushort ChunkWord(JsonElement f) =>
        (ushort)(f[0].GetInt32() | (f[1].GetInt32() << 10) | (f[2].GetInt32() << 11) |
                 (f[3].GetInt32() << 12) | (f[4].GetInt32() << 14));

    public static ushort[] ChunksFromJson(JsonElement root)
    {
        Expect(root, ChunksFormat);
        var list = new List<ushort>();
        foreach (var chunk in root.GetProperty("chunks").EnumerateArray())
        foreach (var row in chunk.EnumerateArray())
        foreach (var e in row.EnumerateArray())
            list.Add(ChunkWord(e));
        return list.ToArray();
    }

    // ------------------------------------------------------------- layout

    public static object LayoutToJson(byte[] fg, byte[] bg)
    {
        var (fw, fh) = LevelData.UsedExtent(fg);
        var (bw, bh) = LevelData.UsedExtent(bg);
        return new
        {
            format = LayoutFormat,
            width = LevelData.LayoutWidth,
            height = LevelData.LayoutHeight,
            cellSize = 128,
            foregroundUsed = new { width = fw, height = fh },
            backgroundUsed = new { width = bw, height = bh },
            foreground = Rows(fg),
            background = Rows(bg),
        };
    }

    private static int[][] Rows(byte[] plane) =>
        Enumerable.Range(0, LevelData.LayoutHeight)
            .Select(r => plane.Skip(r * LevelData.LayoutWidth).Take(LevelData.LayoutWidth).Select(b => (int)b).ToArray())
            .ToArray();

    public static (byte[] Fg, byte[] Bg) LayoutFromJson(JsonElement root)
    {
        Expect(root, LayoutFormat);
        return (Plane(root.GetProperty("foreground")), Plane(root.GetProperty("background")));

        static byte[] Plane(JsonElement rows) =>
            rows.EnumerateArray().SelectMany(r => r.EnumerateArray().Select(v => (byte)v.GetInt32())).ToArray();
    }

    // ------------------------------------------------------------- objects and rings

    public const string ObjectsFormat = "s2port.objects/1";
    public const string RingsFormat = "s2port.rings/1";

    public static object ObjectsToJson(IReadOnlyList<ObjectPlacement> objects, int listAddress = 0, int boundaryX = 0) => new
    {
        format = ObjectsFormat,
        listAddress,
        boundaryX,
        fields = new[] { "x", "y", "id", "subtype", "flipX", "flipY", "remember" },
        count = objects.Count,
        objects = objects.Select(o => new[] { o.X, o.Y, o.Id, o.Subtype, o.FlipX ? 1 : 0, o.FlipY ? 1 : 0, o.Remember ? 1 : 0 }),
    };

    public static IReadOnlyList<ObjectPlacement> ObjectsFromJson(JsonElement root)
    {
        Expect(root, ObjectsFormat);
        return root.GetProperty("objects").EnumerateArray().Select(o => new ObjectPlacement(
            o[0].GetInt32(), o[1].GetInt32(), o[2].GetInt32(), o[3].GetInt32(),
            o[4].GetInt32() != 0, o[5].GetInt32() != 0, o[6].GetInt32() != 0)).ToList();
    }

    public static object RingsToJson(IReadOnlyList<RingGroup> groups) => new
    {
        format = RingsFormat,
        fields = new[] { "x", "y", "count", "vertical" },
        spacing = RingGroup.Spacing,
        groupCount = groups.Count,
        ringCount = groups.Sum(g => g.Count),
        groups = groups.Select(g => new[] { g.X, g.Y, g.Count, g.Vertical ? 1 : 0 }),
    };

    public static IReadOnlyList<RingGroup> RingsFromJson(JsonElement root)
    {
        Expect(root, RingsFormat);
        return root.GetProperty("groups").EnumerateArray().Select(g => new RingGroup(
            g[0].GetInt32(), g[1].GetInt32(), g[2].GetInt32(), g[3].GetInt32() != 0)).ToList();
    }

    private static void Expect(JsonElement root, string format)
    {
        string? actual = root.GetProperty("format").GetString();
        if (actual != format) throw new InvalidDataException($"Expected format {format}, found {actual}.");
    }
}
