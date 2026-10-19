namespace SonicBros3.Logic;

/// <summary>
/// The rings a hurt player drops, object $37 (ROM 0x14F78).
///
/// The first frame turns the object into the first of up to 32 rings, spreads them in a fan (speeds from the sine
/// table) and zeroes the player's ring count. The rings bounce (the floor is checked every 8 frames, staggered by
/// slot), can be collected again while the shared timer <see cref="Vars.RingSpillAnimCounter"/> runs, and vanish
/// when it reaches 0.
/// </summary>
public sealed class LostRings(ObjectContext ctx, Objects objects) : GameObject(ctx)
{
    public const uint Mappings = 0x152C4;
    private readonly byte[] _anims = ctx.Art["lost-rings"].Animations!;

    private const int Scatter = 0, Bouncing = 2, Collected = 4, Sparkle = 6, Gone = 8;
    private const int MaxRings = 32, Gravity = 0x18;
    private const int SfxRingLoss = 0xC6;

    /// <summary>$3F: set when the rings are Tails'.</summary>
    private static bool IsTails(SonicObject o) => o.Byte(0x3F) != 0;

    public override void Run(SonicObject o)
    {
        switch (o.Routine)
        {
            case Scatter: ScatterRings(o); Bounce(o); break;
            case Bouncing: Bounce(o); break;
            case Collected:
                o.Routine = Sparkle;
                o.CollisionFlags = 0;
                o.Priority = 1;
                TouchResponse.CollectRingFor(Ram, Ctx.Sound, tails: IsTails(o));   // CollectRing (0x14EC2)
                goto case Sparkle;
            case Sparkle:
                ObjectKit.Animate(Ram, o, _anims);
                Display(o);
                break;
            case Gone: Delete(o); break;
        }
    }

    /// <summary>0x14F90: one ring per ring held (up to 32), fanned out in pairs mirrored left and right.</summary>
    private void ScatterRings(SonicObject o)
    {
        bool tails = IsTails(o);
        int left = Math.Min((int)Ram.U16(tails ? Vars.RingCountP2 : Vars.RingCount), MaxRings) - 1;
        // The fan: d4's low byte is the angle (16 steps of $10), its high byte the speed shift; each pair's
        // speed halves after a full turn. d4 and the x speed flip sign for every other ring.
        int d4 = 0x288, xSpeed = 0, ySpeed = 0;
        var ring = o;
        while (true)
        {
            ring.Id = 0x37;
            ring.Routine += 2;
            ring.YRadius = 8;
            ring.XRadius = 8;
            ring.X = o.X;
            ring.Y = o.Y;
            ring.Mappings = Mappings;
            ring.ArtTile = 0x26BC;
            ring.RenderFlags = Render.OnScreen | Render.LevelCoords;
            ring.Priority = 3;
            ring.CollisionFlags = 0x47;
            ring.WidthPixels = 8;
            Ram.W8(Vars.RingSpillAnimCounter, 0xFF);
            if ((short)d4 >= 0)
            {
                var (sin, cos) = MdMath.CalcSine(d4 & 0xFF);
                int shift = (d4 & 0xFFFF) >> 8;
                xSpeed = (sin << shift) & 0xFFFF;
                ySpeed = (cos << shift) & 0xFFFF;
                int angle = (d4 & 0xFF) + 0x10;
                d4 = (d4 & 0xFF00) | (angle & 0xFF);
                if (angle > 0xFF)
                {
                    int next = (d4 & 0xFFFF) - 0x80;
                    d4 = next < 0 ? 0x288 : next;
                }
            }
            ring.XVel = (short)xSpeed;
            ring.YVel = (short)ySpeed;
            xSpeed = (-xSpeed) & 0xFFFF;
            d4 = (-d4) & 0xFFFF;
            if (left-- == 0) break;
            int slot = Objects.FindFreeSlot(Ram);
            if (slot < 0) break;
            ring = new SonicObject(Ram, slot);
        }
        Ctx.Sound.PlaySoundSecondary(SfxRingLoss);
        Ram.W16(tails ? Vars.RingCountP2 : Vars.RingCount, 0);
        Ram.W8(tails ? Vars.UpdateHudRingsP2 : Vars.UpdateHudRings, 0x80);
        Ram.W8(tails ? Vars.ExtraLifeFlagsP2 : Vars.ExtraLifeFlags, 0);
    }

    /// <summary>0x15076.</summary>
    private void Bounce(SonicObject o)
    {
        o.MappingFrame = Ram.U8(Vars.RingSpillAnimFrame);
        ObjectKit.Move(o);
        o.YVel += Gravity;
        bool checkFloor = ((Ram.U8(Vars.VintRuncountLow) + objects.SlotCounter) & 7) == 0;
        if (o.YVel >= 0 && checkFloor)
        {
            if (o.OnScreen)
            {
                int feet = o.Y + o.YRadius;
                Ram.W8(Vars.PrimaryAngle, 0);
                int distance = (short)Ctx.Collision.RingFindFloor(feet, o.X, 0x10, 0x0C, 0, Vars.PrimaryAngle);
                if (distance < 0)
                {
                    // Land: bounce back up at 3/4 speed.
                    o.Y += distance;
                    o.YVel = (short)(o.YVel - (o.YVel >> 2));
                    o.YVel = (short)-o.YVel;
                }
            }
            else if (TwoPlayerMode) { Delete(o); return; }
        }
        if (Ram.U8(Vars.RingSpillAnimCounter) == 0) { Delete(o); return; }
        int levelBottom = (Ram.U16(Vars.CameraMaxYPos) + 0xE0) & 0xFFFF;
        if (levelBottom < o.Y) { Delete(o); return; }
        Display(o);
    }
}
