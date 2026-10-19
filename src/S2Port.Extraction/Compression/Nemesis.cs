namespace S2Port.Extraction.Compression;

/// <summary>
/// Nemesis decompressor, matching NemDec at ROM 0x1166. Produces 4bpp tile data (32 bytes/tile).
/// Header word: bit 15 = XOR mode, bits 0-14 = tile count. Then a code table, then an MSB-first bitstream.
/// </summary>
public static class Nemesis
{
    public static byte[] Decompress(ReadOnlySpan<byte> src, out int consumed)
    {
        int pos = 0;
        int header = (src[0] << 8) | src[1];
        pos = 2;
        bool xorMode = (header & 0x8000) != 0;
        int tileCount = header & 0x7FFF;

        // Code table: (length, code) -> (nibble, run length)
        var codes = new Dictionary<(int Len, int Code), (int Nibble, int Run)>();
        int nibble = 0;
        int b = src[pos++];
        while (b != 0xFF)
        {
            if ((b & 0x80) != 0)
            {
                nibble = b & 0x0F;
                b = src[pos++];
                continue;
            }
            int run = ((b >> 4) & 7) + 1;
            int len = b & 0x0F;
            int code = src[pos++];
            codes[(len, code)] = (nibble, run);
            b = src[pos++];
        }

        int bitBuf = 0, bitCount = 0;
        int ReadBits(ReadOnlySpan<byte> s, int n)
        {
            while (bitCount < n)
            {
                bitBuf = (bitBuf << 8) | s[pos++];
                bitCount += 8;
            }
            bitCount -= n;
            int v = (bitBuf >> bitCount) & ((1 << n) - 1);
            bitBuf &= (1 << bitCount) - 1;
            return v;
        }

        int totalNibbles = tileCount * 64;
        var nibbles = new byte[totalNibbles];
        int written = 0;
        while (written < totalNibbles)
        {
            int code = 0, len = 0, value, run;
            while (true)
            {
                code = (code << 1) | ReadBits(src, 1);
                len++;
                if (codes.TryGetValue((len, code), out var entry))
                {
                    (value, run) = entry;
                    break;
                }
                if (len == 6 && code == 0x3F)
                {
                    // Escape: 3-bit run-1, 4-bit nibble inline.
                    run = ReadBits(src, 3) + 1;
                    value = ReadBits(src, 4);
                    break;
                }
                if (len > 8) throw new InvalidDataException("Nemesis: unmatched code.");
            }
            for (int i = 0; i < run && written < totalNibbles; i++) nibbles[written++] = (byte)value;
        }

        var output = new byte[tileCount * 32];
        uint prev = 0;
        for (int row = 0; row < tileCount * 8; row++)
        {
            uint v = 0;
            for (int i = 0; i < 8; i++) v = (v << 4) | nibbles[row * 8 + i];
            if (xorMode) { v ^= prev; prev = v; }
            output[row * 4 + 0] = (byte)(v >> 24);
            output[row * 4 + 1] = (byte)(v >> 16);
            output[row * 4 + 2] = (byte)(v >> 8);
            output[row * 4 + 3] = (byte)v;
        }

        consumed = pos;
        return output;
    }

    public static byte[] Decompress(RomImage rom, int addr, out int consumed) =>
        Decompress(rom.Span[addr..], out consumed);
}
