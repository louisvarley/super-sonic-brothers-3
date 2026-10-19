using System.Text.Json;
using S2Port.Extraction.Graphics;

namespace S2Port.Extraction.Smb3;

/// <summary>
/// Sprites for the Easter egg's objects, from smb3.nes: the bumpable blocks (Plains 16x16 tiles, their four animation
/// frames from the animated CHR banks) and the 1-1 enemies. Enemy frames are composed the way the SMB3 draw routines
/// place their 8x16 sprites (GroundTroop_DrawMirrored, Troopa_Draw, ParaGoomba_Draw; pattern pairs from the ObjP tables
/// in prg004/prg005, head tip $C1, feet Troopa_FootByEvenOddFrame, wings $CD/$CF). This is a visual reconstruction for
/// the Easter egg (checked by eye against the CHR and MAME's sprites), not a ported routine.
///
/// Output: zones/zone_40/objects.png (all pieces as MD tiles, loaded at VRAM <see cref="VramTile"/>) and
/// objects/smb3-*/sprites.json (mappings relative to that art; made-up table pointers from $F00000, outside the ROM).
/// Enemies use palette line 3 (NES sprite palette p -> colours p*4+1..3), blocks line 2 (background palettes).
/// </summary>
public sealed class Smb3ObjectArt(NesRom rom, string outDir)
{
    public const int VramTile = 0x340;
    public const uint PointerBase = 0xF00000;

    /// <summary>The 1 KiB CHR banks at PPU $1000/$1400/$1800/$1C00 in 1-1 (PatTable_BankSel+2..5, read in MAME).</summary>
    private static readonly int[] SpriteBanks = [0x53, 0x04, 0x22, 0x4F];

    private const int GenBank4 = 4, GenBank5 = 5;
    private const int ObjP72 = 0xA223, ObjP6C = 0xA1F9, ObjPA0 = 0xA1D4, ObjPA4 = 0xA1E4,
        TroopaFoot = 0xB4A2, TroopaYOff = 0xB4A0, ObjP82 = 0xA275;

    private readonly List<byte[]> _tiles = [];
    private readonly Dictionary<(int Id, int Pal, int Bank), int> _units = [];
    private readonly Dictionary<(int Meta, int Frame), int> _blocks = [];   // 16x16 block -> first MD tile

    /// <summary>A piece: offset from the object's position, an 8x16 NES sprite (odd id) or a block, flip.</summary>
    private readonly record struct Piece(int X, int Y, int Tile, int Size, bool FlipX, bool FlipY = false);

    public void Build()
    {
        var sets = new List<(string Name, List<Piece[]> Frames)>
        {
            ("smb3-block", Blocks()),
            ("smb3-goomba", Goomba(3, wings: false)),
            ("smb3-paragoomba", Goomba(1, wings: true)),
            ("smb3-koopa-green", Koopa(2, wings: false)),
            ("smb3-koopa-red", Koopa(1, wings: false)),
            ("smb3-paratroopa", Koopa(2, wings: true)),
            ("smb3-piranha", Piranha(rom.Prg(GenBank5, ObjPA0, 16), 2, 2)),
            ("smb3-venus", Piranha(rom.Prg(GenBank5, ObjPA4, 16), 1, 2, tall: true)),
            ("smb3-fireball", [[Unit(-4, -8, 0x65, 1, false)], [Unit(-4, -8, 0x65, 1, true)],
                               [Unit(-4, -8, 0x65, 1, true, true)], [Unit(-4, -8, 0x65, 1, false, true)]]),
            // SObj_Microgoomba / Microgoomba_Draw: sprite $FF (bank $4F at $1C00), palette 3, flipped every 8 ticks.
            ("smb3-microgoomba", [[Unit(-4, -8, 0xFF, 3, false)], [Unit(-4, -8, 0xFF, 3, true)]]),
            ("smb3-piranha-red", Piranha(rom.Prg(GenBank5, ObjPA0, 16), 1, 2, tall: true)),
            ("smb3-piranha-fire", Piranha(rom.Prg(GenBank5, ObjPA4, 16), 2, 2)),
            ("smb3-boomerang-bro", BoomerangBro()),
            ("smb3-boomerang", [
                [Unit(-4, -8, 0x8B, 2, true, true, bank: 0x4E)],
                [Unit(-4, -8, 0x8F, 2, true, true, bank: 0x4E)],
                [Unit(-4, -8, 0x89, 2, false, bank: 0x4E)],
                [Unit(-4, -8, 0x8D, 2, false, bank: 0x4E)],
            ]),
        };

        for (int i = 0; i < sets.Count; i++)
        {
            var (name, frames) = sets[i];
            string dir = Path.Combine(outDir, "objects", name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "sprites.json"), JsonSerializer.Serialize(new
            {
                format = "s2port.sprites/1",
                romPointer = (int)(PointerBase + (uint)i * 0x1000),
                source = "smb3.nes (Easter egg; see Smb3ObjectArt)",
                pieceFields = new[] { "y", "size", "pattern", "x" },
                mappings = frames.Select(f => f.Select(p => new[]
                {
                    p.Y, p.Size, p.Tile | (p.FlipX ? 0x800 : 0) | (p.FlipY ? 0x1000 : 0), p.X,
                })),
            }, Manifest.JsonOptions));
        }

        var art = _tiles.SelectMany(t => t).ToArray();
        if (VramTile + _tiles.Count > 0x47C) throw new InvalidDataException($"SMB3 object art too big ({_tiles.Count} tiles)");
        string zoneDir = Path.Combine(outDir, "zones", $"zone_{Smb3ZoneBuilder.Zone:X2}");
        var (w, h, px) = Tiles.ToSheet(art, 16, _ => 3);
        IndexedPng.Write(Path.Combine(zoneDir, "objects.png"), w, h, px, PreviewColours(), transparentIndices: [0, 16, 32, 48]);
        TileCount = _tiles.Count;
        WritePreview(sets, Path.Combine(zoneDir, "objects-preview.png"));
    }

    public int TileCount { get; private set; }

    // ------------------------------------------------------------------ pieces

    /// <summary>An 8x16 NES sprite (id odd: tiles id-1 and id from the $1000 table) in palette <paramref name="pal"/>.</summary>
    private Piece Unit(int x, int y, int id, int pal, bool flipX, bool flipY = false, int? bank = null)
    {
        var key = (id, pal, bank ?? -1);
        if (!_units.TryGetValue(key, out int tile))
        {
            tile = _tiles.Count;
            foreach (int t in new[] { id & 0xFE, id | 1 })
                _tiles.Add(Smb3ZoneBuilder.NesToMd(rom.ChrTile(bank ?? SpriteBanks[t >> 6], t & 0x3F), pal));
            _units[key] = tile;
        }
        return new Piece(x, y, tile, 1, flipX, flipY);   // size 1: 1 tile wide, 2 high
    }

    /// <summary>A Plains 16x16 tile as one 2x2-tile piece (MD order: column-major), animation frame 0-3.</summary>
    private Piece Block(int meta, int frame)
    {
        if (!_blocks.TryGetValue((meta, frame), out int tile))
        {
            tile = _tiles.Count;
            var (bank, addr) = Smb3Map.TileLayoutTs1;
            int[] quads = [rom.Prg(bank, addr + meta), rom.Prg(bank, addr + 256 + meta), rom.Prg(bank, addr + 512 + meta), rom.Prg(bank, addr + 768 + meta)];
            foreach (int chr in quads)   // UL, LL, UR, LR = MD column-major order
            {
                int b = chr < 0x80 ? Smb3Map.PlainsBgBank : Smb3Map.PlainsAnimatedBanks[frame];
                _tiles.Add(Smb3ZoneBuilder.NesToMd(rom.ChrTile(b, chr & 0x7F), meta >> 6));
            }
            _blocks[(meta, frame)] = tile;
        }
        return new Piece(-8, -8, tile, 5, false);   // size 5: 2x2 tiles
    }

    // ------------------------------------------------------------------ sets

    /// <summary>Frames 0-3: ? block; 4-7: brick; 8: used-up block (TILEA_BLOCKEMPTY $5F).</summary>
    private List<Piece[]> Blocks()
    {
        var frames = new List<Piece[]>();
        foreach (int meta in new[] { 0x63, 0x67 })
            for (int f = 0; f < 4; f++) frames.Add([Block(meta, f)]);
        frames.Add([Block(0x5F, 0)]);
        return frames;
    }

    /// <summary>Goomba (ObjP72: mirrored pairs): walk 0, walk 1, squashed; Paragoomba adds wings up/down (frames 3-4).</summary>
    private List<Piece[]> Goomba(int pal, bool wings)
    {
        var p = rom.Prg(GenBank4, ObjP72, 8);
        Piece[] Pair(int a, int b) => [Unit(-8, -8, a, pal, false), Unit(0, -8, b, pal, true)];
        var frames = new List<Piece[]> { Pair(p[0], p[1]), Pair(p[2], p[3]), Pair(p[6], p[7]) };
        if (wings)
            foreach (int wing in new[] { 0xCD, 0xCF })
                frames.Add([.. Pair(p[0], p[1]), Unit(-10, -18, wing, 2, true), Unit(2, -18, wing, 2, false)]);
        return frames;
    }

    /// <summary>
    /// Koopa Troopa facing left (ObjP6C frames 0-1, Troopa_Draw): head tip $C1, body pair (head side in palette 3),
    /// feet from Troopa_FootByEvenOddFrame; frames 0-1 walk (2-3 with a wing), then the shell (ObjP6C frame 2, $FD
    /// mirrored) and the shell upside down. Opaque feet are aligned with the native floor-probe radius.
    /// </summary>
    private List<Piece[]> Koopa(int pal, bool wings)
    {
        var p = rom.Prg(GenBank4, ObjP6C, 14);
        var foot = rom.Prg(GenBank4, TroopaFoot, 6);
        var yOff = rom.Prg(GenBank4, TroopaYOff, 2);
        Piece[] Walk(int f, int wing)
        {
            int y = yOff[f] - 26;
            var list = new List<Piece>
            {
                Unit(-8, y - 16, 0xC1, 3, false),
                Unit(-8, y, p[f * 2], 3, false),
                Unit(0, y, p[f * 2 + 1], pal, false),
                Unit(-8, y + 16, foot[f * 3], 3, false),
                Unit(0, y + 16, foot[f * 3 + 1], 3, false),
            };
            if (wing != 0) list.Add(Unit(0, y - 8, wing, 1, false));
            return AlignFeet(list, 16);
        }
        var frames = new List<Piece[]> { Walk(0, 0), Walk(1, 0) };
        if (wings) { frames.Add(Walk(0, 0xCD)); frames.Add(Walk(1, 0xCF)); }
        frames.Add(AlignFeet([Unit(-8, -8, p[4], pal, false), Unit(0, -8, p[5], pal, true)], 8));
        frames.Add(AlignFeet([Unit(-8, -8, p[4], pal, false, true), Unit(0, -8, p[5], pal, true, true)], 8));
        return frames;
    }

    private Piece[] AlignFeet(List<Piece> pieces, int radius)
    {
        int bottom = int.MinValue;
        foreach (var piece in pieces)
        for (int y = 0; y < 16; y++)
        {
            int sourceY = piece.FlipY ? 15 - y : y;
            var row = _tiles[piece.Tile + sourceY / 8].AsSpan((sourceY & 7) * 4, 4);
            if (row.IndexOfAnyExcept((byte)0) >= 0) bottom = Math.Max(bottom, piece.Y + y);
        }
        if (bottom == int.MinValue) throw new InvalidDataException("Koopa frame has no visible foot pixels.");
        return pieces.Select(p => p with { Y = p.Y + radius - bottom }).ToArray();
    }

    /// <summary>
    /// A piranha plant (ObjPA0 / ObjPA4: 4 bytes per frame, head pair then stem pair): mouth open / closed, drawn
    /// upward from the object's position (the pipe top), 32 px tall. Equal pairs are drawn mirrored.
    /// </summary>
    private List<Piece[]> Piranha(byte[] p, int headPal, int stemPal, bool tall = false)
    {
        var frames = new List<Piece[]>();
        for (int f = 0; f < 2; f++)
        {
            int h0 = p[f * 4], h1 = p[f * 4 + 1], s0 = p[f * 4 + 2], s1 = p[f * 4 + 3];
            int headY = tall ? -48 : -32;
            var pieces = new List<Piece>
            {
                Unit(-8, headY, h0, headPal, false), Unit(0, headY, h1, headPal, h0 == h1),
                Unit(-8, headY + 16, s0, stemPal, false), Unit(0, headY + 16, s1, stemPal, s0 == s1),
            };
            if (tall)
                pieces.AddRange([Unit(-8, -16, s0, stemPal, false), Unit(0, -16, s1, stemPal, s0 == s1)]);
            frames.Add(pieces.ToArray());
        }
        return frames;
    }

    private List<Piece[]> BoomerangBro()
    {
        byte[] patterns = rom.Prg(GenBank4, ObjP82, 16);
        return Enumerable.Range(0, 4).Select(f => new[]
        {
            Unit(-8, -24, patterns[f * 4], 2, false, bank: 0x4E),
            Unit(0, -24, patterns[f * 4 + 1], 2, false, bank: 0x4E),
            Unit(-8, -8, patterns[f * 4 + 2], 2, false, bank: 0x4E),
            Unit(0, -8, patterns[f * 4 + 3], 2, false, bank: 0x4E),
        }).ToList();
    }

    // ------------------------------------------------------------------ preview

    /// <summary>A 64-colour list for PNGs: line 2 = the NES background palettes, line 3 = the sprite palettes.</summary>
    private List<(byte, byte, byte)> PreviewColours()
    {
        var bg = rom.Prg(Smb3Palettes.Bank, Smb3Palettes.PlainsBg, 16);
        var spr = rom.Prg(Smb3Palettes.Bank, Smb3Palettes.Sprites, 16);
        return Enumerable.Range(0, 64).Select(i =>
            i is >= 32 and < 48 ? Smb3Palettes.ToRgb(bg[i - 32]) : i >= 48 ? Smb3Palettes.ToRgb(spr[i - 48]) : ((byte)0, (byte)0, (byte)0)).ToList();
    }

    private void WritePreview(List<(string Name, List<Piece[]> Frames)> sets, string path)
    {
        int cols = sets.Max(s => s.Frames.Count);
        int cw = 40, ch = 56;
        int width = cols * cw, height = sets.Count * ch;
        var px = new byte[width * height];
        Array.Fill(px, (byte)1);
        for (int s = 0; s < sets.Count; s++)
        for (int f = 0; f < sets[s].Frames.Count; f++)
        foreach (var piece in sets[s].Frames[f])
        {
            int tw = piece.Size == 5 ? 2 : 1, th = 2;
            int line = s == 0 ? 2 : 3;
            for (int ty = 0; ty < th * 8; ty++)
            for (int tx = 0; tx < tw * 8; tx++)
            {
                int sx = piece.FlipX ? tw * 8 - 1 - tx : tx, sy = piece.FlipY ? th * 8 - 1 - ty : ty;
                int tile = piece.Tile + (sx / 8) * th + sy / 8;
                byte b = _tiles[tile][(sy % 8) * 4 + (sx % 8) / 2];
                int c = (sx % 8 & 1) == 0 ? b >> 4 : b & 15;
                if (c == 0) continue;
                int x = f * cw + cw / 2 + piece.X + tx, y = s * ch + 40 + piece.Y + ty;
                if ((uint)x < width && (uint)y < height) px[y * width + x] = (byte)(line * 16 + c);
            }
        }
        var colours = PreviewColours();
        colours[1] = (100, 180, 220);
        IndexedPng.Write(path, width, height, px, colours);
    }
}
