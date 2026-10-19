namespace SonicBros3.Logic;

/// <summary>
/// The invisible solid block, object $71 (ROM 0x24EEE, Sonic 2's). Subtype high nibble: width in 16 px; low nibble:
/// height in 8 px (both minus one). Solid to players (SolidObject_Always); cleared out of range.
/// </summary>
public sealed class InvisibleBlock(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x24F74;

    public override void Run(SonicObject o)
    {
        if (o.Routine == 0)
        {
            o.Routine = 2;
            o.Mappings = Mappings;
            o.ArtTile = 0x8680;
            o.RenderFlags |= Render.LevelCoords;
            int size = o.Subtype;
            o.WidthPixels = ((size & 0xF0) + 0x10) >> 1;
            o.YRadius = ((size & 0x0F) + 1) << 3;
        }
        int halfHeight = o.Byte(0x16);   // y_radius, unsigned
        Ctx.Solid.SolidObjectAlways(o, o.WidthPixels + 0x0B, halfHeight, halfHeight + 1, o.X);
        if (!TwoPlayerMode && OutOfRange(o.X)) o.Clear();
    }
}
