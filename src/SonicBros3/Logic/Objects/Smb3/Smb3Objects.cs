using S2Port.Extraction.Smb3;

namespace SonicBros3.Logic;

/// <summary>
/// The SMB3 World 1-1 Easter egg's objects (zone $40, see S2Port.Extraction.Smb3). Their sprites come from the player's
/// smb3.nes (objects/smb3-*); their behaviour follows SMB3's (speeds, hops, the piranhas' timing) translated to Sonic
/// rules: jumping or rolling into an enemy destroys it like a badnik, otherwise it hurts. Not co-simulated: there is no
/// Sonic 2 original to compare with. Object ids $E0-$E6 (unused by Sonic 2 and the hack).
/// </summary>
public static class Smb3
{
    public const int Block = 0xE0, Goomba = 0xE1, Paragoomba = 0xE2, Koopa = 0xE3, Paratroopa = 0xE4, Piranha = 0xE5,
        Fireball = 0xE6, GoombaPipe = 0xE8, MicroGoomba = 0xE9, BoomerangBro = 0xEA, Boomerang = 0xEB;

    /// <summary>The art sets in Smb3ObjectArt's order: their made-up mapping pointers.</summary>
    public const uint ArtBlock = 0xF00000, ArtGoomba = 0xF01000, ArtParagoomba = 0xF02000, ArtKoopaGreen = 0xF03000,
        ArtKoopaRed = 0xF04000, ArtParatroopa = 0xF05000, ArtPiranha = 0xF06000, ArtVenus = 0xF07000, ArtFireball = 0xF08000,
        ArtMicroGoomba = 0xF09000, ArtPiranhaRed = 0xF0A000, ArtPiranhaFire = 0xF0B000,
        ArtBoomerangBro = 0xF0C000, ArtBoomerang = 0xF0D000;

    /// <summary>VRAM tile of the object art (Smb3ObjectArt.VramTile): line 2 for blocks, line 3 for sprites.</summary>
    public const int ArtTileBlocks = 0x4340, ArtTileSprites = 0x6340;

    public static bool IsEnemy(int id) => id is >= Goomba and <= Piranha or BoomerangBro;
    public const int StompCooldown = 0x3A;

    public static (int Width, int Height, int Y) ContactBounds(SonicObject o)
    {
        if (o.Id == Piranha)
        {
            int direction = (o.Subtype & Smb3Plant.Ceiling) == 0 ? 1 : -1;
            int height = Smb3Plant.IsRed(o.Subtype) ? 48 : 32;
            int anchor = o.Word(0x30);
            int exposed = Math.Clamp(height - (short)(o.Y - anchor) * direction, 0, height);
            return (8, exposed / 2, anchor - direction * ((exposed + 1) / 2));
        }
        if (o.Id is Koopa or Paratroopa) return (8, o.YRadius, o.Y);
        if (o.Id == BoomerangBro) return (8, 16, o.Y - 8);
        return (8, 8, o.Y);
    }

    public static void Init(Ram ram, SonicObject o, uint mappings, int artTile, int width, int collision)
    {
        o.Routine = 2;
        o.Mappings = mappings;
        o.ArtTile = artTile;
        o.RenderFlags = Render.LevelCoords;
        o.Priority = 4;
        o.WidthPixels = width;
        o.CollisionFlags = collision;
    }

    /// <summary>Face Sonic (SMB3 enemies start walking towards the player): status bit 0 = facing right.</summary>
    public static void FaceSonic(Ram ram, SonicObject o)
    {
        if (ram.Sonic.X >= o.X) o.Status |= FacingRight; else o.Status &= ~FacingRight;
    }

    /// <summary>Status bit 0 for these objects: facing (and walking) right.</summary>
    public const int FacingRight = ObjStatus.XFlip;
    public static bool IsFacingRight(SonicObject o) => (o.Status & FacingRight) != 0;

    /// <summary>$30, for every SMB3 object: a frame counter driving its animation.</summary>
    public static int Tick(SonicObject o)
    {
        if (o.Byte(StompCooldown) != 0) o.SetByte(StompCooldown, o.Byte(StompCooldown) - 1);
        int t = o.Byte(0x30) + 1;   // (256 on the frame it wraps, as before)
        o.SetByte(0x30, t);
        return t;
    }

    /// <summary>The art faces left; flip it when facing right.</summary>
    public static void SetFlip(SonicObject o) => o.RenderFlags = (o.RenderFlags & ~3) | (o.Status & FacingRight);

    /// <summary>Fall, land on the floor; walk at <paramref name="speed"/>, turn at walls (and at ledges if asked).</summary>
    public static bool Walk(Ram ram, Collision col, SonicObject o, int speed, bool turnAtLedges)
    {
        o.XVel = (short)(IsFacingRight(o) ? speed : -speed);
        int previousFeet = o.Y + o.YRadius;
        ObjectKit.MoveAndFall(o);
        bool onFloor = false;
        if (o.YVel >= 0)
        {
            int d = (short)ObjectKit.CheckFloorDist(ram, col, o);
            if (d <= 0)
            {
                o.Y += d;
                o.YVel = 0;
                onFloor = true;
            }
            if (BlockFloor(ram, o.X, previousFeet) is { } top && o.Y + o.YRadius >= top - 1)
            {
                o.Y = top - 1 - o.YRadius;
                o.YVel = 0;
                onFloor = true;
            }
        }
        int wall = IsFacingRight(o) ? ObjectKit.CheckRightWall(ram, col, o, 8) : ObjectKit.CheckLeftWall(ram, col, o, -8);
        if ((short)wall < 0)
        {
            o.Status ^= FacingRight;
            o.X += IsFacingRight(o) ? 1 : -1;
        }
        else if (turnAtLedges && onFloor)
        {
            int ahead = (o.X + (IsFacingRight(o) ? 8 : -8)) & 0xFFFF;
            var (dist, _) = ObjectKit.CheckFloor(ram, col, o, ahead);
            if (BlockFloor(ram, ahead, o.Y + o.YRadius) is { } top)
                dist = Math.Min(dist, top - 1 - (o.Y + o.YRadius));
            if ((short)dist >= 8) o.Status ^= FacingRight;
        }
        return onFloor;
    }

    private static int? BlockFloor(Ram ram, int x, int minimumY)
    {
        int? nearest = null;
        for (int slot = Objects.FirstDynamicSlot; slot < Vars.DynamicObjectRamEnd; slot += Objects.SlotSize)
        {
            if (ram.U8(slot) != Block) continue;
            var block = new SonicObject(ram, slot);
            int top = block.Y - 8;
            if (Math.Abs((short)(block.X - x)) >= 8 || top < minimumY) continue;
            if (nearest is null || top < nearest.Value) nearest = top;
        }
        return nearest;
    }

    public static bool Overlaps(SonicObject a, SonicObject b, int w, int h) =>
        Math.Abs((short)(a.X - b.X)) < w && Math.Abs((short)(a.Y - b.Y)) < h;
}

/// <summary>
/// $E0: a ? block or brick (subtype = the SMB3 tile: $60-$65 ? blocks, $67-$70 bricks). Solid. Hit from below it
/// bumps and gives its contents, then turns into a used block: a coin is a ring; the power-ups become Sonic's (fire
/// flower or leaf = shield, star = invincibility, 1-up = extra life); a 10-coin brick gives a ring per hit, up to 10.
/// A plain brick breaks when hit from below or rolled into.
/// </summary>
public sealed class Smb3Block(ObjectContext ctx) : GameObject(ctx)
{
    private const int FrameUsed = 8;
    private const int PlainBrick = 0x67, TenCoinBrick = 0x6D, OneUp = 0x6E;
    private const int MonitorShield = 6, MonitorInvincibility = 7, MonitorSonicLife = 1;
    private const int SfxBump = 0xA3, SfxBreak = 0xCB;

    /// <summary>The block's own variables.</summary>
    private readonly struct State(SonicObject o)
    {
        /// <summary>$30: the resting y.</summary>
        public int RestY { get => o.Word(0x30); set => o.SetWord(0x30, value); }
        /// <summary>$32: frames left of the bump.</summary>
        public int Bump { get => o.Byte(0x32); set => o.SetByte(0x32, value); }
        /// <summary>$33: coins left in a 10-coin brick.</summary>
        public int CoinsLeft { get => o.Byte(0x33); set => o.SetByte(0x33, value); }
        /// <summary>$34: used up.</summary>
        public bool Used { get => o.Byte(0x34) != 0; set => o.SetByte(0x34, value ? 1 : 0); }
    }

    public override void Run(SonicObject o)
    {
        var s = new State(o);
        if (o.Routine == 0)
        {
            Smb3.Init(Ram, o, Smb3.ArtBlock, Smb3.ArtTileBlocks, 8, 0);
            s.RestY = o.Y;
            s.Bump = 0;
            s.CoinsLeft = o.Subtype == TenCoinBrick ? 10 : 0;
        }
        int tile = o.Subtype;
        bool brick = tile is >= 0x67 and <= 0x70;
        if (!s.Used) o.MappingFrame = (brick ? 4 : 0) + ((Ram.U16(Vars.LevelFrameCounter) >> 3) & 3);
        else o.MappingFrame = FrameUsed;

        // The bump: up 4 px and back over 8 frames.
        int bump = s.Bump;
        if (bump != 0)
        {
            s.Bump = bump - 1;
            o.Y = s.RestY - (bump > 4 ? 8 - bump : bump);
        }
        else o.Y = s.RestY;

        bool BallTouches(SonicObject p) => p.Routine < 4 && p.Rolling &&
            Math.Abs((short)(p.X - o.X)) < p.XRadius + 8 &&
            Math.Abs((short)(p.Y - o.Y)) < p.YRadius + 8;
        bool ballContact = tile == PlainBrick && !s.Used && bump == 0 && (BallTouches(Sonic) || BallTouches(Tails));
        if (ballContact) { Break(o); return; }
        var contact = Ctx.Solid.SolidObject(o, 0x1B, 8, 9, o.X);
        // SolidObject's d6: bits 18/19 = Sonic/Tails hit it from below, bit 16 = Sonic pushing its side.
        bool hitBelow = (contact.D6 & ((1 << 18) | (1 << 19))) != 0;
        bool rolledInto = tile == PlainBrick && (contact.D6 & (1 << 16)) != 0 && Sonic.Anim == 2;
        if ((hitBelow || rolledInto) && !s.Used && bump == 0)
        {
            bool tailsOnly = (contact.D6 & (1 << 19)) != 0 && (contact.D6 & (1 << 18)) == 0;
            var player = tailsOnly ? Tails : Sonic;
            if (tile == PlainBrick) { Break(o); return; }
            s.Bump = 8;
            Ctx.Sound.PlaySound(SfxBump);
            Give(o, s, player, tile);
        }
        DisplayOrMarkGone(o);
    }

    private void Give(SonicObject o, State s, SonicObject player, int tile)
    {
        switch (tile)
        {
            case 0x63 or 0x65 or 0x6B or 0x64 or 0x6C:   // coin
                RingPop(o);
                s.Used = true;
                break;
            case TenCoinBrick:
                RingPop(o);
                s.CoinsLeft--;
                if (s.CoinsLeft == 0) s.Used = true;
                break;
            case 0x60 or 0x61 or 0x68 or 0x69:   // fire flower / leaf
                Contents(o, player, MonitorShield);
                s.Used = true;
                break;
            case 0x62 or 0x6A:   // star
                Contents(o, player, MonitorInvincibility);
                s.Used = true;
                break;
            case OneUp:
                Contents(o, player, MonitorSonicLife);
                s.Used = true;
                break;
            default:   // P-switch, vine: no Sonic equivalent
                s.Used = true;
                break;
        }
    }

    private void RingPop(SonicObject o)
    {
        TouchResponse.CollectRingFor(Ram, Ctx.Sound);
        if (Spawn() is { } points)
        {
            points.Id = 0x29;   // a points popup (10 points)
            points.X = o.X;
            points.Y = o.Y - 0x10;
            points.MappingFrame = 0;
        }
    }

    /// <summary>The rising monitor icon (object $2E) gives the reward as a monitor would.</summary>
    private void Contents(SonicObject o, SonicObject player, int monitorItem)
    {
        if (Spawn() is not { } c) return;
        c.Id = 0x2E;
        c.X = o.X;
        c.Y = o.Y - 0x10;
        c.Anim = monitorItem;
        c.SetWord(Monitor.BrokenBy, player.Address);
    }

    private void Break(SonicObject o)
    {
        Ctx.Sound.PlaySound(SfxBreak);
        Ctx.Touch.AddPoints(5);
        int entry = ObjectKit.RespawnEntry(Ram, o);
        if (o.RespawnIndex != 0) Ram.W8(entry, Ram.U8(entry) | 0x80);   // stays broken
        // Release a player standing on it.
        if ((o.Status & ObjStatus.StandingP1) != 0) Sonic.Status = (Sonic.Status & ~8) | 2;
        if ((o.Status & ObjStatus.StandingP2) != 0) Tails.Status = (Tails.Status & ~8) | 2;
        if (Spawn() is { } e) { e.Id = 0x27; e.Routine = Explosion.FromMonitor; e.X = o.X; e.Y = o.Y; }
        Delete(o);
    }
}

/// <summary>
/// $E1 Goomba / $E2 Paragoomba: walks at SMB3's half a pixel a frame, towards Sonic at first, turning at walls. The
/// Paragoomba hops (three small hops, then a big one); hit once, it loses its wings and walks on as a Goomba.
/// Paragoomba subtype 1 is 1-2's micro goomba dropper (OBJ_PARAGOOMBAWITHMICROS): it flies over Sonic instead
/// (Paragoomba_FlyAbovePlayer / DropMicrogoombas: up to $38 above him, then a micro goomba every $20 frames for $80
/// frames, bobbing; x speed towards him, accelerating 1/16 px a frame to 1.25 px). A Goomba from a Goomba pipe
/// (<see cref="Smb3GoombaPipe"/>) first walks out of the pipe for <see cref="Emerge"/> frames (SMB3's Var1 = $28).
/// </summary>
public sealed class Smb3Goomba(ObjectContext ctx) : GameObject(ctx)
{
    /// <summary>$31: the Paragoomba's hop count (0-3; the fourth hop is big).</summary>
    private const int Hops = 0x31;
    /// <summary>$33: frames left walking out of a Goomba pipe (no gravity, behind the pipe).</summary>
    public const int Emerge = 0x33;
    /// <summary>$34/$35 (flyer): drop phase timer, the drop counter.</summary>
    private const int FlyTimer = 0x34, DropCount = 0x35;

    public override void Run(SonicObject o)
    {
        bool winged = o.Id == Smb3.Paragoomba;
        if (winged && o.Subtype == 1 && (o.Status & ObjStatus.Destroyed) == 0) { Fly(o); return; }
        if (!winged && o.Byte(Emerge) != 0)
        {
            if (o.Routine == 0) { Smb3.Init(Ram, o, Smb3.ArtGoomba, Smb3.ArtTileSprites, 8, 0); o.YRadius = 8; o.XRadius = 8; }
            o.SetByte(Emerge, o.Byte(Emerge) - 1);
            o.XVel = (short)(Smb3.IsFacingRight(o) ? 0x80 : -0x80);
            ObjectKit.Move(o);
            o.MappingFrame = (Smb3.Tick(o) >> 3) & 1;
            if (o.Byte(Emerge) == 0) o.CollisionFlags = 0x0B;
            Smb3.SetFlip(o);
            DisplayOrMarkGone(o);
            return;
        }
        if (o.Routine == 0)
        {
            Smb3.Init(Ram, o, winged ? Smb3.ArtParagoomba : Smb3.ArtGoomba, Smb3.ArtTileSprites, 8, 0x0B);
            o.YRadius = 8;
            o.XRadius = 8;
            if (winged) o.CollisionProperty = 1;   // takes a hit before it is destroyed
            Smb3.FaceSonic(Ram, o);
        }
        if (winged && (o.Status & ObjStatus.Destroyed) != 0)
        {
            // Wings knocked off: carry on as a plain Goomba (the slot keeps its respawn entry).
            o.Id = Smb3.Goomba;
            o.Status &= ~ObjStatus.Destroyed;
            o.Mappings = Smb3.ArtGoomba;
            o.CollisionFlags = 0x0B;
        }
        bool onFloor = Smb3.Walk(Ram, Ctx.Collision, o, 0x80, false);
        int tick = Smb3.Tick(o);
        if (o.Id == Smb3.Paragoomba)
        {
            if (onFloor)
            {
                int hops = o.Byte(Hops);
                o.YVel = (short)(hops == 3 ? -0x500 : -0x200);
                o.SetByte(Hops, (hops + 1) & 3);
                Smb3.FaceSonic(Ram, o);
            }
            o.MappingFrame = 3 + ((tick >> 3) & 1);
        }
        else o.MappingFrame = (tick >> 3) & 1;
        if (o.CollisionFlags == 0) o.CollisionFlags = 0x0B;   // a bounce-off cleared it
        Smb3.SetFlip(o);
        DisplayOrMarkGone(o);
    }
    /// <summary>The micro goomba dropper's flight: rise to $38 above Sonic, then $80 frames bobbing and dropping.</summary>
    private void Fly(SonicObject o)
    {
        if (o.Routine == 0)
        {
            Smb3.Init(Ram, o, Smb3.ArtParagoomba, Smb3.ArtTileSprites, 8, 0x0B);
            o.YRadius = 8;
            o.XRadius = 8;
            o.CollisionProperty = 1;
            Smb3.FaceSonic(Ram, o);
            o.RoutineSecondary = 0;
        }
        int tick = Smb3.Tick(o);
        if (o.RoutineSecondary == 0)
        {
            // Paragoomba_FlyAbovePlayer: up 1 px a frame until $38 above Sonic.
            if ((short)(Sonic.Y - o.Y) >= 0x38) { o.RoutineSecondary = 2; o.SetByte(FlyTimer, 0x80); o.YVel = 0; }
            else o.YVel = -0x100;
        }
        else
        {
            // Paragoomba_DropMicrogoombas: a micro goomba every $20 frames, bobbing 16 frames each way.
            int t = o.Byte(FlyTimer) - 1;
            o.SetByte(FlyTimer, t);
            if (t <= 0) o.RoutineSecondary = 0;
            int drops = (o.Byte(DropCount) + 1) & 0xFF;
            o.SetByte(DropCount, drops);
            if ((drops & 0x1F) == 0 && Spawn() is { } m)
            {
                m.Id = Smb3.MicroGoomba;
                m.X = o.X + 4;
                m.Y = o.Y + 4;
            }
            o.YVel = (short)((drops & 0x10) == 0 ? 0x80 : -0x80);
        }
        // X: accelerate 1/16 px a frame towards the way it faces, to 1.25 px; face Sonic every 64 frames.
        int limit = Smb3.IsFacingRight(o) ? 0x140 : -0x140;
        if (o.XVel != limit) o.XVel = (short)(o.XVel + (limit > 0 ? 0x10 : -0x10));
        if ((tick & 0x3F) == 0) Smb3.FaceSonic(Ram, o);
        ObjectKit.Move(o);
        o.MappingFrame = 3 + ((tick >> 2) & 1);
        if (o.CollisionFlags == 0) o.CollisionFlags = 0x0B;
        Smb3.SetFlip(o);
        DisplayOrMarkGone(o);
    }
}

/// <summary>
/// $E3 Koopa Troopa (subtype 0 green, 1 red: the red one turns at ledges) and $E4 the hopping green Paratroopa. A
/// Paratroopa hit once becomes a green Koopa; a Koopa hit once becomes a shell. Touching a still shell kicks it. A
/// sliding shell (4 px a frame) bounces off walls, knocks out other SMB3 enemies, hurts Sonic unless he attacks it,
/// and is destroyed when he does.
/// </summary>
public sealed class Smb3Koopa(ObjectContext ctx) : GameObject(ctx)
{
    private const int Walking = 0, Shell = 2, Sliding = 4;
    private const int SfxKick = 0xAC;
    /// <summary>$32: frames before the shell can be kicked (still), or before it hurts (sliding).</summary>
    private const int Cooldown = 0x32;

    public override void Run(SonicObject o)
    {
        bool winged = o.Id == Smb3.Paratroopa;
        bool red = !winged && o.Subtype != 0;
        if (o.Routine == 0)
        {
            Smb3.Init(Ram, o, winged ? Smb3.ArtParatroopa : red ? Smb3.ArtKoopaRed : Smb3.ArtKoopaGreen, Smb3.ArtTileSprites, 8, 0x0B);
            o.YRadius = 0x10;
            o.XRadius = 8;
            o.CollisionProperty = 1;
            Smb3.FaceSonic(Ram, o);
        }
        int tick = Smb3.Tick(o);
        switch (o.RoutineSecondary)
        {
            case Walking:
                if ((o.Status & ObjStatus.Destroyed) != 0)
                {
                    o.Status &= ~ObjStatus.Destroyed;
                    if (winged)
                    {
                        // Wings knocked off.
                        o.Id = Smb3.Koopa;
                        o.Subtype = 0;
                        o.Mappings = Smb3.ArtKoopaGreen;
                        o.CollisionProperty = 1;
                        o.CollisionFlags = 0x0B;
                        break;
                    }
                    ToShell(o);
                    break;
                }
                bool onFloor = Smb3.Walk(Ram, Ctx.Collision, o, 0x80, red);
                if (winged)
                {
                    if (onFloor) o.YVel = -0x380;
                    else o.YVel -= 0x10;   // the hopper's slight float
                    o.MappingFrame = 2 + ((tick >> 2) & 1);
                }
                else o.MappingFrame = (tick >> 3) & 1;
                if (o.CollisionFlags == 0) o.CollisionFlags = 0x0B;
                break;
            case Shell:
                Smb3.Walk(Ram, Ctx.Collision, o, 0, false);
                if (o.Byte(Cooldown) != 0) o.SetByte(Cooldown, o.Byte(Cooldown) - 1);
                else if (Smb3.Overlaps(o, Sonic, 0x14, 0x18)) Kick(o, Sonic);
                break;
            case Sliding:
                if ((o.Status & ObjStatus.Destroyed) != 0)
                {
                    o.Status &= ~ObjStatus.Destroyed;
                    ToShell(o);
                    break;
                }
                if (o.Byte(Cooldown) != 0)
                {
                    o.SetByte(Cooldown, o.Byte(Cooldown) - 1);
                    if (o.Byte(Cooldown) == 0) o.CollisionFlags = 0x0B;
                }
                Smb3.Walk(Ram, Ctx.Collision, o, 0x400, false);
                KnockOutOthers(o);
                break;
        }
        Smb3.SetFlip(o);
        DisplayOrMarkGone(o);
    }

    private static int ShellFrame(SonicObject o) => o.Id == Smb3.Paratroopa ? 4 : 2;

    private static void ToShell(SonicObject o)
    {
        o.RoutineSecondary = Shell;
        o.CollisionFlags = 0x0B;
        o.SetByte(Cooldown, 16);   // Sonic bounces off first; no kick for a moment
        o.XVel = 0;
        o.Y += o.YRadius - 8;
        o.YRadius = 8;
        o.MappingFrame = ShellFrame(o);
    }

    private void Kick(SonicObject o, SonicObject sonic)
    {
        o.RoutineSecondary = Sliding;
        if (sonic.X < o.X) o.Status |= Smb3.FacingRight;   // away from Sonic
        else o.Status &= ~Smb3.FacingRight;
        o.SetByte(Cooldown, 12);   // harmless while it leaves Sonic
        Ctx.Sound.PlaySound(SfxKick);
    }

    private void KnockOutOthers(SonicObject o)
    {
        for (int a = Objects.FirstDynamicSlot; a < Vars.TailsTails; a += Objects.SlotSize)
        {
            if (a == o.Address || !Smb3.IsEnemy(Ram.U8(a))) continue;
            var other = new SonicObject(Ram, a);
            if (!Smb3.Overlaps(o, other, 0x10, 0x14)) continue;
            Ctx.Touch.AddPoints(10);
            other.Id = 0x27;   // an explosion (with an animal)
            other.Routine = 0;
            other.CollisionFlags = 0;
        }
    }
}

/// <summary>
/// $E5 piranha plants (Smb3Plant subtypes preserve colour, fire capability and ceiling orientation). Placed at the pipe's top
/// centre. It rises 32 px, waits (the fire types spit a fireball at Sonic halfway), sinks and waits hidden; it stays
/// in the pipe while Sonic is within 27 px (SMB3's Level_ObjCalcXDiffs + $1B check). The pipes are drawn in front
/// (high-priority blocks), so the plant appears from inside.
/// </summary>
public sealed class Smb3Piranha(ObjectContext ctx) : GameObject(ctx)
{
    private const int Rising = 0, Out = 2, Sinking = 4, Hidden = 6;

    /// <summary>The plant's own variables.</summary>
    private readonly struct State(SonicObject o)
    {
        /// <summary>$30: the pipe's top.</summary>
        public int PipeTop { get => o.Word(0x30); set => o.SetWord(0x30, value); }
        /// <summary>$34: frames left in the current phase.</summary>
        public int Timer { get => o.Word(0x34); set => o.SetWord(0x34, value); }
    }

    public override void Run(SonicObject o)
    {
        var s = new State(o);
        int kind = o.Subtype & 3;
        bool shoots = Smb3Plant.Shoots(kind);
        bool red = Smb3Plant.IsRed(kind);
        int direction = (o.Subtype & Smb3Plant.Ceiling) == 0 ? 1 : -1;
        int height = red ? 48 : 32;
        if (o.Routine == 0)
        {
            uint art = kind switch
            {
                Smb3Plant.Green => Smb3.ArtPiranha,
                Smb3Plant.GreenFire => Smb3.ArtPiranhaFire,
                Smb3Plant.RedFire => Smb3.ArtVenus,
                _ => Smb3.ArtPiranhaRed,
            };
            Smb3.Init(Ram, o, art, Smb3.ArtTileSprites & 0x7FFF, 8, 0x0B);
            o.X += 8;
            o.Y -= 8;
            s.PipeTop = o.Y;
            o.Y += height * direction;
            o.RoutineSecondary = Hidden;
            s.Timer = 0x40;
        }
        int timer = s.Timer - 1;
        s.Timer = Math.Max(timer, 0);
        switch (o.RoutineSecondary)
        {
            case Rising:
                o.Y -= direction;
                if ((short)(o.Y - s.PipeTop) * direction <= 0) { o.RoutineSecondary = Out; s.Timer = shoots ? 0x60 : 0x30; }
                break;
            case Out:
                if (shoots && timer == 0x30) Spit(o, s, Sonic, direction, height);
                if (timer <= 0) o.RoutineSecondary = Sinking;
                break;
            case Sinking:
                o.Y += direction;
                if ((short)(o.Y - s.PipeTop) * direction >= height) { o.RoutineSecondary = Hidden; s.Timer = 0x60; }
                break;
            case Hidden:
                bool sonicNear = Math.Abs((short)(Sonic.X - o.X)) + 0x1B < 0x37;
                if (timer <= 0 && !sonicNear) o.RoutineSecondary = Rising;
                break;
        }
        // Only touchable when out of the pipe.
        o.CollisionFlags = (short)(o.Y - s.PipeTop) * direction < height - 8 ? 0x0B : 0;
        o.MappingFrame = (Ram.U16(Vars.LevelFrameCounter) >> 3) & 1;
        if (shoots) Smb3.FaceSonic(Ram, o);
        else o.Status &= ~Smb3.FacingRight;
        Smb3.SetFlip(o);
        if (direction < 0) o.RenderFlags |= 2;
        DisplayOrMarkGone(o);
    }

    /// <summary>Piranha_SpitFire: aimed at Sonic, steep or shallow (PiranhaFireball_X/YVel $0B/$05, $0B/$0E in 1/16 px).</summary>
    private void Spit(SonicObject o, State s, SonicObject sonic, int direction, int height)
    {
        if (Spawn() is not { } f) return;
        f.Id = Smb3.Fireball;
        f.X = o.X;
        f.Y = s.PipeTop - (height - 8) * direction;
        int dx = (short)(sonic.X - o.X), dy = (short)(sonic.Y - f.Y);
        bool steep = Math.Abs(dy) > Math.Abs(dx) / 2;
        f.XVel = (short)((steep ? 0xB0 : 0xE0) * (dx < 0 ? -1 : 1));
        f.YVel = (short)((steep ? 0xB0 : 0x50) * (dy < 0 ? -1 : 1));
    }
}

/// <summary>$E6: a piranha's fireball: flies straight, spinning; hurts and cannot be destroyed; gone off screen.</summary>
public sealed class Smb3Fireball(ObjectContext ctx) : GameObject(ctx)
{
    public override void Run(SonicObject o)
    {
        if (o.Routine == 0) Smb3.Init(Ram, o, Smb3.ArtFireball, Smb3.ArtTileSprites, 4, 0x80 | 0x0B);
        ObjectKit.Move(o);
        int tick = Smb3.Tick(o);
        o.MappingFrame = (tick >> 2) & 3;
        if (tick > 30 && !o.OnScreen) { Delete(o); return; }
        Display(o);
    }
}


/// <summary>
/// $E8: a Goomba pipe (OBJ_CFIRE_GOOMBAPIPE_R, CFire_GoombaPipe prg007): every $70 frames, three times in four, a
/// Goomba walks out of the pipe to the right, if Sonic is on that side (SMB3 removes it otherwise). The Goomba starts
/// 3 pixels above the pipe's position, faces right and walks out for $28 frames. Invisible; the pipe is level art.
/// </summary>
public sealed class Smb3GoombaPipe(ObjectContext ctx) : GameObject(ctx)
{
    private const int Timer = 0x30, Count = 0x32;

    public override void Run(SonicObject o)
    {
        if (o.Routine == 0)
        {
            o.Routine = 2;
            o.SetWord(Timer, 0);
        }
        int t = o.Word(Timer);
        if (t != 0) o.SetWord(Timer, t - 1);
        else
        {
            o.SetWord(Timer, 0x70);
            int count = (o.Byte(Count) + 1) & 0xFF;
            o.SetByte(Count, count);
            if ((count & 3) != 0 && Sonic.X >= o.X && Spawn() is { } g)
            {
                g.Id = Smb3.Goomba;
                g.X = o.X;
                g.Y = o.Y - 3;
                g.Status |= Smb3.FacingRight;
                g.SetByte(Smb3Goomba.Emerge, 0x28);
            }
        }
        MarkObjGone(o);
    }
}

/// <summary>
/// $E9: a micro goomba (SObj_Microgoomba, prg007), dropped by 1-2's flying Paragoomba. It drifts down swaying (x speed
/// swinging between +/-1 px a frame by 1/16 px, falling up to 1 px a frame) until it touches Sonic. Rolling or
/// invincible, he knocks it out like a badnik; otherwise it sticks to him (SMB3's Player_UphillSpeedIdx: he is
/// slowed, here his ground speed held to 3 px a frame) and bobs on him. Its grip grows every 16 frames up to 5; each
/// button press loosens it by one, and at 0 (or once he is invincible or hurt) it flops off.
/// </summary>
public sealed class Smb3MicroGoomba(ObjectContext ctx) : GameObject(ctx)
{
    private const int Falling = 2, Stuck = 4, Flopping = 6;
    private const int Grip = 0x32, Clock = 0x33;
    private const int SlowSpeed = 0x300;

    public override void Run(SonicObject o)
    {
        if (o.Routine == 0)
        {
            Smb3.Init(Ram, o, Smb3.ArtMicroGoomba, Smb3.ArtTileSprites, 4, 0);
            o.Routine = Falling;
            o.Priority = 3;
        }
        int clock = (o.Byte(Clock) + 1) & 0xFF;
        o.SetByte(Clock, clock);
        o.MappingFrame = (Ram.U16(Vars.LevelFrameCounter) >> 3) & 1;
        switch (o.Routine)
        {
            case Falling:
            {
                // Swaying: accelerate 1/16 px a frame one way until +/-1 px, then the other (Microgoomba_XAccel).
                int dir = (clock & 0x20) == 0 ? 1 : -1;
                o.XVel = (short)Math.Clamp(o.XVel + dir * 0x10, -0x100, 0x100);
                if (o.YVel < 0x100) o.YVel += 0x10;
                ObjectKit.Move(o);
                if (Smb3.Overlaps(o, Sonic, 4 + 9, 8 + Sonic.YRadius) && Sonic.Routine < 4)
                {
                    bool attacking = Sonic.Invincible || Sonic.Anim is PlayerAnim.Roll or PlayerAnim.Spindash;
                    if (attacking)
                    {
                        Ctx.Touch.AddPoints(10);
                        o.Id = 0x27;   // the explosion (with an animal)
                        o.Routine = 0;
                        return;
                    }
                    o.Routine = Stuck;
                    o.SetByte(Grip, 1);
                    Ctx.Sound.PlaySound(Sfx.Hurt);
                }
                if (o.Y > Ram.U16(Vars.CameraYPos) + 0x100) { Delete(o); return; }
                Display(o);
                return;
            }
            case Stuck:
            {
                if (Sonic.Invincible || Sonic.Routine >= 4) { FlopOff(o); return; }
                int grip = o.Byte(Grip);
                if ((Ram.U16(Vars.LevelFrameCounter) & 0x0F) == 0 && grip < 5) grip++;
                if ((Ram.U8(Vars.Ctrl1Press) & 0x70) != 0) grip--;
                o.SetByte(Grip, grip);
                if (grip <= 0) { FlopOff(o); return; }
                // Slowed down while it clings on.
                if (Sonic.Inertia > SlowSpeed) Sonic.Inertia = SlowSpeed;
                if (Sonic.Inertia < -SlowSpeed) Sonic.Inertia = -SlowSpeed;
                if (Sonic.XVel > SlowSpeed) Sonic.XVel = SlowSpeed;
                if (Sonic.XVel < -SlowSpeed) Sonic.XVel = -SlowSpeed;
                // Bobbing on him: 16 frames up, 16 down (SpecialObj_Var1), and side to side.
                int bob = (clock >> 2) & 0x0F;
                if (((clock >> 2) & 0x10) != 0) bob = 0x0F - bob;
                o.X = Sonic.X - 3 + (bob - 8) / 2;
                o.Y = Sonic.Y - 4 + bob / 2;
                Display(o);
                return;
            }
            case Flopping:
                ObjectKit.MoveAndFall(o);
                if (o.Y > Ram.U16(Vars.CameraYPos) + 0x100) { Delete(o); return; }
                Display(o);
                return;
        }
    }

    private void FlopOff(SonicObject o)
    {
        o.Routine = Flopping;
        o.YVel = -0x200;
        o.XVel = (short)((Ram.U16(Vars.LevelFrameCounter) & 1) == 0 ? 0x80 : -0x80);
        o.RenderFlags |= Render.YFlip;
    }
}
