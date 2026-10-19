namespace S2Port.Extraction.Compression;

/// <summary>
/// Enigma decompressor (plane maps), matching EniDec at ROM 0x1990 register for register: a 6-byte header (inline
/// value width, which of priority / palette 1 / palette 0 / flip Y / flip X are stored inline, the incrementing start
/// word, the common word), then 6/7-bit entries read from a 16-bit bit buffer. <paramref name="artTile"/> is added to
/// every word, as d0 in the original.
/// </summary>
public static class Enigma
{
    private static readonly ushort[] AndVals =
        [1, 3, 7, 0xF, 0x1F, 0x3F, 0x7F, 0xFF, 0x1FF, 0x3FF, 0x7FF, 0xFFF, 0x1FFF, 0x3FFF, 0x7FFF, 0xFFFF];

    public static ushort[] Decompress(ReadOnlySpan<byte> src, int artTile = 0) => Decompress(src.ToArray(), artTile);

    public static ushort[] Decompress(byte[] src, int artTile)
    {
        var output = new List<ushort>(0x800);
        int a0 = 0;
        int a3 = artTile & 0xFFFF;
        int a5 = (sbyte)src[a0++];                       // inline value width, sign-extended (move.b / ext.w)
        int d4 = (src[a0++] << 3) & 0xFF;                // PCCVH flags << 3
        int a2 = ((src[a0] << 8 | src[a0 + 1]) + a3) & 0xFFFF; a0 += 2;
        int a4 = ((src[a0] << 8 | src[a0 + 1]) + a3) & 0xFFFF; a0 += 2;
        int d5 = (src[a0] << 8 | src[a0 + 1]) & 0xFFFF; a0 += 2;
        int d6 = 16;

        void ChkGetNextByte(int d0)
        {
            d6 -= d0;
            if (d6 >= 9) return;
            d6 += 8;
            d5 = ((d5 << 8) | src[a0++]) & 0xFFFF;
        }

        int GetInlineCopyVal()
        {
            int d3 = a3;
            int d1b = d4;
            bool Bit(int mask)
            {
                bool carry = (d1b & 0x80) != 0;
                d1b = (d1b << 1) & 0xFF;
                if (!carry) return false;
                d6--;
                return (d5 & (1 << d6)) != 0;
            }
            if (Bit(0)) d3 |= 0x8000;
            if (Bit(0)) d3 = (d3 + 0x4000) & 0xFFFF;
            if (Bit(0)) d3 = (d3 + 0x2000) & 0xFFFF;
            if (Bit(0)) d3 |= 0x1000;
            if (Bit(0)) d3 |= 0x0800;
            int d1 = d5;
            int d7 = d6 - a5;
            if (d7 < 0)
            {
                d6 = d7 + 16;
                int deficit = -d7;
                d1 = (d1 << deficit) & 0xFFFF;
                int b = src[a0];
                b = ((b << deficit) | (b >> (8 - deficit))) & 0xFF;   // rol.b
                b &= AndVals[deficit - 1];
                d1 = (d1 + b) & 0xFFFF;
                d1 = (d1 & AndVals[a5 - 1]) + d3;
                d5 = ((src[a0] << 8) | src[a0 + 1]) & 0xFFFF;
                a0 += 2;
                return d1 & 0xFFFF;
            }
            if (d7 == 0)
            {
                d6 = 16;
                d1 = (d1 & AndVals[a5 - 1]) + d3;
                d5 = ((src[a0] << 8) | src[a0 + 1]) & 0xFFFF;
                a0 += 2;
                return d1 & 0xFFFF;
            }
            d1 = ((d1 >> d7) & AndVals[a5 - 1]) + d3;
            ChkGetNextByte(a5);
            return d1 & 0xFFFF;
        }

        while (true)
        {
            int d0 = 7;
            int d7 = d6 - d0;
            int d1 = (d5 >> d7) & 0x7F;
            int d2 = d1;
            if (d1 < 0x40) { d0 = 6; d2 >>= 1; }
            ChkGetNextByte(d0);
            d2 &= 0xF;
            d1 >>= 4;
            switch (d1)
            {
                case 0 or 1:
                    for (int i = 0; i <= d2; i++) { output.Add((ushort)a2); a2 = (a2 + 1) & 0xFFFF; }
                    break;
                case 2 or 3:
                    for (int i = 0; i <= d2; i++) output.Add((ushort)a4);
                    break;
                case 4:
                {
                    int v = GetInlineCopyVal();
                    for (int i = 0; i <= d2; i++) output.Add((ushort)v);
                    break;
                }
                case 5:
                {
                    int v = GetInlineCopyVal();
                    for (int i = 0; i <= d2; i++) { output.Add((ushort)v); v = (v + 1) & 0xFFFF; }
                    break;
                }
                case 6:
                {
                    int v = GetInlineCopyVal();
                    for (int i = 0; i <= d2; i++) { output.Add((ushort)v); v = (v - 1) & 0xFFFF; }
                    break;
                }
                default:
                    if (d2 == 0xF) return [.. output];
                    for (int i = 0; i <= d2; i++) output.Add((ushort)GetInlineCopyVal());
                    break;
            }
        }
    }
}
