#!/usr/bin/env python3
"""Consolidate bench-stock lap evidence into a machine-readable report.

Walks workspace/bench/lap<N>/<scenario>/ run-meta.json + stats.json and emits
bench-stock.md + bench-stock.json at the laps root. The bench numbers come from
the stats-json `bench` block (the same counts the client also prints as its
BENCH_SUMMARY console line). A repeatability section compares per-scenario wall
across laps (+-20% threshold) so the 2-lap claim is computed, not asserted.

Usage:
  bench_report.py --laps-dir <dir> [--require-laps N] [--out <dir>]
"""

from __future__ import annotations

import argparse
import datetime as dt
import io
import json
import sys
from pathlib import Path

from json_shape import as_cell, as_count, as_dict, as_int, as_list, as_number

# Locale-independent text boundary: the rendered text comes from UTF-8 JSON
# evidence, but a C-locale runner gives stdout an ASCII codec and print()
# raises. See scenario_env.py for the same block and its reason.
if isinstance(sys.stdout, io.TextIOWrapper):
    sys.stdout.reconfigure(encoding="utf-8")
if isinstance(sys.stderr, io.TextIOWrapper):
    sys.stderr.reconfigure(encoding="utf-8")

TOLERANCE = 0.20  # per-scenario wall repeatability bound


def iso_delta(a: str, b: str) -> float | None:
    """Seconds between two UTC stamps, or None when they do not make one.

    A negative span is a clock step or a mis-stamped run, not a zero-second
    run: clamping it to 0.0 published 0 as a measured wall and then blamed
    the 100% repeatability delta on host contention.

    One stamp carrying a UTC offset and the other not is the same class of
    input as a malformed one, and the subtraction is what raises for it, so
    the arithmetic shares the guard.
    """
    try:
        ta = dt.datetime.fromisoformat(a)
        tb = dt.datetime.fromisoformat(b)
        span = (tb - ta).total_seconds()
    except (ValueError, TypeError, OverflowError):
        return None
    return span if span >= 0 else None


def apm_summary(run_dir: Path) -> dict:
    """Best-effort APM capture summary: lag verdict from apm.log, plus IPC and
    per-layer scores from the session summary.json when present."""
    verdict = "n/a"
    ipc: float | None = None
    layers: dict[str, float] = {}
    log = run_dir / "apm.log"
    if log.is_file():
        try:
            for line in log.read_text(encoding="utf-8", errors="replace").splitlines():
                if "lag diagnosis" in line or "lagVerdict" in line:
                    # A key with nothing after the colon is an unfilled
                    # template, not a verdict: 'n/a' says the capture did not
                    # report one, a blank cell only looks empty.
                    verdict = as_cell(line.strip().split(":", 1)[-1])
                    break
        except OSError as e:
            # The verdict cell is the only cost evidence this summary carries;
            # swallowing the read fault publishes 'n/a' as if the capture had
            # simply reported no verdict.
            print(f"WARN: unreadable {log}: {e}; APM verdict omitted",
                  file=sys.stderr)
    sessions = sorted((run_dir / "apm").glob("session_*/summary.json"))
    if sessions:
        try:
            s = json.loads(sessions[-1].read_text(encoding="utf-8"))
        except (ValueError, OSError) as e:
            print(f"WARN: unreadable {sessions[-1]}: {e}; APM layers omitted",
                  file=sys.stderr)
            s = None
        for layer in as_list(as_dict(s).get("layers")):
            if not isinstance(layer, dict):
                continue
            name = layer.get("layer")
            score = as_number(layer.get("score"))
            if isinstance(name, str) and name and score is not None:
                layers[name] = score
            ipc_value = as_number(as_dict(layer.get("signals")).get("ipc"))
            if name == "cpu" and ipc_value is not None:
                ipc = round(ipc_value, 3)
    return {"verdict": verdict, "ipc": ipc, "layers": layers}


def apm_cell(run_dir: Path) -> str:
    """One report cell: 'verdict; ipc=..; scheduler=..' (layers with data)."""
    a = apm_summary(run_dir)
    parts = [a["verdict"]]
    if a["ipc"] is not None:
        parts.append(f"ipc={a['ipc']}")
    top = sorted(a["layers"].items(), key=lambda kv: -kv[1])[:3]
    for name, score in top:
        parts.append(f"{name}={score:.0f}")
    return "; ".join(parts)


def load_lap(lap_dir: Path) -> dict:
    scenarios = {}
    for meta_path in sorted(lap_dir.glob("*/run-meta.json")):
        sc = meta_path.parent.name
        # One corrupt/truncated run-meta.json (a lap killed mid-write) must skip
        # that scenario, not abort consolidation of every lap - same policy as
        # the stats.json load below.
        try:
            meta = json.loads(meta_path.read_text(encoding="utf-8"))
        except (ValueError, OSError):
            print(f"WARN: skipping unreadable {meta_path}", file=sys.stderr)
            continue
        if not isinstance(meta, dict):
            print(f"WARN: skipping non-object {meta_path}", file=sys.stderr)
            continue
        stats = {}
        stats_path = meta_path.parent / "stats.json"
        stats_unreadable = False
        if stats_path.is_file():
            try:
                stats = json.loads(stats_path.read_text(encoding="utf-8"))
            except (ValueError, OSError) as e:
                # stats.json is the authoritative join outcome. Falling back to
                # run-meta silently would let a lap killed mid-write report the
                # older, partial counts as this lap's measurement.
                print(f"WARN: unreadable {stats_path} ({e}); join counts fall back "
                      f"to run-meta summary", file=sys.stderr)
                stats_unreadable = True
            else:
                if not isinstance(stats, dict):
                    print(f"WARN: non-object {stats_path}; join counts fall back "
                          f"to run-meta summary", file=sys.stderr)
                    stats = {}
                    stats_unreadable = True
        # Normalize every value the renderer formats. A cell reaches the
        # markdown table through int() and `:.1f`, both of which raise on a
        # string or a container, and a well-formed JSON capture of the wrong
        # shape is exactly what a foreign writer produces. An absent bench
        # block stays empty: the renderer reads that as n/a, and filling it in
        # would publish a zero-second window as a measurement.
        raw_bench = as_dict(stats.get("bench") if isinstance(stats, dict) else None)
        bench = {
            "windowStartMs": as_int(raw_bench.get("windowStartMs")) or 0,
            "windowEndMs": as_int(raw_bench.get("windowEndMs")) or 0,
            "actionsPerSec": as_number(raw_bench.get("actionsPerSec")) or 0.0,
            "activeMin": as_cell(raw_bench.get("activeMin")),
            "activeMax": as_cell(raw_bench.get("activeMax")),
        } if raw_bench else {}
        summary = as_dict(meta.get("summary"))
        # stats.json is the authoritative join outcome (client.log can contain
        # binary bytes that defeat grep); fall back to run-meta summary.
        joins_pass = (stats.get("pass")
                      if isinstance(stats, dict) and stats.get("pass") is not None
                      else summary.get("pass"))
        joins_fail = (stats.get("fail")
                      if isinstance(stats, dict) and stats.get("fail") is not None
                      else summary.get("fail"))
        wall = iso_delta(meta.get("startUtc", ""), meta.get("endUtc", ""))
        scenarios[sc] = {
            "wallS": round(wall, 1) if wall is not None else None,
            "joinsPass": as_count(joins_pass),
            "joinsFail": as_count(joins_fail),
            "statsUnreadable": stats_unreadable,
            "hostLoad": (f"{as_cell(meta.get('hostLoadStart'))}"
                         f"->{as_cell(meta.get('hostLoadEnd'))}"),
            "bench": bench,
            "apm": apm_cell(meta_path.parent),
        }
    return {"scenarios": scenarios}


def render_md(laps: list[tuple[str, dict]]) -> str:
    lines = ["# bench-stock (stock dedicated benchmark)\n"]
    lines.append(f"- laps: {len(laps)} ({', '.join(n for n, _ in laps)})")
    first = laps[0][1]["scenarios"]
    lines.append(f"- scenarios: {', '.join(sorted(first))}")
    lines.append("\n## Per-lap scenario rows\n")
    lines.append("| lap | scenario | joins pass/fail | wall (s) | hostLoad | "
                 "bench window | actions/s | active min/max | APM |")
    lines.append("|---|---|---|---|---|---|---|---|---|")
    for name, lap in laps:
        for sc in sorted(lap["scenarios"]):
            s = lap["scenarios"][sc]
            b = s["bench"]
            win = "n/a"
            if b:
                win = f"{int(b.get('windowStartMs', 0))}-{int(b.get('windowEndMs', 0))}"
            aps = f"{b.get('actionsPerSec', 0):.1f}" if b else "n/a"
            active = f"{b.get('activeMin', '?')}/{b.get('activeMax', '?')}" if b else "n/a"
            wall = f"{s['wallS']}" if s["wallS"] is not None else "n/a"
            lines.append(f"| {name} | {sc} | {s['joinsPass']}/{s['joinsFail']} | "
                         f"{wall} | {s['hostLoad']} | {win} | {aps} | {active} | "
                         f"{s['apm']} |")
    degraded = [f"{name}/{sc}" for name, lap in laps
                for sc, s in lap["scenarios"].items() if s.get("statsUnreadable")]
    if degraded:
        lines.append(f"\n- stats.json unreadable for: {', '.join(degraded)} "
                     f"(join counts came from run-meta, not the authoritative "
                     f"stats artifact)")
    # Repeatability across laps.
    if len(laps) >= 2:
        lines.append("\n## Repeatability (per-scenario wall, +-20% bound)\n")
        lines.append("| scenario | " + " | ".join(n for n, _ in laps)
                     + " | delta% | verdict |")
        # scenario + one column per lap + delta% + verdict. A fixed separator
        # would only be right at two laps and would break the table at three.
        lines.append("|" + "---|" * (len(laps) + 3))
        for sc in sorted(first):
            walls = []
            for _, lap in laps:
                s = lap["scenarios"].get(sc, {})
                walls.append(s.get("wallS"))
            if any(w is None for w in walls) or not walls:
                verdict, delta_cell = "n/a (missing wall)", "n/a"
            elif walls[0] == 0:
                verdict, delta_cell = "n/a (zero base wall)", "n/a"
            else:
                base = walls[0]
                worst = max(abs((w - base) / base) for w in walls)
                delta_cell = f"{worst*100:.1f}%"
                verdict = f"OK ({delta_cell})" if worst <= TOLERANCE else \
                    f"OVER ({delta_cell}) - hostLoad check"
            lines.append(f"| {sc} | " + " | ".join(
                f"{w:.1f}" if w is not None else "n/a" for w in walls)
                + f" | {delta_cell} | {verdict} |")
        # Load-sensitive axis (bench profile only): actions/s lap-to-lap. Every
        # lap must have the reading; a partial series would compare laps that
        # did not measure the same thing.
        raw_aps = [(lap["scenarios"].get("bench", {}).get("bench") or {}).get("actionsPerSec")
                   for _, lap in laps]
        bench_aps = [float(a) for a in raw_aps if a is not None]
        if len(bench_aps) == len(raw_aps) >= 2 and bench_aps[0]:
            # Worst deviation from lap 1 across every lap, same as the wall rows
            # above: comparing only the first two silently drops lap 3+.
            aps_base = bench_aps[0]
            aps_delta = max(abs((a - aps_base) / aps_base) for a in bench_aps)
            aps_ok = "OK" if aps_delta <= TOLERANCE else "OVER"
            lines.append(f"\n- bench actions/s: {' -> '.join(f'{a:.2f}' for a in bench_aps)} "
                         f"(delta {aps_delta*100:.1f}% {aps_ok}, +-{TOLERANCE*100:.0f}% bound)")
        lines.append(f"\n- tolerance: +-{TOLERANCE*100:.0f}% per scenario; "
                     "over-tolerance rows are a finding (host contention), "
                     "recorded with hostLoad, never hidden.")
    return "\n".join(lines) + "\n"


def main() -> int:
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--laps-dir", type=Path, default=Path("workspace/bench"),
                    help="root holding lap<N> dirs (default: %(default)s)")
    ap.add_argument("--out", type=Path, default=None,
                    help="output dir for bench-stock.md + .json "
                         "(default: --laps-dir)")
    ap.add_argument("--require-laps", type=int, default=0,
                    help="fail unless at least N laps have evidence (default: "
                         "%(default)s, no minimum)")
    args = ap.parse_args()

    if not args.laps_dir.is_dir():
        print(f"ERROR: no lap evidence under {args.laps_dir} (not a directory)",
              file=sys.stderr)
        return 2
    lap_dirs = sorted(
        d for d in args.laps_dir.iterdir()
        if d.is_dir()
        and any(p.name == "run-meta.json" for p in d.glob("*/run-meta.json"))
    )
    if not lap_dirs:
        print(f"ERROR: no lap evidence under {args.laps_dir}", file=sys.stderr)
        return 2
    if args.require_laps and len(lap_dirs) < args.require_laps:
        print(f"ERROR: need {args.require_laps} laps, found {len(lap_dirs)} "
              f"({[d.name for d in lap_dirs]})", file=sys.stderr)
        return 2

    laps = [(d.name, load_lap(d)) for d in lap_dirs]
    payload = {
        "schema": "7dtd.loadgen.benchstock.v1",
        "tolerance": TOLERANCE,
        "laps": {name: lap for name, lap in laps},
    }
    md = render_md(laps)
    out_dir = args.out or args.laps_dir
    out_dir.mkdir(parents=True, exist_ok=True)
    (out_dir / "bench-stock.md").write_text(md, encoding="utf-8", newline="\n")
    (out_dir / "bench-stock.json").write_text(
        json.dumps(payload, indent=1, sort_keys=True), encoding="utf-8", newline="\n")
    print(md)
    return 0


if __name__ == "__main__":
    sys.exit(main())
