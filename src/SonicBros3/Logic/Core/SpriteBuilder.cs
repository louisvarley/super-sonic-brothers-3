namespace SonicBros3.Logic;

/// <summary>
/// DisplaySprite (ROM 0x1A23A) and BuildSprites (0x1A354, 1-player path) with the piece writer BuildSpr_Normal
/// (0x1A584). Objects queue themselves in 8 priority lists at $AC00; BuildSprites turns their mapping frames into
/// hardware sprite entries in the sprite table at $F800 {y, size, link, pattern, x} (all +128).
/// Mappings are looked up by the ROM pointer the object stores at $04.
/// </summary>
public sealed class SpriteBuilder(Ram ram, ObjectArt art, SpriteData? rings = null)
{
    public Hud? Hud { get; set; }

    /// <summary>DisplaySprite3 (0x1A27A): queue in the list at byte offset d0 (a multiple of $80).</summary>
    public static void DisplaySpriteAt(Ram ram, SonicObject o, int listOffset)
    {
        int list = Vars.ObjectDisplayLists + listOffset;
        if (ram.U16(list) >= 0x7E) return;
        ram.W16(list, ram.U16(list) + 2);
        ram.W16(list + ram.U16(list), o.Address);
    }

    public static void DisplaySprite(Ram ram, SonicObject o)
    {
        if (o.Id == 0) return;
        int list = Vars.ObjectDisplayLists + ((o.Priority & 7) << 7);   // a $80-byte list per priority
        if (ram.U16(list) >= 0x7E) return;
        ram.W16(list, ram.U16(list) + 2);
        ram.W16(list + ram.U16(list), o.Address);
    }

    public void BuildSprites()
    {
        if (ram.U16(Vars.TwoPlayerMode) != 0) throw new UnportedException(0x1A6C6, "BuildSprites 2P");
        int a2 = Vars.SpriteTable;
        int d5 = 0;
        int firstList = 0;
        if (ram.U8(Vars.LevelStartedFlag) != 0)
        {
            Hud?.Build(this, ref a2, ref d5);
            if (ram.U8(Vars.SpriteCount + 0x1) != 0)
            {
                DrawList(Vars.ObjectDisplayLists, ref a2, ref d5);   // priority 0 in front of rings
                firstList = 1;
            }
            BuildRings(ref a2, ref d5);
        }
        for (int n = firstList; n < 8; n++) DrawList(Vars.ObjectDisplayLists + n * 0x80, ref a2, ref d5);
        ram.W8(Vars.SpriteCount, d5);
        if (d5 == 0x50) ram.W8(a2 - 5, 0);    // last sprite's link
        else ram.W32(a2, 0);
    }

    private void DrawList(int list, ref int a2, ref int d5)
    {
        int d6 = 2;
        while (ram.U16(list) != 0)
        {
            var o = new SonicObject(ram, ram.U16(list + d6));
            if (o.Id != 0) DrawObject(o, ref a2, ref d5);
            d6 += 2;
            ram.W16(list, ram.U16(list) - 2);
        }
    }

    /// <summary>BuildRings (0x1AEF0): one sprite per visible ring in the window; no 80-sprite check.</summary>
    private void BuildRings(ref int a2, ref int d5)
    {
        if (rings is null) return;
        int a0 = ram.U16(Vars.RingStartAddr), end = ram.U16(Vars.RingEndAddr);
        int camX = ram.U16(Vars.CameraXPos), camY = ram.U16(Vars.CameraYPos);
        for (; a0 != end; a0 = (a0 + 6) & 0xFFFF)
        {
            if ((short)ram.U16(a0) < 0) continue;
            int d3 = (ram.U16(a0 + 2) - camX + 0x80) & 0xFFFF;
            int d2 = (((ram.U16(a0 + 4) - camY) & 0x7FF) + 8);
            if (d2 >= 0xF0) continue;
            d2 += 0x78;
            int frame = ram.U8(a0 + 1);
            if (frame == 0) frame = ram.U8(Vars.RingsAnimFrame);
            var p = rings.Frames[frame][0];
            ram.W16(a2, p.Y + d2);
            ram.W8(a2 + 2, p.Size);
            d5++;
            ram.W8(a2 + 3, d5);
            ram.W16(a2 + 4, p.Pattern + 0x26BC);
            ram.W16(a2 + 6, p.X + d3);
            a2 += 8;
        }
    }

    /// <summary>
    /// The unflipped static piece writer (0x1A59C) used by BuildHUD: x is wrapped to 9 bits, and an x of 0 becomes 1
    /// (x=0 would mask the rest of the line's sprites on hardware). Stops at 80 sprites.
    /// </summary>
    public void WriteStatic(MappingPiece[] pieces, int d2, int d3, int artTile, ref int a2, ref int d5)
    {
        foreach (var p in pieces)
        {
            ram.W16(a2, p.Y + d2);
            ram.W8(a2 + 2, p.Size);
            d5++;
            ram.W8(a2 + 3, d5);
            ram.W16(a2 + 4, p.Pattern + artTile);
            int x = (p.X + d3) & 0x1FF;
            ram.W16(a2 + 6, x == 0 ? 1 : x);
            a2 += 8;
            if (d5 >= 0x50) return;
        }
    }

    private void DrawObject(SonicObject o, ref int a2, ref int d5)
    {
        o.RenderFlags &= 0x7F;
        int d4 = o.RenderFlags;
        if ((d4 & Render.MultiSprite) != 0) { DrawMulti(o, d4, ref a2, ref d5); return; }
        int d2, d3;
        if ((d4 & 0x0C) == 0)
        {
            // Screen-fixed object: position is already in sprite coordinates.
            d2 = o.XSub;   // screen objects keep their y here
            d3 = o.X;
        }
        else
        {
            int camX = ram.U16(Vars.CameraXPosCopy), camY = ram.U16(Vars.CameraYPosCopy);
            int width = o.WidthPixels;
            d3 = (short)(o.X - camX);
            if ((short)(d3 + width) < 0) return;
            if ((short)(d3 - width) >= 0x140) return;
            d3 += 0x80;
            if ((d4 & 0x10) != 0)
            {
                int h = (byte)o.YRadius;
                d2 = (short)(o.Y - camY);
                if ((short)(d2 + h) < 0) return;
                if ((short)(d2 - h) >= 0xE0) return;
                d2 += 0x80;
            }
            else
            {
                d2 = (o.Y - camY + 0x80) & 0x7FF;
                if (d2 < 0x60 || d2 >= 0x180) return;
            }
        }

        uint pointer = o.Mappings;
        MappingPiece[] pieces;
        if ((d4 & 0x20) != 0)
        {
            // Static mappings (0x1A45A): the pointer is one piece, drawn alone.
            if (!art.TryStatic(pointer, out var piece)) throw new UnportedException((int)pointer, "static sprite piece for this object");
            pieces = [piece];
        }
        else
        {
            if (!art.TryTable(pointer, out var data)) throw new UnportedException((int)pointer, "sprite mappings for this object");
            pieces = data.Frames[o.MappingFrame];
        }
        if (pieces.Length > 0) WritePieces(o, pieces, d2, d3, d4, ref a2, ref d5);
        o.RenderFlags |= 0x80;   // on screen
    }

    /// <summary>
    /// Multi-sprite objects (0x1A48E): a main frame ($0B, skipped when 0) at the object's position, then $0F
    /// sub-sprites from $10 as {x, y, unused byte, frame byte}, drawn without per-sprite range checks.
    /// The on-screen test uses $0E as the half width (and $14 as the half height with render flag bit 4).
    /// </summary>
    private void DrawMulti(SonicObject o, int d4, ref int a2, ref int d5)
    {
        uint pointer = o.Mappings;
        if (!art.TryTable(pointer, out var data)) throw new UnportedException((int)pointer, "sprite mappings for this object");
        int camX = ram.U16(Vars.CameraXPos), camY = ram.U16(Vars.CameraYPos);
        var multi = new MultiSprite(o);
        int width = multi.Width;
        int d3 = (short)(o.X - camX);
        if ((short)(d3 + width) < 0 || (short)(d3 - width) >= 0x140) return;
        d3 += 0x80;
        int d2;
        if ((d4 & 0x10) != 0)
        {
            int h = multi.Height;
            d2 = (short)(o.Y - camY);
            if ((short)(d2 + h) < 0 || (short)(d2 - h) >= 0xE0) return;
            d2 += 0x80;
        }
        else
        {
            d2 = (o.Y - camY + 0x80) & 0x7FF;
            if (d2 < 0x60 || d2 >= 0x180) return;
        }
        int main = multi.MainFrame;
        if (main != 0 && d5 < 0x50) WritePieces(o, data.Frames[main], d2, d3, d4, ref a2, ref d5);
        o.RenderFlags |= 0x80;
        int count = multi.ChildCount;
        for (int i = 0; i < count; i++)
        {
            int x = (multi.ChildX(i) - camX + 0x80) & 0xFFFF;
            int y = (multi.ChildY(i) - camY + 0x80) & 0x7FF;
            int frame = multi.ChildFrame(i);
            if (d5 < 0x50) WritePieces(o, data.Frames[frame], y, x, d4, ref a2, ref d5);
        }
    }

    /// <summary>BuildSpr_Normal (0x1A584) and its flipped variants.</summary>
    private void WritePieces(SonicObject o, MappingPiece[] pieces, int d2, int d3, int d4, ref int a2, ref int d5)
    {
        int artTile = o.ArtTile;
        bool fx = (d4 & 1) != 0, fy = (d4 & 2) != 0;
        if (d5 >= 0x50) return;
        foreach (var p in pieces)
        {
            int width = (((p.Size >> 2) & 3) + 1) * 8, height = ((p.Size & 3) + 1) * 8;
            int y = fy ? -p.Y - height : p.Y;
            int x = fx ? -p.X - width : p.X;
            int pattern = (p.Pattern + artTile) ^ (fx ? 0x800 : 0) ^ (fy ? 0x1000 : 0);
            ram.W16(a2, y + d2);
            ram.W8(a2 + 2, p.Size);
            d5++;
            ram.W8(a2 + 3, d5);
            ram.W16(a2 + 4, pattern);
            int sx = (x + d3) & 0x1FF;
            ram.W16(a2 + 6, sx == 0 ? 1 : sx);
            a2 += 8;
            // Only the unflipped path checks the 80-sprite limit inside the loop.
            if (!fx && !fy && d5 >= 0x50) return;
        }
    }
}
