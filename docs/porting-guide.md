# Porting guide: how to port another zone

This is the hand-over document: everything needed to continue the port (new zones, objects, bosses) without the
history of how the existing ones were done. Read it with `README.md`, `docs/logic-port.md` and the reports
(`docs/report-*.md`, newest first: 12 title/level select, 11 Marble Zone, 10 Green Hill).

## 1. The rules

* **This is a port, not a remake.** Game logic is the hack's 68k code ported routine by routine into C#: same order
  of operations, same RAM layout, same byte/word arithmetic and its wrap-arounds. Every class and method says which
  ROM routine it is (`/// <summary>0x155EC2: ...`). No 68k interpreter, no "close enough" rewrites.
* **Readable C#, original layout underneath.** Objects keep their fields in the 68k RAM slot (so MAME's RAM can be
  compared byte for byte), but code never uses raw offsets or addresses: standard fields by name (`o.Subtype`,
  `o.Routine`, `o.WidthPixels`), an object's own variables through a small `State` view, RAM variables through
  `Vars` (Sonic 2 disassembly names), routines, animations and sounds as named constants. See 4.4.
* **The ROM is read only by the extractor.** `src/S2Port.Extraction` turns `rom.bin` into `data.bin` (a zip); the game
  reads only `data.bin` (through `DataFile`). No ROM bytes in source code: tables are extracted (see 4.3). Small
  constant tables written as code are tolerated when documented with their ROM address.
* **Everything is validated against MAME 0.242** (the reference emulator), frame by frame, before it is called done.
* **Local only**: the user keeps the project off GitHub; never push anywhere. `rom.bin`, `data.bin`, `smb3.nes` and
  `trainer-runs/` are gitignored and must stay so.
* Keep the existing style (4.4): XML doc comments with ROM addresses, names instead of numbers, one file per object,
  and a report in `docs/` for each zone.

## 2. Building, running, testing

```
export PATH=$HOME/.dotnet:$PATH
dotnet build S2Port.slnx
dotnet test S2Port.slnx                                    # 66+ tests; data tests need data.bin (else they pass trivially)
dotnet run --project src/Game                              # title screen
dotnet run --project src/Game -- --zone 13 --act 2         # straight into a zone/act (hex zone)
dotnet run --project src/Game -- --zone 0 --start 2000,600 # debug start position
# headless: --screenshot out.png --frames N [--inputs script.txt] [--trace] [--mute] [--wav out.wav]
```

`data.bin` is rebuilt automatically when `Extractor.Version` changes: **bump it whenever you change extraction**.

## 3. Map of the code

| Where | What |
|---|---|
| `src/S2Port.Extraction/RomMap.cs` | ROM addresses the extractor reads (object sprite mappings, data tables, ...). |
| `src/S2Port.Extraction/Extractor.cs`, `Frontend.cs`, `Smb3/` | Extraction (levels, art, palettes, PLCs, sound data, front end, the SMB3 Easter egg). |
| `src/Game/Logic/Core/` | The object model: `SonicObject` (a slot's fields by name), `Vars` (every RAM variable by name), `Flags` (`Render`, `ObjStatus` bits), `Ids` (`PlayerAnim`, `Music`, `Sfx`), `Ram`, `GameObject` (`ObjectContext.cs`), `Objects` (RunObjects and **the registry: register new objects here**), `ObjectKit` (shared routines), `SpriteBuilder`. |
| `src/Game/Logic/Level/` | `World` (one level; `Step` = one frame of the level loop), `Camera` (level events per zone, background deformation, `IsZonePorted`), `Collision`, `Solid`, `TouchResponse`, `RingsManager`, `Hud`, `Oscillation`, `LavaLine`, `PlcQueue` (art loaded during play). |
| `src/Game/Logic/Player/` | Sonic and Tails (`Player`, `PlayerTails`, the CPU), their animation code and Tails' tails. |
| `src/Game/Logic/Objects/<Zone>/` | One file per object: `Common/` (rings, monitors, springs, signpost...), `GreenHill/`, `MarbleZone/`, `Smb3/`. New zone: a new folder. |
| `src/Game/Runtime/` | `LevelSession` (act flow, title cards, restarts), `Vdp` (rendering), `AnimatedTiles`, `BackgroundPlane`. |
| `src/Game/Audio/` | The 68k SMPS driver (hack zones' music) and the YM2612/PSG models. |
| `src/Game/Frontend/` | Title screen, level select, `GameFlow`. |
| `tools/Disasm/m68k.py` | Reading the ROM's code (section 5). |
| `tools/RefEmu/` | MAME harness (section 6). |
| `tools/Validate/` | Comparisons against MAME (section 7). |

## 4. Porting one object

### 4.1 Find its code

The object table is at `0x199CA`: **8 bytes per id, two pointers**: the routine for Sonic 2's own zones, then the
routine for the hack's zones (RunObjects picks the second when `$FFE6` is set, i.e. in zones `$11` and up).

```
python3 tools/Disasm/m68k.py obj 47          # object $47: Sonic 2 zones 0x...., hack zones 0x......
```

**Trap:** always use the column for the zone you are porting. (Green Hill's `$4B`/`$7D` were first ported from the
wrong column.) An id whose two pointers are equal is the same code in both kinds of zone.

### 4.2 Read it

```
python3 tools/Disasm/m68k.py dis 155EC2 156342 > obj73.asm
python3 tools/Disasm/m68k.py table 155ED0 5    # a routine table: "move.w tbl(pc,d0.w),d1 / jmp tbl(pc,d1.w)"
python3 tools/Disasm/m68k.py bytes 155EDA 12   # data inside the code (it disassembles as nonsense)
```

Capstone shows jump tables and data as junk instructions: decode them with `table` / `bytes`. Read `bra`/`jmp` targets
carefully: a `jmp DeleteObject` followed by the caller's code means the caller keeps running on the cleared slot
(see `Missile` in `Objects/GreenHill/BuzzBomber.cs`); reproduce such quirks.

Useful references: Sonic 2's disassembly (github.com/sonicretro/s2disasm) and Sonic 1's (sonicretro/s1disasm) explain
what most routines do. **The hack differs** (positions, timings, RAM slots, extra code), so port from the hack's ROM
and use the disassemblies only to understand.

Common routines already ported (call these, don't re-port). In `GameObject`: `Display` (DisplaySprite 0x1A23A),
`Delete` (DeleteObject 0x1A22C), `Spawn` (SingleObjLoad 0x1BD56), `MarkObjGone`, `DisplayOrMarkGone`, `OutOfRange` /
`OutOfRangeS1` (the despawn tests), `Sonic`, `Tails`, `CurrentZone`. In `ObjectKit`: `Move` (ObjectMove 0x1A0F2),
`MoveAndFall` (0x1A0C6), `CheckFloorDist` (0x22CF2), `CheckFloor`, `CheckCeiling`, `CheckLeftWall`/`CheckRightWall`,
`Animate` (AnimateSprite 0x1A28E), `FindFreeSlotAfter` (0x1BD76), `RandomNumber`. Also `Ctx.Solid.SolidObject`
(0x1D4AE) and friends, `Ctx.Touch.AddPoints` / `Hurt` / `Kill`, `BossState.Move` (BossMove) and
`GhzBoss.Explosions`.

### 4.3 Its data

* **Sprite mappings / animations**: add `("name", mappingsAddr, animationsAddr, 0)` to `RomMap.ObjectSprites`; the
  extractor writes `objects/<name>/sprites.json`. Objects find their mappings by pointer (`$04` = the ROM address),
  so set `$04` to the mappings address exactly as the original does.
* **Other tables** (speeds, positions, height maps): add `("<name>/<file>.json", addr, length)` to
  `RomMap.ObjectDataTables`, read with `ctx.Art.DataTable("<name>/<file>.json")` (gives the ROM base address and bytes,
  so code can index by ROM address).
* **Art** comes through the PLC lists (`plc/plc_lists.json`) the level or the object requests (`LoadPlc`).
  * During play, requests go through `PlcQueue`. The art reaches VRAM at once, but the queue at `$F680` drains
    3 tiles a frame, as the original's does, so objects that wait for it (the results, game over) wait as long.
* Bump `Extractor.Version`.

### 4.4 Write it

Copy the shape of a similar ported object (`Objects/MarbleZone/` has twenty-one; `Spikes.cs`, `Platform.cs` and
`Crabmeat.cs` are good short examples). One file and class per object, deriving from `GameObject(ctx)`, with
`Run(SonicObject o)` switching on `o.Routine`. The conventions:

* **Routines** are named constants: `private const int Init = 0, Main = 2, Gone = 4;` and `case Main:`. Set them by
  name too (`o.Routine = Main;`), not `+= 2`, unless the original genuinely adds to an unknown value.
* **Standard fields** by name: `o.X`, `o.Y`, `o.XVel`, `o.Subtype`, `o.Routine`, `o.RoutineSecondary`,
  `o.MappingFrame`, `o.Anim`, `o.Priority`, `o.WidthPixels`, `o.CollisionFlags`, `o.Mappings`, `o.ArtTile`, ...
  Byte fields read 0-255 and take any int, like a `move.b`.
* **The object's own variables** ($2C-$3F and spare bytes) get names through a small view in the class:

  ```csharp
  /// <summary>The block's own variables.</summary>
  private readonly struct State(SonicObject o)
  {
      /// <summary>$30: where it was placed.</summary>
      public int BaseX { get => o.Word(0x30); set => o.SetWord(0x30, value); }
      /// <summary>$38: frames to wait.</summary>
      public int Timer { get => o.Byte(0x38); set => o.SetByte(0x38, value); }
  }
  ...
  var s = new State(o);
  s.Timer--;
  ```

  Document each one with its offset. This is the only place raw offsets appear.
* **Bits** by name: `o.RenderFlags |= Render.LevelCoords`, `o.OnScreen`, `(o.Status & ObjStatus.StandingP1)`,
  `ObjStatus.Destroyed`; for players `p.InAir`, `p.Rolling`, `p.OnObject`, `p.Pushing`, `p.Invincible`, ...
* **RAM variables** through `Vars` (`Ram.U16(Vars.CameraXPos)`); add a missing one there with its Sonic 2 name
  (`s2.constants.asm`), or a descriptive name marked "hack" if the hack repurposed it.
* **Ids**: other objects' ids as constants (`Animal.Id`, `Missile.Id`, `private const int ExplosionId = 0x27`),
  player animations as `PlayerAnim.*`, sounds as `Sfx.*` / `Music.*` (or a named local constant when the hack's
  Sonic 1 sound has another meaning).
* Multi-sprite objects (chains, bridges) use `MultiSprite`; bosses share `BossState`.

Register it in `Objects.cs` (`_ported[0x47] = new Bumper(ctx).Run;` for the hack's zones, `RegisterS2(0x47, ...)`
for Sonic 2's zones).

## 5. Porting a zone

Per zone (see reports 10 and 11 for the full list of what Green Hill and Marble needed):

1. **Survey** the acts' object layouts (`zones/zone_XX/act_N/objects.json` inside data.bin; a zip) and list the ids not
   yet registered in `Objects.cs`.
2. **Level systems**: `Camera.IsZonePorted`; `Camera.ZoneEvents` (DynamicLevelEvents 0x10804, per zone/act: camera
   limits, boss trigger); `Camera.DeformBgLayer` (SwScrl_<zone>); `BackgroundPlane` layers; animated tiles (generic
   scripts are already extracted; custom code like Marble's magma needs porting); palette cycling; `LavaLine`'s
   per-act table (0x16467A) for hack zones; `World.InitCameraValues`.
3. **Objects**, one at a time, each validated (section 7).
4. **The boss** (usually spawned by the act's level events) and the act end (signpost / egg prison, results, next act).
5. **Tests** in `tests/S2Port.Tests` (e.g. the boss-to-results test in `GreenHillEndTests.cs`) and a report in `docs/`.

The level order (`Results.NextLevel`, the ROM's table) and the level select pick a new zone up as soon as
`Camera.IsZonePorted` includes it.

## 6. Recording the original (MAME)

`tools/RefEmu/run.sh <inputs.txt> <frames> <out-dir> [dump-frames]` runs the ROM in MAME headless (Linux: MAME 0.242
unpacked under `~/.local/opt/mame-deb`; see `docs/reference-emulator.md`). Environment options:

| Option | Use |
|---|---|
| `POKES=602:FE10:13,602:FE11:01` | Pick the zone/act after the title (frame 602). E.g. zone `$13` act 2. |
| `POKES=900:B008:19,900:B009:00,...` | Teleport Sonic (X at `$B008`, Y at `$B00C`) once the level runs; the camera catches up and objects load as it scrolls. |
| `TAP=FE04 TAP_FROM=790 TAP_TO=5000` | Full RAM at every game-logic iteration (`ramstream.bin`): what co-simulation needs. |
| `AUTOPLAY=chase:73:1000` | From frame 1000, steer Sonic to object `$73` and jump at it (bosses); jumps walls; then the egg prison. |
| `SNAPS=1200,1500` / `DUMPS=1500` / `WAV=out.wav` | Screenshots / RAM+VRAM dumps / audio. |
| `TAP_Z80=1` (with `TAP`) | Also capture the Z80 sound driver: its RAM at each Z80 VBlank, the 68k's mailbox writes and the YM/PSG writes (for `Validate z80`). MAME 0.242 then segfaults on exit, after the files are written. |
| `POKES=1260:FE30:01,...` (star post) | Sonic 2's zones, from the level select: restart at a poked star post, so objects load properly (teleports skip the object manager there). See report 13. |

A typical boss recording (Marble Zone 3, `$B02B=02` makes Sonic invincible):

```
POKES=602:FE10:14,602:FE11:00,900:B008:19,900:B009:00,900:B00C:02,900:B00D:A0,900:B02B:02 \
AUTOPLAY=chase:73:1000 TAP=FE04 TAP_FROM=790 TAP_TO=5000 \
tools/RefEmu/run.sh tools/RefEmu/inputs/zone11-run-jump.txt 5000 /tmp/mzboss
```

## 7. Validating

**Object co-simulation** is the main check:

```
dotnet run --project tools/Validate -- objects data.bin /tmp/mzboss 14 1 2896
```

For every logic iteration it loads MAME's RAM, runs one `World.Step`, and compares every object slot (and the sound
driver's RAM in hack zones) with MAME's next iteration. Output: per object id, slot-iterations matching / differing,
with the first differences printed. **Expected** differences: the frames you poked or teleported, level reloads, and
lag frames in the sound driver. Anything else is a porting bug: the printed field offset tells you which variable.

Also: `screen` (render vs a MAME screenshot), `player-stream` (Sonic's physics), `level-ram` (extracted level data
vs a RAM dump). See the comments at the top of `tools/Validate/Program.cs`.

**Refactoring or tidying ported code**: keep a set of recordings and save the co-simulation output before the change;
after it, the output must be identical (and the tests pass). That is how the code was converted to the named style:
sixteen Green Hill / Marble Zone / Emerald Hill recordings stayed byte-identical through every step.

## 8. Known traps

* **Two object-table columns** (4.1).
* **`$FFE6`** (hack zones flag) changes RunObjects' table, the sound driver (`$FFE5`: 68k driver in hack zones, the
  Z80 driver in Sonic 2's zones), and some shared routines' behaviour.
* **Sonic 2's own zones** (`$00`-`$10`, e.g. Emerald Hill `$00`) use the first table column and **Sonic 2's Z80
  sound driver** (`SmpsZ80Driver`; Sonic 2's sound ids, e.g. `$A7` is the sparkle there but the push sound in the
  hack's zones).
* Objects that delete themselves and fall through into the caller's code: reproduce it (they write into the cleared
  slot).
* Byte/word wrap-around and signed vs unsigned compares (`bcs`/`bhi` are unsigned, `blt`/`bgt` signed).
* RunObjects also runs on the frame a level ends (`$FE02` set): the level loop stops right after it (0x51C4).
* The hack's "lava line" (0x164470): a palette split and badnik palette changes in hack zones.

## 9. Where things stand

* Ported: Green Hill (`$11`, `$12`), Marble Zone (`$13`, `$14`), Emerald Hill (`$00`, both acts and the boss; report 13), the title screen and level select, both sound drivers, the SMB3 1-1
  Easter egg (zone `$40`), the 68k sound driver.
* Next by the level order: Spring Yard (`$15`, `$16`): new objects `$56` (blocks, many), `$47` bumper, `$4F`, `$12`,
  `$43` Roller, `$50` Yadrin, `$57` spiked ball, the boss; background, events, palette cycling.
* Not ported: the special stages, continue/SEGA screens, demos, Sonic 2's zones other than EHZ.
