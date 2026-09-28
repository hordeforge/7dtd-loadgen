"""scripts/webdash_password_hash.py: the WebDashboardPassword value written
into a rendered serverconfig.

7DTD authenticates the web dashboard as base64(MD5(utf8(password))). A drift in
either half produces a hash no credential can match, and the operator meets it
at the browser instead of where the value was written. The password itself must
never reach argv or stdout: only the hash does. The expected values are the
literal game encoding, not a re-implementation of it.
"""

from __future__ import annotations

import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TOOL = ROOT / "scripts" / "webdash_password_hash.py"


def _run(password: str | None) -> subprocess.CompletedProcess[str]:
    env = {k: v for k, v in os.environ.items() if k != "RE_ADMIN_WEB_PASSWORD"}
    if password is not None:
        env["RE_ADMIN_WEB_PASSWORD"] = password
    return subprocess.run(
        [sys.executable, str(TOOL)],
        capture_output=True, text=True, encoding="utf-8",
        errors="replace", timeout=30, check=False, env=env,
    )


def test_hash_matches_the_game_encoding() -> None:
    r = _run("hunter2")
    assert r.returncode == 0, r.stderr
    assert r.stdout == "KrljkMfb40Od500MmwsXZw=="
    assert "hunter2" not in r.stdout
    assert r.stderr == ""


def test_non_ascii_password_hashes_as_utf8() -> None:
    r = _run("pässwörd")
    assert r.returncode == 0, r.stderr
    assert r.stdout == "EoQeS6XjfS+/x4RYxnFK3g=="


def test_hash_has_no_trailing_newline() -> None:
    # The value is interpolated into an XML attribute; a trailing newline would
    # be written back into serverconfig.xml verbatim.
    assert _run("hunter2").stdout == _run("hunter2").stdout.rstrip("\n")


def test_unset_password_fails_loudly_without_printing_a_hash() -> None:
    # An empty hash would render a dashboard reachable with the empty password.
    r = _run(None)
    assert r.returncode == 2
    assert r.stdout == ""
    assert "RE_ADMIN_WEB_PASSWORD is empty" in r.stderr


def test_empty_password_fails_the_same_way() -> None:
    r = _run("")
    assert r.returncode == 2
    assert r.stdout == ""
    assert "RE_ADMIN_WEB_PASSWORD is empty" in r.stderr
