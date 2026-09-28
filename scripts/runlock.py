"""Advisory run lock for a loadgen target, shared by the Python profiles.

scripts/run_loadgen.sh already refuses an overlapping cohort with one flock per
host:port and exit 4. The two load profiles need the same guard for a stronger
reason: they do not merely add a second cohort, they REPLACE the first run.
start_dedicated_prefab.sh opens with `pkill -x 7DaysToDieServe`, so a second
profile kills the first profile's dedicated mid-measurement, and both profiles'
teardown ends in procs.kill(BOT_PROC) / procs.kill(SERVER_PROC), which matches
by cmdline substring and so stops the other run's cohort and server as well.
The result is two runs that both report numbers from a world neither of them
measured.

The lock file name and tag are byte-identical to the shell runner's
(`7dtd-loadgen-<host>-<port>.lock` under XDG_RUNTIME_DIR), so a profile and a
cohort launched through run_loadgen.sh exclude each other too. The profiles
pass LOADGEN_ALLOW_OVERLAP=1 to the run_loadgen.sh they start internally,
which is the documented opt-out for exactly that deliberate nesting.

Re-running a profile after it has exited is unaffected: flock is released when
the holding process dies, so the second run takes the lock freely.
"""

from __future__ import annotations

import fcntl
import os
import re
import sys
from pathlib import Path

# Same normalization the shell runner applies (printf | tr -c 'A-Za-z0-9._-' '_').
_UNSAFE = re.compile(r"[^A-Za-z0-9._-]")

# The shell runner's exit code for "another run holds this target". Callers and
# operators already key automation off it, so the Python guard reuses it.
LOCK_BUSY_EXIT = 4


def lock_path(host: str, port: str | int) -> Path:
    """Where the advisory lock for one target lives. Matches run_loadgen.sh."""
    tag = _UNSAFE.sub("_", f"{host}-{port}")
    base = Path(os.environ.get("XDG_RUNTIME_DIR") or os.environ.get("TMPDIR") or "/tmp")
    return base / f"7dtd-loadgen-{tag}.lock"


def acquire(host: str, port: str | int) -> int | None:
    """Take the target's lock, or return None when another run holds it.

    The returned fd must stay open for the life of the run: closing it (or
    exiting) releases the lock.
    """
    path = lock_path(host, port)
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        fd = os.open(path, os.O_CREAT | os.O_RDWR, 0o600)
    except OSError as e:
        # An unwritable runtime dir must not read as "no other run is going":
        # say why the guard is inert and let the operator decide.
        print(f"runlock: cannot open {path} ({e}); overlap guard is INACTIVE",
              file=sys.stderr)
        return None
    try:
        fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError:
        os.close(fd)
        return None
    os.ftruncate(fd, 0)
    os.write(fd, f"{os.getpid()}\n".encode())
    return fd


def acquire_or_exit(host: str, port: str | int, what: str) -> int:
    """Acquire, or exit LOCK_BUSY_EXIT naming the holder's target.

    Refusing loudly is the whole point: a second profile silently replacing the
    first one produces two plausible-looking reports and no error.
    """
    fd = acquire(host, port)
    if fd is None:
        print(
            f"ERROR: another loadgen run holds {lock_path(host, port)} "
            f"(target {host}:{port}).",
            file=sys.stderr,
        )
        print(f"       A second {what} would kill the first run's dedicated and "
              f"cohort, and both would report numbers from a world neither "
              f"measured.", file=sys.stderr)
        print("       Wait for it to finish, stop it, or set "
              "LOADGEN_ALLOW_OVERLAP=1 if you meant it.", file=sys.stderr)
        raise SystemExit(LOCK_BUSY_EXIT)
    return fd
