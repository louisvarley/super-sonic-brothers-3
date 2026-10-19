using System.Text.Json;
using S2Port.Extraction.Levels;

namespace SonicBros3.Logic;

/// <summary>
/// The HUD. Its sprites (SCORE/TIME/RINGS labels and the digit slots) are drawn by BuildHUD (0x44D58) inside
/// BuildSprites; the digits themselves are tiles that HudUpdate (0x4531E, run in VBlank) rewrites in VRAM
/// when the score, ring count, time or lives change. Hud_Base (0x4563C) writes the initial digit tiles at level start.
/// The level timer is advanced here too, as in the original.
/// </summary>
public sealed class Hud
{

    // VRAM addresses from the VDP commands in HudUpdate.
    private const int VramBase = 0xDC40, VramScore = 0xDC80, VramMinutes = 0xDE40, VramSeconds = 0xDEC0;
    private const int VramRings = 0xDF40, VramLives = 0xFBA0;

    private static readonly int[] Score = [100000, 10000, 1000, 100, 10, 1];
    private static readonly int[] Rings = [100, 10, 1];
    private static readonly int[] Seconds = [10, 1];
    private static readonly int[] Minutes = [1];

    private readonly Ram _ram;
    private readonly byte[] _digits, _livesDigits;
    private readonly int[] _baseTiles, _initRingsTiles;

    public SpriteData Sprites { get; }
    public byte[]? Vram { get; set; }

    public Hud(Ram ram, string dataDir)
    {
        _ram = ram;
        string dir = Path.Combine(dataDir, "objects", "hud");
        using var doc = JsonDocument.Parse(S2Port.Extraction.DataFile.ReadAllText(Path.Combine(dir, "hud.json")));
        var root = doc.RootElement;
        _digits = LevelLoader.LoadTileArt(Path.Combine(dir, root.GetProperty("digits").GetString()!), root.GetProperty("digitTiles").GetInt32());
        _livesDigits = LevelLoader.LoadTileArt(Path.Combine(dir, root.GetProperty("livesDigits").GetString()!), root.GetProperty("livesDigitTiles").GetInt32());
        _baseTiles = root.GetProperty("baseTiles").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        _initRingsTiles = root.GetProperty("initRingsTiles").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Sprites = SpriteData.Load(dataDir, "hud");
    }

    /// <summary>Hud_Base (0x4563C, 1-player): lives digits, then the 15 two-tile slots from $6E2.</summary>
    public void Base()
    {
        Lives();
        WriteTileList(VramBase, _baseTiles);
    }

    /// <summary>BuildHUD (0x44D58): choose the frame (flash RINGS at 0 rings, TIME at 9 minutes) and draw it.</summary>
    public void Build(SpriteBuilder sprites, ref int a2, ref int d5)
    {
        bool blinkOff = _ram.Bit(Vars.LevelFrameCounter + 0x1, 3);
        int frame = 0;
        bool timeWarning = _ram.U8(Vars.TimedMode) == 0 ? _ram.U8(Vars.TimerMinute) == 9 : (int)_ram.U32(Vars.Timer) < 0x600;
        if (_ram.U16(Vars.RingCount) == 0)
        {
            if (!blinkOff) frame = 1 + (timeWarning ? 2 : 0);
        }
        else if (!blinkOff && timeWarning) frame = 2;
        sprites.WriteStatic(Sprites.Frames[frame], 0x108, 0x90, 0x86CA, ref a2, ref d5);
    }

    /// <summary>HudUpdate (0x4531E), 1-player, not in debug mode.</summary>
    public void Update()
    {
        if (_ram.U16(Vars.TwoPlayerMode) != 0) throw new UnportedException(0x4551E, "2-player HUD");
        if (_ram.U16(Vars.DebugModeFlag) != 0) throw new UnportedException(0x45468, "debug-mode HUD");
        if (_ram.U8(Vars.UpdateHudScore) != 0)
        {
            _ram.W8(Vars.UpdateHudScore, 0);
            Number(VramScore, _ram.U32(Vars.Score), Score, true);
        }
        if (_ram.U8(Vars.UpdateHudRings) != 0)
        {
            if ((sbyte)_ram.U8(Vars.UpdateHudRings) < 0) WriteTileList(VramRings, _initRingsTiles);   // Hud_InitRings 0x45628
            _ram.W8(Vars.UpdateHudRings, 0);
            Number(VramRings, _ram.U16(Vars.RingCount), Rings, true);
        }
        if (_ram.U8(Vars.UpdateHudTimer) != 0 && _ram.U16(Vars.GamePaused) == 0 && AdvanceTimer())
        {
            Number(VramMinutes, _ram.U8(Vars.TimerMinute), Minutes, false);
            Number(VramSeconds, _ram.U8(Vars.TimerSecond), Seconds, false);
        }
        if (_ram.U8(Vars.UpdateHudLives) != 0)
        {
            _ram.W8(Vars.UpdateHudLives, 0);
            Lives();
        }
        if (_ram.U8(Vars.UpdateBonusScore) != 0)
        {
            // 0x4541A: the results' total, time, ring and perfect bonus counters, 4 digits each from VRAM $A400.
            _ram.W8(Vars.UpdateBonusScore, 0);
            int vram = 0xA400;
            foreach (int a in new[] { Vars.TotalBonusCountdown, Vars.BonusCountdown1, Vars.BonusCountdown2, Vars.BonusCountdown3 })
            {
                Bonus(vram, _ram.U16(a));
                vram += 0x100;
            }
        }
    }

    /// <summary>Returns true when minutes/seconds changed and need drawing.</summary>
    private bool AdvanceTimer()
    {
        const int a = Vars.Timer;
        if (_ram.U8(Vars.TimedMode) == 0)
        {
            if (_ram.U32(a) == 0x00093B3B) { TimeOver(); return false; }
            if (Inc(Vars.TimerFrame) < 60) return false;
            _ram.W8(Vars.TimerFrame, 0);
            if (Inc(Vars.TimerSecond) < 60) return true;
            _ram.W8(Vars.TimerSecond, 0);
            if (Inc(Vars.TimerMinute) >= 9) _ram.W8(Vars.TimerMinute, 9);
            return true;
        }
        // Count-down mode (a hack addition): 0x453B8.
        if (_ram.U32(a) == 0x0000003B) { TimeOver(); return false; }
        if (Dec(Vars.TimerFrame) >= 0) return false;
        _ram.W8(Vars.TimerFrame, 0x3B);
        if (Dec(Vars.TimerSecond) >= 0) return true;
        _ram.W8(Vars.TimerSecond, 0x3B);
        if (Dec(Vars.TimerMinute) < 0) _ram.W8(Vars.TimerMinute, 0);
        return true;

        int Inc(int addr) { int v = (_ram.U8(addr) + 1) & 0xFF; _ram.W8(addr, v); return v; }
        int Dec(int addr) { int v = (_ram.U8(addr) - 1) & 0xFF; _ram.W8(addr, v); return (sbyte)v; }
    }

    private void TimeOver()
    {
        _ram.W8(Vars.UpdateHudTimer, 0);
        throw new UnportedException(0x45452, "time over (KillCharacter)");
    }

    /// <summary>
    /// The decimal writers (0x4571C, 0x457F0): one 8x16 digit (2 tiles) per place, every place advancing 2 tiles.
    /// With leading-zero suppression nothing is written for leading zeros (the slot keeps whatever it had).
    /// </summary>
    private void Number(int vram, long value, int[] places, bool suppressLeadingZeros)
    {
        bool started = false;
        foreach (int place in places)
        {
            int digit = (int)(value / place);
            value %= place;
            if (digit != 0) started = true;
            if (started || !suppressLeadingZeros) Copy(_digits, digit * 2, 2, vram);
            vram += 0x40;
        }
    }

    /// <summary>0x45842: 4 digits; leading zeros (and a zero value) are written as blank tiles.</summary>
    private void Bonus(int vram, int value)
    {
        bool started = false;
        foreach (int place in new[] { 1000, 100, 10, 1 })
        {
            int digit = value / place;
            value %= place;
            if (digit != 0) started = true;
            if (started) Copy(_digits, digit * 2, 2, vram);
            else Vram?.AsSpan(vram, 64).Clear();
            vram += 0x40;
        }
    }

    /// <summary>Hud_Lives (0x458B0): two 8x8 digits; a leading zero is a blank tile.</summary>
    private void Lives()
    {
        int vram = VramLives;
        long value = _ram.U8(Vars.LifeCount);
        bool started = false;
        for (int i = 0; i < Seconds.Length; i++, vram += 0x40)
        {
            int digit = (int)(value / Seconds[i]);
            value %= Seconds[i];
            if (digit != 0) started = true;
            if (started || i == Seconds.Length - 1) Copy(_livesDigits, digit, 1, vram);
            else Vram?.AsSpan(vram, 32).Clear();
        }
    }

    /// <summary>Two tiles per entry from the digit art; $FF entries are blank (0x4565E).</summary>
    private void WriteTileList(int vram, int[] tiles)
    {
        foreach (int t in tiles)
        {
            if (t >= 0x80) Vram?.AsSpan(vram, 64).Clear();
            else Copy(_digits, t, 2, vram);
            vram += 64;
        }
    }

    private void Copy(byte[] art, int tile, int count, int vram) =>
        art.AsSpan(tile * 32, count * 32).CopyTo(Vram.AsSpan(vram));
}
