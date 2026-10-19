namespace SonicBros3.Logic;

/// <summary>
/// Spikes, object $36 in the hack's zones (ROM 0x19306, Sonic 1's code).
///
/// Subtype high nibble: size and orientation (a frame and half width from <see cref="Sizes"/>).
/// Subtype low nibble: 0 still, 1 moves up and down, 2 moves left and right (each: retract 32 px, wait 60 frames,
/// extend, wait 60 frames).
///
/// Spikes are solid. Upward spikes hurt a player standing on them or bumping into them; sideways spikes (frames 1
/// and 5) hurt on side contact.
/// </summary>
public sealed class Spikes(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x194D4;

    /// <summary>Per size (subtype high nibble): mapping frame and half width.</summary>
    private static readonly (int Frame, int Width)[] Sizes = [(0, 0x14), (1, 0x10), (2, 0x04), (3, 0x1C), (4, 0x40), (5, 0x10)];

    private const int Still = 0, UpDown = 1, LeftRight = 2;
    /// <summary>How far the spikes retract: 32 px, in 8.8 fixed point.</summary>
    private const int RetractDistance = 0x2000, RetractSpeed = 0x800;
    private const int WaitFrames = 60;
    private const int SfxSpikesMove = 0xB6;

    /// <summary>The spikes' own variables in their slot.</summary>
    private readonly struct State(SonicObject o)
    {
        /// <summary>$30, $32: where the spikes were placed.</summary>
        public int BaseX { get => o.Word(0x30); set => o.SetWord(0x30, value); }
        public int BaseY { get => o.Word(0x32); set => o.SetWord(0x32, value); }
        /// <summary>$34: how far retracted (8.8; the high byte is whole pixels).</summary>
        public int Retracted { get => o.Word(0x34); set => o.SetWord(0x34, value); }
        public int RetractedPixels => o.Byte(0x34);
        /// <summary>$36: nonzero while extending back out.</summary>
        public bool Extending { get => o.Word(0x36) != 0; set => o.SetWord(0x36, value ? 1 : 0); }
        /// <summary>$38: frames left to wait before moving again.</summary>
        public int Wait { get => o.Word(0x38); set => o.SetWord(0x38, value); }
    }

    public override void Run(SonicObject o)
    {
        var s = new State(o);
        if (o.Routine == 0) Init(o, s);
        Move(o, s);
        CheckPlayer(o, Sonic, 3);
        if (Tails.OnScreen) CheckPlayer(o, Tails, 4);
        Display(o);
        if (OutOfRange(s.BaseX)) Delete(o);
    }

    private void Init(SonicObject o, State s)
    {
        o.Routine = 2;
        o.Mappings = Mappings;
        o.ArtTile = CurrentZone >= 0x17 ? 0x550 : 0x578;
        o.RenderFlags |= Render.LevelCoords;
        o.Priority = 4;
        int subtype = o.Subtype;
        o.Subtype = subtype & 0x0F;
        var (frame, width) = Sizes[subtype >> 4];
        o.MappingFrame = frame;
        o.WidthPixels = width;
        s.BaseX = o.X;
        s.BaseY = o.Y;
    }

    /// <summary>0x1939C: solid against one player, hurting them on the sharp side.</summary>
    /// <param name="standingBit">The status bit for "this player stands on it" (3 Sonic, 4 Tails).</param>
    private void CheckPlayer(SonicObject o, SonicObject player, int standingBit)
    {
        if (o.MappingFrame is 1 or 5)
        {
            // Sideways spikes: only touching their side hurts.
            int halfHeight = o.MappingFrame == 5 ? 4 : 0x14;
            int side = Ctx.Solid.SolidOne(o, player, 0x1B, halfHeight, halfHeight + 1, o.X, ref standingBit);
            if (Ram.Bit(o.StatusAddr, standingBit)) return;
            if (side == 1) Hurt(o, player);
            return;
        }
        int contact = Ctx.Solid.SolidOne(o, player, o.WidthPixels + 0x0B, 0x10, 0x11, o.X, ref standingBit);
        if (Ram.Bit(o.StatusAddr, standingBit) || contact < 0) Hurt(o, player);
    }

    /// <summary>0x193F6 then 0x190CE: undo the player's last vertical move (twice, as the original does) and hurt them.</summary>
    private void Hurt(SonicObject o, SonicObject player)
    {
        if (player.Routine >= 4) return;   // already hurt or dying
        UndoYMove(player);
        bool invincible = player.Invincible;
        if (invincible || player.InvulnerableTime != 0 || player.Routine >= 4) return;
        UndoYMove(player);
        Ctx.Touch.Hurt(player, o);
    }

    private static void UndoYMove(SonicObject player) => player.YLong = (uint)((int)player.YLong - (player.YVel << 8));

    /// <summary>0x19432: movement by subtype.</summary>
    private void Move(SonicObject o, State s)
    {
        int type = o.Subtype;
        if (type == Still) return;
        if (type > LeftRight) throw new UnportedException(0x19432, $"spikes movement type {type}");
        Retract(o, s);
        if (type == UpDown) o.Y = s.BaseY + s.RetractedPixels;
        else o.X = s.BaseX + s.RetractedPixels;
    }

    /// <summary>0x19472: wait, then retract or extend by 8 px a frame.</summary>
    private void Retract(SonicObject o, State s)
    {
        if (s.Wait != 0)
        {
            s.Wait--;
            if (s.Wait == 0 && o.OnScreen) Ctx.Sound.PlaySound(SfxSpikesMove);
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
