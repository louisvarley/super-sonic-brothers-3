using S2Port.Extraction.Levels;
using SonicBros3.Logic;
using SonicBros3.Runtime;

namespace SonicBros3.Frontend;

/// <summary>What a front-end screen asks for when it ends.</summary>
public abstract record ScreenResult
{
    public sealed record StartGame(int Zone, int Act) : ScreenResult;
    public sealed record OpenLevelSelect : ScreenResult;
    public sealed record BackToTitle : ScreenResult;
    /// <summary>Sonic Bros. 3: the spade panel's card matching game.</summary>
    public sealed record StartMatchGame : ScreenResult;
    public sealed record BackToMap : ScreenResult;
}

/// <summary>
/// The common machinery of the screens that are not levels (title, level select): 68k RAM, VRAM, the joypad bytes
/// the original reads, and a frame-by-frame mode routine written as an iterator (each yield is one WaitForVint).
/// </summary>
public abstract class Screen
{
    /// <summary>VRAM addresses of the two planes' nametables.</summary>
    protected const int PlaneA = 0xC000, PlaneB = 0xE000;

    protected Screen(string dataDir, Audio.ISoundChips? chips = null)
    {
        DataDir = dataDir;
        Art = new ObjectArt(dataDir);
        Ram.W8(Vars.GraphicsFlags, Overseas);
        Sound = new RamSound(Ram);
        Chips = chips;
        // The front end runs with Sonic 2's Z80 driver ($FFE5 = 0, as at boot).
        if (chips is not null) Z80Sound = Audio.SmpsZ80Driver.For(chips, dataDir);
    }

    /// <summary>Sound requests (the original's PlayMusic / PlaySound queue in RAM).</summary>
    protected RamSound Sound { get; }
    protected Audio.ISoundChips? Chips { get; }
    /// <summary>Sonic 2's Z80 driver, which plays while <see cref="Vars.SoundDriverSelect"/> is 0.</summary>
    protected Audio.SmpsZ80Driver? Z80Sound { get; }

    public string DataDir { get; }
    public Ram Ram { get; } = new();

    /// <summary>Graphics_Flags ($FFF8) as the boot code sets it from the version register: an overseas NTSC console.</summary>
    public const int Overseas = 0x80;
    public byte[] Vram { get; } = new byte[0x10000];
    public ObjectArt Art { get; }
    public ScreenResult? Result { get; protected set; }
    public int BackdropIndex { get; protected set; } = 0x20;

    private IEnumerator<bool>? _mode;
    private int _held;

    /// <summary>The mode routine: one iteration per frame.</summary>
    protected abstract IEnumerable<bool> Mode();

    /// <summary>One frame: VBlank's work (joypad, run counter), then the mode routine up to its next WaitForVint.</summary>
    public void Step(int buttons)
    {
        Ram.W8(Vars.Ctrl1Held, buttons);                      // Ctrl_1_Held
        Ram.W8(Vars.Ctrl1Press, buttons & ~_held);        // Ctrl_1_Press
        _held = buttons;
        Ram.W32(Vars.VintRuncount, Ram.U32(Vars.VintRuncount) + 1);             // Vint_runcount
        _mode ??= Mode().GetEnumerator();
        if (Result is null && !_mode.MoveNext() && Result is null) Result = new ScreenResult.BackToTitle();
        UpdateSound();
    }

    /// <summary>VBlank's sound part: the 68k's queue to the Z80 (sndDriverInput, 0x11A8) and the Z80's interrupt.</summary>
    protected virtual void UpdateSound()
    {
        if (Z80Sound is null || Ram.U8(Vars.SoundDriverSelect) != 0) return;
        Z80Sound.SoundDriverInput(Ram);
        Z80Sound.VInt();
    }

    public virtual void Render(Vdp vdp)
    {
        for (int i = 0; i < 64; i++) vdp.Cram[i] = Ram.U16(Vars.NormalPalette + i * 2);
        vdp.BackdropIndex = BackdropIndex;
        vdp.SplitLine = -1;
        vdp.LineScrollA = LineScrollA;
        vdp.LineScrollB = LineScrollB;
        vdp.RenderScreen(Vram, Ram);
    }

    /// <summary>Per-line vertical scroll of the planes (see <see cref="Vdp.LineScrollA"/>); null = VSRAM as usual.</summary>
    protected Func<int, int?>? LineScrollA { get; set; }
    protected Func<int, int?>? LineScrollB { get; set; }

    // ------------------------------------------------------------------ shared routines

    /// <summary>The front end's art list (frontend/art.json) for one screen, decompressed into VRAM.</summary>
    protected void LoadArt(string screen)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(S2Port.Extraction.DataFile.ReadAllText(Path.Combine(DataDir, "frontend", "art.json")));
        foreach (var e in doc.RootElement.GetProperty("art").EnumerateArray())
        {
            if (e.GetProperty("screen").GetString() != screen) continue;
            var tiles = LevelLoader.LoadTileArt(Path.GetFullPath(Path.Combine(DataDir, "frontend", e.GetProperty("art").GetString()!)),
                e.GetProperty("tiles").GetInt32());
            tiles.CopyTo(Vram.AsSpan(e.GetProperty("vramTile").GetInt32() * 32));
        }
    }

    /// <summary>A byte of a ROM region extracted as objects/frontend/&lt;file&gt;, by ROM address.</summary>
    protected byte Rom(string file, int address)
    {
        var (b, data) = Art.DataTable($"frontend/{file}");
        return data[address - b];
    }

    protected int RomWord(string file, int address) => (Rom(file, address) << 8) | Rom(file, address + 1);

    protected ReadOnlySpan<byte> RomFrom(string file, int address)
    {
        var (b, data) = Art.DataTable($"frontend/{file}");
        return data.AsSpan(address - b);
    }

    /// <summary>PlaneMapToVRAM_H40 (0x15DA): width x height words to a 64-cell-wide nametable.</summary>
    protected void PlaneMapToVram(ReadOnlySpan<ushort> map, int dest, int width, int height)
    {
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int a = (dest + y * 128 + x * 2) & 0xFFFF;
            ushort w = map[y * width + x];
            Vram[a] = (byte)(w >> 8);
            Vram[a + 1] = (byte)w;
        }
    }

    protected void WriteVramWord(int address, int value)
    {
        Vram[address & 0xFFFF] = (byte)(value >> 8);
        Vram[(address + 1) & 0xFFFF] = (byte)value;
    }

    /// <summary>ClearScreen (0x1328): planes, sprite table and scroll tables.</summary>
    protected void ClearScreen()
    {
        Vram.AsSpan(PlaneA, 0x4000).Clear();   // both planes
        Ram.Bytes.AsSpan(Vars.SpriteTable, 0x280).Clear();
        Ram.Bytes.AsSpan(Vars.HorizScrollBuf, 0x400).Clear();
        Ram.W32(Vars.VscrollFactorFg, 0);                        // Vscroll_Factor
    }

    protected void ClearRam(int start, int length) => Ram.Bytes.AsSpan(start, length).Clear();

    /// <summary>Pal_FadeToBlack (0x2B7E): 22 frames.</summary>
    protected IEnumerable<bool> FadeToBlack()
    {
        Ram.W16(Vars.PalFadeStart, 0x3F);
        for (int i = 0; i < PaletteFade.Steps; i++)
        {
            yield return true;
            PaletteFade.FadeOutStep(Ram);
        }
    }

    /// <summary>Pal_FadeFromBlack (0x2ADA): 22 frames.</summary>
    protected IEnumerable<bool> FadeFromBlack()
    {
        PaletteFade.BeginFadeIn(Ram);
        for (int i = 0; i < PaletteFade.Steps; i++)
        {
            yield return true;
            PaletteFade.FadeInStep(Ram);
        }
    }
}
