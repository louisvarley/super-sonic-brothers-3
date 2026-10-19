namespace S2Port.Extraction.Smb3;

/// <summary>
/// Turns an SMB3 song into a song for the hack's 68k SMPS driver (the Sonic 1 format the port's driver plays), from
/// the ported sequencer's sound register writes (<see cref="Smb3Music"/>): a frame-by-frame model of what each NES
/// channel sounds, then notes, rests and volume steps.
/// <list type="bullet">
/// <item>Square 2 (the melody) -> PSG 1, square 1 -> PSG 2: exact pitches (nearest semitone; an octave up below the
///   PSG's range) and the per-frame volume envelope as tied note segments with volume steps.</item>
/// <item>Triangle (bass) -> FM 1 with a single sine operator; it sounds while the NES linear and length counters
///   would let it.</item>
/// <item>Noise -> the PSG noise channel; samples (DMC) -> the DAC's kick or snare.</item>
/// </list>
/// The song plays the intro once and loops back to the segment SMB3 loops to. Durations are frames (divider 1), placed
/// so that the driver's tempo hold keeps them on SMB3's frames. This is an arrangement, not a port: the instrument
/// choices are this port's.
/// </summary>
public sealed class Smb3SongConverter
{
    private const double NesCpu = 1789773.0, PsgClock = 3579545.0, FmClock = 7670453.0;
    private static readonly int[] LengthTable =
        [10, 254, 20, 2, 40, 4, 80, 6, 160, 8, 60, 10, 14, 12, 26, 14, 12, 16, 24, 18, 48, 20, 96, 22, 192, 24, 72, 26, 16, 28, 32, 30];

    /// <summary>The note (MIDI number) and PSG attenuation / FM on-off a channel has in one frame; Note -1 = silent.</summary>
    public readonly record struct FrameState(int Note, int Volume, bool Attack);

    /// <summary>Square 2's frame-by-frame notes from the song's start (what PSG 1 plays), for validation.</summary>
    public static FrameState[] MelodyTimeline(NesRom rom, int smb3Song, int frames)
    {
        var music = new Smb3Music(rom);
        music.Start(smb3Song);
        var writes = new List<List<(int Reg, int Value)>>();
        for (int f = 0; f < frames; f++) { music.Frame(); writes.Add([.. music.Writes]); }
        return Squares(writes, 0x4004, frames);
    }

    /// <summary>The PSG note byte this converter uses for a MIDI note (for validation).</summary>
    public int PsgNote(int midi) => SmpsNote(midi, fm: false);

    private readonly ushort[] _psgFreq, _fmFreq;

    public Smb3SongConverter(ushort[] psgFrequencies, ushort[] fmFrequencies)
    {
        _psgFreq = psgFrequencies;
        _fmFreq = fmFrequencies;
    }

    /// <summary>The song's bytes, built to sit at <paramref name="address"/> (pointers inside are relative).</summary>
    public byte[] Convert(NesRom rom, int smb3Song, int address, out int introFrames, out int loopFrames,
        bool set2A = false)
    {
        var music = new Smb3Music(rom);
        music.Start(smb3Song, set2A);
        // Run until a segment starts a second time: the loop is from its first start to there.
        var frames = new List<List<(int Reg, int Value)>>();
        var firstStart = new Dictionary<int, int>();
        int loopStart = -1, end = -1;
        for (int f = 0; f < 60 * 600 && end < 0; f++)
        {
            music.Frame();
            if (!music.Playing) { loopStart = end = f; break; }
            if (music.SegmentStarted >= 0)
            {
                if (firstStart.TryGetValue(music.SegmentStarted, out int at)) { loopStart = at; end = f; break; }
                firstStart[music.SegmentStarted] = f;
            }
            frames.Add([.. music.Writes]);
        }
        if (end < 0) throw new InvalidDataException("SMB3 song did not loop or finish within 10 minutes");
        introFrames = loopStart;
        loopFrames = end - loopStart;
        _rom = rom;

        var sq2 = Squares(frames, 0x4004, end);
        var sq1 = Squares(frames, 0x4000, end);
        var tri = Triangle(frames, end);
        var noise = Noise(frames, end);
        var dmc = Samples(frames, end);

        // The driver holds every track back one frame each `tempo` frames (TempoWait, 0x130D08). With a tempo that divides
        // the intro and the loop, those frames fall at the same places on every pass, and each event is written at the
        // driver time it is reached at the original frame: M(t) = t - floor(t / tempo).
        int tempo = Gcd(loopStart == 0 ? end : loopStart, end - loopStart);
        while (tempo > 0xFF) tempo /= 2;
        if (tempo < 2) tempo = 0xFF;
        _tempo = tempo;

        var w = new SongWriter(address);
        // Header: voices, 2 FM tracks (DAC, FM1), 3 PSG tracks, divider 1, the tempo.
        w.Word(0); w.Byte(2); w.Byte(3); w.Byte(1); w.Byte(tempo);
        int dacEntry = w.Entry4(), fm1Entry = w.Entry4();
        int psg1Entry = w.Entry6(), psg2Entry = w.Entry6(), psg3Entry = w.Entry6();
        w.PatchHeaderWord(0, w.Position);
        w.Bytes(Voice);

        w.Track(dacEntry, 0, 0);
        WriteDac(w, dmc, loopStart, end);
        w.Track(fm1Entry, 0, 0x12);   // the bass under the melody
        WriteChannel(w, tri, loopStart, end, fm: true, first: [0xEF, 0x00]);
        w.Track(psg1Entry, 0, 0, psg: true);
        WriteChannel(w, sq2, loopStart, end, fm: false, first: []);
        w.Track(psg2Entry, 0, 0, psg: true);
        WriteChannel(w, sq1, loopStart, end, fm: false, first: []);
        w.Track(psg3Entry, 0, 0, psg: true);
        WriteChannel(w, noise, loopStart, end, fm: false, first: [0xF3, 0xE4]);
        return w.ToArray();
    }

    private NesRom? _rom;
    private int _tempo = 0xFF;

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

    /// <summary>Driver time of original frame t.</summary>
    private int M(int t) => t - t / _tempo;

    /// <summary>DMC_MODLEN_LUT (prg031 $E31A): the samples' lengths; SMB3's short drum is the kick, longer ones the snare.</summary>
    private const int DmcLengths = 0xE31A;

    private int DacNote(int sample) => _rom!.Prg(31, DmcLengths + sample - 1) <= 0x10 ? 0x81 : 0x82;

    // ------------------------------------------------------------------ the NES side

    private static double SquareHz(int period) => NesCpu / (16.0 * (period + 1));
    private static int Midi(double hz) => (int)Math.Round(69 + 12 * Math.Log2(hz / 440.0));

    /// <summary>A NES volume (0-15, linear) as a PSG attenuation (2 dB steps); 0 is silence.</summary>
    /// <remarks>Two steps (4 dB) louder than the plain conversion, so the squares carry the tune over the FM bass and
    /// the DAC drums as they do on the NES.</remarks>
    private static int Attenuation(int volume) =>
        volume == 0 ? -1 : Math.Clamp((int)Math.Round(-20 * Math.Log10(volume / 15.0) / 2) - 2, 0, 15);

    private static FrameState[] Squares(List<List<(int Reg, int Value)>> frames, int ctlReg, int end)
    {
        var states = new FrameState[end];
        int period = 0, ctl = 0;
        for (int f = 0; f < end; f++)
        {
            bool attack = false;
            foreach (var (reg, v) in frames[f])
            {
                if (reg == ctlReg) ctl = v;
                else if (reg == ctlReg + 2) period = (period & 0x700) | v;
                else if (reg == ctlReg + 3) { period = (period & 0xFF) | ((v & 7) << 8); attack = true; }
            }
            int att = Attenuation((ctl & 0x10) != 0 ? ctl & 0x0F : 0);
            states[f] = att < 0 || period < 8 ? new FrameState(-1, 0, false) : new FrameState(Midi(SquareHz(period)), att, attack);
        }
        return states;
    }

    /// <summary>The triangle: linear counter clocked four times a frame, length counter twice (frame sequencer, mode 0).</summary>
    private static FrameState[] Triangle(List<List<(int Reg, int Value)>> frames, int end)
    {
        var states = new FrameState[end];
        int period = 0, control = 0, linear = 0, length = 0;
        bool reload = false;
        for (int f = 0; f < end; f++)
        {
            bool attack = false;
            foreach (var (reg, v) in frames[f])
            {
                if (reg == 0x4008) control = v;
                else if (reg == 0x400A) period = (period & 0x700) | v;
                else if (reg == 0x400B) { period = (period & 0xFF) | ((v & 7) << 8); length = LengthTable[v >> 3]; reload = true; attack = true; }
            }
            for (int q = 0; q < 4; q++)
            {
                if (reload) linear = control & 0x7F;
                else if (linear > 0) linear--;
                if ((control & 0x80) == 0) reload = false;
                if (q % 2 == 1 && (control & 0x80) == 0 && length > 0) length--;
            }
            bool sounding = linear > 0 && length > 0 && period >= 2;
            states[f] = sounding ? new FrameState(Midi(NesCpu / (32.0 * (period + 1))), 0, attack) : new FrameState(-1, 0, false);
        }
        return states;
    }

    /// <summary>Noise hits: they last as long as the length counter ($400F), at the constant volume of $400C.</summary>
    private static FrameState[] Noise(List<List<(int Reg, int Value)>> frames, int end)
    {
        var states = new FrameState[end];
        int left = 0, att = -1;
        for (int f = 0; f < end; f++)
        {
            bool attack = false;
            int ctl = -1, len = -1;
            foreach (var (reg, v) in frames[f])
            {
                if (reg == 0x400C) ctl = v;
                else if (reg == 0x400F) len = v;
            }
            if (len >= 0 && ctl >= 0)
            {
                att = Attenuation(ctl & 0x0F);
                left = Math.Max(1, LengthTable[len >> 3] / 2);
                attack = att >= 0;
            }
            states[f] = left > 0 && att >= 0 ? new FrameState(0, Math.Min(15, att + 2), attack) : new FrameState(-1, 0, false);
            if (left > 0) left--;
        }
        return states;
    }

    /// <summary>DMC sample starts: sample number per frame (0 = none).</summary>
    private static int[] Samples(List<List<(int Reg, int Value)>> frames, int end)
    {
        var hits = new int[end];
        for (int f = 0; f < end; f++)
            foreach (var (reg, v) in frames[f])
                if (reg == 0x4100) hits[f] = v;
        return hits;
    }

    // ------------------------------------------------------------------ the SMPS side

    /// <summary>The FM voice for the triangle: algorithm 0, only operator 4 (the carrier) audible: a sine.</summary>
    private static readonly byte[] Voice =
    [
        0x00,                         // feedback 0, algorithm 0
        0x01, 0x01, 0x01, 0x01,       // DT/MUL
        0x1F, 0x1F, 0x1F, 0x1F,       // RS/AR
        0x00, 0x00, 0x00, 0x00,       // AM/D1R
        0x00, 0x00, 0x00, 0x00,       // D2R
        0x0A, 0x0A, 0x0A, 0x0A,       // D1L/RR
        0x7F, 0x7F, 0x7F, 0x00,       // TL (operators 1, 3, 2 silent; 4 is the carrier)
    ];

    /// <summary>The driver's note byte ($81+) nearest a MIDI note.</summary>
    private int SmpsNote(int midi, bool fm)
    {
        double hz = 440.0 * Math.Pow(2, (midi - 69) / 12.0);
        var table = fm ? _fmFreq : _psgFreq;
        int best = 0;
        double bestErr = double.MaxValue;
        for (int i = 0; i < table.Length; i++)
        {
            double f = fm
                ? (table[i] & 0x7FF) * FmClock / 144.0 / Math.Pow(2, 21 - (table[i] >> 11))
                : PsgClock / (32.0 * table[i]);
            if (f <= 0) continue;
            double err = Math.Abs(Math.Log2(f / hz));
            if (err < bestErr) { bestErr = err; best = i; }
        }
        if (!fm && bestErr > 0.5 / 12 && midi < 60) return SmpsNote(midi + 12, fm);   // below the PSG's range: an octave up
        return 0x81 + best;
    }

    private void WriteChannel(SongWriter w, FrameState[] s, int loopStart, int end, bool fm, byte[] first)
    {
        w.Bytes(first);
        int volume = 0;
        void Section(int from, int to)
        {
            int f = from;
            while (f < to)
            {
                var st = s[f];
                int run = 1;
                while (f + run < to && s[f + run] == st with { Attack = false } && !s[f + run].Attack) run++;
                int frames = M(f + run) - M(f);
                if (frames == 0) { f += run; continue; }   // a held frame swallowed this one
                if (st.Note < 0) { w.Rest(frames); f += run; continue; }
                if (!fm && st.Volume != volume) { w.Byte(0xEC); w.Byte((byte)(st.Volume - volume)); volume = st.Volume; }
                bool tie = !st.Attack && f > from && s[f - 1].Note >= 0;
                w.Note(SmpsNote(st.Note, fm), frames, tie);
                f += run;
            }
        }
        Section(0, loopStart);
        int loopAt = w.Position;
        Section(loopStart, end);
        if (loopStart == end) w.Byte(0xF2);
        else w.Jump(loopAt);
    }

    private void WriteDac(SongWriter w, int[] hits, int loopStart, int end)
    {
        void Section(int from, int to)
        {
            int f = from;
            // Rest up to the first hit.
            int next = f;
            while (next < to && hits[next] == 0) next++;
            if (M(next) > M(f)) w.Rest(M(next) - M(f));
            f = next;
            while (f < to)
            {
                int run = 1;
                while (f + run < to && hits[f + run] == 0) run++;
                if (M(f + run) > M(f)) w.Note(DacNote(hits[f]), M(f + run) - M(f), tie: false);
                f += run;
            }
        }
        Section(0, loopStart);
        int loopAt = w.Position;
        Section(loopStart, end);
        if (loopStart == end) w.Byte(0xF2);
        else w.Jump(loopAt);
    }

    /// <summary>Song bytes with the driver's relative pointers.</summary>
    private sealed class SongWriter(int address)
    {
        private readonly List<byte> _b = [];
        public int Position => _b.Count;
        public void Byte(int v) => _b.Add((byte)v);
        public void Bytes(IEnumerable<byte> v) => _b.AddRange(v);
        public void Word(int v) { Byte(v >> 8); Byte(v); }
        public int Entry4() { int p = Position; Word(0); Word(0); return p; }
        public int Entry6() { int p = Position; Word(0); Word(0); Word(0); return p; }
        public void PatchHeaderWord(int at, int value) { _b[at] = (byte)(value >> 8); _b[at + 1] = (byte)value; }

        /// <summary>Points a track entry here (offsets from the header), with transpose and volume.</summary>
        public void Track(int entry, int transpose, int volume, bool psg = false)
        {
            PatchHeaderWord(entry, Position);
            _b[entry + 2] = (byte)transpose;
            _b[entry + 3] = (byte)volume;
            if (psg) _b[entry + 5] = 0;   // no PSG envelope: the volume steps are explicit
        }

        public void Rest(int frames)
        {
            while (frames > 0) { int d = Math.Min(frames, 0x7F); Byte(0x80); Byte(d); frames -= d; }
        }

        public void Note(int note, int frames, bool tie)
        {
            bool first = true;
            while (frames > 0)
            {
                int d = Math.Min(frames, 0x7F);
                if (tie || !first) Byte(0xE7);
                Byte(note);
                Byte(d);
                frames -= d;
                first = false;
            }
        }

        /// <summary>$F6: jump to song offset <paramref name="target"/> (operand address + 1 + offset).</summary>
        public void Jump(int target)
        {
            Byte(0xF6);
            int operand = Position;
            Word(target - (operand + 1));
        }

        public byte[] ToArray() => [.. _b];
        public int Address => address;
    }
}
