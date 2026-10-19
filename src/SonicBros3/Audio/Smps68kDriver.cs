using System.Text.Json;
using SonicBros3.Logic;

namespace SonicBros3.Audio;

/// <summary>Where the sound driver's chip writes go (the YM2612, the PSG and the Z80 DAC driver).</summary>
public interface ISoundChips
{
    /// <summary>A YM2612 register write; part 0 = $A04000/1, part 1 = $A04002/3.</summary>
    void WriteYm(int part, int register, int value);
    /// <summary>A byte written to the PSG port ($C00011).</summary>
    void WritePsg(int value);
    /// <summary>The Z80 DAC driver's request byte ($A01FFF): a sample ID $81-$83.</summary>
    void PlayDac(int sample);
    /// <summary>Sample $83's pitch byte in the Z80 driver ($A000EA).</summary>
    void SetDacPitch(int pitch);
    /// <summary>The SEGA voice (command $FA): the original streams it to the DAC in a 68k busy loop.</summary>
    void PlaySegaPcm();
    /// <summary>
    /// Sonic 2's Z80 driver: unsigned 8-bit samples it writes to the DAC (register $2A) from its busy loop, at
    /// <paramref name="rate"/> Hz, replacing any sample still playing (an empty one stops it).
    /// </summary>
    void PlayDacPcm(byte[] samples, double rate) { }
}

public sealed class NoChips : ISoundChips
{
    public void WriteYm(int part, int register, int value) { }
    public void WritePsg(int value) { }
    public void PlayDac(int sample) { }
    public void SetDacPitch(int pitch) { }
    public void PlaySegaPcm() { }
}

/// <summary>The 68k driver's data, read at the original ROM addresses (extracted/audio/smps68k/player.json).</summary>
public sealed class Smps68kData
{
    private readonly List<(int Base, byte[] Data)> _segments = [];
    public ushort[] FmFrequencies { get; }
    public ushort[] PsgFrequencies { get; }
    public byte[] DacPitches { get; }
    public byte[] CarrierMasks { get; }
    public byte[] VoiceRegisters { get; }
    public byte[] LevelMusic { get; }
    public byte[] LevelMusic2P { get; }

    /// <param name="extraSongs">
    /// Optional JSON listing songs to add ({"songs": [{"id", "address", "file"}]}): each one's bytes are mapped at its
    /// address and the music pointer table's entry for its id points there (the SMB3 Easter egg's song).
    /// </param>
    public Smps68kData(string dataDir, string? extraSongs = null)
    {
        string dir = Path.Combine(dataDir, "audio", "smps68k");
        using var doc = JsonDocument.Parse(S2Port.Extraction.DataFile.ReadAllText(Path.Combine(dir, "player.json")));
        var r = doc.RootElement;
        foreach (var s in r.GetProperty("segments").EnumerateArray())
            _segments.Add((s.GetProperty("baseAddress").GetInt32(), S2Port.Extraction.DataFile.ReadAllBytes(Path.Combine(dir, s.GetProperty("file").GetString()!))));
        byte[] Bytes(string name) => r.GetProperty(name).EnumerateArray().Select(v => (byte)v.GetInt32()).ToArray();
        ushort[] Words(string name) => r.GetProperty(name).EnumerateArray().Select(v => (ushort)v.GetInt32()).ToArray();
        FmFrequencies = Words("fmFrequencies");
        PsgFrequencies = Words("psgFrequencies");
        DacPitches = Bytes("dacPitches");
        CarrierMasks = Bytes("carrierMasks");
        VoiceRegisters = Bytes("voiceRegisters");
        LevelMusic = Bytes("levelMusic");
        LevelMusic2P = Bytes("levelMusic2P");
        if (extraSongs is not null && S2Port.Extraction.DataFile.Exists(extraSongs))
        {
            using var extra = JsonDocument.Parse(S2Port.Extraction.DataFile.ReadAllText(extraSongs));
            foreach (var song in extra.RootElement.GetProperty("songs").EnumerateArray())
            {
                int address = song.GetProperty("address").GetInt32();
                _segments.Add((address, S2Port.Extraction.DataFile.ReadAllBytes(Path.Combine(Path.GetDirectoryName(extraSongs)!, song.GetProperty("file").GetString()!))));
                int entry = U32(MusicIndexPointer) + (song.GetProperty("id").GetInt32() - 0x81) * 4;
                for (int i = 0; i < 4; i++) Patch(entry + i, (byte)(address >> (24 - i * 8)));
            }
        }
    }

    /// <summary>The zone music list (0x4BEE; the 2-player one at 0x4C0C) without loading the driver's data.</summary>
    public static byte[] LoadLevelMusic(string dataDir, bool twoPlayer)
    {
        using var doc = JsonDocument.Parse(S2Port.Extraction.DataFile.ReadAllText(Path.Combine(dataDir, "audio", "smps68k", "player.json")));
        return doc.RootElement.GetProperty(twoPlayer ? "levelMusic2P" : "levelMusic").EnumerateArray().Select(v => (byte)v.GetInt32()).ToArray();
    }

    /// <summary>Where the driver finds its music pointer table (0x12FFF4).</summary>
    private const int MusicIndexPointer = 0x12FFF4;

    private void Patch(int address, byte value)
    {
        for (int s = 0; s < _segments.Count; s++)
        {
            var (b, d) = _segments[s];
            if (address < b || address >= b + d.Length) continue;
            d[address - b] = value;   // this instance's own copy, read from the extracted files
            return;
        }
    }

    /// <summary>A byte at a ROM address in the driver's data block or the sound data (0 elsewhere).</summary>
    public byte U8(int address)
    {
        address &= 0xFFFFFF;
        foreach (var (b, d) in _segments)
            if (address >= b && address < b + d.Length) return d[address - b];
        return 0;
    }

    public int U16(int address) => (U8(address) << 8) | U8(address + 1);
    public int U32(int address) => (U16(address) << 16) | U16(address + 2);
}

/// <summary>
/// The hack's Sonic 1-style 68k SMPS driver (update routine 0x1301DC, run every VBlank when $FFE5 is set), ported
/// routine by routine. Its state lives in RAM at $F000 exactly as in the original (validated against MAME), and its
/// YM2612 / PSG / DAC writes go to an <see cref="ISoundChips"/>.
///
/// RAM: $F000 globals, tracks of $30 bytes: music DAC $40, FM1-6 $70-$160, PSG1-3 $190-$1F0; SFX FM $220-$280,
/// PSG $2B0-$310; special SFX FM $340, PSG $370; the music saved for the 1-up jingle at $3A0-$5BF.
/// Several routines of the original return through the caller's caller (addq.w #4/#8,sp); here they return a flag.
/// </summary>
public sealed class Smps68kDriver(Ram ram, Smps68kData data, ISoundChips chips)
{
    public const int Base = 0xF000;
    private const int A6 = Base;
    private const int TrackSize = 0x30;
    private const int MusicDac = A6 + 0x40, MusicFm1 = A6 + 0x70, MusicPsg1 = A6 + 0x190;
    private const int SfxFm1 = A6 + 0x220, SpecialFm = A6 + 0x340, SpecialPsg = A6 + 0x370, Saved = A6 + 0x3A0;

    // Pointers in the driver's data block (0x12FFEC-0x130007).
    private const int PriorityTablePtr = 0x12FFEC, MusicIndexPtr = 0x12FFF4, SfxIndexPtr = 0x12FFF8,
        SfxExtraIndexPtr = 0x12FFFC, SpeedUpTablePtr = 0x130000, PsgEnvelopesPtr = 0x130004;

    /// <summary>Channel bytes of the music tracks (0x1308BE: DAC, FM1-6; 0x1308C6: PSG1-3).</summary>
    private static readonly byte[] FmChannels = [0x06, 0x00, 0x01, 0x02, 0x04, 0x05, 0x06];
    private static readonly byte[] PsgChannels = [0x80, 0xA0, 0xC0];
    /// <summary>0x1309CE: the music track an SFX channel overrides; 0x1309EE: the SFX track for it.</summary>
    private static readonly int[] OverriddenTrack = [0xF0D0, 0, 0xF100, 0xF130, 0xF190, 0xF1C0, 0xF1F0, 0xF1F0];
    private static readonly int[] SfxTrack = [0xF220, 0, 0xF250, 0xF280, 0xF2B0, 0xF2E0, 0xF310, 0xF310];

    private int _a5;   // the current track
    private int _a4;   // its data pointer while parsing

    private int B(int offset) => ram.U8(_a5 + offset);
    private void SetB(int offset, int v) => ram.W8(_a5 + offset, v);
    private bool Bit(int bit) => (ram.U8(_a5) & (1 << bit)) != 0;
    private void Set(int bit) => ram.W8(_a5, ram.U8(_a5) | (1 << bit));
    private void Clr(int bit) => ram.W8(_a5, ram.U8(_a5) & ~(1 << bit));
    private static bool BitOf(Ram r, int a, int bit) => (r.U8(a) & (1 << bit)) != 0;
    private int G(int offset) => ram.U8(A6 + offset);
    private void SetG(int offset, int v) => ram.W8(A6 + offset, v);
    private int Next() => data.U8(_a4++);

    private static int Table(int[] table, int byteIndex) =>
        byteIndex >= 0 && byteIndex / 4 < table.Length && byteIndex % 4 == 0 ? table[byteIndex / 4] : 0;

    /// <summary>UpdateMusic (0x1301DC), once per VBlank.</summary>
    public void Update()
    {
        SetG(0x0E, 0);
        if (G(0x03) != 0) { PauseMusic(); return; }
        SetG(0x01, G(0x01) - 1);
        if (G(0x01) == 0) TempoWait();
        if (G(0x04) != 0) DoFadeOut();
        if (G(0x24) != 0) DoFadeIn();
        if (ram.U16(A6 + 0x0A) != 0) CycleSoundQueue();
        if (G(0x09) != 0x80 && PlaySoundId()) return;   // starting music or the SEGA voice ends the update

        _a5 = MusicDac;
        if ((sbyte)B(0) < 0 && DacUpdateTrack()) return;
        SetG(0x08, 0);
        for (int i = 0; i < 6; i++) { _a5 += TrackSize; if ((sbyte)B(0) < 0) FmUpdateTrack(); }
        for (int i = 0; i < 3; i++) { _a5 += TrackSize; if ((sbyte)B(0) < 0) PsgUpdateTrack(); }
        SetG(0x0E, 0x80);
        for (int i = 0; i < 3; i++) { _a5 += TrackSize; if ((sbyte)B(0) < 0) FmUpdateTrack(); }
        for (int i = 0; i < 3; i++) { _a5 += TrackSize; if ((sbyte)B(0) < 0) PsgUpdateTrack(); }
        SetG(0x0E, 0x40);
        _a5 += TrackSize; if ((sbyte)B(0) < 0) FmUpdateTrack();
        _a5 += TrackSize; if ((sbyte)B(0) < 0) PsgUpdateTrack();
    }

    // ------------------------------------------------------------------ tracks

    /// <summary>0x1302E8. Returns true when a $F2 on this track ended the whole update (its addq #8 pops UpdateMusic's return).</summary>
    private bool DacUpdateTrack()
    {
        SetB(0x0E, B(0x0E) - 1);
        if (B(0x0E) != 0) return false;
        SetG(0x08, 0x80);
        _a4 = (int)ram.U32(_a5 + 4);
        int d5;
        while ((d5 = Next()) >= 0xE0)
            if (CoordFlag(d5) == FlagResult.ExitTwo) return true;
        if ((sbyte)d5 < 0)
        {
            SetB(0x10, d5);
            d5 = Next();
            if ((sbyte)d5 < 0) { _a4--; SetB(0x0E, B(0x0F)); }
            else SetDuration(d5);
        }
        else SetDuration(d5);
        ram.W32(_a5 + 4, (uint)_a4);
        if (Bit(2)) return false;
        int d0 = B(0x10);
        if (d0 == 0x80) return false;
        if ((d0 & 8) != 0)
        {
            chips.SetDacPitch(data.DacPitches[(d0 - 0x88) & 7]);
            chips.PlayDac(0x83);
        }
        else chips.PlayDac(d0);
        return false;
    }

    /// <summary>0x130364.</summary>
    private void FmUpdateTrack()
    {
        SetB(0x0E, B(0x0E) - 1);
        if (B(0x0E) == 0)
        {
            Clr(4);
            if (!FmDoNext()) return;
            FmUpdateFreq();
            FmNoteOn();
            return;
        }
        if (NoteFillUpdate()) return;
        if (DoModulation() is not { } d6) return;
        FmUpdateFreqWith(d6);
    }

    /// <summary>0x130386: parse up to the next note. False when a flag stopped the track (addq #8).</summary>
    private bool FmDoNext()
    {
        _a4 = (int)ram.U32(_a5 + 4);
        Clr(1);
        int d5;
        while ((d5 = Next()) >= 0xE0)
            if (CoordFlag(d5) != FlagResult.Continue) return false;
        FmNoteOff();
        if ((sbyte)d5 >= 0) { SetDuration(d5); FinishTrackUpdate(); return true; }
        FmSetFreq(d5);
        d5 = Next();
        if ((sbyte)d5 >= 0) SetDuration(d5);
        else _a4--;
        FinishTrackUpdate();
        return true;
    }

    /// <summary>0x1303BC (a rest also runs FinishTrackUpdate here, as the original falls into it).</summary>
    private void FmSetFreq(int d5)
    {
        d5 = (d5 - 0x80) & 0xFF;
        if (d5 == 0)
        {
            Set(1);
            ram.W16(_a5 + 0x10, 0);
            FinishTrackUpdate();
            return;
        }
        d5 = ((d5 + B(0x08)) & 0x7F) * 2;
        ram.W16(_a5 + 0x10, data.FmFrequencies[d5 / 2]);
    }

    /// <summary>0x1303DA: duration = d5 * the track's tempo divider (byte arithmetic).</summary>
    private void SetDuration(int d5)
    {
        int d0 = d5;
        int d1 = B(0x02);
        while (true)
        {
            d1 = (d1 - 1) & 0xFF;
            if (d1 == 0) break;
            d0 = (d0 + d5) & 0xFF;
        }
        SetB(0x0F, d0);
        SetB(0x0E, d0);
    }

    /// <summary>0x1303FA.</summary>
    private void FinishTrackUpdate()
    {
        ram.W32(_a5 + 4, (uint)_a4);
        SetB(0x0E, B(0x0F));
        if (Bit(4)) return;
        SetB(0x12, B(0x13));
        SetB(0x0C, 0);
        if (!Bit(3)) return;
        int a0 = (int)ram.U32(_a5 + 0x14);
        SetB(0x18, data.U8(a0));
        SetB(0x19, data.U8(a0 + 1));
        SetB(0x1A, data.U8(a0 + 2));
        SetB(0x1B, data.U8(a0 + 3) >> 1);
        ram.W16(_a5 + 0x1C, 0);
    }

    /// <summary>0x130438. True when the note fill ran out (the note is released and the track's update ends).</summary>
    private bool NoteFillUpdate()
    {
        if (B(0x12) == 0) return false;
        SetB(0x12, B(0x12) - 1);
        if (B(0x12) != 0) return false;
        Set(1);
        if ((sbyte)B(0x01) < 0) PsgNoteOff(); else FmNoteOff();
        return true;
    }

    /// <summary>0x130460: the modulated frequency when it changes this frame, else null (the track's update ends).</summary>
    private int? DoModulation()
    {
        if (!Bit(3)) return null;
        if (B(0x18) != 0) { SetB(0x18, B(0x18) - 1); return null; }
        SetB(0x19, B(0x19) - 1);
        if (B(0x19) != 0) return null;
        int a0 = (int)ram.U32(_a5 + 0x14);
        SetB(0x19, data.U8(a0 + 1));
        if (B(0x1B) == 0)
        {
            SetB(0x1B, data.U8(a0 + 3));
            SetB(0x1A, -B(0x1A));
            return null;
        }
        SetB(0x1B, B(0x1B) - 1);
        int d6 = ((sbyte)B(0x1A) + ram.U16(_a5 + 0x1C)) & 0xFFFF;
        ram.W16(_a5 + 0x1C, d6);
        return (d6 + ram.U16(_a5 + 0x10)) & 0xFFFF;
    }

    /// <summary>0x1304B2.</summary>
    private void FmUpdateFreq()
    {
        if (Bit(1)) return;
        int d6 = ram.U16(_a5 + 0x10);
        if (d6 == 0) { Set(1); return; }
        FmUpdateFreqWith(d6);
    }

    /// <summary>0x1304BE.</summary>
    private void FmUpdateFreqWith(int d6)
    {
        d6 = (d6 + (sbyte)B(0x1E)) & 0xFFFF;
        if (Bit(2)) return;
        WriteFmIorII(0xA4, d6 >> 8);
        WriteFmIorII(0xA0, d6 & 0xFF);
    }

    /// <summary>0x130F4C.</summary>
    private void PsgUpdateTrack()
    {
        SetB(0x0E, B(0x0E) - 1);
        if (B(0x0E) == 0)
        {
            Clr(4);
            if (!PsgDoNext()) return;
            PsgDoNoteOn();
            PsgUpdateVolume();
            return;
        }
        if (NoteFillUpdate()) return;
        if (B(0x0B) != 0) PsgUpdateVolume();
        if (DoModulation() is not { } d6) return;
        PsgUpdateFreqWith(d6);
    }

    /// <summary>0x130F74.</summary>
    private bool PsgDoNext()
    {
        Clr(1);
        _a4 = (int)ram.U32(_a5 + 4);
        int d5;
        while ((d5 = Next()) >= 0xE0)
            if (CoordFlag(d5) != FlagResult.Continue) return false;
        if ((sbyte)d5 >= 0) { SetDuration(d5); FinishTrackUpdate(); return true; }
        PsgSetFreq(d5);
        d5 = Next();
        if ((sbyte)d5 >= 0) SetDuration(d5);
        else _a4--;
        FinishTrackUpdate();
        return true;
    }

    /// <summary>0x130FA8 (both branches end in FinishTrackUpdate, which the caller runs again).</summary>
    private void PsgSetFreq(int d5)
    {
        d5 -= 0x81;
        if (d5 < 0)
        {
            Set(1);
            ram.W16(_a5 + 0x10, 0xFFFF);
            FinishTrackUpdate();
            PsgNoteOff();
            return;
        }
        d5 = ((d5 + B(0x08)) & 0x7F) * 2;
        ram.W16(_a5 + 0x10, data.PsgFrequencies[d5 / 2]);
        FinishTrackUpdate();
    }

    /// <summary>0x130FD8.</summary>
    private void PsgDoNoteOn()
    {
        int d6 = ram.U16(_a5 + 0x10);
        if ((short)d6 < 0) { Set(1); return; }
        PsgUpdateFreqWith(d6);
    }

    /// <summary>0x130FDE.</summary>
    private void PsgUpdateFreqWith(int d6)
    {
        d6 = (d6 + (sbyte)B(0x1E)) & 0xFFFF;
        if (Bit(2) || Bit(1)) return;
        int d0 = B(0x01);
        if (d0 == 0xE0) d0 = 0xC0;
        d0 |= d6 & 0x0F;
        chips.WritePsg(d0 & 0xFF);
        chips.WritePsg((d6 >> 4) & 0x3F);
    }

    /// <summary>0x13102A: volume plus the PSG envelope.</summary>
    private void PsgUpdateVolume()
    {
        int d6 = B(0x09);
        int voice = B(0x0B);
        if (voice != 0)
        {
            int table = data.U32(PsgEnvelopesPtr);
            int env = data.U32(table + (voice - 1) * 4);
            int d0 = data.U8(env + B(0x0C));
            SetB(0x0C, B(0x0C) + 1);
            if (d0 == 0x80) { SetB(0x0C, B(0x0C) - 1); return; }
            d6 = (d6 + d0) & 0xFF;
            if (d6 >= 0x10) d6 = 0x0F;
        }
        PsgSetVolume(d6);
    }

    /// <summary>0x131066.</summary>
    private void PsgSetVolume(int d6)
    {
        if (Bit(1) || Bit(2)) return;
        if (Bit(4) && !(B(0x13) == 0 || B(0x12) != 0)) return;
        chips.WritePsg(((d6 | B(0x01)) + 0x10) & 0xFF);
    }

    /// <summary>0x13109C.</summary>
    private void PsgNoteOff()
    {
        if (Bit(2)) return;
        chips.WritePsg(B(0x01) | 0x1F);
    }

    // ------------------------------------------------------------------ FM writes

    /// <summary>0x130DDE.</summary>
    private void FmNoteOn()
    {
        if (Bit(1) || Bit(2)) return;
        chips.WriteYm(0, 0x28, B(0x01) | 0xF0);
    }

    /// <summary>0x130DFA.</summary>
    private void FmNoteOff()
    {
        if (Bit(4) || Bit(2)) return;
        chips.WriteYm(0, 0x28, B(0x01));
    }

    /// <summary>0x130E12.</summary>
    private void WriteFmIorIIMain(int reg, int value)
    {
        if (Bit(2)) return;
        WriteFmIorII(reg, value);
    }

    /// <summary>0x130E1E: a channel register in part I or II depending on the track's channel.</summary>
    private void WriteFmIorII(int reg, int value)
    {
        int ch = B(0x01);
        if ((ch & 4) != 0) chips.WriteYm(1, (reg + (ch & ~4)) & 0xFF, value & 0xFF);
        else chips.WriteYm(0, (reg + ch) & 0xFF, value & 0xFF);
    }

    /// <summary>0x13134A: load FM voice <paramref name="index"/> from the voice table at <paramref name="table"/>.</summary>
    private void SetVoice(int table, int index)
    {
        int a1 = table + 25 * (index & 0xFF);
        int d1 = data.U8(a1++);
        SetB(0x1F, d1);
        int algorithm = d1;
        WriteFmIorII(0xB0, d1);
        int r = 0;
        for (int i = 0; i < 20; i++) WriteFmIorII(data.VoiceRegisters[r++], data.U8(a1++));
        int carriers = data.CarrierMasks[algorithm & 7];
        int volume = B(0x09);
        for (int i = 0; i < 4; i++)
        {
            int reg = data.VoiceRegisters[r++];
            int v = data.U8(a1++);
            if ((carriers & 1) != 0) v = (v + volume) & 0xFF;
            carriers >>= 1;
            WriteFmIorII(reg, v);
        }
        WriteFmIorII(0xB4, B(0x0A));
    }

    /// <summary>0x1313B0: rewrite the carriers' total levels for the track's volume.</summary>
    private void SetTotalLevels()
    {
        if (Bit(2)) return;
        int index = B(0x0B);
        // As the original, any SFX mode uses the special SFX voice pointer.
        int a1 = (int)ram.U32(A6 + (G(0x0E) != 0 ? 0x20 : 0x18));
        a1 += 25 * index + 0x15;
        int carriers = data.CarrierMasks[B(0x1F) & 7];
        int volume = B(0x09);
        if ((sbyte)volume < 0) return;
        for (int i = 0; i < 4; i++)
        {
            int reg = data.VoiceRegisters[20 + i];
            int v = data.U8(a1++);
            bool carrier = (carriers & 1) != 0;
            carriers >>= 1;
            if (!carrier) continue;
            v += volume;
            if (v > 0xFF) continue;
            WriteFmIorII(reg, v);
        }
    }

    // ------------------------------------------------------------------ coordination flags

    private enum FlagResult { Continue, ExitTwo }

    /// <summary>0x131156: flags $E0-$F9 (handler table 0x131160). ExitTwo = the handler's addq #8,sp.</summary>
    private FlagResult CoordFlag(int flag)
    {
        switch (flag)
        {
            case 0xE0:   // panning / AMS / FMS
            {
                int d1 = Next();
                if ((sbyte)B(0x01) < 0) break;
                d1 |= B(0x0A) & 0x37;
                SetB(0x0A, d1);
                WriteFmIorIIMain(0xB4, d1);
                break;
            }
            case 0xE1: SetB(0x1E, Next()); break;
            case 0xE2: SetG(0x07, Next()); break;
            case 0xE3:   // return from a $F8 call
            {
                int d0 = B(0x0D);
                _a4 = (int)ram.U32(_a5 + d0);
                ram.W32(_a5 + d0, 0);
                _a4 += 2;
                SetB(0x0D, d0 + 4);
                break;
            }
            case 0xE4: RestoreAfterOneUp(); return FlagResult.ExitTwo;
            case 0xE5: SetB(0x02, Next()); break;
            case 0xE6: SetB(0x09, B(0x09) + Next()); SetTotalLevels(); break;
            case 0xE7: Set(4); break;
            case 0xE8: { int v = data.U8(_a4); SetB(0x12, v); SetB(0x13, Next()); break; }
            case 0xE9: SetB(0x08, B(0x08) + Next()); break;
            case 0xEA: { int v = data.U8(_a4); SetG(0x02, v); SetG(0x01, Next()); break; }
            case 0xEB:
            {
                int d0 = Next();
                for (int i = 0; i < 10; i++) ram.W8(MusicDac + i * TrackSize + 2, d0);
                break;
            }
            case 0xEC: SetB(0x09, B(0x09) + Next()); break;
            case 0xED: SetG(0x2C, 0); break;
            case 0xEE: StopSpecialFm4(); return FlagResult.ExitTwo;
            case 0xEF:
            {
                int d0 = Next();
                SetB(0x0B, d0);
                if (Bit(2)) break;
                int a1 = (int)ram.U32(A6 + 0x18);
                if (G(0x0E) != 0)
                {
                    a1 = (int)ram.U32(_a5 + 0x20);
                    if ((sbyte)G(0x0E) >= 0) a1 = (int)ram.U32(A6 + 0x20);
                }
                SetVoice(a1, d0);
                break;
            }
            case 0xF0:
                Set(3);
                ram.W32(_a5 + 0x14, (uint)_a4);
                SetB(0x18, Next());
                SetB(0x19, Next());
                SetB(0x1A, Next());
                SetB(0x1B, Next() >> 1);
                ram.W16(_a5 + 0x1C, 0);
                break;
            case 0xF1: Set(3); break;
            case 0xF2: StopTrack(); return FlagResult.ExitTwo;
            case 0xF3:
                SetB(0x01, 0xE0);
                SetB(0x1F, Next());
                if (!Bit(2)) chips.WritePsg(data.U8(_a4 - 1));
                break;
            case 0xF4: Clr(3); break;
            case 0xF5: SetB(0x0B, Next()); break;
            case 0xF6: Jump(); break;
            case 0xF7:
            {
                int index = Next();
                int count = Next();
                if (B(0x24 + index) == 0) SetB(0x24 + index, count);
                SetB(0x24 + index, B(0x24 + index) - 1);
                if (B(0x24 + index) != 0) Jump();
                else _a4 += 2;
                break;
            }
            case 0xF8:
            {
                int d0 = (B(0x0D) - 4) & 0xFF;
                ram.W32(_a5 + d0, (uint)_a4);
                SetB(0x0D, d0);
                Jump();
                break;
            }
            case 0xF9:
                chips.WriteYm(0, 0x88, 0x0F);
                chips.WriteYm(0, 0x8C, 0x0F);
                break;
            default:
                // Above $F9 the original jumps past its table into code; no sound data uses them.
                break;
        }
        return FlagResult.Continue;
    }

    /// <summary>0x131526: branch to operand address + 1 + offset.</summary>
    private void Jump()
    {
        int offset = (short)((data.U8(_a4) << 8) | data.U8(_a4 + 1));
        _a4 += 2;
        _a4 = _a4 + offset - 1;
    }

    /// <summary>0x131454 ($F2).</summary>
    private void StopTrack()
    {
        Clr(7);
        Clr(4);
        if ((sbyte)B(0x01) < 0) PsgNoteOff();
        else
        {
            if ((sbyte)G(0x08) < 0) return;   // the DAC track (its update set $F008)
            FmNoteOff();
        }
        if ((sbyte)G(0x0E) >= 0) return;   // only an SFX track gives its channel back
        SetG(0x00, 0);
        int d0 = B(0x01);
        int a3 = _a5;
        if ((sbyte)d0 >= 0)
        {
            int a1;
            if (d0 == 4 && (sbyte)ram.U8(SpecialFm) < 0)
            {
                _a5 = SpecialFm;
                a1 = (int)ram.U32(A6 + 0x20);
            }
            else
            {
                _a5 = Table(OverriddenTrack, ((d0 - 2) << 2) & 0xFF);
                if (_a5 == 0 || (sbyte)B(0) >= 0) { _a5 = a3; return; }
                a1 = (int)ram.U32(A6 + 0x18);
            }
            Clr(2);
            Set(1);
            SetVoice(a1, B(0x0B));
            _a5 = a3;
            return;
        }
        int a0 = SpecialPsg;
        if (!((sbyte)ram.U8(a0) < 0 && (d0 == 0xE0 || d0 == 0xC0)))
            a0 = Table(OverriddenTrack, (d0 >> 3) & 0x1F);
        if (a0 == 0) return;
        ram.W8(a0, ram.U8(a0) & ~4);
        ram.W8(a0, ram.U8(a0) | 2);
        if (ram.U8(a0 + 1) == 0xE0) chips.WritePsg(ram.U8(a0 + 0x1F));
    }

    /// <summary>0x1312F0 ($EE): stop the special SFX FM4 track and give FM4 back to the music.</summary>
    private void StopSpecialFm4()
    {
        Clr(7);
        Clr(4);
        FmNoteOff();
        if ((sbyte)ram.U8(A6 + 0x250) < 0) return;
        int a3 = _a5;
        _a5 = A6 + 0x100;
        int a1 = (int)ram.U32(A6 + 0x18);
        Clr(2);
        Set(1);
        SetVoice(a1, B(0x0B));
        _a5 = a3;
    }

    /// <summary>0x131210 ($E4): the 1-up jingle ended; bring the saved music back and fade it in.</summary>
    private void RestoreAfterOneUp()
    {
        for (int i = 0; i < 0x220; i++) ram.W8(A6 + i, ram.U8(Saved + i));
        ram.W8(MusicDac, ram.U8(MusicDac) | 4);
        int a3 = _a5;
        int d6 = (0x28 - G(0x26)) & 0xFF;
        _a5 = MusicFm1;
        for (int i = 0; i < 6; i++, _a5 += TrackSize)
        {
            if (!Bit(7)) continue;
            Set(1);
            SetB(0x09, B(0x09) + d6);
            if (Bit(2)) continue;
            SetVoice((int)ram.U32(A6 + 0x18), B(0x0B));
        }
        for (int i = 0; i < 3; i++, _a5 += TrackSize)
        {
            if (!Bit(7)) continue;
            Set(1);
            PsgNoteOff();
            SetB(0x09, B(0x09) + d6);
        }
        _a5 = a3;
        SetG(0x24, 0x80);
        SetG(0x26, 0x28);
        SetG(0x27, 0);
    }

    // ------------------------------------------------------------------ requests

    /// <summary>0x13059C: take the highest-priority request from the queue ($F00A-$F00C) into $F009.</summary>
    private void CycleSoundQueue()
    {
        int table = data.U32(PriorityTablePtr);
        int d3 = G(0x00);
        for (int i = 0; i < 3; i++)
        {
            int d1 = ram.U8(A6 + 0x0A + i);
            ram.W8(A6 + 0x0A + i, 0);
            if (d1 < 0x81) continue;
            if (G(0x09) != 0x80) { SetG(0x0A, d1); continue; }
            int d2 = data.U8(table + ((d1 - 0x81) & 0x7F));
            if (d2 < d3) continue;
            d3 = d2;
            SetG(0x09, d1);
        }
        if ((sbyte)d3 >= 0) SetG(0x00, d3);
    }

    /// <summary>0x1305E2. True when the rest of this frame's update is skipped (music start, SEGA voice).</summary>
    private bool PlaySoundId()
    {
        int d7 = G(0x09);
        if (d7 == 0) { StopAllSound(); return false; }
        if (d7 < 0x80) return false;
        SetG(0x09, 0x80);
        if (d7 >= 0xE0 && d7 < 0xF8)
        {
            // The hack's extra SFX table (0x137342): no B5 alternation here.
            if (G(0x27) != 0 || G(0x04) != 0 || G(0x24) != 0) { SetG(0x00, 0); return false; }
            PlaySfxCommon(d7, data.U32(SfxExtraIndexPtr), d7 - 0xE0);
            return false;
        }
        if (d7 == 0xFF) return false;   // reaches the extra SFX table with index $FF (garbage); nothing requests it
        if (d7 <= 0x9F) { PlayMusic(d7); return true; }
        if (d7 <= 0xF0) { PlaySfx(d7); return false; }
        switch (d7)
        {
            case 0xF9: FadeOutMusic(); return false;
            case 0xFA: case 0xFE: chips.PlaySegaPcm(); return true;
            case 0xFB: SpeedUp(); return false;
            case 0xFC: NormalSpeed(); return false;
            case 0xFD: StopAllSound(); return false;
            default: return false;   // $F8
        }
    }

    /// <summary>0x1306DA.</summary>
    private void PlayMusic(int d7)
    {
        if (d7 == 0x98)
        {
            if (G(0x27) != 0) return;
            for (int i = 0; i < 10; i++) ram.W8(MusicDac + i * TrackSize, ram.U8(MusicDac + i * TrackSize) & ~4);
            for (int i = 0; i < 6; i++) ram.W8(SfxFm1 + i * TrackSize, ram.U8(SfxFm1 + i * TrackSize) & 0x7F);
            SetG(0x00, 0);
            for (int i = 0; i < 0x220; i++) ram.W8(Saved + i, ram.U8(A6 + i));
            SetG(0x27, 0x80);
            SetG(0x00, 0);
        }
        else
        {
            SetG(0x27, 0);
            SetG(0x26, 0);
        }
        InitMusicPlayback();
        int index = d7 - 0x81;
        SetG(0x29, data.U8(data.U32(SpeedUpTablePtr) + index));
        int a4 = data.U32(data.U32(MusicIndexPtr) + index * 4);
        ram.W32(A6 + 0x18, (uint)(data.U16(a4) + a4));
        int tempo = data.U8(a4 + 5);
        SetG(0x28, tempo);
        if (G(0x2A) != 0) tempo = G(0x29);
        SetG(0x02, tempo);
        SetG(0x01, tempo);
        int a3 = a4;
        a4 += 6;
        int divider = data.U8(a3 + 4);
        int fmCount = data.U8(a3 + 2);
        if (fmCount != 0)
        {
            int a1 = MusicDac;
            for (int i = 0; i < fmCount; i++, a1 += TrackSize)
            {
                ram.W8(a1, ram.U8(a1) | 0x80);
                ram.W8(a1 + 1, i < FmChannels.Length ? FmChannels[i] : 0);
                ram.W8(a1 + 2, divider);
                ram.W8(a1 + 0x0D, TrackSize);
                ram.W8(a1 + 0x0A, 0xC0);
                ram.W8(a1 + 0x0E, 1);
                ram.W32(a1 + 4, (uint)(data.U16(a4) + a3));
                ram.W16(a1 + 8, data.U16(a4 + 2));
                a4 += 4;
            }
            if (fmCount == 7) chips.WriteYm(0, 0x2B, 0x00);   // no DAC: FM6 plays
            else
            {
                chips.WriteYm(0, 0x28, 0x06);
                chips.WriteYm(1, 0x42, 0x7F);
                chips.WriteYm(1, 0x4A, 0x7F);
                chips.WriteYm(1, 0x46, 0x7F);
                chips.WriteYm(1, 0x4E, 0x7F);
                chips.WriteYm(1, 0xB6, 0xC0);
            }
        }
        int psgCount = data.U8(a3 + 3);
        if (psgCount != 0)
        {
            int a1 = MusicPsg1;
            for (int i = 0; i < psgCount; i++, a1 += TrackSize)
            {
                ram.W8(a1, ram.U8(a1) | 0x80);
                ram.W8(a1 + 1, i < PsgChannels.Length ? PsgChannels[i] : 0);
                ram.W8(a1 + 2, divider);
                ram.W8(a1 + 0x0D, TrackSize);
                ram.W8(a1 + 0x0E, 1);
                ram.W32(a1 + 4, (uint)(data.U16(a4) + a3));
                ram.W16(a1 + 8, data.U16(a4 + 2));
                ram.W8(a1 + 0x0B, data.U8(a4 + 5));
                a4 += 6;
            }
        }
        // SFX already playing keep their channels.
        for (int i = 0; i < 6; i++)
        {
            int a1 = SfxFm1 + i * TrackSize;
            if ((sbyte)ram.U8(a1) >= 0) continue;
            int d0 = ram.U8(a1 + 1);
            d0 = (sbyte)d0 < 0 ? d0 >> 3 : ((d0 - 2) << 2) & 0xFF;
            int a0 = Table(OverriddenTrack, d0);
            if (a0 != 0) ram.W8(a0, ram.U8(a0) | 4);
        }
        if ((short)ram.U16(SpecialFm) < 0) ram.W8(A6 + 0x100, ram.U8(A6 + 0x100) | 4);
        if ((short)ram.U16(SpecialPsg) < 0) ram.W8(A6 + 0x1F0, ram.U8(A6 + 0x1F0) | 4);
        _a5 = MusicFm1;
        for (int i = 0; i < 6; i++, _a5 += TrackSize) FmNoteOff();
        for (int i = 0; i < 3; i++, _a5 += TrackSize) PsgNoteOff();
    }

    /// <summary>0x1308CA.</summary>
    private void PlaySfx(int d7)
    {
        if (G(0x27) != 0 || G(0x04) != 0 || G(0x24) != 0) { SetG(0x00, 0); return; }
        if (d7 == 0xB5)
        {
            if (G(0x2B) == 0) d7 = 0xCE;   // ring sound alternates left/right
            SetG(0x2B, G(0x2B) ^ 1);
        }
        if (d7 == 0xA7)
        {
            if (G(0x2C) != 0) return;
            SetG(0x2C, 0x80);
        }
        PlaySfxCommon(d7, data.U32(SfxIndexPtr), d7 - 0xA0);
    }

    /// <summary>0x130916.</summary>
    private void PlaySfxCommon(int id, int table, int index)
    {
        int a3 = data.U32(table + (index & 0x3FFF) * 4);
        int a1 = a3;
        int voices = data.U16(a1) + a3;
        a1 += 2;
        int divider = data.U8(a1++);
        int count = data.U8(a1++);
        for (int i = 0; i < count; i++)
        {
            int d3 = data.U8(a1 + 1);
            int d4 = d3;
            if ((sbyte)d3 >= 0) d3 = ((d3 - 2) << 2) & 0xFFFF;
            else
            {
                d3 >>= 3;
            }
            int over = Table(OverriddenTrack, d3);
            if (over != 0) ram.W8(over, ram.U8(over) | 4);
            if (d4 == 0xC0)
            {
                chips.WritePsg(0xDF);
                chips.WritePsg(0xFF);
            }
            int a5 = Table(SfxTrack, d3);
            if (a5 == 0) { a1 += 6; continue; }
            for (int k = 0; k < TrackSize; k++) ram.W8(a5 + k, 0);
            ram.W16(a5, data.U16(a1));
            ram.W8(a5 + 2, divider);
            ram.W32(a5 + 4, (uint)(data.U16(a1 + 2) + a3));
            ram.W16(a5 + 8, data.U16(a1 + 4));
            a1 += 6;
            ram.W8(a5 + 0x0E, 1);
            ram.W8(a5 + 0x0D, TrackSize);
            if ((sbyte)d4 >= 0)
            {
                ram.W8(a5 + 0x0A, 0xC0);
                ram.W32(a5 + 0x20, (uint)voices);
            }
        }
        if ((sbyte)ram.U8(A6 + 0x250) < 0) ram.W8(SpecialFm, ram.U8(SpecialFm) | 4);
        if ((sbyte)ram.U8(A6 + 0x310) < 0) ram.W8(SpecialPsg, ram.U8(SpecialPsg) | 4);
    }

    /// <summary>0x130AE2: stop all SFX and give their channels back to the music.</summary>
    private void StopSfx()
    {
        SetG(0x00, 0);
        int a3 = 0;
        for (_a5 = SfxFm1; _a5 < SfxFm1 + 6 * TrackSize; _a5 += TrackSize)
        {
            if ((sbyte)B(0) >= 0) continue;
            Clr(7);
            int d3 = B(0x01);
            if ((sbyte)d3 >= 0)
            {
                FmNoteOff();
                int a1;
                int self = _a5;
                if (d3 == 4 && (sbyte)ram.U8(SpecialFm) < 0)
                {
                    _a5 = SpecialFm;
                    a1 = (int)ram.U32(A6 + 0x20);
                }
                else
                {
                    a3 = self;
                    _a5 = Table(OverriddenTrack, ((d3 - 2) << 2) & 0xFF);
                    a1 = (int)ram.U32(A6 + 0x18);
                    if (_a5 == 0) { _a5 = a3; continue; }
                }
                Clr(2);
                Set(1);
                SetVoice(a1, B(0x0B));
                _a5 = a3 != 0 ? a3 : self;   // the original restores a stale a3 on the special-SFX path
                continue;
            }
            PsgNoteOff();
            int a0 = SpecialPsg;
            if (d3 != 0xE0 && d3 != 0xC0) a0 = Table(OverriddenTrack, (d3 >> 3) & 0x1F);
            if (a0 == 0) continue;
            ram.W8(a0, ram.U8(a0) & ~4);
            ram.W8(a0, ram.U8(a0) | 2);
            if (ram.U8(a0 + 1) == 0xE0) chips.WritePsg(ram.U8(a0 + 0x1F));
        }
    }

    /// <summary>0x130B7C.</summary>
    private void StopSpecialSfx()
    {
        _a5 = SpecialFm;
        if ((sbyte)B(0) < 0)
        {
            Clr(7);
            if (!Bit(2))
            {
                chips.WriteYm(0, 0x28, B(0x01));
                _a5 = A6 + 0x100;
                Clr(2);
                Set(1);
                if ((sbyte)B(0) < 0) SetVoice((int)ram.U32(A6 + 0x18), B(0x0B));
            }
        }
        _a5 = SpecialPsg;
        if ((sbyte)B(0) < 0)
        {
            Clr(7);
            if (!Bit(2))
            {
                chips.WritePsg(B(0x01) | 0x1F);
                _a5 = A6 + 0x1F0;
                Clr(2);
                Set(1);
                if ((sbyte)B(0) < 0 && B(0x01) == 0xE0) chips.WritePsg(B(0x1F));
            }
        }
    }

    /// <summary>0x130BE6 ($F9).</summary>
    private void FadeOutMusic()
    {
        StopSfx();
        StopSpecialSfx();
        SetG(0x06, 3);
        SetG(0x04, 0x28);
        ram.W8(MusicDac, 0);
        SetG(0x2A, 0);
    }

    /// <summary>0x130C04.</summary>
    private void DoFadeOut()
    {
        if (G(0x06) != 0) { SetG(0x06, G(0x06) - 1); return; }
        SetG(0x04, G(0x04) - 1);
        if (G(0x04) == 0) { StopAllSound(); return; }
        SetG(0x06, 3);
        _a5 = MusicFm1;
        for (int i = 0; i < 6; i++, _a5 += TrackSize)
        {
            if ((sbyte)B(0) >= 0) continue;
            SetB(0x09, B(0x09) + 1);
            if ((sbyte)B(0x09) < 0) Clr(7);
            else SetTotalLevels();
        }
        for (int i = 0; i < 3; i++, _a5 += TrackSize)
        {
            if ((sbyte)B(0) >= 0) continue;
            SetB(0x09, B(0x09) + 1);
            if (B(0x09) >= 0x10) Clr(7);
            else PsgSetVolume(B(0x09));
        }
    }

    /// <summary>0x130D78.</summary>
    private void DoFadeIn()
    {
        if (G(0x25) != 0) { SetG(0x25, G(0x25) - 1); return; }
        if (G(0x26) == 0)
        {
            ram.W8(MusicDac, ram.U8(MusicDac) & ~4);
            SetG(0x24, 0);
            return;
        }
        SetG(0x26, G(0x26) - 1);
        SetG(0x25, 2);
        _a5 = MusicFm1;
        for (int i = 0; i < 6; i++, _a5 += TrackSize)
        {
            if ((sbyte)B(0) >= 0) continue;
            SetB(0x09, B(0x09) - 1);
            SetTotalLevels();
        }
        for (int i = 0; i < 3; i++, _a5 += TrackSize)
        {
            if ((sbyte)B(0) >= 0) continue;
            SetB(0x09, B(0x09) - 1);
            int d6 = B(0x09);
            if (d6 >= 0x10) d6 = 0x0F;
            PsgSetVolume(d6);
        }
    }

    /// <summary>0x130D08: every tempo period, every music track waits one more frame.</summary>
    private void TempoWait()
    {
        SetG(0x01, G(0x02));
        for (int i = 0; i < 10; i++)
        {
            int a = MusicDac + i * TrackSize + 0x0E;
            ram.W8(a, ram.U8(a) + 1);
        }
    }

    /// <summary>0x130D20 ($FB).</summary>
    private void SpeedUp()
    {
        if (G(0x27) != 0)
        {
            ram.W8(Saved + 0x02, ram.U8(Saved + 0x29));
            ram.W8(Saved + 0x01, ram.U8(Saved + 0x29));
            ram.W8(Saved + 0x2A, 0x80);
            return;
        }
        SetG(0x02, G(0x29));
        SetG(0x01, G(0x29));
        SetG(0x2A, 0x80);
    }

    /// <summary>0x130D4E ($FC).</summary>
    private void NormalSpeed()
    {
        if (G(0x27) != 0)
        {
            ram.W8(Saved + 0x02, ram.U8(Saved + 0x28));
            ram.W8(Saved + 0x01, ram.U8(Saved + 0x28));
            ram.W8(Saved + 0x2A, 0);
            return;
        }
        SetG(0x02, G(0x28));
        SetG(0x01, G(0x28));
        SetG(0x2A, 0);
    }

    /// <summary>0x130C9E.</summary>
    private void StopAllSound()
    {
        chips.WriteYm(0, 0x2B, 0x80);
        chips.WriteYm(0, 0x27, 0x00);
        for (int i = 0; i < 0x390; i++) ram.W8(A6 + i, 0);
        SetG(0x09, 0x80);
        FmSilenceAll();
        PsgSilenceAll();
    }

    /// <summary>0x130CCA: clear the music state, keeping the priority, 1-up / speed / fade flags and the queue.</summary>
    private void InitMusicPlayback()
    {
        int d1 = G(0x00), d2 = G(0x27), d3 = G(0x2A), d4 = G(0x26), d5 = ram.U16(A6 + 0x0A);
        for (int i = 0; i < 0x220; i++) ram.W8(A6 + i, 0);
        SetG(0x00, d1);
        SetG(0x27, d2);
        SetG(0x2A, d3);
        SetG(0x26, d4);
        ram.W16(A6 + 0x0A, d5);
        SetG(0x09, 0x80);
        FmSilenceAll();
        PsgSilenceAll();
    }

    /// <summary>0x130C6A.</summary>
    private void FmSilenceAll()
    {
        for (int d3 = 2; d3 >= 0; d3--)
        {
            chips.WriteYm(0, 0x28, d3);
            chips.WriteYm(0, 0x28, d3 + 4);
        }
        int d0 = 0x40;
        for (int ch = 0; ch < 3; ch++)
        {
            for (int op = 0; op < 4; op++)
            {
                chips.WriteYm(0, d0, 0x7F);
                chips.WriteYm(1, d0, 0x7F);
                d0 += 4;
            }
            d0 -= 0x0F;
        }
    }

    /// <summary>0x1310B2.</summary>
    private void PsgSilenceAll()
    {
        chips.WritePsg(0x9F);
        chips.WritePsg(0xBF);
        chips.WritePsg(0xDF);
        chips.WritePsg(0xFF);
    }

    /// <summary>0x1304EA: $F003 set = pause ($80 = resume).</summary>
    private void PauseMusic()
    {
        if ((sbyte)G(0x03) < 0)
        {
            SetG(0x03, 0);
            void RestorePan(int track)
            {
                _a5 = track;
                if (Bit(7) && !Bit(2)) WriteFmIorII(0xB4, B(0x0A));
            }
            for (int i = 0; i < 7; i++) RestorePan(MusicDac + i * TrackSize);
            for (int i = 0; i < 3; i++) RestorePan(SfxFm1 + i * TrackSize);
            RestorePan(SpecialFm);
            return;
        }
        if (G(0x03) == 2) return;
        SetG(0x03, 2);
        for (int i = 0; i < 3; i++)
        {
            chips.WriteYm(0, 0xB4 + i, 0);
            chips.WriteYm(1, 0xB4 + i, 0);
        }
        for (int d3 = 2; d3 >= 0; d3--)
        {
            chips.WriteYm(0, 0x28, d3);
            chips.WriteYm(0, 0x28, d3 + 4);
        }
        PsgSilenceAll();
    }
}
