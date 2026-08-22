#!/usr/bin/env bash
# Stock-vs-zdtd comparison runner (SUT harness).
#
# Runs the same client scenario against the stock dedicated server and/or
# zdtd, captures the observable surface per run (server log, loadgen outcome,
# telnet snapshot, save-file inventory), and in --sut all mode diffs the two
# runs into a machine-readable report via tools/sut_report.py.
#
#   ./scripts/compare_sut.sh --scenario join-probe --sut all
#   ./scripts/compare_sut.sh --scenario join-probe --sut zdtd
#
# Scenario config is the same for both servers: this script's client knobs
# (count/actions/timeout) come from env so a single config drives both sides.
# A difference between the two runs is a FINDING to triage (zdtd bug vs harness
# artifact vs known divergence), never a pass to fake.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT_ROOT="${COMPARE_OUT:-$ROOT/workspace/comparison}"
SCENARIO_ID=""
SUTS=""

# A harness that is killed mid-run (SIGTERM/SIGINT) must not leave the booted
# server behind: the next run's pkill would otherwise kill an unrelated
# server, and an orphan holds the ports. Track the current server PID file
# and clean it on exit.
CURRENT_PIDFILE=""
cleanup() {
  if [[ -n "$CURRENT_PIDFILE" && -f "$CURRENT_PIDFILE" ]]; then
    kill -9 "$(cat "$CURRENT_PIDFILE")" 2>/dev/null || true
  fi
}
trap cleanup EXIT INT TERM

while [[ $# -gt 0 ]]; do
  case "$1" in
    --scenario) SCENARIO_ID="$2"; shift 2 ;;
    --sut)
      case "$2" in
        stock) SUTS="stock" ;;
        zdtd) SUTS="zdtd" ;;
        all) SUTS="stock zdtd" ;;
        *) echo "ERROR: --sut must be stock|zdtd|all" >&2; exit 2 ;;
      esac
      shift 2 ;;
    --world) WORLD_NAME="$2"; shift 2 ;;
    --list)
      python3 "$ROOT/scripts/sut_catalog.py" list
      exit 0 ;;
    -h|--help)
      echo "Usage: $0 --scenario <id> --sut stock|zdtd|all [client envs]"
      echo "       $0 --list"
      echo "       $0 --world <name> (default Navezgane; non-default worlds get"
      echo "                          a -<world> tagged evidence dir)"
      echo "Env knobs: COMPARE_COUNT COMPARE_ACTIONS COMPARE_TIMEOUT_MS"
      echo "          COMPARE_SPAWN_ENTITY/PER_PLAYER/EVERY_MS COMPARE_SNAPSHOT_DELAY_MS"
      echo "          COMPARE_WORLD COMPARE_HOST COMPARE_TELNET_PASSWORD"
      echo "          COMPARE_APM=0|1 (stock cost capture; default 1)"
      echo "          COMPARE_APM_SECONDS (capture window; default 30)"
      exit 0 ;;
    *) echo "ERROR: unknown arg $1" >&2; exit 2 ;;
  esac
done

if [[ -z "$SCENARIO_ID" || -z "$SUTS" ]]; then
  echo "ERROR: --scenario and --sut required" >&2
  exit 2
fi

# Scenario knobs: env (if explicitly set) wins, then the catalog, then
# defaults. The catalog is scripts/scenarios/sut.json.
read -r CAT_COUNT CAT_ACTIONS CAT_TIMEOUT CAT_SPAWN_ENT CAT_SPAWN_PER CAT_SPAWN_EVERY CAT_SNAPSHOT_DELAY < <(
  python3 "$ROOT/scripts/sut_catalog.py" get "$SCENARIO_ID"
)
COUNT="${COMPARE_COUNT:-${CAT_COUNT:-1}}"
ACTIONS="${COMPARE_ACTIONS:-${CAT_ACTIONS:-0}}"
TIMEOUT_MS="${COMPARE_TIMEOUT_MS:-${CAT_TIMEOUT:-60000}}"
# Optional spawn-pressure knobs from the catalog; LOADGEN_* envs pass through
# to run_loadgen.sh when unset, so a user override still wins.
SPAWN_ENTITY="${COMPARE_SPAWN_ENTITY:-${CAT_SPAWN_ENT:-}}"
SPAWN_PER_PLAYER="${COMPARE_SPAWN_PER_PLAYER:-${CAT_SPAWN_PER:-}}"
SPAWN_EVERY_MS="${COMPARE_SPAWN_EVERY_MS:-${CAT_SPAWN_EVERY:-}}"
SNAPSHOT_DELAY_MS="${COMPARE_SNAPSHOT_DELAY_MS:-${CAT_SNAPSHOT_DELAY:-0}}"
WORLD_NAME="${WORLD_NAME:-${COMPARE_WORLD:-Navezgane}}"
HOST="${COMPARE_HOST:-127.0.0.1}"
# Stock-side cost axis via the sibling 7dtd-apm tool (bridge must be installed
# in the stock dedicated server; see ../7dtd-apm). COMPARE_APM=0 disables;
# COMPARE_APM_SECONDS sizes the window (default 30s, aligned with the telnet
# snapshot while the bot is connected). A capture failure is logged and never
# fails the scenario.
COMPARE_APM="${COMPARE_APM:-1}"
APM_SECONDS="${COMPARE_APM_SECONDS:-30}"
APM_PROJECT="$ROOT/../7dtd-apm"
# Stock telnet auth (test-only lab password, never a secret).
TELNET_PASSWORD="${COMPARE_TELNET_PASSWORD:-retest}"
# Per-side admin/telnet ports. Hosts may have 8081/8082 occupied by an
# unrelated service (docker containers); when that happens BOTH servers fail
# to bind their admin console and every telnet axis silently degrades (empty
# snapshots, dead spawn pressure, phantom 0/0 entity rows) - observed
# 2026-08-18 with docker-proxy on 8081/8082. Override with
# COMPARE_TELNET_PORT_STOCK / COMPARE_TELNET_PORT_ZDTD (e.g. 8084/8085).
TELNET_PORT_STOCK="${COMPARE_TELNET_PORT_STOCK:-8081}"
TELNET_PORT_ZDTD="${COMPARE_TELNET_PORT_ZDTD:-8082}"

echo "=== compare scenario '$SCENARIO_ID' on: $SUTS (count=$COUNT actions=$ACTIONS) ==="

# Evidence must never silently clobber: a non-default world writes to a
# world-tagged dir (join-fast + Pregen08k01 -> join-fast-pregen08k01), unless
# the scenario id already carries that tag (compare-worlds convention).
# This is what keeps the canonical dir (default world) and the per-world
# matrix dirs distinct without manual renaming.
WORLD_TAG="${WORLD_NAME,,}"
if [[ -n "$WORLD_TAG" && "$WORLD_TAG" != "navezgane" ]]; then
  if [[ "$SCENARIO_ID" != *"-${WORLD_TAG}" ]]; then
    echo "note: world '$WORLD_NAME' is not the default - evidence goes to ${SCENARIO_ID}-${WORLD_TAG}"
    OUT_ROOT="$OUT_ROOT/${SCENARIO_ID}-${WORLD_TAG}"
  fi
fi

# Sanity: a scenario id that already encodes a world must agree with it.
for known in navezgane pregen06k01 pregen06k02 pregen08k01 pregen08k02; do
  if [[ "$SCENARIO_ID" == *"-${known}" && "$known" != "$WORLD_TAG" ]]; then
    echo "WARN: scenario id '$SCENARIO_ID' implies world '$known' but --world/COMPARE_WORLD='$WORLD_NAME' - run-meta will tell the truth, the id won't"
  fi
done

for sut in $SUTS; do
  run_dir="$OUT_ROOT/$SCENARIO_ID/$sut"
  rm -rf "$run_dir"
  mkdir -p "$run_dir"
  echo "--- SUT: $sut -> $run_dir"

  case "$sut" in
    stock)
      # Bots speak LiteNetLib directly, so they hit the LiteNet data port =
      # ServerPort + 2 (26902 for the stock 26900 server). 26900 itself is the
      # game client's "Connect to IP" port; a bot connect there fails.
      STOCK_SERVER_PORT="$(grep -oP 'name="ServerPort" value="\K[0-9]+' \
        "$ROOT/scripts/serverconfig_loadgen.xml" | head -1)"
      STOCK_SERVER_PORT="${STOCK_SERVER_PORT:-26900}"
      # Admin console must actually bind: without it every telnet axis is a
      # silent phantom. Fail loudly when the port is already taken.
      TELNET_PORT="$TELNET_PORT_STOCK"
      if grep -q ":$TELNET_PORT " <<<"$(ss -tln 2>/dev/null || true)"; then
        echo "  ERROR: admin telnet port $TELNET_PORT already in use (unrelated service?); pick free ports via COMPARE_TELNET_PORT_STOCK/COMPARE_TELNET_PORT_ZDTD" >&2
        exit 1
      fi
      USERDATA="$run_dir/userdata"
      RE_WORLD_NAME="$WORLD_NAME" RE_GAME_NAME="${SCENARIO_ID}_stock" \
        RE_DEDICATED_USERDATA="$USERDATA" RE_MAX_ZOMBIES=16 \
        RE_TELNET_PORT="$TELNET_PORT" \
        bash "$ROOT/scripts/start_dedicated_prefab.sh" >"$run_dir/boot.log" 2>&1 &
      ready=0
      for _ in $(seq 1 150); do
        # The join-ready signal is the server log's "StartGame done", not telnet
        # up: telnet accepts connections while the world is still loading and
        # every login is then denied with EKickReason ServerStateAuthorization
        # (live-observed 2026-08-12: 5 denials before StartGame done).
        stock_log="$(cat "$USERDATA/dedicated.logpath" 2>/dev/null || true)"
        if [[ -n "$stock_log" && -f "$stock_log" ]] && \
           grep -q "StartGame done" "$stock_log" 2>/dev/null; then
          ready=1; break
        fi
        sleep 1
      done
      if [[ "$ready" != 1 ]]; then
        echo "  stock: not ready in 150s; see boot.log" >&2
        kill -9 "$(cat "$USERDATA/dedicated.pid" 2>/dev/null || echo 0)" 2>/dev/null || true
        exit 1
      fi
      echo "  $sut ready (StartGame done in server log)"
      BOT_PORT=$((STOCK_SERVER_PORT + 2))
      PIDFILE="$USERDATA/dedicated.pid"
      CURRENT_PIDFILE="$PIDFILE"
      # gettime first and last so the capture can derive the game-clock rate.
      TELNET_CMD="gettime,getgamestat,listents,listplayers,gettime"
      ;;
    zdtd)
      # Same game options stock runs with (live values from the stock run's
      # getgamestat/getgamepref: day 60/18, max zombies 16, difficulty 1, move
      # 2/3). Written per scenario so both servers get one config each.
      ZDTD_CFG="$run_dir/serverconfig.xml"
      cat >"$ZDTD_CFG" <<EOF
<ServerSettings>
  <property name="GameWorld" value="$WORLD_NAME"/>
  <property name="GameName" value="${SCENARIO_ID}_zdtd"/>
  <property name="ServerMaxPlayerCount" value="64"/>
  <property name="MaxSpawnedZombies" value="16"/>
  <property name="EnemyDifficulty" value="1"/>
  <property name="EnemySpawnMode" value="true"/>
  <property name="GameDifficulty" value="1"/>
  <property name="PlayerKillingMode" value="0"/>
  <property name="LandClaimExpiryDays" value="7"/>
  <property name="DayNightLength" value="60"/>
  <property name="DayLightLength" value="18"/>
  <property name="ZombieMove" value="2"/>
  <property name="ZombieMoveNight" value="3"/>
  <property name="EACEnabled" value="false"/>
</ServerSettings>
EOF
      TELNET_PORT="$TELNET_PORT_ZDTD"
      if grep -q ":$TELNET_PORT " <<<"$(ss -tln 2>/dev/null || true)"; then
        echo "  ERROR: admin telnet port $TELNET_PORT already in use (unrelated service?); pick free ports via COMPARE_TELNET_PORT_STOCK/COMPARE_TELNET_PORT_ZDTD" >&2
        exit 1
      fi
      RE_SUT_PORT=27120 RE_SUT_ADMIN_PORT="$TELNET_PORT" RE_SUT_WORLD="$run_dir/world" \
        RE_SUT_WORLD_NAME="$WORLD_NAME" RE_SUT_SERVERCONFIG="$ZDTD_CFG" \
        RE_SUT_LOGFILE="$run_dir/server.log" \
        bash "$ROOT/scripts/sut_zdtd.sh" >"$run_dir/boot.log" 2>&1 &
      ready=0
      for _ in $(seq 1 180); do
        # "config port=" prints mid-init; the network-ready marker is the last
        # init line (challenge + negotiated package mappings), printed with a
        # two-space indent (not the "zdtd: " prefix).
        if grep -q 'challenge=0x.*mappings=' "$run_dir/server.log" 2>/dev/null; then ready=1; break; fi
        sleep 1
      done
      if [[ "$ready" != 1 ]]; then
        echo "  zdtd: not ready in 180s; see boot.log" >&2
        exit 1
      fi
      echo "  zdtd ready (challenge line in server.log)"
      BOT_PORT=$((27120 + 2))  # zdtd binds LiteNetLib on --port + 2
      PIDFILE="$run_dir/world/dedicated.pid"
      CURRENT_PIDFILE="$PIDFILE"
      TELNET_CMD="gettime,getgamestat,listents,listplayers,gettime"
      ;;
  esac

  # Post-ready health check: the ready line can precede full usability, or the
  # server process can die from host pressure (swap/oom). Verify process, UDP
  # listener and console before spending a client window on a dead side, so a
  # failed phase fails loudly instead of reporting a phantom "ran with 0 joins".
  if ! kill -0 "$(cat "$PIDFILE" 2>/dev/null || echo 0)" 2>/dev/null; then
    echo "  ERROR: $sut server process died after ready; see server.log" >&2
    exit 1
  fi
  # The UDP listener can bind a beat after the ready log line; retry before
  # declaring a dead side. grep reads ss output via substitution (not a pipe)
  # so the check keys on grep's verdict: under set -o pipefail, a transient
  # ss non-zero exit would otherwise override a matching grep and false-fail
  # a healthy server (live-observed).
  udp_ok=0
  for _ in $(seq 1 15); do
    if grep -q ":$BOT_PORT " <<<"$(ss -uln 2>/dev/null || true)"; then udp_ok=1; break; fi
    sleep 1
  done
  if [[ "$udp_ok" != 1 ]]; then
    echo "  ERROR: $sut not listening on UDP $BOT_PORT after ready" >&2
    exit 1
  fi
  PROBE_ARGS=(--commands gettime --out /dev/null)
  if [[ "$sut" == "stock" ]]; then
    PROBE_ARGS+=(--password "$TELNET_PASSWORD")
  fi
  if ! python3 "$ROOT/tools/sut_telnet.py" "$HOST" "$TELNET_PORT" "${PROBE_ARGS[@]}"; then
    echo "  ERROR: $sut admin console not answering after ready" >&2
    exit 1
  fi
  echo "  $sut healthy (process, UDP $BOT_PORT, console)"

  # Small settle so the connection manager is accepting logins. No separate
  # login probe: a probe's join + disconnect recycles its loopback bind, which
  # the real client then reuses and stock's per-IP throttle drops at LiteNet
  # level (live-observed 8 recv=0 fails). The client's own rejoin loop plus the
  # honest zero-PASS finding are the right failure surface for the residual
  # post-ready denial window.
  sleep 5

  # Run the client - the SAME scenario on both servers. Run it in the
  # background so the telnet snapshot happens while the bot is connected
  # (stock and zdtd both kick players when the client times out; a snapshot
  # after the client exits always reads 0 players).
  LOADGEN_MODE=join LOADGEN_COUNT="$COUNT" LOADGEN_ACTIONS="$ACTIONS" \
    LOADGEN_TIMEOUT="$TIMEOUT_MS" LOADGEN_HOST="$HOST" LOADGEN_PORT="$BOT_PORT" \
    LOADGEN_SPAWN_ENTITY="$SPAWN_ENTITY" LOADGEN_SPAWN_PER_PLAYER="$SPAWN_PER_PLAYER" \
    LOADGEN_SPAWN_EVERY_MS="$SPAWN_EVERY_MS" \
    LOADGEN_TELNET_HOST="$HOST" LOADGEN_TELNET_PORT="$TELNET_PORT" \
    LOADGEN_TELNET_PASSWORD="$TELNET_PASSWORD" \
    bash "$ROOT/scripts/run_loadgen.sh" >"$run_dir/loadgen.log" 2>&1 &
  CLIENT_PID=$!
  joined=0
  for _ in $(seq 1 40); do
    if ! kill -0 "$CLIENT_PID" 2>/dev/null; then break; fi
    # The JOINED line is written the moment the bot enters the game world;
    # "PASS joined" is only the session-end summary (logged at disconnect),
    # too late for a snapshot.
    if grep -q "JOINED entity=" "$run_dir/loadgen.log" 2>/dev/null; then joined=1; break; fi
    sleep 1
  done
  if [[ "$joined" == 1 ]]; then
    echo "  client joined; snapshot while connected"
  else
    echo "  WARN: client never joined before snapshot" >&2
  fi

  # Telnet-style snapshot (both servers expose a stock-shaped console; zdtd
  # via --admin-port). Entity/player counts come from listents/listplayers;
  # gettime twice so the capture can derive the game-clock rate.
  # Pressure scenarios snapshot late so the accumulated entities are visible.
  if (( SNAPSHOT_DELAY_MS > 0 )); then
    echo "  snapshot delay ${SNAPSHOT_DELAY_MS}ms (pressure accumulation)"
    sleep $((SNAPSHOT_DELAY_MS / 1000))
  fi

  # Stock cost axis: 7dtd-apm capture over the connected window, aligned with
  # the telnet snapshot below. Runs detached; we wait for it after the client.
  APM_PID=""
  if [[ "$sut" == "stock" && "$COMPARE_APM" != "0" ]] && [[ -d "$APM_PROJECT" ]] \
     && command -v uv >/dev/null; then
    APM_DIR="$run_dir/apm"
    mkdir -p "$APM_DIR"
    SEVENDTD_APM_DIR="$APM_DIR" SEVENDTD_TELNET_PASSWORD="$TELNET_PASSWORD" \
      uv run --project "$APM_PROJECT" 7dtd-apm capture --seconds "$APM_SECONDS" --no-app \
      --telnet-port "$TELNET_PORT" >"$run_dir/apm.log" 2>&1 &
    APM_PID=$!
    echo "  apm capture started (${APM_SECONDS}s, no-app; window aligns with snapshot)"
  fi

  TELNET_ARGS=(--out "$run_dir/telnet.txt" --commands "$TELNET_CMD" --tail-sleep 12)
  if [[ "$sut" == "stock" ]]; then
    TELNET_ARGS+=(--password "$TELNET_PASSWORD")
  fi
  python3 "$ROOT/tools/sut_telnet.py" "$HOST" "$TELNET_PORT" "${TELNET_ARGS[@]}" \
    || echo "  (telnet snapshot failed)"

  # Wait for the client to finish, then summarize the join outcome.
  wait "$CLIENT_PID" || true
  joins=$(grep -c "PASS joined" "$run_dir/loadgen.log" || true)
  echo "  client done: $joins join PASS(es)"

  # The apm capture ends on its own after APM_SECONDS; bounded wait so the
  # server stays up until the session finalizes. Failure is not a scenario
  # result - log it and move on.
  if [[ -n "$APM_PID" ]] && kill -0 "$APM_PID" 2>/dev/null; then
    echo "  waiting for apm capture to finalize"
    if ! wait "$APM_PID"; then
      echo "  WARN: apm capture failed (see $run_dir/apm.log)" >&2
    fi
  fi

  # Capture the stock server log (it is written to a timestamped file under
  # userdata; the start script records the path). zdtd already logs to
  # $run_dir/server.log. Snapshot AFTER the client + telnet session so the log
  # covers the whole run (stock getgamestat dumps its 81 GameStats into it).
  if [[ "$sut" == "stock" ]]; then
    stock_log="$(cat "$USERDATA/dedicated.logpath" 2>/dev/null || true)"
    if [[ -n "$stock_log" && -f "$stock_log" ]]; then
      cp "$stock_log" "$run_dir/server.log"
    else
      echo "  WARN: stock server log not found at $stock_log" >&2
    fi
  fi

  # Run metadata (auditability: what was under test, when, with which knobs).
  # The capture embeds this into surface.json, so a REPORT.md/diff.json always
  # names the exact loadgen + zdtd revisions it compared. hostLoad lets a
  # reader judge whether cost numbers (wall, APM) were taken under contention.
  LOADGEN_GIT="$(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || echo unknown)"
  LOADGEN_DIRTY="$(git -C "$ROOT" status --porcelain 2>/dev/null | wc -l)"
  ZDTD_GIT="$(git -C "${ZDTD_ROOT:-$ROOT/../zdtd}" rev-parse --short HEAD 2>/dev/null || echo unknown)"
  ZDTD_DIRTY="$(git -C "${ZDTD_ROOT:-$ROOT/../zdtd}" status --porcelain 2>/dev/null | wc -l)"
  HOST_LOAD="$(cut -d' ' -f1 /proc/loadavg 2>/dev/null || echo unknown)"
  cat >"$run_dir/run-meta.json" <<EOF
{
  "scenario": "$SCENARIO_ID",
  "sut": "$sut",
  "startedAt": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "hostLoad": "$HOST_LOAD",
  "client": {"count": "$COUNT", "actions": "$ACTIONS", "timeoutMs": "$TIMEOUT_MS", "host": "$HOST",
             "spawnEntity": "$SPAWN_ENTITY", "spawnPerPlayer": "$SPAWN_PER_PLAYER", "spawnEveryMs": "$SPAWN_EVERY_MS"},
  "loadgen": {"git": "$LOADGEN_GIT", "dirtyFiles": "$LOADGEN_DIRTY"},
  "zdtd": {"git": "$ZDTD_GIT", "dirtyFiles": "$ZDTD_DIRTY"}
}
EOF

  # Surface capture (per run, machine-readable). A capture failure is a harness
  # bug, not a scenario result - fail loudly.
  python3 "$ROOT/tools/sut_capture.py" "$run_dir" "$sut" >"$run_dir/surface.json"

  # Teardown.
  if [[ -f "$PIDFILE" ]]; then
    kill -9 "$(cat "$PIDFILE")" 2>/dev/null || true
  fi
  sleep 2
  echo "  torn down"
done

if [[ "$SUTS" == "stock zdtd" ]]; then
  echo "=== diff report ==="
  python3 "$ROOT/tools/sut_report.py" "$OUT_ROOT/$SCENARIO_ID"
  echo "report: $OUT_ROOT/$SCENARIO_ID/REPORT.md"
fi
