using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonicBros3.Frontend;

namespace SonicBros3.Runtime;

/// <summary>
/// Deterministic, additive headless trace mode for equivalence testing. It changes no gameplay: it only steps the
/// existing <see cref="GameFlow"/> with a scripted pad and records state. See docs/INVENTORY.md and docs/PORT_LOG.md.
///
/// File layout (.sb3g), all integers little-endian:
///   "SB3GOLD1"                                    8 bytes
///   u32 headerLength, UTF-8 JSON header (GoldenHeader)
///   records until EOF:
///     u8 kind: 0 = tick, 1 = snapshot, 2 = end
///     tick:      fixed record, see WriteTick
///     snapshot:  u32 tick, u32 jsonLength, UTF-8 JSON
///     end:       u32 tickCount
/// The header carries no timestamps, so regenerating an unchanged trace produces identical bytes.
/// </summary>
public sealed class GoldenTrace : IDisposable
{
    public const string Magic = "SB3GOLD1";

    /// <summary>RAM regions whose hashes are compared first; a mismatch names the subsystem before snapshots.</summary>
    public static readonly (string Name, int Start, int Length)[] Regions =
    [
        ("objects", 0xB080, 0xD400 - 0xB080),
        ("level",   0x0000, 0xA800),
        ("camera",  0xEE00, 0x0100),
        ("rings",   0xE800, 0x0100),
        ("sound",   0xF000, 0x0600),
        ("hud",     0xFE00, 0x0060),
    ];

    /// <summary>The scalar fields in each tick record, in file order.</summary>
    public static readonly string[] Fields =
    [
        "tick", "phase", "zone", "act", "cameraX", "cameraY",
        "sonic.x", "sonic.y", "sonic.xv", "sonic.yv", "sonic.inertia", "sonic.angle", "sonic.status",
        "sonic.status2", "sonic.routine", "sonic.anim", "sonic.frame", "sonic.sub",
        "tails.x", "tails.y", "tails.xv", "tails.yv", "tails.inertia", "tails.angle", "tails.status",
        "tails.status2", "tails.routine", "tails.anim", "tails.frame", "tails.sub",
        "rings", "score", "timer", "lives", "gameMode", "wholeRam", "screenHash",
    ];

    private readonly BinaryWriter _w;
    private readonly GameFlow? _flow;
    private readonly LevelSession? _session;
    private readonly int _hashEvery;
    private readonly int _snapshotEvery;
    private readonly uint[] _rgba = new uint[Vdp.Width * Vdp.Height];
    private int _ticks;

    public GoldenTrace(string path, GameFlow flow, GoldenHeader header, int hashEvery, int snapshotEvery)
        : this(path, flow, null, header, hashEvery, snapshotEvery) { }

    /// <summary>A raw <see cref="LevelSession"/> trace, for zones the SMB3 flow does not reach (Sonic zones, campaign acts).</summary>
    public GoldenTrace(string path, LevelSession session, GoldenHeader header, int hashEvery, int snapshotEvery)
        : this(path, null, session, header, hashEvery, snapshotEvery) { }

    private GoldenTrace(string path, GameFlow? flow, LevelSession? session, GoldenHeader header,
        int hashEvery, int snapshotEvery)
    {
        _flow = flow;
        _session = session;
        _hashEvery = Math.Max(1, hashEvery);
        _snapshotEvery = Math.Max(1, snapshotEvery);
        var stream = File.Create(path);
        _w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        _w.Write(Encoding.ASCII.GetBytes(Magic));
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(header, ManifestJson);
        _w.Write((uint)json.Length);
        _w.Write(json);
    }

    public static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = false };

    /// <summary>One frame: step the flow exactly as the host does, then record.</summary>
    public void Step(int buttons)
    {
        if (_session is { } raw) raw.Step(buttons);
        else _flow!.Step(buttons);
        _ticks++;
        bool screen = _ticks % _hashEvery == 0;
        bool snapshot = _ticks % _snapshotEvery == 0;

        var session = _session ?? _flow?.Session;
        uint tick = (uint)_ticks;
        byte phase = session is null ? (byte)0xFF : (byte)session.Phase;
        byte zone = 0, act = 0;
        int camX = 0, camY = 0;
        ushort rings = 0;
        uint score = 0, timer = 0;
        byte lives = 0, gameMode = 0;
        Entity sonic = default, tails = default;
        ulong wholeRam = 0, screenHash = 0;
        Span<ulong> regions = stackalloc ulong[Regions.Length];

        if (session is { } s)
        {
            var ram = s.World.Ram;
            (zone, act) = ((byte)s.CurrentAct.Zone, (byte)s.CurrentAct.Act);
            camX = s.CameraX;
            camY = s.CameraY;
            var so = s.World.Sonic.O;
            var to = s.World.Tails.O;
            sonic = new Entity(so.X, so.Y, so.XVel, so.YVel, so.Inertia, so.Angle, so.Status,
                so.StatusSecondary, so.Routine, so.Anim, so.MappingFrame, so.Subtype);
            tails = new Entity(to.X, to.Y, to.XVel, to.YVel, to.Inertia, to.Angle, to.Status,
                to.StatusSecondary, to.Routine, to.Anim, to.MappingFrame, to.Subtype);
            rings = (ushort)ram.U16(Logic.Vars.RingCount);
            score = ram.U32(Logic.Vars.Score);
            timer = ram.U32(Logic.Vars.Timer);
            lives = (byte)ram.U8(Logic.Vars.LifeCount);
            gameMode = (byte)ram.U8(Logic.Vars.GameMode);
            wholeRam = Hash(ram.Bytes);
            for (int i = 0; i < Regions.Length; i++)
            {
                var (_, start, length) = Regions[i];
                regions[i] = Hash(ram.Bytes.AsSpan(start, length));
            }
        }

        if (screen)
        {
            if (_session is { } sessionForRender)
            {
                sessionForRender.Render();
                sessionForRender.Vdp.ToRgba(_rgba);
            }
            else
            {
                _flow!.Render();
                _flow.Vdp.ToRgba(_rgba);
            }
            screenHash = Hash(MemoryMarshal.AsBytes(_rgba.AsSpan()));
        }

        WriteTick(tick, phase, zone, act, camX, camY, sonic, tails, rings, score, timer, lives, gameMode,
            wholeRam, regions, screenHash);
        if (snapshot) WriteSnapshot(session);
    }

    private void WriteTick(uint tick, byte phase, byte zone, byte act, int camX, int camY, Entity sonic,
        Entity tails, ushort rings, uint score, uint timer, byte lives, byte gameMode, ulong wholeRam,
        Span<ulong> regions, ulong screenHash)
    {
        _w.Write((byte)0);
        _w.Write(tick);
        _w.Write(phase);
        _w.Write(zone);
        _w.Write(act);
        _w.Write(camX);
        _w.Write(camY);
        WriteEntity(sonic);
        WriteEntity(tails);
        _w.Write(rings);
        _w.Write(score);
        _w.Write(timer);
        _w.Write(lives);
        _w.Write(gameMode);
        _w.Write(wholeRam);
        foreach (ulong h in regions) _w.Write(h);
        _w.Write(screenHash);
    }

    private void WriteEntity(Entity e)
    {
        _w.Write(e.X);
        _w.Write(e.Y);
        _w.Write(e.XVel);
        _w.Write(e.YVel);
        _w.Write(e.Inertia);
        _w.Write(e.Angle);
        _w.Write(e.Status);
        _w.Write(e.StatusSecondary);
        _w.Write(e.Routine);
        _w.Write(e.Anim);
        _w.Write(e.Frame);
        _w.Write(e.Subtype);
    }

    private void WriteSnapshot(LevelSession? session)
    {
        object body;
        if (session is { } s)
        {
            var ram = s.World.Ram;
            var objects = new List<object>();
            for (int a = Logic.Vars.MainCharacter; a < Logic.Vars.ObjectRamEnd; a += Logic.Objects.SlotSize)
            {
                if (ram.U8(a) == 0) continue;
                var o = new Logic.SonicObject(ram, a);
                objects.Add(new
                {
                    slot = Logic.Objects.SlotIndex(a), id = o.Id, x = o.X, y = o.Y, xsub = o.XSub, ysub = o.YSub,
                    xv = o.XVel, yv = o.YVel, inertia = o.Inertia, sub = o.Subtype, routine = o.Routine,
                    routine2 = o.RoutineSecondary, status = o.Status, status2 = o.StatusSecondary, anim = o.Anim,
                    frame = o.MappingFrame, art = o.ArtTile,
                });
            }
            body = new
            {
                kind = "level", tick = _ticks, phase = s.Phase.ToString(), zone = s.CurrentAct.Zone,
                act = s.CurrentAct.Act, cameraX = s.CameraX, cameraY = s.CameraY,
                ram = Convert.ToHexString(SHA256.HashData(ram.Bytes)), objects,
            };
        }
        else
        {
            var p = _flow!.Progress;
            body = new
            {
                kind = "screen", tick = _ticks, describe = _flow.Describe(),
                lives = p.Lives, score = p.Score, rings = p.Rings, world = p.World, mapScreen = p.MapScreen,
                position = p.Position, cards = p.Cards.Select(c => c.ToString()).ToArray(),
                cleared = p.Cleared.Order().ToArray(), powerUps = p.PowerUps.ToArray(),
            };
        }
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(body, ManifestJson);
        _w.Write((byte)1);
        _w.Write((uint)_ticks);
        _w.Write((uint)json.Length);
        _w.Write(json);
    }

    /// <summary>Finish the file with an end record.</summary>
    public void Finish()
    {
        _w.Write((byte)2);
        _w.Write((uint)_ticks);
        _w.Flush();
        _w.Dispose();
    }

    public void Dispose() => _w.Dispose();

    public static ulong Hash(ReadOnlySpan<byte> span) =>
        BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(span));

    private struct Entity
    {
        public int X, Y;
        public short XVel, YVel, Inertia;
        public byte Angle, Status, StatusSecondary, Routine, Anim, Frame, Subtype;
        public Entity(int x, int y, short xv, short yv, short inertia, int angle, int status, int status2,
            int routine, int anim, int frame, int subtype)
        {
            X = x; Y = y; XVel = xv; YVel = yv; Inertia = inertia;
            Angle = (byte)angle; Status = (byte)status; StatusSecondary = (byte)status2; Routine = (byte)routine;
            Anim = (byte)anim; Frame = (byte)frame; Subtype = (byte)subtype;
        }
    }
}

/// <summary>Everything needed to interpret a trace without the legacy binary or the ROM archive.</summary>
public sealed record GoldenHeader(
    string Format, string Engine, string Scenario, string Archive, string ArchiveSha256,
    string Start, int Zone, int Act, int World, int MapScreen, string Level, string DebugStart, int Frames,
    int HashEvery, int SnapshotEvery, string[] Regions, string[] Fields);
