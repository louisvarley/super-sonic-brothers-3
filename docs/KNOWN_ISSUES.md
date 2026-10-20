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

## M5a software renderer (closed)

| | |
|---|---|
| **id** | K4 |
| **file/function** | `engine-core::{vdp,background_plane,animated_tiles,plc,sprite_builder,hud}` and `Session::render`; the legacy oracle is `Runtime/Vdp.cs` + `GoldenTrace`'s 30-tick `screenHash`. |
| **state** | **Closed.** All 24 required level traces match all 240 framebuffer hashes (600 ticks, every 30): SMB3 1-1/1-2/1-3 and Sonic EHZ/GHZ/MZ act 1, each idle/right/jump/spindash. |
| **reproduction** | `cargo test -p engine-core --test render_motion -- --nocapture` (not ignored). |
| **closing fixes** | Game Over `$39` waits for PLC 3 and spawns both halves from the player check; explosions animate on their spawn tick (needed by the frozen 1-3 spindash screen); Buzz Bomber/missile, Masher, Buzzer, Coconuts, Chopper and lost-rings animation blocks were wired in; dust skid puffs spawn; EHZ's near-hills background advances once per five-line band. |

## M5c front-end screens: title and map exact, card game pending (K5-match)

| | |
|---|---|
| **id** | K5 |
| **state** | **Title, title?map and map are exact.** `frontend-title` matches all 600 ticks, `frontend-title-map` all 700 ticks, and `frontend-map` all 400 ticks, including every 30-tick framebuffer hash. `Sb3TitleScreen` ports the curtain, logo streams, drop/shake, glow, palette fades, 1P/2P menu, skip/timeout, Sonic/Tails/Goomba/Koopa actor scripts and title line scroll. |
| **remaining** | `frontend-match` is still the placeholder and diverges at tick 30 (legacy `16094679856854692594`, port `15085034060434167613`). Bonus-room tile-memory/CHR-bank composition, text/status layout and the N-spade card phase are not ported. |
| **reproduction** | `cargo test -p engine-core --test frontend_screens -- --nocapture` (strict title/title-map/map); `cargo test -p engine-core --test frontend_screens -- --ignored --nocapture` (K5-match). |

## M6a 68k audio: exact against the legacy oracle (closed for SMB3 levels)

| | |
|---|---|
| **id** | K6 |
| **file/function** | `legacy/src/SonicBros3/Audio/{Smps68kDriver,Ym2612,Sn76489,SoundMixer}.cs`; port in `crates/engine-core/src/audio/` and `crates/rom-assets/src/audio.rs`. |
| **state** | **Closed for the 68k path.** The driver is ported routine-for-routine, including queue/priority handling, coordination flags, tempo/fade/speed, SFX/special-SFX allocation, DAC requests and the 1-up save/restore. `Ym2612` and `Sn76489` are direct ports; `SoundMixer` renders 48 kHz stereo. `Session::enable_audio` sets `$FFE5`/`$FF90`, queues the act's music (`zones/zone_XX/music.json`, id $94/$95/$96 for 1-1/1-2/1-3), and runs `UpdateSound` after VBlank. Native output uses `cpal`; `--mute` disables it. |
| **verification** | `audio_driver_parity` drives zone 1-1's $94 song for 3000 VBlanks and matches the legacy C# driver byte-for-byte in RAM `$F000-$F2FF` and the FNV of every rendered sample stretch at frames 1/2/5/10/30/60/120/240/600/900/1200/1800/3000. `audio_smoke` verifies 1-1/1-2/1-3 are non-silent and audio does not change the framebuffer/camera/level counter. |
| **reproduction** | `cargo test -p engine-core --test audio_driver_parity --test audio_smoke -- --nocapture`. |
| **still open** | Sonic-zone Z80 (`legacy/src/SonicBros3/Audio/SmpsZ80Driver.cs`) is untouched; the front-end/title music also needs the Z80 path. Gameplay SFX requests are not wired to the driver yet. |

## M6b gameplay SFX: Sonic 68k + SMB3 PCM

| | |
|---|---|
| **id** | K6b |
| **state** | Sonic events use the existing 68k SFX queue; SMB3 events use generated PCMs rendered from the source sound engine. The PCM player overlaps effects and mixes after the SMPS mixer. |
| **mapping** | bump/kick/pipe/fire = QPlayer 02/08/10/20; coin/powerup/1up = QLevel1 01/20/40; crumble/boomerang = QLevel2 01/04; map-world/move/enter = QMap 01/02/04; course-clear/death = QMusic1 20/01. Sonic jump/ring/hurt/skid/roll/spindash/spring/spikes/monitor/shield/starpost use their existing Sonic IDs. |
| **open** | Pipe entry and a few rare object events have no dedicated Mario PCM yet. Title/map Z80 music is still unported. |

## M7 campaign: metadata, all-world maps and area loading (closed for loading; gameplay partial)

| | |
|---|---|
| **id** | K7 |
| **state** | **Campaign structure and map rendering complete.** `rom-assets::CampaignData` reads `campaign/campaign.json` (8 worlds, 19 screens, 257 areas: 255 available, 1 `failed`, 1 `blocked`) and exposes world/screen ownership, source metadata, widths/heights and alternate ids. `LevelData::load_dir` loads `zones/area_*/act_1`; all 255 available areas load and step 60 ticks without panic. `MapScreen`/`GameFlow` use each world's map cells, palette, entries, paths, panels, cards, intro box and status bar; World 1 remains byte-identical with or without campaign metadata, and `frontend-map` stays exact. Native campaign mode loads map panels, starts the area's `Session`, returns to the map on death/goal, carries lives/score/rings/cards, and plays map/area 68k music. |
| **verification** | `campaign_metadata`: counts and ownership. `campaign_areas`: 255/255 available areas loaded and stepped. `campaign_maps`: all 19 screens render non-empty. `campaign_map_enter`: World 1 movement reaches the 1-1 panel. `campaign_world1_map`: World 1 hashes match the base archive for 400 ticks. Native unit tests cover map music and death returning to the map. |
| **reproduction** | `cargo test -p rom-assets --test campaign_metadata -- --nocapture`; `cargo test -p engine-core --test campaign_areas --test campaign_maps --test campaign_map_enter --test campaign_world1_map -- --nocapture`; `cargo test -p sonic-bros-3 -- --nocapture`; `sonic-bros-3 --data web-data/campaign.bin --list-campaign`. |
| **open** | Source map commands, world transitions, locks, bridges, wandering enemies and world-clear boss events are not ported (the manifest's own blockers). The failed area is `ts01-15-A4F9-C166` (`opcode $00 at $CFD7 not emulated`); the blocked area is `ts08-18-B481-CE4B` (vertical 180 rows, current layout supports 128; no native goal/event adapter). Some `native-partial` areas still lack native actor behaviors and goal adapters. Map pipe travel, the N-spade card game and the title/title-map screens remain K5/partial. Game over currently starts a fresh World 1 map rather than the still-placeholder title. Campaign acts 256/257 load their correct directories but the legacy byte act field in Session reports 1. |

## Suspected, to confirm during Phase 1/2


| # | Area | Suspicion | How to confirm |
|---|---|---|---|
| S1 | `Smb3ObjectConverter.Rules` / `campaign/backlog.json` | Campaign areas report missing enemy behaviours; unported ids despawn via `MarkObjGone`, so "some levels have no enemies". This is the documented `native-partial` state, not a crash. | Load a `native-partial` area and compare its object list to `unmappedObjects`. |
| S2 | `Runtime/LevelSession.cs` `StepPipe` | Pipe entry uses fixed pixel windows (`<=8`/`<=6`/`<=12`) and hard-coded durations; the brief calls pipes "buggy". Behaviour must be ported exactly. | Input scripts entering/exiting every pipe mouth in 1-1/1-2/1-3. |
| S3 | `extracted/*` vs C# `Extractor` quirks | The extractor has documented "kept quirks" (e.g. unstable ring sort, sign-extended offsets). Any mis-port changes behaviour; reproduce the *effective* behaviour. | Compare Rust extraction output byte-for-byte against `sonicbros3.bin`. |

## Non-game issues (not behaviour; for awareness)

* **Derived-artifact mutation**: running the game rebuilds `sonicbros3.bin` when `Extractor.Version` changes. Not a game
  bug, but golden-trace provenance must pin/regenerate it. See `PORT_LOG.md` 2026-10-19 finding 4.
