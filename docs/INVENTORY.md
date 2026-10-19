# Inventory (Phase 0)

_Status: draft for approval. Written 2026-10-19 after reading the repo, building it, running it headless and running the
test suite. Line-level detail was read for the files listed as "read"; others are described from the existing
`docs/` and the module's public surface. Corrections welcome._

## 0. TL;DR

* The **game** is C# (`src/SonicBros3`). It is a hand-port of the 68k engine in a **modified Sonic 2 ROM** plus
  conversions of SMB3 data. It reads **no ROM at run time**: it reads a prebuilt derived archive.
* **Node.js (`web/`)** is a **web server + a partial stock-ROM conversion/catalog pipeline + the browser front-end**. It
  does not contain gameplay. The browser runs the *same C# engine* compiled to WebAssembly.
* The **asset pipeline** (`src/S2Port.Extraction` + `Smb3` + `tools/ConvertSmb3`) turns ROMs into
  `sonicbros3.bin` (engine assets + SMB3 zones + a whole-ROM "conversion" import). A further **campaign** batch
  produces `web-data/campaign.bin` (255 experimental native SMB3 areas).
* Verified today: `dotnet build SonicBros3.slnx` succeeds; the 136-test suite passes; `--screenshot … --frames 120`
  runs headless and writes a PNG. Running the game **rebuilt `sonicbros3.bin`** (see §11.4).

## 1. ROMs and derived data (the ground truth)

All at repo root, all gitignored, none tracked:

| File | Size | SHA-1 | What it is | Used by |
|---|---|---|---|---|
| `rom.bin` | 2 MiB | `6651a52a04caeec071b8e469308caa5b5c4cdd2f` | **A modified Sonic 2** ("the hack"): engine + hack zones/art/audio. Header `SONIC THE HEDGEHOG 2`, `GM 00001051-01`. | the C# engine's extractor; `tools/Disasm` |
| `Sonic The Hedgehog 2 (World) (Rev A).md` | 1 MiB | `8bca5dcef1af3e00098666fd892dc1c2a76333f9` | Stock Sonic 2 (a `.md` binary despite the name) | only the Node catalog (`web/roms.mjs`) |
| `smb3.nes` | 384 KiB | `a03e7e526e79df222e048ae22214bca2bc49c449` | SMB3 (USA), iNES | C# SMB3 module + Node catalog |
| `sonicbros3.bin` | ~5 MiB | (derived) | **The game's asset archive**; a ZIP. Built from `rom.bin`+`smb3.nes`. | the game (desktop & browser) |
| `data.bin` | ~4 MiB | (derived) | An older `tools/Extract` output (engine-asset ZIP only). Not referenced by any code. | nothing (stale) |
| `web-data/campaign.bin` | ~7.6 MiB | (derived) | Campaign ZIP: base archive + 255 native SMB3 areas | `web/server.mjs` |
| `Super Mario Bros. 3 (USA).zip` | 222 KiB | — | just the source zip for `smb3.nes` | nothing |
| `trainer-runs/*` | — | — | old RL "trainer" run data, no code references it | nothing |

**Consequence for the port (decision needed):** the engine is *not* fed raw ROMs today; it is fed the derived
`sonicbros3.bin`. "Load ROMs at run time" therefore means porting the **whole extraction/build pipeline** (Sonic 2
extractor + offline 6502 + SMB3 zone builder + conversion), not just a ROM reader. See §8, §12 Q3.

## 2. Top-level layout

```
src/S2Port.Extraction/   C# asset extraction library (ROM -> engine assets; SMB3 decode/convert)
src/SonicBros3/          C# game: engine logic + software VDP renderer + audio + frontend + MonoGame host
src/SonicBros3.Browser/  C# WASM bridge (JSExport) that recompiles src/SonicBros3 without the host
tests/SonicBros3.Tests/  C# xUnit tests (136)
tools/Extract/           CLI: ROM -> engine-asset archive (low level)
tools/ConvertSmb3/       CLI: full build + campaign batch (high level)
tools/Validate/          CLI: SMB3 decoder/music checks vs the original 6502/APU
tools/RefEmu/            MAME harness for the *Genesis* (nes.sh is for SMB3) + input scripts
tools/Disasm/            m68k.py: disassembly helpers for rom.bin
web/                     Node server, WASM build glue, stock-ROM catalog/convert, browser UI
docs/                    existing design/porting/report docs (this file added)
```

## 3. C# — `src/S2Port.Extraction` (build / asset-extraction tooling)

Library. Entry surface: `Extractor.EnsureExtracted/Extract`, `DataFile`, `LevelLoader`, `Smb3.*`.

| File | Purpose |
|---|---|
| `Extractor.cs` (1303 ln) | The whole Sonic 2 extractor: reads `rom.bin`, writes `extracted/`/archive. `Version = 71` gating. Emits palettes, collision sets, PLCs, zones, audio, sprites, HUD, frontend, object data tables. |
| `RomMap.cs` | Every ROM address the extractor reads (build-specific; `SupportedSha1`). |
| `RomImage.cs` | ROM load, SHA-1, header checksum. |
| `DataFile.cs` | The archive abstraction: paths like `…/data.bin/zones/…` transparently read a ZIP; `Pack`/`Unpack`/`Forget`. |
| `DataPaths.cs` | Locates the data folder by walking parents for `rom.bin`/`sonicbros3.bin`/`extracted/`. |
| `Manifest.cs`, `CompactJson.cs` | Extraction manifest + compact JSON writer. |
| `Compression/Kosinski|Nemesis|Enigma|Saxman.cs` | The four ROM compressors. (Docs say Enigma/Saxman were "not yet", but they now exist.) |
| `Graphics/IndexedPng.cs` | Reads/writes indexed PNGs (tile sheets, previews). |
| `Graphics/MdColor.cs`, `Tiles.cs` | CRAM↔RGB, tile-sheet↔4bpp. |
| `Levels/LevelData.cs`, `LevelJson.cs`, `LevelLoader.cs` | Load an act from the extracted folder: tiles/blocks/chunks/layout/collision/objects/rings/PLCs/animated tiles/palette cycle/bounds/start. `LoadTileArt`, `LoadCollisionSet`. |
| `Levels/PlaneRenderer.cs` | Renders an act's plane to a PNG (previews). |
| `Audio/Smps68k.cs`, `SmpsZ80.cs`, `Wav.cs` | Walk both SMPS sound formats, export songs/SFX/DAC + WAV helper. |
| `Frontend.cs` | Extracts title/level-select art & tables. |
| `Smb3/NesRom.cs` | iNES load, `Prg/ChrTile`, SMB3 SHA-1 gate (`Smb3Map.SupportedSha1`). |
| `Smb3/Cpu6502.cs` | Offline 6502 core (MMC3 banking) used **at build time** to run SMB3's own level loaders. |
| `Smb3/Smb3LevelDecoder.cs` (874 ln) | Ports SMB3's tileset-specific level generators; cross-checked against `Cpu6502`. |
| `Smb3/Smb3Levels.cs` | The three hand-registered native levels 1-1/1-2/1-3 (zones `$40/$41/$42`). |
| `Smb3/Smb3ZoneBuilder.cs` (398 ln) | Builds a native engine zone from a decoded SMB3 area (tiles→MD 4bpp, blocks, chunks, layout, collision, objects, rings, palettes, animation). `Version` gating. |
| `Smb3/Smb3Catalog.cs` | Discovers all 257 areas from the world pointer tables. |
| `Smb3/Smb3Campaign.cs` | The campaign batch: builds every area, records status/blockers, writes `campaign/campaign.json` + backlog. `Version = 3`, zone `$43`. |
| `Smb3/Smb3Conversion.cs` | The whole-ROM import: `conversion/*` report/catalog/tilesets/graphics/objects/music. `Version = 5`. |
| `Smb3/Smb3ObjectConverter.cs` | Maps source SMB3 object ids to native object ids (`Rules`). |
| `Smb3/Smb3ObjectArt.cs`, `Smb3Music.cs`, `Smb3SongConverter.cs`, `Smb3Palettes.cs`, `Smb3Map.cs`, `Smb3Gameplay.cs`, `Smb3Effects.cs` | Object art, NES music sequencer + song conversion to SMPS, palettes, map constants, gameplay metadata (water/pipes), pipe/map sound effects. |

## 4. C# — `src/SonicBros3` (the game: runtime engine + host)

`Program.cs` parses args and either runs headless (`--screenshot`) or starts `S2Game` (MonoGame). `SonicBrosData.EnsureBuilt`
extracts/builds on first launch.

### 4.1 Runtime shell
| File | Purpose |
|---|---|
| `Program.cs` | CLI + headless screenshot/WAV runner + input-script loop. |
| `S2Game.cs` | MonoGame DesktopGL host: fixed 60 Hz, keyboard→MD pad, 3× point-scaled RGBA, `DynamicSoundEffectInstance`. |
| `Runtime/LevelSession.cs` | Act flow: title-card phases, fades, pipe travel, act transitions, game over, `ExitOnRestart`, SMB3 campaign areas, save/restore state. |
| `Runtime/Vdp.cs` | Software VDP: 320×224 CRAM-index framebuffer; planes in hardware priority; per-line H-scroll; sprites from RAM SAT (link order, 80/20/320 limits, masking); palette split; `ToRgba`. |
| `Runtime/BackgroundPlane.cs` | Plane B nametable maintained by ports of `DrawInitialBG`/`LoadTilesAsYouMove` incl. Marble Zone's layer rows. |
| `Runtime/AnimatedTiles.cs` | Generic animated tiles + Marble magma custom routine. |
| `Runtime/InputScript.cs` | `RefEmu`-format input scripts. |

### 4.2 Engine logic (deterministic; RAM-centric)
| File | Purpose |
|---|---|
| `Logic/Core/Ram.cs` | 64 KiB big-endian 68k work-RAM model; byte/word/long aliasing, `TestSet/TestClear`. **State lives here.** |
| `Logic/Core/Vars.cs` | Every RAM variable by address (Sonic 2 names). |
| `Logic/Core/SonicObject.cs` | A 64-byte object slot as named fields; `MultiSprite` view. |
| `Logic/Core/Objects.cs` | `RunObjects`, the object registry (hack vs S2 columns), `MarkObjGone`/`DeleteObject`/`FindFreeSlot`. |
| `Logic/Core/ObjectManager.cs` | Spawns objects from the layout as the camera moves. |
| `Logic/Core/ObjectContext.cs`, `ObjectKit.cs`, `ObjectArt.cs`, `SpriteBuilder.cs`, `SpriteData.cs`, `MdMath.cs`, `Flags.cs`, `Ids.cs`, `Ram.cs`, `Sound.cs`, `UnportedException.cs` | Context, shared routines, art/mappings, SAT builder, sine/arctan, bit constants, ids, sound queue, "not ported" exception. |
| `Logic/Level/World.cs` | One act: RAM setup (`LevelDataLoad`/`LevelSizeLoad`/`InitPlayers`), the per-frame **level loop order**, title card, palette cycle, signpost, sound. |
| `Logic/Level/Camera.cs` (673 ln) | `ScrollHoriz/Verti`, `DeformBgLayer`, `DynamicLevelEvents`, per-zone background scroll. |
| `Logic/Level/Collision.cs` | Terrain probes (`AnglePos`, floor/wall). |
| `Logic/Level/Solid.cs`, `TouchResponse.cs` | Solid objects/platforms; collision responses (rings/hurt/kill/points). |
| `Logic/Level/RingsManager.cs`, `Hud.cs`, `LavaLine.cs`, `Oscillation.cs`, `PaletteFade.cs`, `PlcQueue.cs` | Rings window, HUD, lava line, oscillation, fades, PLC queue. |
| `Logic/Player/Player.cs` (1155 ln), `PlayerTails.cs`, `SonicAnimation.cs`, `TailsAnimation.cs` | Sonic physics/animation, Tails (physics + CPU follower), animation scripts. |
| `Logic/Objects/{Common,GreenHill,MarbleZone,EmeraldHill,Smb3}/*` (~115 files) | One file per object type: rings, monitors, springs, enemies, bosses, SMB3 objects (goomba/koopa/plants/boomerang/microgoomba/goal roulette/…). |

### 4.3 Frontend / screens
`GameFlow.cs` (title→map→level→map; lives/score/cards), `Sb3TitleScreen.cs`, `TitleScreen.cs`, `LevelSelectScreen.cs`,
`WorldMapScreen.cs`, `MatchGameScreen.cs` (N-Spade card game), `Screen.cs`, `SpriteKit.cs`, `MiniSonicArt.cs`.

### 4.4 SMB3 presentation
`Smb3/NesPpu.cs` (nametables, palette RAM, `Video_Upd_Table` streams), `NesPresenter.cs` (NES (tile,palette)→MD tile,
margin fill), `Smb3Assets.cs` (copies 7 PRG + 31 CHR banks and the bonus room into `sb3/`), `StatusBar.cs`,
`SonicBrosData.cs` (build entry point).

### 4.5 Audio
`Audio/Smps68kDriver.cs` (1133 ln; ported 68k SMPS, RAM at `$F000`), `SmpsZ80Driver.cs` (1400 ln; Sonic 2 Z80 driver
re-implemented with 8 KiB RAM + self-modifying operands), `Ym2612.cs`, `Sn76489.cs`, `SoundMixer.cs`.
The driver's RAM is compared byte-for-byte against MAME in validation.

## 5. C# — `src/SonicBros3.Browser` (platform shell)

`Program.cs`: `[JSExport] Load/LoadCampaign/Step/Render/Sound/Describe/Campaign`. Writes the archive to an in-memory
FS, steps one 60 Hz frame, returns RGBA + 800 stereo PCM samples per frame. `main.mjs` is a stub. The `.csproj`
compiles all of `src/SonicBros3` **except** `Program.cs`/`S2Game.cs`.

## 6. Node.js — `web/` (server / build glue / conversion / browser UI)

| File | Purpose |
|---|---|
| `server.mjs` | Allowlisted static server + `/api/game` (metadata) + `/api/assets` (the archive) + `/_framework/*` (WASM) + `/healthz`. CSP, GET/HEAD only. Picks `web-data/campaign.bin` if present else `sonicbros3.bin`. |
| `build.mjs` | `ensureBrowserBuild`: `dotnet publish` the browser project into `web/runtime` when sources are newer. |
| `roms.mjs` | Finds the stock Sonic 2 `.md` (header + SHA-1 gate) and `smb3.nes`; `NesRom` reader. |
| `catalog.mjs` | Discovers worlds/areas from the SMB3 ROM; per-area blockers; writes `catalog/report/backlog`. |
| `assets.mjs` | Stock-ROM conversions used only for browser **metadata/preview**: level cells, metatile atlases, enemy sprite sheets, Sonic/Tails sprite sheets, world map. **Not used for gameplay.** |
| `cpu6502.mjs` | A second 6502 (JS) mirroring the C# one, used by the catalog to run level generators. |
| `png.mjs` | PNG encode. |
| `convert.mjs` | CLI: `npm run convert`. |
| `public/app.mjs` | Browser host: loads WASM + archive, 60 Hz accumulator, Canvas output, WebAudio, gamepad, campaign browser. |
| `public/index.html`, `style.css` | UI shell. |
| `test/web.test.mjs` | Node tests for conversion/catalog/server. |

## 7. Tools

| Tool | Purpose |
|---|---|
| `tools/Extract` | `dotnet run --project tools/Extract -- rom.bin out.bin` — engine extraction only. |
| `tools/ConvertSmb3` | Full build / campaign batch; also validates each native area by loading it 180 frames headless. |
| `tools/Validate` | `smb3-level`, `smb3-level-6502`, `smb3-music` checks. |
| `tools/RefEmu` | MAME harness + Lua + input scripts (`nes.sh` runs SMB3 in MAME). |
| `tools/Disasm/m68k.py` | Capstone-based 68k helpers (obj/dis/table/bytes) for `rom.bin`. |

## 8. End-to-end data flow

```
rom.bin (Sonic 2 hack) ─┐
                        ├─ Extractor (Kosinski/Nemesis/Enigma/Saxman, ROM tables)
                        │     └─ engine assets: zones, collision, PLC art, audio, sprites, HUD, frontend
smb3.nes ───────────────┼─ Smb3Assets (copy PRG/CHR banks + run 6502 for the bonus room → sb3/)
                        ├─ Smb3ZoneBuilder × {1-1,1-2,1-3}  (zones $40/$41/$42)
                        └─ Smb3Conversion (whole-ROM import) ───► sonicbros3.bin (ZIP)
                                     ▲
                                     └─ Smb3Campaign (+ offline 6502) ───► web-data/campaign.bin (ZIP)

game run-time:  sonicbros3.bin  ──►  World/LevelSession  ──►  Vdp / SoundMixer  ──►  screen + audio
browser:        sonicbros3.bin + WASM(SonicBros3) ──► app.mjs
```

## 9. Classification

| Kind | Where |
|---|---|
| **Runtime engine (must be ported)** | `src/SonicBros3/Runtime`, `Logic`, `Frontend`, `Smb3`, `Audio` |
| **Build / asset-extraction tooling (must be ported to run at run time)** | `src/S2Port.Extraction` (+ `Smb3`), `tools/Extract`, `tools/ConvertSmb3`, `web/roms.mjs`, `web/catalog.mjs`, `web/cpu6502.mjs` |
| **Web server / UI shell** | `web/server.mjs`, `web/public/*`, `web/build.mjs`, `src/SonicBros3.Browser` |
| **Dev-only (not shipped; keep as reference)** | `tools/RefEmu`, `tools/Disasm`, `tools/Validate`, `web/assets.mjs` (browser preview only) |
| **Tests** | `tests/SonicBros3.Tests`, `web/test` |
| **Dead/stale** | `data.bin`, `trainer-runs/`, `Super Mario Bros. 3 (USA).zip`, `SonicBros3.Browser/main.mjs` |

## 10. Runtime model (for the port)

* Fixed **60 Hz**; `World.Step` order is documented in `World.cs` (RunObjects → camera/deform → rings → PLC → palette
  cycle → animations → sprites → object manager → VBlank HUD/PLC/sound). This **order must be preserved exactly**.
* All game state is in a **64 KiB big-endian RAM byte array** addressed by low 16 bits; byte/word/long aliasing is
  load-bearing. This maps cleanly to a Rust `[u8; 0x10000]` + accessor helpers.
* Rendering is `320×224` **CRAM indices** + two 64-word palettes (mid-screen split), sprites from the RAM SAT.
* Audio = ported SMPS players writing to modelled YM2612/PSG/DAC; one 800-sample stereo block per frame at 48 kHz.
* **No RNG used by name**: the original's `RandomNumber` (0x408C) reads `Vint_runcount` low byte; keep it.
* "Not ported" paths throw `UnportedException` (session stops with a message). The port must reproduce this.

## 11. Discrepancies between the brief and the repo (decisions needed)

1. **Not a git repository.** `git rev-parse` fails; there is no `.git`, so `legacy/`, `.gitignore` and CI have nothing
   to attach to. (`.gitignore`/`.dockerignore` exist as files only.)
2. **The engine's ROM is `rom.bin` = a *modified* Sonic 2 (hack)**, not the stock `…(Rev A).md`. The stock `.md` is used
   only by the Node catalog. Confirm the port's required user files are `rom.bin` + `smb3.nes`.
3. **The engine reads a derived archive, not ROMs.** Rebuilding that archive is a large part of the codebase
   (the offline 6502, SMB3 decoders, zone builder, music conversion). Decision Q3 below.
4. **First run mutates `sonicbros3.bin`.** I ran the game once and it rebuilt the archive because it was out of date
   vs `Extractor.Version` (SHA-1 `E64D7F8D…` → `DACE0F36…`, 5,142,065 → 5,160,314 bytes). The 136 tests still pass.
   This means the "oracle" binary depends on the *build* of a derived file; for golden traces we must pin the archive
   (or regenerate it deterministically and hash it).
5. **"Some levels have no enemies"** is not a coding bug but a design state: campaign areas are `native-partial`; object
   ids without a `Smb3ObjectConverter.Rules` mapping are reported in `campaign/backlog.json`, and unported ids
   despawn via `MarkObjGone`. There are *also* genuine bugs to preserve (Q in §12).

## 12. Decisions (answered 2026-10-19)

* **Git**: use `https://github.com/louisvarley/super-sonic-brothers-3` (was empty). Baseline committed and pushed;
  `legacy/` now holds the old implementation.
* **ROMs**: the shipped build must **not** read ROMs at run time. A build/first-run step extracts what it needs into
  `data.bin` (ZIP); the game loads that. Target source: **stock Sonic 2** + **stock SMB3**; `rom.bin` is a leftover.
* **Extraction model**: extract at build/first-run, then load `data.bin`.
* **Scope**: port everything — engine, SMB3 frontend, both sound drivers, YM2612/PSG/DAC.
* **Toolchain**: rustup installed; Docker intentionally skipped (Phase 3 Docker will be written but not verified here).
* **Traces**: hash the RGBA framebuffer; `data.bin` and `trainer-runs/` are disposable.
* **Asset strategy**: two workstreams. (1) Port the engine + existing importer and prove tick-equivalence against a
  pinned local derived archive (never committed/shipped). (2) Replace the importer with a stock-ROM importer and
  document unavoidable deviations (no Green Hill/Marble; substitute collision/audio).
* **Green Hill / Marble**: dropped from the stock build (hack-only; only reachable from the unused Sonic
  level-select screen).
* **`data.bin` production**: auto-extract on first run when ROMs are beside the exe or passed by CLI, plus an explicit
  extract command. Release zips contain no `data.bin`.

## 13. Proposed Rust workspace (approval wanted)

```
Cargo.toml (workspace)
crates/
  golden-trace/    # test-support: reads .sb3g traces, reports the first divergent tick/field (done, Phase 1)
  engine-core/     # no I/O, no_std-friendly: Ram/Vars/objects, player, camera, collision, level systems,
                   # SMB3 screens, software VDP framebuffer, SMPS players + YM2612/PSG/DAC models.
                   # Assets enter through a plain data struct (no file access).
  rom-assets/      # ROM loading; Kosinski/Nemesis/Enigma/Saxman; S2 extractor; offline 6502;
                   # SMB3 catalog/decoder/zone-builder/conversion/campaign; archive (zip) read/write;
                   # indexed-PNG read. Ported 1:1 from src/S2Port.Extraction + Smb3.
  platform-native/ # bin: CLI (ROM flags), winit window + cpal audio, static CRT; headless runner
                   # (--trace/--screenshot/--inputs) used to generate golden traces.
  platform-web/    # cdylib: wasm-bindgen exports mirroring the Browser bridge.
xtask/             # golden-trace generation, legacy harness invocation, ROM validation
tools/golden/      # Phase 1 generator (done)
tests/golden/      # committed .sb3g.gz traces + manifest + inputs (done)
legacy/            # the current Node+C# implementation, kept as the oracle (moved; build/tests verified)
docs/              # INVENTORY.md, PORT_LOG.md, KNOWN_ISSUES.md, plus the existing docs
```

Notes: one `GameState` passed as `&mut`; entities by slot index into the RAM array (as today); fixed timestep;
`engine-core` deterministic and platform-free so native and WASM share it. `legacy/` also gets the additive
**headless trace mode** that Phase 1 needs (no behaviour change to gameplay).

## 14. How to build / run today (verified)

```powershell
dotnet build SonicBros3.slnx                     # OK (2 nullable warnings)
dotnet test tests/SonicBros3.Tests                # 136 passed
dotnet run --project src/SonicBros3               # play (needs sonicbros3.bin; builds it if stale)
dotnet run --project src/SonicBros3 -- --start level --level 1-1 --screenshot out.png --frames 120 --trace
npm start                                          # web UI at http://127.0.0.1:3000 (publishes WASM with .NET)
npm test                                           # Node tests
npm run campaign                                   # build web-data/campaign.bin
```
