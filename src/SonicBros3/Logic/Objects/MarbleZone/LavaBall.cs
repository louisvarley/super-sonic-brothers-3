namespace SonicBros3.Logic;

/// <summary>
/// A lava ball, object $14 (ROM 0x153738, Sonic 1's Marble Zone). It hurts. Subtypes:
///   0-3  leap up at -$400..-$700 and fall back (gone below their start)
///   4, 5 fly up / down until the ceiling / floor
///   6, 7 fly left / right until a wall
/// then it bursts (its animation's $FC command ends it).
/// </summary>
public sealed class LavaBall(ObjectContext ctx) : GameObject(ctx)
{
    private static readonly short[] Speeds = [-0x400, -0x500, -0x600, -0x700, -0x200, 0x200, -0x200, 0x200, 0];
    private readonly byte[] _anims = ctx.Art["mz-lava-ball"].Animations!;
    private const int Init = 0, Flying = 2;
    private const int FlyUp = 4, FlyDown = 5, FlyLeft = 6, FlyRight = 7, Bursting = 8;
    private const int AnimBurstVertical = 1, AnimSideways = 2, AnimBurstSideways = 3;
    private const int Gravity = 0x18;
    private const int SfxLavaBall = 0xAE;

    /// <summary>$30: the y it was thrown from.</summary>
    private static int StartY(SonicObject o) => o.Word(0x30);

    public override void Run(SonicObject o)
    {
        switch (o.Routine)
        {
            case Init:
                Initialise(o);
                goto case Flying;
            case Flying:
                Fly(o);
                break;
            default:
                o.Clear();
                break;
        }
        Display(o);   // 0x153746 (DisplaySprite skips an emptied slot)
    }

    private void Initialise(SonicObject o)
    {
        o.Routine = Flying;
        o.YRadius = 8;
        o.XRadius = 8;
        o.Mappings = GrassFire.Mappings;
        o.ArtTile = CurrentZone >= 0x19 ? 0x500 : 0x345;
        o.RenderFlags = Render.LevelCoords;
        o.Priority = 3;
        o.CollisionFlags = 0x8B;
        o.SetWord(0x30, o.Y);
        if (o.Byte(0x29) != 0) o.Priority += 2;
        int type = o.Subtype;
        o.YVel = type < Speeds.Length ? Speeds[type] : (short)0;
        o.WidthPixels = 8;
        if (type >= FlyLeft)
        {
            o.WidthPixels = 0x10;
            o.Anim = AnimSideways;
            o.XVel = o.YVel;
            o.YVel = 0;
        }
        Ctx.Sound.PlaySound(SfxLavaBall);
    }

    private void Fly(SonicObject o)
    {
        switch (o.Subtype)
        {
            case 0 or 1 or 2 or 3:   // 0x153844: leap and fall back
                o.YVel += Gravity;
                if (StartY(o) < o.Y) o.Routine += 2;
                // Upside down while rising.
                o.Status &= ~ObjStatus.YFlip;
                if (o.YVel < 0) o.Status |= ObjStatus.YFlip;
                break;
            case FlyUp:   // 0x15386C
                o.Status |= ObjStatus.YFlip;
                if (ObjectKit.CheckCeiling(Ram, Ctx.Collision, o).Distance < 0) Burst(o, AnimBurstVertical, vertical: true);
                break;
            case FlyDown:   // 0x153890
                o.Status &= ~ObjStatus.YFlip;
                if ((short)ObjectKit.CheckFloorDist(Ram, Ctx.Collision, o) < 0) Burst(o, AnimBurstVertical, vertical: true);
                break;
            case FlyLeft:   // 0x1538B4
                o.Status |= ObjStatus.XFlip;
                if (ObjectKit.CheckRightWall(Ram, Ctx.Collision, o, -8) < 0) Burst(o, AnimBurstSideways, vertical: false);
                break;
            case FlyRight:   // 0x1538DA
                o.Status &= ~ObjStatus.XFlip;
                if (ObjectKit.CheckLeftWall(Ram, Ctx.Collision, o, 8) < 0) Burst(o, AnimBurstSideways, vertical: false);
                break;
        }
        ObjectKit.Move(o);
        ObjectKit.Animate(Ram, o, _anims);
        if (OutOfRangeS1(o.X)) o.Clear();
    }

    private static void Burst(SonicObject o, int anim, bool vertical)
    {
        o.Subtype = Bursting;
        o.Anim = anim;
        if (vertical) o.YVel = 0;
        else o.XVel = 0;
    }
}
