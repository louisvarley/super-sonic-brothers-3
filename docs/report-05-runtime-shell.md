# Report 5: Runtime shell (2026-10-16)

## Done

* `src/Game` (MonoGame DesktopGL 3.8.2, .NET 10). It loads an act from `extracted/` through `LevelLoader`, renders
  with a software VDP, runs animated tiles, and has a free camera. A headless `--screenshot` mode is used for validation.
* First-launch extraction is wired in. The game runs with the ROM path pointing nowhere, and if the data folder is
  missing and there's no ROM, it gives a clear error.
* The MAME harness can now take screenshots (`SNAPS=`).

## Validated

* The foreground renders pixel-for-pixel like MAME, apart from sprites: 98.1% and 98.8% of FG pixels match in two
  EHZ demo frames, and every mismatch is under a sprite.
* **The display lags the RAM camera by one frame.** This matters for every future frame comparison.
* The window opens and renders under Xvfb (OpenGL through Mesa). Interactive use needs a real display.
* 26 unit tests pass.

## Next (logic port)

1. **Player object:** movement, ground and air physics, collision against the extracted height/angle data. Compare
   `p1_x/p1_y/p1_xvel/p1_yvel/p1_inertia/p1_angle` frame by frame with `trace.csv` from a fixed input recording.
2. **Camera** following the player, and the per-zone background scroll for zone `$11` (the first level).
3. **Sprites:** the player's mappings and animations come with step 1, and the VDP gets a sprite layer.
