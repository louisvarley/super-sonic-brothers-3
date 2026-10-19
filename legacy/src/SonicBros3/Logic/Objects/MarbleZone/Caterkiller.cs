namespace SonicBros3.Logic;

/// <summary>
/// Caterkiller, object $78 (ROM 0x154CCE, Sonic 1's): a head (an enemy) and three body segments (which hurt;
/// touching one marks it hit).
///
/// The head waits 7 frames, then inches forward for 16 frames (alternately pushing off at -$C0 and gliding),
/// following the floor. It records each frame's height change in a 16-entry history ($80 = turned round at a ledge
/// or step). Each segment follows the piece in front of it, replaying that piece's history. A hit splits the chain:
/// every piece bounces away. When the head is destroyed the segments go too.
/// </summary>
public sealed class Caterkiller(ObjectContext ctx) : GameObject(ctx)
{
    public const int Id = 0x78;
    public const uint Mappings = 0x1550C8;
    private const int Init = 0, Head = 2, FirstSegment = 4, MiddleSegment = 6, LastSegment = 8, Gone = 0x0A, Bouncing = 0x0C;
    private const int Waiting = 0, Inching = 2;
    private const int TurnedRound = 0x80;
    private const int ExplosionId = 0x27;

    /// <summary>0x1551E8: the body's rise as it inches (indexed by the angle; negative = the end of a move).</summary>
    private readonly byte[] _wave = ctx.Art.DataTable("mz-caterkiller/wave.json").Data;
    /// <summary>0x155062: the speeds the pieces split at (head, segments).</summary>
    private static readonly short[] SplitSpeeds = [-0x200, -0x180, 0x180, 0x200];

    /// <summary>A piece's variables.</summary>
    private readonly struct State(SonicObject o)
    {
        /// <summary>$2A: frames left in the wait or the move (head).</summary>
        public int StepTimer { get => o.Byte(0x2A); set => o.SetByte(0x2A, value); }
        /// <summary>$2B: bit 4 the pushing phase, bit 7 the body is rising (animating).</summary>
        public int Motion { get => o.Byte(0x2B); set => o.SetByte(0x2B, value); }
        /// <summary>$2C-$3B: the height history (16 entries); $3C: the next entry.</summary>
        public int History(int i) => o.Byte(0x2C + i);
        public void SetHistory(int i, int value) => o.SetByte(0x2C + i, value);
        public int HistoryIndex { get => o.Byte(0x3C); set => o.SetByte(0x3C, value); }
        /// <summary>Segments, $3E: the slot of the piece in front.</summary>
        public int InFront => o.Word(0x3E);
    }

    public override void Run(SonicObject o)
    {
        var s = new State(o);
        switch (o.Routine)
        {
            case Init: Initialise(o, s); break;
            case Head: UpdateHead(o, s); break;
            case FirstSegment or LastSegment: FollowInFront(o, s); break;
            case MiddleSegment: UpdateMiddle(o, s); break;
            case Gone: o.Clear(); break;
            case Bouncing: Bounce(o); break;
        }
    }

    /// <summary>0x154CEC: fall to the floor, then build the body.</summary>
    private void Initialise(SonicObject o, State s)
    {
        o.YRadius = 7;
        o.XRadius = 8;
        ObjectKit.MoveAndFall(o);
        int distance = (short)ObjectKit.CheckFloorDist(Ram, Ctx.Collision, o);
        if (distance >= 0) return;
        o.Y += distance;
        o.YVel = 0;
        o.Routine = Head;
        o.Mappings = Mappings;
        o.ArtTile = CurrentZone == 0x1B ? 0x22B0 : 0x2444;
        o.RenderFlags = (o.RenderFlags & 3) | Render.LevelCoords;
        o.Status = o.RenderFlags;
        o.Priority = 4;
        o.WidthPixels = 8;
        o.CollisionFlags = 0x0B;
        int x = o.X, spacing = (o.Status & ObjStatus.XFlip) != 0 ? -0x0C : 0x0C;
        int routine = FirstSegment, historyIndex = 4, inFront = o.Address;
        for (int i = 0; i < 3; i++)
        {
            int slot = ObjectKit.FindFreeSlotAfter(Ram, o);
            if (slot < 0) { GoAway(o); return; }
            var segment = new SonicObject(Ram, slot);
            segment.Id = Id;
            segment.Routine = routine;
            routine += 2;
            segment.Mappings = o.Mappings;
            segment.ArtTile = o.ArtTile;
            segment.Priority = 5;
            segment.WidthPixels = 8;
            segment.CollisionFlags = 0xCB;
            x = (x + spacing) & 0xFFFF;
            segment.X = x;
            segment.Y = o.Y;
            segment.Status = o.Status;
            segment.RenderFlags = o.Status;
            segment.MappingFrame = 8;
            segment.SetLong(0x3C, 0xFFFF0000u | (uint)inFront);   // $3E: the piece in front
            new State(segment).HistoryIndex = historyIndex;        // ($3C: a 4-entry head start per segment)
            historyIndex += 4;
            inFront = slot;
        }
        s.StepTimer = 7;
        s.HistoryIndex = 0;
        UpdateHead(o, s);
    }

    /// <summary>0x154E38: gone out of range: its respawn entry is cleared, deleted next frame.</summary>
    private void GoAway(SonicObject o)
    {
        if (o.RespawnIndex != 0)
        {
            int entry = ObjectKit.RespawnEntry(Ram, o);
            Ram.W8(entry, Ram.U8(entry) & 0x7F);
        }
        o.Routine = Gone;
        o.CollisionFlags = 0;
    }

    private void UpdateHead(SonicObject o, State s)
    {
        if ((o.Status & ObjStatus.Destroyed) != 0) { Split(o); return; }
        if (o.RoutineSecondary == Waiting) Wait(o, s);
        else Inch(o, s);
        int motion = s.Motion;
        if ((motion & 0x80) != 0)
        {
            int i = o.Angle & 0x7F;
            o.Angle += 4;
            int rise = _wave[i];
            if ((sbyte)rise < 0) s.Motion = motion & 0x7F;   // the rise is over
            else o.MappingFrame = rise + (motion & 0x10);
        }
        if (OutOfRange(o.X)) { GoAway(o); return; }
        Display(o);
    }

    /// <summary>0x154E62.</summary>
    private void Wait(SonicObject o, State s)
    {
        s.StepTimer--;
        if ((sbyte)s.StepTimer >= 0) return;
        o.RoutineSecondary = Inching;
        s.StepTimer = 0x10;
        o.XVel = -0xC0;
        o.Inertia = 0x40;
        bool wasPushing = (s.Motion & 0x10) != 0;
        s.Motion ^= 0x10;
        if (!wasPushing)
        {
            o.XVel = 0;
            o.Inertia = (short)-o.Inertia;
        }
        s.Motion |= 0x80;
        Inch(o, s);
    }

    /// <summary>0x154E96.</summary>
    private void Inch(SonicObject o, State s)
    {
        s.StepTimer--;
        if ((sbyte)s.StepTimer < 0)
        {
            o.RoutineSecondary = Waiting;
            s.StepTimer = 7;
            o.XVel = 0;
            o.Inertia = 0;
            return;
        }
        if (o.XVel == 0) return;
        if (!Advance(o, o.XVel)) return;
        int distance = (short)ObjectKit.CheckFloorDist(Ram, Ctx.Collision, o);
        if (distance < -8 || distance >= 0x0C) { TurnRound(o, s, isHead: true); return; }
        o.Y += distance;
        int k = s.HistoryIndex;
        s.HistoryIndex = (k + 1) & 0x0F;
        s.SetHistory(k, distance);
    }

    /// <summary>x.l += speed (mirrored when flipped) * 256; true when the whole-pixel x changed.</summary>
    private static bool Advance(SonicObject o, int speed)
    {
        uint x = o.XLong;
        int old = (int)(x >> 16);
        int step = (o.Status & ObjStatus.XFlip) != 0 ? -speed : speed;
        o.XLong = x + (uint)(step << 8);
        return o.X != old;
    }

    /// <summary>0x154F08 / 0x154FE2: turn round at a ledge or step, recording it in the history.</summary>
    private static void TurnRound(SonicObject o, State s, bool isHead)
    {
        if (isHead) s.SetHistory(s.HistoryIndex, TurnedRound);
        o.XSub = -o.XSub;
        if (o.XSub != 0 && (o.Status & ObjStatus.XFlip) != 0 && (isHead || o.XVel == -0xC0))
        {
            o.X--;
            s.HistoryIndex++;
            s.SetHistory(s.HistoryIndex, 0);
        }
        o.Status ^= ObjStatus.XFlip;
        o.RenderFlags = o.Status;
        s.HistoryIndex = (s.HistoryIndex + 1) & 0x0F;
    }

    /// <summary>0x154F4C: the middle segment's frame looks 4 steps ahead.</summary>
    private void UpdateMiddle(SonicObject o, State s)
    {
        s.Motion = new State(new SonicObject(Ram, s.InFront)).Motion;
        if ((s.Motion & 0x80) != 0)
        {
            int i = o.Angle & 0x7F;
            o.Angle += 4;
            if ((sbyte)_wave[(i + 4) & 0xFF] < 0) o.Angle += 4;
            o.MappingFrame = _wave[i] + 8;
        }
        FollowInFront(o, s);
    }

    /// <summary>0x154F7E: follow the piece in front, replaying its history.</summary>
    private void FollowInFront(SonicObject o, State s)
    {
        var front = new SonicObject(Ram, s.InFront);
        var f = new State(front);
        if ((o.Status & ObjStatus.Destroyed) != 0)
        {
            front.Status |= ObjStatus.Destroyed;   // the hit splits the chain
            Split(o);
            return;
        }
        s.Motion = f.Motion;
        o.RoutineSecondary = front.RoutineSecondary;
        if (o.RoutineSecondary != Waiting)
        {
            o.Inertia = front.Inertia;
            o.XVel = (short)(front.XVel + o.Inertia);
            if (Advance(o, o.XVel))
            {
                int k = s.HistoryIndex;
                int change = f.History(k);
                if (change == TurnedRound)
                {
                    s.SetHistory(k, TurnedRound);
                    TurnRound(o, s, isHead: false);
                }
                else
                {
                    o.Y += (sbyte)change;
                    s.HistoryIndex = (k + 1) & 0x0F;
                    s.SetHistory(k, change);
                }
            }
        }
        if (front.Routine == Bouncing)
        {
            front.Status |= ObjStatus.Destroyed;
            Split(o);
            return;
        }
        if (front.Id == ExplosionId || front.Routine == Gone)
        {
            // The head was destroyed (or went): go too.
            o.Routine = Gone;
            o.CollisionFlags = 0;
        }
        Display(o);
    }

    /// <summary>0x155070: break away and bounce.</summary>
    private void Split(SonicObject o)
    {
        int r = o.Routine;
        int speed = r >= Head && r <= LastSegment ? SplitSpeeds[(r - Head) / 2] : 0;
        if ((o.Status & ObjStatus.XFlip) != 0) speed = -speed;
        o.XVel = (short)speed;
        o.YVel = -0x400;
        o.Routine = Bouncing;
        o.MappingFrame &= 0xF8;
        Bounce(o);
    }

    /// <summary>0x15509A.</summary>
    private void Bounce(SonicObject o)
    {
        ObjectKit.MoveAndFall(o);
        if (o.YVel >= 0)
        {
            int distance = (short)ObjectKit.CheckFloorDist(Ram, Ctx.Collision, o);
            if (distance < 0)
            {
                o.Y += distance;
                o.YVel = -0x400;
            }
        }
        if (!o.OnScreen) { GoAway(o); return; }
        Display(o);
    }
}
