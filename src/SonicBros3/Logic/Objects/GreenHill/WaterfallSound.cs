namespace SonicBros3.Logic;

/// <summary>The waterfall sound, object $49 in the hack's zones (ROM 0x152642): plays its sound every 64 frames.</summary>
public sealed class WaterfallSound(ObjectContext ctx) : GameObject(ctx)
{
    private const int SfxWaterfall = 0xD0;

    public override void Run(SonicObject o)
    {
        if (o.Routine == 0)
        {
            o.Routine = 2;
            o.RenderFlags = Render.LevelCoords;
        }
        if ((Ram.U8(Vars.VintRuncountLow) & 0x3F) == 0) Ctx.Sound.PlaySound(SfxWaterfall);
        if (OutOfRange(o.X)) Delete(o);
    }
}
