namespace SonicBros3.Logic;

/// <summary>
/// Crabmeat, object $1F in the hack's zones (ROM 0x151F10; Sonic 1's). Waits, then walks for 128 frames following
/// the floor (turning back at ledges and steps). Every other wait, while on screen, it fires two projectiles
/// instead of walking. Its walk and stand animations depend on the slope it is on.
/// The projectiles are the same object id in routines <see cref="MissileInit"/> and <see cref="MissileMain"/>.
/// </summary>
public sealed class Crabmeat(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x152166;
    private readonly byte[] _anims = ctx.Art["crabmeat"].Animations!;

    private const int Init = 0, Main = 2, Gone = 4, MissileInit = 6, MissileMain = 8;
    // Main's states (routine_secondary).
    private const int Waiting = 0, Walking = 2;
    // Animations: stand (+ slope 0-2), walk (+ slope), fire, projectile.
    private const int AnimStand = 0, AnimWalk = 3, AnimFire = 6, AnimMissile = 7;
    private const int WalkFrames = 0x7F, WaitFrames = 0x3B;

    /// <summary>The crab's own variables.</summary>
    private readonly struct State(SonicObject o)
    {
        /// <summary>$30: frames left in the current wait or walk.</summary>
        public int Timer { get => o.Word(0x30); set => o.SetWord(0x30, value); }
        /// <summary>$32 bit 0: snap to the floor this frame (every other frame while walking).</summary>
        public bool SnapToFloor { get => (o.Byte(0x32) & 1) != 0; }
        public void ToggleSnap() => o.SetByte(0x32, o.Byte(0x32) ^ 1);
        /// <summary>$32 bit 1: fire after this wait (every other wait).</summary>
        public bool FireNext { get => (o.Byte(0x32) & 2) != 0; }
        public void ToggleFire() => o.SetByte(0x32, o.Byte(0x32) ^ 2);
    }

    public override void Run(SonicObject o)
    {
        switch (o.Routine)
        {
            case Init: Initialise(o); break;
            case Main:
                if (o.RoutineSecondary == Waiting) Wait(o, new State(o));
                else Walk(o, new State(o));
                ObjectKit.Animate(Ram, o, _anims);
                DisplayOrMarkGone(o);
                break;
            case Gone: Delete(o); break;
            case MissileInit:
                o.Routine = MissileMain;
                SetArt(o);
                o.CollisionFlags = 0x87;
                o.WidthPixels = 8;
                o.YVel = -0x400;
                o.Anim = AnimMissile;
                goto case MissileMain;
            case MissileMain:
                ObjectKit.Animate(Ram, o, _anims);
                ObjectKit.MoveAndFall(o);
                Display(o);
                if (((Ram.U16(Vars.CameraMaxYPos) + 0xE0) & 0xFFFF) < o.Y) Delete(o);   // fell off the level
                break;
        }
    }

    private void SetArt(SonicObject o)
    {
        o.Mappings = Mappings;
        o.ArtTile = CurrentZone >= 0x15 ? 0x3BE : 0x3C2;
        o.RenderFlags = Render.LevelCoords;
        o.Priority = 3;
    }

    /// <summary>Fall until it lands; then start waiting.</summary>
    private void Initialise(SonicObject o)
    {
        o.YRadius = 0x10;
        o.XRadius = 8;
        SetArt(o);
        o.CollisionFlags = 6;
        o.WidthPixels = 0x15;
        ObjectKit.MoveAndFall(o);
        var (distance, angle) = ObjectKit.CheckFloor(Ram, Ctx.Collision, o);
        if (distance >= 0) return;
        o.Y += distance;
        o.Angle = angle;
        o.YVel = 0;
        o.Routine = Main;
    }

    /// <summary>0x151FAE.</summary>
    private void Wait(SonicObject o, State s)
    {
        s.Timer--;
        if ((short)s.Timer >= 0) return;
        if (o.OnScreen)
        {
            bool fire = s.FireNext;
            s.ToggleFire();
            if (fire) { Fire(o, s); return; }
        }
        // Turn round and walk.
        o.RoutineSecondary = Walking;
        s.Timer = WalkFrames;
        o.Anim = AnimWalk + SlopeAnim(o);
        bool wasFacingLeft = (o.Status & ObjStatus.XFlip) != 0;   // for the crab, status bit 0 = facing left
        o.Status ^= ObjStatus.XFlip;
        o.XVel = wasFacingLeft ? (short)0x80 : (short)-0x80;
    }

    /// <summary>0x151FEA: two projectiles, left and right.</summary>
    private void Fire(SonicObject o, State s)
    {
        s.Timer = WaitFrames;
        o.Anim = AnimFire;
        foreach (int dx in new[] { -0x10, 0x10 })
        {
            if (Spawn() is not { } m) return;
            m.Id = o.Id;
            m.Routine = MissileInit;
            m.X = o.X + dx;
            m.Y = o.Y;
            m.XVel = dx < 0 ? (short)-0x100 : (short)0x100;
        }
    }

    /// <summary>0x15204C.</summary>
    private void Walk(SonicObject o, State s)
    {
        s.Timer--;
        if ((short)s.Timer < 0) { Stop(o, s); return; }
        ObjectKit.Move(o);
        bool snap = s.SnapToFloor;
        s.ToggleSnap();
        if (snap)
        {
            var (distance, angle) = ObjectKit.CheckFloor(Ram, Ctx.Collision, o);
            o.Y += distance;
            o.Angle = angle;
            o.Anim = AnimWalk + SlopeAnim(o);
            return;
        }
        // Look 16 px ahead for a ledge or a step.
        bool facingLeft = (o.Status & ObjStatus.XFlip) != 0;
        int aheadX = facingLeft ? o.X - 0x10 : o.X + 0x10;
        var (ahead, _) = ObjectKit.CheckFloor(Ram, Ctx.Collision, o, aheadX & 0xFFFF);
        if (ahead < -8 || ahead >= 0x0C) Stop(o, s);
    }

    private void Stop(SonicObject o, State s)
    {
        o.RoutineSecondary = Waiting;
        s.Timer = WaitFrames;
        o.XVel = 0;
        o.Anim = AnimStand + SlopeAnim(o);
    }

    /// <summary>0x1520BC: 0 flat, 1 or 2 on a slope up or down relative to its facing.</summary>
    private static int SlopeAnim(SonicObject o)
    {
        int angle = o.Angle;
        bool left = (o.Status & ObjStatus.XFlip) != 0;
        if ((sbyte)angle >= 0)
        {
            if (angle < 6) return 0;
            return left ? 1 : 2;
        }
        if (angle > 0xFA) return 0;
        return left ? 2 : 1;
    }
}
