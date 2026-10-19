namespace SonicBros3.Logic;

/// <summary>
/// Buzz Bomber, object $22 in the hack's zones (ROM 0x151B12; Sonic 1's). Flies at 4 px a frame for 128 frames,
/// then pauses and turns round. When Sonic comes within $60 px horizontally while it is on screen, it stops and,
/// after the pause, fires a projectile (<see cref="Missile"/>).
/// </summary>
public sealed class BuzzBomber(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x151C88;
    private readonly byte[] _anims = ctx.Art["buzz-bomber"].Animations!;

    private const int Init = 0, Main = 2, Gone = 4;
    private const int Waiting = 0, Flying = 2;
    private const int AnimHover = 0, AnimFly = 1, AnimFire = 2;
    private const int FlySpeed = 0x400, FlyFrames = 0x7F, PauseFrames = 0x3B, AimFrames = 0x1D;
    private const int SightRange = 0x60;

    /// <summary>The Buzz Bomber's own variables.</summary>
    private readonly struct State(SonicObject o)
    {
        /// <summary>$32: frames left in the current flight or pause.</summary>
        public int Timer { get => o.Word(0x32); set => o.SetWord(0x32, value); }
        /// <summary>$34: 0 hunting, 2 Sonic seen (fire after the pause), 1 fired (no more until it turns).</summary>
        public int Attack { get => o.Byte(0x34); set => o.SetByte(0x34, value); }
    }
    private const int Hunting = 0, Fired = 1, SonicSeen = 2;

    public override void Run(SonicObject o)
    {
        switch (o.Routine)
        {
            case Init:
                o.Routine = Main;
                o.Mappings = Mappings;
                o.ArtTile = ZoneArt(0x406);
                o.RenderFlags = Render.LevelCoords;
                o.Priority = 3;
                o.CollisionFlags = 8;
                o.WidthPixels = 0x18;
                goto case Main;
            case Main:
                if (o.RoutineSecondary == Waiting) Wait(o, new State(o));
                else Fly(o, new State(o));
                ObjectKit.Animate(Ram, o, _anims);
                DisplayOrMarkGone(o);
                break;
            case Gone: Delete(o); break;
        }
    }

    /// <summary>Zone-dependent art tile (0x151B38).</summary>
    private int ZoneArt(int normal)
    {
        int zone = CurrentZone;
        int palette = normal & 0xF800;
        return zone < 0x13 ? normal : zone < 0x15 ? palette | 0x298 : palette | 0x53D;
    }

    /// <summary>0x151B8E: pause; then fire if Sonic was seen, else fly off.</summary>
    private void Wait(SonicObject o, State s)
    {
        s.Timer--;
        if ((short)s.Timer >= 0) return;
        if ((s.Attack & SonicSeen) != 0) { Fire(o, s); return; }
        o.RoutineSecondary = Flying;
        s.Timer = FlyFrames;
        o.Anim = AnimFly;
        o.XVel = (o.Status & ObjStatus.XFlip) != 0 ? (short)FlySpeed : (short)-FlySpeed;
    }

    /// <summary>0x151BC0.</summary>
    private void Fire(SonicObject o, State s)
    {
        if (Spawn() is not { } m) return;
        bool right = (o.Status & ObjStatus.XFlip) != 0;
        m.Id = Missile.Id;
        m.X = o.X + (right ? 0x18 : -0x18);
        m.Y = o.Y + 0x1C;
        m.YVel = 0x200;
        m.XVel = right ? (short)0x200 : (short)-0x200;
        m.Status = o.Status;
        m.SetWord(0x32, 0x0E);                          // the missile's delay
        m.SetLong(0x3C, 0xFFFF0000u | (uint)o.Address);  // its owner
        s.Attack = Fired;
        s.Timer = PauseFrames;
        o.Anim = AnimFire;
    }

    /// <summary>0x151C24: fly; stop when the time is up or Sonic comes into sight.</summary>
    private void Fly(SonicObject o, State s)
    {
        s.Timer--;
        if ((short)s.Timer < 0)
        {
            s.Attack = Hunting;
            o.Status ^= ObjStatus.XFlip;   // turn round
            s.Timer = PauseFrames;
            Stop(o);
            return;
        }
        ObjectKit.Move(o);
        if (s.Attack != Hunting) return;
        int distance = Math.Abs((short)(Sonic.X - o.X));
        if (distance >= SightRange || !o.OnScreen) return;
        s.Attack = SonicSeen;
        s.Timer = AimFrames;
        Stop(o);
    }

    private static void Stop(SonicObject o)
    {
        o.RoutineSecondary = Waiting;
        o.XVel = 0;
        o.Anim = AnimHover;
    }
}

/// <summary>
/// The Buzz Bomber and Newtron projectile, object $23 in the hack's zones (ROM 0x151DD2; Sonic 1's).
/// - Buzz Bomber's (subtype 0) waits, shows the muzzle flare, then flies. It vanishes if its owner has exploded.
/// - Newtron's (other subtypes) flies straight while on screen.
/// </summary>
public sealed class Missile(ObjectContext ctx) : GameObject(ctx)
{
    public const int Id = 0x23;
    public const uint Mappings = 0x151ED4;
    private readonly byte[] _anims = ctx.Art["missile"].Animations!;

    private const int Delay = 0, Flare = 2, Flying = 4, Gone = 6, NewtronFlying = 8;
    private const int AnimFlare = 0, AnimFly = 1;
    private const int ExplosionId = 0x27;

    /// <summary>$32: frames to wait before appearing.</summary>
    private static int DelayTimer(SonicObject o) => o.Word(0x32);
    /// <summary>$3C: the owner's slot (long, $FFFFxxxx).</summary>
    private static int Owner(SonicObject o) => (int)(o.Long(0x3C) & 0xFFFF);

    public override void Run(SonicObject o)
    {
        switch (o.Routine)
        {
            case Delay:
                o.SetWord(0x32, DelayTimer(o) - 1);
                if ((short)DelayTimer(o) >= 0) { CheckOwner(o); return; }
                o.Routine = Flare;
                o.Mappings = Mappings;
                int zone = CurrentZone;
                o.ArtTile = zone < 0x13 ? 0x2406 : zone < 0x15 ? 0x2298 : 0x253D;
                o.RenderFlags = Render.LevelCoords;
                o.Priority = 3;
                o.WidthPixels = 8;
                o.Status &= ObjStatus.XFlip | ObjStatus.YFlip;
                if (o.Subtype != 0)
                {
                    o.Routine = NewtronFlying;
                    o.CollisionFlags = 0x87;
                    o.Anim = AnimFly;
                    AnimateAndDisplay(o);
                    return;
                }
                goto case Flare;
            case Flare:   // the flare animation ends by advancing to Flying
                CheckOwner(o);
                AnimateAndDisplay(o);   // runs even if the check just deleted it (it animates the cleared slot)
                break;
            case Flying:
                if ((o.Status & ObjStatus.Destroyed) != 0) { Delete(o); return; }
                o.CollisionFlags = 0x87;
                o.Anim = AnimFly;
                ObjectKit.Move(o);
                AnimateAndDisplay(o);
                if (((Ram.U16(Vars.CameraMaxYPos) + 0xE0) & 0xFFFF) < o.Y) Delete(o);   // fell off the level
                break;
            case Gone: Delete(o); break;
            case NewtronFlying:
                if (!o.OnScreen) { Delete(o); return; }
                ObjectKit.Move(o);
                AnimateAndDisplay(o);
                break;
        }
    }

    private void AnimateAndDisplay(SonicObject o)
    {
        ObjectKit.Animate(Ram, o, _anims);
        Display(o);
    }

    /// <summary>0x151E64: the owner turned into an explosion: remove the projectile.</summary>
    private void CheckOwner(SonicObject o)
    {
        if (Ram.U8(Owner(o)) == ExplosionId) Delete(o);
    }
}
