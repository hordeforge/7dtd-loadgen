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
    pref_count=$(rg -c "<decoration" "$WORLD_DIR/prefabs.xml" 2>/dev/null || echo 0)
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
    # ps-visible for the lifetime of the short-lived python3 process (same
    # rule that keeps LOADGEN_KEY / LOADGEN_TELNET_PASSWORD out of argv).
    WEB_HASH="$(RE_ADMIN_WEB_PASSWORD="$RE_ADMIN_WEB_PASSWORD" python3 -c 'import base64, hashlib, os, sys; sys.stdout.write(base64.b64encode(hashlib.md5(os.environ["RE_ADMIN_WEB_PASSWORD"].encode("utf-8")).digest()).decode())')"
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
# Config rendering lives in render_serverconfig.py; every value passes as
# argv data, never interpolated into the program's source.
python3 "$ROOT/scripts/render_serverconfig.py" \
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

LOG="$USERDATA/server_prefab_${WORLD_NAME}_${WORLD_GEN_SIZE}_$(date +%Y-%m-%d__%H-%M-%S).txt"
echo "$LOG" >"$USERDATA/dedicated.logpath"
echo "Log: $LOG"
echo "Note: first RWG boot generates the 4k world (can take several minutes)."

cd "$DS_DIR"
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

# RWG gen can take a while; allow up to ~10 min
ready=0
for i in $(seq 1 300); do
  if rg -q "StartGame done" "$LOG" 2>/dev/null; then
    echo "Server ready ($i * 2s)"
    rg -n "GameWorld|GameName|WorldGen|EnemySpawnMode|StartGame done|createWorld|Generating|RWG" "$LOG" | head -40
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
    echo "… still waiting (${i}*2s); last log lines:"
    tail -3 "$LOG" 2>/dev/null || true
  fi
  sleep 2
done
if [[ "$ready" != "1" ]]; then
  echo "ERROR: timeout waiting for StartGame" >&2
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
ss -uln | rg '2690[0-2]|8081' || true
echo "OK dedicated up: world=$WORLD_NAME size=$WORLD_GEN_SIZE seed=$WORLD_GEN_SEED"
echo "LiteNet join port typically 26902. Stop: kill \$(cat $USERDATA/dedicated.pid)"
