using System.Text.Json;
using S2Port.Extraction;
using S2Port.Extraction.Smb3;
using SonicBros3.Smb3;
using System.IO.Compression;
using SonicBros3.Runtime;

try
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    bool force = false, strict = false, campaign = false;
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--force": force = true; break;
            case "--strict": strict = true; break;
            case "--campaign": campaign = true; break;
            case "--rom" or "--smb3" or "--output" or "--base":
                string key = args[i];
                if (++i >= args.Length || args[i].StartsWith("--"))
                    throw new ArgumentException($"{key} requires a path.");
                if (!options.TryAdd(key, args[i])) throw new ArgumentException($"Duplicate option {key}.");
                break;
            case "--help":
                Console.WriteLine("ConvertSmb3 [--rom rom.bin] [--smb3 smb3.nes] [--output sonicbros3.bin] [--force] [--strict]");
                Console.WriteLine("ConvertSmb3 --campaign --base sonicbros3.bin [--smb3 smb3.nes] [--output web-data/campaign.bin] [--force] [--strict]");
                Console.WriteLine("Exit 0: batch built (may contain blockers); 1: error; 2: --strict found incomplete coverage.");
                return 0;
            default: throw new ArgumentException($"Unknown option {args[i]}. Use --help.");
        }
    }
    string root = DataPaths.BaseFolder();
    string output = Path.GetFullPath(options.GetValueOrDefault("--output",
        Path.Combine(root, campaign ? Path.Combine("web-data", "campaign.bin") : "sonicbros3.bin")));
    if (campaign)
    {
        string baseArchive = Path.GetFullPath(options.GetValueOrDefault("--base", Path.Combine(root, "sonicbros3.bin")));
        if (output.Equals(baseArchive, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Campaign output must not overwrite the original base archive.");
        if (!DataFile.IsArchive(baseArchive)) throw new InvalidDataException("--base must be the existing original-engine asset archive.");
        var rom = NesRom.Load(options.GetValueOrDefault("--smb3", Path.Combine(root, "smb3.nes")));
        if (!force && File.Exists(output) && Smb3Campaign.IsCurrent(output, baseArchive, rom))
        {
            var cached = Smb3Campaign.Load(output)!;
            Console.WriteLine($"Campaign is current: {output}");
            return strict && !cached.Complete ? 2 : 0;
        }
        string staging = Path.Combine(Path.GetTempPath(), "sonicbros3-campaign-" + Guid.NewGuid().ToString("N"));
        string publication = output + ".staged-" + Guid.NewGuid().ToString("N") + ".bin";
        Directory.CreateDirectory(staging);
        try
        {
            ZipFile.ExtractToDirectory(baseArchive, staging);
            var result = Smb3Campaign.Build(rom, staging, Smb3Campaign.Hash(baseArchive), Console.WriteLine);
            int loaded = 0;
            var areas = result.Areas.ToArray();
            foreach (var area in result.Areas.Where(a => a.Status is not ("blocked" or "failed")))
            {
                try
                {
                    var session = new LevelSession(staging, Smb3Campaign.Zone, area.Act);
                    for (int frame = 0; frame < 180; frame++) session.Step(0);
                    session.Render();
                    if (session.StopMessage is { } stopped) throw new InvalidDataException(stopped);
                    loaded++;
                }
                catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException or IOException)
                {
                    Console.WriteLine($"Native load failed for {area.Source.Id}: {error.Message}");
                    areas[area.Act - 1] = area with { Status = "blocked",
                        Blockers = [.. area.Blockers, $"Native load/render: {error.Message}"] };
                }
            }
            result = result with { Areas = areas };
            Smb3Campaign.Save(staging, result);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            DataFile.Pack(staging, publication);
            DataFile.Forget(output);
            File.Move(publication, output, overwrite: true);
            Console.WriteLine($"Campaign: {result.Worlds.Length} worlds, {result.Worlds.Sum(w => w.Map.Length / 144)} map screens, " +
                $"{result.Areas.Length} source areas, {loaded} native load/render checks passed, " +
                $"{result.Areas.Count(a => a.Status is "blocked" or "failed")} unavailable.");
            Console.WriteLine($"Output: {output}");
            Console.WriteLine("NOT a complete campaign. See campaign/campaign.json and campaign/backlog.json inside the archive.");
            return strict && !result.Complete ? 2 : 0;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            if (File.Exists(publication)) File.Delete(publication);
            if (File.Exists(publication + ".new")) File.Delete(publication + ".new");
        }
    }
    SonicBrosData.EnsureBuilt(options.GetValueOrDefault("--rom", Path.Combine(root, "rom.bin")),
        options.GetValueOrDefault("--smb3", Path.Combine(root, "smb3.nes")), output, Console.WriteLine, force);
    string reportPath = Path.Combine(output, Smb3Conversion.ReportPath);
    var report = JsonSerializer.Deserialize<Smb3ConversionReport>(DataFile.ReadAllText(reportPath), Manifest.JsonOptions)
        ?? throw new InvalidDataException("Conversion report is empty.");
    Console.WriteLine($"Coverage report: {reportPath}");
    if (!report.Complete) Console.WriteLine("Campaign is not fully playable: review area blockers and logic/backlog.json.");
    return strict && !report.Complete ? 2 : 0;
}
catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or JsonException)
{
    Console.Error.WriteLine($"Conversion failed: {ex.Message}");
    return 1;
}
