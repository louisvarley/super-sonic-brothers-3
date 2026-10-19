using S2Port.Extraction;
using S2Port.Extraction.Smb3;

namespace SonicBros3.Smb3;

/// <summary>One build entry point for both the game and the batch CLI.</summary>
public static class SonicBrosData
{
    public static void EnsureBuilt(string romPath, string nesPath, string output, Action<string>? log = null,
        bool force = false)
    {
        string fullOutput = Path.GetFullPath(output);
        if (new[] { romPath, nesPath }.Any(p => string.Equals(Path.GetFullPath(p), fullOutput, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Output must not overwrite either source ROM.");
        var nes = NesRom.Load(nesPath);
        Smb3Catalog.RequireSupported(nes);
        Extractor.EnsureExtracted(romPath, output, log);
        if (!force && Smb3Conversion.IsCurrent(output, nes) && Smb3Assets.IsCurrent(output, nes) &&
            Smb3Levels.All.All(l => Smb3ZoneBuilder.IsCurrent(output, nes, l))) return;
        bool archive = Extractor.IsArchivePath(output);
        string work = archive ? DataFile.Unpack(output) : output;
        string report = Path.Combine(work, Smb3Conversion.ReportPath);
        if (File.Exists(report)) File.Delete(report);
        foreach (var level in Smb3Levels.All)
            if (force || !Smb3ZoneBuilder.IsCurrent(work, nes, level))
                new Smb3ZoneBuilder(nes, work, log, level).Build();
        Smb3Assets.EnsureBuilt(nes, work, log);
        Smb3Conversion.Build(nes, work, log);
        if (archive) DataFile.Pack(work, output);
    }
}
