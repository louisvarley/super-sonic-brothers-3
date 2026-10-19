namespace SonicBros3.Logic;

/// <summary>
/// The end-of-act giant ring, object $4B in the hack's zones (ROM 0x150FE), Sonic 1 style. It only appears with 50
/// rings or more, and while the Sonic 1 emeralds are not all collected (<see cref="Vars.Sonic1EmeraldCount"/> = 6).
/// Appearing starts streaming its art to VRAM (<see cref="Vars.BigRingArtLeft"/>, see <see cref="World"/>). Sonic
/// touching it (Tails can't) spawns the flash (object $DE) and plays its sound. Its frame follows the ring
/// animation. Leaving the screen clears it for good (no respawn).
/// </summary>
public sealed class GiantRing(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x15328;
    private const int WaitForRings = 0, Shown = 2, Touched = 4;
    private const int FlashId = 0xDE;
    private const int SfxGiantRing = 0xC3;

    public override void Run(SonicObject o)
    {
        switch (o.Routine)
        {
            case WaitForRings:
                if (Ram.U8(Vars.TimedMode) != 0) { o.Clear(); return; }
                o.Mappings = Mappings;
                o.ArtTile = ArtTile(Ram);
                o.RenderFlags |= Render.LevelCoords;
                o.WidthPixels = 0x40;
                if (o.OnScreen)
                {
                    if (Ram.U8(Vars.Sonic1EmeraldCount) == 6) { o.Clear(); return; }
                    if (Ram.U16(Vars.RingCount) < 50) return;
                    o.Routine = Shown;
                    o.Priority = 2;
                    o.CollisionFlags = 0x52;
                    Ram.W16(Vars.BigRingArtLeft, 0xC40);
                }
                Show(o);
                break;
            case Shown: Show(o); break;
            case Touched: OnTouched(o); break;
            default: o.Clear(); break;
        }
    }

    /// <summary>The art tile (zones $0F and $19 use another palette line).</summary>
    public static int ArtTile(Ram ram) => ram.U8(Vars.CurrentZoneAndAct) is 0x19 or 0x0F ? 0x2200 : 0x23D2;

    /// <summary>0x15180.</summary>
    private void Show(SonicObject o)
    {
        o.MappingFrame = Ram.U8(Vars.RingsAnimFrame);
        if (OutOfRange(o.X)) { o.Clear(); return; }
        Display(o);
    }

    /// <summary>0x1519E: set by TouchResponse when a player touches it.</summary>
    private void OnTouched(SonicObject o)
    {
        o.Routine = Shown;
        bool timerStopped = Ram.U8(Vars.UpdateHudTimer) == 0;
        bool byTails = o.Word(0x3E) == Vars.Sidekick;   // $3E: who touched it
        if (!timerStopped || byTails) { Show(o); return; }
        o.CollisionFlags = 0;
        if (Spawn() is { } flash)
        {
            flash.Id = FlashId;
            flash.X = o.X;
            flash.Y = o.Y;
            flash.SetLong(0x3C, 0xFFFF0000u | (uint)o.Address);
            if (Sonic.X >= o.X) flash.RenderFlags |= Render.XFlip;
        }
        Ctx.Sound.PlaySoundSecondary(SfxGiantRing);
        o.Clear();
    }
}

/// <summary>
/// The giant ring's flash, object $DE (ROM 0x151F4). It replaces the ring art with its own (0x43F50) and animates 8
/// frames (2 frames each). At frame 3 Sonic vanishes (animation $1C), <see cref="Vars.SpecialStageRequested"/> is set
/// and his shield and invincibility flags are cleared; at the end Sonic's slot is cleared. The special stage (game
/// mode $2C) is not ported: the next level load skips it (see <see cref="Runtime.LevelSession"/>).
/// </summary>
public sealed class GiantRingFlash(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x15428;
    private const int Init = 0, Animating = 2;
    private const int Frames = 8, SonicVanishes = 3;
    private const int SonicAnimVanished = 0x1C;

    public override void Run(SonicObject o)
    {
        switch (o.Routine)
        {
            case Init:
                o.Routine = Animating;
                o.Mappings = Mappings;
                o.ArtTile = GiantRing.ArtTile(Ram);
                Ctx.LoadFlashArt?.Invoke();
                Ram.W16(Vars.BigRingArtLeft, 0);
                o.RenderFlags |= Render.LevelCoords;
                o.Priority = 0;
                o.WidthPixels = 0x20;
                o.MappingFrame = 0xFF;
                goto case Animating;
            case Animating:
                if (!Animate(o)) return;
                if (OutOfRange(o.X)) { o.Clear(); return; }
                Display(o);
                break;
            default:
                o.Clear();
                break;
        }
    }

    /// <summary>0x1526C. False when the flash ended (the original pops its caller's return: no display).</summary>
    private bool Animate(SonicObject o)
    {
        o.AnimFrameDuration--;
        if ((sbyte)o.AnimFrameDuration >= 0) return true;
        o.AnimFrameDuration = 1;
        o.MappingFrame++;
        if (o.MappingFrame >= Frames)
        {
            o.Routine += 2;
            Sonic.SetWord(0, 0);   // Sonic's id (and render flags): gone
            return false;
        }
        if (o.MappingFrame == SonicVanishes)
        {
            Sonic.Anim = SonicAnimVanished;
            Ram.W8(Vars.SpecialStageRequested, 1);
            Sonic.StatusSecondary &= ~3;   // no shield, no invincibility
        }
        return true;
    }
}
