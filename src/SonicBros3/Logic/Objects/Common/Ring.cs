namespace SonicBros3.Logic;

/// <summary>
/// A ring as an object, object $25 (ROM 0x14E44). Level rings are handled by <see cref="RingsManager"/> instead;
/// this is used for the sparkles the signpost scatters (spawned straight into <see cref="Sparkle"/>).
/// </summary>
public sealed class Ring(ObjectContext ctx) : GameObject(ctx)
{
    private readonly byte[] _anims = ctx.Art["lost-rings"].Animations!;

    private const int Init = 0, Spin = 2, Collected = 4;
    /// <summary>The sparkle animation; its end advances the routine to <see cref="Gone"/>.</summary>
    public const int Sparkle = 6;
    private const int Gone = 8;

    /// <summary>$32: the ring's original x (the despawn test uses it).</summary>
    private static int BaseX(SonicObject o) => o.Word(0x32);
    /// <summary>$3F: set when Tails collected it.</summary>
    private static bool ByTails(SonicObject o) => o.Byte(0x3F) != 0;

    public override void Run(SonicObject o)
    {
        switch (o.Routine)
        {
            case Init:
                o.Routine = Spin;
                o.SetWord(0x32, o.X);
                o.Mappings = LostRings.Mappings;
                o.ArtTile = 0x26BC;
                o.RenderFlags = Render.LevelCoords;
                o.Priority = 2;
                o.CollisionFlags = 0x47;
                o.WidthPixels = 8;
                goto case Spin;
            case Spin:
                o.MappingFrame = Ram.U8(Vars.RingsAnimFrame);
                MarkObjGone2(o, BaseX(o));
                break;
            case Collected:
                o.Routine = Sparkle;
                o.CollisionFlags = 0;
                o.Priority = 1;
                TouchResponse.CollectRingFor(Ram, Ctx.Sound, tails: ByTails(o));
                goto case Sparkle;
            case Sparkle:
                ObjectKit.Animate(Ram, o, _anims);
                Display(o);
                break;
            case Gone: Delete(o); break;
        }
    }

    /// <summary>MarkObjGone2 (0x1A150): MarkObjGone's display variant, testing a given x.</summary>
    private void MarkObjGone2(SonicObject o, int x)
    {
        if (TwoPlayerMode || !OutOfRange(x)) { Display(o); return; }
        if (o.RespawnIndex != 0) Ram.Clear(ObjectKit.RespawnEntry(Ram, o), 7);
        Delete(o);
    }
}
