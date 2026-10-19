using System.Text.Json;

namespace SonicBros3.Logic;

/// <summary>
/// Oscillating numbers: 16 {value, speed} word pairs at $FE60, with one direction bit each in the word at $FE5E.
/// Each frame (OscillateNumDo 0x5DCC) a value's speed accelerates toward its limit and reverses there, giving the
/// smooth swings that platforms and other objects read. OscillateNumInit (0x5D26) loads the starting state at
/// level start. The hack's zones use their own starting values and parameters.
/// </summary>
public sealed class Oscillation
{
    private const int Count = 16;
    private readonly Ram _ram;
    private readonly int[] _initial, _parameters;

    /// <summary>The address of oscillator <paramref name="n"/>'s value (a word; the high byte is what objects read).</summary>
    public static int Value(int n) => Vars.OscillatingData + 2 + n * 4;

    public Oscillation(Ram ram, string dataDir, bool hackZone)
    {
        _ram = ram;
        using var doc = JsonDocument.Parse(S2Port.Extraction.DataFile.ReadAllText(Path.Combine(dataDir, "objects", "oscillation.json")));
        var set = doc.RootElement.GetProperty(hackZone ? "hack" : "normal");
        _initial = set.GetProperty("initial").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        _parameters = set.GetProperty("parameters").EnumerateArray().Select(e => e.GetInt32()).ToArray();
    }

    public void Init()
    {
        for (int i = 0; i < _initial.Length; i++) _ram.W16(Vars.OscillatingData + i * 2, _initial[i]);
    }

    public void Step()
    {
        if (_ram.U16(Vars.TwoPlayerMode) == 0 && _ram.Sonic.Routine >= 6) return;   // frozen while Sonic dies
        int directions = _ram.U16(Vars.OscillatingData);
        for (int k = 0; k < Count; k++)
        {
            int bit = 1 << (Count - 1 - k);    // oscillator 0 uses bit 15
            int value = Value(k), speedAddr = value + 2;
            int acceleration = _parameters[k * 2], limit = _parameters[k * 2 + 1] & 0xFF;
            bool falling = (directions & bit) != 0;
            int speed = (_ram.U16(speedAddr) + (falling ? -acceleration : acceleration)) & 0xFFFF;
            _ram.W16(speedAddr, speed);
            _ram.W16(value, _ram.U16(value) + speed);
            if (!falling && limit <= _ram.U8(value)) directions |= bit;
            else if (falling && limit > _ram.U8(value)) directions &= ~bit;
        }
        _ram.W16(Vars.OscillatingData, directions);
    }
}
