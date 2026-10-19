# Reference emulator

MAME 0.242 (Ubuntu 22.04 package), unpacked into `~/.local/opt/mame-deb` without a system install:

```sh
cd ~/.local/opt/mame-deb
apt-get download mame libpugixml1v5 liblua5.3-0 libutf8proc2 libportaudio2 libportmidi0 \
  libqt5core5a libqt5gui5 libqt5widgets5 libsdl2-ttf-2.0-0 libdouble-conversion3 libpcre2-16-0 libmd4c0
for d in *.deb; do dpkg-deb -x "$d" root; done
```

`tools/RefEmu/run.sh` sets `LD_LIBRARY_PATH` and SDL dummy drivers, so it runs headless. Override the paths
with `$MAME` / `$MAME_LIBS` if you install MAME some other way.

## Harness

```sh
tools/RefEmu/run.sh <inputs.txt> <frames> <out-dir> [dump,frames] [watch.txt]
```

* **Inputs** (`tools/RefEmu/inputs/*.txt`): `<frame> <buttons>`, buttons from `U D L R A B C S` or `-`.
  The state holds until the next line.
* **Watch list** (`watch-default.txt`): `<name> <hex addr> <b|w|l|sb|sw|sl>`. Each listed address is written
  every frame to `trace.csv`.
* **Dumps:** `vram_<frame>.bin` (64 KiB VDP VRAM, read through the `0/m_vram` save-state item; MAME's `videoram` space is the SMS-mode mapping and reads as zero) and `ram_<frame>.bin`, the 64 KiB of 68k RAM (`$FF0000-$FFFFFF`), so a file offset equals the RAM address minus `$FF0000`.
* MAME 0.242 segfaults on shutdown after the script exits. `run.sh` ignores the crash and checks
  instead that the trace has one row per frame.

Runs at roughly 13x real time (`-nothrottle`).

**Screenshots:** `SNAPS=10500,10600 tools/RefEmu/run.sh ...` runs MAME under `xvfb-run` with `-video soft` and saves
`snap/0000.png`, ... (320x224 native) at those frames, at about 8x real time.

## Observed boot timeline (no input)

| Frame | Game mode | Zone/act |
|---|---|---|
| 1 | `$00` Sega screen | |
| 324 | `$04` title | |
| 1128 / 1328 | `$88` -> `$08` demo | `$1100` |
| 4151 / 4357 | demo | `$1300` |
| 7184 / 7370 | demo | `$1500` |
| 10193 / 10362 | demo | `$0000` |

Pressing Start at frame 600 starts a new game: mode `$8C` (level load), then `$0C` at frame 802, in **zone `$11` act 1**.

`$FFFFE5` (sound driver select) goes 0 -> 1 when the first demo starts (frame 1328).

## Level data validation

```sh
dotnet run --project tools/Validate -- level-ram extracted <ram.bin> <zoneHex> <act> [vram.bin]
```

Loads the act through `LevelLoader` (the same path the game will use) and compares it with the RAM dump:

| Zone/act | Layout | Blocks | Chunks | Palette |
|---|---|---|---|---|
| `$11` act 1 (new game, frame 1499) | 4096/4096 | 1756/1756 | 16384/16384 | 64/64 |
| `$13` act 1 (demo) | 4096/4096 | 1488/1488 | 16384/16384 | 64/64 |
| `$15` act 1 (demo) | 4096/4096 | 1724/1724 | 16384/16384 | 58/64, cycling |
| `$00` act 1 (demo) | 4096/4096 | 3072/3072 | 16384/16384 | 60/64, cycling |

Palette differences are rotations of the extracted values (EHZ colours 19/20/30/31 rotate). They come from
runtime palette cycling, not extraction errors. The cycling scripts belong to the game logic and are not extracted yet.

### Report 3 additions (collision, objects, rings, VRAM)

| Zone/act | Collision A/B (used blocks) | Set | Rings | Object list ptrs | Used tiles vs VRAM |
|---|---|---|---|---|---|
| `$11` act 1 | match | alternate | 156/156 | 4/4 | 671/671 |
| `$13` act 1 | 297/297 (7 stale) | alternate | 63/63 | 4/4 | 490/506, custom-code tiles |
| `$15` act 1 | match | alternate | 158/158 | 4/4 | 711/711 |
| `$00` act 1 | match | normal | 227/227 | 4/4 | 820/820 |

"Stale" means collision entries for blocks no chunk uses, left over from the previous demo zone. The demo
path doesn't clear collision RAM.
