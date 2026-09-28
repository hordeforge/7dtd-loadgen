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

# Guard: GAME_NAME is a caller-supplied path component that reaches the rm -rf
# below, so "../../.." retargets the delete outside the saves tree, and an
# empty name makes the glob "Saves/*/" and wipes every world. Game names are
# letters, digits, dot, dash and underscore; anything else is a mistake, and
# the leading-alnum requirement rejects "", "   " and a name of dots.
if [[ ! "$GAME_NAME" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]]; then
  echo "reset_world: refusing to run with GAME_NAME='$GAME_NAME'" >&2
  echo "  it reaches 'rm -rf \$USERDATA/Saves/*/\$GAME_NAME'; use a game name of" >&2
  echo "  letters, digits, dot, dash and underscore" >&2
  exit 1
fi

# Overlap guard: this script stops the dedicated and wipes its save, so a reset
# raced against a bench lap or a comparison destroys the world that run is
# measuring, and both report numbers from a world neither measured. Same lock
# file, tag and exit code as the boot, the cohort runner and runlock.py, keyed
# on the LiteNet join port (ServerPort + 2) the runners default to.
# shellcheck source=scripts/harness_lib.sh
source "$ROOT/scripts/harness_lib.sh"
JOIN_PORT="$(sed -n 's|.*name="ServerPort" value="\([0-9]*\)".*|\1|p' \
  "$ROOT/scripts/serverconfig_loadgen.xml" | head -1)"
acquire_run_lock "${RE_LOCK_HOST:-127.0.0.1}" "$(( ${JOIN_PORT:-26900} + 2 ))" 9 "world reset"

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
  # The lock is already held here, and the boot takes the same one: the opt-out
  # is the nested case, the same one the load profiles pass to their own lanes.
  LOADGEN_ALLOW_OVERLAP=1 exec "$ROOT/scripts/start_dedicated_prefab.sh"
fi
