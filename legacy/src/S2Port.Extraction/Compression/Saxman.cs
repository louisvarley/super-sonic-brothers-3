namespace S2Port.Extraction.Compression;

/// <summary>
/// Saxman decompressor (Sonic 2 Z80 driver and its songs). Descriptor bytes are read LSB first; 1 = literal,
/// 0 = back-reference {lo, hi}: offset = lo | (hi &amp; 0xF0) &lt;&lt; 4, count = (hi &amp; 0x0F) + 3, addressed in a
/// 4 KiB window starting 0x12 bytes in; references before the start of output produce zeros.
/// </summary>
public static class Saxman
{
    /// <summary>Stream with a 2-byte little-endian size header (songs; decompressor at Z80 0x1271).</summary>
    public static byte[] DecompressWithHeader(ReadOnlySpan<byte> src, out int consumed)
    {
        int size = src[0] | (src[1] << 8);
        var result = Decompress(src.Slice(2), size);
        consumed = size + 2;
        return result;
    }

    /// <summary>Headerless stream of known compressed length (the driver itself; length in the 68k loader).</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> src, int compressedLength)
    {
        var output = new List<byte>(0x2000);
        int p = 0, descriptor = 0, bits = 0;
        while (p < compressedLength)
        {
            if (bits == 0) { descriptor = src[p++]; bits = 8; }
            int bit = descriptor & 1;
            descriptor >>= 1;
            bits--;
            if (p >= compressedLength) break;
            if (bit == 1)
            {
                output.Add(src[p++]);
                continue;
            }
            int lo = src[p++], hi = src[p++];
            int offset = lo | ((hi & 0xF0) << 4);
            int count = (hi & 0x0F) + 3;
            int from = ((offset + 0x12) & 0xFFF) + (output.Count & ~0xFFF);
            if (from >= output.Count) from -= 0x1000;
            for (int i = 0; i < count; i++, from++)
                output.Add(from >= 0 ? output[from] : (byte)0);
        }
        return output.ToArray();
    }
}
