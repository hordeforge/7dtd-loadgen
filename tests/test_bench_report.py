#!/usr/bin/env python3
"""Offline gates for tools/bench_report.py (bench-stock consolidation).

Synthetic lap evidence dirs verify the repeatability math (per-scenario wall
within +-20% = OK, over = OVER with a hostLoad caveat) and the report shape.
"""

from __future__ import annotations

import json
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TOOL = ROOT / "tools" / "bench_report.py"


def _make_lap(root: Path, lap: str, scenarios: dict[str, dict]) -> Path:
    d = root / lap
    for sc, meta in scenarios.items():
        scd = d / sc
        scd.mkdir(parents=True)
        meta["startUtc"] = meta.get("startUtc", "2026-08-22T10:00:00Z")
        meta["endUtc"] = meta.get(
            "endUtc",
            "2026-08-22T10:01:00Z" if meta.get("wallS", 60) == 60
            else "2026-08-22T10:01:30Z",
        )
        (scd / "run-meta.json").write_text(json.dumps(meta), encoding="utf-8")
        if "bench" in meta:
            (scd / "stats.json").write_text(json.dumps(
                {"passRate": 1.0, "bench": meta["bench"]}), encoding="utf-8")
    return d


def _run(root: Path, out: Path, extra: list[str] | None = None) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [sys.executable, str(TOOL), "--laps-dir", str(root), "--out", str(out)]
        + (extra or []),
        capture_output=True, text=True, timeout=30,
    )


def test_report_repeatability_ok(tmp_path):
    t0 = time.time()
    meta = {"scenario": "bench", "summary": {"pass": 16, "fail": 0},
            "hostLoadStart": "1.0", "hostLoadEnd": "1.2",
            "startUtc": time.strftime("2026-08-22T%H:%M:%SZ", time.gmtime(t0)),
            "endUtc": time.strftime("2026-08-22T%H:%M:%SZ", time.gmtime(t0 + 100)),
            "bench": {"windowStartMs": 30000, "windowEndMs": 90000,
                      "actionsPerSec": 280.0, "activeMin": 0, "activeMax": 16}}
    m2 = dict(meta, endUtc=time.strftime(
        "2026-08-22T%H:%M:%SZ", time.gmtime(t0 + 110)))  # +10% wall
    _make_lap(tmp_path, "lap1", {"bench": meta})
    _make_lap(tmp_path, "lap2", {"bench": m2})
    out = tmp_path / "out"
    r = _run(tmp_path, out)
    assert r.returncode == 0, r.stderr
    md = (out / "bench-stock.md").read_text(encoding="utf-8")
    assert "| bench | 100.0 | 110.0 | 10.0% | OK (10.0%) |" in md
    payload = json.loads((out / "bench-stock.json").read_text(encoding="utf-8"))
    assert payload["schema"] == "7dtd.loadgen.benchstock.v1"
    assert set(payload["laps"]) == {"lap1", "lap2"}


def test_report_over_tolerance_flagged(tmp_path):
    t0 = time.time()
    meta = {"scenario": "soak-4bot", "summary": {"pass": 4, "fail": 0},
            "hostLoadStart": "1.0", "hostLoadEnd": "8.5",
            "startUtc": time.strftime("2026-08-22T%H:%M:%SZ", time.gmtime(t0)),
            "endUtc": time.strftime("2026-08-22T%H:%M:%SZ", time.gmtime(t0 + 300))}
    m2 = dict(meta, endUtc=time.strftime(
        "2026-08-22T%H:%M:%SZ", time.gmtime(t0 + 420)),  # +40% wall
        hostLoadEnd="9.0")
    _make_lap(tmp_path, "lap1", {"soak-4bot": meta})
    _make_lap(tmp_path, "lap2", {"soak-4bot": m2})
    out = tmp_path / "out"
    r = _run(tmp_path, out)
    assert r.returncode == 0, r.stderr
    md = (out / "bench-stock.md").read_text(encoding="utf-8")
    assert "OVER (40.0%) - hostLoad check" in md


def test_require_laps_gate(tmp_path):
    _make_lap(tmp_path, "lap1", {"bench": {"scenario": "bench",
                                           "summary": {"pass": 1, "fail": 0}}})
    out = tmp_path / "out"
    r = _run(tmp_path, out, ["--require-laps", "2"])
    assert r.returncode == 2
    assert "need 2 laps, found 1" in r.stderr


def test_apm_cell_includes_ipc_and_layer_scores(tmp_path):
    """A session summary.json with layers+ipc enriches the report cell."""
    _make_lap(tmp_path, "lap1", {"bench": {
        "scenario": "bench", "summary": {"pass": 16, "fail": 0},
        "bench": {"actionsPerSec": 41.0}}})
    scd = tmp_path / "lap1" / "bench"
    apm = scd / "apm" / "session_x" / "summary.json"
    apm.parent.mkdir(parents=True)
    apm.write_text(json.dumps({
        "layers": [
            {"layer": "scheduler", "score": 50.0},
            {"layer": "cpu", "score": 15.0, "signals": {"ipc": 2.062}},
        ]}), encoding="utf-8")
    (scd / "apm.log").write_text(
        "finalized ...\n>> lag diagnosis: server met its tick deadline this window\n",
        encoding="utf-8")
    out = tmp_path / "out"
    r = _run(tmp_path, out)
    assert r.returncode == 0, r.stderr
    md = (out / "bench-stock.md").read_text(encoding="utf-8")
    assert "server met its tick deadline this window; ipc=2.062; scheduler=50; cpu=15" in md
