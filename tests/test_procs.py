"""scripts/procs.py: the /proc walk that replaced pgrep/pkill on teardown paths.

Drives real child processes, because the point of the module is that it reads
the kernel's own view of a cmdline rather than parsing another tool's output.
"""

from __future__ import annotations

import os
import subprocess
import sys
import time

import procs

# Unique per test run so a stale marker from an earlier run cannot match.
MARKER = f"7dtd-loadgen-procs-test-{os.getpid()}"


def _sleeper() -> subprocess.Popen[bytes]:
    """A child whose cmdline carries MARKER and that exits on SIGTERM."""
    return subprocess.Popen([sys.executable, "-c", f"import time; time.sleep(120)  # {MARKER}"])


def _wait_gone(proc: subprocess.Popen[bytes], timeout: float = 15.0) -> bool:
    try:
        proc.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        return False
    return True


def test_find_matches_cmdline_substring():
    proc = _sleeper()
    try:
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline and proc.pid not in procs.find(MARKER):
            time.sleep(0.05)
        assert proc.pid in procs.find(MARKER)
    finally:
        proc.kill()
        proc.wait(timeout=10)


def test_find_skips_self_and_parent():
    # A teardown that matched its own runner would kill the harness mid-run.
    assert os.getpid() not in procs.find("")
    assert os.getppid() not in procs.find("")


def test_find_returns_empty_for_no_match():
    assert procs.find("no-process-anywhere-has-this-in-its-cmdline-4f7a") == []


def test_kill_terminates_matches():
    proc = _sleeper()
    deadline = time.monotonic() + 10
    while time.monotonic() < deadline and proc.pid not in procs.find(MARKER):
        time.sleep(0.05)
    killed = procs.kill(MARKER)
    assert proc.pid in killed
    assert _wait_gone(proc), "process survived kill()"


def test_kill_with_no_matches_is_not_an_error():
    # Teardown runs on every exit path, including ones where nothing started.
    assert procs.kill("no-process-anywhere-has-this-in-its-cmdline-4f7a") == []
