using S2Port.Extraction.Levels;

namespace SonicBros3.Logic;

/// <summary>
/// Terrain collision (ROM 0x22416-0x22F98): Find_Tile, FindFloor/FindWall and the check routines built on them.
/// Reads level data from <see cref="Ram"/> exactly where the original keeps it (layout $8000, chunks $0000,
/// collision indexes $D600/$D900 via Collision_addr), and the angle/height/width tables from the extracted
/// collision sets. Probes report the surface angle by writing a byte to $F768 or $F76A (the original's a4).
/// </summary>
public sealed class Collision(Ram ram, CollisionSet normal, CollisionSet alternate)
{
    private CollisionSet Set => ram.U8(Vars.HackZoneFlag) != 0 ? alternate : normal;

    /// <summary>Find_Tile (0x22416): RAM address of the block word under (x, y), FG plane, wrapping at 128x16 chunks.</summary>
    /// <summary>The block word address the last Find_Tile returned (the original's a1 after a probe).</summary>
    public int LastTile { get; private set; }

    public int FindTile(int y, int x)
    {
        int d0 = (y * 2) & 0xF00;
        int d1 = (x & 0xFFFF) >> 3;
        int d4 = d1;
        d1 = (d1 >> 4) & 0x7F;
        d0 += d1;
        int chunk = ram.U8(Vars.LevelLayout + d0);
        int addr = chunk * 0x80;               // chunk table at 0x22450: entry n = n * $80
        addr = (addr + (y & 0x70)) & 0xFFFF;
        addr = (addr + (d4 & 0xE)) & 0xFFFF;
        LastTile = addr;
        return addr;                           // $FFFF0000 + addr
    }

    /// <summary>
    /// FindFloor (0x22650). y = d2, x = d3, step = a3 (+/-16), solidBit = d5, flip = d6, angleAddr = a4.
    /// Returns d1: distance to the surface (negative = inside).
    /// </summary>
    public int FindFloor(int y, int x, int step, int solidBit, int flip, int angleAddr)
    {
        var r = Probe(y, x, solidBit, angleAddr, vertical: true);
        if (r is null) return FindFloor2(y + step, x, solidBit, flip, angleAddr) + 0x10;
        int d0 = r.Value.height;
        if (((r.Value.word ^ flip) & 0x800) != 0) d0 = -d0;
        if (d0 == 0) return FindFloor2(y + step, x, solidBit, flip, angleAddr) + 0x10;
        if (d0 > 0)
        {
            if ((d0 & 0xFF) == 0x10) return FindFloor2(y - step, x, solidBit, flip, angleAddr) - 0x10;
            return 0xF - (d0 + (y & 0xF));
        }
        d0 += y & 0xF;
        if (d0 >= 0) return FindFloor2(y + step, x, solidBit, flip, angleAddr) + 0x10;
        return FindFloor2(y - step, x, solidBit, flip, angleAddr) - 0x10;
    }

    /// <summary>FindFloor2 (0x22710): the second-block probe; never recurses.</summary>
    public int FindFloor2(int y, int x, int solidBit, int flip, int angleAddr)
    {
        y = (short)y;
        var r = Probe(y, x, solidBit, angleAddr, vertical: true);
        if (r is null) return 0xF - (y & 0xF);
        int d0 = r.Value.height;
        if (((r.Value.word ^ flip) & 0x800) != 0) d0 = -d0;
        if (d0 == 0) return 0xF - (y & 0xF);
        if (d0 > 0) return 0xF - (d0 + (y & 0xF));
        int d1 = y & 0xF;
        if (d0 + d1 >= 0) return 0xF - (y & 0xF);
        return ~d1;
    }

    /// <summary>
    /// The single-block floor probe used by RingCheckFloorDist (0x227C0): like FindFloor, but an empty block or zero
    /// height gives $10 instead of probing the next block down; a full block probes one block up.
    /// </summary>
    public int RingFindFloor(int y, int x, int step, int solidBit, int flip, int angleAddr)
    {
        var r = Probe(y, x, solidBit, angleAddr, vertical: true);
        if (r is null) return 0x10;
        int d0 = r.Value.height;
        if (((r.Value.word ^ flip) & 0x800) != 0) d0 = -d0;
        if (d0 == 0) return 0x10;
        if (d0 > 0)
        {
            if ((d0 & 0xFF) == 0x10) return FindFloor2(y - step, x, solidBit, flip, angleAddr) - 0x10;
            return 0xF - (d0 + (y & 0xF));
        }
        if (d0 + (y & 0xF) >= 0) return 0x10;
        return FindFloor2(y - step, x, solidBit, flip, angleAddr) - 0x10;
    }

    /// <summary>FindWall (0x22878): as FindFloor with axes swapped; step moves x; uses the width array.</summary>
    public int FindWall(int y, int x, int step, int solidBit, int flip, int angleAddr)
    {
        var r = Probe(y, x, solidBit, angleAddr, vertical: false);
        if (r is null) return FindWall2(y, x + step, solidBit, flip, angleAddr) + 0x10;
        int d0 = r.Value.height;
        if (((r.Value.word ^ flip) & 0x400) != 0) d0 = -d0;
        if (d0 == 0) return FindWall2(y, x + step, solidBit, flip, angleAddr) + 0x10;
        if (d0 > 0)
        {
            if ((d0 & 0xFF) == 0x10) return FindWall2(y, x - step, solidBit, flip, angleAddr) - 0x10;
            return 0xF - (d0 + (x & 0xF));
        }
        d0 += x & 0xF;
        if (d0 >= 0) return FindWall2(y, x + step, solidBit, flip, angleAddr) + 0x10;
        return FindWall2(y, x - step, solidBit, flip, angleAddr) - 0x10;
    }

    /// <summary>FindWall2 (0x22938).</summary>
    public int FindWall2(int y, int x, int solidBit, int flip, int angleAddr)
    {
        x = (short)x;
        var r = Probe(y, x, solidBit, angleAddr, vertical: false);
        if (r is null) return 0xF - (x & 0xF);
        int d0 = r.Value.height;
        if (((r.Value.word ^ flip) & 0x400) != 0) d0 = -d0;
        if (d0 == 0) return 0xF - (x & 0xF);
        if (d0 > 0) return 0xF - (d0 + (x & 0xF));
        int d1 = x & 0xF;
        if (d0 + d1 >= 0) return 0xF - (x & 0xF);
        return ~d1;
    }

    /// <summary>
    /// Shared body of the four finders: looks up the block word, checks its solidity bit, maps it to a collision
    /// ID, writes the (flip-adjusted) angle to angleAddr and returns the signed height/width for the column/row.
    /// Null means "no solid tile here".
    /// </summary>
    private (int word, int height)? Probe(int y, int x, int solidBit, int angleAddr, bool vertical)
    {
        int word = ram.U16(FindTile(y, x));
        int block = word & 0x3FF;
        if (block == 0 || (word & (1 << (solidBit & 31))) == 0) return null;
        int colAddr = (int)(ram.U32(Vars.CollisionAddr) & 0xFFFF);
        int id = ram.U8(colAddr + block);
        if (id == 0) return null;
        var set = Set;
        int angle = set.Angles[id];
        int index;
        if (vertical)
        {
            int d1 = x;
            if ((word & 0x400) != 0) { d1 = ~d1; angle = -angle; }
            if ((word & 0x800) != 0) angle = -(angle + 0x40) - 0x40;
            index = id * 16 + (d1 & 0xF);
            ram.W8(angleAddr, angle);
            return (word, set.Heights[index]);
        }
        else
        {
            int d1 = y;
            if ((word & 0x800) != 0) { d1 = ~d1; angle = -(angle + 0x40) - 0x40; }
            if ((word & 0x400) != 0) angle = -angle;
            index = id * 16 + (d1 & 0xF);
            ram.W8(angleAddr, angle);
            return (word, set.Widths[index]);
        }
    }

    // ------------------------------------------------------------------ Collision_addr / layer selection

    /// <summary>Sets Collision_addr from the object's layer byte at $3E ($0C = primary/layer A).</summary>
    public void SelectLayer(SonicObject o) =>
        ram.W32(Vars.CollisionAddr, o.TopSolidBit == 0x0C ? 0xFFFF0000 | Vars.PrimaryCollision : 0xFFFF0000 | Vars.SecondaryCollision);

    // ------------------------------------------------------------------ paired probes (results like 0x22BBE)

    public readonly record struct Pair(int Distance, int Other, byte Angle);

    /// <summary>0x22BBE: pick the nearer of two probes and its angle; odd angle = use the cardinal default.</summary>
    private Pair Nearer(int first, int second, byte cardinal)
    {
        int d0 = first, d1 = second;
        byte d3 = ram.U8(Vars.SecondaryAngle);
        if (d1 > d0) { d3 = ram.U8(Vars.PrimaryAngle); (d0, d1) = (d1, d0); }
        if ((d3 & 1) != 0) d3 = cardinal;
        return new Pair(d1, d0, d3);
    }

    private byte Single(byte cardinal)
    {
        byte d3 = ram.U8(Vars.PrimaryAngle);
        return (d3 & 1) != 0 ? cardinal : d3;
    }

    /// <summary>Sonic_CheckFloor (0x22B46): two floor probes below the feet; sets Collision_addr and uses layer A.</summary>
    public Pair CheckFloor(SonicObject o)
    {
        SelectLayer(o);
        int solid = o.TopSolidBit;
        int d1a = FindFloor(o.Y + o.YRadius, o.X + o.XRadius, 0x10, solid, 0, Vars.PrimaryAngle);
        int d1b = FindFloor(o.Y + o.YRadius, o.X - o.XRadius, 0x10, solid, 0, Vars.SecondaryAngle);
        return Nearer(d1a, d1b, 0x00);
    }

    /// <summary>0x22E26: two ceiling probes (uses the current Collision_addr and solidity bit).</summary>
    public Pair CheckCeiling(SonicObject o, int solid)
    {
        int y = ((o.Y - o.YRadius) ^ 0xF);
        int d1a = FindFloor(y, o.X + o.XRadius, -0x10, solid, 0x800, Vars.PrimaryAngle);
        int d1b = FindFloor(y, o.X - o.XRadius, -0x10, solid, 0x800, Vars.SecondaryAngle);
        return Nearer(d1a, d1b, 0x80);
    }

    /// <summary>0x22D74: two right-wall probes (CalcRoomOverHead, angle $C0).</summary>
    public Pair CheckRightWallPair(SonicObject o, int solid)
    {
        int x = o.X + o.YRadius;
        int d1a = FindWall(o.Y - o.XRadius, x, 0x10, solid, 0, Vars.PrimaryAngle);
        int d1b = FindWall(o.Y + o.XRadius, x, 0x10, solid, 0, Vars.SecondaryAngle);
        return Nearer(d1a, d1b, 0xC0);
    }

    /// <summary>0x22EEE: two left-wall probes (CalcRoomOverHead, angle $40).</summary>
    public Pair CheckLeftWallPair(SonicObject o, int solid)
    {
        int x = (o.X - o.YRadius) ^ 0xF;
        int d1a = FindWall(o.Y - o.XRadius, x, -0x10, solid, 0x400, Vars.PrimaryAngle);
        int d1b = FindWall(o.Y + o.XRadius, x, -0x10, solid, 0x400, Vars.SecondaryAngle);
        return Nearer(d1a, d1b, 0x40);
    }

    /// <summary>0x22BDE entry: one floor probe 10 px below (y, x).</summary>
    public (int Distance, byte Angle) FloorDist(int y, int x, int solid)
    {
        int d1 = FindFloor(y + 0xA, x, 0x10, solid, 0, Vars.PrimaryAngle);
        return (d1, Single(0x00));
    }

    /// <summary>0x22E96 entry: one ceiling probe 10 px above.</summary>
    public (int Distance, byte Angle) CeilingDist(int y, int x, int solid)
    {
        int d1 = FindFloor((y - 0xA) ^ 0xF, x, -0x10, solid, 0x800, Vars.PrimaryAngle);
        return (d1, Single(0x80));
    }

    /// <summary>CheckRightWallDist (0x22DD4 / entry 0x22DDC): one wall probe 10 px right.</summary>
    public (int Distance, byte Angle) RightWallDist(int y, int x, int solid)
    {
        int d1 = FindWall(y, x + 0xA, 0x10, solid, 0, Vars.PrimaryAngle);
        return (d1, Single(0xC0));
    }

    /// <summary>CheckLeftWallDist (0x22F56 / entry 0x22F5E): one wall probe 10 px left.</summary>
    public (int Distance, byte Angle) LeftWallDist(int y, int x, int solid)
    {
        int d1 = FindWall(y, (x - 0xA) ^ 0xF, -0x10, solid, 0x400, Vars.PrimaryAngle);
        return (d1, Single(0x40));
    }

    /// <summary>ChkFloorEdge (0x22C4E, with x given at 0x22C52): one floor probe under the feet at x.</summary>
    public (int Distance, byte Angle) FloorEdge(SonicObject o, int x)
    {
        SelectLayer(o);
        ram.W8(Vars.PrimaryAngle, 0);
        int d1 = FindFloor(o.Y + o.YRadius, x, 0x10, o.TopSolidBit, 0, Vars.PrimaryAngle);
        byte d3 = ram.U8(Vars.PrimaryAngle);
        return (d1, (d3 & 1) != 0 ? (byte)0 : d3);
    }

    /// <summary>
    /// CalcRoomInFront (0x22A7C): probe in the direction of travel from the position after this frame's move.
    /// d0 is the probe direction angle.
    /// </summary>
    public (int Distance, byte Angle) CalcRoomInFront(SonicObject o, int d0)
    {
        SelectLayer(o);
        int solid = o.LrbSolidBit;
        int x = (int)((o.XLong + ((long)o.XVel << 8)) >> 16);
        int y = (int)((o.YLong + ((long)o.YVel << 8)) >> 16);
        ram.W8(Vars.PrimaryAngle, d0);
        ram.W8(Vars.SecondaryAngle, d0);
        int quadrant = RoundedQuadrant(d0);
        if (quadrant == 0x00) return FloorDist((short)y, (short)x, solid);
        if (quadrant == 0x80) return CeilingDist((short)y, (short)x, solid);
        if ((d0 & 0x38) == 0) y += 8;
        return quadrant == 0x40 ? LeftWallDist((short)y, (short)x, solid) : RightWallDist((short)y, (short)x, solid);
    }

    /// <summary>
    /// CalcRoomOverHead (0x22B02): room in direction d0 from the current position. The floor case falls through
    /// into Sonic_CheckFloor (which reselects the layer).
    /// </summary>
    public int CalcRoomOverHead(SonicObject o, int d0)
    {
        SelectLayer(o);
        int solid = o.LrbSolidBit;
        ram.W8(Vars.PrimaryAngle, d0);
        ram.W8(Vars.SecondaryAngle, d0);
        int q = (d0 + 0x20) & 0xC0;
        return q switch
        {
            0x40 => CheckLeftWallPair(o, solid).Distance,
            0x80 => CheckCeiling(o, solid).Distance,
            0xC0 => CheckRightWallPair(o, solid).Distance,
            _ => CheckFloor(o).Distance,
        };
    }

    /// <summary>Quadrant rounding used by AnglePos / CalcRoomInFront (Rev01): ties go toward the floor/ceiling.</summary>
    public static int RoundedQuadrant(int angle)
    {
        sbyte a = (sbyte)angle;
        int d0;
        if ((sbyte)(angle + 0x20) >= 0)
            d0 = (a < 0 ? angle + 1 : angle) + 0x1F;
        else
            d0 = (a < 0 ? angle - 1 : angle) + 0x20;
        return d0 & 0xC0;
    }
}
