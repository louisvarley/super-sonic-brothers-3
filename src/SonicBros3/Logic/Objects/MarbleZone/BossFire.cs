namespace SonicBros3.Logic;

/// <summary>
/// The Marble Zone boss's fire, object $74 in the hack's zones (ROM 0x156342).
///
/// Subtype 1 (dropped by the boss): it falls, harmless for 30 frames, then hurts; it lands and splits into two fires
/// running along the floor left and right. Each runner leaves a still fire (subtype 0) every 16 pixels, and drops off
/// ledges up to 3 times. Still fires burn for $67 frames, then flicker out. Fires are deleted below y $2E8.
/// </summary>
public sealed class BossFire(ObjectContext ctx) : GameObject(ctx)
{
    public const int Id = 0x74;
    private readonly byte[] _anims = ctx.Art["mz-lava-ball"].Animations!;
    private const int Init = 0, Moving = 2, Still = 4;
    // A moving fire's states (routine_secondary).
    private const int Falling = 0, Splitting = 2, Running = 4, DroppingOff = 6;
    private const int RunSpeed = 0xA0, StopX = 0x1940, FloorLimit = 0x2E8;
    private const int SfxLavaBall = 0xAE;

    /// <summary>The fire's own variables.</summary>
    private readonly struct State(SonicObject o)
    {
        /// <summary>$29: a frame timer (harmless time, burning time) or the ledges left.</summary>
        public int Timer { get => o.Byte(0x29); set => o.SetByte(0x29, value); }
        /// <summary>$30: last frame's x; $32: where the last still fire was left.</summary>
        public int LastX { get => o.Word(0x30); set => o.SetWord(0x30, value); }
        public int LastFireX { get => o.Word(0x32); set => o.SetWord(0x32, value); }
        /// <summary>$38: the floor's y.</summary>
        public int FloorY { get => o.Word(0x38); set => o.SetWord(0x38, value); }
    }

    public override void Run(SonicObject o)
    {
        var s = new State(o);
        switch (o.Routine)
        {
            case Init:
                if (Initialise(o, s)) Move(o, s);
                else Burn(o, s);
                break;
            case Moving: Move(o, s); break;
            case Still: Burn(o, s); break;
            default: Delete(o); break;
        }
        Display(o);   // 0x156350 (DisplaySprite skips an emptied slot)
    }

    /// <summary>0x15635E: true for a dropped fire, false for a still one.</summary>
    private bool Initialise(SonicObject o, State s)
    {
        o.YRadius = 8;
        o.XRadius = 8;
        o.Mappings = GrassFire.Mappings;
        o.ArtTile = 0x345;
        o.RenderFlags = Render.LevelCoords;
        o.Priority = 5;
        s.FloorY = o.Y;
        o.WidthPixels = 8;
        o.Routine = Moving;
        if (o.Subtype == 0)
        {
            o.CollisionFlags = 0x8B;
            o.Routine = Still;
            return false;
        }
        s.Timer = 0x1E;   // harmless while falling away from the ship
        Ctx.Sound.PlaySound(SfxLavaBall);
        return true;
    }

    /// <summary>0x1563B8.</summary>
    private void Move(SonicObject o, State s)
    {
        switch (o.RoutineSecondary)
        {
            case Falling: Fall(o, s); break;
            case Splitting: Split(o, s); break;
            case Running: RunAlong(o, s); break;
            case DroppingOff: DropOff(o, s); break;
        }
        ObjectKit.Move(o);
        ObjectKit.Animate(Ram, o, _anims);   // also on a cleared slot (0x156524 returns here)
        if (o.Y > FloorLimit) Delete(o);
    }

    /// <summary>0x1563F0: harmless for 30 frames, then fall to the floor.</summary>
    private void Fall(SonicObject o, State s)
    {
        o.Status |= 2;
        s.Timer--;
        if ((sbyte)s.Timer >= 0) return;
        o.CollisionFlags = 0x8B;
        o.Subtype = 0;
        o.YVel += 0x18;
        o.Status &= ~2;
        if ((short)ObjectKit.CheckFloorDist(Ram, Ctx.Collision, o) < 0) o.RoutineSecondary = Splitting;
    }

    /// <summary>0x156422: split into a right runner (this one) and a left runner (a copy).</summary>
    private void Split(SonicObject o, State s)
    {
        o.Y -= 2;
        o.ArtTile |= 0x8000;   // in front
        o.XVel = RunSpeed;
        o.YVel = 0;
        s.LastX = o.X;
        s.FloorY = o.Y;
        s.Timer = 3;   // ledges it may drop off
        int slot = ObjectKit.FindFreeSlotAfter(Ram, o);
        if (slot >= 0)
        {
            Ram.Bytes.AsSpan(o.Address, Objects.SlotSize).CopyTo(Ram.Bytes.AsSpan(slot));
            var copy = new SonicObject(Ram, slot);
            copy.XVel = (short)-copy.XVel;
            copy.RoutineSecondary += 2;
        }
        o.RoutineSecondary = Running;
    }

    /// <summary>0x156490: run along the floor, leaving a still fire every 16 pixels.</summary>
    private void RunAlong(SonicObject o, State s)
    {
        if ((short)ObjectKit.CheckFloorDist(Ram, Ctx.Collision, o) >= 0) { o.RoutineSecondary = DroppingOff; return; }
        int x = o.X;
        if ((short)x > StopX) { o.Routine += 2; return; }
        int last = s.LastX;
        bool crossed16 = x != last && (x & 0x10) != (last & 0x10);
        if (crossed16)
        {
            int slot = ObjectKit.FindFreeSlotAfter(Ram, o);   // 0x156470
            if (slot >= 0)
            {
                var fire = new SonicObject(Ram, slot);
                fire.X = o.X;
                fire.Y = o.Y;
                fire.Id = Id;
                fire.SetWord(0x28, 0x67);   // subtype 0 (still), burning for $67 frames
            }
            s.LastFireX = o.X;
        }
        s.LastX = o.X;
    }

    /// <summary>0x1564D4: off a ledge: fall; on landing go back to the last fire; deleted the third time.</summary>
    private void DropOff(SonicObject o, State s)
    {
        o.Status &= ~2;
        o.YVel += 0x24;
        int dx = Math.Abs((short)(o.X - s.LastFireX));
        if (dx == 0x12) o.ArtTile &= 0x7FFF;   // behind
        if ((short)ObjectKit.CheckFloorDist(Ram, Ctx.Collision, o) >= 0) return;
        s.Timer--;
        if (s.Timer == 0) { Delete(o); return; }
        o.YVel = 0;
        o.X = s.LastFireX;
        o.Y = s.FloorY;
        o.ArtTile |= 0x8000;
        o.RoutineSecondary = Running;
    }

    /// <summary>0x15652A: a still fire; after its time it flickers out (animation 1 ends by deleting it).</summary>
    private void Burn(SonicObject o, State s)
    {
        o.ArtTile |= 0x8000;
        s.Timer--;
        if (s.Timer == 0)
        {
            o.Anim = 1;
            o.Y -= 4;
            o.CollisionFlags = 0;
        }
        ObjectKit.Animate(Ram, o, _anims);
    }
}
