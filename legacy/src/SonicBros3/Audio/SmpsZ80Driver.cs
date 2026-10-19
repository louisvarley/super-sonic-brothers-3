using S2Port.Extraction.Audio;

namespace SonicBros3.Audio;

/// <summary>
/// What the Z80 driver reads: its own code and tables (the decompressed driver, loaded into the Z80's RAM from 0, as
/// the 68k's SoundDriverLoad does) and the ROM banks it maps into its window at $8000.
/// </summary>
public sealed class SmpsZ80Data
{
    public byte[] DriverImage { get; }
    private readonly Dictionary<int, byte[]> _banks = [];

    public SmpsZ80Data(string dataDir)
    {
        string dir = Path.Combine(dataDir, "audio", "smpsz80");
        DriverImage = S2Port.Extraction.DataFile.ReadAllBytes(Path.Combine(dir, "driver.bin"));
        foreach (int bankBase in SmpsZ80.Banks)
            _banks[bankBase >> 15] = S2Port.Extraction.DataFile.ReadAllBytes(Path.Combine(dir, $"bank_{bankBase:X6}.bin"));
    }

    /// <summary>A byte of ROM bank <paramref name="bank"/> (the 68k address bits 15-23) at window offset 0-$7FFF.</summary>
    public byte Bank(int bank, int offset) => _banks.TryGetValue(bank, out var b) ? b[offset & 0x7FFF] : (byte)0;
}

/// <summary>
/// Sonic 2's Z80 sound driver (title screen and Sonic 2's zones, $FFE5 = 0), ported routine by routine from its code
/// (the Sonic 2 disassembly's s2.sounddriver.asm; the hack's copy is byte-identical apart from bank numbers and DAC
/// pitches). Everything lives in an 8 KiB array laid out exactly as the Z80's RAM, so it can be compared with MAME's:
///
///   $0000-$12FD  the driver's code and tables (the code modifies some of its own operands: those bytes are written
///                here too, and read back where the original reads them)
///   $12FE-$1307  a few globals (zPALUpdTick ... zPaused)
///   $1380-$1B3F  the current song, Saxman-decompressed (uncompressed songs play straight from the ROM window)
///   $1B80        zAbsVar (the "mailbox" the 68k writes requests into), then the tracks, $2A bytes each:
///                music DAC $1B98, FM1-6 $1BC2-$1C94, PSG1-3 $1CBE-$1D12; SFX FM3-5 $1D3C-$1D90, PSG1-3 $1DBA-$1E0E;
///                the copy saved while the extra-life jingle plays at $1E38.
///
/// <see cref="VInt"/> is the Z80's VBlank interrupt (zVInt, $0038): once per frame. Between interrupts the original
/// streams the current DAC sample to the YM2612 in a busy loop; here a sample's start hands the decoded sample to
/// <see cref="ISoundChips.PlayDacPcm"/>. Several routines leave their caller early by popping its return address;
/// those return a flag here instead.
/// </summary>
public sealed class SmpsZ80Driver(SmpsZ80Data data, ISoundChips chips)
{
    /// <summary>The Z80's RAM.</summary>
    public byte[] Ram { get; } = new byte[0x2000];

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ISoundChips, SmpsZ80Driver> Loaded = new();

    /// <summary>
    /// The Z80 that drives these chips (the sound CPU and its RAM are part of the console's sound hardware, so the
    /// driver lives on across levels); loaded and started the first time.
    /// </summary>
    public static SmpsZ80Driver For(ISoundChips chips, string dataDir) => Loaded.GetValue(chips, c =>
    {
        var driver = new SmpsZ80Driver(new SmpsZ80Data(dataDir), c);
        driver.Reset();
        return driver;
    });

    // ------------------------------------------------------------------ the memory map

    public const int AbsVar = 0x1B80;
    private const int TrackSize = 0x2A;
    public const int SongDac = 0x1B98, SongFm1 = 0x1BC2, SongPsg1 = 0x1CBE, SfxFm3 = 0x1D3C, SfxEnd = 0x1E38;
    private const int SongEnd = SfxFm3, SaveVar = 0x1E38, SaveLength = 0x1BC;
    private const int MusicTracks = 10, MusicFmTracks = 6, MusicPsgTracks = 3, SfxFmTracks = 3, SfxPsgTracks = 3;
    private const int MusicData = 0x1380;

    // zVar (zAbsVar) offsets.
    private const int SfxPriorityVal = 0x00, TempoTimeout = 0x01, CurrentTempo = 0x02, StopMusic = 0x03,
        FadeOutCounter = 0x04, FadeOutDelay = 0x05, Communication = 0x06, DacUpdating = 0x07, QueueToPlay = 0x08,
        Queue0 = 0x09, Queue1 = 0x0A, Queue2 = 0x0B, VoiceTblPtr = 0x0C, FadeInFlag = 0x0E, FadeInDelay = 0x0F,
        FadeInCounter = 0x10, OneUpPlaying = 0x11, TempoMod = 0x12, TempoTurbo = 0x13, SpeedUpFlag = 0x14,
        DacEnabled = 0x15, MusicBankNumber = 0x16, IsPalFlag = 0x17;

    // zTrack offsets.
    private const int PlaybackControl = 0x00, VoiceControl = 0x01, TempoDivider = 0x02, DataPointer = 0x03,
        Transpose = 0x05, Volume = 0x06, AmsFmsPan = 0x07, VoiceIndex = 0x08, VolFlutter = 0x09, StackPointer = 0x0A,
        DurationTimeout = 0x0B, SavedDuration = 0x0C, SavedDac = 0x0D, FreqLow = 0x0D, FreqHigh = 0x0E,
        NoteFillTimeout = 0x0F, NoteFillMaster = 0x10, ModulationPtr = 0x11, ModulationWait = 0x13,
        ModulationSpeed = 0x14, ModulationDelta = 0x15, ModulationSteps = 0x16, ModulationVal = 0x17, Detune = 0x19,
        VolTlMask = 0x1A, PsgNoise = 0x1B, VoicePtr = 0x1C, TlPtr = 0x1E, LoopCounters = 0x20, GoSubStack = 0x2A;

    // PlaybackControl bits.
    private const int AtRest = 0x02, SfxOverride = 0x04, ModulationOn = 0x08, NoAttack = 0x10, Playing = 0x80;

    // The globals after the code.
    private const int PalModeByte = 0x0007, PalUpdTick = 0x12FE, CurDac = 0x12FF, CurSong = 0x1300, DoSfxFlag = 0x1301,
        RingSpeaker = 0x1302, GloopFlag = 0x1303, SpindashPlayingCounter = 0x1304,
        SpindashExtraFrequencyIndex = 0x1305, SpindashActiveFlag = 0x1306, Paused = 0x1307;

    // Tables in the driver (Z80 addresses).
    private const int DacDecodeTable = 0x01B3, MusicTrackOffsets = 0x01C3, SfxTrackOffsets = 0x01D3,
        VolTlMaskTable = 0x0DF1, FmDacInitBytes = 0x0916, PsgInitBytes = 0x091D, SfxPriorities = 0x0FD8,
        PsgEnvelopes = 0x1029, MasterPlaylist = 0x11F5, SpedUpTempos = 0x1214;

    // The operands the code rewrites (the instruction's address + 1 or 2).
    private const int OpDacStart = 0x0105, OpDacLength = 0x0109, OpDacDelay = 0x010C, OpDacDataStore = 0x022C,
        OpFmFreq = 0x0294, OpPsgFreq = 0x046F, OpCommandJump = 0x06D6, OpTrackStore = 0x08F7,
        OpBgmToOverride = 0x09D0, OpBgmToSfx = 0x09DB, OpIsPsg = 0x0A1E, OpVoicePtr = 0x0A27, OpFmPointer = 0x0A6E,
        OpPsgPointer = 0x0A9D, OpCoordFlag = 0x0C93, OpAlgorithm = 0x0E66, OpFmTrackOffs = 0x0F1F,
        OpPsgTrackOffs = 0x0F5C, OpDecRemaining = 0x12E9, OpDecSource = 0x12F4;
    /// <summary>Operands that are constants in this build: the SFX index and the SEGA voice's address, length, delay.</summary>
    private const int OpSfxIndex = 0x0985, OpSegaAddress = 0x0704, OpSegaLength = 0x0707, OpSegaDelay = 0x0717;

    /// <summary>Where each bankswitch macro's nine bank-bit writes start ($73 = ld (hl),e = 1, $77 = ld (hl),a = 0).</summary>
    private const int BankSwitchSfx = 0x009A, BankSwitchDac = 0x00D2, BankSwitchSega = 0x06FA,
        BankSwitchMusic1 = 0x0C6F, BankSwitchMusic2 = 0x0C7F;

    private const int MusicFirst = 0x81, MusicEnd = 0xA0, ExtraLife = 0x98, SfxFirst = 0xA0, SfxEnd2 = 0xF1,
        CommandFirst = 0xF8, PauseCommand = 0xFE, SfxRing = 0xB5, SfxRingLeft = 0xCE, SfxGloop = 0xDA, SfxSpindash = 0xE0;

    // ------------------------------------------------------------------ CPU state the port needs

    private int _bank;   // the bank register (68k address bits 15-23)
    private int _ix;     // the current track (or zAbsVar)
    /// <summary>Set by cfFadeInToPrevious: the rest of this interrupt goes straight to zUpdateDAC.</summary>
    private bool _skipToDacUpdate;
    /// <summary>
    /// Frames the Z80 is still inside zPlaySegaSound's loop, which streams the SEGA voice from within an interrupt:
    /// the interrupts meanwhile are missed. (The port finishes that interrupt's work at once.)
    /// </summary>
    public int BusyFrames { get; set; }

    /// <summary>Boot: the driver's code copied in, then zStartDAC ($0167): zClearTrackPlaybackMem.</summary>
    public void Reset()
    {
        Array.Clear(Ram);
        data.DriverImage.CopyTo(Ram, 0);
        Ram[PalModeByte] = 0;   // SoundDriverLoad: the VDP's PAL bit (NTSC)
        ClearTrackPlaybackMem();
    }

    // ------------------------------------------------------------------ memory and hardware

    private int R(int a)
    {
        a &= 0xFFFF;
        if (a < 0x2000) return Ram[a];
        if (a >= 0x8000) return data.Bank(_bank, a - 0x8000);
        return 0;
    }

    private int R16(int a) => R(a) | (R(a + 1) << 8);
    private void W(int a, int v) { if ((a & 0xFFFF) < 0x2000) Ram[a & 0xFFFF] = (byte)v; }
    private void W16(int a, int v) { W(a, v); W(a + 1, v >> 8); }

    private int V(int offset) => Ram[AbsVar + offset];
    private void SetV(int offset, int v) => Ram[AbsVar + offset] = (byte)v;
    private int T(int offset) => Ram[(_ix + offset) & 0x1FFF];
    private void SetT(int offset, int v) => Ram[(_ix + offset) & 0x1FFF] = (byte)v;
    private bool Has(int bits) => (T(PlaybackControl) & bits) != 0;
    private void SetBits(int bits) => SetT(PlaybackControl, T(PlaybackControl) | bits);
    private void ClearBits(int bits) => SetT(PlaybackControl, T(PlaybackControl) & ~bits);
    private bool IsPsg => (T(VoiceControl) & 0x80) != 0;

    /// <summary>A bankswitch macro: decode its nine bank bits from the code.</summary>
    private void BankSwitch(int sequence)
    {
        int bank = 0;
        for (int i = 0; i < 9; i++) if (Ram[sequence + i] == 0x73) bank |= 1 << i;
        _bank = bank;
    }

    private void WriteFmI(int register, int value) => chips.WriteYm(0, register & 0xFF, value & 0xFF);
    private void WriteFmII(int register, int value) => chips.WriteYm(1, register & 0xFF, value & 0xFF);
    /// <summary>zWriteFMIorII (rst $10): part II when the track's voice control has bit 2 set.</summary>
    private void WriteFm(int register, int value)
    {
        if ((T(VoiceControl) & 4) != 0) WriteFmII(register, value); else WriteFmI(register, value);
    }
    private void WritePsg(int value) => chips.WritePsg(value & 0xFF);

    // ------------------------------------------------------------------ the 68k side

    /// <summary>
    /// sndDriverInput (0x11A8, in the 68k's VBlank): hand the 68k's sound queue ($FFE0-$FFE4) to the driver's mailbox.
    /// Music goes when the driver has taken the last request; the four SFX slots go into empty mailbox slots (the
    /// fourth reads the second music slot $FFE4 and lands on VoiceTblPtr's low byte, a bug in the original).
    /// </summary>
    public void SoundDriverInput(Logic.Ram ram)
    {
        const int SoundQueue = 0xFFE0;
        if (V(QueueToPlay) == 0x80)
        {
            int music = ram.U8(SoundQueue);
            if (music != 0) ram.W8(SoundQueue, 0);
            else
            {
                music = ram.U8(SoundQueue + 4);
                if (music != 0) ram.W8(SoundQueue + 4, 0);
            }
            if (music != 0)
            {
                int d1 = (music - PauseCommand) & 0xFF;
                if (music >= PauseCommand) SetV(StopMusic, d1 + 0x7F);   // $FE pause -> $7F, $FF resume -> $80
                else SetV(QueueToPlay, music);
            }
        }
        for (int slot = 3; slot >= 0; slot--)
        {
            int sfx = ram.U8(SoundQueue + 1 + slot);
            if (sfx == 0 || V(Queue0 + slot) != 0) continue;
            ram.W8(SoundQueue + 1 + slot, 0);
            SetV(Queue0 + slot, sfx);
        }
    }

    // ------------------------------------------------------------------ zVInt ($0038)

    /// <summary>The Z80's VBlank interrupt: requests, fades, then one update of the music and the SFX.</summary>
    public void VInt()
    {
        if (BusyFrames > 0)
        {
            // Still streaming the SEGA voice; a new request ends it (the loop checks QueueToPlay).
            if (V(QueueToPlay) == 0x80) { BusyFrames--; return; }
            BusyFrames = 0;
            chips.PlayDacPcm([], 1);
        }
        _skipToDacUpdate = false;
        BankSwitchToMusic();
        Ram[DoSfxFlag] = 0;
        _ix = AbsVar;
        if (V(StopMusic) != 0)
        {
            PauseMusic();
            UpdateDac();
            return;
        }
        if (V(FadeOutCounter) != 0) UpdateFadeout();
        if (V(FadeInFlag) != 0) UpdateFadeIn();
        if ((V(Queue0) | V(Queue1) | V(Queue2)) != 0) CycleQueue();
        if (V(QueueToPlay) != 0x80) PlaySoundByIndex(V(QueueToPlay));
        if (Ram[SpindashPlayingCounter] != 0) Ram[SpindashPlayingCounter]--;

        // PAL: every sixth frame updates twice.
        if ((V(IsPalFlag) & Ram[PalModeByte]) != 0 && --Ram[PalUpdTick] == 0)
        {
            Ram[PalUpdTick] = 5;
            UpdateMusic();
        }
        if (!_skipToDacUpdate) UpdateMusic();
        if (!_skipToDacUpdate)
        {
            // The SFX tracks, carrying on from the music's last track (ix is left at PSG3).
            BankSwitch(BankSwitchSfx);
            Ram[DoSfxFlag] = 0x80;
            for (int i = 0; i < SfxFmTracks; i++)
            {
                _ix += TrackSize;
                if (Has(Playing)) FmUpdateTrack();
            }
            for (int i = 0; i < SfxPsgTracks; i++)
            {
                _ix += TrackSize;
                if (Has(Playing)) PsgUpdateTrack();
            }
        }
        UpdateDac();
    }

    /// <summary>zUpdateDAC ($00CC): start the DAC sample queued by the DAC track (zCurDAC >= $80).</summary>
    private void UpdateDac()
    {
        BankSwitch(BankSwitchDac);
        int cur = Ram[CurDac];
        if ((cur & 0x80) == 0) return;
        int index = (cur - 0x81) & 0xFF;
        Ram[CurDac] = (byte)index;
        int a = (index * 4 + (0x1233 & 0xFF)) & 0xFF;   // zDACPtrTbl's low byte
        Ram[OpDacStart] = (byte)a;
        Ram[OpDacLength] = (byte)(a + 2);
        int start = R16(Ram[OpDacStart] | (Ram[OpDacStart + 1] << 8));
        int length = R16(Ram[OpDacLength] | (Ram[OpDacLength + 1] << 8));
        int delay = Ram[OpDacDelay];
        // zWriteToDAC: each byte gives two samples, high nibble first, as deltas from zDACDecodeTbl added to an
        // accumulator that starts at $80.
        var pcm = new byte[length * 2];
        int acc = 0x80;
        for (int i = 0; i < length; i++)
        {
            int b = R(start + i);
            acc = (acc + Ram[DacDecodeTable + (b >> 4)]) & 0xFF;
            pcm[i * 2] = (byte)acc;
            acc = (acc + Ram[DacDecodeTable + (b & 0x0F)]) & 0xFF;
            pcm[i * 2 + 1] = (byte)acc;
        }
        chips.PlayDacPcm(pcm, SmpsZ80.SampleRate(delay));
    }

    /// <summary>zUpdateMusic ($0110): TempoWait, then the DAC, FM and PSG music tracks. Leaves ix at PSG3.</summary>
    private void UpdateMusic()
    {
        TempoWait();
        SetV(DacUpdating, 0xFF);
        _ix = SongDac;
        if (Has(Playing)) DacUpdateTrack();
        if (_skipToDacUpdate) return;
        SetV(DacUpdating, 0);
        for (int i = 0; i < MusicFmTracks; i++)
        {
            _ix += TrackSize;
            if (Has(Playing)) FmUpdateTrack();
        }
        for (int i = 0; i < MusicPsgTracks; i++)
        {
            _ix += TrackSize;
            if (Has(Playing)) PsgUpdateTrack();
        }
    }

    /// <summary>TempoWait ($014C): the tempo accumulator; without a carry every music track waits one more tick.</summary>
    private void TempoWait()
    {
        int sum = V(CurrentTempo) + V(TempoTimeout);
        SetV(TempoTimeout, sum);
        if (sum > 0xFF) return;
        for (int i = 0; i < MusicTracks; i++)
        {
            int a = SongDac + i * TrackSize + DurationTimeout;
            Ram[a]++;
        }
    }

    // ------------------------------------------------------------------ track updates

    /// <summary>zDACUpdateTrack ($01E3).</summary>
    private void DacUpdateTrack()
    {
        if (--Ram[_ix + DurationTimeout] != 0) return;
        int hl = T(DataPointer) | (T(DataPointer + 1) << 8);
        int a;
        while (true)
        {
            a = R(hl);
            hl = (hl + 1) & 0xFFFF;
            if (a < 0xE0) break;
            if (CoordFlag(a, ref hl) != FlagResult.Continue) return;
        }
        if (a < 0x80) SetDuration(a);
        else
        {
            SetT(SavedDac, a);
            int next = R(hl);
            if (next < 0x80)
            {
                hl = (hl + 1) & 0xFFFF;
                SetDuration(next);
            }
            else SetT(DurationTimeout, T(SavedDuration));
        }
        // zDACAfterDur.
        SetT(DataPointer, hl);
        SetT(DataPointer + 1, hl >> 8);
        if (Has(SfxOverride)) return;
        int note = T(SavedDac);
        if (note == 0x80) return;
        int low = (((note - 0x81) * 2) + (0x124F & 0xFF)) & 0xFF;   // zDACMasterPlaylist's low byte
        Ram[OpDacDataStore] = (byte)low;
        int entry = Ram[OpDacDataStore] | (Ram[OpDacDataStore + 1] << 8);
        Ram[CurDac] = (byte)R(entry);
        Ram[OpDacDelay] = (byte)R(entry + 1);
    }

    /// <summary>zFMUpdateTrack ($0237).</summary>
    private void FmUpdateTrack()
    {
        if (--Ram[_ix + DurationTimeout] != 0)
        {
            if (NoteFillExpired()) { FmNoteOff(); return; }
            if (DoModulation(out int f)) FmUpdateFreq(f);
            return;
        }
        ClearBits(NoAttack);
        if (!FmDoNext()) return;
        FmPrepareNote();
        FmNoteOn();
        if (DoModulation(out int freq)) FmUpdateFreq(freq);
    }

    /// <summary>zFMDoNext ($0258): flags, then a note and/or duration. False when a flag stopped the track.</summary>
    private bool FmDoNext()
    {
        int hl = T(DataPointer) | (T(DataPointer + 1) << 8);
        ClearBits(AtRest);
        int a;
        while (true)
        {
            a = R(hl);
            hl = (hl + 1) & 0xFFFF;
            if (a < 0xE0) break;
            var r = CoordFlag(a, ref hl);
            if (r != FlagResult.Continue) return false;
        }
        FmNoteOff();
        if (a < 0x80) SetDuration(a);
        else
        {
            FmSetFreq(a);
            int next = R(hl);
            if (next < 0x80)
            {
                hl = (hl + 1) & 0xFFFF;
                SetDuration(next);
            }
        }
        FinishTrackUpdate(hl);
        return true;
    }

    /// <summary>zFMSetFreq ($0285): the note's frequency from zFrequencies (the operand's low byte only: it wraps in the page).</summary>
    private void FmSetFreq(int note)
    {
        int a = (note - 0x80) & 0xFF;
        if (a == 0)
        {
            SetBits(AtRest);
            SetT(FreqLow, 0);
            SetT(FreqHigh, 0);
            return;
        }
        a = (a + T(Transpose)) & 0xFF;
        a = (a * 2) & 0xFF;
        a = (a + (0x0534 & 0xFF)) & 0xFF;
        Ram[OpFmFreq] = (byte)a;
        int freq = R16(Ram[OpFmFreq] | (Ram[OpFmFreq + 1] << 8));
        SetT(FreqLow, freq);
        SetT(FreqHigh, freq >> 8);
    }

    /// <summary>zSetDuration ($02A9): duration times the tempo divider (8-bit).</summary>
    private void SetDuration(int a)
    {
        int c = a;
        int b = T(TempoDivider);
        while (true)
        {
            b = (b - 1) & 0xFF;
            if (b == 0) break;
            a = (a + c) & 0xFF;
        }
        SetT(SavedDuration, a);
        SetT(DurationTimeout, a);
    }

    /// <summary>zFinishTrackUpdate ($02BA).</summary>
    private void FinishTrackUpdate(int hl)
    {
        SetT(DataPointer, hl);
        SetT(DataPointer + 1, hl >> 8);
        SetT(DurationTimeout, T(SavedDuration));
        if (Has(NoAttack)) return;
        SetT(NoteFillTimeout, T(NoteFillMaster));
        SetT(VolFlutter, 0);
        if (!Has(ModulationOn)) return;
        SetModulation(T(ModulationPtr) | (T(ModulationPtr + 1) << 8));
    }

    /// <summary>zNoteFillUpdate ($02E3): true when the note fill ran out (the track rests; the caller keys off and stops).</summary>
    private bool NoteFillExpired()
    {
        if (T(NoteFillTimeout) == 0) return false;
        SetT(NoteFillTimeout, T(NoteFillTimeout) - 1);
        if (T(NoteFillTimeout) != 0) return false;
        SetBits(AtRest);
        return true;
    }

    /// <summary>
    /// zDoModulation ($02FB): true with the modulated frequency when it changed (the original then jumps back into
    /// the caller to write it; otherwise it returns past the caller).
    /// </summary>
    private bool DoModulation(out int freq)
    {
        freq = 0;
        if (Has(AtRest) || !Has(ModulationOn)) return false;
        if (T(ModulationWait) != 0)
        {
            SetT(ModulationWait, T(ModulationWait) - 1);
            return false;
        }
        SetT(ModulationSpeed, T(ModulationSpeed) - 1);
        if (T(ModulationSpeed) != 0) return false;
        int hl = (T(ModulationPtr) | (T(ModulationPtr + 1) << 8)) + 1;
        SetT(ModulationSpeed, R(hl));
        if (T(ModulationSteps) == 0)
        {
            SetT(ModulationSteps, R(hl + 2));
            SetT(ModulationDelta, -T(ModulationDelta));
            return false;
        }
        SetT(ModulationSteps, T(ModulationSteps) - 1);
        int value = (T(ModulationVal) | (T(ModulationVal + 1) << 8)) + (sbyte)T(ModulationDelta);
        SetT(ModulationVal, value);
        SetT(ModulationVal + 1, value >> 8);
        freq = (value + (T(FreqLow) | (T(FreqHigh) << 8))) & 0xFFFF;
        return true;
    }

    /// <summary>zFMPrepareNote ($03E5).</summary>
    private void FmPrepareNote()
    {
        if (Has(AtRest)) return;
        int de = T(FreqLow) | (T(FreqHigh) << 8);
        if (de == 0) { SetBits(AtRest); return; }
        FmUpdateFreq(de);
    }

    /// <summary>zFMUpdateFreq ($03F5): frequency plus detune to $A4/$A0.</summary>
    private void FmUpdateFreq(int de)
    {
        if (Has(SfxOverride)) return;
        int hl = (de + (sbyte)T(Detune)) & 0xFFFF;
        int reg = (T(VoiceControl) & 3) + 0xA4;
        WriteFm(reg, hl >> 8);
        WriteFm(reg - 4, hl & 0xFF);
    }

    /// <summary>zPSGUpdateTrack ($0414).</summary>
    private void PsgUpdateTrack()
    {
        if (--Ram[_ix + DurationTimeout] != 0)
        {
            if (NoteFillExpired()) { PsgNoteOff(); return; }
            PsgUpdateVolFx();
            if (DoModulation(out int f)) PsgUpdateFreq(f);
            return;
        }
        ClearBits(NoAttack);
        if (!PsgDoNext()) return;
        PsgDoNoteOn();
        PsgDoVolFx();
        if (DoModulation(out int freq)) PsgUpdateFreq(freq);
    }

    /// <summary>zPSGDoNext ($0438).</summary>
    private bool PsgDoNext()
    {
        int hl = T(DataPointer) | (T(DataPointer + 1) << 8);
        ClearBits(AtRest);
        int a;
        while (true)
        {
            a = R(hl);
            hl = (hl + 1) & 0xFFFF;
            if (a < 0xE0) break;
            if (CoordFlag(a, ref hl) != FlagResult.Continue) return false;
        }
        if (a < 0x80) SetDuration(a);
        else
        {
            PsgSetFreq(a);
            int next = R(hl);
            if (next < 0x80)
            {
                hl = (hl + 1) & 0xFFFF;
                SetDuration(next);
            }
        }
        FinishTrackUpdate(hl);
        return true;
    }

    /// <summary>zPSGSetFreq ($0460).</summary>
    private void PsgSetFreq(int note)
    {
        if (note < 0x81)
        {
            SetBits(AtRest);
            SetT(FreqLow, 0xFF);
            SetT(FreqHigh, 0xFF);
            PsgNoteOff();
            return;
        }
        int a = (note - 0x81 + T(Transpose)) & 0xFF;
        a = (a * 2) & 0xFF;
        a = (a + (0x0359 & 0xFF)) & 0xFF;
        Ram[OpPsgFreq] = (byte)a;
        int freq = R16(Ram[OpPsgFreq] | (Ram[OpPsgFreq + 1] << 8));
        SetT(FreqLow, freq);
        SetT(FreqHigh, freq >> 8);
    }

    /// <summary>zPSGDoNoteOn ($0487).</summary>
    private void PsgDoNoteOn()
    {
        if ((T(FreqHigh) & 0x80) != 0) { SetBits(AtRest); return; }
        PsgUpdateFreq(T(FreqLow) | (T(FreqHigh) << 8));
    }

    /// <summary>zPSGUpdateFreq ($0493): frequency plus detune, as the two tone writes.</summary>
    private void PsgUpdateFreq(int de)
    {
        if ((T(PlaybackControl) & (AtRest | SfxOverride)) != 0) return;
        int hl = (de + (sbyte)T(Detune)) & 0xFFFF;
        int vc = T(VoiceControl);
        if (vc == 0xE0) vc = 0xC0;
        int l = hl & 0xFF, h = hl >> 8;
        WritePsg((l & 0x0F) | vc);
        // srl h / rra / srl h / rra / rra / rra: the bits of h rotate into the top of l.
        int carry = h & 1; h >>= 1;
        int a = (carry << 7) | (l >> 1); carry = l & 1;
        int c2 = h & 1; h >>= 1;
        int prev = a;
        a = (c2 << 7) | (prev >> 1); carry = prev & 1;
        prev = a;
        a = (carry << 7) | (prev >> 1); carry = prev & 1;
        prev = a;
        a = (carry << 7) | (prev >> 1);
        WritePsg(a & 0x3F);
    }

    /// <summary>zPSGUpdateVolFX ($04CA): only with a volume envelope.</summary>
    private void PsgUpdateVolFx()
    {
        if (T(VoiceIndex) == 0) return;
        PsgDoVolFx();
    }

    /// <summary>zPSGDoVolFX ($04CF): the envelope's next step added to the volume.</summary>
    private void PsgDoVolFx()
    {
        int b = T(Volume);
        int tone = T(VoiceIndex);
        if (tone != 0)
        {
            int entry = PsgEnvelopes + (((tone - 1) * 2) & 0xFF);
            int env = R16(entry);
            int step = R((env + T(VolFlutter)) & 0xFFFF);
            SetT(VolFlutter, T(VolFlutter) + 1);
            if (step == 0x80)
            {
                // zVolEnvHold: stay on the last step (no volume write this frame).
                SetT(VolFlutter, T(VolFlutter) - 1);
                return;
            }
            b = (step + b) & 0xFF;
        }
        PsgUpdateVol(b);
    }

    /// <summary>zPSGUpdateVol ($04F9).</summary>
    private void PsgUpdateVol(int b)
    {
        if ((T(PlaybackControl) & (AtRest | SfxOverride)) != 0) return;
        if (Has(NoAttack))
        {
            // zPSGCheckNoteFill: a held note stays silent once its note fill ran out.
            if (T(NoteFillMaster) != 0 && T(NoteFillTimeout) == 0) return;
        }
        int a = b >= 0x10 ? 0x0F : b;
        WritePsg(a | T(VoiceControl) | 0x10);
    }

    /// <summary>zPSGNoteOff ($0526).</summary>
    private void PsgNoteOff()
    {
        if (Has(SfxOverride)) return;
        WritePsg(T(VoiceControl) | 0x1F);
    }

    /// <summary>zPSGSilenceAll ($05F4).</summary>
    private void PsgSilenceAll()
    {
        WritePsg(0x9F);
        WritePsg(0xBF);
        WritePsg(0xDF);
        WritePsg(0xFF);
    }

    /// <summary>zFMNoteOn ($0C46).</summary>
    private void FmNoteOn()
    {
        if ((T(PlaybackControl) & (AtRest | SfxOverride)) != 0) return;
        WriteFmI(0x28, T(VoiceControl) | 0xF0);
    }

    /// <summary>zFMNoteOff ($0C56).</summary>
    private void FmNoteOff()
    {
        if ((T(PlaybackControl) & (NoAttack | SfxOverride)) != 0) return;
        WriteFmI(0x28, T(VoiceControl));
    }

    /// <summary>zFMSilenceAll ($0B36).</summary>
    private void FmSilenceAll()
    {
        for (int b = 3; b > 0; b--)
        {
            WriteFmI(0x28, b - 1);
            WriteFmI(0x28, (b - 1) | 4);
        }
        for (int reg = 0x30; reg < 0x90; reg++)
        {
            WriteFmI(reg, 0xFF);
            WriteFmII(reg, 0xFF);
        }
    }

    private void BankSwitchToMusic() => BankSwitch(V(MusicBankNumber) != 0 ? BankSwitchMusic2 : BankSwitchMusic1);

    // ------------------------------------------------------------------ pause

    /// <summary>zPauseMusic ($0600): $7F pauses (silence everything once); $80 resumes (voices rewritten).</summary>
    private void PauseMusic()
    {
        if ((V(StopMusic) & 0x80) == 0)
        {
            if (Ram[Paused] != 0) return;
            Ram[Paused] = 0xFF;
            FmSilenceAll();
            PsgSilenceAll();
            return;
        }
        int savedIx = _ix;
        SetV(StopMusic, 0);
        Ram[Paused] = 0;
        _ix = SongDac;
        ResumeTracks(1 + MusicFmTracks);
        BankSwitch(BankSwitchSfx);
        Ram[DoSfxFlag] = 0xFF;
        _ix = SfxFm3;
        ResumeTracks(SfxFmTracks);
        Ram[DoSfxFlag] = 0;
        BankSwitchToMusic();
        _ix = savedIx;
    }

    /// <summary>zResumeTrack ($064D): panning and voice back on each playing track not overridden by an SFX.</summary>
    private void ResumeTracks(int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (Has(Playing) && !Has(SfxOverride))
            {
                WriteFm((T(VoiceControl) & 3) + 0xB4, T(AmsFmsPan));
                SetVoiceCont(T(VoiceIndex));
            }
            _ix += TrackSize;
        }
    }

    // ------------------------------------------------------------------ requests

    /// <summary>zCycleQueue ($0674): take the first request from the three mailbox slots (SFX by priority).</summary>
    private void CycleQueue()
    {
        if (V(QueueToPlay) != 0x80) return;
        int hl = AbsVar + Queue0;
        int c = V(SfxPriorityVal);
        for (int b = 3; b > 0; b--)
        {
            int a = Ram[hl];
            int e = a;
            Ram[hl] = 0;
            hl++;
            if (a < MusicFirst) continue;
            if (a >= CommandFirst || a < SfxFirst)
            {
                SetV(QueueToPlay, e);
                return;
            }
            int priority = Ram[SfxPriorities + a - SfxFirst];
            if (priority >= c)
            {
                c = priority;
                SetV(QueueToPlay, e);
            }
            if ((c & 0x80) != 0) return;
            SetV(SfxPriorityVal, c);
            return;
        }
    }

    /// <summary>zPlaySoundByIndex ($06B2).</summary>
    private void PlaySoundByIndex(int a)
    {
        if (a == 0) { ClearTrackPlaybackMem(); return; }
        if (a < 0x80) return;
        SetV(QueueToPlay, 0x80);
        if (a < MusicEnd) { PlayMusic(a); return; }
        if (a < SfxEnd2) { PlaySoundCheckRing(a); return; }
        if (a < CommandFirst || a >= PauseCommand) return;
        Ram[OpCommandJump] = (byte)((a - CommandFirst) * 4);
        switch (a)
        {
            case 0xF8: StopSoundEffects(); break;
            case 0xF9: FadeOutMusic(); break;
            case 0xFA: PlaySegaSound(); break;
            case 0xFB: SpeedUpMusic(); break;
            case 0xFC: SlowDownMusic(); break;
            case 0xFD: SetV(StopMusic, 0); ClearTrackPlaybackMem(); break;   // zStopSoundAndMusic
        }
    }

    /// <summary>zPlaySegaSound ($06EF): the SEGA voice, streamed to the DAC inside the interrupt.</summary>
    private void PlaySegaSound()
    {
        WriteFmI(0x2B, 0x80);
        BankSwitch(BankSwitchSega);
        int start = Ram[OpSegaAddress] | (Ram[OpSegaAddress + 1] << 8);
        int pairs = Ram[OpSegaLength] | (Ram[OpSegaLength + 1] << 8);
        var pcm = new byte[pairs * 2];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (byte)R(start + i);
        // 152 cycles per two samples with the delay loop at 1, 26 more per extra step.
        double rate = 2 * 3579545.0 / (152 + 26 * (Ram[OpSegaDelay] - 1));
        chips.PlayDacPcm(pcm, rate);
        BusyFrames = (int)Math.Ceiling(pcm.Length / rate * 60);
        // .stop: back to the music bank, the DAC as the song wants it.
        BankSwitchToMusic();
        WriteFmI(0x2B, V(DacEnabled));
    }

    /// <summary>zPlayMusic ($073D).</summary>
    private void PlayMusic(int id)
    {
        StopSoundEffects();
        Ram[CurSong] = (byte)id;
        if (id == ExtraLife)
        {
            if (V(OneUpPlaying) == 0)
            {
                for (int i = 0; i < MusicTracks; i++) Ram[SongDac + i * TrackSize] &= unchecked((byte)~SfxOverride);
                for (int i = 0; i < SfxFmTracks + SfxPsgTracks; i++) Ram[SfxFm3 + i * TrackSize] &= 0x7F;
                Array.Copy(Ram, AbsVar, Ram, SaveVar, SaveLength);
                SetV(OneUpPlaying, 0x80);
                SetV(SfxPriorityVal, 0);
            }
        }
        else
        {
            SetV(OneUpPlaying, 0);
            SetV(FadeInCounter, 0);
            SetV(FadeOutCounter, 0);
        }
        BgmLoad();
    }

    /// <summary>zBGMLoad ($078E): find the song (decompressing it to $1380 if needed) and set up its tracks.</summary>
    private void BgmLoad()
    {
        InitMusicPlayback();
        int index = (Ram[CurSong] - MusicFirst) & 0xFF;
        SetV(TempoTurbo, Ram[SpedUpTempos + index]);
        int flags = Ram[MasterPlaylist + index];
        SetV(MusicBankNumber, flags & 0x80);
        SetV(IsPalFlag, (flags & 0x40) == 0 ? 0xFF : 0x00);
        bool uncompressed = (flags & 0x20) != 0;
        int pointer = 0x8000 + ((flags & 0x1F) * 2);
        BankSwitchToMusic();
        int de = R16(pointer);
        if (!uncompressed)
        {
            SaxmanDecompress(de);
            de = MusicData;
        }
        int ix = de;
        SetV(VoiceTblPtr, R(ix));
        SetV(VoiceTblPtr + 1, R(ix + 1));
        int tempo = R(ix + 5);
        SetV(TempoMod, tempo);
        if (V(SpeedUpFlag) != 0) tempo = V(TempoTurbo);
        SetV(CurrentTempo, tempo);
        SetV(TempoTimeout, tempo);
        Ram[PalUpdTick] = 5;
        int hl = ix + 6;
        int fmCount = R(ix + 2);
        if (fmCount != 0)
        {
            int divider = R(ix + 4);
            int init = FmDacInitBytes;
            int track = SongDac;
            for (int i = 0; i < fmCount; i++)
            {
                Ram[track + PlaybackControl] = Playing | AtRest;
                Ram[track + VoiceControl] = Ram[init++];
                Ram[track + TempoDivider] = (byte)divider;
                Ram[track + StackPointer] = GoSubStack;
                Ram[track + AmsFmsPan] = 0xC0;
                Ram[track + DurationTimeout] = 1;
                for (int k = 0; k < 4; k++) Ram[(track + DataPointer + k) & 0x1FFF] = (byte)R(hl++);
                track += TrackSize;
            }
            int dacEnable;
            if (fmCount == 7) dacEnable = 0;
            else
            {
                WriteFmI(0x28, 6);
                for (int reg = 0x42; reg < 0x52; reg += 4) WriteFmII(reg, 0xFF);
                WriteFmII(0xB6, 0xC0);
                dacEnable = 0x80;
            }
            SetV(DacEnabled, dacEnable);
            WriteFmI(0x2B, dacEnable);
        }
        // zInitBGMPSG.
        int psgCount = R(ix + 3);
        if (psgCount != 0)
        {
            int divider = R(ix + 4);
            int init = PsgInitBytes;
            int track = SongPsg1;
            for (int i = 0; i < psgCount; i++)
            {
                Ram[track + PlaybackControl] = Playing | AtRest;
                Ram[track + VoiceControl] = Ram[init++];
                Ram[track + TempoDivider] = (byte)divider;
                Ram[track + StackPointer] = GoSubStack;
                Ram[track + DurationTimeout] = 1;
                for (int k = 0; k < 4; k++) Ram[(track + DataPointer + k) & 0x1FFF] = (byte)R(hl++);
                hl++;
                Ram[track + VoiceIndex] = (byte)R(hl++);
                track += TrackSize;
            }
        }
        InitSfx();
    }

    /// <summary>zInitSFX ($08D0): (the original clears, not sets, each playing SFX's music track override bit), then keys off.</summary>
    private void InitSfx()
    {
        _ix = SfxFm3;
        for (int i = 0; i < SfxFmTracks + SfxPsgTracks; i++, _ix += TrackSize)
        {
            if (!Has(Playing)) continue;
            int a = T(VoiceControl);
            int index = (a & 0x80) != 0 ? (a >> 4) & 0x0F : ((a - 2) * 2) & 0xFF;
            Ram[OpTrackStore] = (byte)(index + (MusicTrackOffsets & 0xFF));
            int music = R16(Ram[OpTrackStore] | (Ram[OpTrackStore + 1] << 8));
            W(music, R(music) & ~SfxOverride);
        }
        _ix = SongFm1;
        for (int i = 0; i < MusicFmTracks; i++, _ix += TrackSize) FmNoteOff();
        for (int i = 0; i < MusicPsgTracks; i++, _ix += TrackSize) PsgNoteOff();
    }

    /// <summary>zPlaySound_CheckRing ($0920): the ring's left/right alternation, the gloop's every-other, the spindash's pitch.</summary>
    private void PlaySoundCheckRing(int c)
    {
        if ((V(OneUpPlaying) | V(FadeInFlag)) != 0)
        {
            SetV(SfxPriorityVal, 0);   // zKillSFXPrio
            return;
        }
        Ram[SpindashActiveFlag] = 0;
        if (c == SfxRing)
        {
            int a = Ram[RingSpeaker];
            if (a == 0) c = SfxRingLeft;
            Ram[RingSpeaker] = (byte)~a;
            PlaySound(c);
            return;
        }
        if (c == SfxGloop)
        {
            Ram[GloopFlag] = (byte)~Ram[GloopFlag];
            if (Ram[GloopFlag] == 0) return;
            PlaySound(c);
            return;
        }
        if (c == SfxSpindash)
        {
            int index = Ram[SpindashPlayingCounter] != 0 ? Ram[SpindashExtraFrequencyIndex] : 0xFF;
            index = (index + 1) & 0xFF;
            if (index < 0x0C) Ram[SpindashExtraFrequencyIndex] = (byte)index;
            Ram[SpindashPlayingCounter] = 0x3C;
            Ram[SpindashActiveFlag] = 0xFF;
        }
        PlaySound(c);
    }

    /// <summary>zPlaySound ($0975): set up the SFX's tracks, each overriding the music track on its channel.</summary>
    private void PlaySound(int c)
    {
        BankSwitch(BankSwitchSfx);
        int table = Ram[OpSfxIndex] | (Ram[OpSfxIndex + 1] << 8);
        int hl = R16(table + (((c - SfxFirst) * 2) & 0xFF));
        int voices = R16(hl);
        hl += 2;
        Ram[OpVoicePtr] = (byte)voices;
        Ram[OpVoicePtr + 1] = (byte)(voices >> 8);
        int divider = R(hl++);
        int count = R(hl++);
        do
        {
            Ram[OpIsPsg] = 0;
            int entry = hl;
            int a = R(hl + 1);
            int index;
            if ((a & 0x80) != 0)
            {
                Ram[OpIsPsg] = (byte)a;
                if (a == 0xC0)
                {
                    WritePsg(a | 0x1F);
                    WritePsg((a | 0x1F) ^ 0x20);
                }
                index = (a >> 4) & 0x0F;
            }
            else index = ((a - 2) * 2) & 0xFF;
            Ram[OpBgmToOverride] = (byte)(index + (MusicTrackOffsets & 0xFF));
            int music = R16(Ram[OpBgmToOverride] | (Ram[OpBgmToOverride + 1] << 8));
            W(music, R(music) | SfxOverride);
            Ram[OpBgmToSfx] = (byte)(Ram[OpBgmToOverride] + (SfxTrackOffsets - MusicTrackOffsets));
            _ix = R16(Ram[OpBgmToSfx] | (Ram[OpBgmToSfx + 1] << 8));
            for (int k = 0; k < TrackSize; k++) Ram[(_ix + k) & 0x1FFF] = 0;
            hl = entry;
            SetT(PlaybackControl, R(hl++));
            SetT(VoiceControl, R(hl++));
            SetT(TempoDivider, divider);
            SetT(DurationTimeout, 1);
            SetT(StackPointer, GoSubStack);
            SetT(DataPointer, R(hl++));
            SetT(DataPointer + 1, R(hl++));
            int transpose = R(hl++);
            if (Ram[SpindashActiveFlag] != 0) transpose = (transpose + Ram[SpindashExtraFrequencyIndex]) & 0xFF;
            SetT(Transpose, transpose);
            SetT(Volume, R(hl++));
            if (Ram[OpIsPsg] == 0)
            {
                SetT(AmsFmsPan, 0xC0);
                SetT(VoicePtr, Ram[OpVoicePtr]);
                SetT(VoicePtr + 1, Ram[OpVoicePtr + 1]);
            }
            count = (count - 1) & 0xFF;
        } while (count != 0);
        BankSwitchToMusic();
    }

    /// <summary>zStopSoundEffects ($0A3C): stop the SFX tracks and give their channels back to the music.</summary>
    private void StopSoundEffects()
    {
        SetV(SfxPriorityVal, 0);
        int sfx = SfxFm3;
        for (int i = 0; i < SfxFmTracks + SfxPsgTracks; i++, sfx += TrackSize)
        {
            _ix = sfx;
            if (!Has(Playing)) continue;
            ClearBits(Playing | NoAttack);
            int a = T(VoiceControl);
            if ((a & 0x80) == 0)
            {
                FmNoteOff();
                Ram[OpFmPointer] = (byte)((((a - 2) * 2) & 0xFF) + (MusicTrackOffsets & 0xFF));
                _ix = R16(Ram[OpFmPointer] | (Ram[OpFmPointer + 1] << 8));
                if (Has(SfxOverride))
                {
                    ClearBits(SfxOverride);
                    SetBits(AtRest);
                    SetVoiceMusic(T(VoiceIndex));
                }
            }
            else
            {
                PsgNoteOff();
                Ram[OpPsgPointer] = (byte)(((a >> 4) & 0x0F) + (MusicTrackOffsets & 0xFF));
                _ix = R16(Ram[OpPsgPointer] | (Ram[OpPsgPointer + 1] << 8));
                ClearBits(SfxOverride);
                SetBits(AtRest);
                if (T(VoiceControl) == 0xE0) WritePsg(T(PsgNoise));
            }
        }
        _ix = sfx;
    }

    /// <summary>zFadeOutMusic ($0ABF).</summary>
    private void FadeOutMusic()
    {
        SetV(FadeOutDelay, 3);
        SetV(FadeOutCounter, 0x28);
        Ram[SongDac + PlaybackControl] = 0;
        SetV(SpeedUpFlag, 0);
    }

    /// <summary>zUpdateFadeout ($0AD1): every fourth frame the music gets one step quieter; at the end, all stops.</summary>
    private void UpdateFadeout()
    {
        if (V(FadeOutDelay) != 0)
        {
            SetV(FadeOutDelay, V(FadeOutDelay) - 1);
            return;
        }
        SetV(FadeOutCounter, V(FadeOutCounter) - 1);
        if (V(FadeOutCounter) == 0) { ClearTrackPlaybackMem(); return; }
        SetV(FadeOutDelay, 3);
        int saved = _ix;
        _ix = SongFm1;
        for (int i = 0; i < MusicFmTracks; i++, _ix += TrackSize)
        {
            if (!Has(Playing)) continue;
            SetT(Volume, T(Volume) + 1);
            if ((T(Volume) & 0x80) != 0) { ClearBits(Playing); continue; }
            SetChanVol();
        }
        for (int i = 0; i < MusicPsgTracks; i++, _ix += TrackSize)
        {
            if (!Has(Playing)) continue;
            SetT(Volume, T(Volume) + 1);
            if (T(Volume) > 0x10) { ClearBits(Playing); continue; }
            PsgUpdateVol(T(Volume));
        }
        _ix = saved;
    }

    /// <summary>zClearTrackPlaybackMem ($0B52): everything stops and the mailbox is cleared.</summary>
    private void ClearTrackPlaybackMem()
    {
        WriteFmI(0x2B, 0x80);
        SetV(DacEnabled, 0x80);
        WriteFmI(0x27, 0);
        Array.Clear(Ram, AbsVar, SfxEnd - AbsVar);
        SetV(QueueToPlay, 0x80);
        FmSilenceAll();
        PsgSilenceAll();
    }

    /// <summary>zInitMusicPlayback ($0B78): clear the music tracks, keeping a few flags (not Queue2, a bug kept).</summary>
    private void InitMusicPlayback()
    {
        _ix = AbsVar;
        int priority = V(SfxPriorityVal), oneUp = V(OneUpPlaying), speedUp = V(SpeedUpFlag), fadeIn = V(FadeInCounter),
            q0 = V(Queue0), q1 = V(Queue1);
        Array.Clear(Ram, AbsVar, SongEnd - AbsVar);
        SetV(Queue0, q0);
        SetV(Queue1, q1);
        SetV(SpeedUpFlag, speedUp);
        SetV(FadeInCounter, fadeIn);
        SetV(SfxPriorityVal, priority);
        SetV(OneUpPlaying, oneUp);
        SetV(QueueToPlay, 0x80);
        FmSilenceAll();
        PsgSilenceAll();
    }

    /// <summary>zSpeedUpMusic ($0BBE) / zSlowDownMusic ($0BCB): into the saved variables while the 1-up jingle plays.</summary>
    private void SpeedUpMusic() => SetTempo(0x80, V(TempoTurbo));
    private void SlowDownMusic() => SetTempo(0, V(TempoMod));

    private void SetTempo(int flag, int tempo)
    {
        int vars = V(OneUpPlaying) != 0 ? SaveVar : AbsVar;
        Ram[vars + CurrentTempo] = (byte)tempo;
        Ram[vars + SpeedUpFlag] = (byte)flag;
    }

    /// <summary>zUpdateFadeIn ($0BE8): after the 1-up jingle, the music comes back one step louder every third frame.</summary>
    private void UpdateFadeIn()
    {
        if (V(FadeInDelay) != 0)
        {
            SetV(FadeInDelay, V(FadeInDelay) - 1);
            return;
        }
        if (V(FadeInCounter) == 0)
        {
            Ram[SongDac + PlaybackControl] &= unchecked((byte)~SfxOverride);
            SetV(FadeInFlag, 0);
            return;
        }
        SetV(FadeInCounter, V(FadeInCounter) - 1);
        SetV(FadeInDelay, 2);
        int saved = _ix;
        _ix = SongFm1;
        for (int i = 0; i < MusicFmTracks; i++, _ix += TrackSize)
        {
            if (!Has(Playing)) continue;
            SetT(Volume, T(Volume) - 1);
            SetChanVol();
        }
        for (int i = 0; i < MusicPsgTracks; i++, _ix += TrackSize)
        {
            if (!Has(Playing)) continue;
            SetT(Volume, T(Volume) - 1);
            PsgUpdateVol(T(Volume));
        }
        _ix = saved;
    }

    // ------------------------------------------------------------------ voices and volume

    /// <summary>cfSetVoiceCont ($0E12): music voices from the song's table, SFX voices from the SFX's own.</summary>
    private void SetVoiceCont(int voice)
    {
        if (Ram[DoSfxFlag] == 0) SetVoiceMusic(voice);
        else SetVoice(voice, T(VoicePtr) | (T(VoicePtr + 1) << 8));
    }

    /// <summary>zSetVoiceMusic ($0E21).</summary>
    private void SetVoiceMusic(int voice) => SetVoice(voice, V(VoiceTblPtr) | (V(VoiceTblPtr + 1) << 8));

    /// <summary>zSetVoice ($0E24): the 25-byte voice to the channel's operator registers, then the volume-adjusted TLs.</summary>
    private void SetVoice(int voice, int table)
    {
        int doubled = (voice * 2) & 0xFF;   // the multiply by 25 overflows its first doubling
        int hl = (table + doubled * 12 + voice) & 0xFFFF;
        int algorithm = R(hl++);
        Ram[OpAlgorithm] = (byte)algorithm;
        int reg = (T(VoiceControl) & 3) + 0xB0;
        WriteFm(reg, algorithm);
        reg -= 0x80;
        for (int i = 0; i < 4; i++, reg += 4) WriteFm(reg, R(hl++));
        int tlRegister = reg;
        reg += 0x10;
        for (int i = 0; i < 16; i++, reg += 4) WriteFm(reg, R(hl++));
        reg += 0x24;
        WriteFm(reg, T(AmsFmsPan));
        SetT(TlPtr, hl);
        SetT(TlPtr + 1, hl >> 8);
        int mask = Ram[VolTlMaskTable + (Ram[OpAlgorithm] & 7)];
        SetT(VolTlMask, mask);
        SetFmTls(tlRegister, hl, mask, T(Volume));
    }

    /// <summary>zSetFMTLs ($0E77): the four TLs, the volume added to the carrier operators (by the algorithm's mask).</summary>
    private void SetFmTls(int reg, int hl, int mask, int volume)
    {
        for (int i = 0; i < 4; i++, reg += 4)
        {
            int c = R(hl++);
            bool carrier = (mask & 1) != 0;
            mask >>= 1;
            if (carrier) c = (volume + c) & 0xFF;
            WriteFm(reg, c);
        }
    }

    /// <summary>zSetChanVol ($0E8A).</summary>
    private void SetChanVol()
    {
        if (IsPsg || Has(SfxOverride)) return;
        int volume = T(Volume);
        if ((volume & 0x80) != 0) return;
        SetFmTls((T(VoiceControl) & 3) + 0x40, T(TlPtr) | (T(TlPtr + 1) << 8), T(VolTlMask), volume);
    }

    /// <summary>zSetModulation ($0EBB): wait, speed, delta and half the step count from the flag's parameters.</summary>
    private void SetModulation(int hl)
    {
        SetT(ModulationWait, R(hl));
        SetT(ModulationSpeed, R(hl + 1));
        SetT(ModulationDelta, R(hl + 2));
        SetT(ModulationSteps, R(hl + 3) >> 1);
        if (Has(NoAttack)) return;
        SetT(ModulationVal, 0);
        SetT(ModulationVal + 1, 0);
    }

    // ------------------------------------------------------------------ coordination flags

    private enum FlagResult { Continue, StopTrack }

    /// <summary>zCoordFlag ($0C89): flags $E0-$F9. The first parameter byte has already been read (and passed).</summary>
    private FlagResult CoordFlag(int flag, ref int hl)
    {
        Ram[OpCoordFlag] = (byte)(((flag - 0xE0) * 4) & 0xFF);
        int a = R(hl);
        hl = (hl + 1) & 0xFFFF;
        switch (flag)
        {
            case 0xE0:   // cfPanningAMSFMS
            {
                if (IsPsg || Has(SfxOverride)) break;
                int pan = (T(AmsFmsPan) & 0x37) | a;
                SetT(AmsFmsPan, pan);
                WriteFm((T(VoiceControl) & 3) + 0xB4, pan);
                break;
            }
            case 0xE1: SetT(Detune, a); break;
            case 0xE2: SetV(Communication, a); break;
            case 0xE3:   // cfJumpReturn
            {
                int sp = T(StackPointer);
                hl = Ram[(_ix + sp) & 0x1FFF] | (Ram[(_ix + sp + 1) & 0x1FFF] << 8);
                SetT(StackPointer, sp + 2);
                break;
            }
            case 0xE4:
                FadeInToPrevious();
                return FlagResult.StopTrack;
            case 0xE5: SetT(TempoDivider, a); break;
            case 0xE6:   // cfChangeFMVolume
                SetT(Volume, T(Volume) + a);
                SetChanVol();
                break;
            case 0xE7:   // cfPreventAttack
                SetBits(NoAttack);
                hl = (hl - 1) & 0xFFFF;
                break;
            case 0xE8:
                SetT(NoteFillTimeout, a);
                SetT(NoteFillMaster, a);
                break;
            case 0xE9: SetT(Transpose, T(Transpose) + a); break;
            case 0xEA: SetV(CurrentTempo, a); break;
            case 0xEB:   // cfSetTempoMod: every music track's divider
                for (int i = 0; i < MusicTracks; i++) Ram[SongDac + i * TrackSize + TempoDivider] = (byte)a;
                break;
            case 0xEC: SetT(Volume, T(Volume) + a); break;
            case 0xED: break;   // cfClearPush: does nothing (and keeps the byte it read)
            case 0xEE: hl = (hl - 1) & 0xFFFF; break;
            case 0xEF:   // cfSetVoice
                SetT(VoiceIndex, a);
                if (Has(SfxOverride)) break;
                SetVoiceCont(a);
                break;
            case 0xF0:   // cfModulation
                SetBits(ModulationOn);
                hl = (hl - 1) & 0xFFFF;
                SetT(ModulationPtr, hl);
                SetT(ModulationPtr + 1, hl >> 8);
                SetModulation(hl);
                hl = (hl + 4) & 0xFFFF;
                break;
            case 0xF1:
                hl = (hl - 1) & 0xFFFF;
                SetBits(ModulationOn);
                break;
            case 0xF2:
                StopTrack();
                return FlagResult.StopTrack;
            case 0xF3:   // cfSetPSGNoise
                SetT(VoiceControl, 0xE0);
                SetT(PsgNoise, a);
                if (!Has(SfxOverride)) WritePsg(a);
                break;
            case 0xF4:
                hl = (hl - 1) & 0xFFFF;
                ClearBits(ModulationOn);
                break;
            case 0xF5: SetT(VoiceIndex, a); break;
            case 0xF6: hl = a | (R(hl) << 8); break;   // cfJumpTo
            case 0xF7:   // cfRepeatAtPos
            {
                int count = R(hl);
                hl = (hl + 1) & 0xFFFF;
                int counter = (_ix + ((a + LoopCounters) & 0xFF)) & 0x1FFF;
                if (Ram[counter] == 0) Ram[counter] = (byte)count;
                Ram[counter]--;
                if (Ram[counter] == 0) hl = (hl + 2) & 0xFFFF;
                else hl = R16(hl);
                break;
            }
            case 0xF8:   // cfJumpToGosub
            {
                int sp = (T(StackPointer) - 2) & 0xFF;
                SetT(StackPointer, sp);
                int target = a | (R(hl) << 8);
                hl = (hl + 1) & 0xFFFF;
                int slot = (_ix + sp) & 0xFFFF;
                W(slot, hl);
                W(slot + 1, hl >> 8);
                hl = target;
                break;
            }
            case 0xF9:   // cfOpF9
                WriteFmI(0x88, 0x0F);
                WriteFmI(0x8C, 0x0F);
                hl = (hl - 1) & 0xFFFF;
                break;
        }
        return FlagResult.Continue;
    }

    /// <summary>cfStopTrack ($0EE4): key off; an SFX hands its channel back to the music track (restoring its voice).</summary>
    private void StopTrack()
    {
        ClearBits(Playing | NoAttack);
        if (IsPsg) PsgNoteOff();
        else
        {
            if ((V(DacUpdating) & 0x80) != 0) return;   // the DAC track
            FmNoteOff();
        }
        if ((Ram[DoSfxFlag] & 0x80) == 0) return;   // a music track
        SetV(SfxPriorityVal, 0);
        int a = T(VoiceControl);
        int saved = _ix;
        if ((a & 0x80) != 0)
        {
            Ram[OpPsgTrackOffs] = (byte)(((a >> 4) & 0x0F) + (MusicTrackOffsets & 0xFF));
            _ix = R16(Ram[OpPsgTrackOffs] | (Ram[OpPsgTrackOffs + 1] << 8));
            ClearBits(SfxOverride);
            SetBits(AtRest);
            if (T(VoiceControl) == 0xE0) WritePsg(T(PsgNoise));
        }
        else
        {
            Ram[OpFmTrackOffs] = (byte)((((a - 2) * 2) & 0xFF) + (MusicTrackOffsets & 0xFF));
            _ix = R16(Ram[OpFmTrackOffs] | (Ram[OpFmTrackOffs + 1] << 8));
            if (Has(SfxOverride))
            {
                BankSwitchToMusic();
                ClearBits(SfxOverride);
                SetBits(AtRest);
                SetVoiceMusic(T(VoiceIndex));
                BankSwitch(BankSwitchSfx);
            }
        }
        _ix = saved;
    }

    /// <summary>
    /// cfFadeInToPrevious ($0D35, the 1-up jingle's end): restore the saved music and fade it in. The original then
    /// drops this interrupt's remaining track updates and jumps to zUpdateDAC.
    /// </summary>
    private void FadeInToPrevious()
    {
        Array.Copy(Ram, SaveVar, Ram, AbsVar, SaveLength);
        BankSwitchToMusic();
        Ram[SongDac + PlaybackControl] |= SfxOverride;
        int c = (0x28 - V(FadeInCounter)) & 0xFF;
        _ix = SongFm1;
        for (int i = 0; i < MusicFmTracks; i++, _ix += TrackSize)
        {
            if (!Has(Playing)) continue;
            SetBits(AtRest);
            SetT(Volume, T(Volume) + c);
            if (Has(SfxOverride)) continue;
            SetVoiceMusic(T(VoiceIndex));
        }
        for (int i = 0; i < MusicPsgTracks; i++, _ix += TrackSize)
        {
            if (!Has(Playing)) continue;
            SetBits(AtRest);
            PsgNoteOff();
            SetT(Volume, T(Volume) + c);
        }
        SetV(FadeInFlag, 0x80);
        SetV(FadeInCounter, 0x28);
        SetV(OneUpPlaying, 0);
        WriteFmI(0x2B, V(DacEnabled));
        _skipToDacUpdate = true;
    }

    // ------------------------------------------------------------------ Saxman

    /// <summary>
    /// zSaxmanDec ($1271): a song from the ROM window (a 2-byte size, then the stream) to $1380. Back-references are
    /// offsets from $1380 (+$12, 12 bits); one before the start of the output fills zeros.
    /// </summary>
    private void SaxmanDecompress(int source)
    {
        int size = R16(source);
        source += 2;
        int remaining = size + 1;
        int de = MusicData;   // the output
        int written = 0;      // the shadow 'de': bytes written so far
        int descriptor = 0, mask = 0;

        int NextByte()
        {
            remaining--;
            W16(OpDecRemaining, remaining);
            W16(OpDecSource, source + 1);
            return R(source++);
        }

        while (true)
        {
            descriptor >>= 1;
            mask >>= 1;
            if ((mask & 1) == 0)
            {
                if (remaining - 1 == 0) { W16(OpDecRemaining, 0); return; }
                descriptor = NextByte();
                mask = 0xFF;
            }
            if ((descriptor & 1) != 0)
            {
                if (remaining - 1 == 0) { W16(OpDecRemaining, 0); return; }
                W(de++, NextByte());
                written++;
                continue;
            }
            if (remaining - 1 == 0) { W16(OpDecRemaining, 0); return; }
            int c = NextByte();
            if (remaining - 1 == 0) { W16(OpDecRemaining, 0); return; }
            int b = NextByte();
            int count = (b & 0x0F) + 3;
            int offset = (((b >> 4) & 0x0F) << 8 | c) + 0x12 & 0x0FFF;
            int start = written;
            written += count;
            if (start - offset < 0)
            {
                for (int i = 0; i < count; i++) W(de++, 0);
                continue;
            }
            int from = MusicData + offset;
            for (int i = 0; i < count; i++) W(de++, R(from++));
        }
    }
}
