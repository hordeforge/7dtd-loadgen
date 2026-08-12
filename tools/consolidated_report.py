#!/usr/bin/env python3
"""Consolidated stock-vs-zdtd comparison report (repeatable, machine-readable).

Walks the comparison workspaces of 7dtd-loadgen (per-scenario diff.json) and
7dtd-playtest (per-suite playtest-compare.json) and emits one CONSISTENT
overview: every scenario/suite that was compared, its verdict, and its
findings/deltas. This replaces the hand-maintained consolidated ledger - the
output is regenerated from committed evidence, so the view cannot drift from
the runs.

A suite/scenario is HONESTLY classified:
  - CLEAN    both sides ran, no per-case/axis differences, no findings
  - DELTAS   both sides ran, differences exist (findings to triage, never faked)
  - ONE-SIDE only one server ran (missing capability or run failure) - never
             reported as compared
  - STALE    only one side's evidence is present

Usage: python3 tools/consolidated_report.py [--playtest-root <dir>] [--out <dir>]
Defaults: playtest root ../7dtd-playtest, out workspace/comparison.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def _load_json(p: Path) -> dict | None:
    try:
        return json.load(open(p, encoding="utf-8"))
    except (OSError, ValueError):
        return None


def collect_loadgen(compare_root: Path) -> list[dict]:
    """Per-scenario rows from workspace/comparison/<scenario>/diff.json."""
    rows = []
    if not compare_root.is_dir():
        return rows
    for scenario_dir in sorted(p for p in compare_root.iterdir() if p.is_dir()):
        d = _load_json(scenario_dir / "diff.json")
        if d is None:
            continue
        verdict = "ONE-SIDE" if d.get("compared") is False else (
            "DELTAS" if d.get("findings") else "CLEAN"
        )
        rows.append({
            "tool": "loadgen",
            "id": scenario_dir.name,
            "compared": bool(d.get("compared")),
            "ran": d.get("ran"),
            "missing": d.get("missing"),
            "verdict": verdict,
            "findings": d.get("findings") or [],
            "summary": None,
        })
    return rows


def collect_playtest(playtest_root: Path) -> list[dict]:
    """Per-suite rows from comparison-playtest/<suite>/playtest-compare.json."""
    rows = []
    if not playtest_root.is_dir():
        return rows
    for suite_dir in sorted(p for p in playtest_root.iterdir() if p.is_dir()):
        d = _load_json(suite_dir / "playtest-compare.json")
        if d is None:
            continue
        stock = d.get("stock", {}).get("summary") or {}
        zdtd = d.get("zdtd", {}).get("summary") or {}
        wall = {"stock": (d.get("stock") or {}).get("wall"),
                "zdtd": (d.get("zdtd") or {}).get("wall")}
        deltas = []
        for case in d.get("cases", []):
            s = (case.get("stock") or {}).get("status")
            z = (case.get("zdtd") or {}).get("status")
            if s != z:
                deltas.append({
                    "case": case["case"],
                    "stock": s,
                    "zdtd": z,
                    "detail": f"{case.get('stock', {}).get('detail', '')} | "
                              f"{case.get('zdtd', {}).get('detail', '')}",
                })
        if d.get("compared") is False:
            verdict = "ONE-SIDE"
        elif deltas or d.get("findings"):
            verdict = "DELTAS"
        else:
            verdict = "CLEAN"
        rows.append({
            "tool": "playtest",
            "id": suite_dir.name,
            "compared": bool(d.get("compared")),
            "ran": d.get("ran"),
            "missing": d.get("missing"),
            "verdict": verdict,
            "findings": d.get("findings") or [],
            "deltas": deltas,
            "summary": {"stock": stock, "zdtd": zdtd},
            "wall": wall,
        })
    return rows


def render(rows: list[dict]) -> str:
    lines = ["# Consolidated stock-vs-zdtd comparison\n",
             "Regenerated from committed per-run evidence (loadgen diff.json, "
             "playtest playtest-compare.json). CLEAN = both sides ran with no "
             "differences; DELTAS = differences recorded as findings (triage, "
             "never faked); ONE-SIDE = only one server ran (never counted as "
             "compared).\n"]
    lines.append("| tool | id | verdict | stock | zdtd | wall s | findings |")
    lines.append("|---|---|---|---|---|---|---|")
    for r in rows:
        if r["tool"] == "playtest":
            s = r["summary"]["stock"]
            z = r["summary"]["zdtd"]
            stock_cell = f"{s.get('pass', 0)}/{s.get('fail', 0)}/{s.get('skip', 0)}"
            zdtd_cell = f"{z.get('pass', 0)}/{z.get('fail', 0)}/{z.get('skip', 0)}"
            wall = r.get("wall") or {}
            wf = lambda v: f"{v:.1f}" if v is not None else "n/a"
            wall_cell = f"{wf(wall.get('stock'))} / {wf(wall.get('zdtd'))}"
        else:
            stock_cell = "ran" if r["compared"] else ("ran" if r["ran"] == "stock" else "n/a")
            zdtd_cell = "ran" if r["compared"] else ("ran" if r["ran"] == "zdtd" else "n/a")
            wall_cell = "n/a"
        lines.append(f"| {r['tool']} | {r['id']} | {r['verdict']} | {stock_cell} "
                     f"| {zdtd_cell} | {wall_cell} | {len(r['findings'])} |")
    lines.append("")
    for r in rows:
        if r["verdict"] == "CLEAN":
            continue
        lines.append(f"## {r['tool']}/{r['id']} - {r['verdict']}\n")
        if r["verdict"] == "ONE-SIDE":
            lines.append(f"- ran: {r.get('ran')} | missing: {r.get('missing')} "
                         f"(missing capability or failed run; not compared)\n")
            continue
        for f in r["findings"]:
            lines.append(f"- finding: {f}")
        for dlt in r.get("deltas", []):
            lines.append(f"- delta {dlt['case']}: {dlt['stock']} vs {dlt['zdtd']} "
                         f"({dlt['detail']})")
        lines.append("")
    clean = sum(1 for r in rows if r["verdict"] == "CLEAN")
    total = len(rows)
    lines.insert(1, f"\nCompared entries: {clean}/{total} CLEAN, "
                    f"{sum(1 for r in rows if r['verdict'] == 'DELTAS')} DELTAS, "
                    f"{sum(1 for r in rows if r['verdict'] == 'ONE-SIDE')} ONE-SIDE.\n")
    return "\n".join(lines) + "\n"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--playtest-root", default=str(ROOT / ".." / "7dtd-playtest" / "workspace" / "comparison-playtest"))
    ap.add_argument("--out", default=str(ROOT / "workspace" / "comparison"))
    args = ap.parse_args()
    rows = collect_loadgen(Path(args.out)) + collect_playtest(Path(args.playtest_root))
    if not rows:
        print("ERROR: no evidence found (run compare-all / playtest-compare first)", file=sys.stderr)
        return 1
    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)
    (out_dir / "CONSOLIDATED.md").write_text(render(rows), encoding="utf-8")
    (out_dir / "CONSOLIDATED.json").write_text(
        json.dumps(rows, indent=1, sort_keys=True), encoding="utf-8")
    print(f"consolidated: {len(rows)} entries -> {out_dir}/CONSOLIDATED.{'md,json'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
