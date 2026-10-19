namespace SonicBros3.Logic;

/// <summary>
/// Newtron, object $42 in the hack's zones (ROM 0x15233E; Sonic 1's): the chameleon that appears when Sonic comes
/// within 128 px.
/// - Subtype 0: fades in facing Sonic, drops to the floor and runs along it (flying straight on at ledges).
/// - Other subtypes: fades in and fires a projectile (object $23, subtype 1).
/// </summary>
public sealed class Newtron(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x1524FE;
    private readonly byte[] _anims = ctx.Art["newtron"].Animations!;

    private const int Init = 0, Main = 2, Gone = 4;
    // Main's states (routine_secondary).
    private const int WaitForSonic = 0, Appear = 2, RunOnFloor = 4, Fly = 6, Fire = 8;
    // Animations.
    private const int AnimAppear = 1, AnimRun = 2, AnimFire = 4;
    private const int Art = 0x500;

    /// <summary>$32: set once the projectile is fired.</summary>
    private const int Fired = 0x32;

    public override void Run(SonicObject o)
    {
        switch (o.Routine)
        {
            case Init:
                o.Routine = Main;
                o.Mappings = Mappings;
                o.ArtTile = Art;
                o.RenderFlags = Render.LevelCoords;
                o.Priority = 4;
                o.WidthPixels = 0x14;
                o.YRadius = 0x10;
                o.XRadius = 8;
                goto case Main;
            case Main:
                switch (o.RoutineSecondary)
                {
                    case WaitForSonic: Wait(o); break;
                    case Appear: AppearAndDrop(o); break;
                    case RunOnFloor: Running(o); break;
                    case Fly: ObjectKit.Move(o); break;
                    case Fire: Firing(o); break;
                }
                ObjectKit.Animate(Ram, o, _anims);
                DisplayOrMarkGone(o);
                break;
            case Gone: Delete(o); break;
        }
    }

    private void FaceSonic(SonicObject o)
    {
        o.Status |= ObjStatus.XFlip;
        if (Sonic.X < o.X) o.Status &= ~ObjStatus.XFlip;
    }

    /// <summary>0x1523AA: face Sonic; appear once he is within 128 px.</summary>
    private void Wait(SonicObject o)
    {
        FaceSonic(o);
        int distance = Math.Abs((short)(Sonic.X - o.X));
        if (distance >= 0x80) return;
        o.RoutineSecondary = Appear;
        o.Anim = AnimAppear;
        if (o.Subtype == 0) return;
        o.ArtTile = Art;
        o.RoutineSecondary = Fire;
        o.Anim = AnimFire;
    }

    /// <summary>0x1523EC: fade in (facing Sonic), then fall to the floor and start running.</summary>
    private void AppearAndDrop(SonicObject o)
    {
        if (o.MappingFrame < 4) { FaceSonic(o); return; }
        if (o.MappingFrame == 1) o.CollisionFlags = 0x0C;   // (never true here; as in the original)
        ObjectKit.MoveAndFall(o);
        int distance = (short)ObjectKit.CheckFloorDist(Ram, Ctx.Collision, o);
        if (distance >= 0) return;
        o.Y += distance;
        o.YVel = 0;
        o.RoutineSecondary = RunOnFloor;
        o.Anim = AnimRun;
        if ((o.ArtTile & 0x2000) != 0) o.Anim++;   // the other palette line runs another animation
        o.CollisionFlags = 0x0D;
        o.XVel = (o.Status & ObjStatus.XFlip) != 0 ? (short)0x200 : (short)-0x200;
    }

    /// <summary>0x152464: run along the floor; fly straight on when it drops away or rises.</summary>
    private void Running(SonicObject o)
    {
        ObjectKit.Move(o);
        int distance = (short)ObjectKit.CheckFloorDist(Ram, Ctx.Collision, o);
        if (distance < -8 || distance >= 0x0C) { o.RoutineSecondary = Fly; return; }
        o.Y += distance;
    }

    /// <summary>0x152490: fire one projectile on frame 2.</summary>
    private void Firing(SonicObject o)
    {
        if (o.MappingFrame == 1) o.CollisionFlags = 0x0C;
        if (o.MappingFrame != 2 || o.Byte(Fired) != 0) return;
        o.SetByte(Fired, 1);
        if (Spawn() is not { } m) return;
        bool right = (o.Status & ObjStatus.XFlip) != 0;
        m.Id = Missile.Id;
        m.X = o.X + (right ? 0x14 : -0x14);
        m.Y = o.Y - 8;
        m.XVel = right ? (short)0x200 : (short)-0x200;
        m.Status = o.Status;
        m.Subtype = 1;
    }
}
