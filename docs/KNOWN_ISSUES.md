# Known issues preserved from the legacy engine

Per hard rule 1 of the port: behaviour that looks wrong is ported **as-is**, and recorded here with enough detail to
reproduce it. This file is filled in during Phases 1–2 as divergences/bugs are confirmed against the legacy engine.
Nothing here is fixed until the user asks.

Schema: **id | file/function (legacy) | symptom | reproduction | status**

## Confirmed (observed directly)

### K1 — SMB3 1-3 spindash freezes the session on a solid ("crushed") path

| | |
|---|---|
| **id** | K1 |
| **file/function** | `legacy/src/SonicBros3/Logic/Level/Solid.cs`, `Solid.Vertical` (ROM 0x1D8E0) throws `UnportedException(0x1D8E0, "crushed by a solid object (KillCharacter)")`; `legacy/src/SonicBros3/Runtime/LevelSession.cs` catches it in `StepPlaying`/`Step`, stores it in `Stopped`, and every later `Step` returns immediately. |
| **symptom** | In World 1-3, spindashing into the early Koopa/solid reaches the unported "crushed by a solid" branch at tick 115. The legacy session then **freezes**: the trace's Sonic/camera/timer stop changing for the remaining 485 ticks. No crush death or restart happens; the screen just stays. |
| **reproduction** | Golden trace `tests/golden/smb3-1-3-playing-spindash.sb3g.gz`, tick 115 (script `tests/golden/inputs/spindash0.txt`). Legacy equivalent: `dotnet run --project legacy/src/SonicBros3 -- --start level --zone 42 --act 1 --no-title-card --inputs tests/golden/inputs/spindash0.txt --frames 120`. |
| **port status** | Reproduced exactly: Rust `Session` returns `Unported` and stores it in `Session::stopped`, then keeps the state frozen. `smb3_levels_motion` asserts all 600 ticks match. **Not fixed** — waiting for the user. |

_(No other confirmed game bugs yet; more will be added as the later milestones are replayed.)_

## Suspected, to confirm during Phase 1/2

| # | Area | Suspicion | How to confirm |
|---|---|---|---|
| S1 | `Smb3ObjectConverter.Rules` / `campaign/backlog.json` | Campaign areas report missing enemy behaviours; unported ids despawn via `MarkObjGone`, so "some levels have no enemies". This is the documented `native-partial` state, not a crash. | Load a `native-partial` area and compare its object list to `unmappedObjects`. |
| S2 | `Runtime/LevelSession.cs` `StepPipe` | Pipe entry uses fixed pixel windows (`<=8`/`<=6`/`<=12`) and hard-coded durations; the brief calls pipes "buggy". Behaviour must be ported exactly. | Input scripts entering/exiting every pipe mouth in 1-1/1-2/1-3. |
| S3 | `extracted/*` vs C# `Extractor` quirks | The extractor has documented "kept quirks" (e.g. unstable ring sort, sign-extended offsets). Any mis-port changes behaviour; reproduce the *effective* behaviour. | Compare Rust extraction output byte-for-byte against `sonicbros3.bin`. |

## Non-game issues (not behaviour; for awareness)

* **Derived-artifact mutation**: running the game rebuilds `sonicbros3.bin` when `Extractor.Version` changes. Not a game
  bug, but golden-trace provenance must pin/regenerate it. See `PORT_LOG.md` 2026-10-19 finding 4.
