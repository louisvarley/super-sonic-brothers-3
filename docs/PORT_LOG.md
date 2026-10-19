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

## 2026-10-19 — Phase 2 plan (for approval)

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

No engine code is written until this plan/layout is approved.
