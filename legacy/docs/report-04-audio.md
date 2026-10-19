# Report 4: Audio (2026-10-16)

## Findings

* **Two sound drivers, switched per zone.** The original Sonic 2 Z80 driver runs on the title and in zones `$00-$10`.
  A Sonic 1-style 68k driver runs in the hack's zones `$11-$1C`. The switch is at 0x142A and is keyed on the hack-zone flag
  `$FFFFE6`. The emulator trace confirms it.
* Both drivers are mapped from their code (pointer tables, flag handler tables, bank switches). Details in [audio.md](audio.md).

## Extracted

| | 68k driver | Z80 driver |
|---|---|---|
| Songs | 19 unique (`$81-$9F`) | 31 (`$81-$9F`) |
| SFX | 48 + special `$D0` | 81 (`$A0-$F0`), one shared bank image |
| DAC samples | 3, + 4 timpani pitches | 7, + 17-note playlist |
| SEGA PCM | 0x137E80, 27000 bytes | 0x121E8C, 12474 bytes |
| Parse problems | 0 | 0 |

Each sound has its raw sequence data (`.smps`) plus a JSON summary (header, channels, decoded FM voices).

## Corrections made during this step

* My first track walker had branch targets off by one, and treated `$E4`/`$EE` as "continue". Reading the
  handlers fixed both. After that, every track in both drivers walks cleanly.
* Z80 SFX can't be cut apart, because they share voice tables and call each other. They are exported as one bank image.
* I had labelled command `$E0` as "fade out" from Sonic 1 memory. In this driver the commands are `$F9-$FD`, and
  each label now comes from its handler.

## Unverified

* DAC sample rates: computed from Z80 cycle counts.
* SEGA PCM rates: not measured.

## Next

The audio data is complete. Turning it into sound means writing an SMPS player for each format and a YM2612 + PSG
core in C#, which is the largest remaining piece outside game logic. I suggest doing it after the runtime shell exists,
so it can be heard in context, and validating it against MAME `-wavwrite` captures of the same song IDs.
