namespace SonicBros3.Logic;

/// <summary>
/// The variables Sonic 1's bosses keep in their slots (the Green Hill and Marble Zone Eggman ships and their parts).
/// </summary>
public readonly struct BossState(SonicObject o)
{
    /// <summary>$30: the ship's x without the hover, as 16.16 (the high word is the pixel).</summary>
    public int BaseX { get => o.Word(0x30); set => o.SetWord(0x30, value); }
    /// <summary>$38: the ship's y without the hover, as 16.16.</summary>
    public int BaseY { get => o.Word(0x38); set => o.SetWord(0x38, value); }
    /// <summary>$34: the parent ship's slot (long, $FFFFxxxx), for the parts.</summary>
    public int Parent { get => (int)(o.Long(0x34) & 0xFFFF); set => o.SetLong(0x34, 0xFFFF0000u | (uint)value); }
    /// <summary>$3C: a frame timer for the current phase.</summary>
    public int Timer { get => o.Word(0x3C); set => o.SetWord(0x3C, value); }
    /// <summary>$3E: frames left of the white flash after a hit.</summary>
    public int FlashTimer { get => o.Byte(0x3E); set => o.SetByte(0x3E, value); }
    /// <summary>$3F: the hover's angle.</summary>
    public int HoverAngle { get => o.Byte(0x3F); set => o.SetByte(0x3F, value); }

    /// <summary>BossMove (0x152EBA): move the base position by the velocity.</summary>
    public void Move()
    {
        o.SetLong(0x30, (uint)((int)o.Long(0x30) + (o.XVel << 8)));
        o.SetLong(0x38, (uint)((int)o.Long(0x38) + (o.YVel << 8)));
    }
}

/// <summary>
/// The Green Hill boss, object $3D in the hack's zones (ROM 0x152688; Sonic 1's Eggman ship). The placed object is
/// the ship; it spawns its face and its exhaust flame (the same id, other routines).
///
/// The ship descends, moves left and lowers the wrecking ball (<see cref="WreckingBall"/>), then swings back and
/// forth. It takes 8 hits, flashing white for 32 frames after each. Defeated: 1000 points, explosions for 180
/// frames, then it drops and rises, the level music and the animal art come back, and it flies off to the right
/// while the camera's right limit opens to $2AC0.
/// </summary>
public sealed class GhzBoss(ObjectContext ctx) : GameObject(ctx)
{
    public const int Id = 0x3D;
    public const uint Mappings = 0x152A10;
    private readonly byte[] _anims = ctx.Art["ghz-boss"].Animations!;

    private const int Init = 0, Ship = 2, FacePart = 4, FlamePart = 6;
    // The ship's phases (routine_secondary).
    private const int Descend = 0, MoveLeft = 2, Pause = 4, Swing = 6, Exploding = 8, DropAndRise = 0x0A, FlyAway = 0x0C;
    private const int WreckingBallId = 0x48;
    private const int ArenaX = 0x2A00, HoverY = 0x338, FinalCameraMaxX = 0x2AC0;
    private const int Hits = 8, FlashFrames = 0x20;
    private const int SfxBossHit = 0xAC;

    /// <summary>Per part: routine, animation (0x15269E).</summary>
    private static readonly (int Routine, int Anim)[] Parts = [(Ship, 0), (FacePart, 1), (FlamePart, 7)];

    public override void Run(SonicObject o)
    {
        switch (o.Routine)
        {
            case Init: CreateParts(o); UpdateShip(o, new BossState(o)); break;
            case Ship: UpdateShip(o, new BossState(o)); break;
            case FacePart: UpdateFace(o); break;
            case FlamePart: UpdateFlame(o); break;
        }
    }

    private void CreateParts(SonicObject o)
    {
        var part = o;
        for (int i = 0; i < Parts.Length; i++)
        {
            if (i > 0)
            {
                int slot = ObjectKit.FindFreeSlotAfter(Ram, o);
                if (slot < 0) break;
                part = new SonicObject(Ram, slot);
            }
            part.Routine = Parts[i].Routine;
            part.Id = Id;
            part.X = o.X;
            part.Y = o.Y;
            part.Mappings = Mappings;
            part.ArtTile = 0x3B0;
            part.RenderFlags = Render.LevelCoords;
            part.WidthPixels = 0x20;
            part.Priority = 3;
            part.Anim = Parts[i].Anim;
            new BossState(part).Parent = o.Address;
        }
        var s = new BossState(o);
        s.BaseX = o.X;
        s.BaseY = o.Y;
        o.CollisionFlags = 0x0F;
        o.CollisionProperty = Hits;
    }

    /// <summary>0x152710.</summary>
    private void UpdateShip(SonicObject o, BossState s)
    {
        switch (o.RoutineSecondary)
        {
            case Descend:
                o.YVel = 0x100;
                s.Move();
                if (s.BaseY == HoverY) { o.YVel = 0; o.RoutineSecondary = MoveLeft; }
                Hover(o, s);
                break;
            case MoveLeft:
                o.XVel = -0x100;
                o.YVel = -0x40;
                s.Move();
                if (s.BaseX == ArenaX)
                {
                    o.XVel = 0;
                    o.YVel = 0;
                    o.RoutineSecondary = Pause;
                    int slot = ObjectKit.FindFreeSlotAfter(Ram, o);
                    if (slot >= 0)
                    {
                        var ball = new SonicObject(Ram, slot);
                        ball.Id = WreckingBallId;
                        ball.X = s.BaseX;
                        ball.Y = s.BaseY;
                        new BossState(ball).Parent = o.Address;
                    }
                    s.Timer = 0x77;
                }
                Hover(o, s);
                break;
            case Pause:
                s.Timer--;
                if ((short)s.Timer < 0)
                {
                    o.RoutineSecondary = Swing;
                    s.Timer = 0x3F;
                    int speed = 0x100;
                    if (s.BaseX == ArenaX) { s.Timer = 0x7F; speed = 0x40; }   // the first swing is from the middle
                    o.XVel = (o.Status & ObjStatus.XFlip) != 0 ? (short)speed : (short)-speed;
                }
                Hover(o, s);
                break;
            case Swing:
                s.Timer--;
                if ((short)s.Timer < 0)
                {
                    o.Status ^= ObjStatus.XFlip;   // turn round
                    s.Timer = 0x3F;
                    o.RoutineSecondary = Pause;
                    o.XVel = 0;
                }
                else s.Move();
                Hover(o, s);
                break;
            case Exploding:
                s.Timer--;
                if ((short)s.Timer >= 0) { Explosions(Ram, o); break; }
                o.Status |= ObjStatus.XFlip;
                o.Status &= ~ObjStatus.Destroyed;
                o.XVel = 0;
                o.RoutineSecondary = DropAndRise;
                s.Timer = 0xFFDA;   // -$26
                if (Ram.U8(Vars.BossDefeatedFlag) == 0) Ram.W8(Vars.BossDefeatedFlag, 1);
                break;
            case DropAndRise:
            {
                s.Timer++;
                int t = s.Timer;
                if (t == 0) o.YVel = 0;
                else if ((short)t < 0) o.YVel += 0x18;   // falling
                else if (t < 0x30) o.YVel -= 8;           // rising
                else if (t == 0x30)
                {
                    o.YVel = 0;
                    Ctx.Sound.PlayMusic(Ram.U16(Vars.LevelMusic));   // 0x1209A: back to the level music
                    LoadAnimalArt(Ctx);                               // 0x120A2
                }
                else if (t >= 0x38) o.RoutineSecondary = FlyAway;
                s.Move();
                Hover(o, s);
                break;
            }
            case FlyAway:
                o.XVel = 0x400;
                o.YVel = -0x40;
                if (Ram.U16(Vars.CameraMaxXPos) == FinalCameraMaxX)
                {
                    if (!o.OnScreen) { Delete(o); return; }
                }
                else
                {
                    // Open the camera's right edge 2 px a frame.
                    Ram.W16(Vars.CameraMaxXPos, Ram.U16(Vars.CameraMaxXPos) + 2);
                    Ram.W16(Vars.TailsMaxXPos, Ram.U16(Vars.TailsMaxXPos) + 2);
                }
                s.Move();
                Hover(o, s);
                break;
        }
        ObjectKit.Animate(Ram, o, _anims);
        o.RenderFlags = (o.RenderFlags & ~3) | (o.Status & 3);
        Display(o);
    }

    /// <summary>The zone PLC list the title card and LoadAnimalArt use (0x1721A, zones 0-$1C).</summary>
    private static readonly byte[] ZonePlcs =
        [0x32, 0x32, 0x32, 0x32, 0x34, 0x34, 0x34, 0x34, 0x36, 0x36, 0x37, 0x33, 0x39, 0x3A, 0x35, 0x3B, 0x38,
         0x45, 0x45, 0x4A, 0x4A, 0x4E, 0x4E, 0x52, 0x52, 0x56, 0x56, 0x5A, 0x5A];

    /// <summary>0x120A2: the zone's art PLC (animals etc.) and the points art (PLC $41).</summary>
    public static void LoadAnimalArt(ObjectContext ctx)
    {
        int zone = ctx.Ram.U8(Vars.CurrentZoneAndAct);
        if (zone >= ZonePlcs.Length) throw new UnportedException(0x120A2, $"zone PLC for zone {zone:X2}");
        ctx.LoadPlc?.Invoke(ZonePlcs[zone]);
        ctx.LoadPlc?.Invoke(0x41);
    }

    /// <summary>0x15276C: bob up and down; react to hits (flash; 100 points and explosions when beaten).</summary>
    private void Hover(SonicObject o, BossState s)
    {
        int sin = MdMath.CalcSine(s.HoverAngle).Sin;
        o.Y = (sin >> 6) + s.BaseY;
        o.X = s.BaseX;
        s.HoverAngle += 2;
        if (o.RoutineSecondary >= Exploding) return;
        if ((o.Status & ObjStatus.Destroyed) != 0)
        {
            Ctx.Touch.AddPoints(100);
            o.RoutineSecondary = Exploding;
            s.Timer = 0xB3;
            return;
        }
        if (o.CollisionFlags != 0) return;   // not just hit
        if (s.FlashTimer == 0)
        {
            s.FlashTimer = FlashFrames;
            Ctx.Sound.PlaySound(SfxBossHit);
        }
        // Flash: toggle one colour between black and white.
        int colour = Vars.NormalPaletteLine2 + 2;
        Ram.W16(colour, Ram.U16(colour) == 0 ? 0xEEE : 0);
        s.FlashTimer--;
        if (s.FlashTimer == 0) o.CollisionFlags = 0x0F;   // touchable again
    }

    /// <summary>BossMove (0x152EBA): move the base position by the velocity.</summary>
    public static void BossMove(SonicObject o) => new BossState(o).Move();

    /// <summary>0x152E78: every 8 frames an explosion (object $58) at a random point around the object.</summary>
    public static void Explosions(Ram ram, SonicObject o)
    {
        if ((ram.U8(Vars.VintRuncountLow) & 7) != 0) return;
        int slot = Objects.FindFreeSlot(ram);
        if (slot < 0) return;
        var explosion = new SonicObject(ram, slot);
        explosion.Id = BossExplosion.Id;
        uint r = ObjectKit.RandomNumber(ram);
        explosion.X = o.X + (int)((r & 0xFF) >> 2) - 0x20;
        explosion.Y = o.Y + (int)(((r >> 8) & 0xFF) >> 3);
    }

    /// <summary>0x152948: the face. Laughs when a player is hurt, winces when hit, shows the defeated face afterwards.</summary>
    private void UpdateFace(SonicObject o)
    {
        var ship = new SonicObject(Ram, new BossState(o).Parent);
        int phase = ship.RoutineSecondary;
        const int AnimNormal = 1, AnimLaugh = 4, AnimHit = 5, AnimDefeated = 0x0A, AnimFlyingAway = 6;
        int anim;
        if (phase >= Exploding + 2)
            anim = AnimDefeated;
        else
        {
            anim = phase == Pause && new BossState(ship).BaseX == ArenaX ? AnimLaugh : AnimNormal;
            if (ship.CollisionFlags == 0) anim = AnimHit;
            else if (Sonic.Routine == 4 || Tails.Routine == 4) anim = AnimLaugh;   // a player is hurt
        }
        o.Anim = anim;
        if (phase == FlyAway)
        {
            o.Anim = AnimFlyingAway;
            if (!o.OnScreen) { Delete(o); return; }
        }
        Follow(o, ship);
    }

    /// <summary>0x1529A2: the exhaust flame.</summary>
    private void UpdateFlame(SonicObject o)
    {
        var ship = new SonicObject(Ram, new BossState(o).Parent);
        const int AnimIdle = 7, AnimMoving = 8, AnimFlyingAway = 0x0B;
        o.Anim = AnimIdle;
        if (ship.RoutineSecondary == FlyAway)
        {
            o.Anim = AnimFlyingAway;
            if (!o.OnScreen) { Delete(o); return; }
        }
        else if (ship.XVel != 0) o.Anim = AnimMoving;
        Follow(o, ship);
    }

    /// <summary>0x1529D6.</summary>
    private void Follow(SonicObject o, SonicObject ship)
    {
        o.X = ship.X;
        o.Y = ship.Y;
        o.Status = ship.Status;
        ObjectKit.Animate(Ram, o, _anims);
        o.RenderFlags = (o.RenderFlags & ~3) | (o.Status & 3);
        Display(o);
    }
}

/// <summary>
/// The Green Hill boss's wrecking ball, object $48 in the hack's zones (ROM 0x152EE0). An anchor (this object), four
/// chain links and the ball. The chain extends to radii $10..$60; then, once the boss swings, the angle accelerates
/// back and forth by 8 up to +-$200. When the boss is beaten the parts become object $3F (deleted); the ball
/// explodes for a while first.
/// </summary>
public sealed class WreckingBall(ObjectContext ctx) : GameObject(ctx)
{
    private const int Init = 0, Extend = 2, Swinging = 4, Link = 6, Ball = 8;
    private const int DeletedId = 0x3F;
    private const int MaxSwingSpeed = 0x200, SwingAcceleration = 8;

    /// <summary>Each part's distance from the anchor (0x152FAC).</summary>
    private static readonly byte[] Radii = [0x00, 0x10, 0x20, 0x30, 0x40, 0x60];

    /// <summary>The anchor's variables (each part keeps its radius at $3C; the boss's slot at $34).</summary>
    private readonly struct State(SonicObject o)
    {
        /// <summary>$26: the swing angle (a word: the high byte is the angle).</summary>
        public int SwingAngle { get => o.Word(0x26); set => o.SetWord(0x26, value); }
        /// <summary>$29 onward: the parts' slot numbers (the count is in the subtype).</summary>
        public SonicObject PartAt(int i) => Objects.Slot(o.Ram, o.Byte(0x29 + i));
        /// <summary>$32: how far below the ship the anchor hangs (grows to $20).</summary>
        public int Drop { get => o.Word(0x32); set => o.SetWord(0x32, value); }
        /// <summary>$38, $3A: the anchor's position.</summary>
        public int AnchorY { get => o.Word(0x38); set => o.SetWord(0x38, value); }
        public int AnchorX { get => o.Word(0x3A); set => o.SetWord(0x3A, value); }
        /// <summary>$3D: swinging back; $3E: the angular speed.</summary>
        public bool SwingingBack { get => o.Byte(0x3D) != 0; set => o.SetByte(0x3D, value ? 1 : 0); }
        public int SwingSpeed { get => o.Word(0x3E); set => o.SetWord(0x3E, value); }
    }

    /// <summary>$3C, per part: its radius (the ball also counts it down as its explosion timer).</summary>
    private static int Radius(SonicObject part) => part.Byte(0x3C);

    public override void Run(SonicObject o)
    {
        var s = new State(o);
        switch (o.Routine)
        {
            case Init: CreateParts(o, s); break;
            case Extend: ExtendChain(o, s); break;
            case Swinging:
                FollowShip(o, s);
                SwingStep(s);
                PlaceParts(o, s, o.Angle);
                Display(o);
                break;
            case Link:
                if (ShipDefeated(o)) { o.Id = DeletedId; o.Routine = 0; }
                Display(o);
                break;
            case Ball: UpdateBall(o); break;
        }
    }

    private SonicObject Ship(SonicObject o) => new(Ram, new BossState(o).Parent);
    private bool ShipDefeated(SonicObject o) => (Ship(o).Status & ObjStatus.Destroyed) != 0;

    private void CreateParts(SonicObject o, State s)
    {
        o.Routine = Extend;
        s.SwingAngle = 0x4080;
        s.SwingSpeed = 0xFE00;
        o.Mappings = 0x152D98;
        o.ArtTile = 0x46C;
        int nextSlotRef = o.Address + 0x28;
        Ram.W8(nextSlotRef++, 0);   // the part count (subtype)
        var part = o;
        for (int i = 0; i < 6; i++)
        {
            if (i > 0)
            {
                int slot = ObjectKit.FindFreeSlotAfter(Ram, o);
                if (slot < 0) break;
                part = new SonicObject(Ram, slot);
                part.X = o.X;
                part.Y = o.Y;
                part.Id = o.Id;
                part.Routine = Link;
                part.Mappings = SwingingPlatform.Mappings;
                part.ArtTile = 0x4380;
                part.MappingFrame = 1;
                o.Subtype++;
            }
            Ram.W8(nextSlotRef++, Objects.SlotIndex(part.Address));
            part.RenderFlags = Render.LevelCoords;
            part.WidthPixels = 8;
            part.Priority = 6;
            part.SetLong(0x34, o.Long(0x34));   // the ship
        }
        // The last part is the ball.
        part.Routine = Ball;
        part.Mappings = 0x1530B2;
        part.ArtTile = 0x4300;
        part.MappingFrame = 1;
        part.Priority = 5;
        part.CollisionFlags = 0x81;
    }

    /// <summary>0x152FB2: each part moves out 1 px a frame to its radius; then swing once the ship starts moving.</summary>
    private void ExtendChain(SonicObject o, State s)
    {
        int count = o.Subtype + 1, radius = 0;
        SonicObject last = o;
        for (int i = 0; i < count; i++)
        {
            var part = s.PartAt(i);
            radius = Radii[i];
            last = part;
            if (Radius(part) != radius) part.SetByte(0x3C, Radius(part) + 1);
        }
        const int ShipSwinging = 6;
        if (Radius(last) == radius && Ship(o).RoutineSecondary == ShipSwinging) o.Routine = Swinging;
        if (s.Drop != 0x20) s.Drop++;
        FollowShip(o, s);
        PlaceParts(o, s, o.Angle);
        Display(o);
    }

    /// <summary>0x153024: the anchor follows the ship; its frame flickers.</summary>
    private void FollowShip(SonicObject o, State s)
    {
        int flicker = o.AnimFrame + 0x20;
        o.AnimFrame = flicker;
        if (flicker > 0xFF) o.MappingFrame ^= 1;
        var ship = Ship(o);
        s.AnchorX = ship.X;
        s.AnchorY = ship.Y + s.Drop;
        o.Status = ship.Status;
        if ((ship.Status & ObjStatus.Destroyed) != 0) { o.Id = DeletedId; o.Routine = 0; }
    }

    /// <summary>0x153152: accelerate the swing one way up to $200, then the other way down to -$200.</summary>
    private static void SwingStep(State s)
    {
        if (!s.SwingingBack)
        {
            int speed = (s.SwingSpeed + SwingAcceleration) & 0xFFFF;
            s.SwingSpeed = speed;
            s.SwingAngle += speed;
            if (speed == MaxSwingSpeed) s.SwingingBack = true;
        }
        else
        {
            int speed = (s.SwingSpeed - SwingAcceleration) & 0xFFFF;
            s.SwingSpeed = speed;
            s.SwingAngle += speed;
            if (speed == (-MaxSwingSpeed & 0xFFFF)) s.SwingingBack = false;
        }
    }

    /// <summary>0x153192: place every part at its radius along the angle.</summary>
    private void PlaceParts(SonicObject o, State s, int angle)
    {
        var (sin, cos) = MdMath.CalcSine(angle & 0xFF);
        int count = o.Subtype + 1;
        for (int i = 0; i < count; i++)
        {
            var part = s.PartAt(i);
            int r = Radius(part);
            part.Y = ((r * sin) >> 8) + s.AnchorY;
            part.X = ((r * cos) >> 8) + s.AnchorX;
        }
    }

    /// <summary>0x15307A: the ball flickers; once the boss is beaten it explodes for a while, then goes.</summary>
    private void UpdateBall(SonicObject o)
    {
        o.MappingFrame = o.MappingFrame == 0 ? 1 : 0;
        if (ShipDefeated(o))
        {
            o.CollisionFlags = 0;
            GhzBoss.Explosions(Ram, o);
            o.SetByte(0x3C, Radius(o) - 1);
            if ((sbyte)Radius(o) < 0) { o.Id = DeletedId; o.Routine = 0; }
        }
        Display(o);
    }
}

/// <summary>A boss explosion, object $58 (ROM 0x316F8): 7 frames of 8, with its sound.</summary>
public sealed class BossExplosion(ObjectContext ctx) : GameObject(ctx)
{
    public const int Id = 0x58;
    private const int SfxBossExplosion = 0xC4;

    public override void Run(SonicObject o)
    {
        if (o.Routine == 0)
        {
            o.Routine = 2;
            o.Mappings = 0x3176E;
            o.ArtTile = 0x8580;
            o.RenderFlags = Render.LevelCoords;
            o.Priority = 0;
            o.CollisionFlags = 0;
            o.WidthPixels = 0x0C;
            o.AnimFrameDuration = 7;
            o.MappingFrame = 0;
            Ctx.Sound.PlaySound(SfxBossExplosion);
            return;
        }
        o.AnimFrameDuration--;
        if ((sbyte)o.AnimFrameDuration < 0)
        {
            o.AnimFrameDuration = 7;
            o.MappingFrame++;
            if (o.MappingFrame == 7) { Delete(o); return; }
        }
        Display(o);
    }
}
