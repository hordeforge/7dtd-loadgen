#!/usr/bin/env python3
"""Consolidate bench-stock lap evidence into a machine-readable report.

Walks workspace/bench/lap<N>/<scenario>/ run-meta.json + stats.json (and the
BENCH_SUMMARY line) and emits bench-stock.md + bench-stock.json at the laps
root. A repeatability section compares per-scenario wall across laps
(+-20% threshold) so the 2-lap claim is computed, not asserted.

Usage:
  bench_report.py --laps-dir <dir> [--require-laps N] [--out <dir>]
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import sys
from pathlib import Path

TOLERANCE = 0.20  # per-scenario wall repeatability bound


def iso_delta(a: str, b: str) -> float | None:
    try:
        ta = dt.datetime.fromisoformat(a.replace("Z", "+00:00"))
        tb = dt.datetime.fromisoformat(b.replace("Z", "+00:00"))
        return max(0.0, (tb - ta).total_seconds())
    except (ValueError, TypeError):
        return None


def apm_summary(run_dir: Path) -> dict:
    """Best-effort APM capture summary: lag verdict from apm.log, plus IPC and
    per-layer scores from the session summary.json when present."""
    out = {"verdict": "n/a", "ipc": None, "layers": {}}
    log = run_dir / "apm.log"
    if log.is_file():
        try:
            for line in log.read_text(encoding="utf-8", errors="replace").splitlines():
                if "lag diagnosis" in line or "lagVerdict" in line:
                    out["verdict"] = line.strip().split(":", 1)[-1].strip()[:80]
                    break
        except OSError:
            pass
    sessions = sorted((run_dir / "apm").glob("session_*/summary.json"))
    if sessions:
        try:
            s = json.loads(sessions[-1].read_text(encoding="utf-8"))
            for layer in s.get("layers") or []:
                name = layer.get("layer")
                score = layer.get("score")
                if name and score is not None:
                    out["layers"][name] = float(score)
                sig = layer.get("signals") or {}
                if name == "cpu" and sig.get("ipc") is not None:
                    out["ipc"] = round(float(sig["ipc"]), 3)
        except (ValueError, OSError, TypeError):
            pass
    return out


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
        if stats_path.is_file():
            try:
                stats = json.loads(stats_path.read_text(encoding="utf-8"))
            except (ValueError, OSError):
                pass
        bench = (stats.get("bench") or {}) if isinstance(stats, dict) else {}
        # stats.json is the authoritative join outcome (client.log can contain
        # binary bytes that defeat grep); fall back to run-meta summary.
        joins_pass = stats.get("pass") if isinstance(stats, dict) and stats.get("pass") is not None \
            else meta.get("summary", {}).get("pass")
        joins_fail = stats.get("fail") if isinstance(stats, dict) and stats.get("fail") is not None \
            else meta.get("summary", {}).get("fail")
        wall = iso_delta(meta.get("startUtc", ""), meta.get("endUtc", ""))
        scenarios[sc] = {
            "wallS": round(wall, 1) if wall is not None else None,
            "joinsPass": joins_pass,
            "joinsFail": joins_fail,
            "hostLoad": f"{meta.get('hostLoadStart')}->{meta.get('hostLoadEnd')}",
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
    # Repeatability across laps.
    if len(laps) >= 2:
        lines.append("\n## Repeatability (per-scenario wall, +-20% bound)\n")
        lines.append("| scenario | " + " | ".join(n for n, _ in laps)
                     + " | delta% | verdict |")
        lines.append("|---|---|---|---|---|")
        names = [n for n, _ in laps]
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
        # Load-sensitive axis (bench profile only): actions/s lap-to-lap.
        aps = []
        for _, lap in laps:
            b = lap["scenarios"].get("bench", {}).get("bench") or {}
            aps.append(b.get("actionsPerSec"))
        if all(a is not None for a in aps) and len(aps) >= 2 and aps[0]:
            aps_delta = abs(aps[1] - aps[0]) / aps[0]
            aps_ok = "OK" if aps_delta <= TOLERANCE else "OVER"
            lines.append(f"\n- bench actions/s: {' -> '.join(f'{a:.2f}' for a in aps)} "
                         f"(delta {aps_delta*100:.1f}% {aps_ok}, +-{TOLERANCE*100:.0f}% bound)")
        lines.append(f"\n- tolerance: +-{TOLERANCE*100:.0f}% per scenario; "
                     "over-tolerance rows are a finding (host contention), "
                     "recorded with hostLoad, never hidden.")
    return "\n".join(lines) + "\n"


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--laps-dir", type=Path, default=Path("workspace/bench"))
    ap.add_argument("--out", type=Path, default=None)
    ap.add_argument("--require-laps", type=int, default=0)
    args = ap.parse_args()

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
    (out_dir / "bench-stock.md").write_text(md, encoding="utf-8")
    (out_dir / "bench-stock.json").write_text(
        json.dumps(payload, indent=1, sort_keys=True), encoding="utf-8")
    print(md)
    return 0


if __name__ == "__main__":
    sys.exit(main())
