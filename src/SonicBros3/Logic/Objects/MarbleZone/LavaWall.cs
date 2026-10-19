namespace SonicBros3.Logic;

/// <summary>
/// The wall of lava, object $4E (ROM 0x155C04, Sonic 1's Marble Zone): a solid wall that hurts. Once Sonic has come
/// within $C0 x $60 px, it chases him at $180 whenever he has moved away (only while he is alive), up to x $6A0. A
/// second piece trails $80 px behind.
/// </summary>
public sealed class LavaWall(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x155D46;
    private readonly byte[] _anims = ctx.Art["mz-lava-wall"].Animations!;
    private const int Init = 0, Chasing = 2, Watching = 4, Trailing = 6, Gone = 8;
    private const int StopX = 0x6A0, ChaseSpeed = 0x180;

    /// <summary>$36: Sonic has been near.</summary>
    private const int Triggered = 0x36;
    /// <summary>$3C: the front piece's slot (long), for the trailing piece.</summary>
    private static int Front(SonicObject o) => (int)(o.Long(0x3C) & 0xFFFF);

    public override void Run(SonicObject o)
    {
        switch (o.Routine)
        {
            case Init: CreatePieces(o); goto case Watching;
            case Watching: Watch(o); goto case Chasing;
            case Chasing: Chase(o); break;
            case Trailing:
            {
                var front = new SonicObject(Ram, Front(o));
                if (front.Routine == Gone) { o.Clear(); return; }
                o.X = front.X - 0x80;
                Display(o);
                break;
            }
            default: o.Clear(); break;
        }
    }

    private void CreatePieces(SonicObject o)
    {
        o.Routine = Watching;
        var piece = o;
        for (int i = 0; i < 2; i++)
        {
            if (i > 0)
            {
                int slot = ObjectKit.FindFreeSlotAfter(Ram, o);
                if (slot < 0) return;
                piece = new SonicObject(Ram, slot);
            }
            piece.Id = o.Id;
            piece.Mappings = Mappings;
            piece.ArtTile = 0x63A8;
            piece.RenderFlags = Render.LevelCoords;
            piece.WidthPixels = 0x50;
            piece.X = o.X;
            piece.Y = o.Y;
            piece.Priority = 1;
            piece.Anim = 0;
            piece.CollisionFlags = 0x94;   // hurts
            piece.SetLong(0x3C, 0xFFFF0000u | (uint)o.Address);
        }
        piece.Routine += 6;   // the second piece trails
        piece.MappingFrame = 4;
    }

    /// <summary>0x155C7C: start the chase once Sonic has been near and moves away.</summary>
    private void Watch(SonicObject o)
    {
        int dx = Math.Abs((short)(Sonic.X - o.X));
        int dy = Math.Abs((short)(Sonic.Y - o.Y));
        if (dx < 0xC0 && dy < 0x60) { o.SetByte(Triggered, 1); return; }
        if (o.Byte(Triggered) == 0) return;
        o.XVel = ChaseSpeed;
        o.Routine = Chasing;
    }

    /// <summary>0x155CB8.</summary>
    private void Chase(SonicObject o)
    {
        int routine = o.Routine;
        Ctx.Solid.SolidObject(o, 0x2B, 0x18, 0x19, o.X);
        o.Routine = routine;
        if (o.X == StopX)
        {
            o.XVel = 0;
            o.SetByte(Triggered, 0);
        }
        ObjectKit.Animate(Ram, o, _anims);
        if (Sonic.Routine < 4) ObjectKit.Move(o);   // only while Sonic is alive
        Display(o);
    }
}
