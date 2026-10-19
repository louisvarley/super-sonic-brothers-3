# Sonic Bros. 3 - original engine, on the web

The **original C# game's mechanics, software renderer, music drivers and
screens run in the browser using WebAssembly**. Node.js serves the website.
The simplified JavaScript gameplay recreation has been removed.

This preserves the original title sequence, miniature map Sonic and path
movement, Sonic/Tails gameplay, music, goal roulette, "COURSE CLEAR!" /
"YOU GOT A CARD" presentation, map card slots and matching-card minigame.

**End goal: all eight SMB3 worlds, with the original engine's fidelity.**
The campaign pipeline now generates original-engine area assets across the
whole ROM and connects all **8 worlds / 19 map screens**. There are **257 source
areas: 255 experimental native builds and 2 unavailable areas**. The original
three reviewed stages are retained. These numbers do **not** mean the campaign
is complete: bosses, missing actors, hazards, routes and world-clear events
still need native implementations. Each area's blockers appear in the browser.

Pipe entrances now support alternate rooms and returns, connected in-level
transit pipes (including vertical-maze bends), bonus/common-exit rooms, and
paired overworld pipe shortcuts. Hold the direction into an aligned mouth;
decorative pipes remain solid scenery. Swimming uses repeated jump-button
presses for upward strokes. Airborne ball contact breaks plain bricks, while
question/content blocks retain their existing behavior. All eight worlds have
map music, and entering a stage uses SMB3's source-sequenced sound and a
closing black border rather than the Sonic giant-ring sound/fade.

## Temporary asset requirement

With the user's approval, the restored version temporarily uses the existing
local **`sonicbros3.bin` game-data archive**. Keep that file in the project root.
The browser loads it into its own in-memory filesystem. No raw ROM is executed
and `rom.bin` is not read. Missing assets are an explicit startup error: there
is no fallback to the simplified game.

**The full stock-ROM importer for the original engine is unfinished.** The Node
conversion pipeline already reads stock Sonic 2 graphics and SMB3 terrain, but
does not yet generate all engine asset formats, sound data and frontend tables.
Until it does, the restored game archive retains its previous asset provenance.
Do not delete the existing archive or assume it can be rebuilt from stock ROMs.

For the Node conversion/catalog pipeline, also keep:

* `smb3.nes`: SMB3 USA, SHA-1
  `a03e7e526e79df222e048ae22214bca2bc49c449`.
* Stock **Sonic the Hedgehog 2 (World) (Rev A)**, any `.md` filename, SHA-1
  `8bca5dcef1af3e00098666fd892dc1c2a76333f9`.

Discovery validates SEGA headers, title/product identity and revision hash.
Ordinary Markdown is ignored. No ROMs or derived assets are bundled in source.

## Run locally

Install Node.js 22+ (24 recommended) and the **.NET 10 SDK**:

```powershell
npm start
```

To build the whole native campaign first:

```powershell
npm run campaign
npm start
```

This reads `smb3.nes` and your existing `sonicbros3.bin`, writes a separate
`web-data/campaign.bin`, and never overwrites the base archive or reads
`rom.bin`. Node automatically uses this derived archive when present. Repeated
campaign runs reuse the output when the converter version and both input
identities match. Use `npm run campaign -- --force` after adapter changes, or
`--strict` to return exit code 2 while gameplay coverage is incomplete.

Open **http://localhost:3000**. The first start publishes the original engine to
WebAssembly; later starts reuse it until its sources change. No npm dependencies
or `npm install` are required. .NET is a build tool, not a game server: gameplay
runs on the user's browser, not in a server-side desktop process.

Settings:

* `ROM_DIR`: directory with both ROMs and, by default, the existing archive.
* `GAME_ARCHIVE`: explicit archive path; otherwise use
  `ROM_DIR/web-data/campaign.bin` if present, then the original archive.
* `PORT`: defaults to 3000.
* `HOST`: defaults to `127.0.0.1`.

## Docker Compose

Install Docker Desktop with Linux containers:

```powershell
docker compose up --build -d
docker compose logs -f game
docker compose down
```

Open **http://localhost:3000**. Docker builds the WebAssembly engine in a .NET SDK
stage. A one-shot `convert` service builds/reuses the native campaign in a
named volume before the Node-only game service starts. No .NET installation is
needed on the host. ROMs and the base archive are excluded from the image and
mounted read-only. The game service is non-root with a read-only filesystem
and read-only access to the generated campaign volume.

For a dedicated data directory, put all three local input files there:

```powershell
$env:ROM_DIR = 'C:\Games\MyRoms'
docker compose up --build -d
```

Keep this private. The server exposes derived game assets to the browser and
has no authentication. Docker configuration has not been run locally where
Docker is unavailable.

## Controls

* **Click Start Adventure to enable music**, then Enter starts/skips the original
  title sequence or enters the selected map panel.
* Arrows move; Z/X/C or Space jump; Down rolls.
* Down + tap jump charges a spindash; release Down to launch.
* P/Escape pauses. Touch controls and standard gamepads are supported.
* Stomps remove wings or make shells; rolling/spindashing defeats enemies in one
  hit. Ordinary plants do not shoot; source fire variants do.

The map is the original screen, not a stage-selection recreation. Direct stage
and minigame buttons are debugging shortcuts that **start a fresh game**;
"Restart at map" likewise resets progress. Use the actual map and goal flow to
carry cards, lives and cleared panels through an adventure. Progress is
in-memory, as in the desktop host; the removed JavaScript prototype's save data
is not imported.

With a campaign archive, **Campaign conversion browser** opens every world's
source map, selects any of its screens, and lists native areas plus their
missing behaviors. "Test native area" is explicitly experimental, not a claim
that the level can be completed. The direct launch controls reset the game.

## Development and conversion

```powershell
npm run build
npm test
npm run test:engine
npm run campaign
npm run campaign -- --strict
npm run convert -- --output web-data
npm run convert -- --strict
```

`npm test` validates Node conversion, exact original-loader terrain hashes,
all-world discovery, export persistence and HTTP allowlisting.
`npm run test:engine` tests the **actual shared C# gameplay**: combat, movement,
plants, goals, non-silent music and original-screen reference hashes. Asset
tests need the local archive; ROM tests need the local ROMs.

The Node `convert` command exports intermediate data, not native levels. The
new `campaign` command additionally builds native tiles, blocks, collision,
chunks, objects, rings, palettes, music, supported CHR animation and runtime
map bindings. Its archive includes `campaign/campaign.json` and an object
backlog ranked by affected areas. One source loader failure and one vertical
area exceeding the current 2048px layout height remain unavailable; missing
gameplay is separately reported for generated areas.

See [browser architecture and parity verification](docs/web-port.md).
