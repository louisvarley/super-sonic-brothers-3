# Port log

Running record of decisions, discrepancies and open items for the Sonic Bros. 3 → Rust port.
Newest entries at the bottom. Phase 0 = inventory only; no port code written yet.

## 2026-10-19 — Phase 0 started

### What I did
Read the repo (docs, C# projects, Node pipeline, tools, tests), built it, ran it headless, ran the test suite.

### Verified facts (commands actually run)
* `dotnet build SonicBros3.slnx` → **succeeds**, 2 nullable warnings (`Runtime/AnimatedTiles.cs:71`).
* `dotnet test tests/SonicBros3.Tests` → **136 passed / 0 failed** (36 s).
* `dotnet run --project src/SonicBros3 -- --start level --level 1-1 --screenshot … --frames 120 --trace` → **runs**,
  writes a PNG (2182 bytes). Phase at frame 120 was `CardWait` (title card still up), as expected.
* ROM SHA-1s confirmed: `rom.bin`=6651…, stock `.md`=8bca…, `smb3.nes`=a03e….

### Findings / discrepancies
1. **Not a git repository** (`.git` absent) despite `.gitignore`/`.dockerignore` existing. Blocks the `legacy/`,
   `.gitignore` and CI requirements until decided. → Q1.
2. **The engine reads a derived archive, not the ROMs.** `World`/`LevelSession` read `sonicbros3.bin` (ZIP); the ROM is
   only read by the extractor/build. So "load ROMs at run time" ⇒ port the extraction pipeline too. → Q3.
3. **The engine's ROM is a modified Sonic 2 (`rom.bin`), not the stock `.md`.** The stock `.md` is used only by the Node
   catalog (browser metadata). → Q2.
4. **First run mutated `sonicbros3.bin`** (stale vs `Extractor.Version`): SHA-1 `E64D7F8D…`→`DACE0F36…`, size
   5,142,065→5,160,314 bytes, mtime updated. Tests still pass. The archive is therefore a *build artifact*, not a
   fixed oracle input; golden traces must pin it (regenerate deterministically and record its hash).
5. `data.bin` (root) is a stale older extraction; no code references it. `trainer-runs/` and the SMB3 zip are
   unreferenced. → Q7.
6. The C# code documents extensive MAME co-simulation validation (per-iteration whole-RAM compare). That is the
   strongest existing equivalence evidence and should inform the Rust golden traces.

### Decisions taken (pending user approval)
* Phase 0 deliverable = `docs/INVENTORY.md` + this log.
* Proposed Rust workspace in `INVENTORY.md` §13.

### Questions raised (see INVENTORY §12)
Q1 git init? · Q2 required ROMs? · Q3 extraction scope (full pipeline vs read existing archive first)? ·
Q4 trace granularity · Q5 frontend+audio in scope? · Q6 install rustup/Docker? · Q7 stale files disposable?

### Next (after approval)
Phase 1: add a deterministic headless trace mode to the legacy engine (additive; no gameplay change), record golden
traces for every level with several input scripts, commit hashes/state dumps under `tests/golden/`.

## 2026-10-19 — Toolchain probe
`dotnet 10.0.401` ✅ · `node v24.20.0` / `npm 11.19.0` ✅ · `git 2.55.0` ✅ · **`cargo`/`rustc` missing** ❌ ·
**`docker` missing** ❌. Phase 2 needs a Rust toolchain; Phase 3's Docker verification needs Docker. → Q6.

## 2026-10-19 — User answers + git baseline

Answered: use https://github.com/louisvarley/super-sonic-brothers-3 (it was empty); **do not load ROMs at run time** —
extract at build/first-run into `data.bin` (ZIP), then load that; use **stock Sonic 2 + stock SMB3**, `rom.bin` is an
old artifact; port **everything** (frontend + audio); install **rustup only**; `data.bin`/`trainer-runs` may go.

Done:
* `git init -b main`, `origin` = the GitHub repo, baseline commit `Phase 0: inventory, port log, ROM guard, git baseline`
  (227 files). **Pushed** `main` (empty repo → new branch).
* `.gitignore` hardened (ROMs: `*.nes/smd/gen/32x/sms/gg/sfc/fig/swc/rom/bin/zip`; top-level `*.md` except
  README/AGENTS; `extracted/`, `data.bin`, `sonicbros3.bin`, `web-data/`, `web/runtime/`, `trainer-runs/`).
* `.github/workflows/ci.yml` `no-roms` job fails if any ROM/derived asset is tracked. Verified locally before commit:
  0 tracked ROM/derived files.

### Blocking conflict raised by the stock-ROM answer
The current game's SMB3 campaign still depends on assets that exist **only in the modified Sonic 2 (`rom.bin`)**:
* `zones/zone_40/collision.json` points at `collision/alternate.json` (the hack's collision set; SMB3 slopes/ramps
  were mapped to its ids `$99/$9C/$9D/$9B/$9E/$9F`).
* `plc/plc_lists.json` + `art/nemesis/*` supply object/HUD/ring art (Nemesis-compressed) used by the SMB3 zones.
* `audio/smps68k/player.json` + bank tables are the 68k SMPS driver's constants/SFX; the converted SMB3 songs play
  through that driver.
* Green Hill (`$11/$12`) and Marble Zone (`$13/$14`) are hack-only; stock Sonic 2 has neither.
* `palettes/pal_03.json` and Sonic/Tails/rings/HUD art are Sonic-2-derived and *can* be re-sourced from the stock
  `.md`, but at different addresses.

So "stock-only" and "reproduce the legacy traces tick-for-tick" cannot both hold for asset-dependent content. Raised
as Q8/Q9/Q10 (below); awaiting the user's choice before Phase 1.

### Recommendation
Split the work: (1) port the engine + existing importer and prove tick-equivalence against a **pinned local archive**
(`sonicbros3.bin`/`data.bin`, gitignored, never shipped) — this is the actual port; (2) then replace the importer with
a stock-ROM importer and document the unavoidable asset deviations. This keeps "port, not redesign" honest and still
delivers the stock pipeline the repo's README calls unfinished.

## 2026-10-19 — Open questions after baseline
* **Q8 asset strategy**: (A) two workstreams as recommended; (B) stock-only from day one (engine equivalence proven on
  fixture bytes only; SMB3 slopes/audio differ); (C) keep hack assets (exact behaviour, contradicts stock request).
* **Q9 Green Hill / Marble Zone**: drop from the stock build (they are reachable only via the currently-unused Sonic
  level-select screen), or import Sonic 1 content for them?
* **Q10 `data.bin` production**: auto-extract on first run when ROMs are found (as today), and/or an explicit
  `extract` command? Release zips must not contain `data.bin`.

## 2026-10-19 — Phase 1: deterministic legacy traces + Rust reader

### Added to the legacy engine (additive; gameplay untouched)
* `legacy/src/SonicBros3/Runtime/GoldenTrace.cs`: deterministic trace writer.
* `legacy/src/SonicBros3/Program.cs`: `--golden`, `--scenario`, `--hash-every`, `--snapshot-every`,
  `--zone`/`--act`, `--campaign-act`, `--world`, `--no-build`, `--list-campaign`.
* `--golden` forces `chips = null` (no sound driver) so the recorded 64 KiB RAM is deterministic. `--screenshot` and
  interactive runs are unchanged.
* `tools/golden/generate.mjs`: scenario list + generator. `tools/golden/README.md` documents the flags.
* `tests/golden/README.md`: trace format, scenario list, provenance, regeneration.

### Trace format (`.sb3g`, gzipped in git)
`"SB3GOLD1"` + JSON header + fixed 134-byte tick records + periodic JSON snapshots + end record. Per tick: phase,
zone/act, camera, Sonic/Tails (12 fields each), rings/score/timer/lives/gameMode, a **whole-RAM hash**, six region
hashes (`objects`, `level`, `camera`, `rings`, `sound`, `hud`) and an **RGBA framebuffer hash every 30 ticks**.
Snapshots every 60 ticks carry the occupied object slots and the full RAM SHA-256.

### Generated (core)
* **52 traces**: SMB3 1-1/1-2/1-3 + Sonic `$00` acts 1–2, `$11` acts 1–2, `$12` act 1, `$13` acts 1–2, `$14` act 1;
  `idle`, `right`, `jump`, `spindash`; plus 1-1 goal/death/pipe, 1-2 hill/goal/goomba-pipe/pipe, 1-3 goal.
* **2.52 MiB gzipped** (10.98 MiB raw). Core levels: 1200 ticks; specials 500–2400.
* Determinism verified: same scenario + same archive → identical SHA-256.
* Framebuffer hashes: low 64 bits of SHA-256 over `Vdp.ToRgba` output at ticks 30, 60, ….
* Campaign set generating every non-blocked area (`idle` + `right`, 420 ticks).

### Rust side
* Workspace `Cargo.toml`; `crates/golden-trace` (gzip + format parser + field-by-field comparator).
* `cargo test -p golden-trace`: **2 passed** — every committed trace parses, matches its manifest SHA-256, has the
  recorded tick count and hash cadence; the divergence reporter names `sonic.x`, `regions.objects`, `tickCount`.
* Toolchain: Rust 1.99.1 installed (rustup, minimal). Docker still absent (Phase 3 verification pending).

### Decisions recorded
* The oracle is the **pinned derived archive** (`sonicbros3.bin` / campaign), not `rom.bin`. The port's shipped
  stock-ROM importer is workstream 2; its output will differ in the documented ways (no Green Hill/Marble, substitute
  collision/audio). This keeps the engine port provable while honouring the stock-ROM goal.
* Audio is not in the bit-exact traces (floating-point chips). Separate non-bit-exact audio tests will follow.

## 2026-10-19 — Phase 1 final set (verified)

* **571 traces / 286,400 ticks / 11.72 MiB gzipped**, all committed under `tests/golden/` with `manifest.json`.
  * 510 campaign traces (every non-blocked area, `idle` + `right`, 420 ticks).
  * 34 Sonic-zone traces (`$00` acts 1–2, `$11` acts 1–2, `$12` act 1, `$13` acts 1–2, `$14` act 1;
    `idle`/`right`/`jump`/`spindash`, plus EHZ act 2 and MZ act 3 boss starts).
  * 23 SMB3 traces (1-1/1-2/1-3, goal/death/hill/goomba-pipe, pipe entry and pipe-jump).
  * 4 frontend traces (title, title→map, map, card game).
* All level traces reach `Playing`. Pipe traces show the session switching to the alternate area (act 30 / 27);
  goal traces show `FadeOut` → map.
* `cargo test -p golden-trace` on the full set: **2 passed** (6.6 s); `cargo fmt --check` and
  `cargo clippy --workspace --all-targets -- -D warnings` clean.
* Determinism re-checked with `--verify`: core 1/1 and frontend 4/4 reproduce byte-identically.
* `tools/golden/{inspect,summarize}.mjs` and `cargo run -p golden-trace --bin compare -- a b` for diagnosis.
* Docs split: the legacy design notes moved to `legacy/docs/`; current docs are `docs/{INVENTORY,PORT_LOG,KNOWN_ISSUES,README}.md`.

**Phase 1 is complete. No Rust engine code has been written yet.**

## 2026-10-20 — Phase 2 M1: engine-core primitives (verified)

New crate `crates/engine-core` (no dependencies, no I/O):
* `Ram`: 64 KiB big-endian byte image; `u8/s8/u16/s16/u32/s32`, byte/word/long aliasing, `bset`/`bclr`, 16-bit
  address wrap. Tests: big-endian aliasing, signed reads, wrap at `$FFFF`, bit ops, dump length.
* `vars`: every address from the legacy `Vars.cs`, in `SCREAMING_SNAKE_CASE`; pinned by tests (slot layout, known
  addresses).
* `object::Obj`: named window on a 64-byte slot (all standard/player fields, status bits, object-specific
  byte/word/long accessors, `slot_index`/`from_slot`, `clear`). Tests: offsets, `$2E` byte/word aliasing, status bits,
  slot index.
* `math`: the **exact** sine/arctan tables embedded (`--dump-math` exported them from the legacy engine), so no libm
  drift. `calc_sine`/`calc_angle` ported with wrapping semantics.
  * Quirk confirmed: `calc_angle(-1,-1)` is **`$A0`**, not `$E0` (the two negations are applied in sequence); test
    pins it.

`cargo test -p engine-core`: **14 passed**. `cargo fmt`/`clippy -D warnings` clean.

## 2026-10-20 — Phase 2 M2: archive + level loader (verified)

New crate `crates/rom-assets`:
* `Archive`: reads the legacy derived ZIP (the engine's `sonicbros3.bin`/`data.bin`), forward-slash entry names,
  `..` resolution, BOM-tolerant JSON.
* `png::read_indexed` (8-bit indexed PNGs) + `tiles::from_sheet` (palette indices → raw 4bpp).
* `level::LevelData::load`: tiles, blocks, chunks, layout, palettes, collision indexes, bounds/start, objects, rings,
  gameplay/music metadata. Bit unpacking matches `LevelJson`.
* `LevelData::fill_level_ram`: the level-data part of the legacy `World` setup (chunks `$0000`, layout `$8000`,
  blocks `$9000`, collision `$D600/$D900`, palette `$FB00`, zone/act `$FE10`, hack flag `$FFE6`).
* Legacy CLI additions (read-only): `--level-ram-hash` (region SHA-256), `--dump-math`.

Oracle fixture `tests/golden/level-ram.json`: region hashes for 1-1/1-2/1-3 generated with `--level-ram-hash`.

`cargo test -p rom-assets`: **4 passed**. The Rust loader reproduces the legacy chunks/layout/blocks/collision/palette
**byte for byte** for zones `$40/$41/$42` (`whole` and composed-VRAM tile count wait for the full `World`/PLC port).
`cargo test --workspace`: 14 + 2 + 4 passed; fmt/clippy clean.

Next: M3 — player physics, collision probes, camera, rings, HUD, PLC queue, then the first divergence-free 1-1 replay.

## 2026-10-20 — Phase 2 M3a: collision probes + isolated 1-1 traces (verified)

* `engine-core::collision`: `find_tile`, `find_floor`/`find_floor2`, `ring_find_floor`, `find_wall`/`find_wall2`,
  `select_layer`, paired `check_floor`/`check_ceiling`/`check_*_wall`, the single-distance probes, `calc_room_*` and
  the Rev01 `rounded_quadrant`. Tests pin quadrant rounding (note `$3F` rounds to `$40`), the layout cell addressing
  (a new cell row every 128 px, not every 16), and layer selection.
* `rom-assets::load_collision_sets`: parses `collision/{normal,alternate}.json` (256 angles, 256x16 heights/widths);
  test loads both from the local archive.
* Added `--no-title-card` to the legacy raw-session path and four **title-card-free** 1-1 traces
  (`smb3-1-1-playing-{idle,right,jump,spindash}`, 600 ticks each). These start at `Playing` on tick 1 so player/camera
  physics can be compared before the title-card/PLC/HUD work.
* `cargo test --workspace`: 17 (engine-core) + 2 (golden-trace) + 5 (rom-assets) passed; fmt/clippy clean.
* Legacy build still green; the trace set is now 575 files.

Next: port `Player` control/ground/roll/jump/air + camera scroll, then compare the four `playing-*` traces field by
field (Sonic/camera first, Tails after `PlayerTails`).

## 2026-10-20 — Phase 2 M3b: player physics + camera motion (verified; object contacts are the M4 frontier)

New `engine-core` modules, ported routine for routine from `Player.cs` / `SonicAnimation.cs` / `Camera.cs`:
* `player.rs`: `Obj01_Init/Control/Dead/Gone/Hurt`, `MdNormal`, `MdRoll`, `MdAir`, `Move`/`MoveLeft`/`MoveRight`,
  `Skid`, `RollSpeed`/`Roll`/`RollLeft`/`RollRight`, `Jump`/`CheckSpindash`, `SlopeResist`/`RollRepel`/`SlopeRepel`,
  `LevelBound`, `ObjectMove`/`ObjectMoveAndFall`, `JumpHeight`/`ChgJumpDir`/`JumpAngle`, `DoLevelCollision` and the
  air responses, `ResetOnFloor`, `AnglePos`/`StickOrFall`/`PickAngle`, `RecordPos`, water. Side effects owned by later
  milestones are explicit no-ops: sprites, DPLC/VRAM, sound, `TouchResponse`/`Solid`, the game-over object slots.
* `animation.rs`: `Sonic_Animate` from the raw extracted block and the normal/super script tables (walk/run, roll,
  push, flip, script commands), because the timers and the pushing bit feed physics and `anim`/`frame` are compared.
* `camera.rs`: `ScrollHoriz`/`ScrollVerti` (including the signed `(short)` deltas, the $10 dead zone, the bias-driven
  speeds, the 16.16 `Camera_Y_Pos` clamp/wrap and the `ror.l #8` diff), the scroll-flag toggles, the
  `Camera_Max_Y` easing, and `SwScrlEasterEgg` (`$40-$43`: flat plane B, 256 scroll-buffer lines).
* `world.rs`: `LevelSource` (implemented by `rom_assets::LevelData`), `LevelDataLoad` via `fill_level_ram`,
  `LevelSizeLoad` (bounds, bias, start, camera), `InitPlayers` positioning, restart carry-over
  (`$FE00-$FE5F`, `$FEB0-$FFFF`, timer/ring reset), `InitCameraValues`, `MainCharacter=1`, `GameMode=$0C`,
  `LevelStartedFlag=1`, `ControlLocked=0`, normal speeds, `Primary/Secondary_Angle`, `Collision_addr`.
  Stubs (documented): Tails, object manager, rings manager, HUD digits, PLCs, sprites, SMPS, title card.
* `session.rs`: one `Playing` frame in `World.Step` order — pad held/pressed, `Level_frame_counter++`, `Sonic.Step`,
  `DeformBgLayer`, `Vint_runcount++`, the HUD's level timer — plus the no-title-card restart (a death request rebuilds
  the act from the previous RAM in the same tick, as `LevelSession.LoadNext` does).
* `rom-assets`: `load_sonic_animation`; `LevelData` now implements `engine_core::world::LevelSource` (including the
  SMB3 water map).

Proof (`crates/engine-core/tests/player_motion.rs`, 600 ticks each, `CompareMask::MOTION`):
* `idle`: every compared field matches to **tick 367**; tick 368 diverges at `sonic.yv` when a walking Goomba reaches
  Sonic and `KillCharacter` fires (-1792 vs 0).
* `right`: matches to **tick 78**; tick 79 diverges at `sonic.xv` on the same Goomba contact (0 vs 948).
* `jump`: matches to **tick 85**; tick 86 diverges at `cameraX` when Sonic is pushed out of a solid SMB3 block's side
  (`Solid.Side` zeroes `xv`/`inertia`).
* `spindash`: **player + camera match for all 600 ticks**; the only MOTION divergence is `score` at tick 70 (a rolled-
  into brick block breaks and awards points). This is the first full-trace player/camera equivalence.

The four pinned divergences are all dynamic-object effects (M4: object manager, `Solid`, `TouchResponse`). The test
pins them and prints the surrounding ticks; it must be tightened to a strict full-MOTION comparison when M4 lands.

Tests: `cargo test --workspace` -> 39 engine-core unit tests (22 of them added here, pinning the wrapping/rounding/
      clamp quirks above) + 1 `player_motion` + 2 (golden-trace) + 5 (rom-assets) passed. `cargo fmt --check` and
      `cargo clippy --workspace --all-targets -- -D warnings` clean.

Next: M4 objects — first the SMB3 block/goomba (`ObjectManager`, `Solid`, `TouchResponse`) to clear these four traces,
then the remaining SMB3 and Sonic-zone objects.

## 2026-10-20 — Phase 2 M4a: 1-1 objects, Tails and the four traces clear (verified)

The four `smb3-1-1-playing-*` traces now match `CompareMask::MOTION` for all 600 ticks with **no pinned
allowances** (the test asserts `compare_masked(...).is_none()` for every scenario).

### Added
* `engine-core::objects`:
  * `manager.rs`: `ObjectsManager` (0x1B824) — the 6-byte placement-list image with the leading boundary record and
    terminator, right/left pointers at `$F770`/`$F774`, `Init`/`Main`/`ChkLoadObj`, the respawn table
    (`$FC00`: 16-bit counters plus one byte per "remember" record whose bit 7 means destroyed) and the 128 px
    camera window (`camera + $280`, `camera - $80`).
  * `mod.rs`: `RunObjects`' slot loop (`$B080-$D3FF`, `SlotCounter`, the "Sonic dying" freeze of the dynamic slots),
    the ported-id registry, `MarkObjGone`/`DeleteObject`, `FindFreeSlot`/`FindFreeSlot2`, `ObjectMove(AndFall)`.
  * `solid.rs`: `SolidObject`/`SolidObject_Always`, `StillOn`, the box overlap, side/top/bottom responses,
    `SetRide`/`MoveOnPlatform`, including the wrapped-16-bit `d0` and the contact bits at 16-21.
  * `touch.rs`: `TouchResponse` (`TouchRings`, enemy chain/points, hurt/kill, monitors, specials), `CollectRingFor`
    (100/200-ring extra lives) and `AddPoints` (50000-point extra life).
  * `sprites.rs`: `DisplaySprite`'s priority lists and `BuildSprites`' per-object range test, which is what sets the
    on-screen flag object code reads (drawing itself is still M5).
  * `smb3.rs`: `$E0` block (?/brick, bump, contents, break), `$E1/$E2` Goomba/Paragoomba (walk, block floors, hops,
    micro-goomba flyer), `$E3/$E4` Koopa/Paratroopa (shell/kick/sliding knock-outs), `$E5` piranha (hidden/rise/out/
    sink, 27 px check, fire timing) and `$E6` fireball.
  * `common.rs`: spilled rings `$37`, explosion `$27`, animal `$28`, points `$29`.
* `engine-core::rings`: `RingsManager` (0x1AD00) — group expansion + the ROM's exchange sort, the `$E800` table, the
  sliding collect window, sparkle timers — and `SyncAnimations` (0x5EB0).
* Tails (needed because the traces' `rings`/`score` change on *his* contacts): `TailsAnimation`
  (`objects/tails/animations.json`), `Player::tails()`/`TailsStep`/`TailsControl`, his balance/record/water/hurt/
  dead/respawn routines, and the full `TailsCPU_Control` state machine (Nakama follow using Sonic's 16-frame position
  and input history, respawn/fly-in, panic spindash).
* `LevelSource` now also exposes the object placements, list address/boundary and ring groups; `rom-assets` converts
  them and loads Tails' animation block.

### Wiring
`World` runs `Rings.Setup`, the first `ObjectManager.Run` and `Rings.Run` after placing the players. `Session::step`
now runs Sonic, Tails, `RunObjects` (with the pre-frame dying flag), `DeformBgLayer`, `Rings.Run`,
`SyncAnimations`, `BuildSprites`, `ObjectManager.Run`, then VBlank (`Vint_runcount`, HUD timer). A restart rebuilds
the act (and its object/ring tables) from the previous RAM.

### Results (`cargo test -p engine-core --test player_motion -- --nocapture`)
* `idle`, `right`, `jump`, `spindash`: **MOTION matches for all 600 ticks** (Sonic + camera + rings/score/lives).
  The pure player/camera part of these tests is unchanged; the object layer now reproduces the Goomba kills, the
  block side/coin hits, Tails' coin-block ring and piranha kill, the hurt ring spill and the level restarts.

### Tests
`cargo test --workspace`: 60 engine-core unit tests (21 new: object manager window/remember records, dying freeze,
MarkObjGone, solid side/top/bottom contacts, hurt/kill/points/100th ring, coin block/bump, Goomba facing, piranha
proximity, ring table/window, Tails history/jump cadence) + 1 `player_motion` + 2 golden-trace + 5 rom-assets.
`cargo fmt --check` and `cargo clippy --workspace --all-targets -- -D warnings` clean.

Next: the remaining 1-1 content (Koopa/shell edge cases are ported but only smoke-tested), then the SMB3 1-2/1-3
objects (pipe, goomba pipe, micro goomba, boomerang, roulette goal) and the Sonic-zone object set.

## 2026-10-20 — Phase 2 M4b: SMB3 World 1-2 and 1-3 clear (verified)

### New traces (generator, committed)
`tools/golden/generate.mjs` now also records `smb3-1-2-playing-{idle,right,jump,spindash}` (zone `$41`) and the same
four for `smb3-1-3` (`$42`): 600 ticks, `noTitleCard: true`, scripts `idle`/`right0`/`jump0`/`spindash0`. Generated
with `node tools/golden/generate.mjs --only smb3-1-2-playing` and `--only smb3-1-3-playing`; the manifest merges the
eight new entries (only `legacyCommit` and the new entries changed; no existing trace bytes touched).

### Ported (1-2/1-3 content)
* `$E8` Goomba pipe: `$70`-frame spawn timer, the 3-in-4 counter, the "Sonic to the right" test and the emerging
  Goomba (`Emerge = $28`, facing right); invisible, despawns with `MarkObjGone`.
* `$E9` micro goomba: swaying fall (1/16 px x acceleration, ±1 px, 1 px fall cap), the cling (Grip grows every 16
  frames to 5, a button press loosens it), Sonic's ground speed clamped to 3 px while stuck, bobbing, flop-off, and
  the 100-point roll/spindash knock-out.
* `$EA` Boomerang Brother (walk/stop phase timer, the `$1F` throw counter, pose frames, 256-frame hop) and `$EB` the
  returning boomerang (age, direction deceleration, gravity, owner/respawn restoration, catch, despawn).
* `$0D` goal roulette (SMB3 zones): card cycling every 8 frames, touch test, `UpdateHudTimer = 0`, the time/ring
  score bonus, control lock + right run-off, fly-away/message timers, `$E7` text objects and the `GoalCard` result.
  The art/font/VRAM half is M5; only RAM/behaviour is ported.
* `smb3_levels_motion.rs`: both levels × four scripts compared with `CompareMask::MOTION`; all eight match 600 ticks.

### Two semantic findings
1. **1-2/1-3 never reach the goal within 600 ticks.** 1-2's furthest Sonic is x=341 (camera 181; it stops against a
   pipe wall), and 1-3's is x=493 (camera 333, spindash; the `right` traces die to the early Koopa and restart). The
   goal records are at x=2696 (1-2) and x=2440 (1-3) and are not even spawned (`camera + $280` stays below them), so
   there is no "first goal tick" in this set; the roulette/exit path is ported but remains unexercised until the
   goal-specific traces/1-1 goal run are brought in (M4c).
2. **1-3 spindash reaches the legacy's own unported path at tick 115**: `Solid.Vertical` throws
   `UnportedException(0x1D8E0, "crushed by a solid object (KillCharacter)")`. `LevelSession.StepPlaying` catches it,
   sets `Stopped`, and every later frame is a no-op, so the oracle trace freezes at tick 115 (Sonic's post-step state,
   camera/timer unchanged). The port now returns the same `Unported` from `Solid` and `Session` stores it in
   `Session::stopped`, reproducing the freeze exactly instead of inventing a crush death. (The same applies to any
   future unported path inside a frame.)

### Verification
* `cargo test -p engine-core --test smb3_levels_motion -- --nocapture`: 8/8 traces match all 600 ticks.
* `cargo test -p engine-core --test player_motion`: 1-1's four traces still match all 600 ticks.
* `cargo test --workspace`: 65 engine-core unit tests (5 new: goomba pipe, micro goomba, boomerang throw/catch, goal
  touch/bonus, crush-stop), 1 (1-1 motion), 1 (1-2/1-3 motion), 2 golden-trace, 5 rom-assets.
* `cargo fmt --check` and `cargo clippy --workspace --all-targets -- -D warnings` clean.

Next (M4c): SMB3 1-2/1-3 goal traces and the goal/exit session flow, then the remaining SMB3 ids (pipes are
session-level, roulette text art is M5) and the Sonic-zone object set.



## 2026-10-20 — Decision: full legacy parity retained

The user confirmed all Sonic-zone content stays in the port's equivalence scope (Emerald Hill, Green Hill, Marble Zone),
even though the stock build does not ship Green Hill/Marble. The golden traces for those zones are part of the engine
equivalence gate, not just a reference. The stock importer remains workstream 2 and may simply not generate those
levels.

## 2026-10-20 — Phase 2 M4c-1: Emerald Hill (Sonic 2 zone $00) clear for 600 ticks

### New traces (generator, committed)
`tools/golden/generate.mjs` now records `sonic-ehz-1-playing-{idle,right,jump,spindash}` and the same four for
`sonic-ehz-2` (zone `$00`, acts 1/2): 600 ticks, `noTitleCard: true`, the existing input scripts. Generated with
`node tools/golden/generate.mjs --only sonic-ehz-1-playing` / `--only sonic-ehz-2-playing`; only the eight new entries
and `legacyCommit` changed in the manifest, no existing trace bytes.

### Engine generality
* `World` now reads `GlobalAssets` from the level source: the normal/hack oscillator start values
  (`objects/oscillation.json`), bridge sag/bend tables, spring slope tables, the spiral height/flip tables and the
  title-screen ripple table used by `SwScrlEhz`.
* `Oscillation` (`0x5D26`/`0x5DCC`) is initialised with the world and stepped in `World.Step` order after the rings
  manager, before `PaletteCycle`; platform angles read `Oscillation.Value(6)`.
* `Camera` gained `SwScrlEhz` (`0xDE9C`), `EhzEvents` (`0x108A4`, act 2 boss script) and the zone-$00 branch of
  `InitCameraValues` (`0xDB64`). The SMB3 Easter-egg background is unchanged.
* `PaletteCycle` now runs from the act's `palette_cycle.json` (Emerald Hill's water cycle), in the original frame
  position before `SyncAnimations`.
* The object registry now has two columns: `HackZoneFlag != 0` keeps the SMB3/hack ids exactly as before;
  `HackZoneFlag == 0` dispatches the Sonic 2 column used by Emerald Hill (the `RegisterS2` ids and the shared
  bridge/platform/monitor/spring/plane-switcher handlers). Unported ids in that column still despawn like the original.

### Ported (Emerald Hill and shared Sonic 2 objects)
`Spiral` (horizontal corkscrew ride, data tables loaded), `Waterfall`, `SceneryS2`, `SpikesS2` (upright/sideways/
upside-down, solid + direct hurt), `Buzzer` with its flame and projectile, `Masher`, `Coconuts`/coconut
(`LoadSubObject`, climb/throw/fall), `Bridge` (multi-sprite logs, sag/bend, standing logs), `Platform` (swing/bob,
top-solid, falling/rising variants), `Spring` (up/sideways/down/diagonal, twirl and layer switch), `Monitor` and its
contents ($2E), and `PlaneSwitcher`. `Solid` gained `SolidAlwaysOne`, `PlatformObject`/`ChkYRange` and
`SlopedSolidOne` for these handlers.

### Findings
1. **Sonic's invulnerability gates item pickups for Tails too.** `Touch_ChkValue` reads `ram.Sonic.InvulnerableTime`
   even when Tails touched the item. Using the touching player's timer let Tails collect Sonic's spilled rings during
   Sonic's hurt animation (ticks 329/294 in EHZ 1 jump/spindash); the gate now reads Sonic's timer.
2. **Act 2's boss trigger is at camera x `$2780`**, but the 600-tick traces only reach camera x 965; the boss spawn
   (`$29D0,$426`, 90 frames after the arena locks) and `EggPrison` are therefore not exercised and remain for the
   goal/boss trace milestone. The level-event script itself is ported.

### Verification
* `cargo test -p engine-core --test ehz_motion -- --nocapture`: 8/8 traces match `CompareMask::MOTION` for all 600
  ticks.
* `cargo test --workspace`: 68 engine-core unit tests, 1 EHZ motion, 1 (1-1 motion), 1 (SMB3 1-2/1-3 motion),
  2 golden-trace, 5 rom-assets — all pass.
* `cargo fmt --all` and `cargo clippy --workspace --all-targets -- -D warnings` clean.

Next (M4c-2): goal/signpost/starpost/giant-ring flow and the act-2 boss + EggPrison traces, then the Green Hill/Marble
object sets.

## 2026-10-20 — Phase 2 M4c-2: act completion and Sonic-zone bosses (verified)

### New traces (generator, committed)
`tools/golden/generate.mjs` now records, all `noTitleCard: true`:
* `smb3-1-1-goal-playing`, `smb3-1-2-goal-playing`, `smb3-1-3-goal-playing` (zone/act raw sessions at the existing
  goal debug starts, 1600 ticks, `goal`/`goal12`/`goal13`);
* `sonic-ehz-2-boss-playing` (zone `$00`, act 2, debug `10704,1008`, 2200 ticks, `jump`);
* `sonic-mz-3-boss-playing` (zone `$14`, debug `6656,700`, 2600 ticks, `jump`);
* `sonic-ghz-3-boss-playing` (zone `$12`, debug `10752,640`, 2600 ticks, `jump`).

The SMB3 goal traces must be generated with `--zone`/`--act` rather than `--level`: `Program.cs` only creates a raw
`LevelSession` (which honours `--no-title-card`) when a zone is given; `--level` starts `GameFlow` and ignores it.

### Camera/level setup
* `Camera` gained `GhzEvents`/`Ghz3Events`, `Mz1Events`/`Mz2Events`/`Mz3Events`, `SwScrlGhz` and `SwScrlMz`.
* Debug starts call only the zone's event routine (`Camera.RunLevelEvents`), not the max-Y easing, then set
  `CameraMaxYPos` to the target — matching `World`'s debug-start block. Running full `DynamicLevelEvents` there made
  the first MZ frame snap the camera from y 604 to 528.

### Objects
* EHZ boss `$56` and all its parts (ship, cockpit, ground car, three wheels, drill, propeller), including the
  eight-hit/flash/defeat/fly-away path.
* GHZ boss `$3D`, wrecking ball `$48` (+ chain links/ball), boss explosion `$58`, Buzz Bomber `$22`/missile `$23`.
* MZ boss `$73`, lava ball `$14`, BossFire `$74`, Caterkiller `$78`, grass platform `$2F` (+ its sloped top).
* Shared hack-column platform/spring corrected via the existing EHZ handlers.
* Act-end objects: signpost `$0D` and results `$3A` (time/ring/perfect bonuses, tally, level-order next act),
  star post `$79` (+ checkpoint save/load), giant ring `$4B`/`$DD` and flash `$DE` (special-stage request), egg
  prison `$3E` (+ prison animals) and game over `$39`.

### Verification
* `cargo test -p engine-core --test act_end_motion -- --nocapture`: **6/6 traces match MOTION for 1600/2200/2600
  ticks**.
* `cargo test --workspace`: 68 engine-core unit tests, 1 act-end, 1 EHZ, 1 1-1, 1 SMB3 1-2/1-3, 2 golden-trace,
  5 rom-assets — all pass.
* `cargo fmt --all` and `cargo clippy --workspace --all-targets -- -D warnings` clean.

### Quirks
* The GHZ/MZ debug-start traces exercise the level's hack-column object set; the object registry now dispatches the
  shared platform/spring and the MZ/GHZ enemies by id there too.
* `Results`/signpost are ported but not exercised by these six traces (the SMB3 goal is not a signpost act); the next
  act load after results is ready for a future signpost trace.

## 2026-10-20 — Phase 2 M4d: Green Hill and Marble Zone acts 1-2 (verified)

### New traces (generator, committed)
`tools/golden/generate.mjs` now records `sonic-ghz-1-playing-{idle,right,jump,spindash}`,
`sonic-ghz-2-playing-{...}`, `sonic-mz-1-playing-{...}` and `sonic-mz-2-playing-{...}`: 600 ticks each,
`noTitleCard: true`, zones `$11`/`$11`/`$13`/`$13` acts 1/2, merged with the existing scripts.

### Camera / level data
* Added `Camera::GhzEvents`/`Ghz3Events`, `Mz1Events`/`Mz2Events`/`Mz3Events`, `SwScrlGhz` and `SwScrlMz` (the
  latter already existed from M4c-2 and is now exercised by acts 1/2).
* Added the lava-line module: `LavaLine.Init`/`Step` (per-act line height, line mode/scanline), exact lava palette
  RAM at `$FFDA..$FFEC`, and a `split` helper for the M5 renderer. `World` initialises it and `Session` steps it
  before the players, matching `World.Step`.

### Objects filled in
* GHZ: Motobug `$40`, Crabmeat `$1F`, Buzz Bomber `$22`/missile `$23`, Chopper `$2B`, Newtron `$42`, Rock `$3B`,
  Wall `$3C`, EdgeWall `$44`, Scenery `$1C`, Spikes `$36`, ForceRoll `$06`, SwingingPlatform `$15`, collapsing
  Ledge `$1A` (real slope/delay tables), WaterfallSound `$49`.
* Shared/hack: monitor `$26` + contents `$2E`, Bridge `$11`, Platform `$18`, Spring `$41`, star post `$79`,
  giant ring `$4B`, results/signpost/game over.
* MZ: LavaBallMaker `$13`, LavaBall `$14`, GrassFire `$19`, GrassPlatform fires, LavaMarker `$54`, InvisibleBlock
  `$71`, GlassBlock `$30`, MarbleBrick `$46`, BreakableBlock `$51`, ChainedStomper `$31`, Button `$32`, PushBlock
  `$33`, MovingBlock `$52`, CollapsingFloor `$53`, GeyserMaker `$4C`, Geyser `$4D`, LavaWall `$4E`, Basaran `$55`,
  Caterkiller `$78`, MZ boss/fire.
* `$7D` remains the original's `DeleteObject` in hack zones.

### Findings
* The MZ lava line does **not** itself kill; MZ deaths in these traces come from lava-marker/fire/lava-ball hitboxes.
  The first MZ2 death (tick 304/283) is a GrassFire spawned when the grass platform reaches `Sink == $20`.
* GHZ1's `jump` ring divergence was Tails collecting the 10-ring monitor contents; the hack column was missing
  `$2E` and had left the contents object inert.
* Crabmeat's walk must check the floor 16 px ahead; without that it walked off the ledge into Sonic.

### Verification
* `cargo test -p engine-core --test ghz_mz_motion -- --nocapture`: all **16/16** traces match MOTION for 600 ticks.
* `cargo test --workspace`: 68 engine-core unit tests + 5 motion tests (act-end, EHZ, GHZ/MZ, 1-1, SMB3) + 2
  golden-trace + 5 rom-assets — all pass.
* `cargo fmt --all` and `cargo clippy --workspace --all-targets -- -D warnings` clean.

## 2026-10-20 - Phase 2 M5a: software VDP and level framebuffer hashes (partial)

### Implemented
* `engine-core::vdp`: 320x224 CRAM-index frame, backdrop/BG-low/FG-low/BG-high/FG-high order, per-line H-scroll,
  full-screen V-scroll, the SAT link walk with the 80/20/320 limits, flips, palette line, high-plane occlusion and
  `to_rgba` in the C# `0xAABBGGRR` memory order (R, G, B, A bytes). `Session::render_hash` is SHA-256 over that
  buffer, low 64 bits little-endian, matching `GoldenTrace`.
* `background_plane` (initial + `LoadTilesAsYouMove`, including Marble's shared layers), `animated_tiles` (generic
  scripts + MZ magma), `plc` (RAM queue counters + immediate art copy), `hud` (Base/Update digits, lives, bonus and
  timer), `sprite_builder` (HUD, display lists, rings, static mappings, multi-sprite, on-screen flag).
* `rom-assets`: composes the 64 KiB VRAM from level art + fromTitleScreen/onLoad/afterTitleCard PLCs + animated
  frame 0; loads every `objects/*/sprites.json` mapping/DPLC/animation block, HUD digits, Sonic/Tails/rings, animal
  tables and the SMB3/zone PLC lists.
* `Session::step` order now matches `World.Step` + `LevelSession.StepPlaying`: players, object DPLC side effects,
  objects, camera/background, rings, PLC, oscillation, palette, BuildSprites, VBlank/HUD/PLC, animated tiles and
  plane B; `render()` copies CRAM and applies the MZ lava split.
* Ported the rendering half of Tails' tails `$05`, dust `$08`, monitor `$26`/contents `$2E`, animal `$28`/points
  `$29`, and the animations for Motobug, Chopper and Buzz Bomber.

### Verification
* `render_motion.rs` replays the six required levels with all four scripts (`idle/right/jump/spindash`) and compares
  `{ram:false, regions:false, screen:true, tails:false}` every 30 ticks.
* **15/24 traces match all 600 ticks (240/240 screen hashes).** Nine traces have a first divergence at the ticks in
  K4; the remaining mismatches are unported effect/object sprite animation, not plane/palette/HUD disagreement.
* Existing suite excluding the new gate: `cargo test --workspace -- --skip
  level_framebuffer_hashes_match_the_legacy_traces` = all pass. `cargo fmt --all` and `cargo clippy --workspace
  --all-targets -- -D warnings` clean.
* The new render gate intentionally still fails (assertion unchanged) until K4 is closed.

## 2026-10-20 - Phase 2 M5b: all 24 level framebuffer traces exact (K4 closed)

### Fixes (all against the legacy oracle)
* Game Over `$39`: the player check now spawns GAME/OVER in slots `$B080/$B0C0`, sets OVER's odd frame, queues PLC 3, and the handler waits for the PLC buffer before the 16 px slide; the first half runs the `$2D0` wait/end path.
* Explosion `$27`: the spawn/init tick now falls through into the animating routine (legacy `goto case`), which also fixes the frozen SMB3 1-3 spindash screen.
* Animated sprites/DPLC: wired Buzz Bomber `$22`, missile `$23`, Masher `$5C`, Buzzer `$4B`, Coconuts `$9D`, Chopper `$2B` and Motobug `$40` animation blocks. Chopper/Masher animate **before** assigning the new `Anim` (the legacy order that keeps their mapping frame in step).
* Dust `$08`: implemented the skid emitter's puff spawns; Tails' dust art/frame now reaches the SAT through the skid puffs.
* Lost rings `$37`: the sparkle routine now runs its own animation block instead of copying `RingSpillAnimFrame` directly.
* Emerald Hill background: the near-hills perspective writes five equal lines per band and advances the 16.16 accumulator once per band, not once per line.
* Animal/points and monitor contents/DPLC wiring from M5a completes the remaining effect art.

### Verification
* `cargo test -p engine-core --test render_motion -- --nocapture`: **24/24** traces, 240/240 hashes each, no divergence; the gate is no longer `#[ignore]`d.
* `cargo test --workspace`: 68 engine-core unit tests, all motion tests (including the render gate), 2 golden-trace and the rom-assets tests pass.
* `cargo fmt --all` and `cargo clippy --workspace --all-targets -- -D warnings` clean.

## 2026-10-20 - Phase 2 M5c: SMB3 front-end screens (partial)

### Implemented
* `rom-assets::load_frontend_assets`: `sb3/prg_*.bin`, `sb3/chr_*.bin`, `bonus_room.bin`, front-end art entries, Sonic/Tails/monitor/SMB3 enemy sprite data, monitor/life icons and palette 03, all from the archive.
* `engine-core::frontend`: `NesPpu` (mirroring, palette, Video_Upd streams), `NesPresenter` (CHR/palette to MD tiles, margins, priority), `StatusBar`, `MiniSonicArt`, `SpriteKit`, `Screen`-style palette/fade/RAM/VRAM helpers, `GameFlow`/`MapProgress`.
* `Vdp::render_screen` with plane A/B nametables, per-line vertical scroll and sprite masking.
* World map: SMB3 World 1 layout, animated banks, mini Sonic, status bar, world-intro box, movement/fade. **`frontend-map` matches all 400 ticks (14/14 hashes).**

### Open
* Title and title?map: first divergence at tick 30, legacy `4688767481692680038`, port `1897499236784740993`. The title screen is still a placeholder: missing the SMB3 curtain/logo/menu PPU streams, actor scripts, sprite/DPLC placement and title line scroll.
* Match: first divergence at tick 30, legacy `16094679856854692594`, port `15085034060434167613`. Bonus-room tile/CHR-bank/palette composition is wrong; the card phase beyond the 400-tick window is not ported.
* `cargo test --workspace` (excluding the new gate) passes; fmt/clippy clean. The new `frontend_screens` assertion is unchanged and currently fails for the three screens above.

## 2026-10-20 — Phase 2 M8a: playable native Rust build (verified)

* New `crates/platform-native` (binary `sonic-bros-3`): `minifb` window, keyboard → Mega Drive pad, fixed 60 Hz, and
  the ported `Session::step`/`render` output presented at 320x224 (scaled 4x). It starts directly in a reviewed level
  (`--level 1-1/1-2/1-3`, or `--zone 0xNN --act N`); `P` pauses, `Esc` quits.
* `--frames N --screenshot out.png` runs headlessly and writes an RGBA PNG; it also prints the tick and the same
  low-64 SHA-256 framebuffer hash the golden traces use.
* Release build with static CRT (`.cargo/config.toml`, `+crt-static`); `sonic-bros-3.exe` is 1.36 MB and needs only
  `sonicbros3.bin`.
* **Verified:** `sonic-bros-3.exe --level 1-1 --frames 30` prints
  `hash=4f5de3aa5c2873f0` = `5718977422660498416`, exactly the legacy `smb3-1-1-playing-idle` tick-30 hash. The
  rendered frame shows SMB3 1-1 with Sonic, Goomba, blocks, bushes and HUD.
* Window smoke test: the process opens and stays running; `--help` and headless modes work.
* Limitations: silent (M6 not started), no web shell, title/card screens still K5, and it reads the derived archive
  (stock importer M7 still pending).

Next: `platform-web` (WASM + canvas + ROM file picker), then audio, the stock importer and packaging.

## 2026-10-20 - Phase 2 M6a: 68k SMPS audio (exact)

### Implemented
* `rom-assets::load_audio_assets` reads `audio/smps68k/player.json` (segments, frequency/voice tables, level music), `driver.json`'s WAV DAC samples, and `zones/zone_XX/music.json` extra SMB3 songs (music pointer table patched as the legacy `Smps68kData` does).
* `engine-core::audio` now contains direct ports of `Ym2612`, `Sn76489`, `SoundMixer` and the 68k `Smps68kDriver`; driver state lives in the game RAM at `$F000`, and the mixer is I/O free (`render` = 800 stereo frames per tick at 48 kHz).
* `Session::enable_audio` is opt-in. It sets the driver-select/LevelMusic bytes, queues the act's music and runs the driver in VBlank after `ProcessDPLC`; without it the RAM and render path are unchanged.
* `platform-native` opens a `cpal` 48 kHz stereo output, feeds it one frame per 60 Hz tick from a bounded queue, and supports `--mute`.

### Verification
* C# oracle probe (same archive, queue byte and VBlanks): driver RAM `$F000-$F2FF` and 48 kHz sample hashes match at frames 1/2/5/10/30/60/120/240/600/900/1200/1800/3000. `audio_driver_parity` pins those hashes.
* `audio_smoke`: 1-1/1-2/1-3 are non-silent and enabling audio leaves the framebuffer, camera and level counter identical.
* `cargo test --workspace`, `cargo fmt --all`, and strict clippy are clean; headless native 1-1 still prints `hash=4f5de3aa5c2873f0`.
* Quirk found while matching the oracle: `SoundMixer`'s scale is the C# `float` product `0.8f * 0.48f`, not an f64 product; using f64 changed sample rounding after several seconds.

### Remaining
* The Sonic-zone/title Z80 SMPS path is not ported. Gameplay SFX are wired in M6b.

## 2026-10-20 - Phase 2 M7: campaign metadata, all-world maps and area loading

### Implemented
* `rom-assets::CampaignData`/`CampaignWorld`/`CampaignArea` load `campaign/campaign.json`; ownership is filled from world entries and `CampaignArea::load_level` loads `zones/<directory>/act_1` through the new `LevelData::load_dir`.
* `FrontendAssets` carries a map-only campaign view; `MapScreen` selects each world's map cells, palette, entry areas, paths, panels, cards and intro box. `GameFlow` can start at any world/screen and returns panel start actions.
* Native campaign mode (`CampaignRunner`) enters panels, seeds lives/score, runs campaign `Session`s, returns to the map on death/goal, carries rings/cards, handles game over, and plays map/area 68k music.
* New CLI: `--map`, `--world`, `--map-screen`, `--campaign-act`, `--list-campaign`; when `--data` is omitted the order is `campaign.bin` beside the exe, `web-data/campaign.bin`, `sonicbros3.bin` beside the exe, then `sonicbros3.bin` in the working directory. Direct `--level` mode is unchanged.

### Verification
* Manifest: 8 worlds, 19 screens, 257 areas (255 available, 1 failed, 1 blocked).
* `campaign_areas`: all 255 available areas load and step 60 ticks (no panic); 172 s in a debug test.
* `campaign_maps`: all 19 map screens render non-empty. `campaign_world1_map`: World 1 is byte-identical with/without campaign metadata for 400 ticks; `frontend-map` remains exact.
* `campaign_map_enter`: World 1 input path reaches the 1-1 panel.
* Native: `--map`, `--world 8 --map-screen 3`, `--campaign-act 2/255/257`, and headless hashes; map music and death-to-map are unit-tested.

### Remaining
* Source map commands, world transitions, locks, bridges, wandering enemies and world-clear bosses are adapters not yet implemented. One failed area (opcode $00 at $CFD7) and one blocked vertical area (180 rows > 128-row layout) remain. Map pipes/card game and the title/match K5 screens are still partial.

## 2026-10-20 - Phase 2 M7 title: exact SMB3 intro/title, boot to World 1 map

### Implemented
* `engine_core::frontend::title::TitleScreen` line-by-line port of `Sb3TitleScreen`: own VRAM art and palette 03, curtain fill/raise, Video_Upd items $01/$08-$22, logo drop/shake, palette fades $03-$06, 3-glow, 1P/2P menu, skip/timeout, and the Sonic/Tails/Goomba/shell actor scripts and DPLC sprites.
* `GameFlow` handles `TitleAction::StartGame` by resetting progress to the World 1 map and `BackToTitle` by recreating the title. Native campaign mode boots at `Start::Title` by default, `--map` skips to the map, and `--title` is explicit.
* Split the frontend gate: title/title-map/map are strict; the card game keeps its ignored K5-match test.
* Fixed `NesPresenter::sprite_tile` to mask tile numbers to the selected 1 KiB CHR bank and wrote SMB3 object sprite palette line 4 after the NES palette upload, matching the legacy title.

### Verification
* `frontend-title`: 600/600 ticks exact.
* `frontend-title-map`: 700/700 ticks exact.
* `frontend-map`: 400/400 ticks exact.
* Native headless: default/`--title` tick 30 = `4111d92eb4b1ef66`; `--map` tick 30 = `1fde8d552a39513d`; `--data sonicbros3.bin --level 1-1` remains `4f5de3aa5c2873f0`.
* Native title?map test enters the map after Start at frames 101/103 and starts map audio.
* Campaign/title map independence test: title hashes match with and without the campaign manifest. Existing campaign/map tests remain green.

### Remaining
* K5-match card game still placeholder/ignored. Title audio is silent because the Z80 SMPS path is not ported.

## 2026-10-20 - Phase 2 M6b: gameplay SFX (Sonic 68k + SMB3 PCM)

* `engine-core::audio::effects` is an I/O-free overlapping PCM effect player; `engine-core::audio::sfx` gates Sonic 68k queue writes and SMB3 source queue bits on `$FFE5`, so audio-disabled traces keep their RAM.
* `rom-assets::load_pcm_effects` loads `campaign/effects/*.pcm` (signed 16-bit LE, 48 kHz mono) into `AudioAssets`.
* New `tools/smb3-effects` reuses `S2Port.Extraction`, runs SMB3's source sound engine and renders pulse/noise channels to PCM: bump, kick, pipe, fire, coin, powerup, 1up, crumble, boomerang, map-world, map-move, map-enter, course-clear, death.
* Wired Sonic jump/ring/hurt/skid/roll/spindash/spring/spikes/monitor/shield/starpost; SMB3 bump/break, stomp/kick, coin, power-up, 1-up, fire, boomerang, goal course-clear, and map move/enter/world. Native mixes the effects; `--mute` still disables output.
* `audio_sfx` verifies Sonic jump/ring/hurt and Mario coin/bump produce non-silent output; `audio_smoke` keeps the no-render-change check. Full workspace tests, fmt, strict clippy and release build pass.

Remaining: pipe entry and a few rare object events have no dedicated Mario PCM yet; title/map Z80 music remains unported.

## 2026-10-20 — Phase 2 M6b: gameplay SFX (verified)

* Sonic SFX are wired at the legacy call sites: player jump/skid/roll/spindash; ring collection, hurt/kill and the
  100/200-ring extra life; star-post checkpoint; EHZ shield/spring; signpost/end-level music. They go through the
  exact 68k SMPS queue (`$F00B`), gated on `$FFE5` so audio-disabled golden traces are unchanged.
* Mario PCM effects use a reserved secondary-queue range (`$80-$8F`): the SMB3 block bump/break, shell kick, coin,
  power-up/1-up, fireball, boomerang, pipe, map move/enter/world and goal fanfare play through `EffectPlayer`.
* `tools/Smb3Effects` runs SMB3's own sound engine offline and writes `web-data/effects/*.pcm` (derived, gitignored).
  `rom-assets` merges those with the archive's `pipe`/`enter-level` effects.
* `mario-crumble`/`mario-boomerang` use SMB3's noise channel, which the current pulse-only renderer does not capture;
  they fall back to `mario-bump`/`mario-fire` until a noise renderer is added (documented in the tool README).
* Tests: `audio_sfx` verifies Sonic jump/ring/hurt, `pipe`, and `mario-bump`/`mario-coin`/`mario-powerup` are
  non-silent, and that SFX requests leave the motion/framebuffer unchanged. All existing tests stay green.
* The blocked subagent had already written this wiring before the provider cut its reply; verified and kept.

## 2026-10-20 — User change batch 1: entry transition, title ding, SFX fixes

* Map → level now runs a **32-frame closing black border** (the SMB3 entry effect) before loading; map input is frozen
  and the `enter-level` PCM continues. The new level replays `enter-level` on its own audio state, so the sound is not
  cut by dropping the map audio.
* Title 1P Start queues `mario-coin` (the ding) through the map's PCM player.
* Piranha-plant fireballs no longer queue `mario-fire` (removed the odd sound; the plant still fires).
* Brick/block break uses the **Sonic Smash** SFX (`Sfx.Smash`, `$B9`) instead of a Mario crumble.
* End-of-level cards already award lives exactly as SMB3: three alike rings=2, shields=3, invincibility=5, mixed=1;
  the slots clear after the award.
* Test: `area_entry_draws_the_closing_border_then_loads` pins the 32-frame border and delayed load.

Remaining from the user's list: the Sonic item/card-game/menu conversion, 2-player Tails, and pipe enter/exit plus
sprite clipping. These are the next batch.

## 2026-10-20 - User change batch 2: Sonic items/card game, 2P Tails, pipes

* **N-Spade card game ported** from `legacy/src/SonicBros3/Frontend/MatchGameScreen.cs`: the bonus-room intro,
  `Bonus_WaitA0`, fade to black, 18-card shuffled deck, 8-frame cursor repeat, 5-frame flips, two misses,
  persistent `Map_NSpadeInProg` deck, status bar, and the Sonic-monitor faces (mushroom = speed shoes,
  flower = shield, star = invincibility, 1-up = Sonic, 10/20 coins = 10/20 rings).
* **Prizes are real Sonic items**: 1-up lives, rings one per frame (100 rings = a life), and speed
  shoes/shield/invincibility are queued in `MapProgress.PowerUps`. At the start of the next act each queued item
  rises out of a monitor at Sonic (`Obj 0x2E` with the item's anim), exactly as the legacy `GameFlow.StartLevel` did;
  the map status now shows the queued item icons. End-level cards already use Sonic monitor icons and the SMB3
  three-card life rule (rings 2, shields 3, invincibility 5, mixed 1).
* **2 PLAYER** now carries `MapProgress.TwoPlayer` from the title. Player 2 drives Tails with WASD + J/K/L (I = Start);
  the engine uses the original `TailsCPU` takeover path, so Tails is player-controlled while P2 is active and
  returns to CPU following when idle. The world map draws a second mini Tails beside mini Sonic in 2P mode.
* **Pipes**: `Smb3Gameplay` is parsed into `world::Gameplay`, and `Session::StepPipe` is ported: mouth detection,
  pipe SFX, local transit paths, exit-mouth placement, alternate/bonus/common-exit seamless area loads (rings, timer,
  score, lives, shield/invincibility object slots, shoes and invulnerability carried), map-pipe return (including the
  paired far-end pipeway destination), and a sprite clip rectangle so a player disappears into a pipe instead of
  drawing over its rim.
* The card backs are the gold ring redraw (not the N-Spade), and the card-game status bar gets the same [S] badge as the map.
* Entry audio no longer starts on the map audio and is cut at load: the 32-frame border plays `map-move`, then the
  new level's own audio plays `enter-level` from the start.
* `--match` boots straight into the card game (with map music) for testing; `--two-player` forces the 2P map/level flag; `--list-campaign` is unchanged.
* Tests: `session::tests::a_local_pipe_carries_the_players_and_returns_control`,
  `tests/match_game.rs` (18-card deal/render), and the existing 575 golden traces still pass.
* **Remaining K5-match**: the bonus-room framebuffer still diverges from the legacy trace at tick 30 (the room's
  tile-memory/CHR composition); the card game itself is now ported and covered behaviourally. The ignored
  `frontend-match` golden remains the strict oracle for a later room-art pass.

## 2026-10-20 - User change batch 3: map/2P/items/cards fixes

* **Overworld movement SFX**: every accepted map step queues `map-move` through the map PCM player.
* **Entry audio/transition**: the 32-frame closing border now belongs to the map's own `EntryProgress` (it is drawn by
  `MapScreen::render_rgba`), so the border and its sound happen together. The map plays `map-move` during the border;
  the level's own audio plays `enter-level` once when the level actually loads. The second CampaignRunner border and
  its duplicate entry sound were removed.
* **2P is turn-based, as SMB3**: player 1 has Sonic's go, player 2 has Tails' go. Only the active character is drawn on
  the overworld; the map and level use `MapProgress.current_player`, and each player has a separate life stock
  (`lives_p1`/`lives_p2`). Turns alternate after each level and after a death. On Tails' turn the main-character slot
  carries Tails with Tails physics/sprites (so the camera/rings/collisions stay on the supported main slot) and is
  driven by either keyboard. `--two-player` starts Sonic's turn; `--tails-turn` starts Tails' turn for testing.
* **Mario kill sound**: native SMB3 enemies now queue `mario-kick` when stomped/spun/jumped into, when a sliding shell
  knocks them out, and when a micro-Goomba is destroyed.
* **Turtle spin**: added a 16-frame turn hysteresis to the SMB3 walker's wall/ledge turn. A red Koopa standing on a
  brick no longer flips back and forth every frame; it walks out of the turn first.
* **? blocks**: in campaign mode item blocks now give a **direct** Sonic reward instead of a floating monitor object:
  a stable mix of shield, invincibility and ten rings across the item blocks, a rarer 1-up block, one-ring coin blocks,
  and one ring from otherwise-unmapped block types (no empty ? boxes). Ten-coin bricks still pay one ring per hit.
  These item/behaviour changes are gated by `Objects.user_changes`, set only by campaign sessions, so the 575 golden
  traces still exercise the legacy oracle unchanged.
* **End-of-level card art**: the unported SMB3 goal-card art (the blank box) is replaced in campaign sessions by the
  matching Sonic monitor icon (ring/shield/invincibility), spawned as a display-only monitor-content object as soon as
  the goal card is chosen. The persistent map slots already show the same monitor icons; `--cards` previews them.
* **Card game presentation**: card backs are the gold-ring redraw, all face icons use the map's known-good Sonic monitor
  frames (ring/shield/star/life), and the broken SMB3 bracket cursor was replaced with a ring marker. The card logic and
  reward test are unchanged (`tests/match_game.rs`).
* `--inputs FILE` drives frames from a golden-style input script; used to verify the map border and card flips.
* **Known data gap**: World 1's castle panels (tileset 7, layout `$AD60`) have no converted campaign area at all, so
  there is no castle level/object list to give enemies or fireballs. The map manifest marks those entries without an
  `areaId`; this needs the stock SMB3 castle extractor (M7), not a runtime patch. The same is true of the castle-like
  `ts07` entries in other worlds.

## 2026-10-21 - User change batch 4: audio/card/goal fixes, 2P removed

* **Card game room fixed (K5 closed)**: the `Video_Upd_Table` entries are little-endian assembler tables, but the
  port read them big-endian. That corrupted the bonus room's nametable/instruction box. `video_update` now uses
  `prg_word_le`; `frontend-match` matches all 400 ticks and is a normal strict test (no longer ignored).
* The card cursor/flip tiles are now resolved by the same `NesPresenter` that renders the card board. Previously a
  second presenter reused MD tiles 1.. and overwrote the cursor/flip art, which caused the striped/glitched selection
  sprites. Tails now hosts from the right side of the table, facing left (native build only; the legacy golden keeps
  Toad's old position).
* **Title intro audio**: the title now has its own 68k SFX driver and queues the C# `Sb3TitleScreen` sounds — logo-drop
  Smash, menu cursor Blip/Ring, spindash rev/release, roll, jump, Goomba explosion, shell Smash and Tails' helicopter.
  It uses a separate title RAM/driver so it cannot disturb level/map audio.
* **Map-load sound**: every map load queues `map-world` (SMB3's overworld arrival sound) on the map audio player.
* **Entry sync**: the map action now hands over immediately. The map stays on screen while a 54-frame border closes to
  black and `enter-level` plays; only after the screen is dark does the level load (with no duplicate entry sound).
  That keeps the animation and the loading sound together and shows no level behind the transition.
* **Goal card roulette**: the goal object gets the legacy `RouletteGoal` three-frame mapping table built from Sonic
  monitor frames (ring/shield/invincibility) plus card-icon frames for the "you got a card" text object. The card
  visibly changes every 8 frames, stops on the touched card, and the final card icon is shown before the map slot
  award. The table is installed only for campaign sessions, preserving the direct goal golden traces.
* **2 PLAYER removed**: the title no longer offers/selects it (the native title blanks the dead SMB3 2 PLAYER line),
  the `--two-player`/`--tails-turn` flags and campaign turn/active-player code are gone, and the player-2 pad is
  neutral. The original Tails CPU sidekick in 1-player mode remains.
* **Still a data gap**: World 1's castle panels (tileset 7) have no converted campaign area at all, so they still
  cannot have doors, enemies or fireballs. That needs the stock SMB3 castle extractor, not a runtime fake.

## Appendix: Phase 2 plan (approved 2026-10-19)

Two workstreams, per the owner's decision:

1. **Engine equivalence against the legacy oracle.** Port `engine-core` module by module and replay the committed
   `.sb3g` traces with `golden-trace`. The oracle input is the pinned local `sonicbros3.bin` / `campaign.bin` (never
   committed/shipped). This proves the port before any asset-source change.
2. **Stock-ROM importer.** Replace the legacy archive as the shipped source with a build/first-run extractor that reads
   stock Sonic 2 + SMB3 and writes `data.bin`. Document the unavoidable deviations (no Green Hill/Marble; the SMB3
   slope collision mapping and 68k SMPS data need stock substitutes). This is the repo's stated unfinished goal.

Module order and checkpoints:

| Milestone | Crates | Proof |
|---|---|---|
| M1 primitives | `engine-core`: `Ram` (big-endian 64 KiB, aliasing), `Vars`, object-slot accessors, `MdMath` tables | unit tests for byte/word/long wrap and every documented math quirk |
| M2 archive + level | `rom-assets` (ZIP/JSON/indexed-PNG reader) + `engine-core` level loader/`World` setup | load `zones/zone_40/act_1` and match the legacy initial RAM hash from a trace snapshot |
| M3 player + camera | `engine-core`: player physics, collision probes, camera, rings, HUD, PLC queue | 1-1 `idle`/`right`/`jump`/`spindash` traces diverge-free |
| M4 objects | `engine-core`: SMB3 objects first (block/goomba/koopa/plants/boomerang/goal), then Sonic common/Green Hill/Marble/Emerald Hill (needed for oracle traces even though the stock build drops them) | per-object traces, then goal/death/pipe/boss traces |
| M5 frontend | `engine-core`: SMB3 title, world map, card game, level select, `Vdp::RenderScreen` | frontend traces + frame hashes |
| M6 audio | `engine-core`: 68k SMPS + Z80 SMPS players, YM2612/PSG/DAC, mixer | non-bit-exact: non-silent, per-band spectrum; driver-RAM replay once the mixer is bit-reproducible |
| M7 stock importer | `rom-assets`: stock Sonic 2 + SMB3 extraction, SMB3 zone builder/campaign, 6502, music conversion | byte-level comparison on the shared subset; documented deviations |
| M8 platforms | `platform-native` (exe, static CRT), `platform-web` (WASM), `xtask` | headless replay, then window/canvas/audio |

Approved; implemented incrementally from M1.
