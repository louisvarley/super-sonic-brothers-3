namespace SonicBros3.Logic;

/// <summary>
/// The fire on the grass, object $19 (ROM 0x1535F4, Sonic 1's Marble Zone). It hurts. The spreading piece (subtype 0)
/// follows the platform's height profile rightwards 1 px a frame, leaving a still piece (subtype 1) every 16 px for
/// $80 px. Every piece sinks with the platform.
/// </summary>
public sealed class GrassFire(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x1536E8;
    private readonly byte[] _anims = ctx.Art["mz-fire"].Animations!;
    private const int Init = 0, Spreading = 2, StillPiece = 4;
    private const int SfxFire = 0xC8;

    /// <summary>A fire piece's variables.</summary>
    public readonly struct State(SonicObject o)
    {
        /// <summary>$2A: where the spreading started.</summary>
        public int StartX { get => o.Word(0x2A); set => o.SetWord(0x2A, value); }
        /// <summary>$2C: its y on the platform before sinking.</summary>
        public int BaseY { get => o.Word(0x2C); set => o.SetWord(0x2C, value); }
        /// <summary>$30: the platform's height profile.</summary>
        public int Profile { get => (int)o.Long(0x30); set => o.SetLong(0x30, value); }
        /// <summary>$38: the platform's slot (long, $FFFFxxxx).</summary>
        public int Platform { get => (int)(o.Long(0x38) & 0xFFFF); set => o.SetLong(0x38, 0xFFFF0000u | (uint)value); }
        /// <summary>$3C: how far the platform has sunk (set by the platform).</summary>
        public int Sink { get => o.Word(0x3C); set => o.SetWord(0x3C, value); }
    }

    public override void Run(SonicObject o)
    {
        var s = new State(o);
        switch (o.Routine)
        {
            case Init:
                o.Routine = Spreading;
                o.Mappings = Mappings;
                o.ArtTile = 0x345;
                s.StartX = o.X;
                o.RenderFlags = Render.LevelCoords;
                o.Priority = 1;
                o.CollisionFlags = 0x8B;
                o.WidthPixels = 8;
                Ctx.Sound.PlaySound(SfxFire);
                if (o.Subtype != 0) { o.Routine = StillPiece; goto case StillPiece; }
                goto case Spreading;
            case Spreading:
            {
                int along = (o.X - s.StartX + 0x0C) & 0xFFFF;
                int height = GrassPlatform.Height(Ctx.Art, s.Profile, along >> 1);
                int topY = (s.BaseY - height) & 0xFFFF;
                o.Y = topY + s.Sink;
                if (along >= 0x84) break;
                o.XLong += 0x10000;   // 1 px right
                if (along >= 0x80) break;
                // Every 16 px (when x + 8 is a multiple of 16 exactly), leave a still piece behind.
                if (((o.XLong + 0x80000) & 0xFFFFF) != 0) break;
                int slot = ObjectKit.FindFreeSlotAfter(Ram, o);
                if (slot < 0) break;
                var piece = new SonicObject(Ram, slot);
                var p = new State(piece);
                piece.Id = o.Id;
                piece.X = o.X;
                p.BaseY = topY;
                p.Sink = s.Sink;
                piece.Subtype = 1;
                GrassPlatform.AddFire(Ram, s.Platform, slot);
                break;
            }
            case StillPiece:
                o.Y = s.BaseY + s.Sink;
                break;
        }
        ObjectKit.Animate(Ram, o, _anims);
        Display(o);
    }
}
