using System.Buffers.Binary;
using System.Security.Cryptography;

namespace S2Port.Extraction;

/// <summary>Read-only view of a raw big-endian Mega Drive ROM image.</summary>
public sealed class RomImage
{
    private readonly byte[] _data;

    public RomImage(byte[] data)
    {
        _data = data;
        Sha1 = Convert.ToHexStringLower(SHA1.HashData(data));
    }

    public static RomImage Load(string path) => new(File.ReadAllBytes(path));

    public int Length => _data.Length;
    public string Sha1 { get; }
    public ReadOnlySpan<byte> Span => _data;

    public byte U8(int addr) => _data[addr];
    public ushort U16(int addr) => BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(addr));
    public uint U32(int addr) => BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(addr));
    public ReadOnlySpan<byte> Slice(int addr, int length) => _data.AsSpan(addr, length);

    /// <summary>Sum of big-endian words from 0x200 to end, as checked by the boot code.</summary>
    public ushort ComputeChecksum()
    {
        ushort sum = 0;
        for (int a = 0x200; a + 1 < _data.Length; a += 2) sum += U16(a);
        return sum;
    }

    public ushort HeaderChecksum => U16(0x18E);
}
