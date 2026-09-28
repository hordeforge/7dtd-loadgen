"""Offline gate for the consolidated comparison report (tools/consolidated_report.py).

Feeds synthetic loadgen diff.json + playtest playtest-compare.json trees and
asserts the honest classification: CLEAN / DELTAS / ONE-SIDE / UNREADABLE, plus
the regenerated CONSOLIDATED output. No servers required.
"""

from __future__ import annotations

import json
from pathlib import Path

from consolidated_report import collect_loadgen, collect_playtest, render


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


def test_corrupt_evidence_is_listed_as_unreadable(tmp_path, capsys):
    """A diff.json that exists but does not parse must appear in the ledger as
    UNREADABLE. Dropping it silently removes the scenario from the overview and
    reads as 'nothing was compared here'."""
    lg = tmp_path / "lg"
    broken = lg / "scen-broken"
    broken.mkdir(parents=True)
    (broken / "diff.json").write_text('{"compared": true', encoding="utf-8")
    _write(lg / "scen-clean" / "diff.json", {"compared": True, "findings": []})

    rows = collect_loadgen(lg)
    verdicts = {r["id"]: r["verdict"] for r in rows}
    assert verdicts == {"scen-clean": "CLEAN", "scen-broken": "UNREADABLE"}
    assert "unreadable" in capsys.readouterr().err

    md = render(rows)
    assert "1 UNREADABLE" in md
    assert "scen-broken" in md


def test_non_object_evidence_is_unreadable(tmp_path):
    pt = tmp_path / "pt" / "suite-bad"
    pt.mkdir(parents=True)
    (pt / "playtest-compare.json").write_text("[1, 2, 3]", encoding="utf-8")
    rows = collect_playtest(tmp_path / "pt")
    assert [r["verdict"] for r in rows] == ["UNREADABLE"]


def test_one_sided_evidence_without_a_diff_is_stale_not_dropped(tmp_path):
    """A `make compare-sut SUT=zdtd` run leaves <scenario>/zdtd/ evidence but
    no diff.json. The scenario is missing its comparison, not missing its run,
    so it must be listed as STALE naming the side that ran; a directory with
    neither side is the absence of evidence and is dropped."""
    lg = tmp_path / "lg"
    (lg / "scen-stale" / "zdtd").mkdir(parents=True)
    (lg / "scen-empty").mkdir(parents=True)
    (lg / "scen-clean").mkdir(parents=True)
    _write(lg / "scen-clean" / "diff.json", {"compared": True, "findings": []})

    rows = collect_loadgen(lg)
    by_id = {r["id"]: r for r in rows}
    assert set(by_id) == {"scen-stale", "scen-clean"}
    stale = by_id["scen-stale"]
    assert stale["verdict"] == "STALE"
    assert stale["compared"] is False
    assert stale["ran"] == ["zdtd"]
    assert stale["missing"] == ["stock"]

    md = render(rows)
    assert "1 STALE" in md
    assert "## loadgen/scen-stale - STALE" in md
    assert "ran: ['zdtd']" in md
