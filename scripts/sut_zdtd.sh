#!/usr/bin/env bash
# SUT (server-under-test) boot for zdtd: build, boot with a fresh world dir,
# hold in the foreground. The compare orchestrator backgrounds this, waits for
# the ready line, runs the client, then kills via dedicated.pid.
#
#   env: RE_SUT_PORT (LiteNet data port; bots join PORT+2)
#        RE_SUT_ADMIN_PORT (stock-shaped telnet console; 0 = off, default 8082)
#        RE_SUT_WORLD      (fresh world dir, created here)
#        RE_SUT_WORLD_NAME (Navezgane | RWG | ... default Navezgane)
#        RE_SUT_GAME_DIR   (stock install for Data/Config + Data/Worlds)
#        RE_SUT_SERVERCONFIG (serverconfig.xml with the game options to apply;
#        the compare harness passes the same values stock runs with)
#        RE_SUT_LOGFILE    (server.log path; default $RE_SUT_WORLD/server.log)
#
# Mirrors zdtd/scripts/smoke-navezgane.sh's boot contract so both sides of the
# comparison use the same observable surface (log lines + save files + a
# stock-shaped telnet console for gettime/listents/listplayers snapshots).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ZIG="${ZIG:-zig}"
ZDTD_ROOT="${ZDTD_ROOT:-$ROOT/../zdtd-server-server}"
PORT="${RE_SUT_PORT:-27120}"
ADMIN_PORT="${RE_SUT_ADMIN_PORT:-8082}"
WORLD="${RE_SUT_WORLD:-$ROOT/../zdtd-server-server/worlds/sut_zdtd}"
WORLD_NAME="${RE_SUT_WORLD_NAME:-Navezgane}"
GAME_DIR="${RE_SUT_GAME_DIR:-$HOME/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server}"
SERVERCONFIG="${RE_SUT_SERVERCONFIG:-}"
LOGFILE="${RE_SUT_LOGFILE:-$WORLD/server.log}"

if ! command -v "$ZIG" >/dev/null 2>&1; then
  echo "sut_zdtd: missing Zig compiler '$ZIG'" >&2
  exit 127
fi
if [[ ! -d "$ZDTD_ROOT" ]]; then
  echo "sut_zdtd: zdtd repo not found at $ZDTD_ROOT" >&2
  exit 2
fi
if [[ ! -d "$GAME_DIR/Data/Config" ]]; then
  echo "sut_zdtd: game install not found (set RE_SUT_GAME_DIR): $GAME_DIR" >&2
  exit 2
fi
if [[ ! "$PORT" =~ ^[0-9]+$ ]] || ((10#$PORT > 65533)); then
  echo "sut_zdtd: PORT must be an integer 0..65533 (got '$PORT')" >&2
  exit 2
fi
if [[ ! "$ADMIN_PORT" =~ ^[0-9]+$ ]] || ((10#$ADMIN_PORT > 65535)); then
  echo "sut_zdtd: ADMIN_PORT must be an integer 0..65535 (got '$ADMIN_PORT')" >&2
  exit 2
fi

rm -rf "$WORLD"
mkdir -p "$WORLD"
cd "$ZDTD_ROOT"
"$ZIG" build
if [[ -n "$SERVERCONFIG" ]]; then
  if [[ ! -f "$SERVERCONFIG" ]]; then
    echo "sut_zdtd: serverconfig not found: $SERVERCONFIG" >&2
    exit 2
  fi
  ./zig-out/bin/zdtd --port "$PORT" --game-dir "$GAME_DIR" --world-name "$WORLD_NAME" \
    --world "$WORLD" --admin-port "$ADMIN_PORT" --serverconfig "$SERVERCONFIG" \
    >"$LOGFILE" 2>&1 &
else
  ./zig-out/bin/zdtd --port "$PORT" --game-dir "$GAME_DIR" --world-name "$WORLD_NAME" \
    --world "$WORLD" --admin-port "$ADMIN_PORT" >"$LOGFILE" 2>&1 &
fi
echo $! >"$WORLD/dedicated.pid"
wait
