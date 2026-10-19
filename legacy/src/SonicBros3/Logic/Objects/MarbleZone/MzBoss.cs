namespace SonicBros3.Logic;

/// <summary>
/// The Marble Zone boss, object $73 in the hack's zones (ROM 0x155EC2; Sonic 1's Eggman ship over the lava). The
/// placed object is the ship; it spawns its face, its flame and the part hanging under it (Green Hill's anchor
/// mappings), the same id in other routines.
///
/// The ship flies left to $1910, then sweeps between $1830 and $1910, raising lava balls from below at random
/// intervals. At each end it dips to y $22C and drops a fire (<see cref="BossFire"/>) that runs along the floor. It
/// takes 8 hits. Defeated: 1000 points, explosions; it drops, rises, the level music and the animal art return, and
/// it flies off to the right while the camera's right limit opens to $1960.
/// </summary>
public sealed class MzBoss(ObjectContext ctx) : GameObject(ctx)
{
    public const int Id = 0x73;
    private readonly byte[] _anims = ctx.Art["ghz-boss"].Animations!;

    private const int Init = 0, Ship = 2, FacePart = 4, FlamePart = 6, AnchorPart = 8;
    // The ship's phases (routine_secondary).
    private const int Approach = 0, Fighting = 2, Exploding = 4, DropAndRise = 6, FlyAway = 8;
    private const int RightEnd = 0x1910, LeftEnd = 0x1830, LavaY = 0x22C, FinalCameraMaxX = 0x1960;
    private const int Hits = 8, FlashFrames = 0x28;
    private const int LavaBallId = 0x14, FireId = 0x74;
    private const int SfxBossHit = 0xAC;

    /// <summary>Per part: routine, animation, priority (0x155EDA).</summary>
    private static readonly (int Routine, int Anim, int Priority)[] Parts =
        [(Ship, 0, 4), (FacePart, 1, 4), (FlamePart, 7, 4), (AnchorPart, 0, 3)];

    /// <summary>The ship, $34: frames until the next lava ball. (The parts keep the ship's slot there.)</summary>
    private const int LavaTimer = 0x34;
    /// <summary>The ship's subtype during the fight: bit 1 set while dipping at an end.</summary>
    private const int Dipping = 2;

    public override void Run(SonicObject o)
    {
        switch (o.Routine)
        {
            case Init: CreateParts(o); UpdateShip(o, new BossState(o)); break;
            case Ship: UpdateShip(o, new BossState(o)); break;
            case FacePart: UpdateFace(o); break;
            case FlamePart: UpdateFlame(o); break;
            case AnchorPart: UpdateAnchor(o); break;
        }
    }

    /// <summary>0x155EE6.</summary>
    private void CreateParts(SonicObject o)
    {
        var s = new BossState(o);
        s.BaseX = o.X;
        s.BaseY = o.Y;
        o.CollisionFlags = 0x0F;
        o.CollisionProperty = Hits;
        var part = o;
        for (int i = 0; i < Parts.Length; i++)
        {
            if (i > 0)
            {
                int slot = ObjectKit.FindFreeSlotAfter(Ram, o);
                if (slot < 0) break;
                part = new SonicObject(Ram, slot);
                part.Id = Id;
                part.X = o.X;
                part.Y = o.Y;
            }
            o.Status &= ~ObjStatus.XFlip;
            part.RoutineSecondary = 0;
            part.Routine = Parts[i].Routine;
            part.Anim = Parts[i].Anim;
            part.Priority = Parts[i].Priority;
            part.Mappings = GhzBoss.Mappings;
            part.ArtTile = 0x3B0;
            part.RenderFlags = Render.LevelCoords;
            part.WidthPixels = 0x20;
            new BossState(part).Parent = o.Address;
        }
    }

    /// <summary>0x155F58.</summary>
    private void UpdateShip(SonicObject o, BossState s)
    {
        switch (o.RoutineSecondary)
        {
            case Approach: FlyIn(o, s); break;
            case Fighting:
                if ((o.Subtype & Dipping) == 0) Sweep(o, s);
                else Dip(o, s);
                o.Subtype &= 6;
                Hover(o, s);
                break;
            case Exploding: Explode(o, s); break;
            case DropAndRise: Recover(o, s); break;
            case FlyAway: Escape(o, s); break;
        }
        ObjectKit.Animate(Ram, o, _anims);   // also on the cleared slot after the escape (0x156240 returns here)
        o.RenderFlags = (o.RenderFlags & ~3) | (o.Status & 3);
        Display(o);
    }

    /// <summary>0x155F92: fly left to the right end of the arena, bobbing.</summary>
    private void FlyIn(SonicObject o, BossState s)
    {
        int angle = s.HoverAngle;
        s.HoverAngle = angle + 2;
        o.YVel = (short)(MdMath.CalcSine(angle).Sin >> 2);
        o.XVel = -0x100;
        s.Move();
        if (s.BaseX == RightEnd)
        {
            o.RoutineSecondary = Fighting;
            o.Subtype = 0;
            o.XVel = 0;
            o.YVel = 0;
        }
        o.SetByte(LavaTimer, (int)(ObjectKit.RandomNumber(Ram) & 0xFF));
        Hover(o, s);
    }

    /// <summary>0x155FCE: the position from the base; react to hits (flash; 100 points and explosions when beaten).</summary>
    private void Hover(SonicObject o, BossState s)
    {
        o.Y = s.BaseY;
        o.X = s.BaseX;
        if (o.RoutineSecondary >= Exploding) return;
        if ((o.Status & ObjStatus.Destroyed) != 0)
        {
            Ctx.Touch.AddPoints(100);
            o.RoutineSecondary = Exploding;
            s.Timer = 0xB4;
            o.XVel = 0;
            return;
        }
        if (o.CollisionFlags != 0) return;
        if (s.FlashTimer == 0)
        {
            s.FlashTimer = FlashFrames;
            Ctx.Sound.PlaySound(SfxBossHit);
        }
        int colour = Vars.NormalPaletteLine2 + 2;
        Ram.W16(colour, Ram.U16(colour) == 0 ? 0xEEE : 0);
        s.FlashTimer--;
        if (s.FlashTimer == 0) o.CollisionFlags = 0x0F;
    }

    /// <summary>0x15605C: sweep across the lava, raising lava balls; at the ends turn and climb or drop.</summary>
    private void Sweep(SonicObject o, BossState s)
    {
        bool facingRight = (o.Status & ObjStatus.XFlip) != 0;
        if (o.XVel == 0)
        {
            // Settle at the lava's height first.
            int y = s.BaseY;
            if (y != LavaY)
            {
                o.YVel = (short)(y < LavaY ? 0x40 : -0x40);
                s.Move();
                return;
            }
            o.XVel = facingRight ? (short)0x200 : (short)-0x200;
            o.YVel = 0x100;
        }
        if (s.FlashTimer < 0x18)
        {
            s.Move();
            o.YVel -= 4;
        }
        int timer = o.Byte(LavaTimer) - 1;
        o.SetByte(LavaTimer, timer);
        if (timer < 0)
        {
            if (Spawn() is { } ball)
            {
                ball.Id = LavaBallId;
                ball.Y = 0x2E8;
                uint r = ObjectKit.RandomNumber(Ram);
                ball.X = (int)((r & 0xFFFF) % 0x50) + 0x1878;
                ball.SetWord(0x28, 0xFF);   // subtype 0 (leaps), $29 set: drawn behind
            }
            o.SetByte(LavaTimer, (int)(ObjectKit.RandomNumber(Ram) & 0x1F) + 0x40);
        }
        if (facingRight)
        {
            if ((short)s.BaseX < RightEnd) return;
            s.BaseX = RightEnd;
        }
        else
        {
            if ((short)s.BaseX > LeftEnd) return;
            s.BaseX = LeftEnd;
        }
        // At an end: climb (or drop) to the lava's height and dip.
        o.XVel = 0;
        o.YVel = -0x180;
        if (s.BaseY < LavaY) o.YVel = 0x180;
        o.Subtype += 2;
    }

    /// <summary>0x15612E: at an end, settle at y $22C, turn round and drop a fire; wait 80 frames.</summary>
    private void Dip(SonicObject o, BossState s)
    {
        s.Move();
        if ((short)(s.BaseY - LavaY) > 0) return;
        if (o.YVel != 0)
        {
            o.YVel = 0;
            s.Timer = 0x50;
            o.Status ^= ObjStatus.XFlip;
            if (Spawn() is { } fire)
            {
                fire.X = s.BaseX;
                fire.Y = s.BaseY + 0x18;
                fire.Id = FireId;
                fire.Subtype = 1;   // dropped
            }
        }
        s.Timer--;
        if (s.Timer == 0) o.Subtype += 2;
    }

    /// <summary>0x156186: explode for 180 frames.</summary>
    private void Explode(SonicObject o, BossState s)
    {
        s.Timer--;
        if ((short)s.Timer >= 0) { GhzBoss.Explosions(Ram, o); return; }
        o.Status |= ObjStatus.XFlip;
        o.Status &= ~ObjStatus.Destroyed;
        o.XVel = 0;
        o.RoutineSecondary = DropAndRise;
        s.Timer = 0xFFDA;   // -$26
        if (Ram.U8(Vars.BossDefeatedFlag) == 0)
        {
            Ram.W8(Vars.BossDefeatedFlag, 1);
            o.YVel = 0;
        }
    }

    /// <summary>0x1561BC: drop (to y $270 at most), rise; the level music and the animal art come back.</summary>
    private void Recover(SonicObject o, BossState s)
    {
        s.Timer++;
        int t = s.Timer;
        if (t == 0) { o.YVel = 0; s.Timer = 0; }
        else if ((short)t < 0)
        {
            if (s.BaseY < 0x270) o.YVel += 0x18;
            else { o.YVel = 0; s.Timer = 0; }
        }
        else if (t < 0x30) o.YVel -= 8;
        else if (t == 0x30)
        {
            o.YVel = 0;
            Ctx.Sound.PlayMusic(Ram.U16(Vars.LevelMusic));   // 0x3313C -> 0x1209A
            GhzBoss.LoadAnimalArt(Ctx);                      // 0x33142 -> 0x120A2
        }
        else if (t >= 0x38) o.RoutineSecondary = FlyAway;
        s.Move();
        Hover(o, s);
    }

    /// <summary>0x156214: fly off right, opening the camera; deleted once off screen.</summary>
    private void Escape(SonicObject o, BossState s)
    {
        o.XVel = 0x500;
        o.YVel = -0x40;
        if (Ram.U16(Vars.CameraMaxXPos) < FinalCameraMaxX)
        {
            Ram.W16(Vars.CameraMaxXPos, Ram.U16(Vars.CameraMaxXPos) + 2);
            Ram.W16(Vars.TailsMaxXPos, Ram.U16(Vars.TailsMaxXPos) + 2);
        }
        else if (!o.OnScreen) { Delete(o); return; }
        s.Move();
        Hover(o, s);
    }

    private SonicObject ShipOf(SonicObject part) => new(Ram, new BossState(part).Parent);

    /// <summary>0x156246: the face. Laughs when a player is hurt, winces when hit, shows the defeated face afterwards.</summary>
    private void UpdateFace(SonicObject o)
    {
        var ship = ShipOf(o);
        int phase = ship.RoutineSecondary;
        const int AnimNormal = 1, AnimLaugh = 4, AnimHit = 5, AnimDefeated = 0x0A, AnimFlyingAway = 6;
        int anim = AnimNormal;
        bool react;
        if (phase == Fighting)
        {
            bool dippingStill = (ship.Subtype & Dipping) != 0 && ship.YVel == 0;
            react = !dippingStill;
            if (dippingStill) anim = AnimLaugh;
        }
        else if (phase == Approach) react = true;
        else { anim = AnimDefeated; react = false; }
        if (react)
        {
            if (ship.CollisionFlags == 0) anim = AnimHit;
            else if (Sonic.Routine == 4 || Tails.Routine == 4) anim = AnimLaugh;
        }
        o.Anim = anim;
        if (phase == FlyAway)
        {
            o.Anim = AnimFlyingAway;
            if (!o.OnScreen) { Delete(o); return; }
        }
        AnimateAndFollow(o, ship);
    }

    /// <summary>0x1562A8: the exhaust flame.</summary>
    private void UpdateFlame(SonicObject o)
    {
        var ship = ShipOf(o);
        const int AnimIdle = 7, AnimMoving = 8, AnimFlyingAway = 0x0B;
        o.Anim = AnimIdle;
        if ((sbyte)ship.RoutineSecondary >= FlyAway)
        {
            o.Anim = AnimFlyingAway;
            if (!o.OnScreen) { Delete(o); return; }
        }
        else if (ship.XVel != 0) o.Anim = AnimMoving;
        AnimateAndFollow(o, ship);
    }

    /// <summary>0x156314: the part hanging under the ship (Green Hill's anchor mappings, frame 4).</summary>
    private void UpdateAnchor(SonicObject o)
    {
        var ship = ShipOf(o);
        if (ship.RoutineSecondary == FlyAway && !o.OnScreen) { Delete(o); return; }
        o.Mappings = 0x152D98;
        o.ArtTile = 0x46C;
        o.MappingFrame = 4;
        Follow(o, ship);
    }

    private void AnimateAndFollow(SonicObject o, SonicObject ship)
    {
        ObjectKit.Animate(Ram, o, _anims);
        Follow(o, ship);
    }

    /// <summary>0x1562E8.</summary>
    private void Follow(SonicObject o, SonicObject ship)
    {
        o.X = ship.X;
        o.Y = ship.Y;
        o.Status = ship.Status;
        o.RenderFlags = (o.RenderFlags & ~3) | (o.Status & 3);
        Display(o);
    }
}
