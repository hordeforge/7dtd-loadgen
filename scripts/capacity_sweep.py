#!/usr/bin/env python3
"""Capacity sweep: step endgame zombies until the frame breaks the tick budget.

Produces the operator number "P players sustain N endgame zombies at 20 TPS".
Parameterized via env: BM_PLAYERS (players, via bloodmoon_profile), SWEEP_STEP
(+zombies/round, default 40), SWEEP_MAX (default 900), SWEEP_BUDGET_MS (default
bloodmoon_profile.FRAME_BUDGET_MS), CAPTURE_AT_CEILING=1 to run a full APM
capture at the ceiling before teardown (deep bridge sections attribute the
per-entity cost at that exact load).

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
import runlock
from loadgen_config import env_bool, env_float, env_int

STEP = env_int("SWEEP_STEP", 40, minimum=1)
MAX_Z = env_int("SWEEP_MAX", 900, minimum=STEP)
BUDGET = env_float("SWEEP_BUDGET_MS", float(B.FRAME_BUDGET_MS), minimum=0.1)
CAPTURE = env_bool("CAPTURE_AT_CEILING")
# Sibling checkout of 7dtd-server-apm (repo root's parent dir); RE_APM_DIR overrides.
APM_DIR = Path(
    os.environ.get("RE_APM_DIR") or Path(__file__).resolve().parents[1].parent / "7dtd-server-apm"
)


# Consecutive over-budget rounds that end the sweep. A single over-budget
# round is noise; the reported break point is the first round of that run.
SUSTAINED_BREAK_ROUNDS = 2


def sample_row(zombies: int, frame_ms: float, budget: float) -> dict:
    """One curve row. over_budget is decided from the raw frame reading, never
    from the rounded display value: a 54.96 ms frame at a 55 ms budget rounds
    to 55.0, so judging the rounded number called it over budget while the
    same round's log line (and the stop counter) called it ok, and the two
    disagreed about where the ceiling is."""
    return {"zombies": zombies, "frame_ms": round(frame_ms, 1),
            "over_budget": frame_ms > budget}


def capacity_ceiling(curve: list[dict]) -> int:
    """Highest zombie count whose frame reading stayed inside the budget.

    The max, not the last row: alive counts drop when endgame zombies die, so
    the final in-budget row is not necessarily the highest load sustained.
    """
    inside = [p["zombies"] for p in curve if not p["over_budget"]]
    return max(inside) if inside else 0


def sustained_break_at(curve: list[dict]) -> int | None:
    """Zombie count of the first round of the trailing over-budget run, or None
    when the run is shorter than the stop threshold.

    Not the last row: the loop stops on the SUSTAINED_BREAK_ROUNDS-th
    consecutive break, so the final row is the last of the run and naming it
    the first reported a count the ceiling had already passed.
    """
    first_over = len(curve)
    for i, row in enumerate(curve):
        if not row["over_budget"]:
            first_over = i + 1
    if len(curve) - first_over < SUSTAINED_BREAK_ROUNDS:
        return None
    return curve[first_over]["zombies"]


def frame_alive():
    """One (frame_ms, zombies_alive) reading, or None when the APM snapshot is
    unreadable. Mapping lost telemetry to 0 read as a perfect frame: every sweep
    round reported 'ok', the over-budget stop never fired, and the final
    CAPACITY number was fabricated from data that was never received. Players
    are subtracted from the live-entity count: the sweep reports a zombie
    ceiling, not a live-entity count inflated by the joined cohort."""
    try:
        d = B.snapshot()
    except B.SnapshotUnavailable as e:
        B.log(f"  apm snapshot unavailable: {e}")
        return None
    w = d.get("world") or {}
    frame_ms = w.get("unityDeltaMs")
    if frame_ms is None:
        return None
    alives = w.get("entityAlives")
    if alives is None:
        return float(frame_ms), 0
    players = w.get("players") or 0
    return float(frame_ms), max(0, int(alives) - int(players))


def main():
    bots = None
    # Held before B.start_server(), which pkills any running dedicated: an
    # overlapping sweep would end this one's server mid-measurement and then
    # stop its cohort in the finally below, leaving both sweeps reporting a
    # ceiling neither measured. The fd outlives the teardown (process exit
    # releases it), so the lock also covers teardown.
    runlock.acquire_or_exit(B.HOST, B.GAME_PORT, "capacity sweep")
    try:
        B.start_server()
        bots, joined = B.join_ramped(B.PLAYERS)
        B.log(f"players stable: {joined}/{B.PLAYERS}")
        B.set_gamestage(B.GAMESTAGE)

        curve = []
        over = 0
        target = 0
        while target < MAX_Z and over < SUSTAINED_BREAK_ROUNDS:
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
            row = sample_row(a, f, BUDGET)
            curve.append(row)
            B.log(f"  zombies={a} frame={f:.1f}ms {'OVER' if row['over_budget'] else 'ok'}")
            over = over + 1 if row["over_budget"] else 0

        B.log("=== CEILING REACHED ===")
        ceiling = capacity_ceiling(curve)
        broke_at = sustained_break_at(curve)
        break_note = (f"first sustained break at ~{broke_at}" if broke_at is not None
                      else "no sustained over-budget break recorded")
        B.log(f"  CAPACITY: {joined} players sustain ~{ceiling} endgame zombies at 20 TPS "
              f"({break_note})")
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
                                    "--pid", str(pids[0]), "--telnet-port", str(B.TELNET_PORT),
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
