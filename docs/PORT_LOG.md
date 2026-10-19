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
