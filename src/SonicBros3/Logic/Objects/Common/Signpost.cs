namespace SonicBros3.Logic;

/// <summary>
/// The end-of-act signpost, object $0D (ROM 0x1CF34), one-player. Active only in acts that end with one
/// (<see cref="Vars.LevelHasSignpost"/>, set at level start by 0x5F1E).
///
/// When Sonic passes it: the signpost jingle plays, the timer stops, the camera's left edge locks, and it spins (with
/// ring sparkles) for 3 x 60 frames. Once Sonic is past the right edge of the screen (controls locked to "hold
/// right"), the results start (<see cref="StartResults"/>).
/// </summary>
public sealed class Signpost(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x1D354;
    private readonly byte[] _anims = ctx.Art["signpost"].Animations!;

    private const int Init = 0, Main = 2;
    private const int Idle = 0, Spinning = 2, Finishing = 4;
    private const int SpinFrames = 0x3C, SparkleFrames = 0x0B;
    private const int AnimSonicFace = 3, AnimTailsFace = 4;   // ... then show a face
    private const int MusicSignpost = 0xCF, MusicActClear = 0x9A;
    private const int ResultsId = 0x3A;
    private const int RightButton = 0x0800;

    /// <summary>Time bonus by (seconds / 15) (0x1D268).</summary>
    private static readonly int[] TimeBonus =
        [5000, 5000, 1000, 500, 400, 400, 300, 300, 200, 200, 200, 200, 100, 100, 100, 100, 50, 50, 50, 50, 0];
    /// <summary>Where the ring sparkles appear, in turn (0x1D18A).</summary>
    private static readonly (sbyte X, sbyte Y)[] Sparkles =
        [(-0x18, -0x10), (8, 8), (-0x10, 0), (0x18, -8), (0, -8), (0x10, 0), (-0x18, 8), (0x18, 0x10)];

    /// <summary>The signpost's own variables.</summary>
    private readonly struct State(SonicObject o)
    {
        /// <summary>$30: frames until the next spin step.</summary>
        public int SpinTimer { get => o.Word(0x30); set => o.SetWord(0x30, value); }
        /// <summary>$32: frames until the next sparkle.</summary>
        public int SparkleTimer { get => o.Word(0x32); set => o.SetWord(0x32, value); }
        /// <summary>$34: the next sparkle position (x2).</summary>
        public int SparkleIndex { get => o.Byte(0x34); set => o.SetByte(0x34, value); }
        /// <summary>$36: the face it stops on (Sonic's or Tails').</summary>
        public int FinalAnim { get => o.Byte(0x36); set => o.SetByte(0x36, value); }
    }

    public override void Run(SonicObject o)
    {
        if (o.Routine == Init) Initialise(o);
        else Update(o, new State(o));
        ObjectKit.Animate(Ram, o, _anims);
        DisplayOrMarkGone(o);
    }

    private void Initialise(SonicObject o)
    {
        if (TwoPlayerMode) throw new UnportedException(0x1CF5E, "2-player signpost");
        if (Ram.U16(Vars.LevelHasSignpost) == 0) { o.X = 0; return; }   // not an act that ends with a signpost
        o.Mappings = Mappings;
        o.ArtTile = 0x434;
        o.Routine = Main;
        o.RenderFlags = Render.LevelCoords;
        o.WidthPixels = 0x18;
        o.Priority = 4;
    }

    /// <summary>0x1CFC0.</summary>
    private void Update(SonicObject o, State s)
    {
        Ram.W16(Vars.LoserTimeLeft, 0x3C3C);
        bool timerRunning = Ram.U8(Vars.UpdateHudTimer) != 0;
        if (timerRunning)
        {
            bool skip = Ram.U16(Vars.CurrentZoneAndAct) == 0x1501 && !o.OnScreen;
            int past = (Sonic.X - o.X) & 0xFFFF;
            if (!skip && Sonic.X >= o.X && past < 0x20)
            {
                // Sonic passed it.
                Ctx.Sound.PlayMusic(MusicSignpost);
                Ram.W8(Vars.UpdateHudTimer, 0);
                o.Anim = 0;        // spin through animations 0-2 ...
                o.PrevAnim = 1;    // (restarted)
                s.SpinTimer = 0;
                Ram.W16(Vars.CameraMinXPos, Ram.U16(Vars.CameraMaxXPos));
                o.RoutineSecondary = Spinning;
                if (Ram.U8(Vars.LoserTimeLeft) <= 0x0C) Ctx.Sound.PlayMusic(Ram.U16(Vars.LevelMusic));
                if (s.FinalAnim == 0) s.FinalAnim = Ram.U16(Vars.PlayerMode) == 2 ? AnimTailsFace : AnimSonicFace;
            }
        }
        switch (o.RoutineSecondary)
        {
            case Spinning: Spin(o, s); break;
            case Finishing: Finish(o); break;
        }
    }

    /// <summary>0x1D0EC: spin through 3 steps of 60 frames, scattering sparkles every 12 frames.</summary>
    private void Spin(SonicObject o, State s)
    {
        s.SpinTimer--;
        if ((short)s.SpinTimer < 0)
        {
            s.SpinTimer = SpinFrames;
            o.Anim++;
            if (o.Anim == 3)
            {
                o.RoutineSecondary = Finishing;
                o.Anim = s.FinalAnim;
            }
        }
        s.SparkleTimer--;
        if ((short)s.SparkleTimer >= 0) return;
        s.SparkleTimer = SparkleFrames;
        var (dx, dy) = Sparkles[s.SparkleIndex >> 1];
        s.SparkleIndex = (s.SparkleIndex + 2) & 0x0E;
        if (Spawn() is not { } sparkle) return;
        sparkle.Id = 0x25;
        sparkle.Routine = Ring.Sparkle;
        sparkle.X = o.X + dx;
        sparkle.Y = o.Y + dy;
        sparkle.Mappings = LostRings.Mappings;
        sparkle.ArtTile = 0x26BC;
        sparkle.RenderFlags = Render.LevelCoords;
        sparkle.Priority = 2;
        sparkle.WidthPixels = 8;
    }

    /// <summary>0x1D19A: walk Sonic off the screen, then start the results.</summary>
    private void Finish(SonicObject o)
    {
        if (Ram.U16(Vars.DebugPlacementMode) != 0) return;
        if (!Sonic.InAir)
        {
            Ram.W8(Vars.ControlLocked, 1);
            Ram.W16(Vars.Ctrl1HeldLogical, RightButton);
        }
        bool sonicExists = Sonic.Id != 0;
        if (sonicExists && Sonic.X < ((Ram.U16(Vars.CameraMaxXPos) + 0x128) & 0xFFFF)) return;
        o.RoutineSecondary = Idle;
        StartResults(Ctx);
    }

    /// <summary>
    /// 0x1D1D4 (also reached from the egg prison): clear Sonic's power-ups, stop the timer, spawn the results, load
    /// their art, compute the time, ring and perfect bonuses and play the act-clear music.
    /// </summary>
    public static void StartResults(ObjectContext ctx)
    {
        var ram = ctx.Ram;
        ram.Sonic.StatusSecondary = 0;
        ram.W8(Vars.UpdateHudTimer, 0);
        if (ram.U8(Vars.TimedMode) != 0) throw new UnportedException(0x1D1E6, "end of act in the hack's timed mode");
        int slot = Objects.FindFreeSlot(ram);
        if (slot >= 0) ram.W8(slot, ResultsId);
        ctx.LoadPlc?.Invoke(ram.U16(Vars.PlayerMode) == 2 ? 0x42 : 0x26);
        ram.W8(Vars.UpdateBonusScore, 1);
        int seconds = ram.U8(Vars.TimerMinute) * 60 + ram.U8(Vars.TimerSecond);
        ram.W16(Vars.BonusCountdown1, TimeBonus[Math.Min(seconds / 15, 0x14)]);
        ram.W16(Vars.BonusCountdown2, ram.U16(Vars.RingCount) * 10);
        ram.W16(Vars.TotalBonusCountdown, 0);
        ram.W16(Vars.BonusCountdown3, 0);
        if (ram.U16(Vars.PerfectRingsLeft) == 0) ram.W16(Vars.BonusCountdown3, 5000);
        ctx.Sound.PlayMusic(MusicActClear);
    }
}

/// <summary>
/// The end-of-act results, object $3A (ROM 0x17384). Eight screen-space pieces ("SONIC GOT THROUGH ACT n", the
/// score, the time, ring and perfect bonuses) slide in. Then the bonuses are counted into the score (10 per frame
/// each, a tally sound every 4 frames, a cha-ching when done). After a pause the next act is set from the level
/// order table (0x176B4) and a level restart is requested.
/// </summary>
public sealed class Results(ObjectContext ctx) : GameObject(ctx)
{
    public const uint Mappings = 0x1825E;
    private const int Id = 0x3A;

    // Routines (each piece has its own).
    private const int Init = 0, Name = 2, Piece = 4, ActWord = 6, ActNumber = 8, WaitBeforeTally = 0x0A, Tallying = 0x0C,
        WaitAfterTally = 0x0E, NextAct = 0x10, ContinueWait = 0x12, ContinueBlink = 0x14, PerfectLine = 0x16;
    private const int SlideSpeed = 0x10;
    private const int SfxTally = 0xCD, SfxChaChing = 0xC5, SfxContinue = 0xBF;

    /// <summary>The pieces: start x, final x, screen y, routine, frame (0x1779C).</summary>
    private static readonly (int X, int TargetX, int Y, int Routine, int Frame)[] Pieces =
    [
        (0x20, 0x120, 0xB8, Name, 0), (0x200, 0x100, 0xCA, Piece, 3), (0x240, 0x140, 0xCA, ActWord, 4),
        (0x278, 0x178, 0xBE, ActNumber, 6), (0x350, 0x120, 0x120, Piece, 9), (0x320, 0x120, 0xF0, Piece, 10),
        (0x330, 0x120, 0x100, Piece, 11), (0x340, 0x120, 0x110, PerfectLine, 14),
    ];

    /// <summary>$30: where the piece slides to.</summary>
    private static int TargetX(SonicObject o) => o.Word(0x30);
    /// <summary>$1E: a frame timer.</summary>
    private static int Timer(SonicObject o) => o.Word(0x1E);
    private static void SetTimer(SonicObject o, int frames) => o.SetWord(0x1E, frames);

    /// <summary>
    /// The level order (0x176B4, extracted): the next zone/act word for each zone ($00-$1C) and act; negative = back
    /// to the title. Null for a zone outside the table.
    /// </summary>
    public static int? NextLevel(ObjectArt art, int zoneAct)
    {
        var (_, table) = art.DataTable("signpost/level-order.json");
        int i = ((zoneAct >> 8) * 2 + (zoneAct & 1)) * 2;
        return i + 1 < table.Length ? (table[i] << 8) | table[i + 1] : null;
    }

    public override void Run(SonicObject o)
    {
        switch (o.Routine)
        {
            case Init:
                if (!CreatePieces(o)) break;
                goto case Name;   // falls into the first piece's routine (0x17402)
            case Name:
                bool tailsAlone = Ram.U16(Vars.PlayerMode) == 2;
                bool overseas = (sbyte)Ram.U8(Vars.GraphicsFlags) < 0;
                o.MappingFrame = tailsAlone ? (overseas ? 2 : 1) : 0;   // "SONIC" / "MILES" / "TAILS"
                Slide(o);
                if (o.X == TargetX(o)) { o.Routine = WaitBeforeTally; SetTimer(o, 0xB4); }
                break;
            case Piece: Slide(o); break;
            case ActWord:
                if (CurrentZone is 0x10 or 0x06 or 0x0E) o.MappingFrame = 5;   // single-act zones
                Slide(o);
                break;
            case ActNumber:
            {
                int zone = CurrentZone;
                if (zone is 0x10 or 0x06 or 0x0E) { Delete(o); break; }
                bool act2Only = zone is 0x05 or 0x12 or 0x14 or 0x16 or 0x18 or 0x1A or 0x1C;
                o.MappingFrame = act2Only ? 8 : Ram.U8(Vars.CurrentAct) + 6;
                Slide(o);
                break;
            }
            case WaitBeforeTally: case WaitAfterTally:
                SetTimer(o, Timer(o) - 1);
                if (Timer(o) == 0) o.Routine += 2;
                Display(o);
                break;
            case Tallying: Tally(o); break;
            case NextAct: GoToNextAct(); break;
            case PerfectLine:
                if (Ram.U16(Vars.PerfectRingsLeft) != 0) { Delete(o); break; }
                Slide(o);
                break;
            case ContinueWait: case ContinueBlink: ContinueIcon(o); break;
            default: throw new UnportedException(0x17384, $"results routine {o.Routine:X2}");
        }
    }

    /// <summary>0x173B0: create the pieces (the first is this object), reusing free or results slots.</summary>
    private bool CreatePieces(SonicObject o)
    {
        if (Ram.U32(Vars.PlcBuffer) != 0) return false;   // wait for the art to load
        int a = o.Address;
        foreach (var p in Pieces)
        {
            while (Ram.U8(a) is not (0 or Id)) a += Objects.SlotSize;
            var piece = new SonicObject(Ram, a);
            piece.Id = Id;
            piece.X = p.X;
            piece.SetWord(0x30, p.TargetX);
            piece.XSub = p.Y;   // screen y
            piece.Routine = p.Routine;
            piece.MappingFrame = p.Frame;
            piece.Mappings = Mappings;
            piece.RenderFlags = 0;   // screen coordinates
            a += Objects.SlotSize;
        }
        return true;
    }

    /// <summary>0x170D2: slide 16 px a frame toward the target; drawn while within x $200.</summary>
    private void Slide(SonicObject o)
    {
        int x = o.X, target = TargetX(o);
        if (x == target) { Display(o); return; }
        o.X = x + (x > target ? -SlideSpeed : SlideSpeed);
        if (o.X <= 0x200) Display(o);
    }

    /// <summary>0x174D0: count 10 points a frame from each bonus into the score.</summary>
    private void Tally(SonicObject o)
    {
        Display(o);
        Ram.W8(Vars.UpdateBonusScore, 1);
        int points = 0;
        foreach (int bonus in new[] { Vars.BonusCountdown1, Vars.BonusCountdown2, Vars.BonusCountdown3 })
        {
            if (Ram.U16(bonus) == 0) continue;
            points += 10;
            Ram.W16(bonus, Ram.U16(bonus) - 10);
        }
        Ram.W16(Vars.TotalBonusCountdown, Ram.U16(Vars.TotalBonusCountdown) + points);
        if (points != 0)
        {
            Ctx.Touch.AddPoints(points);
            if ((Ram.U8(Vars.VintRuncountLow) & 3) == 0) Ctx.Sound.PlaySound(SfxTally);
            return;
        }
        Ctx.Sound.PlaySound(SfxChaChing);
        o.Routine = WaitAfterTally;
        SetTimer(o, 0xB4);
        if (Ram.U16(Vars.TotalBonusCountdown) < 1000) return;
        // 1000 or more bonus points earn a continue (0x17530).
        SetTimer(o, 0x12C);
        int slot = o.Address + Objects.SlotSize;
        while (Ram.U8(slot) != 0) slot += Objects.SlotSize;
        var icon = new SonicObject(Ram, slot);
        icon.Id = Id;
        icon.Routine = ContinueWait;
        icon.X = 0x188;
        icon.XSub = 0x118;   // screen y
        icon.Mappings = Mappings;
        icon.RenderFlags = 0;
        SetTimer(icon, 0x3C);
        Ram.W8(Vars.ContinueCount, Ram.U8(Vars.ContinueCount) + 1);
    }

    /// <summary>0x175DC / 0x175F8: the continue icon appears after a second, with its jingle, and blinks.</summary>
    private void ContinueIcon(SonicObject o)
    {
        if (o.Routine == ContinueWait)
        {
            if (Timer(o) != 0) { SetTimer(o, Timer(o) - 1); return; }
            o.Routine = ContinueBlink;
            Ctx.Sound.PlaySound(SfxContinue);
        }
        SetTimer(o, Timer(o) - 1);
        if ((short)Timer(o) < 0)
        {
            SetTimer(o, 0x13);
            o.AnimFrame = (o.AnimFrame + 1) & 1;
        }
        o.MappingFrame = 0x0C + o.AnimFrame;
        if (Ram.Bit(Vars.LevelFrameCounter + 1, 4)) Display(o);   // blink every 16 frames
    }

    /// <summary>
    /// The SMB3 Easter egg is not in the level order: finishing it ends the session (zone $FF is never ported, and
    /// nothing follows it).
    /// </summary>
    private void EndEasterEgg()
    {
        Ram.W16(Vars.CurrentZoneAndAct, 0xFF00);
        Ram.W8(Vars.LastStarPoleHit, 0);
        Ram.W8(Vars.LastStarPoleHitP2, 0);
        Ram.W16(Vars.LevelInactiveFlag, 1);
    }

    /// <summary>0x17592: go to the next act.</summary>
    private void GoToNextAct()
    {
        int current = Ram.U16(Vars.CurrentZoneAndAct);
        if (current == 0x1B01) throw new UnportedException(0x17624, "zone 1B act 2 results");
        if (Camera.IsEasterEgg(current >> 8)) { EndEasterEgg(); return; }
        if (NextLevel(Ctx.Art, current) is not { } next || (short)next < 0)
            throw new UnportedException(0x175B8, $"level order after {current:X4}");
        Ram.W16(Vars.CurrentZoneAndAct, next);
        Ram.W8(Vars.LastStarPoleHit, 0);
        Ram.W8(Vars.LastStarPoleHitP2, 0);
        Ram.W16(Vars.LevelInactiveFlag, 1);
    }
}
