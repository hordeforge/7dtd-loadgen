#!/usr/bin/env bash
# Start a RealEarth-enabled dedicated server for loadgen bot scenarios.
# Delegates install/expand/world prep to sibling 7dtd-realearth; keeps bot ownership here.
#
#   ./scripts/start_dedicated_realearth.sh
#   RE_SCENARIO_PACK=h500 RE_WORLD_NAME=RealEarth_H500 ./scripts/start_dedicated_realearth.sh
#   RE_SCENARIO_PACK=everest RE_WORLD_NAME=RealEarth_HeightTest ./scripts/start_dedicated_realearth.sh
#
# Bots (this project):
#   LOADGEN_PORT=26902 LOADGEN_MODE=join LOADGEN_COUNT=6 ./scripts/run_loadgen.sh
#   make join-realearth
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RE_ROOT="${REALEARTH_ROOT:-$ROOT/../7dtd-realearth}"
PACK="${RE_SCENARIO_PACK:-h500}"
WORLD_NAME="${RE_WORLD_NAME:-}"
USERDATA="${RE_DEDICATED_USERDATA:-$HOME/.cache/realearth-dedicated}"

if [[ ! -d "$RE_ROOT" ]]; then
  echo "ERROR: RealEarth project not found at $RE_ROOT" >&2
  echo "Set REALEARTH_ROOT or keep sibling layout Desktop/7dtd/{7dtd-loadgen,7dtd-realearth}" >&2
  exit 1
fi

case "$PACK" in
  h500|H500)
    WORLD_NAME="${WORLD_NAME:-RealEarth_H500}"
    echo "=== loadgen → RealEarth dedicated (H500 staged peak) ==="
    # Ensure H500 pack exists (offline synthetic; no DEM network)
    if [[ ! -d "$RE_ROOT/data/samples/height_test_500/tiles" ]]; then
      echo "Building H500 sample pack in 7dtd-realearth..."
      make -C "$RE_ROOT" height-map-500
    fi
    # Install pack + mod into dedicated Mods (expand + SharedFixed config)
    if [[ -x "$RE_ROOT/scripts/install_height_pack.sh" ]]; then
      chmod +x "$RE_ROOT/scripts/install_height_pack.sh"
      RE_WORLD_NAME="$WORLD_NAME" "$RE_ROOT/scripts/install_height_pack.sh" h500 || true
    fi
    ;;
  everest|height_test|HeightTest)
    WORLD_NAME="${WORLD_NAME:-RealEarth_HeightTest}"
    echo "=== loadgen → RealEarth dedicated (Everest / height_test pack) ==="
    if [[ ! -d "$RE_ROOT/data/samples/height_test/tiles" ]]; then
      echo "NOTE: height_test pack missing; run: make -C $RE_ROOT install-height" >&2
    fi
    if [[ -x "$RE_ROOT/scripts/install_height_pack.sh" ]]; then
      chmod +x "$RE_ROOT/scripts/install_height_pack.sh"
      RE_WORLD_NAME="$WORLD_NAME" "$RE_ROOT/scripts/install_height_pack.sh" everest || true
    fi
    ;;
  *)
    echo "ERROR: unknown RE_SCENARIO_PACK=$PACK (use h500|everest)" >&2
    exit 2
    ;;
esac

export RE_WORLD_NAME="$WORLD_NAME"
export RE_DEDICATED_USERDATA="$USERDATA"
export SEVENDTD_SERVER_DIR="${SEVENDTD_SERVER_DIR:-$HOME/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server}"
export SEVENDTD_GAME_DIR="${SEVENDTD_GAME_DIR:-$HOME/.local/share/Steam/steamapps/common/7 Days To Die}"

echo "RealEarth root: $RE_ROOT"
echo "World:          $WORLD_NAME"
echo "UserData:       $USERDATA"
echo "Port:           26900 (see 7dtd-realearth/scripts/serverconfig_height_test.xml)"
echo "Bots:           cd $ROOT && LOADGEN_PORT=26902 make join-realearth"

# Prefer long-running minimal dedicated (leaves server up for bots)
START="$RE_ROOT/scripts/start_dedicated_minimal.sh"
if [[ ! -x "$START" ]]; then
  chmod +x "$START" 2>/dev/null || true
fi
if [[ ! -f "$START" ]]; then
  echo "ERROR: missing $START" >&2
  exit 1
fi
exec bash "$START"
