# tools/Smb3Effects

Renders Mario (SMB3) gameplay sound effects into 48 kHz mono PCM by running SMB3's own 6502 sound engine offline.
This is build tooling; it writes `web-data/effects/*.pcm`, which the Rust port mixes alongside the 68k Sonic SMPS
driver.

```powershell
dotnet run --project tools/Smb3Effects -- --rom smb3.nes --out web-data/effects
```

The generated files are derived from the user's ROM and are **gitignored** (under `web-data/`). The port loads them
from `web-data/effects` at runtime; `campaign.bin` also contains the `pipe` and `enter-level` effects.

| File | SMB3 source queue | Notes |
|---|---|---|
| `mario-bump` | QPlayer `$02` | block bump |
| `mario-kick` | QPlayer `$08` | stomp / shell kick |
| `mario-fire` | QPlayer `$20` | fireball |
| `mario-coin` | QLevel1 `$01` | coin / block coin |
| `mario-powerup` | QLevel1 `$20` | power-up |
| `mario-1up` | QLevel1 `$40` | 1-up |
| `pipe` | QPlayer `$10` | pipe (also in the archive) |
| `map-move` | QMap `$02` | map path move |
| `enter-level` | QMap `$04` | enter a level (also in the archive) |
| `map-world` | QMap `$01` | world intro |
| `course-clear` | QMusic1 `$20` | goal fanfare |

`mario-crumble` (QLevel2 `$01`, brick break) and `mario-boomerang` (QLevel2 `$04`) use SMB3's noise channel; the
current pulse-only renderer captures silence for them, so the Rust port falls back to `mario-bump` / `mario-fire`
until a noise-channel renderer is added. The PCM player itself supports overlapping effects and is mixed with the
68k driver's output.
