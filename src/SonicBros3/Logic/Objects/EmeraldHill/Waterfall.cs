namespace SonicBros3.Logic;

/// <summary>
/// The waterfall, object $49 in Sonic 2's zones (ROM 0x24BAC; in the hack's zones $49 is the waterfall sound,
/// <see cref="WaterfallSound"/>). A tall sprite whose frame depends on whether a player is within $40 px of it
/// horizontally: the subtype picks the variant (the frame is subtype, plus 1 while a player is near: the splash).
/// </summary>
public sealed class Waterfall(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x24C5E;
    private const int Init = 0, Main = 2;
    private const int NearRange = 0x40;

    public override void Run(SonicObject o)
    {
        if (o.Routine == Init)
        {
            o.Routine = Main;
            o.Mappings = Mappings;
            o.ArtTile = 0x239E;   // (Adjust2PArtPointer: 1-player, unchanged)
            o.RenderFlags = Render.LevelCoords;
            o.WidthPixels = 0x20;
            o.SetWord(0x30, o.X);
            o.Priority = 0;
            o.YRadius = 0x80;
            o.RenderFlags |= Render.UseYRadius;
        }
        // 0x24BF8.
        if (!TwoPlayerMode && OutOfRange(o.X)) { Delete(o); return; }   // (no respawn entry cleared)
        int left = (o.X - NearRange) & 0xFFFF, right = (o.X + NearRange) & 0xFFFF;
        bool Near(SonicObject p) => p.X >= left && p.X < right;
        o.MappingFrame = 0;
        if (Near(Sonic) || Near(Tails)) o.MappingFrame = 1;
        o.MappingFrame += o.Subtype;
        Display(o);
    }
}
