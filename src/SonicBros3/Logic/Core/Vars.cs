namespace SonicBros3.Logic;

/// <summary>
/// The game's variables in work RAM, by address. Names follow the Sonic 2 disassembly (s2.constants.asm) in
/// PascalCase; variables the hack added or repurposed are marked "hack". Everything lives in <see cref="Ram"/> at
/// these addresses, as in the original, so the port's RAM can be compared with MAME's byte for byte.
/// "P2" variables are Tails' copies (two-player mode, also used for Tails in one-player games).
/// </summary>
public static class Vars
{
    // ---- Level data ----
    public const int ChunkTable = 0x0000;            // 128x128 chunk mappings
    public const int LevelLayout = 0x8000;           // foreground/background rows interleaved
    public const int BlockTable = 0x9000;            // 16x16 block mappings
    /// <summary>Scratch for layer deformation; the title screen and Emerald Hill keep their wave offset at +0.</summary>
    public const int TempArrayLayerDef = 0xA800;
    public const int ObjectDisplayLists = 0xAC00;    // 8 priority lists of $40 words (DisplaySprite)

    // ---- Object slots ----
    public const int MainCharacter = 0xB000;         // Sonic
    public const int Sidekick = 0xB040;              // Tails
    /// <summary>Slot 2: title card zone name / "GAME" of GAME OVER / "TIME" of TIME OVER.</summary>
    public const int TitleCardZoneName = 0xB080;
    /// <summary>Slot 3: title card "ZONE" / "OVER" of GAME OVER and TIME OVER.</summary>
    public const int TitleCardZone = 0xB0C0;
    public const int TitleCardActNumber = 0xB100;
    public const int TitleCardBackground = 0xB140;
    public const int TitleCardBottom = 0xB180;
    public const int TitleCardLeft = 0xB1C0;
    public const int FirstDynamicSlot = 0xB400;
    /// <summary>The end of the dynamic slots; then the effect objects' fixed slots up to <see cref="ObjectRamEnd"/>.</summary>
    public const int DynamicObjectRamEnd = 0xD000;
    public const int ObjectRamEnd = 0xD400;
    // Effect objects, in fixed slots after the dynamic ones.
    public const int TailsTails = 0xD000;
    public const int SonicDust = 0xD100, TailsDust = 0xD140;
    public const int SonicShield = 0xD180, TailsShield = 0xD1C0;
    public const int SonicInvincibilityStars = 0xD200, TailsInvincibilityStars = 0xD300;
    public const int PrimaryCollision = 0xD600;      // collision index per 16x16 block, path A
    public const int SecondaryCollision = 0xD900;    // path B

    // ---- Scroll, rings, camera ----
    public const int HorizScrollBuf = 0xE000;        // per line: foreground word, background word
    public const int SonicStatRecordBuf = 0xE400;    // Sonic's input/status history (Tails follows it)
    public const int SonicPosRecordBuf = 0xE500;     // Sonic's x/y history
    public const int TailsPosRecordBuf = 0xE600;
    /// <summary>hack: cloud scroll counters (Sonic 2's CNZ saucer data).</summary>
    public const int CloudCounters = 0xE700;
    public const int RingPositions = 0xE800, RingPositionsFirst = 0xE806;
    public const int CameraXPos = 0xEE00, CameraYPos = 0xEE04;
    public const int CameraBgXPos = 0xEE08, CameraBgYPos = 0xEE0C;
    public const int CameraBg2XPos = 0xEE10, CameraBg2YPos = 0xEE14;
    public const int CameraBg3XPos = 0xEE18, CameraBg3YPos = 0xEE1C;
    public const int CameraXPosP2 = 0xEE20, CameraYPosP2 = 0xEE24;
    public const int CameraBgXPosP2 = 0xEE28, CameraBgYPosP2 = 0xEE2C;
    public const int CameraBg2XPosP2 = 0xEE30, CameraBg2YPosP2 = 0xEE34;
    public const int CameraBg3XPosP2 = 0xEE38, CameraBg3YPosP2 = 0xEE3C;
    // Toggled each time a layer crosses a 16-px block boundary (the drawing code then redraws a row or column).
    public const int HorizBlockCrossedFlag = 0xEE40, VertiBlockCrossedFlag = 0xEE41;
    public const int HorizBlockCrossedFlagBg = 0xEE42, VertiBlockCrossedFlagBg = 0xEE43;
    public const int HorizBlockCrossedFlagBg2 = 0xEE44, HorizBlockCrossedFlagBg3 = 0xEE46;
    public const int ScrollFlags = 0xEE50, ScrollFlagsBg = 0xEE52, ScrollFlagsBg2 = 0xEE54, ScrollFlagsBg3 = 0xEE56;
    public const int CameraXPosDiff = 0xEEB0, CameraYPosDiff = 0xEEB2;
    public const int CameraXPosDiffP2 = 0xEEB8, CameraYPosDiffP2 = 0xEEBA;
    public const int ScrollLock = 0xEEBE, ScrollLockP2 = 0xEEBF;
    public const int CameraMaxYPosTarget = 0xEEC6;
    public const int CameraMinXPos = 0xEEC8, CameraMaxXPos = 0xEECA, CameraMinYPos = 0xEECC, CameraMaxYPos = 0xEECE;
    public const int HorizScrollDelayVal = 0xEED0, HorizScrollDelayValP2 = 0xEED4;
    public const int SonicPosRecordIndex = 0xEED2, TailsPosRecordIndex = 0xEED6;
    public const int CameraYPosBias = 0xEED8, CameraYPosBiasP2 = 0xEEDA;
    public const int DeformLock = 0xEEDC;
    public const int CameraMaxYPosChanging = 0xEEDE;
    /// <summary>The level event script's state (DynamicResize: 0, 2, 4, ...).</summary>
    public const int DynamicResizeRoutine = 0xEEDF;
    public const int CameraXPosCopy = 0xEEF0, CameraYPosCopy = 0xEEF4;
    public const int TailsMinXPos = 0xEEF8, TailsMaxXPos = 0xEEFA, TailsMinYPos = 0xEEFC, TailsMaxYPos = 0xEEFE;
    public const int RingConsumptionTable = 0xEF80;  // count of sparkling rings, then their addresses
    public const int RingConsumptionList = 0xEF82;

    // ---- Sound (hack: the Sonic 1 style 68k SMPS driver's RAM is at $F000) ----
    /// <summary>hack: 68k SMPS driver queue (music).</summary>
    public const int SmpsQueue1 = 0xF00A;
    /// <summary>hack: 68k SMPS driver queue (sound effects).</summary>
    public const int SmpsQueue2 = 0xF00B;

    // ---- Game state ----
    public const int GameMode = 0xF600;
    public const int Ctrl1HeldLogical = 0xF602, Ctrl1PressLogical = 0xF603;
    public const int Ctrl1Held = 0xF604, Ctrl1Press = 0xF605;
    public const int Ctrl2Held = 0xF606, Ctrl2Press = 0xF607;
    public const int DemoTimeLeft = 0xF614;
    public const int VscrollFactorFg = 0xF616, VscrollFactorBg = 0xF618, VscrollFactorP2Fg = 0xF61E;
    public const int TeleportTimer = 0xF622, TeleportFlag = 0xF623;
    /// <summary>hack: the lava line's scanline (see <see cref="LavaLine"/>).</summary>
    public const int LavaLineScanline = 0xF625;
    public const int PalFadeStart = 0xF626, PalFadeLength = 0xF627;
    public const int SpriteCount = 0xF62C;
    public const int PalCycleFrame = 0xF632, PalCycleTimer = 0xF634;
    public const int RngSeed = 0xF636;
    public const int GamePaused = 0xF63A;
    public const int WaterLevel1 = 0xF646, WaterLevel2 = 0xF648;
    public const int WaterRoutine = 0xF64D, WaterFullscreenFlag = 0xF64E;
    public const int SuperSonicPalette = 0xF65F;
    /// <summary>Tails' logical joypad (held, pressed): the CPU or player 2 drives it.</summary>
    public const int Ctrl2HeldLogical = 0xF66A, Ctrl2PressLogical = 0xF66B;
    public const int SonicLookDelayCounter = 0xF66C, TailsLookDelayCounter = 0xF66E;
    public const int PlcBuffer = 0xF680;
    // Tails' CPU control (one-player games).
    public const int TailsControlCounter = 0xF702, TailsRespawnCounter = 0xF704, TailsCpuRoutine = 0xF708;
    public const int TailsCpuTargetX = 0xF70A, TailsCpuTargetY = 0xF70C, TailsInteractId = 0xF70E, TailsCpuJumping = 0xF70F;
    public const int RingsManagerRoutine = 0xF710;
    public const int LevelStartedFlag = 0xF711;
    public const int RingStartAddr = 0xF712, RingEndAddr = 0xF714;
    public const int RingStartAddrP2 = 0xF716, RingEndAddrP2 = 0xF718;
    public const int WaterFlag = 0xF730;
    /// <summary>Start of the bosses' shared variables.</summary>
    public const int BossSpawnDelay = 0xF73A;
    public const int SonicTopSpeed = 0xF760, SonicAcceleration = 0xF762, SonicDeceleration = 0xF764;
    public const int SonicLastLoadedDplc = 0xF766;
    public const int PrimaryAngle = 0xF768, SecondaryAngle = 0xF76A;
    // The object manager (loads objects from the level's layout as the camera moves).
    public const int ObjPlacementRoutine = 0xF76C, CameraXPosLast = 0xF76E, ObjLoadAddrRight = 0xF770, ObjLoadAddrLeft = 0xF774;
    public const int ObjLoadAddrRightP2 = 0xF778, ObjLoadAddrLeftP2 = 0xF77C;
    public const int ObjectRamBlockIndices = 0xF780;
    public const int CameraXPosLastP2 = 0xF78C;
    /// <summary>Long: the collision index in use, $FFFFD600 or $FFFFD900.</summary>
    public const int CollisionAddr = 0xF796;
    /// <summary>hack: Marble Zone's chained stomper block's y (Sonic 1's v_obj31ypos).</summary>
    public const int ChainStomperY = 0xF7A4;
    public const int BossDefeatedFlag = 0xF7A7;
    public const int CurrentBossId = 0xF7AA;
    /// <summary>hack: tiles of the giant ring's art still to stream to VRAM.</summary>
    public const int BigRingArtLeft = 0xF7BE;
    public const int WindTunnelFlag = 0xF7C7;
    public const int ControlLocked = 0xF7CC;
    /// <summary>hack: set by the giant ring's flash; the special stage follows the act.</summary>
    public const int SpecialStageRequested = 0xF7CE;
    public const int ControlLockedP2 = 0xF7CF;
    public const int ChainBonusCounter = 0xF7D0;
    public const int BonusCountdown1 = 0xF7D2, BonusCountdown2 = 0xF7D4;   // time bonus, ring bonus
    public const int UpdateBonusScore = 0xF7D6;
    /// <summary>Camera x rounded down to $80: the object manager's window.</summary>
    public const int CameraXPosCoarse = 0xF7DA;
    public const int TailsLastLoadedDplc = 0xF7DE, TailsTailsLastLoadedDplc = 0xF7DF;
    /// <summary>16 switch flags set by buttons (Sonic 1's f_switch), e.g. Marble Zone.</summary>
    public const int ButtonVineTrigger = 0xF7E0;
    /// <summary>Animated-tile counters (2 bytes per animation: timer, frame).</summary>
    public const int AnimCounters = 0xF7F0;
    public const int SpriteTable = 0xF800;
    public const int NormalPalette = 0xFB00, NormalPaletteLine2 = 0xFB20, NormalPaletteLine3 = 0xFB40, NormalPaletteLine4 = 0xFB60;
    public const int TargetPalette = 0xFB80;
    public const int ObjectRespawnTable = 0xFC00;

    // ---- Level session ----
    public const int LevelInactiveFlag = 0xFE02;
    public const int LevelFrameCounter = 0xFE04;
    public const int DebugPlacementMode = 0xFE08;
    /// <summary>Long: frames since power on. Code often tests its low byte for "every N frames".</summary>
    public const int VintRuncount = 0xFE0C;
    public const int VintRuncountLow = 0xFE0F;
    /// <summary>Word: zone (high byte), act (low byte, 0-based).</summary>
    public const int CurrentZoneAndAct = 0xFE10, CurrentAct = 0xFE11;
    public const int LifeCount = 0xFE12;
    public const int CurrentSpecialAct = 0xFE17;
    public const int ContinueCount = 0xFE18;
    public const int SuperSonicFlag = 0xFE19;
    public const int TimeOverFlag = 0xFE1A;
    public const int ExtraLifeFlags = 0xFE1B;
    public const int UpdateHudLives = 0xFE1C, UpdateHudRings = 0xFE1D, UpdateHudTimer = 0xFE1E, UpdateHudScore = 0xFE1F;
    public const int RingCount = 0xFE20;
    /// <summary>Long timer: unused byte, minutes, seconds, frames.</summary>
    public const int Timer = 0xFE22, TimerMinute = 0xFE23, TimerSecond = 0xFE24, TimerFrame = 0xFE25;
    public const int Score = 0xFE26;
    public const int LastStarPoleHit = 0xFE30;
    // Saved at a star post, restored when the player dies.
    public const int SavedLastStarPoleHit = 0xFE31;
    public const int SavedXPos = 0xFE32, SavedYPos = 0xFE34;
    public const int SavedRingCount = 0xFE36, SavedTimer = 0xFE38;
    public const int SavedArtTile = 0xFE3C, SavedSolidBits = 0xFE3E;
    public const int SavedCameraXPos = 0xFE40;
    public const int SavedWaterLevel = 0xFE50, SavedWaterRoutine = 0xFE52, SavedWaterMove = 0xFE53;
    public const int SavedExtraLifeFlags = 0xFE54;
    public const int SavedCameraMaxYPos = 0xFE56;
    public const int SavedDynamicResizeRoutine = 0xFE58;
    /// <summary>Oscillating values for platforms (see <see cref="Oscillation"/>).</summary>
    public const int OscillatingData = 0xFE5E;
    public const int LogspikeAnimCounter = 0xFEA0, LogspikeAnimFrame = 0xFEA1;
    public const int RingsAnimCounter = 0xFEA2, RingsAnimFrame = 0xFEA3;
    public const int UnknownAnimCounter = 0xFEA4, UnknownAnimFrame = 0xFEA5;
    public const int RingSpillAnimCounter = 0xFEA6, RingSpillAnimFrame = 0xFEA7, RingSpillAnimAccum = 0xFEA8;
    public const int TailsTopSpeed = 0xFEC0, TailsAcceleration = 0xFEC2, TailsDeceleration = 0xFEC4;
    public const int LifeCountP2 = 0xFEC6, ExtraLifeFlagsP2 = 0xFEC7;
    public const int UpdateHudLivesP2 = 0xFEC8, UpdateHudRingsP2 = 0xFEC9, UpdateHudTimerP2 = 0xFECA;
    public const int TimeOverFlagP2 = 0xFECC;
    public const int RingCountP2 = 0xFED0;
    public const int TimerP2 = 0xFED2;
    public const int LastStarPoleHitP2 = 0xFEE0;
    public const int SavedXPosP2 = 0xFEE2, SavedYPosP2 = 0xFEE4;
    public const int SavedArtTileP2 = 0xFEEC, SavedSolidBitsP2 = 0xFEEE;
    public const int RingsCollected = 0xFEF0, RingsCollectedP2 = 0xFEF2;
    public const int MonitorsBroken = 0xFEF4, MonitorsBrokenP2 = 0xFEF6;
    public const int LoserTimeLeft = 0xFEF8;
    /// <summary>hack: a timed mode (no special stages, no time-over reset of the timer).</summary>
    public const int TimedMode = 0xFF12;
    /// <summary>Rings left in the act (all collected: the perfect bonus).</summary>
    public const int PerfectRingsLeft = 0xFF40;
    public const int PerfectRingsFlag = 0xFF42;
    /// <summary>Casino Night's slot machines (cleared at level start).</summary>
    public const int SlotMachineInUse = 0xFF4C, SlotMachineRoutine = 0xFF4E;
    public const int PlayerMode = 0xFF70;
    // Menus.
    public const int LevelSelectHoldTimer = 0xFF80, LevelSelectZone = 0xFF82, SoundTestSound = 0xFF84;
    public const int TitleScreenOption = 0xFF86;
    public const int TotalBonusCountdown = 0xFF8E;
    public const int LevelMusic = 0xFF90;
    /// <summary>The perfect bonus countdown (all rings collected).</summary>
    public const int BonusCountdown3 = 0xFF92;
    /// <summary>hack: Sonic 1 emeralds collected (6 = all).</summary>
    public const int Sonic1EmeraldCount = 0xFFAF;
    /// <summary>hack: emeralds collected over both games ($0D = all 13).</summary>
    public const int EmeraldCount = 0xFFB1;
    public const int NextExtraLifeScore = 0xFFC0;
    public const int LevelHasSignpost = 0xFFC8;
    public const int TwoPlayerMode = 0xFFD8;
    /// <summary>hack: below the lava line (see <see cref="LavaLine"/>).</summary>
    public const int LavaLineBelow = 0xFFDA;
    // The Z80 sound driver's queues (Sonic 2 zones).
    public const int MusicToPlay = 0xFFE0, SfxToPlay = 0xFFE1, SfxToPlay2 = 0xFFE2, MusicToPlay2 = 0xFFE4;
    /// <summary>hack: which sound driver runs: 0 the Z80 (Sonic 2 zones), 1 the 68k SMPS (the hack's zones).</summary>
    public const int SoundDriverSelect = 0xFFE5;
    /// <summary>hack: set in the hack's own zones ($11 up): their object column, collision set and sound driver.</summary>
    public const int HackZoneFlag = 0xFFE6;
    // hack: the lava line (Marble Zone; see LavaLine).
    public const int LavaLineEnabled = 0xFFE7, LavaLineHeight = 0xFFE8, LavaLineMode = 0xFFEA;
    public const int LavaLinePaletteMode = 0xFFEB, LavaLineExtra = 0xFFEC, LavaLinePaletteState = 0xFFEE;
    /// <summary>Bit 7: overseas (English) console. The title screen's TM depends on it.</summary>
    public const int GraphicsFlags = 0xFFF8;
    public const int DebugModeFlag = 0xFFFA;
}
