#!/usr/bin/env bash
# Stock dedicated server benchmark lane: boots ONE stock dedicated (fixed
# world/seed, fresh save per lap) and runs the scenario matrix against it,
# attaching 7dtd-apm on the stock side and writing per-scenario evidence
# (client log, stats-json with the bench block, APM capture, run-meta with
# hostLoad) under workspace/bench/lap<N>/<scenario>/.
#
# Usage:  make bench-stock LAP=1   (or)   bash scripts/bench_stock.sh --lap 1
# Env:    LAP / --lap        evidence dir workspace/bench/lap<N>
#         COMPARE_WORLD      world (default Navezgane)
#         BENCH_ADMIN_PORT   admin telnet (default 8084; docker owns 8081/8082)
#         COMPARE_APM        0 disables the APM capture (default 1)
#         COMPARE_APM_SECONDS  capture size (default 30)
#         BENCH_LAPS_ONLY    1 runs only the bench profile (CI-ish smoke)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LAP="1"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --lap) LAP="$2"; shift 2 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

WORLD_NAME="${COMPARE_WORLD:-Navezgane}"
ADMIN_PORT="${BENCH_ADMIN_PORT:-8084}"
COMPARE_APM="${COMPARE_APM:-1}"
APM_SECONDS="${COMPARE_APM_SECONDS:-30}"
APM_PROJECT="$ROOT/../7dtd-apm"
TELNET_PASSWORD="${COMPARE_TELNET_PASSWORD:-retest}"
BENCH_LAPS_ONLY="${BENCH_LAPS_ONLY:-0}"
OUT="$ROOT/workspace/bench/lap$LAP"
mkdir -p "$OUT"

# A harness that dies mid-lap (set -e abort, SIGINT) must not leave the booted
# server holding ports and memory: the next lap's pre-flight would refuse to
# start and the orphan keeps loading the host. Track the pidfile and kill on exit.
SERVER_PIDFILE=""
cleanup() {
  if [[ -n "$SERVER_PIDFILE" && -f "$SERVER_PIDFILE" ]]; then
    kill -9 "$(cat "$SERVER_PIDFILE" 2>/dev/null)" 2>/dev/null || true
  fi
}
trap cleanup EXIT INT TERM

git_short() { git -C "$1" rev-parse --short HEAD 2>/dev/null || echo unknown; }
git_dirty() { git -C "$1" status --porcelain 2>/dev/null | wc -l; }
hostload() { awk '{print $1}' /proc/loadavg 2>/dev/null || echo "n/a"; }

# Matrix: scenario id -> loadgen args (the bench profile gets its own knobs).
# Bench has warmup+window so its APM capture is aligned with the window.
declare -A SCEN_MATRIX=(
  [probe-15s]="--profile probe --timeout 20000 --count 1"
  [join-fast]="--profile probe --timeout 30000 --count 1"
  [join-probe]="--profile probe --timeout 70000 --count 1"
  [wander-2bot]="--profile steady-wander --timeout 100000 --count 2"
  [soak-4bot]="--profile steady-wander --timeout 320000 --count 4"
  [bench]="--profile bench --count 16 --timeout 130000"
  [horde-lite]="--profile probe --timeout 70000 --count 1 --spawn-entity zombieBoe --spawn-per-player 4 --spawn-every-ms 15000"
)
BENCH_WARMUP_MS=30000  # must match the bench profile preset

# Pre-flight: the admin port must be bindable (docker owns 8081/8082 on this host).
if grep -q ":$ADMIN_PORT " <<<"$(ss -tln 2>/dev/null || true)"; then
  echo "ERROR: admin telnet port $ADMIN_PORT already in use; set BENCH_ADMIN_PORT" >&2
  exit 1
fi
if pgrep -af "7DaysToDieServer.x86_64" >/dev/null 2>&1; then
  echo "ERROR: a stock dedicated is already running" >&2
  exit 1
fi

echo "=== bench-stock lap $LAP world=$WORLD_NAME admin=$ADMIN_PORT ==="
# Sort keys: associative-array iteration order is hash-based, and lap
# summaries/evidence must list scenarios identically on every run.
SCEN_KEYS=$(printf '%s\n' "${!SCEN_MATRIX[@]}" | LC_ALL=C sort)
echo "matrix: $SCEN_KEYS"

# One server session per lap, fresh save (per-lap userdata).
USERDATA="$OUT/userdata"
RE_WORLD_NAME="$WORLD_NAME" RE_GAME_NAME="bench_stock_lap${LAP}" \
  RE_DEDICATED_USERDATA="$USERDATA" RE_MAX_ZOMBIES=16 RE_TELNET_PORT="$ADMIN_PORT" \
  bash "$ROOT/scripts/start_dedicated_prefab.sh" >"$OUT/boot.log" 2>&1 &
ready=0
for _ in $(seq 1 150); do
  stock_log="$(cat "$USERDATA/dedicated.logpath" 2>/dev/null || true)"
  if [[ -n "$stock_log" && -f "$stock_log" ]] && grep -q "StartGame done" "$stock_log" 2>/dev/null; then
    ready=1; break
  fi
  sleep 1
done
if [[ "$ready" != 1 ]]; then
  echo "ERROR: stock not ready in 150s; see $OUT/boot.log" >&2
  kill -9 "$(cat "$USERDATA/dedicated.pid" 2>/dev/null || echo 0)" 2>/dev/null || true
  exit 1
fi
SERVER_PIDFILE="$USERDATA/dedicated.pid"
echo "  stock ready (StartGame done); hostLoad=$(hostload)"

mapfile -t scenarios <<<"$SCEN_KEYS"
[[ "$BENCH_LAPS_ONLY" == "1" ]] && scenarios=(bench)
summary=()
for sc in "${scenarios[@]}"; do
  run_dir="$OUT/$sc"
  mkdir -p "$run_dir"
  echo "--- scenario: $sc (hostLoad=$(hostload))"
  h0=$(hostload)
  t0=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  args="${SCEN_MATRIX[$sc]}"
  loadgen_env=(
    LOADGEN_MODE=join LOADGEN_HOST=127.0.0.1 LOADGEN_PORT=26902
    LOADGEN_TELNET_HOST=127.0.0.1 LOADGEN_TELNET_PORT="$ADMIN_PORT"
    LOADGEN_TELNET_PASSWORD="$TELNET_PASSWORD"
    LOADGEN_STATS_JSON="$run_dir/stats.json"
  )
  # Bench profile: --profile bench sets its own ramp/warmup/window/timeout.
  if [[ "$sc" == "bench" ]]; then
    loadgen_env+=(LOADGEN_BENCH_WARMUP_MS="$BENCH_WARMUP_MS" LOADGEN_BENCH_WINDOW_MS=60000)
  fi
  env "${loadgen_env[@]}" bash -c "bash '$ROOT/scripts/run_loadgen.sh' $args" \
    >"$run_dir/client.log" 2>&1 &
  CLIENT_PID=$!

  # APM capture on the stock side. For the bench profile, align the capture
  # with the measurement window (start after the warm-up); otherwise capture
  # over the connected window immediately.
  APM_PID=""
  if [[ "$COMPARE_APM" != "0" ]] && [[ -d "$APM_PROJECT" ]] && command -v uv >/dev/null; then
    if [[ "$sc" == "bench" ]]; then
      sleep $((BENCH_WARMUP_MS / 1000))
    else
      sleep 8
    fi
    mkdir -p "$run_dir/apm"
    SEVENDTD_APM_DIR="$run_dir/apm" SEVENDTD_TELNET_PASSWORD="$TELNET_PASSWORD" \
      uv run --project "$APM_PROJECT" 7dtd-apm capture --seconds "$APM_SECONDS" --no-app \
        --telnet-port "$ADMIN_PORT" >"$run_dir/apm.log" 2>&1 &
    APM_PID=$!
  fi

  wait "$CLIENT_PID" || true
  if [[ -n "$APM_PID" ]]; then wait "$APM_PID" || true; fi
  h1=$(hostload)
  t1=$(date -u +%Y-%m-%dT%H:%M:%SZ)

  read -r pass fail <<<"$(python3 "$ROOT/scripts/stats_pass_fail.py" "$run_dir/stats.json")"
  bench_line=$(grep -a "BENCH_SUMMARY" "$run_dir/client.log" 2>/dev/null | tail -1 || true)
  cat >"$run_dir/run-meta.json" <<EOF
{
  "scenario": "$sc",
  "lap": $LAP,
  "world": "$WORLD_NAME",
  "startUtc": "$t0",
  "endUtc": "$t1",
  "hostLoadStart": "$h0",
  "hostLoadEnd": "$h1",
  "loadgen": {"git": "$(git_short "$ROOT")", "dirtyFiles": $(git_dirty "$ROOT")},
  "server": {"adminPort": $ADMIN_PORT, "litePort": 26902},
  "matrixArgs": "$args",
  "summary": {"pass": "${pass:-0}", "fail": "${fail:-0}"}
}
EOF
  summary+=("$sc pass=${pass:-0} fail=${fail:-0} load=${h0}->${h1}")
  echo "  $sc: pass=${pass:-0} fail=${fail:-0} hostLoad=${h0}->${h1}"
  [[ -n "$bench_line" ]] && echo "  $bench_line"
done

# Stop the server cleanly (the EXIT trap is a no-op backstop after this).
kill "$(cat "$USERDATA/dedicated.pid" 2>/dev/null || echo 0)" 2>/dev/null || true
SERVER_PIDFILE=""
sleep 2

echo "=== lap $LAP summary ==="
printf '%s\n' "${summary[@]}"
echo "evidence: $OUT"
