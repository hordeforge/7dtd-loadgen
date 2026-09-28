"""Give stdout and stderr a UTF-8 codec independent of the caller's locale.

Every report here renders UTF-8 JSON evidence, but a C-locale runner (CI
containers, POSIX shells) leaves the platform-default stdout codec at ASCII, so
print() raises UnicodeEncodeError on the first non-ASCII player name, world name
or scenario title and the tool exits non-zero instead of printing the line it
was asked for. Naming the encoding beats inheriting the caller's.

Call this at import time, before anything writes. A redirected stream is not a
TextIOWrapper and has no codec to fix, so it is left alone.
"""

from __future__ import annotations

import io
import sys


def use_utf8_stdio() -> None:
    for stream in (sys.stdout, sys.stderr):
        if isinstance(stream, io.TextIOWrapper):
            stream.reconfigure(encoding="utf-8")
