using System.Text.Json;
using S2Port.Extraction.Audio;

namespace SonicBros3.Audio;

/// <summary>
/// The Mega Drive's sound hardware as the 68k driver sees it: the YM2612, the PSG and the Z80 DAC driver (which
/// streams DPCM-decoded samples into YM register $2A at a rate set by its pitch byte). Chip writes are applied as
/// they come; <see cref="Render"/> then produces one frame's audio at the output rate (stereo 16-bit).
/// </summary>
public sealed class SoundMixer : ISoundChips
{
    private readonly Ym2612 _ym = new();
    private readonly Sn76489 _psg = new(Ym2612.SampleRate);
    private readonly byte[][] _samples;
    private readonly int[] _pitches;
    private byte[]? _playing;
    private double _position, _step;
    private readonly int _outputRate;
    private double _resample;          // fractional position between native samples
    private int _prevL, _prevR, _curL, _curR;
    private byte[]? _effect;
    private double _effectPosition;

    public void PlayEffect(string dataDir, string name)
    {
        _effect = S2Port.Extraction.DataFile.ReadAllBytes(Path.Combine(dataDir, "campaign", "effects", name + ".pcm"));
        _effectPosition = 0;
    }

    /// <summary>Debugging: render only these parts ("ym", "psg", "dac"); null = all.</summary>
    public string? Solo { get; set; }

    /// <summary>Master volume, 0-1.</summary>
    public float Volume { get; set; } = 0.8f;

    public SoundMixer(string dataDir, int outputRate = 48000)
    {
        _outputRate = outputRate;
        string dir = Path.Combine(dataDir, "audio", "smps68k");
        using var doc = JsonDocument.Parse(S2Port.Extraction.DataFile.ReadAllText(Path.Combine(dir, "driver.json")));
        var samples = doc.RootElement.GetProperty("dac").GetProperty("samples").EnumerateArray().ToList();
        _samples = samples.Select(s => ReadWavData(Path.Combine(dir, s.GetProperty("wav").GetString()!))).ToArray();
        _pitches = samples.Select(s => s.GetProperty("pitch").GetInt32()).ToArray();
    }

    private static byte[] ReadWavData(string path)
    {
        var b = S2Port.Extraction.DataFile.ReadAllBytes(path);
        for (int i = 12; i + 8 <= b.Length;)
        {
            int size = BitConverter.ToInt32(b, i + 4);
            if (b[i] == 'd' && b[i + 1] == 'a' && b[i + 2] == 't' && b[i + 3] == 'a') return b.AsSpan(i + 8, Math.Min(size, b.Length - i - 8)).ToArray();
            i += 8 + size + (size & 1);
        }
        return [];
    }

    public void WriteYm(int part, int register, int value) => _ym.Write(part, register, value);
    public void WritePsg(int value) => _psg.Write(value);

    public void PlayDac(int sample)
    {
        int i = sample - 0x81;
        if (i < 0 || i >= _samples.Length) return;
        _playing = _samples[i];
        _position = 0;
        _step = Smps68k.SampleRate(_pitches[i]) / Ym2612.SampleRate;
    }

    public void SetDacPitch(int pitch)
    {
        if (_pitches.Length > 2) _pitches[2] = pitch;
    }

    public void PlaySegaPcm() { }   // only used on the SEGA screen, which the port does not have

    public void PlayDacPcm(byte[] samples, double rate)
    {
        _playing = samples.Length > 0 ? samples : null;
        _position = 0;
        _step = rate / Ym2612.SampleRate;
    }   // only used on the SEGA screen, which the port does not have

    private void NativeSample(out int left, out int right)
    {
        if (_playing is { } s)
        {
            int p = (int)_position;
            if (p < s.Length) { _ym.Dac = s[p]; _position += _step; }
            else _playing = null;
        }
        _ym.Clock(out left, out right);
        int psg = _psg.Clock();
        if (Solo is not null)
        {
            if (Solo == "psg") left = right = 0;
            if (Solo != "psg") psg = 0;
        }
        left += psg;
        right += psg;
    }

    /// <summary>Renders <paramref name="frames"/> stereo output frames into <paramref name="buffer"/> (interleaved L/R).</summary>
    public void Render(Span<short> buffer, int frames)
    {
        double ratio = Ym2612.SampleRate / _outputRate;
        float scale = Volume * 0.48f;   // level matched to MAME's output for the same music
        for (int i = 0; i < frames; i++)
        {
            _resample += ratio;
            while (_resample >= 1)
            {
                _resample -= 1;
                _prevL = _curL; _prevR = _curR;
                NativeSample(out _curL, out _curR);
            }
            double t = _resample;
            double l = (_prevL + (_curL - _prevL) * t) * scale;
            double r = (_prevR + (_curR - _prevR) * t) * scale;
            if (_effect is { } effect)
            {
                int index = (int)_effectPosition * 2;
                if (index + 1 < effect.Length)
                {
                    int sample = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(effect.AsSpan(index, 2));
                    l += sample * Volume;
                    r += sample * Volume;
                    _effectPosition += S2Port.Extraction.Smb3.Smb3Effects.SampleRate / (double)_outputRate;
                }
                else _effect = null;
            }
            buffer[i * 2] = (short)Math.Clamp(l, short.MinValue, short.MaxValue);
            buffer[i * 2 + 1] = (short)Math.Clamp(r, short.MinValue, short.MaxValue);
        }
    }

    public int OutputRate => _outputRate;
}
