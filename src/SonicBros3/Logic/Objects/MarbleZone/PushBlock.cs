namespace SonicBros3.Logic;

/// <summary>
/// The push block, object $33 (ROM 0x154564, Sonic 1's Marble Zone, as the hack has it). Solid. A player pushing it
/// while facing it moves it 1 px a frame (and the player, with ground speed +-$40 and a scrape sound), unless a wall
/// stops it. Pushed over an edge it slides off ($400) and falls. Landing on lava (block index $16A and up) it floats
/// along at an eighth of its sliding speed, and sinks slowly once stopped, until gone.
///
/// Zone specifics: in act $1300 between x $A20 and $AA1 it rides the stomper below
/// (<see cref="Vars.ChainStomperY"/>); at set places of $1301 and $1400 it starts a lava geyser (object $4C).
/// Leaving the screen, it goes back to its start once that is out of view.
/// </summary>
public sealed class PushBlock(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x1549A8;
    private const int Init = 0, Main = 2, WaitOffScreen = 4;
    // The block's state (routine_secondary).
    private const int Free = 0, Carrying = 2, Falling = 4, SlidingOff = 6;
    private static readonly (int Width, int Frame)[] Sizes = [(0x10, 0), (0x40, 1)];
    private const int FirstLavaBlock = 0x16A;
    private const int GeyserMakerId = 0x4C;
    private const int SfxPush = 0xA7;

    /// <summary>The block's own variables.</summary>
    private readonly struct State(SonicObject o)
    {
        /// <summary>$30: the speed it slid off the edge at (it floats on lava at an eighth of it).</summary>
        public int SlideSpeed { get => o.Word(0x30); set => o.SetWord(0x30, value); }
        /// <summary>$32: floating on lava.</summary>
        public bool OnLava { get => o.Byte(0x32) != 0; set => o.SetByte(0x32, value ? 1 : 0); }
        /// <summary>$34, $36: where it was placed.</summary>
        public int StartX { get => o.Word(0x34); set => o.SetWord(0x34, value); }
        public int StartY { get => o.Word(0x36); set => o.SetWord(0x36, value); }
    }

    public override void Run(SonicObject o)
    {
        var s = new State(o);
        switch (o.Routine)
        {
            case Init:
                if (!Initialise(o, s)) return;
                goto case Main;
            case Main:
                Update(o, s);
                break;
            case WaitOffScreen:   // 0x1546AC: wait at the start until off screen
                if (IsOnScreen(o)) return;
                o.Routine = Main;
                s.OnLava = false;
                o.XVel = 0;
                o.YVel = 0;
                break;
        }
    }

    /// <summary>0x15457C. False when it was already moved (respawn bit 0): gone.</summary>
    private bool Initialise(SonicObject o, State s)
    {
        o.Routine = Main;
        o.YRadius = 0x0F;
        o.XRadius = 0x0F;
        o.Mappings = Mappings;
        o.ArtTile = CurrentZone == 0x17 ? 0x43DE : 0x4500;
        o.RenderFlags = Render.LevelCoords;
        o.Priority = 3;
        s.StartX = o.X;
        s.StartY = o.Y;
        int size = o.Subtype & 7;
        var (width, frame) = size < Sizes.Length ? Sizes[size] : Sizes[0];
        o.WidthPixels = width;
        o.MappingFrame = frame;
        if (o.Subtype != 0) o.ArtTile = 0xC500;
        if (o.RespawnIndex != 0)
        {
            int entry = ObjectKit.RespawnEntry(Ram, o);
            Ram.W8(entry, Ram.U8(entry) & 0x7F);
            bool moved = (Ram.U8(entry) & 1) != 0;
            Ram.W8(entry, Ram.U8(entry) | 1);
            if (moved) { o.Clear(); return false; }
        }
        return true;
    }

    private void Update(SonicObject o, State s)
    {
        int oldX = o.X;
        if (!s.OnLava)
        {
            Solid(o, s, o.WidthPixels + 0x0B, 0x10, 0x11, o.X);
            if (Ram.U16(Vars.CurrentZoneAndAct) == 0x1300)
            {
                // Ride the stomper below.
                o.Subtype &= 0x7F;
                if (o.X >= 0xA20 && o.X < 0xAA1)
                {
                    o.Y = Ram.U16(Vars.ChainStomperY) - 0x1C;
                    Ram.W8(Vars.ChainStomperY, Ram.U8(Vars.ChainStomperY) | 0x80);
                    o.Subtype |= 0x80;
                }
            }
        }
        else if (!FloatOnLava(o, s, oldX)) { GoBack(o, s); return; }
        if (!OutOfRange(o.X)) { Display(o); return; }
        GoBack(o, s);
    }

    /// <summary>0x154670: back to the start (and wait there off screen) if that is in range, else gone for good.</summary>
    private void GoBack(SonicObject o, State s)
    {
        if (!OutOfRange(s.StartX))
        {
            o.X = s.StartX;
            o.Y = s.StartY;
            o.Routine = WaitOffScreen;
            if (IsOnScreen(o)) return;
            o.Routine = Main;
            s.OnLava = false;
            o.XVel = 0;
            o.YVel = 0;
            return;
        }
        if (o.RespawnIndex != 0)
        {
            int entry = ObjectKit.RespawnEntry(Ram, o);
            Ram.W8(entry, Ram.U8(entry) & 0xFE);
        }
        o.Clear();
    }

    /// <summary>0x1539BA: on screen, counting its width.</summary>
    private bool IsOnScreen(SonicObject o)
    {
        int w = o.WidthPixels;
        int dx = (short)(o.X - Ram.U16(Vars.CameraXPos) + w);
        if (dx < 0) return false;
        dx -= w * 2;
        if (dx >= 0x140) return false;
        int dy = (short)(o.Y - Ram.U16(Vars.CameraYPos));
        return dy >= 0 && dy < 0xE0;
    }

    /// <summary>0x1546C6: floating on lava (or falling into it). False when it has sunk (riders drop off).</summary>
    private bool FloatOnLava(SonicObject o, State s, int oldX)
    {
        if (o.RoutineSecondary < Falling) ObjectKit.Move(o);
        if ((o.Status & 2) != 0)
        {
            // Still falling.
            o.YVel += 0x18;
            int distance = (short)ObjectKit.CheckFloorDist(Ram, Ctx.Collision, o);
            if (distance < 0)
            {
                o.Y += distance;
                o.YVel = 0;
                o.Status &= ~2;
                Landed(o, s);
            }
        }
        else if (o.XVel != 0)
        {
            int w = o.WidthPixels;
            int wall = o.XVel > 0 ? ObjectKit.CheckLeftWall(Ram, Ctx.Collision, o, w) : ObjectKit.CheckRightWall(Ram, Ctx.Collision, o, ~w);
            if (wall < 0) o.XVel = 0;
        }
        else
        {
            // Stopped: sink $2000 sub-pixels a frame, counting the frames in y_sub's low byte (gone after $A0).
            o.YLong += 0x2001;
            if (o.Byte(0x0F) >= 0xA0)
            {
                Sonic.Status &= ~8;   // not on an object
                o.Status &= ~ObjStatus.StandingP1;
                return false;
            }
        }
        Solid(o, s, o.WidthPixels + 0x0B, 0x10, 0x11, oldX);
        StartGeyser(o);
        return true;
    }

    /// <summary>Landed: on lava, float along at an eighth of the slide speed.</summary>
    private void Landed(SonicObject o, State s)
    {
        if ((Ram.U16(Ctx.Collision.LastTile) & 0x3FF) < FirstLavaBlock) return;
        o.XVel = (short)((short)s.SlideSpeed >> 3);
        s.OnLava = true;
        o.YSub = 0;
    }

    /// <summary>0x15479C: lava geysers at set places.</summary>
    private void StartGeyser(SonicObject o)
    {
        int dx;
        int zoneAct = Ram.U16(Vars.CurrentZoneAndAct);
        if (zoneAct == 0x1301 && o.X is 0xDD0 or 0xCC0 or 0xBA0) dx = -0x20;
        else if (zoneAct == 0x1400 && o.X is 0x560 or 0x5C0) dx = 0x20;
        else return;
        if (Spawn() is not { } maker) return;
        maker.Id = GeyserMakerId;
        maker.X = o.X + dx;
        maker.Y = o.Y + 0x10;
        maker.SetLong(0x3C, 0xFFFF0000u | (uint)o.Address);
    }

    /// <summary>0x154808: the block's solidity and movement, by its state.</summary>
    private void Solid(SonicObject o, State s, int halfWidth, int halfHeight, int standHeight, int oldX)
    {
        switch (o.RoutineSecondary)
        {
            case Free:
            {
                int standingBit = 3;
                int contact = Ctx.Solid.SolidOne(o, Sonic, halfWidth, halfHeight, standHeight, oldX, ref standingBit);
                Push(o, s, Sonic, contact, Ctx.Solid.LastSideD0);
                if (!Tails.OnScreen) return;
                standingBit = 4;
                contact = Ctx.Solid.SolidOne(o, Tails, halfWidth, halfHeight, standHeight, oldX, ref standingBit);
                if ((o.Status & ObjStatus.PushingP1) != 0) return;
                Push(o, s, Tails, contact, Ctx.Solid.LastSideD0);
                return;
            }
            case Carrying:
                if ((Sonic.Status & 8) == 0) { o.RoutineSecondary = Free; return; }
                Ctx.Solid.MoveOnPlatform(o, Sonic, oldX, standHeight);
                return;
            case Falling:
            {
                ObjectKit.Move(o);
                o.YVel += 0x18;
                int distance = (short)ObjectKit.CheckFloorDist(Ram, Ctx.Collision, o);
                if (distance >= 0) return;
                o.Y += distance;
                o.YVel = 0;
                o.RoutineSecondary = Free;
                Landed(o, s);
                return;
            }
            default:   // SlidingOff: slide over the edge until 16-px aligned, then fall
                ObjectKit.Move(o);
                if ((o.X & 0x0C) != 0) return;
                o.X &= 0xFFF0;
                s.SlideSpeed = o.XVel;
                o.XVel = 0;
                o.RoutineSecondary = Falling;
                return;
        }
    }

    /// <summary>0x1548D0: a side contact by a player facing the block pushes it, unless a wall is in the way.</summary>
    /// <param name="side">Which side the player is on (SolidObject's d0): positive left of it, negative right.</param>
    private void Push(SonicObject o, State s, SonicObject player, int contact, int side)
    {
        if ((short)contact <= 0 || s.OnLava || side == 0) return;
        int w = o.WidthPixels;
        int step;
        if (side > 0)
        {
            if (player.FacingLeft) return;
            if (ObjectKit.CheckLeftWall(Ram, Ctx.Collision, o, w) < 0) return;
            step = 1;
        }
        else
        {
            if (!player.FacingLeft) return;
            if (ObjectKit.CheckRightWall(Ram, Ctx.Collision, o, ~w) < 0) return;
            step = -1;
        }
        o.XLong = (uint)(o.XLong + step * 0x10000);
        player.X += step;
        player.Inertia = (short)(step * 0x40);
        player.XVel = 0;
        Ctx.Sound.PlaySound(SfxPush);
        if ((o.Subtype & 0x80) != 0) return;   // riding the stomper
        int floor = (short)ObjectKit.CheckFloorDist(Ram, Ctx.Collision, o);
        if (floor <= 4) { o.Y += floor; return; }
        // Over the edge.
        o.XVel = (short)(step * 0x400);
        o.RoutineSecondary = SlidingOff;
    }
}
