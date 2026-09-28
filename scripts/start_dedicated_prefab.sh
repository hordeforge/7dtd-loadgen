#!/usr/bin/env bash
# Dedicated on a stock TFP prefab world OR RWG-generated map for bot POI/sleeper tests.
# Vanilla terrain (RealEarth mod disabled). LiteNetLib-only, EAC off, telnet on.
#
# Defaults: RWG 4096 (true 4k, loads faster than 6k/8k pregens, full prefab/sleeper pipeline).
#
#   # 4k RWG (default)
#   ./scripts/start_dedicated_prefab.sh
#
#   # Stock 6k pregen
#   RE_WORLD_NAME=Pregen06k01 ./scripts/start_dedicated_prefab.sh
#
#   # Stock Navezgane
#   RE_WORLD_NAME=Navezgane ./scripts/start_dedicated_prefab.sh
#
#   # Custom RWG size/seed
#   RE_WORLD_NAME=RWG RE_WORLD_GEN_SIZE=4096 RE_WORLD_GEN_SEED=botpoi4k ./scripts/start_dedicated_prefab.sh
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

# Answered before anything else: this script stops the running dedicated,
# quarantines mods and boots a server, so a `--help` that fell through would
# kill the operator's server. The world is configured by the RE_* environment
# variables, so the flag list is the only argument surface.
usage() {
  cat <<EOF
Usage: $0

Start a stock 7DTD dedicated on a prefab world (bots' POI/sleeper pressure).
Everything is configured by the environment:

  RE_WORLD_NAME       stock world to run: RWG (default, generated),
                      Pregen06k01, Navezgane, ...
  RE_WORLD_GEN_SIZE   RWG map size, default 4096
  RE_WORLD_GEN_SEED   RWG seed, default botpoi4k
  RE_GAME_NAME        save name, default BotPoi_<world>_<size>
  RE_SERVER_MAX_PLAYERS  ServerMaxPlayerCount, default 64
  RE_DEDICATED_USERDATA  server userdata, default ~/.cache/7dtd-loadgen
  SEVENDTD_SERVER_DIR     dedicated install, default the Steam library

Flags: -h, --help print this text.
EOF
}
case "${1:-}" in
  -h|--help) usage; exit 0 ;;
  "") ;;
  *) echo "ERROR: $0 takes no arguments, got '$1' (see --help)" >&2; exit 2 ;;
esac

# shellcheck source=scripts/python_env.sh
source "$ROOT/scripts/python_env.sh"
DS_DIR="${SEVENDTD_SERVER_DIR:-$HOME/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server}"
USERDATA="${RE_DEDICATED_USERDATA:-$HOME/.cache/7dtd-loadgen}"
# RWG = generate; otherwise must exist under Data/Worlds/
WORLD_NAME="${RE_WORLD_NAME:-RWG}"
WORLD_GEN_SIZE="${RE_WORLD_GEN_SIZE:-4096}"
WORLD_GEN_SEED="${RE_WORLD_GEN_SEED:-botpoi4k}"
GAME_NAME="${RE_GAME_NAME:-BotPoi_${WORLD_NAME}_${WORLD_GEN_SIZE}}"
# Overrides ServerMaxPlayerCount in the generated config. Defaults to 64 for
# normal play/tests; set RE_SERVER_MAX_PLAYERS=1024 for the 1000-scale ladder
# (serverconfig_loadgen.xml's base 1024 is intentionally capped down here).
MAX_PLAYERS="${RE_SERVER_MAX_PLAYERS:-64}"
# World-wide cap on the game's OWN zombie spawning (scaled x1.9 on blood moons,
# x2.1 for sleepers). Default 64 (~122 effective on a blood moon). The blood-moon
# stress profile raises this so the game does not throttle against a heavy load;
# note manual telnet spawns bypass the cap regardless. High values hit performance
# hard (that is the point of the stress ladder).
MAX_ZOMBIES="${RE_MAX_ZOMBIES:-64}"
ENEMY_DIFFICULTY="${RE_ENEMY_DIFFICULTY:-1}"
# Admin console port (telnet). RE_TELNET_PORT overrides the config default so
# harness runs can dodge a host-occupied 8081 (docker containers, other tools).
TELNET_PORT="${RE_TELNET_PORT:-8081}"
# DynamicMesh off by default (keeps non-mesh measurement baselines unchanged);
# RE_DYNAMIC_MESH=1 enables it for mesh-streaming A/Bs. Stock V3.1.0 ParseBool
# accepts only True/False - normalize any truthy input (1/yes/on/true).
DYNAMIC_MESH="${RE_DYNAMIC_MESH:-false}"
case "${DYNAMIC_MESH,,}" in
  1|yes|on|true) DYNAMIC_MESH="true" ;;
  *)             DYNAMIC_MESH="false" ;;
esac
CONFIG_SRC="$ROOT/scripts/serverconfig_loadgen.xml"
# Safehouse owns the serverconfig renderer for the whole workspace. Point
# SANDBOX_ROOT at the checkout when it does not sit beside this repo.
SANDBOX_ROOT="${SANDBOX_ROOT:-$ROOT/../7dtd-sandbox}"
SBCONFIG="$SANDBOX_ROOT/scripts/sbconfig.py"

# Rerun safety, taken before every other check and every mutating step below. Everything after
# this point overwrites shared state: platform.cfg is rewritten, the RealEarth
# mod is quarantined, and `pkill -x 7DaysToDieServe` stops whatever dedicated is
# running. A second execution of this script therefore does not re-apply its
# own effects, it destroys the first execution's: the server a bench lap or a
# comparison is measuring dies mid-run and both report numbers from a world
# neither measured. That is the same hazard AGENTS.md rule 10 records for the
# cohort and the two Python profiles, and this boot is the step that performs
# it, so it is the step that must refuse.
#
# The lock is the one run_loadgen.sh and scripts/runlock.py already contend
# for (same file name, same tag), keyed on the LiteNet data port the bots join
# (ServerPort + 2, the convention the runners' port defaults encode), so a
# boot here excludes a shell-launched cohort and a Python profile, and they
# exclude it. LOADGEN_ALLOW_OVERLAP=1 is the documented opt-out, and it is how
# a caller that already holds the lock itself (bloodmoon_profile,
# capacity_sweep) reaches this boot without deadlocking against its own lock.
JOIN_PORT="$(sed -n 's/.*name="ServerPort" value="\([0-9]*\)".*/\1/p' "$CONFIG_SRC" | head -1)"
JOIN_PORT="${JOIN_PORT:-26900}"
JOIN_PORT=$((JOIN_PORT + 2))
LOCK_HOST="${RE_LOCK_HOST:-127.0.0.1}"
if [[ "${LOADGEN_ALLOW_OVERLAP:-0}" != "1" ]] && command -v flock >/dev/null 2>&1; then
  lock_tag="$(printf '%s' "${LOCK_HOST}-${JOIN_PORT}" | tr -c 'A-Za-z0-9._-' '_')"
  LOCK_FILE="${XDG_RUNTIME_DIR:-${TMPDIR:-/tmp}}/7dtd-loadgen-${lock_tag}.lock"
  exec 9>"$LOCK_FILE"
  if ! flock -n 9; then
    echo "ERROR: another loadgen run holds $LOCK_FILE (target $LOCK_HOST:$JOIN_PORT)." >&2
    echo "       Booting here would stop that run's dedicated mid-measurement," >&2
    echo "       and both would report numbers from a world neither measured." >&2
    echo "       Wait for it to finish, stop it, or set LOADGEN_ALLOW_OVERLAP=1" >&2
    echo "       if you meant it." >&2
    exit 4
  fi
fi

if [[ ! -f "$SBCONFIG" ]]; then
  echo "ERROR: serverconfig renderer not found: $SBCONFIG (set SANDBOX_ROOT)" >&2
  exit 1
fi

# Publish Mono JIT method address ranges for Linux perf. Without this, managed
# frames appear as [unknown] even though sampling itself succeeds.
case " ${MONO_ENV_OPTIONS:-} " in
  *" --jitmap "*) ;;
  *) export MONO_ENV_OPTIONS="${MONO_ENV_OPTIONS:+$MONO_ENV_OPTIONS }--jitmap" ;;
esac

if [[ ! -x "$DS_DIR/7DaysToDieServer.x86_64" ]]; then
  echo "ERROR: dedicated server not found: $DS_DIR" >&2
  exit 1
fi

pref_count="n/a (RWG generates on first boot)"
if [[ "$WORLD_NAME" != "RWG" ]]; then
  WORLD_DIR="$DS_DIR/Data/Worlds/$WORLD_NAME"
  if [[ ! -d "$WORLD_DIR" ]]; then
    echo "ERROR: stock world not found: $WORLD_DIR" >&2
    echo "Available:" >&2
    ls -1 "$DS_DIR/Data/Worlds" 2>/dev/null || true
    exit 1
  fi
  if [[ -f "$WORLD_DIR/prefabs.xml" ]]; then
    # grep, not rg: dedicated hosts ship coreutils but not ripgrep. grep -c
    # exits nonzero on zero matches (after printing 0), so normalize to 0.
    pref_count=$(grep -c "<decoration" "$WORLD_DIR/prefabs.xml" 2>/dev/null) || pref_count=0
    [[ "$pref_count" =~ ^[0-9]+$ ]] || pref_count=0
  fi
fi

echo "=== Dedicated prefab / RWG world (POI/sleeper) ==="
echo "Server:   $DS_DIR"
echo "UserData: $USERDATA"
echo "World:    $WORLD_NAME  genSize=$WORLD_GEN_SIZE  seed=$WORLD_GEN_SEED"
echo "          prefabs≈$pref_count  GameName=$GAME_NAME  MaxPlayers=$MAX_PLAYERS"

# Local simulated-client auth
PCFG="$DS_DIR/platform.cfg"
if [[ -f "$PCFG" ]]; then
  [[ -f "$PCFG.re-bak" ]] || cp "$PCFG" "$PCFG.re-bak"
  cat >"$PCFG" <<'EOF'
platform=Steam
crossplatform=None
serverplatforms=Steam,LAN,Local,
EOF
fi

# Stop previous dedicated. Match the exact (15-char truncated) comm so we never
# hit this script or unrelated processes; SIGTERM, then SIGKILL any stragglers.
pkill -x 7DaysToDieServe 2>/dev/null || true
sleep 2
pkill -9 -x 7DaysToDieServe 2>/dev/null || true
sleep 1

# Disable RealEarth for pure stock/RWG terrain. A renamed directory below Mods/
# is still discovered by 7DTD, so quarantine it beside (not inside) Mods/.
if [[ -d "$DS_DIR/Mods/RealEarth" ]]; then
  mkdir -p "$DS_DIR/Mods.disabled"
  rm -rf "$DS_DIR/Mods.disabled/RealEarth"
  mv "$DS_DIR/Mods/RealEarth" "$DS_DIR/Mods.disabled/RealEarth"
  echo "RealEarth mod → Mods.disabled/RealEarth"
fi
if [[ -d "$DS_DIR/Mods/RealEarth.off" ]]; then
  mkdir -p "$DS_DIR/Mods.disabled"
  rm -rf "$DS_DIR/Mods.disabled/RealEarth"
  mv "$DS_DIR/Mods/RealEarth.off" "$DS_DIR/Mods.disabled/RealEarth"
  echo "RealEarth.off quarantine → Mods.disabled/RealEarth"
fi

mkdir -p "$USERDATA/Saves" "$USERDATA/Logs" "$USERDATA/GeneratedWorlds"

# Persist the APM web-dashboard admin (level-0 user + "admin"/"admin" webuser)
# across world / userdata / Steam-verify wipes. serveradmin.xml is regenerated
# empty on a fresh save, so re-seed it from the repo template whenever our
# webuser is absent. Idempotent: a save that already has it is left untouched.
# (7DTD hashes webuser passwords as base64(MD5(utf8(pass))); createwebuser is
# in-game-console-only, hence the file seed.)
SERVERADMIN="$USERDATA/Saves/serveradmin.xml"
SERVERADMIN_SEED="$ROOT/scripts/serveradmin_apm_seed.xml"
if [[ -f "$SERVERADMIN_SEED" ]] && ! grep -q 'name="admin"' "$SERVERADMIN" 2>/dev/null; then
  # The committed seed carries synthetic placeholder platform ids. Personal
  # identities stay local: export RE_ADMIN_STEAM_ID64 / RE_ADMIN_EOS_ID to have
  # your own ids substituted into the seeded copy (never written back to the repo).
  cp "$SERVERADMIN_SEED" "$SERVERADMIN"
  # Credential file: the webuser password hash must not be world-readable on a
  # shared host, and the default umask (022) would leave it at 644.
  chmod 600 "$SERVERADMIN"
  if [[ "${RE_ADMIN_STEAM_ID64:-}" =~ ^[0-9]{17}$ ]]; then
    sed -i "s/76561198000000001/${RE_ADMIN_STEAM_ID64}/g" "$SERVERADMIN"
  fi
  if [[ "${RE_ADMIN_EOS_ID:-}" =~ ^[0-9a-fA-F]{32}$ ]]; then
    sed -i "s/00020000000000000000000000000001/${RE_ADMIN_EOS_ID}/g" "$SERVERADMIN"
  fi
  # Web dashboard credential: admin/admin by default (lab-only). Export
  # RE_ADMIN_WEB_PASSWORD to seed a different one; 7DTD stores webuser passes
  # as base64(MD5(utf8(pass))). The plaintext is never echoed.
  WEB_NOTE="admin/admin webuser"
  if [[ -n "${RE_ADMIN_WEB_PASSWORD:-}" ]]; then
    # Hash via env passthrough, never argv: a password argument would be
    # ps-visible (same rule that keeps LOADGEN_KEY / LOADGEN_TELNET_PASSWORD
    # out of argv). The hashing helper is a proper script file.
    WEB_HASH="$(RE_ADMIN_WEB_PASSWORD="$RE_ADMIN_WEB_PASSWORD" \
      run_python "$ROOT/scripts/webdash_password_hash.py")"
    sed -i "s|pass=\"ISMvKXpXpadDiUoOSoAfww==\"|pass=\"${WEB_HASH}\"|" "$SERVERADMIN"
    unset WEB_HASH RE_ADMIN_WEB_PASSWORD
    WEB_NOTE="webuser pass from RE_ADMIN_WEB_PASSWORD"
  fi
  if [[ -z "${RE_ADMIN_STEAM_ID64:-}${RE_ADMIN_EOS_ID:-}" ]]; then
    echo "note: admin seed uses placeholder platform ids; set RE_ADMIN_STEAM_ID64 / RE_ADMIN_EOS_ID to bind your own"
  fi
  echo "seeded APM dashboard admin (${WEB_NOTE}) → $SERVERADMIN"
fi

TMPCFG="$USERDATA/serverconfig_prefab.xml"
# Config rendering lives in the sibling Safehouse renderer (7dtd-sandbox), the
# workspace's one serverconfig writer; every value passes as argv data, never
# interpolated into the program's source.
python3 "$SBCONFIG" render \
  "$CONFIG_SRC" "$TMPCFG" --userdata "$USERDATA" \
  --set "GameWorld=$WORLD_NAME" \
  --set "GameName=$GAME_NAME" \
  --set "WorldGenSeed=$WORLD_GEN_SEED" \
  --set "WorldGenSize=$WORLD_GEN_SIZE" \
  --set "ServerMaxPlayerCount=$MAX_PLAYERS" \
  --set "EACEnabled=false" \
  --set "ServerAllowCrossplay=false" \
  --set "ServerDisabledNetworkProtocols=SteamNetworking" \
  --set "ServerVisibility=0" \
  --set "WebDashboardEnabled=true" \
  --set "IgnoreEOSSanctions=true" \
  --set "EnemySpawnMode=true" \
  --set "ZombieMove=2" \
  --set "ZombieMoveNight=3" \
  --set "MaxSpawnedZombies=$MAX_ZOMBIES" \
  --set "EnemyDifficulty=$ENEMY_DIFFICULTY" \
  --set "TelnetPort=$TELNET_PORT" \
  --set "DayNightLength=40" \
  --set "DayLightLength=12" \
  --set "BuildCreate=false" \
  --set "DynamicMeshEnabled=$DYNAMIC_MESH"
# The rendered config carries TelnetPassword: keep it owner-readable only.
chmod 600 "$TMPCFG"

# UTC stamp: a local stamp repeats across a fall-back transition, so two starts
# an hour apart could resolve to the same log file and the second -logfile write
# truncates the first run's evidence. The UTC form also sorts lexicographically.
LOG="$USERDATA/server_prefab_${WORLD_NAME}_${WORLD_GEN_SIZE}_$(date -u +%Y%m%dT%H%M%SZ).txt"
echo "$LOG" >"$USERDATA/dedicated.logpath"
echo "Log: $LOG"
echo "Note: first RWG boot generates the 4k world (can take several minutes)."

cd "$DS_DIR"
# The lock has done its job: every destructive step above is done, and the
# dedicated is the only thing left that must not be duplicated. Releasing it
# here is what lets the cohort this server exists to load take the same lock
# seconds later. Holding it across the launch would make RE_DEDICATED_FOREGROUND=1
# (the mode that never returns) exclude every join against its own server for
# as long as the server runs.
exec 9>&-
if [[ "${RE_DEDICATED_FOREGROUND:-0}" == "1" ]]; then
  echo "starting in foreground (RE_DEDICATED_FOREGROUND=1)"
  exec ./7DaysToDieServer.x86_64 \
    -logfile "$LOG" \
    -quit -batchmode -nographics -dedicated \
    -configfile="$TMPCFG"
fi
nohup ./7DaysToDieServer.x86_64 \
  -logfile "$LOG" \
  -quit -batchmode -nographics -dedicated \
  -configfile="$TMPCFG" \
  >"$USERDATA/server_stdout_prefab.txt" 2>&1 &
echo $! >"$USERDATA/dedicated.pid"
echo "started pid=$(cat "$USERDATA/dedicated.pid")"

# RWG gen can take a while; allow up to ~10 min of sleeping. The loop's real
# cost is the sleep plus a grep over a log that grows to hundreds of MB, so the
# iteration count is not a duration: on a loaded host 300 rounds take far longer
# than 600s, and a message reading "60 * 2s" sent the operator looking for a
# 2-minute boot that had already run for ten. Every message below reports the
# measured elapsed time instead.
ready=0
ready_start=$SECONDS
for i in $(seq 1 300); do
  # grep, not rg: on hosts without ripgrep an rg-based ready probe never
  # matches (false "timeout waiting for StartGame", then kills a healthy
  # server) and the progress rg -n below aborts under pipefail.
  if grep -q "StartGame done" "$LOG" 2>/dev/null; then
    echo "Server ready after $(( SECONDS - ready_start ))s"
    grep -En "GameWorld|GameName|WorldGen|EnemySpawnMode|StartGame done|createWorld|Generating|RWG" "$LOG" 2>/dev/null | head -40 || true
    ready=1
    break
  fi
  if ! kill -0 "$(cat "$USERDATA/dedicated.pid")" 2>/dev/null; then
    echo "ERROR: server exited early" >&2
    tail -60 "$LOG" || true
    exit 1
  fi
  # progress crumbs during long RWG gen
  if (( i % 15 == 0 )); then
    echo "… still waiting ($(( SECONDS - ready_start ))s); last log lines:"
    tail -3 "$LOG" 2>/dev/null || true
  fi
  sleep 2
done
if [[ "$ready" != "1" ]]; then
  echo "ERROR: timeout waiting for StartGame after $(( SECONDS - ready_start ))s" >&2
  tail -60 "$LOG" || true
  # A half-booted server still holds the game + telnet ports and loads the
  # host; this script owns it, so stop it instead of orphaning it.
  pid="$(cat "$USERDATA/dedicated.pid" 2>/dev/null || true)"
  if [[ -n "$pid" ]]; then
    kill "$pid" 2>/dev/null || true
    sleep 3
    kill -9 "$pid" 2>/dev/null || true
  fi
  exit 1
fi
ss -uln | grep -E '2690[0-2]|8081' || true
echo "OK dedicated up: world=$WORLD_NAME size=$WORLD_GEN_SIZE seed=$WORLD_GEN_SEED"
echo "LiteNet join port typically 26902. Stop: kill \$(cat $USERDATA/dedicated.pid)"
