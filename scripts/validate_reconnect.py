#!/usr/bin/env python3
"""Live reconnect validation: server restart mid-cohort.

Starts a stock dedicated server, joins a small loadgen cohort (ramped), kills
the server mid-session, restarts it, and asserts the bots rejoin once the
server is back (loadgen's RunWithRejoin retries until the wall clock expires).

This closes the TODO "Test reconnect behavior after server restart" with a
real restart, not just the unit-tested state machine.

Usage:
  python3 scripts/validate_reconnect.py [--players 8] [--hold-before-kill 40]

Env:
  SKIP_SERVER_START=1 to reuse a running dedicated server (then the script
  only kills/restarts it via the pid from `ss`).
"""
from __future__ import annotations

import argparse
import os
import signal
import socket
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DS_DIR = Path(
    os.environ.get(
        "SEVENDTD_SERVER_DIR",
        str(Path.home() / ".local/share/Steam/steamapps/common/7 Days to Die Dedicated Server"),
    )
)
GAME_PORT = 26900
TELNET_PORT = 8081


def telnet_ready(timeout_s: float = 180.0) -> bool:
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        try:
            with socket.create_connection(("127.0.0.1", TELNET_PORT), timeout=2):
                return True
        except OSError:
            time.sleep(3)
    return False


def wait_gone(timeout_s: float = 60.0) -> bool:
    # Monotonic deadlines: an NTP step mid-wait must not truncate or extend it.
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        try:
            with socket.create_connection(("127.0.0.1", GAME_PORT), timeout=2):
                time.sleep(2)
        except OSError:
            return True
    return False


def server_pid() -> int | None:
    try:
        out = subprocess.run(
            ["ss", "-tlnp"], capture_output=True,
            text=True, encoding="utf-8", errors="replace", check=False
        ).stdout
    except OSError as e:
        # A missing/failing ss must not masquerade later as "no server pid
        # (already down)": say why the lookup failed.
        print(f"[reconnect] server pid lookup failed: {e}", file=sys.stderr)
        return None
    # Telnet (8081) binds during boot before the game port (26900) - check
    # both so a kill during startup still finds the server.
    for port in (TELNET_PORT, GAME_PORT):
        for line in out.splitlines():
            if f":{port} " in line and "pid=" in line:
                pid = line.split("pid=")[1].split(",")[0]
                return int(pid)
    return None


def kill_server() -> bool:
    pid = server_pid()
    if pid is None:
        print("[reconnect] no server pid found (already down)")
        return True
    print(f"[reconnect] killing server pid={pid}")
    os.kill(pid, signal.SIGTERM)
    if not wait_gone():
        print("[reconnect] server did not stop in time")
        return False
    time.sleep(5)  # let the port fully release before restart
    print("[reconnect] server down")
    return True


def start_server(players: int) -> None:
    env = dict(
        os.environ,
        RE_WORLD_NAME="Navezgane",
        RE_GAME_NAME=f"ReconnectStd_{time.strftime('%m%d_%H%M%S')}",
        RE_SERVER_MAX_PLAYERS=str(max(players, 16)),
    )
    subprocess.Popen(
        ["bash", str(ROOT / "scripts/start_dedicated_navezgane.sh")],
        cwd=ROOT, env=env,
        stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT,
    )


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--players", type=int, default=8)
    ap.add_argument("--hold-before-kill", type=float, default=40.0, help="seconds of walking before kill")
    ap.add_argument("--hold-after-restart", type=float, default=30.0, help="seconds to observe rejoins")
    args = ap.parse_args()

    skip_start = os.environ.get("SKIP_SERVER_START", "0") == "1"
    if not skip_start:
        print(f"[reconnect] starting dedicated (Navezgane, {args.players} players)...")
        start_server(args.players)
        if not telnet_ready():
            print("[reconnect] FAIL: server did not come up")
            return 1
        print("[reconnect] server up")

    # Join cohort (ramped to avoid the join-churn race; long timeout so the
    # bots keep retrying through the kill + restart).
    print(f"[reconnect] joining {args.players} bots (ramp 2.5 s)...")
    dotnet = os.environ.get("DOTNET_ROOT", "")
    exe = ROOT / "src/LoadGen/bin/Release/net8.0/7dtd-loadgen.dll"
    log_path = ROOT / "server" / "logs" / f"reconnect_{time.strftime('%Y%m%d_%H%M%S')}.out"
    log_path.parent.mkdir(parents=True, exist_ok=True)
    cmd = [
        "dotnet", str(exe),
        "--join", "--count", str(args.players), "--concurrency", str(args.players),
        "--mode", "wander", "--respawn", "--max-lives", "0",
        "--timeout", str(int(args.hold_before_kill + args.hold_after_restart + 120) * 1000),
        "--pace-ms", "40", "--no-spawn-zombies", "--ramp-ms", "2500",
        "--min-pass-rate", "0.5",  # tolerate the kill gap; rejoins count as passes
        "--log", str(log_path),
        "--host", "127.0.0.1", "--port", str(GAME_PORT + 2),
    ]
    # The cohort must never outlive this script: every exit path (failed kill,
    # failed restart, Ctrl-C) terminates the bots in the finally below, or they
    # keep wandering against a dead server until their wall clock expires.
    with log_path.open("w", encoding="utf-8") as fh:
        proc = subprocess.Popen(cmd, stdout=fh, stderr=subprocess.STDOUT)
        try:
            time.sleep(args.hold_before_kill)

            # Kill and restart mid-cohort.
            print("[reconnect] killing server mid-cohort...")
            if not kill_server():
                print("[reconnect] FAIL: aborting with the cohort still up")
                return 1
            print("[reconnect] restarting server...")
            start_server(args.players)
            if not telnet_ready():
                print("[reconnect] FAIL: server did not come back up")
                return 1
            print("[reconnect] server restarted")

            # Observe rejoins for the hold window.
            time.sleep(args.hold_after_restart)
        finally:
            proc.terminate()
            try:
                proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                proc.kill()
    out = (log_path.read_text(encoding="utf-8", errors="replace")
           if log_path.exists() else "")
    joins = out.count("STAGE Joined")
    rejoin_lines = [l for l in out.splitlines() if "REJOIN" in l]
    joined_lines = [l for l in out.splitlines() if "PASS joined" in l]
    # A rejoin is any retry past the first join attempt per bot. Success = at
    # least one bot logged a REJOIN after the server came back, and at least
    # one PASS joined overall.
    ok = joins >= 1 and len(rejoin_lines) >= 1 and len(joined_lines) >= 1
    print(f"[reconnect] joins={joins} rejoin events={len(rejoin_lines)} joined={len(joined_lines)} "
          f"log={log_path.name}")
    print(f"[reconnect] {'PASS' if ok else 'FAIL'}: bots rejoined after server restart")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
