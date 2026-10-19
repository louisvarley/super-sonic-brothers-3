# Logic port

## Approach

Game logic is hand-ported from the original 68k code, routine by routine, into `src/Game/Logic` (paths below are
relative to it: `Core/`, `Level/`, `Player/`, `Objects/<zone>/`). Each method cites the ROM address it came from. The
code reads as ordinary C# with names for every field, variable and constant; `docs/porting-guide.md` (4.4) has the
conventions.

* **RAM model (`Ram`):** state lives in a 64 KiB model of 68k work RAM, laid out like the original, with named accessors
  (`SonicObject`, `Vars`). Byte/word aliasing (e.g. `move.w #$900,anim` setting two fields) behaves exactly as on
  hardware, and validation can start from an emulator RAM dump.
* **ROM data:** level layout, chunks, blocks and collision indexes are loaded into that RAM from the extracted files
  (`World`). The angle/height/width tables come from the extracted collision sets. The sine and arctan tables are
  regenerated in code; `Validate math` checks them against the ROM.
* **Unported paths:** code that hasn't been ported yet throws `UnportedException` with its ROM address, instead of
  silently doing something else.
* **Kept quirks:** the original's quirks are kept where they affect behaviour. Examples: unsigned carry checks in
  friction and deceleration; the skid check comparing inertia's high byte; gravity applied after the move; Rev01 angle
  rounding; the ROM's unstable ring sort.

## Ported so far

| Area | ROM | File |
|---|---|---|
| Sonic: init, control, normal/roll/air/jump modes | 0x1DD08-0x1EEEC | `Player/Player.cs` |
| Ground movement, friction, skid, look up/down, balancing | 0x1E0DA-0x1E544 | `Player/Player.cs` |
| Rolling, spindash, jump, jump height, air control/drag | 0x1E546-0x1EC78 | `Player/Player.cs` |
| Air collision, landing (`ResetOnFloor`) | 0x1EC7A-0x1EEEC | `Player/Player.cs` |
| `AnglePos` and terrain probes (Find_Tile, FindFloor/Wall, room checks) | 0x220B4-0x22F98 | `Player/Player.cs`, `Level/Collision.cs` |
| `ObjectMove` / `ObjectMoveAndFall` | 0x1A0C6 / 0x1A0F2 | `Player/Player.cs` |
| `Sonic_Animate` (scripts extracted to `objects/sonic/animations.json`) | 0x1F138 | `Player/SonicAnimation.cs` |
| `CalcSine` / `CalcAngle` | 0x40B6 / 0x434E | `Core/MdMath.cs` |
| `LevelSizeLoad` bounds and start positions (extracted) | 0xD784 | `Level/World.cs` |
| `TouchResponse`, `Touch_Rings`, `CollectRing`, `HurtCharacter`, `KillCharacter`, `AddPoints` | 0x43776, 0x1AE32, 0x14EC8, 0x43A82, 0x43B44, 0x4529A | `Level/TouchResponse.cs` |
| `Obj01_Hurt` / `Sonic_HurtStop` | 0x1EEEE / 0x1EF52 | `Player/Player.cs` |
| `DeformBgLayer`, `ScrollHoriz`, `ScrollVerti`, scroll flags | 0xDCD6, 0xF7A6, 0xF81C, 0xF784/0xF932 | `Level/Camera.cs` |
| `DynamicLevelEvents` (zone `$11`: Sonic 1 Green Hill events) | 0x10804, 0x11872 | `Level/Camera.cs` |
| Zone `$11` background deformation (Sonic 1 Green Hill) | 0xF006, helpers 0xF71C/0xF750 | `Level/Camera.cs` |
| Zone `$11` palette cycle (data extracted to `palette_cycle.json`) | 0x2040, table 0x258C | `Level/World.cs` |
| `InitCameraValues` (zone `$11` part) | 0xDAEC, 0xDC7C | `Level/World.cs` |
| Plane B drawing: `DrawInitialBG`, background part of `LoadTilesAsYouMove` | 0x1050E, 0xFB16-0xFEA6 | `Runtime/BackgroundPlane.cs` |
| `LoadSonicDynPLC` (art/DPLC/mappings extracted to `objects/sonic/`) | 0x1F65E | `Player/Player.cs` |
| `DisplaySprite`, `DisplaySprite3`, `BuildSprites` (1P, static and multi-sprite), `BuildSpr_Normal` | 0x1A23A, 0x1A27A, 0x1A354, 0x1A48E, 0x1A584 | `Core/SpriteBuilder.cs` |
| `ObjectsManager`, `RunObjects`, `MarkObjGone`, `SingleObjLoad(2)`, `DeleteObject` | 0x1B824, 0x19956, 0x1A118/0x1A184, 0x1BD56/0x1BD76, 0x1A22C | `Core/ObjectManager.cs`, `Core/Objects.cs`, `Core/ObjectKit.cs` |
| `AnimateSprite`, `ObjCheckFloorDist`, `RandomNumber` | 0x1A28E, 0x22CF2, 0x408C | `Core/ObjectKit.cs` |
| Solid objects and platforms, `MvSonicOnPtfm`, `RideObject_SetRide` | 0x1D4AE-0x1DCEC | `Level/Solid.cs` |
| `RingsManager`, `BuildRings`, `ChangeRingFrame` | 0x1AD00, 0x1AEF0, 0x5EB0 | `Level/RingsManager.cs`, `Core/SpriteBuilder.cs` |
| `BuildHUD`, `HudUpdate`, `Hud_Base` | 0x44D58, 0x4531E, 0x4563C | `Level/Hud.cs` |
| Objects `$03 $11 $26 $27 $28 $29 $2E $37 $40` | see report 9 | `Objects/Common/PlaneSwitcher.cs`, `Objects/Common/Bridge.cs`, `Objects/Common/Monitor.cs`, `Objects/Common/Animal.cs`, `Objects/GreenHill/Motobug.cs`, `Objects/Common/LostRings.cs` |
| Tails (Obj02 copy of Sonic's code, CPU follower, Obj05 tails) | 0x1F6B8, 0x20C24, 0x21076 | `Player/Player.cs`, `Player/PlayerTails.cs`, `Player/TailsAnimation.cs` |
| All Green Hill objects, boss, egg prison, power-ups, giant ring, game over | see report 10 | one file per object |
| `KillCharacter`, death, `CheckGameOver`, level restart (`$FE02`), `Level_Inactive_flag` check after `RunObjects` | 0x43B44, 0x1EFEA, 0x51C4 | `Player/Player.cs`, `Level/World.cs`, `Runtime/LevelSession.cs` |
| 68k SMPS sound driver (all of it) | 0x1301DC-0x131575 | `Audio/Smps68kDriver.cs` |
| YM2612, PSG, Z80 DAC sample player (hardware models, not ROM code) | | `Audio/Ym2612.cs`, `Audio/Sn76489.cs`, `Audio/SoundMixer.cs` |

| Level start: `Pal_FadeToBlack`, `LoadTitleCard`, Obj34 title card and its VBlank plane fills, the start sequence | 0x2B7E, 0x18D7C, 0x16EC0, 0x18B26, 0x4C2A-0x5192 | `Objects/Common/TitleCard.cs`, `Level/World.cs`, `Runtime/LevelSession.cs` |

**Not yet ported:** the special stage (game mode `$2C`; giant rings skip it), the continue and SEGA/title
screens (game over stops the session), water, Super Sonic, the zones other than Green Hill (`$11`, `$12`) and Marble
Zone (`$13`, `$14`); Emerald Hill (`$00`) is in progress.

## Validation

### Per-iteration capture

`TAP=FE04` makes the MAME harness snapshot all of RAM each time the level loop increments `Level_frame_counter`
(0x51AC), i.e. once per game-logic iteration. Sampling once per video frame doesn't work: lag frames make the game
run 0 or 2 iterations in some video frames.

```sh
BLOCKS=B000:40 TAP=FE04 TAP_FROM=800 TAP_TO=1500 tools/RefEmu/run.sh tools/RefEmu/inputs/zone11-player-physics2.txt 1500 out
```

The Lua garbage collector must not run inside a MAME memory-tap callback, and the tap handle must stay referenced,
or MAME 0.242 crashes. The harness handles both.

### Checks

| Command | What it shows |
|---|---|
| `Validate player-stream <data> <out> 0 N [--continue]` | Co-simulation: each iteration, the emulator's RAM is copied in except Sonic-owned regions, then Sonic's slot and owned globals are compared with the next snapshot |
| `Validate player-world <data> <out> 11 1 N` | Game path only: `World` built from extracted data and driven by the recorded joypad state, compared with the emulator's trajectory |
| `Validate objects <data> <out> 11 1 N` | Object co-simulation: whole emulator RAM in, one `World.Step`, every ported object's slot and Sonic's slot compared with the next snapshot |
| `Validate math` | Regenerated sine/arctan tables vs ROM |

### Results (zone `$11` act 1)

| Recording | Covers | Co-simulation | Game path |
|---|---|---|---|
| `zone11-player-physics` | Run, jump, air control, landing, skid/turn | 295/295 iterations exact, until the enemy hit at frame 1097 | 294/294 |
| `zone11-player-physics2` | Look up (camera bias), duck, 3-rev spindash, roll, turn while rolling, run, jump | 692 of 698 exact; the 6 differences are all object interaction (enemy-kill bounce, standing on a solid object) | 350/350 up to the first object contact |

### Results with touch collision, hurt and camera (report 7)

| Check | Recording 1 (598 iterations) | Recording 2 (698 iterations) |
|---|---|---|
| `player-stream --camera` (Sonic + touch + hurt + camera RAM) | 7 differing iterations | 5 differing iterations |
| `player-world` (game path: Sonic, camera, H-scroll buffer, palette) | 294/294 | 350/350 |

All the remaining co-simulation differences come from object code that runs after Sonic:
* the lost-rings object clearing the ring count when Sonic is hit
* a solid object setting "on object" and carrying Sonic

The game-path checks seed the title-card phase (cloud counters, palette-cycle and animated-tile counters) from the
emulator's first snapshot, because `World` starts at the first level iteration and does not replay the title card.

`Validate screen` compares a full rendered frame with a MAME screenshot. Foreground, mountains, parallax and palette
match. The remaining differences are sprites and parts of the water and cloud bands.

For the water and clouds, background lines scrolling at other rates read the 512 px-wide hardware nametable, which only
holds the part of the layout the game last drew for that band's layer. The renderer currently reads the layout
directly. Matching this exactly means porting the game's background drawing into an emulated nametable.

### Report 8: nametable and sprites

* `Validate planeb`: the emulated plane B nametable matches VRAM `$E000` cell for cell (2048/2048) at the level start
  and at frames 1140 and 1300.
* The VDP draws plane B from that nametable, wrapping at 512x256, and draws sprites from the RAM sprite table: link
  order, 80 total, 20 / 320 px per line, priority against high-priority plane pixels.
* `Validate screen`: frames 1140 and 1300 match MAME at 94.5% and 96.4%. Sonic's sprite is pixel-exact. All remaining
  differences are unported objects (Tails, Motobug, monitor) and the HUD.
