using System.Security.Cryptography;
using System.Text.Json;

namespace S2Port.Extraction.Smb3;

public sealed record Smb3CampaignArea(int Act, Smb3Area Source, string Directory, string Status,
    int[] UnmappedObjects, string[] Blockers, int Width, int Height, string? AlternateAreaId);
public sealed record Smb3CampaignData(int Version, string RomSha1, string BaseSha256,
    Smb3World[] Worlds, Smb3CampaignArea[] Areas, string[] Blockers)
{
    public bool Complete => Blockers.Length == 0 && Areas.All(a => a.Status == "engine-ready");
}

/// <summary>Native batch adapter on top of the user's existing engine assets; never rebuilds from rom.bin.</summary>
public static class Smb3Campaign
{
    public const int Version = 3, Zone = 0x43;
    public static readonly string ManifestPath = Path.Combine("campaign", "campaign.json");

    public static Smb3CampaignData? Load(string dataDir) =>
        DataFile.Exists(Path.Combine(dataDir, ManifestPath))
            ? JsonSerializer.Deserialize<Smb3CampaignData>(DataFile.ReadAllText(Path.Combine(dataDir, ManifestPath)), Manifest.JsonOptions)
                ?? throw new InvalidDataException("Empty campaign manifest.")
            : null;

    public static string AreaDirectory(string dataDir, int act)
    {
        var campaign = Load(dataDir) ?? throw new InvalidDataException("Campaign data has not been built.");
        var area = campaign.Areas.SingleOrDefault(a => a.Act == act)
            ?? throw new ArgumentOutOfRangeException(nameof(act), $"Unknown campaign act {act}.");
        if (area.Status is "failed" or "blocked")
            throw new InvalidDataException($"{area.Source.Id} is unavailable: {string.Join(" ", area.Blockers)}");
        return Path.Combine(dataDir, "zones", area.Directory);
    }

    public static bool IsCurrent(string archive, string baseArchive, NesRom rom)
    {
        var manifest = Load(archive);
        return manifest is { Version: Version } && manifest.RomSha1 == rom.Sha1 &&
            manifest.BaseSha256 == Hash(baseArchive);
    }

    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static Smb3CampaignData Build(NesRom rom, string directory, string baseSha256, Action<string>? log = null)
    {
        Smb3Catalog.RequireSupported(rom);
        var catalog = Smb3Catalog.Read(rom);
        // Preserve the old stage adapters and all of their presentation assets.
        foreach (var level in Smb3Levels.All)
            new Smb3ZoneBuilder(rom, directory, log, level).Build();
        Smb3Conversion.Build(rom, directory, log);
        Smb3Effects.Build(rom, directory);
        var results = new List<Smb3CampaignArea>();
        foreach (var (area, index) in catalog.Areas.Select((area, index) => (area, index)))
        {
            var blockers = new List<string>();
            string folder = $"area_{area.Id}", status = "native-partial";
            int width = 0, height = 0;
            int[] unknown = [];
            string? alternateId = null;
            try
            {
                var decoded = new Smb3DecodedArea(rom, area);
                width = decoded.Vertical ? 256 : (decoded.Width + 1) * 256;
                height = decoded.Vertical ? (decoded.Width + 1) * 240 : 432;
                var objects = Smb3ObjectConverter.Read(rom, area.ObjectBank, area.ObjectAddress);
                unknown = objects.Where(o => !Smb3ObjectConverter.Rules.ContainsKey(o.Id))
                    .Select(o => o.Id).Distinct().Order().ToArray();
                if (unknown.Length != 0)
                    blockers.Add($"Missing native object behaviors: {string.Join(", ", unknown.Select(id => $"${id:X2}"))}.");
                alternateId = catalog.Areas.FirstOrDefault(a => a.Tileset == (decoded.Header[6] & 15) &&
                    a.LayoutAddress == (decoded.Header[0] | decoded.Header[1] << 8) &&
                    a.ObjectAddress == (decoded.Header[2] | decoded.Header[3] << 8))?.Id;
                if (!objects.Any(o => o.Id == 0x41))
                    blockers.Add("This area has no native goal card; its source exit/boss/event needs an adapter.");
                if (area.Tileset is not (1 or 3))
                    blockers.Add("Tileset-specific hazards, ice, conveyors and event semantics remain incomplete; terrain is an experimental native build.");
                if (decoded.Vertical)
                    blockers.Add("Vertical entry coordinates and object encoding still need validation against the source.");
                if (objects.Any(o => o.Id is >= 0xD0 and <= 0xDF))
                    blockers.Add("Source scrolling/controller objects require native event adapters.");

                int palette = rom.Prg(27, 0xB7D2 + area.Tileset * 2) | rom.Prg(27, 0xB7D3 + area.Tileset * 2) << 8;
                int bgIndex = decoded.Header[7] & 31;
                if (bgIndex > 22) throw new InvalidDataException($"Invalid background CHR bank index {bgIndex}.");
                int music = decoded.Header[8] & 15;
                if (music > 10) throw new InvalidDataException($"Unsupported music selector {music}.");
                var recipe = new Smb3Level(area.Id, Zone, (area.LayoutBank, area.LayoutAddress),
                    (area.ObjectBank, area.ObjectAddress), area.Tileset,
                    (area.LayoutBank, 0xA000), (area.LayoutBank, 0xA400),
                    rom.Prg(30, 0x9762 + bgIndex), palette, (music + 1) * 16,
                    Smb3ZoneBuilder.MusicId, Smb3ZoneBuilder.MusicAddress);
                new Smb3ZoneBuilder(rom, directory, log, recipe, area).Build();
            }
            catch (InvalidDataException error)
            {
                status = "blocked";
                blockers.Add(error.Message);
                log?.Invoke($"{area.Id}: blocked - {error.Message}");
            }
            catch (InvalidOperationException error) when (error.Message.StartsWith("opcode $", StringComparison.Ordinal))
            {
                status = "failed";
                blockers.Add(error.Message);
                log?.Invoke($"{area.Id}: failed - {error.Message}");
            }
            results.Add(new(index + 1, area, folder, status, unknown, blockers.ToArray(), width, height, alternateId));
        }
        var result = new Smb3CampaignData(Version, rom.Sha1, baseSha256, catalog.Worlds, results.ToArray(),
        [
            "Source map commands, world transitions, locks, bridges, wandering enemies and world-clear boss events need native adapters.",
            "Native builds are not full gameplay conversions: missing actors and per-area exit/hazard/event blockers remain listed.",
            "The complete stock Sonic 2 engine asset importer remains unfinished; the approved existing local base archive is required.",
        ]);
        Save(directory, result);
        return result;
    }

    public static void Save(string directory, Smb3CampaignData campaign)
    {
        string path = Path.Combine(directory, ManifestPath);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(campaign, Manifest.JsonOptions));
        string backlog = Path.Combine(directory, "campaign", "backlog.json");
        File.WriteAllText(backlog, JsonSerializer.Serialize(campaign.Areas.SelectMany(a =>
            a.UnmappedObjects.Select(id => (Id: id, Area: a.Source.Id))).GroupBy(x => x.Id)
                .Select(g => new { sourceId = g.Key, affectedAreas = g.Select(x => x.Area).ToArray() })
                .OrderByDescending(g => g.affectedAreas.Length).ThenBy(g => g.sourceId), Manifest.JsonOptions));
    }
}
