namespace SonicBros3.Logic;

/// <summary>
/// The chained stomper, object $31 (ROM 0x153E4A, Sonic 1's Marble Zone): the block, spikes underneath (except for
/// subtype $2x), its chain and the fitting at the top: four objects with this id.
///
/// Subtype bit 7 picks a switch and a new subtype from a table. The low nibble:
///   0           drops while its switch is off, climbs while it is on
///   1, 2, 4, 6  drop, wait a second, climb
///   3, 5        wait for Sonic within $90 px, then become the next type
/// The extension runs from 0 to the type's limit. Anyone standing on it while it is mostly retracted is crushed
/// against the ceiling.
/// </summary>
public sealed class ChainedStomper(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x154182;
    private const int Init = 0, Block = 2, Spikes = 4, Fitting = 6, Chain = 8;
    private const int Gravity = 0x70, ClimbSpeed = 0x80;
    private const int SfxThud = 0xBD, SfxRattle = 0xC7;

    /// <summary>Subtype bit 7: the switch number and the new subtype.</summary>
    private static readonly (int Switch, int Subtype)[] SwitchTable = [(0, 0), (1, 0)];
    /// <summary>The pieces: routine, y offset from the block, frame.</summary>
    private static readonly (int Routine, int Dy, int Frame)[] Pieces = [(Block, 0, 0), (Spikes, 0x1C, 1), (Chain, -0x34, 3), (Fitting, -0x10, 2)];
    /// <summary>How far each type extends (8.8 px).</summary>
    private static readonly int[] Limits = [0x7000, 0xA000, 0x5000, 0x7800, 0x3800, 0x5800, 0xB800];
    /// <summary>The block's half width and frame by subtype bits 4-6.</summary>
    private static readonly (int Width, int Frame)[] Sizes = [(0x38, 0), (0x30, 9), (0x10, 0x0A)];

    /// <summary>A piece's variables.</summary>
    private readonly struct State(SonicObject o)
    {
        /// <summary>$30: the piece's y with the stomper retracted.</summary>
        public int BaseY { get => o.Word(0x30); set => o.SetWord(0x30, value); }
        /// <summary>$32: the extension (8.8; the high byte is whole pixels).</summary>
        public int Extension { get => o.Word(0x32); set => o.SetWord(0x32, value); }
        public int ExtensionPixels => o.Byte(0x32);
        /// <summary>$34: how far it extends.</summary>
        public int Limit { get => o.Word(0x34); set => o.SetWord(0x34, value); }
        /// <summary>$36: climbing back; $38: frames to wait at the bottom.</summary>
        public bool Climbing { get => o.Word(0x36) != 0; set => o.SetWord(0x36, value ? 1 : 0); }
        public int Wait { get => o.Word(0x38); set => o.SetWord(0x38, value); }
        /// <summary>$3A: its switch.</summary>
        public int Switch { get => o.Byte(0x3A); set => o.SetByte(0x3A, value); }
        /// <summary>$3C: the block's slot, for the other pieces (long, $FFFFxxxx).</summary>
        public int Parent { get => (int)(o.Long(0x3C) & 0xFFFF); set => o.SetLong(0x3C, 0xFFFF0000u | (uint)value); }
    }

    public override void Run(SonicObject o)
    {
        var s = new State(o);
        switch (o.Routine)
        {
            case Init: CreatePieces(o, s); goto case Block;
            case Block: UpdateBlock(o, s); break;
            case Chain:   // 0x153FD2: the chain's frame shows its length
            {
                o.YRadius = 0x80;
                o.RenderFlags |= Render.UseYRadius;
                var block = new State(new SonicObject(Ram, s.Parent));
                o.MappingFrame = (block.ExtensionPixels >> 5) + 3;
                goto case Spikes;
            }
            case Spikes:   // 0x153FEE: the spikes and the chain follow the block
                o.Y = s.BaseY + new State(new SonicObject(Ram, s.Parent)).ExtensionPixels;
                Display(o);
                break;
            case Fitting: Display(o); break;
        }
        if (OutOfRange(o.X)) o.Clear();
    }

    /// <summary>0x153E80.</summary>
    private void CreatePieces(SonicObject o, State s)
    {
        int subtype = o.Subtype;
        if ((subtype & 0x80) != 0)
        {
            int i = subtype & 0x7F;
            var entry = i < SwitchTable.Length ? SwitchTable[i] : (Switch: 0, Subtype: 0);
            s.Switch = entry.Switch;
            subtype = entry.Subtype;
            o.Subtype = subtype;
        }
        int type = subtype & 0x0F;
        int limit = type < Limits.Length ? Limits[type] : 0;
        if (type == 0) s.Extension = limit;   // switch-driven: starts extended
        // 0x153EAE: this slot, then three more from the free slots after it, take the pieces in order. For subtype
        // $2x the spikes are skipped (that slot takes the next piece instead).
        var piece = o;
        int next = 0, left = 3;
        bool allocated = true;
        while (true)
        {
            var (routine, dy, frame) = Pieces[next++];
            var p = new State(piece);
            piece.Routine = routine;
            piece.Id = o.Id;
            piece.X = o.X;
            piece.Y = o.Y + dy;
            piece.Mappings = Mappings;
            piece.ArtTile = 0x300;
            piece.RenderFlags = Render.LevelCoords;
            p.BaseY = piece.Y;
            piece.Subtype = o.Subtype;
            piece.WidthPixels = 0x10;
            p.Limit = limit;
            piece.Priority = 4;
            piece.MappingFrame = frame;
            if (routine == Spikes)
            {
                left--;
                if ((o.Subtype & 0xF0) == 0x20) continue;   // no spikes
                piece.WidthPixels = 0x38;
                piece.CollisionFlags = 0x90;   // hurts
                left++;
            }
            p.Parent = o.Address;
            if (--left < 0) break;
            int slot = ObjectKit.FindFreeSlotAfter(Ram, o);
            if (slot < 0) { allocated = false; break; }
            piece = new SonicObject(Ram, slot);
        }
        if (allocated) piece.Priority = 3;
        int size = ((o.Subtype >> 3) & 0x0E) / 2;
        var (width, sizeFrame) = size < Sizes.Length ? Sizes[size] : Sizes[0];
        o.WidthPixels = width;
        o.MappingFrame = sizeFrame;
    }

    /// <summary>0x153F66: move, stand-on (crushing when nearly retracted), display.</summary>
    private void UpdateBlock(SonicObject o, State s)
    {
        Move(o, s);
        Ram.W16(Vars.ChainStomperY, o.Y);
        Ctx.Solid.SolidObject(o, o.WidthPixels + 0x0B, 0x0C, 0x0D, o.X);
        if ((o.Status & ObjStatus.StandingP1) != 0 && s.ExtensionPixels < 0x10) Ctx.Touch.Kill(Sonic, o);
        if ((o.Status & ObjStatus.StandingP2) != 0 && s.ExtensionPixels < 0x0C) Ctx.Touch.Kill(Tails, o);
        Display(o);
    }

    /// <summary>0x15401C.</summary>
    private void Move(SonicObject o, State s)
    {
        switch (o.Subtype & 0x0F)
        {
            case 0: FollowSwitch(o, s); break;
            case 1 or 2 or 4 or 6: DropAndClimb(o, s); break;
            case 3 or 5:
                if (Math.Abs((short)(Sonic.X - o.X)) < 0x90) o.Subtype++;
                break;
        }
        o.Y = s.BaseY + s.ExtensionPixels;
    }

    /// <summary>0x15403C: climb while its switch is pressed, drop when released.</summary>
    private void FollowSwitch(SonicObject o, State s)
    {
        if (Ram.U8(Vars.ButtonVineTrigger + s.Switch) == 0) { Drop(o, s); return; }
        bool stop = (short)Ram.U16(Vars.ChainStomperY) < 0 && s.ExtensionPixels == 0x10;
        if (!stop && s.Extension != 0)
        {
            Rattle(o);
            int up = s.Extension - ClimbSpeed;
            s.Extension = up < 0 ? 0 : up;
            if (up >= 0) return;
        }
        o.YVel = 0;
    }

    /// <summary>0x1540D8: drop to the limit, wait a second, climb back at $80 a frame.</summary>
    private void DropAndClimb(SonicObject o, State s)
    {
        if (s.Climbing)
        {
            if (s.Wait != 0) { s.Wait--; return; }
            Rattle(o);
            int up = s.Extension - ClimbSpeed;
            if (up >= 0) { s.Extension = up; return; }
            s.Extension = 0;
            o.YVel = 0;
            s.Climbing = false;
            return;
        }
        if (Drop(o, s))
        {
            s.Climbing = true;
            s.Wait = 0x3C;
        }
    }

    /// <summary>Fall toward the limit under gravity; true when it just hit the bottom (with a thud).</summary>
    private bool Drop(SonicObject o, State s)
    {
        int limit = s.Limit;
        if (s.Extension == limit) return false;
        int speed = o.YVel;
        o.YVel += Gravity;
        s.Extension = (s.Extension + speed) & 0xFFFF;
        if (limit > s.Extension) return false;
        s.Extension = limit;
        o.YVel = 0;
        if (o.OnScreen) Ctx.Sound.PlaySound(SfxThud);
        return true;
    }

    private void Rattle(SonicObject o)
    {
        if ((Ram.U8(Vars.VintRuncountLow) & 0x0F) == 0 && o.OnScreen) Ctx.Sound.PlaySound(SfxRattle);
    }
}
