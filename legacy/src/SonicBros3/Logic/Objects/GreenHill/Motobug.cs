namespace SonicBros3.Logic;

/// <summary>
/// Motobug, object $40 in the hack's zones (ROM 0x1518E6; Sonic 1's Green Hill code). Falls to the floor, then
/// alternates waiting (60 frames) and driving at 1 px a frame, turning at ledges and walls. While driving it leaves
/// an exhaust puff every 16 frames: the same object id with animation 2.
/// </summary>
public sealed class Motobug(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x151A58;
    private readonly byte[] _anims = ctx.Art["motobug"].Animations!;

    private const int Init = 0, Main = 2, Exhaust = 4, Gone = 6;
    private const int Waiting = 0, Driving = 2;
    private const int AnimStill = 0, AnimDrive = 1, AnimExhaust = 2;
    private const int WaitFrames = 0x3B;

    /// <summary>The Motobug's own variables.</summary>
    private readonly struct State(SonicObject o)
    {
        /// <summary>$30: frames left to wait.</summary>
        public int WaitTimer { get => o.Word(0x30); set => o.SetWord(0x30, value); }
        /// <summary>$33: frames until the next exhaust puff.</summary>
        public int PuffTimer { get => o.Byte(0x33); set => o.SetByte(0x33, value); }
    }

    public override void Run(SonicObject o)
    {
        switch (o.Routine)
        {
            case Init: Initialise(o); break;
            case Main:
                if (o.RoutineSecondary == Waiting) Wait(o, new State(o));
                else Drive(o, new State(o));
                ObjectKit.Animate(Ram, o, _anims);
                DisplayOrMarkGone(o);
                break;
            case Exhaust: AnimateExhaust(o); break;
            case Gone: Delete(o); break;
        }
    }

    private void Initialise(SonicObject o)
    {
        o.Mappings = Mappings;
        o.ArtTile = 0x555;
        o.RenderFlags = Render.LevelCoords;
        o.Priority = 4;
        o.WidthPixels = 0x14;
        if (o.Anim != AnimStill)
        {
            o.Routine = Exhaust;
            AnimateExhaust(o);
            return;
        }
        // Fall until it lands.
        o.YRadius = 0x0E;
        o.XRadius = 8;
        o.CollisionFlags = 0x0C;
        ObjectKit.MoveAndFall(o);
        int distance = (short)ObjectKit.CheckFloorDist(Ram, Ctx.Collision, o);
        if (distance >= 0) return;
        o.Y += distance;
        o.YVel = 0;
        o.Routine = Main;
        o.Status ^= ObjStatus.XFlip;
    }

    /// <summary>0x1519B6: wait, then turn round and drive.</summary>
    private void Wait(SonicObject o, State s)
    {
        s.WaitTimer--;
        if ((short)s.WaitTimer >= 0) return;
        o.RoutineSecondary = Driving;
        o.Anim = AnimDrive;
        bool wasFlipped = (o.Status & ObjStatus.XFlip) != 0;
        o.Status ^= ObjStatus.XFlip;
        o.XVel = wasFlipped ? (short)-0x100 : (short)0x100;
    }

    /// <summary>0x1519DA: drive along the floor; stop at a ledge or step. Puff every 16 frames.</summary>
    private void Drive(SonicObject o, State s)
    {
        ObjectKit.Move(o);
        int distance = (short)ObjectKit.CheckFloorDist(Ram, Ctx.Collision, o);
        if (distance < -8 || distance >= 0x0C)
        {
            o.RoutineSecondary = Waiting;
            s.WaitTimer = WaitFrames;
            o.XVel = 0;
            o.Anim = AnimStill;
            return;
        }
        o.Y += distance;
        s.PuffTimer--;
        if ((sbyte)s.PuffTimer >= 0) return;
        s.PuffTimer = 0x0F;
        if (Spawn() is not { } puff) return;
        puff.Id = o.Id;
        puff.X = o.X;
        puff.Y = o.Y;
        puff.Status = o.Status;
        puff.Anim = AnimExhaust;
    }

    private void AnimateExhaust(SonicObject o)
    {
        ObjectKit.Animate(Ram, o, _anims);   // the animation ends by advancing to Gone
        Display(o);
    }
}
