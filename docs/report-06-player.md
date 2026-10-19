# Report 6: Player logic port (2026-10-16)

## Result

Sonic's movement is ported and **matches the original exactly, frame by frame**, everywhere objects aren't involved:
* every byte of his object slot and the globals he owns
* ground running, friction, skidding, slopes
* rolling, the spindash, jumping, variable jump height, air control and drag
* landing on flat and sloped ground, wall stops, looking up/down, the idle-animation physics suppression
* the animation timers that feed back into physics

It is playable in `src/Game` (arrow keys + Z/X/C) with a placeholder box for Sonic.

## How it was validated

* **Per-iteration capture:** the reference emulator records full RAM once per game-logic iteration, using a write
  tap on the level loop's frame counter.
* **Co-simulation:** the C# Sonic runs inside the emulator's world, with everything except his own state copied in
  each iteration, and his state is compared with the original's every iteration.
* **Independent check:** a second check builds the world only from extracted files and reproduces the same
  trajectories from the same inputs.

Details: [logic-port.md](logic-port.md).

## Findings along the way

* **Sonic's code is shared; objects aren't.** Sonic's code is retail Sonic 2 (Rev01) and the same in all zones, but the
  object dispatcher (0x19990) uses a different code table in the hack's zones: 185 of 223 object IDs have
  hack-specific implementations there.
* **Hack changes in collision:** the floor/wall finders use the alternate collision set in the hack's zones, and the
  vertical wrap (`andi.w #$7FF,y`) is disabled there.
* **Lag frames:** these make per-video-frame comparison unreliable; per-iteration capture fixes that.
* **Title screen:** the title needs three Start presses (frames 400/500/600) to begin a new game.

## Next

1. **Object interaction:** `TouchResponse` (rings, enemies, hurt) and `SolidObject`, then the hurt/death routines.
   That ends the remaining divergences in the recordings.
2. **Camera:** `ScrollHoriz/ScrollVert` (replacing the placeholder) and zone `$11`'s background scroll.
3. **Sonic's sprite:** mappings, dynamic art (DPLC) and a sprite layer in the VDP.
4. **Objects for zone `$11`:** the hack's own object set (rings, monitors, enemies, springs) via its code table.
