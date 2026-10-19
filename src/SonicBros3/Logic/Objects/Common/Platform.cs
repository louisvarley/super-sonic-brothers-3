namespace SonicBros3.Logic;

/// <summary>
/// Platforms, object $18 (ROM 0x13064).
///
/// Subtype bits 4-6: the size (a width and frame from <see cref="Sizes"/>). Bit 7: solid on all sides (else only on
/// top). Low nibble, the movement:
///   0, 9  still
///   1, 5  swing sideways (5 the other way)
///   2, 6  swing up and down (6 the other way); $A, $B the same at half height
///   3     falls 30 frames after being stood on (then 4: falling)
///   7     waits for a switch, then rises (8: rising)
/// Swings follow the oscillating value <c>Oscillation.Value(6)</c>. The platform sags while stood on.
/// </summary>
public sealed class Platform(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x134C8, MappingsS2 = 0x13418;
    private const int Init = 0, TopSolid = 2, Gone = 4, FallingFree = 6, Solid = 8;
    private static readonly (int Width, int Frame)[] Sizes = [(0x20, 0), (0x20, 1), (0x20, 2), (0x40, 3), (0x30, 4)];
    private const int MaxSag = 0x40, SagStep = 4;
    private const int Gravity = 0x38;

    /// <summary>The platform's own variables.</summary>
    private readonly struct State(SonicObject o)
    {
        /// <summary>$2C: the y it bobs around (a long while falling: 16.16).</summary>
        public int BobY { get => o.Word(0x2C); set => o.SetWord(0x2C, value); }
        /// <summary>$32: the x it was placed at.</summary>
        public int BaseX { get => o.Word(0x32); set => o.SetWord(0x32, value); }
        /// <summary>$34: the y it was placed at.</summary>
        public int BaseY { get => o.Word(0x34); set => o.SetWord(0x34, value); }
        /// <summary>$38: how far the sag has eased in (0 to $40).</summary>
        public int Sag { get => o.Byte(0x38); set => o.SetByte(0x38, value); }
        /// <summary>$3A: frames until it falls or rises.</summary>
        public int Timer { get => o.Word(0x3A); set => o.SetWord(0x3A, value); }
    }

    public override void Run(SonicObject o)
    {
        var s = new State(o);
        switch (o.Routine)
        {
            case Init:
                if (Initialise(o, s)) UpdateSolid(o, s);
                else UpdateTopSolid(o, s);
                break;
            case TopSolid: UpdateTopSolid(o, s); break;
            case Gone: Delete(o); break;
            case FallingFree: Move(o, s); Bob(o, s); DisplayOrDelete(o, s); break;
            case Solid: UpdateSolid(o, s); break;
        }
    }

    /// <summary>Returns true for the solid variant (subtype bit 7).</summary>
    private bool Initialise(SonicObject o, State s)
    {
        o.Routine = TopSolid;
        int subtype = o.Subtype;
        var (width, frame) = Sizes[(subtype >> 4) & 7];
        o.WidthPixels = width;
        o.MappingFrame = frame;
        // 0x130A2: the mappings by zone (Sonic 2's zones, ARZ's own, the hack's from $11, Spring Yard on from $15).
        int zone = CurrentZone;
        o.Mappings = MappingsS2;
        o.ArtTile = 0x4000;
        if (zone == 0x0F) o.Mappings = 0x13470;
        if (zone >= 0x11)
        {
            o.Mappings = Mappings;
            if (subtype == 0x0A)
            {
                o.MappingFrame++;
                o.WidthPixels = 0x20;
            }
        }
        if (zone >= 0x15)
        {
            o.Mappings = 0x13540;
            o.WidthPixels = 0x20;
        }
        o.RenderFlags = Render.LevelCoords;
        o.Priority = 4;
        s.BobY = o.Y;
        s.BaseY = o.Y;
        s.BaseX = o.X;
        o.SetWord(0x26, 0x80);   // angle
        o.Subtype = subtype & 0x0F;
        if ((subtype & 0x80) == 0) return false;
        o.Routine = Solid;
        o.YRadius = 0x30;
        o.RenderFlags |= Render.UseYRadius;
        return true;
    }

    /// <summary>Ease the sag in while stood on, out otherwise.</summary>
    private static void UpdateSag(SonicObject o, State s)
    {
        if ((o.Status & (ObjStatus.StandingP1 | ObjStatus.StandingP2)) == 0)
        {
            if (s.Sag != 0) s.Sag -= SagStep;
        }
        else if (s.Sag != MaxSag) s.Sag += SagStep;
    }

    /// <summary>0x1318C.</summary>
    private void UpdateTopSolid(SonicObject o, State s)
    {
        UpdateSag(o, s);
        int oldX = o.X;
        Move(o, s);
        Bob(o, s);
        Ctx.Solid.PlatformObject(o, o.WidthPixels, 0, 8, oldX);
        DisplayOrDelete(o, s);
    }

    /// <summary>0x131F6.</summary>
    private void UpdateSolid(SonicObject o, State s)
    {
        UpdateSag(o, s);
        int oldX = o.X;
        Move(o, s);
        Bob(o, s);
        int halfHeight = (byte)o.YRadius;
        Ctx.Solid.SolidObject(o, o.WidthPixels + 0x0B, halfHeight, halfHeight + 1, oldX);
        DisplayOrDelete(o, s);
    }

    /// <summary>0x131D2.</summary>
    private void DisplayOrDelete(SonicObject o, State s)
    {
        if (!TwoPlayerMode && OutOfRange(s.BaseX)) Delete(o);
        else Display(o);
    }

    /// <summary>0x13240: y = bob y + sin(sag) * $400 >> 16.</summary>
    private static void Bob(SonicObject o, State s)
    {
        int sin = MdMath.CalcSine(s.Sag).Sin;
        o.Y = ((sin * 0x400) >> 16) + s.BobY;
    }

    /// <summary>0x1325A: movement by subtype.</summary>
    private void Move(SonicObject o, State s)
    {
        int angle = o.Angle;
        int swing = (sbyte)(angle - 0x40), reverse = (sbyte)(-angle + 0x40);
        switch (o.Subtype & 0x0F)
        {
            case 0: case 9: return;
            case 1: o.X = s.BaseX + swing; break;
            case 5: o.X = s.BaseX + reverse; break;
            case 2: s.BobY = s.BaseY + swing; break;
            case 6: s.BobY = s.BaseY + reverse; break;
            case 0x0A: s.BobY = s.BaseY + (swing >> 1); break;
            case 0x0B: s.BobY = s.BaseY + (reverse >> 1); break;
            case 3: WaitToFall(o, s); return;
            case 4: Fall(o, s); return;
            case 7: WaitForSwitch(o, s); return;
            case 8:   // rise $200 px, then stop
                s.BobY -= 2;
                if (s.BobY == ((s.BaseY - 0x200) & 0xFFFF)) o.Subtype = 0;
                return;
        }
        o.Angle = Ram.U8(Oscillation.Value(6));   // 0x13410
    }

    /// <summary>0x132FA: once stood on, wait 30 frames, then start falling.</summary>
    private void WaitToFall(SonicObject o, State s)
    {
        if (s.Timer == 0)
        {
            if ((o.Status & (ObjStatus.StandingP1 | ObjStatus.StandingP2)) != 0) s.Timer = 0x1E;
            return;
        }
        s.Timer--;
        if (s.Timer != 0) return;
        s.Timer = 0x20;
        o.Subtype++;   // 4: falling
    }

    /// <summary>0x13324: after 32 frames drop the riders and fall under gravity; deleted below the level.</summary>
    private void Fall(SonicObject o, State s)
    {
        if (s.Timer != 0)
        {
            s.Timer--;
            if (s.Timer == 0)
            {
                if (Ram.TestClear(o.StatusAddr, 3)) Drop(o, Sonic);
                if (Ram.TestClear(o.StatusAddr, 4)) Drop(o, Tails);
                o.Routine = FallingFree;
            }
        }
        o.SetLong(0x2C, (uint)((int)o.Long(0x2C) + (o.YVel << 8)));   // the bob y as 16.16
        o.YVel += Gravity;
        int limit = (Ram.U16(Vars.CameraMaxYPos) + 0x120) & 0xFFFF;
        if (limit < s.BobY) o.Routine = Gone;
    }

    /// <summary>The player falls with the platform.</summary>
    private static void Drop(SonicObject o, SonicObject player)
    {
        player.InAir = true;     // in the air
        player.OnObject = false;   // not on an object
        player.Routine = 2;
        player.YVel = o.YVel;
    }

    /// <summary>0x1339A: wait for the switch flag (subtype high nibble), then rise after 60 frames.</summary>
    private void WaitForSwitch(SonicObject o, State s)
    {
        if (s.Timer == 0)
        {
            if (Ram.U8(Vars.ButtonVineTrigger + (o.Subtype >> 4)) != 0) s.Timer = 0x3C;
            return;
        }
        s.Timer--;
        if (s.Timer == 0) o.Subtype++;   // 8: rising
    }
}
