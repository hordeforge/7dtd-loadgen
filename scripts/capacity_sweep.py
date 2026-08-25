#!/usr/bin/env python3
"""Capacity sweep: step endgame zombies until the frame breaks the tick budget.

Produces the operator number "P players sustain N endgame zombies at 20 TPS".
Parameterized via env: BM_PLAYERS (players, via bloodmoon_profile), SWEEP_STEP
(+zombies/round, default 40), SWEEP_MAX (default 900), SWEEP_BUDGET_MS (55),
CAPTURE_AT_CEILING=1 to run a full APM capture at the ceiling before teardown
(deep bridge sections attribute the per-entity cost at that exact load).

Uses the blood-moon standard's server bring-up, join ramp, gamestage, and
endgame spawn mix (scripts/bloodmoon_profile.py).
"""
import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import bloodmoon_profile as B

STEP = int(os.environ.get("SWEEP_STEP", "40"))
MAX_Z = int(os.environ.get("SWEEP_MAX", "900"))
BUDGET = float(os.environ.get("SWEEP_BUDGET_MS", "55"))
CAPTURE = os.environ.get("CAPTURE_AT_CEILING", "0") == "1"
# Sibling checkout of 7dtd-server-apm (repo root's parent dir); RE_APM_DIR overrides.
APM_DIR = Path(os.environ.get("RE_APM_DIR") or Path(__file__).resolve().parents[1].parent / "7dtd-server-apm")


def frame_alive():
    d = B.snapshot()
    w = d.get("world") or {}
    return (w.get("unityDeltaMs") or 0), (w.get("entityAlives") or 0)


def main():
    bots = None
    try:
        B.start_server()
        bots, joined = B.join_ramped(B.PLAYERS)
        B.log(f"players stable: {joined}/{B.PLAYERS}")
        B.set_gamestage(B.GAMESTAGE)

        curve = []
        over = 0
        target = 0
        while target < MAX_Z and over < 2:
            target += STEP
            B.spawn_endgame(target)
            time.sleep(15)
            f1, a1 = frame_alive()
            time.sleep(5)
            f2, a2 = frame_alive()
            f = (f1 + f2) / 2 if f1 and f2 else (f1 or f2)
            curve.append({"zombies": a2 or a1, "frame_ms": round(f, 1)})
            B.log(f"  zombies={a2 or a1} frame={f:.1f}ms {'OVER' if f > BUDGET else 'ok'}")
            over = over + 1 if f > BUDGET else 0

        B.log("=== CEILING REACHED ===")
        ok = [p for p in curve if p["frame_ms"] <= BUDGET]
        ceiling = ok[-1]["zombies"] if ok else 0
        B.log(f"  CAPACITY: {joined} players sustain ~{ceiling} endgame zombies at 20 TPS "
              f"(first sustained break at ~{curve[-1]['zombies']})")
        B.log(f"  curve: {json.dumps(curve)}")

        if CAPTURE:
            # Feature-test the capture toolchain (same guard bench_stock.sh /
            # compare_sut.sh apply): a host without uv or without the sibling
            # checkout must skip the optional capture, not crash the sweep
            # after the ceiling was already measured.
            if not (shutil.which("uv") and APM_DIR.is_dir()):
                B.log("apm capture skipped: need uv on PATH and the sibling "
                      "7dtd-server-apm checkout (RE_APM_DIR)")
            else:
                pids = subprocess.run(["pgrep", "-f", "7DaysToDieServer.x86_6[4]"],
                                      capture_output=True,
                                      text=True, encoding="utf-8", errors="replace",
                                      check=False).stdout.split()
                if pids:
                    B.log("=== capture at ceiling (90s, deep sections) ===")
                    subprocess.run(["uv", "run", "7dtd-server-apm", "capture", "--seconds", "90",
                                    "--pid", pids[0], "--telnet-port", "8081", "--reset-bridge"],
                                   cwd=str(APM_DIR), check=False)
                else:
                    B.log("server process not found; skipping ceiling capture")
    finally:
        # Every exit path stops the cohort and the server this sweep owns;
        # a leaked workload keeps loading the host until its wall clock expires.
        B.teardown(bots)
        subprocess.run(["pkill", "-9", "-f", "7DaysToDieServer.x86_6[4]"], check=False)
    B.log("=== CAPACITY SWEEP COMPLETE ===")


if __name__ == "__main__":
    main()
