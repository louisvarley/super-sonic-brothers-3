namespace SonicBros3.Logic;

/// <summary>
/// Green Hill's edge walls, object $44 in the hack's zones (ROM 0x1517FA; Sonic 1's object on Sonic 2's
/// SolidObject). Subtype = mapping frame; bit 4 makes it decoration only (not solid).
///
/// Deleted (without clearing its respawn entry) once $280 px outside the window Sonic 1 uses: the camera's x minus
/// $80, rounded down to $80.
///
/// Inference: the original calls SolidObject with only d1/d2 set; d3 (the height used while standing on it) and d4
/// (the previous x) are left over from the previous object. The port uses d2+1 and the wall's own x, the values
/// that keep a standing player in place; this only matters if a player stands on top of a wall.
/// </summary>
public sealed class EdgeWall(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x15187A;
    private const int Init = 0, Solid = 2, Decoration = 4;
    private const int DecorationOnly = 0x10;

    public override void Run(SonicObject o)
    {
        if (o.Routine == Init)
        {
            o.Routine = Solid;
            o.Mappings = Mappings;
            o.ArtTile = 0x434C;
            o.RenderFlags |= Render.LevelCoords;
            o.WidthPixels = 8;
            o.Priority = 6;
            o.MappingFrame = o.Subtype & ~DecorationOnly;
            if ((o.Subtype & DecorationOnly) != 0) o.Routine = Decoration;
        }
        if (o.Routine == Solid) Ctx.Solid.SolidObject(o, 0x13, 0x28, 0x29, o.X);
        Display(o);
        if (ObjectKit.OutOfRangeS1(Ram, o.X)) Delete(o);
    }
}
