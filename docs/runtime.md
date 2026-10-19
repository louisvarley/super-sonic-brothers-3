# Runtime shell (`src/Game`)

```sh
export PATH=$HOME/.dotnet:$PATH
dotnet run --project src/Game -- --zone 11 --act 1                         # play: arrows, Z/X/C = A/B/C, Enter = Start
dotnet run --project src/Game -- --zone 00 --act 1 --freecam 637,561       # level viewer (arrows move camera)
dotnet run --project src/Game -- --zone 11 --act 1 --inputs script.txt \
    --frames 300 --screenshot out.png                                       # headless replay of an input script
```

* **First launch:** `Extractor.EnsureExtracted` runs if `extracted/manifest.json` is missing or was made from another ROM or
  extractor version. After that the game reads only `extracted/`, and the ROM is not needed. This was checked
  with `--rom /nonexistent/rom.bin`.
* **`Runtime/Vdp.cs`:** software plane renderer into a 320x224 buffer of CRAM indices, in hardware priority
  order (backdrop, BG low, FG low, BG high, FG high). Tiles are resolved layout -> chunk -> block -> tile, and a
  block flip moves the quadrant and toggles the tile's own flip.
* **`Runtime/AnimatedTiles.cs`:** the generic animated-tile interpreter, using the ROM's timer semantics
  (`subq`/`bcc`; a frame shows for duration + 1 frames).
* **`S2Game.cs`:** MonoGame DesktopGL host. Runs at 60 Hz, converts CRAM to RGB, and draws scaled 3x with point sampling.

## Validated

The foreground render was compared with MAME screenshots of the zone `$00` demo (`SNAPS=... tools/RefEmu/run.sh`):

| Frame | Camera (from trace, previous frame) | FG pixels matching MAME |
|---|---|---|
| 10500 | 101, 533 | 98.1% |
| 10600 | 637, 561 | 98.8% |

All of the mismatches are under sprites (Sonic, Tails, lives counter), which aren't drawn yet.
**The displayed scroll is one frame behind the camera in the RAM trace**: the scroll registers are written during VBlank.
Comparisons must use the previous frame's camera.

## Not yet

* Sprites (needs object mappings, which come with the object port).
* Real background scrolling and per-line deformation. Each zone has its own code, so the BG uses a placeholder.
* Palette cycling, water/HInt palette split, custom tile animation (HTZ, CNZ, ARZ, zones 13/14).
* Audio output (data is extracted; the SMPS player and YM2612/PSG are not written).
