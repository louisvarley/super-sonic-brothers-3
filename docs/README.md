# Documentation

## Current (Rust port)

| Document | Contents |
|---|---|
| [INVENTORY.md](INVENTORY.md) | Phase 0: every module, how Node/C# communicate, asset extraction, entry points, classifications, decisions, workspace layout. |
| [PORT_LOG.md](PORT_LOG.md) | Running log of decisions, discrepancies, verification output and open questions. |
| [KNOWN_ISSUES.md](KNOWN_ISSUES.md) | Bugs/quirk behaviour preserved from the legacy engine (filled during the port). |

Related:

* [`../tests/golden/README.md`](../tests/golden/README.md) — golden-trace format, scenario list, provenance, regeneration.
* [`../tools/golden/README.md`](../tools/golden/README.md) — generator/CLI flags.
* [`../README.md`](../README.md) — project overview and ROM policy.

## Legacy design notes

The original project's documents are kept under [`../legacy/docs/`](../legacy/docs/) as the oracle's reference:

* `sonic-bros-3.md`, `logic-port.md`, `porting-guide.md` — engine, per-object porting, validation.
* `memory-map.md`, `formats.md`, `audio.md`, `conversion.md`, `runtime.md`, `web-port.md`,
  `reference-emulator.md` — ROM layout, data formats, sound, conversion, hosts, MAME harness.
* `report-01` … `report-13` — the original chronological reports.

Those documents use the legacy tree's paths (`src/Game`, `src/S2Port.Extraction`, `web/`). After the move they live
under `legacy/` with the same relative layout, so read `src/…` as `legacy/src/…` and `web/…` as `legacy/web/…`.
