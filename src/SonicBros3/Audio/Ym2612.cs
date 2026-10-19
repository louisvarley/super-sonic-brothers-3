namespace SonicBros3.Audio;

/// <summary>
/// YM2612 FM synthesis, sampled at the chip's native rate (clock / 144, ~53267 Hz on NTSC).
/// Phase and envelope generators follow the hardware as documented by Nemesis and modelled by MAME's fm2612.c:
/// 20-bit phase accumulators with the detune table, the 3-sample envelope clock with its rate/shift/increment
/// tables, and log-sine + exponent tables for the operator output. The LFO is not emulated (the driver never enables
/// it: register $22 is not written), nor is SSG-EG or channel 3's special mode ($27 is only ever written as 0).
/// The DAC (registers $2A/$2B) replaces channel 6's output.
/// </summary>
public sealed class Ym2612
{
    public const double ClockNtsc = 7670453;
    public static double SampleRate => ClockNtsc / 144;

    private const int SinBits = 10, SinLen = 1 << SinBits, SinMask = SinLen - 1;
    private const int TlResLen = 256, TlTabLen = 13 * 2 * TlResLen;
    private const int EnvQuiet = TlTabLen >> 3;
    private const int MaxAtt = 1023;

    private static readonly int[] TlTab = new int[TlTabLen];
    private static readonly int[] SinTab = new int[SinLen];

    private static readonly byte[] DetuneTable =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 6, 6, 7, 8, 8, 8, 8,
        1, 1, 1, 1, 2, 2, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 6, 6, 7, 8, 8, 9, 10, 11, 12, 13, 14, 16, 16, 16, 16,
        2, 2, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 6, 6, 7, 8, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 20, 22, 22, 22, 22,
    ];

    private static readonly byte[] EgInc =
    [
        0, 1, 0, 1, 0, 1, 0, 1,
        0, 1, 0, 1, 1, 1, 0, 1,
        0, 1, 1, 1, 0, 1, 1, 1,
        0, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 2, 1, 1, 1, 2,
        1, 2, 1, 2, 1, 2, 1, 2,
        1, 2, 2, 2, 1, 2, 2, 2,
        2, 2, 2, 2, 2, 2, 2, 2,
        2, 2, 2, 4, 2, 2, 2, 4,
        2, 4, 2, 4, 2, 4, 2, 4,
        2, 4, 4, 4, 2, 4, 4, 4,
        4, 4, 4, 4, 4, 4, 4, 4,
        4, 4, 4, 8, 4, 4, 4, 8,
        4, 8, 4, 8, 4, 8, 4, 8,
        4, 8, 8, 8, 4, 8, 8, 8,
        8, 8, 8, 8, 8, 8, 8, 8,
        16, 16, 16, 16, 16, 16, 16, 16,
        0, 0, 0, 0, 0, 0, 0, 0,
    ];

    /// <summary>Per rate (0-63, plus 32 below for "rate 0" and 32 above): EgInc row * 8, and the counter shift.</summary>
    private static readonly int[] RateSelect = new int[128];
    private static readonly int[] RateShift = new int[128];

    static Ym2612()
    {
        for (int x = 0; x < TlResLen; x++)
        {
            double m = Math.Floor((1 << 16) / Math.Pow(2, (x + 1) * (1.0 / 32) / 8));
            int n = (int)m >> 4;
            n = (n >> 1) + (n & 1);
            n <<= 2;
            for (int i = 0; i < 13; i++)
            {
                TlTab[x * 2 + i * 2 * TlResLen] = n >> i;
                TlTab[x * 2 + 1 + i * 2 * TlResLen] = -(n >> i);
            }
        }
        for (int i = 0; i < SinLen; i++)
        {
            double m = Math.Sin((i * 2 + 1) * Math.PI / SinLen);
            double o = 8 * Math.Log(1.0 / Math.Abs(m)) / Math.Log(2);
            o /= 1.0 / 32;
            int n = (int)(2 * o);
            n = (n >> 1) + (n & 1);
            SinTab[i] = n * 2 + (m >= 0 ? 0 : 1);
        }
        for (int r = 0; r < 128; r++)
        {
            int rate = r - 32;
            int row, shift;
            if (rate < 0) { row = 18; shift = 0; }
            else if (rate < 48)
            {
                int whole = rate >> 2, frac = rate & 3;
                row = whole == 0 ? (frac < 2 ? 18 : 0) : whole == 1 ? (frac < 2 ? 0 : 2) : frac;
                shift = 11 - whole;
            }
            else if (rate < 60) { row = 4 + (rate - 48); shift = 0; }
            else { row = 16; shift = 0; }
            RateSelect[r] = row * 8;
            RateShift[r] = shift;
        }
    }

    private enum EgPhase { Off, Release, Sustain, Decay, Attack }

    private sealed class Operator
    {
        public int Dt, Mul = 1, Tl, Ks, Ar, D1r, D2r, Sl, Rr;
        public bool KeyOn;
        public uint Phase;
        public int Volume = MaxAtt;
        public EgPhase State = EgPhase.Off;
        public int Ksr;
        public int Increment;
        // Effective rates (with the +32 table offset) after key scaling.
        public int ArRate, D1Rate, D2Rate, RrRate;
    }

    private sealed class Channel
    {
        public readonly Operator[] Ops = [new(), new(), new(), new()];   // register order: op1, op3, op2, op4
        public int Fnum, Block, Algorithm, Feedback;
        public bool Left = true, Right = true;
        public int Op1Out0, Op1Out1;
        public int KeyCode;
    }

    private readonly Channel[] _ch = [new(), new(), new(), new(), new(), new()];
    private readonly int[] _fnumLatch = new int[6];
    private int _egCounter, _egTimer;
    private int _dac = 0x80;
    private bool _dacEnabled;

    /// <summary>The DAC value ($2A), also driven directly by the DAC sample player.</summary>
    public int Dac { get => _dac; set => _dac = value & 0xFF; }

    public void Write(int part, int reg, int value)
    {
        value &= 0xFF;
        if (part == 0 && reg < 0x30)
        {
            switch (reg)
            {
                case 0x28:
                {
                    int c = value & 3;
                    if (c == 3) return;
                    if ((value & 4) != 0) c += 3;
                    var ch = _ch[c];
                    // Bits 4-7 are operators 1-4; our array is in register order (op1, op3, op2, op4).
                    SetKey(ch.Ops[0], (value & 0x10) != 0);
                    SetKey(ch.Ops[2], (value & 0x20) != 0);
                    SetKey(ch.Ops[1], (value & 0x40) != 0);
                    SetKey(ch.Ops[3], (value & 0x80) != 0);
                    return;
                }
                case 0x2A: _dac = value; return;
                case 0x2B: _dacEnabled = (value & 0x80) != 0; return;
                default: return;
            }
        }
        int chIndex = reg & 3;
        if (chIndex == 3) return;
        chIndex += part * 3;
        var chan = _ch[chIndex];
        if (reg < 0xA0)
        {
            var op = chan.Ops[(reg >> 2) & 3];
            switch (reg & 0xF0)
            {
                case 0x30: op.Dt = (value >> 4) & 7; op.Mul = value & 15; break;
                case 0x40: op.Tl = value & 0x7F; break;
                case 0x50: op.Ks = value >> 6; op.Ar = value & 0x1F; break;
                case 0x60: op.D1r = value & 0x1F; break;
                case 0x70: op.D2r = value & 0x1F; break;
                case 0x80:
                {
                    int sl = value >> 4;
                    op.Sl = sl == 15 ? 0x3E0 : sl << 5;
                    op.Rr = value & 15;
                    break;
                }
                default: return;   // $90 SSG-EG: not emulated
            }
            UpdateChannel(chan);
            return;
        }
        switch (reg & 0xFC)
        {
            case 0xA0:
                chan.Fnum = ((_fnumLatch[chIndex] & 7) << 8) | value;
                chan.Block = (_fnumLatch[chIndex] >> 3) & 7;
                UpdateChannel(chan);
                break;
            case 0xA4: _fnumLatch[chIndex] = value & 0x3F; break;
            case 0xB0: chan.Algorithm = value & 7; chan.Feedback = (value >> 3) & 7; break;
            case 0xB4: chan.Left = (value & 0x80) != 0; chan.Right = (value & 0x40) != 0; break;
        }
    }

    private static void SetKey(Operator op, bool on)
    {
        if (on && !op.KeyOn)
        {
            op.Phase = 0;
            op.State = EgPhase.Attack;
            if (op.ArRate >= 32 + 62) { op.Volume = 0; op.State = EgPhase.Decay; }
        }
        else if (!on && op.KeyOn && op.State != EgPhase.Off) op.State = EgPhase.Release;
        op.KeyOn = on;
    }

    private static void UpdateChannel(Channel ch)
    {
        int f = ch.Fnum;
        int n4 = (f >> 10) & 1;
        int n3 = ((f >> 10) & 1) & (((f >> 9) | (f >> 8) | (f >> 7)) & 1) | (~(f >> 10) & 1) & ((f >> 9) & (f >> 8) & (f >> 7) & 1);
        ch.KeyCode = (ch.Block << 2) | (n4 << 1) | n3;
        int fc = (f << ch.Block) >> 1;
        foreach (var op in ch.Ops)
        {
            int dt = DetuneTable[(op.Dt & 3) * 32 + ch.KeyCode];
            if ((op.Dt & 4) != 0) dt = -dt;
            int inc = (fc + dt) & 0x1FFFF;
            op.Increment = op.Mul == 0 ? inc >> 1 : (inc * op.Mul) & 0xFFFFF;
            op.Ksr = ch.KeyCode >> (3 - op.Ks);
            op.ArRate = op.Ar == 0 ? 0 : 32 + (op.Ar << 1) + op.Ksr;
            op.D1Rate = op.D1r == 0 ? 0 : 32 + (op.D1r << 1) + op.Ksr;
            op.D2Rate = op.D2r == 0 ? 0 : 32 + (op.D2r << 1) + op.Ksr;
            op.RrRate = 32 + (op.Rr << 2) + 2 + op.Ksr;
            op.ArRate = Math.Min(op.ArRate, 127);
            op.D1Rate = Math.Min(op.D1Rate, 127);
            op.D2Rate = Math.Min(op.D2Rate, 127);
            op.RrRate = Math.Min(op.RrRate, 127);
        }
    }

    private void ClockEnvelopes()
    {
        _egCounter++;
        foreach (var ch in _ch)
        foreach (var op in ch.Ops)
        {
            switch (op.State)
            {
                case EgPhase.Attack:
                {
                    int r = op.ArRate;
                    if ((_egCounter & ((1 << RateShift[r]) - 1)) != 0) break;
                    int inc = EgInc[RateSelect[r] + ((_egCounter >> RateShift[r]) & 7)];
                    op.Volume += (~op.Volume * inc) >> 4;
                    if (op.Volume <= 0) { op.Volume = 0; op.State = EgPhase.Decay; }
                    break;
                }
                case EgPhase.Decay:
                {
                    int r = op.D1Rate;
                    if ((_egCounter & ((1 << RateShift[r]) - 1)) == 0)
                        op.Volume += EgInc[RateSelect[r] + ((_egCounter >> RateShift[r]) & 7)];
                    if (op.Volume >= op.Sl) op.State = EgPhase.Sustain;
                    break;
                }
                case EgPhase.Sustain:
                {
                    int r = op.D2Rate;
                    if ((_egCounter & ((1 << RateShift[r]) - 1)) != 0) break;
                    op.Volume += EgInc[RateSelect[r] + ((_egCounter >> RateShift[r]) & 7)];
                    if (op.Volume >= MaxAtt) op.Volume = MaxAtt;
                    break;
                }
                case EgPhase.Release:
                {
                    int r = op.RrRate;
                    if ((_egCounter & ((1 << RateShift[r]) - 1)) != 0) break;
                    op.Volume += EgInc[RateSelect[r] + ((_egCounter >> RateShift[r]) & 7)];
                    if (op.Volume >= MaxAtt) { op.Volume = MaxAtt; op.State = EgPhase.Off; }
                    break;
                }
            }
        }
    }

    /// <summary>Operator output for a phase offset given in sine-table units.</summary>
    private static int OpCalc(Operator op, int modulation)
    {
        int env = op.Volume + (op.Tl << 3);
        if (env >= EnvQuiet) return 0;
        int p = (env << 3) + SinTab[((int)(op.Phase >> 10) + modulation) & SinMask];
        return p >= TlTabLen ? 0 : TlTab[p];
    }

    /// <summary>Renders one native-rate sample (stereo, each channel's output within ±8191).</summary>
    public void Clock(out int left, out int right)
    {
        if (++_egTimer >= 3) { _egTimer = 0; ClockEnvelopes(); }
        left = right = 0;
        for (int c = 0; c < 6; c++)
        {
            var ch = _ch[c];
            int output;
            if (c == 5 && _dacEnabled) output = (_dac - 0x80) << 6;
            else output = ChannelOutput(ch);
            foreach (var op in ch.Ops) op.Phase = (op.Phase + (uint)op.Increment) & 0xFFFFF;
            if (output > 8191) output = 8191; else if (output < -8192) output = -8192;
            if (ch.Left) left += output;
            if (ch.Right) right += output;
        }
    }

    private static int ChannelOutput(Channel ch)
    {
        var o = ch.Ops;   // [0] = op1, [1] = op3, [2] = op2, [3] = op4
        int fbInput = ch.Feedback != 0 ? (ch.Op1Out0 + ch.Op1Out1) >> (10 - ch.Feedback) : 0;
        int op1 = OpCalc(o[0], fbInput);
        ch.Op1Out0 = ch.Op1Out1;
        ch.Op1Out1 = op1;
        int m(int v) => v >> 1;
        switch (ch.Algorithm)
        {
            case 0: { int op2 = OpCalc(o[2], m(op1)); int op3 = OpCalc(o[1], m(op2)); return OpCalc(o[3], m(op3)); }
            case 1: { int op2 = OpCalc(o[2], 0); int op3 = OpCalc(o[1], m(op1 + op2)); return OpCalc(o[3], m(op3)); }
            case 2: { int op2 = OpCalc(o[2], 0); int op3 = OpCalc(o[1], m(op2)); return OpCalc(o[3], m(op1 + op3)); }
            case 3: { int op2 = OpCalc(o[2], m(op1)); int op3 = OpCalc(o[1], 0); return OpCalc(o[3], m(op2 + op3)); }
            case 4: { int op2 = OpCalc(o[2], m(op1)); int op3 = OpCalc(o[1], 0); return op2 + OpCalc(o[3], m(op3)); }
            case 5: return OpCalc(o[2], m(op1)) + OpCalc(o[1], m(op1)) + OpCalc(o[3], m(op1));
            case 6: return OpCalc(o[2], m(op1)) + OpCalc(o[1], 0) + OpCalc(o[3], 0);
            default: return op1 + OpCalc(o[2], 0) + OpCalc(o[1], 0) + OpCalc(o[3], 0);
        }
    }
}
