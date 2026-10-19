namespace SonicBros3.Logic;

/// <summary>
/// CalcSine (ROM 0x40B6) and CalcAngle (0x434E). Both tables are regenerated rather than copied; tools/Validate
/// checks them against the ROM tables (sine at 0x40CE, arctan at 0x43B0).
/// </summary>
public static class MdMath
{
    /// <summary>320 entries: one full circle of 256 plus a 64-entry quarter so cosine can index +$40.</summary>
    public static readonly short[] Sine = BuildSine();

    /// <summary>Arctan for ratios 0..256 (in 1/256), result 0..$20 in 256-per-circle units.</summary>
    public static readonly byte[] Arctan = Enumerable.Range(0, 257)
        .Select(i => (byte)Math.Round(Math.Atan(i / 256.0) * 128 / Math.PI, MidpointRounding.AwayFromZero)).ToArray();

    private static short[] BuildSine()
    {
        var t = new short[320];
        for (int i = 0; i < 320; i++) t[i] = (short)(int)(Math.Sin(2 * Math.PI * i / 256) * 256);
        // The ROM table differs from the truncated formula at two entries.
        t[147] = -117;
        t[237] = -117;
        return t;
    }

    /// <summary>Returns (sin, cos) for an angle byte, as the ROM's d0/d1.</summary>
    public static (short Sin, short Cos) CalcSine(int angle)
    {
        int i = angle & 0xFF;
        return (Sine[i], Sine[i + 0x40]);
    }

    /// <summary>Angle byte of the vector (x = d1, y = d2); returns $40 for (0, 0).</summary>
    public static byte CalcAngle(short x, short y)
    {
        if (x == 0 && y == 0) return 0x40;
        int ax = Math.Abs((int)x) & 0xFFFF, ay = Math.Abs((int)y) & 0xFFFF;
        int d0;
        if (ay < ax) d0 = Arctan[(ay << 8) / ax];          // divu: quotient fits (ay < ax)
        else d0 = (0x40 - Arctan[(ax << 8) / ay]) & 0xFF;
        if (x < 0) d0 = (-d0 + 0x80) & 0xFFFF;
        if (y < 0) d0 = (-d0 + 0x100) & 0xFFFF;
        return (byte)d0;
    }
}
