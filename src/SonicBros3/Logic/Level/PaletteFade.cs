namespace SonicBros3.Logic;

/// <summary>
/// The palette fades: Normal_palette ($FB00) steps towards Target_palette ($FB80) or towards black, one step per
/// frame, over the range Palette_fade_start / Palette_fade_length ($F626 / $F627).
/// </summary>
public static class PaletteFade
{

    /// <summary>The 22 steps (frames) a full fade takes (dbf #$15).</summary>
    public const int Steps = 0x16;

    /// <summary>Pal_FadeFromBlack (0x2ADA) setup: the whole palette, black.</summary>
    public static void BeginFadeIn(Ram ram)
    {
        ram.W16(Vars.PalFadeStart, 0x3F);
        for (int i = 0; i <= 0x3F; i++) ram.W16(Vars.NormalPalette + i * 2, 0);
    }

    /// <summary>Pal_FadeIn (0x2B12) over the fade range: Pal_FadeFromBlack.UpdateColour (0x2B52) per colour.</summary>
    public static void FadeInStep(Ram ram)
    {
        int start = ram.U8(Vars.PalFadeStart), count = ram.U8(Vars.PalFadeLength);
        for (int i = 0; i <= count; i++) ram.W16(Vars.NormalPalette + start + i * 2, UpdateColour(ram.U16(Vars.TargetPalette + start + i * 2), ram.U16(Vars.NormalPalette + start + i * 2)));
    }

    /// <summary>0x2B52: blue, else green, else red goes up by one step towards the target.</summary>
    public static int UpdateColour(int target, int current)
    {
        if (current == target) return current;
        if (current + 0x200 <= target) return current + 0x200;
        if (current + 0x20 <= target) return current + 0x20;
        return current + 2;
    }

    /// <summary>Pal_FadeToBlack's step (0x2B7E): red, else green, else blue goes down by one step.</summary>
    public static void FadeOutStep(Ram ram)
    {
        for (int i = 0; i < 64; i++)
        {
            int a = Vars.NormalPalette + i * 2, c = ram.U16(a);
            if (c == 0) continue;
            if ((c & 0x00E) != 0) c -= 0x002;
            else if ((c & 0x0E0) != 0) c -= 0x020;
            else if ((c & 0xE00) != 0) c -= 0x200;
            ram.W16(a, c);
        }
    }

    /// <summary>PalLoad_ForFade (0x2E26): a palette into Target_palette.</summary>
    public static void LoadForFade(Ram ram, string dataDir, int id) => Load(ram, dataDir, id, Vars.TargetPalette);

    /// <summary>PalLoad_Now (0x2E42): a palette into Normal_palette.</summary>
    public static void LoadNow(Ram ram, string dataDir, int id) => Load(ram, dataDir, id, Vars.NormalPalette);

    private static void Load(Ram ram, string dataDir, int id, int bank)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(S2Port.Extraction.DataFile.ReadAllText(Path.Combine(dataDir, "palettes", $"pal_{id:X2}.json")));
        int start = doc.RootElement.GetProperty("startColour").GetInt32(), i = 0;
        foreach (var c in doc.RootElement.GetProperty("colours").EnumerateArray())
            ram.W16(bank + (start + i++) * 2, Convert.ToUInt16(c.GetProperty("word").GetString(), 16));
    }
}
