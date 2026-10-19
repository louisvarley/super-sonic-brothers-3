using S2Port.Extraction.Levels;
using SonicBros3.Logic;
using S2Port.Extraction.Smb3;

namespace SonicBros3.Runtime;

/// <summary>
/// A playable act: the ported game logic (<see cref="World"/>), animated tiles and the VDP renderer.
/// With FreeCamera set, the logic is not run and the camera is moved directly (level viewer).
///
/// Level starts follow the original (Level, 0x4C2A): when an act ends or restarts the screen fades to black
/// (Pal_FadeToBlack 0x2B7E, 22 frames; the music fades too), stays black while the next act loads, then the title card
/// slides in, waits for the level art, and uncovers the level before play starts (see <see cref="Phase"/>).
/// </summary>
public sealed class LevelSession
{
    private readonly string _dataDir;
    private int _zone, _act;
    private readonly int _firstZone, _firstAct;
    private (int X, int Y)? _debugStart;

    public World World { get; private set; } = null!;
    public LevelData Level => World.Level;
    public AnimatedTiles Animation { get; private set; } = null!;
    public Vdp Vdp { get; } = new();
    public bool FreeCamera { get; set; }
    public BackgroundPlane PlaneB { get; private set; } = null!;
    /// <summary>Set when the logic reached code that is not ported yet; the session stops stepping.</summary>
    public UnportedException? Stopped { get; private set; }
    /// <summary>Set when the player finished the last ported act (the next act's zone is not ported yet).</summary>
    public int? CompletedTo { get; private set; }
    /// <summary>Set when the game-over sequence ended (no lives left).</summary>
    public bool IsGameOver { get; private set; }
    /// <summary>Why the session stopped, for display; null while running.</summary>
    public string? StopMessage =>
        IsGameOver ? "Game over" :
        CompletedTo is { } next ? Camera.IsEasterEgg(_finishedZone) ? $"{ZoneName(_finishedZone)} complete!"
            : $"{ZoneName(_finishedZone)} complete! (zone ${next >> 8:X2} and the zones after it are not ported yet)" : Stopped?.Message;
    /// <summary>Zones (zone &lt;&lt; 8 | act) the level order passed over because they are not ported yet.</summary>
    public List<int> ZonesSkipped { get; } = [];

    /// <summary>The hack's names for its zones (each zone is two zone ids: acts 1-2, then act 3).</summary>
    public static string ZoneName(int zone) => zone switch
    {
        0x11 or 0x12 => "Green Hill",
        0x13 or 0x14 => "Marble Zone",
        S2Port.Extraction.Smb3.Smb3Campaign.Zone => "Super Mario Bros. 3 campaign area",
        Camera.EasterEggZone => "Super Mario Bros. 3 World 1-1",
        Camera.World12Zone => "Super Mario Bros. 3 World 1-2",
        var smb3Zone when S2Port.Extraction.Smb3.Smb3Levels.ByZone(smb3Zone) is { } level => $"Super Mario Bros. 3 World {level.Name}",
        _ => $"Zone ${zone:X2}",
    };
    public bool IsStopped => Stopped is not null || CompletedTo is not null || IsGameOver || Exited;

    /// <summary>
    /// Sonic Bros. 3: a death (a level restart request) or the end of the SMB3 goal fades out and ends the session
    /// (<see cref="Exited"/>), back to the world map, instead of restarting the act.
    /// </summary>
    public bool ExitOnRestart { get; init; }
    /// <summary>The session faded out after a death or the goal (with <see cref="ExitOnRestart"/>).</summary>
    public bool Exited { get; private set; }
    /// <summary>The card won at the SMB3 goal, when that is how the session ended.</summary>
    public int? GoalCard => World.GoalCard;
    private bool _exiting;
    public bool DrawBackground { get; set; } = true;
    public int CameraX { get; set; }
    public int CameraY { get; set; }
    public long FrameCount { get; private set; }
    /// <summary>Giant rings entered: the special stage they lead to is not ported and is skipped.</summary>
    public int SpecialStagesSkipped { get; private set; }

    /// <summary>Where the sound driver's chip writes go; null = no sound driver (muted, faster).</summary>
    public Audio.ISoundChips? Chips { get; }

    public enum StartPhase
    {
        /// <summary>The level is being played.</summary>
        Playing,
        /// <summary>The finished act fades to black.</summary>
        FadeOut,
        /// <summary>Black while the next act loads (the original decompresses art).</summary>
        Loading,
        /// <summary>The title card builds and slides in.</summary>
        CardIn,
        /// <summary>The card is in place; the original waits for the level art to finish decompressing.</summary>
        CardWait,
        /// <summary>The level is set up and the card uncovers it (objects run, controls locked).</summary>
        CardOut,
    }

    /// <summary>Where the level start is; buttons are ignored outside <see cref="StartPhase.Playing"/>.</summary>
    public StartPhase Phase { get; private set; }
    /// <summary>Title cards and fades (true, as the original); false starts acts instantly.</summary>
    public bool TitleCards { get; }
    public bool InTransition => Phase != StartPhase.Playing;

    // Measured on the original (MAME, zone $11): 22 fade frames, 27 black frames, 92 frames waiting for the art.
    private const int FadeFrames = 22, LoadingFrames = 27, ArtWaitFrames = 92;
    private int _phaseFrames;
    private bool _completeAfterFade;
    private int _finishedZone;
    private Smb3Pipe? _pipe;
    private int _pipeFrames, _pipeExitDirection, _pipeCooldown;
    public bool InPipe => _pipe is not null || _pipeExitDirection != 0;
    public bool MapPipeExit { get; private set; }
    public bool MapPipeFarExit { get; private set; }
    public int CampaignWorld { get; init; } = 1;
    private int? _bonusReturnAct;

    public LevelSession(string dataDir, int zone, int act, (int X, int Y)? debugStart = null, Audio.ISoundChips? chips = null,
        bool titleCards = true, Action<Logic.Ram>? newGame = null)
    {
        Chips = chips;
        TitleCards = titleCards;
        _dataDir = dataDir;
        _zone = zone;
        _act = act;
        (_firstZone, _firstAct) = (zone, act);
        _debugStart = debugStart;
        Load(null);
        newGame?.Invoke(World.Ram);
    }

    /// <summary>(Re)starts the act; <paramref name="previous"/> carries lives, score etc. over a restart.</summary>
    private void Load(Logic.Ram? previous, bool seamless = false)
    {
        World = new World(_dataDir, _zone, _act, _debugStart, previous, Chips, TitleCards && !seamless);
        Animation = new AnimatedTiles(Level, World.Ram);
        PlaneB = new BackgroundPlane(Level, World.Ram);
        PlaneB.DrawInitial();
        Level.Cram.CopyTo(Vdp.Cram, 0);
        CameraX = World.CameraX;
        CameraY = World.CameraY;
        Stopped = null;
        CompletedTo = null;
        IsGameOver = false;
        _phaseFrames = 0;
        if (!TitleCards || seamless) Phase = StartPhase.Playing;
        else if (previous is null) StartCard();   // a new game starts at the title card
        else Phase = StartPhase.Loading;
        if (previous is null && _debugStart is null && Level.Gameplay?.InitialExitDirection is > 0)
        {
            _pipeExitDirection = Level.Gameplay.InitialExitDirection;
            _pipeFrames = 0;
        }
    }

    private void StartCard()
    {
        World.StartTitleCard();
        Phase = StartPhase.CardIn;
        _phaseFrames = 0;
    }

    /// <summary>The current act (zone, 1-based act).</summary>
    public (int Zone, int Act) CurrentAct => (_zone, _act);

    /// <summary>
    /// A snapshot of the game state: the act and the 68k RAM, which holds all of the logic's state (the object
    /// co-simulation reloads RAM every iteration and matches the original). VRAM is not part of it: art loaded at
    /// run time (PLCs, Sonic's frame) shows up again as the game reloads it.
    /// </summary>
    public (int Zone, int Act, byte[] Ram) SaveState() => (_zone, _act, World.Ram.Bytes.ToArray());

    /// <summary>Restores a <see cref="SaveState"/> snapshot (reusing the loaded act when it is the same one).</summary>
    public void RestoreState(int zone, int act, ReadOnlySpan<byte> ram)
    {
        if ((zone, act) != (_zone, _act))
        {
            (_zone, _act) = (zone, act);
            _debugStart = null;
            Load(null);
        }
        World.Ram.Load(ram);
        World.TitleCardPlane.End();
        Phase = StartPhase.Playing;
        PlaneB.DrawInitial();
        CameraX = World.CameraX;
        CameraY = World.CameraY;
        Stopped = null;
        CompletedTo = null;
        IsGameOver = false;
    }

    /// <summary>Starts the act again from scratch (a new game); after completing the ported game, from the first act.</summary>
    public void Restart()
    {
        if (CompletedTo is not null || IsGameOver) (_zone, _act) = (_firstZone, _firstAct);
        _pipe = null;
        _pipeFrames = _pipeExitDirection = _pipeCooldown = 0;
        _bonusReturnAct = null;
        MapPipeExit = MapPipeFarExit = Exited = _exiting = false;
        Load(null);
    }

    /// <summary>Runs one frame. buttons: Mega Drive pad bits (Up 0, Down 1, Left 2, Right 3, B 4, C 5, A 6, Start 7).</summary>
    public void Step(int buttons)
    {
        if (Phase == StartPhase.Playing && !IsStopped && StepPipe(buttons)) return;
        if (IsStopped) return;
        if (!FreeCamera && Phase != StartPhase.Playing)
        {
            try { StepLevelStart(); }
            catch (UnportedException e) { Stopped = e; }
            FrameCount++;
            return;
        }

        StepPlaying(buttons);
    }

    private bool StepPipe(int buttons)
    {
        if (_pipeCooldown > 0) _pipeCooldown--;
        if (_pipe is null && _pipeExitDirection == 0)
        {
            if (_pipeCooldown != 0 || World.Sonic.O.Routine != PlayerRoutine.Control || Level.Gameplay is not { } gameplay)
                return false;
            var player = World.Sonic.O;
            _pipe = gameplay.Pipes.FirstOrDefault(p => (buttons & p.Direction) != 0 &&
                (p.Direction is 1 or 2
                    ? Math.Abs(player.X - p.X) <= 8 && Math.Abs(player.Y + (p.Direction == 2 ? player.YRadius : -player.YRadius) - p.Y) <= 6
                    : Math.Abs(player.Y - p.Y) <= 12 && Math.Abs(player.X + (p.Direction == 8 ? player.XRadius : -player.XRadius) - p.X) <= 6));
            if (_pipe is null) return false;
            if (_bonusReturnAct is not null && _pipe.Kind == "map") _pipe = _pipe with { Kind = "bonus-return" };
            if (_pipe.Kind != "map" && _pipe.Kind != "local") ResolvePipeTarget(_pipe);
            _pipeFrames = 0;
            if (_pipe.Direction is 1 or 2) player.X = _pipe.X;
            else player.Y = _pipe.Y;
            World.Tails.O.X = player.X;
            World.Tails.O.Y = player.Y;
            PipeSound();
        }
        int direction = _pipeExitDirection != 0 ? _pipeExitDirection : _pipe!.Direction;
        if (_pipe is { Kind: "local" } local && _pipeFrames >= 32)
        {
            int distance = _pipeFrames - 32, index = Math.Min(distance / 8, local.Path.Length - 2);
            int fraction = Math.Min(distance - index * 8, 8);
            var from = local.Path[index];
            var to = local.Path[index + 1];
            foreach (var player in new[] { World.Sonic.O, World.Tails.O })
            {
                player.X = Math.Abs(to[0] - from[0]) > 16 ? to[0] : from[0] + (to[0] - from[0]) * fraction / 8;
                player.Y = from[1] + (to[1] - from[1]) * fraction / 8;
            }
            direction = 0;
        }
        if (_pipeExitDirection != 0 && _pipeFrames == 0 && Level.Gameplay is { } exitGameplay)
        {
            int opposite = direction switch { 1 => 2, 2 => 1, 4 => 8, _ => 4 };
            var mouth = exitGameplay.Pipes.Where(p => p.Direction == opposite)
                .OrderBy(p => Math.Abs(p.X - World.Sonic.O.X) + Math.Abs(p.Y - World.Sonic.O.Y)).FirstOrDefault();
            if (mouth is not null && Math.Abs(mouth.X - World.Sonic.O.X) + Math.Abs(mouth.Y - World.Sonic.O.Y) < 96)
                foreach (var player in new[] { World.Sonic.O, World.Tails.O })
                {
                    player.X = mouth.X + (direction == 8 ? -9 : direction == 4 ? 9 : 0);
                    player.Y = mouth.Y + (direction == 2 ? -19 : direction == 1 ? 19 : 0);
                }
        }
        foreach (var player in new[] { World.Sonic.O, World.Tails.O })
        {
            player.ObjControl = 0x81;
            player.XVel = player.YVel = player.Inertia = 0;
            player.Anim = PlayerAnim.Walk;
            player.Rolling = player.RollJumping = false;
            player.YRadius = player.Address == Vars.MainCharacter ? 19 : 15;
            player.Priority = 4;
            player.X += direction == 8 ? 1 : direction == 4 ? -1 : 0;
            player.Y += direction == 2 ? 1 : direction == 1 ? -1 : 0;
        }
        World.Step(0);
        Animation.Step();
        PlaneB.Update();
        CameraX = World.CameraX;
        CameraY = World.CameraY;
        FrameCount++;
        int duration = _pipeExitDirection != 0 ? 48 :
            _pipe is { Kind: "local" } transit ? 33 + (transit.Path.Length - 1) * 8 : 32;
        if (++_pipeFrames < duration) return true;
        if (_pipeExitDirection != 0)
        {
            _pipeExitDirection = 0;
            _pipeCooldown = 24;
            foreach (var player in new[] { World.Sonic.O, World.Tails.O }) { player.ObjControl = 0; player.Priority = 2; }
            return true;
        }
        var entry = _pipe!;
        _pipe = null;
        if (entry.Kind == "local")
        {
            _pipeExitDirection = entry.ExitDirection;
            _pipeFrames = 0;
            PipeSound();
            return true;
        }
        if (entry.Kind == "map")
        {
            MapPipeExit = true;
            int startX = Level.Start.X;
            MapPipeFarExit = Level.Gameplay!.MapPipeway && Math.Abs(entry.X - startX) > 48;
            Exited = true;
            return true;
        }
        int targetAct = ResolvePipeTarget(entry);
        if (entry.Kind == "bonus") _bonusReturnAct = CampaignSource(Smb3Campaign.Load(_dataDir)!).Act;
        else if (_bonusReturnAct is not null) _bonusReturnAct = null;
        var previous = World.Ram;
        int rings = previous.U16(Vars.RingCount);
        uint timer = previous.U32(Vars.Timer);
        var powers = new[] { World.Sonic.O, World.Tails.O }
            .Select(p => (p.StatusSecondary, p.InvulnerableTime, p.InvincibilityTime, p.SpeedShoesTime)).ToArray();
        _act = targetAct;
        _zone = Smb3Campaign.Zone;
        int exit = entry.ExitDirection;
        _debugStart = (entry.TargetX, entry.TargetY);
        Load(previous, seamless: true);
        _debugStart = null;
        Phase = StartPhase.Playing;
        World.Ram.W16(Vars.RingCount, rings);
        World.Ram.W32(Vars.Timer, timer);
        for (int i = 0; i < 2; i++)
        {
            var player = i == 0 ? World.Sonic.O : World.Tails.O;
            player.StatusSecondary = powers[i].StatusSecondary;
            player.InvulnerableTime = powers[i].InvulnerableTime;
            player.InvincibilityTime = powers[i].InvincibilityTime;
            player.SpeedShoesTime = powers[i].SpeedShoesTime;
        }
        foreach (int slot in new[] { Vars.SonicShield, Vars.TailsShield, Vars.SonicInvincibilityStars, Vars.TailsInvincibilityStars })
            previous.Bytes.AsSpan(slot, Objects.SlotSize).CopyTo(World.Ram.Bytes.AsSpan(slot, Objects.SlotSize));
        World.Ram.W16(Vars.RingCountP2, previous.U16(Vars.RingCountP2));
        World.Ram.W32(Vars.TimerP2, previous.U32(Vars.TimerP2));
        World.Ram.W8(Vars.ExtraLifeFlags, previous.U8(Vars.ExtraLifeFlags));
        World.Ram.W8(Vars.ExtraLifeFlagsP2, previous.U8(Vars.ExtraLifeFlagsP2));
        _pipeExitDirection = exit;
        _pipeFrames = 0;
        if (exit == 0) _pipeCooldown = 24;
        PipeSound();
        return true;
    }

    private int ResolvePipeTarget(Smb3Pipe pipe)
    {
        var campaign = Smb3Campaign.Load(_dataDir)
            ?? throw new InvalidDataException("Pipe travel requires the native campaign archive.");
        Smb3CampaignArea source = CampaignSource(campaign);
        if (_bonusReturnAct is { } returning) return returning;
        string? target = pipe.Kind switch
        {
            "common-exit" => CampaignWorld == 4 ? "ts01-15-BDB8-C560" : "ts01-15-BD68-C560",
            "bonus" => campaign.Areas.Single(a => a.Source.LayoutAddress == new[] { 0xB1AF, 0xB1BC, 0xB28B, 0xB372,
                    0xB3D8, 0xB470, 0xB5E2, 0xB65B }[CampaignWorld - 1] && a.Source.Tileset == 14).Source.Id,
            _ => source.AlternateAreaId,
        };
        var destination = campaign.Areas.SingleOrDefault(a => a.Source.Id == target);
        if (destination is null || destination.Status is "failed" or "blocked")
            throw new InvalidDataException($"Pipe destination from {source.Source.Id} is unavailable: {target ?? "no source alternate"}.");
        return destination.Act;
    }

    private void PipeSound()
    {
        if (Chips is Audio.SoundMixer mixer) mixer.PlayEffect(_dataDir, "pipe");
    }

    private Smb3CampaignArea CampaignSource(Smb3CampaignData campaign) =>
        _zone == Smb3Campaign.Zone ? campaign.Areas.Single(a => a.Act == _act)
            : campaign.Areas.Single(a => a.Source.LayoutAddress == Smb3Levels.ByZone(_zone)!.Layout.Addr &&
                a.Source.ObjectAddress == Smb3Levels.ByZone(_zone)!.Objects.Addr);

    private void StepPlaying(int buttons)
    {
        if (!FreeCamera)
        {
            try { World.Step(buttons); }
            catch (UnportedException e)
            {
                Stopped = e;
                return;
            }
            if (World.Ram.U8(Vars.GameMode) != 0x0C)
            {
                // Game over: the original goes to the continue screen ($14) or back to the SEGA screen (0).
                IsGameOver = true;
                return;
            }
            if (ExitOnRestart && (World.RestartRequested || World.GoalCard is not null))
            {
                // Back to the map: the music and the screen fade out (Level, 0x4C76), then the session ends.
                _exiting = true;
                World.Sound.PlaySound(0xF9);
                Phase = StartPhase.FadeOut;
                _phaseFrames = 0;
                return;
            }
            if (World.RestartRequested)
            {
                // A death restarts the act; the results screen changes $FE10 first to move to the next act.
                _finishedZone = _zone;
                if (_zone != Smb3Campaign.Zone)
                {
                    _zone = World.Ram.U8(Logic.Vars.CurrentZoneAndAct);
                    _act = World.Ram.U8(Logic.Vars.CurrentZoneAndAct + 1) + 1;
                }
                SkipUnportedZones();
                _debugStart = null;   // a debug start applies to the first load only
                // A giant ring set $F7CE: the original plays the special stage (game mode $2C) before this load.
                // It is not ported, so the game goes straight on (as after a stage without an emerald).
                if (World.Ram.U8(Vars.SpecialStageRequested) != 0) SpecialStagesSkipped++;
                _completeAfterFade = !Camera.IsZonePorted(_zone);
                if (TitleCards)
                {
                    // Level (0x4C76): fade the music and the screen out first.
                    World.Sound.PlaySound(0xF9);
                    Phase = StartPhase.FadeOut;
                    _phaseFrames = 0;
                    return;
                }
                LoadNext();
                return;
            }
            PlaneB.Update();   // LoadTilesAsYouMove runs in the following VBlank
            CameraX = World.CameraX;
            CameraY = World.CameraY;
        }
        Animation.Step();
        FrameCount++;
    }

    /// <summary>
    /// Follows the level order past zones that are not ported yet, to the next one that is (the original would play
    /// them). With none left, the session completes after the fade.
    /// </summary>
    private void SkipUnportedZones()
    {
        if (_zone == Smb3Campaign.Zone) return;
        var skipped = new List<int>();
        if (NextPortedLevel(World.Art, (_zone << 8) | (_act - 1), Camera.IsZonePorted, skipped) is not { } level) return;
        ZonesSkipped.AddRange(skipped);
        (_zone, _act) = (level >> 8, (level & 0xFF) + 1);
    }

    /// <summary>
    /// The first level (zone &lt;&lt; 8 | act index) from <paramref name="level"/> on, in the level order, whose zone is
    /// ported; the ones passed over go to <paramref name="skipped"/>. Null when the order ends or loops first.
    /// </summary>
    public static int? NextPortedLevel(Logic.ObjectArt art, int level, Func<int, bool> isPorted, List<int> skipped)
    {
        while (!isPorted(level >> 8))
        {
            if (skipped.Contains(level) || Logic.Results.NextLevel(art, level) is not { } next || (short)next < 0) return null;
            skipped.Add(level);
            level = next;
        }
        return level;
    }

    private void LoadNext()
    {
        if (_completeAfterFade)
        {
            CompletedTo = (_zone << 8) | (_act - 1);
            return;
        }
        try { Load(World.Ram); }
        catch (UnportedException e) { Stopped = e; }
    }

    /// <summary>One frame of the fade, the black loading pause or the title card.</summary>
    private void StepLevelStart()
    {
        _phaseFrames++;
        switch (Phase)
        {
            case StartPhase.FadeOut:
                World.FadeToBlackStep();
                World.UpdateSound();
                if (_phaseFrames >= FadeFrames)
                {
                    if (_exiting) Exited = true;
                    else LoadNext();
                }
                break;
            case StartPhase.Loading:
                World.UpdateSound();
                if (_phaseFrames >= LoadingFrames) StartCard();
                break;
            case StartPhase.CardIn:
                World.StepTitleCard(levelReady: false);
                if (World.TitleCardIn) { Phase = StartPhase.CardWait; _phaseFrames = 0; }
                break;
            case StartPhase.CardWait:
                World.StepTitleCard(levelReady: false);
                if (_phaseFrames >= ArtWaitFrames)
                {
                    World.TitleCardLevelReady();
                    Phase = StartPhase.CardOut;
                    _phaseFrames = 0;
                }
                break;
            case StartPhase.CardOut:
                World.StepTitleCard(levelReady: true);
                if (World.TitleCardUncovered)
                {
                    World.FinishTitleCard();
                    Phase = StartPhase.Playing;
                }
                PlaneB.Update();
                CameraX = World.CameraX;
                CameraY = World.CameraY;
                break;
        }
    }

    public void Render()
    {
        if (!FreeCamera && Phase is StartPhase.Loading || (Phase == StartPhase.FadeOut && (_completeAfterFade || _exiting) && _phaseFrames >= FadeFrames))
        {
            Vdp.RenderBlack();
            return;
        }
        if (FreeCamera)
            Vdp.RenderLevel(Level, CameraX, CameraY, CameraX / 2, CameraY / 4, DrawBackground);   // viewer: simple parallax
        else
        {
            for (int i = 0; i < 64; i++) Vdp.Cram[i] = World.Ram.U16(Vars.NormalPalette + i * 2);   // palette RAM -> CRAM
            // The hack's lava line: an H-interrupt changes a few colours from a line down.
            if (Phase == StartPhase.Playing && World.LavaLine.Split(Vdp.Cram) is { } split)
            {
                Vdp.SplitLine = split.Line;
                split.Cram.CopyTo(Vdp.CramBelow, 0);
            }
            else Vdp.SplitLine = -1;
            // While the card builds, plane B is still empty (the original draws it with the level, under the card).
            bool background = DrawBackground && Phase is not (StartPhase.CardIn or StartPhase.CardWait);
            var planeA = World.TitleCardPlane.Active ? World.TitleCardPlane.Cells : null;
            Vdp.RenderLevel(Level, World.Ram, background, PlaneB, planeA);                               // game: scroll RAM
        }

    }
}
