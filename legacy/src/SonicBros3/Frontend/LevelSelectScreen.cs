using S2Port.Extraction.Compression;
using SonicBros3.Logic;

namespace SonicBros3.Frontend;

/// <summary>
/// The level select, game mode $28 (MenuScreen 0xA174, level select 0xA914): the hack's list of every zone (Sonic 1's,
/// then Sonic 2's, the special stages and the sound test) over the animated "SONIC" background. This port's additions:
/// entries for zones that are not ported are drawn grey (palette line 1, a grey copy of line 0) and Start ignores them;
/// "SUPER MARIO 3" (the SMB3 Easter egg, zone $40) is added as the last entry. The sound test plays what the ported 68k
/// driver plays ($80 and up); Start on it returns to the title (the SEGA screen in the original).
/// </summary>
public sealed class LevelSelectScreen(string dataDir, Func<int, bool> isPorted, Audio.ISoundChips? chips = null) : Screen(dataDir, chips)
{
    /// <summary>Palette lines for the text: normal (white), unavailable (grey), highlighted (yellow).</summary>
    private const int Normal = 0, Unavailable = 0x2000, Highlighted = 0x6000;
    /// <summary>Where the sound test\'s number is drawn on plane A.</summary>
    private const int SoundTestDigits = 0xCC44;

    private const string Tables = "levelselect.json";
    /// <summary>The hack's 42 entries ($29 = sound test) plus SUPER MARIO 3.</summary>
    public const int SoundTestEntry = 0x29, MarioEntry = 0x2A, Entries = 0x2B;
    /// <summary>Sonic 2's options / level select music (the Z80 driver's $91).</summary>
    private const int OptionsMusic = 0x91;
    private const int MarioRow = 0x1A;

    private ushort[] _map = [];
    private Audio.Smps68kDriver? _driver;

    /// <summary>LevelSelect_Order (0xAA6A), plus the Easter egg.</summary>
    private int Order(int entry) => entry == MarioEntry ? Camera.EasterEggZone << 8 : RomWord(Tables, 0xAA6A + entry * 2);

    public bool IsAvailable(int entry)
    {
        if (entry == SoundTestEntry) return true;
        // The Easter egg is there when it was built from the player's smb3.nes (Smb3ZoneBuilder).
        if (entry == MarioEntry) return S2Port.Extraction.DataFile.Exists(Path.Combine(DataDir, "zones", $"zone_{Camera.EasterEggZone:X2}", "act_1", "level.json"));
        int order = Order(entry);
        return order is not (0x4000 or 0x4100) && (order & 0x8000) == 0 && isPorted(order >> 8);
    }

    /// <summary>LevSel_MarkTable (0xAD56): {row, 2*column} of the name and of the act number.</summary>
    private (int Row, int Col, int Row2, int Col2) Mark(int entry) => entry == MarioEntry
        ? (MarioRow, 0x2C, MarioRow, 0x48)
        : (Rom(Tables, 0xAD56 + entry * 4), Rom(Tables, 0xAD57 + entry * 4), Rom(Tables, 0xAD58 + entry * 4), Rom(Tables, 0xAD59 + entry * 4));

    protected override IEnumerable<bool> Mode()
    {
        foreach (var f in FadeToBlack()) yield return f;
        ClearScreen();
        BackdropIndex = 0;                                    // $8700
        ClearRam(Vars.ObjectDisplayLists, 0x400);
        ClearRam(Vars.MainCharacter, Vars.DynamicObjectRamEnd - Vars.MainCharacter);
        LoadArt("levelselect");
        PlaneMapToVram(Enigma.Decompress(RomFrom("menu-back.json", 0x84126), 0x6000), PlaneB, 40, 28);

        // 0xA914: the text map (kept, as Chunk_Table, for highlighting), with this port's additions.
        _map = Enigma.Decompress(RomFrom("levelselect-text.json", 0xB17A), 0);
        if (_map.Length < 40 * 28) Array.Resize(ref _map, 40 * 28);
        WriteText(MarioRow, 0x16, "SUPER MARIO 3");
        WriteText(MarioRow, 0x24, "1");
        PlaneMapToVram(_map, PlaneA, 40, 28);
        for (int e = 0; e < Entries; e++) if (!IsAvailable(e)) MarkFields(e, Unavailable);
        DrawSoundNumber(0);
        ClearRam(Vars.AnimCounters, 0x10);
        AnimateBackground();
        PaletteFade.LoadForFade(Ram, DataDir, 0x26);
        for (int i = 0; i < 8; i++)   // 0xA962: Normal line 2 -> Target line 2, Normal line 2 cleared
        {
            Ram.W32(Vars.TargetPalette + 0x40 + i * 4, Ram.U32(Vars.NormalPalette + 0x40 + i * 4));
            Ram.W32(Vars.NormalPalette + 0x40 + i * 4, 0);
        }
        Sound.PlayMusic(OptionsMusic);   // 0xA974
        GreyLine();
        yield return true;
        foreach (var f in FadeFromBlack()) yield return f;

        while (true)   // 0xA9B2
        {
            yield return true;
            int old = Ram.U16(Vars.LevelSelectZone);
            MarkFields(old, IsAvailable(old) ? Normal : Unavailable);
            Controls();
            MarkFields(Ram.U16(Vars.LevelSelectZone), Highlighted);
            AnimateBackground();
            if ((Ram.U8(Vars.Ctrl1Press) & 0x80) == 0) continue;
            int entry = Ram.U16(Vars.LevelSelectZone);
            if (!IsAvailable(entry)) continue;
            int order = Order(entry);
            if ((order & 0x8000) != 0) { Result = new ScreenResult.BackToTitle(); yield break; }   // sound test
            Result = new ScreenResult.StartGame(order >> 8, (order & 0xFF) + 1);
            yield break;
        }
    }

    /// <summary>The level select's charset (tile $10 + index of "0123456789*@:.A-Z"); a space is tile 0.</summary>
    private void WriteText(int row, int col, string text)
    {
        for (int i = 0; i < text.Length; i++)
            _map[row * 40 + col + i] = (ushort)(text[i] == ' ' ? 0 : 0x10 + "0123456789*@:.ABCDEFGHIJKLMNOPQRSTUVWXYZ".IndexOf(text[i]));
    }

    /// <summary>Palette line 1: a dimmed grey copy of line 0's text colours, for unavailable entries.</summary>
    private void GreyLine()
    {
        for (int i = 0; i < 16; i++)
        {
            int c = Ram.U16(Vars.TargetPalette + i * 2);
            int lum = (((c >> 1) & 7) + ((c >> 5) & 7) + ((c >> 9) & 7)) / 3;
            int g = lum / 2;   // white -> $666
            Ram.W16(Vars.TargetPalette + 0x20 + i * 2, i == 0 ? 0 : (g << 1) | (g << 5) | (g << 9));
        }
    }

    /// <summary>LevelSelect_MarkFields (0xAC20): an entry's name (14 cells) and act number, from the map, in palette d3.</summary>
    private void MarkFields(int entry, int d3)
    {
        var (row, col, row2, col2) = Mark(entry);
        for (int i = 0; i < 14; i++)
            WriteVramWord(PlaneA + row * 0x80 + col + i * 2, (_map[row * 40 + col / 2 + i] + d3) & 0xFFFF);
        if (row2 != 0)
            WriteVramWord(PlaneA + row2 * 0x80 + col2, (_map[row2 * 40 + col2 / 2] + d3) & 0xFFFF);
        if (entry == SoundTestEntry) DrawSoundNumber(d3);
    }

    /// <summary>0xACC2: the sound test value as two hex digits at row 24, column 34.</summary>
    private void DrawSoundNumber(int d3)
    {
        int v = Ram.U16(Vars.SoundTestSound) & 0xFF;
        foreach (var (digit, i) in new[] { (v >> 4, 0), (v & 0xF, 1) })
            WriteVramWord(SoundTestDigits + i * 2, (0x10 + (digit >= 10 ? digit + 4 : digit) + d3) & 0xFFFF);
    }

    /// <summary>LevSelControls (0xAB1A), with one more entry (SUPER MARIO 3) after the sound test.</summary>
    private void Controls()
    {
        int press = Ram.U8(Vars.Ctrl1Press), held = Ram.U8(Vars.Ctrl1Held);
        bool repeat = (press & 3) != 0;
        if (!repeat)
        {
            Ram.W16(Vars.LevelSelectHoldTimer, Ram.U16(Vars.LevelSelectHoldTimer) - 1);
            repeat = (short)Ram.U16(Vars.LevelSelectHoldTimer) < 0;
        }
        if (repeat)
        {
            Ram.W16(Vars.LevelSelectHoldTimer, 0x0B);
            if ((held & 3) != 0)
            {
                int d0 = Ram.U16(Vars.LevelSelectZone);
                if ((held & 1) != 0 && --d0 < 0) d0 = Entries - 1;
                if ((held & 2) != 0 && ++d0 >= Entries) d0 = 0;
                Ram.W16(Vars.LevelSelectZone, d0);
                return;
            }
        }
        int entry = Ram.U16(Vars.LevelSelectZone);
        if (entry == SoundTestEntry) { SoundTestControls(press); return; }
        if ((press & 0x0C) == 0) return;
        // LevelSelect_SwitchTable (0xABF6): the entry level with this one in the other column.
        Ram.W16(Vars.LevelSelectZone, entry == MarioEntry ? 0x16 : Rom(Tables, 0xABF6 + entry));
    }

    /// <summary>0xAB6A: left/right change the value, A adds $10, B or C plays it.</summary>
    private void SoundTestControls(int press)
    {
        int d0 = Ram.U16(Vars.SoundTestSound);
        if ((press & 4) != 0) d0 = (d0 - 1) & 0xFF;
        if ((press & 8) != 0) d0 = (d0 + 1) & 0xFF;
        if ((press & 0x40) != 0) d0 = (d0 + 0x10) & 0xFF;
        Ram.W16(Vars.SoundTestSound, d0);
        if ((press & 0x30) == 0) return;
        // 0xABA6: $00-$7F are the Z80 driver's sounds $80-$FF, $80-$FF the hack's 68k driver's; switching drivers
        // loads the other one afresh (0x1472).
        bool z80 = (d0 & 0x80) == 0;
        if (z80 && Ram.U8(Vars.SoundDriverSelect) != 0)
        {
            Ram.W8(Vars.SoundDriverSelect, 0);
            Z80Sound?.Reset();
        }
        else if (!z80 && Ram.U8(Vars.SoundDriverSelect) == 0)
        {
            Ram.W8(Vars.SoundDriverSelect, 1);
            if (Chips is not null)
            {
                _driver = new Audio.Smps68kDriver(Ram, new Audio.Smps68kData(DataDir), Chips);
                Ram.W8(Audio.Smps68kDriver.Base + 0x09, 0x80);
            }
        }
        Sound.PlayMusic((d0 & 0x7F) + 0x80);
    }

    protected override void UpdateSound()
    {
        if (Ram.U8(Vars.SoundDriverSelect) != 0) _driver?.Update();
        else base.UpdateSound();
    }

    /// <summary>Dynamic_Normal (0x44... in levels) over Anim_SonicMilesBG (0x9D60): the background's "SONIC" morph.</summary>
    private void AnimateBackground()
    {
        const string script = "menu-back-anim.json";
        int count = RomWord(script, 0x9D60) + 1;
        int a = 0x9D62;
        for (int s = 0; s < count; s++)
        {
            int counter = Vars.AnimCounters + s * 2;
            int timer = Ram.U8(counter);
            int frames = Rom(script, a + 6), tilesPerFrame = Rom(script, a + 7);
            int dest = RomWord(script, a + 4);
            int globalDuration = Rom(script, a);
            Ram.W8(counter, timer - 1);
            if (timer == 0)
            {
                int frame = Ram.U8(counter + 1);
                if (frame >= frames) frame = 0;
                Ram.W8(counter + 1, frame + 1);
                bool perFrame = globalDuration == 0xFF;
                int tile = Rom(script, a + 8 + frame * (perFrame ? 2 : 1));
                Ram.W8(counter, perFrame ? Rom(script, a + 9 + frame * 2) : globalDuration);
                var (b, art) = Art.DataTable("frontend/menu-back-art.json");
                art.AsSpan(tile * 32, tilesPerFrame * 32).CopyTo(Vram.AsSpan(dest));
            }
            a += 8 + frames * (globalDuration == 0xFF ? 2 : 1);
            a = (a + 1) & ~1;
        }
    }
}
