using System.Text.Json;

namespace SonicBros3.Logic;

/// <summary>
/// The animal freed from a destroyed enemy or the egg prison, object $28 (ROM 0x14778). The species is picked at
/// random from the zone's pair. It pops up, lands, then hops or flies away; a points popup (object $29) appears with
/// it. Animals placed in the level (subtype != 0: the ending) are not ported.
/// </summary>
public sealed class Animal : GameObject
{
    public const int Id = 0x28;
    private const int Init = 0, PopOut = 2, InPrison = 0x1C;
    private const int PointsId = 0x29;

    private readonly byte[] _zoneTypes;
    private readonly (int XVel, int YVel, uint Mappings)[] _types;

    /// <summary>The animal's own variables.</summary>
    private readonly struct State(SonicObject o)
    {
        /// <summary>$30: the species.</summary>
        public int Species { get => o.Byte(0x30); set => o.SetByte(0x30, value); }
        /// <summary>$32, $34: the species' hop or flight speeds.</summary>
        public int HopXVel { get => o.Word(0x32); set => o.SetWord(0x32, value); }
        public int HopYVel { get => o.Word(0x34); set => o.SetWord(0x34, value); }
        /// <summary>$36: frames to wait in the prison.</summary>
        public int PrisonDelay { get => o.Word(0x36); set => o.SetWord(0x36, value); }
        /// <summary>$38: freed from the egg prison.</summary>
        public bool FromPrison { get => o.Byte(0x38) != 0; set => o.SetByte(0x38, value ? 1 : 0); }
        /// <summary>$3E: the points popup's value index (x2), set by whoever destroyed the enemy.</summary>
        public int PointsIndex => o.Word(0x3E);
    }

    /// <summary>Makes a fresh slot an animal freed from the egg prison, appearing after <paramref name="delay"/> frames.</summary>
    public static void SpawnFromPrison(SonicObject a, int x, int y, int delay)
    {
        a.Id = Id;
        a.X = x;
        a.Y = y;
        var s = new State(a);
        s.FromPrison = true;
        s.PrisonDelay = delay;
    }

    public Animal(ObjectContext ctx) : base(ctx)
    {
        using var doc = JsonDocument.Parse(S2Port.Extraction.DataFile.ReadAllText(Path.Combine(ctx.Art.DataDir, "objects", "animal", "animals.json")));
        var root = doc.RootElement;
        _zoneTypes = root.GetProperty("zoneTypes").EnumerateArray().Select(e => (byte)e.GetInt32()).ToArray();
        _types = root.GetProperty("types").EnumerateArray()
            .Select(t => (t[0].GetInt32(), t[1].GetInt32(), (uint)t[2].GetInt64())).ToArray();
    }

    public override void Run(SonicObject o)
    {
        var s = new State(o);
        int routine = o.Routine;
        if (routine == Init) { Initialise(o, s); return; }
        if (routine == PopOut) { Pop(o, s); return; }
        if (routine == InPrison)
        {
            // 0x14AF6: wait, then pop out like the others.
            if (!o.OnScreen) { Delete(o); return; }
            s.PrisonDelay--;
            if (s.PrisonDelay == 0) { o.Routine = PopOut; o.Priority = 1; }
            Display(o);
            return;
        }
        if (routine > InPrison || o.Subtype != 0) throw new UnportedException(0x148C2, "placed animals (ending)");
        // After landing the routine is 4 + species * 2; the flying species use 0x14A76, the others 0x14A3A.
        if (routine is 6 or 0x0E or 0x12) Fly(o, s);
        else Hop(o, s);
    }

    /// <summary>0x14930.</summary>
    private void Initialise(SonicObject o, State s)
    {
        if (o.Subtype != 0) throw new UnportedException(0x148CA, "placed animals (ending, capsule)");
        o.Routine = PopOut;
        int pick = (int)ObjectKit.RandomNumber(Ram) & 1;
        o.ArtTile = pick == 0 ? 0x580 : 0x594;
        int zone = CurrentZone;
        if (Camera.IsEasterEgg(zone)) zone = 0x11;   // the SMB3 Easter egg borrows Green Hill's animals
        int species = _zoneTypes[zone * 2 + pick];
        s.Species = species;
        var t = _types[species];
        s.HopXVel = t.XVel;
        s.HopYVel = t.YVel;
        o.Mappings = t.Mappings;
        o.YRadius = 0x0C;
        o.RenderFlags = Render.LevelCoords | Render.XFlip;
        o.Priority = 6;
        o.WidthPixels = 8;
        o.AnimFrameDuration = 7;
        o.MappingFrame = 2;
        o.YVel = -0x400;
        if (s.FromPrison)
        {
            o.Routine = InPrison;
            o.XVel = 0;
            Display(o);
            return;
        }
        if (Spawn() is { } points)
        {
            points.Id = PointsId;
            points.X = o.X;
            points.Y = o.Y;
            points.MappingFrame = s.PointsIndex >> 1;
        }
        Display(o);
    }

    /// <summary>0x149E0: rise and fall out of the explosion until landing.</summary>
    private void Pop(SonicObject o, State s)
    {
        if (!o.OnScreen) { Delete(o); return; }
        ObjectKit.MoveAndFall(o);
        if (o.YVel >= 0)
        {
            int distance = (short)ObjectKit.CheckFloorDist(Ram, Ctx.Collision, o);
            if (distance < 0)
            {
                o.Y += distance;
                o.XVel = (short)s.HopXVel;
                o.YVel = (short)s.HopYVel;
                o.MappingFrame = 1;
                o.Routine = s.Species * 2 + 4;
                // Prison animals run off in both directions.
                if (s.FromPrison && Ram.Bit(Vars.VintRuncountLow, 4))
                {
                    o.XVel = (short)-o.XVel;
                    o.RenderFlags ^= Render.XFlip;
                }
            }
        }
        Display(o);
    }

    /// <summary>0x14A3A: hop.</summary>
    private void Hop(SonicObject o, State s)
    {
        ObjectKit.MoveAndFall(o);
        o.MappingFrame = 1;
        if (o.YVel >= 0)
        {
            o.MappingFrame = 0;
            int distance = (short)ObjectKit.CheckFloorDist(Ram, Ctx.Collision, o);
            if (distance < 0)
            {
                o.Y += distance;
                o.YVel = (short)s.HopYVel;
            }
        }
        DisplayWhileOnScreen(o);
    }

    /// <summary>0x14A76: fly: bounce off the floor, flapping every 2 frames.</summary>
    private void Fly(SonicObject o, State s)
    {
        ObjectKit.Move(o);
        o.YVel += 0x18;
        if (o.YVel >= 0)
        {
            int distance = (short)ObjectKit.CheckFloorDist(Ram, Ctx.Collision, o);
            if (distance < 0)
            {
                o.Y += distance;
                o.YVel = (short)s.HopYVel;
            }
        }
        o.AnimFrameDuration--;
        if ((sbyte)o.AnimFrameDuration < 0)
        {
            o.AnimFrameDuration = 1;
            o.MappingFrame = (o.MappingFrame + 1) & 1;
        }
        DisplayWhileOnScreen(o);
    }

    private void DisplayWhileOnScreen(SonicObject o)
    {
        if (!o.OnScreen) Delete(o);
        else Display(o);
    }
}

/// <summary>The points popup, object $29 (ROM 0x14CC8): rises and slows down; deleted once it stops rising.</summary>
public sealed class Points(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x14DD2;

    public override void Run(SonicObject o)
    {
        if (o.Routine == 0)
        {
            o.Routine = 2;
            o.Mappings = Mappings;
            o.ArtTile = 0x84AC;
            o.RenderFlags = Render.LevelCoords;
            o.Priority = 1;
            o.WidthPixels = 8;
            o.YVel = -0x300;
        }
        if (o.YVel >= 0) { Delete(o); return; }
        ObjectKit.Move(o);
        o.YVel += 0x18;
        Display(o);
    }
}
