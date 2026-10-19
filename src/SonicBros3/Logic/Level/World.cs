using S2Port.Extraction.Levels;
using SonicBros3.Audio;

namespace SonicBros3.Logic;

/// <summary>
/// Sets up 68k RAM for an act from extracted data, the way the original's level loader leaves it
/// (LevelDataLoad, LoadCollisionIndexes, LevelSizeLoad), and places Sonic.
/// </summary>
public sealed class World
{
    public Ram Ram { get; } = new();
    public LevelData Level { get; private set; }
    public Player Sonic { get; }
    /// <summary>The sidekick (slot $B040); present unless the zone has none (InitPlayers 0x52CE).</summary>
    public Player Tails { get; }
    /// <summary>The 68k SMPS driver (hack zones), run in each frame's VBlank; null when the world was made without sound.</summary>
    public Smps68kDriver? SoundDriver { get; }
    /// <summary>Sonic 2's Z80 driver, in Sonic 2's zones (null in the hack's zones or without sound).</summary>
    public SmpsZ80Driver? Z80Sound { get; }

    /// <param name="previous">
    /// The RAM of the run being restarted (after a death), or null for a new game. A restart keeps the globals the
    /// original's level init leaves alone ($FE00-$FE5F and $FEB0-$FFFF: lives, score, checkpoint...).
    /// </param>
    /// <param name="chips">Where the sound driver's chip writes go; null runs no sound driver at all.</param>
    /// <param name="titleCard">
    /// Start with the title card (as the original's level start does): the card's objects run first, with the card's
    /// palette and the music queued by <see cref="StartTitleCard"/>; see <see cref="Runtime.LevelSession"/>.
    /// </param>
    public World(string dataDir, int zone, int act, (int X, int Y)? debugStart = null, Ram? previous = null, ISoundChips? chips = null,
        bool titleCard = false)
    {
        Level = LevelLoader.Load(dataDir, zone, act);
        if (debugStart is { } ds) Level = Level.WithStart(ds);
        var ram = Ram;
        if (previous is not null)
        {
            // The variables that live across levels (score, lives, saved state...): $FE00-$FE5F and $FEB0-$FFFF.
            previous.Bytes.AsSpan(0xFE00, 0x60).CopyTo(ram.Bytes.AsSpan(0xFE00));
            previous.Bytes.AsSpan(0xFEB0, 0x150).CopyTo(ram.Bytes.AsSpan(0xFEB0));
            previous.Bytes.AsSpan(Smps68kDriver.Base, 0x5C0).CopyTo(ram.Bytes.AsSpan(Smps68kDriver.Base));   // the driver plays on
            // Level init (0x4FFC) without a checkpoint: rings, time and the extra-life flags reset.
            if (ram.U8(Vars.LastStarPoleHit) == 0)
            {
                ram.W16(Vars.RingCount, 0);
                if (ram.U8(Vars.TimedMode) == 0) ram.W32(Vars.Timer, 0);
                ram.W8(Vars.ExtraLifeFlags, 0);
                ram.W16(Vars.RingCountP2, 0);
                ram.W32(Vars.TimerP2, 0);
                ram.W8(Vars.ExtraLifeFlagsP2, 0);
            }
            foreach (int a in new[] { Vars.TimeOverFlag, Vars.TimeOverFlagP2, Vars.SlotMachineRoutine, Vars.TeleportTimer, Vars.TeleportFlag })
                ram.W8(a, 0);
            foreach (int a in new[] { Vars.SlotMachineInUse, Vars.DebugPlacementMode, Vars.LevelInactiveFlag, Vars.RingsCollected,
                         Vars.RingsCollectedP2, Vars.MonitorsBroken, Vars.MonitorsBrokenP2, Vars.LoserTimeLeft })
                ram.W16(a, 0);
            ram.W16(Vars.LevelFrameCounter, 0);
        }

        // Chunks at $0000, layout at $8000 (FG/BG rows interleaved), blocks at $9000.
        for (int i = 0; i < Level.ChunkWords.Length && i * 2 < 0x8000; i++) ram.W16(Vars.ChunkTable + i * 2, Level.ChunkWords[i]);
        for (int row = 0; row < LevelData.LayoutHeight; row++)
        for (int x = 0; x < LevelData.LayoutWidth; x++)
        {
            ram.W8(Vars.LevelLayout + row * 0x100 + x, Level.Foreground[row * LevelData.LayoutWidth + x]);
            ram.W8(Vars.LevelLayout + row * 0x100 + 0x80 + x, Level.Background[row * LevelData.LayoutWidth + x]);
        }
        for (int i = 0; i < Level.BlockWords.Length; i++) ram.W16(Vars.BlockTable + i * 2, Level.BlockWords[i]);
        if (Level.CollisionPrimary is { } p) p.CopyTo(ram.Bytes, Vars.PrimaryCollision);
        if (Level.CollisionSecondary is { } s) s.CopyTo(ram.Bytes, Vars.SecondaryCollision);

        ram.W16(Vars.CurrentZoneAndAct, (zone << 8) | ((act - 1) & 255));
        for (int i = 0; i < 64; i++) ram.W16(Vars.NormalPalette + i * 2, Level.Cram[i]);
        ram.W8(Vars.HackZoneFlag, zone >= 0x11 ? 1 : 0);   // Level, 0x4C56
        // SoundDriverLoad (0x142A): the 68k driver in the hack's zones, Sonic 2's Z80 driver in its own; the Z80
        // driver is loaded afresh only when coming from the 68k one.
        bool reloadZ80 = previous is not null && previous.U8(Vars.SoundDriverSelect) != 0;
        ram.W8(Vars.SoundDriverSelect, ram.U8(Vars.HackZoneFlag));

        // LevelSizeLoad (0xD784): bounds, default camera bias, start position and initial camera.
        var (minX, maxX, minY, maxY) = Level.Bounds;
        ram.W16(Vars.CameraMinXPos, minX);
        ram.W16(Vars.CameraMaxXPos, maxX);
        ram.W16(Vars.CameraMinYPos, minY);
        ram.W16(Vars.CameraMaxYPos, maxY);
        ram.W16(Vars.CameraYPosBias, 0x60);
        ram.W8(Vars.MainCharacter, 1);
        ram.Sonic.X = Level.Start.X;
        ram.W16(Vars.MainCharacter + 0x0C, Level.Start.Y);
        if (ram.U8(Vars.LastStarPoleHit) != 0) Starpost.LoadData(ram);   // restarting from a star post (0xD99E)
        int startX = ram.Sonic.X, startY = ram.Sonic.Y;
        // Tails' copies of the camera limits (equal to Sonic's in Sonic-and-Tails mode).
        for (int i = 0; i < 8; i += 2) ram.W16(Vars.TailsMinXPos + i, ram.U16(Vars.CameraMinXPos + i));
        // InitPlayers (0x52CE), Sonic-and-Tails mode: dust objects, and Tails 32 px behind Sonic.
        ram.W8(PlayerProfile.Sonic.Dust, 8);
        if (zone is not (0x06 or 0x0E or 0x10))
        {
            ram.W8(Vars.Sidekick, 2);
            ram.Tails.X = startX - 0x20;
            ram.W16(Vars.Sidekick + 0x0C, startY + 4);
            ram.W8(PlayerProfile.Tails.Dust, 8);
        }
        int camX = startX - 0xA0;
        if (camX < 0) camX = 0;
        if ((uint)camX >= (uint)maxX) camX = maxX;
        int camY = startY - 0x60;
        if (camY < 0) camY = 0;
        maxY = ram.U16(Vars.CameraMaxYPos);
        if (camY >= (short)maxY) camY = maxY;

        Rings = new RingsManager(ram);
        Rings.Setup(Level.ExpandRings());
        if (previous is null)
        {
            // A new game (title screen): 3 lives, the first extra life at 50000 points.
            ram.W8(Vars.LifeCount, 3);
            ram.W32(Vars.NextExtraLifeScore, 5000);
        }

        var normal = LevelLoader.LoadCollisionSet(Path.Combine(dataDir, "collision", "normal.json"));
        var alternate = LevelLoader.LoadCollisionSet(Path.Combine(dataDir, "collision", "alternate.json"));
        Sound = new RamSound(ram);
        if (chips is not null && ram.U8(Vars.SoundDriverSelect) == 0)
        {
            Z80Sound = SmpsZ80Driver.For(chips, dataDir);
            if (reloadZ80) Z80Sound.Reset();
            int music = Smps68kData.LoadLevelMusic(dataDir, ram.U16(Vars.TwoPlayerMode) != 0)[zone];
            ram.W16(Vars.LevelMusic, music);
            if (!titleCard) Sound.PlayMusic(music);
        }
        if (chips is not null && ram.U8(Vars.SoundDriverSelect) != 0)
        {
            // The SMB3 levels bring their own songs (zones/zone_XX/music.json), else play Green Hill's music.
            string? extraSongs = Level.MusicFile ?? (Logic.Camera.IsEasterEgg(zone) ? Path.Combine(dataDir, "zones", $"zone_{zone:X2}", "music.json") : null);
            var soundData = new Smps68kData(dataDir, extraSongs);
            SoundDriver = new Smps68kDriver(ram, soundData, chips);
            if (previous is null) ram.W8(Smps68kDriver.Base + 0x09, 0x80);   // the driver as left by its init at boot: nothing to play
            // Level (0x4E94): the zone's music (the 2-player list in 2-player mode; $1801 has its own).
            // The SMB3 Easter egg (zone $40, past the music list) plays Green Hill's music.
            int musicZone = Logic.Camera.IsEasterEgg(zone) ? 0x11 : zone;
            int music = (ram.U16(Vars.TwoPlayerMode) != 0 ? soundData.LevelMusic2P : soundData.LevelMusic)[musicZone];
            if (extraSongs is not null && S2Port.Extraction.DataFile.Exists(extraSongs))
            {
                using var songs = System.Text.Json.JsonDocument.Parse(S2Port.Extraction.DataFile.ReadAllText(extraSongs));
                music = songs.RootElement.GetProperty("songs")[0].GetProperty("id").GetInt32();
            }
            if (ram.U16(Vars.CurrentZoneAndAct) == 0x1801) music = 0x86;
            ram.W16(Vars.LevelMusic, music);
            if (!titleCard) Sound.PlayMusic(music);
        }
        Plc = new PlcQueue(ram, Level);
        var collision = new Collision(ram, normal, alternate);
        Sonic = new Player(ram, collision, SonicAnimation.Load(ram, dataDir), Sound);
        var tailsAnim = new TailsAnimation(ram, dataDir);
        Tails = new Player(ram, collision, tailsAnim, Sound, PlayerProfile.Tails);
        if (Level.Gameplay is { } gameplay)
        {
            Sonic.WaterAt = gameplay.IsWater;
            Tails.WaterAt = gameplay.IsWater;
        }
        Camera = new Camera(ram) { LoadPlc = Plc.Load, Sound = Sound, WaveTableData = new ObjectArt(dataDir).DataTable("frontend/title-scroll.json").Data };
        Oscillation = new Oscillation(ram, dataDir, zone >= 0x11);
        Oscillation.Init();   // OscillateNumInit (0x5D26)
        var sonicSprites = SpriteData.Load(dataDir, "sonic");
        Sonic.Sprites = sonicSprites;
        Sonic.Vram = Level.Vram;
        Sonic.LoadPlc = Plc.Load;
        ram.W8(Vars.GameMode, 0x0C);   // level mode
        var tailsSprites = SpriteData.Load(dataDir, "tails");
        Tails.Sprites = tailsSprites;
        Tails.Vram = Level.Vram;
        Art = new ObjectArt(dataDir);
        Art.LoadAll();
        Art.Add("sonic", sonicSprites, 0x76860);
        Sprites = new SpriteBuilder(ram, Art, SpriteData.Load(dataDir, "rings"));
        LavaLine = new LavaLine(ram, Art, Level.Vram);
        LavaLine.Init();   // 0x4E82
        ram.W8(Vars.SonicLastLoadedDplc, 0xFF);   // no DPLC frame loaded yet
        Hud = new Hud(ram, dataDir) { Vram = Level.Vram };
        Sprites.Hud = Hud;
        Hud.Base();             // Hud_Base (0x4F16)
        var context = new ObjectContext(ram, collision, Art, Sound) { TailsAnimation = tailsAnim, TailsSprites = tailsSprites, Vram = Level.Vram, LoadPlc = Plc.Load, LoadFlashArt = LoadGiantRingFlashArt,
            DeformBgLayer = () => Camera!.DeformBgLayer(),
            AfterTitleCard = () => { foreach (int id in Level.AfterTitleCardPlcs) Plc.Load(id); },
            TitleCardNameFrame = z => _titleCardArt?.NameFrame(z) ?? 0 };
        context.GoalReached = card => GoalCard = card;
        Objects = new Objects(context);
        _dataDir = dataDir;
        ObjectManager = new ObjectManager(ram, Level.Objects, Level.ObjectListAddress, Level.ObjectBoundaryX);
        ram.W16(Vars.CameraXPos, camX);
        ram.W16(Vars.CameraYPos, camY);
        ram.W16(Vars.HorizBlockCrossedFlag, 0x1010);                // scroll-flag toggles (LevelSizeLoad)
        ram.W16(Vars.CameraMaxYPosTarget, maxY);
        InitCameraValues(camX, camY, zone);
        // 0x5F1E: acts that end with a signpost (the others end with a boss or the like).
        int[] noSignpost = [0x0001, 0x0500, 0x0600, 0x0701, 0x0A01, 0x0B01, 0x0C01, 0x0D01, 0x0E00, 0x0F01,
                            0x1000, 0x1200, 0x1400, 0x1600, 0x1800, 0x1A00, 0x1C00];
        ram.W16(Vars.LevelHasSignpost, !Logic.Camera.IsEasterEgg(zone) &&
            (ram.U16(Vars.TwoPlayerMode) != 0 || !noSignpost.Contains(ram.U16(Vars.CurrentZoneAndAct))) ? 1 : 0);
        // Level init (0x505A): the HUD redraws score, rings and time on the first VBlank.
        ram.W8(Vars.UpdateHudScore, 1);
        ram.W8(Vars.UpdateHudRings, 1);
        ram.W8(Vars.UpdateHudTimer, 1);
        ram.W8(Vars.UpdateHudTimerP2, 1);
        ObjectManager.Run();   // level init runs the object manager once before the first frame
        Rings.Run();           // and lays out the ring window
        if (debugStart is not null)
        {
            // Debug starts skip the level events that normally lower the bottom limit on the way there.
            Camera.RunLevelEvents();
            ram.W16(Vars.CameraMaxYPos, ram.U16(Vars.CameraMaxYPosTarget));
            // InitCameraValues puts background layers 2/3 at the camera x, which is right at an act start; for a
            // debug start, put them where scrolling there from x 0 would have (Green Hill: x/2 and x*3/8), so they
            // stay inside the background layout.
            if (zone is 0x11 or 0x12 && ram.U8(Vars.LastStarPoleHit) == 0)
            {
                int cx = ram.U16(Vars.CameraXPos);
                ram.W32(Vars.CameraBg2XPos, (uint)(cx << 15));
                ram.W32(Vars.CameraBg3XPos, (uint)(cx * 0x6000));
            }
            if (zone is 0x13 or 0x14 && ram.U8(Vars.LastStarPoleHit) == 0)
            {
                int cx = ram.U16(Vars.CameraXPos);   // Marble Zone: layers at 3/4, 1/2 and 1/4 of the camera's x
                ram.W32(Vars.CameraBgXPos, (uint)(cx * 0xC000));
                ram.W32(Vars.CameraBg2XPos, (uint)(cx << 15));
                ram.W32(Vars.CameraBg3XPos, (uint)(cx << 14));
            }
        }
        ram.W8(Vars.LevelStartedFlag, 1);     // ring/HUD drawing enabled (set by the time the level loop runs; see docs)
        if (titleCard)
        {
            string? campaignName = null;
            if (zone == S2Port.Extraction.Smb3.Smb3Campaign.Zone)
            {
                var campaign = S2Port.Extraction.Smb3.Smb3Campaign.Load(dataDir)!;
                var area = campaign.Areas.Single(a => a.Act == act);
                var sourceWorld = campaign.Worlds.FirstOrDefault(w => w.Entries.Any(e => e.AreaId == area.Source.Id));
                campaignName = sourceWorld is null ? "SECRET AREA" : new[]
                    { "GRASS LAND", "DESERT LAND", "WATER LAND", "GIANT LAND", "SKY LAND", "ICE LAND", "PIPE LAND", "DARK LAND" }[sourceWorld.Number - 1];
            }
            _titleCardArt = new TitleCardArt(dataDir, campaignName);
            Art.Add("title-card-smb3", _titleCardArt.CampaignName ?? TitleCardArt.EasterEggName(_titleCardArt.LetterTile, _titleCardArt.BaseTile),
                TitleCardArt.EasterEggNameMappings);
            for (int i = 0; i < 64; i++) ram.W16(Vars.NormalPalette + i * 2, Level.TitleCardCram[i]);
            ram.W8(Vars.LevelStartedFlag, 0);
            ram.W8(Vars.ControlLocked, 1);
            ram.W8(Vars.ControlLockedP2, 1);
        }
    }

    // ------------------------------------------------------------------ level start with the title card

    private readonly TitleCardArt? _titleCardArt;
    /// <summary>Plane A as the title card leaves it (an override over the level's plane A while it is active).</summary>
    public TitleCardPlane TitleCardPlane { get; } = new();

    /// <summary>0x4EBC: the zone music and the title card object (its art loaded by LoadTitleCard, 0x18D7C).</summary>
    public void StartTitleCard()
    {
        if (Ram.U16(Vars.LevelMusic) != 0) Sound.PlayMusic(Ram.U16(Vars.LevelMusic));
        if (Level.Vram is { } vram) _titleCardArt?.Load(vram, Ram.U8(Vars.CurrentZoneAndAct));
        TitleCardObject.Start(Ram);
        TitleCardPlane.Begin();
    }

    /// <summary>
    /// One frame of the level start's title-card loops: before the level is set up (0x4EC6) only the card runs; after
    /// (0x5138) everything does, with the controls locked. VBlank draws the card's plane fills.
    /// </summary>
    public void StepTitleCard(bool levelReady)
    {
        if (levelReady)
        {
            if (Ram.U8(Vars.MainCharacter) != 0) Sonic.Step();
            if (Ram.U8(Vars.Sidekick) == 2) Tails.Step();
            Objects.RunAfterPlayers();
        }
        else Objects.RunRange(Vars.TitleCardZoneName, Vars.TitleCardLeft + Objects.SlotSize);
        Sprites.BuildSprites();
        Ram.W32(Vars.VintRuncount, Ram.U32(Vars.VintRuncount) + 1);
        TitleCardPlane.Update(Ram);
        UpdateSound();
    }

    /// <summary>The card is in place (0x4EFC: the zone name reached its target).</summary>
    public bool TitleCardIn => TitleCardObject.IsIn(Ram);

    /// <summary>
    /// The level part of the start (0x4F1C-0x5132): the level's palette, one RunObjects pass, then the card's exit:
    /// VBlank switches to uncovering ($B0BE) and the red strip starts retracting.
    /// </summary>
    public void TitleCardLevelReady()
    {
        for (int i = 0; i < 64; i++) Ram.W16(Vars.NormalPalette + i * 2, Level.Cram[i]);
        if (Ram.U8(Vars.MainCharacter) != 0) Sonic.Step();
        if (Ram.U8(Vars.Sidekick) == 2) Tails.Step();
        Objects.RunAfterPlayers();
        Sprites.BuildSprites();
        TitleCardObject.BeginExit(Ram);
    }

    /// <summary>The blue piece has gone (0x5152): the level is uncovered.</summary>
    public bool TitleCardUncovered => TitleCardObject.Uncovered(Ram);

    /// <summary>0x5158-0x5192: the text slides away during play; controls and the HUD come on.</summary>
    public void FinishTitleCard()
    {
        TitleCardObject.SlideTextOut(Ram);
        Ram.W8(Vars.ControlLocked, 0);
        Ram.W8(Vars.ControlLockedP2, 0);
        Ram.W8(Vars.LevelStartedFlag, 1);
        TitleCardPlane.End();
    }

    /// <summary>One step of Pal_FadeToBlack (0x2B9E): each colour loses red, else green, else blue.</summary>
    public void FadeToBlackStep()
    {
        for (int i = 0; i < 64; i++)
        {
            int a = Vars.NormalPalette + i * 2, c = Ram.U16(a);
            if (c == 0) continue;
            if ((c & 0x00E) != 0) c -= 0x002;
            else if ((c & 0x0E0) != 0) c -= 0x020;
            else if ((c & 0xE00) != 0) c -= 0x200;
            Ram.W16(a, c);
        }
    }

    /// <summary>InitCameraValues (0xDAEC) and the zone's own part (zone $11: 0xDC7C).</summary>
    private void InitCameraValues(int camX, int camY, int zone)
    {
        if (Ram.U8(Vars.LastStarPoleHit) == 0)   // after a star post the saved background positions are kept (0xDAEC)
        {
            foreach (int a in new[] { Vars.CameraBgYPos, Vars.CameraBg2YPos, Vars.CameraBgYPosP2, Vars.CameraBg2YPosP2 }) Ram.W16(a, camY);
            foreach (int a in new[] { Vars.CameraBgXPos, Vars.CameraBg2XPos, Vars.CameraBg3XPos, Vars.CameraBgXPosP2,
                         Vars.CameraBg2XPosP2, Vars.CameraBg3XPosP2 })
                Ram.W16(a, camX);
        }
        if (zone is 0x13 or 0x14 || Logic.Camera.IsEasterEgg(zone)) return;   // 0xDC98: Marble Zone has no part of its own
        if (zone == 0x00)
        {
            // 0xDB64 (Emerald Hill): background layer positions and the scroll state at $A800 cleared.
            foreach (int a in new[] { Vars.CameraBgXPos, Vars.CameraBgYPos, Vars.CameraBg2YPos, Vars.CameraBg3YPos,
                         Vars.TempArrayLayerDef, Vars.TempArrayLayerDef + 4, Vars.TempArrayLayerDef + 8,
                         Vars.CameraBgXPosP2, Vars.CameraBgYPosP2, Vars.CameraBg2YPosP2, Vars.CameraBg3YPosP2 })
                Ram.W32(a, 0);
            return;
        }
        if (zone is not (0x11 or 0x12)) throw new UnportedException(0xDB1A, $"camera init for zone {zone:X2}");
        Ram.W32(Vars.CameraBgXPos, 0);
        Ram.W32(Vars.CameraBgYPos, 0);
        Ram.W32(Vars.CameraBg2YPos, 0);
        Ram.W32(Vars.CameraBg3YPos, 0);
        for (int i = 0; i < 12; i++) Ram.W8(Vars.CloudCounters + i, 0);
    }

    public Camera Camera { get; }
    /// <summary>Set when the SMB3 goal's sequence ended: the card won (0 rings, 1 shield, 2 invincibility).</summary>
    public int? GoalCard { get; private set; }
    public LavaLine LavaLine { get; }
    public Oscillation Oscillation { get; }

    /// <summary>The art queued during play (see <see cref="PlcQueue"/>).</summary>
    public PlcQueue Plc { get; }
    /// <summary>The original's level-restart request ($FE02), set when Sonic's death countdown ends.</summary>
    public bool RestartRequested => Ram.U16(Vars.LevelInactiveFlag) != 0;
    public SpriteBuilder Sprites { get; }
    public Objects Objects { get; }
    public RingsManager Rings { get; }
    public Hud Hud { get; }
    public ObjectArt Art { get; }
    public ISoundRequests Sound { get; }
    public ObjectManager ObjectManager { get; }
    public int CameraX => Ram.U16(Vars.CameraXPos);
    public int CameraY => Ram.U16(Vars.CameraYPos);

    /// <summary>One game frame. buttons: held Mega Drive pad bits (Up=0 Down=1 Left=2 Right=3 B=4 C=5 A=6 Start=7).</summary>
    /// <param name="pressed">Newly pressed buttons; computed from the previous frame when omitted.</param>
    public void Step(int buttons, int? pressed = null)
    {
        int previous = Ram.U8(Vars.Ctrl1Held);
        Ram.W8(Vars.Ctrl1Held, buttons);
        Ram.W8(Vars.Ctrl1Press, pressed ?? (buttons & ~previous));
        Ram.W16(Vars.LevelFrameCounter, Ram.U16(Vars.LevelFrameCounter) + 1);   // Level_frame_counter
        // Level loop order (0x519E): RunObjects, DeformBgLayer, ..., palette cycle, BuildSprites, ObjectsManager.
        bool dying = Ram.Sonic.Routine >= 6;   // RunObjects checks this before running anyone
        LavaLine.Step();                                    // 0x16461A, just before RunObjects
        if (Ram.U8(Vars.MainCharacter) != 0) Sonic.Step();   // RunObjects skips an empty slot (the giant ring's flash clears it)
        if (Ram.U8(Vars.Sidekick) == 2) Tails.Step();
        Objects.RunAfterPlayers(dying);
        if (RestartRequested)
        {
            UpdateSound();   // the reload's VBlanks keep the music going
            return;                  // 0x51C4: tst.w (Level_Inactive_flag) / bne Level, straight after RunObjects
        }
        Camera.DeformBgLayer();
        StreamGiantRingArt();
        Rings.Run();
        Plc.Run();             // RunPLC_RAM
        Oscillation.Step();
        PaletteCycle();
        SyncAnimations.Step(Ram);
        SignpostArtLoad();
        Sprites.BuildSprites();
        ObjectManager.Run();
        // VBlank after the frame: Vint_runcount, then the HUD (0x81E).
        Ram.W32(Vars.VintRuncount, Ram.U32(Vars.VintRuncount) + 1);
        Hud.Update();
        Plc.Process();         // ProcessDPLC
        UpdateSound();
    }

    private readonly string _dataDir;

    /// <summary>
    /// The sound driver's part of a VBlank: the 68k driver's update, or the 68k handing its sound queue to the Z80
    /// (sndDriverInput, 0x11A8) and the Z80's own VBlank interrupt.
    /// </summary>
    public void UpdateSound()
    {
        SoundDriver?.Update();
        if (Z80Sound is { } z80)
        {
            z80.SoundDriverInput(Ram);
            z80.VInt();
        }
    }

    /// <summary>$F7BE: bytes of giant ring art still to copy to VRAM (set to $C40 when the ring appears).</summary>
    private const int BigRingVram = 0x7A40;

    /// <summary>0x43EE0 (start of the animated-art routine 0x43F98): $1C0 bytes of the ring art per frame, last part first.</summary>
    private void StreamGiantRingArt()
    {
        int left = Ram.U16(Vars.BigRingArtLeft);
        if (left == 0) return;
        left = (left - 0x1C0) & 0xFFFF;
        Ram.W16(Vars.BigRingArtLeft, left);
        if (Level.Vram is not { } vram) return;
        var art = LevelLoader.LoadTileArt(Path.Combine(_dataDir, "objects", "giant-ring", "art.png"), 0xC40 / 32);
        art.AsSpan(left, Math.Min(0x1C0, art.Length - left)).CopyTo(vram.AsSpan(BigRingVram + left));
    }

    /// <summary>0x43F50: the flash's art over the ring's.</summary>
    private void LoadGiantRingFlashArt()
    {
        if (Level.Vram is not { } vram) return;
        var art = LevelLoader.LoadTileArt(Path.Combine(_dataDir, "objects", "giant-ring-flash", "art.png"), 0x54);
        art.CopyTo(vram.AsSpan(BigRingVram));
    }

    /// <summary>
    /// 0x5FC6: in acts that end with a signpost, once the camera is within $100 px of the right limit (timer still
    /// running) the left limit is locked there and the signpost art is loaded (PLC $27).
    /// </summary>
    private void SignpostArtLoad()
    {
        if (Ram.U16(Vars.LevelHasSignpost) == 0 || Ram.U16(Vars.DebugPlacementMode) != 0) return;
        int d1 = (Ram.U16(Vars.CameraMaxXPos) - 0x100) & 0xFFFF;
        if ((short)Ram.U16(Vars.CameraXPos) < (short)d1 || Ram.U8(Vars.UpdateHudTimer) == 0 || Ram.U16(Vars.CameraMinXPos) == d1)
        {
            if (Ram.U16(Vars.TwoPlayerMode) != 0) throw new UnportedException(0x5FFE, "2-player signpost art");
            return;
        }
        Ram.W16(Vars.CameraMinXPos, d1);
        if (Ram.U16(Vars.TwoPlayerMode) != 0) return;
        Plc.Load(0x27);
    }

    /// <summary>
    /// The zone's palette cycle (PalCycle_Load, 0x1BB0; e.g. Green Hill's water 0x2040, Emerald Hill's 0x1C04): every
    /// TimerReload+1 frames, copy the next frame's colours.
    /// </summary>
    private void PaletteCycle()
    {
        if (Level.PaletteCycle is not { } cycle) return;
        int timer = (short)Ram.U16(Vars.PalCycleTimer) - 1;
        Ram.W16(Vars.PalCycleTimer, timer);
        if (timer >= 0) return;
        Ram.W16(Vars.PalCycleTimer, cycle.TimerReload);
        int frame = Ram.U16(Vars.PalCycleFrame);
        Ram.W16(Vars.PalCycleFrame, frame + 1);
        var colours = cycle.Frames[frame & 3];
        for (int i = 0; i < colours.Length; i++) Ram.W16(Vars.NormalPalette + cycle.Colours[i] * 2, colours[i]);
    }

}
