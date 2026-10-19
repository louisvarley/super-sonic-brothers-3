namespace S2Port.Extraction.Smb3;

/// <summary>Renders the source sound engine's two pulse channels for pipe and map-entry effects.</summary>
public static class Smb3Effects
{
    public const int SampleRate = 48000;
    public static void Build(NesRom rom, string directory)
    {
        string output = Path.Combine(directory, "campaign", "effects");
        Directory.CreateDirectory(output);
        foreach (var (name, queue, value) in new[] { ("enter-level", 0x4F6, 4), ("pipe", 0x4F1, 0x10) })
        {
            var cpu = new Cpu6502(rom);
            cpu.Banks[1] = 28;
            cpu.Banks[2] = 29;
            var registers = new byte[24];
            double[] phase = [0, 0];
            int[] envelope = [15, 15], divider = [0, 0], length = [0, 0];
            int[] lengths = [10, 254, 20, 2, 40, 4, 80, 6, 160, 8, 60, 10, 14, 12, 26, 14,
                12, 16, 24, 18, 48, 20, 96, 22, 192, 24, 72, 26, 16, 28, 32, 30];
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
            for (int frame = 0; frame < 120; frame++)
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
            if (last < 0) throw new InvalidDataException($"Source {name} effect rendered silence.");
            var bytes = new byte[(last + 1) * 2];
            for (int i = 0; i <= last; i++)
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), samples[i]);
            File.WriteAllBytes(Path.Combine(output, name + ".pcm"), bytes);
        }
    }
}
