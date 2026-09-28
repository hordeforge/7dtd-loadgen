"""--help contract for the hand-rolled Python CLIs.

These entry points parse argv themselves instead of using argparse, and each
one used to treat --help as a bad invocation: usage went to stderr with exit
2, and stats_pass_fail.py went further and printed its documented "0 0"
fallback on stdout as if it had been asked for a stats file. Help on stdout
with exit 0 is the convention across this repo (compare_sut.sh, the C#
client, every argparse tool), so it is pinned here for all of them.
"""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]

HAND_ROLLED = [
    "scripts/stats_pass_fail.py",
    "scripts/sut_catalog.py",
    "scripts/coverage_badge.py",
    "tools/sut_capture.py",
    "tools/sut_report.py",
]


def _run(script: str, *args: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [sys.executable, str(ROOT / script), *args], cwd=str(ROOT),
        capture_output=True, text=True, encoding="utf-8",
        errors="replace", timeout=60, check=False,
    )


@pytest.mark.parametrize("script", HAND_ROLLED)
@pytest.mark.parametrize("flag", ["-h", "--help"])
def test_help_goes_to_stdout_and_exits_zero(script: str, flag: str) -> None:
    r = _run(script, flag)
    assert r.returncode == 0, r.stderr
    assert r.stdout.strip(), "help printed nothing to stdout"
    assert r.stderr == ""


@pytest.mark.parametrize("script", HAND_ROLLED)
def test_wrong_arity_still_reports_usage_on_stderr(script: str) -> None:
    r = _run(script, "a", "b", "c", "d", "e")
    assert r.returncode == 2, r.stdout
    assert r.stdout == ""


ARGPARSE_TOOLS = [
    "tools/bench_report.py",
    "tools/consolidated_report.py",
    "tools/sut_telnet.py",
    "scripts/validate_reconnect.py",
]


@pytest.mark.parametrize("script", ARGPARSE_TOOLS)
def test_argparse_help_carries_the_module_docstring(script: str) -> None:
    r = _run(script, "--help")
    assert r.returncode == 0, r.stderr
    assert r.stderr == ""
    # A bare usage line with an empty description leaves the operator with no
    # way to learn what the tool reads or writes.
    first_line = (ROOT / script).read_text(encoding="utf-8").splitlines()[1]
    assert first_line.strip('"') in r.stdout
