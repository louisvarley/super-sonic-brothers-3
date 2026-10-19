namespace SonicBros3.Logic;

/// <summary>
/// The lava marker, object $54 (ROM 0x24DFA, Sonic 2's): an invisible area that hurts. The subtype picks its size.
/// Shown only in debug mode. Out of range it is cleared (it has no respawn entry).
/// </summary>
public sealed class LavaMarker(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x24E7A, DebugMappings = 0x24E82;
    /// <summary>Touch flags per subtype: "hurts", sizes $16, $14, $15.</summary>
    private static readonly byte[] Collision = [0x96, 0x94, 0x95];

    public override void Run(SonicObject o)
    {
        if (o.Routine == 0)
        {
            o.Routine = 2;
            int size = o.Subtype;
            o.CollisionFlags = size < Collision.Length ? Collision[size] : 0;
            bool debug = Ram.U16(Vars.DebugPlacementMode) != 0;
            o.Mappings = debug ? DebugMappings : Mappings;
            o.ArtTile = 0x8680;
            o.RenderFlags = Render.OnScreen | Render.LevelCoords;
            o.WidthPixels = 0x80;
            o.Priority = 4;
            o.MappingFrame = size;
        }
        if (!TwoPlayerMode && OutOfRange(o.X)) { o.Clear(); return; }
        if (Ram.U16(Vars.DebugPlacementMode) != 0) Display(o);
    }
}
