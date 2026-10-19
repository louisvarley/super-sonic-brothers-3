namespace SonicBros3.Logic;

/// <summary>
/// LoadSubObject (ROM 0x3A814): Sonic 2's later badniks set themselves up from a table indexed by subtype (word
/// offsets at 0x3A848) of {mappings, art tile, render flags (or'ed in), priority, half width, collision flags}. The
/// entries the ported objects use are transcribed here with their addresses.
/// </summary>
public static class SubObjects
{
    public readonly record struct Data(uint Mappings, int Art, int RenderFlags, int Priority, int Width, int Collision);

    /// <summary>By subtype.</summary>
    private static readonly Dictionary<int, Data> Table = new()
    {
        [0x1E] = new(0x3BF94, 0x03EE, 0x04, 5, 0x0C, 0x09),   // 0x3BF7C: Coconuts
        [0x20] = new(0x3BF94, 0x03EE, 0x84, 4, 0x08, 0x8B),   // 0x3B98A: Coconuts' coconut (object $98)
    };

    public static void Load(SonicObject o)
    {
        if (!Table.TryGetValue(o.Subtype, out var d)) throw new UnportedException(0x3A814, $"LoadSubObject entry for subtype {o.Subtype:X2}");
        o.Mappings = d.Mappings;
        o.ArtTile = d.Art;   // (Adjust2PArtPointer: 1-player, unchanged)
        o.RenderFlags |= d.RenderFlags;
        o.Priority = d.Priority;
        o.WidthPixels = d.Width;
        o.CollisionFlags = d.Collision;
        o.Routine += 2;
    }

    /// <summary>
    /// Obj_GetOrientationToPlayer (0x3A8F6): the nearer player horizontally (Sonic on a tie), whether it is to the
    /// right (true: the original's d0 = 2) and below (d1 = 2), and the object's x minus the player's (d2).
    /// </summary>
    public static (SonicObject Player, bool PlayerRight, bool PlayerBelow, int Dx) OrientationToPlayer(Ram ram, SonicObject o)
    {
        var player = ram.Sonic;
        int dx = (short)(o.X - ram.Sonic.X);
        int dxTails = (short)(o.X - ram.Tails.X);
        if ((Math.Abs(dx) & 0xFFFF) > (Math.Abs(dxTails) & 0xFFFF)) { player = ram.Tails; dx = dxTails; }
        bool below = o.Y < player.Y;   // sub.w / bcc: unsigned
        return (player, dx < 0, below, dx);
    }
}

/// <summary>
/// Coconuts, object $9D in Sonic 2's zones (ROM 0x3BDFA): the monkey on Emerald Hill's palm trees. It climbs up and down
/// the trunk in a fixed pattern of steps, facing the nearer player; when that player is within $60 px horizontally, it
/// stops (every 32 frames at most) and throws a coconut (<see cref="Coconut"/>).
/// </summary>
public sealed class Coconuts(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x3BF94;
    private readonly byte[] _anims = ctx.Art["coconuts"].Animations!;
    private const int Init = 0, Waiting = 2, Climbing = 4, Throwing = 6;
    private const int Aiming = 0, Thrown = 2;
    private const int CoconutId = 0x98, CoconutSubtype = 0x20;
    /// <summary>The climb steps (0x3BEA2): the high byte of y_vel, then the frames it lasts.</summary>
    private static readonly (int Speed, int Frames)[] ClimbSteps = [(0xFF, 0x20), (0x01, 0x18), (0xFF, 0x10), (0x01, 0x28), (0xFF, 0x20), (0x01, 0x10)];

    /// <summary>The monkey's own variables.</summary>
    private readonly struct State(SonicObject o)
    {
        /// <summary>$2A: frames left in the current step.</summary>
        public int Timer { get => o.Byte(0x2A); set => o.SetByte(0x2A, value); }
        /// <summary>$2C: the next climb step (x2).</summary>
        public int ClimbStep { get => o.Word(0x2C); set => o.SetWord(0x2C, value); }
        /// <summary>$2E: frames before it can throw again.</summary>
        public int ThrowDelay { get => o.Byte(0x2E); set => o.SetByte(0x2E, value); }
    }

    public override void Run(SonicObject o)
    {
        var s = new State(o);
        switch (o.Routine)
        {
            case Init:
                SubObjects.Load(o);
                s.Timer = 0x10;
                return;   // (not displayed this frame)
            case Waiting: Wait(o, s); break;
            case Climbing:
                // 0x3BEAE.
                s.Timer--;
                if (s.Timer == 0)
                {
                    o.Routine = Waiting;
                    s.Timer = 0x10;
                    break;
                }
                ObjectKit.Move(o);
                ObjectKit.Animate(Ram, o, _anims);
                break;
            case Throwing:
                if (o.RoutineSecondary == Aiming) Aim(o, s);
                else AfterThrow(o, s);
                break;
        }
        DisplayOrMarkGone(o);   // MarkObjGone (0x1A118)
    }

    /// <summary>0x3BE1C: face the nearer player; throw when near; climb when the wait is over.</summary>
    private void Wait(SonicObject o, State s)
    {
        var (_, playerRight, _, dx) = SubObjects.OrientationToPlayer(Ram, o);
        o.RenderFlags &= ~Render.XFlip;
        o.Status &= ~ObjStatus.XFlip;
        if (playerRight)
        {
            o.RenderFlags |= Render.XFlip;
            o.Status |= ObjStatus.XFlip;
        }
        bool near = ((dx + 0x60) & 0xFFFF) < 0xC0;
        if (near)
        {
            if (s.ThrowDelay == 0)
            {
                o.Routine = Throwing;
                o.MappingFrame = 1;
                s.Timer = 8;
                s.ThrowDelay = 0x20;
                return;
            }
            s.ThrowDelay--;
        }
        s.Timer--;
        if ((sbyte)s.Timer >= 0) return;
        o.Routine = Climbing;
        NextClimbStep(o, s);
    }

    /// <summary>0x3BE82.</summary>
    private static void NextClimbStep(SonicObject o, State s)
    {
        int step = s.ClimbStep;
        if (step >= ClimbSteps.Length * 2) step = 0;
        s.ClimbStep = step + 2;
        var (speed, frames) = ClimbSteps[step / 2];
        o.SetByte(0x12, speed);   // y_vel's high byte only
        s.Timer = frames;
    }

    /// <summary>0x3BEEA: hold the coconut up for 8 frames, then throw.</summary>
    private void Aim(SonicObject o, State s)
    {
        s.Timer--;
        if ((sbyte)s.Timer >= 0) return;
        o.RoutineSecondary = Thrown;
        s.Timer = 8;
        o.MappingFrame = 2;
        ThrowCoconut(o);
    }

    /// <summary>0x3BF06: 8 frames later, climb on.</summary>
    private static void AfterThrow(SonicObject o, State s)
    {
        s.Timer--;
        if ((sbyte)s.Timer >= 0) return;
        o.RoutineSecondary = 0;
        o.Routine = Climbing;
        s.Timer = 8;
        NextClimbStep(o, s);
    }

    /// <summary>0x3BF22.</summary>
    private void ThrowCoconut(SonicObject o)
    {
        if (Spawn() is not { } c) return;
        c.Id = CoconutId;
        c.MappingFrame = 3;
        c.Subtype = CoconutSubtype;
        c.X = o.X;
        c.Y = o.Y - 13;
        bool facingRight = (o.RenderFlags & Render.XFlip) != 0;
        // 0x3BF74: (x offset, x speed) facing right / facing left.
        (int dx, int speed) = facingRight ? (-0x0B, 0x100) : (0x0B, -0x100);
        c.X += dx;
        c.XVel = (short)speed;
        c.YVel = -0x100;
        c.SetLong(0x2A, Coconut.FallRoutine);
    }
}

/// <summary>
/// Object $98 in Sonic 2's zones (ROM 0x3B8F0): a projectile set up by LoadSubObject that runs the routine its thrower
/// stored at $2A each frame, and is deleted once off screen. Only Coconuts' coconut (0x3B930: fall with gravity $20) is
/// ported.
/// </summary>
public sealed class Coconut(ObjectContext ctx) : GameObject(ctx)
{
    public const uint FallRoutine = 0x3B930;
    private const int Init = 0, Main = 2;

    public override void Run(SonicObject o)
    {
        if (o.Routine == Init) { SubObjects.Load(o); return; }
        if (!o.OnScreen) { Delete(o); return; }
        uint routine = o.Long(0x2A);
        if (routine != FallRoutine) throw new UnportedException((int)routine, "object $98 projectile routine");
        o.YVel += 0x20;
        ObjectKit.Move(o);
        DisplayOrMarkGone(o);   // MarkObjGone (0x1A118)
    }
}
