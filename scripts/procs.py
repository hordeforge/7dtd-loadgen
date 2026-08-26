"""Find and kill lab processes by cmdline substring, via /proc.

Replaces pgrep/pkill: no external process, no shell, and no regex escaping of
the pattern. The bracket trick those calls needed ("7DaysToDieServer.x86_6[4]",
so the matcher does not match itself) is unnecessary here because the walk
skips this process and its ancestors explicitly.

Linux only, which is what the dedicated server and every runner in this repo
target.
"""

from __future__ import annotations

import os
import signal
import time
from pathlib import Path

PROC = Path("/proc")

# Grace between SIGTERM and SIGKILL. The dedicated server flushes its save on
# SIGTERM; killing outright loses the world the next run would have reused.
TERM_GRACE_S = 5.0
TERM_POLL_S = 0.2


def _cmdline(pid: int) -> str:
    """NUL-separated /proc cmdline as one space-joined string ('' if gone)."""
    try:
        raw = (PROC / str(pid) / "cmdline").read_bytes()
    except (OSError, ValueError):
        return ""
    return raw.replace(b"\0", b" ").decode("utf-8", errors="replace").strip()


def find(pattern: str) -> list[int]:
    """PIDs whose cmdline contains `pattern`, excluding this process and its
    parent (a runner must never match and kill its own launcher)."""
    skip = {os.getpid(), os.getppid()}
    hits = []
    for entry in PROC.iterdir():
        if not entry.name.isdigit():
            continue
        pid = int(entry.name)
        if pid in skip:
            continue
        if pattern in _cmdline(pid):
            hits.append(pid)
    return sorted(hits)


def _alive(pid: int) -> bool:
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    return True


def kill(pattern: str) -> list[int]:
    """SIGTERM every match, then SIGKILL whatever is still up after the grace
    window. Returns the PIDs signalled. Missing or foreign processes are not an
    error: teardown runs on every exit path and must not raise there."""
    pids = find(pattern)
    for pid in pids:
        try:
            os.kill(pid, signal.SIGTERM)
        except (ProcessLookupError, PermissionError):
            pass
    deadline = time.monotonic() + TERM_GRACE_S
    while time.monotonic() < deadline:
        if not any(_alive(pid) for pid in pids):
            return pids
        time.sleep(TERM_POLL_S)
    for pid in pids:
        try:
            os.kill(pid, signal.SIGKILL)
        except (ProcessLookupError, PermissionError):
            pass
    return pids
