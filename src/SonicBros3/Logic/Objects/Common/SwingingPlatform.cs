namespace SonicBros3.Logic;

/// <summary>
/// The swinging platform, object $15 (ROM 0x12750): the plain swing (subtype high nibble 0) used in Green Hill. The
/// subtype's low nibble is the chain length.
///
/// The chain is a separate multi-sprite object (displayed in priority list 4). The platform hangs from the anchor
/// (where it was placed) and follows the oscillating angle <c>Oscillation.Value(6)</c>, mirrored when x-flipped.
/// Positions are recomputed only when the angle changes.
/// </summary>
public sealed class SwingingPlatform(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x12E28;
    private const int Init = 0, Main = 2;
    private const int LinkSpacing = 0x10;

    /// <summary>The platform's own variables.</summary>
    private readonly struct State(SonicObject o)
    {
        /// <summary>$2E: the angle the positions were computed for.</summary>
        public int LastAngle { get => o.Byte(0x2E); set => o.SetByte(0x2E, value); }
        /// <summary>$30: the chain object's slot (long, $FFFFxxxx).</summary>
        public int ChainSlot { get => (int)(o.Long(0x30) & 0xFFFF); set => o.SetLong(0x30, 0xFFFF0000u | (uint)value); }
        /// <summary>$38, $3A: the anchor.</summary>
        public int AnchorY { get => o.Word(0x38); set => o.SetWord(0x38, value); }
        public int AnchorX { get => o.Word(0x3A); set => o.SetWord(0x3A, value); }
    }

    public override void Run(SonicObject o)
    {
        if ((o.RenderFlags & Render.MultiSprite) != 0) { SpriteBuilder.DisplaySpriteAt(Ram, o, 0x200); return; }   // the chain
        var s = new State(o);
        switch (o.Routine)
        {
            case Init: Initialise(o, s); Swinging(o, s); break;
            case Main: Swinging(o, s); break;
            default: throw new UnportedException(0x12B64, $"swinging platform routine {o.Routine}");
        }
    }

    private void Initialise(SonicObject o, State s)
    {
        o.Routine = Main;
        o.Mappings = 0x12D1A;
        o.ArtTile = 0x43E3;
        o.RenderFlags = Render.LevelCoords;
        o.Priority = 3;
        o.WidthPixels = 0x20;
        o.YRadius = 0x10;
        s.AnchorY = o.Y;
        s.AnchorX = o.X;
        int zone = CurrentZone;
        if (zone is 0x0B or 0x0F or 0x1A or 0x1B || Ram.U8(Vars.HackZoneFlag) == 0)
            throw new UnportedException(0x127B6, $"swinging platform for zone {zone:X2}");
        o.Mappings = Mappings;
        o.ArtTile = 0x4380;
        o.WidthPixels = 0x18;
        o.YRadius = 8;
        if ((o.Subtype & 0x80) != 0) throw new UnportedException(0x1287E, "swinging platform with subtype bit 7");
        int type = o.Subtype & 0x70, links = o.Subtype & 0x0F;
        if (type != 0) throw new UnportedException(0x128B0, $"swinging platform type {type:X2}");
        int x = o.X, y = o.Y;
        int slot = ObjectKit.FindFreeSlotAfter(Ram, o);
        if (slot >= 0)
        {
            // The chain: one child sprite per link, hanging straight down.
            var chain = new SonicObject(Ram, slot);
            var multi = new MultiSprite(chain);
            chain.Id = o.Id;
            chain.Mappings = o.Mappings;
            chain.ArtTile = o.ArtTile;
            chain.RenderFlags = Render.MultiSprite | Render.LevelCoords;
            multi.Width = 0x48;
            multi.ChildCount = links;
            for (int i = 0; i < links; i++)
            {
                multi.SetChild(i, x, y, 1);
                y = (y + LinkSpacing) & 0xFFFF;
            }
            multi.SetChildFrame(0, 2);   // the anchor
            // The original swaps child 4's position with the main sprite's (the chain object's own x/y).
            chain.X = multi.ChildX(4);
            chain.Y = multi.ChildY(4);
            multi.SetChildX(4, x);
            multi.SetChildY(4, y);
            multi.MainFrame = 1;
            y = (y + 8) & 0xFFFF;
            o.Y = y;
            multi.Height = 0x50;
            chain.RenderFlags |= Render.UseYRadius;
            s.ChainSlot = slot;
        }
        o.SetWord(0x26, 0x8000);   // angle (and $27)
        o.SetWord(0x3E, 0);
        o.Subtype = type;
    }

    /// <summary>0x1297A.</summary>
    private void Swinging(SonicObject o, State s)
    {
        int oldX = o.X;
        Swing(o, s);
        Ctx.Solid.PlatformObject(o, o.WidthPixels, 0, (byte)o.YRadius + 1, oldX);
        if (TwoPlayerMode || !OutOfRange(s.AnchorX)) { Display(o); return; }
        Delete(new SonicObject(Ram, s.ChainSlot));
        Delete(o);
    }

    /// <summary>0x1299A: place the chain links and the platform along the current angle.</summary>
    private void Swing(SonicObject o, State s)
    {
        int angle = Ram.U8(Oscillation.Value(6));
        if (s.LastAngle == angle) return;
        s.LastAngle = angle;
        if ((o.Status & ObjStatus.XFlip) != 0) angle = (-angle + 0x80) & 0xFFFF;
        var (sin, cos) = MdMath.CalcSine(angle & 0xFF);
        int y0 = s.AnchorY, x0 = s.AnchorX;
        var chain = new SonicObject(Ram, s.ChainSlot);
        var multi = new MultiSprite(chain);
        int count = multi.ChildCount;
        if (count == 0) return;
        // 16.16 steps of 16 px along the angle.
        int stepY = (sin << 16) >> 4, stepX = (cos << 16) >> 4;
        int dy = 0, dx = 0;
        for (int i = 0; i < count; i++)
        {
            multi.SetChildX(i, (dx >> 16) + x0);
            multi.SetChildY(i, (dy >> 16) + y0);
            dy += stepY;
            dx += stepX;
        }
        // The end of the chain goes in child 4's place; child 4's old position becomes the main sprite's.
        int oldX = multi.ChildX(4), oldY = multi.ChildY(4);
        multi.SetChildX(4, (dx >> 16) + x0);
        multi.SetChildY(4, (dy >> 16) + y0);
        chain.X = oldX;
        chain.Y = oldY;
        // The platform hangs half a link further on.
        dy += stepY >> 1;
        dx += stepX >> 1;
        o.Y = (dy >> 16) + y0;
        o.X = (dx >> 16) + x0;
    }
}
