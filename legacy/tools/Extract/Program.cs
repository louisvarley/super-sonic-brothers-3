using S2Port.Extraction;

// Low-level engine extraction only. Use tools/ConvertSmb3 for complete Sonic Bros. 3 data.
var positional = args.Where(a => !a.StartsWith("--")).ToArray();
bool force = args.Contains("--force");
bool previews = args.Contains("--previews");
string romPath = positional.ElementAtOrDefault(0) ?? "rom.bin";
string outDir = positional.ElementAtOrDefault(1) ?? "engine-data.bin";

if (!File.Exists(romPath))
{
    Console.Error.WriteLine($"ROM not found: {romPath}");
    return 1;
}

var rom = RomImage.Load(romPath);
Console.WriteLine($"ROM      {romPath} ({rom.Length} bytes)");
Console.WriteLine($"SHA-1    {rom.Sha1}{(rom.Sha1 == RomMap.SupportedSha1 ? " (supported)" : " (UNSUPPORTED)")}");
Console.WriteLine($"Checksum header 0x{rom.HeaderChecksum:X4}, computed 0x{rom.ComputeChecksum():X4}");

try
{
    Extractor.Extract(rom, outDir, Console.WriteLine, allowUnknownRom: force, previews: previews);
    return 0;
}
catch (InvalidDataException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
