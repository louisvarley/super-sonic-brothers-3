using System.Text.Json;
using S2Port.Extraction.Levels;

namespace SonicBros3.Logic;

/// <summary>One mapping piece: y offset, size byte, pattern word (relative to art_tile), x offset.</summary>
public readonly record struct MappingPiece(int Y, int Size, int Pattern, int X);

/// <summary>An object's sprite data: mapping frames, and optional dynamic art (DPLCs + uncompressed tiles).</summary>
public sealed class SpriteData
{
    public required IReadOnlyList<MappingPiece[]> Frames { get; init; }
    public IReadOnlyList<(int FirstTile, int Count)[]>? Dplc { get; init; }
    public byte[]? Art { get; init; }
    /// <summary>ROM address of the mapping table, which object code stores at offset $04 (used as a key).</summary>
    public int RomPointer { get; init; }
    public int[]? FrameOffsets { get; init; }
    /// <summary>AnimateSprite scripts as a raw block; the table of word offsets is at its start.</summary>
    public byte[]? Animations { get; init; }

    /// <summary>
    /// The pointers this data answers to: the table itself, and every piece of every frame (static mappings point
    /// straight at one piece: frame start + 2 + 8 per piece).
    /// </summary>
    public IEnumerable<(uint Pointer, bool Static, int Frame, int Piece)> Pointers()
    {
        yield return ((uint)RomPointer, false, 0, 0);
        if (FrameOffsets is null) yield break;
        for (int f = 0; f < FrameOffsets.Length; f++)
            for (int k = 0; k < Frames[f].Length; k++)
                yield return ((uint)(RomPointer + FrameOffsets[f] + 2 + k * 8), true, f, k);
    }

    /// <summary>Loads objects/&lt;name&gt;/sprites.json (format s2port.sprites/1).</summary>
    public static SpriteData Load(string dataDir, string name)
    {
        string dir = Path.Combine(dataDir, "objects", name);
        using var doc = JsonDocument.Parse(S2Port.Extraction.DataFile.ReadAllText(Path.Combine(dir, "sprites.json")));
        var root = doc.RootElement;
        var frames = root.GetProperty("mappings").EnumerateArray()
            .Select(f => f.EnumerateArray().Select(p => new MappingPiece(p[0].GetInt32(), p[1].GetInt32(), p[2].GetInt32(), p[3].GetInt32())).ToArray())
            .ToList();
        IReadOnlyList<(int, int)[]>? dplc = null;
        byte[]? art = null;
        if (root.TryGetProperty("dplc", out var d))
        {
            dplc = d.EnumerateArray().Select(f => f.EnumerateArray().Select(e => (e[0].GetInt32(), e[1].GetInt32())).ToArray()).ToList();
            art = LevelLoader.LoadTileArt(Path.Combine(dir, root.GetProperty("art").GetString()!), root.GetProperty("artTiles").GetInt32());
        }
        if (root.TryGetProperty("art", out var artName) && art is null)
            art = LevelLoader.LoadTileArt(Path.Combine(dir, artName.GetString()!), root.GetProperty("artTiles").GetInt32());
        int pointer = root.TryGetProperty("romPointer", out var rp) ? rp.GetInt32() : 0;
        int[]? offsets = root.TryGetProperty("frameOffsets", out var fo) ? fo.EnumerateArray().Select(e => e.GetInt32()).ToArray() : null;
        byte[]? anims = root.TryGetProperty("animations", out var an) && an.ValueKind == JsonValueKind.Array
            ? an.EnumerateArray().Select(e => (byte)e.GetInt32()).ToArray() : null;
        return new SpriteData { Frames = frames, Dplc = dplc, Art = art, RomPointer = pointer, FrameOffsets = offsets, Animations = anims };
    }
}
