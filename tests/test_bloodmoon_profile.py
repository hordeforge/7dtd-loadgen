"""bloodmoon_profile: telnet stream decoding and its startup config gate.

Per-chunk decoding manufactures U+FFFD whenever TCP splits a multi-byte
sequence (player names inside listplayers rows), the exact defect the C#
Utf8ChunkDecoder fixes on the client side. ASCII ids survive any split, so a
broken decode hides until the first non-ASCII name - these tests pin the
stream-level contract instead.
"""

from __future__ import annotations

import os
import subprocess
import sys
from pathlib import Path

import bloodmoon_profile
import pytest

ROOT = Path(__file__).resolve().parents[1]


def test_split_multibyte_name_survives_chunk_boundary():
    row = "0. id=17, Zo\u00e9, pos=(1.0, 2.0, 3.0)".encode("utf-8")
    # Cut mid-sequence: byte 4 is inside the 2-byte e-acute.
    assert bloodmoon_profile.decode_stream([row[:4], row[4:]]) == row.decode("utf-8")


def test_split_four_byte_emoji_across_three_chunks():
    body = "REFake1 \U0001f600 died".encode("utf-8")
    chunks = [body[:3], body[3:7], body[7:]]  # split the 4-byte emoji into 2+1+1
    assert bloodmoon_profile.decode_stream(chunks) == body.decode("utf-8")


def test_invalid_bytes_replace_without_raising():
    # Undecodable input degrades visibly (one U+FFFD per bad run) instead of
    # crashing the profile loop; legal bytes around it stay intact.
    assert bloodmoon_profile.decode_stream([b"\xff id=5"]) == "\ufffd id=5"


def _profile_startup(env: dict[str, str]) -> subprocess.CompletedProcess[str]:
    """Run the profile's real entry point with the given LOADGEN_* overlay.
    The config gate runs at import, so a rejected value exits before the
    script takes the run lock or touches the network."""
    child_env = dict(os.environ, **env)
    return subprocess.run(
        [sys.executable, str(ROOT / "scripts" / "bloodmoon_profile.py")],
        cwd=str(ROOT), env=child_env, capture_output=True, text=True,
        encoding="utf-8", errors="replace", timeout=60, check=False,
    )


@pytest.mark.parametrize(
    ("env", "named"),
    [
        ({"LOADGEN_PORT": "not-a-port"}, "LOADGEN_PORT"),
        ({"LOADGEN_PORT": "70000"}, "65535"),
        ({"LOADGEN_TELNET_PORT": "0"}, "LOADGEN_TELNET_PORT"),
        ({"LOADGEN_HOST": "127.0.0.1; reboot"}, "LOADGEN_HOST"),
    ],
)
def test_a_bad_target_knob_exits_before_the_run(env: dict[str, str], named: str) -> None:
    # LOADGEN_PORT travelled as an unvalidated string into the client's own
    # env and into the run lock's key, so a typo surfaced as a connect
    # failure or a second profile's lock, minutes into the run.
    r = _profile_startup(env)
    assert r.returncode != 0, r.stdout
    assert named in r.stderr
