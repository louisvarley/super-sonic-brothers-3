namespace SonicBros3.Logic;

/// <summary>
/// Masher, object $5C in Sonic 2's zones (ROM 0x315F8): the piranha that leaps out of the water at Emerald Hill's
/// bridges, from its start height at -$500 (-$400 the first time) with gravity $18, biting near the top of the leap.
/// </summary>
public sealed class Masher(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x316A6;
    private readonly byte[] _anims = ctx.Art["masher"].Animations!;
    private const int Init = 0, Leaping = 2;
    private const short FirstLeap = -0x400, LeapSpeed = -0x500, Gravity = 0x18;
    /// <summary>Animations: 0 biting fast (low), 1 biting slowly (near the water), 2 mouth shut (falling, low).</summary>
    private const int AnimFast = 0, AnimSlow = 1, AnimShut = 2;

    /// <summary>$30: the height it leaps from.</summary>
    private static int StartY(SonicObject o) => o.Word(0x30);

    public override void Run(SonicObject o)
    {
        if (o.Routine == Init)
        {
            // 0x3160E.
            o.Routine = Leaping;
            o.Mappings = Mappings;
            o.ArtTile = 0x414;   // (Adjust2PArtPointer: 1-player, unchanged)
            o.RenderFlags = Render.LevelCoords;
            o.Priority = 4;
            o.CollisionFlags = 9;
            o.WidthPixels = 0x10;
            o.YVel = FirstLeap;
            o.SetWord(0x30, o.Y);
        }
        Leap(o);   // the init falls through into the leap
        DisplayOrMarkGone(o);   // MarkObjGone (0x1A118)
    }

    /// <summary>0x31648.</summary>
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
