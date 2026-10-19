namespace SonicBros3.Logic;

/// <summary>
/// The item monitor, object $26 (ROM 0x155B0). Solid to walking players; broken by a rolling player (TouchResponse
/// sets routine <see cref="Breaking"/>); knocked up when hit from below (it falls back with routine_secondary set).
/// Breaking spawns the contents (<see cref="MonitorContents"/>) and an explosion.
/// </summary>
public sealed class Monitor(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x15C7A;
    private readonly byte[] _anims = ctx.Art["monitor"].Animations!;

    private const int Init = 0, Main = 2, Animating = 6, Broken = 8;
    /// <summary>Set by TouchResponse when a rolling player hits it.</summary>
    public const int Breaking = 4;
    private const int FrameBroken = 0x0B, AnimBroken = 0x0A, AnimTwoPlayer = 9;
    private const int ContentsId = 0x2E, ExplosionId = 0x27;
    private const int PlayerAnimRoll = 2;
    private const int HalfWidth = 0x1A, HalfHeight = 0x10;

    /// <summary>$3E: the slot of the player who broke it (set by TouchResponse).</summary>
    public const int BrokenBy = 0x3E;

    public override void Run(SonicObject o)
    {
        switch (o.Routine)
        {
            case Init: Initialise(o); break;
            case Main: Update(o); break;
            case Breaking: Break(o); break;
            case Animating: AnimateAndDisplay(o); break;
            case Broken: DisplayOrMarkGone(o); break;
        }
    }

    private void Initialise(SonicObject o)
    {
        o.Routine = Main;
        o.YRadius = 0x0E;
        o.XRadius = 0x0E;
        o.Mappings = Mappings;
        o.ArtTile = 0x680;
        o.RenderFlags = Render.LevelCoords;
        o.Priority = 3;
        o.WidthPixels = 0x0F;
        int respawn = ObjectKit.RespawnEntry(Ram, o);
        Ram.Clear(respawn, 7);
        if (Ram.Bit(respawn, 0))
        {
            o.Routine = Broken;   // already broken
            o.MappingFrame = FrameBroken;
            return;
        }
        o.CollisionFlags = 0x46;
        o.Anim = o.Subtype;
        if (TwoPlayerMode) o.Anim = AnimTwoPlayer;
        Update(o);
    }

    /// <summary>0x1563A.</summary>
    private void Update(SonicObject o)
    {
        if (o.RoutineSecondary != 0)
        {
            // Knocked up: fall back to the floor.
            ObjectKit.MoveAndFall(o);
            int distance = (short)ObjectKit.CheckFloorDist(Ram, Ctx.Collision, o);
            if (distance < 0)
            {
                o.Y += distance;
                o.YVel = 0;
                o.RoutineSecondary = 0;
            }
        }
        // Solid to each player, except to a rolling Sonic (he breaks it instead).
        int standingBit = 3;
        if (Ram.Bit(o.StatusAddr, standingBit)) StandOn(o, Sonic, standingBit);
        else if (Sonic.Anim != PlayerAnimRoll) Ctx.Solid.SolidObjectCont(o, Sonic, HalfWidth, 0x0F, HalfHeight, ref standingBit);
        standingBit = 4;
        if (Ram.Bit(o.StatusAddr, standingBit)) StandOn(o, Tails, standingBit);
        else if (!TwoPlayerMode || Tails.Anim != PlayerAnimRoll) Ctx.Solid.SolidObjectCont(o, Tails, HalfWidth, 0x0F, HalfHeight, ref standingBit);
        AnimateAndDisplay(o);
    }

    /// <summary>0x156C8: a player standing on the monitor.</summary>
    private void StandOn(SonicObject o, SonicObject player, int standingBit)
    {
        if (Ctx.Solid.StillOn(o, player, HalfWidth, standingBit)) Ctx.Solid.MoveOnPlatform(o, player, o.X, HalfHeight);
    }

    private void AnimateAndDisplay(SonicObject o)
    {
        ObjectKit.Animate(Ram, o, _anims);
        DisplayOrMarkGone(o);
    }

    /// <summary>0x15704: drop standing players, spawn the contents and an explosion, mark it broken.</summary>
    private void Break(SonicObject o)
    {
        int riders = o.Status & (ObjStatus.StandingP1 | ObjStatus.StandingP2 | ObjStatus.PushingP1 | ObjStatus.PushingP2);
        if (riders != 0)
        {
            // Off the monitor and in the air (clear "on object" and "pushing", set "in air").
            if ((riders & (ObjStatus.StandingP1 | ObjStatus.PushingP1)) != 0) Sonic.Status = (Sonic.Status & 0xD7) | 2;
            if ((riders & (ObjStatus.StandingP2 | ObjStatus.PushingP2)) != 0) Tails.Status = (Tails.Status & 0xD7) | 2;
        }
        o.Status = 0;
        o.Routine = Animating;
        o.CollisionFlags = 0;
        if (Spawn() is { } contents)
        {
            contents.Id = ContentsId;
            contents.X = o.X;
            contents.Y = o.Y;
            contents.Anim = o.Anim;
            contents.SetWord(BrokenBy, o.Word(BrokenBy));
        }
        if (Spawn() is { } explosion)
        {
            explosion.Id = ExplosionId;
            explosion.Routine = Explosion.FromMonitor;
            explosion.X = o.X;
            explosion.Y = o.Y;
        }
        Ram.Set(ObjectKit.RespawnEntry(Ram, o), 0);
        o.Anim = AnimBroken;
        Display(o);
    }
}

/// <summary>
/// The monitor's contents, object $2E (ROM 0x15798): the icon rises out of the broken monitor, then gives its item
/// to the player who broke it and lingers for 30 frames.
/// </summary>
public sealed class MonitorContents(ObjectContext ctx) : GameObject(ctx)
{
    private readonly int[] _frameOffsets = ctx.Art["monitor"].FrameOffsets!;
    private const int Init = 0, Rising = 2, Lingering = 4;
    private const int RiseSpeed = -0x300, RiseDeceleration = 0x18;

    /// <summary>The items, by the monitor's animation.</summary>
    private const int Static = 0, SonicLife = 1, TailsLife = 2, Eggman = 3, TenRings = 4, Shoes = 5, ShieldItem = 6,
        Invincible = 7, Teleport = 8;
    private const int MusicOneUp = 0x98, MusicSpeedShoes = 0xFB, MusicInvincibility = 0x97;
    private const int SfxRing = 0xB5, SfxShield = 0xAF;
    private const int ShieldId = 0x38, StarsId = 0x35;

    public override void Run(SonicObject o)
    {
        switch (o.Routine)
        {
            case Init: Initialise(o); break;
            case Rising: Rise(o); Display(o); break;
            case Lingering:
                o.SetWord(0x1E, o.Word(0x1E) - 1);
                if ((short)o.Word(0x1E) < 0) Delete(o);
                else Display(o);
                break;
        }
    }

    private void Initialise(SonicObject o)
    {
        o.Routine = Rising;
        o.ArtTile = 0x8680;
        o.RenderFlags = Render.StaticMappings | Render.LevelCoords;
        o.Priority = 3;
        o.WidthPixels = 8;
        o.YVel = RiseSpeed;
        if (TwoPlayerMode) throw new UnportedException(0x157DE, "2-player random monitor");
        int frame = o.Anim + 1;
        o.MappingFrame = frame;
        o.Mappings = Monitor.Mappings + (uint)_frameOffsets[frame] + 2;   // static: points straight at the icon piece
        Rise(o);
        Display(o);
    }

    /// <summary>0x15828: rise until stopped, then give the item.</summary>
    private void Rise(SonicObject o)
    {
        if (o.YVel < 0)
        {
            ObjectKit.Move(o);
            o.YVel += RiseDeceleration;
            return;
        }
        o.Routine = Lingering;
        o.SetWord(0x1E, 0x1D);
        var player = new SonicObject(Ram, o.Word(Monitor.BrokenBy));
        bool sonic = player.Address == Vars.MainCharacter;
        int monitorsBroken = sonic ? Vars.MonitorsBroken : Vars.MonitorsBrokenP2;
        switch (o.Anim)
        {
            case Static: case Eggman: throw new UnportedException(0x1587C, "Eggman monitor (hurt)");
            case SonicLife: Increment(monitorsBroken); ExtraLife(true); break;
            case TailsLife: Increment(monitorsBroken); ExtraLife(false); break;
            case TenRings: Rings(sonic, monitorsBroken); break;
            case Shoes: SpeedShoes(player, sonic, monitorsBroken); break;
            case ShieldItem: Shield(o, player, monitorsBroken); break;
            case Invincible: Invincibility(o, player, monitorsBroken); break;
            case Teleport: throw new UnportedException(0x159EA, "teleport monitor");
            default: Increment(monitorsBroken); break;
        }
    }

    private void Increment(int wordVar) => Ram.W16(wordVar, Ram.U16(wordVar) + 1);

    /// <summary>0x15882 (Sonic) / 0x15898 (Tails): +1 life and the 1-up jingle.</summary>
    private void ExtraLife(bool sonic)
    {
        // (The original counts the monitor again here.)
        Increment(sonic ? Vars.MonitorsBroken : Vars.MonitorsBrokenP2);
        int lives = sonic ? Vars.LifeCount : Vars.LifeCountP2, update = sonic ? Vars.UpdateHudLives : Vars.UpdateHudLivesP2;
        Ram.W8(lives, Ram.U8(lives) + 1);
        Ram.W8(update, Ram.U8(update) + 1);
        Ctx.Sound.PlayMusic(MusicOneUp);
    }

    /// <summary>0x158AE: +10 rings (capped at 999); reaching 100 and 200 rings gives a life, once each.</summary>
    private void Rings(bool sonic, int monitorsBroken)
    {
        Increment(monitorsBroken);
        int rings = sonic ? Vars.RingCount : Vars.RingCountP2;
        int update = sonic ? Vars.UpdateHudRings : Vars.UpdateHudRingsP2;
        int lifeFlags = sonic ? Vars.ExtraLifeFlags : Vars.ExtraLifeFlagsP2;
        int collected = sonic ? Vars.RingsCollected : Vars.RingsCollectedP2;
        Ram.W16(collected, Math.Min(Ram.U16(collected) + 10, 999));
        Ram.W16(rings, Math.Min(Ram.U16(rings) + 10, 999));
        Ram.W8(update, Ram.U8(update) | 1);
        if (Ram.U16(rings) >= 100)
        {
            if (!Ram.TestSet(lifeFlags, 1)) { ExtraLife(sonic); return; }
            if (Ram.U16(rings) >= 200 && !Ram.TestSet(lifeFlags, 2)) { ExtraLife(sonic); return; }
        }
        Ctx.Sound.PlayMusic(SfxRing);
    }

    /// <summary>0x15924.</summary>
    private void SpeedShoes(SonicObject player, bool sonic, int monitorsBroken)
    {
        Increment(monitorsBroken);
        player.HasSpeedShoes = true;
        player.SpeedShoesTime = 0x4B0;
        bool mainCharacter = sonic && Ram.U16(Vars.PlayerMode) != 2;
        int topSpeed = mainCharacter ? Vars.SonicTopSpeed : Vars.TailsTopSpeed;
        Ram.W16(topSpeed, 0xC00);
        Ram.W16(topSpeed + 2, 0x18);   // acceleration
        Ram.W16(topSpeed + 4, 0x80);   // deceleration
        Ctx.Sound.PlayMusic(MusicSpeedShoes);
    }

    /// <summary>0x15970: the shield object goes in a fixed slot.</summary>
    private void Shield(SonicObject o, SonicObject player, int monitorsBroken)
    {
        Increment(monitorsBroken);
        player.HasShield = true;
        Ctx.Sound.PlayMusic(SfxShield);
        var shield = new SonicObject(Ram, BrokenByTails(o) ? Vars.TailsShield : Vars.SonicShield);
        shield.Id = ShieldId;
        shield.SetWord(0x3E, player.Address);
    }

    /// <summary>0x159A0: invincibility (not while super); its music unless the boss's is on or the player is drowning.</summary>
    private void Invincibility(SonicObject o, SonicObject player, int monitorsBroken)
    {
        Increment(monitorsBroken);
        if (Ram.U8(Vars.SuperSonicFlag) != 0) return;
        player.Invincible = true;
        player.InvincibilityTime = 0x4B0;
        if (Ram.U8(Vars.CurrentBossId) == 0 && player.AirLeft > 0x0C) Ctx.Sound.PlayMusic(MusicInvincibility);
        var stars = new SonicObject(Ram, BrokenByTails(o) ? Vars.TailsInvincibilityStars : Vars.SonicInvincibilityStars);
        stars.Id = StarsId;
        stars.SetWord(0x3E, player.Address);
    }

    /// <summary>$3F, the low byte of the breaker's slot: nonzero for Tails ($B040).</summary>
    private static bool BrokenByTails(SonicObject o) => o.Byte(0x3F) != 0;
}

/// <summary>
/// An explosion, object $27 (ROM 0x25096). Started at <see cref="Init"/> (destroyed enemies), it first frees an
/// animal; monitors start it at <see cref="FromMonitor"/>. Five frames (the first lasts 4 frames, the others 8), then
/// it is deleted.
/// </summary>
public sealed class Explosion(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x25174;
    private const int Init = 0, Animating = 4;
    public const int FromMonitor = 2;
    private const int SfxExplosion = 0xC1;

    public override void Run(SonicObject o)
    {
        switch (o.Routine)
        {
            case Init:
                o.Routine = FromMonitor;
                if (Spawn() is { } animal)
                {
                    animal.Id = Animal.Id;
                    animal.X = o.X;
                    animal.Y = o.Y;
                    animal.SetWord(0x3E, o.Word(0x3E));   // the points the enemy was worth
                }
                goto case FromMonitor;
            case FromMonitor:
                o.Routine = Animating;
                o.Mappings = Mappings;
                o.ArtTile = 0x5A4;
                o.RenderFlags = Render.LevelCoords;
                o.Priority = 1;
                o.CollisionFlags = 0;
                o.WidthPixels = 0x0C;
                o.AnimFrameDuration = 3;
                o.MappingFrame = 0;
                Ctx.Sound.PlaySound(SfxExplosion);
                goto case Animating;
            case Animating:
                o.AnimFrameDuration--;
                if ((sbyte)o.AnimFrameDuration < 0)
                {
                    o.AnimFrameDuration = 7;
                    o.MappingFrame++;
                    if (o.MappingFrame == 5) { Delete(o); return; }
                }
                Display(o);
                break;
        }
    }
}
