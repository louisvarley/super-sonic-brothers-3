using System.Security.Cryptography;
using System.Text.Json;
using S2Port.Extraction;
using S2Port.Extraction.Graphics;
using S2Port.Extraction.Smb3;
using SonicBros3;
using SonicBros3.Frontend;
using SonicBros3.Runtime;
using SonicBros3.Smb3;

// SonicBros3 [--rom rom.bin] [--smb3 smb3.nes] [--data sonicbros3.bin] [--mute]
//            [--start title|map|level|match] [--level 1-1|1-2|1-3]   (title by default; level = straight into that
//                                            level; match = the spade panel's card game)
//            [--zone 0x11 --act 1] [--campaign-act N] [--world 1] [--map-screen 0]  (trace any legacy level)
//            [--screenshot out.png --frames N [--inputs script.txt] [--wav out.wav]]   (headless)
//            [--golden out.sb3g --frames N [--inputs script.txt] [--hash-every K] [--snapshot-every N]]
//            [--debug-start x,y]             (testing: where Sonic starts the next level) [--trace]
// The game needs both of the player's own images: the Sonic 2 hack (rom.bin) for the engine's data and Super Mario
// Bros. 3 (smb3.nes) for the title, the World 1 map and 1-1. Both are found beside the executable or above it.
// Input scripts use the tools/RefEmu format ("<frame> <buttons>", buttons from U D L R A B C S or -).
var opts = ParseArgs(args);
if (opts.TryGetValue("level", out var requestedLevel) && !Smb3Levels.All.Any(l => l.Name == requestedLevel))
{
    Console.Error.WriteLine($"Level '{requestedLevel}' is not engine-ready. Available levels: {string.Join(", ", Smb3Levels.All.Select(l => l.Name))}. See conversion/report.json for import coverage.");
    return 1;
}
if (opts.TryGetValue("start", out var requestedStart) && requestedStart is not ("title" or "map" or "level" or "match"))
{
    Console.Error.WriteLine($"Unknown start screen '{requestedStart}'. Use title, map, level or match.");
    return 1;
}
string baseDir = DataPaths.BaseFolder();
string dataDir = opts.GetValueOrDefault("data", Path.Combine(baseDir, "sonicbros3.bin"));
string romPath = opts.GetValueOrDefault("rom", Path.Combine(baseDir, "rom.bin"));
string nesPath = opts.GetValueOrDefault("smb3", Path.Combine(baseDir, "smb3.nes"));

// Read-only listing for the golden-trace generator; must not trigger the archive build/rebuild.
if (opts.ContainsKey("list-campaign"))
{
    var campaign = S2Port.Extraction.Smb3.Smb3Campaign.Load(dataDir)
        ?? throw new InvalidDataException("No campaign manifest in the archive.");
    var rows = campaign.Areas.Select(a => new
    {
        act = a.Act,
        status = a.Status,
        source = a.Source.Id,
        world = campaign.Worlds.FirstOrDefault(w => w.Entries.Any(e => e.AreaId == a.Source.Id))?.Number ?? 1,
        width = a.Width,
        height = a.Height,
    }).ToArray();
    Console.WriteLine(JsonSerializer.Serialize(rows));
    return 0;
}

try
{
    if (opts.ContainsKey("no-build"))
    {
        if (!File.Exists(dataDir)) throw new FileNotFoundException("--no-build needs an existing asset archive.", dataDir);
    }
    else
    {
        SonicBrosData.EnsureBuilt(romPath, nesPath, dataDir, Console.WriteLine);
    }
}
catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine(ex is FileNotFoundException f ? $"{f.Message} ({f.FileName})" : ex.Message);
    return 1;
}

bool headless = opts.ContainsKey("screenshot") || opts.ContainsKey("golden");

// Golden traces run without a sound driver (as the existing frame-hash tests do) so the recorded RAM is deterministic
// and independent of floating-point chip models. Audio is covered by separate tests.
var mixer = args.Contains("--mute") || (headless && !opts.ContainsKey("wav")) ? null : new SonicBros3.Audio.SoundMixer(dataDir);
var start = opts.GetValueOrDefault("start", "title") switch
{
    "map" => GameFlow.Start.Map,
    "level" => GameFlow.Start.Level,
    "match" => GameFlow.Start.Match,
    _ => GameFlow.Start.Title,
};
(int, int)? debugStart = opts.TryGetValue("debug-start", out var ds) ? (int.Parse(ds.Split(',')[0]), int.Parse(ds.Split(',')[1])) : null;
int startZone = Smb3Levels.All.Single(l => l.Name == "1-1").Zone;
int startAct = 1, startWorld = 1, startMapScreen = 0;
if (opts.TryGetValue("level", out var levelName))
{
    var startLevel = Smb3Levels.All.Single(l => l.Name == levelName);
    startZone = startLevel.Zone;
    if (start == GameFlow.Start.Level && startLevel.Limitation is { } limitation)
        Console.WriteLine($"World {startLevel.Name}: {limitation}");
}
if (opts.TryGetValue("zone", out var zoneText)) startZone = Convert.ToInt32(zoneText, 16);
if (opts.TryGetValue("act", out var actText)) startAct = int.Parse(actText);
if (opts.TryGetValue("campaign-act", out var campaignText))
{
    startZone = S2Port.Extraction.Smb3.Smb3Campaign.Zone;
    startAct = int.Parse(campaignText);
}
if (opts.TryGetValue("world", out var worldText)) startWorld = int.Parse(worldText);
if (opts.TryGetValue("map-screen", out var screenText)) startMapScreen = int.Parse(screenText);
// The SMB3 GameFlow only knows its map panels. For any other zone (Sonic zones, campaign acts) step the level
// session directly; this is the same World/LevelSession the flow uses.
bool rawStart = start == GameFlow.Start.Level &&
    (opts.ContainsKey("zone") || opts.ContainsKey("campaign-act") || opts.ContainsKey("raw-session"));
GameFlow? flow = null;
LevelSession? rawSession = null;
if (rawStart)
    rawSession = new LevelSession(dataDir, startZone, startAct, debugStart, mixer, titleCards: true)
        { CampaignWorld = startWorld };
else
    flow = new GameFlow(dataDir, mixer, start, debugStart, startZone, startAct, startWorld, startMapScreen);

if (opts.TryGetValue("golden", out var goldenPath))
{
    int frames = int.Parse(opts.GetValueOrDefault("frames", "1"));
    int hashEvery = int.Parse(opts.GetValueOrDefault("hash-every", "30"));
    int snapshotEvery = int.Parse(opts.GetValueOrDefault("snapshot-every", "60"));
    var script = opts.TryGetValue("inputs", out var inputsPath) ? InputScript.Load(inputsPath) : null;
    string archivePath = Path.GetFullPath(dataDir);
    string archiveSha = File.Exists(archivePath)
        ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath))) : "";
    var header = new GoldenHeader(
        Format: "sb3.golden/1",
        Engine: "legacy-csharp",
        Scenario: opts.GetValueOrDefault("scenario", Path.GetFileNameWithoutExtension(goldenPath)),
        Archive: Path.GetRelativePath(baseDir, archivePath).Replace('\\', '/'),
        ArchiveSha256: archiveSha,
        Start: start.ToString(),
        Zone: startZone,
        Act: startAct,
        World: startWorld,
        MapScreen: startMapScreen,
        Level: opts.GetValueOrDefault("level", ""),
        DebugStart: opts.GetValueOrDefault("debug-start", ""),
        Frames: frames,
        HashEvery: hashEvery,
        SnapshotEvery: snapshotEvery,
        Regions: GoldenTrace.Regions.Select(r => r.Name).ToArray(),
        Fields: GoldenTrace.Fields);
    using var trace = rawSession is not null
        ? new GoldenTrace(goldenPath, rawSession, header, hashEvery, snapshotEvery)
        : new GoldenTrace(goldenPath, flow!, header, hashEvery, snapshotEvery);
    for (int i = 1; i <= frames; i++) trace.Step(script?.ButtonsAt(i) ?? 0);
    trace.Finish();
    string describe = rawSession is not null
        ? $"level, phase {rawSession.Phase}, Sonic {rawSession.World.Sonic.O.X},{rawSession.World.Sonic.O.Y}"
        : flow!.Describe();
    Console.WriteLine($"Wrote {goldenPath} ({frames} ticks, {describe})");
    return 0;
}

if (opts.TryGetValue("screenshot", out var shot))
{
    int frames = int.Parse(opts.GetValueOrDefault("frames", "1"));
    var script = opts.TryGetValue("inputs", out var inputsPath) ? InputScript.Load(inputsPath) : null;
    var wav = new List<short>();
    var buffer = new short[800 * 2];
    for (int i = 1; i <= frames; i++)
    {
        if (rawSession is not null) rawSession.Step(script?.ButtonsAt(i) ?? 0);
        else flow!.Step(script?.ButtonsAt(i) ?? 0);
        var session = rawSession ?? flow?.Session;
        if (args.Contains("--trace") && session is { } ts && ts.Phase == LevelSession.StartPhase.Playing)
        {
            var so = ts.World.Sonic.O;
            Console.WriteLine($"f{i}: x={so.X} y={so.Y} angle=${so.Angle:X2} inertia={so.Inertia} xvel={so.XVel} air={so.InAir} status=${so.Status:X2}");
        }
        if (mixer is not null) { mixer.Render(buffer, 800); wav.AddRange(buffer); }
    }
    if (mixer is not null && opts.TryGetValue("wav", out var wavPath)) WriteWav16Stereo(wavPath, wav, mixer.OutputRate);
    Vdp vdp;
    if (rawSession is not null) { rawSession.Render(); vdp = rawSession.Vdp; }
    else { flow!.Render(); vdp = flow.Vdp; }
    var pixels = (byte[])vdp.Frame.Clone();
    if (vdp.SplitLine >= 0)
        for (int i = vdp.SplitLine * Vdp.Width; i < pixels.Length; i++) pixels[i] += 64;
    IndexedPng.Write(shot, Vdp.Width, Vdp.Height, pixels, vdp.Cram.Concat(vdp.CramBelow).Select(c => new MdColor(c).ToRgb()).ToList());
    string shotDescribe = rawSession is not null
        ? $"level, phase {rawSession.Phase}, Sonic {rawSession.World.Sonic.O.X},{rawSession.World.Sonic.O.Y}"
        : flow!.Describe();
    Console.WriteLine($"Wrote {shot} ({shotDescribe})");
    return 0;
}

if (flow is null)
{
    Console.Error.WriteLine("A raw level (--zone/--campaign-act) can only be run with --golden or --screenshot.");
    return 1;
}
using var game = new S2Game(flow, mixer);
game.Run();
return 0;

static Dictionary<string, string> ParseArgs(string[] args)
{
    var d = new Dictionary<string, string>();
    for (int i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--")) continue;
        bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
        d[args[i][2..]] = hasValue ? args[++i] : "";
    }
    return d;
}

static void WriteWav16Stereo(string path, List<short> samples, int rate)
{
    using var w = new BinaryWriter(File.Create(path));
    w.Write("RIFF"u8);
    w.Write(36 + samples.Count * 2);
    w.Write("WAVEfmt "u8);
    w.Write(16);
    w.Write((short)1);
    w.Write((short)2);
    w.Write(rate);
    w.Write(rate * 4);
    w.Write((short)4);
    w.Write((short)16);
    w.Write("data"u8);
    w.Write(samples.Count * 2);
    foreach (var v in samples) w.Write(v);
}
