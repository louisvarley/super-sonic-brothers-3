# Original-engine browser host

The native-JavaScript recreation was rejected because it changed mechanics and
lost presentation. The approved approach is **Node.js hosting the original C#
engine compiled to browser WebAssembly**, not a new game simulation.

## Architecture

* [Browser project](../src/SonicBros3.Browser/SonicBros3.Browser.csproj) compiles
  the existing engine sources directly. Only the desktop entry point and
  MonoGame host are excluded. Physics, combat, software VDP, title, world map,
  goal cards, minigame, Tails AI and audio drivers are shared unchanged.
* [Browser bridge](../src/SonicBros3.Browser/Program.cs) loads the local asset
  archive into an in-memory filesystem and exports fixed-step input, RGBA
  frames, stereo PCM and state descriptions.
* [Browser host](../web/public/app.mjs) supplies Mega Drive button bits, a 60 Hz
  accumulator, pixel-exact Canvas output and WebAudio playback of the existing
  YM2612/PSG/DAC mix. No synthetic replacement jingles or JS physics remain.
  The initial click enables browser audio. Focus loss pauses and flushes audio.
* [Node server](../web/server.mjs) serves allowlisted UI, published WebAssembly
  resources, conversion metadata and the local extracted asset archive. No
  per-player game process or display server is needed.
* [Build script](../web/build.mjs) publishes only when engine sources change.
  Docker publishes in its SDK stage; production Node does not invoke .NET.

No browser resource or asset is downloaded from a third-party CDN.

## Asset migration status

The user explicitly approved temporary reuse of their existing local
`sonicbros3.bin` to restore fidelity first. `GAME_ARCHIVE` selects its location.
It is required, not silently substituted. It retains the old importer's asset
provenance, although neither Node nor the browser host reads `rom.bin`.

The stock-ROM pipeline remains in [web](../web): header-based `.md` discovery,
original SMB3 loader execution, stock Sonic/Tails graphics and 8-world catalog.
It does **not yet generate a complete original-engine asset archive**.
Replacing the temporary archive dependency remains unfinished work.

Verified stock Sonic 2 World Rev A offsets (revision-specific):

| Table | Sonic | Tails |
| --- | --- | --- |
| Raw art | `0x50000` | `0x64320` |
| Mappings | `0x6FBE0` | `0x739E2` |
| Dynamic tile lists | `0x714E0` | `0x7446C` |

Player palette: `0x2902`. Keep the stock hash check; different revisions are not
interchangeable. Do not restore a raw modified-ROM requirement to the web host.

## Verification

[Browser parity tests](../tests/SonicBros3.Tests/BrowserParityTests.cs) pin
desktop RGBA frame hashes for the title (600 frames), world map, card minigame
and all three levels (180 frames each). Browser WebAssembly output was compared
to these same six hashes and matched **byte for byte**. Tests also check
non-silent music for every level and both goal messages before returning with a
card. The existing combat/terrain/object tests continue to exercise shared code.

An interactive browser check ran level 1-1 at approximately 60 simulated
updates/second, with original player control and music. This is not a guarantee
for every device: the non-AOT WebAssembly build should be profiled on deployment
targets before considering performance complete.

## Remaining campaign work

The required end goal is all eight worlds with the original game's fidelity.
The native campaign pipeline now builds 255 of 257 source areas and connects
8 world maps / 19 screens. The reviewed original stages remain unchanged.
The new builds are explicitly experimental, with per-area blockers, not 255
finished levels. Missing actors, moving platforms, other hazard/ice semantics,
bosses, event-driven secret routes and world-clear progression remain unfinished.
One source loader failure and one 2880px vertical area are unavailable.

`npm run campaign` seeds a staging directory from the approved base archive,
uses the full-ROM catalog and shared native builder, checks each generated area
through its original title-card sequence and render, writes the manifest and
ranked backlog, then atomically publishes `web-data/campaign.bin`. The base is
never overwritten. Version, ROM SHA-1 and base archive SHA-256 control reuse.
`--strict` returns 2 while any campaign or area blockers remain.

Area identity uses zone `$43` plus a full 1-based session act index (up to 257), not an
overflowing sequence of zone bytes. The loader resolves the act through the
manifest to its own asset directory; music follows that directory across
loads/restarts. Original engine RAM retains only the low act byte; the session
retains the full identity across saves and death restarts. Source area IDs, alternate header targets and world-entry
bindings remain intact. Completion keys include world and map screen.

The browser bridge exposes campaign metadata from the loaded archive; the UI
lets the user inspect each area's blockers and explicitly test it. Normal
map entry still uses the original map, mini Sonic, card handling and game flow.
Direct launches reset progress, as the existing debug buttons do.

[Campaign regression tests](../tests/SonicBros3.Tests/CampaignTests.cs) check all
255 available areas through 180 title-card/initial frames, full identity/act
range, unavailable-area errors and world/screen-scoped completion. All 19 maps
were also rendered in browser WebAssembly (19 distinct frame hashes; World 1
retains its pinned original hash), and a native stage from each of the eight
worlds was stepped for 240 frames with non-silent PCM.

Shared-engine gameplay now includes directional pipe entry/exit, alternate-area
returns, common/bonus rooms, connected transit pipes and paired map-pipe exits.
Pipe subarea loads preserve rings, time, lives, score and player power-up state,
without another title card. Source water cells drive reduced underwater physics
and repeated jump-button swimming strokes for Sonic and Tails. Ball contact
from above or either side breaks plain bricks. Overworld music uses the eight
source map songs; stage entry uses source-sequenced pulse audio and a black
border closing effect. These changes are not JavaScript-only approximations.

The desktop entry point is still available for comparison. New gameplay should
stay in the shared engine, so browser and desktop cannot diverge again.
