namespace SonicBros3.Logic;

/// <summary>
/// Buzzer, object $4B in Sonic 2's zones (ROM 0x312D0; in the hack's zones $4B is the giant ring). The bee flies at 1 px
/// a frame for 256 frames, pauses 30 frames and turns round. When a player (Sonic and Tails on alternate frames) is
/// $28-$30 px in front of it, it stops for 50 frames and fires a projectile 30 frames in. Its exhaust flame is a second
/// object (routine <see cref="Flame"/>), shown while the bee moves; the projectile is a third (<see cref="Projectile"/>).
/// </summary>
public sealed class Buzzer(ObjectContext ctx) : GameObject(ctx)
{
    public const int Id = 0x4B;
    public const uint Mappings = 0x3154E;
    private readonly byte[] _anims = ctx.Art["buzzer"].Animations!;

    private const int Init = 0, Main = 2, Flame = 4, Projectile = 6;
    private const int Flying = 0, Shooting = 2;
    private const int AnimFlame = 1, AnimProjectile = 2, AnimShooting = 3;
    private const int Art = 0x3D2;
    private const int FlyFrames = 0x100, PauseFrames = 0x1E, TurnAt = 0x0F, ShootFrames = 0x32, FireAt = 0x14;

    /// <summary>The Buzzer's own variables.</summary>
    private readonly struct State(SonicObject o)
    {
        /// <summary>$2A: the bee's slot, for its flame (long).</summary>
        public int Parent { get => (int)(o.Long(0x2A) & 0xFFFF); set => o.SetLong(0x2A, 0xFFFF0000u | (uint)value); }
        /// <summary>$2E: frames left to fly; $30: the pause (negative while flying; the flame shows then).</summary>
        public int FlyTimer { get => o.Word(0x2E); set => o.SetWord(0x2E, value); }
        public int PauseTimer { get => o.Word(0x30); set => o.SetWord(0x30, value); }
        /// <summary>$32: has shot (no more until it turns).</summary>
        public bool HasShot { get => o.Byte(0x32) != 0; set => o.SetByte(0x32, value ? 0xFF : 0); }
        /// <summary>$34: frames left of the shooting pause.</summary>
        public int ShootTimer { get => o.Word(0x34); set => o.SetWord(0x34, value); }
    }

    public override void Run(SonicObject o)
    {
        var s = new State(o);
        switch (o.Routine)
        {
            case Init: Initialise(o, s); break;
            case Main:
                if (o.RoutineSecondary == Flying) Fly(o, s);
                else Shoot(o, s);
                ObjectKit.Animate(Ram, o, _anims);
                DisplayOrMarkGone(o);
                break;
            case Flame: UpdateFlame(o, s); break;
            case Projectile:   // 0x312E6
                ObjectKit.Move(o);
                ObjectKit.Animate(Ram, o, _anims);
                DisplayOrMarkGone(o);
                break;
        }
    }

    /// <summary>0x31330: the bee and its flame.</summary>
    private void Initialise(SonicObject o, State s)
    {
        o.Mappings = Mappings;
        o.ArtTile = Art;   // (Adjust2PArtPointer: 1-player, unchanged)
        o.RenderFlags |= Render.LevelCoords;
        o.CollisionFlags = 0x0A;
        o.Priority = 4;
        o.WidthPixels = 0x10;
        o.YRadius = 0x10;
        o.XRadius = 0x18;
        o.Priority = 3;
        o.Routine = Main;
        int slot = ObjectKit.FindFreeSlotAfter(Ram, o);
        if (slot < 0) return;   // (and no speed is set)
        var flame = new SonicObject(Ram, slot);
        flame.Id = Id;
        flame.Routine = Flame;
        flame.Mappings = Mappings;
        flame.ArtTile = Art;
        flame.Priority = 4;
        flame.WidthPixels = 0x10;
        flame.Status = o.Status;
        flame.RenderFlags = o.RenderFlags;
        flame.Anim = AnimFlame;
        new State(flame).Parent = o.Address;
        flame.X = o.X;
        flame.Y = o.Y;
        s.FlyTimer = FlyFrames;
        o.XVel = -0x100;
        if ((o.RenderFlags & Render.XFlip) != 0) o.XVel = (short)-o.XVel;
    }

    /// <summary>0x313FA: fly, pause, turn; look for a player.</summary>
    private void Fly(SonicObject o, State s)
    {
        LookForPlayer(o, s);
        s.PauseTimer--;
        int pause = s.PauseTimer;
        if (pause == TurnAt)
        {
            // 0x31420: turn round.
            s.HasShot = false;
            o.XVel = (short)-o.XVel;
            o.RenderFlags ^= Render.XFlip;
            o.Status ^= ObjStatus.XFlip;
            s.FlyTimer = FlyFrames;
            return;
        }
        if ((short)pause >= 0) return;   // pausing
        s.FlyTimer--;
        if ((short)s.FlyTimer > 0) { ObjectKit.Move(o); return; }
        s.PauseTimer = PauseFrames;
    }

    /// <summary>0x3143C: a player (Sonic or Tails, on alternate frames) $28-$30 px in front: start shooting.</summary>
    private void LookForPlayer(SonicObject o, State s)
    {
        if (s.HasShot) return;
        var player = (Ram.U8(Vars.VintRuncountLow) & 1) != 0 ? Tails : Sonic;
        int dx = (short)(o.X - player.X);
        int distance = Math.Abs(dx);
        if (distance < 0x28 || distance > 0x30) return;
        bool facingRight = (o.RenderFlags & Render.XFlip) != 0;
        bool playerLeft = dx >= 0;
        if (playerLeft == facingRight) return;   // behind it
        s.HasShot = true;
        o.RoutineSecondary = Shooting;
        o.Anim = AnimShooting;
        s.ShootTimer = ShootFrames;
    }

    /// <summary>0x3149A: the shooting pause; the projectile 30 frames in.</summary>
    private void Shoot(SonicObject o, State s)
    {
        int t = s.ShootTimer - 1;
        if (t < 0) { o.RoutineSecondary = Flying; return; }
        s.ShootTimer = t;
        if (t != FireAt) return;
        int slot = ObjectKit.FindFreeSlotAfter(Ram, o);
        if (slot < 0)
        {
            // SingleObjLoad2 failed and left a1 at $D000: the original still adds the offset to that slot's x.
            Ram.W16(Vars.TailsTails + 8, Ram.U16(Vars.TailsTails + 8) + FireAt);
            return;
        }
        var shot = new SonicObject(Ram, slot);
        shot.Id = Id;
        shot.Routine = Projectile;
        shot.Mappings = Mappings;
        shot.ArtTile = Art;
        shot.Priority = 4;
        shot.CollisionFlags = 0x98;   // hurts
        shot.WidthPixels = 0x10;
        shot.Status = o.Status;
        shot.RenderFlags = o.RenderFlags;
        shot.Anim = AnimProjectile;
        shot.X = o.X;
        shot.Y = o.Y + 0x18;
        int dx = 0x0D;
        shot.YVel = 0x180;
        shot.XVel = -0x180;
        if ((shot.RenderFlags & Render.XFlip) != 0)
        {
            shot.XVel = (short)-shot.XVel;
            dx = -dx;
        }
        shot.X += dx;
    }

    /// <summary>0x312F8: the flame follows the bee, shown while it flies; it goes with the bee.</summary>
    private void UpdateFlame(SonicObject o, State s)
    {
        var bee = new SonicObject(Ram, s.Parent);
        if (bee.Id == 0) { Delete(o); return; }
        if ((short)new State(bee).PauseTimer >= 0) return;   // the bee pauses: no flame (not displayed)
        o.X = bee.X;
        o.Y = bee.Y;
        o.Status = bee.Status;
        o.RenderFlags = bee.RenderFlags;
        ObjectKit.Animate(Ram, o, _anims);
        DisplayOrMarkGone(o);
    }
}
