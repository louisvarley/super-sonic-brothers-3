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
