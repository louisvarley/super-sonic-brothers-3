namespace SonicBros3.Logic;

/// <summary>
/// The solid-object family (ROM 0x1D4AE-0x1DCEC): objects that players can stand on, push against or hit from below.
/// Register conventions follow the original: d1 = half width, d2 = half height (jumping), d3 = half height
/// (walking), d4 = the object's x before it moved this frame. d6 is the player's status bit in the object (3 for
/// Sonic, 4 for Tails) and collects contact flags in its upper bits: side 16/17, bottom 18/19, top 20/21.
/// Returns d4: 0 none, 1 side, -1 top, -2 bottom.
/// </summary>
public sealed class Solid(Ram ram)
{
    private const int P1 = Vars.MainCharacter, P2 = Vars.Sidekick;

    public readonly record struct Result(int D4, int D6);

    private static bool Bit(Ram ram, int addr, int bit) => (ram.U8(addr) & (1 << (bit & 7))) != 0;
    private static void BSet(Ram ram, int addr, int bit) => ram.W8(addr, ram.U8(addr) | (1 << (bit & 7)));
    private static void BClr(Ram ram, int addr, int bit) => ram.W8(addr, ram.U8(addr) & ~(1 << (bit & 7)));

    // ------------------------------------------------------------------ entry points

    /// <summary>SolidObject (0x1D4AE): Sonic always, Tails only while both are on screen.</summary>
    public Result SolidObject(SonicObject o, int d1, int d2, int d3, int d4)
    {
        int d6 = 3;
        int r = PerPlayer(o, new SonicObject(ram, P1), d1, d2, d3, d4, ref d6, onScreenTest: true);
        if ((sbyte)ram.U8(P2 + 1) >= 0 || (sbyte)o.RenderFlags >= 0) return new(d4, d6);
        d6 = (d6 & ~0xFF) | ((d6 + 1) & 0xFF);
        r = PerPlayer(o, new SonicObject(ram, P2), d1, d2, d3, d4, ref d6, onScreenTest: true);
        return new(r, d6);
    }

    /// <summary>SolidObject_Always (0x1D516): no on-screen test.</summary>
    public Result SolidObjectAlways(SonicObject o, int d1, int d2, int d3, int d4)
    {
        int d6 = 3;
        PerPlayer(o, new SonicObject(ram, P1), d1, d2, d3, d4, ref d6, onScreenTest: false);
        d6 = (d6 & ~0xFF) | ((d6 + 1) & 0xFF);
        int r = PerPlayer(o, new SonicObject(ram, P2), d1, d2, d3, d4, ref d6, onScreenTest: false);
        return new(r, d6);
    }

    /// <summary>The per-player body of SolidObject_Always (0x1D52C), with a1/d6 chosen by the caller.</summary>
    public int SolidAlwaysOne(SonicObject o, SonicObject p, int d1, int d2, int d3, int d4, ref int d6) =>
        PerPlayer(o, p, d1, d2, d3, d4, ref d6, onScreenTest: false);

    /// <summary>The per-player body of SolidObject (0x1D4D4), with a1/d6 chosen by the caller.</summary>
    public int SolidOne(SonicObject o, SonicObject p, int d1, int d2, int d3, int d4, ref int d6) =>
        PerPlayer(o, p, d1, d2, d3, d4, ref d6, onScreenTest: true);

    private int PerPlayer(SonicObject o, SonicObject p, int d1, int d2, int d3, int d4, ref int d6, bool onScreenTest)
    {
        if (!Bit(ram, o.StatusAddr, d6))
        {
            if (onScreenTest && (sbyte)o.RenderFlags >= 0) return NotTouching(o, p, ref d6);
            return SolidObjectCont(o, p, d1, d2, d3, ref d6);
        }
        if (!StillOn(o, p, d1, d6)) return 0;
        MoveOnPlatform(o, p, d4, d3);
        return 0;
    }

    /// <summary>The standing check shared by every entry point: walk off the edges or leave in the air -> detach.</summary>
    public bool StillOn(SonicObject o, SonicObject p, int d1, int d6)
    {
        int d2 = (d1 * 2) & 0xFFFF;
        if (!p.InAir)
        {
            int d0 = (p.X - o.X + d1) & 0xFFFF;
            if ((short)d0 >= 0 && d0 < d2) return true;
        }
        BClr(ram, p.StatusAddr, 3);
        BSet(ram, p.StatusAddr, 1);
        BClr(ram, o.StatusAddr, d6);
        return false;
    }

    /// <summary>SolidObject_cont (0x1D78E): one player against the object's box.</summary>
    public int SolidObjectCont(SonicObject o, SonicObject p, int d1, int d2, int d3, ref int d6)
    {
        int d0 = (p.X - o.X + d1) & 0xFFFF;
        if ((short)d0 < 0) return NotTouching(o, p, ref d6);
        d3 = (d1 * 2) & 0xFFFF;
        if (d0 > d3) return NotTouching(o, p, ref d6);
        d2 = (d2 + p.YRadius) & 0xFFFF;
        d3 = (p.Y - o.Y + 4 + d2) & 0xFFFF;
        if ((short)d3 < 0) return NotTouching(o, p, ref d6);
        d3 &= 0x7FF;
        int d4 = (d2 * 2) & 0xFFFF;
        if (d3 >= d4) return NotTouching(o, p, ref d6);
        return Touching(o, p, d0, d1, d2, d3, d4, ref d6);
    }

    /// <summary>
    /// SlopedSolid_SingleCharacter (0x1D584): a solid object whose top follows a height table (one signed byte per 2
    /// px from the left, mirrored when the object is flipped). Standing players are carried along the slope; others
    /// collide as with SolidObject, the top being raised by the slope at their x. Returns d4 (-2 = hit from below).
    /// </summary>
    public int SlopedSolidOne(SonicObject o, SonicObject p, int d1, int d2, int d4, sbyte[] slope, ref int d6)
    {
        if (Bit(ram, o.StatusAddr, d6))
        {
            int width = (d1 * 2) & 0xFFFF;
            if (!p.InAir)
            {
                int x = (p.X - o.X + d1) & 0xFFFF;
                if ((short)x >= 0 && x < width) { MoveWithSlope(o, p, d1, d4, slope); return 0; }
            }
            BClr(ram, p.StatusAddr, 3);
            BSet(ram, p.StatusAddr, 1);
            BClr(ram, o.StatusAddr, d6);
            return 0;
        }
        // 0x1D6CC
        int d0 = (p.X - o.X + d1) & 0xFFFF;
        if ((short)d0 < 0) return NotTouching(o, p, ref d6);
        int d3 = (d1 * 2) & 0xFFFF;
        if (d0 > d3) return NotTouching(o, p, ref d6);
        int d5 = d0;
        if ((o.RenderFlags & 1) != 0) d5 = (~d5 + d3) & 0xFFFF;
        d5 >>= 1;
        d3 = (sbyte)(byte)(slope[d5] - slope[0]);
        d5 = (o.Y - d3) & 0xFFFF;
        d2 = (d2 + p.YRadius) & 0xFFFF;
        d3 = (p.Y - d5 + 4 + d2) & 0xFFFF;
        if ((short)d3 < 0) return NotTouching(o, p, ref d6);
        int d4b = (d2 * 2) & 0xFFFF;
        if (d3 >= d4b) return NotTouching(o, p, ref d6);
        return Touching(o, p, d0, d1, d2, d3, d4b, ref d6);
    }

    /// <summary>0x1D96A: a standing player rides the slope (and the object's x movement).</summary>
    private void MoveWithSlope(SonicObject o, SonicObject p, int d1, int oldX, sbyte[] slope)
    {
        if (!Bit(ram, p.StatusAddr, 3)) return;
        int d0 = ((p.X - o.X + d1) & 0xFFFF) >> 1;
        if ((o.RenderFlags & 1) != 0) d0 = (~d0 + d1) & 0xFFFF;
        int h = slope[d0];
        p.Y = (o.Y - h - (byte)p.YRadius) & 0xFFFF;
        p.X = (p.X - (oldX - o.X)) & 0xFFFF;
    }

    /// <summary>0x1D7CC: the player overlaps the box; resolve by the shallower axis.</summary>
    private int Touching(SonicObject o, SonicObject p, int d0, int d1, int d2, int d3, int d4, ref int d6)
    {
        if ((sbyte)p.ObjControl < 0) return NotTouching(o, p, ref d6);
        if (p.Routine >= 6 || ram.U16(Vars.DebugPlacementMode) != 0) return 0;
        int d5 = d0;
        if (d1 < d0)
        {
            d1 = (d1 * 2) & 0xFFFF;
            d0 = (d0 - d1) & 0xFFFF;
            d5 = (-d0) & 0xFFFF;
        }
        d1 = d3;
        if (d2 < d3)
        {
            d3 = (d3 - 4 - d4) & 0xFFFF;
            d1 = (-d3) & 0xFFFF;
        }
        if (d5 > d1) return Vertical(o, p, d0, d1, d3, ref d6);
        return Side(o, p, d0, d1, ref d6);
    }

    /// <summary>0x1D808: side contact (or a shallow overlap of at most 4 px, which only clears pushing).</summary>
    /// <summary>d0 as a side contact left it: the x overlap, positive when the player is on the left (push blocks read it).</summary>
    public int LastSideD0 { get; private set; }

    private int Side(SonicObject o, SonicObject p, int d0, int d1, ref int d6)
    {
        LastSideD0 = (short)d0;
        if (d1 <= 4)
        {
            ClearPush(o, p, d6);
            d6 |= 1 << ((d6 + 0x0D) & 31);
            return 1;
        }
        bool stop = (short)d0 != 0 && ((short)d0 < 0 ? p.XVel < 0 : p.XVel >= 0);
        if (stop) { p.Inertia = 0; p.XVel = 0; }
        p.X = (p.X - d0) & 0xFFFF;
        if (p.InAir)
        {
            ClearPush(o, p, d6);
            d6 |= 1 << ((d6 + 0x0D) & 31);
            return 1;
        }
        BSet(ram, o.StatusAddr, d6 + 2);
        BSet(ram, p.StatusAddr, 5);
        d6 |= 1 << ((d6 + 0x0D) & 31);
        return 1;
    }

    /// <summary>0x1D88C: top (landing) or bottom contact.</summary>
    private int Vertical(SonicObject o, SonicObject p, int d0, int d1, int d3, ref int d6)
    {
        if ((short)d3 >= 0)
        {
            if (d3 < 0x10 || (o.Id == 0x85 && d3 < 0x14)) return Land(o, p, d3, ref d6);
            return NotTouching(o, p, ref d6);
        }
        if (p.YVel == 0)
        {
            if (!p.InAir)
            {
                int d4 = Math.Abs((short)d0);
                if (d4 < 0x10) return Side(o, p, d0, d1, ref d6);
                throw new UnportedException(0x1D8E0, "crushed by a solid object (KillCharacter)");
            }
        }
        else if (p.YVel < 0 && (short)d3 < 0)
        {
            p.Y = (p.Y - d3) & 0xFFFF;
            p.YVel = 0;
        }
        d6 |= 1 << ((d6 + 0x0F) & 31);
        return -2;
    }

    /// <summary>0x1D8F4: land on top if within the object's width ($19) and not moving up.</summary>
    private int Land(SonicObject o, SonicObject p, int d3, ref int d6)
    {
        d3 = (d3 - 4) & 0xFFFF;
        int d1 = o.WidthPixels;
        int d2 = d1 * 2;
        d1 = (d1 + p.X - o.X) & 0xFFFF;
        if ((short)d1 < 0 || d1 >= d2) return 0;
        if (p.YVel < 0) return 0;
        p.Y = (p.Y - d3 - 1) & 0xFFFF;
        SetRide(o, p, d6);
        d6 |= 1 << ((d6 + 0x11) & 31);
        return -1;
    }

    /// <summary>0x1D862: not touching; stop pushing if this player was.</summary>
    private int NotTouching(SonicObject o, SonicObject p, ref int d6)
    {
        if (Bit(ram, o.StatusAddr, d6 + 2))
        {
            if (p.Anim != PlayerAnim.Roll) { p.Anim = PlayerAnim.Walk; p.PrevAnim = 1; }   // restart the walk
            ClearPush(o, p, d6);
        }
        return 0;
    }

    private void ClearPush(SonicObject o, SonicObject p, int d6)
    {
        BClr(ram, o.StatusAddr, d6 + 2);
        BClr(ram, p.StatusAddr, 5);
    }

    // ------------------------------------------------------------------ standing

    /// <summary>MvSonicOnPtfm (0x1D930): keep a standing player on top (d3 above y) and carry it by the object's x delta.</summary>
    public void MoveOnPlatform(SonicObject o, SonicObject p, int oldX, int d3) => MoveOnPlatformTo(o, p, oldX, (o.Y - d3) & 0xFFFF);

    /// <summary>MvSonicOnPtfm2 (0x1D938): fixed height 9.</summary>
    public void MoveOnPlatform2(SonicObject o, SonicObject p, int oldX) => MoveOnPlatformTo(o, p, oldX, (o.Y - 9) & 0xFFFF);

    private void MoveOnPlatformTo(SonicObject o, SonicObject p, int oldX, int top)
    {
        if ((sbyte)p.ObjControl < 0 || p.Routine >= 6 || ram.U16(Vars.DebugPlacementMode) != 0) return;
        p.Y = (top - (byte)p.YRadius) & 0xFFFF;
        p.X = (p.X - (oldX - o.X)) & 0xFFFF;
    }

    /// <summary>RideObject_SetRide (0x1DBB2): the player now stands on this object.</summary>
    public void SetRide(SonicObject o, SonicObject p, int d6)
    {
        if (p.OnObject)
        {
            int other = p.InteractObject.Address;
            BClr(ram, other + 0x22, d6);
        }
        p.Interact = Objects.SlotIndex(o.Address);
        p.Angle = 0;
        p.YVel = 0;
        p.Inertia = p.XVel;
        if (p.InAir)
        {
            // Sonic's or Tails' landing reset (0x1EE7C / 0x209BC); Player picks by object id.
            Player.ResetOnFloorPart2(ram, p);
        }
        BSet(ram, p.StatusAddr, 3);
        BClr(ram, p.StatusAddr, 1);
        BSet(ram, o.StatusAddr, d6);
    }

    // ------------------------------------------------------------------ sloped platforms

    /// <summary>
    /// SlopedPlatform (0x1DA28): top-solid with a height profile (one signed byte per 2 px, mirrored when the
    /// object is x-flipped).
    /// </summary>
    public void SlopedPlatform(SonicObject o, int d1, int d4, sbyte[] slope)
    {
        int d6 = 3;
        SlopedPerPlayer(o, new SonicObject(ram, P1), d1, d4, slope, d6);
        SlopedPerPlayer(o, new SonicObject(ram, P2), d1, d4, slope, d6 + 1);
    }

    private void SlopedPerPlayer(SonicObject o, SonicObject p, int d1, int d4, sbyte[] slope, int d6)
    {
        if (!Bit(ram, o.StatusAddr, d6))
        {
            // SlopedPlatform_cont (0x1DC2E).
            if (p.YVel < 0) return;
            int d0 = (p.X - o.X + d1) & 0xFFFF;
            int w = (d1 * 2) & 0xFFFF;
            if ((short)d0 < 0 || d0 >= w) return;
            if ((o.RenderFlags & 1) != 0) d0 = ((~d0) + w) & 0xFFFF;
            int d3 = slope[d0 >> 1];
            ChkYRange(o, p, (o.Y - d3) & 0xFFFF, d6);
            return;
        }
        if (!StillOn(o, p, d1, d6)) return;
        // MvSonicOnSlope (0x1D96A).
        if (!p.OnObject) return;
        int i = ((p.X - o.X + d1) & 0xFFFF) >> 1;
        if ((o.RenderFlags & 1) != 0) i = ((~i) + d1) & 0xFFFF;
        int h = slope[i];
        p.Y = (o.Y - h - (byte)p.YRadius) & 0xFFFF;
        p.X = (p.X - (d4 - o.X)) & 0xFFFF;
    }

    // ------------------------------------------------------------------ platforms (top-solid only)

    /// <summary>PlatformObject (0x1D9D0).</summary>
    public Result PlatformObject(SonicObject o, int d1, int d2, int d3, int d4)
    {
        int d6 = 3;
        PlatformPerPlayer(o, new SonicObject(ram, P1), d1, d3, d4, ref d6);
        d6 = (d6 & ~0xFF) | ((d6 + 1) & 0xFF);
        PlatformPerPlayer(o, new SonicObject(ram, P2), d1, d3, d4, ref d6);
        return new(0, d6);
    }

    private void PlatformPerPlayer(SonicObject o, SonicObject p, int d1, int d3, int d4, ref int d6)
    {
        if (!Bit(ram, o.StatusAddr, d6)) { PlatformCont(o, p, d1, d3, d6); return; }
        if (!StillOn(o, p, d1, d6)) return;
        MoveOnPlatform(o, p, d4, d3);
    }

    /// <summary>PlatformObject_cont (0x1DB58): land on the top if falling onto it within its width.</summary>
    public void PlatformCont(SonicObject o, SonicObject p, int d1, int d3, int d6)
    {
        if (p.YVel < 0) return;
        int d0 = (p.X - o.X + d1) & 0xFFFF;
        if ((short)d0 < 0 || d0 >= ((d1 * 2) & 0xFFFF)) return;
        ChkYRange(o, p, (o.Y - d3) & 0xFFFF, d6);
    }

    /// <summary>PlatformObject_ChkYRange (0x1DB7C): feet within 16 px above the top -> stand on it.</summary>
    public void ChkYRange(SonicObject o, SonicObject p, int top, int d6)
    {
        int d2 = p.Y;
        int feet = (d2 + p.YRadius + 4) & 0xFFFF;
        if (top > feet) return;
        int d0 = (top - feet) & 0xFFFF;
        if (d0 < 0xFFF0) return;
        if ((sbyte)p.ObjControl < 0 || p.Routine >= 6) return;
        p.Y = (d2 + (short)d0 + 3) & 0xFFFF;
        SetRide(o, p, d6);
    }
}
