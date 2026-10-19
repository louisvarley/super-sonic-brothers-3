namespace SonicBros3.Logic;

/// <summary>
/// Scenery, object $1C in Sonic 2's zones (ROM 0x14034; the hack's zones have their own, <see cref="Scenery"/>): a
/// static sprite chosen by subtype from the table at 0x14046 {frame and mappings, art tile, half width, priority},
/// with a half height from 0x140EE. Emerald Hill uses subtype 2, the bridge stakes.
/// </summary>
public sealed class SceneryS2(ObjectContext ctx) : GameObject(ctx)
{
    private const int TableAddress = 0x14046;
    private const int Init = 0, Main = 2;

    /// <summary>Per subtype: the mappings long (its top byte is the frame), art tile, half width, priority.</summary>
    private static readonly (uint FrameAndMappings, int Art, int Width, int Priority)[] Table =
    [
        (0x00014440, 0x43FD, 4, 6), (0x01014440, 0x43FD, 4, 6), (0x010126F8, 0x43B6, 4, 1),
    ];

    public override void Run(SonicObject o)
    {
        if (o.Routine == Init)
        {
            // 0x14104.
            o.Routine = Main;
            int subtype = o.Subtype;
            if (subtype >= Table.Length) throw new UnportedException(TableAddress + subtype * 8, $"scenery subtype {subtype} in Sonic 2's zones");
            var t = Table[subtype];
            o.MappingFrame = (int)(t.FrameAndMappings >> 24);
            o.Mappings = t.FrameAndMappings;   // the 68000 ignores the top byte
            o.ArtTile = t.Art;                  // (Adjust2PArtPointer: 1-player, unchanged)
            o.RenderFlags |= Render.LevelCoords;
            o.WidthPixels = t.Width;
            o.Priority = t.Priority;
            // The half heights at 0x140EE are 0 for these subtypes (no y_radius).
        }
        DisplayOrMarkGone(o);   // MarkObjGone (0x1A118)
    }
}
