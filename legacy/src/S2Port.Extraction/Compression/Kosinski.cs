namespace S2Port.Extraction.Compression;

/// <summary>
/// Kosinski decompressor, matching KosDec at ROM 0x1B0E.
/// Descriptor fields are 16-bit little-endian, consumed LSB first; a new field is
/// fetched as soon as the previous one is exhausted (before any data bytes are read).
/// </summary>
public static class Kosinski
{
    public static byte[] Decompress(ReadOnlySpan<byte> src, out int consumed)
    {
        var output = new List<byte>(0x8000);
        int pos = 0;
        int field = 0, bitsLeft = 0;

        void Load(ReadOnlySpan<byte> s)
        {
            field = s[pos] | (s[pos + 1] << 8);
            pos += 2;
            bitsLeft = 16;
        }

        int Bit(ReadOnlySpan<byte> s)
        {
            int b = field & 1;
            field >>= 1;
            if (--bitsLeft == 0) Load(s);
            return b;
        }

        Load(src);
        while (true)
        {
            if (Bit(src) == 1)
            {
                output.Add(src[pos++]);
                continue;
            }

            int count, offset;
            if (Bit(src) == 1)
            {
                // Full-format match: 13-bit offset, 3-bit count (or extended count byte).
                int lo = src[pos++], hi = src[pos++];
                offset = (((hi & 0xF8) << 5) | lo) - 0x2000;
                count = hi & 7;
                if (count != 0)
                {
                    count += 2;
                }
                else
                {
                    int ext = src[pos++];
                    if (ext == 0) break;     // end of stream
                    if (ext == 1) continue;  // padding / no-op
                    count = ext + 1;
                }
            }
            else
            {
                // Inline match: 2-bit count from descriptor, 8-bit offset.
                count = ((Bit(src) << 1) | Bit(src)) + 2;
                offset = src[pos++] - 0x100;
            }

            int from = output.Count + offset;
            if (from < 0) throw new InvalidDataException($"Kosinski back-reference before start (offset {offset}).");
            for (int i = 0; i < count; i++) output.Add(output[from + i]);
        }

        consumed = pos;
        return output.ToArray();
    }

    public static byte[] Decompress(RomImage rom, int addr, out int consumed) =>
        Decompress(rom.Span[addr..], out consumed);
}
