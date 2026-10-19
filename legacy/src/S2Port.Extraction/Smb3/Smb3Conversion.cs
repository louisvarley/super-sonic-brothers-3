using System.Text.Json;

namespace S2Port.Extraction.Smb3;

public sealed record Smb3AreaResult(string Id, string Status, int[] UnmappedObjects, string[] Blockers);
public sealed record Smb3SongResult(string Set, int Selector, string Status, int IntroFrames,
    int LoopFrames, string? Error);
public sealed record Smb3ConversionReport(int Version, string RomSha1, int ExtractorVersion,
    int WorldCount, Smb3AreaResult[] Areas, Smb3SongResult[] Songs, string[] CampaignBlockers)
{
    public bool Complete => Areas.All(a => a.Status == "engine-ready") &&
        Songs.All(s => s.Status == "converted") && CampaignBlockers.Length == 0;
}

/// <summary>A deterministic whole-cartridge import with explicit native-engine readiness, not a gameplay emulator.</summary>
public static class Smb3Conversion
{
    public const int Version = 5;
    public static readonly string ReportPath = Path.Combine("conversion", "report.json");

    public static bool IsCurrent(string dataDir, NesRom rom)
    {
        string path = Path.Combine(dataDir, ReportPath);
        if (!DataFile.Exists(path)) return false;
        var report = JsonSerializer.Deserialize<Smb3ConversionReport>(DataFile.ReadAllText(path), Manifest.JsonOptions);
        return report is { Version: Version, ExtractorVersion: Extractor.Version } && report.RomSha1 == rom.Sha1;
    }

    public static Smb3ConversionReport Build(NesRom rom, string output, Action<string>? log = null)
    {
        Smb3Catalog.RequireSupported(rom);
        if (Extractor.IsArchivePath(output))
            throw new ArgumentException("Conversion.Build requires an extracted working directory.", nameof(output));
        string existingReport = Path.Combine(output, ReportPath);
        if (File.Exists(existingReport)) File.Delete(existingReport);
        var catalog = Smb3Catalog.Read(rom);
        void Json(string name, object value)
        {
            string path = Path.Combine(output, "conversion", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value, Manifest.JsonOptions));
        }
        Json("catalog.json", catalog);
        Json("logic/rules.json", Smb3ObjectConverter.Rules);
        string graphics = Path.Combine(output, "conversion", "graphics");
        Directory.CreateDirectory(graphics);
        for (int bank = 0; bank < rom.ChrBankCount; bank++)
        for (int palette = 0; palette < 4; palette++)
        {
            var tiles = new List<byte>();
            for (int tile = 0; tile < 64; tile++)
                tiles.AddRange(Smb3ZoneBuilder.NesToMd(rom.ChrTile(bank, tile), palette));
            File.WriteAllBytes(Path.Combine(graphics, $"chr_{bank:X2}_p{palette}.bin"), tiles.ToArray());
        }
        var areaResults = new List<Smb3AreaResult>();
        foreach (var area in catalog.Areas)
        {
            var blockers = new List<string>();
            Smb3ObjectConverter.SourceObject[] objects;
            try { objects = Smb3ObjectConverter.Read(rom, area.ObjectBank, area.ObjectAddress); }
            catch (InvalidDataException ex)
            {
                log?.Invoke($"{area.Id}: {ex.Message}");
                areaResults.Add(new(area.Id, "failed", [], [ex.Message]));
                continue;
            }
            var unknown = objects.Where(o => !Smb3ObjectConverter.Rules.ContainsKey(o.Id))
                .Select(o => o.Id).Distinct().Order().ToArray();
            var native = objects.Select(o => new { source = o, placement = Smb3ObjectConverter.Convert(o) }).ToArray();
            Json($"areas/{area.Id}/objects.json", native);
            try
            {
                var decoded = new Smb3DecodedArea(rom, area);
                File.WriteAllBytes(Path.Combine(output, "conversion", "areas", area.Id, "tiles.bin"), decoded.TileMemory);
                Json($"areas/{area.Id}/area.json", new
                {
                    format = "sonicbros3.area/1", source = area, header = decoded.Header.Select(b => (int)b),
                    tileMemory = "tiles.bin", tileLayout = $"../../tilesets/ts{area.Tileset:D2}.json",
                    orientation = decoded.Vertical ? "vertical" : "horizontal",
                    screens = decoded.Width + 1, screenRows = Smb3Map.ScreenRows,
                    screenColumns = 16, seed = Smb3DecodedArea.Seed, instructions = decoded.Instructions,
                    alternateLayout = decoded.Header[0] | decoded.Header[1] << 8,
                    alternateObjects = decoded.Header[2] | decoded.Header[3] << 8,
                    alternateTileset = decoded.Header[6] & 15,
                });
                int paletteAddress = rom.Prg(27, 0xB7D2 + area.Tileset * 2) |
                    rom.Prg(27, 0xB7D3 + area.Tileset * 2) << 8;
                int bgPalette = decoded.Header[5] & 7, spritePalette = ((decoded.Header[5] >> 3) & 3) | 8;
                int bgIndex = decoded.Header[7] & 31;
                if (bgIndex > 22) throw new InvalidDataException($"Invalid background bank index {bgIndex}.");
                Json($"areas/{area.Id}/graphics.json", new
                {
                    format = "sonicbros3.graphics/1",
                    chrDirectory = "../../graphics",
                    chrEncoding = "32 bytes per Mega Drive 4bpp tile, 64 tiles per 1 KiB source bank; chr_XX_pN.bin",
                    backgroundBanks = new[] { (int)rom.Prg(30, 0x9762 + bgIndex), (int)rom.Prg(30, 0x9779 + bgIndex) },
                    backgroundPalette = rom.Prg(27, paletteAddress + bgPalette * 16, 16).Select(b => (int)Smb3Palettes.ToMd(b)),
                    spritePalette = rom.Prg(27, paletteAddress + spritePalette * 16, 16).Select(b => (int)Smb3Palettes.ToMd(b)),
                    animationBanks = rom.Prg(30, 0x9779 + bgIndex) == 0x60 ? Smb3Map.PlainsAnimatedBanks : [],
                    note = "CHR has been converted; native palette-line assignment and tileset-specific animation/event semantics need adapters.",
                });
                if (decoded.Vertical) blockers.Add("Vertical layout/camera and junction adapters are not implemented.");
            }
            catch (InvalidOperationException ex)
            {
                blockers.Add($"Original loader failed: {ex.Message}");
                log?.Invoke($"{area.Id}: {ex.Message}");
                areaResults.Add(new(area.Id, "failed", unknown, blockers.ToArray()));
                continue;
            }
            var level = Smb3Levels.All.FirstOrDefault(l => l.Tileset == area.Tileset &&
                l.Layout == (area.LayoutBank, area.LayoutAddress) && l.Objects == (area.ObjectBank, area.ObjectAddress));
            if (level is null)
                blockers.Add("Decoded intermediate data; native terrain, palette, animation, entrance and event adapters need approval.");
            if (unknown.Length > 0) blockers.Add("Native object behaviours are missing; see unmappedObjects.");
            if (level?.Limitation is { } limitation) blockers.Add(limitation);
            if (level is not null && !DataFile.Exists(Path.Combine(output, "zones", $"zone_{level.Zone:X2}", "smb3.json")))
                blockers.Add("Native zone has not been built.");
            areaResults.Add(new(area.Id, blockers.Count == 0 ? "engine-ready" : level is not null ? "native-partial" : "decoded",
                unknown, blockers.ToArray()));
        }
        foreach (int tileset in catalog.Areas.Select(a => a.Tileset).Distinct().Order())
        {
            int bank = rom.Prg(30, 0x83E9 + tileset);
            Json($"tilesets/ts{tileset:D2}.json", new
            {
                format = "sonicbros3.tileset/1", tileset, bank,
                // Layout stores four 256-byte quadrant arrays: UL, LL, UR, LR.
                layout = rom.Prg(bank, 0xA000, 1024).Select(b => (int)b),
                collisionThresholds = rom.Prg(bank, 0xA400, 8).Select(b => (int)b),
            });
        }
        foreach (var world in catalog.Worlds) Json($"worlds/world{world.Number}.json", world);
        var songs = ConvertSongs(rom, output, Json, log);
        Json("logic/backlog.json", areaResults.SelectMany(a => a.UnmappedObjects.Select(id => (id, a.Id)))
            .GroupBy(o => o.id).OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Select(g => new { sourceId = g.Key, affectedAreas = g.Select(o => o.Id).ToArray() }));
        var report = new Smb3ConversionReport(Version, rom.Sha1, Extractor.Version, catalog.Worlds.Length,
            areaResults.ToArray(), songs,
            [
                "World maps 2-8, multi-screen map travel, locks, inventory, airships and world completion need native flow adapters.",
                "Special map entries (null areaId in catalog) are commands/minigames, not ordinary levels.",
                "Alternate headers are discovered recursively; object-driven warps and scripted transitions need native logic adapters.",
                "The offline 6502 runs level generators only. ROM gameplay machine code is not translated into Sonic behaviours.",
            ]);
        Json("report.json", report);
        log?.Invoke($"Conversion: {report.WorldCount} worlds, {report.Areas.Length} areas " +
            $"({report.Areas.Count(a => a.Status == "engine-ready")} engine-ready, " +
            $"{report.Areas.Count(a => a.Status == "native-partial")} native main-route builds with blockers, " +
            $"{report.Areas.Count(a => a.Status == "failed")} failed), " +
            $"{songs.Count(s => s.Status == "converted")}/{songs.Length} songs converted.");
        return report;
    }

    private static Smb3SongResult[] ConvertSongs(NesRom rom, string output,
        Action<string, object> json, Action<string>? log)
    {
        string player = Path.Combine(output, "audio", "smps68k", "player.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(player));
        ushort[] Words(string name) => doc.RootElement.GetProperty(name).EnumerateArray()
            .Select(v => (ushort)v.GetInt32()).ToArray();
        var results = new List<Smb3SongResult>();
        foreach (bool set2A in new[] { true, false })
        for (int n = 1; n <= (set2A ? 15 : 12); n++)
        {
            string set = set2A ? "2A" : "2B";
            int selector = n * 16;
            try
            {
                var converter = new Smb3SongConverter(Words("psgFrequencies"), Words("fmFrequencies"));
                // Each song can be mounted alone in the existing driver's extraSongs slot.
                byte[] song = converter.Convert(rom, selector, Smb3ZoneBuilder.MusicAddress,
                    out int intro, out int loop, set2A);
                string name = $"set{set}_{selector:X2}";
                json($"music/{name}.json", new
                {
                    songs = new[] { new { id = Smb3ZoneBuilder.MusicId, address = Smb3ZoneBuilder.MusicAddress, file = name + ".bin" } },
                    sourceSet = set, selector, introFrames = intro, loopFrames = loop,
                });
                File.WriteAllBytes(Path.Combine(output, "conversion", "music", name + ".bin"), song);
                results.Add(new(set, selector, "converted", intro, loop, null));
            }
            catch (InvalidDataException ex)
            {
                log?.Invoke($"Music set {set} ${selector:X2}: {ex.Message}");
                results.Add(new(set, selector, "failed", 0, 0, ex.Message));
            }
        }
        return results.ToArray();
    }
}
