# Report 13: Emerald Hill (2026-10-18)

Emerald Hill is Sonic 2's own first zone, zone `$00`, kept in the hack unchanged. Its code runs from the object
table's **first column** (Sonic 2's zones), so its objects are registered with `RegisterS2`. Ids whose two columns
point at the same routine fall back to the hack zones' port automatically. Both acts now play from start to finish,
boss and egg prison included. After act 2 the ROM's level order goes on to `$0D00` (Chemical Plant, not ported), so
the game skips ahead to the next ported zone (currently Marble Zone 1).

**Sound:** Sonic 2's zones use Sonic 2's Z80 sound driver, ported since (docs/audio.md).

## Done

### Level systems
* **Background and deformation** (`Camera.SwScrlEhz`, Sonic 2's SwScrl_EHZ 0xDE9C).
* **Level events** (`Camera.EhzEvents`, 0x108A4). Act 1 has none.
  * Act 2: from camera x `$2780` the left limit follows the camera and the bottom limit becomes `$390`.
  * At `$28F0` the arena locks (`$28F0`-`$2940`), the music fades, the boss flag becomes 2 and PLC `$29` loads.
  * 90 frames later the boss (object `$56`, subtype `$81`) spawns at (`$29D0`, `$426`) with the boss music.
* **Palette cycle**: the waterfalls' colours (0x1C04, table 0x22C8).
  * Every 8 frames, colours 19-20 and 30-31 take the next of 4 frames.
  * The palette-cycle data is now general: `palette_cycle.json` format 2 lists the target colours, so Green Hill's
    (0x2040, colours 40-43) and Emerald Hill's both use `World.PaletteCycle`.
* **Touch response during the boss**: boss id 2 leaves the hitbox sizes alone (0x43C4A), the same as Green Hill's.

### The PLC queue (all zones)
Art loaded during play goes through the original's queue (`PlcQueue`):
* `LoadPLC` (0x17F2) appends the entries to `Plc_Buffer` (`$F680`).
* `RunPLC_RAM` (0x185E, in the level loop) starts an entry.
* `ProcessDPLC` (0x18D0, in VBlank) decompresses 3 tiles a frame.

The port still copies the art into VRAM at once, but it keeps the queue and its counters exactly as the original
does, frame by frame. Objects that wait for the queue to empty now wait as long as in MAME. Two recordings had shown
the difference:
* **The results screen** appeared about 63 frames early.
* **The game-over text** (`$39`) also appeared early.

Both now match. Level-load PLCs stay synchronous, as in the original.

### Objects (Sonic 2's column)

| Id | Object | ROM | File |
|---|---|---|---|
| `$06` | Spiral (corkscrew) path | 0x25518 | `Spiral.cs` |
| `$1C` | Scenery (bridge stakes etc.) | 0x14034 | `SceneryS2.cs` |
| `$36` | Spikes (Sonic 2's) | 0x18F3E | `SpikesS2.cs` |
| `$49` | Waterfall | 0x24BAC | `Waterfall.cs` |
| `$4B` | Buzzer | 0x312D0 | `Buzzer.cs` |
| `$56` | **The boss**: drill car with Eggman | 0x33154 | `EhzBoss.cs` |
| `$5C` | Masher | 0x315F8 | `Masher.cs` |
| `$9D` / `$98` | Coconuts / its coconut | 0x3BDFA | `Coconuts.cs` |
| `$DD` | Giant ring (the same code as the hack's `$4B`) | 0x150FE | |

Shared with the hack's zones (the same routine in both columns, already ported): `$03` plane switcher, `$0D`
signpost, `$11` bridge, `$18` platform, `$25` ring, `$26` monitor, `$3E` egg prison, `$41` spring, `$58` boss
explosion, `$79` star post.

### The boss (`$56`)
The placed object is the ship's bottom half. It spawns, in slot order, the other parts (same id, other routines):
* the cockpit with Eggman;
* the ground car;
* two foreground wheels and a background wheel;
* the drill;
* the propeller.

The ship flies in diagonally and lands on the car, and the propeller winds down. Then the car drives at `-$200`
between x `$28A0` and `$2B08`; its height is the average of the two foreground wheels, which follow the floor.

* **Hits:** eight. Each makes the boss flash white for 32 frames and Eggman wince.
* **Laugh:** Eggman laughs while a player is hurt.
* **The drill:** with one hit left, it flies off forwards when Sonic is on the side it points to.
* **The defeat:**
  1. 1000 points.
  2. The car bounces up and falls, with explosions, for `$B3` frames; the wheels bounce away.
  3. The egg prison's art loads (PLC `$40`).
  4. A new propeller winds up, the level music returns and the boss-defeated flag is set.
  5. Eggman rises and flies off right while the camera's right limit opens to `$2AB0`.

## Validation

| Recording | What | Result |
|---|---|---|
| `ehz/m1` | Act 1 start: badniks, monitors, waterfalls, bridges | all ported objects match; differences only on the setup/reset frames |
| `ehz/e1` | Act 1: the first spiral (star-post restart) | match, except the first (poked) iteration |
| `ehz/e2` | Act 1 end: spirals, signpost, results, giant ring | match, except the first iteration (the results now included) |
| `ehz2/b1` | Act 2: the whole boss fight, egg prison | `$56`: 19345 slot-iterations, **0 differ**; `$58`, `$3E`, `$37` 0 differ |

In `ehz2/b1` the star post (`$79`) differs for 3 iterations: the recording's 50-ring poke makes it want to spawn the
special-stage star ring (0x2342E). That is a deliberate omission, because the special stages are outside the port.

Tests: `Zone00Act2_BossPrisonAndResults_LeaveEmeraldHill` (boss to the next level, in the game itself) and
`PlcQueueTests`.

### Recording technique: the star-post restart
Teleport pokes after the level has started skip the object manager, so objects near the destination never load.
Instead, poke a star-post restart while the level is loading, with the level-select route
`tools/RefEmu/inputs/levelselect-ehz1-hold-right.txt` (act 2: `levelselect-ehz2-boss.txt`). At frame 1260:

| Address | Value |
|---|---|
| `FE30`, `FE31` | 1 (star post hit) |
| `FE32` / `FE34` | x / y |
| `FE3C` | `0780` |
| `FE3E` | `0C0D` |
| `FE40` / `FE42` | camera x / y |
| `FE56` | max y |

For example:

```
P="1260:FE30:01,1260:FE31:01,1260:FE32:26,1260:FE33:AC,1260:FE34:02,1260:FE35:BC,1260:FE3C:07,1260:FE3D:80,\
1260:FE3E:0C,1260:FE3F:0D,1260:FE40:26,1260:FE41:0C,1260:FE42:02,1260:FE43:5C,1260:FE56:03,1260:FE57:90"
POKES=$P TAP=FE04 TAP_FROM=1260 TAP_TO=5000 tools/RefEmu/run.sh tools/RefEmu/inputs/levelselect-ehz2-boss.txt 5000 out
```

For the boss, keep Sonic alive by poking rings (`FE20`) every few hundred frames; jumping on the spot every 24
frames is enough to beat it.

## Not done
* The special-stage star ring at star posts.
* 2-player mode.
