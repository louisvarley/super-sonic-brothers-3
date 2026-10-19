using System.Text.Json;

namespace SonicBros3.Logic;

/// <summary>The purple rock, object $3B in the hack's zones (ROM 0x19574): a solid block.</summary>
public sealed class Rock(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x19604;

    public override void Run(SonicObject o)
    {
        if (o.Routine == 0)
        {
            o.Routine = 2;
            o.ArtTile = 0x63AA;
            o.Mappings = Mappings;
            o.RenderFlags = Render.LevelCoords;
            o.WidthPixels = 0x13;
            o.Priority = 4;
        }
        Ctx.Solid.SolidObject(o, 0x1B, 0x10, 0x10, o.X);
        if (OutOfRange(o.X)) Delete(o);
        else Display(o);
    }
}

/// <summary>
/// The breakable wall, object $3C in the hack's zones (ROM 0x19618; Sonic 1's). Solid; a rolling player pushing
/// into it at $480 or more smashes it into one object per mapping piece, flung with the speeds from the extracted
/// tables. Every routine ends with MarkObjGone's display variant, so falling fragments are queued for display twice
/// per frame, as in the original.
/// </summary>
public sealed class Wall : GameObject
{
    public const uint Mappings = 0x19886;
    private const int Init = 0, Intact = 2, Fragment = 4;
    private const int SmashSpeed = 0x480, Gravity = 0x70;
    private const int PlayerAnimRoll = 2;
    private const int SfxSmash = 0xCB;

    /// <summary>Fragment speeds (x, y pairs per piece) when hit from the left and from the right.</summary>
    private readonly int[] _fromLeft, _fromRight;
    private readonly SpriteData _sprites;

    /// <summary>$30, $32: Sonic's and Tails' x speed before the solid check stopped them.</summary>
    private const int SonicSpeed = 0x30, TailsSpeed = 0x32;

    public Wall(ObjectContext ctx) : base(ctx)
    {
        using var doc = JsonDocument.Parse(S2Port.Extraction.DataFile.ReadAllText(Path.Combine(ctx.Art.DataDir, "objects", "wall", "wall.json")));
        _fromLeft = doc.RootElement.GetProperty("fromLeft").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        _fromRight = doc.RootElement.GetProperty("fromRight").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        _sprites = ctx.Art["wall"];
    }

    public override void Run(SonicObject o)
    {
        switch (o.Routine)
        {
            case Init: Initialise(o); WhileIntact(o); break;
            case Intact: WhileIntact(o); break;
            case Fragment: Fall(o); break;
        }
        DisplayOrMarkGone(o);
    }

    private void Initialise(SonicObject o)
    {
        o.Routine = Intact;
        o.Mappings = Mappings;
        o.ArtTile = CurrentZone >= 0x19 ? 0x453C : 0x4340;
        o.RenderFlags = Render.LevelCoords;
        o.WidthPixels = 0x10;
        o.Priority = 4;
        o.MappingFrame = o.Subtype;
    }

    /// <summary>0x1966C.</summary>
    private void WhileIntact(SonicObject o)
    {
        o.SetWord(SonicSpeed, Sonic.XVel);
        o.SetWord(TailsSpeed, Tails.XVel);
        Ctx.Solid.SolidObject(o, 0x1B, 0x20, 0x20, o.X);
        // Both players are checked even after a smash (the smash returns into the loop, 0x19698).
        TrySmash(o, Sonic, 5, SonicSpeed);
        TrySmash(o, Tails, 6, TailsSpeed);
    }

    /// <param name="pushingBit">The wall's status bit for "this player pushes it" (5 Sonic, 6 Tails).</param>
    private void TrySmash(SonicObject o, SonicObject player, int pushingBit, int savedSpeed)
    {
        if (!Ram.Bit(o.StatusAddr, pushingBit) || player.Anim != PlayerAnimRoll) return;
        int speed = (short)o.Word(savedSpeed);
        if ((Math.Abs(speed) & 0xFFFF) < SmashSpeed) return;
        // Keep going through it.
        player.XVel = (short)speed;
        player.X += 4;
        var speeds = _fromLeft;
        if (o.X >= player.X)
        {
            player.X -= 8;
            speeds = _fromRight;
        }
        player.Inertia = player.XVel;
        Ram.Clear(o.StatusAddr, pushingBit);
        player.Pushing = false;   // not pushing
        Smash(o, speeds);
        Fall(o);
    }

    /// <summary>0x1970A: one fragment per mapping piece (the wall itself becomes the first).</summary>
    private void Smash(SonicObject o, int[] speeds)
    {
        int frame = o.MappingFrame;
        uint piecePointer = Mappings + (uint)_sprites.FrameOffsets![frame] + 2;
        int count = _sprites.Frames[frame].Length;
        o.RenderFlags |= Render.StaticMappings;
        var piece = o;
        for (int k = 0; k < count; k++)
        {
            if (k > 0)
            {
                int slot = ObjectKit.FindFreeSlotAfter(Ram, o);
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
            piece.XVel = (short)speeds[k * 2];
            piece.YVel = (short)speeds[k * 2 + 1];
        }
        Ctx.Sound.PlaySound(SfxSmash);
    }

    /// <summary>0x196F4.</summary>
    private void Fall(SonicObject o)
    {
        ObjectKit.Move(o);
        o.YVel += Gravity;
        if (!o.OnScreen) Delete(o);
        else Display(o);
    }
}
