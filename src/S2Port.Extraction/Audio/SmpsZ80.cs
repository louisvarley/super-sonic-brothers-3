using S2Port.Extraction.Compression;

namespace S2Port.Extraction.Audio;

/// <summary>
/// The Sonic 2 Z80 SMPS driver, used on the title screen and in zones &lt; $11 ($FFFFE5 = 0).
/// The driver is Saxman-compressed inside the relocated SoundDriverLoad (0x101D26). Unlike the 68k driver, all
/// pointers in songs and SFX are absolute little-endian Z80 addresses, so each export records its base address.
/// </summary>
public static class SmpsZ80
{
    public const int DriverSource = 0x101E0E, DriverCompressedLength = 0xF66;   // 0x101D70: lea / move.w #$F66

    // Z80 addresses inside the decompressed driver (from its code; see docs/audio.md).
    public const int MusicPlaylist = 0x11F5;      // u8 flags per song $81-$9F (zPlayMusic 0x07A1)
    public const int SpeedUpTempos = 0x1214;      // u8 per song (0x0799)
    public const int SfxPriorities = 0x0FD8;      // u8 per SFX from $A0 (0x0694)
    public const int DacSampleTable = 0x1233;     // {LE ptr, LE length} per sample, self-modified loads at 0x0105/0x0109
    public const int DacSampleCount = 7;
    public const int DacPlaylist = 0x124F;        // {sample, pitch} per DAC note from $81 (0x022A)
    public const int DacPlaylistCount = 17;
    public const int DacDeltaTable = 0x01B3;      // iy base for add a,(iy+nibble)

    public const int SongDecompressAddress = 0x1380;
    public const int MusicCount = 0x1F, SfxCount = 0x51;
    public const int SfxTableWindow = 0xEE91;     // in the SFX bank (0x0984)

    /// <summary>68k banks the driver maps at Z80 0x8000 (9-bit writes to $6000: 0x0C6C / 0x0C7C / 0x00CC).</summary>
    public const int MusicBank0 = 0x108000, MusicBank1 = 0x128000, SfxBank = 0x128000, DacBank = 0x100000;
    /// <summary>The SEGA voice's bank (zPlaySegaSound).</summary>
    public const int SegaBank = 0x120000;
    public static readonly int[] Banks = [DacBank, MusicBank0, SegaBank, MusicBank1];

    public static int BankAddress(int bankBase, int windowPtr) => bankBase + windowPtr - 0x8000;

    /// <summary>Parameter bytes per flag, read from the handler table at Z80 0x0C94 (the dispatcher pre-reads one byte).</summary>
    public static readonly IReadOnlyDictionary<byte, int> FlagParams = new Dictionary<byte, int>
    {
        [0xE0] = 1, [0xE1] = 1, [0xE2] = 1, [0xE3] = 0, [0xE4] = 0, [0xE5] = 1, [0xE6] = 1, [0xE7] = 0,
        [0xE8] = 1, [0xE9] = 1, [0xEA] = 1, [0xEB] = 1, [0xEC] = 1, [0xED] = 1, [0xEE] = 0, [0xEF] = 1,
        [0xF0] = 4, [0xF1] = 0, [0xF2] = 0, [0xF3] = 1, [0xF4] = 0, [0xF5] = 1, [0xF6] = 2, [0xF7] = 4,
        [0xF8] = 2, [0xF9] = 0,
    };

    private static readonly HashSet<byte> Terminators = [0xF2, 0xE3, 0xE4];

    public static byte[] DecompressDriver(RomImage rom) =>
        Saxman.Decompress(rom.Span[DriverSource..], DriverCompressedLength);

    /// <summary>A sound's bytes as the driver sees them, starting at Z80 address <see cref="Base"/>.</summary>
    public sealed record Image(byte[] Data, int Base, int RomAddress, int? CompressedSize)
    {
        public byte this[int z80Address] => Data[z80Address - Base];
        public int Le(int z80Address) => this[z80Address] | (this[z80Address + 1] << 8);
    }

    public sealed record TrackWalk(int MaxAddress, IReadOnlySet<int> VoicesUsed, IReadOnlyList<string> Problems);

    public static TrackWalk Walk(Image img, int start, int lo, int hi)
    {
        var seen = new HashSet<int>();
        var todo = new Stack<int>([start]);
        var voices = new HashSet<int>();
        var problems = new List<string>();
        int max = start;
        while (todo.Count > 0)
        {
            int a = todo.Pop();
            while (seen.Add(a))
            {
                if (a < lo || a >= hi || a - img.Base >= img.Data.Length) { problems.Add($"Z80 0x{a:X4} outside 0x{lo:X4}-0x{hi:X4}"); break; }
                byte b = img[a];
                max = Math.Max(max, a);
                if (b < 0xE0) { a++; continue; }
                int n = FlagParams[b];
                max = Math.Max(max, a + n);
                if (b == 0xEF) voices.Add(img[a + 1]);
                if (Terminators.Contains(b)) break;
                switch (b)
                {
                    case 0xF6: a = img.Le(a + 1); continue;
                    case 0xF8: todo.Push(img.Le(a + 1)); a += 3; continue;
                    case 0xF7: todo.Push(img.Le(a + 3)); a += 5; continue;
                    default: a += 1 + n; continue;
                }
            }
        }
        return new TrackWalk(max, voices, problems);
    }

    public sealed record SongInfo(int Id, int Flags, int Bank, bool Uncompressed, bool PalSpeedUpDisabled, Image Image);

    public static SongInfo LoadSong(RomImage rom, byte[] driver, int index)
    {
        int flags = driver[MusicPlaylist + index];
        int bank = (flags & 0x80) != 0 ? MusicBank1 : MusicBank0;
        int ptr = rom.U8(bank + (flags & 0x1F) * 2) | (rom.U8(bank + (flags & 0x1F) * 2 + 1) << 8);
        int romAddr = BankAddress(bank, ptr);
        bool uncompressed = (flags & 0x20) != 0;
        Image img;
        if (uncompressed)
        {
            img = new Image(rom.Slice(romAddr, Math.Min(0x10000 - ptr, rom.Length - romAddr)).ToArray(), ptr, romAddr, null);
        }
        else
        {
            var data = Saxman.DecompressWithHeader(rom.Span[romAddr..], out int consumed);
            img = new Image(data, SongDecompressAddress, romAddr, consumed);
        }
        return new SongInfo(0x81 + index, flags, bank, uncompressed, (flags & 0x40) == 0, img);
    }

    public sealed record Channel(string Kind, int Pointer, int Transpose, int Volume, int? Assignment = null, int? PsgVoice = null);

    /// <param name="Start">Lowest Z80 address the sound uses (header, tracks or voices).</param>
    /// <param name="End">One past the highest Z80 address the sound uses.</param>
    public sealed record Sound(int Header, int Start, int End, int VoicePointer, int VoiceCount, IReadOnlyList<Channel> Channels,
        int? TempoDivider, int? Tempo, IReadOnlyList<string> Problems)
    {
        public int Length => End - Header;
    }

    /// <summary>Header as in the 68k format but little-endian and absolute: LE voices, FM, PSG, divider, tempo, channels.</summary>
    public static Sound ParseMusic(Image img) => ParseMusic(img, img.Base);

    public static Sound ParseMusic(Image img, int b)
    {
        int fm = img[b + 2], psg = img[b + 3];
        var channels = new List<Channel>();
        for (int i = 0; i < fm; i++)
        {
            int e = b + 6 + i * 4;
            channels.Add(new Channel(i == 0 ? "dac" : "fm", img.Le(e), (sbyte)img[e + 2], (sbyte)img[e + 3]));
        }
        for (int i = 0; i < psg; i++)
        {
            int e = b + 6 + fm * 4 + i * 6;
            channels.Add(new Channel("psg", img.Le(e), (sbyte)img[e + 2], (sbyte)img[e + 3], PsgVoice: img[e + 5]));
        }
        // Songs are self-contained: tracks lie between the header and the voice table.
        return Finish(img, b, img.Le(b), channels, img[b + 4], img[b + 5], strict: true);
    }

    /// <summary>SFX may share voice tables and call into each other, so they are walked against the whole bank image.</summary>
    public static Sound ParseSfx(Image bank, int b)
    {
        int count = bank[b + 3];
        var channels = new List<Channel>();
        for (int i = 0; i < count; i++)
        {
            int e = b + 4 + i * 6;
            int id = bank[e + 1];
            channels.Add(new Channel((id & 0x80) != 0 ? "psg" : "fm", bank.Le(e + 2), (sbyte)bank[e + 4], (sbyte)bank[e + 5], Assignment: id));
        }
        return Finish(bank, b, bank.Le(b), channels, bank[b + 2], null, strict: false);
    }

    private static Sound Finish(Image img, int header, int voicePtr, List<Channel> channels, int? divider, int? tempo, bool strict)
    {
        int lo = strict ? header : img.Base;
        int hi = strict && voicePtr > header ? voicePtr : img.Base + img.Data.Length;
        var walks = channels.Select(c => Walk(img, c.Pointer, lo, hi)).ToList();
        var voices = walks.SelectMany(w => w.VoicesUsed).ToList();
        int voiceCount = voices.Count > 0 ? voices.Max() + 1 : 0;
        int voiceEnd = voiceCount > 0 ? voicePtr + voiceCount * Smps68k.VoiceSize : 0;
        int end = Math.Max(walks.Max(w => w.MaxAddress) + 1, voiceEnd);
        int start = new[] { header, voiceCount > 0 ? voicePtr : header }.Concat(channels.Select(c => c.Pointer)).Min();
        return new Sound(header, start, end, voicePtr, voiceCount, channels, divider, tempo, walks.SelectMany(w => w.Problems).ToList());
    }

    /// <summary>DAC loop 0x0174-0x01B0: 262 + 26*pitch Z80 cycles per byte (two samples). Computed, unverified.</summary>
    public static double SampleRate(int pitch) => 3579545.0 / ((262 + 26.0 * pitch) / 2);
}
