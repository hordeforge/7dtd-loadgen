#!/usr/bin/env bash
# Reset the dedicated server to a pristine, reproducible state: stop it, delete
# the playthrough save (block changes, entities, loot, player data), and keep the
# deterministic generated world (terrain regenerates identically from the fixed
# RWG seed). Use before a canonical benchmark run so results are comparable.
#
#   ./scripts/reset_world.sh            # stop + wipe save (server left stopped)
#   ./scripts/reset_world.sh --start    # stop + wipe + relaunch dedicated
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
USERDATA="${RE_DEDICATED_USERDATA:-$HOME/.cache/7dtd-loadgen}"
WORLD_GEN_SIZE="${RE_WORLD_GEN_SIZE:-4096}"
WORLD_NAME="${RE_WORLD_NAME:-RWG}"
GAME_NAME="${RE_GAME_NAME:-BotPoi_${WORLD_NAME}_${WORLD_GEN_SIZE}}"

start=0
[[ "${1:-}" == "--start" ]] && start=1

# Guard: an empty GAME_NAME would make the wipe glob "Saves/*/" and rm -rf every
# world. Never allow that.
if [[ -z "${GAME_NAME// }" ]]; then
  echo "reset_world: refusing to run with empty GAME_NAME (would wipe all saves)" >&2
  exit 1
fi

# Stop any running dedicated server (comm is truncated to 15 chars).
if pids=$(pgrep -x 7DaysToDieServe); then
  echo "reset_world: stopping server pids: $pids"
  # shellcheck disable=SC2086
  kill $pids 2>/dev/null || true
  for _ in $(seq 1 20); do
    pgrep -x 7DaysToDieServe >/dev/null || break
    sleep 0.5
  done
  pgrep -x 7DaysToDieServe >/dev/null && { echo "reset_world: force kill"; pkill -9 -x 7DaysToDieServe || true; sleep 1; }
fi

# Wipe the playthrough save(s) for this GAME_NAME under any generated world.
shopt -s nullglob
wiped=0
for save in "$USERDATA/Saves/"*"/$GAME_NAME"; do
  echo "reset_world: removing save $save"
  rm -rf "$save"
  wiped=$((wiped + 1))
done
shopt -u nullglob
echo "reset_world: wiped $wiped save(s); generated world kept for deterministic terrain"

if [[ "$start" == "1" ]]; then
  echo "reset_world: relaunching dedicated"
  exec "$ROOT/scripts/start_dedicated_prefab.sh"
fi
