#!/usr/bin/env bash
# Run a named scenario from scripts/scenarios/realearth.json (or path in $1).
# CI-safe scenarios (server: null) run immediately. Live scenarios start bots only
# (server must already be up, or pass --start-server).
#
#   ./scripts/run_scenario.sh re-selftest-client-path
#   ./scripts/run_scenario.sh re-h500-join-wander --start-server
#   ./scripts/run_scenario.sh --list
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SCENARIO_FILE="${LOADGEN_SCENARIO_FILE:-$ROOT/scripts/scenarios/realearth.json}"
START_SERVER=0
LIST=0
ID=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --list|-l) LIST=1; shift ;;
    --start-server) START_SERVER=1; shift ;;
    --file) SCENARIO_FILE="$2"; shift 2 ;;
    -h|--help)
      echo "Usage: $0 [--list] [--start-server] [--file path.json] <scenario-id>"
      exit 0
      ;;
    *) ID="$1"; shift ;;
  esac
done

if [[ ! -f "$SCENARIO_FILE" ]]; then
  echo "ERROR: scenario file not found: $SCENARIO_FILE" >&2
  exit 1
fi

if [[ "$LIST" == "1" ]]; then
  exec python3 "$ROOT/scripts/scenario_env.py" --list "$SCENARIO_FILE"
fi

if [[ -z "$ID" ]]; then
  echo "ERROR: scenario id required (try --list)" >&2
  exit 2
fi

# Export client env from JSON scenario (argv-passed; fail loudly on an unknown id)
scenario_exports="$(python3 "$ROOT/scripts/scenario_env.py" export "$SCENARIO_FILE" "$ID")" || exit 1
eval "$scenario_exports"

echo "=== scenario $LOADGEN_SCENARIO_ID ==="
echo "mode=$LOADGEN_MODE port=${LOADGEN_PORT:-} count=${LOADGEN_COUNT:-}"

if [[ -n "${LOADGEN_SERVER_SCRIPT:-}" && "$START_SERVER" == "1" ]]; then
  echo "Starting server via $LOADGEN_SERVER_SCRIPT (background)..."
  chmod +x "$ROOT/scripts/$LOADGEN_SERVER_SCRIPT"
  # Server script often exec's and blocks; run in background
  bash "$ROOT/scripts/$LOADGEN_SERVER_SCRIPT" &
  SPID=$!
  echo "server pid=$SPID (waiting for listen)"
  # Probe the telnet TCP port, not LOADGEN_PORT: that is the LiteNetLib UDP game
  # port and /dev/tcp is TCP-only, so probing it can never succeed. The telnet
  # admin port is a real TCP listener that comes up once the server is ready.
  READY_PORT="${LOADGEN_TELNET_PORT:-8081}"
  server_ready=0
  for _ in $(seq 1 90); do
    if bash -c "echo >/dev/tcp/${LOADGEN_HOST}/${READY_PORT}" 2>/dev/null; then
      echo "telnet port ${READY_PORT} open"
      server_ready=1
      break
    fi
    sleep 2
  done
  if [[ "$server_ready" != "1" ]]; then
    # Fail loudly instead of pointing bots at a server that never came up:
    # every join would fail with confusing per-bot errors and a false gate FAIL.
    echo "ERROR: server did not open ${LOADGEN_HOST}:${READY_PORT} within 180s; see its boot output" >&2
    pidfile="${RE_DEDICATED_USERDATA:-$HOME/.cache/7dtd-loadgen}/dedicated.pid"
    if [[ -f "$pidfile" ]]; then
      kill -9 "$(cat "$pidfile" 2>/dev/null)" 2>/dev/null || true
      echo "killed half-booted server (pidfile $pidfile)" >&2
    fi
    exit 1
  fi
fi

chmod +x "$ROOT/scripts/run_loadgen.sh"

# self-test-join is handled by EXE flag path
if [[ "${LOADGEN_MODE}" == "self-test-join" ]]; then
  EXE="$ROOT/src/LoadGen/bin/Release/net8.0/7dtd-loadgen"
  make -C "$ROOT" build
  SEED="${LOADGEN_SEED:-7}"
  ACTIONS="${LOADGEN_ACTIONS:-24}"
  MANIFEST_ARGS=()
  if [[ -n "${LOADGEN_RUN_MANIFEST:-}" ]]; then
    MANIFEST_ARGS+=(--run-manifest "$LOADGEN_RUN_MANIFEST")
  elif [[ "${LOADGEN_WRITE_RUN_MANIFEST:-0}" == "1" ]]; then
    OUT_DIR="${LOADGEN_SCRATCH:-$ROOT/src/LoadGen/bin}"
    mkdir -p "$OUT_DIR"
    MANIFEST_ARGS+=(--run-manifest "$OUT_DIR/loadgen_run_${LOADGEN_SCENARIO_ID:-selftest}.json")
  fi
  SCEN_ARGS=()
  if [[ -n "${LOADGEN_SCENARIO_ID:-}" ]]; then
    SCEN_ARGS+=(--scenario-id "$LOADGEN_SCENARIO_ID")
  fi
  if [[ -x "$EXE" ]]; then
    "$EXE" --self-test-join --actions "$ACTIONS" --seed "$SEED" "${SCEN_ARGS[@]}" "${MANIFEST_ARGS[@]}"
  else
    dotnet exec "$ROOT/src/LoadGen/bin/Release/net8.0/7dtd-loadgen.dll" \
      --self-test-join --actions "$ACTIONS" --seed "$SEED" "${SCEN_ARGS[@]}" "${MANIFEST_ARGS[@]}"
  fi
  exit $?
fi

exec "$ROOT/scripts/run_loadgen.sh"
