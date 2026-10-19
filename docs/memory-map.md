# Memory map

Everything here was confirmed against `rom.bin` (SHA-1 `6651a52a04caeec071b8e469308caa5b5c4cdd2f`)
by disassembly or by decoding the data. Items marked **(inference)** are not yet confirmed and say
how to check them.

Labels in `CamelCase` follow the community Sonic 2 disassembly (s2disasm) because the engine is Sonic 2's.
Treat that naming as a reading aid: the hack has moved and extended many routines.

## Cartridge header / vectors

| Field | Value |
|---|---|
| Initial SSP | `$FFFE00` |
| Entry point (`EntryPoint`) | `0x000218` |
| VBlank / level-6 (`V_Int`) | `0x000412` |
| HBlank / level-4 (`H_Int`) | `0x001032` |
| All other exceptions | `0x000200` |
| Title | `SONIC THE HEDGEHOG 2`, `GM 00001051-01`, `(C)SEGA 1992.SEP` |
| ROM end in header | `0x1FFFFF` (2 MiB; retail Sonic 2 is 1 MiB) |
| SRAM declared | `RA F8 20`, `$200001-$2003FF` (odd bytes); retail has none |
| Checksum | header `0x80A5` = computed `0x80A5` |

## ROM layout (by content)

| Range | Content |
|---|---|
| `0x000000-0x0FFFFF` | Sonic 2 engine and data, layout broadly retail-like but with relocated tables |
| `0x100000-0x167FFF` | Hack additions: code (e.g. `0x101D26`, `0x1301DC`, `0x15E282`, `0x164EB8`, `0x1658A4`) and data (art for zones `0x11+`, Z80 driver at `0x131576`) |
| `0x168000-0x1FFFFF` | Zero padding |

## Code

| Address | Name | Notes |
|---|---|---|
| `0x000218` | `EntryPoint` | Standard Sega TMSS/init block, VDP init table at `0x2AA` |
| `0x000316` | post-init | Warm-boot check `'init'` at `$FFFFFC`, clears RAM, then `0x1278`, `0x1472`, `0x120C` |
| `0x000388`..`0x000394` | hack init | `jsr 0x1658A4` / `0x1659BE` (SRAM load), `jsr 0x164EB8` (hack state init) |
| `0x0003A0` | `MainGameLoop` | `move.b Game_Mode,d0; andi #$3C; jsr GameModesArray(pc,d0.w)` |
| `0x0003AE` | `GameModesArray` | 13 `bra.w` entries (modes `$00-$30`) |
| `0x000412` | `V_Int` | Dispatches on `Vint_routine` (`$FFF62A`) through word table at `0x47E` |
| `0x001166` | `NemDec` | Nemesis decompressor |
| `0x001278` | `VDPSetupGame` | VDP register table at `0x1302` |
| `0x00142A` | sound driver switch | Sets `$FFFFE5` from `$FFFFE6` and reloads the driver (see docs/audio.md) |
| `0x001472` | `SoundDriverLoad` (patched) | Branches on `$FFFFE5` |
| `0x0014DC` / `0x0014FA` | `PlayMusic` / `PlaySound` | Route to `$FFE0/E1` or `$F00A/B` by driver |
| `0x0017F2` | `LoadPLC` | Uses PLC table `0x46CD4` |
| `0x001994` | `EniDec` | Enigma decompressor |
| `0x001B0E` | `KosDec` | Kosinski decompressor |
| `0x002E26` | `PalLoad_ForFade` | Writes to `PalPointers.dest + $80` (fade target buffer) |
| `0x002E42` | `PalLoad_Now` **(inference: name)** | Same table, writes to `dest` |
| `0x004C2A` | `Level` (game mode `$0C`) | Loads zone PLCs from `LevelArtPointers` at `0x4CB6` |
| `0x0063C2` | `LoadZoneTiles` | Kosinski art to `$FFFF0000`; HTZ/WFZ/DEZ special cases |
| `0x010696` | layout loader | Kosinski layout to `$FFFF8000`, index `zone*2+act` (`ror.b #1` / `lsr.w #6`) |
| `0x0106B6` | Sonic 1 layout loader (dead) | No callers; uncompressed FG/BG format, retail leftover |
| `0x044810` | `LoadAnimatedBlocks` (inference: name) | Copies raw block words into the top of `$FFFF9000`; HTZ also calls `0x44D14` |
| `0x005C12` | `LoadCollisionIndexes` | Kosinski primary -> `$FFFFD600`, secondary -> `$FFFFD900`; sets `$FFFFF796` |
| `0x01B03A` | rings setup | Expands `Off_Rings` into `$FFFFE806`, exchange-sorts by x (`0x1B0B6`) |
| `0x01B83C` | `ObjectsManager_Init` | `Off_Objects`; 2P CNZ lists at `0x1BDA6` / `0x1C20E` |
| `0x01BCCA` | object spawn from placement | x->`$08`, y->`$0C`, flags->`$01`/`$22`, id->`$00`, subtype->`$28` |
| `0x022670`.. `0x022958` | floor/wall finders | Pick collision set by `$FFFFE6` |
| `0x043F98` | animated level tiles dispatch | Table `0x43FB8`; generic interpreter `0x443EC`; custom `0x4402E` (HTZ), `0x44210` (CNZ), `0x44230` (ARZ), `0x4423A` (zones 13/14) |
| `0x0017F2` / `0x001824` | `LoadPLC` / `LoadPLC2` | Queue into `$FFFFF680` (6-byte entries) |
| `0x0105D4` | `LevelDataLoad` | Blocks to `$FFFF9000`, chunks to `$FFFF0000`, layout to `$FFFF8000`, zone palette |
| `0x101D26` | S2 `SoundDriverLoad` (relocated) | Inline Saxman decompressor at `0x101D70`, output length `$F66` |
| `0x1301DC` | 68k sound driver update | Called from `V_Int` when `$FFFFE5` != 0 |
| `0x165868`..`0x165A80` | SRAM routines | Read/write odd bytes at `$200003+` |

## Data tables

| Address | Name | Format |
|---|---|---|
| `0x002E96` | `PalPointers` | `0x35` x {u32 src, u16 RAM dest, u16 longs-1} |
| `0x046B78` | `LevelArtPointers` | `0x1D` x 12 bytes: {u8 PLC1, u24 art(Kos)}, {u8 PLC2, u24 16x16 blocks(Kos)}, {u8 palette, u24 128x128 chunks(Kos)} |
| `0x046CD4` | PLC offset table | `0x5E` signed word offsets -> {s16 count-1, n x {u32 Nemesis src, u16 VRAM dest}} |
| `0x005C3E` / `0x005CB2` | `Off_ColP` / `Off_ColS` | u32 per zone, Kosinski collision indexes (unused slots 01/03/09 point at `0x4CC1A`) |
| `0x0477EA` / `0x0478EA` / `0x0488EA` | Sonic 2 collision set | Angle map (256), heights (256x16), widths (256x16) |
| `0x04A518` / `0x04A618` / `0x04B618` | Alternate collision set (hack) | Same layout; used when `$FFFFE6`=1 (zones >= `$11`) |
| `0x043FB8` | Animated tiles table | Per zone {s16 routine, s16 script list}, relative to `0x43FB8` |
| `0x0F56EC` | `Off_Rings` | 58 signed word offsets -> {x, y\|count\|vertical} groups, ended by negative x |
| `0x0FC756` | `Off_Objects` | 58 signed word offsets -> 6-byte placements, ended by x=`$FFFF` |
| `0x04488C` | Animated blocks | Word offset per zone -> {u16 dest offset from `$FFFF9000` (0 = none), u16 words-1, words}. 2P CNZ uses `0x44C12` |
| `0x04CC1A` | Level layout offsets | 58 word offsets, index `zone*2+act`, to Kosinski layouts of `0x1000` bytes (confirmed against RAM) |
| `0x0A00E8` | HTZ art overlay | Kosinski, overlaid at art offset `0x3F80`, final art length `0x6FC0` |
| `0x0CF4F8` | WFZ art overlay | Kosinski, overlaid at art offset `0x60E0`, final art length `0x6F20` |
| `0x09FBD8` | HTZ block overlay | Kosinski, overlaid at block offset `0x980` |
| `0x0DFDEB` | Zone `$15` act 1 chunks | Replaces table chunks for `$1501` |
| `0x131576` | Z80 DAC driver (68k-SMPS path) | Kosinski, 7110 bytes; 3 DPCM samples |
| `0x12FFEC`-`0x130007` | 68k SMPS pointer block | Music `0x1300FC`, SFX `0x13727E`, special `0x13733E` |
| `0x101E0E` | Sonic 2 Z80 driver | Saxman, `$F66` bytes -> 4872 |
| `0x100000` / `0x108000` / `0x120000` / `0x128000` | Z80 banks | DAC / music / SEGA PCM / music+SFX |

## RAM variables seen so far

| Address | Name | Notes |
|---|---|---|
| `$FFFF0000` | chunk / art buffer | |
| `$FFFF8000` | level layout | |
| `$FFFF9000` | 16x16 block mappings | |
| `$FFFFD600` / `$D900` | Collision index layer A / B | 0x300 bytes each |
| `$FFFFE806` | Ring table | {status, x, y} per ring, `$FFFF` terminated |
| `$FFFFF680` | PLC queue | |
| `$FFFFF770`-`$F77C` | Object manager list pointers | Point into the ROM placement list |
| `$FFFFF796` | `Collision_addr` | `$FFFFD600` or `$FFFFD900` |
| `$FFFFF7F0` | Animated-tile counters | 2 bytes per script {timer, next frame} |
| `$FFFFFFE6` | Collision set select | Hack; 1 for zones >= `$11` (`0x4C56`) |
| `$FFFFB000` | object RAM (player 1 at `$B000`, player 2 at `$B040`) | P1 x `$B008`, y `$B00C`, xvel `$B010`, yvel `$B012`, inertia `$B014`, angle `$B026` (confirmed in trace) |
| `$FFFFEE00` / `$EE04` | Camera X / Y | Confirmed in trace |
| `$FFFFF000` | 68k sound driver RAM | Hack path only |
| `$FFFFF600` | `Game_Mode` | |
| `$FFFFF62A` | `Vint_routine` | |
| `$FFFFFB00` | `Normal_palette` (64 words) | `$FB80` = fade target |
| `$FFFFFE10` | `Current_Zone` (byte) / `Current_ZoneAndAct` (word) | |
| `$FFFFFF70`/`72` | player mode / saved option | Written from SRAM |
| `$FFFFFF76` | SRAM present flag | Hack |
| `$FFFFFFD8` | `Two_player_mode` | |
| `$FFFFFFE5` | Sound driver select | Hack. 0 at boot, set to 1 when the first demo starts (trace). What sets it is still unknown; needs a write watchpoint |
