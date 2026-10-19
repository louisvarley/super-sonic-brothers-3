namespace SonicBros3.Audio;

/// <summary>
/// The SN76489-compatible PSG in the Mega Drive's VDP: three square-wave tones and a noise channel, 2 dB volume
/// steps, a 16-bit noise shift register tapped at bits 0 and 3 (Sega variant). Clocked at 3579545 Hz / 16.
/// </summary>
public sealed class Sn76489
{
    public const double ClockNtsc = 3579545;

    private static readonly int[] VolumeTable = BuildVolumes();
    private readonly int[] _tone = new int[3];
    private readonly int[] _volume = [15, 15, 15, 15];
    private readonly int[] _counter = new int[4];
    private readonly bool[] _output = new bool[4];
    private int _noiseControl, _latch;
    private int _lfsr = 0x8000;
    private double _clockAccumulator;
    private readonly double _ticksPerSample;

    public Sn76489(double sampleRate) => _ticksPerSample = ClockNtsc / 16 / sampleRate;

    private static int[] BuildVolumes()
    {
        var t = new int[16];
        for (int i = 0; i < 15; i++) t[i] = (int)(2800 * Math.Pow(10, -2.0 * i / 20));
        return t;
    }

    public void Write(int value)
    {
        if ((value & 0x80) != 0)
        {
            _latch = (value >> 4) & 7;
            int data = value & 0x0F;
            int ch = _latch >> 1;
            if ((_latch & 1) != 0) _volume[ch] = data;
            else if (ch < 3) _tone[ch] = (_tone[ch] & 0x3F0) | data;
            else { _noiseControl = data & 7; _lfsr = 0x8000; }
        }
        else
        {
            int ch = _latch >> 1;
            if ((_latch & 1) != 0) _volume[ch] = value & 0x0F;
            else if (ch < 3) _tone[ch] = (_tone[ch] & 0x0F) | ((value & 0x3F) << 4);
            else { _noiseControl = value & 7; _lfsr = 0x8000; }
        }
    }

    /// <summary>
    /// One output sample (mono): each channel's level averaged over the PSG ticks in this sample's period, so square
    /// waves and noise above the output rate do not alias back into the audible range.
    /// </summary>
    public int Clock()
    {
        _clockAccumulator += _ticksPerSample;
        int ticks = (int)_clockAccumulator;
        _clockAccumulator -= ticks;
        if (ticks == 0) return Level();
        int total = 0;
        for (int t = 0; t < ticks; t++)
        {
            for (int c = 0; c < 3; c++)
            {
                if (--_counter[c] > 0) continue;
                _counter[c] = _tone[c] == 0 ? 1 : _tone[c];
                _output[c] = !_output[c];
            }
            if (--_counter[3] <= 0)
            {
                int rate = _noiseControl & 3;
                _counter[3] = rate == 3 ? Math.Max(_tone[2], 1) * 2 : 0x20 << rate;   // one shift per two counter periods
                bool white = (_noiseControl & 4) != 0;
                int bit = white ? ((_lfsr & 1) ^ ((_lfsr >> 3) & 1)) : _lfsr & 1;
                _lfsr = (_lfsr >> 1) | (bit << 15);
                _output[3] = (_lfsr & 1) != 0;
            }
            total += Level();
        }
        return total / ticks;
    }

    private int Level()
    {
        int sum = 0;
        for (int c = 0; c < 3; c++)
            if (_tone[c] > 1) sum += _output[c] ? VolumeTable[_volume[c]] : -VolumeTable[_volume[c]];
        sum += _output[3] ? VolumeTable[_volume[3]] : -VolumeTable[_volume[3]];
        return sum;
    }
}
