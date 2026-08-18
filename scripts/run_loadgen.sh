#!/usr/bin/env bash
# LiteNetLib simulated-client probe (1..1000+ in one process).
# Not a full game client — connectivity + handshake progress for CI / load.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.cache/dotnet-sdk}"
export PATH="${DOTNET_ROOT}:${PATH}"
HOST="${LOADGEN_HOST:-127.0.0.1}"
# Bots speak LiteNetLib directly, so they must hit the LiteNet data port =
# ServerPort + 2 (26902 for the stock 26900 server). 26900 itself is the game
# client's "Connect to IP" port and a bot connect there fails (ConnectionFailed).
PORT="${LOADGEN_PORT:-26902}"
KEY="${LOADGEN_KEY:-}"
TIMEOUT="${LOADGEN_TIMEOUT:-8000}"
COUNT="${LOADGEN_COUNT:-2}"
CONCURRENCY="${LOADGEN_CONCURRENCY:-0}"   # 0 = CLI auto
MIN_PASS="${LOADGEN_MIN_PASS_RATE:-0.95}"
RAMP_MS="${LOADGEN_RAMP_MS:-0}"
QUIET="${LOADGEN_QUIET:-}"
SELF_TEST="${LOADGEN_SELF_TEST:-0}"
# MODE: probe (default) | join | self-test-join
MODE="${LOADGEN_MODE:-probe}"
ACTIONS="${LOADGEN_ACTIONS:-24}"
# Bot behaviour (join only). Empty = CLI scale defaults (wander@1, mixed@count>=2)
BOT_MODE="${LOADGEN_BOT_MODE:-}"
# Weighted per-bot mode mix, e.g. "traverse:35,combat:20,bait:15". Overrides BOT_MODE.
BOT_MIX="${LOADGEN_BOT_MIX:-}"
DEATH="${LOADGEN_DEATH:-}"
PACE_MS="${LOADGEN_PACE_MS:-}"
SPAWN_ENTITY="${LOADGEN_SPAWN_ENTITY:-}"
SPAWN_PER_PLAYER="${LOADGEN_SPAWN_PER_PLAYER:-}"
SPAWN_EVERY_MS="${LOADGEN_SPAWN_EVERY_MS:-}"
HORDE_EVERY_MS="${LOADGEN_HORDE_EVERY_MS:-}"
TELNET_HOST="${LOADGEN_TELNET_HOST:-}"
TELNET_PORT="${LOADGEN_TELNET_PORT:-}"
TELNET_PASSWORD="${LOADGEN_TELNET_PASSWORD:-}"
HORDE_WAVES="${LOADGEN_HORDE_WAVES:-}"
MAX_DYNAMITE="${LOADGEN_MAX_DYNAMITE:-}"
NO_SPAWN="${LOADGEN_NO_SPAWN:-}"
SEED="${LOADGEN_SEED:-}"
SCRATCH_OUT="${RE_SCRATCH:-}"
PROJ="$ROOT/src/LoadGen/LoadGen.csproj"
OUT_DIR="$ROOT/src/LoadGen/bin/Release/net8.0"
EXE="$OUT_DIR/7dtd-loadgen"

if [[ "$SELF_TEST" == "1" ]]; then
  MODE="self-test"
fi

echo "=== 7DTD load generator ==="
echo "Mode=$MODE Host=$HOST Port=$PORT Count=$COUNT Concurrency=${CONCURRENCY:-auto} Timeout=${TIMEOUT}ms bot=${BOT_MODE:-auto} death=${DEATH:-auto}"

if [[ ! -x "$DOTNET_ROOT/dotnet" && ! -x "$(command -v dotnet 2>/dev/null || true)" ]]; then
  echo "ERROR: dotnet SDK not found (set DOTNET_ROOT)" >&2
  exit 1
fi

# Large loads need more sockets/files than default soft limits
if (( COUNT >= 100 )); then
  # best-effort; ignore if not permitted
  ulimit -n 65535 2>/dev/null || ulimit -n 16384 2>/dev/null || true
  echo "ulimit -n = $(ulimit -n 2>/dev/null || echo unknown)"
fi

dotnet build "$PROJ" -c Release -v q
test -f "$EXE" || test -f "$OUT_DIR/7dtd-loadgen.dll"

LOG="${SCRATCH_OUT:-$ROOT/src/LoadGen/bin}/simulated_client.log"
mkdir -p "$(dirname "$LOG")"

args=()
case "$MODE" in
  self-test-join)
    args+=(--self-test-join --actions "$ACTIONS" --log "$LOG")
    ;;
  self-test)
    args+=(--self-test --count "$COUNT" --timeout "$TIMEOUT" --log "$LOG")
    ;;
  join)
    args+=(--join --host "$HOST" --port "$PORT" --count "$COUNT" --timeout "$TIMEOUT"
           --actions "$ACTIONS" --min-pass-rate "$MIN_PASS" --log "$LOG")
    if [[ -n "$KEY" ]]; then args+=(--key "$KEY"); fi
    if [[ -n "$BOT_MIX" ]]; then args+=(--bot-mix "$BOT_MIX"); fi
    if [[ -n "$BOT_MODE" ]]; then args+=(--mode "$BOT_MODE"); fi
    if [[ -n "$DEATH" ]]; then args+=(--death "$DEATH"); fi
    if [[ -n "$PACE_MS" ]]; then args+=(--pace-ms "$PACE_MS"); fi
    if [[ -n "$SPAWN_ENTITY" ]]; then args+=(--spawn-entity "$SPAWN_ENTITY"); fi
    if [[ -n "$SPAWN_PER_PLAYER" ]]; then args+=(--spawn-per-player "$SPAWN_PER_PLAYER"); fi
    if [[ -n "$SPAWN_EVERY_MS" ]]; then args+=(--spawn-every-ms "$SPAWN_EVERY_MS"); fi
    if [[ -n "$TELNET_HOST" ]]; then args+=(--telnet-host "$TELNET_HOST"); fi
    if [[ -n "$TELNET_PORT" ]]; then args+=(--telnet-port "$TELNET_PORT"); fi
    if [[ -n "$TELNET_PASSWORD" ]]; then args+=(--telnet-password "$TELNET_PASSWORD"); fi
    if [[ -n "$HORDE_EVERY_MS" ]]; then args+=(--horde-every-ms "$HORDE_EVERY_MS"); fi
    if [[ -n "$HORDE_WAVES" ]]; then args+=(--horde-waves "$HORDE_WAVES"); fi
    if [[ -n "$MAX_DYNAMITE" ]]; then args+=(--max-dynamite "$MAX_DYNAMITE"); fi
    if [[ -n "$NO_SPAWN" ]]; then args+=(--no-spawn-zombies); fi
    if [[ -n "$SEED" ]]; then args+=(--seed "$SEED"); fi
    if [[ -n "${LOADGEN_STATS_JSON:-}" ]]; then args+=(--stats-json "$LOADGEN_STATS_JSON"); fi
    if [[ "$RAMP_MS" != "0" ]]; then args+=(--ramp-ms "$RAMP_MS"); fi
    ;;
  *)
    args+=(--host "$HOST" --port "$PORT" --count "$COUNT" --timeout "$TIMEOUT"
           --min-pass-rate "$MIN_PASS" --log "$LOG")
    if [[ -n "$KEY" ]]; then args+=(--key "$KEY"); fi
    ;;
esac
if [[ "$CONCURRENCY" != "0" && -n "$CONCURRENCY" && "$MODE" != "self-test-join" ]]; then
  args+=(--concurrency "$CONCURRENCY")
fi
if [[ "$RAMP_MS" != "0" && "$MODE" == "probe" ]]; then
  args+=(--ramp-ms "$RAMP_MS")
fi
if [[ -n "$QUIET" || "$COUNT" -ge 32 ]] && [[ "$MODE" == "probe" || "$MODE" == "self-test" ]]; then
  args+=(--quiet)
fi

run_cli() {
  if [[ -f "$EXE" ]]; then
    "$EXE" "$@"
  else
    dotnet exec "$OUT_DIR/7dtd-loadgen.dll" "$@"
  fi
}

set +e
run_cli "${args[@]}"
rc=$?
set -e

MANIFEST="${LOADGEN_MANIFEST:-${SCRATCH_OUT:-$ROOT/src/LoadGen/bin}/loadgen_manifest.json}"
mkdir -p "$(dirname "$MANIFEST")"
LOADGEN_RC="$rc" LOADGEN_MODE="$MODE" LOADGEN_HOST="$HOST" LOADGEN_PORT="$PORT" \
LOADGEN_COUNT="$COUNT" LOADGEN_CONCURRENCY="$CONCURRENCY" LOADGEN_TIMEOUT="$TIMEOUT" \
LOADGEN_ACTIONS="$ACTIONS" LOADGEN_RAMP_MS="$RAMP_MS" LOADGEN_BOT_MODE="$BOT_MODE" LOADGEN_BOT_MIX="$BOT_MIX" \
LOADGEN_DEATH="$DEATH" LOADGEN_MANIFEST_PATH="$MANIFEST" \
LOADGEN_MAX_DYNAMITE="$MAX_DYNAMITE" LOADGEN_SEED="$SEED" LOADGEN_SPAWN_ENTITY="$SPAWN_ENTITY" \
LOADGEN_SPAWN_PER_PLAYER="$SPAWN_PER_PLAYER" LOADGEN_SPAWN_EVERY_MS="$SPAWN_EVERY_MS" python3 - <<'PY'
import json, os
from datetime import datetime, timezone
from pathlib import Path

def integer(name):
    try: return int(os.environ.get(name, "0"))
    except ValueError: return 0

manifest = {
    "schema": "7dtd.loadgen.run.v1", "endedAt": datetime.now(timezone.utc).isoformat(),
    "mode": os.environ["LOADGEN_MODE"], "target": {"host": os.environ["LOADGEN_HOST"], "port": integer("LOADGEN_PORT")},
    "workload": {"clients": integer("LOADGEN_COUNT"), "concurrency": integer("LOADGEN_CONCURRENCY"),
        "timeoutMs": integer("LOADGEN_TIMEOUT"), "actionsPerClient": integer("LOADGEN_ACTIONS"),
        "rampMs": integer("LOADGEN_RAMP_MS"), "botMode": os.environ.get("LOADGEN_BOT_MODE") or "auto",
        "deathMode": os.environ.get("LOADGEN_DEATH") or "auto",
        "seed": os.environ.get("LOADGEN_SEED") or "default",
        "maxDynamite": os.environ.get("LOADGEN_MAX_DYNAMITE") or "default",
        "spawnEntity": os.environ.get("LOADGEN_SPAWN_ENTITY") or "default",
        "spawnPerPlayer": integer("LOADGEN_SPAWN_PER_PLAYER"),
        "spawnEveryMs": integer("LOADGEN_SPAWN_EVERY_MS")},
    "result": {"exitCode": integer("LOADGEN_RC"), "passed": integer("LOADGEN_RC") == 0}
}
Path(os.environ["LOADGEN_MANIFEST_PATH"]).write_text(json.dumps(manifest, indent=2) + "\n")
PY
echo "manifest: $MANIFEST"

if [[ -n "$SCRATCH_OUT" && -d "$SCRATCH_OUT" && -f "$LOG" ]]; then
  dest="$SCRATCH_OUT/simulated_client.log"
  if [[ "$(readlink -f "$LOG" 2>/dev/null || realpath "$LOG" 2>/dev/null || echo "$LOG")" \
     != "$(readlink -f "$dest" 2>/dev/null || realpath "$dest" 2>/dev/null || echo "$dest")" ]]; then
    cp -f "$LOG" "$dest"
  fi
  if (( COUNT >= 100 )); then
    cp -f "$LOG" "$SCRATCH_OUT/simulated_client_n${COUNT}.log"
  fi
fi

if (( rc == 0 )); then
  echo "PASS: $COUNT simulated client(s) (exit 0)"
  exit 0
fi
echo "FAIL: simulated client load count=$COUNT exit=$rc"
exit "$rc"
