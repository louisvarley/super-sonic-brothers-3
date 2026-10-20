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
