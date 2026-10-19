#!/usr/bin/env bash
# Runs smb3.nes (or $ROM) headless in MAME's NES driver with an input script; see nes.lua.
#   tools/RefEmu/nes.sh <inputs.txt> <frames> <out-dir>     (DUMPS=f1,f2  SNAPS=f1,f2)
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
inputs="$(realpath "$1")"; frames="$2"; out="$(realpath -m "$3")"
rom="${ROM:-$repo/smb3.nes}"
MAME="${MAME:-$HOME/.local/opt/mame-deb/root/usr/games/mame}"
MAME_LIBS="${MAME_LIBS:-$HOME/.local/opt/mame-deb/root/usr/lib/x86_64-linux-gnu}"
mkdir -p "$out"
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
cd "$work"
set +e
# Screenshots work with -video none (the xvfb/x11 route used for the Genesis hangs before the script starts here).
# -seconds_to_run is a backstop: exit() from the frame callback is not always honoured promptly.
video=(-video none -seconds_to_run $((frames / 60 + 3)) -snapname "%i" -snapview native -snapshot_directory "$out/snap")
env LD_LIBRARY_PATH="$MAME_LIBS${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" \
  SDL_AUDIODRIVER=dummy SDL_VIDEODRIVER=dummy \
  NES_INPUTS="$inputs" NES_FRAMES="$frames" NES_OUT="$out" NES_DUMPS="${DUMPS:-}" NES_SNAPS="${SNAPS:-}" NES_HOLD="${HOLD:-}" NES_APULOG="${APULOG:-}" \
  "$MAME" nes -cart "$rom" "${video[@]}" -sound none -nothrottle -skip_gameinfo \
    -nvram_directory "$work/nvram" -cfg_directory "$work/cfg" -autoboot_script "$here/nes.lua"
set -e
echo "Trace: $out/trace.csv"
