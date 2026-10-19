namespace S2Port.Extraction;

/// <summary>
/// Finds the folder holding rom.bin / sonicbros3.bin (or an extracted/ folder) when no path is given. IDEs such as Visual Studio start programs in
/// their build output folder (bin/Debug/net10.0), so besides the current directory every parent of the executable's
/// folder is searched; the first folder with rom.bin, sonicbros3.bin or extracted/manifest.json wins (else the current
/// directory).
/// </summary>
public static class DataPaths
{
    public static string BaseFolder()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "rom.bin")) || File.Exists(Path.Combine(dir.FullName, "sonicbros3.bin")) ||
                    File.Exists(Path.Combine(dir.FullName, "extracted", "manifest.json")))
                    return dir.FullName;
        return Directory.GetCurrentDirectory();
    }
}
