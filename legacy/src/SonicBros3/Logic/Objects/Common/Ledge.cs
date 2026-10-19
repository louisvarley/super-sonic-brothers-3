using System.Text.Json;

namespace SonicBros3.Logic;

/// <summary>
/// The collapsing ledge, object $1A (ROM 0x13586). A sloped top-solid platform. Once stood on, it waits 7 frames,
/// then breaks into one object per mapping piece (each with static mappings). Each fragment falls after its own
/// delay (the table at 0x13984), still carrying riders until it goes.
/// </summary>
public sealed class Ledge : GameObject
{
    public const uint Mappings = 0x139FE;
    private const int Init = 0, Intact = 2, Fragment = 4;
    private const int BreakDelay = 7;
    private const int SfxCollapse = 0xB9;

    private readonly sbyte[] _slope;
    private readonly byte[] _delays;
    private readonly int[] _frameOffsets;
    private readonly SpriteData _sprites;

    /// <summary>The ledge's own variables.</summary>
    private readonly struct State(SonicObject o)
    {
        /// <summary>$38: frames until it breaks (intact) or falls (fragment).</summary>
        public int Timer { get => o.Byte(0x38); set => o.SetByte(0x38, value); }
        /// <summary>$3A: set once a player has stood on it.</summary>
        public bool Touched { get => o.Byte(0x3A) != 0; set => o.SetByte(0x3A, value ? 1 : 0); }
    }

    public Ledge(ObjectContext ctx) : base(ctx)
    {
        using var doc = JsonDocument.Parse(S2Port.Extraction.DataFile.ReadAllText(Path.Combine(ctx.Art.DataDir, "objects", "ledge", "ledge.json")));
        _slope = doc.RootElement.GetProperty("slope").EnumerateArray().Select(e => (sbyte)e.GetInt32()).ToArray();
        _delays = doc.RootElement.GetProperty("delays").EnumerateArray().Select(e => (byte)e.GetInt32()).ToArray();
        _sprites = ctx.Art["ledge"];
        _frameOffsets = _sprites.FrameOffsets!;
    }

    public override void Run(SonicObject o)
    {
        var s = new State(o);
        switch (o.Routine)
        {
            case Init: Initialise(o, s); WhileIntact(o, s); break;
            case Intact: WhileIntact(o, s); break;
            case Fragment: Falling(o, s); break;
        }
    }

    private void Initialise(SonicObject o, State s)
    {
        o.Routine = Intact;
        o.Mappings = Mappings;
        o.ArtTile = 0x4000;
        o.RenderFlags |= Render.LevelCoords;
        o.Priority = 4;
        s.Timer = BreakDelay;
        o.MappingFrame = o.Subtype;
        o.SetLong(0x34, 0x13984);   // the delay table
        int zone = CurrentZone;
        if (zone is 8 or 0x0A) throw new UnportedException(0x135D0, $"collapsing platform for zone {zone:X2}");
        o.SetLong(0x3C, 0x139CE);   // the slope table
        o.WidthPixels = Ram.U8(Vars.HackZoneFlag) != 0 ? 0x30 : 0x34;
        o.YRadius = 0x38;
        o.RenderFlags |= Render.UseYRadius;
    }

    /// <summary>0x13652.</summary>
    private void WhileIntact(SonicObject o, State s)
    {
        if (s.Touched)
        {
            if (s.Timer == 0) { Break(o); return; }
            s.Timer--;
        }
        ActAsPlatform(o, s);
    }

    /// <summary>0x13664: mark touched while stood on, then act as a sloped platform.</summary>
    private void ActAsPlatform(SonicObject o, State s)
    {
        if ((o.Status & (ObjStatus.StandingP1 | ObjStatus.StandingP2)) != 0) s.Touched = true;
        Ctx.Solid.SlopedPlatform(o, o.WidthPixels, o.X, _slope);
        DisplayOrMarkGone(o);
    }

    /// <summary>0x138FC: switch to the broken frame and split it into one object per mapping piece.</summary>
    private void Break(SonicObject o)
    {
        o.MappingFrame += 2;
        int frame = o.MappingFrame;
        // Each fragment's mappings point straight at its piece (static mappings, 8 bytes per piece).
        uint piecePointer = Mappings + (uint)_frameOffsets[frame] + 2;
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
            piece.Routine = Fragment;
            piece.Id = o.Id;
            piece.Mappings = piecePointer;
            piece.RenderFlags = o.RenderFlags;
            piece.X = o.X;
            piece.Y = o.Y;
            piece.ArtTile = o.ArtTile;
            piece.Priority = o.Priority;
            piece.WidthPixels = o.WidthPixels;
            piece.YRadius = o.YRadius;
            new State(piece).Timer = _delays[k];
            if (piece.Address < o.Address) Display(piece);   // 0x1A25C: its slot already ran this frame
        }
        Display(o);
        Ctx.Sound.PlaySound(SfxCollapse);
    }

    /// <summary>0x1368A.</summary>
    private void Falling(SonicObject o, State s)
    {
        if (s.Timer == 0)
        {
            ObjectKit.MoveAndFall(o);
            if (!o.OnScreen) Delete(o); else Display(o);
            return;
        }
        if (!s.Touched)
        {
            s.Timer--;
            Display(o);
            return;
        }
        ActAsPlatform(o, s);
        s.Timer--;
        if (s.Timer != 0) return;
        // Falling away: drop the riders.
        foreach (var player in new[] { Sonic, Tails })
        {
            if (!player.OnObject) continue;
            player.OnObject = false;   // on object
            player.Pushing = false;   // pushing
            player.PrevAnim = 1;
        }
    }
}
