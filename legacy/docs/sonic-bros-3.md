# Sonic Bros. 3 (`src/SonicBros3`)

> The C# engine described here now also runs in the browser through WebAssembly,
> served by Node.js. See [browser hosting](web-port.md) and the
> [README](../README.md) for startup, temporary asset requirements and campaign scope.

The project's only game, built on the native Sonic engine: Super Mario Bros. 3's opening, the World 1 map, Worlds 1-1/1-2 and 1-3's main route
and the N-Spade card game, played as Sonic (with Tails). Like S2Port it contains no original data; it needs both of the player's own images beside the
executable (or in a folder above it):

* `rom.bin`: the Sonic 2 hack S2Port runs (the engine's data: Sonic, Tails, objects, sound).
* `smb3.nes`: Super Mario Bros. 3 (USA), SHA-1 `a03e7e52…c449` (title, map, 1-1).

```sh
dotnet run --project src/SonicBros3                       # title screen
dotnet run --project src/SonicBros3 -- --start map        # straight to the World 1 map
dotnet run --project src/SonicBros3 -- --start level      # straight into 1-1 (--level 1-2 for 1-2)
dotnet run --project src/SonicBros3 -- --start level --level 1-3
dotnet run --project src/SonicBros3 -- --start match      # straight into the card game
# headless: --screenshot out.png --frames N [--inputs script.txt] [--debug-start x,y] [--trace]
dotnet test tests/SonicBros3.Tests
```

Controls: arrows, Z/X/C = A/B/C, Enter = Start. On the title, Start skips the intro, then starts; up/down or A/B/C
moves the 1P/2P cursor (SMB3's SELECT). On the map, the arrows walk and any of A/B/C/Start enters a panel.

The game keeps its data file, `sonicbros3.bin` (engine extraction, native SMB3 zones, `sb3/` and the bulk `conversion/` import), built on the
first launch. It is gitignored with the other `*.bin` files.

See [conversion pipeline](conversion.md) for batch conversion, coverage reporting
and the explicit distinction between imported data and fully playable content.

## Engine origins

`src/SonicBros3` started as a copy of `src/Game` with the namespace `S2Port.Game` renamed to `SonicBros3`, so the
engine could originally evolve independently. The original game and trainer have
now been removed; `S2Port.Extraction` remains the engine asset library. Changes made when the engine was copied:

| Where | Change |
|---|---|
| `Runtime/Vdp.cs`, `Frontend/Screen.cs` | `LineScrollA/B`: per-line vertical scroll for screens (the NES's mid-frame scroll split). Not a Mega Drive VDP feature; off unless a screen sets it. |
| `Runtime/LevelSession.cs` | `ExitOnRestart`: a death or the end of the goal fades out and ends the session (`Exited`), back to the map. `newGame` callback for the lives and score carried in. |
| `Logic/Core/ObjectContext.cs`, `Logic/Level/World.cs` | `GoalReached` / `World.GoalCard`. |
| `Logic/Core/Objects.cs` | In the SMB3 zones, object `$0D` (the signpost's slot) runs `RouletteGoal`; `$E8` Goomba pipes and `$E9` micro goombas. |
| `Logic/Level/Camera.cs` and friends | Zone `$41` (1-2) is an SMB3 zone like `$40` (`Camera.IsEasterEgg`); its title card reads GRASS LAND act 2. |
| `Logic/Level/World.cs` | An SMB3 zone's music id comes from its `music.json`. |
| `Frontend/GameFlow.cs` | Title → map → level → map, as SMB3. |

## SMB3 data (`Smb3/`)

`Smb3Assets` copies whole PRG banks (10, 12, 22, 24, 25, 27, 30) and the CHR banks the screens select into `sb3/`;
nothing is converted at build time, except the bonus room (below). The screens run SMB3's own data through `NesPpu` (nametables, mirroring, palette RAM, the
`Video_Upd_Table` stream format) and `NesPresenter`, which turns each (CHR tile, NES palette) pair into one Mega Drive
tile on first use (as `Smb3ZoneBuilder` does for 1-1): background palettes on line 2, sprite palettes on line 3. The
NES picture is 256 wide; NES column c is plane column c + 4, and the screens fill the 32-pixel margins.

Addresses (this image; checked against captainsouthbird's disassembly): `Video_Upd_Table2` bank 25 `$C5DA`,
`Title_3GlowColors` bank 24 `$AAAA`, `Tile_Layout_TS0` bank 12 `$A000`, `Tile_Attributes_TS0` `$A400`,
`W1_Map_Layout` `$A5AA`, `PalSet_Maps` bank 27 `$ABD2`, `StatusBar` (`$2B00` version) bank 30 `$811B`,
`Video_DoWXMario00` bank 10 `$C000`.

## Title (`Frontend/Sb3TitleScreen.cs`)

SMB3's `Do_Title_Screen` sequence: the curtain (`Title_Display_Curtain`), palette and checkerboard floor, the curtain
rising 2 lines a frame to 208, the logo streamed in one item a frame (items `$08`-`$22`), dropped 5 lines a frame and
shaken `$10` times, the four fade-in steps every 5 frames, the glowing 3, then the 1 PLAYER / 2 PLAYER menu (the
cursor at `Title_Menu_1P2PCursorY`, the coin sound on Start, the 20 × 96-frame timeout back to the start). Below NES
line 193 the floor stays put (`IntIRQ_TitleEnding`): plane B holds it, plane A the scrolled part.

Mario's and Luigi's action scripts are replaced by this game's routine, using the Sonic 2 sprites: they run in from
either side (held at the sides for `$40` frames from scroll 78, as Mario and Luigi are), Tails spin-jumps over a ducking
Sonic, the logo drops when Mario's script would drop it, a Goomba falls in and Sonic spindashes through it, Tails flies
up past the logo, a Koopa shell drops and Sonic rolls into it, and they end either side of the menu. The Goomba and
shell are the 1-1 zone's SMB3 sprites (`objects/smb3-*`) on 1-1's sprite palette. The logo is SMB3's, unchanged.

## World 1 map (`Frontend/WorldMapScreen.cs`)

* The layout and tileset are SMB3's: tile memory rows 15-26 as `Map_Reload_with_Completions` builds them (black row,
  top border, the 16 × 9 layout, `Map_Bottom_Tiles`), through `Tile_Layout_TS0`, palette `PalSet_Maps` (World 1).
* `Map_DoAnimations`: the first background bank cycles `$14/$70/$72/$74`, `$11 + 1` frames each.
* The status bar is the `StatusBar` stream on banks `$5C`-`$5F` from line 193, with the values filled in as
  `StatusBar_Fill_*` do: WORLD 1, lives, score, rings in the coin counter, an `[S]` badge redrawn over `[M]`, and the
  goal cards (Sonic monitor icons) in the three card slots.
* The WORLD 1 box (`Video_DoWXMario00`) says SONIC; its S is hand-drawn over the unused Z glyph.
* Movement follows `Map_CheckDoMove`: a direction is taken when the adjacent tile is a path tile for it
  (`Map_Object_Valid_*`), and moves 32 pixels at 2 a frame. On a level panel not cleared yet, only the way back is
  open. Enterable tiles follow `Tile_Attributes_TS0` plus `Map_Completable_Tiles`.
* Cleared panels show an "S" panel: SMB3's M-panel CHR tiles (`$88`-`$8B`), redrawn.
* Mini Sonic is drawn for this game (`MiniSonicArt`): two 16 × 16 frames in SMB3's map-sprite style and Sonic's palette.

Panels 1, 2 and 3 (1-1, 1-2, 1-3) and the spade panel (the card game) are built; the others bump. Cleared levels can be
replayed (SMB3 shuts cleared panels), so more cards can be collected.

Addresses come from captainsouthbird's disassembly assembled with nesasm (a symbol listing, `smb3.fns`); the
assembled image matches this one byte for byte in the banks used here apart from a few operands (and, in bank 22, the
US text "Miss twice and your out!" where the disassembly has "You can only miss twice!").

## World 1-2 (`S2Port.Extraction/Smb3`)

`Smb3ZoneBuilder` builds registered horizontal Plains/Hills levels (`Smb3Levels`: 1-1 as zone `$40`, 1-2 as zone `$41`, 1-3 as zone `$42`) using `Smb3DecodedArea`, which executes the original ROM loader offline. 1-2's header says
tileset 1, but that field is the alternate area's tileset (Level_AltTileset); the map loads 1-2 as tileset 3, Hills
(W1_ByRowType's `$23`), from the Hills bank (16, `$AF2A`). `Smb3LevelDecoder` takes the map's tileset and ports the
Hills dispatch (LoadLevel_Generator_TS3 / LeveLoad_FixedSizeGen_TS3) and the bank 14 generators 1-2 uses: the 45 and
22.5 degree slopes both ways, vertical ground edges, corners, ground rectangles (DecoGround, a 4-byte command),
background fill, background bushes, tunnels, and the horizontal wall pipes, keeping the 6502's quirks (a zero-length
background bush or wall pipe runs 255; slopes that cross a screen's left edge step back $1B0).

The decoder is checked against SMB3's own loader run on a 6502 core (`S2Port.Extraction/Smb3/Cpu6502.cs`, official
opcodes over an MMC3): `tools/Validate smb3-level-6502 smb3.nes` and the `LevelLoaderTests` compare every cell of tile
memory, 4752 of 4752 in 1-1 (which also checks the core, since that decoder matches MAME) and in 1-2.

The zone: BG CHR bank `$6C` (Level_BG_Pages1 index 19), PalSet_HillsUnder, the athletic theme (Set 2B `$90`, music id
`$95`). The game uses the hack's collision set for zones `$11` and up (`collision/alternate.json`); the slopes use its
ramps: `$99` 45 degrees up is shape 80, `$9C`/`$9D` 22.5 degrees up are 28/29. The set has no falling ramps of these,
so `$9B`, `$9E` and `$9F` are those shapes placed x-flipped in their chunks (the floor code mirrors a flipped block's
heights and angle), their tiles pre-mirrored so they still look as SMB3 draws them. Sonic runs, rolls and spindashes
up and down 1-2's hills like any Sonic slope.
At direct 45-degree-to-flat joins, SMB3 surface-angle fallback uses the same floor/ceiling tie rounding as
the terrain probes. This prevents a downhill roll from turning into downward wall-running through the floor;
the original non-SMB3 angle fallback is unchanged.

New objects (`Logic/Objects/Smb3/Smb3Objects.cs`):

* **`$E8` Goomba pipe** (OBJ_CFIRE_GOOMBAPIPE_R, CFire_GoombaPipe): every `$70` frames, three times in four, a Goomba
  walks out of the pipe to the right for `$28` frames (behind the pipe, no gravity) if Sonic is on that side.
* **`$E2` subtype 1, the micro goomba dropper** (OBJ_PARAGOOMBAWITHMICROS): flies up to `$38` above Sonic, then drops a
  micro goomba every `$20` frames for `$80` frames, bobbing, its x speed swinging towards him.
* **`$E9` micro goomba** (SObj_Microgoomba): sways down. Rolling or invincible Sonic knocks it out; otherwise it clings
  on and slows him (ground speed held to 3 px a frame, SMB3's Player_UphillSpeedIdx); button presses loosen it
  (grip up to 5, growing every 16 frames), and it flops off when he is hurt or invincible. Art: CHR `$FF` of bank
  `$4F` (Smb3ObjectArt's new `smb3-microgoomba`).

## The card game (`Frontend/MatchGameScreen.cs`)

The spade panel on the World 1 map opens SMB3's N-Spade, the memory card game Toad hosts, with Tails as the host:

* **The bonus room**: built by SMB3's own loader (LevelLoad_TS15_TS16_TS17 on the 6502 core at build time,
  `sb3/bonus_room.bin`: one static screen, so running the original is exact and simpler than porting its five
  generators), drawn with Tile_Layout_TS15_TS16_TS17 and PalSet_Bonus. The instruction box builds over five frames,
  then the text types a character every 4 frames with a blip every other one, then `$A0` frames (A/B/C/Start skip it).
  Tails stands behind the table (its tiles drawn in front of him) where Toad does; Sonic stands where the room puts
  Mario's tiles (removed).
* **The cards**: NSpade_DoGame's states ported: Video_NSpadeBG, Card_InitialSet shuffled by Card_Shuffle (Randomize,
  seeded from the frames the screen has run, the score and the lives), cards drawn with SMB3's graphics buffer
  templates patched as Card_RenderBack / Card_RenderFace / Card_Blackout do, the 10-sprite cursor, the 5-frame flip,
  two misses allowed, and the deck kept between visits until all nine pairs are found.
* **Sonic theme**: the backs show a gold ring in place of the N-Spade (CHR `$2E/$2F/$3E/$3F` of bank `$5C`, redrawn);
  the faces are Sonic 2 monitor icons over the blank picture: mushroom = speed shoes, flower = shield, star =
  invincibility, 1-up = Sonic, 10/20 coins = rings (SMB3's "1UP"/"10"/"20" lines kept).
* **Prizes** (Card_MatchPairReward): an extra life; 10 or 20 rings, one a frame, 100 rings a life; the power-ups come
  out of a monitor at Sonic as the next level starts (SMB3 puts them in the inventory).
* The panel shuts after a game (it shows the S panel) and opens again whenever a level is cleared.

## 1-1 and the goal (`Logic/Objects/Smb3/RouletteGoal.cs`)

1-1 is the Easter egg zone unchanged, except that the signpost becomes SMB3's goal roulette (`ObjNorm_EndLevelCard`);
1-2 and 1-3 use the same goal.
The card in the goal box changes every 8 frames through SMB3's three cards, here Sonic 2 monitor icons: rings
(mushroom), shield (flower), invincibility (star). Touching it keeps the card shown. The timer stops and the act-clear
jingle plays. Sonic runs off to the right with controls locked (the signpost's way), and the card spins up and off the
screen. Then "COURSE CLEAR!" and "YOU GOT A CARD", in SMB3's own goal font and card frame (`Video_CourseClear` /
`Video_YouGotCard` tiles), appear on the `EndLevelCard_MsgAndCard` timer, and the level fades out to the map with the
card. Sonic's time and ring bonuses go into the score (SMB3 counts its timer into the score at this point).

Three cards give lives as `EndLevelCard_xUpByMatch` does: three alike give 2 (rings), 3 (shields) or 5
(invincibility); a mixed set gives 1. The slots then empty. A death goes back to the map with a life less; game over
goes back to the title.

## World 1-3 and enemy corrections

1-3's main route, native Boomerang Brother/returning boomerangs, title card and
map panel are wired into the game. The white-block/warp-whistle secret is not
implemented; conversion reports it as `native-partial`, not fully converted.
Headless regressions cover entering panel 3 and jumping into the goal near the
end, receiving a card and clearing the correct panel. They do not prove an
uninterrupted start-to-finish traversal of every route.

Plants retain the source ROM's ordinary/fire, red/green and ceiling variants.
Ordinary plants never shoot; the three plants in 1-2 are all source fire types.
Koopa walking/winged/shell artwork is aligned by its actual opaque foot pixels
to the floor probe, shell transitions preserve foot contact, and walking enemies
can stand on converted bricks/question blocks.

Combat deliberately mixes both games: landing on an enemy from above keeps
Mario-style stages (wings off, then a turtle becomes a shell; stomping a sliding
shell stops it). Rolling or spindashing into an enemy defeats it in one hit,
including winged enemies and shells. A descending jump from above counts as a
stomp even though Sonic's jump sprite is spinning; an airborne side spin is a
one-hit attack. Invincibility also bypasses those stages. Stomps bounce upward
without reversing horizontal speed and briefly protect the transformed enemy
from repeat contact while Sonic separates. Walking into enemies and touching
projectiles still hurts. Contact bounds follow the taller turtle/brother bodies
and the exposed part of plants, not the pipe anchor or hidden stem.

## Not done

* Music on the title, the map and the card game. SMB3's title is silent, so that part is faithful. The World 1 map
  theme and the bonus game's Set 2A songs are imported by the bulk converter but are not wired into these screens.
* 1-3's white-block/warp-whistle secret and the rest of the campaign after 1-3.
* 1-2's bonus room under the pipe (the alternate layout), the P-meter, Mario's power-up forms. The micro goombas'
  effect on jumping (SMB3 also shortens Mario's jumps).
* The Toad houses and the spade panel's own game (the slot-machine Spade); the card game stands in for the latter.
* The map's Hammer Bro, the "HELP" bubble, the panel-clear poof, the W1 box's strip-by-strip erase.
* The title's Koopas walking under the menu (`Title_Menu_UpdateKoopas`). The 2 PLAYER option plays as 1 player.
* The logo still says SUPER MARIO BROS. 3.
