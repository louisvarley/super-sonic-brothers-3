# Golden traces (Phase 1)

These are the **oracle**: tick-by-tick recordings of the legacy C# engine. The Rust port must reproduce them exactly.
They contain **no pixels and no audio**, only per-tick state, region hashes and periodic JSON snapshots.

## Files

* `<scenario>.sb3g.gz` — a gzip-compressed `.sb3g` trace (format below).
* `manifest.json` — every scenario, its source level/zone/act/inputs, the file SHA-256, and the SHA-256 of the
  legacy asset archives used to record it.
* `inputs/*.txt` — the input scripts in the existing `tools/RefEmu` format (`<frame> <buttons>`, e.g. `201 R`).
  Buttons: `U D L R B C A S` (Mega Drive pad bits 0–7).

## `.sb3g` format (all integers little-endian)

```
"SB3GOLD1"                         8 bytes
u32 headerLength, UTF-8 JSON header
records until EOF:
  u8 kind = 0 (tick):
    u32 tick, u8 phase, u8 zone, u8 act, i32 cameraX, i32 cameraY
    Entity sonic (21 bytes), Entity tails (21 bytes)
    u16 rings, u32 score, u32 timer, u8 lives, u8 gameMode
    u64 wholeRam
    u64 regions[header.regions.len()]      // objects, level, camera, rings, sound, hud
    u64 screenHash                          // 8-byte slice of SHA-256(RGBA 320x224), 0 on non-hash ticks
  u8 kind = 1 (snapshot):
    u32 tick, u32 jsonLength, UTF-8 JSON   // occupied objects, RAM SHA-256, phase/camera
  u8 kind = 2 (end):
    u32 tickCount
```

`Entity` = `i32 x, i32 y, i16 xv, i16 yv, i16 inertia, u8 angle, u8 status, u8 status2, u8 routine, u8 anim,
u8 frame, u8 subtype`.

Per-tick `act` is the low byte, as 68k RAM `$FE11` stores it. Campaign areas can have a session act index above 255
(up to 257); the full value is in the header (`Act`) and in every snapshot's `act`. Compare the session identity from
the snapshot, and the low byte from the tick. This mirrors the engine: RAM keeps the byte, the session keeps the index.

Framebuffer hashes are recorded every `header.hashEvery` ticks (30). `screenHash` is the low 64 bits of
SHA-256 over the RGBA output **after** the tick's render, exactly as `Vdp.ToRgba` produces it.

## What was recorded

* **Core (52 traces)**: SMB3 1-1/1-2/1-3 and the eight ported Sonic acts
  (`$00` acts 1–2, `$11` acts 1–2, `$12` act 1, `$13` acts 1–2, `$14` act 1), each with
  `idle`, `right`, `jump` and `spindash`, plus goal/death/pipe/hill paths.
* **Campaign (`--campaign`)**: every non-blocked area in `web-data/campaign.bin`, `idle` and `right` for 420 ticks.

Traces include the real level start (title card) for core levels. Gameplay inputs begin after frame 200; the card
ends at ~frame 162 for SMB3 1-1. The header records `debugStart` for traces that start at a test position.

## Which oracle archive?

`manifest.json` pins `sonicbros3.bin` (core) and `web-data/campaign.bin` (campaign) by SHA-256. Those archives are
derived from the **modified Sonic 2 `rom.bin`** and `smb3.nes` and are **not committed** (they are ROM-derived).
`rom.bin` itself is **not** read by the traces; only the derived archives are. The stock-ROM importer is a separate
workstream (see `docs/INVENTORY.md` and `docs/PORT_LOG.md`).

If the archive is rebuilt to different bytes, regenerate the traces and review the diff — the manifest will change.

## Determinism

* No wall clock, no audio device, no sound driver (`chips = null`), fixed 60 Hz stepping.
* Running the same scenario twice produces byte-identical files (verified).
* Audio is excluded on purpose: the YM2612/PSG models use floating point. Audio equivalence is covered by separate,
  non-bit-exact tests (non-silent output + per-band spectrum).

## Regenerate

```sh
node tools/golden/generate.mjs            # core traces
node tools/golden/generate.mjs --campaign # core + all campaign areas
node tools/golden/generate.mjs --only smb3-1-1-idle --raw  # one scenario, uncompressed, for debugging
node tools/golden/generate.mjs --verify   # re-run everything and compare to the manifest hashes
```

Requires the legacy toolchain (`dotnet` 10) and the local archives. The generator builds the legacy engine, runs it
headlessly, gzips the result and rewrites `manifest.json`.

## Level-data oracle (`level-ram.json`)

`level-ram.json` is the milestone-2 fixture: SHA-256 of the legacy `World`'s level-data RAM regions after setup
(chunks, layout, blocks, collision, palette) for zones `$40`, `$41`, `$42`. It is generated read-only by:

```sh
dotnet run --project legacy/src/SonicBros3 -- --level-ram-hash --no-build --data sonicbros3.bin --zone 40 --act 1
```

`cargo test -p rom-assets` loads the same archive through the Rust `LevelData`/`fill_level_ram` and must reproduce
every hash. (`whole` in the fixture includes players/objects/camera and is only compared after the full `World` port;
`tiles` is the legacy composed-VRAM count, 2048, while this milestone loads level art only.)

## Verify from Rust

```sh
cargo test -p golden-trace
```

This parses every committed trace, checks its manifest hash, tick count, framebuffer-hash cadence, and exercises the
first-divergence reporter (`sonic.x`, `regions.objects`, `tickCount`).
