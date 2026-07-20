#!/usr/bin/env python3
"""Standard blood-moon stress profile: 64 live players + ~1000 ENDGAME zombies.

The canonical worst-case load: if the sim holds ~20 TPS here, every lighter load is
fine. Composition is a fixed, deterministic endgame mix (radiated commons, ferals,
exploding cops + demolishers, a screamer) so runs are reproducible - unlike an
RNG game horde. Zombies are telnet-spawned (bypasses the MaxSpawnedZombies world cap,
which otherwise limits the game's own spawns to ~MaxSpawnedZombies x1.9 on a blood
moon). Players join on a gentle ramp (~1/s) to avoid the connect/disconnect storm that
64 simultaneous joins triggers.

Env: BM_PLAYERS (64), BM_ZOMBIES (1000), BM_GAMESTAGE (250), BM_HOLD_S (0 = hold until
Ctrl-C; >0 = teardown after N s). Requires a dedicated server reachable on telnet; use
--start-server to bring one up with the blood-moon server caps.
"""
import json
import os
import re
import socket
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
HOST = os.environ.get("LOADGEN_HOST", "127.0.0.1")
GAME_PORT = os.environ.get("LOADGEN_PORT", "26902")
TELNET_PORT = int(os.environ.get("LOADGEN_TELNET_PORT", "8081"))
TELNET_PW = os.environ.get("SEVENDTD_TELNET_PASSWORD", "retest")
DOTNET = os.environ.get("DOTNET_ROOT", str(Path.home() / ".cache/dotnet-sdk"))
APM_SNAP = Path(os.environ.get("APM_SNAPSHOT", str(
    Path.home() / ".local/share/Steam/steamapps/common/7 Days to Die Dedicated Server"
    / "Mods/7dtd-apm-bridge/telemetry/apm_app_latest.json")))

PLAYERS = int(os.environ.get("BM_PLAYERS", "64"))
ZOMBIES = int(os.environ.get("BM_ZOMBIES", "1000"))
GAMESTAGE = int(os.environ.get("BM_GAMESTAGE", "250"))
HOLD_S = int(os.environ.get("BM_HOLD_S", "0"))

# Deterministic endgame composition (weights per 20-zombie cycle). ~15% exploders
# (FatCop + Demolition), the rest radiated/feral tanks + a screamer. All names
# verified present in this build's entityclasses.xml.
ENDGAME_MIX = (
    ["zombieBoeRadiated"] * 3 + ["zombieMarleneRadiated"] * 2 + ["zombieJoeRadiated"] * 2
    + ["zombieArleneRadiated"] * 2 + ["zombieSteveRadiated"] * 2      # radiated bulk (11)
    + ["zombieBikerFeral"] * 2 + ["zombieWightFeral"] * 1             # feral tanks (3)
    + ["zombieSoldierRadiated"] * 1                                   # heavy (1)
    + ["zombieFatCop"] * 2 + ["zombieDemolition"] * 1                 # exploders (3)
    + ["zombieScreamer"] * 1                                          # summoner (1)
)  # 20 per cycle


def log(msg):
    print(f"[{time.strftime('%H:%M:%S')}] {msg}", flush=True)


def telnet(cmds, settle=1.0):
    out = []
    try:
        with socket.create_connection((HOST, TELNET_PORT), timeout=10) as s:
            s.settimeout(3)

            def drain(sec):
                end = time.time() + sec
                while time.time() < end:
                    try:
                        b = s.recv(65536)
                        if b:
                            out.append(b.decode("utf-8", "replace"))
                    except socket.timeout:
                        break
            drain(0.5)
            s.sendall((TELNET_PW + "\n").encode())
            drain(0.5)
            for c in cmds:
                s.sendall((c + "\n").encode())
                time.sleep(0.02)
            drain(settle)
    except OSError as e:
        log(f"telnet error: {e}")
    return "".join(out)


def player_ids():
    return [int(m) for m in re.findall(r"\d+\.\s*id=(\d+),", telnet(["listplayers"]))]


def alive():
    telnet(["apm dump"])
    time.sleep(1.5)
    try:
        return int((json.loads(APM_SNAP.read_text()).get("world") or {}).get("entityAlives") or 0)
    except (OSError, json.JSONDecodeError, ValueError):
        return -1


def start_server():
    env = dict(os.environ, DOTNET_ROOT=DOTNET, RE_WORLD_NAME="Navezgane",
               RE_GAME_NAME="BloodMoonStd", RE_SERVER_MAX_PLAYERS=str(max(PLAYERS, 64)),
               RE_MAX_ZOMBIES=str(max(ZOMBIES, 64)), RE_ENEMY_DIFFICULTY="5")
    log(f"starting server (MaxSpawnedZombies={max(ZOMBIES,64)}, maxplayers={max(PLAYERS,64)})...")
    subprocess.run(["bash", str(ROOT / "scripts/start_dedicated_prefab.sh")], cwd=ROOT,
                   env=env, timeout=400, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(5)


def join_ramped(target):
    # Single loadgen process, gentle ramp (~1 join/s) so joins do not storm the connect
    # pump. Unique bot names come from one process (multi-process would name-collide).
    ramp_ms = str(target * 1000)
    env = dict(os.environ, DOTNET_ROOT=DOTNET, LOADGEN_MODE="join", LOADGEN_HOST=HOST,
               LOADGEN_PORT=GAME_PORT, LOADGEN_COUNT=str(target), LOADGEN_CONCURRENCY=str(target),
               LOADGEN_RAMP_MS=ramp_ms, LOADGEN_TIMEOUT="3600000", LOADGEN_BOT_MODE="wander",
               LOADGEN_ACTIONS="100000000", LOADGEN_NO_SPAWN="1", LOADGEN_SEED="7777",
               LOADGEN_QUIET="1")
    fh = (ROOT / "bloodmoon_bots.log").open("wb")
    p = subprocess.Popen(["bash", str(ROOT / "scripts/run_loadgen.sh")], cwd=ROOT, env=env,
                         stdout=fh, stderr=fh)
    # wait for a STABLE cohort (tolerate ramp churn): count must hold >= 90% target twice.
    deadline = time.time() + target * 1000 / 1000 + 180
    hits = 0
    while time.time() < deadline:
        time.sleep(10)
        n = len(player_ids())
        log(f"  join ramp: {n}/{target} players")
        if n >= target * 0.9:
            hits += 1
            if hits >= 2:
                break
        else:
            hits = 0
    return p, len(player_ids())


def set_gamestage(stage):
    ids = player_ids()
    # Try the known command forms; log which is accepted (get-only builds will error).
    for tmpl in (f"setgamestage {{i}} {stage}", f"gamestage {{i}} {stage}"):
        r = telnet([tmpl.format(i=pid) for pid in ids[:2]], settle=1.5)
        if "unknown command" not in r.lower() and "error" not in r.lower():
            telnet([tmpl.format(i=pid) for pid in ids], settle=2)
            log(f"  gamestage set via '{tmpl.split()[0]}' -> {stage}")
            return tmpl.split()[0]
    log("  gamestage set command not accepted (build may derive it from level); skipping")
    return None


def spawn_endgame(target):
    cur = alive()
    stalls = 0
    telnet_fails = 0
    while cur < target and stalls < 4:
        ids = player_ids()
        if not ids:  # listplayers can time out under load; retry before believing it
            time.sleep(2)
            ids = player_ids()
        if not ids:
            if snap_players() > 0:  # players are really there, telnet just hitched
                telnet_fails += 1
                if telnet_fails >= 6:
                    # Server too bogged to service telnet = it has already saturated.
                    # Stop here and report the reached ceiling instead of hanging.
                    log(f"  telnet unresponsive under load x{telnet_fails} - server saturated "
                        f"at ~{cur} zombies; stopping spawn")
                    break
                log(f"  listplayers empty but players present (x{telnet_fails}) - retrying")
                time.sleep(3)
                continue
            log("  no players - aborting spawn")
            break
        telnet_fails = 0
        # ~1 cycle of the mix distributed across players per round.
        cmds = []
        mi = 0
        per = max(1, (target - cur) // max(1, len(ids)) // 4 + 1)
        for pid in ids:
            for _ in range(per):
                cmds.append(f"spawnentity {pid} {ENDGAME_MIX[mi % len(ENDGAME_MIX)]}")
                mi += 1
        telnet(cmds, settle=4)
        new = alive()
        log(f"  spawn: alive={new}/{target}")
        stalls = stalls + 1 if new <= cur + 2 else 0
        cur = new
    return cur


def snapshot():
    telnet(["apm dump"])
    time.sleep(1.5)
    try:
        return json.loads(APM_SNAP.read_text())
    except (OSError, json.JSONDecodeError):
        return {}


def snap_players():
    return (snapshot().get("world") or {}).get("players") or 0


def health():
    d = snapshot()
    w = d.get("world") or {}
    u = d.get("update") or {}
    # unityDeltaMs is the real frame period (the "are we at 20 TPS" signal: <=55ms ok).
    return {"entityAlives": w.get("entityAlives"), "players": w.get("players"),
            "frameMs": w.get("unityDeltaMs"), "tickAvgMs": u.get("serverTickIntervalAvgMs"),
            "tickMaxMs": u.get("serverTickIntervalMaxMs"), "gmMaxMs": u.get("gmUpdateDurationMaxMs"),
            "lateTicks": u.get("lateTicks"), "stallMs": u.get("tickStallMsTotal")}


def main():
    if "--start-server" in sys.argv:
        start_server()
    log(f"=== BLOOD MOON STANDARD: {PLAYERS} players + {ZOMBIES} endgame zombies (GS{GAMESTAGE}) ===")
    bots, joined = join_ramped(PLAYERS)
    log(f"players stable: {joined}/{PLAYERS}")
    set_gamestage(GAMESTAGE)
    log(f"spawning endgame mix to {ZOMBIES}...")
    za = spawn_endgame(ZOMBIES)
    time.sleep(8)  # let the spawn churn settle before reading steady-state health
    h = health()
    log("=== LOAD ESTABLISHED ===")
    log(f"  players={h.get('players')}  zombies~{za}/{ZOMBIES}  entityAlives={h.get('entityAlives')}")
    log(f"  frame={h.get('frameMs')}ms (50ms=20TPS budget)  tickMax={h.get('tickMaxMs')}ms  "
        f"gmMax={h.get('gmMaxMs')}ms  lateTicks={h.get('lateTicks')}  stall={h.get('stallMs')}ms")
    frame = h.get("frameMs")
    keeps = isinstance(frame, (int, float)) and frame <= 55
    log(f"  VERDICT: {'HOLDS ~20 TPS' if keeps else f'OVER BUDGET at {frame}ms/frame (cannot hold 20 TPS)'}")
    if HOLD_S <= 0:
        log("holding load (BM_HOLD_S=0). Attach APM/capture now. Ctrl-C to tear down.")
        try:
            while True:
                time.sleep(30)
                h = health()
                log(f"  hold: alive={h.get('entityAlives')} frame={h.get('frameMs')}ms lateTicks={h.get('lateTicks')}")
        except KeyboardInterrupt:
            pass
    else:
        log(f"holding {HOLD_S}s...")
        time.sleep(HOLD_S)
    log("tearing down")
    telnet(["kickall", "kick all"])
    bots.terminate()
    try:
        bots.wait(timeout=15)
    except subprocess.TimeoutExpired:
        bots.kill()
    subprocess.run(["pkill", "-9", "-f", "net8.0/7dtd-loadge[n]"], check=False)
    log("=== BLOOD MOON STANDARD COMPLETE ===")


if __name__ == "__main__":
    main()
