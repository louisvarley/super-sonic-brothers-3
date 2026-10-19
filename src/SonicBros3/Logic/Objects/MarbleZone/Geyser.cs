namespace SonicBros3.Logic;

/// <summary>
/// The lava geyser, object $4D (ROM 0x15555C, Sonic 1's Marble Zone). A lava head (subtype 0 rises at -$500 and falls
/// back; 1 starts $250 higher and pours down) with a column that hurts, $60 below it, stretching with it and picking
/// its frame by its length. Subtype 1 also has a flipped lower piece. It ends when back at its start height.
/// </summary>
public sealed class Geyser(ObjectContext ctx) : GameObject(ctx)
{
    public const int Id = 0x4D;
    private readonly byte[] _anims = ctx.Art["mz-geyser"].Animations!;
    private const int Init = 0, Head = 2, Column = 4;
    private static readonly short[] Speeds = [-0x500, 0];
    private const int Gravity = 0x18;
    private const int SfxGeyser = 0xC8;

    /// <summary>A geyser piece's variables.</summary>
    public readonly struct State(SonicObject o)
    {
        /// <summary>$30: the height it ends at.</summary>
        public int EndY { get => o.Word(0x30); set => o.SetWord(0x30, value); }
        /// <summary>$3C: the launcher's slot (the head's, for the column).</summary>
        public int Maker { get => (int)(o.Long(0x3C) & 0xFFFF); set => o.SetLong(0x3C, 0xFFFF0000u | (uint)value); }
    }

    public override void Run(SonicObject o)
    {
        var s = new State(o);
        switch (o.Routine)
        {
            case Init: Initialise(o, s); goto case Head;
            case Head: UpdateHead(o, s); break;
            case Column: UpdateColumn(o, s); break;
            default: o.Clear(); break;
        }
        Display(o);   // 0x15556A (DisplaySprite skips an emptied slot)
    }

    private void Initialise(SonicObject o, State s)
    {
        o.Routine = Head;
        s.EndY = o.Y;
        if (o.Subtype != 0) o.Y -= 0x250;
        int type = o.Subtype;
        o.YVel = type < Speeds.Length ? Speeds[type] : (short)0;
        Setup(o, o);
        int slot = ObjectKit.FindFreeSlotAfter(Ram, o);
        if (slot >= 0)
        {
            // The column, $60 below the head.
            var column = new SonicObject(Ram, slot);
            var c = new State(column);
            Setup(column, o);
            column.Y += 0x60;
            c.EndY = s.EndY + 0x60;
            column.CollisionFlags = 0x93;   // hurts
            column.YRadius = 0x80;
            column.RenderFlags |= Render.UseYRadius;
            column.Routine += 4;            // Column
            c.Maker = o.Address;
            if (o.Subtype != 0)
            {
                int lowerSlot = ObjectKit.FindFreeSlotAfter(Ram, o);
                if (lowerSlot >= 0)
                {
                    // Pouring down: a flipped lower piece.
                    var lower = new SonicObject(Ram, lowerSlot);
                    Setup(lower, o);
                    lower.Routine += 2;
                    lower.ArtTile |= 0x1000;   // y-flipped
                    lower.Y += 0x100;
                    lower.Priority = 0;
                    new State(lower).EndY = s.EndY;
                    new State(lower).Maker = s.Maker;
                    o.Subtype = 0;
                }
            }
        }
        Ctx.Sound.PlaySound(SfxGeyser);
    }

    /// <summary>0x1555B0.</summary>
    private static void Setup(SonicObject piece, SonicObject head)
    {
        piece.Id = Id;
        piece.Mappings = GeyserMaker.Mappings;
        piece.ArtTile = 0x63A8;
        piece.RenderFlags = Render.LevelCoords;
        piece.WidthPixels = 0x20;
        piece.X = head.X;
        piece.Y = head.Y;
        piece.Subtype = head.Subtype;
        piece.Priority = 1;
        piece.Anim = head.Subtype != 0 ? 2 : 5;
    }

    /// <summary>0x155668: rise and fall; back at the start, the launcher is done.</summary>
    private void UpdateHead(SonicObject o, State s)
    {
        o.YVel += Gravity;
        if (s.EndY < o.Y)
        {
            o.Routine += 4;
            new SonicObject(Ram, s.Maker).Anim = o.Subtype == 0 ? 3 : 1;
        }
        ObjectKit.Move(o);
        ObjectKit.Animate(Ram, o, _anims);
        if (OutOfRange(o.X)) o.Clear();
    }

    /// <summary>0x1556E4: hang below the head; the frame by its length.</summary>
    private void UpdateColumn(SonicObject o, State s)
    {
        var head = new SonicObject(Ram, s.Maker);
        if (head.Routine == 6) { o.Clear(); return; }   // the head has ended
        int y = (head.Y + 0x60) & 0xFFFF;
        o.Y = y;
        int length = (short)-(y - s.EndY);
        int firstFrame = 8;
        if (length < 0x40) firstFrame = 0x0B;
        if (length > 0x80) firstFrame = 0x0E;
        o.AnimFrameDuration--;
        if ((sbyte)o.AnimFrameDuration < 0)
        {
            o.AnimFrameDuration = 7;
            o.AnimFrame++;
            if (o.AnimFrame >= 2) o.AnimFrame = 0;
        }
        o.MappingFrame = o.AnimFrame + firstFrame;
        if (OutOfRange(o.X)) o.Clear();
    }
}
