namespace SonicBros3.Logic;

/// <summary>
/// The invisible plane (collision layer) switcher, object $03 (ROM 0x23CD0). Loops rely on it: a player crossing
/// its line, within its range, is moved between collision layer A (solidity bits $0C/$0D) and B ($0E/$0F), and gets
/// a sprite priority.
///
/// Subtype bits:
///   0-1  range each way: $20/$40/$80/$100
///   2    the line is horizontal (players cross it moving up or down)
///   3/4  layer B after crossing right (down) / left (up); else layer A
///   5/6  high sprite priority after crossing right (down) / left (up)
///   7    grounded players only
/// An x-flipped placement only changes the priority.
/// </summary>
public static class PlaneSwitcher
{
    private const int Init = 0, VerticalLine = 2, HorizontalLine = 4;
    private static readonly int[] Ranges = [0x20, 0x40, 0x80, 0x100];   // 0x23D5C

    /// <summary>$32: the range each way.</summary>
    private static int Range(SonicObject o) => o.Word(0x32);
    /// <summary>$34 + player number: 1 while that player is past the line.</summary>
    private const int PastLine = 0x34;

    public static void Step(Ram ram, SonicObject o)
    {
        switch (o.Routine)
        {
            case Init: Initialise(ram, o); break;
            case VerticalLine: Check(ram, o, horizontal: false); break;
            case HorizontalLine: Check(ram, o, horizontal: true); break;
        }
        Objects.MarkObjGone(ram, o);
    }

    private static SonicObject[] Players(Ram ram) => [ram.Sonic, ram.Tails];

    private static void Initialise(Ram ram, SonicObject o)
    {
        o.Routine = VerticalLine;
        o.Mappings = 0x23FAC;   // only drawn in debug mode
        o.ArtTile = 0x26BC;
        o.RenderFlags |= Render.LevelCoords;
        o.WidthPixels = 0x10;
        o.Priority = 5;
        int subtype = o.Subtype;
        bool horizontal = (subtype & 4) != 0;
        if (horizontal) o.Routine = HorizontalLine;
        o.MappingFrame = subtype & (horizontal ? 7 : 3);
        o.SetWord(0x32, Ranges[subtype & 3]);
        var players = Players(ram);
        for (int p = 0; p < players.Length; p++)
        {
            int mine = horizontal ? o.Y : o.X, theirs = horizontal ? players[p].Y : players[p].X;
            if (mine < theirs) o.SetByte(PastLine + p, 1);
        }
        Check(ram, o, horizontal);
    }

    /// <summary>0x23D98 (vertical line) / 0x23EA2 (horizontal line): check each player for a crossing.</summary>
    private static void Check(Ram ram, SonicObject o, bool horizontal)
    {
        if (ram.U16(Vars.DebugPlacementMode) != 0) return;   // debug mode: drawn instead (not ported)
        int line = horizontal ? o.Y : o.X;
        int centre = horizontal ? o.X : o.Y;
        int range = Range(o);
        int subtype = o.Subtype;
        var players = Players(ram);
        for (int p = 0; p < players.Length; p++)
        {
            var player = players[p];
            int along = horizontal ? player.Y : player.X;
            int across = (short)(horizontal ? player.X : player.Y);
            bool wasPast = o.Byte(PastLine + p) != 0;
            if (!wasPast)
            {
                if (line > along) continue;           // still before the line
                o.SetByte(PastLine + p, 1);
            }
            else
            {
                if (line <= along) continue;          // still past the line
                o.SetByte(PastLine + p, 0);
            }
            if (across < (short)(centre - range) || across >= (short)(centre + range)) continue;
            bool groundedOnly = (subtype & 0x80) != 0;
            if (groundedOnly && player.InAir) continue;
            bool forward = !wasPast;
            if ((o.RenderFlags & Render.XFlip) == 0)
            {
                bool layerB = (subtype & (forward ? 0x08 : 0x10)) != 0;
                player.TopSolidBit = layerB ? 0x0E : 0x0C;
                player.LrbSolidBit = layerB ? 0x0F : 0x0D;
            }
            int art = player.ArtTile & 0x7FFF;
            if ((subtype & (forward ? 0x20 : 0x40)) != 0) art |= 0x8000;   // high priority
            player.ArtTile = art;
        }
    }
}
