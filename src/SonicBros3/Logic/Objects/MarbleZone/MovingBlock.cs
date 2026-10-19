namespace SonicBros3.Logic;

/// <summary>
/// The moving block, object $52 (ROM 0x1549D8, Sonic 1's, on Sonic 2's PlatformObject). Size and frame by subtype
/// bits 4-7. Low nibble:
///   1        sways with oscillator 3 (x-flip inverts)
///   2, 4, 9  wait to be stood on, then become the next type
///   3, 5     move right until a wall (3 then stops; 5 then falls as 6)
///   6        falls until it lands
///   7        waits hidden for switch 2, then becomes 4
///   8        rises and sinks with oscillator 7
///   $A       goes out (twice its width) and back, waiting 5 seconds out there
/// </summary>
public sealed class MovingBlock(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x154C3E;
    private static readonly (int Width, int Frame)[] Sizes = [(0x10, 0), (0x20, 1), (0x20, 2), (0x40, 3), (0x30, 4)];
    private const int Sways = 1, MovesUntilWall = 3, Falls = 6, WaitsForSwitch = 7, RisesAndSinks = 8, OutAndBack = 0x0A;

    /// <summary>The block's own variables.</summary>
    private readonly struct State(SonicObject o)
    {
        /// <summary>$30, $32: where it was placed (x follows it while it moves right).</summary>
        public int BaseX { get => o.Word(0x30); set => o.SetWord(0x30, value); }
        public int BaseY { get => o.Word(0x32); set => o.SetWord(0x32, value); }
        /// <summary>$34: frames to wait out there; $36: going back.</summary>
        public int Wait { get => o.Word(0x34); set => o.SetWord(0x34, value); }
        public bool Returning { get => o.Word(0x36) != 0; set => o.SetWord(0x36, value ? 1 : 0); }
    }

    public override void Run(SonicObject o)
    {
        var s = new State(o);
        switch (o.Routine)
        {
            case 0:
                Initialise(o, s);
                goto case 2;
            case 2:
                if (o.Subtype == WaitsForSwitch) { Move(o, s); return; }
                int oldX = o.X;
                Move(o, s);
                if (o.Id == 0) return;
                Ctx.Solid.PlatformObject(o, o.WidthPixels, -0x10, 9, oldX);
                break;
            default:
                throw new UnportedException(0x154AA4, "moving block routine 4");
        }
        if (OutOfRange(s.BaseX)) { o.Clear(); return; }
        Display(o);
    }

    private void Initialise(SonicObject o, State s)
    {
        o.Routine = 2;
        o.Mappings = Mappings;
        o.ArtTile = 0x4500;
        int zone = CurrentZone;
        if (zone == 0x17)
        {
            o.Mappings = 0x154CC2;
            o.ArtTile = 0x43BC;
            o.YRadius = 7;
        }
        if (zone == 0x1B) o.ArtTile = o.Subtype == 0x28 ? 0x22C0 : 0x4429;
        o.RenderFlags = Render.LevelCoords;
        int size = ((o.Subtype >> 3) & 0x1E) / 2;
        var (width, frame) = size < Sizes.Length ? Sizes[size] : Sizes[0];
        o.WidthPixels = width;
        o.MappingFrame = frame;
        o.Priority = 4;
        s.BaseX = o.X;
        s.BaseY = o.Y;
        o.Subtype &= 0x0F;
    }

    /// <summary>0x154AD4.</summary>
    private void Move(SonicObject o, State s)
    {
        int w = o.WidthPixels;
        bool flipped = (o.Status & ObjStatus.XFlip) != 0;
        switch (o.Subtype & 0x0F)
        {
            case Sways:
            {
                int swing = Ram.U8(Oscillation.Value(3));
                if (flipped) swing = -swing + 0x60;
                o.X = s.BaseX - swing;
                break;
            }
            case 2 or 4 or 9:
                if ((o.Status & (ObjStatus.StandingP1 | ObjStatus.StandingP2)) != 0) { o.RoutineSecondary = 2; o.Subtype++; }
                break;
            case MovesUntilWall or 5:
                if (ObjectKit.CheckLeftWall(Ram, Ctx.Collision, o, w) >= 0)
                {
                    o.X++;
                    s.BaseX = o.X;
                }
                else if ((o.Subtype & 0x0F) == MovesUntilWall) o.Subtype = 0;
                else o.Subtype++;   // 6: fall
                break;
            case Falls:
            {
                ObjectKit.Move(o);
                o.YVel += 0x18;
                int distance = (short)ObjectKit.CheckFloorDist(Ram, Ctx.Collision, o);
                if (distance >= 0) break;
                o.Y += distance;
                o.YVel = 0;
                o.Subtype = 0;
                break;
            }
            case WaitsForSwitch:
                if (Ram.U8(Vars.ButtonVineTrigger + 2) != 0) o.Subtype -= 3;   // 4: wait to be stood on
                if (OutOfRange(s.BaseX)) o.Clear();
                break;
            case RisesAndSinks:
            {
                int swing = Ram.U8(Oscillation.Value(7));
                if (flipped) swing = -swing + 0x80;
                o.Y = s.BaseY - swing;
                break;
            }
            case OutAndBack:
            {
                int distance = flipped ? -w * 2 : w * 2, step = flipped ? -8 : 8;
                int offset = (short)(o.X - s.BaseX);
                if (!s.Returning)
                {
                    if (offset == (short)distance)
                    {
                        s.Wait--;
                        if (s.Wait == 0) s.Returning = true;
                    }
                    else
                    {
                        o.X += step;
                        s.Wait = 0x12C;
                    }
                }
                else if (offset == 0)
                {
                    s.Returning = false;
                    o.Subtype--;
                }
                else o.X -= step;
                break;
            }
        }
    }
}
