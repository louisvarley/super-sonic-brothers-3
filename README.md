# Sonic Bros. 3 — Rust port

A behaviour-preserving port of the existing Sonic the Hedgehog 2 + Super Mario Bros. 3 hybrid engine
(currently Node.js + C#) to Rust.

> **Status:** Phase 0 (inventory) and Phase 1 (characterisation/golden traces) are complete. Phase 2 is in progress:
> `engine-core` primitives, archive/level loading, collision, player/camera and the SMB3 1-1 object set are ported and
> verified against the legacy traces. The executable, web UI, audio, frontend and packaging are not built yet.
> See `docs/PORT_LOG.md`.

## Rules of the port

* **The legacy implementation is the oracle.** Gameplay must match it tick for tick, including current bugs.
  `legacy/` is kept until the owner says to delete it.
* **No ROMs or extracted assets** in git, Docker images or release artifacts. ROMs are supplied by the user at
  runtime; the shipped build extracts what it needs into a local `data.bin` and loads that. CI fails if a ROM or
  derived asset is tracked.
* **Stock sources.** The shipped asset pipeline targets a stock Sonic 2 ROM plus stock SMB3 (`smb3.nes`). The
  modified `rom.bin` used by the legacy engine is a build artefact, not a shipped input (see
  `docs/INVENTORY.md` §12).
* Engine logic is a port, not a redesign: original fixed-point/integer semantics, update order, timing, collision
  and level loading. Intentional deviations are documented in `docs/KNOWN_ISSUES.md` and the trace diff.

## Layout

```
crates/             Rust workspace (golden-trace implemented; engine-core/rom-assets/platform-* to come)
tests/golden/       committed legacy golden traces (.sb3g.gz), inputs and manifest
tools/golden/       Phase 1 trace generator
legacy/             the existing Node.js + C# implementation (buildable oracle)
docs/               inventory, port log, known issues, and the legacy design notes
```

## Play the Rust port (native, in progress)

```powershell
cargo build --release -p sonic-bros-3
.\target\release\sonic-bros-3.exe --level 1-1
```

The release binary is a single static-CRT `.exe` (no MSVC redistributable). It needs the campaign archive
`campaign.bin` (or the older `sonicbros3.bin`) in the current directory, or `--data PATH`. With campaign data it boots
at the SMB3 title/curtain, then the World 1 map. Controls: arrows + **Z/X/C/Space/Enter**; **P** = pause,
**Esc** = quit. 2-player mode is removed. `--match`, `--cards`, `--map` and `--campaign-act N` are available for
testing.

```powershell
# other reviewed levels
.\target\release\sonic-bros-3.exe --level 1-2
.\target\release\sonic-bros-3.exe --level 1-3
# a Sonic zone (Emerald Hill act 1, Green Hill act 1, Marble act 1)
.\target\release\sonic-bros-3.exe --zone 00 --act 1
.\target\release\sonic-bros-3.exe --zone 11 --act 1
.\target\release\sonic-bros-3.exe --zone 13 --act 1
# headless render + hash (for verification)
.\target\release\sonic-bros-3.exe --level 1-1 --frames 30 --screenshot out.png
```

Audio: the 68k SMPS path (SMB3 levels/map music + Sonic SFX) and Mario PCM effects are working. Regenerate the Mario
effects with `dotnet run --project tools/Smb3Effects -- --rom smb3.nes --out web-data/effects`; the Z80 path used by
Sonic-zone/title music is not ported yet, but the title's 68k SFX are. The N-Spade card game, Sonic item prizes and
pipe travel are ported; the bonus-room/card-game framebuffer traces are strict gates now. 2-player mode is removed
from the menu and build. There is no Rust web/WASM shell yet, and the native game reads the legacy derived archive
for now; the stock Sonic 2 + SMB3 importer (including the missing castle areas) is the remaining asset workstream.

## ROMs

Put your own ROMs **outside git** (they are ignored):

* a stock **Sonic the Hedgehog 2 (World) (Rev A)** `.md` (SHA-1 `8bca5dce…633f9`), and
* **Super Mario Bros. 3 (USA)** `smb3.nes` (SHA-1 `a03e7e52…c449`).

The build/first-run step validates size and checksum, extracts the assets into `data.bin` (ZIP), and the game loads
only `data.bin`. `data.bin` is ignored and must never be committed or put in a release/Docker image.

## Working with the legacy oracle (Phase 1)

Requires .NET 10.

```sh
dotnet build legacy/SonicBros3.slnx
dotnet test legacy/tests/SonicBros3.Tests        # 136 tests
dotnet run --project legacy/src/SonicBros3      # play the oracle (needs the legacy derived archive)
```

Record traces (needs the local derived archives; not committed):

```sh
node tools/golden/generate.mjs
cargo test -p golden-trace
```

See `tests/golden/README.md` for the trace format and scenario list.

## Documentation

* `docs/INVENTORY.md` — every module, how the pieces communicate, how assets are extracted, what runs where.
* `docs/PORT_LOG.md` — decisions, discrepancies, and open questions.
* `docs/KNOWN_ISSUES.md` — bugs preserved from the legacy engine (filled during the port).
* `legacy/README.md` — the original project README; `docs/` also keeps the original design/porting reports.

## Licence

The legacy tree contains no licence file. A licence must be chosen by the project owner before the Phase 3 release
archive is published.
