namespace SonicBros3.Logic;

/// <summary>
/// Spikes, object $36 in Sonic 2's zones (ROM 0x18F3E; in the hack's zones $36 is Sonic 1's code, <see cref="Spikes"/>).
///
/// Subtype high nibble: the size (a half width and half height from <see cref="Sizes"/>; it is also the frame).
/// Sizes 4-7 point sideways; a y-flipped placement hangs upside down. Low nibble: 0 still, 1 moves up and down,
/// 2 left and right (retract 32 px, wait 60 frames, extend, wait 60 frames).
/// The spikes are solid; they hurt a player touching their sharp side (top, side or bottom).
/// </summary>
public sealed class SpikesS2(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x191A6;
    private const int Init = 0, Upright = 2, Sideways = 4, UpsideDown = 6;
    private const int Still = 0, UpDown = 1, LeftRight = 2;
    private const int RetractDistance = 0x2000, RetractSpeed = 0x800, WaitFrames = 60;

    /// <summary>Per size: half width, half height (0x18F54).</summary>
    private static readonly (int Width, int Height)[] Sizes =
        [(0x10, 0x10), (0x20, 0x10), (0x30, 0x10), (0x40, 0x10), (0x10, 0x10), (0x10, 0x20), (0x10, 0x30), (0x10, 0x40)];

    /// <summary>The spikes' own variables.</summary>
    private readonly struct State(SonicObject o)
    {
        /// <summary>$30, $32: where they were placed.</summary>
        public int BaseX { get => o.Word(0x30); set => o.SetWord(0x30, value); }
        public int BaseY { get => o.Word(0x32); set => o.SetWord(0x32, value); }
        /// <summary>$34: how far retracted (8.8; the high byte is whole pixels).</summary>
        public int Retracted { get => o.Word(0x34); set => o.SetWord(0x34, value); }
        public int RetractedPixels => o.Byte(0x34);
        /// <summary>$36: extending back out; $38: frames left to wait.</summary>
        public bool Extending { get => o.Word(0x36) != 0; set => o.SetWord(0x36, value ? 1 : 0); }
        public int Wait { get => o.Word(0x38); set => o.SetWord(0x38, value); }
    }

    public override void Run(SonicObject o)
    {
        var s = new State(o);
        switch (o.Routine)
        {
            case Init: Initialise(o, s); break;
            case Upright: UpdateUpright(o, s); break;
            case Sideways: UpdateSideways(o, s); break;
            case UpsideDown: UpdateUpsideDown(o, s); break;
        }
    }

    /// <summary>0x18F64.</summary>
    private static void Initialise(SonicObject o, State s)
    {
        o.Routine = Upright;
        o.Mappings = Mappings;
        o.ArtTile = 0x2434;
        o.RenderFlags |= Render.LevelCoords;
        o.Priority = 4;
        int size = o.Subtype >> 4;
        o.Subtype &= 0x0F;
        (o.WidthPixels, o.YRadius) = Sizes[size];
        o.MappingFrame = size;
        if (size >= 4)
        {
            o.Routine = Sideways;
            o.ArtTile = 0x242C;
        }
        if ((o.Status & ObjStatus.YFlip) != 0) o.Routine = UpsideDown;
        s.BaseX = o.X;
        s.BaseY = o.Y;
        // (Adjust2PArtPointer: 1-player, unchanged.)
    }

    private Solid.Result BeSolid(SonicObject o, int previousX)
    {
        int halfHeight = o.Byte(0x16);   // y_radius, unsigned
        return Ctx.Solid.SolidObject(o, o.WidthPixels + 0x0B, halfHeight, halfHeight + 1, previousX);
    }

    /// <summary>0x18FD4: pointing up: hurts players standing on them.</summary>
    private void UpdateUpright(SonicObject o, State s)
    {
        Move(o, s);
        BeSolid(o, o.X);
        if ((o.Status & ObjStatus.StandingP1) != 0) HurtPlayer(o, Sonic);
        if ((o.Status & ObjStatus.StandingP2) != 0) HurtPlayer(o, Tails);
        MarkObjGone2(o, s.BaseX);
    }

    /// <summary>0x19024: pointing sideways: hurts players touching their side.</summary>
    private void UpdateSideways(SonicObject o, State s)
    {
        int previousX = o.X;
        Move(o, s);
        int contact = BeSolid(o, previousX).D6 >> 16;   // swap d6: side contacts, bit 0 Sonic, bit 1 Tails
        if ((contact & 1) != 0)
        {
            HurtPlayer(o, Sonic);
            o.Status &= ~ObjStatus.PushingP1;
        }
        if ((contact & 2) != 0)
        {
            HurtPlayer(o, Tails);
            o.Status &= ~ObjStatus.PushingP2;
        }
        MarkObjGone2(o, s.BaseX);
    }

    /// <summary>0x19080: hanging upside down: hurts players hitting them from below.</summary>
    private void UpdateUpsideDown(SonicObject o, State s)
    {
        Move(o, s);
        int contact = BeSolid(o, o.X).D6 >> 16;   // bottom contacts: bit 2 Sonic, bit 3 Tails
        if ((contact & 4) != 0) HurtPlayer(o, Sonic);
        if ((contact & 8) != 0) HurtPlayer(o, Tails);
        MarkObjGone2(o, s.BaseX);
    }

    /// <summary>0x190CE: unless invincible, invulnerable or already hurt: undo the player's last vertical move, hurt them.</summary>
    private void HurtPlayer(SonicObject o, SonicObject player)
    {
        if (player.Invincible || player.InvulnerableTime != 0 || player.Routine >= PlayerRoutine.Hurt) return;
        player.YLong = (uint)((int)player.YLong - (player.YVel << 8));
        Ctx.Touch.Hurt(player, o);   // HurtCharacter past its invincibility checks (0x43A98)
    }

    /// <summary>MarkObjGone2 (0x1A150): display while in range (from the given x), else delete.</summary>
    private void MarkObjGone2(SonicObject o, int x)
    {
        if (TwoPlayerMode || !OutOfRange(x)) { Display(o); return; }
        if (o.RespawnIndex != 0)
        {
            int entry = ObjectKit.RespawnEntry(Ram, o);
            Ram.Clear(entry, 7);
        }
        Delete(o);
    }

    /// <summary>0x19104: movement by subtype.</summary>
    private void Move(SonicObject o, State s)
    {
        switch (o.Subtype)
        {
            case Still: return;
            case UpDown:
                Retract(o, s);
                o.Y = s.RetractedPixels + s.BaseY;
                return;
            case LeftRight:
                Retract(o, s);
                o.X = s.RetractedPixels + s.BaseX;
                return;
            default: throw new UnportedException(0x19104, $"spikes movement type {o.Subtype}");
        }
    }

    /// <summary>0x19144: wait, then retract or extend by 8 px a frame.</summary>
    private void Retract(SonicObject o, State s)
    {
        if (s.Wait != 0)
        {
            s.Wait--;
            if (s.Wait == 0 && o.OnScreen) Ctx.Sound.PlaySound(Sfx.SpikesMove);
            return;
        }
        if (s.Extending)
        {
            int d = s.Retracted - RetractSpeed;
            if (d >= 0) { s.Retracted = d; return; }
            s.Retracted = 0;
            s.Extending = false;
            s.Wait = WaitFrames;
            return;
        }
        int r = s.Retracted + RetractSpeed;
        if (r < RetractDistance) { s.Retracted = r; return; }
        s.Retracted = RetractDistance;
        s.Extending = true;
        s.Wait = WaitFrames;
    }
}
