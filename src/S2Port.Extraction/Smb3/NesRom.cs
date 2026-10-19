using System.Security.Cryptography;

namespace S2Port.Extraction.Smb3;

/// <summary>
/// Read-only view of an iNES image (smb3.nes): 16-byte header, PRG ROM in 8 KiB banks, CHR ROM in 1 KiB banks. Reads
/// address PRG by bank and CPU address, as the MMC3 maps an 8 KiB bank into $8000/$A000/$C000/$E000.
/// </summary>
public sealed class NesRom
{
    private readonly byte[] _data;
    private readonly int _prgSize;

    public NesRom(byte[] data)
    {
        if (data.Length < 16 || data[0] != 'N' || data[1] != 'E' || data[2] != 'S' || data[3] != 0x1A)
            throw new InvalidDataException("not an iNES image");
        _data = data;
        _prgSize = data[4] * 0x4000;
        if ((data[6] & 4) != 0 || (data[7] & 12) == 8)
            throw new InvalidDataException("Trainer-bearing and NES 2.0 images are not supported.");
        if (_prgSize == 0 || data.Length < 16 + _prgSize + data[5] * 0x2000)
            throw new InvalidDataException("Truncated iNES image.");
        Sha1 = Convert.ToHexStringLower(SHA1.HashData(data));
    }

    public static NesRom Load(string path) => new(File.ReadAllBytes(path));

    public string Sha1 { get; }
    public int Mapper => (_data[6] >> 4) | (_data[7] & 0xF0);
    public int ChrBankCount => _data[5] * 8;

    /// <summary>A byte of 8 KiB PRG bank <paramref name="bank"/> at CPU address <paramref name="cpuAddress"/>.</summary>
    public byte Prg(int bank, int cpuAddress) => _data[16 + bank * 0x2000 + (cpuAddress & 0x1FFF)];

    public byte[] Prg(int bank, int cpuAddress, int length)
    {
        var r = new byte[length];
        for (int i = 0; i < length; i++) r[i] = Prg(bank, cpuAddress + i);
        return r;
    }

    /// <summary>The 16 bytes of an 8x8 2bpp tile from 1 KiB CHR bank <paramref name="bank1K"/>.</summary>
    public ReadOnlySpan<byte> ChrTile(int bank1K, int tile) => _data.AsSpan(16 + _prgSize + bank1K * 0x400 + tile * 16, 16);
}
