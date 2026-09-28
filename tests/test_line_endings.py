"""Line-ending policy gates.

Two rules, both checked here because a regression in either is invisible on
Linux and only bites a checkout that translates to CRLF:

1. Files the shell lane rewrites or the renderers parse by line (serverconfig
   and serveradmin XML, the scenario JSON catalogs) must be pinned to LF in
   .gitattributes, the same way the .sh/.py/.cs files already are. `text=auto`
   alone normalizes on commit but still checks out CRLF under autocrlf=true.
2. Generated report evidence must be written with LF regardless of host, so a
   report regenerated on Windows is byte-identical to one from Linux and does
   not churn the diff against committed evidence.
"""

from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BENCH_REPORT = ROOT / "tools" / "bench_report.py"

# Data files the shell lane edits line-wise (sed -i, sbconfig.py render) or the
# Python lanes parse; each one needs its own eol=lf rule.
EDITED_DATA_FILES = [
    ROOT / "scripts" / "serveradmin_apm_seed.xml",
    ROOT / "scripts" / "serverconfig_loadgen.xml",
    ROOT / "scripts" / "scenarios" / "realearth.json",
    ROOT / "scripts" / "scenarios" / "sut.json",
]


def test_gitattributes_pins_eol_lf_for_scripts_and_data_files():
    attrs = (ROOT / ".gitattributes").read_text(encoding="utf-8").splitlines()
    for ext in ("sh", "py", "cs", "xml", "json"):
        assert f"*.{ext} text eol=lf" in attrs, f"*.{ext} text eol=lf missing"
    assert "Makefile text eol=lf" in attrs
    for path in EDITED_DATA_FILES:
        assert path.is_file(), f"{path} is gone; update this gate"
        assert f"*{path.suffix} text eol=lf" in attrs, f"{path.name} has no eol=lf rule"


def test_bench_report_artifacts_are_lf_only(tmp_path):
    lap = tmp_path / "lap1" / "bench"
    lap.mkdir(parents=True)
    (lap / "run-meta.json").write_text(json.dumps({
        "scenario": "bench", "summary": {"pass": 4, "fail": 0},
        "hostLoadStart": "1.0", "hostLoadEnd": "1.0",
        "startUtc": "2026-08-22T10:00:00Z", "endUtc": "2026-08-22T10:01:00Z",
        "bench": {"windowStartMs": 30000, "windowEndMs": 90000,
                  "actionsPerSec": 280.0, "activeMin": 0, "activeMax": 4},
    }), encoding="utf-8")
    (lap / "stats.json").write_text(
        json.dumps({"passRate": 1.0, "bench": {"actionsPerSec": 280.0}}),
        encoding="utf-8")

    out = tmp_path / "out"
    proc = subprocess.run(
        [sys.executable, str(BENCH_REPORT), "--laps-dir", str(tmp_path), "--out", str(out)],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
        timeout=30, check=False)
    assert proc.returncode == 0, proc.stderr
    for name in ("bench-stock.md", "bench-stock.json"):
        assert b"\r" not in (out / name).read_bytes(), f"{name} carries CR: host newlines leaked"
