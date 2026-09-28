"""scripts/stats_pass_fail.py: the pass/fail numbers bench_stock.sh embeds in
run-meta.json and that tools/bench_report.py then tabulates.

Its stdout is parsed by `read -r pass fail <<<"$(...)"` in bench_stock.sh, so
the "pass fail" line and exit 0 are a wire contract, not a convenience. The
0 0 fallback is the risk: a missing or corrupt stats.json must not read as a
measured all-fail run, and must say on stderr that it fired.
"""

from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TOOL = ROOT / "scripts" / "stats_pass_fail.py"


def _run(*args: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [sys.executable, str(TOOL), *args],
        capture_output=True, text=True, encoding="utf-8",
        errors="replace", timeout=30, check=False,
    )


def test_counts_come_from_the_stats_artifact(tmp_path):
    stats = tmp_path / "stats.json"
    stats.write_text(
        json.dumps({"schema": "7dtd.loadgen.stats.v1", "total": 16,
                    "pass": 12, "fail": 4, "passRate": 0.75}),
        encoding="utf-8",
    )
    r = _run(str(stats))
    assert r.returncode == 0, r.stderr
    assert r.stdout.split() == ["12", "4"]
    assert r.stderr == ""


def test_zero_counts_are_a_real_measurement_not_a_fallback(tmp_path):
    # A run where nothing joined still exits 0 with "0 0" and no warning: the
    # bench lane must not conflate that with unreadable telemetry.
    stats = tmp_path / "stats.json"
    stats.write_text(json.dumps({"total": 8, "pass": 0, "fail": 8}), encoding="utf-8")
    r = _run(str(stats))
    assert r.returncode == 0, r.stderr
    assert r.stdout.split() == ["0", "8"]
    assert r.stderr == ""


def test_missing_file_falls_back_and_says_why(tmp_path):
    r = _run(str(tmp_path / "absent.json"))
    assert r.returncode == 0
    assert r.stdout.split() == ["0", "0"]
    assert "no join counts from" in r.stderr
    assert "FileNotFoundError" in r.stderr


def test_truncated_json_falls_back_and_says_why(tmp_path):
    # Killed mid-write: the byte-identical contract the bench lane depends on
    # is a parse failure, not a partial count.
    stats = tmp_path / "stats.json"
    stats.write_text('{"pass": 12, "fail', encoding="utf-8")
    r = _run(str(stats))
    assert r.returncode == 0
    assert r.stdout.split() == ["0", "0"]
    assert "JSONDecodeError" in r.stderr


def test_non_numeric_counts_fall_back_instead_of_crashing(tmp_path):
    # A stray string in the counts (hand-edited artifact, schema drift) used to
    # raise ValueError out of main(); the documented fallback is 0 0 + a note.
    stats = tmp_path / "stats.json"
    stats.write_text(json.dumps({"pass": "many", "fail": 0}), encoding="utf-8")
    r = _run(str(stats))
    assert r.returncode == 0, r.stderr
    assert r.stdout.split() == ["0", "0"]
    assert "ValueError" in r.stderr


def test_wrong_arity_exits_2_without_inventing_counts():
    r = _run()
    assert r.returncode == 2
    assert r.stdout == ""
    r2 = _run("a.json", "b.json")
    assert r2.returncode == 2
    assert r2.stdout == ""
