namespace SonicBros3.Logic;

/// <summary>
/// The Marble Zone brick, object $46 (ROM 0x1539EE, Sonic 1's): solid while on screen. Subtype bits 0-2:
///   0  still
///   1  bobs with oscillator 5 (bit 3 inverts)
///   2  waits until Sonic is within $90 px, then becomes 3
///   3  falls until it lands; then 0, or 4 if it landed on lava (block index $16A and up)
///   4  floats on the lava with oscillator 4
/// </summary>
public sealed class MarbleBrick(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x153B1E;
    private const int Still = 0, Bobs = 1, WaitsForSonic = 2, Falls = 3, Floats = 4;
    private const int FirstLavaBlock = 0x16A;

    /// <summary>$30: the y it bobs around.</summary>
    private static int BaseY(SonicObject o) => o.Word(0x30);

    public override void Run(SonicObject o)
    {
        if (o.Routine == 0)
        {
            o.Routine = 2;
            o.YRadius = 0x0F;
            o.XRadius = 0x0F;
            o.Mappings = Mappings;
            o.ArtTile = 0x4000;
            o.RenderFlags = Render.LevelCoords;
            o.Priority = 3;
            o.WidthPixels = 0x10;
            o.SetWord(0x30, o.Y);
            o.SetWord(0x32, 0x5C0);
        }
        if (o.OnScreen)
        {
            Move(o);
            Ctx.Solid.SolidObject(o, 0x1B, 0x10, 0x11, o.X);
        }
        if (OutOfRange(o.X)) { o.Clear(); return; }
        Display(o);
    }

    private void Move(SonicObject o)
    {
        switch (o.Subtype & 7)
        {
            case WaitsForSonic:   // 0x153A92
                if (Math.Abs((short)(Sonic.X - o.X)) < 0x90) o.Subtype = Falls;
                goto case Bobs;
            case Bobs:   // 0x153AAA
            {
                int swing = Ram.U8(Oscillation.Value(5));
                if ((o.Subtype & 8) != 0) swing = -swing + 0x10;
                o.Y = BaseY(o) - swing;
                break;
            }
            case Falls:   // 0x153ACA
            {
                ObjectKit.Move(o);
                o.YVel += 0x18;
                int distance = (short)ObjectKit.CheckFloorDist(Ram, Ctx.Collision, o);
                if (distance >= 0) break;
                o.Y += distance;
                o.YVel = 0;
                o.SetWord(0x30, o.Y);
                bool onLava = (Ram.U16(Ctx.Collision.LastTile) & 0x3FF) >= FirstLavaBlock;
                o.Subtype = onLava ? Floats : Still;
                break;
            }
            case Floats:   // 0x153B0A
                o.Y = BaseY(o) - (Ram.U8(Oscillation.Value(4)) >> 3);
                break;
        }
    }
}
