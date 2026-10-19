# Known issues preserved from the legacy engine

Per hard rule 1 of the port: behaviour that looks wrong is ported **as-is**, and recorded here with enough detail to
reproduce it. This file is filled in during Phases 1–2 as divergences/bugs are confirmed against the legacy engine.
Nothing here is fixed until the user asks.

Schema: **id | file/function (legacy) | symptom | reproduction | status**

## Confirmed (observed directly)

_(none yet — to be populated while writing characterisation tests)_

## Suspected, to confirm during Phase 1/2

| # | Area | Suspicion | How to confirm |
|---|---|---|---|
| S1 | `Smb3ObjectConverter.Rules` / `campaign/backlog.json` | Campaign areas report missing enemy behaviours; unported ids despawn via `MarkObjGone`, so "some levels have no enemies". This is the documented `native-partial` state, not a crash. | Load a `native-partial` area and compare its object list to `unmappedObjects`. |
| S2 | `Runtime/LevelSession.cs` `StepPipe` | Pipe entry uses fixed pixel windows (`<=8`/`<=6`/`<=12`) and hard-coded durations; the brief calls pipes "buggy". Behaviour must be ported exactly. | Input scripts entering/exiting every pipe mouth in 1-1/1-2/1-3. |
| S3 | `extracted/*` vs C# `Extractor` quirks | The extractor has documented "kept quirks" (e.g. unstable ring sort, sign-extended offsets). Any mis-port changes behaviour; reproduce the *effective* behaviour. | Compare Rust extraction output byte-for-byte against `sonicbros3.bin`. |

## Non-game issues (not behaviour; for awareness)

* **Derived-artifact mutation**: running the game rebuilds `sonicbros3.bin` when `Extractor.Version` changes. Not a game
  bug, but golden-trace provenance must pin/regenerate it. See `PORT_LOG.md` 2026-10-19 finding 4.
