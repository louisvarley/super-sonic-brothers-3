# Report 2: Level data and reference emulator (2026-10-16)

## Done

* **Level extraction:** all 29 zones x 2 acts. Each zone has `blocks.json` and `chunks.json`, and each act has `layout.json` and
  `level.json` (see [formats.md](formats.md)). `--previews` renders every plane to PNG.
* **`LevelLoader`:** rebuilds an act from the extracted folder alone (tiles PNG, JSON, palettes). The game will load levels through it.
* **Reference emulator:** MAME 0.242 runs headless through `tools/RefEmu` and supports input scripts, per-frame RAM traces and RAM dumps.
  See [reference-emulator.md](reference-emulator.md).
* **`tools/Validate level-ram`:** compares what `LevelLoader` produces with emulator RAM.

## Confirmed

* **Byte-exact match with RAM** for layout, blocks and chunks in zones `$11`, `$13`, `$15` and `$00` (act 1).
* **Palette composition:** `PalPointers[3]` followed by the zone palette matches RAM exactly. The only differences
  are rotations caused by palette cycling. This resolves the open inference in report 1.
* **Player and camera RAM:** the player object fields and camera position behave as expected in the trace.
  Top running speed is inertia `$600`, the same as Sonic 2.
* **Animated blocks:** a new table, `LoadAnimatedBlocks` at `0x4488C`, fills the top of the block buffer in 9 retail-slot zones.
  Before I found it, those zones' chunks referenced blocks with no data. They now match RAM in full (EHZ: all 3072 block words).

## Learned about the hack

* A new game starts in **zone `$11`**, and the attract demos play zones `$11`, `$13`, `$15` and `$00`.
* The hack's own zones (`$11-$1C`) have no animated blocks and no out-of-range references.
* `$FFFFE5` (sound driver select) changes 0 -> 1 when the first demo starts.

## Open

* Palette cycling scripts: these are runtime logic, to be reimplemented with the level code.
* Some blocks use tile numbers above the level art (animated art and PLC art). They render transparent until PLC/animated-art extraction is done.
* HTZ tile patch routine (`0x44D14`, source `0x108460`): not handled yet. HTZ here reuses EHZ art.
* 2-player variants of the block and tile data are ignored.

## Next

1. Collision: height/width arrays, angle table, and the per-zone collision index (primary/secondary).
2. Object placement per act (object layouts and ring layouts).
3. PLC/Nemesis object art and animated level art, which also fills in the transparent tiles above.
4. A watchpoint on `$FFFFE5` to find what selects the sound driver.
