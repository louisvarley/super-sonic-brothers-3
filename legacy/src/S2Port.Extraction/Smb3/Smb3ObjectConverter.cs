using S2Port.Extraction.Levels;

namespace S2Port.Extraction.Smb3;

/// <summary>Shared native behaviour mappings. Unmapped IDs must remain visible in conversion reports.</summary>
public static class Smb3ObjectConverter
{
    public sealed record SourceObject(int Id, int Column, int Row);
    public sealed record Rule(int TargetId, int Subtype = 0, int OffsetY = 0);

    public static IReadOnlyDictionary<int, Rule> Rules { get; } = new Dictionary<int, Rule>
    {
        [0x72] = new(0xE1), [0x73] = new(0xE2), [0x74] = new(0xE2, 1),
        [0xC1] = new(0xE8), [0x6C] = new(0xE3, OffsetY: -8), [0x6D] = new(0xE3, 1, -8),
        [0x6E] = new(0xE4, OffsetY: -8), [0x82] = new(0xEA),
        [0xA0] = new(0xE5, Smb3Plant.Green), [0xA1] = new(0xE5, Smb3Plant.Green | Smb3Plant.Ceiling),
        [0xA2] = new(0xE5, Smb3Plant.Red), [0xA3] = new(0xE5, Smb3Plant.Red | Smb3Plant.Ceiling),
        [0xA4] = new(0xE5, Smb3Plant.GreenFire), [0xA5] = new(0xE5, Smb3Plant.GreenFire | Smb3Plant.Ceiling),
        [0xA6] = new(0xE5, Smb3Plant.RedFire), [0xA7] = new(0xE5, Smb3Plant.RedFire | Smb3Plant.Ceiling),
        [0x41] = new(0x0D, OffsetY: 8),
    };

    public static ObjectPlacement? Convert(SourceObject source)
    {
        if (!Rules.TryGetValue(source.Id, out var rule)) return null;
        return new(source.Column * 16 + 8, source.Row * 16 + 8 + rule.OffsetY,
            rule.TargetId, rule.Subtype, false, false, false);
    }

    public static SourceObject[] Read(NesRom rom, int bank, int address)
    {
        if (address is < 0xA000 or >= 0xE000)
            throw new InvalidDataException($"Invalid object pointer ${address:X4}.");
        var objects = new List<SourceObject>();
        int end = (address & 0xE000) + 0x2000;
        for (int p = address + 1; p < end; p += 3)
        {
            int id = rom.Prg(bank, p);
            if (id == 0xFF) return objects.ToArray();
            if (p + 2 >= end) break;
            objects.Add(new(id, rom.Prg(bank, p + 1), rom.Prg(bank, p + 2)));
        }
        throw new InvalidDataException($"Unterminated object list in bank {bank} at ${address:X4}.");
    }
}

/// <summary>Native subtypes preserve both colour/fire identity and pipe orientation.</summary>
public static class Smb3Plant
{
    public const int Green = 0, GreenFire = 1, RedFire = 2, Red = 3, Ceiling = 4;
    public static bool Shoots(int subtype) => (subtype & 3) is GreenFire or RedFire;
    public static bool IsRed(int subtype) => (subtype & 3) is Red or RedFire;
}
