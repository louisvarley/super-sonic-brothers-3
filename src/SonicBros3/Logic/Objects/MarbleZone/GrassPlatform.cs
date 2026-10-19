namespace SonicBros3.Logic;

/// <summary>
/// The large grassy platform, object $2F (ROM 0x1531D6, Sonic 1's Marble Zone). Solid, with a sloped top from one of
/// three height profiles (subtype bits 4-6 pick the profile, frame and width).
///
/// Subtype bits 0-2: 0 still; 1-4 bob with oscillators 0-3 (bit 3 inverts); 5 sinks while stood on (along a sine, up
/// to 16 px), and halfway down starts a fire spreading along it (<see cref="GrassFire"/>). It keeps the fires' slots
/// in a list and moves them with it. Off screen the fires go out; out of range (from its start x) it is cleared.
/// </summary>
public sealed class GrassPlatform(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x153500;
    private const int TypeTable = 0x1531E8;
    private const int Still = 0, Sinks = 5;
    private const int FireId = 0x19;

    /// <summary>Per type (subtype bits 4-6): the profile's offset from the table, the frame, the half width (0x1531E8).</summary>
    private static readonly (int Offset, int Frame, int Width)[] Types = [(0x254, 0, 0x40), (0x2CC, 1, 0x40), (0x2A0, 2, 0x20)];
    private readonly (int Base, byte[] Data) _heights = ctx.Art.DataTable("mz-grass-platform/heights.json");
    private readonly Dictionary<int, sbyte[]> _profiles = [];

    /// <summary>The platform's own variables.</summary>
    public readonly struct State(SonicObject o)
    {
        /// <summary>$2A, $2C: where it was placed.</summary>
        public int BaseX { get => o.Word(0x2A); set => o.SetWord(0x2A, value); }
        public int BaseY { get => o.Word(0x2C); set => o.SetWord(0x2C, value); }
        /// <summary>$30: its height profile (the original's ROM pointer).</summary>
        public int Profile { get => (int)o.Long(0x30); set => o.SetLong(0x30, value); }
        /// <summary>$34: how far it has sunk (an angle, 0 to $40).</summary>
        public int Sink { get => o.Byte(0x34); set => o.SetByte(0x34, value); }
        /// <summary>$35: the fire has started.</summary>
        public bool FireStarted { get => o.Byte(0x35) != 0; set => o.SetByte(0x35, value ? 1 : 0); }
        /// <summary>$36: the number of fire pieces; $37 onward: their slot numbers.</summary>
        public int FireCount { get => o.Byte(0x36); set => o.SetByte(0x36, value); }
        public int FireSlot(int i) => o.Byte(0x37 + i);
        public void SetFireSlot(int i, int slot) => o.SetByte(0x37 + i, slot);
    }

    public override void Run(SonicObject o)
    {
        var s = new State(o);
        if (o.Routine == 0) Initialise(o, s);
        int oldX = o.X;
        Move(o, s);
        int halfWidth = o.WidthPixels + 0x0B;
        int halfHeight = o.MappingFrame == 2 ? 0x30 : 0x20;
        Ctx.Solid.SolidObject(o, halfWidth, halfHeight, halfHeight + 1, oldX);
        Ctx.Solid.SlopedPlatform(o, halfWidth, o.X, ProfileOf(s));
        bool stoodOn = (o.Status & (ObjStatus.StandingP1 | ObjStatus.StandingP2)) != 0;
        o.RoutineSecondary = stoodOn ? 2 : 0;
        Display(o);
        DeleteCheck(o, s);
    }

    private void Initialise(SonicObject o, State s)
    {
        o.Routine = 2;
        o.Mappings = Mappings;
        o.ArtTile = 0xC000;
        o.RenderFlags = Render.LevelCoords;
        o.Priority = 5;
        s.BaseY = o.Y;
        s.BaseX = o.X;
        int t = (o.Subtype >> 4) & 7;
        var type = t < Types.Length ? Types[t] : Types[0];
        s.Profile = TypeTable + type.Offset;
        o.MappingFrame = type.Frame;
        o.WidthPixels = type.Width;
        o.Subtype &= 0x0F;
        o.YRadius = 0x40;
        o.RenderFlags |= Render.UseYRadius;
    }

    /// <summary>The height profile, as signed bytes from its start.</summary>
    private sbyte[] ProfileOf(State s)
    {
        int start = s.Profile - _heights.Base;
        if (_profiles.TryGetValue(start, out var cached)) return cached;
        var profile = new sbyte[0x80];
        for (int i = 0; i < profile.Length; i++)
            if (start >= 0 && start + i < _heights.Data.Length) profile[i] = (sbyte)_heights.Data[start + i];
        return _profiles[start] = profile;
    }

    /// <summary>One height from a profile (the fire follows the platform's top).</summary>
    public static int Height(ObjectArt art, int profile, int index)
    {
        var (b, data) = art.DataTable("mz-grass-platform/heights.json");
        int i = profile - b + index;
        return i >= 0 && i < data.Length ? data[i] : 0;
    }

    /// <summary>0x1532BA.</summary>
    private void Move(SonicObject o, State s)
    {
        int type = o.Subtype & 7;
        if (type == Still) return;
        if (type == Sinks) { Sinking(o, s); return; }
        if (type > Sinks) return;
        int swing = Ram.U8(Oscillation.Value(type - 1));
        int range = type switch { 1 => 0x20, 2 => 0x30, 3 => 0x40, _ => 0x60 };
        if ((o.Subtype & 8) != 0) swing = -swing + range;
        o.Y = s.BaseY - swing;
    }

    /// <summary>0x15331A: sink while stood on, start the fire halfway, carry the fires down with it.</summary>
    private void Sinking(SonicObject o, State s)
    {
        int sink = s.Sink;
        if (o.RoutineSecondary == 0) sink = Math.Max(sink - 2, 0);
        else sink = Math.Min(sink + 4, 0x40);
        s.Sink = sink;
        int depth = (ushort)MdMath.CalcSine(sink).Sin >> 4;
        o.Y = s.BaseY + depth;
        if (s.Sink == 0x20 && !s.FireStarted)
        {
            s.FireStarted = true;
            int slot = ObjectKit.FindFreeSlotAfter(Ram, o);
            if (slot >= 0)
            {
                var fire = new SonicObject(Ram, slot);
                var f = new GrassFire.State(fire);
                fire.Id = FireId;
                fire.X = o.X - 0x40;
                f.BaseY = s.BaseY + 8 - 3;
                f.Profile = s.Profile;
                f.Platform = o.Address;
                AddFire(Ram, o.Address, slot);
            }
        }
        for (int i = 0; i < s.FireCount; i++) new GrassFire.State(Objects.Slot(Ram, s.FireSlot(i))).Sink = depth;
    }

    /// <summary>0x1533B8: add a fire to the platform's list.</summary>
    public static void AddFire(Ram ram, int platform, int fireSlot)
    {
        var s = new State(new SonicObject(ram, platform));
        int n = s.FireCount;
        s.FireCount = n + 1;
        s.SetFireSlot(n, Objects.SlotIndex(fireSlot));
    }

    /// <summary>0x1533D6: off screen the fires go out (and the platform rises again); out of range it goes.</summary>
    private void DeleteCheck(SonicObject o, State s)
    {
        if (s.FireStarted && !o.OnScreen)
        {
            int count = s.FireCount;
            s.FireCount = 0;
            for (int i = 0; i < count; i++)
            {
                int slot = s.FireSlot(i);
                s.SetFireSlot(i, 0);
                Objects.Slot(Ram, slot).Clear();
            }
            s.FireStarted = false;
            s.Sink = 0;
            return;
        }
        if (OutOfRangeS1(s.BaseX)) o.Clear();
    }
}
