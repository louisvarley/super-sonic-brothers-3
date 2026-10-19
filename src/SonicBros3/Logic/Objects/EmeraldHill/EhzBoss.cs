namespace SonicBros3.Logic;

/// <summary>The variables the Emerald Hill boss's parts keep in their slots.</summary>
public readonly struct EhzBossState(SonicObject o)
{
    /// <summary>$2A: a countdown for the current phase.</summary>
    public int Timer { get => (short)o.Word(0x2A); set => o.SetWord(0x2A, value); }
    /// <summary>$2C: the step within a phase (the main car's third routine counter).</summary>
    public int Step { get => o.Byte(0x2C); set => o.SetByte(0x2C, value); }
    /// <summary>$2D: the main car's flags (see the EhzBoss.Flag* constants), which the other parts follow.</summary>
    public int Flags { get => o.Byte(0x2D); set => o.SetByte(0x2D, value); }
    /// <summary>$2E: the two foreground wheels' y positions added up each frame; the car sits on their average.</summary>
    public int WheelYSum { get => o.Word(0x2E); set => o.SetWord(0x2E, value); }
    /// <summary>$34: the linked part (the cockpit for the main car; the main car for every other part).</summary>
    public int Link { get => (int)(o.Long(0x34) & 0xFFFF); set => o.SetLong(0x34, 0xFFFF0000u | (uint)value); }
    /// <summary>$3C: frames of explosions left after the defeat.</summary>
    public int DefeatTimer { get => (short)o.Word(0x3C); set => o.SetWord(0x3C, value); }
    /// <summary>$3E: frames left of the white flash after a hit.</summary>
    public int FlashTimer { get => o.Byte(0x3E); set => o.SetByte(0x3E, value); }

    public bool Has(int flag) => (Flags & flag) != 0;
    public void Set(int flag) => Flags |= flag;
    public void Clear(int flag) => Flags &= ~flag;
}

/// <summary>
/// The Emerald Hill boss, object $56 in Sonic 2's zones (ROM 0x33154, Sonic 2's code unchanged): Eggman flies in on
/// his propeller ship, lands on a drill car, and drives back and forth across the arena. Eight hits, a white flash
/// after each. With one hit left the drill shoots off the front when Sonic is ahead of it. Beaten: 1000 points,
/// explosions while it falls, then Eggman flies off on a fresh propeller and the egg prison's art loads.
///
/// The placed object is the main car (the ship's bottom half), which spawns the other parts with the same id and
/// other routines: the cockpit with Eggman, the ground car, three wheels, the drill and the propeller.
/// </summary>
public sealed class EhzBoss(ObjectContext ctx) : GameObject(ctx)
{
    public const int Id = 0x56;
    public const uint ShipMappings = 0x33D24, CarMappings = 0x33C84, PropellerMappings = 0x33B9C;
    private readonly byte[] _shipAnims = ctx.Art["ehz-boss-ship"].Animations!;
    private readonly byte[] _carAnims = ctx.Art["ehz-boss-car"].Animations!;
    private readonly byte[] _propellerAnims = ctx.Art["ehz-boss-propeller"].Animations!;

    // Routines: one per part.
    private const int Init = 0, MainCar = 2, Propeller = 4, GroundCar = 6, Wheel = 8, Drill = 0x0A,
        PropellerReloaded = 0x0C, Cockpit = 0x0E;
    // The main car's phases (routine_secondary).
    private const int FlyIn = 0, Land = 2, Drive = 4, Falling = 6, Idle = 8, FlyOff = 0x0A;

    /// <summary>The main car's flags ($2D).</summary>
    public const int FlagOnGround = 1, FlagActive = 2, FlagFlyingOff = 4, FlagDrillLoose = 8;

    private const int ArtShip = 0x03A0, ArtCar = 0x0400, ArtPropeller = 0x056C;
    private const int Palette1 = 0x2000;
    private const int ArenaLeft = 0x28A0, ArenaRight = 0x2B08;
    private const int CarStartX = 0x2AF0, LandingX = 0x29D0, LandingY = 0x41E;
    /// <summary>The ground parts wait until the camera's left limit reaches the arena.</summary>
    private const int ArenaCameraX = 0x28F0;
    private const int Hits = 8, FlashFrames = 0x20, DriveSpeed = -0x200;
    private const int PlcEggPrison = 0x40;

    // Cockpit animations (ehz-boss-ship): 1 normal, 2 hit, 3 laughing, 4 flying off.
    private const int AnimNormal = 1, AnimHit = 2, AnimLaugh = 3, AnimFlyOff = 4;
    // Propeller animations: 0 spinning, 1 winding down, 2 winding up.
    private const int AnimWindDown = 1, AnimWindUp = 2;

    /// <summary>Per wheel (subtype): where it stops rolling in (0x2F67C).</summary>
    private static readonly int[] WheelStopX = [0x29EC, 0x29C4, 0x29A4];

    public override void Run(SonicObject o)
    {
        switch (o.Routine)
        {
            case Init: Create(o); break;
            case MainCar: UpdateMainCar(o, new EhzBossState(o)); break;
            case Propeller: UpdatePropeller(o, new EhzBossState(o)); break;
            case GroundCar: UpdateGroundCar(o); break;
            case Wheel: UpdateWheel(o, new EhzBossState(o)); break;
            case Drill: UpdateDrill(o); break;
            case PropellerReloaded: UpdatePropellerReloaded(o, new EhzBossState(o)); break;
            case Cockpit: UpdateCockpit(o); break;
        }
    }

    // ------------------------------------------------------------------ setting up

    /// <summary>0x33170: the main car, then the parts in slot order (a part is skipped when no slot is free).</summary>
    private void Create(SonicObject o)
    {
        o.Mappings = ShipMappings;
        o.ArtTile = Palette1 | ArtShip;
        o.RenderFlags |= Render.LevelCoords;
        o.Subtype = 0x81;
        o.X = LandingX;
        o.Y = 0x426;
        o.WidthPixels = 0x20;
        o.YRadius = 0x14;
        o.Priority = 4;
        o.CollisionFlags = 0x0F;
        o.CollisionProperty = Hits;
        o.Routine = MainCar;
        o.SetWord(0x30, o.X);
        o.SetWord(0x38, o.Y);
        var s = new EhzBossState(o);

        if (SpawnAfter(o) is { } top)
        {
            new EhzBossState(top).Link = o.Address;
            s.Link = top.Address;
            top.Mappings = ShipMappings;
            top.ArtTile = ArtShip;
            top.RenderFlags = Render.LevelCoords;
            top.WidthPixels = 0x20;
            top.Priority = 4;
            top.XLong = o.XLong;
            top.YLong = o.YLong;
            top.Routine = Cockpit;
            top.Anim = AnimNormal;
            top.RenderFlags = o.RenderFlags;
        }
        if (SpawnPart(o, CarMappings, ArtCar, width: 0x30, priority: 3, CarStartX) is { } car)
        {
            car.YRadius = 0x10;
            car.Routine = GroundCar;
        }
        CreateWheelsAndDrill(o);
        o.SetWord(0x38, o.Word(0x38) - 8);
        o.X = CarStartX;
        o.Y = 0x2F8;
        if (SpawnPart(o, PropellerMappings, Palette1 | ArtPropeller, width: 0x40, priority: 3, x: null) is { } propeller)
        {
            new EhzBossState(propeller).Timer = 0x1E;
            propeller.Routine = Propeller;
        }
    }

    /// <summary>0x332CE: two foreground wheels, the background wheel, and the drill.</summary>
    private void CreateWheelsAndDrill(SonicObject o)
    {
        (int Dx, int Priority, int Frame, int Anim)[] wheels = [(0x1C, 2, 4, 1), (-0x0C, 2, 4, 1), (-0x2C, 3, 6, 2)];
        for (int i = 0; i < wheels.Length; i++)
        {
            var (dx, priority, frame, anim) = wheels[i];
            if (SpawnPart(o, CarMappings, Palette1 | ArtCar, width: 0x10, priority, CarStartX) is not { } wheel) continue;
            wheel.YRadius = 0x10;
            wheel.XRadius = 0x10;
            wheel.X += dx;
            wheel.Y += 0x0C;
            wheel.Routine = Wheel;
            wheel.MappingFrame = frame;
            wheel.Anim = anim;
            new EhzBossState(wheel).Timer = 0x0A;
            wheel.Subtype = i;
        }
        if (SpawnPart(o, CarMappings, Palette1 | ArtCar, width: 0x20, priority: 2, CarStartX) is { } drill)
        {
            drill.X -= 0x36;
            drill.Y += 8;
            drill.Routine = Drill;
            drill.MappingFrame = 1;
            drill.Anim = 0;
        }
    }

    /// <summary>A part linked to the main car, at its y (and at x, or the car's x when null).</summary>
    private SonicObject? SpawnPart(SonicObject o, uint mappings, int artTile, int width, int priority, int? x)
    {
        if (SpawnAfter(o) is not { } part) return null;
        part.Id = Id;
        new EhzBossState(part).Link = o.Address;
        part.Mappings = mappings;
        part.ArtTile = artTile;
        part.RenderFlags = Render.LevelCoords;
        part.WidthPixels = width;
        part.Priority = priority;
        if (x is { } px) part.X = px; else part.XLong = o.XLong;
        part.YLong = o.YLong;
        return part;
    }

    /// <summary>AllocateObjectAfterCurrent (0x1BD76), with the id set.</summary>
    private SonicObject? SpawnAfter(SonicObject o)
    {
        int slot = ObjectKit.FindFreeSlotAfter(Ram, o);
        if (slot < 0) return null;
        var part = new SonicObject(Ram, slot) { Id = Id };
        return part;
    }

    // ------------------------------------------------------------------ the main car (ship bottom)

    /// <summary>0x33490.</summary>
    private void UpdateMainCar(SonicObject o, EhzBossState s)
    {
        switch (o.RoutineSecondary)
        {
            case FlyIn:   // diagonally down to the landing point, not touchable yet
                o.CollisionFlags = 0;
                if (o.X > LandingX) { o.X--; o.Y++; }
                else { o.X = LandingX; o.RoutineSecondary = Land; }
                break;
            case Land:
                if (s.Step == 0)
                {
                    if (o.Y < LandingY) o.Y++;
                    else { s.Step = 2; s.Set(FlagOnGround); s.Timer = 60; }
                }
                else if (--s.Timer < 0)
                {
                    o.XVel = DriveSpeed;
                    o.RoutineSecondary = Drive;
                    o.CollisionFlags = 0x0F;
                    s.Set(FlagActive);
                }
                break;
            case Drive:
                HandleHits(o, s);
                TurnAtArenaEdges(o);
                o.Y = ((s.WheelYSum & 0xFFFF) >> 1) - 0x14;   // ride on the foreground wheels
                s.WheelYSum = 0;
                o.XLong = (uint)((int)o.XLong + (o.XVel << 8));
                break;
            case Falling:
                if (--s.DefeatTimer < 0)
                {
                    o.XVel = 0;
                    o.RoutineSecondary = Idle;
                    s.DefeatTimer = -0x26;
                    s.Timer = 0x0C;
                    break;
                }
                Explosions(Ram, o);
                ObjectKit.MoveAndFall(o);
                int floor = ObjectKit.CheckFloor(Ram, Ctx.Collision, o).Distance;
                if (floor < 0) { o.Y += floor; o.YVel = 0; }
                break;
            case Idle:
                if (--s.Timer < 0) { o.RoutineSecondary = FlyOff; s.Step = 0; }
                break;
            case FlyOff:
                if (!FlyAway(o, s)) return;   // gone
                break;
        }
        Display(o);
    }

    /// <summary>0x335B8: Eggman gets a new propeller, waits, then rises and flies off right. False once deleted.</summary>
    private bool FlyAway(SonicObject o, EhzBossState s)
    {
        switch (s.Step)
        {
            case 0:
                s.Clear(FlagOnGround);
                if (SpawnAfter(o) is not { } propeller) break;
                new EhzBossState(propeller).Link = o.Address;
                propeller.Mappings = PropellerMappings;
                propeller.ArtTile = Palette1 | ArtPropeller;
                propeller.RenderFlags = Render.LevelCoords;
                propeller.WidthPixels = 0x20;
                propeller.Priority = 3;
                propeller.XLong = o.XLong;
                propeller.YLong = o.YLong;
                propeller.Y += 0x0C;
                propeller.Status = o.Status;
                propeller.RenderFlags = o.RenderFlags;
                propeller.Routine = PropellerReloaded;
                propeller.Anim = AnimWindUp;
                new EhzBossState(propeller).Timer = 0x10;
                s.Timer = 0x32;
                s.Step = 2;
                Ctx.Sound.PlayMusic(Ram.U16(Vars.LevelMusic));   // PlayLevelMusic (0x1209A)
                Ram.W8(Vars.BossDefeatedFlag, 1);
                break;
            case 2:
                if (--s.Timer >= 0) break;
                s.Set(FlagFlyingOff);
                s.Timer = 0x60;
                s.Step = 4;
                GhzBoss.LoadAnimalArt(Ctx);   // 0x120A2
                break;
            case 4:
                if (--s.Timer >= 0) o.Y--;   // rise first
                else
                {
                    o.Status |= ObjStatus.XFlip;   // then head right
                    o.RenderFlags |= Render.XFlip;
                    o.X += 6;
                }
                if (Ram.U16(Vars.CameraMaxXPos) < 0x2AB0)
                    Ram.W16(Vars.CameraMaxXPos, Ram.U16(Vars.CameraMaxXPos) + 2);
                else if (!o.OnScreen)
                {
                    Objects.DeleteObject(Ram, new SonicObject(Ram, s.Link));   // the cockpit
                    Delete(o);
                    return false;
                }
                break;
        }
        return true;
    }

    /// <summary>0x336B0: turn round at either side of the arena.</summary>
    private static void TurnAtArenaEdges(SonicObject o)
    {
        int x = (short)o.X;
        if (x > ArenaLeft && x < ArenaRight) return;
        o.Status ^= ObjStatus.XFlip;
        o.RenderFlags ^= Render.XFlip;
        o.XVel = (short)-o.XVel;
    }

    /// <summary>0x336D2: a hit flashes the boss white for 32 frames; the last hit starts the defeat.</summary>
    private void HandleHits(SonicObject o, EhzBossState s)
    {
        if (o.RoutineSecondary >= Falling) return;
        if ((o.Status & ObjStatus.Destroyed) != 0) { Defeat(o, s); return; }
        if (o.CollisionFlags != 0) return;   // not just hit
        if (s.FlashTimer == 0)
        {
            s.FlashTimer = FlashFrames;
            Ctx.Sound.PlaySound(Sfx.BossHit);
        }
        int colour = Vars.NormalPaletteLine2 + 2;
        Ram.W16(colour, Ram.U16(colour) == 0 ? 0xEEE : 0);
        if (--s.FlashTimer == 0) o.CollisionFlags = 0x0F;   // touchable again
    }

    /// <summary>0x3371A: 1000 points; the car bounces up and falls; the drill comes loose; the egg prison's art loads.</summary>
    private void Defeat(SonicObject o, EhzBossState s)
    {
        Ctx.Touch.AddPoints(100);
        o.RoutineSecondary = Falling;
        o.XVel = 0;
        o.YVel = -0x180;
        s.DefeatTimer = 0xB3;
        s.Set(FlagDrillLoose);
        var cockpit = new SonicObject(Ram, s.Link);
        cockpit.Anim = AnimFlyOff;
        cockpit.MappingFrame = 6;
        Ctx.LoadPlc?.Invoke(PlcEggPrison);
    }

    /// <summary>Boss_LoadExplosion (0x31930): every 8 frames an explosion (object $58) within $20 px of the object.</summary>
    public static void Explosions(Ram ram, SonicObject o)
    {
        if ((ram.U8(Vars.VintRuncountLow) & 7) != 0) return;
        int slot = Objects.FindFreeSlot(ram);
        if (slot < 0) return;
        var explosion = new SonicObject(ram, slot) { Id = BossExplosion.Id };
        explosion.X = o.X;
        explosion.Y = o.Y;
        uint r = ObjectKit.RandomNumber(ram);
        explosion.X += (int)((r & 0xFF) >> 2) - 0x20;
        explosion.Y += (int)(((r >> 8) & 0xFF) >> 2) - 0x20;
    }

    // ------------------------------------------------------------------ the other parts

    /// <summary>0x3377A: follows the main car, with the helicopter sound; winds down once Eggman lands.</summary>
    private void UpdatePropeller(SonicObject o, EhzBossState s)
    {
        if (o.RoutineSecondary == 0)
        {
            var car = new SonicObject(Ram, s.Link);
            if (car.Id != Id) { Delete(o); return; }
            if (new EhzBossState(car).Has(FlagOnGround))
            {
                o.Anim = AnimWindDown;
                s.Timer = 0x18;
                o.RoutineSecondary = 2;
                Ctx.Sound.PlaySound(Music.StopSfx);
            }
            else if ((Ram.U8(Vars.VintRuncountLow) & 0x1F) == 0)
                Ctx.Sound.PlaySound(Sfx.Helicopter);
            o.X = car.X;
            o.Y = car.Y;
            o.Status = car.Status;
            o.RenderFlags = car.RenderFlags;
        }
        else if (--s.Timer < 0)
        {
            // Wound down: sink into the car, then go.
            if (s.Timer <= -0x10) { Delete(o); return; }
            o.Priority = 4;
            o.Y++;
            Display(o);
            return;
        }
        ObjectKit.Animate(Ram, o, _propellerAnims);
        Display(o);
    }

    /// <summary>0x33756: the new propeller rises out of the car winding up, then carries Eggman off.</summary>
    private void UpdatePropellerReloaded(SonicObject o, EhzBossState s)
    {
        o.Y--;
        if (--s.Timer >= 0) { Display(o); return; }
        o.Routine = Propeller;
        ObjectKit.Animate(Ram, o, _propellerAnims);
        Display(o);
    }

    /// <summary>0x33822: rolls in from the right, then rides under the main car while it drives.</summary>
    private void UpdateGroundCar(SonicObject o)
    {
        if (o.RoutineSecondary == 0)
        {
            if (Ram.U16(Vars.CameraMinXPos) >= ArenaCameraX)
            {
                if (o.X > LandingX) o.X--;
                else { o.X = LandingX; o.RoutineSecondary = 2; }
            }
            Display(o);
            return;
        }
        var car = new SonicObject(Ram, new EhzBossState(o).Link);
        var flags = new EhzBossState(car);
        if (flags.Has(FlagActive) && !flags.Has(FlagFlyingOff))
        {
            o.X = car.X;
            o.Y = car.Y + 8;
            o.Status = car.Status;
            if ((o.Status & ObjStatus.Destroyed) == 0) o.RenderFlags = car.RenderFlags;
        }
        Display(o);
    }

    /// <summary>0x33890: a wheel. The two foreground wheels (priority 2) add their y to the car's $2E.</summary>
    private void UpdateWheel(SonicObject o, EhzBossState s)
    {
        switch (o.RoutineSecondary)
        {
            case 0:   // roll in to the start position
                if (Ram.U16(Vars.CameraMinXPos) < ArenaCameraX) break;
                o.YVel = 0x100;
                int stop = WheelStopX[Math.Min(o.Subtype, 2)];
                if (o.X > stop) o.X--;
                else { o.X = stop; o.RoutineSecondary = 2; }
                FallToFloor(o);
                if (o.RoutineSecondary != 0) o.XVel = DriveSpeed;
                ObjectKit.Animate(Ram, o, _carAnims);
                break;
            case 2:   // wait for the boss to start driving
            {
                var car = new SonicObject(Ram, s.Link);
                if (car.Id != Id) { Delete(o); return; }
                if (!new EhzBossState(car).Has(FlagActive)) break;
                o.RoutineSecondary = 4;
                if (o.Priority == 2) AddToWheelSum(o, car);
                break;
            }
            case 4:   // drive with the car
            {
                var car = new SonicObject(Ram, s.Link);
                if (car.Id != Id) { Delete(o); return; }
                o.Status = car.Status;
                o.RenderFlags = car.RenderFlags;
                if ((o.Status & ObjStatus.Destroyed) != 0) o.RoutineSecondary = 6;
                TurnAtArenaEdges(o);
                FallToFloor(o);
                o.YVel = 0x100;
                if (o.Priority == 2) AddToWheelSum(o, car);
                ObjectKit.Animate(Ram, o, _carAnims);
                break;
            }
            case 6:   // the boss is beaten: a pause, then a big bounce (the front wheels keep going, the back one turns)
                if (--s.Timer >= 0) break;
                o.RoutineSecondary = 8;
                s.Timer = 0x0A;
                o.YVel = -0x300;
                if (o.Priority != 2) o.XVel = (short)-o.XVel;
                break;
            case 8:   // bounce away until out of range
                if (--s.Timer >= 0) break;
                ObjectKit.MoveAndFall(o);
                int floor = ObjectKit.CheckFloor(Ram, Ctx.Collision, o).Distance;
                if (floor < 0) { o.YVel = -0x200; o.Y += floor; }
                DisplayOrMarkGone(o);   // MarkObjGone (0x1A118)
                return;
        }
        Display(o);
    }

    private void FallToFloor(SonicObject o)
    {
        ObjectKit.MoveAndFall(o);
        int floor = ObjectKit.CheckFloor(Ram, Ctx.Collision, o).Distance;
        if (floor < 0) o.Y += floor;
    }

    private static void AddToWheelSum(SonicObject wheel, SonicObject car)
    {
        var s = new EhzBossState(car);
        s.WheelYSum = (s.WheelYSum + wheel.Y) & 0xFFFF;
    }

    /// <summary>0x33A20: the drill at the car's front; it hurts. With one hit left it flies off towards Sonic.</summary>
    private void UpdateDrill(SonicObject o)
    {
        if (o.RoutineSecondary == 0)
        {
            if (Ram.U16(Vars.CameraMinXPos) >= ArenaCameraX)
            {
                const int StopX = 0x299A;
                if (o.X > StopX) o.X--;
                else { o.X = StopX; o.RoutineSecondary = 2; }
            }
            Display(o);
            return;
        }
        var car = new SonicObject(Ram, new EhzBossState(o).Link);
        if (car.Id != Id) { Delete(o); return; }
        var flags = new EhzBossState(car);
        if (flags.Has(FlagDrillLoose))
        {
            o.X += (o.Status & ObjStatus.XFlip) != 0 ? 3 : -3;   // flies straight on, 3 px a frame
            ObjectKit.Animate(Ram, o, _carAnims);
            Display(o);
            return;
        }
        // 0x33AD6: with one hit left, the drill shoots off when Sonic is on the side it points to.
        if (car.CollisionProperty == 1)
        {
            bool sonicToTheRight = (short)(o.X - Sonic.X) < 0;
            bool facingRight = (car.Status & ObjStatus.XFlip) != 0;
            if (sonicToTheRight == facingRight) flags.Set(FlagDrillLoose);
        }
        if (!flags.Has(FlagActive)) { Display(o); return; }
        o.CollisionFlags = 0x8B;   // hurts
        o.X = car.X;
        o.Y = car.Y + 0x10;
        o.Status = car.Status;
        o.RenderFlags = car.RenderFlags;
        o.X += (o.Status & ObjStatus.XFlip) != 0 ? 0x36 : -0x36;
        ObjectKit.Animate(Ram, o, _carAnims);
        Display(o);
    }

    /// <summary>0x33B06: Eggman in the cockpit: winces when hit, laughs while a player is hurt.</summary>
    private void UpdateCockpit(SonicObject o)
    {
        var car = new SonicObject(Ram, new EhzBossState(o).Link);
        o.XLong = car.XLong;
        o.YLong = car.YLong;
        o.Status = car.Status;
        o.RenderFlags = car.RenderFlags;
        if (new EhzBossState(car).FlashTimer == FlashFrames - 1) o.Anim = AnimHit;   // just hit
        const int Hurt = 4;
        if ((Sonic.Routine == Hurt || Tails.Routine == Hurt) && o.Anim != AnimHit) o.Anim = AnimLaugh;
        ObjectKit.Animate(Ram, o, _shipAnims);
        Display(o);
    }
}
