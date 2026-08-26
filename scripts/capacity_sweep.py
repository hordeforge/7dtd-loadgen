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
import procs

STEP = int(os.environ.get("SWEEP_STEP", "40"))
MAX_Z = int(os.environ.get("SWEEP_MAX", "900"))
BUDGET = float(os.environ.get("SWEEP_BUDGET_MS", "55"))
CAPTURE = os.environ.get("CAPTURE_AT_CEILING", "0") == "1"
# Sibling checkout of 7dtd-server-apm (repo root's parent dir); RE_APM_DIR overrides.
APM_DIR = Path(os.environ.get("RE_APM_DIR") or Path(__file__).resolve().parents[1].parent / "7dtd-server-apm")


def frame_alive():
    """One (frame_ms, entity_alives) reading, or None when the APM snapshot is
    unreadable. Mapping lost telemetry to 0 read as a perfect frame: every sweep
    round reported 'ok', the over-budget stop never fired, and the final
    CAPACITY number was fabricated from data that was never received."""
    d = B.snapshot()
    w = d.get("world") or {}
    frame_ms = w.get("unityDeltaMs")
    if frame_ms is None:
        return None
    alives = w.get("entityAlives")
    return float(frame_ms), int(alives) if alives is not None else 0


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
            s1 = frame_alive()
            time.sleep(5)
            s2 = frame_alive()
            samples = [s for s in (s1, s2) if s is not None]
            if not samples:
                # Unreadable telemetry must stop the sweep: judging frames on a
                # fabricated 0.0ms reading would report every remaining round
                # as 'ok' and invent a capacity ceiling.
                B.log("  apm snapshot unreadable (unityDeltaMs missing); stopping "
                      "the sweep instead of recording an unfounded 'ok' row")
                break
            if len(samples) == 1:
                B.log("  WARN: one of two apm samples unreadable; judging on the survivor")
                f, a = samples[0]
            else:
                f = (samples[0][0] + samples[1][0]) / 2
                a = samples[1][1]
            curve.append({"zombies": a, "frame_ms": round(f, 1)})
            B.log(f"  zombies={a} frame={f:.1f}ms {'OVER' if f > BUDGET else 'ok'}")
            over = over + 1 if f > BUDGET else 0

        B.log("=== CEILING REACHED ===")
        ok = [p for p in curve if p["frame_ms"] <= BUDGET]
        ceiling = ok[-1]["zombies"] if ok else 0
        last_z = curve[-1]["zombies"] if curve else 0
        B.log(f"  CAPACITY: {joined} players sustain ~{ceiling} endgame zombies at 20 TPS "
              f"(first sustained break at ~{last_z})")
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
                pids = procs.find(B.SERVER_PROC)
                if pids:
                    B.log("=== capture at ceiling (90s, deep sections) ===")
                    subprocess.run(["uv", "run", "7dtd-server-apm", "capture", "--seconds", "90",
                                    "--pid", str(pids[0]), "--telnet-port", "8081",
                                    "--reset-bridge"],
                                   cwd=str(APM_DIR), check=False)
                else:
                    B.log("server process not found; skipping ceiling capture")
    finally:
        # Every exit path stops the cohort and the server this sweep owns;
        # a leaked workload keeps loading the host until its wall clock expires.
        B.teardown(bots)
        procs.kill(B.SERVER_PROC)
    B.log("=== CAPACITY SWEEP COMPLETE ===")


if __name__ == "__main__":
    main()
