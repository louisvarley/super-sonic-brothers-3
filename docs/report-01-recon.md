# Report 1: Recon and first extractor (2026-10-16)

## 1. What the ROM is

* **Identity:** header `SONIC THE HEDGEHOG 2`, `GM 00001051-01`. SHA-1 `6651a52a04caeec071b8e469308caa5b5c4cdd2f`.
* **A modified Sonic 2, not retail:**
  * The header declares 2 MiB and SRAM. Retail Sonic 2 is 1 MiB with neither.
  * Data runs to `0x167FFF`, and the boot code calls into the new area (SRAM load at `0x1658A4`, sound driver at `0x1301DC`).
  * The header checksum was updated to match (`0x80A5`).
* **Content added by the hack:**
  * 12 zone IDs (`0x11-0x1C`) beyond retail's `0x00-0x10`
  * 27 PLC lists (`0x43-0x5D`)
  * 53 palettes (retail has fewer)
  * SRAM save support
  * a second sound driver
* **Where the art comes from:** zone 00 decodes to Emerald Hill art. Zone 11, to my eye, looks like Sonic 1's Green Hill
  Zone. I can't tell from the ROM alone which assets are new and which are reused. A byte comparison
  against a retail Rev01 dump would answer it.

## 2. Engine lineage

The engine is **Sonic 2's** itself, not merely derived from it:
* The entry code, main loop and `GameModesArray` match the retail layout.
* `V_Int` and the `LevelArtPointers` / `PalPointers` / PLC formats are Sonic 2's.
* `LoadZoneTiles` keeps its HTZ/WFZ/DEZ special cases.

The relocated retail `SoundDriverLoad` is also present.

The hack layers changes on top: extended tables, per-act palette and chunk overrides (`$1501`, `$1801`, `$1B01`)
and a Sonic 1-style 68k SMPS driver (**inference**, see §4). The public s2disasm labels are therefore a good
reading aid, as long as every address is re-checked in this ROM.

## 3. Memory map and formats

See [memory-map.md](memory-map.md) and [formats.md](formats.md).

* **Compression:**
  * Kosinski (`0x1B0E`), Nemesis (`0x1166`) and Enigma (`0x1994`) were each found as routines.
  * Kosinski and Nemesis were also verified by decoding all level data and all 487 PLC entries without error.
  * Saxman is inline in the retail sound-driver loader. Enigma and Saxman decoders are not written yet.

## 4. Audio

`SoundDriverLoad` (`0x1472`) chooses between two drivers based on `$FFFFE5`:

| `$FFFFE5` | Path | Evidence |
|---|---|---|
| 0 | Retail Sonic 2 Z80 SMPS, Saxman-compressed, uploaded by the relocated loader at `0x101D26` | Instruction-for-instruction retail `SoundDriverLoad`, output length `$F66` |
| ≠0 | 68k SMPS (Sonic 1 style) at `0x1301DC`, called every VBlank. Small Z80 DAC driver from `0x131576` (Kosinski) | Driver RAM at `$FFF000`, polls `$A01FFD` like S1's DAC driver; Z80 blob decodes to valid Z80 code |

**Still to verify:** what sets `$FFFFE5`, and whether each path has its own music data. This needs a watchpoint in the
emulator. If both drivers ship different sequence formats, audio extraction needs two SMPS parsers.

## 5. Conversion strategy (recommendation)

**Recommendation: hand-reconstruction by subsystem, guided by the disassembly. Not static recompilation.**

| | Static recompilation (68k -> C#) | Hand reconstruction |
|---|---|---|
| Output | Unreadable register-level C#, emulating flags and memory | Readable classes: `Player`, `Camera`, `ObjectManager`... |
| Jump tables | Must be resolved for every `jsr (pc,d0.w)` / routine-counter table (game modes, `V_Int`, every object's routine index). Misses break at runtime. | Rewritten as `switch`/state machines |
| VDP/Z80 coupling | Still needs a cycle-faithful VDP/DMA model, because the code writes ports directly | Renderer only needs the VDP *features* used |
| Hack code at `0x100000+` | Comes along automatically | Has to be found and rewritten |
| Bit-exact physics | Easy | Needs careful porting of fixed-point maths, checked against the emulator |
| Speed to first playable | Fast | Slower |
| Maintainability | Poor | Good |

Static recompilation is only worth it as a temporary oracle. Your "no 68k interpreter" rule already
excludes shipping it, so I'd skip it and use an emulator as the oracle instead.

**Can't be resolved statically, so needs runtime tracing:**
* computed jumps through routine tables (game modes, `V_Int` routine table at `0x47E`, object routines,
  object ID -> code pointer table)
* `$FFFFE5` driver selection
* both Z80 uploads (`0x101D26` Saxman path, `0x1490` Kosinski path)
* the DMA queue and VBlank timing
* SRAM contents

I haven't found any self-modifying code or code executed from RAM yet, but I haven't ruled it out. I'll
check with an execute-from-RAM breakpoint during validation.

**Rough effort** for one experienced developer to full parity. These are low-confidence estimates:

| Work | Estimate |
|---|---|
| Remaining extraction (blocks, chunks, layouts, collision, objects, sprites/mappings, Enigma screens, audio) | 3-5 weeks |
| Runtime shell (planes, scroll modes, sprites with limits, palette effects, water HInt, input, audio out) | 2-4 weeks |
| Core engine (level load, player physics, collision, camera, rings, HUD, game modes) | 6-10 weeks |
| Objects, badniks, bosses (retail ~200 object IDs, plus the hack's) | 3-6 months |
| Audio: pre-rendered via an emulated YM2612/PSG, or a real-time SMPS player (×2 drivers) | 2-5 weeks |
| Emulator-diff validation tooling | 2-3 weeks |

**Total: about 6-10 person-months.** Objects dominate, so a vertical slice is the sensible first milestone:
one zone, the player and rings, no badniks.

## 6. Reference emulator (stage 2, not started)

Recommendation: MAME, for its debugger, watchpoints, trace logging and Lua scripting, which drive scripted input playback and per-frame RAM dumps for the diff harness. BlastEm, which has a GDB stub, is a lighter alternative. No emulator is installed on this machine yet.

## 7. What exists now

* `src/S2Port.Extraction`: ROM reader, Kosinski, Nemesis, indexed PNG writer/reader, palette
  conversion, extractor with manifest, and `Extractor.EnsureExtracted()` for the game's first-launch extraction
  (it checks the manifest's ROM SHA-1 and extractor version).
* `tools/Extract`: `dotnet run --project tools/Extract -- rom.bin extracted` writes all palettes (53) and all
  level tilesets (29 zones) in about 1.5 s.
* `tests/S2Port.Tests`: 10 tests (decompressor vectors, colour conversion, PNG and tile round trips).

## 8. Next steps

1. Decode the 16x16 blocks, 128x128 chunks and level layouts into JSON. Render one full zone map as a PNG to check it.
2. Collision height/angle arrays and per-zone collision indexes.
3. PLC/Nemesis object art and animated level art (this also resolves `blockRefsOutOfRange`).
4. Set up MAME, record a CRAM dump in zone 00 and compare it with `pal_03`+`pal_04` to confirm the palette composition.
