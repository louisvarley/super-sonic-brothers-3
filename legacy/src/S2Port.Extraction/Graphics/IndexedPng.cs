using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace S2Port.Extraction.Graphics;

/// <summary>Minimal writer/reader for 8-bit indexed PNGs (PLTE + optional tRNS).</summary>
public static class IndexedPng
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <param name="pixels">width*height palette indices.</param>
    /// <param name="palette">RGB entries (max 256).</param>
    /// <param name="transparentIndices">Indices written as fully transparent in tRNS.</param>
    public static void Write(Stream output, int width, int height, ReadOnlySpan<byte> pixels,
        IReadOnlyList<(byte R, byte G, byte B)> palette, IEnumerable<int>? transparentIndices = null)
    {
        if (pixels.Length != width * height) throw new ArgumentException("Pixel buffer size mismatch.");
        if (palette.Count is 0 or > 256) throw new ArgumentException("Palette must have 1-256 entries.");

        output.Write(Signature);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 3;  // colour type: indexed
        WriteChunk(output, "IHDR", ihdr);

        var plte = new byte[palette.Count * 3];
        for (int i = 0; i < palette.Count; i++)
        {
            plte[i * 3] = palette[i].R;
            plte[i * 3 + 1] = palette[i].G;
            plte[i * 3 + 2] = palette[i].B;
        }
        WriteChunk(output, "PLTE", plte);

        if (transparentIndices is not null)
        {
            var trns = Enumerable.Repeat((byte)255, palette.Count).ToArray();
            foreach (int t in transparentIndices)
                if (t < trns.Length) trns[t] = 0;
            WriteChunk(output, "tRNS", trns);
        }

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (int y = 0; y < height; y++)
            {
                z.WriteByte(0); // filter: none
                z.Write(pixels.Slice(y * width, width));
            }
        }
        WriteChunk(output, "IDAT", raw.ToArray());
        WriteChunk(output, "IEND", []);
    }

    public static void Write(string path, int width, int height, ReadOnlySpan<byte> pixels,
        IReadOnlyList<(byte R, byte G, byte B)> palette, IEnumerable<int>? transparentIndices = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        Write(fs, width, height, pixels, palette, transparentIndices);
    }

    /// <summary>Reads back an 8-bit indexed PNG written by <see cref="Write(Stream,int,int,ReadOnlySpan{byte},IReadOnlyList{ValueTuple{byte,byte,byte}},IEnumerable{int}?)"/>.</summary>
    public static (int Width, int Height, byte[] Pixels, (byte R, byte G, byte B)[] Palette) Read(Stream input)
    {
        var sig = new byte[8];
        input.ReadExactly(sig);
        if (!sig.AsSpan().SequenceEqual(Signature)) throw new InvalidDataException("Not a PNG.");

        int width = 0, height = 0;
        (byte, byte, byte)[] palette = [];
        using var idat = new MemoryStream();
        var lenBuf = new byte[8];
        while (true)
        {
            input.ReadExactly(lenBuf);
            int len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
            string type = Encoding.ASCII.GetString(lenBuf, 4, 4);
            var data = new byte[len];
            input.ReadExactly(data);
            input.ReadExactly(new byte[4]); // CRC
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(data);
                    height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4));
                    if (data[8] != 8 || data[9] != 3) throw new NotSupportedException("Only 8-bit indexed PNGs are supported.");
                    break;
                case "PLTE":
                    palette = new (byte, byte, byte)[len / 3];
                    for (int i = 0; i < palette.Length; i++) palette[i] = (data[i * 3], data[i * 3 + 1], data[i * 3 + 2]);
                    break;
                case "IDAT":
                    idat.Write(data);
                    break;
                case "IEND":
                    goto done;
            }
        }
    done:
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        var pixels = new byte[width * height];
        var row = new byte[width + 1];
        for (int y = 0; y < height; y++)
        {
            z.ReadExactly(row);
            if (row[0] != 0) throw new NotSupportedException("Only filter type 0 is supported.");
            row.AsSpan(1).CopyTo(pixels.AsSpan(y * width));
        }
        return (width, height, pixels, palette);
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, data.Length);
        s.Write(buf);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc32.Update(Crc32.Update(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(buf, crc);
        s.Write(buf);
    }

    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
