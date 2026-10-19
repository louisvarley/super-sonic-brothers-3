# Report 12: title screen and level select (2026-10-17)

The game now starts like the original: the "Sonic and Miles 'Tails' Prower in" text, then the "Sonic the Hedgehog 1 and
2" title with Sonic, Tails, the emblem and the island. The menu offers **START GAME** (Green Hill act 1) and **LEVEL
SELECT**; the level select is the hack's own screen with every zone, the ported ones selectable, and the SMB3 Easter egg
at the end. When a game ends (game over, or no ported zone left) the title comes back after three seconds.

```
dotnet run --project src/Game                       # title screen and menus
dotnet run --project src/Game -- --zone 11 --act 1  # straight into Green Hill act 1 (as before; the trainer builds sessions itself)
```

## Title screen (game mode $04, 0x46B8; `src/Game/Frontend/TitleScreen.cs`)

* The mode routine is ported as a frame-by-frame iterator (each `yield` is a WaitForVint): fade out, the intro text
  (0xC9DC: a high byte per text, then one byte per cell) on the credit font, fade in, the title art, fade out, the plane
  maps (Enigma: background in two parts on plane B, the emblem plus "(c) 1992 SEGA" on plane A), the palette, object $0E,
  fade in, then the loop: objects, SwScrl_Title (0xDE3C: lines 160-191 at a quarter of the camera, 192-207 rippling
  from a wave table), BuildSprites and the masking-sprite fix (0x4954).
* **Object $0E** (0x15D5C) is the hack's version of Sonic 2's title object: Sonic flying in (position table), Tails,
  their hands, the emblem top, the flashing star (9 places), the falling star, the masking sprite (hides Sonic and Tails
  behind the emblem), the star colour cycle, and Start-to-skip (0x16400). **Object $C9** (0x16234) fades palette lines
  in (the emblem) and down from white (the background, after the flash).
* **The menu** replaces the hack's $0F (NEW GAME / ARENA MODE / OPTIONS) with START GAME / LEVEL SELECT, built from the
  hack's menu font in the same layout (chosen item on the top line in palette line 3 with the cursor, the next one
  below; the last one alone).
* The intro text stays up for 71 frames while the original decompresses the title art with interrupts off (measured in
  MAME). Graphics_Flags ($FFF8) is set as the boot code does for an overseas console, which shows the TM on the emblem.
* After its time runs out the original plays a demo; here the title starts again.
* Silent: the title's music and sounds are the Z80 driver's.

## Level select (game mode $28, 0xA914; `src/Game/Frontend/LevelSelectScreen.cs`)

The hack's 42 entries (Sonic 1's zones, Sonic 2's, the two special stages, the sound test) from its tables: the level
order (0xAA6A), the left/right switch table (0xABF6) and the highlight table (0xAD56); the text map and the animated
"SONIC" background (Anim_SonicMilesBG, 0x9D60). Additions:

* Entries for zones that are not ported are grey (palette line 1, a dimmed copy of line 0); Start ignores them.
* **SUPER MARIO 3** is entry $2A, below the sound test (available when the Easter egg was built from smb3.nes).
* The sound test plays sounds $80 and up through the ported 68k driver (lower ones are the Z80 driver's).
* Start on the sound test returns to the title (the SEGA screen in the original).

## Engine pieces

* **Enigma decompressor** (`Compression/Enigma.cs`, EniDec 0x1990): the three title maps match MAME's VRAM cell for cell.
* **`Vdp.RenderScreen`**: planes A/B from VRAM nametables, per-line scroll, sprites with sprite masking.
* **`PaletteFade`**: Pal_FadeFromBlack / Pal_FadeToBlack / PalLoad_ForFade.
* **`GameFlow`**: title -> game or level select -> game; a stopped game returns to the title. `S2Game` hosts it.
* Extraction: `objects/frontend/*.json` (the ROM regions the ported code reads by address) and `frontend/art.json`.

## Validation

| Check | Result |
|---|---|
| Enigma maps vs MAME VRAM (title, frame 950) | 1120 + 672 + 1000 cells match |
| Title at the same point of object $0E's clock as MAME frame 950 | identical outside the menu rows except 3 pixels |
| Intro text | same text and placement |
| Tests (65) | the title skipping its intro and starting Green Hill; the menu opening the level select; the level select ignoring unported entries, starting Marble Zone act 3, the Mario level, and returning from the sound test; game over returning to the title |

## Not done

The SEGA screen (its chant needs the Z80 driver), the demos, the Z80 driver itself (title, menu and Sonic 2 zone music),
the options and arena screens.
