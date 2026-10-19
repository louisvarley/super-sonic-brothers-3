using S2Port.Extraction.Smb3;

namespace S2Port.Validate;

/// <summary>
/// smb3-level &lt;smb3.nes&gt; &lt;cpu.bin&gt; [pool hex]: the ported SMB3 level loader (World 1-1) vs tile memory ($6000-$794F) in a
/// tools/RefEmu/nes.sh dump taken inside 1-1 (cpu_N.bin: $0000-$07FF then $6000-$7FFF). Cells the random cloud
/// generator wrote (in MAME's run or ours) are reported separately: they depend on the random pool at load time.
/// </summary>
public static class Smb3LevelCheck
{
    public static int Run(string[] args)
    {
        var rom = NesRom.Load(args[0]);
        var dump = File.ReadAllBytes(args[1]);
        var mame = dump.AsSpan(0x800, Smb3Map.TileMemSize);
        var decoder = new Smb3LevelDecoder(rom, Smb3Map.Level11Layout);
        // The random pool shapes only the clouds: the one in the dump, or (third argument, hex) the pool at load time.
        (args.Length > 2 ? Convert.FromHexString(args[2]) : dump.AsSpan(0x781, 10).ToArray()).CopyTo(decoder.RandomPool, 0);
        decoder.Load();
        var ours = decoder.TileMem;
        int screens = decoder.Width + 1, cells = screens * Smb3Map.ScreenSize, bad = 0, random = 0;
        byte[] pupClouds = [rom.Prg(Smb3Map.GenBank, Smb3Map.LL_PUPClouds), rom.Prg(Smb3Map.GenBank, Smb3Map.LL_PUPClouds + 1),
            rom.Prg(Smb3Map.GenBank, Smb3Map.LL_PUPClouds + 2)];
        for (int i = 0; i < cells; i++)
        {
            if (ours[i] == mame[i]) continue;
            if (decoder.RandomCells.Contains(i) || pupClouds.Contains(mame[i])) { random++; continue; }
            if (bad++ < 30)
                Console.WriteLine($"screen {i / Smb3Map.ScreenSize} row {i % Smb3Map.ScreenSize / 16} col {i % 16}: mame {mame[i]:X2} ours {ours[i]:X2}");
        }
        Console.WriteLine($"1-1: {screens} screens, {cells - bad - random}/{cells} cells match; {random} random-cloud cells differ, {bad} others");
        return bad == 0 ? 0 : 2;
    }
}

/// <summary>
/// smb3-music &lt;smb3.nes&gt; &lt;apu.csv&gt; [first frame] [frames]: the ported SMB3 sequencer (World 1-1, song $10) vs MAME's sound register
/// log (tools/RefEmu/nes.sh with APULOG=1). The song start is the first frame writing $0B to $4015. Square, triangle and
/// noise registers are compared write for write per frame; DMC sample starts by frame.
/// </summary>
public static class Smb3MusicCheck
{
    public static int Run(string[] args)
    {
        var rom = NesRom.Load(args[0]);
        var log = File.ReadLines(args[1]).Skip(1).Select(l => l.Split(','))
            .Select(p => (Frame: int.Parse(p[0]), Reg: Convert.ToInt32(p[1], 16), Value: Convert.ToInt32(p[2], 16))).ToList();
        // The song start: the first segment load ($0B to $4015) at or after the given MAME frame.
        int from = args.Length > 2 ? int.Parse(args[2]) : 0;
        int start = log.First(w => w.Frame >= from && w.Reg == 0x4015 && w.Value == 0x0B).Frame;
        int frames = args.Length > 3 ? int.Parse(args[3]) : log.Max(w => w.Frame) - start;
        var byFrame = log.Where(w => w.Frame >= start).GroupBy(w => w.Frame - start).ToDictionary(g => g.Key, g => g.ToList());
        var music = new Smb3Music(rom);
        music.Start(0x10);
        bool Tone(int r) => r is >= 0x4000 and <= 0x400F;
        int bad = 0, segments = 0;
        for (int f = 0; f < frames; f++)
        {
            music.Frame();
            if (music.SegmentStarted >= 0) segments++;
            var ours = music.Writes.Where(w => Tone(w.Reg)).Select(w => $"{w.Reg:X4}={w.Value:X2}").ToList();
            var theirs = byFrame.TryGetValue(f, out var l) ? l : [];
            var mame = theirs.Where(w => Tone(w.Reg)).Select(w => $"{w.Reg:X4}={w.Value:X2}").ToList();
            bool dmcOurs = music.Writes.Any(w => w.Reg == 0x4100), dmcMame = theirs.Any(w => w.Reg == 0x4012);   // the sample address ($4010 is rewritten every frame)
            if (ours.SequenceEqual(mame) && dmcOurs == dmcMame) continue;
            if (bad++ < 8)
                Console.WriteLine($"frame {f}: ours [{string.Join(' ', ours)}]{(dmcOurs ? " DMC" : "")}\n         mame [{string.Join(' ', mame)}]{(dmcMame ? " DMC" : "")}");
        }
        Console.WriteLine($"{frames} frames from MAME frame {start}: {frames - bad} match, {bad} differ; {segments} segments started");
        return bad == 0 ? 0 : 2;
    }
}

/// <summary>
/// smb3-level-6502 &lt;smb3.nes&gt; [1-1|1-2]: the ported level loader vs SMB3's own (LevelLoad_TS1 / LevelLoad_TS3, from
/// $A408 in the layout's style bank, generators in bank 14) run on <see cref="Cpu6502"/>: every cell of tile memory.
/// 1-1 checks the 6502 core (that decoder matches MAME); 1-2 checks the Hills generators, which MAME has not been run
/// on. Both get the same random pool (only 1-1's power-up clouds draw from it).
/// </summary>
public static class Smb3Level6502Check
{
    // smb3.fns (captainsouthbird's disassembly assembled with nesasm): RAM the loader reads.
    private const int Level_LayPtr_AddrL = 0x61, Level_JctCtl = 0x3DE, Level_Tileset = 0x70A, Random_Pool = 0x781,
        LevelLoad_TS1_TS3 = 0xA408;

    public static int Run(string[] args)
    {
        var rom = NesRom.Load(args[0]);
        int bad = 0;
        foreach (var level in args.Length > 1 ? args[1..] : ["1-1", "1-2"])
        {
            var (layout, tileset) = level switch
            {
                "1-1" => (Smb3Map.Level11Layout, 1),
                "1-2" => (Smb3Map.Level12Layout, Smb3Map.Level12Tileset),
                _ => throw new ArgumentException($"unknown level {level}"),
            };
            byte[] pool = Convert.FromHexString("F9E211D5F65DB10A6800");
            var decoder = new Smb3LevelDecoder(rom, layout, tileset);
            pool.CopyTo(decoder.RandomPool, 0);
            decoder.Load();

            var cpu = new Cpu6502(rom);
            cpu.Banks[1] = layout.Bank;   // PAGE_A000_ByTileset: 15 Plains, 16 Hills
            cpu.Banks[2] = 14;            // PAGE_C000_ByTileset
            cpu.Ram[Level_LayPtr_AddrL] = (byte)layout.Addr;
            cpu.Ram[Level_LayPtr_AddrL + 1] = (byte)(layout.Addr >> 8);
            cpu.Ram[Level_JctCtl] = 0;
            cpu.Ram[Level_Tileset] = (byte)tileset;
            pool.CopyTo(cpu.Ram, Random_Pool);
            cpu.Call(LevelLoad_TS1_TS3);

            int screens = decoder.Width + 1, cells = screens * Smb3Map.ScreenSize, diff = 0;
            for (int i = 0; i < cells; i++)
            {
                if (decoder.TileMem[i] == cpu.PrgRam[i]) continue;
                if (diff++ < 20)
                    Console.WriteLine($"{level} screen {i / Smb3Map.ScreenSize} row {i % Smb3Map.ScreenSize / 16} col {i % 16}: " +
                                      $"6502 {cpu.PrgRam[i]:X2} ours {decoder.TileMem[i]:X2}");
            }
            Console.WriteLine($"{level}: {screens} screens, {cells - diff}/{cells} cells match the original loader ({cpu.Steps} 6502 steps)");
            bad += diff;
        }
        return bad == 0 ? 0 : 2;
    }
}
