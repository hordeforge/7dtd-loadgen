"""Offline CLI gates for scripts/compare_sut.sh (no servers booted).

Exercises the entry point's parsing surface: --list, --help, bad --sut,
missing args, and catalog resolution (join-probe vs a catalog scenario). The
boot/capture paths are covered by the live harness + tools tests.
"""

from __future__ import annotations

import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "compare_sut.sh"


def _run(*args: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        ["bash", str(SCRIPT), *args], cwd=str(ROOT),
        capture_output=True, text=True, encoding="utf-8",
        errors="replace", timeout=60,
    )


def test_list_shows_catalog_ids():
    r = _run("--list")
    assert r.returncode == 0, r.stderr
    for sid in ("join-probe", "wander-2bot", "join-fast"):
        assert sid in r.stdout


def test_help_exits_zero():
    r = _run("--help")
    assert r.returncode == 0
    assert "--sut stock|zdtd|all" in r.stdout


def test_bad_sut_rejected():
    r = _run("--scenario", "join-probe", "--sut", "bogus")
    assert r.returncode == 2
    assert "stock|zdtd|all" in r.stderr


def test_missing_args_rejected():
    r = _run("--scenario", "join-probe")
    assert r.returncode == 2
    assert "--scenario and --sut required" in r.stderr
