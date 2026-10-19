namespace SonicBros3.Logic;

/// <summary>
/// The egg prison (animal capsule), object $3E (ROM 0x433F0). Four parts: the body, the button on top, the lid and
/// an invisible animal emitter. Standing on the button stops the timer; the lid is blown off (with an explosion),
/// 8 animals spill out, then the emitter releases one every 8 frames for 3 seconds. When no animal is left, the
/// results start (0x1D1D4).
/// </summary>
public sealed class EggPrison(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x43658;
    private readonly byte[] _anims = ctx.Art["egg-prison"].Animations!;

    private const int Init = 0, Body = 2, Button = 4, Lid = 6, Emitter = 8, WaitForAnimals = 0x0A;
    private const int ExplosionId = 0x27;
    private const int EmitFrames = 0xB4;

    /// <summary>Per part: y above the body, routine, half width, priority, frame (0x4340A).</summary>
    private static readonly (int Dy, int Routine, int Width, int Priority, int Frame)[] Parts =
        [(0, Body, 0x20, 4, 0), (0x28, Button, 0x10, 5, 4), (0x18, Lid, 8, 3, 5), (0, Emitter, 0x20, 4, 0)];

    /// <summary>The parts' variables.</summary>
    private readonly struct State(SonicObject o)
    {
        /// <summary>$30: the part's resting y (the button sinks 8 px from it).</summary>
        public int BaseY { get => o.Word(0x30); set => o.SetWord(0x30, value); }
        /// <summary>Button, $32: pressed.</summary>
        public bool Pressed { get => o.Word(0x32) != 0; set => o.SetWord(0x32, value ? 1 : 0); }
        /// <summary>Body, $34: frames until the animals spill out.</summary>
        public int Timer { get => o.Word(0x34); set => o.SetWord(0x34, value); }
        /// <summary>Body, $38/$3A/$3C: the button's, the lid's and the emitter's slots.</summary>
        public int ButtonSlot => o.Word(0x38);
        public int LidSlot => o.Word(0x3A);
        public int EmitterSlot => o.Word(0x3C);
    }

    public override void Run(SonicObject o)
    {
        var s = new State(o);
        switch (o.Routine)
        {
            case Init: CreateParts(o); break;
            case Body: RunBody(o, s); break;
            case Button: RunButton(o, s); break;
            case Lid:
                if (o.RoutineSecondary != 0)   // blown off
                {
                    if (!o.OnScreen) { Delete(o); return; }
                    ObjectKit.MoveAndFall(o);
                }
                DisplayOrMarkGone(o);
                break;
            case Emitter: RunEmitter(o); break;
            case WaitForAnimals: WaitForAnimalsToLeave(o); break;
        }
    }

    private void CreateParts(SonicObject o)
    {
        var part = o;
        int nextSlotRef = o.Address + 0x38;
        for (int i = 0; i < Parts.Length; i++)
        {
            if (i > 0)
            {
                int slot = Objects.FindFreeSlot(Ram);
                if (slot < 0) return;
                Ram.W16(nextSlotRef, slot);
                nextSlotRef += 2;
                part = new SonicObject(Ram, slot);
            }
            var p = Parts[i];
            part.Id = o.Id;
            part.X = o.X;
            part.Y = o.Y - p.Dy;
            new State(part).BaseY = part.Y;
            part.Mappings = Mappings;
            part.ArtTile = 0x2680;
            part.RenderFlags = Render.OnScreen | Render.LevelCoords;
            part.Routine = p.Routine;
            part.WidthPixels = p.Width;
            part.Priority = p.Priority;
            part.MappingFrame = p.Frame;
        }
    }

    /// <summary>0x43480.</summary>
    private void RunBody(SonicObject o, State s)
    {
        switch (o.RoutineSecondary)
        {
            case 0:   // wait for the button
            {
                if (!new State(new SonicObject(Ram, s.ButtonSlot)).Pressed) break;
                var lid = new SonicObject(Ram, s.LidSlot);
                if (Spawn() is { } explosion)
                {
                    explosion.Id = ExplosionId;
                    explosion.Routine = Explosion.FromMonitor;
                    explosion.X = lid.X;
                    explosion.Y = lid.Y;
                }
                lid.YVel = -0x400;
                lid.XVel = 0x800;
                lid.RoutineSecondary += 2;
                s.Timer = 0x1D;
                o.RoutineSecondary = 2;
                if (Ram.U16(Vars.CurrentZoneAndAct) == 0x1800)
                {
                    // Lock the camera where it is.
                    int x = Ram.U16(Vars.CameraXPos);
                    foreach (int a in new[] { Vars.CameraMinXPos, Vars.TailsMinXPos, Vars.CameraMaxXPos, Vars.TailsMaxXPos }) Ram.W16(a, x);
                }
                break;
            }
            case 2:   // wait, then release 8 animals and start the emitter
                s.Timer--;
                if ((short)s.Timer >= 0) break;
                o.Anim = 1;
                int delay = 0x9A, dx = -0x1C;
                for (int i = 0; i < 8; i++)
                {
                    if (Spawn() is not { } animal) break;
                    Animal.SpawnFromPrison(animal, o.X + dx, o.Y, delay);
                    dx += 7;
                    delay -= 8;
                }
                var emitter = new SonicObject(Ram, s.EmitterSlot);
                emitter.SetWord(0x1E, EmitFrames);
                emitter.RoutineSecondary += 2;
                o.RoutineSecondary = 4;
                break;
        }
        Ctx.Solid.SolidObject(o, 0x2B, 0x18, 0x18, o.X);
        ObjectKit.Animate(Ram, o, _anims);
        DisplayOrMarkGone(o);
    }

    /// <summary>0x43578.</summary>
    private void RunButton(SonicObject o, State s)
    {
        Ctx.Solid.SolidObject(o, 0x1B, 8, 8, o.X);
        o.Y = s.BaseY;
        if ((o.Status & (ObjStatus.StandingP1 | ObjStatus.StandingP2)) != 0)
        {
            o.Y += 8;
            Ram.W8(Vars.UpdateHudTimer, 0);   // stop the timer
            s.Pressed = true;
        }
        DisplayOrMarkGone(o);
    }

    /// <summary>0x435CC: release an animal every 8 frames for 180 frames.</summary>
    private void RunEmitter(SonicObject o)
    {
        if (o.RoutineSecondary == 0) return;
        if ((Ram.U8(Vars.VintRuncountLow) & 7) == 0 && Spawn() is { } a)
        {
            uint r = ObjectKit.RandomNumber(Ram);
            int dx = (int)(r & 0x1F) - 6;
            if ((short)Ram.U16(Vars.RngSeed + 2) < 0) dx = -dx;   // the new seed's low word
            Animal.SpawnFromPrison(a, o.X + dx, o.Y, 0x0C);
        }
        int timer = o.Word(0x1E) - 1;
        o.SetWord(0x1E, timer);
        if (o.Word(0x1E) != 0) return;
        o.Routine = WaitForAnimals;
        o.SetWord(0x1E, EmitFrames);
    }

    /// <summary>0x43628.</summary>
    private void WaitForAnimalsToLeave(SonicObject o)
    {
        for (int a = Objects.FirstDynamicSlot; a < Objects.FirstDynamicSlot + Objects.DynamicSlots * Objects.SlotSize; a += Objects.SlotSize)
            if (Ram.U8(a) == Animal.Id) return;
        Signpost.StartResults(Ctx);
        Delete(o);
    }
}
