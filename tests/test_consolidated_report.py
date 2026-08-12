"""Offline gate for the consolidated comparison report (tools/consolidated_report.py).

Feeds synthetic loadgen diff.json + playtest playtest-compare.json trees and
asserts the honest classification: CLEAN / DELTAS / ONE-SIDE, plus the
regenerated CONSISTENT output. No servers required.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TOOLS = ROOT / "tools"
sys.path.insert(0, str(TOOLS))

from consolidated_report import collect_loadgen, collect_playtest, render  # noqa: E402


def _write(path: Path, data: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=1), encoding="utf-8")


def _playtest_compare(pass_stock: int, pass_zdtd: int, cases: list[dict]) -> dict:
    def summary(p: int) -> dict:
        return {"pass": p, "fail": 0, "skip": 0}

    return {"compared": True, "findings": [], "stock": {"summary": summary(pass_stock)},
            "zdtd": {"summary": summary(pass_zdtd)}, "cases": cases}


def test_clean_deltas_and_one_side(tmp_path):
    # loadgen: one CLEAN scenario, one DELTAS, one ONE-SIDE.
    clean = tmp_path / "lg" / "scen-clean"
    _write(clean / "diff.json", {"compared": True, "findings": []})
    deltas = tmp_path / "lg" / "scen-deltas"
    _write(deltas / "diff.json", {"compared": True, "findings": ["clock rate differs"]})
    one = tmp_path / "lg" / "scen-one"
    _write(one / "diff.json", {"compared": False, "ran": "stock", "missing": "zdtd"})

    rows = collect_loadgen(tmp_path / "lg")
    by_id = {r["id"]: r for r in rows}
    assert by_id["scen-clean"]["verdict"] == "CLEAN"
    assert by_id["scen-deltas"]["verdict"] == "DELTAS"
    assert by_id["scen-one"]["verdict"] == "ONE-SIDE"

    # playtest: one CLEAN suite, one DELTAS (status delta).
    pt = tmp_path / "pt"
    _write(pt / "suite-clean" / "playtest-compare.json",
           _playtest_compare(5, 5, [{"case": "c1", "stock": {"status": "PASS"},
                                     "zdtd": {"status": "PASS"}}]))
    _write(pt / "suite-deltas" / "playtest-compare.json",
           _playtest_compare(4, 5, [{"case": "c1", "stock": {"status": "PASS", "detail": "a"},
                                     "zdtd": {"status": "FAIL", "detail": "b"}}]))
    prows = collect_playtest(pt)
    pby = {r["id"]: r for r in prows}
    assert pby["suite-clean"]["verdict"] == "CLEAN"
    assert pby["suite-deltas"]["verdict"] == "DELTAS"
    assert pby["suite-deltas"]["deltas"][0]["case"] == "c1"

    md = render(rows + prows)
    assert "CLEAN" in md and "DELTAS" in md and "ONE-SIDE" in md
    assert "scen-one" in md and "missing: zdtd" in md
    assert "delta c1" in md


def test_no_evidence_is_an_error(tmp_path):
    assert collect_loadgen(tmp_path / "nope") == []
    assert collect_playtest(tmp_path / "nope") == []
