namespace SonicBros3.Logic;

/// <summary>
/// Chopper, object $2B in the hack's zones (ROM 0x152278; Sonic 1's): the piranha that leaps out of the water from
/// its start height at -$700 with gravity $18, its mouth opening near the top of the leap.
/// </summary>
public sealed class Chopper(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x152314;
    private readonly byte[] _anims = ctx.Art["chopper"].Animations!;

    private const short LeapSpeed = -0x700, Gravity = 0x18;
    /// <summary>Animations: 0 mouth fast (falling), 1 mouth slow (near the water), 2 mouth shut (near the top).</summary>
    private const int AnimFast = 0, AnimSlow = 1, AnimShut = 2;

    /// <summary>$30: the height it leaps from (and returns to).</summary>
    private static int StartY(SonicObject o) => o.Word(0x30);

    public override void Run(SonicObject o)
    {
        if (o.Routine == 0)
        {
            o.Routine = 2;
            o.Mappings = Mappings;
            o.ArtTile = 0x43D;
            o.RenderFlags = Render.LevelCoords;
            o.Priority = 4;
            o.CollisionFlags = 9;
            o.WidthPixels = 0x10;
            o.YVel = LeapSpeed;
            o.SetWord(0x30, o.Y);
        }
        Leap(o);   // init falls through into the leap (0x1522C4)
        DisplayOrMarkGone(o);
    }

    /// <summary>0x1522C4.</summary>
    private void Leap(SonicObject o)
    {
        ObjectKit.Animate(Ram, o, _anims);
        ObjectKit.Move(o);
        o.YVel += Gravity;
        if (StartY(o) < o.Y)
        {
            // Back in the water: leap again.
            o.Y = StartY(o);
            o.YVel = LeapSpeed;
        }
        o.Anim = AnimSlow;
        if (((StartY(o) - 0xC0) & 0xFFFF) >= o.Y) return;
        // Within $C0 px of the water.
        o.Anim = o.YVel >= 0 ? AnimShut : AnimFast;
    }
}
