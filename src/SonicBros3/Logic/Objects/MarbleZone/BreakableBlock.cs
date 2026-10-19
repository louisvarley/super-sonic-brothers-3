namespace SonicBros3.Logic;

/// <summary>
/// The breakable block, object $51 (ROM 0x276F4, Sonic 2's; the hack's zones use their own art). A player who rolls
/// onto it bounces up at -$300 and it shatters into four pieces (Sonic 2's SmashObject), scoring 100/200/500/1000 by
/// the chain count (<see cref="Vars.ChainBonusCounter"/>; 10000 from 16 on), which the landing would otherwise reset.
/// </summary>
public sealed class BreakableBlock(ObjectContext ctx) : GameObject(ctx)
{
    private const uint Mappings = 0x27A68, HackMappings = 0x27AC0;
    private const int Init = 0, Intact = 2, Piece = 4;
    private const int PlayerAnimRoll = 2;
    private const int PointsId = 0x29;
    private const int SfxSmash = 0xCB;
    /// <summary>Points by chain count, in tens (AddPoints' unit).</summary>
    private static readonly int[] PointValues = [10, 20, 50, 100];
    private readonly SpriteData _hack = ctx.Art["mz-breakable"];
    private readonly (int Base, byte[] Data) _speeds = ctx.Art.DataTable("mz-breakable/speeds.json");

    /// <summary>The block's own variables.</summary>
    private readonly struct State(SonicObject o)
    {
        /// <summary>$32, $33: Sonic's and Tails' animation before the solid check (rolling?).</summary>
        public int SonicAnim { get => o.Byte(0x32); set => o.SetByte(0x32, value); }
        public int TailsAnim { get => o.Byte(0x33); set => o.SetByte(0x33, value); }
        /// <summary>$38: the chain count before the landing reset it.</summary>
        public int Chain { get => o.Word(0x38); set => o.SetWord(0x38, value); }
        /// <summary>$3C: the pieces' speed table (the original's ROM pointer).</summary>
        public int Speeds { get => (int)o.Long(0x3C); set => o.SetLong(0x3C, value); }
    }

    public override void Run(SonicObject o)
    {
        var s = new State(o);
        switch (o.Routine)
        {
            case Init: Initialise(o, s); goto case Intact;
            case Intact: WhileIntact(o, s); break;
            default: Fall(o); break;
        }
    }

    private void Initialise(SonicObject o, State s)
    {
        o.Routine = Intact;
        o.Mappings = Mappings;
        o.ArtTile = 0x43B2;
        o.WidthPixels = 0x18;
        s.Speeds = 0x27888;
        if (CurrentZone == 0x0D) throw new UnportedException(0x27730, "breakable block in zone 0D");
        if (Ram.U8(Vars.HackZoneFlag) != 0)
        {
            o.Mappings = HackMappings;
            o.ArtTile = 0x4500;
            o.WidthPixels = 0x10;
            s.Speeds = 0x278B0;
        }
        o.RenderFlags = Render.LevelCoords;
        o.Priority = 4;
    }

    private void WhileIntact(SonicObject o, State s)
    {
        s.Chain = Ram.U16(Vars.ChainBonusCounter);
        s.SonicAnim = Sonic.Anim;
        s.TailsAnim = Tails.Anim;
        Ctx.Solid.SolidObject(o, o.WidthPixels + 0x0B, 0x10, 0x11, o.X);
        int standing = o.Status & (ObjStatus.StandingP1 | ObjStatus.StandingP2);
        bool sonicRolled = s.SonicAnim == PlayerAnimRoll, tailsRolled = s.TailsAnim == PlayerAnimRoll;
        if (standing == (ObjStatus.StandingP1 | ObjStatus.StandingP2))
        {
            if (!sonicRolled && !tailsRolled) { DisplayOrMarkGone(o); return; }
            Bounce(Sonic, sonicRolled);
            Bounce(Tails, tailsRolled);
        }
        else if ((standing & ObjStatus.StandingP1) != 0)
        {
            if (!sonicRolled) { DisplayOrMarkGone(o); return; }
            Bounce(Sonic, true);
        }
        else if ((standing & ObjStatus.StandingP2) != 0)
        {
            if (!tailsRolled) { DisplayOrMarkGone(o); return; }
            Bounce(Tails, true);
        }
        else { DisplayOrMarkGone(o); return; }
        Break(o, s);
    }

    /// <summary>0x277FE / 0x27804: off the block and into the air (bouncing up if rolling).</summary>
    private static void Bounce(SonicObject player, bool rolling)
    {
        if (rolling)
        {
            player.Status |= 4;   // rolling
            player.YRadius = 0x0E;
            player.XRadius = 7;
            player.Anim = PlayerAnimRoll;
            player.YVel = -0x300;
        }
        player.Status |= 2;      // in the air
        player.Status &= ~8;     // not on an object
        player.Routine = 2;
    }

    /// <summary>0x2784E.</summary>
    private void Break(SonicObject o, State s)
    {
        Ram.W16(Vars.ChainBonusCounter, s.Chain);
        o.Status &= ~(ObjStatus.StandingP1 | ObjStatus.StandingP2);
        int speeds = s.Speeds - _speeds.Base;
        if (Ram.U8(Vars.HackZoneFlag) != 0) o.MappingFrame = 1;
        Smash(o, speeds);
        SpawnPoints(o);
        Fall(o);
    }

    /// <summary>SmashObject (0x1970A): the frame's pieces become objects with speeds from the table.</summary>
    private void Smash(SonicObject o, int speedIndex)
    {
        uint mappings = o.Mappings;
        if (mappings != HackMappings) throw new UnportedException(0x1970A, "breakable block outside the hack's zones");
        int frame = o.MappingFrame;
        uint piecePointer = mappings + (uint)_hack.FrameOffsets![frame] + 2;
        int count = _hack.Frames[frame].Length;
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
            piece.Routine = Piece;
            piece.Id = o.Id;
            piece.Mappings = piecePointer;
            piece.RenderFlags = o.RenderFlags;
            piece.X = o.X;
            piece.Y = o.Y;
            piece.ArtTile = o.ArtTile;
            piece.Priority = o.Priority;
            piece.WidthPixels = o.WidthPixels;
            piece.XVel = (short)((_speeds.Data[speedIndex] << 8) | _speeds.Data[speedIndex + 1]);
            piece.YVel = (short)((_speeds.Data[speedIndex + 2] << 8) | _speeds.Data[speedIndex + 3]);
            speedIndex += 4;
        }
        Ctx.Sound.PlaySound(SfxSmash);
    }

    /// <summary>0x278C0: points by the chain count.</summary>
    private void SpawnPoints(SonicObject o)
    {
        if (Spawn() is not { } points) return;
        points.Id = PointsId;
        points.X = o.X;
        points.Y = o.Y;
        int chain = Ram.U16(Vars.ChainBonusCounter);
        Ram.W16(Vars.ChainBonusCounter, chain + 2);
        if (chain >= 6) chain = 6;
        int value = PointValues[chain / 2];
        if (Ram.U16(Vars.ChainBonusCounter) >= 0x20) { value = 1000; chain = 0x0A; }
        Ctx.Touch.AddPoints(value);
        points.MappingFrame = chain >> 1;
    }

    /// <summary>0x27872: a falling piece.</summary>
    private void Fall(SonicObject o)
    {
        ObjectKit.Move(o);
        o.YVel += 0x18;
        if (!o.OnScreen) { o.Clear(); return; }
        Display(o);
    }
}
