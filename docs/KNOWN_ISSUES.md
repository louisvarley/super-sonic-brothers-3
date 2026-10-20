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

### K2 — Tails' item pickups are gated by Sonic's invulnerability timer

| | |
|---|---|
| **id** | K2 |
| **file/function** | `legacy/src/SonicBros3/Logic/Level/TouchResponse.cs`, `Touch_ChkValue` (0x43A82): the invulnerability gate reads `ram.Sonic.InvulnerableTime` even when the touching player is Tails. |
| **symptom** | During Sonic's hurt animation, Tails cannot collect spilled rings/items (or, depending on the timer, can collect while Sonic is invulnerable). The two players do not use their own timers. |
| **reproduction** | Emerald Hill act 1 `jump`/`spindash` traces at ticks 329/294: using the touching player's timer lets Tails collect Sonic's spilled rings; the legacy trace does not. |
| **port status** | Reproduced: the gate reads Sonic's timer for both players. `ehz_motion` asserts all 600 ticks match. **Not fixed.** |

### K3 - Debug starts run only the zone's event routine (not DynamicLevelEvents' max-Y easing)

| | |
|---|---|
| **id** | K3 |
| **file/function** | `legacy/src/SonicBros3/Logic/Level/Camera.cs`, `Camera.RunLevelEvents` (0x10804's zone half); `legacy/src/SonicBros3/Logic/Level/World.cs`, debug-start block after the first `ObjectManager.Run`. |
| **symptom** | A debug start calls only `ZoneEvents` and then sets `CameraMaxYPos` to `CameraMaxYPosTarget`. It does **not** run the max-Y easing at the end of `DynamicLevelEvents`, and does not change `CameraYPos`. Running the full `DynamicLevelEvents` during setup sets `CameraMaxYPosChanging` and clamps the first vertical scroll to the new max (in MZ act 3, camera y 604 snaps to 528 instead of staying 604). |
| **reproduction** | `sonic-mz-3-boss-playing`: debug start `6656,700`. The first tick has camera y 604. |
| **port status** | Reproduced: `Camera::run_level_events` calls only `zone_events`, and `World::new_at` sets `CameraMaxYPos` afterwards. `act_end_motion` matches. **Not fixed.** |

## M5a software renderer: partial framebuffer parity (port gap, not a legacy bug)

| | |
|---|---|
| **id** | K4 |
| **file/function** | `engine-core::{vdp,background_plane,animated_tiles,plc,sprite_builder,hud}` and `Session::render`; the legacy oracle is `Runtime/Vdp.cs` + `GoldenTrace`'s 30-tick `screenHash`. |
| **state** | The renderer, VRAM composition, HUD, DPLC, Tails' tails, dust, monitor contents/monitor animation, animal/points, Motobug/Chopper/Buzz Bomber animation, and the five hardware-priority planes are implemented. **15 of the 24 required level traces match all 240 framebuffer hashes (600 ticks, every 30).** The remaining traces diverge only in sprite/effect areas; backgrounds, HUD, palette and scrolling match up to the first listed tick. |
| **open divergences** | `smb3-1-1-right` t600 and `smb3-1-3-right` t600: Game Over slide (`$39`) timing/text, which currently uses `Display` without the PLC wait and the two-half frame/position timing. `smb3-1-1-jump` t450: a later effect sprite. `smb3-1-3-spindash` t120: the block-break/points effect sprite. `sonic-ehz-1-{right,jump,spindash}` t180/t150/t120: Tails/effect object animation (Tails' fields are outside MOTION, so the logic gate does not cover it). `sonic-ghz-1-jump` t240: Chopper's animated frame plus a second effect sprite. `sonic-ghz-1-spindash` t150, `sonic-mz-1-{right,jump,spindash}` t300/t210/t330: zone object/effect animation and DPLC art not reached by idle/right. |
| **reproduction** | `cargo test -p engine-core --test render_motion -- --ignored --nocapture`. The test compares with `{ram:false, regions:false, screen:true, tails:false}` and asserts no divergence, so it fails until every listed sprite/effect is ported. It is `#[ignore]`d by default only so the rest of the workspace stays green; it is not weakened. |
| **status** | **Open (M5a partial).** The assertion has not been weakened or split; the exact first hashes are printed above. |

## Suspected, to confirm during Phase 1/2

| # | Area | Suspicion | How to confirm |
|---|---|---|---|
| S1 | `Smb3ObjectConverter.Rules` / `campaign/backlog.json` | Campaign areas report missing enemy behaviours; unported ids despawn via `MarkObjGone`, so "some levels have no enemies". This is the documented `native-partial` state, not a crash. | Load a `native-partial` area and compare its object list to `unmappedObjects`. |
| S2 | `Runtime/LevelSession.cs` `StepPipe` | Pipe entry uses fixed pixel windows (`<=8`/`<=6`/`<=12`) and hard-coded durations; the brief calls pipes "buggy". Behaviour must be ported exactly. | Input scripts entering/exiting every pipe mouth in 1-1/1-2/1-3. |
| S3 | `extracted/*` vs C# `Extractor` quirks | The extractor has documented "kept quirks" (e.g. unstable ring sort, sign-extended offsets). Any mis-port changes behaviour; reproduce the *effective* behaviour. | Compare Rust extraction output byte-for-byte against `sonicbros3.bin`. |

## Non-game issues (not behaviour; for awareness)

* **Derived-artifact mutation**: running the game rebuilds `sonicbros3.bin` when `Extractor.Version` changes. Not a game
  bug, but golden-trace provenance must pin/regenerate it. See `PORT_LOG.md` 2026-10-19 finding 4.
