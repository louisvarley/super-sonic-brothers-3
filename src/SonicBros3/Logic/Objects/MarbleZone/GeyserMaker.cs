namespace SonicBros3.Logic;

/// <summary>
/// The lava geyser launcher, object $4C (ROM 0x15546C, Sonic 1's Marble Zone). Every 120 frames, when Sonic is up to
/// $170 px above it, it erupts (an animation, then a geyser: <see cref="Geyser"/>). Subtype 0 is the one a push block
/// starts at set places: it launches that block upwards at -$580, and goes when done.
/// </summary>
public sealed class GeyserMaker(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x155746;
    private readonly byte[] _anims = ctx.Art["mz-geyser"].Animations!;
    private const int Init = 0, Waiting = 2, Bubbling = 4, Erupt = 6, Erupting = 8, Done = 0x0A;
    private const int GeyserId = 0x4D;
    private const int AnimIdle = 0, AnimErupt = 1, AnimEruptFromBlock = 4;

    /// <summary>The launcher's own variables.</summary>
    private readonly struct State(SonicObject o)
    {
        /// <summary>$32: frames until it checks again; $34: the interval (120).</summary>
        public int Timer { get => o.Word(0x32); set => o.SetWord(0x32, value); }
        public int Interval { get => o.Word(0x34); set => o.SetWord(0x34, value); }
        /// <summary>$3C: the push block that started it (subtype 0).</summary>
        public int Block => (int)(o.Long(0x3C) & 0xFFFF);
    }

    public override void Run(SonicObject o)
    {
        var s = new State(o);
        switch (o.Routine)
        {
            case Init:
                o.Routine = Waiting;
                o.Mappings = Mappings;
                o.ArtTile = 0xE3A8;
                o.RenderFlags = Render.LevelCoords;
                o.Priority = 1;
                o.WidthPixels = 0x38;
                s.Interval = 0x78;
                goto case Waiting;
            case Waiting:
            {
                s.Timer--;
                if ((short)s.Timer >= 0) break;
                s.Timer = s.Interval;
                int sonicY = Sonic.Y;
                if (sonicY >= o.Y || sonicY < ((o.Y - 0x170) & 0xFFFF)) break;
                o.Routine = Bubbling;
                break;
            }
            case Bubbling:   // the bubbling animation ends by advancing to Erupt
                if (o.Subtype != 0) { o.Routine = Erupt; break; }
                AnimateAndDisplay(o);
                break;
            case Erupt:
            {
                o.Routine = Erupting;
                if (Spawn() is { } geyser)
                {
                    geyser.Id = GeyserId;
                    geyser.X = o.X;
                    geyser.Y = o.Y;
                    geyser.Subtype = o.Subtype;
                    new Geyser.State(geyser).Maker = o.Address;
                }
                o.Anim = AnimErupt;
                if (o.Subtype != 0) o.Anim = AnimEruptFromBlock;
                else
                {
                    // Launch the push block.
                    var block = new SonicObject(Ram, s.Block);
                    block.Status |= 2;   // in the air
                    block.YVel = -0x580;
                }
                AnimateAndDisplay(o);
                break;
            }
            case Erupting: AnimateAndDisplay(o); break;
            case Done:   // set by the geyser when it ends
                o.Anim = AnimIdle;
                o.Routine = Waiting;
                if (o.Subtype == 0) { o.Clear(); return; }
                break;
        }
        if (o.Id != 0 && OutOfRange(o.X)) o.Clear();
    }

    private void AnimateAndDisplay(SonicObject o)
    {
        ObjectKit.Animate(Ram, o, _anims);
        Display(o);
    }
}
