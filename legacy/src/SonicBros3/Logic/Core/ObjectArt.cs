namespace SonicBros3.Logic;

/// <summary>
/// All loaded object sprite data, by name, and by the ROM pointer that object code stores at slot offset $04
/// (a mapping table, or for "static" mappings, a single frame's first piece).
/// </summary>
public sealed class ObjectArt
{
    private readonly string _dataDir;
    private readonly Dictionary<string, SpriteData> _byName = [];
    private readonly Dictionary<uint, SpriteData> _tables = [];
    private readonly Dictionary<uint, MappingPiece> _static = [];

    public ObjectArt(string dataDir) => _dataDir = dataDir;

    public string DataDir => _dataDir;

    public SpriteData this[string name] => _byName.TryGetValue(name, out var d) ? d : Load(name);

    /// <summary>Loads every objects/*/sprites.json that object code addresses by ROM pointer.</summary>
    public void LoadAll()
    {
        var root = Path.Combine(_dataDir, "objects");
        if (!S2Port.Extraction.DataFile.DirectoryExists(root)) return;
        foreach (var dir in S2Port.Extraction.DataFile.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(dir);
            if (_byName.ContainsKey(name) || !S2Port.Extraction.DataFile.Exists(Path.Combine(dir, "sprites.json"))) continue;
            var data = SpriteData.Load(_dataDir, name);
            if (data.RomPointer != 0) Add(name, data);
        }
    }

    public SpriteData Load(string name)
    {
        var data = SpriteData.Load(_dataDir, name);
        Add(name, data);
        return data;
    }

    public void Add(string name, SpriteData data, uint? pointer = null)
    {
        _byName[name] = data;
        if (pointer is { } p) _tables[p] = data;
        if (data.RomPointer == 0) return;
        foreach (var (ptr, isStatic, frame, piece) in data.Pointers())
        {
            if (!isStatic) _tables[ptr] = data;
            else _static.TryAdd(ptr, data.Frames[frame][piece]);
        }
    }

    private readonly Dictionary<string, (int Base, byte[] Data)> _tables2 = [];

    /// <summary>A raw data table extracted from ROM (objects/&lt;file&gt;), addressed by its ROM address.</summary>
    public (int Base, byte[] Data) DataTable(string file)
    {
        lock (_tables2)
        {
            if (_tables2.TryGetValue(file, out var t)) return t;
            using var doc = System.Text.Json.JsonDocument.Parse(S2Port.Extraction.DataFile.ReadAllText(Path.Combine(_dataDir, "objects", file)));
            var r = doc.RootElement;
            t = (r.GetProperty("baseAddress").GetInt32(), r.GetProperty("data").EnumerateArray().Select(v => (byte)v.GetInt32()).ToArray());
            return _tables2[file] = t;
        }
    }

    // The 68000 has a 24-bit address bus: a pointer's top byte is ignored (some tables keep data there).
    public bool TryTable(uint pointer, out SpriteData data) => _tables.TryGetValue(pointer & 0xFFFFFF, out data!);
    public bool TryStatic(uint pointer, out MappingPiece piece) => _static.TryGetValue(pointer & 0xFFFFFF, out piece);
}
