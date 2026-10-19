using SonicBros3.Logic;

namespace SonicBros3.Frontend;

/// <summary>
/// Sprites for the screens that are not levels: mapping frames (with their DPLC art copied to VRAM, as the objects'
/// DPLC routines do) written straight into the sprite table at $F800, linked in drawing order (earlier = in front).
/// Coordinates are screen pixels.
/// </summary>
public sealed class SpriteKit(Ram ram, byte[] vram)
{
    private int _count;

    public void Begin() => _count = 0;

    /// <summary>Ends the table: the last entry's link is 0 (an empty table gets one off-screen sprite).</summary>
    public void End()
    {
        if (_count == 0) Piece(-64, -64, 0, 0);
        ram.W8(Vars.SpriteTable + (_count - 1) * 8 + 3, 0);
    }

    public void Piece(int x, int y, int size, int pattern)
    {
        if (_count >= 80) return;
        int e = Vars.SpriteTable + _count * 8;
        ram.W16(e, (y + 128) & 0x3FF);
        ram.W8(e + 2, size);
        ram.W8(e + 3, _count + 1);
        ram.W16(e + 4, pattern);
        int sx = (x + 128) & 0x1FF;
        ram.W16(e + 6, sx == 0 ? 1 : sx);   // x = 0 would mask the line's later sprites
        _count++;
    }

    /// <summary>A mapping frame at (x, y); <paramref name="artTile"/> is added to each piece's pattern, as the VDP's
    /// art_tile is in BuildSprites.</summary>
    public void Frame(SpriteData data, int frame, int x, int y, int artTile, bool flipX = false, bool flipY = false)
    {
        foreach (var p in data.Frames[frame])
        {
            int w = ((p.Size >> 2) & 3) + 1, h = (p.Size & 3) + 1;
            int px = flipX ? -p.X - w * 8 : p.X, py = flipY ? -p.Y - h * 8 : p.Y;
            int pattern = ((p.Pattern + artTile) ^ (flipX ? 0x800 : 0) ^ (flipY ? 0x1000 : 0)) & 0xFFFF;
            Piece(x + px, y + py, p.Size, pattern);
        }
    }

    /// <summary>Copies a frame's DPLC tiles to VRAM from <paramref name="vramTile"/> on.</summary>
    public void LoadDplc(SpriteData data, int frame, int vramTile)
    {
        int t = vramTile;
        foreach (var (first, count) in data.Dplc![frame])
        {
            data.Art.AsSpan(first * 32, count * 32).CopyTo(vram.AsSpan(t * 32));
            t += count;
        }
    }
}
