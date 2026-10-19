namespace SonicBros3.Logic;

/// <summary>
/// The hack's "lava line" (its own code, 0x164470 / 0x16461A): in its zones ($FFE7 set at level start) a per-act
/// routine sets a line height $FFE8 from the camera position each frame; below that line the H-interrupt rewrites
/// palette colours 18-20 (from $FFDA) and 30 (from $FFEC when set). $FFEA: 0 = line below the screen, 1 = on screen
/// at line $F625, 2 = the camera is below the line. In Green Hill, entering or leaving the line's stretch also swaps
/// the art at tile $500 (0x164CEC).
/// </summary>
public sealed class LavaLine(Ram ram, ObjectArt art, byte[]? vram)
{

    /// <summary>0x164470 (level start).</summary>
    public void Init()
    {
        ram.W8(Vars.LavaLineEnabled, 0);
        if (ram.U8(Vars.HackZoneFlag) == 0 || ram.U8(Vars.WaterFlag) != 0) return;
        ram.W8(Vars.LavaLineEnabled, 1);
        ram.W8(Vars.LavaLinePaletteMode, 0);
        ram.W16(Vars.LavaLineHeight, 0xFFFF);
        ram.W16(Vars.LavaLineExtra, 0);
        int zone = ram.U8(Vars.CurrentZoneAndAct);
        int set = zone < 0x13 ? 0 : zone < 0x19 ? 1 : zone < 0x1B ? 2 : 1;
        var colours = art.DataTable("lava-line/colours.json").Data;
        for (int i = 0; i < 6; i++) ram.W8(Vars.LavaLineBelow + i, colours[set * 6 + i]);
        ram.W8(Vars.LavaLinePaletteState, 0);   // 0x4E88
    }

    /// <summary>0x16461A (each frame, before the objects run).</summary>
    public void Step()
    {
        if (ram.U8(Vars.LavaLineEnabled) == 0) return;
        var (d1, d2) = Zone();
        SetHeight(d1, d2);
        if (ram.U8(Vars.LavaLinePaletteMode) != 0) throw new UnportedException(0x1644DE, $"lava-line palette mode {ram.U8(Vars.LavaLinePaletteMode)}");
        ram.W8(Vars.LavaLineMode, 1);
        int d0 = (ram.U16(Vars.LavaLineHeight) - ram.U16(Vars.CameraYPos)) & 0xFFFF;
        if (ram.U16(Vars.LavaLineHeight) < ram.U16(Vars.CameraYPos))
        {
            ram.W8(Vars.LavaLineScanline, 0xDF);
            ram.W8(Vars.LavaLineMode, 2);
            return;
        }
        if (d0 >= 0xDF)
        {
            d0 = 0xDF;
            ram.W8(Vars.LavaLineMode, 0);
        }
        ram.W8(Vars.LavaLineScanline, d0);
    }

    /// <summary>0x16467A: the act's line height (d1, $FFFF = none) and, in Green Hill, its art set (d2).</summary>
    private (int Height, int Art) Zone()
    {
        int camX = ram.U16(Vars.CameraXPos), camY = ram.U16(Vars.CameraYPos);
        switch (ram.U16(Vars.CurrentZoneAndAct))
        {
            case 0x1100: return Stretch(camX, 0x1CC0, 0x1E80, 0x310);
            case 0x1101: return Stretch(camX, 0x15C0, 0x1780, 0x220);
            case 0x1200: return Stretch(camX, 0x6C0, 0x840, 0x320);
            case 0x1201: return (0xFFFF, 0);
            case 0x1300:   // 0x16475C
                if (camX < 0x440) return (0x244, 0);
                if (camX < 0x900) return (0x184, 0);
                if (camX < 0x1340) return (0xFFFF, 0);
                if (camX < 0x1700) return (0x260, 0);
                return (0, 0);
            case 0x1301: return Mz2(camX, camY);
            case 0x1400: return Mz3(camX, camY);
            case 0x1401: return (0xFFFF, 0);
            case var zoneAndAct when Camera.IsEasterEgg(zoneAndAct >> 8): return (0xFFFF, 0);
            default: throw new UnportedException(0x16467A, $"lava line for zone/act {ram.U16(Vars.CurrentZoneAndAct):X4}");
        }

        static (int, int) Stretch(int camX, int from, int to, int height) =>
            camX < from ? (0xFFFF, 0) : camX < to ? (height, 1) : (0xFFFF, 0);
    }

    /// <summary>0x16479C.</summary>
    private static (int, int) Mz2(int camX, int camY)
    {
        int d1;
        if (camX >= 0x1200) goto L814;
        if (camX >= 0xE00) goto L7F8;
        if (camX < 0x340) return (0x274, 0);
        if (camX < 0x500 && camY < 0x3E0) return (0x3C0, 0);
        if (camX >= 0xA80) goto L7F8;
        if (camY >= 0x3E0) return (0xFFFF, 0);
        if (camX >= 0x700) goto L7F8;
        return (0xFFFF, 0);
    L7F8:
        if (camX < 0xE00) return (0x18C, 0);
        if (camY >= 0x120) goto L814;
        return (0x18C, 0);
    L814:
        if (camX < 0x1200) return (0x4B0, 0);
        d1 = camY >= 0x4A0 ? 0x540 : 0;
        return (d1, 0);
    }

    /// <summary>0x164838.</summary>
    private static (int, int) Mz3(int camX, int camY)
    {
        if (camX >= 0x1600) return (0xFFFF, 0);
        if (camX >= 0x1200)
        {
            if (camY < 0x3A0) return (0xFFFF, 0);
            return camY < 0x560 ? (0x4E0, 0) : (0xFFFF, 0);
        }
        if (camX >= 0xC00) return (0x600, 0);
        return camY < 0x360 ? (0x174, 0) : (0x520, 0);
    }

    /// <summary>0x164CEC.</summary>
    private void SetHeight(int d1, int d2)
    {
        ram.W16(Vars.LavaLineHeight, d1);
        int zone = ram.U8(Vars.CurrentZoneAndAct);
        if (zone == 0x16) { ram.W8(Vars.LavaLinePaletteState, d2); return; }
        if (zone is not (0x11 or 0x12)) return;
        if (ram.U8(Vars.LavaLinePaletteState) == d2) return;
        ram.W8(Vars.LavaLinePaletteState, d2);
        if (vram is null) return;
        var data = art.DataTable(d2 == 0 ? "lava-line/ghz-art-0.json" : "lava-line/ghz-art-1.json").Data;
        data.AsSpan(0, Math.Min(data.Length, 0xAA0)).CopyTo(vram.AsSpan(0xA000));
    }

    /// <summary>The colours below the line (for the renderer), or null when there is no split on screen.</summary>
    public (int Line, ushort[] Cram)? Split(ushort[] cram)
    {
        if (ram.U8(Vars.LavaLineEnabled) == 0 || ram.U8(Vars.LavaLineMode) == 0) return null;
        var below = (ushort[])cram.Clone();
        for (int i = 0; i < 3; i++) below[18 + i] = ram.U16(Vars.LavaLineBelow + i * 2);
        if (ram.U16(Vars.LavaLineExtra) != 0) below[30] = ram.U16(Vars.LavaLineExtra);
        return (ram.U8(Vars.LavaLineScanline) + 1, below);
    }
}
