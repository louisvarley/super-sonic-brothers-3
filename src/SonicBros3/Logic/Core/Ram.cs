using System.Buffers.Binary;

namespace SonicBros3.Logic;

/// <summary>
/// The 64 KiB of 68k work RAM ($FF0000-$FFFFFF), big-endian, addressed by the low 16 bits.
/// Game logic keeps its state here, laid out as the original does, so byte/word aliasing behaves identically and
/// a state can be loaded straight from an emulator RAM dump for validation.
/// </summary>
public sealed class Ram
{
    public readonly byte[] Bytes = new byte[0x10000];

    private SonicObject? _sonic, _tails;
    /// <summary>The main character's slot ($B000).</summary>
    public SonicObject Sonic => _sonic ??= new SonicObject(this, Vars.MainCharacter);
    /// <summary>The sidekick's slot ($B040).</summary>
    public SonicObject Tails => _tails ??= new SonicObject(this, Vars.Sidekick);

    public byte U8(int a) => Bytes[a & 0xFFFF];
    public sbyte S8(int a) => (sbyte)Bytes[a & 0xFFFF];
    public ushort U16(int a) => BinaryPrimitives.ReadUInt16BigEndian(Bytes.AsSpan(a & 0xFFFF));
    public short S16(int a) => (short)U16(a);
    public uint U32(int a) => BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan(a & 0xFFFF));
    public int S32(int a) => (int)U32(a);

    public void W8(int a, int v) => Bytes[a & 0xFFFF] = (byte)v;
    public void W16(int a, int v) => BinaryPrimitives.WriteUInt16BigEndian(Bytes.AsSpan(a & 0xFFFF), (ushort)v);
    public void W32(int a, long v) => BinaryPrimitives.WriteUInt32BigEndian(Bytes.AsSpan(a & 0xFFFF), (uint)v);

    public bool Bit(int a, int bit) => (Bytes[a & 0xFFFF] & (1 << bit)) != 0;
    public void Set(int a, int bit) => Bytes[a & 0xFFFF] |= (byte)(1 << bit);
    public void Clear(int a, int bit) => Bytes[a & 0xFFFF] &= (byte)~(1 << bit);

    /// <summary>bset: sets the bit and returns its previous state.</summary>
    public bool TestSet(int a, int bit) { bool was = Bit(a, bit); Set(a, bit); return was; }
    /// <summary>bclr: clears the bit and returns its previous state.</summary>
    public bool TestClear(int a, int bit) { bool was = Bit(a, bit); Clear(a, bit); return was; }

    public void Load(ReadOnlySpan<byte> dump)
    {
        if (dump.Length != Bytes.Length) throw new ArgumentException("RAM dump must be 64 KiB.");
        dump.CopyTo(Bytes);
    }
}
