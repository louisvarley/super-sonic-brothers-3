# Data formats

## Compression

All four are confirmed by finding the decompressor in the ROM and decoding real data without errors.

| Format | ROM routine | Used for | Status in extractor |
|---|---|---|---|
| Kosinski | `0x1B0E` | Level art, 16x16 blocks, 128x128 chunks, layouts, Z80 DAC driver | Implemented (`Compression/Kosinski.cs`) |
| Nemesis | `0x1166` | Object/HUD/badnik art loaded through PLCs | Implemented (`Compression/Nemesis.cs`); all 487 PLC entries decode |
| Enigma | `0x1994` | Plane mappings (title, menus, special stage) | Not yet |
| Saxman | inline at `0x101D70` | Retail-style Z80 SMPS driver | Not yet |

### Kosinski
A 16-bit little-endian descriptor field is read LSB first. When its last bit is consumed, the next field
is read at once, before any data bytes. `1` = literal byte. `01` = full match: two bytes `lo, hi`,
offset = `((hi & 0xF8) << 5 | lo) - 0x2000`, count = `hi & 7` (+2), or if 0 an extra byte
(`0` = end, `1` = no-op, n = n+1 bytes). `00` = inline match: two descriptor bits = count-2, one byte = offset-0x100.

### Nemesis
Header word: bit 15 = XOR mode, bits 0-14 = tile count. Code table: a byte with bit 7 set chooses the
current nibble value. Otherwise the byte is `0RRR LLLL` (run-1, code length) followed by the code
byte, and the table ends at `0xFF`. The bitstream is MSB first. Code `111111` is an escape: 3 bits run-1, then a 4-bit
nibble. XOR mode XORs each 32-bit row with the previous output row.

## Graphics

* **Tiles:** 8x8, 4bpp, 32 bytes, high nibble = left pixel.
* **Colour:** CRAM word `0000 BBB0 GGG0 RRR0`. The extractor converts 3-bit channels linearly (`c*255/7`)
  and always keeps the raw word in the JSON.
* **Pattern word** (blocks, plane maps): bit 15 priority, bits 13-14 palette line, bit 12 V-flip,
  bit 11 H-flip, bits 0-10 tile index.
* **16x16 block:** 4 pattern words (TL, TR, BL, BR).
* **128x128 chunk:** 8x8 block references (word each), 128 bytes; 256 chunks = 32 KiB per zone.

## Extracted output (`extracted/`)

| Path | Content |
|---|---|
| `manifest.json` | Extractor version, ROM SHA-1, and for each item its ROM source address, format and sizes |
| `palettes/pal_XX.json` | `PalPointers[XX]`: start colour/line, raw CRAM words, RGB |
| `palettes/pal_XX.png` | Preview swatch, 16 colours per row |
| `zones/zone_XX/tiles.png` | Level art sheet, 16 tiles wide, 8-bit indexed, 64-entry PLTE |
| `zones/zone_XX/tiles.json` | Tile count, palettes used, encoding, stats |
| `zones/zone_XX/blocks.json` | 16x16 blocks: per block 4 x `[tile, paletteLine, flipX, flipY, priority]` (TL, TR, BL, BR). Index = RAM block number, including animated blocks |
| `zones/zone_XX/chunks.json` | 256 chunks x 8 rows x 8 x `[block, flipX, flipY, solidityPrimary, soliditySecondary]` |
| `zones/zone_XX/act_N/layout.json` | 128x16 chunk IDs for `foreground` and `background`, plus the used extent |
| `zones/zone_XX/act_N/level.json` | Ties an act together: relative paths to tiles/blocks/chunks/layout/palettes, unresolved-reference counts |
| `zones/zone_XX/act_N/chunks.json` | Only where an act overrides the zone's chunks (`$15` act 2) |
| `zones/zone_XX/collision.json` | Collision ID per block, `primary` (layer A) and `secondary` (layer B), plus which set |
| `collision/{normal,alternate}.json` | Angle per ID, 16 signed heights and widths per ID |
| `collision/*_heights.png` | Height masks, 16x16 grid by collision ID (preview) |
| `zones/zone_XX/act_N/objects.json` | Placements `[x, y, id, subtype, flipX, flipY, remember]` |
| `zones/zone_XX/act_N/rings.json` | Ring groups `[x, y, count, vertical]`, 24 px spacing |
| `zones/zone_XX/animated_tiles.json` | Animated tile scripts (art, VRAM tile, frames `[tileOffset, duration]`) and whether custom code is involved |
| `art/animated/unc_*.png` | Uncompressed animated tile art |
| `plc/plc_lists.json` | All 94 PLC lists: art + destination VRAM tile |
| `art/nemesis/nem_*.png` | All 274 Nemesis art blocks used by PLCs (raw 4bpp in the low nibble) |
| `previews/zone_XX_actN_{fg,bg}.png` | Only with `--previews`: software render of each plane |

Act folders are numbered from 1 (`act_1` = act byte 0).

**Bit layouts** (all confirmed by rendering and by byte-exact comparison with emulator RAM):
* Block pattern word: bit 15 priority, 13-14 palette line, 12 Y-flip, 11 X-flip, 0-10 tile.
* Chunk block word: bits 14-15 secondary solidity, 12-13 primary solidity, 11 Y-flip, 10 X-flip, 0-9 block.
  Solidity: bit 0 top, bit 1 left/right/bottom.
* RAM layout: `0x1000` bytes; rows `0x100` apart, FG at `+0x00`, BG at `+0x80`.

**Tileset PNG pixel encoding:** `index = paletteLine * 16 + colour`. The raw 4bpp value is `index & 15`.
The PLTE is the zone's composed 64-colour CRAM (base palette `0x03` then the zone palette), so replacing
the PLTE re-colours the whole sheet. Indices 0/16/32/48 are transparent (tRNS). Each tile's line is
the line most often used by the 16x16 blocks that reference it. The renderer uses the line from the block, so this
only affects how the sheet looks in an image viewer.

**Out-of-range tile references:** some blocks reference VRAM tiles above the level art
(animated art and PLC-loaded art). They are counted in `tiles.json` as `blockRefsOutOfRange` and will be
resolved when PLC/animated art extraction is added. They render as transparent for now.

**Unresolved block references:** after animated blocks are added, only the unused placeholder zones
`01/02/03/09` still reference blocks with no data. They reuse EHZ data without its animated-block entry and
can't be reached in play.

**Collision:** height value h for a column: h > 0 means solid for h pixels up from the bottom; h < 0 means solid
for -h pixels down from the top. Widths are the same for rows. The game's floor code also applies flips
and the 0xFF angle fallback, which belongs to the physics port.

**Rings in RAM** are expanded and sorted with the ROM's exchange sort, which is not stable.
`LevelData.ExpandRings()` uses the same algorithm, so equal-x rings come out in the ROM's order.

**Composed VRAM** (`LevelLoader`): level art at tile 0, then the act's `plcs.onLoad` lists in order, then frame 0 of
each animated-tile script. Tiles written by custom animation code (HTZ, zones 13/14) are not included.
