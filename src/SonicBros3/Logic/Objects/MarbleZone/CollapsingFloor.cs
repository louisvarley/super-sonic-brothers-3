namespace SonicBros3.Logic;

/// <summary>
/// The collapsing floor, object $53 (ROM 0x136DE, Sonic 2's; Marble Zone art in the hack's zones). Stood on, it waits
/// 7 frames, then splits into its mapping pieces, each falling after its own delay (two delay tables, by subtype bit
/// 0). Players standing on it drop when the last piece goes.
/// </summary>
public sealed class CollapsingFloor(ObjectContext ctx) : GameObject(ctx)
{
    private const uint HackMappings = 0x13F58;
    private const int Init = 0, Intact = 2, Piece = 4;
    private const int SfxCollapse = 0xB9;
    private readonly SpriteData _sprites = ctx.Art["mz-collapsing-floor"];
    private readonly (int Base, byte[] Data) _delays = ctx.Art.DataTable("mz-collapsing-floor/delays.json");

    /// <summary>The floor's own variables.</summary>
    private readonly struct State(SonicObject o)
    {
        /// <summary>$34: the delay table (the original's ROM pointer).</summary>
        public int Delays { get => (int)o.Long(0x34); set => o.SetLong(0x34, value); }
        /// <summary>$38: frames until it collapses (intact) or falls (piece).</summary>
        public int Timer { get => o.Byte(0x38); set => o.SetByte(0x38, value); }
        /// <summary>$3A: stood on.</summary>
        public bool Touched { get => o.Byte(0x3A) != 0; set => o.SetByte(0x3A, value ? 1 : 0); }
    }

    public override void Run(SonicObject o)
    {
        var s = new State(o);
        switch (o.Routine)
        {
            case Init: Initialise(o, s); goto case Intact;
            case Intact: WhileIntact(o, s); break;
            default: Falling(o, s); break;
        }
    }

    private void Initialise(SonicObject o, State s)
    {
        o.Routine = Intact;
        o.Mappings = 0x13C9E;
        o.ArtTile = 0x42B8;
        o.RenderFlags |= Render.LevelCoords;
        o.Priority = 4;
        s.Timer = 7;
        o.WidthPixels = 0x44;
        int zone = CurrentZone;
        if (zone is not (>= 0x13 and < 0x19)) throw new UnportedException(0x13734, $"collapsing floor in zone {zone:X2}");
        o.Mappings = HackMappings;
        o.ArtTile = 0x4500;
        o.WidthPixels = 0x20;
        s.Delays = (o.Subtype & 1) != 0 ? 0x139B1 : 0x139A9;
    }

    /// <summary>0x13860.</summary>
    private void WhileIntact(SonicObject o, State s)
    {
        if (s.Touched)
        {
            if (s.Timer == 0) { Collapse(o, s); return; }
            s.Timer--;
        }
        if ((o.Status & (ObjStatus.StandingP1 | ObjStatus.StandingP2)) != 0) s.Touched = true;
        ActAsPlatform(o);
        DisplayOrMarkGone(o);
    }

    private void ActAsPlatform(SonicObject o)
    {
        int standHeight = Ram.U8(Vars.HackZoneFlag) != 0 ? 9 : 0x10;
        Ctx.Solid.PlatformObject(o, o.WidthPixels, 0, standHeight, o.X);
    }

    /// <summary>0x138F6: one object per mapping piece (static mappings), each with its delay.</summary>
    private void Collapse(SonicObject o, State s)
    {
        o.MappingFrame++;
        int delays = s.Delays - _delays.Base;
        int frame = o.MappingFrame;
        uint piecePointer = HackMappings + (uint)_sprites.FrameOffsets![frame] + 2;
        int count = _sprites.Frames[frame].Length;
        o.RenderFlags |= Render.StaticMappings;
        var piece = o;
        for (int k = 0; k < count; k++)
        {
            if (k > 0)
            {
                int slot = Objects.FindFreeSlot(Ram);
                if (slot < 0) break;
                piece = new SonicObject(Ram, slot);
                piecePointer += 8;
            }
            piece.Routine = Piece;
            piece.Id = o.Id;
            piece.Mappings = piecePointer;
            piece.RenderFlags = o.RenderFlags;
            piece.X = o.X;
            piece.Y = o.Y;
            piece.ArtTile = o.ArtTile;
            piece.Priority = o.Priority;
            piece.WidthPixels = o.WidthPixels;
            piece.SetByte(0x16, o.Byte(0x16));   // y_radius
            new State(piece).Timer = _delays.Data[delays + k];
            if (piece.Address < o.Address) Display(piece);   // its slot already ran this frame
        }
        Display(o);
        Ctx.Sound.PlaySound(SfxCollapse);
    }

    /// <summary>0x138A2.</summary>
    private void Falling(SonicObject o, State s)
    {
        if (s.Timer == 0)
        {
            ObjectKit.MoveAndFall(o);
            if (!o.OnScreen) { o.Clear(); return; }
            Display(o);
            return;
        }
        if (!s.Touched)
        {
            s.Timer--;
            Display(o);
            return;
        }
        ActAsPlatform(o);
        DisplayOrMarkGone(o);
        s.Timer--;
        if (s.Timer != 0) return;
        // The last moment: drop the riders.
        foreach (var player in new[] { Sonic, Tails })
        {
            if ((player.Status & 8) == 0) continue;
            player.Status &= ~0x28;   // not on an object, not pushing
            player.PrevAnim = 1;
        }
    }
}
