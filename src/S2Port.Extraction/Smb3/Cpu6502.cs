namespace S2Port.Extraction.Smb3;

/// <summary>
/// A plain NMOS 6502 (official opcodes; no decimal mode, as the NES's 2A03) over an MMC3 cartridge: 2 KiB RAM mirrored
/// to $1FFF, 8 KiB PRG RAM at $6000, PRG banks at $8000/$A000/$C000 (selected by the MMC3's R6/R7 or set directly) and
/// the last bank at $E000. PPU/APU registers read 0 and ignore writes. For running original SMB3 routines: the level
/// loader, to check the ports against (tools/Validate smb3-level-6502), and the bonus room's one-screen layout,
/// which Sonic Bros. 3 builds with SMB3's own loader rather than a port (Smb3Assets).
/// </summary>
public sealed class Cpu6502
{
    private readonly NesRom _rom;
    public byte[] Ram { get; } = new byte[0x800];
    public byte[] PrgRam { get; } = new byte[0x2000];
    /// <summary>The PRG banks mapped at $8000, $A000, $C000, $E000.</summary>
    public int[] Banks { get; } = [30, 0, 0, 31];
    private int _mmc3Select;

    public byte A, X, Y, S = 0xFD;
    public int PC;
    private bool _c, _z, _i = true, _d, _v, _n;
    public long Steps { get; private set; }
    public Action<int, byte>? ApuWrite { get; set; }

    public Cpu6502(NesRom rom) => _rom = rom;

    public byte Read(int a)
    {
        a &= 0xFFFF;
        if (a < 0x2000) return Ram[a & 0x7FF];
        if (a < 0x6000) return 0;
        if (a < 0x8000) return PrgRam[a - 0x6000];
        return _rom.Prg(Banks[(a - 0x8000) >> 13], a);
    }

    public void Write(int a, byte v)
    {
        a &= 0xFFFF;
        if (a < 0x2000) Ram[a & 0x7FF] = v;
        else if (a is >= 0x4000 and <= 0x4017) ApuWrite?.Invoke(a, v);
        else if (a is >= 0x6000 and < 0x8000) PrgRam[a - 0x6000] = v;
        else if (a is >= 0x8000 and < 0xA000)
        {
            if ((a & 1) == 0) _mmc3Select = v;
            else if ((_mmc3Select & 7) == 6) Banks[(_mmc3Select & 0x40) != 0 ? 2 : 0] = v & 0x3F;
            else if ((_mmc3Select & 7) == 7) Banks[1] = v & 0x3F;
        }
    }

    /// <summary>JSR to <paramref name="address"/> and run until it returns (or <paramref name="maxSteps"/>).</summary>
    public void Call(int address, long maxSteps = 50_000_000)
    {
        const int sentinel = 0x0400;
        Push((byte)((sentinel - 1) >> 8));
        Push(unchecked((byte)(sentinel - 1)));
        PC = address;
        while (PC != sentinel)
        {
            if (Steps++ > maxSteps) throw new InvalidOperationException($"no return after {maxSteps} steps (PC ${PC:X4})");
            Step();
        }
    }

    private void Push(byte v) { Write(0x100 + S, v); S--; }
    private byte Pull() { S++; return Read(0x100 + S); }
    private int Fetch() => Read(PC++);
    private int Fetch16() { int lo = Fetch(); return lo | (Fetch() << 8); }
    private int Read16Zp(int a) => Read(a & 0xFF) | (Read((a + 1) & 0xFF) << 8);
    private int Read16Bug(int a) => Read(a) | (Read((a & 0xFF00) | ((a + 1) & 0xFF)) << 8);

    private byte P(bool brk) => (byte)((_n ? 0x80 : 0) | (_v ? 0x40 : 0) | 0x20 | (brk ? 0x10 : 0) | (_d ? 8 : 0) |
                                       (_i ? 4 : 0) | (_z ? 2 : 0) | (_c ? 1 : 0));
    private void SetP(byte p)
    {
        _n = (p & 0x80) != 0; _v = (p & 0x40) != 0; _d = (p & 8) != 0; _i = (p & 4) != 0; _z = (p & 2) != 0; _c = (p & 1) != 0;
    }
    private byte NZ(int v) { byte b = (byte)v; _z = b == 0; _n = (b & 0x80) != 0; return b; }

    // Addressing modes: the effective address.
    private int Zp() => Fetch();
    private int ZpX() => (Fetch() + X) & 0xFF;
    private int ZpY() => (Fetch() + Y) & 0xFF;
    private int Abs() => Fetch16();
    private int AbsX() => (Fetch16() + X) & 0xFFFF;
    private int AbsY() => (Fetch16() + Y) & 0xFFFF;
    private int IndX() => Read16Zp(Fetch() + X);
    private int IndY() => (Read16Zp(Fetch()) + Y) & 0xFFFF;

    private void Adc(int m)
    {
        int r = A + m + (_c ? 1 : 0);
        _v = ((A ^ r) & (m ^ r) & 0x80) != 0;
        _c = r > 0xFF;
        A = NZ(r);
    }
    private void Sbc(int m) => Adc(m ^ 0xFF);
    private void Cmp(int reg, int m) { _c = reg >= m; NZ(reg - m); }
    private void Bit(int m) { _z = (A & m) == 0; _n = (m & 0x80) != 0; _v = (m & 0x40) != 0; }
    private void Branch(bool take) { int o = (sbyte)Fetch(); if (take) PC = (PC + o) & 0xFFFF; }
    private byte Asl(int v) { _c = (v & 0x80) != 0; return NZ(v << 1); }
    private byte Lsr(int v) { _c = (v & 1) != 0; return NZ(v >> 1); }
    private byte Rol(int v) { int r = (v << 1) | (_c ? 1 : 0); _c = (v & 0x80) != 0; return NZ(r); }
    private byte Ror(int v) { int r = (v >> 1) | (_c ? 0x80 : 0); _c = (v & 1) != 0; return NZ(r); }
    private void Rmw(int a, Func<int, byte> f) => Write(a, f(Read(a)));

    public void Step()
    {
        int op = Fetch();
        switch (op)
        {
            // Loads and stores.
            case 0xA9: A = NZ(Fetch()); break;
            case 0xA5: A = NZ(Read(Zp())); break;
            case 0xB5: A = NZ(Read(ZpX())); break;
            case 0xAD: A = NZ(Read(Abs())); break;
            case 0xBD: A = NZ(Read(AbsX())); break;
            case 0xB9: A = NZ(Read(AbsY())); break;
            case 0xA1: A = NZ(Read(IndX())); break;
            case 0xB1: A = NZ(Read(IndY())); break;
            case 0xA2: X = NZ(Fetch()); break;
            case 0xA6: X = NZ(Read(Zp())); break;
            case 0xB6: X = NZ(Read(ZpY())); break;
            case 0xAE: X = NZ(Read(Abs())); break;
            case 0xBE: X = NZ(Read(AbsY())); break;
            case 0xA0: Y = NZ(Fetch()); break;
            case 0xA4: Y = NZ(Read(Zp())); break;
            case 0xB4: Y = NZ(Read(ZpX())); break;
            case 0xAC: Y = NZ(Read(Abs())); break;
            case 0xBC: Y = NZ(Read(AbsX())); break;
            case 0x85: Write(Zp(), A); break;
            case 0x95: Write(ZpX(), A); break;
            case 0x8D: Write(Abs(), A); break;
            case 0x9D: Write(AbsX(), A); break;
            case 0x99: Write(AbsY(), A); break;
            case 0x81: Write(IndX(), A); break;
            case 0x91: Write(IndY(), A); break;
            case 0x86: Write(Zp(), X); break;
            case 0x96: Write(ZpY(), X); break;
            case 0x8E: Write(Abs(), X); break;
            case 0x84: Write(Zp(), Y); break;
            case 0x94: Write(ZpX(), Y); break;
            case 0x8C: Write(Abs(), Y); break;
            // Transfers and stack.
            case 0xAA: X = NZ(A); break;
            case 0xA8: Y = NZ(A); break;
            case 0x8A: A = NZ(X); break;
            case 0x98: A = NZ(Y); break;
            case 0xBA: X = NZ(S); break;
            case 0x9A: S = X; break;
            case 0x48: Push(A); break;
            case 0x68: A = NZ(Pull()); break;
            case 0x08: Push(P(true)); break;
            case 0x28: SetP(Pull()); break;
            // Arithmetic and logic.
            case 0x69: Adc(Fetch()); break;
            case 0x65: Adc(Read(Zp())); break;
            case 0x75: Adc(Read(ZpX())); break;
            case 0x6D: Adc(Read(Abs())); break;
            case 0x7D: Adc(Read(AbsX())); break;
            case 0x79: Adc(Read(AbsY())); break;
            case 0x61: Adc(Read(IndX())); break;
            case 0x71: Adc(Read(IndY())); break;
            case 0xE9: Sbc(Fetch()); break;
            case 0xE5: Sbc(Read(Zp())); break;
            case 0xF5: Sbc(Read(ZpX())); break;
            case 0xED: Sbc(Read(Abs())); break;
            case 0xFD: Sbc(Read(AbsX())); break;
            case 0xF9: Sbc(Read(AbsY())); break;
            case 0xE1: Sbc(Read(IndX())); break;
            case 0xF1: Sbc(Read(IndY())); break;
            case 0x29: A = NZ(A & Fetch()); break;
            case 0x25: A = NZ(A & Read(Zp())); break;
            case 0x35: A = NZ(A & Read(ZpX())); break;
            case 0x2D: A = NZ(A & Read(Abs())); break;
            case 0x3D: A = NZ(A & Read(AbsX())); break;
            case 0x39: A = NZ(A & Read(AbsY())); break;
            case 0x21: A = NZ(A & Read(IndX())); break;
            case 0x31: A = NZ(A & Read(IndY())); break;
            case 0x09: A = NZ(A | Fetch()); break;
            case 0x05: A = NZ(A | Read(Zp())); break;
            case 0x15: A = NZ(A | Read(ZpX())); break;
            case 0x0D: A = NZ(A | Read(Abs())); break;
            case 0x1D: A = NZ(A | Read(AbsX())); break;
            case 0x19: A = NZ(A | Read(AbsY())); break;
            case 0x01: A = NZ(A | Read(IndX())); break;
            case 0x11: A = NZ(A | Read(IndY())); break;
            case 0x49: A = NZ(A ^ Fetch()); break;
            case 0x45: A = NZ(A ^ Read(Zp())); break;
            case 0x55: A = NZ(A ^ Read(ZpX())); break;
            case 0x4D: A = NZ(A ^ Read(Abs())); break;
            case 0x5D: A = NZ(A ^ Read(AbsX())); break;
            case 0x59: A = NZ(A ^ Read(AbsY())); break;
            case 0x41: A = NZ(A ^ Read(IndX())); break;
            case 0x51: A = NZ(A ^ Read(IndY())); break;
            case 0xC9: Cmp(A, Fetch()); break;
            case 0xC5: Cmp(A, Read(Zp())); break;
            case 0xD5: Cmp(A, Read(ZpX())); break;
            case 0xCD: Cmp(A, Read(Abs())); break;
            case 0xDD: Cmp(A, Read(AbsX())); break;
            case 0xD9: Cmp(A, Read(AbsY())); break;
            case 0xC1: Cmp(A, Read(IndX())); break;
            case 0xD1: Cmp(A, Read(IndY())); break;
            case 0xE0: Cmp(X, Fetch()); break;
            case 0xE4: Cmp(X, Read(Zp())); break;
            case 0xEC: Cmp(X, Read(Abs())); break;
            case 0xC0: Cmp(Y, Fetch()); break;
            case 0xC4: Cmp(Y, Read(Zp())); break;
            case 0xCC: Cmp(Y, Read(Abs())); break;
            case 0x24: Bit(Read(Zp())); break;
            case 0x2C: Bit(Read(Abs())); break;
            // Increments and decrements.
            case 0xE6: { int a = Zp(); Rmw(a, v => NZ(v + 1)); break; }
            case 0xF6: { int a = ZpX(); Rmw(a, v => NZ(v + 1)); break; }
            case 0xEE: { int a = Abs(); Rmw(a, v => NZ(v + 1)); break; }
            case 0xFE: { int a = AbsX(); Rmw(a, v => NZ(v + 1)); break; }
            case 0xC6: { int a = Zp(); Rmw(a, v => NZ(v - 1)); break; }
            case 0xD6: { int a = ZpX(); Rmw(a, v => NZ(v - 1)); break; }
            case 0xCE: { int a = Abs(); Rmw(a, v => NZ(v - 1)); break; }
            case 0xDE: { int a = AbsX(); Rmw(a, v => NZ(v - 1)); break; }
            case 0xE8: X = NZ(X + 1); break;
            case 0xC8: Y = NZ(Y + 1); break;
            case 0xCA: X = NZ(X - 1); break;
            case 0x88: Y = NZ(Y - 1); break;
            // Shifts.
            case 0x0A: A = Asl(A); break;
            case 0x06: { int a = Zp(); Rmw(a, Asl); break; }
            case 0x16: { int a = ZpX(); Rmw(a, Asl); break; }
            case 0x0E: { int a = Abs(); Rmw(a, Asl); break; }
            case 0x1E: { int a = AbsX(); Rmw(a, Asl); break; }
            case 0x4A: A = Lsr(A); break;
            case 0x46: { int a = Zp(); Rmw(a, Lsr); break; }
            case 0x56: { int a = ZpX(); Rmw(a, Lsr); break; }
            case 0x4E: { int a = Abs(); Rmw(a, Lsr); break; }
            case 0x5E: { int a = AbsX(); Rmw(a, Lsr); break; }
            case 0x2A: A = Rol(A); break;
            case 0x26: { int a = Zp(); Rmw(a, Rol); break; }
            case 0x36: { int a = ZpX(); Rmw(a, Rol); break; }
            case 0x2E: { int a = Abs(); Rmw(a, Rol); break; }
            case 0x3E: { int a = AbsX(); Rmw(a, Rol); break; }
            case 0x6A: A = Ror(A); break;
            case 0x66: { int a = Zp(); Rmw(a, Ror); break; }
            case 0x76: { int a = ZpX(); Rmw(a, Ror); break; }
            case 0x6E: { int a = Abs(); Rmw(a, Ror); break; }
            case 0x7E: { int a = AbsX(); Rmw(a, Ror); break; }
            // Jumps, calls, branches.
            case 0x4C: PC = Fetch16(); break;
            case 0x6C: PC = Read16Bug(Fetch16()); break;
            case 0x20: { int t = Fetch16(); int r = PC - 1; Push((byte)(r >> 8)); Push((byte)r); PC = t; break; }
            case 0x60: { int lo = Pull(); PC = ((Pull() << 8) | lo) + 1; break; }
            case 0x40: { SetP(Pull()); int lo = Pull(); PC = (Pull() << 8) | lo; break; }
            case 0x10: Branch(!_n); break;
            case 0x30: Branch(_n); break;
            case 0x50: Branch(!_v); break;
            case 0x70: Branch(_v); break;
            case 0x90: Branch(!_c); break;
            case 0xB0: Branch(_c); break;
            case 0xD0: Branch(!_z); break;
            case 0xF0: Branch(_z); break;
            // Flags.
            case 0x18: _c = false; break;
            case 0x38: _c = true; break;
            case 0x58: _i = false; break;
            case 0x78: _i = true; break;
            case 0xB8: _v = false; break;
            case 0xD8: _d = false; break;
            case 0xF8: _d = true; break;
            case 0xEA: break;
            default: throw new InvalidOperationException($"opcode ${op:X2} at ${(PC - 1) & 0xFFFF:X4} not emulated");
        }
    }
}
