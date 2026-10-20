// Renders the SMB3 source sound engine's pulse channels for gameplay effects into 48 kHz mono PCM.
//
// This is build tooling, not gameplay: it runs SMB3's own 6502 sound engine offline (the same harness as
// legacy/src/S2Port.Extraction/Smb3/Smb3Effects.cs) with a queue bit set, and writes `web-data/effects/<name>.pcm`.
// The Rust port loads those files (or the copies in campaign.bin) and plays them for Mario events.
//
// Usage: dotnet run --project tools/Smb3Effects -- [--rom smb3.nes] [--out web-data/effects]
using S2Port.Extraction.Smb3;

var cli = Environment.GetCommandLineArgs().Skip(1).ToArray();
string rom = "smb3.nes", outDir = "web-data/effects";
for (int i = 0; i < cli.Length; i++)
{
    if (cli[i] == "--rom" && i + 1 < cli.Length) rom = cli[++i];
    else if (cli[i] == "--out" && i + 1 < cli.Length) outDir = cli[++i];
    else { Console.Error.WriteLine("usage: Smb3Effects [--rom smb3.nes] [--out web-data/effects]"); return 1; }
}
if (!File.Exists(rom)) { Console.Error.WriteLine($"ROM not found: {rom}"); return 1; }

// Queue addresses and bits from the SMB3 disassembly (Sound_QPlayer/QLevel1/QLevel2/QMusic1/QMap).
// Pulse effects use QPlayer/QLevel1; the level-2 queue is mixed into the same output.
var effects = new (string Name, int Queue, int Value, int Frames)[]
{
    ("mario-bump",      0x4F1, 0x02, 60),
    ("mario-kick",      0x4F1, 0x08, 60),
    ("mario-fire",      0x4F1, 0x20, 50),
    ("mario-coin",      0x4F2, 0x01, 40),
    ("mario-powerup",   0x4F2, 0x20, 90),
    ("mario-1up",       0x4F2, 0x40, 120),
    ("mario-crumble",   0x4F3, 0x01, 60),
    ("mario-boomerang", 0x4F3, 0x04, 60),
    ("pipe",            0x4F1, 0x10, 120),
    ("map-move",        0x4F6, 0x02, 40),
    ("enter-level",     0x4F6, 0x04, 120),
    ("map-world",       0x4F6, 0x01, 120),
    ("course-clear",    0x4F4, 0x20, 240),
};

Directory.CreateDirectory(outDir);
var nes = NesRom.Load(rom);
int ok = 0;
foreach (var (name, queue, value, frames) in effects)
{
    try
    {
        byte[] pcm = Render(nes, queue, value, frames);
        File.WriteAllBytes(Path.Combine(outDir, name + ".pcm"), pcm);
        Console.WriteLine($"{name,-16} {pcm.Length / 2,6} samples  {pcm.Length / 96.0,6:F2}s  {pcm.Length / 2,6}");
        ok++;
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"{name}: {e.Message}");
    }
}
Console.WriteLine($"{ok}/{effects.Length} effects -> {Path.GetFullPath(outDir)}");
return ok == effects.Length ? 0 : 2;

static byte[] Render(NesRom rom, int queue, int value, int frames)
{
    const int SampleRate = 48000;
    var cpu = new Cpu6502(rom);
    cpu.Banks[1] = 28;
    cpu.Banks[2] = 29;
    var registers = new byte[24];
    double[] phase = [0, 0];
    int[] envelope = [15, 15], divider = [0, 0], length = [0, 0];
    int[] lengths =
    [
        10, 254, 20, 2, 40, 4, 80, 6, 160, 8, 60, 10, 14, 12, 26, 14,
        12, 16, 24, 18, 48, 20, 96, 22, 192, 24, 72, 26, 16, 28, 32, 30,
    ];
    registers[0x15] = 15;
    cpu.ApuWrite = (address, data) =>
    {
        registers[address - 0x4000] = data;
        if (address == 0x4015)
            for (int channel = 0; channel < 2; channel++)
                if ((data & (1 << channel)) == 0) length[channel] = 0;
        if (address is 0x4003 or 0x4007)
        {
            int channel = (address - 0x4003) / 4;
            length[channel] = lengths[data >> 3];
            envelope[channel] = 15;
            divider[channel] = registers[channel * 4] & 15;
        }
    };
    cpu.Ram[queue] = (byte)value;
    var samples = new List<short>();
    for (int frame = 0; frame < frames; frame++)
    {
        cpu.Call(0xA000, maxSteps: cpu.Steps + 100_000);
        for (int sample = 0; sample < 800; sample++)
        {
            double mix = 0;
            for (int channel = 0; channel < 2; channel++)
            {
                int start = channel * 4, control = registers[start];
                int timer = registers[start + 2] | (registers[start + 3] & 7) << 8;
                if (sample % 200 == 0 && (control & 16) == 0)
                {
                    if (divider[channel]-- == 0)
                    {
                        divider[channel] = control & 15;
                        if (envelope[channel] > 0) envelope[channel]--;
                        else if ((control & 32) != 0) envelope[channel] = 15;
                    }
                }
                if (sample == 0 && (frame & 1) == 0 && (control & 32) == 0 && length[channel] > 0)
                    length[channel]--;
                if (timer < 8 || length[channel] == 0 || (registers[0x15] & (1 << channel)) == 0) continue;
                phase[channel] = (phase[channel] + 1789773.0 / (16 * (timer + 1) * SampleRate)) % 1;
                double duty = new[] { .125, .25, .5, .75 }[control >> 6];
                int volume = (control & 16) != 0 ? control & 15 : envelope[channel];
                mix += (phase[channel] < duty ? 1 - duty : -duty) * volume * 650;
            }
            samples.Add((short)Math.Clamp(mix, short.MinValue, short.MaxValue));
        }
    }
    int last = samples.FindLastIndex(s => s != 0);
    if (last < 0) throw new InvalidOperationException("rendered silence");
    var bytes = new byte[(last + 1) * 2];
    for (int i = 0; i <= last; i++)
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), samples[i]);
    return bytes;
}
