# tools/golden

Generates the Phase 1 legacy golden traces in `tests/golden/`. See `tests/golden/README.md` for the format and the
level/scenario list.

```sh
node tools/golden/generate.mjs               # 52 core traces
node tools/golden/generate.mjs --campaign   # + every non-blocked campaign area (idle/right)
node tools/golden/generate.mjs --only smb3-1-1-idle --raw
```

The generator:

1. builds `legacy/src/SonicBros3` (Debug);
2. for each scenario, runs the legacy exe with `--golden … --no-build --data … --scenario … --frames … --inputs …`;
3. gzips the `.sb3g` and deletes the raw file (unless `--raw`);
4. writes `tests/golden/manifest.json` with file hashes, the legacy commit, and the pinned oracle-archive hashes.

The legacy CLI flags (added in `legacy/src/SonicBros3/Program.cs`, gameplay unchanged):

| Flag | Meaning |
|---|---|
| `--golden PATH` | write a `.sb3g` trace instead of starting the window |
| `--scenario NAME` | scenario name in the header (defaults to the output file name) |
| `--frames N` | ticks to step |
| `--inputs script.txt` | input script (`<frame> <buttons>`; existing `RefEmu` format) |
| `--hash-every K` | render + RGBA hash every K ticks (default 30) |
| `--snapshot-every N` | write a full JSON snapshot every N ticks (default 60) |
| `--zone 0xNN --act N` | step a `LevelSession` directly (Sonic zones / campaign) |
| `--campaign-act N --world W` | campaign area (needs `--data web-data/campaign.bin`) |
| `--debug-start X,Y` | level test start position (as the existing tests use) |
| `--no-build` | never rebuild the asset archive; fail if missing |
| `--list-campaign` | print the campaign areas as JSON (read-only) |
| `--level-ram-hash` | print level-data RAM region hashes (milestone-2 oracle; read-only) |
| `--dump-math` | print the exact sine/arctan tables (embedded in `engine-core`) |

`--golden` forces `chips = null` (no sound driver) so RAM is deterministic and independent of floating-point chip
models. Interactive runs and `--screenshot` are unchanged.
