# Audio

The ROM has **two complete sound drivers**. `SoundDriverLoad` (0x142A) chooses one at every level load:

| `$FFFFE6` (hack zone) | `$FFFFE5` | Driver | Used for |
|---|---|---|---|
| 0 | 0 | Sonic 2 Z80 SMPS | Sega screen, title, zones `$00-$10` |
| 1 | 1 | Sonic 1-style 68k SMPS + small Z80 DAC driver | Zones `$11-$1C` |

Confirmed in the emulator trace: `$FFFFE5` is 1 during the zone `$11/$13/$15` demos and 0 in zone `$00` and on the title.
Requests go through `PlayMusic` / `PlaySound` (0x14DC / 0x14FA), which write to `$FFFFE0/E1` (Z80 driver)
or `$FFFFF00A/B` (68k driver).

## 68k driver (`extracted/audio/smps68k/`)

* **Code:** update routine 0x1301DC (called from `V_Int`); pointer block 0x12FFEC-0x130007.
* **Music index:** pointer table at 0x1300FC, IDs `$81-$9F` (19 unique songs). **SFX:** 0x13727E, `$A0-$CF`.
  **Special SFX:** 0x13733E, `$D0`.
* **Format:** Sonic 1 SMPS. Big-endian, and pointers are **relative** (header offsets from song start; branch
  target = operand address + 1 + offset), so each sound is exported as a self-contained `.smps`.
* **Coordination flags:** `$E0-$F9` only, with parameter counts taken from the handler table at 0x131160.
  `$E3`, `$E4`, `$EE` and `$F2` end a track.
* **Commands:** `$F9` fade out, `$FA` SEGA PCM, `$FB` speed-up tempo, `$FC` normal tempo, `$FD` stop all.
* **DAC:** Z80 driver (Kosinski at 0x131576) holds 3 DPCM samples, table at Z80 `0x00D6`. Notes `$88-$8B`
  write a pitch from 0x13035E to Z80 `$00EA` and play `$83`. The pitch persists for later `$83` notes.
* **Validation:** every track of every song and SFX walks cleanly (no unknown flags, all branches inside the sound).

## Z80 driver (`extracted/audio/smpsz80/`)

* **Code:** Saxman-compressed (headerless, `$F66` bytes) at 0x101E0E. It decompresses to 4872 bytes of Z80
  code starting `DI; LD SP,1B80h; JP 0167h`.
* **Banks** (9-bit writes to `$6000`): music 0x108000 / 0x128000, SFX 0x128000, DAC 0x100000, SEGA PCM 0x120000.
* **Music:** playlist at Z80 0x11F5, one byte per song:

  | Bit(s) | Meaning |
  |---|---|
  | 7 | Bank |
  | 6 | PAL speed-up off |
  | 5 | Uncompressed |
  | 0-4 | Index into the bank's LE pointer table |

  Compressed songs are Saxman with a LE size header, unpacked to Z80 `0x1380`. That gives 31 songs `$81-$9F`.
* **SFX:** table at bank window `0xEE91` (ROM 0x12EE91), IDs `$A0-$F0` (81). SFX share voice tables and call
  into each other, so they are exported as one bank image (`sfx/sfx_bank.smps`, base `0xEF33`) plus `sfx.json`.
* **Format:** Sonic 2 SMPS. Little-endian **absolute** Z80 pointers, so each file records its `baseAddress`.
  Flag parameter counts are from the handler table at Z80 0x0C94. They differ from the 68k driver in two
  places: `$ED` takes 1 parameter, and `$EE` is a no-op.
* **Commands:** `$F8` stop SFX, `$F9` fade out, `$FA` SEGA PCM, `$FB/$FC` tempo, `$FD` stop all.
* **Special SFX:** `$B5`/`$CE` alternate, `$DA` plays every other request, `$E0` rises in pitch on repeats.
* **DAC:** 7 DPCM samples (table at Z80 0x1233), plus a 17-entry note playlist at 0x124F mapping DAC notes `$81-$91` to `{sample, pitch}`.
  The driver uses **self-modifying code** here: it patches the low bytes of the table loads (0x0105/0x0109), the
  delta-table displacement (0x018D/0x01AA) and the pitch (0x010C). A port reads the tables directly instead.
* **Validation:** all 31 songs and 81 SFX walk cleanly.

## DPCM and rates

Both drivers use the same DPCM: the accumulator starts at `$80`, the high nibble is decoded first, and the deltas are
`0,1,2,4,8,16,32,64,128,-1,-2,-4,-8,-16,-32,-64`. Rates come from counting Z80 cycles in each playback loop:

| Driver | Rate (Hz) | Kick (pitch `$17`) | Snare (pitch `$01`) |
|---|---|---|---|
| 68k-driver DAC | `3579545 / ((275 + 26*pitch) / 2)` | ~8200 | ~23800 |
| Z80 driver | `3579545 / ((262 + 26*pitch) / 2)` | ~8320 | ~24860 |

**Unverified:** these ignore 68k bus contention and interrupts. They can be checked against a MAME `-wavwrite` capture.

## Playback (68k driver, hack zones)

* **Driver:** `src/Game/Audio/Smps68kDriver.cs` ports the update routine (0x1301DC) and everything it calls,
  routine by routine: queue/priority (0x13059C), music/SFX/1-up start, the DAC/FM/PSG track updates, all 26
  coordination flags (including their `addq #4/#8,sp` early exits), fades, tempo, pause and silencing.
  Its state lives in RAM at `$F000-$F5BF` exactly like the original's.
* **Data it reads:** `audio/smps68k/player.json` plus `bank_tables.bin` / `bank_sounds.bin`, the driver's data block and
  all sound data at their ROM addresses (the driver keeps absolute pointers in RAM, so they match the emulator's),
  and the constant tables inside the driver code. `levelMusic` is the zone music list (0x4BEE).
* **Hack additions:** SFX `$E0-$F7` index a second table at 0x137342, which is the main SFX table from entry `$D1`
  on (only `$D1`/`$E0` is valid). `$FE` also plays the SEGA voice.
* **Validation:** driver RAM is compared each iteration in the object co-simulation. Every recording matches;
  the few differing iterations are lag frames (the driver runs once per VBlank, so a lag frame runs it twice
  between two logic iterations).
* **Chips:** `Ym2612.cs` (hardware phase/envelope generators, MAME-style log-sin/exp tables; no LFO, SSG-EG or
  channel-3 mode, none of which the driver uses), `Sn76489.cs`, and `SoundMixer.cs` (DAC samples at their
  pitch-derived rates into register `$2A`, stereo resampling to 48 kHz).
* **Check against MAME's audio** (`WAV=out.wav tools/RefEmu/run.sh ...` records it): for the Green Hill music,
  the per-band spectrum (30 Hz-16 kHz) is within about 2 dB of MAME's in every band, and the overall level is
  matched. `S2Port.Game --screenshot x.png --frames N --wav out.wav [--solo ym|psg]` renders the port's.

## Playback (Z80 driver: title, level select, Sonic 2's zones)

* **Driver:** `src/Game/Audio/SmpsZ80Driver.cs` ports Sonic 2's Z80 driver routine by routine from the Sonic 2
  disassembly's `s2.sounddriver.asm`.
  * The hack's copy is the stock driver. Assembled from the disassembly, it differs only in the ROM bank numbers,
    the SEGA voice's length and the DAC pitch table.
  * Its state is an 8 KiB array laid out exactly as the Z80's RAM, and it starts from the decompressed driver
    (`audio/smpsz80/driver.bin`, extracted).
  * The operands the code rewrites (self-modifying code) are written there too, and read back where the original
    reads them. That keeps quirks such as 256-byte page wraps in table lookups.
  * The ROM banks it maps at `$8000` are extracted as `bank_XXXXXX.bin`. Bank numbers are decoded from the driver's
    own bankswitch instructions.
* **Timing:** each frame the 68k hands its sound queue (`$FFE0-$FFE4`) to the driver's mailbox (sndDriverInput,
  0x11A8), then the Z80's VBlank interrupt (`VInt`) runs.
  * **The DAC:** the original streams drums from a busy loop between interrupts. The port decodes a sample when it
    starts and hands it to the mixer (`ISoundChips.PlayDacPcm`).
  * **The SEGA voice:** the original plays it inside an interrupt. The port finishes that interrupt at once, then
    skips the interrupts the Z80 would miss.
* **Where it runs:** `SmpsZ80Driver.For(chips)` keeps one driver per set of sound chips. It lives across levels, as
  the real Z80 does; the 68k reloads it only when switching over from the hack's 68k driver (0x142A).
  * The title screen plays `$FD`, the sparkle `$A7` and the title music `$99`.
  * The level select plays `$91`. Its sound test plays Z80 sounds for `$00-$7F` and 68k sounds for `$80-$FF`
    (0xABA6), switching drivers as the original does.
* **Validation** (`tools/Validate z80 data.bin <dir>`, from a MAME capture with `TAP_Z80=1`):
  * For every Z80 interrupt, it loads MAME's Z80 RAM, runs the port's interrupt and replays the 68k's mailbox
    writes. A write goes in before or after the interrupt, according to whether the Z80 had already read that byte.
  * It then compares the whole RAM and every YM2612/PSG write with MAME's next snapshot. Excluded: the stack, and
    the two DAC-loop operands rewritten for every nibble.
  * **Results:** all match on the boot, SEGA screen, title and level select (1136 interrupts); the EHZ 2 boss fight
    (720); and a run that pokes the rarer requests (2399). The rarer requests: 1-up and its fade back,
    speed-up/slow-down, pause/resume, invincibility, fade-out, stop, ring/gloop/spindash variants, and SFX stop.
* **Mixer:** the PSG now averages each channel over the PSG ticks in an output sample (square waves and noise no
  longer alias). Against MAME, EHZ's music is within 1.6 dB in every band from 30 Hz to 16 kHz, and Green Hill's
  8-16 kHz band went from +4.2 dB to +0.8 dB.

## Not done yet

* SEGA PCM sample rates: the 68k driver plays it in a busy loop that polls the joypad, so the rate has to be measured.
