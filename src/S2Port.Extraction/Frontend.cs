using System.Text.Json;
using S2Port.Extraction.Compression;
using S2Port.Extraction.Graphics;

namespace S2Port.Extraction;

/// <summary>
/// The title screen (game mode $04, 0x46B8) and level select (game mode $28, MenuScreen 0xA174 / 0xA914): their art
/// (Nemesis, as PNG sheets in art/nemesis), and the ROM regions their ported code reads by address (plane maps,
/// tables, palettes, the intro text), as objects/frontend/*.json data tables.
/// </summary>
public static class FrontendMap
{
    /// <summary>Nemesis art the two screens decompress, with the VRAM tile each one goes to.</summary>
    public static readonly (string Screen, int Source, int VramTile)[] Art =
    [
        ("title-intro", 0x00D49A, 0x500),   // credit text font (the "Sonic and Miles 'Tails' Prower in" text)
        ("title", 0x07BEB4, 0x000),   // title background and emblem
        ("title", 0x07DC32, 0x1D0),   // title sprites (Sonic, Tails, stars)
        ("title", 0x080274, 0x472),   // menu junk (the menu cursor)
        ("title", 0x0839F2, 0x680),   // menu font
        ("levelselect", 0x0839F2, 0x010),
        ("levelselect", 0x084F36, 0x070),
    ];

    /// <summary>ROM regions read by address (objects/&lt;file&gt;).</summary>
    public static readonly (string File, int Address, int Length)[] Tables =
    [
        ("frontend/title-object.json", 0x15D5C, 0x16AD8 - 0x15D5C),   // Obj0E/ObjC9 code, positions, palettes, animations
        ("frontend/title-maps.json", 0x7BCF6, 0x7BEB4 - 0x7BCF6 + 4), // the three Enigma plane maps (+4: EniDec reads ahead)
        ("frontend/intro-text.json", 0xCA1A, 0x1C),                    // the intro text list (0xC9DC draws it)
        ("frontend/intro-text-strings.json", 0xD45D, 0xD49A - 0xD45D),
        ("frontend/title-scroll.json", 0xDFA8, 0x40),                  // the wave table of SwScrl_Title (0xDE3C) and SwScrl_EHZ
        ("frontend/copyright.json", 0x4BD0, 22),                       // the "(c) 1992 SEGA" words (0x4884)
        ("frontend/levelselect.json", 0xAA6A, 0xADFE - 0xAA6A),        // order, column switch and highlight tables
        ("frontend/levelselect-text.json", 0xB17A, 0xB3B8 - 0xB17A + 4), // the level select text map (Enigma)
        ("frontend/menu-back.json", 0x84126, 0x842D2 - 0x84126 + 4),   // the "SONIC" background map (Enigma)
        ("frontend/menu-back-anim.json", 0x9D60, 0x16),                // its animation script (Dynamic_Normal)
        ("frontend/menu-back-art.json", 0x842D2, 0x28 * 32),           // the animation's uncompressed art
    ];
}

public sealed partial class Extractor
{
    private void ExtractFrontend()
    {
        var written = new HashSet<int>();
        var art = new List<object>();
        foreach (var (screen, src, vramTile) in FrontendMap.Art)
        {
            string png = $"art/nemesis/nem_{src:X6}.png";
            var data = Nemesis.Decompress(rom, src, out int packed);
            if (written.Add(src) && !File.Exists(Path.Combine(outDir, png)))
            {
                var (w, h, px) = Tiles.ToSheet(data, 16);
                IndexedPng.Write(Path.Combine(outDir, png), w, h, px,
                    Enumerable.Range(0, 64).Select(i => ((byte)(i * 4), (byte)(i * 4), (byte)(i * 4))).ToList());
                _manifest.Items.Add(new ManifestItem(png, "nemesis-art", [SourceRef.At(src, "nemesis", packed, data.Length)], "front-end art"));
            }
            art.Add(new { screen, art = $"../art/nemesis/nem_{src:X6}.png", tiles = data.Length / Tiles.BytesPerTile, vramTile });
        }
        WriteJson("frontend/art.json", new { format = "s2port.frontend-art/1", art });
        foreach (var (file, address, length) in FrontendMap.Tables)
        {
            string rel = Path.Combine("objects", file);
            WriteCompactJson(rel, new { baseAddress = address, data = rom.Slice(address, length).ToArray().Select(b => (int)b) });
            _manifest.Items.Add(new ManifestItem(rel, "data-table", [SourceRef.At(address, "raw", size: length)]));
        }
        log?.Invoke($"Front end: {written.Count} art blocks, {FrontendMap.Tables.Length} tables");
    }
}
