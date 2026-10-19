namespace SonicBros3.Logic;

/// <summary>
/// Scenery, object $1C in the hack's zones (ROM 0x14150): a static sprite chosen by subtype from the table at
/// 0x141AA. In Green Hill only subtype 3 (the bridge stake) is used.
/// </summary>
public sealed class Scenery(ObjectContext ctx) : GameObject(ctx)
{
    private const uint BridgeMappings = 0x12710;

    /// <summary>Per subtype: mappings, art tile, frame, half width, priority, collision flags.</summary>
    private static readonly (uint Mappings, int Art, int Frame, int Width, int Priority, int Collision)[] Table =
    [
        (0x141D2, 0x4544, 0, 8, 2, 0), (0x141D2, 0x4544, 0, 8, 2, 0), (0x141D2, 0x4544, 0, 8, 2, 0),
        (BridgeMappings, 0x438E, 1, 0x10, 1, 0),   // the bridge stake
    ];

    public override void Run(SonicObject o)
    {
        if (o.Routine == 0)
        {
            o.Routine = 2;
            int subtype = o.Subtype;
            if (subtype >= Table.Length) throw new UnportedException(0x14166, $"scenery subtype {subtype}");
            var t = Table[subtype];
            if (t.Mappings != BridgeMappings) throw new UnportedException(0x141D2, "scenery mappings 0x141D2");
            o.Mappings = t.Mappings;
            o.ArtTile = t.Art;
            o.RenderFlags |= Render.LevelCoords;
            o.MappingFrame = t.Frame;
            o.WidthPixels = t.Width;
            o.Priority = t.Priority;
            o.CollisionFlags = t.Collision;
        }
        if (OutOfRange(o.X)) Delete(o);
        else Display(o);
    }
}
