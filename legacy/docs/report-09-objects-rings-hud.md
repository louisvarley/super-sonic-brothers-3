# Report 9: Objects, the loop fix, rings and the HUD (2026-10-16)

## Done

### Object system
* **`ObjectsManager`:** spawns objects from the act's placement list as the camera moves. The RAM pointers into
  the list (`$F770/$F774`) use the original's numbering. `objects.json` records the list's ROM address as
  metadata, so the game's RAM matches the emulator's byte for byte.
* **`RunObjects`:** covers all `$90` slots and dispatches through the hack-zone table (the second pointer in each
  entry of `0x199CA`). Unported objects only run `MarkObjGone`, so they despawn like the original.
* **Shared routines (`ObjectKit.cs`, `Solid.cs`):**
  * `ObjectMove`, `ObjectMoveAndFall`, `AnimateSprite` (all commands `$F9-$FF`)
  * `MarkObjGone` (and its display variant), `SingleObjLoad`/`SingleObjLoad2`
  * `ObjCheckFloorDist`, the ring floor probe, `RandomNumber`
  * the solid-object family: `SolidObject`, `SolidObject_Always`, `SolidObject_cont`, the platform routines,
    `MvSonicOnPtfm`, `RideObject_SetRide`
* **Sprites:**
  * static mappings (a pointer straight at one piece)
  * multi-sprite objects (`0x1A48E`; the bridge's logs)
  * `DisplaySprite3`
  * object mappings and animation scripts are extracted per object (`objects/<name>/sprites.json`), with the ROM
    pointer they answer to
* **Sound requests:** written to the original's RAM queue (`$FFE0-$FFE4`, or `$F00A/$F00B` for the 68k driver
  used in hack zones), so the audio player can consume them from RAM.

### Ported objects (hack zone table)

| Id | Object | ROM |
|---|---|---|
| `$03` | Plane switcher (fixed the reported loop bug: Sonic now goes all the way round) | 0x23CD0 |
| `$11` | Log bridge, with its multi-sprite log children; bend tables extracted to `objects/bridge/bridge.json` | 0x120E0 |
| `$26` | Monitor (solid, broken by rolling or from below, falls when knocked) | 0x155B0 |
| `$2E` | Monitor contents: 1-up, rings (+10, lives at 100/200), shoes, shield, invincibility | 0x15798 |
| `$27` | Explosion (spawns the animal when it comes from an enemy) | 0x25096 |
| `$28` | Animal (the species comes from a per-zone table; hop or fly away). Tables extracted to `objects/animal/animals.json` | 0x14778 |
| `$29` | Points popup | 0x14CC8 |
| `$37` | Lost rings (the fan pattern, bounce every 8th frame staggered by slot, re-collectable, timer `$FEA6`) | 0x14F78 |
| `$40` | Motobug (hack zones: Sonic 1 Green Hill code), with its exhaust puffs | 0x1518E6 |

### Rings and HUD
* **Rings:**
  * `RingsManager`: the window over the x-sorted ring table, and collected-ring sparkles
  * `ChangeRingFrame`: the shared animation counters `$FEA0-$FEA9`
  * `BuildRings`, inside `BuildSprites` in the original order (HUD, then rings, then the priority lists)
* **HUD:**
  * `BuildHUD`: the flashing RINGS at 0 rings and the flashing TIME at 9 minutes
  * `HudUpdate`, in VBlank: the level timer, plus score, ring, time and lives digits written into VRAM
  * `Hud_Base` at level start
  * the digit art is extracted as indexed PNGs (`objects/hud/`)
* **Frame and VBlank counters:** `$FE04` advances before each frame. `$FE0C` (VBlank count, an input to
  `RandomNumber`) advances after it, as on hardware.

## Validation

New check `Validate objects <data> <trace> 11 1 N`. Each iteration it loads the emulator's whole RAM, runs one
`World.Step` with the recorded joypad (held and newly pressed), and compares every slot that holds a ported object,
plus Sonic's slot, with the next snapshot.

| Recording | What happens | Object co-simulation | Game path (`player-world`, extracted data only) |
|---|---|---|---|
| `zone11-monitor` (new) | Jump onto a ledge, push the monitor, break it from above | all slots exact (monitor 378, contents 62, explosion 35, Motobug 535) | **378/378** |
| `zone11-player-physics` | Run, jump, hit by the Motobug, rings scatter, bridge | all exact except 2 bridge iterations where Tails stands on it | **598/598** (was 294) |
| `zone11-player-physics2` | Spindash, roll, kill the Motobug, bridge | all exact except 1 bridge iteration (Tails) | 697/698 (was 350); the last difference is Tails on the bridge |

The game-path check now also compares the ring window and table, the sparkle list, the sync counters, the ring
count, the score, the timer, the HUD flags, the frame and VBlank counters, and the random seed.

| Other check | Result |
|---|---|
| HUD digit tiles `$6E2-$6FF` and lives digits `$7DD-$7DE` vs VRAM dumps | match (they were the main VRAM differences in report 8) |
| Full screen vs MAME, monitor recording frames 1060 / 1110 | 99.2% / 98.0%; the rest is Tails and an off-route Motobug |
| Full screen, physics recording frames 1140 / 1300 | 96.9% / 98.2% (were 94.5% / 96.4%) |
| Tests | 45 pass, including ring collection with HUD timing and the ring sync counter |

Seeding note: as before, the title card isn't replayed, so the validators seed its counters from the first
snapshot. The level timer is now one of them, because the title card's last VBlank already ticks it.

## Next

1. **Tails (`$B040`):** the sidekick object, its CPU control and its tails. He is the last source of differences
   in these recordings, and he appears on screen.
2. **More zone `$11` objects:** spikes `$36`, platforms `$18`, `$1A`/`$1F` (collapsing), springs `$41`, rock `$3B`,
   the hack's `$22`/`$2B`/`$42`/`$44`/`$49`/`$4B`, checkpoint `$79`, signpost `$0D`, shield `$38`, invincibility stars `$35`.
3. **Title card and level start**, so the validators no longer need seeding; then death and respawn.
4. **Audio playback:** an SMPS player plus YM2612/PSG emulation, fed from the RAM sound queue.
