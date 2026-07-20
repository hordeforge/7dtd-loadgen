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
  python3 - <<PY
import json
from pathlib import Path
doc = json.loads(Path("$SCENARIO_FILE").read_text())
for s in doc["scenarios"]:
    ci = " [ci]" if s.get("ci") else ""
    opt = " [optional]" if s.get("optional") else ""
    print(f"{s['id']:28} {s['title']}{ci}{opt}")
PY
  exit 0
fi

if [[ -z "$ID" ]]; then
  echo "ERROR: scenario id required (try --list)" >&2
  exit 2
fi

# Export client env from JSON scenario
eval "$(python3 - <<PY
import json, os, shlex
from pathlib import Path
doc = json.loads(Path("$SCENARIO_FILE").read_text())
sc = next((s for s in doc["scenarios"] if s["id"] == "$ID"), None)
if not sc:
    raise SystemExit(f"unknown scenario: $ID")
defs = doc.get("defaults", {})
client = sc.get("client") or {}
server = sc.get("server")
mode = client.get("mode", "probe")
port = int(client.get("port", defs.get("port", 26902)))  # bot data port = ServerPort+2
host = client.get("host", defs.get("host", "127.0.0.1"))
print(f"export LOADGEN_SCENARIO_ID={shlex.quote(sc['id'])}")
print(f"export LOADGEN_MODE={shlex.quote(mode if mode != 'self-test-join' else 'self-test-join')}")
if mode == "self-test-join":
    print("export LOADGEN_SELF_TEST=0")
    print(f"export LOADGEN_MODE=self-test-join")
telnet_port = int(client.get("telnetPort", defs.get("telnetPort", 8081)))
print(f"export LOADGEN_HOST={shlex.quote(str(host))}")
print(f"export LOADGEN_PORT={port}")
print(f"export LOADGEN_TELNET_PORT={telnet_port}")
print(f"export LOADGEN_COUNT={int(client.get('count', 1))}")
print(f"export LOADGEN_TIMEOUT={int(client.get('timeoutMs', 8000))}")
print(f"export LOADGEN_ACTIONS={int(client.get('actions', 24))}")
print(f"export LOADGEN_MIN_PASS_RATE={float(client.get('minPassRate', 0.95))}")
if client.get("botMode"):
    print(f"export LOADGEN_BOT_MODE={shlex.quote(client['botMode'])}")
if client.get("death"):
    print(f"export LOADGEN_DEATH={shlex.quote(client['death'])}")
if client.get("rampMs"):
    print(f"export LOADGEN_RAMP_MS={int(client['rampMs'])}")
if client.get("maxDynamite") is not None:
    print(f"export LOADGEN_MAX_DYNAMITE={int(client['maxDynamite'])}")
if client.get("noSpawnZombies"):
    print("export LOADGEN_NO_SPAWN=1")
if client.get("seed") is not None:
    print(f"export LOADGEN_SEED={int(client['seed'])}")
if client.get("writeRunManifest"):
    print("export LOADGEN_WRITE_RUN_MANIFEST=1")
if sc.get("priority"):
    print(f"export LOADGEN_PRIORITY={shlex.quote(str(sc['priority']))}")
# server start metadata
if server:
    print(f"export LOADGEN_SERVER_SCRIPT={shlex.quote(server.get('script',''))}")
    env = server.get("env") or {}
    for k, v in env.items():
        print(f"export {k}={shlex.quote(str(v))}")
else:
    print("export LOADGEN_SERVER_SCRIPT=")
print(f"export LOADGEN_SCENARIO_CI={1 if sc.get('ci') else 0}")
print(f"export LOADGEN_SCENARIO_OPTIONAL={1 if sc.get('optional') else 0}")
PY
)"

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
  for i in $(seq 1 90); do
    if bash -c "echo >/dev/tcp/${LOADGEN_HOST}/${READY_PORT}" 2>/dev/null; then
      echo "telnet port ${READY_PORT} open"
      break
    fi
    sleep 2
  done
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
