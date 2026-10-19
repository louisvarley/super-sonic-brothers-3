namespace SonicBros3.Logic;

/// <summary>
/// Basaran, object $55 (ROM 0x155268, Sonic 1's Marble Zone bat). It hangs until Sonic is within $80 px horizontally
/// and below it; then (on its frame of every 8, offset by its slot) it drops, swoops at +-$100 at his height, and
/// once he is $80 px away flies back up to the ceiling.
/// </summary>
public sealed class Basaran(ObjectContext ctx, Objects objects) : GameObject(ctx)
{
    public const uint Mappings = 0x1553E8;
    private readonly byte[] _anims = ctx.Art["mz-basaran"].Animations!;
    private const int Hanging = 0, Dropping = 2, Swooping = 4, Returning = 6;
    private const int AnimHang = 0, AnimDrop = 1, AnimFly = 2;
    private const int Range = 0x80, SwoopSpeed = 0x100, Gravity = 0x18;
    private const int SfxFlap = 0xC0;

    /// <summary>$36: the height it swoops at (Sonic's y when it dropped).</summary>
    private const int TargetY = 0x36;

    public override void Run(SonicObject o)
    {
        if (o.Routine == 0)
        {
            o.Routine = 2;
            o.Mappings = Mappings;
            o.ArtTile = 0x8518;
            o.RenderFlags = Render.LevelCoords;
            o.YRadius = 0x0C;
            o.Priority = 2;
            o.CollisionFlags = 0x0B;
            o.WidthPixels = 0x10;
        }
        int slot = objects.SlotCounter;
        switch (o.RoutineSecondary)
        {
            case Hanging: Hang(o, slot); break;
            case Dropping: if (!Drop(o)) return; break;
            case Swooping: Swoop(o, slot); break;
            case Returning: FlyBackUp(o); break;
        }
        ObjectKit.Animate(Ram, o, _anims);
        ObjectKit.HackPalette(Ram, o);
        DisplayOrMarkGone(o);
    }

    /// <summary>0x1553C6: face Sonic; returns the distance to him and the speed toward him.</summary>
    private (int Distance, int Speed) FaceSonic(SonicObject o)
    {
        o.Status |= ObjStatus.XFlip;
        if (Sonic.X < o.X)
        {
            o.Status &= ~ObjStatus.XFlip;
            return ((o.X - Sonic.X) & 0xFFFF, -SwoopSpeed);
        }
        return ((Sonic.X - o.X) & 0xFFFF, SwoopSpeed);
    }

    private bool MyTurn(int slot) => ((Ram.U8(Vars.VintRuncountLow) + slot) & 7) == 0;

    private void Hang(SonicObject o, int slot)
    {
        if (FaceSonic(o).Distance >= Range) return;
        int sonicY = Sonic.Y;
        o.SetWord(TargetY, sonicY);
        if (sonicY < o.Y || sonicY - o.Y >= Range) return;   // only when Sonic is below it
        if (Ram.U16(Vars.DebugPlacementMode) != 0) return;
        if (!MyTurn(slot)) return;
        o.Anim = AnimDrop;
        o.RoutineSecondary = Dropping;
    }

    /// <summary>0x155312. False when it went off screen below its target (gone).</summary>
    private bool Drop(SonicObject o)
    {
        ObjectKit.Move(o);
        o.YVel += Gravity;
        var (_, speed) = FaceSonic(o);
        int target = o.Word(TargetY);
        if (target < o.Y)
        {
            if (!o.OnScreen) { o.Clear(); return false; }
            return true;
        }
        if (target - o.Y >= 0x10) return true;
        // At Sonic's height: swoop.
        o.XVel = (short)speed;
        o.YVel = 0;
        o.Anim = AnimFly;
        o.RoutineSecondary = Swooping;
        return true;
    }

    private void Swoop(SonicObject o, int slot)
    {
        if ((Ram.U8(Vars.VintRuncountLow) & 0x0F) == 0) Ctx.Sound.PlaySound(SfxFlap);
        ObjectKit.Move(o);
        int distance = Math.Abs((short)(Sonic.X - o.X));
        if (distance < Range) return;
        if (MyTurn(slot)) o.RoutineSecondary = Returning;
    }

    private void FlyBackUp(SonicObject o)
    {
        ObjectKit.Move(o);
        o.YVel -= Gravity;
        int ceiling = ObjectKit.CheckCeiling(Ram, Ctx.Collision, o).Distance;
        if (ceiling >= 0) return;
        o.Y -= ceiling;
        o.X &= 0xFFF8;
        o.XVel = 0;
        o.YVel = 0;
        o.Anim = AnimHang;
        o.RoutineSecondary = Hanging;
    }
}
