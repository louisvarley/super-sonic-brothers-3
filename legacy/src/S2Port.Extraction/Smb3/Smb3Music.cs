namespace S2Port.Extraction.Smb3;

/// <summary>
/// SMB3's music sequencer (prg031 Sound_PlayMusic / Music_Sq2Track ... the DMC track, with its tables in banks 28 and
/// 31), ported for one Set 2B song with no sound effects playing. <see cref="Frame"/> runs one NMI's worth and records
/// the sound register writes ($4000-$4015) it makes, as the original makes them; validated against MAME's register log
/// in World 1-1 (tools/Validate smb3-music). The Easter egg turns the result into a song for the Sonic sound driver.
/// </summary>
public sealed class Smb3Music
{
    // Addresses (bank 28 at $A000, 29 at $C000, 31 at $E000 while the sound engine runs).
    private const int Set2BIndexOffs = 0xB400, Set2BHeaders = 0xB42D, Set2BStarts = 0xB530, Set2BEnds = 0xB53C,
        Set2BLoops = 0xB548, RestLut = 0xE874, PatchLong = 0xE765, PatchShort = 0xE775, NoteTable = 0xE839,
        NoiseLutA = 0xE74A, NoiseLutB = 0xE74E, NoiseLutC = 0xE752;

    private readonly NesRom _rom;

    public Smb3Music(NesRom rom) => _rom = rom;

    private byte Cpu(int a) => a switch
    {
        >= 0xE000 => _rom.Prg(31, a),
        >= 0xC000 => _rom.Prg(29, a),
        >= 0xA000 => _rom.Prg(28, a),
        _ => throw new ArgumentOutOfRangeException(nameof(a), $"${a:X4} is not mapped while the sound engine runs"),
    };

    /// <summary>Sound register writes of the current frame: (register $4000-$4017, value).</summary>
    public List<(int Reg, int Value)> Writes { get; } = [];
    /// <summary>The segment index started this frame (Music_NextIndex - 1), or -1.</summary>
    public int SegmentStarted { get; private set; } = -1;

    // Sequencer state (the original's RAM variables).
    private int _start, _end, _loop, _nextIndex, _restBase, _base;
    private int _sq2Off, _sq1Off, _triPos, _nsePos, _nseStart, _pcmPos, _pcmStart;
    private int _sq2Rest, _sq1Rest, _triRest, _nseRest, _dmcRest;
    private int _sq2RestH, _sq1RestH, _triRestH, _nseRestH, _dmcRestH;
    private int _sq2Patch, _sq1Patch, _sq2NoteLen, _sq1NoteLen, _sq1AltRamp, _sq1Bend, _sq2Bend;
    private int _sq1CurFL, _sq2CurFL, _dmcCurrent, _dmcTime, _dmcQueue;
    private bool _playing;
    public bool Playing => _playing;
    private int _indexTable = Set2BIndexOffs, _headers = Set2BHeaders;

    private void W(int reg, int v) => Writes.Add((reg, v & 0xFF));

    /// <summary>SndMus_QueueSet2B: start Set 2B song <paramref name="song"/> ($10-$C0).</summary>
    public void Start(int song, bool set2A = false)
    {
        int count = set2A ? 15 : 12;
        if (song < 0x10 || song > count * 0x10 || (song & 15) != 0)
            throw new ArgumentOutOfRangeException(nameof(song), "Expected a song selector in multiples of $10.");
        _indexTable = set2A ? 0xA740 : Set2BIndexOffs;
        _headers = set2A ? 0xA76C : Set2BHeaders;
        int y = song >> 4;
        _start = Cpu((set2A ? 0xA86D : Set2BStarts) + y - 1);
        _end = (Cpu((set2A ? 0xA87C : Set2BEnds) + y - 1) + 2) & 0xFF;
        _loop = Cpu((set2A ? 0xA88B : Set2BLoops) + y - 1);
        _nextIndex = _start;
        _playing = true;
        _pendingStart = true;
    }

    private bool _pendingStart;

    /// <summary>One frame of Sound_PlayMusic.</summary>
    public void Frame()
    {
        Writes.Clear();
        SegmentStarted = -1;
        if (!_playing) return;
        if (_pendingStart) { _pendingStart = false; Next(); return; }
        Sq2Track();
    }

    /// <summary>SndMus2B_Next / SndMus2B_LoadNext, then PRG031_E48C and on into the tracks.</summary>
    private void Next()
    {
        while (true)
        {
            _nextIndex = (_nextIndex + 1) & 0xFF;
            if (_nextIndex != _end) break;
            if (_loop == 0) { _playing = false; W(0x4015, 0); W(0x4015, 0x0F); return; }
            _nextIndex = _loop;
        }
        SegmentStarted = _nextIndex - 1;
        int y = Cpu(_indexTable + _nextIndex - 1);
        _restBase = Cpu(_headers + y);
        _base = Cpu(_headers + y + 1) | (Cpu(_headers + y + 2) << 8);
        _triPos = Cpu(_headers + y + 3);
        _sq1Off = Cpu(_headers + y + 4);
        _nsePos = _nseStart = Cpu(_headers + y + 5);
        _pcmPos = _pcmStart = Cpu(_headers + y + 6);
        _sq2Rest = _sq1Rest = _triRest = _nseRest = _dmcRest = 1;
        _sq2Off = 0;
        _sq1AltRamp = _sq1Bend = _sq2Bend = 0;
        W(0x4015, 0x0B);
        W(0x4015, 0x0F);
        Sq2Track();
    }

    private int Data(int off) => Cpu((_base + (off & 0xFF)) & 0xFFFF);

    /// <summary>Music_GetRestTicks (no low-time offset).</summary>
    private int RestTicks(int b) => Cpu(RestLut + (((b & 0x0F) + _restBase) & 0xFF));

    /// <summary>Music_CalcNoteLen.</summary>
    private static int NoteLen(int restH) => restH < 19 ? 0x16 : 0x3F;

    /// <summary>Music_PatchGetCTL.</summary>
    private int PatchCtl(int restH, int patch, int y)
    {
        int x = (patch & 0x70) >> 3;
        int table = restH >= 19 ? PatchLong : PatchShort;
        int ptr = Cpu(table + x) | (Cpu(table + x + 1) << 8);
        return Cpu((ptr + y) & 0xFFFF);
    }

    /// <summary>Sound_Sq1_NoteOn_NoPAPURAMP / Sq2 / Tri (x = 0, 4, 8): false for a rest ($7E).</summary>
    private bool NoteOn(int note, int x)
    {
        if (note == 0x7E) return false;
        int octave = 1;
        int a = note;
        if ((a & 0x80) == 0)
            do { octave++; a = (a - 24) & 0xFF; } while ((a & 0x80) == 0);
        a = (a + 24) & 0xFF;
        int lo = Cpu(NoteTable + a), hi = Cpu(NoteTable + a + 1);
        int f = (hi << 8) | lo;
        while (octave-- > 0) f >>= 1;
        lo = f & 0xFF; hi = f >> 8;
        if (note >= 56) lo = (lo - 1) & 0xFF;
        if (x == 4)
        {
            if (_sq1Patch == 0xE0)
            {
                lo = (lo - 2) & 0xFF;
                W(0x4006, lo); _sq2CurFL = lo;
                W(0x4007, hi | 0x08);
                return true;
            }
            if (_sq2Bend != 0) { _sq2CurFL = lo; return true; }
        }
        W(0x4002 + x, lo);
        if (x == 0) _sq1CurFL = lo; else if (x == 4) _sq2CurFL = lo;
        W(0x4003 + x, hi | 0x08);
        return true;
    }

    /// <summary>Music_UpdateBend.</summary>
    private void UpdateBend(int rest, ref int bend, int curFL, int reg)
    {
        if ((rest & 3) == 3) bend = (bend - 1) & 0xFF;
        int a = bend;
        if (bend < curFL) { bend = 0; a = curFL; }
        W(reg, a);
    }

    private void Sq2Track()
    {
        if (--_sq2Rest == 0)
        {
            int b = Data(_sq2Off++);
            if (b == 0) { Next(); return; }
            while (b >= 0x80)
            {
                if (b == 0xFF) { _sq2Off++; b = Data(_sq2Off++); if (b == 0) { Next(); return; } continue; }
                _sq2Patch = b & 0xF0;
                _sq2RestH = RestTicks(b);
                b = Data(_sq2Off++);
                break;
            }
            while (true)
            {
                _sq2NoteLen = NoteOn(b, 4) ? NoteLen(_sq2RestH) : 0;
                if (Data(_sq2Off++) == 0xFF) { _sq2Bend = _sq2CurFL; b = Data(_sq2Off++); continue; }
                _sq2Off--;
                break;
            }
            _sq2Rest = _sq2RestH;
        }
        if (_sq2Bend != 0) UpdateBend(_sq2Rest, ref _sq2Bend, _sq2CurFL, 0x4006);
        int y = _sq2NoteLen;
        if (y != 0) _sq2NoteLen--;
        W(0x4004, PatchCtl(_sq2RestH, _sq2Patch, y));
        W(0x4005, 0x7F);
        Sq1Track();
    }

    private void Sq1Track()
    {
        if (--_sq1Rest == 0)
        {
            while (true)
            {
                int b = Data(_sq1Off++);
                if (b == 0xFF) { _sq1Off++; continue; }
                if (b >= 0x80)
                {
                    _sq1Patch = b & 0xF0;
                    _sq1RestH = RestTicks(b);
                    b = Data(_sq1Off++);
                }
                if (b == 0)
                {
                    W(0x4000, 0x83);
                    W(0x4001, 0x94);
                    _sq1AltRamp = 0x94;
                    continue;
                }
                while (true)
                {
                    _sq1NoteLen = NoteOn(b, 0) ? NoteLen(_sq1RestH) : 0;
                    if (Data(_sq1Off++) == 0xFF) { _sq1Bend = _sq1CurFL; b = Data(_sq1Off++); continue; }
                    _sq1Off--;
                    break;
                }
                break;
            }
            _sq1Rest = _sq1RestH;
        }
        if (_sq1Bend != 0) UpdateBend(_sq1Rest, ref _sq1Bend, _sq1CurFL, 0x4002);
        int y = _sq1NoteLen;
        if (y != 0) _sq1NoteLen--;
        W(0x4000, PatchCtl(_sq1RestH, _sq1Patch, y));
        W(0x4001, _sq1AltRamp != 0 ? _sq1AltRamp : 0x7F);
        TriTrack();
    }

    private void TriTrack()
    {
        if (_triPos != 0 && --_triRest == 0)
        {
            int b = Data(_triPos++);
            _triPos &= 0xFF;
            int tcr;
            if (b >= 0x80)
            {
                _triRestH = RestTicks(b);
                W(0x4008, 0x1F);
                b = Data(_triPos++);
                _triPos &= 0xFF;
                tcr = b == 0 ? 0 : TriNote(b);
            }
            else tcr = TriNote(b);
            W(0x4008, tcr);
            _triRest = _triRestH;
        }
        NoiseTrack();
    }

    private int TriNote(int b)
    {
        if (!NoteOn(b, 8)) return 0x80;
        return _triRestH < 0x0A ? 0x18 : _triRestH < 0x13 ? 0x20 : _triRestH >= 0x25 ? 0xFF : 0x50;
    }

    private void NoiseTrack()
    {
        if (_nsePos != 0 && --_nseRest == 0)
        {
            while (true)
            {
                int b = Data(_nsePos++);
                _nsePos &= 0xFF;
                if (b == 0) { _nsePos = _nseStart; continue; }
                if (b >= 0x80)
                {
                    _nseRestH = RestTicks(b);
                    b = Data(_nsePos++);
                    _nsePos &= 0xFF;
                    if (b == 0) { _nsePos = _nseStart; continue; }
                }
                int y = b >> 1;
                W(0x400C, Cpu(NoiseLutA + y));
                W(0x400E, Cpu(NoiseLutB + y));
                W(0x400F, Cpu(NoiseLutC + y));
                _nseRest = _nseRestH;
                break;
            }
        }
        PcmTrack();
    }

    /// <summary>The DMC track; the sample chosen is recorded as a write to $4010's companion "sample" pseudo-register $4100.</summary>
    private void PcmTrack()
    {
        if (_pcmPos != 0 && --_dmcRest == 0)
        {
            while (true)
            {
                int b = Data(_pcmPos++);
                _pcmPos &= 0xFF;
                if (b == 0) { _pcmPos = _pcmStart; continue; }
                if (b >= 0x80)
                {
                    _dmcRestH = RestTicks(b);
                    b = Data(_pcmPos++);
                    _pcmPos &= 0xFF;
                    if (b == 0) { _pcmPos = _pcmStart; continue; }
                }
                _dmcQueue = b;
                _dmcRest = _dmcRestH;
                break;
            }
        }
        PlayDmc();
        _dmcQueue = 0;
    }

    /// <summary>Music_PlayDMC: a new sample (recorded as pseudo-register $4100 = sample number), or the 160-frame stop.</summary>
    private void PlayDmc()
    {
        if (_dmcQueue == 0x7E) { _dmcCurrent = 0; W(0x4015, 0x0F); return; }
        if (_dmcQueue != 0)
        {
            _dmcCurrent = _dmcQueue;
            _dmcTime = 0xA0;
            W(0x4100, _dmcQueue);
            return;
        }
        if (_dmcCurrent == 0) { W(0x4015, 0x0F); return; }
        if (--_dmcTime == 0) { _dmcCurrent = 0; W(0x4015, 0x0F); }
    }
}
