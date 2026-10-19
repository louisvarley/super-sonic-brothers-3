namespace SonicBros3.Logic;

/// <summary>
/// The button, object $32 (ROM 0x154376, Sonic 1's). Solid while on screen. It is pressed while stood on (or, with
/// subtype bit 7, while a push block sits on it). Pressed, it sets bit 0 (bit 7 with subtype bit 6) of the switch
/// flag <see cref="Vars.ButtonVineTrigger"/> + subtype low nibble, with a click on the press. Subtype bit 5 makes it
/// flash.
/// </summary>
public sealed class Button(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x154514;
    private const int PushBlockId = 0x33;
    private const int SfxClick = 0xCD;

    public override void Run(SonicObject o)
    {
        if (o.Routine == 0)
        {
            o.Routine = 2;
            o.Mappings = Mappings;
            int zone = CurrentZone;
            o.ArtTile = zone is 0x13 or 0x14 ? 0x4570 : zone == 0x1B ? 0x23A9 : 0x2454;
            o.RenderFlags = Render.LevelCoords;
            o.WidthPixels = 0x10;
            o.Priority = 4;
            o.Y += 3;
        }
        if (o.OnScreen) Update(o);
        Display(o);
        if (OutOfRange(o.X)) o.Clear();
    }

    private void Update(SonicObject o)
    {
        Ctx.Solid.SolidObject(o, 0x1B, 5, 5, o.X);
        bool stoodOn = (o.Status & (ObjStatus.StandingP1 | ObjStatus.StandingP2)) != 0;
        o.RoutineSecondary = stoodOn ? 2 : 0;
        o.MappingFrame &= ~1;
        int subtype = o.Subtype;
        int flag = Vars.ButtonVineTrigger + (subtype & 0x0F);
        int bit = (subtype & 0x40) != 0 ? 7 : 0;
        bool byBlock = (subtype & 0x80) != 0 && BlockOnTop(o);
        if (byBlock || o.RoutineSecondary != 0)
        {
            if (Ram.U8(flag) == 0) Ctx.Sound.PlaySound(SfxClick);
            Ram.W8(flag, Ram.U8(flag) | (1 << bit));
            o.MappingFrame |= 1;   // pressed
        }
        else Ram.W8(flag, Ram.U8(flag) & ~(1 << bit));
        if ((subtype & 0x20) != 0)
        {
            o.AnimFrameDuration--;
            if ((sbyte)o.AnimFrameDuration < 0)
            {
                o.AnimFrameDuration = 7;
                o.MappingFrame ^= 2;   // flash
            }
        }
    }

    /// <summary>0x154492: an on-screen push block (16 px half-size) overlapping the button's box.</summary>
    private bool BlockOnTop(SonicObject o)
    {
        int left = (o.X - 0x10) & 0xFFFF, top = (o.Y - 8) & 0xFFFF;
        for (int a = Vars.MainCharacter; a < Vars.MainCharacter + 0x60 * Objects.SlotSize; a += Objects.SlotSize)
        {
            var other = new SonicObject(Ram, a);
            if (!other.OnScreen || other.Id != PushBlockId) continue;
            if (!Overlap(other.X, left, 0x20)) continue;
            if (!Overlap(other.Y, top, 0x10)) continue;
            return true;
        }
        return false;

        // 0x1544DE: within [start, start + size] after taking 16 off, or (borrow) within 32 px before it.
        static bool Overlap(int pos, int start, int size)
        {
            int v = (pos - 0x10) & 0xFFFF;
            if (v >= start) return v - start <= size;
            return ((v - start) & 0xFFFF) + 0x20 > 0xFFFF;
        }
    }
}
