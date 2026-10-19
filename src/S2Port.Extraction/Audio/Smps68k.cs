namespace S2Port.Extraction.Audio;

/// <summary>
/// Sonic 1-style 68k SMPS driver used by the hack's zones (update routine 0x1301DC, selected when $FFFFE5 = 1).
/// All pointers inside songs and SFX are relative, so each sound is exported as a self-contained binary.
/// </summary>
public static class Smps68k
{
    /// <summary>Pointer block before the driver code (0x12FFEC-0x130007).</summary>
    public const int PriorityTablePtr = 0x12FFEC, SpecialSfxIndexPtr = 0x12FFF0, MusicIndexPtr = 0x12FFF4,
        SfxIndexPtr = 0x12FFF8, SpeedUpTablePtr = 0x130000, PsgEnvelopeIndexPtr = 0x130004;

    /// <summary>The driver's data block (pointer block to the priority table) and the sound data area.</summary>
    public const int DataBlockStart = 0x12FFEC, DataBlockEnd = 0x1301DC, SoundDataStart = 0x132CD6, SoundDataEnd = 0x137E80;
    /// <summary>Constant tables inside the driver code.</summary>
    public const int FmFrequencyTable = 0x130E8C, PsgFrequencyTable = 0x1310CA, CarrierMaskTable = 0x1313A8, VoiceRegisterTable = 0x131414;

    public const int FirstMusic = 0x81, MusicCount = 0x1F;   // $81-$9F
    public const int FirstSfx = 0xA0, SfxCount = 0x30;       // $A0-$CF
    public const int SpecialSfx = 0xD0;
    public const int VoiceSize = 25;

    /// <summary>
    /// Parameter bytes per coordination flag, from the handler table at 0x131160 ($E0-$F9; nothing above $F9).
    /// </summary>
    public static readonly IReadOnlyDictionary<byte, int> FlagParams = new Dictionary<byte, int>
    {
        [0xE0] = 1, [0xE1] = 1, [0xE2] = 1, [0xE3] = 0, [0xE4] = 0, [0xE5] = 1, [0xE6] = 1, [0xE7] = 0,
        [0xE8] = 1, [0xE9] = 1, [0xEA] = 1, [0xEB] = 1, [0xEC] = 1, [0xED] = 0, [0xEE] = 0, [0xEF] = 1,
        [0xF0] = 4, [0xF1] = 0, [0xF2] = 0, [0xF3] = 1, [0xF4] = 0, [0xF5] = 1, [0xF6] = 2, [0xF7] = 4,
        [0xF8] = 2, [0xF9] = 0,
    };

    /// <summary>Flags that end a track: $F2 stop, $E4 restore previous song, $EE stop special, $E3 return.</summary>
    private static readonly HashSet<byte> Terminators = [0xF2, 0xE4, 0xEE, 0xE3];

    public sealed record TrackWalk(int MaxAddress, IReadOnlySet<int> VoicesUsed, IReadOnlySet<int> Notes, IReadOnlyList<string> Problems);

    /// <summary>
    /// Follows a track through jumps ($F6), loops ($F7) and calls ($F8). Branch target = operand address + 1 + offset
    /// (handler at 0x131526: reads the word, adds it, subtracts 1).
    /// </summary>
    public static TrackWalk Walk(RomImage rom, int start, int lo, int hi)
    {
        var seen = new HashSet<int>();
        var todo = new Stack<int>([start]);
        var voices = new HashSet<int>();
        var notes = new HashSet<int>();
        var problems = new List<string>();
        int max = start;
        while (todo.Count > 0)
        {
            int a = todo.Pop();
            while (seen.Add(a))
            {
                if (a < lo || a >= hi) { problems.Add($"address 0x{a:X6} outside 0x{lo:X6}-0x{hi:X6}"); break; }
                byte b = rom.U8(a);
                max = Math.Max(max, a);
                if (b < 0xE0)
                {
                    if (b > 0x80) notes.Add(b);
                    a++;
                    continue;
                }
                if (!FlagParams.TryGetValue(b, out int n)) { problems.Add($"unknown flag {b:X2} at 0x{a:X6}"); break; }
                max = Math.Max(max, a + n);
                if (b == 0xEF)
                {
                    int voice = rom.U8(a + 1);
                    if (voice >= 0x80) problems.Add($"voice {voice:X2} >= $80 at 0x{a:X6}");
                    voices.Add(voice & 0x7F);
                }
                if (Terminators.Contains(b)) break;
                short Rel(int operand) => (short)rom.U16(operand);
                switch (b)
                {
                    case 0xF6: a = a + 2 + Rel(a + 1); continue;
                    case 0xF8: todo.Push(a + 2 + Rel(a + 1)); a += 3; continue;
                    case 0xF7: todo.Push(a + 4 + Rel(a + 3)); a += 5; continue;
                    default: a += 1 + n; continue;
                }
            }
        }
        return new TrackWalk(max, voices, notes, problems);
    }

    public sealed record Channel(string Kind, int Offset, int Transpose, int Volume, int? Assignment = null, int? PsgVoice = null);

    public sealed record Sound(int Address, int Length, int VoiceOffset, int VoiceCount, IReadOnlyList<Channel> Channels,
        int? TempoDivider, int? Tempo, IReadOnlyList<string> Problems);

    /// <summary>Music header: u16 voices, u8 FM+DAC channels, u8 PSG channels, u8 tempo divider, u8 tempo, then channels.</summary>
    public static Sound ParseMusic(RomImage rom, int s)
    {
        int voiceOff = rom.U16(s), fm = rom.U8(s + 2), psg = rom.U8(s + 3);
        var channels = new List<Channel>();
        for (int i = 0; i < fm; i++)
        {
            int e = s + 6 + i * 4;
            channels.Add(new Channel(i == 0 ? "dac" : "fm", rom.U16(e), (sbyte)rom.U8(e + 2), (sbyte)rom.U8(e + 3)));
        }
        for (int i = 0; i < psg; i++)
        {
            int e = s + 6 + fm * 4 + i * 6;
            channels.Add(new Channel("psg", rom.U16(e), (sbyte)rom.U8(e + 2), (sbyte)rom.U8(e + 3), PsgVoice: rom.U8(e + 5)));
        }
        return Finish(rom, s, voiceOff, channels, rom.U8(s + 4), rom.U8(s + 5));
    }

    /// <summary>SFX header: u16 voices, u8 tempo divider, u8 channels, then {u8 flags, u8 channel id, u16 ptr, s8 transpose, s8 volume}.</summary>
    public static Sound ParseSfx(RomImage rom, int s)
    {
        int voiceOff = rom.U16(s), count = rom.U8(s + 3);
        var channels = new List<Channel>();
        for (int i = 0; i < count; i++)
        {
            int e = s + 4 + i * 6;
            int id = rom.U8(e + 1);
            string kind = (id & 0x80) != 0 ? "psg" : "fm";
            channels.Add(new Channel(kind, rom.U16(e + 2), (sbyte)rom.U8(e + 4), (sbyte)rom.U8(e + 5), Assignment: id));
        }
        return Finish(rom, s, voiceOff, channels, rom.U8(s + 2), null);
    }

    private static Sound Finish(RomImage rom, int s, int voiceOff, List<Channel> channels, int? divider, int? tempo)
    {
        // Tracks must lie between the header and the voice table (or anywhere after the header if there are no voices).
        int hi = voiceOff > 0 ? s + voiceOff : s + 0x8000;
        var walks = channels.Select(c => Walk(rom, s + c.Offset, s, hi)).ToList();
        var problems = walks.SelectMany(w => w.Problems).ToList();
        var voices = walks.SelectMany(w => w.VoicesUsed).ToList();
        int voiceCount = voiceOff > 0 && voices.Count > 0 ? voices.Max() + 1 : 0;
        int end = voiceOff > 0 && voiceCount > 0
            ? s + voiceOff + voiceCount * VoiceSize
            : walks.Max(w => w.MaxAddress) + 1;
        return new Sound(s, end - s, voiceOff, voiceCount, channels, divider, tempo, problems);
    }

    /// <summary>
    /// S1 FM voice (25 bytes): feedback/algorithm, then 6 groups of 4 operator bytes in register order
    /// DT/MUL, RS/AR, AM/D1R, D2R, D1L/RR, TL (operators in order 1,3,2,4).
    /// </summary>
    public static object DecodeVoice(ReadOnlySpan<byte> v) => new
    {
        feedback = (v[0] >> 3) & 7,
        algorithm = v[0] & 7,
        operatorOrder = "op1, op3, op2, op4",
        dtMul = new[] { v[1], v[2], v[3], v[4] },
        rsAr = new[] { v[5], v[6], v[7], v[8] },
        amD1r = new[] { v[9], v[10], v[11], v[12] },
        d2r = new[] { v[13], v[14], v[15], v[16] },
        d1lRr = new[] { v[17], v[18], v[19], v[20] },
        tl = new[] { v[21], v[22], v[23], v[24] },
    };

    // ---------------------------------------------------------------- DAC (Z80 driver at 0x131576)

    /// <summary>Z80 driver sample table at Z80 0x00D6: 8-byte entries {LE ptr, LE length, pitch, 3 unused}; IDs $81-$83.</summary>
    public const int Z80SampleTable = 0x00D6, Z80DeltaTable = 0x0022, Z80SampleCount = 3;

    /// <summary>68k-side pitch table for DAC notes $88-$8B (0x13035E), written to Z80 $00EA (sample $83's pitch).</summary>
    public const int TimpaniPitchTable = 0x13035E;

    /// <summary>
    /// Playback rate from the Z80 loop (0x0077-0x00B3): 275 + 26*pitch cycles per byte (two nibbles) at 3.579545 MHz.
    /// Computed by counting instruction cycles; ignores bus contention.
    /// </summary>
    public static double SampleRate(int pitch) => 3579545.0 / ((275 + 26.0 * pitch) / 2);

    /// <summary>DPCM: accumulator starts at $80, high nibble first, acc += delta[nibble].</summary>
    public static byte[] DecodeDpcm(ReadOnlySpan<byte> data, ReadOnlySpan<byte> deltas)
    {
        var pcm = new byte[data.Length * 2];
        byte acc = 0x80;
        for (int i = 0; i < data.Length; i++)
        {
            acc += deltas[data[i] >> 4];
            pcm[i * 2] = acc;
            acc += deltas[data[i] & 0xF];
            pcm[i * 2 + 1] = acc;
        }
        return pcm;
    }
}
