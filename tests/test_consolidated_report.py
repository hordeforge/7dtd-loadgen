"""Offline gate for the consolidated comparison report (tools/consolidated_report.py).

Feeds synthetic loadgen diff.json + playtest playtest-compare.json trees and
asserts the honest classification: CLEAN / DELTAS / ONE-SIDE / UNREADABLE /
STALE, plus the regenerated CONSOLIDATED output. No servers required.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

from consolidated_report import (
    DIFF_SCHEMA,
    collect_loadgen,
    collect_playtest,
    main,
    playtest_suites_in_ledger,
    render,
)


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
    _write(one / "diff.json", {"schema": DIFF_SCHEMA, "compared": False,
                               "ran": ["stock"], "missing": ["zdtd"]})

    rows = collect_loadgen(tmp_path / "lg")
    by_id = {r["id"]: r for r in rows}
    assert by_id["scen-clean"]["verdict"] == "CLEAN"
    assert by_id["scen-deltas"]["verdict"] == "DELTAS"
    assert by_id["scen-one"]["verdict"] == "ONE-SIDE"
    assert by_id["scen-one"]["ran"] == ["stock"]
    assert by_id["scen-one"]["missing"] == ["zdtd"]

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
    assert "scen-one" in md and "missing: ['zdtd']" in md
    assert "delta c1" in md


def test_legacy_scalar_sides_read_as_the_list_shape(tmp_path):
    """diff.json written before the sides became lists names them as a bare
    string. Committed evidence keeps that shape, so the reader coerces it at
    the boundary and classifies it exactly as the list form."""
    lg = tmp_path / "lg"
    _write(lg / "scen-legacy" / "diff.json",
           {"compared": False, "ran": "stock", "missing": "zdtd"})
    _write(lg / "scen-listed" / "diff.json",
           {"schema": DIFF_SCHEMA, "compared": False,
            "ran": ["stock"], "missing": ["zdtd"]})

    rows = collect_loadgen(lg)
    by_id = {r["id"]: r for r in rows}
    assert by_id["scen-legacy"]["ran"] == ["stock"]
    assert by_id["scen-legacy"]["missing"] == ["zdtd"]
    assert by_id["scen-legacy"]["verdict"] == "ONE-SIDE"
    # The two shapes are indistinguishable downstream, table included.
    md = render([by_id["scen-legacy"]])
    assert "scen-legacy | ONE-SIDE | ran | n/a | n/a | 0 |" in md
    assert "ran: ['stock'] | missing: ['zdtd']" in md


def test_a_diff_from_another_report_revision_is_unreadable(tmp_path):
    """A diff.json stamped with a schema this reader does not implement has
    different field meanings. Scoring it under this revision's meanings would
    report a verdict from a document it cannot read."""
    lg = tmp_path / "lg"
    _write(lg / "scen-future" / "diff.json",
           {"schema": "7dtd.loadgen.diff.v2", "compared": True, "findings": []})

    rows = collect_loadgen(lg)
    assert [r["verdict"] for r in rows] == ["UNREADABLE"]
    assert "7dtd.loadgen.diff.v2" in rows[0]["findings"][0]


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


def test_wrong_shaped_playtest_evidence_survives(tmp_path):
    """playtest-compare.json is written by 7dtd-playtest. Well-formed JSON of
    the wrong shape must still land in the ledger with a verdict, not raise out
    of the collector and take every other suite's row with it."""
    pt = tmp_path / "pt" / "suite-weird"
    pt.mkdir(parents=True)
    (pt / "playtest-compare.json").write_text(json.dumps({
        "compared": True,
        "stock": {"summary": ["not", "a", "map"], "wall": "12 min"},
        "zdtd": 7,
        "cases": ["not-a-case", {"stock": "PASS", "zdtd": {"status": "FAIL"}}],
        "findings": {"not": "a list"},
    }), encoding="utf-8")
    _write(tmp_path / "pt" / "suite-clean" / "playtest-compare.json",
           _playtest_compare(5, 5, [{"case": "c1", "stock": {"status": "PASS"},
                                     "zdtd": {"status": "PASS"}}]))

    rows = collect_playtest(tmp_path / "pt")
    by_id = {r["id"]: r for r in rows}
    assert by_id["suite-clean"]["verdict"] == "CLEAN"
    assert by_id["suite-weird"]["verdict"] == "DELTAS"
    assert by_id["suite-weird"]["wall"] == {"stock": None, "zdtd": None}
    assert all(isinstance(f, str) for f in by_id["suite-weird"]["findings"])
    assert "suite-weird" in render(rows)


def test_regeneration_refuses_to_drop_playtest_suites(tmp_path, monkeypatch, capsys):
    """The playtest evidence is a sibling checkout. A clone without
    ../7dtd-playtest regenerates a smaller ledger and exits 0, which reads as
    'those suites were never compared'. The committed rows must survive a run
    that cannot see their source, with a message naming the missing path."""
    out = tmp_path / "out"
    out.mkdir()
    _write(out / "scen-clean" / "diff.json", {"compared": True, "findings": []})
    committed = [{"tool": "playtest", "id": "smoke", "verdict": "CLEAN"},
                 {"tool": "playtest", "id": "soak_long", "verdict": "DELTAS"}]
    (out / "CONSOLIDATED.json").write_text(json.dumps(committed), encoding="utf-8")
    before = (out / "CONSOLIDATED.json").read_text(encoding="utf-8")

    assert playtest_suites_in_ledger(out) == ["smoke", "soak_long"]

    monkeypatch.setattr(sys, "argv", [
        "consolidated_report.py", "--out", str(out),
        "--playtest-root", str(tmp_path / "absent-sibling")])
    assert main() == 1

    err = capsys.readouterr().err
    assert "playtest evidence not found" in err
    assert "smoke, soak_long" in err
    assert (out / "CONSOLIDATED.json").read_text(encoding="utf-8") == before
    assert not (out / "CONSOLIDATED.md").exists()


def test_missing_playtest_root_warns_when_nothing_is_at_stake(tmp_path, monkeypatch, capsys):
    """With no playtest rows in the committed ledger there is nothing to drop,
    so the run proceeds and says the view is loadgen-only rather than failing."""
    out = tmp_path / "out"
    out.mkdir()
    _write(out / "scen-clean" / "diff.json", {"compared": True, "findings": []})

    monkeypatch.setattr(sys, "argv", [
        "consolidated_report.py", "--out", str(out),
        "--playtest-root", str(tmp_path / "absent-sibling")])
    assert main() == 0

    assert "loadgen scenarios only" in capsys.readouterr().err
    assert "scen-clean" in (out / "CONSOLIDATED.md").read_text(encoding="utf-8")
