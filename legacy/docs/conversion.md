# Whole-ROM conversion pipeline

## Native campaign batch (current web workflow)

```powershell
npm run campaign
npm run campaign -- --force
npm run campaign -- --strict
```

This uses the existing approved `sonicbros3.bin` as an engine-asset base and
the source `smb3.nes`, with **no `rom.bin` access**. Output is a separate
`web-data/campaign.bin`, automatically preferred by the Node host.
Docker Compose runs the equivalent conversion as a prerequisite service.

The batch processes 257 areas across 13 tilesets, builds native terrain and
mapped objects for 255, and wires 8 source world maps / 19 screens. The catalog
also follows the ROM's per-world bonus-room and common-exit tables, adding nine
areas not reachable by alternate headers alone. Three
vertical areas fit the engine layout; the fourth (2880px) is explicitly blocked.
The known invalid original-loader entry is reported as failed.

| New archive path | Content |
| --- | --- |
| `campaign/campaign.json` | Input identities/version, all world bindings, act-to-area registry, dimensions, alternate target, native status and explicit blockers |
| `campaign/backlog.json` | Unmapped object IDs ranked by affected source areas |
| `zones/area_<source-id>/act_1/level.json` | Engine-loadable level using native tiles, blocks, chunks, layout, collision, mapped objects, rings and original-style assets |
| `zones/area_<source-id>/music.*` | Per-area header-selected SMB3 song mounted through the original sound driver |
| `zones/area_<source-id>/animation.*` | CHR animation when the source selects the supported shared animated bank |
| `zones/area_<source-id>/act_1/gameplay.json` | Source metatiles for water detection, directional pipe mouths, junction destinations, connected transit paths and initial pipe-exit direction |
| `campaign/effects/*.pcm` | Locally generated pipe/map-entry effects from original sound-engine pulse-channel writes |

All native areas are marked `native-partial`, not complete gameplay. Missing
source objects are recorded; unsupported native exits are not replaced with
invented goals. Water cells are passable and drive shared Sonic/Tails swimming.
Directional pipes use source junction tables; connected transit paths follow
the actual pipe bodies/elbows, including vertical-maze horizontal wrapping.
Map pipeways return through the paired source object-list binding without
clearing a level or awarding a card. Other hazards, scrolling controllers,
bosses and map commands still need behavioral adapters. The browser displays
these limitations before a test launch. `complete` is false and `--strict`
returns 2 until the real behaviors and campaign progression exist.

The CLI checks title-card stepping and rendering before publishing. A load
failure blocks that area instead of pretending the build succeeded. Temporary
staging is separate from the input archive, and the final archive is replaced
only after the batch finishes. Unchanged input hashes/version reuse the output.
Increment `Smb3Campaign.Version` when changing batch/runtime adapter output.

## Legacy desktop conversion

> Historical desktop pipeline. The default Node/web implementation is described
> in [web migration](web-port.md) and the [README](../README.md). The Node
> converter uses stock Sonic 2 `.md` + `smb3.nes`; its CLI is `npm run convert`.
> The restored browser engine temporarily needs the existing game archive:
> the full stock-ROM engine asset importer remains unfinished.

`tools/ConvertSmb3` and the game both call `SonicBrosData.EnsureBuilt`. One command
builds the engine assets, native playable zones, screen assets and whole-ROM
import. Archive updates are staged and packed after the conversion report is
written. Current builds are reused; `--force` regenerates converted content.
Increment `Smb3Conversion.Version` when changing import output or behaviour
mappings, and `Smb3ZoneBuilder.Version` when changing native zone/art output.

## Output inside sonicbros3.bin

| Path | Content |
| --- | --- |
| `conversion/report.json` | Version/source identity, area and song statuses, campaign blockers, `complete` |
| `conversion/catalog.json` | All eight world maps, map entry bindings, recursively discovered ordinary areas |
| `conversion/worlds/worldN.json` | Map cells (integer array), screens stored consecutively in 16 x 9 blocks; entry screen, row, column, tileset and source pointers |
| `conversion/areas/<id>/area.json` | Nine-byte header, orientation, original pointers, deterministic generator seed and tile-memory format |
| `conversion/areas/<id>/tiles.bin` | Original loader's 6480-byte tile-memory snapshot, not native collision or a playable level |
| `conversion/areas/<id>/graphics.json` | BG CHR selection, converted palette values and links to shared native tiles |
| `conversion/areas/<id>/objects.json` | Every source object with a native placement where a behaviour mapping exists; unknown objects retain their source data |
| `conversion/tilesets/tsNN.json` | Original quadrant arrays and per-quadrant floor/wall thresholds |
| `conversion/graphics/chr_XX_pN.bin` | Every CHR bank in native Mega Drive 4bpp: 64 tiles x 32 bytes, one file per NES palette quadrant |
| `conversion/logic/rules.json` | Snapshot of the shared native object registry |
| `conversion/logic/backlog.json` | Missing behaviour IDs ranked by number of affected areas |
| `conversion/music/set2A_XX.*`, `set2B_XX.*` | SMPS song bytes and existing-driver-compatible `extraSongs` manifests |
| `zones/zone_40`, `zones/zone_41` | Native, playable 1-1 and 1-2 |
| `zones/zone_42` | Native 1-3 main route; white-block/warp-whistle secret remains unsupported |

Paths in the archive use ZIP separators. Source ROMs are identified by SHA-1,
not guessed by filename. Import output contains copyrighted data derived from
your own ROMs: keep it local.

## What is automatic

The importer reads this ROM revision's world pointer tables, rather than
maintaining a hand-written list of level names. IDs include tileset, layout bank,
layout pointer and object pointer, so shared layouts with different object lists
remain distinct. Special entries are preserved as commands with `areaId: null`;
they must not be interpreted as ordinary level pointers.

Alternate header pointers and per-world bonus/common exits are followed to a fixed point. Some unused alternate
headers point to invalid data; those are reported as loader failures. Object- or
script-driven destinations are not comprehensively discovered.

`Smb3DecodedArea` executes the cartridge's tileset-specific loader on the existing
offline 6502 core with a two-million-instruction limit per area. This covers
unported terrain generators without writing hundreds of duplicate C# handlers.
The raw dump also preserves vertical layouts; `Tile(row, column)` now selects
the correct horizontal-screen or vertical-row addressing.

The music stage imports 15 Set 2A and 12 Set 2B selectors using the shared
sequencer and SMPS arranger. These are arrangements for PSG/FM/DAC, not
bit-identical NES audio. Map music uses each world's Set 2A selector. The
campaign additionally runs the source sound driver offline for pipe and
map-entry effects and renders its two pulse channels; this is not a complete
NES APU emulator. Other sound effects, Set 1 fanfares and interactive music
changes are not converted by this stage. Each manifest mounts **one song** in
slot `$94` at `$F80000`; don't merge all manifests into a single driver instance.
Existing playable levels retain their current song IDs and audio behaviour.

## Minimal-effort adapter workflow

1. Run the converter once and inspect `report.json` and `logic/backlog.json`.
2. Implement a missing behaviour once in `src/SonicBros3/Logic/Objects`, register
   it in `Logic/Core/Objects.cs`, and map the source ID in
   `Smb3ObjectConverter.Rules`. Both the zone builder and batch import use the
   same registry; rebuilding applies that mapping to every affected area.
   A mapping is not a substitute for implementing the actual behaviour.
3. Reuse `Smb3ZoneBuilder` and the shared tile/palette data for a reviewed native
   level recipe in `Smb3Levels`. Native zones now consume the original loader's
   decoded terrain, so generators do not need to be reimplemented individually.
   The campaign builder uses source-header CHR banks, palettes, music and
   dimensions across all tilesets. Only Plains/Hills have reviewed gameplay
   semantics. Other collision semantics, entrances, animations, events and
   transitions still need adapters; generating terrain does not supply them.
4. Wire reviewed levels into native map/game flow, add headless gameplay tests
   and increase the conversion version. `SonicBrosData` builds every registered
   recipe, so individual build commands are unnecessary.
5. Re-run `--force`; use `--strict` as the eventual full-campaign completion gate.

Statuses: `engine-ready` means a registered native zone with mapped object IDs
was built; `native-partial` means a native zone was built but has explicit
blockers (1-3's secret route and its unmapped trigger `$07`); `decoded` means
intermediate data was exported but native work remains; `failed` includes its
explicit error. World/campaign blockers are separate:
even engine-ready areas do not prove complete progression.

The shared object registry distinguishes ordinary/fire plants, green/red colour
and ceiling orientation for source IDs `$A0`-`$A7`. Only fire variants shoot;
all three plants in the source 1-2 are legitimately fire variants. Source `$82`
uses the native Boomerang Brother and returning projectile. Koopa placements
and opaque sprite feet match their native floor probes, including shell
transitions; walking enemies also stand on interactive block objects rather
than falling through the terrain cells removed for those blocks.

The current pipeline is infrastructure for a full port, **not an automatic
machine-code translator**. Remaining native work includes other terrain
semantics, bosses, other enemies, moving platforms, water/auto-scroll, junctions,
inventory, world-map events, minigames and later-world progression. The report
intentionally remains incomplete until those adapters exist.

ROM table structure and labels were cross-checked with
[Southbird's SMB3 disassembly](https://github.com/captainsouthbird/smb3)
([project](http://www.sonicepoch.com/sm3mix/)); disassembly source is not bundled.
