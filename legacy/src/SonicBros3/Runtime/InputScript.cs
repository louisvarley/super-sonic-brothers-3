namespace SonicBros3.Runtime;

/// <summary>Input script in the tools/RefEmu format: "&lt;frame&gt; &lt;buttons&gt;" lines; state holds until the next line.</summary>
public sealed class InputScript(List<(int Frame, int Buttons)> changes)
{
    public static InputScript Load(string path)
    {
        var list = new List<(int, int)>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            list.Add((int.Parse(parts[0]), Parse(parts[1])));
        }
        return new InputScript(list.OrderBy(c => c.Item1).ToList());
    }

    public static int Parse(string buttons)
    {
        int b = 0;
        foreach (char c in buttons)
            b |= c switch { 'U' => 0x01, 'D' => 0x02, 'L' => 0x04, 'R' => 0x08, 'B' => 0x10, 'C' => 0x20, 'A' => 0x40, 'S' => 0x80, _ => 0 };
        return b;
    }

    public int ButtonsAt(int frame)
    {
        int b = 0;
        foreach (var (f, buttons) in changes)
        {
            if (f > frame) break;
            b = buttons;
        }
        return b;
    }
}
