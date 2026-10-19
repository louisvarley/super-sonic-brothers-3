# Report 3: Collision, objects, rings, PLC and animated art (2026-10-16)

## Done (each validated against MAME RAM/VRAM dumps unless noted)

| Data | Source | Output | Validation |
|---|---|---|---|
| Collision indexes | `LoadCollisionIndexes` 0x5C12, Kosinski | `zones/*/collision.json` | Byte-exact for used blocks in 4 zones |
| Collision sets | Floor/wall code 0x22670+ | `collision/normal.json`, `alternate.json` | Set choice matches `$FFFFE6` in all 4 zones |
| Object placement | `Off_Objects` 0xFC756 | `act_N/objects.json` | Manager pointers land on records inside each list; live slots agree |
| Rings | `Off_Rings` 0xF56EC | `act_N/rings.json` | Expanded+sorted table byte-exact in 4 zones |
| PLC lists + Nemesis art | 0x46CD4 | `plc/plc_lists.json`, `art/nemesis/` (274 blocks) | Through composed VRAM |
| Animated level tiles | 0x43FB8 | `zones/*/animated_tiles.json`, `art/animated/` | VRAM exact for zones 00/11/15 at the live frame |

`LevelLoader` now composes a full VRAM image (level art + load-time PLCs + animation frame 0). Previews are
rendered through it, so they also test the game's load path.

## Hack findings

* **Second collision set.** The hack's zones (`>= $11`) use their own angle/height/width arrays, chosen by
  `$FFFFE6`, which `Level` sets at 0x4C56. The masks differ visibly from Sonic 2's (`collision/*_heights.png`).
* The hack's object lists have no leading boundary record (retail lists start with `FFFF 0000 0000`). The port's
  object manager must not rely on one.
* Zones 13/14 use a custom tile animation routine (0x4423A, art at 0x1114F6), like HTZ's (0x4402E).

## Corrections to earlier assumptions

* Table offsets for objects, rings and PLCs are **sign-extended**: the ROM uses `adda.w` / `lea (a1,d0.w)`.
  Treating them as unsigned gave garbage for the hack's zones.
* Ring order: the ROM's exchange sort is not stable. A stable sort matched only 189/226 EHZ rings, and the
  ROM algorithm matches all of them.

## Open

* Custom animation code (HTZ, CNZ, ARZ, zones 13/14) and palette cycling: game logic, still to port.
* Sprite mappings and animations: their pointers live in object code, so they come with the object port
  (one object at a time).
* PLCs requested at runtime (HUD `02`, `32`, `41`, animals...): data is extracted; when each is loaded is logic.
* Audio: sound driver identification is done (report 1), but no extraction yet.

## Next

1. Audio: identify both drivers' music/SFX pointer tables and dump sequence data; DAC samples.
2. Runtime shell skeleton (MonoGame or SDL2): load an act through `LevelLoader`, scroll the camera, draw both planes.
3. Start the logic port with the player object, checked frame by frame against `trace.csv`.
