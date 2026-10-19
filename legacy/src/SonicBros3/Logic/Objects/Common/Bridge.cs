using System.Text.Json;

namespace SonicBros3.Logic;

/// <summary>
/// The log bridge, object $11 (ROM 0x120E0). The placed object holds the logic and spawns one or two multi-sprite
/// children (up to 8 logs each) that only draw the logs. Players stand on it like a top-solid platform; the log
/// under each player sets where the bridge sags, and the sag eases in and out (0 to $40).
/// The children (render flag <see cref="Render.MultiSprite"/>) only display, in priority list 3.
/// </summary>
public sealed class Bridge : GameObject
{
    public const uint Mappings = 0x12710, MappingsS2 = 0x126F8;
    private const int Init = 0, Main = 2;
    private const int LogsPerChild = 8, LogWidth = 0x10, MaxSag = 0x40, SagStep = 4;

    /// <summary>How deep each log sags, per bridge length and standing log.</summary>
    private readonly byte[] _depression;
    /// <summary>The bend profile: how much of the sag each log gets.</summary>
    private readonly byte[] _bend;
    private readonly int _depressionFirst;

    /// <summary>The bridge's own variables.</summary>
    private readonly struct State(SonicObject o)
    {
        /// <summary>$30, $34: the two log children's slots (longs, $FFFFxxxx).</summary>
        public int FirstLogs { get => (int)(o.Long(0x30) & 0xFFFF); set => o.SetLong(0x30, 0xFFFF0000u | (uint)value); }
        public int SecondLogs { get => (int)(o.Long(0x34) & 0xFFFF); set => o.SetLong(0x34, 0xFFFF0000u | (uint)value); }
        /// <summary>$3B (Tails), $3F (Sonic): the log each player stands on; $3F is also the one the sag centres on.</summary>
        public const int TailsLog = 0x3B, SonicLog = 0x3F;
        public int SagLog { get => o.Byte(SonicLog); set => o.SetByte(SonicLog, value); }
        /// <summary>$3C: the bridge's resting y.</summary>
        public int BaseY { get => o.Word(0x3C); set => o.SetWord(0x3C, value); }
        /// <summary>$3E: how far the sag has eased in (0 to $40).</summary>
        public int Sag { get => o.Byte(0x3E); set => o.SetByte(0x3E, value); }
    }

    public Bridge(ObjectContext ctx) : base(ctx)
    {
        using var doc = JsonDocument.Parse(S2Port.Extraction.DataFile.ReadAllText(Path.Combine(ctx.Art.DataDir, "objects", "bridge", "bridge.json")));
        var root = doc.RootElement;
        _depression = root.GetProperty("depression").EnumerateArray().Select(e => (byte)e.GetInt32()).ToArray();
        _bend = root.GetProperty("bend").EnumerateArray().Select(e => (byte)e.GetInt32()).ToArray();
        _depressionFirst = root.GetProperty("depressionFirstSubtype").GetInt32();
    }

    public override void Run(SonicObject o)
    {
        if ((o.RenderFlags & Render.MultiSprite) != 0) { SpriteBuilder.DisplaySpriteAt(Ram, o, 0x180); return; }   // logs
        var s = new State(o);
        switch (o.Routine)
        {
            case Init: Initialise(o, s); Update(o, s); break;
            case Main: Update(o, s); break;
            default: throw new UnportedException(0x12294, "bridge routines for zone 8");
        }
    }

    private void Initialise(SonicObject o, State s)
    {
        o.Routine = Main;
        if (CurrentZone == 8) throw new UnportedException(0x1212A, "zone 8 bridge");
        // 0x1210E: Sonic 2's bridge (Emerald Hill's logs), or the hack's zones' own.
        bool hack = Ram.U8(Vars.HackZoneFlag) != 0;
        o.Mappings = hack ? Mappings : MappingsS2;
        o.ArtTile = hack ? 0x438E : 0x43B6;
        o.Priority = 3;
        o.RenderFlags = Render.LevelCoords;
        o.WidthPixels = 0x80;
        int y = o.Y;
        s.BaseY = y;
        int logs = o.Subtype;
        int x = (o.X - ((logs >> 1) << 4)) & 0xFFFF;   // the leftmost log
        var first = SpawnLogs(o, LogsPerChild, ref x, y);
        if (first is null) return;
        first.X = new MultiSprite(first).ChildX(4) - 8;   // the children's own x: the middle of their logs
        s.FirstLogs = first.Address;
        int rest = logs - LogsPerChild;
        if (rest <= 0) return;
        var second = SpawnLogs(o, rest, ref x, y);
        if (second is null) return;
        s.SecondLogs = second.Address;
        second.X = (second.Word(0x10 + rest * 3) - 8) & 0xFFFF;
    }

    /// <summary>0x121B4: a child holding <paramref name="count"/> logs, 16 px apart.</summary>
    private SonicObject? SpawnLogs(SonicObject o, int count, ref int x, int y)
    {
        int slot = ObjectKit.FindFreeSlotAfter(Ram, o);
        if (slot < 0) return null;
        var child = new SonicObject(Ram, slot);
        child.Id = o.Id;
        child.X = o.X;
        child.Y = o.Y;
        child.Mappings = o.Mappings;
        child.ArtTile = o.ArtTile;
        child.RenderFlags = o.RenderFlags | Render.MultiSprite;
        var multi = new MultiSprite(child);
        multi.Width = 0x40;
        multi.ChildCount = count;
        for (int i = 0; i < count; i++)
        {
            multi.SetChild(i, x, y, 0);
            x = (x + LogWidth) & 0xFFFF;
        }
        return child;
    }

    /// <summary>0x12202.</summary>
    private void Update(SonicObject o, State s)
    {
        int standing = o.Status & (ObjStatus.StandingP1 | ObjStatus.StandingP2);
        bool bend = true;
        if (standing == 0)
        {
            if (s.Sag == 0) bend = false;
            else s.Sag -= SagStep;
        }
        else
        {
            if ((standing & ObjStatus.StandingP2) != 0)
            {
                // Tails on it: the sag drifts toward his log.
                int sonicLog = o.Byte(State.SonicLog), tailsLog = o.Byte(State.TailsLog);
                if (sonicLog != tailsLog) s.SagLog = sonicLog >= tailsLog ? sonicLog - 1 : sonicLog + 1;
            }
            if (s.Sag != MaxSag) s.Sag += SagStep;
        }
        if (bend) Bend(o, s);
        int logs = o.Subtype;
        int halfWidth = logs * 8 + 8, width = logs * 16;
        StandOn(o, s, Tails, 4, State.TailsLog, halfWidth, width);
        StandOn(o, s, Sonic, 3, State.SonicLog, halfWidth, width);
        if (TwoPlayerMode || !OutOfRange(o.X)) return;
        Delete(new SonicObject(Ram, s.FirstLogs));
        if (logs > LogsPerChild) Delete(new SonicObject(Ram, s.SecondLogs));
        Delete(o);
    }

    /// <summary>0x12314: one player on the bridge, standing on the log under them.</summary>
    /// <param name="standingBit">The bridge's status bit for "this player stands on it" (3 Sonic, 4 Tails).</param>
    private void StandOn(SonicObject o, State s, SonicObject player, int standingBit, int logVar, int halfWidth, int width)
    {
        if (!Ram.Bit(o.StatusAddr, standingBit))
        {
            // PlatformObject11_cont (0x1DB3A), then record the log if the player landed.
            if (player.YVel >= 0)
            {
                int along = (player.X - o.X + halfWidth) & 0xFFFF;
                if ((short)along >= 0 && along < width) Ctx.Solid.ChkYRange(o, player, (o.Y - 8) & 0xFFFF, standingBit);
            }
            if (Ram.Bit(o.StatusAddr, standingBit)) o.SetByte(logVar, ((player.X - o.X + halfWidth) & 0xFFFF) >> 4);
            return;
        }
        int x = (player.X - o.X + halfWidth) & 0xFFFF;
        if (player.InAir || (short)x < 0 || x >= width)
        {
            // Walked or jumped off.
            player.OnObject = false;
            Ram.Clear(o.StatusAddr, standingBit);
            return;
        }
        int log = x >> 4;
        o.SetByte(logVar, log);
        var logs = new MultiSprite(new SonicObject(Ram, log < LogsPerChild ? s.FirstLogs : s.SecondLogs));
        int logY = logs.ChildY(log % LogsPerChild);
        player.Y = logY - 8 - (byte)player.YRadius;
    }

    /// <summary>0x12470: set each log's y from the sag amount, the standing log and the bend profile.</summary>
    private void Bend(SonicObject o, State s)
    {
        int sag = MdMath.CalcSine(s.Sag).Sin & 0xFFFF;
        int logs = o.Subtype;
        int standing = s.SagLog;
        int key = standing + (logs << 4);
        int depth = _depression[key - (_depressionFirst << 4)];
        int profileRow = (key & 0x0F) << 4;
        int baseY = s.BaseY;
        // Logs are written in order through the first child's 8 entries, then the second child's.
        int entry = s.FirstLogs + 0x12;
        int end = s.FirstLogs + 0x42;
        // Up to and including the standing log, the profile from the standing log's row ...
        int profile = profileRow;
        for (int n = standing; n >= 0; n--) Write(_bend[profile++]);
        // ... then the logs after it, from the mirrored row, backwards.
        int after = (sbyte)(byte)-((standing + 1 - logs) & 0xFF);
        if (after < 0) return;
        profile = (after << 4) + after;
        for (int n = after - 1; n >= 0; n--) Write(_bend[--profile]);

        void Write(int bendAmount)
        {
            uint y = (uint)((bendAmount + 1) & 0xFFFF) * (uint)(depth & 0xFFFF);
            y = (y & 0xFFFF) * (uint)sag;
            Ram.W16(entry, (int)((y >> 16) + baseY) & 0xFFFF);
            entry += 6;
            if (entry == end)
            {
                entry = s.SecondLogs + 0x12;
                end = -1;
            }
        }
    }
}
