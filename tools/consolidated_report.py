#!/usr/bin/env python3
"""Consolidated stock-vs-zdtd comparison report (repeatable, machine-readable).

Walks the comparison workspaces of 7dtd-loadgen (per-scenario diff.json) and
7dtd-playtest (per-suite playtest-compare.json) and emits one CONSOLIDATED
overview (CONSOLIDATED.md + .json): every scenario/suite that was compared, its
verdict, and its findings/deltas. This replaces the hand-maintained consolidated
ledger - the output is regenerated from committed evidence, so the view cannot
drift from the runs.

A suite/scenario is HONESTLY classified:
  - CLEAN       both sides ran, no per-case/axis differences, no findings
  - DELTAS      both sides ran, differences exist (findings to triage, never faked)
  - ONE-SIDE    only one server ran (missing capability or run failure) - never
                reported as compared
  - UNREADABLE  the evidence file exists but could not be parsed. It is listed
                as its own verdict: dropping the entry would silently remove a
                scenario from the ledger and read as "nothing was compared here".
  - STALE       one side's evidence is on disk but no diff.json was written for
                it, or the two sides come from different invocations. Both sides
                ran, so neither ONE-SIDE nor a comparison describes the entry.

Usage: python3 tools/consolidated_report.py [--playtest-root <dir>] [--out <dir>]
Defaults: playtest root ../7dtd-playtest/workspace/comparison-playtest, out
workspace/comparison.

Regeneration refuses to run when it would drop playtest suites the committed
ledger already holds: the playtest evidence lives in a sibling checkout, and a
clone without it would otherwise rewrite the ledger smaller and exit 0.
"""

from __future__ import annotations

import argparse
import io
import json
import sys
from collections import Counter
from pathlib import Path

from json_shape import as_cell, as_dict, as_list, as_number, as_sides

# Locale-independent text boundary: the rendered text comes from UTF-8 JSON
# evidence, but a C-locale runner gives stdout an ASCII codec and print()
# raises. See scenario_env.py for the same block and its reason.
if isinstance(sys.stdout, io.TextIOWrapper):
    sys.stdout.reconfigure(encoding="utf-8")
if isinstance(sys.stderr, io.TextIOWrapper):
    sys.stderr.reconfigure(encoding="utf-8")

ROOT = Path(__file__).resolve().parents[1]

# Verdict labels in the order the header line reports them.
VERDICT_ORDER = ("CLEAN", "DELTAS", "ONE-SIDE", "UNREADABLE", "STALE")

# The diff.json contract this reader understands. Evidence written before the
# id was stamped carries none and is read as this version; evidence stamped
# with any other id comes from a different report revision, whose field
# meanings are not these, so it is listed as UNREADABLE instead of scored.
DIFF_SCHEMA = "7dtd.loadgen.diff.v1"


def _load_json(p: Path) -> tuple[dict | None, str | None]:
    """(document, error). A missing file is absence of evidence; a file that is
    present but unparseable is broken evidence, and the caller must not fold the
    two into the same empty result."""
    if not p.is_file():
        return None, None
    try:
        with open(p, encoding="utf-8") as fh:
            doc = json.load(fh)
    except (OSError, ValueError) as e:
        return None, f"{p.name}: {e.__class__.__name__}: {e}"
    if not isinstance(doc, dict):
        return None, f"{p.name}: expected a JSON object, got {type(doc).__name__}"
    return doc, None


def _unreadable_row(tool: str, ident: str, detail: str) -> dict:
    return {
        "tool": tool,
        "id": ident,
        "compared": False,
        "ran": None,
        "missing": None,
        "verdict": "UNREADABLE",
        "findings": [f"evidence unreadable ({detail})"],
        "summary": None,
    }


def collect_loadgen(compare_root: Path) -> list[dict]:
    """Per-scenario rows from workspace/comparison/<scenario>/diff.json."""
    rows: list[dict] = []
    if not compare_root.is_dir():
        return rows
    for scenario_dir in sorted(p for p in compare_root.iterdir() if p.is_dir()):
        d, err = _load_json(scenario_dir / "diff.json")
        if err is not None:
            print(f"WARN: {scenario_dir.name}/diff.json unreadable: {err}", file=sys.stderr)
            rows.append(_unreadable_row("loadgen", scenario_dir.name, err))
            continue
        if d is None:
            # No diff.json: a one-sided run (`make compare-sut SUT=zdtd`) still
            # left evidence, and silently dropping it hides the run entirely.
            present = [s for s in ("stock", "zdtd") if (scenario_dir / s).is_dir()]
            if not present:
                continue
            rows.append({
                "tool": "loadgen",
                "id": scenario_dir.name,
                "compared": False,
                "ran": present,
                "missing": [s for s in ("stock", "zdtd") if s not in present],
                "verdict": "STALE",
                "findings": [],
                "summary": None,
            })
            continue
        schema = d.get("schema")
        if schema is not None and schema != DIFF_SCHEMA:
            err = (f"{scenario_dir.name}/diff.json: schema {schema!r}, "
                   f"this report reads {DIFF_SCHEMA}")
            print(f"WARN: {err}", file=sys.stderr)
            rows.append(_unreadable_row("loadgen", scenario_dir.name, err))
            continue
        if d.get("stale"):
            # Both sides hold evidence, but from different invocations: a
            # one-sided rerun replaced one of them. That is neither a comparison
            # nor a one-sided run, and reporting it as ONE-SIDE would read as
            # "the other server could not run".
            rows.append({
                "tool": "loadgen",
                "id": scenario_dir.name,
                "compared": False,
                "ran": ["stock", "zdtd"],
                "missing": [],
                "verdict": "STALE",
                "findings": d.get("findings") or [],
                "summary": None,
            })
            continue
        findings = [as_cell(f) for f in as_list(d.get("findings"))]
        if not d.get("compared"):
            verdict = "ONE-SIDE"
        elif findings:
            verdict = "DELTAS"
        else:
            verdict = "CLEAN"
        rows.append({
            "tool": "loadgen",
            "id": scenario_dir.name,
            "compared": bool(d.get("compared")),
            "ran": as_sides(d.get("ran")),
            "missing": as_sides(d.get("missing")),
            "verdict": verdict,
            "findings": findings,
            "summary": None,
        })
    return rows


def collect_playtest(playtest_root: Path) -> list[dict]:
    """Per-suite rows from comparison-playtest/<suite>/playtest-compare.json."""
    rows: list[dict] = []
    if not playtest_root.is_dir():
        return rows
    for suite_dir in sorted(p for p in playtest_root.iterdir() if p.is_dir()):
        d, err = _load_json(suite_dir / "playtest-compare.json")
        if err is not None:
            print(f"WARN: {suite_dir.name}/playtest-compare.json unreadable: {err}",
                  file=sys.stderr)
            rows.append(_unreadable_row("playtest", suite_dir.name, err))
            continue
        if d is None:
            continue
        # playtest-compare.json is written by 7dtd-playtest: well-formed JSON
        # of the wrong shape is a capture this reader has to survive, not a
        # reason to lose the whole consolidated ledger to an AttributeError.
        stock = as_dict(as_dict(d.get("stock")).get("summary"))
        zdtd = as_dict(as_dict(d.get("zdtd")).get("summary"))
        wall = {"stock": as_number(as_dict(d.get("stock")).get("wall")),
                "zdtd": as_number(as_dict(d.get("zdtd")).get("wall"))}
        deltas = []
        for case in as_list(d.get("cases")):
            case = as_dict(case)
            s_case = as_dict(case.get("stock"))
            z_case = as_dict(case.get("zdtd"))
            s = s_case.get("status")
            z = z_case.get("status")
            if s != z:
                deltas.append({
                    "case": as_cell(case.get("case")),
                    "stock": as_cell(s),
                    "zdtd": as_cell(z),
                    "detail": f"{as_cell(s_case.get('detail'))} | "
                              f"{as_cell(z_case.get('detail'))}",
                })
        findings = [as_cell(f) for f in as_list(d.get("findings"))]
        if not d.get("compared"):
            verdict = "ONE-SIDE"
        elif deltas or findings:
            verdict = "DELTAS"
        else:
            verdict = "CLEAN"
        rows.append({
            "tool": "playtest",
            "id": suite_dir.name,
            "compared": bool(d.get("compared")),
            "ran": as_sides(d.get("ran")),
            "missing": as_sides(d.get("missing")),
            "verdict": verdict,
            "findings": findings,
            "deltas": deltas,
            "summary": {"stock": stock, "zdtd": zdtd},
            "wall": wall,
        })
    return rows


def render(rows: list[dict]) -> str:
    def fmt_wall(v: float | None) -> str:
        return f"{v:.1f}" if v is not None else "n/a"

    lines = ["# Consolidated stock-vs-zdtd comparison\n",
             ("Regenerated from committed per-run evidence (loadgen diff.json, "
              "playtest playtest-compare.json). CLEAN = both sides ran with no "
              "differences; DELTAS = differences recorded as findings (triage, "
              "never faked); ONE-SIDE = only one server ran (never counted as "
              "compared). UNREADABLE = evidence present but unparseable; "
              "STALE = one side's evidence present, no diff.json written.\n")]
    lines.append("| tool | id | verdict | stock | zdtd | wall s | findings |")
    lines.append("|---|---|---|---|---|---|---|")
    for r in rows:
        if r["verdict"] == "UNREADABLE":
            stock_cell = zdtd_cell = "unreadable"
            wall_cell = "n/a"
        elif r["tool"] == "playtest":
            s = r["summary"]["stock"]
            z = r["summary"]["zdtd"]
            stock_cell = f"{s.get('pass', 0)}/{s.get('fail', 0)}/{s.get('skip', 0)}"
            zdtd_cell = f"{z.get('pass', 0)}/{z.get('fail', 0)}/{z.get('skip', 0)}"
            wall = r.get("wall") or {}
            wall_cell = f"{fmt_wall(wall.get('stock'))} / {fmt_wall(wall.get('zdtd'))}"
        else:
            ran = r["ran"]
            stock_cell = "ran" if r["compared"] or "stock" in ran else "n/a"
            zdtd_cell = "ran" if r["compared"] or "zdtd" in ran else "n/a"
            wall_cell = "n/a"
        lines.append(f"| {r['tool']} | {r['id']} | {r['verdict']} | {stock_cell} "
                     f"| {zdtd_cell} | {wall_cell} | {len(r['findings'])} |")
    lines.append("")
    for r in rows:
        if r["verdict"] == "CLEAN":
            continue
        lines.append(f"## {r['tool']}/{r['id']} - {r['verdict']}\n")
        if r["verdict"] in ("ONE-SIDE", "STALE"):
            lines.append(f"- ran: {r.get('ran')} | missing: {r.get('missing')} "
                         f"(missing capability, failed run, or a one-sided rerun; "
                         f"not compared)\n")
            continue
        if r["verdict"] == "UNREADABLE":
            for f in r["findings"]:
                lines.append(f"- {f}; the entry is listed, not scored "
                             f"(re-run compare to regenerate it)\n")
            continue
        for f in r["findings"]:
            lines.append(f"- finding: {f}")
        for dlt in r.get("deltas", []):
            lines.append(f"- delta {dlt['case']}: {dlt['stock']} vs {dlt['zdtd']} "
                         f"({dlt['detail']})")
        lines.append("")
    verdicts = Counter(r["verdict"] for r in rows)
    lines.insert(1, "\nCompared entries: "
                    + ", ".join(f"{verdicts[v]} {v}" for v in VERDICT_ORDER)
                    + f" out of {len(rows)}.\n")
    return "\n".join(lines) + "\n"


def playtest_suites_in_ledger(out_dir: Path) -> list[str]:
    """Suite ids the committed CONSOLIDATED.json already carries from
    7dtd-playtest. That file is a previous run of this same tool, so an absent
    or unparseable one just means nothing has been generated here yet."""
    path = out_dir / "CONSOLIDATED.json"
    if not path.is_file():
        return []
    try:
        with open(path, encoding="utf-8") as fh:
            doc = json.load(fh)
    except (OSError, ValueError):
        return []
    return [row["id"] for row in as_list(doc)
            if isinstance(row, dict) and row.get("tool") == "playtest"
            and isinstance(row.get("id"), str)]


def main() -> int:
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    playtest_default = ROOT / ".." / "7dtd-playtest" / "workspace" / "comparison-playtest"
    ap.add_argument("--playtest-root", default=str(playtest_default),
                    help="7dtd-playtest comparison-playtest dir "
                         "(default: %(default)s)")
    ap.add_argument("--out", default=str(ROOT / "workspace" / "comparison"),
                    help="dir for CONSOLIDATED.md + CONSOLIDATED.json "
                         "(default: %(default)s)")
    args = ap.parse_args()
    out_dir = Path(args.out)
    playtest_root = Path(args.playtest_root)
    if not playtest_root.is_dir():
        # The playtest evidence is a sibling checkout, so a clean clone without
        # it regenerates a smaller ledger and still exits 0. Refuse instead: a
        # committed view that loses suites reads as "those were never compared".
        held = playtest_suites_in_ledger(out_dir)
        if held:
            print(f"ERROR: playtest evidence not found: {playtest_root}", file=sys.stderr)
            print(f"       the committed ledger holds {len(held)} 7dtd-playtest "
                  f"suite(s): {', '.join(held)}", file=sys.stderr)
            print("       regenerating now would drop them. Check out "
                  "../7dtd-playtest beside this repo, or pass --playtest-root.",
                  file=sys.stderr)
            return 1
        print(f"WARN: no playtest evidence at {playtest_root}; the ledger will "
              f"cover the loadgen scenarios only", file=sys.stderr)
    rows = collect_loadgen(out_dir) + collect_playtest(playtest_root)
    if not rows:
        print("ERROR: no evidence found (run compare-all / playtest-compare first)",
              file=sys.stderr)
        return 1
    out_dir.mkdir(parents=True, exist_ok=True)
    (out_dir / "CONSOLIDATED.md").write_text(render(rows), encoding="utf-8", newline="\n")
    (out_dir / "CONSOLIDATED.json").write_text(
        json.dumps(rows, indent=1, sort_keys=True), encoding="utf-8", newline="\n")
    print(f"consolidated: {len(rows)} entries -> {out_dir}/CONSOLIDATED.{'md,json'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
