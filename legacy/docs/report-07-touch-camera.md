# Report 7: Touch collision, hurt, camera, background and palette cycle (2026-10-16)

## Done

* **Touch collision:**
  * rings, enemies (destroy, chain points, bounce), monitors and special collision types
  * hurt (`HurtCharacter`, lost-rings spawn, knockback, invulnerability) and the hurt routine until landing
  * hitbox sizes read from the ROM; my first version had them from memory and was wrong from entry 44 on
* **Camera:** `ScrollHoriz` / `ScrollVerti` with the spindash scroll delay and vertical sub-pixels, plus scroll-flag
  bookkeeping.
* **Zone `$11` world:** the level events (camera max-Y easing) and background deformation, both Sonic 1 Green Hill code
  carried over by the hack, and the palette cycle (table extracted).
* **Rendering:** the game renders from its own RAM: the per-line H-scroll buffer, vertical scroll and live palette.

## Validation

* **Co-simulation:** Sonic + touch + hurt + camera RAM match the emulator in about 1,300 iterations from two
  recordings. The enemy hit, enemy-kill bounce and the whole knockback-and-landing sequence now match. The only
  differences are object effects: the lost-rings object zeroing the ring count, and a solid platform.
* **Game path:** `World` built only from extracted data reproduces Sonic, the camera, the whole H-scroll buffer and the
  palette exactly over 294 and 350 iterations. That requires seeding the title-card phase (cloud, palette-cycle and
  animated-tile counters), because the title card isn't replayed.
* **Full screen vs MAME:** 95% and 85% of pixels match in two frames. The rest is sprites, plus background
  nametable-wrap effects in the water and cloud bands (see logic-port.md).

## Next

1. **Nametable emulation:** port the background/foreground tile-drawing code into a 64x32 nametable model and
   render from it, which closes the water and cloud gap.
2. **Sonic's sprite:** mappings, dynamic art, and a sprite layer.
3. **Zone `$11`'s objects:** the hack's object table, starting with rings, monitors, the Motobug and platforms (which
   remove the remaining co-simulation differences).
4. **Title card / level start:** so the game reaches the first iteration in the same state without seeding.
