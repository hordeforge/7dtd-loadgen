"""Golden wire + self-test-join gates for 7dtd-loadgen."""

from __future__ import annotations

import os
from pathlib import Path

from loadgen_cli import run as _run

ROOT = Path(__file__).resolve().parents[1]
SCRATCH = Path(os.environ.get("RE_SCRATCH", Path.home() / ".cache" / "7dtd-loadgen" / "test"))

GOLDEN_POS_BODY = 30
GOLDEN_REL_BODY = 20
GOLDEN_REL_CONTENT_LEN = 22
GOLDEN_FLAGS_BODY = 6


def test_golden_wire_cli():
    SCRATCH.mkdir(parents=True, exist_ok=True)
    r = _run(["--golden-wire"], timeout=30)
    out = r.stdout + r.stderr
    (SCRATCH / "golden_wire.txt").write_text(out, encoding="utf-8")
    assert r.returncode == 0, out
    assert "PASS golden-wire" in out
    assert f"RelPos body={GOLDEN_REL_BODY}" in out
    assert f"PosAndRot body={GOLDEN_POS_BODY}" in out


def test_relpos_constants_in_source():
    src = (ROOT / "src" / "LoadGen" / "PackageCodec.cs").read_text(encoding="utf-8")
    assert f"EntityRelPosAndRotNoQ = {GOLDEN_REL_BODY}" in src
    assert f"EntityRelPosAndRotNoQContentLen = {GOLDEN_REL_CONTENT_LEN}" in src
    assert "EntityRelPosAndRotNoQ = 36" not in src


def test_self_test_join_respawn_loop():
    SCRATCH.mkdir(parents=True, exist_ok=True)
    r = _run(["--self-test-join", "--actions", "24", "--seed", "7"], timeout=40)
    full = (r.stdout or "") + (r.stderr or "")
    (SCRATCH / "self_test_join.txt").write_text(full, encoding="utf-8")
    assert r.returncode == 0, full
    assert "PASS: self-test-join" in full
    assert "ACTION walk#" in full
    assert "DEATH #" in full
    assert "RESPAWN ok" in full or "STAGE Respawned" in full


def test_help_mentions_respawn_and_join():
    r = _run(["--help"], timeout=15)
    out = r.stdout + r.stderr
    assert r.returncode == 0
    assert "--join" in out
    assert "--self-test-join" in out
    # Death/respawn is a documented workload knob; either phrasing alone is not
    # enough, the flag family must actually be listed.
    assert "--respawn" in out


def test_observer_flags_are_documented_and_require_jsonl_output():
    help_result = _run(["--help"], timeout=15)
    help_text = help_result.stdout + help_result.stderr
    assert "--observe-cvar NAME" in help_text
    assert "--observe-buff NAME" in help_text
    assert "--events-jsonl PATH" in help_text

    result = _run(
        ["--join", "--observe-cvar", "atomicProtection", "--no-spawn-zombies"],
        timeout=15,
    )
    output = result.stdout + result.stderr
    assert result.returncode == 2
    assert "invalid --events-jsonl 'missing'" in output


def test_unwritable_events_jsonl_fails_clean():
    """An unwritable --events-jsonl path is a usage error (exit 2, named flag),
    not an unhandled-exception crash after validation."""
    SCRATCH.mkdir(parents=True, exist_ok=True)
    blocked = SCRATCH / "events_jsonl_blocked"
    blocked.write_text("", encoding="utf-8")  # a file where a directory is needed
    r = _run(
        ["--join", "--observe-cvar", "atomicProtection", "--no-spawn-zombies",
         "--events-jsonl", str(blocked / "events.jsonl")],
        timeout=20,
    )
    output = r.stdout + r.stderr
    assert r.returncode == 2, output[-2000:]
    assert "invalid --events-jsonl" in output
    assert "Traceback" not in r.stderr
