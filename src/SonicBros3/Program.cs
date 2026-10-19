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
//            [--screenshot out.png --frames N [--inputs script.txt] [--wav out.wav]]   (headless)
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

try
{
    SonicBrosData.EnsureBuilt(romPath, nesPath, dataDir, Console.WriteLine);
}
catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine(ex is FileNotFoundException f ? $"{f.Message} ({f.FileName})" : ex.Message);
    return 1;
}

bool headless = opts.ContainsKey("screenshot");
var mixer = args.Contains("--mute") || (headless && !opts.ContainsKey("wav")) ? null : new SonicBros3.Audio.SoundMixer(dataDir);
var start = opts.GetValueOrDefault("start", "title") switch
{
    "map" => GameFlow.Start.Map,
    "level" => GameFlow.Start.Level,
    "match" => GameFlow.Start.Match,
    _ => GameFlow.Start.Title,
};
(int, int)? debugStart = opts.TryGetValue("debug-start", out var ds) ? (int.Parse(ds.Split(',')[0]), int.Parse(ds.Split(',')[1])) : null;
var startLevel = Smb3Levels.All.Single(l => l.Name == opts.GetValueOrDefault("level", "1-1"));
int startZone = startLevel.Zone;
if (start == GameFlow.Start.Level && startLevel.Limitation is { } limitation)
    Console.WriteLine($"World {startLevel.Name}: {limitation}");
var flow = new GameFlow(dataDir, mixer, start, debugStart, startZone);

if (opts.TryGetValue("screenshot", out var shot))
{
    int frames = int.Parse(opts.GetValueOrDefault("frames", "1"));
    var script = opts.TryGetValue("inputs", out var inputsPath) ? InputScript.Load(inputsPath) : null;
    var wav = new List<short>();
    var buffer = new short[800 * 2];
    for (int i = 1; i <= frames; i++)
    {
        flow.Step(script?.ButtonsAt(i) ?? 0);
        if (args.Contains("--trace") && flow.Session is { } ts && ts.Phase == LevelSession.StartPhase.Playing)
        {
            var so = ts.World.Sonic.O;
            Console.WriteLine($"f{i}: x={so.X} y={so.Y} angle=${so.Angle:X2} inertia={so.Inertia} xvel={so.XVel} air={so.InAir} status=${so.Status:X2}");
        }
        if (mixer is not null) { mixer.Render(buffer, 800); wav.AddRange(buffer); }
    }
    if (mixer is not null && opts.TryGetValue("wav", out var wavPath)) WriteWav16Stereo(wavPath, wav, mixer.OutputRate);
    flow.Render();
    var vdp = flow.Vdp;
    var pixels = (byte[])vdp.Frame.Clone();
    if (vdp.SplitLine >= 0)
        for (int i = vdp.SplitLine * Vdp.Width; i < pixels.Length; i++) pixels[i] += 64;
    IndexedPng.Write(shot, Vdp.Width, Vdp.Height, pixels, vdp.Cram.Concat(vdp.CramBelow).Select(c => new MdColor(c).ToRgb()).ToList());
    Console.WriteLine($"Wrote {shot} ({flow.Describe()})");
    return 0;
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
