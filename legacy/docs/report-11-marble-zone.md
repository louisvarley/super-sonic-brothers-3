# Report 11: Marble Zone (2026-10-17)

Marble Zone is the hack's zone `$13` (acts 1 and 2) plus zone `$14` act 1 (act 3: the boss and the egg prison).
It plays from act 1 to the end of act 3. The game now follows the hack's level order: Green Hill leads on to Marble
Zone, and zones that are not ported yet are skipped (see "Level order" below). After the Marble Zone act 3 results
the session stops with "Marble Zone complete!", because no zone after it is ported.

## Done

### Level systems
* **Background** (`BackgroundPlane.MarbleLayers`, 0x1003E) and its deformation (`Camera.SwScrlMz`, 0xF0F6).
* **Level events** for all three acts (`Mz1Events`, `Mz2Events`, `Mz3Events`). In act 3, at camera x `$17F0` the
  events lock the camera at `$1800`, start the boss music, load PLCs `$47`/`$40` and spawn the boss (object `$73`) at
  (`$19F0`, `$22C`).
* **Animated magma** (0x4423A): the art is extracted raw (`art/animated/magma_1114F6.bin`) and shifted per frame, as
  the original does.
* **The hack's lava line** (init 0x164470, per frame 0x16461A, per-act table 0x16467A). Below a moving line the
  H-interrupt (0x1032) rewrites colours 18-20 (and 30); the renderer splits the palette at that line. Badniks pick
  their palette line from it (0x164D38, `ObjectKit.HackPalette`). Green Hill uses the same system to swap art at tile
  `$500`.

### Objects (hack zone table)

| Id | Object | ROM |
|---|---|---|
| `$13` / `$14` | Lava ball maker / lava ball | |
| `$19` | Grass fire | 0x1535F4 |
| `$2F` | Grass platform (three height profiles) | |
| `$30` / `$31` | Glass block / chained stomper | |
| `$32` / `$33` | Button / push block | |
| `$46` | Brick | |
| `$4C` / `$4D` | Geyser maker / geyser | |
| `$4E` | Lava wall (chases Sonic) | |
| `$51` | Breakable block | 0x27888 speeds |
| `$52` | Moving block | |
| `$53` | Collapsing floor | |
| `$54` / `$71` | Lava marker / invisible block | |
| `$55` | Basaran (bat) | |
| `$73` | **The boss** (ship, face, flame, hanging part) | 0x155EC2 |
| `$74` | **The boss's fire** | 0x156342 |
| `$78` | Caterkiller | |

**The boss (`$73`).** The placed object is the ship and spawns its face, flame and a hanging part (Green Hill's anchor
mappings, frame 4). It flies left to `$1910`, then sweeps between `$1830` and `$1910`, swooping down over the lava.
At random intervals it raises a lava ball (`$14`) from the pit. At each end it settles at y `$22C`, turns round and
drops a fire. It takes 8 hits. When defeated it scores 1000 points, explodes for 180 frames, drops, rises, brings
back the level music and the animal art, and flies off to the right while the camera's right limit opens to `$1960`.
The Green Hill boss helpers it shares are now public: BossMove (0x152EBA) and Explosions (0x152E78). LoadAnimalArt
(0x120A2) uses the zone PLC table (0x1721A) for every zone.

**The fire (`$74`).** A dropped fire is harmless for 30 frames, then falls to the floor and splits into two runners,
one going left and one going right. Every 16 pixels a runner leaves a static fire, which burns for `$67` frames and
then flickers out. A runner that leaves a ledge falls and returns to its last fire; the third time, it is deleted.

### Level order
* The results screen (0x17592) now reads the full level order (0x176B4, extracted to
  `objects/signpost/level-order.json`) instead of a hand-written list.
* `LevelSession` follows it. When the next zone isn't ported, the session walks the order to the next zone that is
  and loads that zone instead (`LevelSession.ZonesSkipped` lists what it passed over). The session stops with
  "<zone> complete!" only when no ported zone is left.
* `--zone`/`--act` start anywhere, and play continues through the order from there. The same applies to the trainer's
  `StartZone`/`StartAct` settings.

## Validation

| Recording | What happens | Differences |
|---|---|---|
| `$13` act 1 run (2,600 iterations) | Running, platforms, lava, enemies, music | none |
| `$13` act 2 run, geysers | Geyser maker/geysers, lava wall | only the teleport/reload iterations |
| `$14` act 1 run | Breakable blocks, collapsing floors | lag frames in the driver |
| `$14` boss (`mzboss`, 2,896 iterations) | Boss fight (8 hits), lava balls, fires splitting and spreading, defeat, explosions, escape, egg prison, animals, results, change to zone `$15` | the teleport poke; driver RAM differs on exactly the 44 lag frames |

The boss recording places Sonic on the block to the right of the pit, invincible (status bit only; with a zero
timer it never runs out), and lets `AUTOPLAY=chase:73` play. The autoplay now also jumps when Sonic pushes against a
wall, so it can climb out of the pit to reach the prison.

A screen comparison of Marble Zone act 1 at frame 1200 matched MAME on 99.4% of pixels.

The tests (now 56) cover:
* the Marble Zone boss through the prison and results to "Marble Zone complete!" in the game itself
* Green Hill's end leading on into Marble Zone act 1
* the act 1 → 2 and act 2 → 3 signposts
* level-order skipping

## Not done
* The **special stage**, the **continue screen** and the **SEGA/title screens**.
* **The Z80 sound driver** (only used outside the hack's zones).
