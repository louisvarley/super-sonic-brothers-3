namespace SonicBros3.Logic;

/// <summary>
/// The glass block, object $30 (ROM 0x153B2A, Sonic 1's Marble Zone): a tall (subtype 0-2) or short (3 and up) block
/// plus its shine (a second object, subtype + 8, which follows the block's height). Subtype bits 0-2:
///   1, 2  bob with oscillator 4 (opposite ways)
///   3     sinks a step each time it is stood on again
///   4     drops when its button is pressed (switch flag: subtype high nibble)
/// </summary>
public sealed class GlassBlock(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x153D7E;
    private const int Init = 0, TallBlock = 2, TallShine = 4, ShortBlock = 6, ShortShine = 8;

    /// <summary>The block and its shine: routine, y offset, frame (0x153B60 / 0x153B66).</summary>
    private static readonly (int Routine, int Dy, int Frame)[] Tall = [(TallBlock, 0, 0), (TallShine, 0, 1)],
        Short = [(ShortBlock, 0, 2), (ShortShine, 0, 1)];

    /// <summary>The block's own variables.</summary>
    private readonly struct State(SonicObject o)
    {
        /// <summary>$30: the y it moves from.</summary>
        public int BaseY { get => o.Word(0x30); set => o.SetWord(0x30, value); }
        /// <summary>$32: how high it stands (falls toward 0).</summary>
        public int Height { get => o.Word(0x32); set => o.SetWord(0x32, value); }
        /// <summary>$34: bit 0 stood on last frame (or: the button was pressed); bit 7 sinking a step.</summary>
        public int Flags { get => o.Byte(0x34); set => o.SetByte(0x34, value); }
        /// <summary>$35 bit 0: stood on before (the next landing sinks it).</summary>
        public int StoodBefore { get => o.Byte(0x35); set => o.SetByte(0x35, value); }
        /// <summary>$36: lines left to sink in this step.</summary>
        public int StepLeft { get => o.Word(0x36); set => o.SetWord(0x36, value); }
        /// <summary>$38: frames before the step starts.</summary>
        public int StepDelay { get => o.Byte(0x38); set => o.SetByte(0x38, value); }
        /// <summary>$3C: the block's slot, for the shine (long, $FFFFxxxx).</summary>
        public int Block { get => (int)(o.Long(0x3C) & 0xFFFF); set => o.SetLong(0x3C, 0xFFFF0000u | (uint)value); }
    }

    public override void Run(SonicObject o)
    {
        var s = new State(o);
        switch (o.Routine)
        {
            case Init: CreatePieces(o, s); break;
            case TallBlock:
                Move(o, s);
                Ctx.Solid.SolidObject(o, 0x2B, 0x48, 0x49, o.X);
                break;
            case TallShine:
                s.Height = new State(new SonicObject(Ram, s.Block)).Height;
                Move(o, s);
                break;
            case ShortBlock:
                Move(o, s);
                Ctx.Solid.SolidObject(o, 0x2B, 0x38, 0x39, o.X);
                break;
            case ShortShine:
            {
                var block = new SonicObject(Ram, s.Block);
                s.Height = new State(block).Height;
                s.BaseY = block.Y;
                Move(o, s);
                break;
            }
        }
        if (OutOfRange(o.X)) { o.Clear(); return; }
        Display(o);
    }

    /// <summary>0x153B6C: the block and its shine, then the block's routine.</summary>
    private void CreatePieces(SonicObject o, State s)
    {
        var pieces = Tall;
        o.YRadius = 0x48;
        if (o.Subtype >= 3) { pieces = Short; o.YRadius = 0x38; }
        var piece = o;
        for (int i = 0; i < 2; i++)
        {
            if (i > 0)
            {
                int slot = ObjectKit.FindFreeSlotAfter(Ram, o);
                if (slot < 0) break;
                piece = new SonicObject(Ram, slot);
            }
            var (routine, dy, frame) = pieces[i];
            var p = new State(piece);
            piece.Routine = routine;
            piece.Id = o.Id;
            piece.X = o.X;
            piece.Y = o.Y + dy;
            piece.Mappings = Mappings;
            piece.ArtTile = 0xC38E;
            piece.RenderFlags = Render.LevelCoords;
            p.BaseY = piece.Y;
            piece.Subtype = o.Subtype;
            piece.WidthPixels = 0x20;
            piece.Priority = 4;
            piece.MappingFrame = frame;
            p.Block = o.Address;
            if (i == 1)
            {
                // The shine.
                piece.WidthPixels = 0x10;
                piece.Priority = 3;
                piece.Subtype = (piece.Subtype + 8) & 0x0F;
            }
        }
        s.Height = 0x90;
        o.RenderFlags |= Render.UseYRadius;
        Move(o, s);
        Ctx.Solid.SolidObject(o, 0x2B, 0x48, 0x49, o.X);   // 0x153C10 (the tall block's routine, as the original falls into)
    }

    /// <summary>0x153C62.</summary>
    private void Move(SonicObject o, State s)
    {
        int type = o.Subtype;
        bool shine = (type & 8) != 0;
        int rise;
        switch (type & 7)
        {
            case 1:
            case 2:
            {
                rise = Ram.U8(Oscillation.Value(4));
                const int Range = 0x40;
                if ((type & 7) == 2) rise = -rise + Range;
                if (shine)
                {
                    rise = (-rise + Range) & 0xFFFF;
                    rise = (rise & 0xFF00) | ((rise & 0xFF) >> 1);   // lsr.b: half, on the low byte only
                    rise += 0x20;
                }
                break;
            }
            case 3:
                if (shine) { rise = Ram.U8(Oscillation.Value(4)) - 0x10; break; }
                SinkWhenStoodOn(o, s);
                rise = s.Height;
                break;
            case 4:
                if (shine) { rise = Ram.U8(Oscillation.Value(4)) - 0x10; break; }
                if (s.Flags == 0 && Ram.U8(Vars.ButtonVineTrigger + (type >> 4)) != 0) s.Flags = 1;
                if (s.Flags != 0 && s.Height != 0) s.Height -= 2;
                rise = s.Height;
                break;
            default:
                return;
        }
        o.Y = s.BaseY - rise;
    }

    /// <summary>Subtype 3: each new landing (after the first) sinks it one step (16 lines, $40 for the last).</summary>
    private static void SinkWhenStoodOn(SonicObject o, State s)
    {
        const int Latched = 1, Sinking = 0x80;
        if ((o.Status & ObjStatus.StandingP1) == 0) s.Flags &= ~Latched;
        else if (s.Flags == 0)
        {
            // A new landing.
            s.Flags = Latched;
            bool before = (s.StoodBefore & 1) != 0;
            s.StoodBefore |= 1;
            if (before)
            {
                s.Flags |= Sinking;
                s.StepLeft = 0x10;
                s.StepDelay = 0x0A;
                if (s.Height == 0x40) s.StepLeft = 0x40;
            }
        }
        if ((s.Flags & Sinking) == 0) return;
        if (s.StepDelay != 0)
        {
            s.StepDelay--;
            if (s.StepDelay != 0) return;
        }
        if (s.Height == 0) { s.Flags &= ~Sinking; return; }
        s.Height--;
        s.StepLeft--;
        if (s.StepLeft == 0) s.Flags &= ~Sinking;
    }
}
