using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace S2Port.Extraction;

/// <summary>Records what was extracted and where in the ROM each item came from.</summary>
public sealed class Manifest
{
    public int ExtractorVersion { get; set; }
    public string RomSha1 { get; set; } = "";
    public int RomLength { get; set; }
    public DateTimeOffset ExtractedAt { get; set; }
    public List<ManifestItem> Items { get; set; } = [];

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Manifest? TryLoad(string path)
    {
        if (!S2Port.Extraction.DataFile.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<Manifest>(S2Port.Extraction.DataFile.ReadAllText(path), JsonOptions); }
        catch (JsonException) { return null; }
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
}

public sealed record ManifestItem(string Path, string Kind, List<SourceRef> Sources, string? Notes = null);

/// <param name="Address">ROM address as hex string, e.g. "0x09D258".</param>
/// <param name="Format">raw | kosinski | nemesis | enigma | saxman</param>
public sealed record SourceRef(string Address, string Format, int? CompressedSize = null, int? Size = null)
{
    public static SourceRef At(int addr, string format, int? compressedSize = null, int? size = null) =>
        new($"0x{addr:X6}", format, compressedSize, size);
}
