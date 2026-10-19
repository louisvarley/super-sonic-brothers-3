# Report 8: Background nametable and Sonic's sprite (2026-10-16)

## Done

* **Background plane B as on hardware:** a 64x32-cell nametable filled by ports of the game's own drawing code.
  `DrawInitialBG` (0x1050E) runs at level start, and the background part of `LoadTilesAsYouMove` (0xFB16) runs each
  frame, where each layer redraws only its own band. Rows nobody redraws keep their initial contents and wrap every
  512 px. This is what the water and cloud bands rely on. `InitCameraValues` (zone `$11`) sets the layers' starting positions.
* **Sonic's sprite:**
  * art, DPLCs and mapping frames extracted (`objects/sonic/`: 2585 tiles, 214 frames)
  * `LoadSonicDynPLC`, `DisplaySprite` (with invulnerability blinking) and `BuildSprites` (1P, with the original's
    unchecked 80-sprite limit in flipped pieces)
  * the VDP draws sprites from the RAM sprite table with hardware ordering, limits and priority
  * the placeholder box is gone.

## Validation

| Check | Result |
|---|---|
| Plane B vs VRAM `$E000` (level start, frames 1140, 1300) | 2048/2048 cells each |
| Full screen vs MAME, frame 1140 / 1300 | 94.5% / 96.4% (was 85.2% before this step) |
| Sonic's sprite | pixel-exact |
| Game path (`player-world`) and co-simulation | unchanged: full match except object effects |

Everything left on screen is objects that aren't ported yet (Tails, the Motobug, the monitor), ring sprites and the HUD.

## Next

1. **Zone `$11` objects:** the hack's object table for this zone, starting with rings (RingsManager, which also
   draws ring sprites), the monitor and the Motobug. Then platforms, which remove the co-simulation's remaining
   differences.
2. **HUD:** score, time, rings and lives, a fixed set of screen-space sprites.
3. **Title card and level start**, so no seeding of title-card phase is needed.
