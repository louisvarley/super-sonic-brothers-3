namespace SonicBros3.Logic;

/// <summary>Thrown when execution reaches original code that has not been ported yet.</summary>
public sealed class UnportedException(int romAddress, string what)
    : Exception($"Unported code reached: {what} (ROM 0x{romAddress:X6})")
{
    public int RomAddress => romAddress;
}
