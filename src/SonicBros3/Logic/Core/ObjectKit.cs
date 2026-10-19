namespace SonicBros3.Logic;

/// <summary>Shared routines object code calls (names follow the Sonic 2 disassembly).</summary>
public static class ObjectKit
{
    /// <summary>ObjectMoveAndFall (0x1A0C6): move by velocity, then add gravity $38 to y_vel.</summary>
    public static void MoveAndFall(SonicObject o)
    {
        int yv = o.YVel;
        o.YVel = (short)(yv + 0x38);
        o.XLong = (uint)((int)o.XLong + (o.XVel << 8));
        o.YLong = (uint)((int)o.YLong + (yv << 8));
    }

    /// <summary>ObjectMove (0x1A0F2).</summary>
    public static void Move(SonicObject o)
    {
        o.XLong = (uint)((int)o.XLong + (o.XVel << 8));
        o.YLong = (uint)((int)o.YLong + (o.YVel << 8));
    }

    /// <summary>
    /// AnimateSprite (0x1A28E) over a raw script block (word offsets at its start). Frame bytes: bits 0-4 mapping
    /// frame, bits 5-6 flip (XORed with the status flip bits). Commands: $FF loop, $FE n back n, $FD n switch anim,
    /// $FC routine+2, $FB restart and clear routine_secondary, $FA routine_secondary+2, $F9 $2A+2.
    /// </summary>
    public static void Animate(Ram ram, SonicObject o, byte[] block)
    {
        int anim = o.Anim;
        if (anim != o.PrevAnim)
        {
            o.PrevAnim = (byte)anim;
            o.AnimFrame = 0;
            o.AnimFrameDuration = 0;
        }
        o.AnimFrameDuration--;
        if ((sbyte)o.AnimFrameDuration >= 0) return;
        int script = (short)((block[anim * 2] << 8) | block[anim * 2 + 1]);
        o.AnimFrameDuration = block[script];
        int d1 = o.AnimFrame;
        int d0 = block[script + 1 + d1];
        if (d0 < 0x80) { Show(d0); return; }
        switch (d0)
        {
            case 0xFF:
                o.AnimFrame = 0;
                Show(block[script + 1]);
                return;
            case 0xFE:
            {
                int back = block[script + 2 + d1];
                o.AnimFrame = (byte)(o.AnimFrame - back);
                Show(block[script + 1 + (byte)(d1 - back)]);
                return;
            }
            case 0xFD: o.Anim = block[script + 2 + d1]; return;
            case 0xFC:
                o.Routine += 2;
                o.AnimFrameDuration = 0;
                o.AnimFrame++;
                return;
            case 0xFB:
                o.AnimFrame = 0;
                o.RoutineSecondary = 0;
                return;
            case 0xFA: o.RoutineSecondary += 2; return;
            case 0xF9: o.SetByte(0x2A, o.Byte(0x2A) + 2); return;
            default: return;
        }

        void Show(int frame)
        {
            o.MappingFrame = (byte)(frame & 0x1F);
            int flip = (((frame << 3) | (frame >> 5)) ^ o.Status) & 3;   // rol.b #3
            o.RenderFlags = (o.RenderFlags & ~3) | flip;
            o.AnimFrame++;
        }
    }

    /// <summary>SingleObjLoad2 (0x1BD76): first free slot after this one, up to $D000; -1 if none.</summary>
    public static int FindFreeSlotAfter(Ram ram, SonicObject o)
    {
        int count = (Vars.DynamicObjectRamEnd - o.Address) >> 6;
        int a = o.Address;
        for (int i = 0; i < count; i++)
        {
            if (ram.U8(a) == 0) return a;
            a += 0x40;
        }
        return -1;
    }

    /// <summary>RandomNumber (0x408C): seed at $F636, mixed with the VBlank counter at $FE0C. Returns the 32-bit d0.</summary>
    public static uint RandomNumber(Ram ram)
    {
        uint d1 = ram.U32(Vars.RngSeed) + ram.U32(Vars.VintRuncount);
        if (d1 == 0) d1 = 0x2A6D365A;
        uint d0 = d1;
        d1 = (d1 << 2) + d0;
        d1 = (d1 << 3) + d0;
        d0 = (d0 & 0xFFFF0000) | (d1 & 0xFFFF);
        d1 = (d1 << 16) | (d1 >> 16);
        d0 = (d0 & 0xFFFF0000) | ((d0 + d1) & 0xFFFF);
        d1 = (d1 & 0xFFFF0000) | (d0 & 0xFFFF);
        d1 = (d1 << 16) | (d1 >> 16);
        ram.W32(Vars.RngSeed, d1);
        return d0;
    }

    /// <summary>ObjCheckFloorDist (0x22CF2): distance below the object's feet (d1) on the current collision layer; angle byte at $F768.</summary>
    public static int CheckFloorDist(Ram ram, Collision col, SonicObject o)
    {
        int y = o.Y + o.YRadius;
        ram.W8(Vars.PrimaryAngle, 0);
        return col.FindFloor(y, o.X, 0x10, 0x0C, 0, Vars.PrimaryAngle);
    }

    /// <summary>
    /// ObjCheckFloorDist (0x22CF2) at the object's x, or 0x22CF6 with a given x: returns d1 (distance) and d3 (the
    /// surface angle from $F768, 0 when the probe's angle is odd, i.e. a "use the cardinal angle" marker).
    /// </summary>
    public static (int Distance, int Angle) CheckFloor(Ram ram, Collision col, SonicObject o, int? x = null)
    {
        int y = o.Y + o.YRadius;
        ram.W8(Vars.PrimaryAngle, 0);
        int d1 = col.FindFloor(y, x ?? o.X, 0x10, 0x0C, 0, Vars.PrimaryAngle);
        int d3 = ram.U8(Vars.PrimaryAngle);
        if ((d3 & 1) != 0) d3 = 0;
        return ((short)d1, d3);
    }

    /// <summary>ObjCheckCeilingDist (0x22EB6): distance above the object's head; angle at $F768 ($80 when odd).</summary>
    public static (int Distance, int Angle) CheckCeiling(Ram ram, Collision col, SonicObject o)
    {
        int y = ((o.Y - (sbyte)o.YRadius) ^ 0xF) & 0xFFFF;
        ram.W8(Vars.PrimaryAngle, 0);
        int d1 = col.FindFloor(y, o.X, -0x10, 0x0D, 0x800, Vars.PrimaryAngle);
        int d3 = ram.U8(Vars.PrimaryAngle);
        return ((short)d1, (d3 & 1) != 0 ? 0x80 : d3);
    }

    /// <summary>0x22DF8: wall distance probing rightwards from x + d3 (Sonic 1's ObjHitWallRight).</summary>
    public static int CheckRightWall(Ram ram, Collision col, SonicObject o, int d3)
    {
        ram.W8(Vars.PrimaryAngle, 0);
        return (short)col.FindWall(o.Y, (o.X + d3) & 0xFFFF, 0x10, 0x0D, 0, Vars.PrimaryAngle);
    }

    /// <summary>0x22F7E: wall distance probing leftwards from x + d3 (Sonic 1's ObjHitWallLeft).</summary>
    public static int CheckLeftWall(Ram ram, Collision col, SonicObject o, int d3)
    {
        ram.W8(Vars.PrimaryAngle, 0);
        return (short)col.FindWall(o.Y, (o.X + d3) & 0xFFFF, -0x10, 0x0D, 0x400, Vars.PrimaryAngle);
    }

    /// <summary>Sonic 1's out-of-range check (camera-based): x more than $280 past the column $80 left of the camera.</summary>
    public static bool OutOfRangeS1(Ram ram, int x) =>
        (((x & 0xFF80) - ((ram.U16(Vars.CameraXPos) - 0x80) & 0xFF80)) & 0xFFFF) > 0x280;

    /// <summary>
    /// 0x164D38 (the hack's): with $FFE7 set, badniks use the other palette line (art tile bit 13), except below the
    /// height at $FFE8 when $FFEA is set (and $F65F clear).
    /// </summary>
    public static void HackPalette(Ram ram, SonicObject o)
    {
        if (ram.U8(Vars.LavaLineEnabled) == 0) return;
        ram.W8(o.Address + 2, ram.U8(o.Address + 2) | 0x20);
        if (ram.U8(Vars.LavaLineMode) == 0 || ram.U8(Vars.SuperSonicPalette) != 0) return;
        if ((short)(ram.U16(Vars.LavaLineHeight) - o.Y - 0x0C) > 0) return;
        ram.W8(o.Address + 2, ram.U8(o.Address + 2) & ~0x20);
    }

    /// <summary>DeleteObject (0x1A22C): clear the slot.</summary>
    public static void Clear(Ram ram, int address) => ram.Bytes.AsSpan(address, 0x40).Clear();

    /// <summary>MarkObjGone (0x1A118) variant that displays the object while it is in range.</summary>
    public static void MarkObjGoneDisplay(Ram ram, SonicObject o)
    {
        if (ram.U16(Vars.TwoPlayerMode) != 0) { SpriteBuilder.DisplaySprite(ram, o); return; }
        int d0 = ((o.X & 0xFF80) - ram.U16(Vars.CameraXPosCoarse)) & 0xFFFF;
        if (d0 <= 0x280) { SpriteBuilder.DisplaySprite(ram, o); return; }
        int index = o.RespawnIndex;
        if (index != 0) ram.Clear(Vars.ObjectRespawnTable + 2 + index, 7);
        Objects.DeleteObject(ram, o);
    }

    /// <summary>Respawn table entry for this object (bit 0 "destroyed", bit 7 "loaded").</summary>
    public static int RespawnEntry(Ram ram, SonicObject o) => Vars.ObjectRespawnTable + 2 + o.RespawnIndex;
}
