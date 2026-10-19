# Report 10: Green Hill complete: Tails, every object, the boss, audio (2026-10-17)

Green Hill is the hack's zone `$11` (acts 1 and 2) plus zone `$12` act 1 (act 3: the boss and the egg prison;
zone `$12` act 2 is an unused duplicate). It now plays from the first act to the end of act 3, with sound.
After the act 3 results the session stopped with "Green Hill complete" (zone `$13` was not ported then; since report 11 it goes on to Marble Zone).

## Done

### Tails
* Tails (Obj02) runs Sonic's code through `PlayerProfile`, the variables in which the original's duplicate code
  differs. His CPU follower logic, animation, DPLC, hurt, death and respawn are ported, as is his tails object (Obj05).

### Objects (hack zone table)

| Id | Object | ROM |
|---|---|---|
| `$06` | Force-roll trigger | |
| `$08` | Spindash/skid dust | 0x1FBDE area |
| `$0D` | Signpost (spins, starts the results) | 0x1CF34 |
| `$15` | Swinging platform | |
| `$17` | Helix (spiked log) | |
| `$18` | Platforms | |
| `$1A` | Collapsing ledge | |
| `$1C` | Scenery | |
| `$1F` | Crabmeat | |
| `$22` / `$23` | Buzz Bomber / its missile | |
| `$25` | Ring | |
| `$2B` | Chopper | |
| `$35` / `$38` | Invincibility stars / shield | |
| `$36` | Spikes | |
| `$39` | GAME OVER | 0x17256 |
| `$3A` | Results (score tally, continue icon) | |
| `$3B` / `$3C` | Rock / breakable wall | |
| `$3D` / `$48` / `$58` | Green Hill boss, its wrecking ball, boss explosions | |
| `$3E` | Egg prison (with the animals' capsule routine) | |
| `$41` | Springs (not the diagonal ones, which Green Hill doesn't use) | |
| `$42` | Newtron | |
| `$44` | Edge wall | |
| `$49` | Waterfall sound | |
| `$4B` | Giant ring (50 rings; streams its art to VRAM) | 0x150FE |
| `$79` | Starpost (the star-ring bonus is skipped) | |
| `$DE` | Giant ring flash | 0x151F4 |
| `$3F`, `$7D` | DeleteObject in hack zones | 0x1A0C2 |

Object `$4B` and `$7D` were briefly mis-identified as the Buzzer and the hidden bonus points: those are the
**first** (Sonic 2 zones) pointers of their table entries. Each entry of the table at 0x199CA is
`{Sonic 2 zones, hack zones}`, and `RunObjects` adds 4 when `$FFE6` is set.

### Level flow
* **Death and restart:** `KillCharacter`, the death fall, `CheckGameOver` and the `$FE02` restart. A restart reloads
  the act with the previous RAM's globals, from a starpost if one was touched.
* **`Level_Inactive_flag` (0x51C4):** the level loop now stops right after `RunObjects` when `$FE02` is set, as in
  the original. (Before, the background and level events ran once more for the next act's zone.)
* **Act changes:** the results screen advances `$FE10`; the session loads the next act from it.
* **Game over:** Obj39, music `$9B`, PLC 3. When it ends the original goes to the continue or SEGA screen; the
  session stops with "Game over" (R starts a new game).
* **Giant ring:** with 50 rings it appears after the act 1/2 signposts. Touching it plays the flash, Sonic vanishes
  and `$F7CE` asks for the special stage (game mode `$2C`, a Sonic 1-style rotating maze). The special stage isn't
  ported: the next load goes straight to the next act (`LevelSession.SpecialStagesSkipped` counts these).
* **Debug starts** put background layers 2 and 3 where scrolling there would have left them. InitCameraValues sets
  them to the camera x, correct only at an act start. This fixes the empty background column seen at high x.

### Audio
See [audio.md](audio.md): the 68k SMPS driver is ported routine by routine and its RAM matches MAME's; YM2612, PSG
and DAC models produce the sound; the zone music starts at level load (0x4E94). The game window plays it through
MonoGame (`--mute` turns it off); headless mode can render a WAV.

## Validation

All with `Validate objects` (object co-simulation, now including the sound driver's RAM). "Matches" means every
slot-iteration of every ported object and every driver-RAM iteration matches except those listed.

| Recording | What happens | Differences |
|---|---|---|
| zone `$11` act 1 run (2,498 iterations) | Running, loops, enemies, rings, music | none |
| zone `$11` act 2 run | Same in act 2 | 6 driver iterations (lag frames) |
| Power-ups | Shield, invincibility (music change), speed shoes (tempo) | 1 lag frame |
| Signpost / results | Signpost spin, act-clear music, tally | the teleport frame; 2 lag frames |
| Boss (`boss3`, `boss5`) | Boss fight, Sonic death; defeat, flee, egg prison, animals, results, act change | the poke frames; 4-8 lag frames |
| Giant ring | 50 rings, ring appears, flash, Sonic vanishes, signpost and results without Sonic | the teleport frame |
| Game over | Last life lost in a pit, GAME/OVER slide, wait, mode change at the same frame | the teleport frame; Obj39's first frame (the original waits a few frames for PLC 3 to decompress) |

Tests (49) include the whole act 3 ending in the game itself (a bot beats the boss and opens the prison; the
session must report completion), game over, and audio (the level music drives all chips; the mixer produces sound).

New harness options: `WAV=` records MAME's audio, and `AUTOPLAY=chase:<id>:<frame>` steers Sonic to any object.

### Level starts: fade and title card
As the original's `Level` (0x4C2A), measured against MAME frame by frame:

| Step | Frames | Original |
|---|---|---|
| fade to black (red, then green, then blue fall one step per colour per frame), music fades (`$F9`) | 22 | `Pal_FadeToBlack` 0x2B7E |
| black while the next act loads | 27 | level loading |
| title card builds: blue rows, yellow band, red strip with zig-zag edge; zone name / "ZONE" / act number slide in | ~45 | Obj34 0x16EC0, VBlank plane fills 0x18B26 |
| card waits for the level art | 92 | `RunPLC` until the queue is empty |
| level ready: the level palette, one RunObjects pass; red retracts, yellow turns blue, the level is uncovered from the bottom (32 px a frame); objects run, controls locked | 26 | 0x4F1C-0x5156 |
| play starts; the text waits 45 frames and slides out, then the after-card PLCs load | | 0x5158, Obj34 routine `$16` |

* `LoadTitleCard` (0x18D7C): the base art and the zone's letters from the letter font are extracted
  (`objects/title-card/`) and copied to VRAM tiles `$580`/`$5DE`.
* The card's plane A fills are kept as a nametable override over the level's plane A (`TitleCardPlane`); the renderer
  draws it in place of the level where set.
* `LevelSession.Phase` tells where a level start is; inputs are ignored outside `Playing`.
  `new LevelSession(..., titleCards: false)` starts acts instantly (the validators' `World` has no title card).

## Not done
* **Special stage, continue screen, SEGA/title screens.**
* **The Z80 sound driver** (only used outside the hack's zones).
